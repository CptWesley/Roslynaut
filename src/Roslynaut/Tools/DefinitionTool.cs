using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class DefinitionTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => GetDefinitionAsync;

    [McpServerTool(Name = "definition", ReadOnly = true)]
    [Description("Resolves the symbol at a position: its signature, documentation and declaration locations (go to definition + hover).")]
    private async Task<Definition> GetDefinitionAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.FilePath)] string filePath,
        [Description(ToolDefaults.Line)] int line,
        [Description(ToolDefaults.Column)] int column,
        CancellationToken cancellationToken)
    {
        var document = await workspace.GetDocumentAsync(projectPath, filePath);
        var symbol = await WorkspaceService.GetSymbolAtAsync(document, line, column, cancellationToken);
        return new(
            SymbolEntry.KindOf(symbol),
            symbol.ToDisplayString(),
            symbol.ContainingAssembly?.Name,
            symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken),
            symbol.Locations.Where(location => location.IsInSource).Select(SourceLocation.From));
    }

    public sealed record Definition(string Kind, string Signature, string? Assembly, string? Documentation, IEnumerable<SourceLocation> Locations);
}
