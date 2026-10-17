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
                    foreach (var caller in await SymbolFinder.FindCallersAsync(symbol, solution, cancellationToken))
                    {
                        if (!seen.Add(caller.CallingSymbol))
                            continue;
                        if (caller.CallingSymbol is IMethodSymbol method && IsTest(method))
                            tests.Add(method);
                        else
                            next.Add(caller.CallingSymbol);
                    }
                }

                frontier = next;
            }
        }

        return tests.Select(test => new TestEntry(SymbolEntry.From(test), solution.GetProject(test.ContainingAssembly, cancellationToken)?.FilePath, $"FullyQualifiedName~{TestName(test)}"));
    }

    private static bool IsTest(IMethodSymbol method) =>
        method.GetAttributes().Any(attribute => BaseTypes(attribute.AttributeClass).Any(type => TestAttributes.Contains(type.ToDisplayString())));

    private static IEnumerable<INamedTypeSymbol> BaseTypes(INamedTypeSymbol? type)
    {
        for (; type is not null; type = type.BaseType)
            yield return type;
    }

    /// <summary>The name test runners use: "Namespace.Outer+Nested.Method".</summary>
    private static string TestName(IMethodSymbol method)
    {
        var type = method.ContainingType.MetadataName;
        for (var outer = method.ContainingType.ContainingType; outer is not null; outer = outer.ContainingType)
            type = $"{outer.MetadataName}+{type}";

        var ns = method.ContainingNamespace;
        return ns.IsGlobalNamespace ? $"{type}.{method.Name}" : $"{ns.ToDisplayString()}.{type}.{method.Name}";
    }

    /// <param name="Filter">A dotnet test --filter expression for this test, for run_tests.</param>
    public sealed record TestEntry(SymbolEntry Test, string? Project, string Filter);
}
