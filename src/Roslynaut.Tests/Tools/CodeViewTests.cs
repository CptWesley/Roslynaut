using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>document_symbols, document_highlight, signature_help, completion, inlay_hints, syntax_tree, code_metrics, search_pattern and find_operations.</summary>
public class CodeViewTests
{
    private const string Code = """
        using System;
        using System.Collections.Generic;

        namespace V
        {
            public class Shop
            {
                private int stock;

                /// <summary>Sells some items.</summary>
                /// <param name="count">How many to sell.</param>
                public bool Sell(int count, bool force = false)
                {
                    if (count > stock && !force)
                        return false;
                    for (var i = 0; i < count; i++)
                        stock--;
                    return true;
                }

                public void Restock()
                {
                    var items = new List<int>();
                    items.Add(stock);
                    stock = items.Count + Math.Max(1, 2);
                    Sell(5);
                    Func<int, int> twice = n => n * 2;
                    object boxed = stock;
                    if (stock < 0) throw new InvalidOperationException("negative");
                }

                public class Note { public string Text = ""; }
            }
        }
        """;

    private static readonly Solution Solution = TestSolution.Create(Code);
    private static readonly Document Document = Solution.Projects.Single().Documents.Single();
    private static readonly SourceText Text = SourceText.From(Code);

    private static int At(string text, int offset = 0) => Code.IndexOf(text, StringComparison.Ordinal) + offset;

    private static int LineOf(string text) => Text.Lines.GetLineFromPosition(At(text)).LineNumber + 1;

    [TestCase(0, new[] { "V.Shop" })]
    [TestCase(1, new[] { "V.Shop", "V.Shop.stock", "V.Shop.Sell", "V.Shop.Restock", "V.Shop.Note" })]
    public async Task DocumentSymbols_Depth(int depth, string[] expected)
    {
        (await DocumentSymbolsTool.OutlineAsync(Document, depth, CancellationToken.None)).Select(TestSolution.PathOf).Should().Equal(expected);
    }

    [Test]
    public async Task DocumentHighlight_ListsDeclarationThenUsesInOrder()
    {
        var stock = (await SymbolTree.ResolveAsync(Solution, "Shop.stock", CancellationToken.None)).Single();

        var accesses = (await DocumentHighlightTool.HighlightAsync(Document, stock, CancellationToken.None)).Select(entry => entry.Access).ToList();

        accesses.Should().Equal("Declaration", "Read", "Write", "Read", "Write", "Read", "Read");
    }

    [Test]
    public async Task SignatureHelp_ShowsOverloadsChoiceAndActiveParameter()
    {
        var signatures = (await SignatureHelpTool.GetAsync(Document, At("Max(1, ", 7), CancellationToken.None)).ToList();

        signatures.Should().HaveCountGreaterThan(5);
        signatures.Should().ContainSingle(signature => signature.Chosen).Which.Should().Match<SignatureHelpTool.Signature>(signature =>
            signature.Display == "System.Math.Max(int, int)" && signature.ActiveParameter == 1);
    }

    [Test]
    public async Task SignatureHelp_ShowsDocumentation()
    {
        var sell = (await SignatureHelpTool.GetAsync(Document, At("Sell(5", 5), CancellationToken.None)).Should().ContainSingle().Subject;

        sell.Should().Match<SignatureHelpTool.Signature>(signature => signature.Summary == "Sells some items." && signature.ActiveParameter == 0 && signature.Chosen);
        sell.Parameters.Should().Equal(
            new SignatureHelpTool.Parameter("count", "int", "How many to sell.", false),
            new SignatureHelpTool.Parameter("force", "bool", null, true));
    }

    [Test]
    public async Task Completion_ListsMembersAfterDot()
    {
        var all = await CompletionTool.CompleteAsync(Document, At("items.Add", 6), null, CancellationToken.None);
        var filtered = await CompletionTool.CompleteAsync(Document, At("items.Add", 6), "cou", CancellationToken.None);

        all.Should().Contain(new CompletionTool.CompletionEntry("Add", "Method", null));
        filtered.Select(entry => entry.Text).Should().Equal("Count");
    }

