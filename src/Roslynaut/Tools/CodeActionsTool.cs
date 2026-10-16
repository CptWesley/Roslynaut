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
        if (line < 1 || line > text.Lines.Count || column < 1)
            throw new McpException($"Position {line}:{column} is outside '{filePath}'.");

        var span = column is null ? text.Lines[line - 1].Span : new TextSpan(text.Lines[line - 1].Start + column.Value - 1, 0);
        return await cache.GetPageAsync<CodeActionEntry>(document.Project.Solution, $"code_actions|{filePath}|{line}|{column}", page, pageSize,
            async () => await codeActions.FindAsync(document, span, cancellationToken));
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
