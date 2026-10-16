using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class ImplementationsTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => FindImplementationsAsync;

    [McpServerTool(Name = "implementations", ReadOnly = true)]
    [Description("Finds what implements or overrides a symbol: the types implementing an interface, the members implementing an interface member, "
        + "and the overrides of an abstract or virtual member. Paged.")]
    private async Task<PagedResult<SymbolEntry>> FindImplementationsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        return await cache.GetPageAsync(solution, $"implementations|{symbol}|{filePath}|{line}|{column}", page, pageSize,
            () => FindAsync(solution, symbols, cancellationToken));
    }

    internal static async Task<IEnumerable<SymbolEntry>> FindAsync(Solution solution, IReadOnlyList<ISymbol> symbols, CancellationToken cancellationToken)
    {
        var found = new List<ISymbol>();
        foreach (var symbol in symbols)
        {
            found.AddRange(await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken));
            found.AddRange(await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cancellationToken));
        }

        return found.Distinct(SymbolEqualityComparer.Default).Select(symbol => SymbolEntry.From(symbol!));
    }
}
