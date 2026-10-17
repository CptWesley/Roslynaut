using System.Collections.Immutable;
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class DocumentHighlightTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => HighlightDocumentAsync;

    [McpServerTool(Name = "document_highlight", ReadOnly = true)]
    [Description("Finds where the symbol at a position occurs in that same file, in order: its declarations, reads, writes and calls.")]
    private async Task<IEnumerable<ReferencesTool.ReferenceEntry>> HighlightDocumentAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.FilePath)] string filePath,
        [Description(ToolDefaults.Line)] int line,
        [Description(ToolDefaults.Column)] int column,
        CancellationToken cancellationToken = default)
    {
        var document = await workspace.GetDocumentAsync(projectPath, filePath);
        return await HighlightAsync(document, await WorkspaceService.GetSymbolAtAsync(document, line, column, cancellationToken), cancellationToken);
    }

    internal static async Task<IEnumerable<ReferencesTool.ReferenceEntry>> HighlightAsync(Document document, ISymbol symbol, CancellationToken cancellationToken)
    {
        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        var entries = SymbolTree.PartsOf(symbol).SelectMany(part => part.Locations).Where(location => location.SourceTree == tree)
            .Select(location => new ReferencesTool.ReferenceEntry(SourceLocation.From(location), symbol.ContainingSymbol?.ToDisplayString(), "Declaration"))
            .ToList();
        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, document.Project.Solution, ImmutableHashSet.Create(document), cancellationToken))
        {
            foreach (var location in reference.Locations)
                entries.Add(await ReferencesTool.EntryOfAsync(location, reference.Definition, cancellationToken));
        }

        return [.. entries.Distinct().OrderBy(entry => entry.Location.Line).ThenBy(entry => entry.Location.Column)];
    }
}
