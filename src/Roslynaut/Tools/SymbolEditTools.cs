using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

/// <summary>Edits by symbol, as Serena's tools do: replace a declaration, insert before or after it, or delete it when unused.</summary>
internal static class SymbolEdits
{
    public enum Placement
    {
        Replace,
        Before,
        After,
    }

    /// <summary>
    /// Replaces the declaration (from the start of its first line, as symbol_source returns it), or inserts code on the lines before it
    /// (and before its documentation comment) or after it.
    /// </summary>
    public static async Task<Solution> EditAsync(Solution solution, IReadOnlyList<ISymbol> symbols, Placement placement, string code, CancellationToken cancellationToken)
    {
        var (document, node) = await DeclarationAsync(solution, symbols, cancellationToken);
        var text = await document.GetTextAsync(cancellationToken);
        var newLine = text.Lines.Count > 1 && text.ToString(TextSpan.FromBounds(text.Lines[0].End, text.Lines[0].EndIncludingLineBreak)) is { Length: > 0 } lineBreak ? lineBreak : "\n";
        var lines = code.EndsWith('\n') ? code : code + newLine;

        TextChange change = placement switch
        {
            Placement.Replace => new(TextSpan.FromBounds(text.Lines.GetLineFromPosition(node.SpanStart).Start, node.Span.End), code.TrimEnd('\r', '\n')),
            Placement.Before => new(new TextSpan(text.Lines.GetLineFromPosition(StartIncludingComments(node)).Start, 0), lines),
            _ => new(new TextSpan(text.Lines.GetLineFromPosition(node.Span.End).EndIncludingLineBreak, 0), lines),
        };
        return solution.WithDocumentText(document.Id, text.WithChanges(change));
    }

    /// <summary>Deletes the symbol's declarations if nothing outside them references it; otherwise returns those references.</summary>
    public static async Task<(Solution? Deleted, IReadOnlyList<SourceLocation> References)> SafeDeleteAsync(
        Solution solution, IReadOnlyList<ISymbol> symbols, CancellationToken cancellationToken)
    {
        if (symbols is not [var symbol])
            throw new McpException($"The symbol path matches {symbols.Count} symbols; pick one by adding a parameter list, or by position.");

        var declarations = await Task.WhenAll(symbol.DeclaringSyntaxReferences.Select(reference => reference.GetSyntaxAsync(cancellationToken)));
        if (declarations.Length == 0)
            throw new McpException($"{symbol.ToDisplayString()} is not declared in source.");

        var references = (await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
            .SelectMany(reference => reference.Locations)
            .Select(location => location.Location)
            .Where(location => !declarations.Any(declaration => declaration.SyntaxTree == location.SourceTree && declaration.FullSpan.Contains(location.SourceSpan)))
            .Select(SourceLocation.From)
            .ToList();
        if (references.Count > 0)
            return (null, references);

        var editor = new SolutionEditor(solution);
        foreach (var declaration in declarations)
            (await editor.GetDocumentEditorAsync(solution.GetDocumentId(declaration.SyntaxTree)!, cancellationToken)).RemoveNode(declaration);
        return (editor.GetChangedSolution(), []);
    }

    private static async Task<(Document Document, SyntaxNode Node)> DeclarationAsync(Solution solution, IReadOnlyList<ISymbol> symbols, CancellationToken cancellationToken)
    {
        if (symbols is not [var symbol] || symbol.DeclaringSyntaxReferences is not [var reference])
        {
            throw new McpException(symbols.Count != 1
                ? $"The symbol path matches {symbols.Count} symbols; pick one by adding a parameter list, or by position."
                : $"{symbols[0].ToDisplayString()} has {symbols[0].DeclaringSyntaxReferences.Length} declarations; edit one by position.");
        }

        var node = await reference.GetSyntaxAsync(cancellationToken);

        // A field or event declares its symbols in variable declarators; edit the whole declaration.
        if (node is VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax field })
            node = field;
        return (solution.GetDocument(node.SyntaxTree) ?? throw new McpException("The declaration is generated code."), node);
    }

    /// <summary>Where a declaration starts with its documentation and other comments.</summary>
    private static int StartIncludingComments(SyntaxNode node) =>
        node.GetLeadingTrivia().FirstOrDefault(trivia => !trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia)) is { RawKind: not 0 } first
            ? first.SpanStart
            : node.SpanStart;
}

public sealed class ReplaceSymbolBodyTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => ReplaceAsync;

    [McpServerTool(Name = "replace_symbol_body", Destructive = true)]
    [Description("Replaces a symbol's whole declaration, such as a method with its signature, with new code; symbol_source returns the code "
        + "this replaces, from the start of its first line. Writes the file to disk.")]
    private async Task<EditResult> ReplaceAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The new declaration, indented as it should be.")] string code,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        var edited = await SymbolEdits.EditAsync(solution, symbols, SymbolEdits.Placement.Replace, code, cancellationToken);
        return new(await WorkspaceService.WriteChangesAsync(solution, edited, cancellationToken));
    }
}

public sealed class InsertBeforeSymbolTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => InsertAsync;

    [McpServerTool(Name = "insert_before_symbol", Destructive = true)]
    [Description("Inserts code on new lines before a symbol's declaration and its documentation comment, such as a new member or a using. "
        + "Writes the file to disk.")]
    private async Task<EditResult> InsertAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The code to insert, indented as it should be.")] string code,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        var edited = await SymbolEdits.EditAsync(solution, symbols, SymbolEdits.Placement.Before, code, cancellationToken);
        return new(await WorkspaceService.WriteChangesAsync(solution, edited, cancellationToken));
    }
}

public sealed class InsertAfterSymbolTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => InsertAsync;

    [McpServerTool(Name = "insert_after_symbol", Destructive = true)]
    [Description("Inserts code on new lines after a symbol's declaration, such as a new member after an existing one. Writes the file to disk.")]
    private async Task<EditResult> InsertAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The code to insert, indented as it should be.")] string code,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        var edited = await SymbolEdits.EditAsync(solution, symbols, SymbolEdits.Placement.After, code, cancellationToken);
        return new(await WorkspaceService.WriteChangesAsync(solution, edited, cancellationToken));
    }
}

public sealed class SafeDeleteSymbolTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => DeleteAsync;

    [McpServerTool(Name = "safe_delete_symbol", Destructive = true)]
    [Description("Deletes a symbol's declaration, but only if nothing else references it; otherwise deletes nothing and returns the references. "
        + "Writes the changed files to disk.")]
    private async Task<SafeDeleteResult> DeleteAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        var (deleted, references) = await SymbolEdits.SafeDeleteAsync(solution, symbols, cancellationToken);
        return deleted is null ? new(false, [], references) : new(true, await WorkspaceService.WriteChangesAsync(solution, deleted, cancellationToken), []);
    }

    /// <param name="References">Why it was not deleted.</param>
    public sealed record SafeDeleteResult(bool Deleted, IReadOnlyList<string> ChangedFiles, IReadOnlyList<SourceLocation> References);
}
