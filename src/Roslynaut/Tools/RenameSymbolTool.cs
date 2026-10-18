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

    internal static async Task<Solution> RenameAsync(Solution solution, IReadOnlyList<ISymbol> symbols, string newName, bool overloads, CancellationToken cancellationToken)
    {
        // Overloads share their name, so renaming overloads of any one of them renames them all.
        var isOverloadSet = overloads && symbols.All(symbol => symbol is IMethodSymbol && symbol.Name == symbols[0].Name);
        if (symbols.Count != 1 && !isOverloadSet)
            throw new McpException($"The symbol path matches {symbols.Count} symbols; pick one by adding a parameter list, or by position.");
        if (!symbols[0].Locations.Any(location => location.IsInSource))
            throw new McpException($"{symbols[0].ToDisplayString()} is not declared in source, so it cannot be renamed.");
        // Roslyn's rename throws on these: their names are their operator tokens or types.
        if (symbols[0] is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion })
            throw new McpException($"{symbols[0].ToDisplayString()} is an operator, which has no name to rename.");
        // @ only escapes a name, such as @class for a keyword, which the rename does where the name needs it.
        newName = newName.StartsWith('@') ? newName[1..] : newName;
        if (!SyntaxFacts.IsValidIdentifier(newName))
            throw new McpException($"'{newName}' is not a valid C# identifier.");

        // As the IDE does: rename the file only for a type it is named after. Roslyn would rename it for any symbol, methods included.
        // And only to a name no other file beside it has, where Roslyn would make one up, such as Gadget_1.cs.
        var renameFile = symbols[0] is INamedTypeSymbol type
            && type.Locations.Any(location => location.SourceTree?.FilePath is { } file && Path.GetFileNameWithoutExtension(file) == type.Name
                && Path.Combine(Path.GetDirectoryName(file)!, newName + Path.GetExtension(file)) is var target
                && (target.Equals(file, StringComparison.OrdinalIgnoreCase) || !File.Exists(target)));
        var renamed = await Renamer.RenameSymbolAsync(solution, symbols[0], new SymbolRenameOptions(RenameOverloads: overloads, RenameFile: renameFile), newName, cancellationToken);

        // Roslyn renames even where it cannot resolve a conflict, such as with a member that has the new name already, and leaves code that
        // does not compile, as an IDE shows.
        // ponytail: only the projects the rename changed; a conflict in a project that depends on them without using the symbol goes unseen.
        var introduced = await DiagnosticsTool.IntroducedErrorsAsync(solution, renamed, renamed.GetChanges(solution).GetProjectChanges().Select(change => change.ProjectId), cancellationToken);
        if (introduced.Count > 0)
        {
            throw new McpException($"Renaming {symbols[0].ToDisplayString()} to {newName} would leave code that does not compile, so nothing changed: "
                + string.Join(" ", introduced.Take(5).Select(error => $"{error.Id} {error.GetMessage()} ({Path.GetFileName(error.Location.SourceTree?.FilePath)}:{error.Location.GetLineSpan().StartLinePosition.Line + 1})")));
        }

        return renamed;
    }

    public sealed record RenameResult(IReadOnlyList<string> ChangedFiles);
}
