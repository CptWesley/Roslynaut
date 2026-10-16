using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class GeneratedSourcesTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => ListGeneratedSourcesAsync;

    [McpServerTool(Name = "generated_sources", ReadOnly = true)]
    [Description("Lists the files that source generators add to the projects, such as for System.Text.Json, regular expressions or logging; "
        + "with a name, returns the content of the matching files. They are part of the compilation but not on disk. Paged.")]
    private async Task<PagedResult<GeneratedSource>> ListGeneratedSourcesAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Optional hint name, or part of it, of the files to return the content of, e.g. 'RegexGenerator' or 'MyContext.g.cs'.")]
        string? name = null,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        return await cache.GetPageAsync(solution, $"generated_sources|{name}", page, pageSize, () => ListAsync(solution, name, cancellationToken));
    }

    internal static async Task<IEnumerable<GeneratedSource>> ListAsync(Solution solution, string? name, CancellationToken cancellationToken)
    {
        var sources = new List<GeneratedSource>();
        foreach (var project in solution.Projects)
        {
            foreach (var document in await project.GetSourceGeneratedDocumentsAsync(cancellationToken))
            {
                if (name is not null && !document.HintName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                var text = await document.GetTextAsync(cancellationToken);
                sources.Add(new(project.Name, document.HintName, document.FilePath, text.Lines.Count, name is null ? null : text.ToString()));
            }
        }

        return sources;
    }

    /// <param name="Name">The hint name the generator gave the file.</param>
    /// <param name="FilePath">The path other tools report locations in this file with.</param>
    /// <param name="Source">The content, when asked for by name.</param>
    public sealed record GeneratedSource(string Project, string Name, string? FilePath, int Lines, string? Source);
}
