using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EntitiesJobify.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace EntitiesJobify.CodeFixes
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(EntitiesJobifyCodeFixProvider)), System.Composition.Shared]
    public sealed class EntitiesJobifyCodeFixProvider : CodeFixProvider
    {
        private const string DiagnosticId = "EJ0001";

        private enum ExecutionMode
        {
            Run,
            Schedule,
            ScheduleParallel
        }

        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticId);

        public override FixAllProvider GetFixAllProvider() => null;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics.FirstOrDefault();
            if (diagnostic == null)
                return;

            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var loop = FindLoop(root, diagnostic.Location.SourceSpan);
            if (loop == null)
                return;

            var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (model == null)
                return;

            context.RegisterCodeFix(
                CodeAction.Create(
                    "IJobEntity に変換（Run）",
                    cancellationToken => ApplyAsync(context.Document, diagnostic.Location.SourceSpan, ExecutionMode.Run, cancellationToken),
                    equivalenceKey: nameof(EntitiesJobifyCodeFixProvider) + ".Run"),
                diagnostic);

            var analysis = LoopAnalysis.Analyze(loop, model, context.CancellationToken);
            if (CanSchedule(analysis))
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        "IJobEntity に変換（Schedule）",
                        cancellationToken => ApplyAsync(context.Document, diagnostic.Location.SourceSpan, ExecutionMode.Schedule, cancellationToken),
                        equivalenceKey: nameof(EntitiesJobifyCodeFixProvider) + ".Schedule"),
                    diagnostic);
            }
            if (CanScheduleParallel(analysis))
                context.RegisterCodeFix(
                    CodeAction.Create(
                        analysis.CanScheduleEcbInParallel && analysis.UpdatedRandoms.Count != 0
                            ? "IJobEntity に変換（ScheduleParallel・エンティティ別の乱数）"
                            : "IJobEntity に変換（ScheduleParallel）",
                        cancellationToken => ApplyAsync(context.Document, diagnostic.Location.SourceSpan, ExecutionMode.ScheduleParallel, cancellationToken),
                        equivalenceKey: nameof(EntitiesJobifyCodeFixProvider) + ".ScheduleParallel"),
                    diagnostic);
        }

        private static async Task<Document> ApplyAsync(Document document, Microsoft.CodeAnalysis.Text.TextSpan diagnosticSpan,
            ExecutionMode executionMode, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var loop = FindLoop(root, diagnosticSpan);
            if (root == null || model == null || loop == null)
                return document;

            var updatedRoot = Transform(root, loop, model, executionMode, cancellationToken);
            if (updatedRoot == root)
                return document;

            var changedDocument = document.WithSyntaxRoot(updatedRoot);
            return await Simplifier.ReduceAsync(
                changedDocument,
                Simplifier.Annotation,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        internal static SyntaxNode Transform(SyntaxNode root, SyntaxNode loop, SemanticModel model, CancellationToken cancellationToken)
        {
            return Transform(root, loop, model, ExecutionMode.Run, cancellationToken);
        }

        private static SyntaxNode Transform(SyntaxNode root, SyntaxNode loop, SemanticModel model,
            ExecutionMode executionMode, CancellationToken cancellationToken)
        {
            var analysis = LoopAnalysis.Analyze(loop, model, cancellationToken);
            var parallelEcb = executionMode == ExecutionMode.ScheduleParallel && analysis.CanScheduleEcbInParallel;
            if (!analysis.CanConvert ||
                (executionMode == ExecutionMode.Schedule && !CanSchedule(analysis)) ||
                (executionMode == ExecutionMode.ScheduleParallel && !CanScheduleParallel(analysis)))
                return root;

            var rootWithJobInterface = EnsureJobInterfaceIsInScope(root, loop, model);
            if (rootWithJobInterface == null)
                return root;
            var needsJobInterfaceAlias = !ReferenceEquals(rootWithJobInterface, root);

            var jobName = CreateJobName(analysis, model, cancellationToken);
            var fieldNames = CreateCaptureFieldNames(analysis.Captures);
            string sortKey = null;
            if (parallelEcb)
            {
                sortKey = "entityIndexInQuery";
                while (analysis.Loop.DescendantTokens().Any(token => token.ValueText == sortKey))
                    sortKey += "_";
            }
            var rewrittenBody = (BlockSyntax)new LoopBodyRewriter(analysis, model, fieldNames, sortKey, cancellationToken).Visit(analysis.Body);
            var job = CreateJobDeclaration(jobName, analysis, rewrittenBody, fieldNames, sortKey);
            var replacement = CreateExecutionStatement(jobName, analysis, fieldNames, executionMode, parallelEcb)
                .WithAdditionalAnnotations(Formatter.Annotation, Simplifier.Annotation)
                .WithLeadingTrivia(loop.GetLeadingTrivia())
                .WithTrailingTrivia(loop.GetTrailingTrivia());
            var updatedSystem = analysis.SystemDeclaration.ReplaceNode(loop, replacement).AddMembers(job);
            var updatedRoot = root.ReplaceNode(analysis.SystemDeclaration, updatedSystem);
            return needsJobInterfaceAlias ? EnsureJobInterfaceIsInScope(updatedRoot, loop, model) ?? updatedRoot : updatedRoot;
        }

        private static bool CanSchedule(LoopAnalysisResult analysis)
        {
            return analysis.CanConvert && !analysis.RequiresRun && analysis.ReadOnlyArrays.Count == 0 &&
                   ReferenceEquals(analysis.OnUpdate.Body.Statements.LastOrDefault(), analysis.Loop);
        }

        private static bool CanScheduleParallel(LoopAnalysisResult analysis)
        {
            return analysis.CanConvert && (analysis.CanScheduleEcbInParallel || CanSchedule(analysis) ||
                   (!analysis.RequiresRun && analysis.ReadOnlyArrays.Count != 0));
        }

        private static SyntaxNode EnsureJobInterfaceIsInScope(SyntaxNode root, SyntaxNode loop, SemanticModel model)
        {
            const string interfaceName = "IJobEntity";
            const string interfaceFullName = "global::Unity.Entities.IJobEntity";
            var inScope = model.LookupNamespacesAndTypes(loop.SpanStart, name: interfaceName);
            var hasEntitiesNamespaceImport = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Any(directive => directive.Alias == null && directive.Name?.ToString() == "Unity.Entities");
            var namespaceIsEntities = loop.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                .Any(declaration => declaration.Name?.ToString() == "Unity.Entities");
            if (inScope.Any(symbol => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == interfaceFullName) &&
                (hasEntitiesNamespaceImport || namespaceIsEntities))
                return root;
            if (inScope.Any(symbol => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != interfaceFullName))
                return null;

            var import = SyntaxFactory.ParseCompilationUnit("using Unity.Entities;\n").Usings.Single();
            return root is CompilationUnitSyntax compilationUnit && !hasEntitiesNamespaceImport && !namespaceIsEntities
                ? compilationUnit.AddUsings(import)
                : root;
        }
        private static SyntaxNode FindLoop(SyntaxNode root, Microsoft.CodeAnalysis.Text.TextSpan span)
        {
            if (root == null)
                return null;
            return root.FindNode(span, getInnermostNodeForTie: true).AncestorsAndSelf()
                .FirstOrDefault(node => node is ForEachStatementSyntax || node is ForEachVariableStatementSyntax);
        }

        private static string CreateJobName(LoopAnalysisResult analysis, SemanticModel model, CancellationToken cancellationToken)
        {
            var system = model.GetDeclaredSymbol(analysis.SystemDeclaration, cancellationToken) as INamedTypeSymbol;
            const string baseName = "EntitiesJobifyJob";
            var name = baseName;
            var index = 2;
            while ((system != null && system.GetMembers(name).Length != 0) ||
                   model.LookupNamespacesAndTypes(analysis.Loop.SpanStart, name: name).Length != 0)
                name = baseName + index++;
            return name;
        }

        private static Dictionary<ILocalSymbol, string> CreateCaptureFieldNames(IEnumerable<ILocalSymbol> captures)
        {
            var names = new Dictionary<ILocalSymbol, string>();
            foreach (var capture in captures)
                names.Add(capture, "Captured_" + capture.Name);
            return names;
        }

        private static StructDeclarationSyntax CreateJobDeclaration(string jobName, LoopAnalysisResult analysis, BlockSyntax body,
            Dictionary<ILocalSymbol, string> captureFields, string sortKey)
        {
            var fields = string.Join("\n", analysis.Captures.Select(capture =>
                (analysis.ReadOnlyArrays.Contains(capture) ? "        [global::Unity.Collections.ReadOnly]\n" : "") +
                "        public " + capture.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) +
                (sortKey != null && analysis.CommandBuffers.Contains(capture) ? ".ParallelWriter" : "") +
                " " + captureFields[capture] + ";"));
            var parameters = string.Join(", ", analysis.Components.Select(component =>
                (component.IsReadWrite ? "ref " : "in ") + component.ComponentType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + " " + EscapeIdentifier(component.ParameterName)));
            if (analysis.EntityVariable != null)
                parameters += ", global::Unity.Entities.Entity " + EscapeIdentifier(analysis.EntityVariable.Name);
            if (sortKey != null)
            {
                parameters += ", [global::Unity.Entities.EntityIndexInQuery] int " + sortKey;
                var randoms = analysis.Captures.Where(analysis.UpdatedRandoms.Contains).Select(random =>
                    SyntaxFactory.ParseStatement(
                        "var " + EscapeIdentifier(random.Name) + " = global::Unity.Mathematics.Random.CreateFromIndex(" +
                        "(uint)(((ulong)this." + captureFields[random] + ".state + (uint)" + sortKey + ") % uint.MaxValue));\n"));
                body = body.WithStatements(body.Statements.InsertRange(0, randoms));
            }
            var fieldSection = fields.Length == 0 ? "" : fields + "\n\n";
            var attributes = (analysis.IsBurstCompiled ? "[global::Unity.Burst.BurstCompile]\n" : "") +
                string.Concat(analysis.WithAll.Select(type =>
                "[WithAll(typeof(" + type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "))]\n")) +
                string.Concat(analysis.WithNone.Select(type =>
                "[WithNone(typeof(" + type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "))]\n"));
            var source = attributes + "private partial struct " + jobName + " : IJobEntity\n{\n" +
                         fieldSection + "    private void Execute(" + parameters + ")\n" + body.ToFullString() + "\n}";
            return SyntaxFactory.ParseMemberDeclaration(source)
                .WithAdditionalAnnotations(Formatter.Annotation, Simplifier.Annotation) as StructDeclarationSyntax;
        }

        private static StatementSyntax CreateExecutionStatement(string jobName, LoopAnalysisResult analysis,
            Dictionary<ILocalSymbol, string> captureFields, ExecutionMode executionMode, bool parallelEcb)
        {
            var initializer = analysis.Captures.Count == 0
                ? "new " + jobName + "()"
                : "new " + jobName + " { " + string.Join(", ", analysis.Captures.Select(capture =>
                    captureFields[capture] + " = " + EscapeIdentifier(capture.Name) +
                    (parallelEcb && analysis.CommandBuffers.Contains(capture) ? ".AsParallelWriter()" : ""))) + " }";
            var stateName = EscapeIdentifier(analysis.OnUpdate.ParameterList.Parameters[0].Identifier.ValueText);

            if (executionMode != ExecutionMode.Run)
            {
                var scheduleMethod = executionMode == ExecutionMode.Schedule ? "Schedule" : "ScheduleParallel";
                var schedule = stateName + ".Dependency = " + initializer + "." + scheduleMethod + "(" + stateName + ".Dependency);";
                if (analysis.ReadOnlyArrays.Count != 0)
                    return SyntaxFactory.ParseStatement("{\n" + schedule + "\n" + stateName + ".Dependency.Complete();\n}");
                return SyntaxFactory.ParseStatement(schedule);
            }

            var completeDependencies = string.Join("\n", analysis.Components.Select(component =>
                stateName + ".EntityManager.CompleteDependencyBefore" + (component.IsReadWrite ? "RW" : "RO") + "<" +
                component.ComponentType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ">();"));
            return SyntaxFactory.ParseStatement("{\n" + completeDependencies + "\n" + initializer + ".Run();\n}");
        }

        private static string EscapeIdentifier(string name)
        {
            return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None
                ? "@" + name
                : name;
        }

        private sealed class LoopBodyRewriter : CSharpSyntaxRewriter
        {
            private readonly LoopAnalysisResult _analysis;
            private readonly SemanticModel _model;
            private readonly Dictionary<ILocalSymbol, string> _captureFields;
            private readonly string _sortKey;
            private readonly CancellationToken _cancellationToken;

            public LoopBodyRewriter(LoopAnalysisResult analysis, SemanticModel model, Dictionary<ILocalSymbol, string> captureFields,
                string sortKey, CancellationToken cancellationToken)
            {
                _analysis = analysis;
                _model = model;
                _captureFields = captureFields;
                _sortKey = sortKey;
                _cancellationToken = cancellationToken;
            }

            public override SyntaxNode VisitContinueStatement(ContinueStatementSyntax node)
            {
                if (LoopAnalysis.IsInNestedLoop(node, _analysis.Loop))
                    return node;

                return SyntaxFactory.ReturnStatement()
                    .WithReturnKeyword(SyntaxFactory.Token(node.ContinueKeyword.LeadingTrivia, SyntaxKind.ReturnKeyword, node.ContinueKeyword.TrailingTrivia))
                    .WithSemicolonToken(node.SemicolonToken);
            }

            public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                if (LoopAnalysis.TryGetComponentWrapper(node, _analysis, _model, _cancellationToken, out _))
                    return node.Expression.WithTriviaFrom(node);
                return base.VisitMemberAccessExpression(node);
            }

            public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                var method = _model.GetSymbolInfo(node, _cancellationToken).Symbol as IMethodSymbol;
                var rewritten = (InvocationExpressionSyntax)base.VisitInvocationExpression(node);
                if (method != null && method.ContainingType.ToDisplayString() == "Unity.Mathematics.math")
                    return rewritten.WithExpression(SyntaxFactory.ParseExpression("global::Unity.Mathematics.math." + method.Name).WithTriviaFrom(node.Expression));
                if (method != null && method.IsStatic && method.ContainingType.ToDisplayString() == "Unity.Transforms.LocalTransform")
                    return rewritten.WithExpression(SyntaxFactory.ParseExpression("global::Unity.Transforms.LocalTransform." + method.Name).WithTriviaFrom(node.Expression));
                if (_sortKey != null && node.Expression is MemberAccessExpressionSyntax access &&
                    _model.GetSymbolInfo(access.Expression, _cancellationToken).Symbol is ILocalSymbol buffer &&
                    _analysis.CommandBuffers.Contains(buffer))
                    return rewritten.WithArgumentList(rewritten.ArgumentList.WithArguments(
                        rewritten.ArgumentList.Arguments.Insert(0, SyntaxFactory.Argument(SyntaxFactory.IdentifierName(_sortKey)))));
                return rewritten;
            }

            public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
            {
                var local = _model.GetSymbolInfo(node, _cancellationToken).Symbol as ILocalSymbol;
                var capture = local == null ? null : _captureFields.Keys.FirstOrDefault(symbol => symbol.Equals(local));
                if (capture == null || (_sortKey != null && _analysis.UpdatedRandoms.Contains(capture)))
                    return base.VisitIdentifierName(node);
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ThisExpression(),
                    SyntaxFactory.IdentifierName(_captureFields[capture])).WithTriviaFrom(node);
            }
        }
    }
}


