using System.ComponentModel;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class ReferencesTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => FindReferencesAsync;

    [McpServerTool(Name = "references", ReadOnly = true)]
    [Description("Finds all references to the symbol at a position across the project or solution.")]
    private async Task<IEnumerable<SourceLocation>> FindReferencesAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.FilePath)] string filePath,
        [Description(ToolDefaults.Line)] int line,
        [Description(ToolDefaults.Column)] int column,
        CancellationToken cancellationToken)
    {
        var symbol = await workspace.GetSymbolAtAsync(projectPath, filePath, line, column, cancellationToken);
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        return references.SelectMany(reference => reference.Locations).Select(reference => SourceLocation.From(reference.Location)).Distinct();
    }
}
