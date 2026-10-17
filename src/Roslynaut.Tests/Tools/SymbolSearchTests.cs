using Microsoft.CodeAnalysis;
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

    // A project's namespace also holds what the projects it references declare there. When its references are not theirs, as for a project of
    // another framework, those are copies of the referenced projects' symbols; references of their own to the same file make that happen too.
    [Test]
    public async Task ResolveSymbolPath_ThroughANamespaceOfSeveralProjects_FindsEachSymbolOnce()
    {
        var lib = TestSolution.Create("namespace Shapes { public class Circle { } } namespace Shapes.Internal { class Registry { } }").Projects.Single();
        var solution = lib.Solution.AddProject("App", "App", LanguageNames.CSharp)
            .WithCompilationOptions(lib.CompilationOptions!)
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddProjectReference(new ProjectReference(lib.Id))
            .AddDocument("App.cs", "namespace Shapes.App { class Program { } }").Project.Solution;

        (await SymbolTree.ResolveAsync(solution, "Shapes.Circle", CancellationToken.None)).Should().ContainSingle();
        var children = new List<SymbolEntry>();
        foreach (var shapes in await SymbolTree.ResolveAsync(solution, "Shapes", CancellationToken.None))
            children.AddRange((await SymbolTree.GetChildrenAsync(solution, shapes, CancellationToken.None)).Select(child => SymbolEntry.From(child)));
        children.Distinct().Select(TestSolution.PathOf).Should().BeEquivalentTo("Shapes.Circle", "Shapes.Internal", "Shapes.App");
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
