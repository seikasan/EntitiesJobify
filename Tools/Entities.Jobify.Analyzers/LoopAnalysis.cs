using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EntitiesJobify.Analyzers
{
    internal sealed class LoopAnalysisResult
    {
        public bool IsTarget;
        public bool CanConvert;
        public bool IsBurstCompiled;
        public bool RequiresRun;
        public bool CanScheduleEcbInParallel;
        public string Reason;
        public SyntaxNode Loop;
        public BlockSyntax Body;
        public ILocalSymbol EntityVariable;
        public MethodDeclarationSyntax OnUpdate;
        public StructDeclarationSyntax SystemDeclaration;
        public readonly List<ComponentBinding> Components = new List<ComponentBinding>();
        public readonly List<ILocalSymbol> Captures = new List<ILocalSymbol>();
        public readonly HashSet<ILocalSymbol> UpdatedRandoms = new HashSet<ILocalSymbol>();
        public readonly HashSet<ILocalSymbol> CommandBuffers = new HashSet<ILocalSymbol>();
        public readonly HashSet<ILocalSymbol> ReadOnlyArrays = new HashSet<ILocalSymbol>();
        public readonly List<INamedTypeSymbol> WithAll = new List<INamedTypeSymbol>();
        public readonly List<INamedTypeSymbol> WithNone = new List<INamedTypeSymbol>();
    }

    internal sealed class ComponentBinding
    {
        public ILocalSymbol Variable;
        public INamedTypeSymbol RefType;
        public INamedTypeSymbol ComponentType;
        public bool IsReadWrite;
        public string ParameterName;
    }

    internal static class LoopAnalysis
    {


        public static LoopAnalysisResult Analyze(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
        {
            var result = new LoopAnalysisResult { Loop = node };
            var statement = node as ForEachStatementSyntax;
            var variableStatement = node as ForEachVariableStatementSyntax;
            var loopExpression = statement != null ? statement.Expression : variableStatement != null ? variableStatement.Expression : null;
            var loopBody = statement != null ? statement.Statement as BlockSyntax : variableStatement != null ? variableStatement.Statement as BlockSyntax : null;
            var method = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            var containingStruct = method?.Parent as StructDeclarationSyntax;

            if (method == null || method.Identifier.ValueText != "OnUpdate" || method.Body == null || node.Parent != method.Body ||
                method.ContainsDiagnostics || node.ContainsDiagnostics)
                return result;
            if (containingStruct == null || !containingStruct.Modifiers.Any(SyntaxKind.PartialKeyword) ||
                containingStruct.TypeParameterList != null || containingStruct.Parent is TypeDeclarationSyntax)
                return result;

            var methodSymbol = model.GetDeclaredSymbol(method, cancellationToken) as IMethodSymbol;
            var systemType = model.GetDeclaredSymbol(containingStruct, cancellationToken) as INamedTypeSymbol;
            var iSystemType = model.Compilation.GetTypeByMetadataName("Unity.Entities.ISystem");
            var systemStateType = model.Compilation.GetTypeByMetadataName("Unity.Entities.SystemState");
            if (methodSymbol == null || systemType == null || iSystemType == null || systemStateType == null)
                return result;
            var iSystemOnUpdate = iSystemType.GetMembers("OnUpdate").OfType<IMethodSymbol>().FirstOrDefault();
            if (iSystemOnUpdate == null || !systemType.AllInterfaces.Any(i => i.Equals(iSystemType)) || !methodSymbol.ReturnsVoid ||
                methodSymbol.Parameters.Length != 1 || !methodSymbol.Parameters[0].Type.Equals(systemStateType) ||
                methodSymbol.Parameters[0].RefKind != RefKind.Ref ||
                !methodSymbol.Equals(systemType.FindImplementationForInterfaceMember(iSystemOnUpdate)))
                return result;

            var requireMatching = model.Compilation.GetTypeByMetadataName("Unity.Entities.RequireMatchingQueriesForUpdateAttribute");
            var burstCompile = model.Compilation.GetTypeByMetadataName("Unity.Burst.BurstCompileAttribute");
            if ((requireMatching != null && HasAttribute(systemType, requireMatching)) ||
                method.ContainsDirectives || containingStruct.ContainsDirectives)
                return result;

            result.IsBurstCompiled = burstCompile != null && HasAttribute(systemType, burstCompile);

            var systemApi = model.Compilation.GetTypeByMetadataName("Unity.Entities.SystemAPI");
            if (systemApi == null || loopExpression == null)
                return result;

            var queryCalls = loopExpression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .Where(invocation =>
                {
                    var symbol = model.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                    return symbol != null && symbol.Name == "Query" && symbol.ContainingType.Equals(systemApi);
                }).ToArray();
            if (queryCalls.Length != 1)
                return result;

            if (model.GetDiagnostics(node.Span, cancellationToken).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                return result;

            result.IsTarget = true;
            result.OnUpdate = method;
            result.SystemDeclaration = containingStruct;
            var query = queryCalls[0];
            var queryMethod = model.GetSymbolInfo(query, cancellationToken).Symbol as IMethodSymbol;
            if (queryMethod == null || queryMethod.TypeArguments.Length < 1 || queryMethod.TypeArguments.Length > 7 || query.ArgumentList.Arguments.Count != 0)
                return Fail(result, "SystemAPI.Query の型引数または引数が未対応です。");
            var enumerableType = model.Compilation.GetTypeByMetadataName("Unity.Entities.QueryEnumerable`" + queryMethod.TypeArguments.Length);
            var enumerableWithEntityType = model.Compilation.GetTypeByMetadataName("Unity.Entities.QueryEnumerableWithEntity`" + queryMethod.TypeArguments.Length);
            var withEntityAccess = false;
            ExpressionSyntax queryExpression = loopExpression;
            while (!queryExpression.IsEquivalentTo(query))
            {
                var invocation = queryExpression as InvocationExpressionSyntax;
                var access = invocation?.Expression as MemberAccessExpressionSyntax;
                var filter = invocation == null ? null : model.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                if (access == null || filter == null || invocation.ArgumentList.Arguments.Count != 0)
                    return Fail(result, "クエリ修飾子またはタグ型が未対応です。");
                queryExpression = access.Expression;
                var filterOwner = filter.ContainingType.OriginalDefinition;
                if (filter.Name == "WithEntityAccess" && filterOwner.Equals(enumerableType) && !withEntityAccess &&
                    filter.TypeArguments.Length == 0 && filter.ReturnType is INamedTypeSymbol withEntity &&
                    withEntity.OriginalDefinition.Equals(enumerableWithEntityType))
                {
                    withEntityAccess = true;
                    continue;
                }
                if ((!filterOwner.Equals(enumerableType) && !filterOwner.Equals(enumerableWithEntityType)) ||
                    (filter.Name != "WithAll" && filter.Name != "WithNone") || filter.TypeArguments.Length == 0)
                    return Fail(result, "クエリ修飾子またはタグ型が未対応です。");
                var tags = filter.Name == "WithAll" ? result.WithAll : result.WithNone;
                foreach (var type in filter.TypeArguments)
                {
                    if (!IsOrdinaryTag(type, model.Compilation, out var tag) ||
                        result.WithAll.Any(existing => existing.Equals(tag)) || result.WithNone.Any(existing => existing.Equals(tag)))
                        return Fail(result, "クエリ修飾子またはタグ型が未対応です。");
                    tags.Add(tag);
                }
            }
            foreach (var attributeName in new[] { "WithAll", "WithNone" })
            {
                var tags = attributeName == "WithAll" ? result.WithAll : result.WithNone;
                if (tags.Count == 0) continue;
                var attribute = model.Compilation.GetTypeByMetadataName("Unity.Entities." + attributeName + "Attribute");
                if (attribute == null || model.LookupNamespacesAndTypes(node.SpanStart, name: attributeName).Any() ||
                    model.LookupNamespacesAndTypes(node.SpanStart, name: attributeName + "Attribute").Any(symbol => !symbol.Equals(attribute)))
                    return Fail(result, "クエリ属性の名前が衝突しています。");
            }

            if (queryMethod.TypeArguments.Length == 1 && !withEntityAccess)
            {
                if (!TryGetRefComponent(queryMethod.TypeArguments[0], model.Compilation, out var refType, out var componentType, out var readWrite))
                    return Fail(result, "単一クエリは unmanaged IComponentData の RefRO / RefRW に対応します。");
                var foreachSymbol = statement != null ? model.GetDeclaredSymbol(statement, cancellationToken) as ILocalSymbol : null;
                if (foreachSymbol == null || !foreachSymbol.Type.Equals(queryMethod.TypeArguments[0]))
                    return Fail(result, "単一クエリは型を推論する識別子 foreach が必要です。");
                result.Components.Add(new ComponentBinding
                {
                    Variable = foreachSymbol,
                    RefType = refType,
                    ComponentType = componentType,
                    IsReadWrite = readWrite,
                    ParameterName = foreachSymbol.Name
                });
            }
            else
            {
                if (variableStatement == null)
                    return Fail(result, "複数要素クエリはタプル分解 foreach が必要です。");
                var declaration = variableStatement.Variable as DeclarationExpressionSyntax;
                var tupleDesignation = declaration?.Designation as ParenthesizedVariableDesignationSyntax;
                if (declaration == null || !(declaration.Type is IdentifierNameSyntax) || ((IdentifierNameSyntax)declaration.Type).Identifier.ValueText != "var" ||
                    tupleDesignation == null || tupleDesignation.Variables.Count != queryMethod.TypeArguments.Length + (withEntityAccess ? 1 : 0) ||
                    tupleDesignation.Variables.Any(v => !(v is SingleVariableDesignationSyntax)))
                    return Fail(result, "クエリ要素と同数の discard のない var タプル分解が必要です。");
                for (var index = 0; index < queryMethod.TypeArguments.Length; index++)
                {
                    if (!TryGetRefComponent(queryMethod.TypeArguments[index], model.Compilation, out var refType, out var componentType, out var readWrite) ||
                        result.Components.Any(component => component.ComponentType.Equals(componentType)))
                        return Fail(result, "クエリには異なる unmanaged IComponentData の RefRO / RefRW が必要です。");
                    var variable = model.GetDeclaredSymbol(tupleDesignation.Variables[index], cancellationToken) as ILocalSymbol;
                    if (variable == null || !variable.Type.Equals(queryMethod.TypeArguments[index]))
                        return Fail(result, "分解したローカル変数とクエリ要素の対応を確認できません。");
                    result.Components.Add(new ComponentBinding { Variable = variable, RefType = refType, ComponentType = componentType, IsReadWrite = readWrite, ParameterName = variable.Name });
                }
                if (withEntityAccess)
                {
                    result.EntityVariable = model.GetDeclaredSymbol(tupleDesignation.Variables.Last(), cancellationToken) as ILocalSymbol;
                    if (result.EntityVariable == null || !result.EntityVariable.Type.Equals(model.Compilation.GetTypeByMetadataName("Unity.Entities.Entity")))
                        return Fail(result, "WithEntityAccess の最後の分解要素には Entity が必要です。");
                }
            }

            if (loopBody == null || loopBody.ContainsDirectives || HasHeaderComment(node, loopBody))
                return Fail(result, "本体はディレクティブのないブロックである必要があります。");
            if (result.Components.Any(component => result.WithAll.Any(type => type.Equals(component.ComponentType)) ||
                result.WithNone.Any(type => type.Equals(component.ComponentType))))
                return Fail(result, "クエリ要素とタグ条件が重複しています。");
            result.Body = loopBody;
            if (!ValidateBody(result, model, cancellationToken, out var reason))
                return Fail(result, reason);

            var following = method.Body.Statements.Skip(method.Body.Statements.IndexOf((StatementSyntax)node) + 1);
            result.CanScheduleEcbInParallel = result.CommandBuffers.Count > 0 &&
                following.All(item => IsSystemDisable(item, methodSymbol.Parameters[0], model, cancellationToken)) &&
                result.CommandBuffers.All(buffer => IsSingletonCommandBuffer(buffer, result, methodSymbol.Parameters[0], model, cancellationToken));
            result.CanConvert = true;
            result.Reason = null;
            return result;
        }

        private static LoopAnalysisResult Fail(LoopAnalysisResult result, string reason)
        {
            result.CanConvert = false;
            result.Reason = reason;
            return result;
        }

        private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attributeType)
        {
            return symbol.GetAttributes().Any(attribute => attribute.AttributeClass != null && attribute.AttributeClass.Equals(attributeType));
        }

        private static bool IsSystemDisable(StatementSyntax statement, IParameterSymbol state, SemanticModel model, CancellationToken cancellationToken)
        {
            return statement is ExpressionStatementSyntax expression &&
                   expression.Expression is AssignmentExpressionSyntax assignment && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                   assignment.Right.IsKind(SyntaxKind.FalseLiteralExpression) &&
                   assignment.Left is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "Enabled" &&
                   state.Equals(model.GetSymbolInfo(member.Expression, cancellationToken).Symbol);
        }

        private static bool IsSingletonCommandBuffer(ILocalSymbol buffer, LoopAnalysisResult result, IParameterSymbol state,
            SemanticModel model, CancellationToken cancellationToken)
        {
            var declaration = buffer.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) as VariableDeclaratorSyntax;
            if (!(declaration?.Parent?.Parent is LocalDeclarationStatementSyntax local) || local.UsingKeyword != default(SyntaxToken) ||
                !(declaration.Initializer?.Value is InvocationExpressionSyntax create) ||
                !(create.Expression is MemberAccessExpressionSyntax access) ||
                !(model.GetSymbolInfo(create, cancellationToken).Symbol is IMethodSymbol method) ||
                method.Name != "CreateCommandBuffer" || method.ContainingAssembly.Name != "Unity.Entities" ||
                !method.ContainingType.AllInterfaces.Any(type => type.Equals(model.Compilation.GetTypeByMetadataName("Unity.Entities.IECBSingleton"))) ||
                create.ArgumentList.Arguments.Count != 1 ||
                !(create.ArgumentList.Arguments[0].Expression is MemberAccessExpressionSyntax world) ||
                world.Name.Identifier.ValueText != "WorldUnmanaged" || !state.Equals(model.GetSymbolInfo(world.Expression, cancellationToken).Symbol))
                return false;

            var body = result.OnUpdate.Body;
            if (body.DescendantNodes().OfType<IdentifierNameSyntax>().Any(identifier =>
                !result.Loop.Span.Contains(identifier.Span) && buffer.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol)))
                return false;

            var singletonExpression = access.Expression;
            if (model.GetSymbolInfo(access.Expression, cancellationToken).Symbol is ILocalSymbol singleton)
            {
                if (body.DescendantNodes().OfType<IdentifierNameSyntax>().Any(identifier =>
                    identifier.Span != access.Expression.Span && singleton.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol)))
                    return false;
                singletonExpression = GetLocalInitializer(singleton, cancellationToken);
            }
            return singletonExpression is InvocationExpressionSyntax get &&
                   model.GetSymbolInfo(get, cancellationToken).Symbol is IMethodSymbol getMethod &&
                   getMethod.Name == "GetSingleton" &&
                   getMethod.ContainingType.Equals(model.Compilation.GetTypeByMetadataName("Unity.Entities.SystemAPI")) &&
                   getMethod.ReturnType.Equals(method.ContainingType);
        }

        private static ExpressionSyntax GetLocalInitializer(ILocalSymbol local, CancellationToken cancellationToken)
        {
            return (local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) as VariableDeclaratorSyntax)?.Initializer?.Value;
        }
        private static bool IsOrdinaryTag(ITypeSymbol type, Compilation compilation, out INamedTypeSymbol tag)
        {
            tag = type as INamedTypeSymbol;
            var component = compilation.GetTypeByMetadataName("Unity.Entities.IComponentData");
            var enableable = compilation.GetTypeByMetadataName("Unity.Entities.IEnableableComponent");
            var shared = compilation.GetTypeByMetadataName("Unity.Entities.ISharedComponentData");
            if (tag == null || tag.TypeKind != TypeKind.Struct || tag.IsGenericType || !tag.IsUnmanagedType ||
                component == null || !tag.AllInterfaces.Any(item => item.Equals(component)) ||
                (enableable != null && tag.AllInterfaces.Any(item => item.Equals(enableable))) ||
                (shared != null && tag.AllInterfaces.Any(item => item.Equals(shared))) ||
                tag.GetMembers().OfType<IFieldSymbol>().Any(field => !field.IsStatic) ||
                tag.Equals(compilation.GetTypeByMetadataName("Unity.Entities.Disabled")) ||
                tag.Equals(compilation.GetTypeByMetadataName("Unity.Entities.Prefab")))
                return false;
            return true;
        }
        private static bool TryGetRefComponent(ITypeSymbol queryType, Compilation compilation, out INamedTypeSymbol refType, out INamedTypeSymbol componentType, out bool readWrite)
        {
            refType = null;
            componentType = null;
            readWrite = false;
            var named = queryType as INamedTypeSymbol;
            var refRw = compilation.GetTypeByMetadataName("Unity.Entities.RefRW`1");
            var refRo = compilation.GetTypeByMetadataName("Unity.Entities.RefRO`1");
            if (named == null || named.TypeArguments.Length != 1 || named.IsUnboundGenericType)
                return false;
            if (refRw != null && named.OriginalDefinition.Equals(refRw))
            {
                readWrite = true;
                refType = named.OriginalDefinition;
            }
            else if (refRo != null && named.OriginalDefinition.Equals(refRo))
            {
                refType = named.OriginalDefinition;
            }
            else
            {
                return false;
            }

            componentType = named.TypeArguments[0] as INamedTypeSymbol;
            var componentInterface = compilation.GetTypeByMetadataName("Unity.Entities.IComponentData");
            var sharedInterface = compilation.GetTypeByMetadataName("Unity.Entities.ISharedComponentData");
            var enableableInterface = compilation.GetTypeByMetadataName("Unity.Entities.IEnableableComponent");
            if (componentType == null || componentType.TypeKind != TypeKind.Struct || componentType.Arity != 0 ||
                !componentType.IsUnmanagedType || componentInterface == null ||
                !componentType.AllInterfaces.Any(i => i.Equals(componentInterface)) ||
                (sharedInterface != null && componentType.AllInterfaces.Any(i => i.Equals(sharedInterface))) ||
                (enableableInterface != null && componentType.AllInterfaces.Any(i => i.Equals(enableableInterface))))
                return false;
            if (!componentType.GetMembers().OfType<IFieldSymbol>().Any(field => !field.IsStatic && !field.IsImplicitlyDeclared))
                return false;
            return true;
        }

        private static bool HasHeaderComment(SyntaxNode loop, BlockSyntax body)
        {
            return loop.DescendantTrivia().Any(trivia =>
                trivia.SpanStart >= loop.SpanStart && trivia.SpanStart < body.SpanStart &&
                (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)));
        }
        private static bool ValidateBody(LoopAnalysisResult result, SemanticModel model, CancellationToken cancellationToken, out string reason)
        {
            reason = null;
            var loopLocals = new HashSet<ILocalSymbol>();
            if (result.EntityVariable != null)
                loopLocals.Add(result.EntityVariable);
            var captures = new HashSet<ILocalSymbol>();
            var writes = 0;
            if (!ValidateStatement(result.Body, result, model, loopLocals, captures, cancellationToken, ref writes, out reason))
                return false;
            if (writes == 0)
            {
                reason = "コンポーネントへの書き込みまたは対応する ECB コマンドがありません。";
                return false;
            }
            if (result.UpdatedRandoms.Count != 0)
            {
                var flow = model.AnalyzeDataFlow((StatementSyntax)result.Loop);
                if (!flow.Succeeded || result.UpdatedRandoms.Any(random => flow.ReadOutside.Contains(random) || flow.Captured.Contains(random)))
                {
                    reason = "ループ外でも参照する乱数の状態を保持する必要があります。";
                    return false;
                }
            }
            foreach (var capture in captures)
            {
                if (result.ReadOnlyArrays.Contains(capture))
                {
                    var declaration = capture.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) as VariableDeclaratorSyntax;
                    var allocator = GetArrayAllocator(declaration?.Initializer?.Value, model, cancellationToken);
                    if (!(declaration?.Parent?.Parent is LocalDeclarationStatementSyntax local) ||
                        local.UsingKeyword == default(SyntaxToken) ||
                        (allocator != "TempJob" && allocator != "Persistent") ||
                        model.Compilation.GetTypeByMetadataName("Unity.Collections.ReadOnlyAttribute") == null)
                    {
                        reason = "ループ外の NativeArray は using 宣言で Allocator.TempJob または Persistent を使って確保してください。";
                        return false;
                    }
                }
                if (!IsValidCapture(capture, result, model, cancellationToken))
                {
                    reason = "ループ外から参照する変数は、OnUpdate 直下で初期化済みで、Job に値渡しできる unmanaged のローカル変数に限ります。";
                    return false;
                }
                result.Captures.Add(capture);
            }
            result.Captures.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            return true;
        }

        private static bool ValidateStatement(StatementSyntax statement, LoopAnalysisResult result, SemanticModel model,
            HashSet<ILocalSymbol> loopLocals, HashSet<ILocalSymbol> captures, CancellationToken cancellationToken,
            ref int writes, out string reason)
        {
            reason = null;
            if (statement is BlockSyntax block)
            {
                foreach (var child in block.Statements)
                    if (!ValidateStatement(child, result, model, loopLocals, captures, cancellationToken, ref writes, out reason))
                        return false;
                return true;
            }
            if (statement is IfStatementSyntax conditional)
            {
                if (!IsAllowedExpression(conditional.Condition, result, model, loopLocals, captures, cancellationToken, out reason) ||
                    !ValidateStatement(conditional.Statement, result, model, loopLocals, captures, cancellationToken, ref writes, out reason))
                    return false;
                return conditional.Else == null ||
                    ValidateStatement(conditional.Else.Statement, result, model, loopLocals, captures, cancellationToken, ref writes, out reason);
            }
            if (statement is ContinueStatementSyntax)
                return true;
            if (statement is BreakStatementSyntax && IsInNestedLoop(statement, result.Loop))
                return true;
            if (statement is ForStatementSyntax forLoop)
            {
                if (forLoop.Declaration != null &&
                    !ValidateLocalDeclaration(forLoop.Declaration, result, model, loopLocals, captures, cancellationToken, out reason))
                    return false;
                foreach (var mutation in forLoop.Initializers.Concat(forLoop.Incrementors))
                    if (!ValidateMutation(mutation, result, model, loopLocals, captures, cancellationToken, ref writes, out reason))
                        return false;
                return (forLoop.Condition == null ||
                        IsAllowedExpression(forLoop.Condition, result, model, loopLocals, captures, cancellationToken, out reason)) &&
                       ValidateStatement(forLoop.Statement, result, model, loopLocals, captures, cancellationToken, ref writes, out reason);
            }
            if (statement is WhileStatementSyntax whileLoop)
                return IsAllowedExpression(whileLoop.Condition, result, model, loopLocals, captures, cancellationToken, out reason) &&
                       ValidateStatement(whileLoop.Statement, result, model, loopLocals, captures, cancellationToken, ref writes, out reason);
            if (statement is DoStatementSyntax doLoop)
                return ValidateStatement(doLoop.Statement, result, model, loopLocals, captures, cancellationToken, ref writes, out reason) &&
                       IsAllowedExpression(doLoop.Condition, result, model, loopLocals, captures, cancellationToken, out reason);
            if (statement is ForEachStatementSyntax innerLoop &&
                TryGetArrayLocal(innerLoop.Expression, model, cancellationToken, out var array) &&
                model.GetDeclaredSymbol(innerLoop, cancellationToken) is ILocalSymbol element &&
                element.RefKind == RefKind.None && element.Type.Equals(((INamedTypeSymbol)array.Type).TypeArguments[0]) &&
                IsArrayElementType(element.Type, model.Compilation))
            {
                RegisterArrayRead(array, result, loopLocals, captures);
                loopLocals.Add(element);
                return ValidateStatement(innerLoop.Statement, result, model, loopLocals, captures, cancellationToken, ref writes, out reason);
            }
            if (statement is LocalDeclarationStatementSyntax localDeclaration)
            {
                if (localDeclaration.AwaitKeyword != default(SyntaxToken) ||
                    localDeclaration.Modifiers.Any(SyntaxKind.RefKeyword) || localDeclaration.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
                {
                    reason = "ref または await を伴うローカル宣言は未対応です。";
                    return false;
                }
                if (localDeclaration.UsingKeyword != default(SyntaxToken))
                    return ValidateTempArray(localDeclaration.Declaration, result, model, loopLocals, captures, cancellationToken, out reason);
                return ValidateLocalDeclaration(localDeclaration.Declaration, result, model, loopLocals, captures, cancellationToken, out reason);
            }
            if (statement is ExpressionStatementSyntax expressionStatement)
            {
                if (expressionStatement.Expression is InvocationExpressionSyntax invocation)
                {
                    if (!ValidateEcbCommand(invocation, result, model, loopLocals, captures, cancellationToken, out reason))
                        return false;
                    writes++;
                    return true;
                }
                return ValidateMutation(expressionStatement.Expression, result, model, loopLocals, captures, cancellationToken, ref writes, out reason);
            }

            reason = "このループ本体の文は未対応です。";
            return false;
        }

        internal static bool IsInNestedLoop(SyntaxNode statement, SyntaxNode queryLoop)
        {
            return statement.Ancestors().TakeWhile(node => node != queryLoop)
                .Any(node => node is ForStatementSyntax || node is WhileStatementSyntax || node is DoStatementSyntax || node is ForEachStatementSyntax);
        }

        private static bool IsNativeArray(ITypeSymbol type, Compilation compilation)
        {
            return type is INamedTypeSymbol named && named.TypeArguments.Length == 1 &&
                   named.OriginalDefinition.Equals(compilation.GetTypeByMetadataName("Unity.Collections.NativeArray`1"));
        }

        private static bool IsEntityArray(ITypeSymbol type, Compilation compilation)
        {
            return IsNativeArray(type, compilation) &&
                   ((INamedTypeSymbol)type).TypeArguments[0].Equals(compilation.GetTypeByMetadataName("Unity.Entities.Entity"));
        }

        private static bool TryGetArrayLocal(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken, out ILocalSymbol local)
        {
            local = expression is IdentifierNameSyntax ? model.GetSymbolInfo(expression, cancellationToken).Symbol as ILocalSymbol : null;
            return local != null && IsNativeArray(local.Type, model.Compilation);
        }

        private static void RegisterArrayRead(ILocalSymbol array, LoopAnalysisResult result, HashSet<ILocalSymbol> loopLocals, HashSet<ILocalSymbol> captures)
        {
            if (loopLocals.Contains(array))
                return;
            result.ReadOnlyArrays.Add(array);
            captures.Add(array);
        }

        private static bool IsLocalEntityArray(ExpressionSyntax expression, SemanticModel model, HashSet<ILocalSymbol> loopLocals,
            CancellationToken cancellationToken)
        {
            return TryGetArrayLocal(expression, model, cancellationToken, out var local) &&
                   loopLocals.Contains(local) && IsEntityArray(local.Type, model.Compilation);
        }

        private static string GetArrayAllocator(ExpressionSyntax initializer, SemanticModel model, CancellationToken cancellationToken)
        {
            if (!(initializer is ObjectCreationExpressionSyntax creation) || creation.Initializer != null ||
                !(model.GetSymbolInfo(creation, cancellationToken).Symbol is IMethodSymbol constructor) ||
                !IsNativeArray(constructor.ContainingType, model.Compilation) ||
                creation.ArgumentList.Arguments.Count < 2 || creation.ArgumentList.Arguments.Any(argument => argument.NameColon != null) ||
                !(model.GetSymbolInfo(creation.ArgumentList.Arguments[1].Expression, cancellationToken).Symbol is IFieldSymbol allocator) ||
                !allocator.ContainingType.Equals(model.Compilation.GetTypeByMetadataName("Unity.Collections.Allocator")))
                return null;
            return allocator.Name;
        }

        private static bool ValidateTempArray(VariableDeclarationSyntax declaration, LoopAnalysisResult result, SemanticModel model,
            HashSet<ILocalSymbol> loopLocals, HashSet<ILocalSymbol> captures, CancellationToken cancellationToken, out string reason)
        {
            reason = "using 宣言は、長さを指定し Allocator.Temp で確保する NativeArray に限ります。";
            foreach (var variable in declaration.Variables)
            {
                if (!(model.GetDeclaredSymbol(variable, cancellationToken) is ILocalSymbol local) ||
                    !IsNativeArray(local.Type, model.Compilation) ||
                    !(variable.Initializer?.Value is ObjectCreationExpressionSyntax creation) ||
                    !local.Type.Equals(model.GetTypeInfo(creation, cancellationToken).Type) ||
                    creation.ArgumentList.Arguments.Count != 2 ||
                    model.GetTypeInfo(creation.ArgumentList.Arguments[0].Expression, cancellationToken).ConvertedType?.SpecialType != SpecialType.System_Int32 ||
                    GetArrayAllocator(creation, model, cancellationToken) != "Temp" ||
                    !IsArrayElementType(((INamedTypeSymbol)local.Type).TypeArguments[0], model.Compilation))
                    return false;

                if (!IsAllowedExpression(creation.ArgumentList.Arguments[0].Expression, result, model, loopLocals, captures, cancellationToken, out reason))
                    return false;
                loopLocals.Add(local);
            }
            return true;
        }

        private static bool IsArrayElementType(ITypeSymbol type, Compilation compilation)
        {
            var nativeContainer = compilation.GetTypeByMetadataName("Unity.Collections.LowLevel.Unsafe.NativeContainerAttribute");
            return IsJobValue(type, nativeContainer, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
        }

        private static bool TryGetArrayElement(ElementAccessExpressionSyntax element, SemanticModel model, CancellationToken cancellationToken,
            out ILocalSymbol array)
        {
            return TryGetArrayLocal(element.Expression, model, cancellationToken, out array) &&
                   element.ArgumentList.Arguments.Count == 1 && element.ArgumentList.Arguments[0].NameColon == null &&
                   model.GetSymbolInfo(element, cancellationToken).Symbol is IPropertySymbol indexer && indexer.IsIndexer &&
                   indexer.ContainingType.Equals(array.Type) &&
                   IsArrayElementType(indexer.Type, model.Compilation);
        }

        private static bool ValidateEcbCommand(InvocationExpressionSyntax invocation, LoopAnalysisResult result, SemanticModel model,
            HashSet<ILocalSymbol> loopLocals, HashSet<ILocalSymbol> captures, CancellationToken cancellationToken, out string reason)
        {
            reason = "未対応の ECB コマンドまたはメソッド呼び出しです。";
            if (!(invocation.Expression is MemberAccessExpressionSyntax access) || !(access.Expression is IdentifierNameSyntax) ||
                !(model.GetSymbolInfo(access.Expression, cancellationToken).Symbol is ILocalSymbol buffer) ||
                !buffer.Type.Equals(model.Compilation.GetTypeByMetadataName("Unity.Entities.EntityCommandBuffer")) ||
                !(model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method) || !method.ContainingType.Equals(buffer.Type) ||
                method.ContainingAssembly.Name != "Unity.Entities" ||
                !IsSupportedEcbCommand(method) || method.Parameters.Length == 0 ||
                !method.Parameters[0].Type.Equals(model.Compilation.GetTypeByMetadataName("Unity.Entities.Entity")) ||
                method.Parameters.Any(parameter => parameter.RefKind != RefKind.None) ||
                (method.Name == "Instantiate" && method.Parameters.Length == 2 && !IsEntityArray(method.Parameters[1].Type, model.Compilation)))
                return false;

            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                if (method.Name == "Instantiate" && IsLocalEntityArray(argument.Expression, model, loopLocals, cancellationToken))
                    continue;
                if (!IsAllowedExpression(argument.Expression, result, model, loopLocals, captures, cancellationToken, out reason))
                    return false;
            }
            captures.Add(buffer);
            result.CommandBuffers.Add(buffer);
            result.RequiresRun = true;
            reason = null;
            return true;
        }

        private static bool IsSupportedEcbCommand(IMethodSymbol method)
        {
            switch (method.Name)
            {
                case "Instantiate":
                    return method.Parameters.Length == 1 || method.Parameters.Length == 2;
                case "SetComponent":
                    return method.Arity == 1 && method.Parameters.Length == 2;
                case "AddComponent":
                    return method.Arity == 1 && (method.Parameters.Length == 1 || method.Parameters.Length == 2);
                case "RemoveComponent":
                    return method.Arity == 1 && method.Parameters.Length == 1;
                case "DestroyEntity":
                    return method.Parameters.Length == 1;
                default:
                    return false;
            }
        }

        private static bool ValidateLocalDeclaration(VariableDeclarationSyntax declaration, LoopAnalysisResult result, SemanticModel model,
            HashSet<ILocalSymbol> loopLocals, HashSet<ILocalSymbol> captures, CancellationToken cancellationToken, out string reason)
        {
            reason = null;
            foreach (var variable in declaration.Variables)
            {
                var local = model.GetDeclaredSymbol(variable, cancellationToken) as ILocalSymbol;
                if (local == null || local.RefKind != RefKind.None || variable.Initializer == null)
                {
                    reason = "ローカル宣言は初期値を持つスカラー型、float2/3/4、Entity または NativeArray の要素に限ります。";
                    return false;
                }
                if (IsNativeArray(local.Type, model.Compilation) &&
                    TryGetArrayLocal(variable.Initializer.Value, model, cancellationToken, out var array) &&
                    loopLocals.Contains(array) && local.Type.Equals(array.Type))
                {
                    loopLocals.Add(local);
                    continue;
                }
                if (!IsSupportedValue(local.Type, model.Compilation) &&
                    !(variable.Initializer.Value is ElementAccessExpressionSyntax element &&
                      local.Type.Equals(model.GetTypeInfo(element, cancellationToken).Type) &&
                      TryGetArrayElement(element, model, cancellationToken, out _)))
                {
                    reason = "ローカル宣言は初期値を持つスカラー型、float2/3/4、Entity または NativeArray の要素に限ります。";
                    return false;
                }
                if (!IsAllowedExpression(variable.Initializer.Value, result, model, loopLocals, captures, cancellationToken, out reason))
                    return false;
                loopLocals.Add(local);
            }
            return true;
        }

        private static bool ValidateMutation(ExpressionSyntax expression, LoopAnalysisResult result, SemanticModel model,
            HashSet<ILocalSymbol> loopLocals, HashSet<ILocalSymbol> captures, CancellationToken cancellationToken,
            ref int writes, out string reason)
        {
            reason = null;
            var assignment = expression as AssignmentExpressionSyntax;
            ExpressionSyntax target = assignment?.Left;
            switch (expression.Kind())
            {
                case SyntaxKind.PreIncrementExpression:
                case SyntaxKind.PreDecrementExpression:
                    target = ((PrefixUnaryExpressionSyntax)expression).Operand;
                    break;
                case SyntaxKind.PostIncrementExpression:
                case SyntaxKind.PostDecrementExpression:
                    target = ((PostfixUnaryExpressionSyntax)expression).Operand;
                    break;
            }
            if (target == null || (assignment != null && !IsSupportedAssignment(assignment.Kind())) ||
                !IsAllowedOperator(model.GetSymbolInfo(expression, cancellationToken).Symbol, model.Compilation))
            {
                reason = "未対応の代入またはインクリメント・デクリメントです。";
                return false;
            }

            var targetLocal = FindFieldRootLocal(target, model, cancellationToken);
            var localWrite = targetLocal != null && loopLocals.Contains(targetLocal) && !targetLocal.IsConst &&
                             !IsNativeArray(targetLocal.Type, model.Compilation);
            if (target is ElementAccessExpressionSyntax element && TryGetArrayElement(element, model, cancellationToken, out var array))
            {
                if (!loopLocals.Contains(array))
                {
                    reason = "ループ外の NativeArray には書き込めません。";
                    return false;
                }
                if (!IsAllowedExpression(element.ArgumentList.Arguments[0].Expression, result, model, loopLocals, captures, cancellationToken, out reason))
                    return false;
                localWrite = true;
            }
            var componentWrite = TryGetComponentField(target, result, model, cancellationToken, out var field, out var writable) && writable;
            if (!componentWrite && assignment != null && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                IsLocalTransform(model.GetTypeInfo(target, cancellationToken).Type, model.Compilation) && target is MemberAccessExpressionSyntax wrapper)
                componentWrite = TryGetComponentWrapper(wrapper, result, model, cancellationToken, out writable) && writable;
            if (!localWrite && !componentWrite)
            {
                reason = "書き込み先はループ内ローカルまたは RefRW のフィールドに限ります。";
                return false;
            }
            if (componentWrite && field != null && !IsSupportedValue(field.Type, model.Compilation))
            {
                var capture = assignment == null ? null : FindFieldRootLocal(assignment.Right, model, cancellationToken);
                if (capture == null || loopLocals.Contains(capture) || !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
                    !field.Type.Equals(model.GetTypeInfo(assignment.Right, cancellationToken).Type))
                {
                    reason = "構造体フィールドへの代入には、同じ型のキャプチャ値を指定してください。";
                    return false;
                }
                captures.Add(capture);
            }
            else if (assignment != null && !IsAllowedExpression(assignment.Right, result, model, loopLocals, captures, cancellationToken, out reason))
            {
                return false;
            }
            if (componentWrite) writes++;
            return true;
        }

        private static bool IsAllowedExpression(ExpressionSyntax expression, LoopAnalysisResult result, SemanticModel model,
            HashSet<ILocalSymbol> loopLocals, HashSet<ILocalSymbol> captures, CancellationToken cancellationToken, out string reason)
        {
            reason = null;
            if (model.GetConversion(expression, cancellationToken).IsUserDefined)
            {
                reason = "ユーザー定義の暗黙変換は未対応です。";
                return false;
            }
            if (expression is ParenthesizedExpressionSyntax parenthesized)
                return IsAllowedExpression(parenthesized.Expression, result, model, loopLocals, captures, cancellationToken, out reason);
            if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NumericLiteralExpression))
                return IsNumeric(model.GetTypeInfo(literal, cancellationToken).Type);
            if (expression.IsKind(SyntaxKind.TrueLiteralExpression) || expression.IsKind(SyntaxKind.FalseLiteralExpression))
                return true;
            if (expression is ElementAccessExpressionSyntax element && TryGetArrayElement(element, model, cancellationToken, out var array))
            {
                RegisterArrayRead(array, result, loopLocals, captures);
                return IsAllowedExpression(element.ArgumentList.Arguments[0].Expression, result, model, loopLocals, captures, cancellationToken, out reason);
            }
            if (expression is IdentifierNameSyntax identifier)
            {
                var symbol = model.GetSymbolInfo(identifier, cancellationToken).Symbol as ILocalSymbol;
                if (symbol == null || !IsSupportedValue(symbol.Type, model.Compilation))
                {
                    reason = "識別子参照は、スカラー型または float2/3/4 のローカル変数に限ります。";
                    return false;
                }
                if (loopLocals.Contains(symbol)) return true;
                captures.Add(symbol);
                return true;
            }
            if (expression is MemberAccessExpressionSyntax member)
            {
                if (member.Name.Identifier.ValueText == "Length" && TryGetArrayLocal(member.Expression, model, cancellationToken, out var sourceArray) &&
                    model.GetSymbolInfo(member, cancellationToken).Symbol is IPropertySymbol length && length.ContainingType.Equals(sourceArray.Type))
                {
                    RegisterArrayRead(sourceArray, result, loopLocals, captures);
                    return true;
                }
                if (IsLocalTransform(model.GetTypeInfo(expression, cancellationToken).Type, model.Compilation) &&
                    TryGetComponentWrapper((MemberAccessExpressionSyntax)expression, result, model, cancellationToken, out _))
                    return true;
                if (IsSupportedValue(model.GetTypeInfo(expression, cancellationToken).Type, model.Compilation))
                {
                    if (TryGetComponentField(expression, result, model, cancellationToken, out _, out _))
                        return true;

                    var local = FindFieldRootLocal(expression, model, cancellationToken);
                    if (local != null)
                    {
                        if (!loopLocals.Contains(local)) captures.Add(local);
                        return true;
                    }

                    if (member.IsKind(SyntaxKind.SimpleMemberAccessExpression) &&
                        model.GetSymbolInfo(member, cancellationToken).Symbol is IFieldSymbol field && !field.IsStatic)
                        return IsAllowedExpression(member.Expression, result, model, loopLocals, captures, cancellationToken, out reason);
                }

                reason = "コンポーネントまたはローカルのスカラー型・float2/3/4 フィールドだけを読み取れます。";
                return false;
            }
            if (expression is InvocationExpressionSyntax invocation)
            {
                var method = model.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                var math = model.Compilation.GetTypeByMetadataName("Unity.Mathematics.math");
                if (method != null && method.IsStatic && IsUnityMathType(math) && method.ContainingType.Equals(math) &&
                    IsSupportedMathMethod(method.Name) && IsSupportedValue(method.ReturnType, model.Compilation) &&
                    method.Parameters.All(parameter => parameter.RefKind == RefKind.None && IsSupportedValue(parameter.Type, model.Compilation)))
                    return ValidateArguments(invocation.ArgumentList.Arguments, result, model, loopLocals, captures, cancellationToken, out reason);

                if (method != null && IsLocalTransform(method.ContainingType, model.Compilation) && method.ReturnType.Equals(method.ContainingType) &&
                    IsSupportedTransformMethod(method) && method.Parameters.All(parameter =>
                        parameter.RefKind == RefKind.None && IsSupportedValue(parameter.Type, model.Compilation)))
                {
                    if (!method.IsStatic && (!(invocation.Expression is MemberAccessExpressionSyntax receiver) ||
                        !IsAllowedExpression(receiver.Expression, result, model, loopLocals, captures, cancellationToken, out reason)))
                        return false;
                    return ValidateArguments(invocation.ArgumentList.Arguments, result, model, loopLocals, captures, cancellationToken, out reason);
                }

                if (method != null && !method.IsStatic && IsUnityMathType(method.ContainingType) &&
                    method.ContainingType.Equals(model.Compilation.GetTypeByMetadataName("Unity.Mathematics.Random")) &&
                    (method.Name == "NextFloat" || method.Name == "NextFloat2" || method.Name == "NextFloat3" || method.Name == "NextFloat4" ||
                     method.Name == "NextInt" || method.Name == "NextUInt" || method.Name == "NextBool") &&
                    invocation.Expression is MemberAccessExpressionSyntax access && access.Expression is IdentifierNameSyntax &&
                    model.GetSymbolInfo(access.Expression, cancellationToken).Symbol is ILocalSymbol random &&
                    random.Type.Equals(method.ContainingType))
                {
                    if (!ValidateArguments(invocation.ArgumentList.Arguments, result, model, loopLocals, captures, cancellationToken, out reason))
                        return false;
                    captures.Add(random);
                    result.UpdatedRandoms.Add(random);
                    result.RequiresRun = true;
                    return true;
                }
                reason = "未対応のメソッド呼び出しです。";
                return false;
            }
            if (expression is ObjectCreationExpressionSyntax creation && creation.Initializer == null &&
                model.GetSymbolInfo(creation, cancellationToken).Symbol is IMethodSymbol constructor &&
                IsVector(constructor.ContainingType, model.Compilation) && constructor.Parameters.All(parameter =>
                    parameter.RefKind == RefKind.None && IsSupportedValue(parameter.Type, model.Compilation)))
            {
                return ValidateArguments(creation.ArgumentList.Arguments, result, model, loopLocals, captures, cancellationToken, out reason);
            }
            if (expression is ConditionalExpressionSyntax conditional && IsSupportedValue(model.GetTypeInfo(conditional, cancellationToken).Type, model.Compilation))
                return IsAllowedExpression(conditional.Condition, result, model, loopLocals, captures, cancellationToken, out reason) &&
                       IsAllowedExpression(conditional.WhenTrue, result, model, loopLocals, captures, cancellationToken, out reason) &&
                       IsAllowedExpression(conditional.WhenFalse, result, model, loopLocals, captures, cancellationToken, out reason);
            if (expression is BinaryExpressionSyntax binary)
            {
                if (!IsSupportedBinary(binary.Kind()) ||
                    !IsAllowedOperator(model.GetSymbolInfo(binary, cancellationToken).Symbol, model.Compilation))
                {
                    reason = "未対応の二項演算子です。";
                    return false;
                }
                return IsAllowedExpression(binary.Left, result, model, loopLocals, captures, cancellationToken, out reason) &&
                       IsAllowedExpression(binary.Right, result, model, loopLocals, captures, cancellationToken, out reason);
            }
            if (expression is PrefixUnaryExpressionSyntax prefix &&
                (prefix.IsKind(SyntaxKind.UnaryPlusExpression) || prefix.IsKind(SyntaxKind.UnaryMinusExpression) ||
                 prefix.IsKind(SyntaxKind.LogicalNotExpression) || prefix.IsKind(SyntaxKind.BitwiseNotExpression)) &&
                IsAllowedOperator(model.GetSymbolInfo(prefix, cancellationToken).Symbol, model.Compilation))
                return IsAllowedExpression(prefix.Operand, result, model, loopLocals, captures, cancellationToken, out reason);
            if (expression is CastExpressionSyntax cast && !IsUserDefinedOperator(model.GetSymbolInfo(cast, cancellationToken).Symbol) &&
                IsNumeric(model.GetTypeInfo(cast, cancellationToken).Type) && IsNumeric(model.GetTypeInfo(cast.Expression, cancellationToken).Type))
                return IsAllowedExpression(cast.Expression, result, model, loopLocals, captures, cancellationToken, out reason);

            reason = "数値リテラル、許可されたローカル、コンポーネントフィールド、組み込み数値演算だけに対応します。";
            return false;
        }

        private static bool ValidateArguments(SeparatedSyntaxList<ArgumentSyntax> arguments, LoopAnalysisResult result, SemanticModel model,
            HashSet<ILocalSymbol> loopLocals, HashSet<ILocalSymbol> captures, CancellationToken cancellationToken, out string reason)
        {
            reason = null;
            foreach (var argument in arguments)
                if (!IsAllowedExpression(argument.Expression, result, model, loopLocals, captures, cancellationToken, out reason))
                    return false;
            return true;
        }

        internal static bool TryGetComponentField(ExpressionSyntax expression, LoopAnalysisResult result, SemanticModel model,
            CancellationToken cancellationToken, out IFieldSymbol field, out bool writable)
        {
            field = null;
            writable = false;
            var current = expression as MemberAccessExpressionSyntax;
            var count = 0;
            while (current != null)
            {
                if (!current.IsKind(SyntaxKind.SimpleMemberAccessExpression))
                    return false;
                if (TryGetComponentWrapper(current, result, model, cancellationToken, out writable))
                    return count > 0 && field != null;
                var member = model.GetSymbolInfo(current, cancellationToken).Symbol as IFieldSymbol;
                if (member == null || member.IsStatic || !member.Type.IsValueType || !member.Type.IsUnmanagedType)
                    return false;
                if (count == 0) field = member;
                count++;
                current = current.Expression as MemberAccessExpressionSyntax;
            }
            return false;
        }

        internal static bool TryGetComponentWrapper(MemberAccessExpressionSyntax wrapper, LoopAnalysisResult result,
            SemanticModel model, CancellationToken cancellationToken, out bool writable)
        {
            writable = false;
            var variable = wrapper.Expression as IdentifierNameSyntax;
            var local = variable == null ? null : model.GetSymbolInfo(variable, cancellationToken).Symbol as ILocalSymbol;
            var binding = result.Components.FirstOrDefault(component => component.Variable.Equals(local));
            if (binding == null) return false;
            var propertyName = wrapper.Name.Identifier.ValueText;
            if (propertyName != "ValueRO" && propertyName != "ValueRW") return false;
            if (!binding.IsReadWrite && propertyName != "ValueRO") return false;
            var property = model.GetSymbolInfo(wrapper, cancellationToken).Symbol as IPropertySymbol;
            if (property == null || !property.ContainingType.OriginalDefinition.Equals(binding.RefType)) return false;
            if (wrapper.DescendantTrivia().Any(trivia => trivia.SpanStart >= wrapper.SpanStart && trivia.Span.End <= wrapper.Span.End &&
                (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)))) return false;
            writable = binding.IsReadWrite && propertyName == "ValueRW";
            return true;
        }

        private static ILocalSymbol FindFieldRootLocal(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken)
        {
            while (true)
            {
                if (expression is ParenthesizedExpressionSyntax parenthesized)
                {
                    expression = parenthesized.Expression;
                    continue;
                }

                if (!(expression is MemberAccessExpressionSyntax member))
                    break;

                var field = model.GetSymbolInfo(member, cancellationToken).Symbol as IFieldSymbol;
                if (!member.IsKind(SyntaxKind.SimpleMemberAccessExpression) ||
                    field == null || field.IsStatic)
                    return null;

                expression = member.Expression;
            }

            return expression is IdentifierNameSyntax
                ? model.GetSymbolInfo(expression, cancellationToken).Symbol as ILocalSymbol
                : null;
        }

        private static bool IsValidCapture(ILocalSymbol local, LoopAnalysisResult result, SemanticModel model, CancellationToken cancellationToken)
        {
            var nativeContainer = model.Compilation.GetTypeByMetadataName("Unity.Collections.LowLevel.Unsafe.NativeContainerAttribute");
            if (local.RefKind != RefKind.None ||
                (!result.CommandBuffers.Contains(local) && !result.ReadOnlyArrays.Contains(local) &&
                 !IsJobValue(local.Type, nativeContainer, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default))))
                return false;
            if (local.IsConst)
            {
                foreach (var identifier in result.Body.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (!local.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol)) continue;
                    foreach (var expression in identifier.AncestorsAndSelf().OfType<ExpressionSyntax>())
                    {
                        var info = model.GetTypeInfo(expression, cancellationToken);
                        if (model.GetConstantValue(expression, cancellationToken).HasValue && info.Type != null && info.ConvertedType != null &&
                            !((CSharpCompilation)model.Compilation).ClassifyConversion(info.Type, info.ConvertedType).IsImplicit)
                            return false;
                    }
                }
            }
            var reference = local.DeclaringSyntaxReferences.FirstOrDefault();
            var declarator = reference?.GetSyntax(cancellationToken) as VariableDeclaratorSyntax;
            var declaration = declarator?.Parent?.Parent as LocalDeclarationStatementSyntax;
            if (declarator?.Initializer == null || declaration == null || declaration.Parent != result.OnUpdate.Body ||
                (result.UpdatedRandoms.Contains(local) && declaration.UsingKeyword != default(SyntaxToken)))
                return false;
            var declarationIndex = result.OnUpdate.Body.Statements.IndexOf(declaration);
            var loopIndex = result.OnUpdate.Body.Statements.IndexOf((StatementSyntax)result.Loop);
            return declarationIndex >= 0 && declarationIndex < loopIndex;
        }

        private static bool IsJobValue(ITypeSymbol type, INamedTypeSymbol nativeContainer, HashSet<ITypeSymbol> checkedTypes)
        {
            if (!(type is INamedTypeSymbol named) || !named.IsUnmanagedType || named.IsRefLikeType || named.MetadataName.StartsWith("<", StringComparison.Ordinal))
                return false;
            if (named.SpecialType != SpecialType.None || named.TypeKind == TypeKind.Enum || !checkedTypes.Add(named))
                return true;
            if (nativeContainer != null && HasAttribute(named, nativeContainer))
                return false;

            return named.GetMembers().OfType<IFieldSymbol>()
                .Where(field => !field.IsStatic)
                .All(field => IsJobValue(field.Type, nativeContainer, checkedTypes));
        }

        private static bool IsUserDefinedOperator(ISymbol symbol)
        {
            var method = symbol as IMethodSymbol;
            return method != null && (method.MethodKind == MethodKind.UserDefinedOperator || method.MethodKind == MethodKind.Conversion);
        }
        private static bool IsUnityMathType(INamedTypeSymbol type)
        {
            return type != null && type.ContainingAssembly.Name == "UnityEngine.MathematicsModule";
        }

        private static bool IsLocalTransform(ITypeSymbol type, Compilation compilation)
        {
            return type != null && type.ContainingAssembly.Name == "Unity.Transforms" &&
                   type.Equals(compilation.GetTypeByMetadataName("Unity.Transforms.LocalTransform"));
        }

        private static bool IsSupportedTransformMethod(IMethodSymbol method)
        {
            if (method.IsStatic)
                return method.Name == "FromPosition" || method.Name == "FromScale";

            return method.Name == "Translate" || method.Name == "WithPosition" || method.Name == "WithScale" ||
                   method.Name == "ApplyScale" || method.Name == "RotateX" || method.Name == "RotateY" || method.Name == "RotateZ";
        }

        private static bool IsVector(ITypeSymbol type, Compilation compilation)
        {
            if (!(type is INamedTypeSymbol named) || !IsUnityMathType(named)) return false;
            return new[] { "Unity.Mathematics.float2", "Unity.Mathematics.float3", "Unity.Mathematics.float4" }
                .Any(name => named.Equals(compilation.GetTypeByMetadataName(name)));
        }

        private static bool IsSupportedValue(ITypeSymbol type, Compilation compilation)
        {
            return IsScalar(type) || IsVector(type, compilation) ||
                   (type != null && type.Equals(compilation.GetTypeByMetadataName("Unity.Entities.Entity")));
        }

        private static bool IsAllowedOperator(ISymbol symbol, Compilation compilation)
        {
            if (!IsUserDefinedOperator(symbol)) return true;
            var method = (IMethodSymbol)symbol;
            if (method.MethodKind != MethodKind.UserDefinedOperator || !IsVector(method.ContainingType, compilation) ||
                !method.ReturnType.Equals(method.ContainingType) || method.Parameters.Any(parameter =>
                    !parameter.Type.Equals(method.ContainingType) && parameter.Type.SpecialType != SpecialType.System_Single))
                return false;
            return method.Name == "op_Addition" || method.Name == "op_Subtraction" || method.Name == "op_Multiply" ||
                   method.Name == "op_Division" || method.Name == "op_Modulus" ||
                   method.Name == "op_UnaryPlus" || method.Name == "op_UnaryNegation";
        }

        private static bool IsSupportedMathMethod(string name)
        {
            switch (name)
            {
                case "abs": case "min": case "max": case "clamp": case "saturate":
                case "lerp": case "unlerp": case "remap": case "step": case "smoothstep": case "select":
                case "dot": case "cross": case "distance": case "distancesq": case "length": case "lengthsq":
                case "normalize": case "normalizesafe": case "reflect": case "project": case "projectsafe":
                case "rcp": case "sqrt": case "rsqrt": case "pow": case "exp": case "log":
                case "sin": case "cos": case "tan": case "asin": case "acos": case "atan": case "atan2":
                case "round": case "floor": case "ceil": case "trunc": case "frac": case "sign":
                case "radians": case "degrees": case "float2": case "float3": case "float4":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsSupportedBinary(SyntaxKind kind)
        {
            return kind == SyntaxKind.AddExpression || kind == SyntaxKind.SubtractExpression || kind == SyntaxKind.MultiplyExpression ||
                   kind == SyntaxKind.DivideExpression || kind == SyntaxKind.ModuloExpression ||
                   kind == SyntaxKind.EqualsExpression || kind == SyntaxKind.NotEqualsExpression ||
                   kind == SyntaxKind.LessThanExpression || kind == SyntaxKind.LessThanOrEqualExpression ||
                   kind == SyntaxKind.GreaterThanExpression || kind == SyntaxKind.GreaterThanOrEqualExpression ||
                   kind == SyntaxKind.LogicalAndExpression || kind == SyntaxKind.LogicalOrExpression ||
                   kind == SyntaxKind.BitwiseAndExpression || kind == SyntaxKind.BitwiseOrExpression || kind == SyntaxKind.ExclusiveOrExpression ||
                   kind == SyntaxKind.LeftShiftExpression || kind == SyntaxKind.RightShiftExpression;
        }

        private static bool IsSupportedAssignment(SyntaxKind kind)
        {
            return kind == SyntaxKind.SimpleAssignmentExpression || kind == SyntaxKind.AddAssignmentExpression ||
                   kind == SyntaxKind.SubtractAssignmentExpression || kind == SyntaxKind.MultiplyAssignmentExpression ||
                   kind == SyntaxKind.DivideAssignmentExpression || kind == SyntaxKind.ModuloAssignmentExpression ||
                   kind == SyntaxKind.AndAssignmentExpression || kind == SyntaxKind.OrAssignmentExpression ||
                   kind == SyntaxKind.ExclusiveOrAssignmentExpression || kind == SyntaxKind.LeftShiftAssignmentExpression ||
                   kind == SyntaxKind.RightShiftAssignmentExpression;
        }

        private static bool IsScalar(ITypeSymbol type)
        {
            return type != null && (type.SpecialType == SpecialType.System_Boolean || IsNumeric(type));
        }

        private static bool IsNumeric(ITypeSymbol type)
        {
            if (type == null) return false;
            switch (type.SpecialType)
            {
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Decimal:
                    return true;
                default:
                    return false;
            }
        }
    }
}









