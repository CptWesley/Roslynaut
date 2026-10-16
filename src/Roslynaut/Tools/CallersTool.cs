using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class CallersTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => FindCallersAsync;

    [McpServerTool(Name = "callers", ReadOnly = true)]
    [Description("Finds the members that call or use a symbol (incoming calls), each with the locations of its calls. "
        + "Calls through an interface or base member are included. Paged.")]
    private async Task<PagedResult<Caller>> FindCallersAsync(
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
        return await cache.GetPageAsync(solution, $"callers|{symbol}|{filePath}|{line}|{column}", page, pageSize,
            () => FindAsync(solution, symbols, cancellationToken));
    }

    internal static async Task<IEnumerable<Caller>> FindAsync(Solution solution, IReadOnlyList<ISymbol> symbols, CancellationToken cancellationToken)
    {
        var callers = new List<Caller>();
        foreach (var symbol in symbols)
        {
            foreach (var caller in await SymbolFinder.FindCallersAsync(symbol, solution, cancellationToken))
                callers.Add(new(SymbolEntry.From(caller.CallingSymbol), [.. caller.Locations.Where(location => location.IsInSource).Select(SourceLocation.From)]));
        }

        return callers;
    }

    public sealed record Caller(SymbolEntry Symbol, IReadOnlyList<SourceLocation> Calls);
}
