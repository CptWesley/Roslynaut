using System.ComponentModel;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class CodeActionsTool(WorkspaceService workspace, CodeActionService codeActions, ResultCache cache) : IMcpTool
{
    public Delegate Handler => FindCodeActionsAsync;

    [McpServerTool(Name = "code_actions", ReadOnly = true)]
    [Description("Lists the code fixes for the diagnostics at a position, line or selection and the refactorings available there, like an IDE's "
        + "light bulb: add a using, implement an interface, generate a method, extract a method from the selected statements, introduce a local for "
        + "the selected expression, fix a warning, and so on. Apply one with apply_code_action. Refactorings that need an IDE dialog are left out; "
        + "change_signature and extract_interface run two of them. Paged.")]
    private async Task<PagedResult<CodeActionEntry>> FindCodeActionsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.FilePath)] string filePath,
        [Description(ToolDefaults.Line)] int line,
        [Description("Optional 1-based column; without it, the whole line counts.")] int? column = null,
        [Description("Optional 1-based last line of a selection, such as the statements to extract into a method.")] int? endLine = null,
        [Description("Optional 1-based column where the selection ends, exclusive; without it, the selection runs to the end of endLine.")] int? endColumn = null,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var document = WorkspaceService.Editable(await workspace.GetDocumentAsync(projectPath, filePath, cancellationToken));
        var text = await document.GetTextAsync(cancellationToken);
        var span = SpanOf(text, filePath, line, column, endLine ?? (endColumn is null ? null : line), endColumn);
        return await cache.GetPageAsync<CodeActionEntry>(document.Project.Solution, $"code_actions|{filePath}|{span}", page, pageSize,
            async () => await codeActions.FindAsync(document, span, cancellationToken));
    }

    /// <summary>A selection from line:column to endLine:endColumn; a whole line without a column; a point without an end.</summary>
    internal static TextSpan SpanOf(SourceText text, string filePath, int line, int? column, int? endLine, int? endColumn)
    {
        if (endLine is null)
            return column is null ? WorkspaceService.LinesOf(text, line, line, filePath) : new TextSpan(WorkspaceService.PositionOf(text, line, column.Value, filePath), 0);

        var lines = WorkspaceService.LinesOf(text, line, endLine.Value, filePath);
        var start = column is null ? lines.Start : WorkspaceService.PositionOf(text, line, column.Value, filePath);
        var end = endColumn is null ? lines.End : WorkspaceService.PositionOf(text, endLine.Value, endColumn.Value, filePath);
        return end >= start ? TextSpan.FromBounds(start, end) : throw new McpException("The selection ends before it starts.");
    }
}

public sealed class FixAllTool(WorkspaceService workspace, CodeActionService codeActions) : IMcpTool
{
    public Delegate Handler => FixAllAsync;

    [McpServerTool(Name = "fix_all", Destructive = true)]
    [Description("Fixes every diagnostic with an id at once, in one file or in the whole solution, as an IDE's 'Fix all occurrences' does; "
        + "writes the changed files to disk and returns their paths.")]
    private async Task<ApplyCodeActionTool.AppliedCodeAction> FixAllAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The diagnostic id, such as CS0168 or IDE0005.")] string diagnosticId,
        [Description("Optional C# file to fix in; without it, the whole solution.")] string? filePath = null,
        [Description("Which fix to apply, by its title as code_actions lists it, when there are several; the first otherwise.")] string? title = null,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        var document = filePath is null ? null : WorkspaceService.Editable(await WorkspaceService.GetDocumentAsync(solution, filePath, cancellationToken));
        var (fixTitle, changed) = await codeActions.FixAllAsync(solution, document, diagnosticId, title, cancellationToken);
        return new(fixTitle, await WorkspaceService.WriteChangesAsync(solution, changed, cancellationToken));
    }
}

public sealed class ApplyCodeActionTool(WorkspaceService workspace, CodeActionService codeActions) : IMcpTool
{
    public Delegate Handler => ApplyCodeActionAsync;

    [McpServerTool(Name = "apply_code_action", Destructive = true)]
    [Description("Applies a code action that code_actions listed, by its id; writes the changed files to disk and returns their paths. "
        + "Ids hold until the code changes. An action whose title ends in '...' picks members, as an IDE would ask for in a dialog.")]
    private async Task<AppliedCodeAction> ApplyCodeActionAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The Id of an entry from code_actions.")] string id,
        [Description("For an action that picks members, such as 'Generate overrides...' or 'Generate constructor from members...': the members to "
            + "use, by name; all it offers by default.")] string[]? members = null,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        var (action, changed) = await codeActions.ApplyAsync(solution, id, cancellationToken, members);
        return new(action.Title, await WorkspaceService.WriteChangesAsync(solution, changed, cancellationToken));
    }

    public sealed record AppliedCodeAction(string Title, IReadOnlyList<string> ChangedFiles);
}
