using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EntitiesJobify.Analyzers;
using EntitiesJobify.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace EntitiesJobify.Tests
{
    public sealed class AnalyzerAndCodeFixTests
    {
        private const string EntitiesStubs = @"
using System;
using System.Collections.Generic;
namespace Unity.Burst
{
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Method)] public sealed class BurstCompileAttribute : Attribute { }
}
namespace Unity.Entities
{
    public interface ISystem { void OnUpdate(ref SystemState state); }
    public interface IComponentData { }
    public interface ISharedComponentData : IComponentData { }
    public interface IEnableableComponent { }
    [AttributeUsage(AttributeTargets.Struct)] public sealed class DisableAutoCreationAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Struct)] public sealed class RequireMatchingQueriesForUpdateAttribute : Attribute { }
    public struct JobHandle { }
    public struct SystemState { public EntityManager EntityManager { get; } public JobHandle Dependency { get; set; } }
    public sealed class EntityManager
    {
        public void CompleteDependencyBeforeRO<T>() { }
        public void CompleteDependencyBeforeRW<T>() { }
    }
    public sealed class RefRW<T> { private T _value; public ref T ValueRW => ref _value; public ref readonly T ValueRO => ref _value; }
    public sealed class RefRO<T> { private T _value; public ref readonly T ValueRO => ref _value; }
    public interface IJobEntity { }
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)] public sealed class WithAllAttribute : Attribute { public WithAllAttribute(params Type[] types) { } }
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)] public sealed class WithNoneAttribute : Attribute { public WithNoneAttribute(params Type[] types) { } }
    public static class IJobEntityExtensions
    {
        public static void Run<T>(this T job) where T : IJobEntity { }
        public static JobHandle Schedule<T>(this T job, JobHandle dependsOn) where T : unmanaged, IJobEntity => default;
        public static JobHandle ScheduleParallel<T>(this T job, JobHandle dependsOn) where T : unmanaged, IJobEntity => default;
    }
    public struct QueryEnumerable<T> : IEnumerable<T>
    {
        public QueryEnumerable<T> WithAll<TTag>() => this;
        public QueryEnumerable<T> WithNone<TTag>() => this;
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Array.Empty<T>()).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    public struct QueryEnumerable<T1, T2, T3> : IEnumerable<(T1, T2, T3)>
    {
        public QueryEnumerable<T1, T2, T3> WithAll<TTag>() => this;
        public QueryEnumerable<T1, T2, T3> WithNone<TTag>() => this;
        public IEnumerator<(T1, T2, T3)> GetEnumerator() => ((IEnumerable<(T1, T2, T3)>)Array.Empty<(T1, T2, T3)>()).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    public static class SystemAPI
    {
        public static TimeData Time => default;
        public static QueryEnumerable<T> Query<T>() => default;
        public static IEnumerable<(TFirst, TSecond)> Query<TFirst, TSecond>() { yield break; }
        public static QueryEnumerable<T1, T2, T3> Query<T1, T2, T3>() => default;
        public static IEnumerable<(T1, T2, T3, T4)> Query<T1, T2, T3, T4>() { yield break; }
        public static IEnumerable<(T1, T2, T3, T4, T5)> Query<T1, T2, T3, T4, T5>() { yield break; }
        public static IEnumerable<(T1, T2, T3, T4, T5, T6)> Query<T1, T2, T3, T4, T5, T6>() { yield break; }
        public static IEnumerable<(T1, T2, T3, T4, T5, T6, T7)> Query<T1, T2, T3, T4, T5, T6, T7>() { yield break; }
    }
    public struct TimeData { public float DeltaTime; }
    public static class QueryExtensions
    {
        public static IEnumerable<T> WithAll<T>(this IEnumerable<T> source) => source;
    }
}
namespace EntitiesJobify.Validation
{
    public struct JobifyPosition : Unity.Entities.IComponentData { public float X; }
    public struct JobifyVelocity : Unity.Entities.IComponentData { public float X; }
    public partial struct VelocityWriterSystem { }
}";

        [Fact]
        public async Task ConvertsComplexMovementSampleThroughCodeAction()
        {
            var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
                "Packages", "com.seikasan.entities-jobify", "Samples~", "ComplexMovement", "ComplexMovementSystem.cs"));
            var source = File.ReadAllText(samplePath).Replace("namespace EntitiesJobify.Sample.ComplexMovement", "namespace EntitiesJobify.Validation.Complex");
            var (workspace, document) = CreateWorkspace(source, includeUnityMath: true);
            var compilation = (CSharpCompilation)await document.Project.GetCompilationAsync();
            Assert.Empty(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            var diagnostic = Assert.Single((await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
            var actions = new List<CodeAction>();
            await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
                (action, _) => actions.Add(action), CancellationToken.None));
            var operations = await GetAction(actions, "IJobEntity に変換（Run）").GetOperationsAsync(CancellationToken.None);
            var changed = Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution.GetDocument(document.Id);
            changed = await Formatter.FormatAsync(changed, cancellationToken: CancellationToken.None);
            var output = (await changed.GetTextAsync()).ToString();
            Assert.Contains("[WithAll(typeof(MovingTag))]", output, StringComparison.Ordinal);
            Assert.Contains("[WithNone(typeof(PausedTag))]", output, StringComparison.Ordinal);
            Assert.Contains("transform.Position += step;", output, StringComparison.Ordinal);
            Assert.Contains("return;", output, StringComparison.Ordinal);
            var changedCompilation = (CSharpCompilation)await changed.Project.GetCompilationAsync();
            Assert.Empty(changedCompilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            var fixtureOutput = Environment.GetEnvironmentVariable("ENTITIES_JOBIFY_COMPLEX_FIXTURE_OUTPUT");
            if (!string.IsNullOrWhiteSpace(fixtureOutput))
                File.WriteAllText(fixtureOutput, output);
            workspace.Dispose();
        }

        [Fact]
        public void RejectsConstantCaptureWhenTheConversionRequiresAConstantExpression()
        {
            var source = SingleComponentSystem("public struct Position : IComponentData { public float X; }",
                "const int delta = 1; foreach (var position in SystemAPI.Query<RefRW<Position>>()) { byte amount = delta + 1; position.ValueRW.X += amount; }");
            var result = AnalyzeFirstLoop(source);
            Assert.True(result.IsTarget);
            Assert.False(result.CanConvert);
        }

        [Fact]
        public async Task ConvertsThreeComponentsWithAllAndNoneTags()
        {
            const string source = @"
using Unity.Entities;
namespace Demo.Tags
{
    public struct Position : IComponentData { public float X; }
    public struct Velocity : IComponentData { public float X; }
    public struct Settings : IComponentData { public bool Enabled; }
    public struct Moving : IComponentData { }
    public struct Paused : IComponentData { }
    [DisableAutoCreation]
    public partial struct MoveSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (position, velocity, settings) in SystemAPI.Query<RefRW<Position>, RefRO<Velocity>, RefRO<Settings>>().WithAll<Moving>().WithNone<Paused>())
            {
                if (!settings.ValueRO.Enabled) continue;
                if (velocity.ValueRO.X >= 0f)
                    position.ValueRW.X += velocity.ValueRO.X;
                else
                    position.ValueRW.X -= velocity.ValueRO.X;
            }
        }
    }
}";
            var (workspace, document) = CreateWorkspace(source);
            var compilation = (CSharpCompilation)await document.Project.GetCompilationAsync();
            var diagnostic = Assert.Single((await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
            var actions = new List<CodeAction>();
            await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(
                document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));
            var operations = await GetAction(actions, "IJobEntity に変換（Run）").GetOperationsAsync(CancellationToken.None);
            var changed = Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution.GetDocument(document.Id);
            changed = await Formatter.FormatAsync(changed, cancellationToken: CancellationToken.None);
            var output = (await changed.GetTextAsync()).ToString();
            Assert.Contains("[WithAll(typeof(Moving))]", output, StringComparison.Ordinal);
            Assert.Contains("[WithNone(typeof(Paused))]", output, StringComparison.Ordinal);
            Assert.Contains("if (!settings.Enabled)", output, StringComparison.Ordinal);
            var changedCompilation = (CSharpCompilation)await changed.Project.GetCompilationAsync();
            Assert.Empty(changedCompilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            var fixtureOutput = Environment.GetEnvironmentVariable("ENTITIES_JOBIFY_TAG_FIXTURE_OUTPUT");
            if (!string.IsNullOrWhiteSpace(fixtureOutput))
                File.WriteAllText(fixtureOutput, output);
            workspace.Dispose();
        }

        [Theory]
        [InlineData("public struct Tag : IComponentData, IEnableableComponent { }", ".WithAll<Tag>()")]
        [InlineData("public struct Tag : IComponentData { }", ".WithAll<Tag>().WithNone<Tag>()")]
        [InlineData("public struct Tag : IComponentData { public int X; }", ".WithAll<Tag>()")]
        public void RejectsUnsupportedTagFilters(string declaration, string filters)
        {
            var source = SingleComponentSystem("public struct Position : IComponentData { public float X; } " + declaration,
                "foreach (var position in SystemAPI.Query<RefRW<Position>>()" + filters + ") { position.ValueRW.X += 1f; }");
            var result = AnalyzeFirstLoop(source);
            Assert.True(result.IsTarget);
            Assert.False(result.CanConvert);
        }

        [Fact]
        public void RejectsAConflictingFilterAttribute()
        {
            var source = SingleComponentSystem(
                "public struct Position : IComponentData { public float X; } public struct Tag : IComponentData { } public sealed class WithAllAttribute : System.Attribute { }",
                "foreach (var position in SystemAPI.Query<RefRW<Position>>().WithAll<Tag>()) { position.ValueRW.X += 1f; }");
            var result = AnalyzeFirstLoop(source);
            Assert.True(result.IsTarget);
            Assert.False(result.CanConvert);
            Assert.Contains("衝突", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsUnrelatedMathMethodsWithTheSameName()
        {
            var source = SingleComponentSystem(
                "public struct Position : IComponentData { public float X; } public static class math { public static float lengthsq(float value) => value; }",
                "foreach (var position in SystemAPI.Query<RefRW<Position>>()) { position.ValueRW.X += math.lengthsq(position.ValueRO.X); }");
            var result = AnalyzeFirstLoop(source);
            Assert.True(result.IsTarget);
            Assert.False(result.CanConvert);
        }

        [Theory]
        [InlineData("foreach (var (a, b) in SystemAPI.Query<RefRO<Position>, RefRO<Velocity>>()) { var x = a.ValueRO.X + b.ValueRO.X; }")]
        [InlineData("foreach (var (a, _) in SystemAPI.Query<RefRW<Position>, RefRO<Velocity>>()) { a.ValueRW.X += 1f; }")]
        public void RejectsAllReadOnlyAndDiscardQueries(string loop)
        {
            var source = SingleComponentSystem(
                "public struct Position : IComponentData { public float X; } public struct Velocity : IComponentData { public float X; }", loop);
            var result = AnalyzeFirstLoop(source);
            Assert.True(result.IsTarget);
            Assert.False(result.CanConvert);
        }

        [Fact]
        public async Task ConvertsSevenDistinctReadOnlyAndReadWriteComponentsInOrder()
        {
            const string source = @"
using Unity.Entities;
namespace Demo
{
    public struct A : IComponentData { public float X; }
    public struct B : IComponentData { public float X; }
    public struct C : IComponentData { public float X; }
    public struct D : IComponentData { public float X; }
    public struct E : IComponentData { public float X; }
    public struct F : IComponentData { public float X; }
    public struct G : IComponentData { public float X; }
    [DisableAutoCreation]
    public partial struct MoveSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (a, b, c, d, e, f, g) in SystemAPI.Query<RefRO<A>, RefRW<B>, RefRO<C>, RefRW<D>, RefRO<E>, RefRO<F>, RefRO<G>>())
            {
                b.ValueRW.X += a.ValueRO.X;
                d.ValueRW.X += c.ValueRO.X + e.ValueRO.X + f.ValueRO.X + g.ValueRO.X;
            }
        }
    }
}";
            var (workspace, document) = CreateWorkspace(source);
            var compilation = (CSharpCompilation)await document.Project.GetCompilationAsync();
            var diagnostic = Assert.Single((await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
            var actions = new List<CodeAction>();
            await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(
                document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));
            var operations = await GetAction(actions, "IJobEntity に変換（Run）").GetOperationsAsync(CancellationToken.None);
            var changed = Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution.GetDocument(document.Id);
            changed = await Formatter.FormatAsync(changed, cancellationToken: CancellationToken.None);
            var output = (await changed.GetTextAsync()).ToString();
            Assert.Contains("in A a, ref B b, in C c, ref D d", output, StringComparison.Ordinal);
            Assert.Contains("CompleteDependencyBeforeRO<G>", output, StringComparison.Ordinal);
            var changedCompilation = (CSharpCompilation)await changed.Project.GetCompilationAsync();
            Assert.Empty(changedCompilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            var fixtureOutput = Environment.GetEnvironmentVariable("ENTITIES_JOBIFY_SEVEN_FIXTURE_OUTPUT");
            if (!string.IsNullOrWhiteSpace(fixtureOutput))
                File.WriteAllText(fixtureOutput, output);
            workspace.Dispose();
        }

        [Fact]
        public void ConvertsNestedFieldsAndUnityMathematicsOperations()
        {
            const string source = @"
using Unity.Entities;
using Unity.Mathematics;
namespace Demo
{
    public struct Data { public float3 Value; }
    public struct Position : IComponentData { public Data Nested; }
    public partial struct MoveSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var delta = 0.5f;
            foreach (var position in SystemAPI.Query<RefRW<Position>>())
            {
                var step = position.ValueRO.Nested.Value * delta;
                if (math.lengthsq(step) > 1f)
                    step = math.normalizesafe(step);
                position.ValueRW.Nested.Value += step;
            }
        }
    }
}";
            var unityManaged = UnityManagedPath();
            var compilation = CreateCompilation(source).AddReferences(
                MetadataReference.CreateFromFile(Path.Combine(unityManaged, "UnityEngine.MathematicsModule.dll")),
                MetadataReference.CreateFromFile(Path.Combine(unityManaged, "UnityEngine.ScriptingModule.dll")));
            Assert.Empty(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            var tree = compilation.SyntaxTrees.Last();
            var loop = tree.GetRoot().DescendantNodes().OfType<ForEachStatementSyntax>().Single();
            var analysis = LoopAnalysis.Analyze(loop, compilation.GetSemanticModel(tree), CancellationToken.None);
            Assert.True(analysis.CanConvert, analysis.Reason);
            var output = EntitiesJobifyCodeFixProvider.Transform(tree.GetRoot(), loop, compilation.GetSemanticModel(tree), CancellationToken.None).ToFullString();
            Assert.Contains("position.Nested.Value", output, StringComparison.Ordinal);
            Assert.Contains("global::Unity.Mathematics.math.normalizesafe(step)", output, StringComparison.Ordinal);
            Assert.Empty(compilation.RemoveSyntaxTrees(tree).AddSyntaxTrees(CSharpSyntaxTree.ParseText(output))
                .GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        }

        [Theory]
        [InlineData("float2")]
        [InlineData("float3")]
        [InlineData("float4")]
        public void ConvertsSupportedVectorTypes(string vectorType)
        {
            var source = SingleComponentSystem(
                "public struct Position : IComponentData { public Unity.Mathematics." + vectorType + " Value; }",
                "foreach (var position in SystemAPI.Query<RefRW<Position>>()) { var value = position.ValueRO.Value; position.ValueRW.Value = (value + value) * 0.5f; }");
            var unityManaged = UnityManagedPath();
            var compilation = CreateCompilation(source).AddReferences(
                MetadataReference.CreateFromFile(Path.Combine(unityManaged, "UnityEngine.MathematicsModule.dll")),
                MetadataReference.CreateFromFile(Path.Combine(unityManaged, "UnityEngine.ScriptingModule.dll")));
            var tree = compilation.SyntaxTrees.Last();
            var loop = tree.GetRoot().DescendantNodes().OfType<ForEachStatementSyntax>().Single();
            var analysis = LoopAnalysis.Analyze(loop, compilation.GetSemanticModel(tree), CancellationToken.None);
            Assert.True(analysis.CanConvert, analysis.Reason);
            var output = EntitiesJobifyCodeFixProvider.Transform(tree.GetRoot(), loop, compilation.GetSemanticModel(tree), CancellationToken.None).ToFullString();
            Assert.Empty(compilation.RemoveSyntaxTrees(tree).AddSyntaxTrees(CSharpSyntaxTree.ParseText(output))
                .GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        }

        [Theory]
        [InlineData("Unity.Mathematics.float2")]
        [InlineData("Unity.Mathematics.float3")]
        [InlineData("Unity.Mathematics.float4")]
        [InlineData("bool")]
        public async Task CapturesSupportedValuesDeclaredBeforeTheLoop(string valueType)
        {
            var source = SingleComponentSystem(
                $"public struct Component : IComponentData {{ public {valueType} Value; }}",
                $@"var initialValue = default({valueType});
            foreach (var component in SystemAPI.Query<RefRW<Component>>())
            {{
                component.ValueRW.Value = initialValue;
            }}");
            var (testWorkspace, document) = CreateWorkspace(source, includeUnityMath: true);
            using var workspace = testWorkspace;
            var compilation = (CSharpCompilation)await document.Project.GetCompilationAsync();
            var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
            var diagnostic = Assert.Single(diagnostics, item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId);
            var actions = new List<CodeAction>();
            await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(
                document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));

            foreach (var mode in new[] { "Run", "Schedule", "ScheduleParallel" })
            {
                var operations = await GetAction(actions, $"IJobEntity に変換（{mode}）").GetOperationsAsync(CancellationToken.None);
                var changed = Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution.GetDocument(document.Id);
                var output = (await changed.GetTextAsync()).ToString();
                Assert.Contains("Captured_initialValue = initialValue", output, StringComparison.Ordinal);
                Assert.Matches(@"component\.Value\s*=\s*(?:this\.)?Captured_initialValue\b", output);
                var changedCompilation = (CSharpCompilation)await changed.Project.GetCompilationAsync();
                Assert.Empty(changedCompilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            }
        }

        [Fact]
        public async Task CapturesUnmanagedStructAndRewritesBothCopyAndNestedFieldRead()
        {
            var source = SingleComponentSystem(
                "public struct Component : IComponentData { public Settings Config; public float Speed; } " +
                "public struct Settings { public Rates Rates; } public struct Rates { public float Speed; }",
                @"var settings = new Settings { Rates = new Rates { Speed = 2f } };
            foreach (var component in SystemAPI.Query<RefRW<Component>>())
            {
                component.ValueRW.Config = settings;
                component.ValueRW.Speed += (settings.Rates).Speed;
            }");

            var (testWorkspace, document) = CreateWorkspace(source);
            using var workspace = testWorkspace;
            var compilation = (CSharpCompilation)await document.Project.GetCompilationAsync();
            var diagnostic = Assert.Single(await compilation.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer())).GetAnalyzerDiagnosticsAsync());
            Assert.Equal(EntitiesJobifyAnalyzer.ConvertibleDiagnosticId, diagnostic.Id);

            var actions = new List<CodeAction>();
            await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(
                document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));

            var operations = await GetAction(actions, "IJobEntity に変換（ScheduleParallel）").GetOperationsAsync(CancellationToken.None);
            var changed = Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution.GetDocument(document.Id);
            var changedCompilation = (CSharpCompilation)await changed.Project.GetCompilationAsync();
            Assert.Empty(changedCompilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            var output = (await changed.GetTextAsync()).ToString();
            Assert.Contains("Captured_settings = settings", output, StringComparison.Ordinal);
            Assert.Matches(@"component\.Config\s*=\s*(?:this\.)?Captured_settings\b", output);
            Assert.Matches(@"component\.Speed\s*\+=\s*\(?(?:this\.)?Captured_settings\.Rates\)?\.Speed", output);
        }

        [Theory]
        [InlineData("public struct Settings { public float Speed; private string Label; }")]
        [InlineData("file struct Settings { public float Speed; }")]
        [InlineData("public ref struct Settings { public float Speed; }")]
        [InlineData("public unsafe struct Settings { public float Speed; private int* Pointer; }")]
        [InlineData("public struct Settings { public float Speed; private Container Storage; } " +
            "[Unity.Collections.LowLevel.Unsafe.NativeContainer] public struct Container { public int Value; }")]
        public void RejectsStructsThatCannotBeCapturedAsJobValues(string settingsDeclaration)
        {
            var source = SingleComponentSystem(
                "public struct Component : IComponentData { public float Speed; } " + settingsDeclaration,
                @"Settings settings = default;
            foreach (var component in SystemAPI.Query<RefRW<Component>>())
            {
                component.ValueRW.Speed = settings.Speed;
            }") + @"
namespace Unity.Collections.LowLevel.Unsafe
{
    public sealed class NativeContainerAttribute : System.Attribute { }
}";
            var compilation = CreateCompilation(source).WithOptions(new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            Assert.Empty(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            var tree = compilation.SyntaxTrees.Last();
            var loop = tree.GetRoot().DescendantNodes().OfType<ForEachStatementSyntax>().Single();
            var analysis = LoopAnalysis.Analyze(loop, compilation.GetSemanticModel(tree), CancellationToken.None);
            Assert.True(analysis.IsTarget);
            Assert.False(analysis.CanConvert);
        }

        [Fact]
        public void RejectsPointerAccessThroughAComponent()
        {
            var source = SingleComponentSystem(
                "public struct Data { public float Value; } public unsafe struct Position : IComponentData { public Data* Ptr; }",
                "foreach (var position in SystemAPI.Query<RefRW<Position>>()) { position.ValueRW.Ptr->Value += 1f; }")
                .Replace("public partial struct MoveSystem", "public unsafe partial struct MoveSystem");
            var compilation = CreateCompilation(source).WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            var tree = compilation.SyntaxTrees.Last();
            Assert.Empty(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            var loop = tree.GetRoot().DescendantNodes().OfType<ForEachStatementSyntax>().Single();
            var result = LoopAnalysis.Analyze(loop, compilation.GetSemanticModel(tree), CancellationToken.None);
            Assert.True(result.IsTarget);
            Assert.False(result.CanConvert);
        }

        [Fact]
        public void ConvertsBranchesLocalAssignmentsAndContinue()
        {
            var source = SingleComponentSystem(
                "public struct Position : IComponentData { public float X; public bool Enabled; }",
                @"foreach (var position in SystemAPI.Query<RefRW<Position>>())
                {
                    if (!position.ValueRO.Enabled || position.ValueRO.X < 0f)
                        continue /* skip */;
                    var amount = 1f;
                    if (position.ValueRO.X > 5f && position.ValueRO.Enabled) { amount = 2f; }
                    else { amount += 3f; }
                    position.ValueRW.X += amount;
                }");
            var compilation = CreateCompilation(source);
            var tree = compilation.SyntaxTrees.Last();
            var loop = tree.GetRoot().DescendantNodes().OfType<ForEachStatementSyntax>().Single();
            var analysis = LoopAnalysis.Analyze(loop, compilation.GetSemanticModel(tree), CancellationToken.None);
            Assert.True(analysis.CanConvert, analysis.Reason);
            var output = EntitiesJobifyCodeFixProvider.Transform(tree.GetRoot(), loop, compilation.GetSemanticModel(tree), CancellationToken.None).ToFullString();
            Assert.Contains("return /* skip */;", output, StringComparison.Ordinal);
            Assert.Contains("position.Enabled", output, StringComparison.Ordinal);
            Assert.DoesNotContain("continue", output, StringComparison.Ordinal);
            Assert.Empty(compilation.RemoveSyntaxTrees(tree).AddSyntaxTrees(CSharpSyntaxTree.ParseText(output))
                .GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        }

        [Theory]
        [InlineData("break;")]
        [InlineData("return;")]
        [InlineData("foreach (var value in new[] { 1 }) { continue; }")]
        public void RejectsControlFlowThatCannotBeMovedToExecute(string controlFlow)
        {
            var source = SingleComponentSystem("public struct Position : IComponentData { public float X; }",
                "foreach (var position in SystemAPI.Query<RefRW<Position>>()) { position.ValueRW.X += 1f; " + controlFlow + " }");
            var result = AnalyzeFirstLoop(source);
            Assert.True(result.IsTarget);
            Assert.False(result.CanConvert);
        }

        [Theory]
        [InlineData("IJobEntity に変換（Run）")]
        [InlineData("IJobEntity に変換（Schedule）")]
        [InlineData("IJobEntity に変換（ScheduleParallel）")]
        public async Task ConvertsPerEntityLoopsAndMathWithoutChangingControlFlow(string actionTitle)
        {
            var source = "using Unity.Mathematics;\nusing EntitiesJobifyJob = Unity.Mathematics.float3;\n" + SingleComponentSystem(
                "public struct Position : IComponentData { public float3 Value; public int Steps; public int Flags; }",
                @"var limit = 5;
                foreach (var position in SystemAPI.Query<RefRW<Position>>())
                {
                    if (position.ValueRO.Steps == 0) continue;
                    var value = new EntitiesJobifyJob(1f, 2f, 3f);
                    for (var i = 0; i < limit; i++)
                    {
                        if (i % 2 == 0) continue;
                        if (i >= position.ValueRO.Steps) break;
                        value.x += math.clamp(math.dot(value, value) / 10f, 0f, 1f);
                    }
                    var countdown = limit;
                    while (countdown > position.ValueRO.Steps) --countdown;
                    do { countdown++; } while (countdown < limit);
                    position.ValueRW.Flags |= 1 << countdown;
                    position.ValueRW.Steps++;
                    position.ValueRW.Value = (countdown > 0 ? math.normalize(value) : -value) * value;
                    position.ValueRW.Value /= 2f;
                }");
            var (workspace, document) = CreateWorkspace(source, includeUnityMath: true);
            using (workspace)
            {
                var compilation = (CSharpCompilation)await document.Project.GetCompilationAsync();
                Assert.Empty(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
                var diagnostic = Assert.Single((await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                    .GetAnalyzerDiagnosticsAsync()).Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
                var actions = new List<CodeAction>();
                await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
                    (action, _) => actions.Add(action), CancellationToken.None));
                var operations = await GetAction(actions, actionTitle).GetOperationsAsync(CancellationToken.None);
                var changed = Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution.GetDocument(document.Id);
                var root = await changed.GetSyntaxRootAsync();
                var execute = Assert.Single(root.DescendantNodes().OfType<MethodDeclarationSyntax>(), method => method.Identifier.ValueText == "Execute");
                var innerLoop = Assert.Single(execute.DescendantNodes().OfType<ForStatementSyntax>());
                Assert.Single(innerLoop.DescendantNodes().OfType<ContinueStatementSyntax>());
                Assert.Single(innerLoop.DescendantNodes().OfType<BreakStatementSyntax>());
                Assert.Single(execute.DescendantNodes().OfType<ReturnStatementSyntax>());
                Assert.DoesNotContain(".ValueRW", execute.ToString(), StringComparison.Ordinal);
                var changedCompilation = await changed.Project.GetCompilationAsync();
                Assert.Empty(changedCompilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            }
        }

        [Fact]
        public void RejectsUpdatingAnOuterLocalFromAnInnerLoop()
        {
            var source = SingleComponentSystem("public struct Position : IComponentData { public int Count; }",
                @"var count = 0;
                foreach (var position in SystemAPI.Query<RefRW<Position>>())
                {
                    while (count < 5) count++;
                    position.ValueRW.Count = count;
                }");
            var result = AnalyzeFirstLoop(source);
            Assert.True(result.IsTarget);
            Assert.False(result.CanConvert);
        }

        [Theory]
        [InlineData("Run", "IJobEntity に変換（Run）")]
        [InlineData("Parallel", "IJobEntity に変換（ScheduleParallel・エンティティ別の乱数）")]
        public async Task ConvertsSpawningWithAnEcbAndRandom(string mode, string actionTitle)
        {
            var path = Path.Combine(ValidationFixtureDirectory(), "SpawnQuerySystem.cs");
            var source = File.ReadAllText(path);
            var (testWorkspace, document) = CreateUnityWorkspace(path, source);
            using var workspace = testWorkspace;
            var compilation = await document.Project.GetCompilationAsync();
            Assert.Empty(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            var diagnostic = Assert.Single((await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
            var actions = new List<CodeAction>();
            await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
                (action, _) => actions.Add(action), CancellationToken.None));
            Assert.Equal(2, actions.Count);
            var operations = await GetAction(actions, actionTitle).GetOperationsAsync(CancellationToken.None);
            var changed = Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution.GetDocument(document.Id);
            changed = await Formatter.FormatAsync(changed, cancellationToken: CancellationToken.None);
            var output = (await changed.GetTextAsync()).ToString();
            Assert.Contains("in Spawner spawner", output, StringComparison.Ordinal);
            Assert.Contains("state.Enabled = false;", output, StringComparison.Ordinal);
            Assert.DoesNotContain("spawner.ValueRO", output, StringComparison.Ordinal);
            if (mode == "Parallel")
            {
                Assert.Contains("ecb.AsParallelWriter()", output, StringComparison.Ordinal);
                Assert.Contains("Instantiate(entityIndexInQuery,", output, StringComparison.Ordinal);
                Assert.Contains("SetComponent(entityIndexInQuery,", output, StringComparison.Ordinal);
                Assert.Contains("random.NextFloat3(", output, StringComparison.Ordinal);
            }
            var changedCompilation = await changed.Project.GetCompilationAsync();
            Assert.Empty(changedCompilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));

            var sourceRoot = (CompilationUnitSyntax)await changed.GetSyntaxRootAsync();
            var fixture = sourceRoot.RemoveNode(sourceRoot.DescendantNodes().OfType<StructDeclarationSyntax>()
                .Single(declaration => declaration.Identifier.ValueText == "Spawner"), SyntaxRemoveOptions.KeepNoTrivia);
            var fixtureText = fixture.ToFullString().Replace("partial struct SpawnQuerySystem", "partial struct Spawn" + mode + "System");
            var fixtureName = "CodeFixSpawn" + mode + "System.cs";
            var fixtureDirectory = Environment.GetEnvironmentVariable("ENTITIES_JOBIFY_SPAWN_FIXTURE_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(fixtureDirectory))
                File.WriteAllText(Path.Combine(fixtureDirectory, fixtureName), fixtureText);
            else
                Assert.True(SyntaxFactory.AreEquivalent(
                    CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path), fixtureName))).GetRoot(),
                    CSharpSyntaxTree.ParseText(fixtureText).GetRoot()));

            foreach (var suffix in new[] { "ecb.Playback(state.EntityManager);", "state.Enabled = random.NextBool();" })
            {
                var unsafeDocument = document.WithText(SourceText.From(source.Replace("state.Enabled = false;", suffix)));
                var unsafeModel = await unsafeDocument.GetSemanticModelAsync();
                var loop = (await unsafeDocument.GetSyntaxRootAsync()).DescendantNodes().OfType<ForEachStatementSyntax>().First();
                var analysis = LoopAnalysis.Analyze(loop, unsafeModel, CancellationToken.None);
                Assert.False(analysis.CanScheduleEcbInParallel);
                Assert.Equal(suffix.StartsWith("ecb.", StringComparison.Ordinal), analysis.CanConvert);
            }
        }

        [Theory]
        [InlineData("Run", "CodeFixNativeArraySystem.cs")]
        [InlineData("ScheduleParallel", "CodeFixNativeArrayParallelSystem.cs")]
        public async Task ConvertsNativeArrayReadsAndPerEntityScratchWrites(string mode, string fileName)
        {
            var directory = ValidationFixtureDirectory();
            var path = Path.Combine(directory, "NativeArrayQuerySystem.cs");
            var source = File.ReadAllText(path);
            var (testWorkspace, document) = CreateUnityWorkspace(path, source);
            using var workspace = testWorkspace;
            var compilation = await document.Project.GetCompilationAsync();
            Assert.Empty(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            var diagnostic = Assert.Single((await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
            var actions = new List<CodeAction>();
            await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
                (action, _) => actions.Add(action), CancellationToken.None));
            Assert.Equal(2, actions.Count);
            var action = GetAction(actions, "IJobEntity に変換（" + mode + "）");
            var operations = await action.GetOperationsAsync(CancellationToken.None);
            var changed = Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution.GetDocument(document.Id);
            changed = await Formatter.FormatAsync(changed, cancellationToken: CancellationToken.None);
            var output = (await changed.GetTextAsync()).ToString();
            Assert.Contains("[ReadOnly]", output, StringComparison.Ordinal);
            Assert.Contains("Captured_weights.Length", output, StringComparison.Ordinal);
            Assert.Contains("samples[index] = Captured_weights[", output, StringComparison.Ordinal);
            Assert.Contains("if (sample <= 0f) continue;", output, StringComparison.Ordinal);
            if (mode == "ScheduleParallel")
            {
                Assert.Contains(".ScheduleParallel(state.Dependency);", output, StringComparison.Ordinal);
                Assert.Contains("state.Dependency.Complete();", output, StringComparison.Ordinal);
                var scope = (BlockSyntax)(await changed.GetSyntaxRootAsync()).DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Single(method => method.Identifier.ValueText == "OnUpdate").Body.Statements[1];
                Assert.Contains("ScheduleParallel", scope.Statements[0].ToString(), StringComparison.Ordinal);
                Assert.Equal("state.Dependency.Complete();", scope.Statements[1].ToString());
            }
            var changedCompilation = await changed.Project.GetCompilationAsync();
            Assert.Empty(changedCompilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));

            var sourceRoot = await changed.GetSyntaxRootAsync();
            var fixture = sourceRoot.RemoveNode(sourceRoot.DescendantNodes().OfType<StructDeclarationSyntax>()
                .Single(declaration => declaration.Identifier.ValueText == "ArrayInput"), SyntaxRemoveOptions.KeepNoTrivia);
            var fixtureText = fixture.ToFullString().Replace("partial struct NativeArrayQuerySystem", "partial struct NativeArray" + mode + "System");
            var fixturePath = Path.Combine(directory, fileName);
            if (Environment.GetEnvironmentVariable("ENTITIES_JOBIFY_WRITE_NATIVE_ARRAY_FIXTURE") == "1")
                File.WriteAllText(fixturePath, fixtureText);
            else
                Assert.True(SyntaxFactory.AreEquivalent(CSharpSyntaxTree.ParseText(File.ReadAllText(fixturePath)).GetRoot(),
                    CSharpSyntaxTree.ParseText(fixtureText).GetRoot()));

            foreach (var variant in new[]
            {
                (Source: source.Replace("Allocator.TempJob", "Allocator.Temp"), Convertible: false, RequiresRun: false),
                (Source: source.Replace("samples[index] =", "weights[index] ="), Convertible: false, RequiresRun: false),
                (Source: source.Replace("using var weights", "var weights"), Convertible: false, RequiresRun: false),
                (Source: source.Replace("weights.Length", "3").Replace("weights[(item.ValueRO.Offset + index) % 3]", "2f"),
                    Convertible: true, RequiresRun: false)
            })
            {
                var variantDocument = document.WithText(SourceText.From(variant.Source));
                var model = await variantDocument.GetSemanticModelAsync();
                var loop = (await variantDocument.GetSyntaxRootAsync()).DescendantNodes().OfType<ForEachStatementSyntax>().First();
                var analysis = LoopAnalysis.Analyze(loop, model, CancellationToken.None);
                Assert.True(analysis.CanConvert == variant.Convertible, analysis.Reason);
                if (analysis.CanConvert)
                    Assert.Equal(variant.RequiresRun, analysis.RequiresRun);
            }
        }

        [Fact]
        public async Task CompletesParallelNativeArrayReadersBeforeFollowingStatements()
        {
            var path = Path.Combine(ValidationFixtureDirectory(), "NativeArrayQuerySystem.cs");
            var source = File.ReadAllText(path).Replace("\r\n", "\n").Replace(
                "                item.ValueRW.Result = sum;\n            }",
                "                item.ValueRW.Result = sum;\n            }\n            state.Enabled = false;");
            var (testWorkspace, document) = CreateUnityWorkspace(path, source);
            using var workspace = testWorkspace;
            var compilation = await document.Project.GetCompilationAsync();
            var diagnostic = Assert.Single((await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
            var actions = new List<CodeAction>();
            await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
                (action, _) => actions.Add(action), CancellationToken.None));
            var operations = await GetAction(actions, "IJobEntity に変換（ScheduleParallel）").GetOperationsAsync(CancellationToken.None);
            var changed = Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution.GetDocument(document.Id);
            var update = (await changed.GetSyntaxRootAsync()).DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(method => method.Identifier.ValueText == "OnUpdate");
            Assert.Contains("state.Dependency.Complete();", update.Body.Statements[1].ToString(), StringComparison.Ordinal);
            Assert.Equal("state.Enabled = false;", update.Body.Statements[2].ToString());
        }

        [Fact]
        public async Task ConvertsEntityAccessWithTagsAndWholeTransformAssignment()
        {
            var directory = ValidationFixtureDirectory();
            var path = Path.Combine(directory, "EntityAccessQuerySystem.cs");
            var source = File.ReadAllText(path);
            var (testWorkspace, document) = CreateUnityWorkspace(path, source);
            using var workspace = testWorkspace;
            var compilation = await document.Project.GetCompilationAsync();
            Assert.Empty(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            var diagnostic = Assert.Single((await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
            var actions = new List<CodeAction>();
            await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
                (action, _) => actions.Add(action), CancellationToken.None));
            Assert.Equal(2, actions.Count);
            foreach (var action in actions)
            {
                var operations = await action.GetOperationsAsync(CancellationToken.None);
                var changed = Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution.GetDocument(document.Id);
                changed = await Formatter.FormatAsync(changed, cancellationToken: CancellationToken.None);
                var output = (await changed.GetTextAsync()).ToString();
                Assert.Contains("ref LocalTransform transform, in Lifetime lifetime, Entity entity", output, StringComparison.Ordinal);
                Assert.Contains("transform = transform.RotateY(lifetime.Remaining)", output, StringComparison.Ordinal);
                Assert.Contains("[WithAll(typeof(Active))]", output, StringComparison.Ordinal);
                Assert.Contains("[WithAll(typeof(Simulated))]", output, StringComparison.Ordinal);
                Assert.Contains("[WithNone(typeof(Frozen))]", output, StringComparison.Ordinal);
                Assert.Contains("[WithNone(typeof(Hidden))]", output, StringComparison.Ordinal);
                var changedCompilation = await changed.Project.GetCompilationAsync();
                Assert.Empty(changedCompilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
                if (action.Title == "IJobEntity に変換（ScheduleParallel）")
                {
                    Assert.Contains("ecb.AsParallelWriter()", output, StringComparison.Ordinal);
                    Assert.Contains("DestroyEntity(entityIndexInQuery, entity)", output, StringComparison.Ordinal);
                    Assert.Contains("AddComponent<Processed>(entityIndexInQuery, entity)", output, StringComparison.Ordinal);
                    Assert.Contains("RemoveComponent<Active>(entityIndexInQuery, entity)", output, StringComparison.Ordinal);
                    var sourceRoot = await changed.GetSyntaxRootAsync();
                    var fixture = sourceRoot.RemoveNodes(sourceRoot.DescendantNodes().OfType<StructDeclarationSyntax>()
                        .Where(declaration => declaration.BaseList?.ToString() == ": IComponentData"), SyntaxRemoveOptions.KeepNoTrivia);
                    var fixtureText = fixture.ToFullString().Replace("partial struct EntityAccessQuerySystem", "partial struct EntityAccessParallelSystem");
                    var fixturePath = Path.Combine(directory, "CodeFixEntityAccessSystem.cs");
                    if (Environment.GetEnvironmentVariable("ENTITIES_JOBIFY_WRITE_ENTITY_ACCESS_FIXTURE") == "1")
                        File.WriteAllText(fixturePath, fixtureText);
                    else
                        Assert.True(SyntaxFactory.AreEquivalent(CSharpSyntaxTree.ParseText(File.ReadAllText(fixturePath)).GetRoot(),
                            CSharpSyntaxTree.ParseText(fixtureText).GetRoot()));
                }
            }

            const string singleQuerySource = @"
using Unity.Entities;
using Unity.Transforms;
public partial struct SingleQuerySystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var ecb = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
        foreach (var (transform, entity) in SystemAPI.Query<RefRO<LocalTransform>>().WithEntityAccess())
            { if (transform.ValueRO.Scale <= 0f) ecb.DestroyEntity(entity); }
    }
}";
            var singleDocument = document.WithText(SourceText.From(singleQuerySource));
            var singleModel = await singleDocument.GetSemanticModelAsync();
            var loop = (await singleDocument.GetSyntaxRootAsync()).DescendantNodes().OfType<ForEachVariableStatementSyntax>().Single();
            var analysis = LoopAnalysis.Analyze(loop, singleModel, CancellationToken.None);
            Assert.True(analysis.CanConvert, analysis.Reason);
            Assert.True(analysis.CanScheduleEcbInParallel);
        }

        [Fact]
        public async Task ConvertsSingleRefRwQueryAndCapturesInitializedNumber()
        {
            const string source = @"
using Unity.Entities;
namespace Demo
{
    public struct Position : IComponentData { public float X; }
    public partial struct MoveSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var dt = 0.5f;
            foreach (var position in SystemAPI.Query<RefRW<Position>>())
            {
                position.ValueRW.X += dt;
            }
        }
    }
}";

            var compilation = CreateCompilation(source);
            var tree = compilation.SyntaxTrees.Last();
            var loop = tree.GetRoot().DescendantNodes().OfType<ForEachStatementSyntax>().Single();
            var result = LoopAnalysis.Analyze(loop, compilation.GetSemanticModel(tree), CancellationToken.None);
            Assert.True(result.CanConvert, result.Reason);
            var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
            var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
            Assert.Equal("EJ0001", diagnostic.Id);

            loop = tree.GetRoot().DescendantNodes().OfType<ForEachStatementSyntax>().Single();
            var convertedRoot = EntitiesJobifyCodeFixProvider.Transform(
                tree.GetRoot(), loop, compilation.GetSemanticModel(tree), CancellationToken.None);
            var convertedSource = convertedRoot.ToFullString();
            Assert.Contains("Captured_dt = dt", convertedSource, StringComparison.Ordinal);
            Assert.Contains("position.X += this.Captured_dt", convertedSource, StringComparison.Ordinal);
            Assert.Contains("new EntitiesJobifyJob", convertedSource, StringComparison.Ordinal);
            Assert.Contains("new EntitiesJobifyJob", convertedSource, StringComparison.Ordinal);
            Assert.Contains(".Run();", convertedSource, StringComparison.Ordinal);
            Assert.Contains("state.EntityManager.CompleteDependencyBeforeRW<global::Demo.Position>()", convertedSource, StringComparison.Ordinal);
            Assert.DoesNotContain("ValueRW", convertedSource, StringComparison.Ordinal);

            var convertedTree = CSharpSyntaxTree.ParseText(convertedSource);
            var convertedCompilation = compilation.RemoveSyntaxTrees(tree).AddSyntaxTrees(convertedTree);
            Assert.Empty(convertedCompilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        }

        [Fact]
        public async Task ConvertsDeconstructedReadOnlyAndReadWriteQueryInEitherOrder()
        {
            const string source = @"
using Unity.Entities;
namespace Demo
{
    public struct Position : IComponentData { public float X; }
    public struct Velocity : IComponentData { public float X; }
    public partial struct MoveSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var dt = 0.5f;
            foreach (var (velocity, position) in SystemAPI.Query<RefRO<Velocity>, RefRW<Position>>())
            {
                position.ValueRW.X += velocity.ValueRO.X * dt;
            }
        }
    }
}";

            var compilation = CreateCompilation(source);
            var tree = compilation.SyntaxTrees.Last();
            var loop = tree.GetRoot().DescendantNodes().OfType<ForEachVariableStatementSyntax>().Single();
            var result = LoopAnalysis.Analyze(loop, compilation.GetSemanticModel(tree), CancellationToken.None);
            Assert.True(result.CanConvert, result.Reason);
            var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
            Assert.Single(diagnostics.Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));

            loop = tree.GetRoot().DescendantNodes().OfType<ForEachVariableStatementSyntax>().Single();
            var convertedRoot = EntitiesJobifyCodeFixProvider.Transform(
                tree.GetRoot(), loop, compilation.GetSemanticModel(tree), CancellationToken.None);
            var convertedSource = convertedRoot.ToFullString();
            Assert.Contains("private void Execute(in global::Demo.Velocity velocity, ref global::Demo.Position position)", convertedSource, StringComparison.Ordinal);
            Assert.Contains("position.X += velocity.X * this.Captured_dt", convertedSource, StringComparison.Ordinal);
            Assert.Empty(compilation.RemoveSyntaxTrees(tree).AddSyntaxTrees(CSharpSyntaxTree.ParseText(convertedSource))
                .GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        }

        [Fact]
        public async Task ConvertsReadWriteBeforeReadOnlyQuery()
        {
            const string source = @"
using Unity.Entities;
namespace Demo
{
    public struct Position : IComponentData { public float X; }
    public struct Velocity : IComponentData { public float X; }
    public partial struct MoveSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (position, velocity) in SystemAPI.Query<RefRW<Position>, RefRO<Velocity>>())
            {
                position.ValueRW.X += velocity.ValueRO.X;
            }
        }
    }
}";

            var compilation = CreateCompilation(source);
            var tree = compilation.SyntaxTrees.Last();
            var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
            Assert.Single(diagnostics.Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));

            var loop = tree.GetRoot().DescendantNodes().OfType<ForEachVariableStatementSyntax>().Single();
            var convertedRoot = EntitiesJobifyCodeFixProvider.Transform(
                tree.GetRoot(), loop, compilation.GetSemanticModel(tree), CancellationToken.None);
            var convertedSource = convertedRoot.ToFullString();
            Assert.Contains("private void Execute(ref global::Demo.Position position, in global::Demo.Velocity velocity)", convertedSource, StringComparison.Ordinal);
            Assert.Empty(compilation.RemoveSyntaxTrees(tree).AddSyntaxTrees(CSharpSyntaxTree.ParseText(convertedSource))
                .GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        }
        [Fact]
        public void ReportsFirstUnsupportedBodyReasonWithoutOfferingConversion()
        {
            const string source = @"
using Unity.Entities;
namespace Demo
{
    public struct Position : IComponentData { public float X; }
    public partial struct MoveSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var position in SystemAPI.Query<RefRW<Position>>())
            {
                position.ValueRW.X = System.Math.Abs(position.ValueRO.X);
            }
        }
    }
}";

            var compilation = CreateCompilation(source);
            var tree = compilation.SyntaxTrees.Last();
            var loop = tree.GetRoot().DescendantNodes().OfType<ForEachStatementSyntax>().Single();
            var result = LoopAnalysis.Analyze(loop, compilation.GetSemanticModel(tree), CancellationToken.None);
            Assert.True(result.IsTarget);
            Assert.False(result.CanConvert);
            Assert.Contains("未対応のメソッド呼び出し", result.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public async Task IgnoresUserDefinedSystemApiWithSameName()
        {
            const string source = @"
using System.Collections.Generic;
namespace Unity.Entities
{
    public interface ISystem { void OnUpdate(ref SystemState state); }
    public struct SystemState { }
    public interface IComponentData { }
    public sealed class RefRW<T> { private T _value; public ref T ValueRW => ref _value; public ref readonly T ValueRO => ref _value; }
}
namespace Demo
{
    public static class SystemAPI { public static IEnumerable<T> Query<T>() { yield break; } }
    public struct Position : Unity.Entities.IComponentData { public float X; }
    public partial struct MoveSystem : Unity.Entities.ISystem
    {
        public void OnUpdate(ref Unity.Entities.SystemState state)
        {
            foreach (var position in SystemAPI.Query<Unity.Entities.RefRW<Position>>())
            {
                position.ValueRW.X += 1f;
            }
        }
    }
}";

            var compilation = CSharpCompilation.Create(
                "FakeEntitiesApi",
                new[] { CSharpSyntaxTree.ParseText(source) },
                PlatformReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
            Assert.DoesNotContain(diagnostics, item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId);
        }

        [Fact]
        public void ExportsRunCodeFixForConvertibleDiagnostic()
        {
            var provider = new EntitiesJobifyCodeFixProvider();

            Assert.Equal(EntitiesJobifyAnalyzer.ConvertibleDiagnosticId, Assert.Single(provider.FixableDiagnosticIds));
            Assert.Null(provider.GetFixAllProvider());
            Assert.Contains(
                typeof(EntitiesJobifyCodeFixProvider).GetCustomAttributesData(),
                attribute => attribute.AttributeType.FullName == "Microsoft.CodeAnalysis.CodeFixes.ExportCodeFixProviderAttribute");
        }

        [Fact]
        public async Task SupportsFileScopedNamespaceAndAliasedEntitiesTypes()
        {
            const string source = @"
using E = Unity.Entities;
namespace Demo;
public struct Position : E.IComponentData { public float X; }
public partial struct MoveSystem : E.ISystem
{
    public void OnUpdate(ref E.SystemState state)
    {
        foreach (var position in E.SystemAPI.Query<E.RefRW<Position>>())
        {
            position.ValueRW.X += 1f;
        }
    }
}";

            var compilation = CreateCompilation(source);
            var tree = compilation.SyntaxTrees.Last();
            var loop = tree.GetRoot().DescendantNodes().OfType<ForEachStatementSyntax>().Single();
            var result = LoopAnalysis.Analyze(loop, compilation.GetSemanticModel(tree), CancellationToken.None);

            Assert.True(result.CanConvert, result.Reason);
            var diagnostic = Assert.Single((await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
            Assert.Equal(loop.ForEachKeyword.Span, diagnostic.Location.SourceSpan);

            var convertedRoot = EntitiesJobifyCodeFixProvider.Transform(tree.GetRoot(), loop, compilation.GetSemanticModel(tree), CancellationToken.None);
            var convertedSource = convertedRoot.ToFullString();
            Assert.Contains("using Unity.Entities;", convertedSource, StringComparison.Ordinal);
            Assert.Contains(".Run();", convertedSource, StringComparison.Ordinal);
            Assert.Empty(compilation.RemoveSyntaxTrees(tree).AddSyntaxTrees(CSharpSyntaxTree.ParseText(convertedSource))
                .GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        }

        [Theory]
        [MemberData(nameof(UnsupportedLoops))]
        public void RejectsUnsupportedQueryShapes(string component, string loopSource)
        {
            var source = SingleComponentSystem(component, loopSource);
            var result = AnalyzeFirstLoop(source);

            Assert.True(result.IsTarget, result.Reason);
            Assert.False(result.CanConvert);
        }

        public static IEnumerable<object[]> UnsupportedLoops()
        {
            const string position = "public struct Position : IComponentData { public float X; }";
            return new[]
            {
                new object[] { position, "foreach (var position in SystemAPI.Query<RefRW<Position>>().WithAll()) { position.ValueRW.X += 1f; }" },
                new object[] { "public struct Position : IComponentData, IEnableableComponent { public float X; }", "foreach (var position in SystemAPI.Query<RefRW<Position>>()) { position.ValueRW.X += 1f; }" },
                new object[] { "public struct Position : ISharedComponentData { public float X; }", "foreach (var position in SystemAPI.Query<RefRW<Position>>()) { position.ValueRW.X += 1f; }" },
                new object[] { "public struct Position : IComponentData { }", "foreach (var position in SystemAPI.Query<RefRW<Position>>()) { }" },
                new object[] { "public struct Position : IComponentData { public float X; }", "foreach (var (position, velocity) in SystemAPI.Query<RefRW<Position>, RefRO<Position>>()) { position.ValueRW.X += velocity.ValueRO.X; }" },
                new object[] { "public struct Position : IComponentData { public float X { get; set; } }", "foreach (var position in SystemAPI.Query<RefRW<Position>>()) { position.ValueRW.X += 1f; }" },
                new object[] { position, "var delta = 0.5f; foreach (var position in SystemAPI.Query<RefRW<Position>>()) { delta += 1f; position.ValueRW.X += delta; }" },
                new object[] { position, "foreach (var position in SystemAPI.Query<RefRW<Position>>()) { position.ValueRW.X += 1f; break; }" },
                new object[] { position, "foreach (var position in SystemAPI.Query<RefRW<Position>>()) { position.ValueRW.X += 1f; return; }" },
                new object[] { position, "foreach (var position in SystemAPI.Query<RefRW<Position>>()) { checked { position.ValueRW.X += 1f; } }" }
            };
        }

        [Fact]
        public async Task ShowsUnsupportedReasonOnlyWhenOptedIn()
        {
            const string source = @"
using Unity.Entities;
namespace Demo
{
    public struct Position : IComponentData { public float X; }
    public partial struct MoveSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var position in SystemAPI.Query<RefRW<Position>>().WithAll())
            {
                position.ValueRW.X += 1f;
            }
        }
    }
}";
            var compilation = CreateCompilation(source);
            var disabled = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
            Assert.DoesNotContain(disabled, item => item.Id == EntitiesJobifyAnalyzer.ReasonDiagnosticId);

            var options = ((CSharpCompilationOptions)compilation.Options).WithSpecificDiagnosticOptions(
                ImmutableDictionary<string, ReportDiagnostic>.Empty.Add(EntitiesJobifyAnalyzer.ReasonDiagnosticId, ReportDiagnostic.Info));
            var enabled = await compilation.WithOptions(options)
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
            var reason = Assert.Single(enabled.Where(item => item.Id == EntitiesJobifyAnalyzer.ReasonDiagnosticId));
            Assert.Contains("クエリ修飾子", reason.GetMessage(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task DoesNotOfferDiagnosticsForBrokenCodeOrRequireMatchingSystems()
        {
            const string broken = @"
using Unity.Entities;
namespace Demo
{
    public struct Position : IComponentData { public float X; }
    public partial struct MoveSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var position in SystemAPI.Query<RefRW<Position>>())
            {
                position.ValueRW.X += ;
            }
        }
    }
}";
            const string requiresAll = @"
using Unity.Entities;
namespace Demo
{
    public struct Position : IComponentData { public float X; }
    [RequireMatchingQueriesForUpdate]
    public partial struct MoveSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var position in SystemAPI.Query<RefRW<Position>>())
            {
                position.ValueRW.X += 1f;
            }
        }
    }
}";

            foreach (var source in new[] { broken, requiresAll })
            {
                var compilation = CreateCompilation(source).WithOptions(
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithSpecificDiagnosticOptions(
                        ImmutableDictionary<string, ReportDiagnostic>.Empty.Add(EntitiesJobifyAnalyzer.ReasonDiagnosticId, ReportDiagnostic.Info)));
                var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                    .GetAnalyzerDiagnosticsAsync();
                Assert.DoesNotContain(diagnostics, item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId ||
                    item.Id == EntitiesJobifyAnalyzer.ReasonDiagnosticId);
            }
        }

        [Fact]
        public async Task RegisteredCodeActionProducesCompilableOutputAndKeepsComments()
        {
            const string source = @"
using Unity.Entities;
namespace EntitiesJobify.Validation
{
    public partial struct CodeFixGeneratedMovementSystem : ISystem
    {
        private partial struct EntitiesJobifyJob { }
        public void OnUpdate(ref SystemState state)
        {
            var delta = 0.5f;
            // Keep this loop's purpose.
            foreach (var (velocity, position) in SystemAPI.Query<RefRO<JobifyVelocity>, RefRW<JobifyPosition>>())
            {
                // Keep the body note.
                position.ValueRW.X += velocity.ValueRO.X * delta;
            }
        }
    }
}";
            var (workspace, document) = CreateWorkspace(source);
            var compilation = (CSharpCompilation)await document.Project.GetCompilationAsync();
            var diagnostic = Assert.Single((await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
            var actions = new List<CodeAction>();
            await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(
                document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));

            var action = GetAction(actions, "IJobEntity に変換（Run）");
            Assert.Equal("IJobEntity に変換（Run）", action.Title);
            var operations = await action.GetOperationsAsync(CancellationToken.None);
            var changedSolution = Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution;
            var changedDocument = changedSolution.GetDocument(document.Id);
            changedDocument = await Formatter.FormatAsync(changedDocument, cancellationToken: CancellationToken.None);
            var changedSource = (await changedDocument.GetTextAsync()).ToString();

            Assert.Contains("EntitiesJobifyJob2", changedSource, StringComparison.Ordinal);
            Assert.Contains("state.EntityManager.CompleteDependencyBeforeRO<JobifyVelocity>()", changedSource, StringComparison.Ordinal);
            Assert.Contains("state.EntityManager.CompleteDependencyBeforeRW<JobifyPosition>()", changedSource, StringComparison.Ordinal);
            Assert.Contains("// Keep this loop's purpose.", changedSource, StringComparison.Ordinal);
            Assert.Contains("// Keep the body note.", changedSource, StringComparison.Ordinal);
            var changedCompilation = (CSharpCompilation)await changedDocument.Project.GetCompilationAsync();
            Assert.Empty(changedCompilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            var fixtureOutput = Environment.GetEnvironmentVariable("ENTITIES_JOBIFY_UNITY_FIXTURE_OUTPUT");
            if (!string.IsNullOrWhiteSpace(fixtureOutput))
                File.WriteAllText(fixtureOutput, changedSource);
            workspace.Dispose();
        }

        [Fact]
        public async Task ScheduleCodeActionChainsSystemDependencyWithoutCompletingIt()
        {
            const string source = @"
using Unity.Entities;
namespace EntitiesJobify.Validation
{
    [DisableAutoCreation]
    public partial struct ScheduleMovementQuerySystem : ISystem
    {
        public void OnCreate(ref SystemState state) { }
        public void OnDestroy(ref SystemState state) { }
        public void OnUpdate(ref SystemState state)
        {
            var delta = 0.5f;
            foreach (var (velocity, position) in SystemAPI.Query<RefRO<JobifyVelocity>, RefRW<JobifyPosition>>())
            {
                position.ValueRW.X += velocity.ValueRO.X * delta;
            }
        }
    }
}";
            var (workspace, document) = CreateWorkspace(source);
            var compilation = (CSharpCompilation)await document.Project.GetCompilationAsync();
            var diagnostic = Assert.Single((await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
            var actions = new List<CodeAction>();
            await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(
                document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));

            Assert.Equal(new[] { "IJobEntity に変換（Run）", "IJobEntity に変換（Schedule）", "IJobEntity に変換（ScheduleParallel）" }, actions.Select(action => action.Title));
            var operations = await GetAction(actions, "IJobEntity に変換（Schedule）").GetOperationsAsync(CancellationToken.None);
            var changedDocument = Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution.GetDocument(document.Id);
            changedDocument = await Formatter.FormatAsync(changedDocument, cancellationToken: CancellationToken.None);
            var changedSource = (await changedDocument.GetTextAsync()).ToString();

            Assert.Contains("state.Dependency = new EntitiesJobifyJob { Captured_delta = delta }.Schedule(state.Dependency);",
                changedSource, StringComparison.Ordinal);
            Assert.DoesNotContain("CompleteDependencyBefore", changedSource, StringComparison.Ordinal);
            var changedCompilation = (CSharpCompilation)await changedDocument.Project.GetCompilationAsync();
            Assert.Empty(changedCompilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            var fixtureOutput = Environment.GetEnvironmentVariable("ENTITIES_JOBIFY_SCHEDULE_FIXTURE_OUTPUT");
            if (!string.IsNullOrWhiteSpace(fixtureOutput))
                File.WriteAllText(fixtureOutput, changedSource);
            workspace.Dispose();
        }

        [Fact]
        public async Task ScheduleCodeActionIsHiddenWhenLoopIsNotTheLastStatement()
        {
            var source = SingleComponentSystem("public struct Position : IComponentData { public float X; }",
                "foreach (var position in SystemAPI.Query<RefRW<Position>>()) { position.ValueRW.X += 1f; } var after = 1f;");
            var (workspace, document) = CreateWorkspace(source);
            var compilation = (CSharpCompilation)await document.Project.GetCompilationAsync();
            var diagnostic = Assert.Single((await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
            var actions = new List<CodeAction>();
            await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(
                document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));

            Assert.Equal(new[] { "IJobEntity に変換（Run）" }, actions.Select(action => action.Title));
            workspace.Dispose();
        }

        [Fact]
        public async Task ScheduleParallelCodeActionChainsDependencyAndInheritsSystemBurstCompile()
        {
            const string source = @"
using Unity.Entities;
namespace EntitiesJobify.Validation
{
    [Unity.Burst.BurstCompile]
    [DisableAutoCreation]
    public partial struct ScheduleParallelMovementQuerySystem : ISystem
    {
        public void OnCreate(ref SystemState state) { }
        public void OnDestroy(ref SystemState state) { }
        public void OnUpdate(ref SystemState state)
        {
            var delta = 0.5f;
            foreach (var (velocity, position) in SystemAPI.Query<RefRO<JobifyVelocity>, RefRW<JobifyPosition>>())
            {
                position.ValueRW.X += velocity.ValueRO.X * delta;
            }
        }
    }
}";
            var (workspace, document) = CreateWorkspace(source);
            var compilation = (CSharpCompilation)await document.Project.GetCompilationAsync();
            var diagnostic = Assert.Single((await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
            var actions = new List<CodeAction>();
            await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
                (action, _) => actions.Add(action), CancellationToken.None));

            var operations = await GetAction(actions, "IJobEntity に変換（ScheduleParallel）").GetOperationsAsync(CancellationToken.None);
            var changedDocument = Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution.GetDocument(document.Id);
            changedDocument = await Formatter.FormatAsync(changedDocument, cancellationToken: CancellationToken.None);
            var changedSource = (await changedDocument.GetTextAsync()).ToString();

            Assert.Contains("state.Dependency = new EntitiesJobifyJob { Captured_delta = delta }.ScheduleParallel(state.Dependency);",
                changedSource, StringComparison.Ordinal);
            Assert.Equal(2, changedSource.Split(new[] { "BurstCompile" }, StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("CompleteDependencyBefore", changedSource, StringComparison.Ordinal);
            var changedCompilation = (CSharpCompilation)await changedDocument.Project.GetCompilationAsync();
            Assert.Empty(changedCompilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            var fixtureOutput = Environment.GetEnvironmentVariable("ENTITIES_JOBIFY_SCHEDULE_PARALLEL_FIXTURE_OUTPUT");
            if (!string.IsNullOrWhiteSpace(fixtureOutput))
                File.WriteAllText(fixtureOutput, changedSource);
            workspace.Dispose();
        }

        [Theory]
        [InlineData("Run")]
        [InlineData("Schedule")]
        [InlineData("ScheduleParallel")]
        public async Task OnUpdateBurstCompileIsInheritedByTheGeneratedJob(string mode)
        {
            const string source = @"
using Unity.Entities;
namespace EntitiesJobify.Validation
{
    [DisableAutoCreation]
    public partial struct BurstMovementQuerySystem : ISystem
    {
        [Unity.Burst.BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var position in SystemAPI.Query<RefRW<JobifyPosition>>())
            {
                position.ValueRW.X += 1f;
            }
        }
    }
}";
            var (workspace, document) = CreateWorkspace(source);
            var compilation = (CSharpCompilation)await document.Project.GetCompilationAsync();
            var diagnostic = Assert.Single((await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
            var actions = new List<CodeAction>();
            await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
                (action, _) => actions.Add(action), CancellationToken.None));

            var operations = await GetAction(actions, "IJobEntity に変換（" + mode + "）").GetOperationsAsync(CancellationToken.None);
            var changedDocument = Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution.GetDocument(document.Id);
            var changedSource = (await changedDocument.GetTextAsync()).ToString();

            Assert.Equal(2, changedSource.Split(new[] { "BurstCompile" }, StringSplitOptions.None).Length - 1);
            var changedCompilation = (CSharpCompilation)await changedDocument.Project.GetCompilationAsync();
            Assert.Empty(changedCompilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            workspace.Dispose();
        }

        [Fact]
        public async Task SimplifiedTypesStillResolveToTheOriginalComponentsWhenNamesCollide()
        {
            const string source = @"
using Unity.Entities;
namespace Demo
{
    public struct Position : IComponentData { public float X; }
    public partial struct MoveSystem : ISystem
    {
        private struct Position { }
        public void OnUpdate(ref SystemState state)
        {
            foreach (var position in SystemAPI.Query<RefRW<global::Demo.Position>>())
            {
                position.ValueRW.X += 1f;
            }
        }
    }
}";
            var (workspace, document) = CreateWorkspace(source);
            var compilation = (CSharpCompilation)await document.Project.GetCompilationAsync();
            var diagnostic = Assert.Single((await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
            var actions = new List<CodeAction>();
            await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(
                document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));
            var operations = await GetAction(actions, "IJobEntity に変換（Run）").GetOperationsAsync(CancellationToken.None);
            var changedDocument = Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution.GetDocument(document.Id);
            var changedCompilation = (CSharpCompilation)await changedDocument.Project.GetCompilationAsync();
            Assert.Empty(changedCompilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            var root = await changedDocument.GetSyntaxRootAsync();
            var execute = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "Execute");
            var model = await changedDocument.GetSemanticModelAsync();
            Assert.Equal("Demo.Position", model.GetTypeInfo(execute.ParameterList.Parameters[0].Type).Type.ToDisplayString());
            workspace.Dispose();
        }

        [Fact]
        public async Task RevalidatesAStaleDiagnosticBeforeApplying()
        {
            const string source = @"
using Unity.Entities;
namespace Demo
{
    public struct Position : IComponentData { public float X; }
    public partial struct MoveSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var delta = 0.5f;
            foreach (var position in SystemAPI.Query<RefRW<Position>>())
            {
                position.ValueRW.X += delta;
            }
        }
    }
}";
            var (workspace, document) = CreateWorkspace(source);
            var originalCompilation = (CSharpCompilation)await document.Project.GetCompilationAsync();
            var staleDiagnostic = Assert.Single((await originalCompilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntitiesJobifyAnalyzer()))
                .GetAnalyzerDiagnosticsAsync()).Where(item => item.Id == EntitiesJobifyAnalyzer.ConvertibleDiagnosticId));
            var editedSource = source.Replace("position.ValueRW.X += delta;", "position.ValueRW.X = System.Math.Abs(position.ValueRO.X);");
            var editedDocument = document.WithText(SourceText.From(editedSource));
            var actions = new List<CodeAction>();
            await new EntitiesJobifyCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(
                editedDocument, staleDiagnostic, (action, _) => actions.Add(action), CancellationToken.None));
            var action = GetAction(actions, "IJobEntity に変換（Run）");
            var operations = await action.GetOperationsAsync(CancellationToken.None);
            var changedSolution = Assert.IsType<ApplyChangesOperation>(Assert.Single(operations)).ChangedSolution;
            var finalText = (await changedSolution.GetDocument(editedDocument.Id).GetTextAsync()).ToString();

            Assert.Equal(editedSource, finalText);
            workspace.Dispose();
        }

        private static CSharpCompilation CreateCompilation(string source)
        {
            var trees = new[] { CSharpSyntaxTree.ParseText(EntitiesStubs), CSharpSyntaxTree.ParseText(source) };
            return CSharpCompilation.Create(
                "EntitiesJobifyTest",
                trees,
                PlatformReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        private static CodeAction GetAction(IEnumerable<CodeAction> actions, string title)
        {
            return Assert.Single(actions, action => action.Title == title);
        }

        private static System.Collections.Generic.IEnumerable<MetadataReference> PlatformReferences()
        {
            return ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path));
        }

        private static string UnityManagedPath()
        {
            return Environment.GetEnvironmentVariable("ENTITIES_JOBIFY_UNITY_MANAGED_PATH") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Unity", "Hub", "Editor", "6000.6.8f1", "Editor", "Data", "Managed", "UnityEngine");
        }

        private static string ValidationFixtureDirectory()
        {
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "Assets", "JobifyValidation"));
        }

        private static (AdhocWorkspace Workspace, Document Document) CreateUnityWorkspace(string path, string source)
        {
            var assemblyDirectory = Path.GetFullPath(Path.Combine(ValidationFixtureDirectory(), "..", "..", "Library", "ScriptAssemblies"));
            var managedDirectory = UnityManagedPath();
            var references = PlatformReferences().Concat(new[]
            {
                Path.Combine(assemblyDirectory, "Unity.Entities.dll"),
                Path.Combine(assemblyDirectory, "Unity.Transforms.dll"),
                Path.Combine(assemblyDirectory, "Unity.Collections.dll"),
                Path.Combine(assemblyDirectory, "Unity.Burst.dll"),
                Path.Combine(managedDirectory, "UnityEngine.BurstModule.dll"),
                Path.Combine(managedDirectory, "UnityEngine.CoreModule.dll"),
                Path.Combine(managedDirectory, "UnityEngine.MathematicsModule.dll"),
                Path.Combine(managedDirectory, "UnityEngine.ScriptingModule.dll")
            }.Select(file => MetadataReference.CreateFromFile(file)));
            var workspace = new AdhocWorkspace();
            var project = workspace.AddProject(Path.GetFileNameWithoutExtension(path), LanguageNames.CSharp)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .WithMetadataReferences(references);
            return (workspace, project.AddDocument(Path.GetFileName(path), source));
        }

        private static LoopAnalysisResult AnalyzeFirstLoop(string source)
        {
            var compilation = CreateCompilation(source);
            var tree = compilation.SyntaxTrees.Last();
            var loop = tree.GetRoot().DescendantNodes().First(node => node is ForEachStatementSyntax || node is ForEachVariableStatementSyntax);
            return LoopAnalysis.Analyze(loop, compilation.GetSemanticModel(tree), CancellationToken.None);
        }

        private static string SingleComponentSystem(string componentDeclaration, string loopSource)
        {
            return @"
using Unity.Entities;
namespace Demo
{
    " + componentDeclaration + @"
    public partial struct MoveSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            " + loopSource + @"
        }
    }
}";
        }

        private static (AdhocWorkspace Workspace, Document Document) CreateWorkspace(string source, bool includeUnityMath = false)
        {
            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp10);
            var solution = workspace.CurrentSolution
                .AddProject(projectId, "EntitiesJobifyCodeFixTest", "EntitiesJobifyCodeFixTest", LanguageNames.CSharp)
                .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .WithProjectParseOptions(projectId, parseOptions)
                .AddMetadataReferences(projectId, PlatformReferences());
            if (includeUnityMath)
            {
                var unityManaged = UnityManagedPath();
                solution = solution.AddMetadataReferences(projectId, new[]
                {
                    MetadataReference.CreateFromFile(Path.Combine(unityManaged, "UnityEngine.MathematicsModule.dll")),
                    MetadataReference.CreateFromFile(Path.Combine(unityManaged, "UnityEngine.ScriptingModule.dll"))
                });
                solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "LocalTransformStub.cs", SourceText.From(
                    "namespace Unity.Transforms { public struct LocalTransform : Unity.Entities.IComponentData { public Unity.Mathematics.float3 Position; } }"));
            }
            var entitiesId = DocumentId.CreateNewId(projectId);
            var sourceId = DocumentId.CreateNewId(projectId);
            solution = solution
                .AddDocument(entitiesId, "EntitiesStubs.cs", SourceText.From(EntitiesStubs))
                .AddDocument(sourceId, "MoveSystem.cs", SourceText.From(source));
            Assert.True(workspace.TryApplyChanges(solution));
            return (workspace, workspace.CurrentSolution.GetDocument(sourceId));
        }
    }
}













