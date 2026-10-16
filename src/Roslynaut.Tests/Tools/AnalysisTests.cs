using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>find_symbols filters, definition of types, callees, expression_info, data_flow, unused_symbols and generated_sources.</summary>
public class AnalysisTests
{
    private const string Code = """
        using System.Collections.Generic;
        using System.Threading.Tasks;

        namespace F
        {
            [System.Obsolete] public class Old { }
            public interface IShape { }
            public abstract class Base : IShape
            {
                public static int Count;
                public abstract void Draw();
                protected virtual async Task<int> LoadAsync() { await Task.Yield(); return 1; }
            }
            public sealed class Leaf : Base
            {
                private readonly string name = "";
                public List<int> Items { get; } = new();
                public Leaf[] Children() => [];
                public override void Draw() { var list = new List<int>(); list.Add(Count); Items.Add(list.Count); }
            }
            internal class Internals
            {
                private int used;
                private int unused;
                private void Helper() { }
                [System.Obsolete] private void Attributed() { }
                public int Use() => used;
                private class Forgotten { private void Inside() { } }
            }
            public static class Flow
            {
                public static int Sum(int[] values)
                {
                    var total = 0;
                    foreach (var value in values)
                        total += value;
                    System.Func<int> read = () => total;
                    return read();
                }
                public static double Number() => 1;
                public static string Fails() => System.Math.Max("a", "b");
            }
        }
        """;

    private static readonly Solution Solution = TestSolution.Create(Code);

    private static async Task<List<string>> Find(string? query = null, string? attribute = null, string? baseType = null, string? type = null,
        string? accessibility = null, string[]? modifiers = null, SymbolCategory kind = SymbolCategory.Any)
    {
        var (named, filtered) = (FindSymbolsTool.ParseQuery(query), SymbolFilters.Parse(attribute, baseType, type, accessibility, modifiers));
        var entries = await FindSymbolsTool.SearchAsync(Solution, symbol => named(symbol) && filtered(symbol), "F", kind, CancellationToken.None);
        return [.. entries.Select(TestSolution.PathOf)];
    }

    [Test]
    public async Task FindSymbols_Filters()
    {
        (await Find(attribute: "Obsolete", kind: SymbolCategory.Type)).Should().Equal("F.Old");
        (await Find(baseType: "IShape")).Should().BeEquivalentTo("F.Base", "F.Leaf");
        (await Find(baseType: "^Base$")).Should().Equal("F.Leaf");
        (await Find(type: "^List<int>$", kind: SymbolCategory.Property)).Should().Equal("F.Leaf.Items");
        (await Find(accessibility: "private", kind: SymbolCategory.Field)).Should().BeEquivalentTo("F.Leaf.name", "F.Internals.used", "F.Internals.unused");
        (await Find(modifiers: ["static"], kind: SymbolCategory.Field)).Should().Equal("F.Base.Count");
        (await Find(modifiers: ["async", "virtual"])).Should().Equal("F.Base.LoadAsync");
    }

    [Test]
    public void FindSymbols_UnknownModifier_Throws()
    {
        FluentActions.Invoking(() => SymbolFilters.Parse(null, null, null, null, ["fancy"])).Should().Throw<McpException>().WithMessage("*fancy*");
    }

    [TestCase("Leaf.Items", "System.Collections.Generic.List<T>")]
    [TestCase("Leaf.Children", "F.Leaf")]
    [TestCase("Leaf.Draw.list", "System.Collections.Generic.List<T>")]
    public async Task Definition_OfType(string path, string expected)
    {
        var symbol = (await SymbolTree.ResolveAsync(Solution, path, CancellationToken.None)).Single();

        DefinitionTool.TypeDefinitionOf(symbol)!.ToDisplayString().Should().Be(expected);
    }

