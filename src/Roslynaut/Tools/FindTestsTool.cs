using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class FindTestsTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    // Derived attributes count too, such as xUnit's [Theory] or MSTest's [DataTestMethod].
    private static readonly HashSet<string> TestAttributes =
    [
        "NUnit.Framework.TestAttribute",
        "NUnit.Framework.TestCaseAttribute",
        "NUnit.Framework.TestCaseSourceAttribute",
        "NUnit.Framework.TheoryAttribute",
        "Xunit.FactAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute",
        "TUnit.Core.TestAttribute",
    ];

    // ponytail: follows callers to a fixed depth; raise it, or index tests by what they reach, if tests go through deeper helpers.
    private const int CallDepth = 4;

    public Delegate Handler => FindTestsAsync;

    [McpServerTool(Name = "find_tests", ReadOnly = true)]
    [Description("Finds NUnit, xUnit, MSTest and TUnit test methods: all of them, or those that use a symbol (for a type, any of its members), directly or through up to 3 calls in between. "
        + "Each comes with its project and a Filter for run_tests. Paged.")]
    private async Task<PagedResult<TestEntry>> FindTestsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Optional: only tests that use this symbol. " + ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = symbol is null && filePath is null
            ? ((await workspace.GetAsync(projectPath)).Solution, null)
            : await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        return await cache.GetPageAsync(solution, $"find_tests|{symbol}|{filePath}|{line}|{column}", page, pageSize,
            () => FindAsync(solution, symbols, cancellationToken));
    }

    /// <param name="symbols">The symbols the tests have to use; null for all tests.</param>
    internal static async Task<IEnumerable<TestEntry>> FindAsync(Solution solution, IReadOnlyList<ISymbol>? symbols, CancellationToken cancellationToken)
    {
        var tests = new List<IMethodSymbol>();
        if (symbols is null)
        {
            var members = SymbolTree.GetDescendantsAsync(solution, await SymbolTree.GetRootNamespacesAsync(solution, cancellationToken), includeLocals: false, cancellationToken);
            await foreach (var method in members.OfType<IMethodSymbol>().Where(IsTest).WithCancellation(cancellationToken))
                tests.Add(method);
        }
        else
        {
            // Walk up the callers, breadth first, collecting the tests among them. A type has no callers of its own: code uses it through its
            // members, constructors included.
            IReadOnlyList<ISymbol> frontier = [.. symbols.SelectMany(symbol => symbol is INamedTypeSymbol type ? type.GetMembers().Where(member => member is not (ITypeSymbol or IMethodSymbol { AssociatedSymbol: not null })) : [symbol])];
            var seen = new HashSet<ISymbol>(frontier, SymbolEqualityComparer.Default);
            tests.AddRange(frontier.OfType<IMethodSymbol>().Where(IsTest));
            for (var depth = 0; depth < CallDepth && frontier.Count > 0; depth++)
            {
                var next = new List<ISymbol>();
                foreach (var symbol in frontier)
                {
                    var callers = (await SymbolFinder.FindCallersAsync(symbol, solution, cancellationToken)).Select(caller => caller.CallingSymbol)
                        .Concat((await CallersTool.MissedCallsAsync(symbol, solution, cancellationToken)).Select(call => call.Caller));
                    foreach (var caller in callers)
                    {
                        if (!seen.Add(caller))
                            continue;
                        if (caller is IMethodSymbol method && IsTest(method))
                            tests.Add(method);
                        else
                            next.Add(caller);
                    }
                }

                frontier = next;
            }
        }

        var fixtures = new Dictionary<INamedTypeSymbol, IReadOnlyList<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
        var entries = new List<TestEntry>();
        foreach (var test in tests)
        {
            if (!fixtures.TryGetValue(test.ContainingType, out var classes))
                fixtures[test.ContainingType] = classes = await FixturesOfAsync(test.ContainingType, solution, cancellationToken);
            entries.Add(new(SymbolEntry.From(test), solution.GetProject(test.ContainingAssembly, cancellationToken)?.FilePath,
                string.Join("|", classes.Select(fixture => FilterOf(test, fixture)))));
        }

        return entries;
    }

    /// <summary>
    /// The classes that run the tests a class declares: itself unless it is abstract, and the classes that inherit them, under whose names they run.
    /// An abstract class that nothing inherits stands for itself, though nothing runs its tests.
    /// </summary>
    private static async Task<IReadOnlyList<INamedTypeSymbol>> FixturesOfAsync(INamedTypeSymbol type, Solution solution, CancellationToken cancellationToken)
    {
        var derived = type.IsSealed ? [] : await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: cancellationToken);
        var fixtures = derived.Prepend(type).Where(fixture => !fixture.IsAbstract).ToList();
        return fixtures.Count > 0 ? fixtures : [type];
    }

    /// <summary>
    /// The filter that runs a test in one class: by its exact name, as a filter by how its name starts would also run tests whose names start the same,
    /// such as Adds_Negative for Adds. NUnit names each case of a test with parameters by its arguments, as in Twice(2), which no filter can spell: VSTest's
    /// escaping of the parenthesis breaks NUnit's adapter. ponytail: those go by how their name starts, so they run such tests too.
    /// NUnit names a generic class by its type arguments, as in Tests&lt;Int32&gt;, so for one of those the filter takes any.
    /// </summary>
    private static string FilterOf(IMethodSymbol test, INamedTypeSymbol fixture)
    {
        var (name, exact) = (TestName(fixture), test.Parameters.Length == 0);
        return fixture.Arity > 0
            ? $"(FullyQualifiedName~{name[..name.LastIndexOf('`')]}<&Name{(exact ? '=' : '~')}{test.Name})"
            : $"FullyQualifiedName{(exact ? '=' : '~')}{name}.{test.Name}";
    }

    private static bool IsTest(IMethodSymbol method) =>
        method.GetAttributes().Any(attribute => BaseTypes(attribute.AttributeClass).Any(type => TestAttributes.Contains(type.ToDisplayString())));

    private static IEnumerable<INamedTypeSymbol> BaseTypes(INamedTypeSymbol? type)
    {
        for (; type is not null; type = type.BaseType)
            yield return type;
    }

    /// <summary>The name test runners give a class: "Namespace.Outer+Nested".</summary>
    private static string TestName(INamedTypeSymbol fixture)
    {
        var type = fixture.MetadataName;
        for (var outer = fixture.ContainingType; outer is not null; outer = outer.ContainingType)
            type = $"{outer.MetadataName}+{type}";

        return fixture.ContainingNamespace.IsGlobalNamespace ? type : $"{fixture.ContainingNamespace.ToDisplayString()}.{type}";
    }

    /// <param name="Filter">A dotnet test --filter expression for this test, for run_tests.</param>
    public sealed record TestEntry(SymbolEntry Test, string? Project, string Filter);
}