    [Test]
    public async Task InlayHints_ShowInferredTypesAndParameterNames()
    {
        var hinted = await InlayHintsTool.AnnotateAsync(Document, LineOf("var items"), WorkspaceService.LinesOf(Text, LineOf("var items"), LineOf("Func<int"), null), CancellationToken.None);

        hinted.Should().Contain($"{LineOf("var items")}: ")
            .And.Contain("var /*List<int>*/ items = new List<int>();")
            .And.Contain("Math.Max(/*val1:*/ 1, /*val2:*/ 2)")
            .And.Contain("Sell(/*count:*/ 5)")
            .And.Contain("/*int*/ n => n * 2");
    }

    [Test]
    public async Task SyntaxTree_DumpsSyntaxOrOperations()
    {
        var lines = WorkspaceService.LinesOf(Text, LineOf("if (count"), LineOf("stock--"), null);

        (await SyntaxTreeTool.DumpAsync(Document, lines, operations: false, CancellationToken.None)).Should().Contain("IfStatement").And.Contain("ForStatement");
        (await SyntaxTreeTool.DumpAsync(Document, lines, operations: true, CancellationToken.None)).Should().Contain("Conditional").And.Contain("Loop").And.Contain("Symbol: V.Shop.stock");
    }

    [Test]
    public async Task CodeMetrics_MeasuresMembersAndTypes()
    {
        var members = (await CodeMetricsTool.MeasureAsync(Solution, "V.Shop", MetricsLevel.Members, CancellationToken.None)).ToList();
        var types = (await CodeMetricsTool.MeasureAsync(Solution, "V.Shop", MetricsLevel.Types, CancellationToken.None)).ToList();

        members.Select(metrics => (metrics.Symbol.Name, metrics.Complexity, metrics.Parameters, metrics.Nesting)).Should().Equal(("Sell", 4, 2, 1), ("Restock", 2, 0, 1));
        types[0].Should().Match<CodeMetricsTool.Metrics>(shop => shop.Symbol.Name == "Shop" && shop.Complexity == 6 && shop.MaxComplexity == 4 && shop.Coupling == 4);
    }

    // A primary constructor is declared by its type, but only its parameters and base arguments are its code.
    [Test]
    public async Task CodeMetrics_PrimaryConstructor_IsNotTheWholeType()
    {
        var solution = TestSolution.Create("""
            public class Base(int size) { }
            public class Box(int size) : Base(size)
            {
                public int Size() => size > 0 ? size : 0;
            }
            """);

        var members = (await CodeMetricsTool.MeasureAsync(solution, "Box", MetricsLevel.Members, CancellationToken.None)).ToList();

        members.Select(metrics => (metrics.Symbol.Name, metrics.Complexity, metrics.Lines)).Should().Equal(("Size", 2, 1), (".ctor", 1, 1));
    }

    [Test]
    public async Task SearchPattern_FindsTextWithinScope()
    {
        var everywhere = await SearchPatternTool.SearchAsync(Solution, new("stock"), null, 0, CancellationToken.None);
        var restock = await SearchPatternTool.SearchAsync(Solution, new("stock"), "Shop.Restock", 1, CancellationToken.None);

        everywhere.Should().HaveCount(8);
        restock.Should().HaveCount(5).And.OnlyContain(match => match.Line >= LineOf("public void Restock()"));
        restock.First().Lines.Should().StartWith($"{LineOf("public void Restock()") - 1}: ");
    }

    [Test]
    public async Task FindOperations_ByKindAndTarget()
    {
        async Task<List<string?>> Find(OperationKind kind, string? target = null) =>
            [.. (await FindOperationsTool.FindAsync(Solution, kind, target is null ? null : new(target), null, CancellationToken.None)).Select(entry => entry.Target)];

        (await Find(OperationKind.ObjectCreation)).Should().Equal("System.Collections.Generic.List<int>", "System.InvalidOperationException");
        (await Find(OperationKind.Throw)).Should().Equal("System.InvalidOperationException");
        (await Find(OperationKind.Conversion, "-> object")).Should().Equal("int -> object");
    }
}