    [Test]
    public async Task Callees_ListWhatAMemberUses()
    {
        var callees = await CalleesTool.FindAsync(Solution, await SymbolTree.ResolveAsync(Solution, "Leaf.Draw", CancellationToken.None), CancellationToken.None);

        callees.Select(callee => callee.Symbol.Signature).Should().BeEquivalentTo(
            "System.Collections.Generic.List<int>.List()",
            "System.Collections.Generic.List<int>.Add(int)",
            "F.Leaf.Items",
            "System.Collections.Generic.List<int>.Count");
        callees.Single(callee => callee.Symbol.Name == "Add").Calls.Should().HaveCount(2);
    }

    [Test]
    public async Task ExpressionInfo_ShowsConversionAndConstant()
    {
        var info = (await DescribeAt("=> 1;", 3)).First();

        info.Should().Match<ExpressionInfoTool.ExpressionInfo>(expression =>
            expression.Type == "int" && expression.ConvertedType == "double" && expression.Conversion == "Implicit numeric" && expression.ConstantValue == "1");
    }

    [Test]
    public async Task ExpressionInfo_ShowsCandidatesOfFailedBinding()
    {
        var invocation = (await DescribeAt("Max(\"a\"", 0)).Single(expression => expression.Expression.StartsWith("System.Math.Max("));

        invocation.CandidateReason.Should().Be("OverloadResolutionFailure");
        invocation.CandidateSymbols.Should().Contain("System.Math.Max(int, int)");
    }

    [Test]
    public async Task DataFlow_ShowsWhatFlowsInAndOut()
    {
        var text = SourceText.From(Code);
        var line = text.Lines.Single(l => l.ToString().Contains("var total = 0;")).LineNumber + 1;
        var flow = await DataFlowTool.AnalyzeAsync(Solution.Projects.Single().Documents.Single(), WorkspaceService.LinesOf(text, line, line + 2, null), CancellationToken.None);

        flow.Should().Match<DataFlowTool.DataFlow>(f => f.Statements == 2 && f.EndPointIsReachable);
        flow.VariablesDeclared.Should().BeEquivalentTo("total", "value");
        flow.DataFlowsIn.Should().Equal("values");
        flow.DataFlowsOut.Should().Equal("total");
        flow.Captured.Should().Equal("total");
    }

    [Test]
    public async Task DataFlow_AcrossBlocks_Throws()
    {
        var text = SourceText.From(Code);
        var line = text.Lines.Single(l => l.ToString().Contains("total += value;")).LineNumber + 1;

        await FluentActions.Awaiting(() => DataFlowTool.AnalyzeAsync(Solution.Projects.Single().Documents.Single(), WorkspaceService.LinesOf(text, line, line + 1, null), CancellationToken.None))
            .Should().ThrowAsync<McpException>().WithMessage("*different blocks*");
    }

    // Use is public, but its type is not, so nothing outside the assembly can call it either.
    [Test]
    public async Task UnusedSymbols_LeavesOutUsedAndAttributed_AndMembersOfUnusedTypes()
    {
        (await UnusedSymbolsTool.FindAsync(Solution, "F.Internals", includePublic: false, CancellationToken.None)).Select(TestSolution.PathOf)
            .Should().BeEquivalentTo("F.Internals.unused", "F.Internals.Helper", "F.Internals.Use", "F.Internals.Forgotten");
    }

    [TestCase(false, new[] { "F.Leaf.name" })]
    [TestCase(true, new[] { "F.Leaf.name", "F.Leaf.Children" })]
    public async Task UnusedSymbols_LeavesOutPublic_UnlessAsked(bool includePublic, string[] expected)
    {
        (await UnusedSymbolsTool.FindAsync(Solution, "F.Leaf", includePublic, CancellationToken.None)).Select(TestSolution.PathOf)
            .Should().BeEquivalentTo(expected);
    }

