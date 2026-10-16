using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

[JsonConverter(typeof(JsonStringEnumConverter<HierarchyDirection>))]
public enum HierarchyDirection
{
    /// <summary>Base types and implemented interfaces.</summary>
    Supertypes,

    /// <summary>Derived types, and for an interface also derived interfaces and implementing types.</summary>
    Subtypes,
}

public sealed class TypeHierarchyTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => GetHierarchyAsync;

    [McpServerTool(Name = "type_hierarchy", ReadOnly = true)]
    [Description("Lists the supertypes (base types, then all implemented interfaces) or the subtypes (all derived types, and for an interface its "
        + "derived interfaces and implementing types) of a type. For a variable, field or property, of its type. Paged.")]
    private async Task<PagedResult<SymbolEntry>> GetHierarchyAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Supertypes or Subtypes.")] HierarchyDirection direction,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        return await cache.GetPageAsync(solution, $"type_hierarchy|{direction}|{symbol}|{filePath}|{line}|{column}", page, pageSize,
            () => FindAsync(solution, symbols, direction, cancellationToken));
    }

    internal static async Task<IEnumerable<SymbolEntry>> FindAsync(
        Solution solution, IReadOnlyList<ISymbol> symbols, HierarchyDirection direction, CancellationToken cancellationToken)
    {
        var found = new List<INamedTypeSymbol>();
        foreach (var type in symbols.Select(SymbolTree.TypeOrSelf).OfType<INamedTypeSymbol>())
        {
            if (direction == HierarchyDirection.Supertypes)
            {
                for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
                    found.Add(baseType);
                found.AddRange(type.AllInterfaces);
            }
            else if (type.TypeKind == TypeKind.Interface)
            {
                found.AddRange(await SymbolFinder.FindDerivedInterfacesAsync(type, solution, transitive: true, cancellationToken: cancellationToken));
                found.AddRange(await SymbolFinder.FindImplementationsAsync(type, solution, transitive: true, cancellationToken: cancellationToken));
            }
            else
            {
                found.AddRange(await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: cancellationToken));
            }
        }

        return found.Distinct(SymbolEqualityComparer.Default).Select(type => SymbolEntry.From(type!));
    }
}
