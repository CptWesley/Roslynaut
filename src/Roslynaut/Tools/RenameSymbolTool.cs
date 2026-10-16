using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Rename;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class RenameSymbolTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => RenameSymbolAsync;

    [McpServerTool(Name = "rename_symbol", Destructive = true)]
    [Description("Renames a symbol declared in source, with all its references across the solution, and the file of a type that has its name. "
        + "Writes the changed files to disk and returns their paths.")]
    private async Task<RenameResult> RenameSymbolAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The new name.")] string newName,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        [Description("For a method, rename its overloads too.")] bool overloads = false,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        var renamed = await RenameAsync(solution, symbols, newName, overloads, cancellationToken);
        return new(await WorkspaceService.WriteChangesAsync(solution, renamed, cancellationToken));
    }

    internal static Task<Solution> RenameAsync(Solution solution, IReadOnlyList<ISymbol> symbols, string newName, bool overloads, CancellationToken cancellationToken)
    {
        // Overloads share their name, so renaming overloads of any one of them renames them all.
        var isOverloadSet = overloads && symbols.All(symbol => symbol is IMethodSymbol && symbol.Name == symbols[0].Name);
        if (symbols.Count != 1 && !isOverloadSet)
            throw new McpException($"The symbol path matches {symbols.Count} symbols; pick one by adding a parameter list, or by position.");
        if (!symbols[0].Locations.Any(location => location.IsInSource))
            throw new McpException($"{symbols[0].ToDisplayString()} is not declared in source, so it cannot be renamed.");
        if (!SyntaxFacts.IsValidIdentifier(newName))
            throw new McpException($"'{newName}' is not a valid C# identifier.");

        // As the IDE does: rename the file only for a type it is named after. Roslyn would rename it for any symbol, methods included.
        var renameFile = symbols[0] is INamedTypeSymbol type
            && type.Locations.Any(location => Path.GetFileNameWithoutExtension(location.SourceTree?.FilePath) == type.Name);
        return Renamer.RenameSymbolAsync(solution, symbols[0], new SymbolRenameOptions(RenameOverloads: overloads, RenameFile: renameFile), newName, cancellationToken);
    }

    public sealed record RenameResult(IReadOnlyList<string> ChangedFiles);
}
