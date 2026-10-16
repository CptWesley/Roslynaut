using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class UnusedSymbolsTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => FindUnusedAsync;

    [McpServerTool(Name = "unused_symbols", ReadOnly = true)]
    [Description("Finds types and members declared in source that nothing references; the members of an unused type are left out. "
        + "Public ones are left out unless asked for, as they can be used from outside. So are overrides, interface implementations, "
        + "constructors, entry points and symbols with attributes, as frameworks, serializers or reflection may use those. Paged.")]
    private async Task<PagedResult<SymbolEntry>> FindUnusedAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Optional path of a C# file, or a symbol path, to search within. " + ToolDefaults.SymbolPath)] string? scope = null,
        [Description("Include symbols that are visible outside the assembly too.")] bool includePublic = false,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        return await cache.GetPageAsync(solution, $"unused_symbols|{scope}|{includePublic}", page, pageSize,
            () => FindAsync(solution, scope, includePublic, cancellationToken));
    }

    // ponytail: one reference search per candidate; index all references once if this gets slow on big solutions.
    internal static async Task<IEnumerable<SymbolEntry>> FindAsync(Solution solution, string? scope, bool includePublic, CancellationToken cancellationToken)
    {
        var roots = scope is null
            ? await SymbolTree.GetRootNamespacesAsync(solution, cancellationToken)
            : (await SymbolTree.ResolveAsync(solution, scope, cancellationToken)).Select(SymbolTree.TypeOrSelf);
        var entryPoints = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var project in solution.Projects)
        {
            if ((await project.GetCompilationAsync(cancellationToken))?.GetEntryPoint(cancellationToken) is { } entryPoint)
                entryPoints.Add(entryPoint);
        }

        // Breadth first, so types come before their members.
        var unused = new List<ISymbol>();
        await foreach (var symbol in SymbolTree.GetDescendantsAsync(solution, roots, includeLocals: false, cancellationToken))
        {
            if (!IsCandidate(symbol, includePublic) || entryPoints.Contains(symbol) || unused.Contains(symbol.ContainingType, SymbolEqualityComparer.Default))
                continue;

            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
            if (!references.Any(reference => reference.Locations.Any()))
                unused.Add(symbol);
        }

        return unused.Select(symbol => SymbolEntry.From(symbol));
    }

    private static bool IsCandidate(ISymbol symbol, bool includePublic) =>
        symbol is INamedTypeSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IFieldSymbol or IEventSymbol
        && symbol.Locations.Any(location => location.IsInSource)
        && !symbol.IsImplicitlyDeclared
        && !symbol.IsOverride
        && symbol.GetAttributes().IsEmpty
        && !ImplementsInterface(symbol)
        && (includePublic || !IsVisibleOutside(symbol));

    private static bool ImplementsInterface(ISymbol symbol) =>
        symbol is IMethodSymbol { ExplicitInterfaceImplementations.IsEmpty: false } or IPropertySymbol { ExplicitInterfaceImplementations.IsEmpty: false }
        || symbol.ContainingType?.AllInterfaces.SelectMany(type => type.GetMembers())
            .Any(member => SymbolEqualityComparer.Default.Equals(symbol.ContainingType.FindImplementationForInterfaceMember(member), symbol)) == true;

    private static bool IsVisibleOutside(ISymbol symbol)
    {
        for (var current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
                return false;
        }

        return true;
    }
}