    [Test]
    public async Task GeneratedSources_ListsAndReturnsGeneratorOutput_WithoutLockingTheGenerator()
    {
        // An assembly of its own: Roslyn takes one the host already loaded, such as this one, from the host and not from its file.
        var directory = Directory.CreateTempSubdirectory("roslynaut-generator-");
        var generator = Path.Combine(directory.FullName, "Generator.dll");
        CSharpCompilation.Create("Generator", [CSharpSyntaxTree.ParseText("""
                using Microsoft.CodeAnalysis;
                [Generator]
                public sealed class HelloGenerator : IIncrementalGenerator
                {
                    public void Initialize(IncrementalGeneratorInitializationContext context) =>
                        context.RegisterPostInitializationOutput(output => output.AddSource("Hello.g.cs", "namespace Generated { public static class Hello { } }"));
                }
                """)],
                ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)),
                new(OutputKind.DynamicallyLinkedLibrary))
            .Emit(generator).Success.Should().BeTrue();
        var solution = Solution.Projects.Single().AddAnalyzerReference(new AnalyzerFileReference(generator, WorkspaceService.NonLockingAnalyzerLoader())).Solution;

        (await GeneratedSourcesTool.ListAsync(solution, null, CancellationToken.None)).Should().ContainSingle()
            .Which.Should().Match<GeneratedSourcesTool.GeneratedSource>(source => source.Name == "Hello.g.cs" && source.Lines == 1 && source.Source == null);
        (await GeneratedSourcesTool.ListAsync(solution, "hello", CancellationToken.None)).Should().ContainSingle()
            .Which.Source.Should().Contain("class Hello");

        // A build that writes the generator again must be able to; Windows lets a loaded assembly be deleted, not written.
        File.WriteAllBytes(generator, []);
        directory.Delete(recursive: true);
    }

    private static async Task<IEnumerable<ExpressionInfoTool.ExpressionInfo>> DescribeAt(string text, int offset) =>
        await ExpressionInfoTool.DescribeAsync(Solution.Projects.Single().Documents.Single(), Code.IndexOf(text, StringComparison.Ordinal) + offset, CancellationToken.None);
}

public class NamespaceGraphTests
{
    private static readonly Microsoft.CodeAnalysis.Solution Layers = TestSolution.Create("""
        namespace App.Domain { public class Order { public App.Data.Repo? Repo; } }
        namespace App.Data { public class Repo { public App.Domain.Order? Last; } }
        namespace App.Web { public class Page { App.Domain.Order? order; void M() { var repo = new App.Data.Repo(); } } }
        """);

    [Test]
    public async Task Graph_HasDependencies_AndTheirCycles()
    {
        var graph = await NamespaceGraphTool.BuildAsync(Layers, depth: null, prefix: null, CancellationToken.None);

        graph.Dependencies.Select(edge => $"{edge.From} -> {edge.To} ({edge.Types})").Should().Equal(
            "App.Data -> App.Domain (1)", "App.Domain -> App.Data (1)", "App.Web -> App.Data (1)", "App.Web -> App.Domain (1)");
        graph.Dependencies[0].Examples.Should().Equal("App.Data.Repo -> App.Domain.Order");
        graph.Cycles.Should().ContainSingle().Which.Should().Equal("App.Data", "App.Domain");
    }

    [Test]
    public async Task Graph_ByDepth_AndPrefix()
    {
        (await NamespaceGraphTool.BuildAsync(Layers, depth: 1, prefix: null, CancellationToken.None)).Dependencies.Should().BeEmpty();
        (await NamespaceGraphTool.BuildAsync(Layers, depth: null, prefix: "App.Web", CancellationToken.None)).Dependencies.Should().HaveCount(2);
    }

    [Test]
    public void Cycles_AreTheGroupsThatReachEachOther()
    {
        NamespaceGraphTool.Cycles([("A", "B"), ("B", "C"), ("C", "A"), ("D", "A"), ("E", "F")])
            .Should().ContainSingle().Which.Should().Equal("A", "B", "C");
    }
}
