using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class DefinitionTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => GetDefinitionsAsync;

    [McpServerTool(Name = "definition", ReadOnly = true)]
    [Description("Resolves a symbol: its signature, documentation and declaration locations (go to definition + hover). "
        + "For a symbol from a referenced assembly, AssemblyPath is the file to pass to assembly_il, assembly_asm or decompile.")]
    private async Task<IEnumerable<Definition>> GetDefinitionsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        var definitions = new List<Definition>();
        foreach (var target in symbols)
        {
            var locations = target.Locations.Where(location => location.IsInSource).Select(SourceLocation.From).ToList();
            definitions.Add(new(
                SymbolEntry.KindOf(target),
                target.ToDisplayString(),
                target.ContainingAssembly?.Name,
                locations.Count == 0 && target.ContainingAssembly is { } assembly ? await AssemblyLoader.FindFileAsync(solution, assembly, cancellationToken) : null,
                target.GetDocumentationCommentXml(cancellationToken: cancellationToken),
                locations));
        }

        return definitions;
    }

    public sealed record Definition(
        string Kind, string Signature, string? Assembly, string? AssemblyPath, string? Documentation, IEnumerable<SourceLocation> Locations);
}
