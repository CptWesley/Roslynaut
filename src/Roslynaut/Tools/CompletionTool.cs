using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class CompletionTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => GetCompletionsAsync;

    [McpServerTool(Name = "completion", ReadOnly = true)]
    [Description("Lists what can be written at a position, as an IDE's completion list does: the members after a dot, or the locals, "
        + "parameters, members, types and keywords in scope there. A filter keeps the list short. Paged.")]
    private async Task<PagedResult<CompletionEntry>> GetCompletionsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.FilePath)] string filePath,
        [Description(ToolDefaults.Line)] int line,
        [Description("1-based column where the text would go, such as just after a dot.")] int column,
        [Description("Optional: only items whose name starts with this, ignoring case.")] string? filter = null,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var document = await workspace.GetDocumentAsync(projectPath, filePath);
        var position = WorkspaceService.PositionOf(await document.GetTextAsync(cancellationToken), line, column, filePath);
        return await cache.GetPageAsync(document.Project.Solution, $"completion|{filePath}|{line}|{column}|{filter}", page, pageSize,
            () => CompleteAsync(document, position, filter, cancellationToken));
    }

    internal static async Task<IEnumerable<CompletionEntry>> CompleteAsync(Document document, int position, string? filter, CancellationToken cancellationToken)
    {
        var service = CompletionService.GetService(document) ?? throw new McpException("Completion is not available for this document.");
        var completions = await service.GetCompletionsAsync(document, position, cancellationToken: cancellationToken);
        return completions.ItemsList
            .Where(item => filter is null || item.FilterText.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.SortText, StringComparer.Ordinal)
            .Select(item => new CompletionEntry(
                item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix,
                item.Tags.FirstOrDefault(),
                string.IsNullOrEmpty(item.InlineDescription) ? null : item.InlineDescription))
            .ToList();
    }

    /// <param name="Kind">What the item is, such as Method, Property, Local, Class or Keyword.</param>
    /// <param name="Detail">More about it, such as the namespace a type would be imported from.</param>
    public sealed record CompletionEntry(string Text, string? Kind, string? Detail);
}
