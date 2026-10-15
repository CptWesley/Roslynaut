using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class ListSymbolsTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => ListSymbolsAsync;

    [McpServerTool(Name = "list_symbols", ReadOnly = true)]
    [Description("Lists the symbols declared in a symbol: a namespace's namespaces and types, a type's members, a method's parameters and locals. "
        + "For a variable, field or property it lists the members of its type. Paged.")]
    private async Task<PagedResult<SymbolEntry>> ListSymbolsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.SymbolPath)] string symbol,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        return await cache.GetPageAsync(solution, $"list_symbols|{symbol}", page, pageSize, async () =>
        {
            var children = new List<SymbolEntry>();
            foreach (var parent in await SymbolTree.ResolveAsync(solution, symbol, cancellationToken))
                children.AddRange((await SymbolTree.GetChildrenAsync(solution, parent, cancellationToken)).Select(child => SymbolEntry.From(child)));

            return children.Distinct();
        });
    }
}
