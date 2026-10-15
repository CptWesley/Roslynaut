using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>Every tool that finds symbols by text also accepts names qualified with namespaces and containing types.</summary>
public class SymbolSearchTests
{
    private const string Bar = "Some.Foo.Package.Foo.Bar";
    private const string Barrier = "Some.Foo.Package.Foo.Barrier";
    private const string Baz = "Some.Foo.Package.Foo.Bar.Baz";
    private const string Qux = "Some.Foo.Package.Foo.Bar.Baz().qux";
    private const string OtherBar = "Other.Bar";

    // The last segment matches as a prefix, as with unqualified names.
    [TestCase("Some.Foo.Package.Foo.Bar", new[] { Bar, Barrier })]
    [TestCase("Foo.Bar", new[] { Bar, Barrier })]
    [TestCase("Package.Foo.Bar.Baz", new[] { Baz })]
    [TestCase("Bar", new[] { Bar, Barrier, OtherBar })]
    public async Task WorkspaceSymbols_FindsQualifiedNames(string query, string[] expected)
    {
        var entries = await WorkspaceSymbolsTool.SearchAsync(TestSolution.Nested, query, CancellationToken.None);

        entries.Select(TestSolution.PathOf).Should().BeEquivalentTo(expected);
    }

    // Used by list_symbols, and by find_symbols for its scope.
    [TestCase("Some.Foo.Package.Foo.Bar", Bar)]
    [TestCase("Package.Foo.Bar", Bar)]
    [TestCase("Some.Foo.Package.Foo.Bar.Baz.qux", Qux)]
    public async Task ResolveSymbolPath_FindsQualifiedNames(string path, string expected)
    {
        var symbols = await SymbolTree.ResolveAsync(TestSolution.Nested, path, CancellationToken.None);

        symbols.Select(symbol => TestSolution.PathOf(SymbolEntry.From(symbol))).Should().ContainSingle().Which.Should().Be(expected);
    }

    [TestCase("Some.Foo.Package.Foo.Bar", new[] { Bar, Barrier })]
    [TestCase("Some.Foo.Package.Foo.Bar$", new[] { Bar })]
    [TestCase(@"Foo\.Bar", new[] { Bar, Barrier })]
    [TestCase("Other.Bar|Foo.Bar$", new[] { OtherBar, Bar })]
    [TestCase("Foo.Bar.Baz.qux", new[] { Qux })]
    [TestCase(@"Package\.Foo", new[] { "Some.Foo.Package.Foo" }, TestName = "FindSymbols_QualifiedName_DoesNotMatchDescendants")]
    [TestCase("^Bar$", new[] { Bar, OtherBar }, TestName = "FindSymbols_UnqualifiedName_MatchesNameOnly")]
    [TestCase("^Ba.*z$", new[] { Baz }, TestName = "FindSymbols_WildcardWithoutQualifier_MatchesNameOnly")]
    public async Task FindSymbols_FindsQualifiedNames(string query, string[] expected)
    {
        (await FindSymbolsAsync(query)).Should().BeEquivalentTo(expected);
    }

    [Test]
    public async Task FindSymbols_FindsQualifiedNamespace()
    {
        (await FindSymbolsAsync("Some.Foo.Package$", kind: SymbolCategory.Namespace)).Should().ContainSingle().Which.Should().Be("Some.Foo.Package");
    }

    [Test]
    public async Task FindSymbols_FindsQualifiedNameWithinScope()
    {
        (await FindSymbolsAsync("Bar.Baz", scope: "Some.Foo.Package.Foo")).Should().ContainSingle().Which.Should().Be(Baz);
    }

    private static async Task<IEnumerable<string>> FindSymbolsAsync(string query, string? scope = null, SymbolCategory kind = SymbolCategory.Any)
    {
        var entries = await FindSymbolsTool.SearchAsync(TestSolution.Nested, FindSymbolsTool.ParseQuery(query), scope, kind, CancellationToken.None);
        return entries.Select(TestSolution.PathOf);
    }
}
