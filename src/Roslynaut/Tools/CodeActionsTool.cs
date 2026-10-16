using System.ComponentModel;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class CodeActionsTool(WorkspaceService workspace, CodeActionService codeActions, ResultCache cache) : IMcpTool
{
    public Delegate Handler => FindCodeActionsAsync;

    [McpServerTool(Name = "code_actions", ReadOnly = true)]
    [Description("Lists the code fixes for the diagnostics at a position or line and the refactorings available there, like an IDE's light bulb: "
        + "add a using, implement an interface, generate a method, fix a warning, and so on. Apply one with apply_code_action. Paged.")]
    private async Task<PagedResult<CodeActionEntry>> FindCodeActionsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.FilePath)] string filePath,
        [Description(ToolDefaults.Line)] int line,
        [Description("Optional 1-based column; without it, the whole line counts.")] int? column = null,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var document = await workspace.GetDocumentAsync(projectPath, filePath);
        var text = await document.GetTextAsync(cancellationToken);
        var span = column is null ? WorkspaceService.LinesOf(text, line, line, filePath) : new TextSpan(WorkspaceService.PositionOf(text, line, column.Value, filePath), 0);
        return await cache.GetPageAsync<CodeActionEntry>(document.Project.Solution, $"code_actions|{filePath}|{line}|{column}", page, pageSize,
            async () => await codeActions.FindAsync(document, span, cancellationToken));
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
        var document = filePath is null ? null : WorkspaceService.GetDocument(solution, filePath);
        var (fixTitle, changed) = await codeActions.FixAllAsync(solution, document, diagnosticId, title, cancellationToken);
        return new(fixTitle, await WorkspaceService.WriteChangesAsync(solution, changed, cancellationToken));
    }
}

public sealed class ApplyCodeActionTool(WorkspaceService workspace, CodeActionService codeActions) : IMcpTool
{
    public Delegate Handler => ApplyCodeActionAsync;

    [McpServerTool(Name = "apply_code_action", Destructive = true)]
    [Description("Applies a code action that code_actions listed, by its id; writes the changed files to disk and returns their paths. "
        + "Ids hold until the code changes.")]
    private async Task<AppliedCodeAction> ApplyCodeActionAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The Id of an entry from code_actions.")] string id,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        var (action, changed) = await codeActions.ApplyAsync(solution, id, cancellationToken);
        return new(action.Title, await WorkspaceService.WriteChangesAsync(solution, changed, cancellationToken));
    }

    public sealed record AppliedCodeAction(string Title, IReadOnlyList<string> ChangedFiles);
}
