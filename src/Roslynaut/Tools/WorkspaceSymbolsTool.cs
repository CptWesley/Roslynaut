using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class WorkspaceSymbolsTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => FindSymbolsAsync;

    [McpServerTool(Name = "workspace_symbols", ReadOnly = true)]
    [Description("Searches type and member declarations by name, matching substrings and camel-case humps like an IDE's 'Go to symbol'. Paged.")]
    private async Task<PagedResult<SymbolEntry>> FindSymbolsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Name or pattern to search for, e.g. 'Workspace' or 'GDA' for 'GetDocumentAsync'. "
            + "May be qualified with namespaces and containing types, e.g. 'Tools.WorkspaceService.GetDocumentAsync'.")] string query,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        return await cache.GetPageAsync(solution, $"workspace_symbols|{query}", page, pageSize,
            () => SearchAsync(solution, query, cancellationToken));
    }

    internal static async Task<IEnumerable<SymbolEntry>> SearchAsync(Solution solution, string query, CancellationToken cancellationToken) =>
        (await SymbolFinder.FindSourceDeclarationsWithPatternAsync(solution, query, SymbolFilter.TypeAndMember, cancellationToken))
            .Select(symbol => SymbolEntry.From(symbol));
}
