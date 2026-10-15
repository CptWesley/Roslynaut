using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class ListSymbolsTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => ListSymbolsAsync;

    [McpServerTool(Name = "list_symbols", ReadOnly = true)]
    [Description("Lists the symbols declared in a symbol: a namespace's namespaces and types, a type's members, a method's parameters and locals. "
        + "For a variable, field or property it lists the members of its type. At most 100 results.")]
    private async Task<IEnumerable<SymbolEntry>> ListSymbolsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.SymbolPath)] string symbol,
        CancellationToken cancellationToken)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        var children = new List<SymbolEntry>();
        foreach (var parent in await SymbolTree.ResolveAsync(solution, symbol, cancellationToken))
            children.AddRange((await SymbolTree.GetChildrenAsync(solution, parent, cancellationToken)).Select(child => SymbolEntry.From(child)));

        return children.Distinct().Take(ToolDefaults.MaxResults);
    }
}
