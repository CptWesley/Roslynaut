using Microsoft.CodeAnalysis;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>organize_usings, fix_all, the symbol edits, safe_delete_symbol and preview_edit.</summary>
public class EditToolsTests
{
    private static Document DocumentOf(Solution solution) => solution.Projects.Single().Documents.Single();

    private static async Task<string> TextOf(Solution solution) => (await DocumentOf(solution).GetTextAsync()).ToString();

    [TestCase(true, "using System.Collections.Generic;\nclass C { List<int> l; }")]
    [TestCase(false, "using System;\nusing System.Collections.Generic;\nusing System.Text;\nclass C { List<int> l; }")]
    public async Task OrganizeUsings_RemovesUnneededAndSorts(bool removeUnused, string expected)
    {
        var solution = TestSolution.Create("using System.Text;\nusing System;\nusing System.Collections.Generic;\nclass C { List<int> l; }");

        (await TextOf((await OrganizeUsingsTool.OrganizeAsync(DocumentOf(solution), removeUnused, CancellationToken.None)).Project.Solution)).Should().Be(expected);
    }

    // A line's indentation comes before it, so it changes too; the lines around keep theirs.
    [Test]
    public async Task Format_Lines_IndentsThemToo()
    {
        var solution = TestSolution.Create("class C\n{\nvoid M()\n{\nreturn  ;\n}\n}\n");

        (await (await FormatTool.FormatAsync(DocumentOf(solution), 5, null, CancellationToken.None)).GetTextAsync()).ToString().ReplaceLineEndings("\n")
            .Should().Be("class C\n{\nvoid M()\n{\n        return;\n}\n}\n");
    }

    [Test]
    public async Task FixAll_FixesEveryOccurrence()
    {
        var solution = TestSolution.Create("class C { void M() { int a; int b; } }");

        var (title, changed) = await new CodeActionService().FixAllAsync(solution, DocumentOf(solution), "CS0168", null, CancellationToken.None);

        title.Should().Be("Remove unused variable");
        (await TextOf(changed)).Should().NotContain("int a").And.NotContain("int b");
    }

    private const string Widget = """
        class Widget
        {
            /// <summary>Spins.</summary>
            public void Spin() { }

            public int Speed;
        }
        """;

    private static async Task<string> Edit(string path, SymbolEdits.Placement placement, string code)
    {
        var solution = TestSolution.Create(Widget);
        var symbols = await SymbolTree.ResolveAsync(solution, path, CancellationToken.None);
        return (await TextOf(await SymbolEdits.EditAsync(solution, symbols, placement, code, CancellationToken.None))).Replace("\r\n", "\n");
    }

    [Test]
    public async Task ReplaceSymbolBody_ReplacesWholeDeclaration()
    {
        (await Edit("Widget.Spin", SymbolEdits.Placement.Replace, "    public void Spin(int times) { }"))
            .Should().Contain("    /// <summary>Spins.</summary>\n    public void Spin(int times) { }\n\n    public int Speed;");
    }

    [Test]
    public async Task InsertBeforeSymbol_GoesBeforeDocumentationComment()
    {
        (await Edit("Widget.Spin", SymbolEdits.Placement.Before, "    public void Stop() { }"))
            .Should().Contain("{\n    public void Stop() { }\n    /// <summary>Spins.</summary>\n    public void Spin() { }");
    }

    [Test]
    public async Task InsertAfterSymbol_GoesOnTheNextLine()
    {
        (await Edit("Widget.Speed", SymbolEdits.Placement.After, "    public int Weight;"))
            .Should().EndWith("    public int Speed;\n    public int Weight;\n}");
    }

    [Test]
    public async Task SafeDelete_DeletesUnreferencedSymbol()
    {
        var solution = TestSolution.Create("class C { int unused; int Used() => 1; int Call() => Used(); }");

        var (deleted, references) = await SymbolEdits.SafeDeleteAsync(solution, await SymbolTree.ResolveAsync(solution, "C.unused", CancellationToken.None), CancellationToken.None);

        references.Should().BeEmpty();
        (await TextOf(deleted!)).Should().NotContain("unused").And.Contain("int Used() => 1;");
    }

    [Test]
    public async Task SafeDelete_ReferencedSymbol_ReturnsReferencesInstead()
    {
        var solution = TestSolution.Create("class C { int Used() => 1; int Call() => Used(); }");

        var (deleted, references) = await SymbolEdits.SafeDeleteAsync(solution, await SymbolTree.ResolveAsync(solution, "C.Used", CancellationToken.None), CancellationToken.None);

        deleted.Should().BeNull();
        references.Should().ContainSingle().Which.Text.Should().Contain("Call() => Used()");
    }

    [Test]
    public async Task PreviewEdit_ReportsIntroducedAndResolvedDiagnostics()
    {
        var solution = new AdhocWorkspace().AddProject("P", LanguageNames.CSharp)
            .WithCompilationOptions(new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddDocument("C.cs", "class C { int Twice(int x) => x * 2; int Use() => Twice(2) + missing; }", filePath: Path.Combine(Path.GetTempPath(), "C.cs"))
            .Project.Solution;
        var file = DocumentOf(solution).FilePath!;

        var preview = await PreviewEditTool.PreviewAsync(solution, [new(file, "Twice(int x)", "Double(int x)"), new(file, "+ missing", "+ 1")], analyzers: false, CancellationToken.None);

        preview.Introduced.Should().ContainSingle().Which.Should().Match<DiagnosticsTool.DiagnosticEntry>(entry => entry.Id == "CS0103" && entry.Message.Contains("Twice"));
        preview.Resolved.Should().ContainSingle().Which.Message.Should().Contain("missing");
    }
}
