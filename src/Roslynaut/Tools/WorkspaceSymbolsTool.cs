using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class WorkspaceSymbolsTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => FindSymbolsAsync;

    [McpServerTool(Name = "workspace_symbols", ReadOnly = true)]
    [Description("Searches type and member declarations by name, matching substrings and camel-case humps like an IDE's 'Go to symbol'.")]
    private async Task<IEnumerable<SymbolEntry>> FindSymbolsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Name or pattern to search for, e.g. 'Workspace' or 'GDA' for 'GetDocumentAsync'.")] string query,
        CancellationToken cancellationToken)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        var symbols = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(solution, query, SymbolFilter.TypeAndMember, cancellationToken);
        return symbols.Take(ToolDefaults.MaxResults).Select(symbol => SymbolEntry.From(symbol));
    }
}
