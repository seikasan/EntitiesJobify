using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EntitiesJobify.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class EntitiesJobifyAnalyzer : DiagnosticAnalyzer
    {
        public const string ConvertibleDiagnosticId = "EJ0001";
        public const string ReasonDiagnosticId = "EJ0002";

        private static readonly DiagnosticDescriptor ConvertibleRule = new DiagnosticDescriptor(
            ConvertibleDiagnosticId,
            "IJobEntity への変換が可能です",
            "この foreach を IJobEntity に変換できます",
            "EntitiesJobify",
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor ReasonRule = new DiagnosticDescriptor(
            ReasonDiagnosticId,
            "IJobEntity に変換できません",
            "IJobEntity への変換対象外です: {0}",
            "EntitiesJobify",
            DiagnosticSeverity.Info,
            isEnabledByDefault: false);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(ConvertibleRule, ReasonRule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeLoop, SyntaxKind.ForEachStatement, SyntaxKind.ForEachVariableStatement);
        }

        private static void AnalyzeLoop(SyntaxNodeAnalysisContext context)
        {
            var result = LoopAnalysis.Analyze(context.Node, context.SemanticModel, context.CancellationToken);
            if (!result.IsTarget)
                return;

            if (result.CanConvert)
            {
                var token = context.Node is ForEachStatementSyntax statement
                    ? statement.ForEachKeyword
                    : ((ForEachVariableStatementSyntax)context.Node).ForEachKeyword;
                context.ReportDiagnostic(Diagnostic.Create(ConvertibleRule, token.GetLocation()));
            }
            else
            {
                context.ReportDiagnostic(Diagnostic.Create(ReasonRule, context.Node.GetLocation(), result.Reason ?? "未対応の構文です。"));
            }
        }
    }
}




