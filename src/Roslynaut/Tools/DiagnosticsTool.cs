using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class DiagnosticsTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => GetDiagnosticsAsync;

    [McpServerTool(Name = "diagnostics", ReadOnly = true)]
    [Description("Returns compiler errors and warnings (errors first) for the whole project/solution or a single file. Paged.")]
    private async Task<PagedResult<DiagnosticEntry>> GetDiagnosticsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Optional path of a C# file to limit diagnostics to.")] string? filePath = null,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        return await cache.GetPageAsync(solution, $"diagnostics|{filePath}", page, pageSize,
            () => CollectAsync(solution, filePath, cancellationToken));
    }

    private static async Task<IEnumerable<DiagnosticEntry>> CollectAsync(Solution solution, string? filePath, CancellationToken cancellationToken)
    {
        IEnumerable<Diagnostic> diagnostics;
        if (filePath is null)
        {
            var compilations = await Task.WhenAll(solution.Projects.Select(project => project.GetCompilationAsync(cancellationToken)));
            diagnostics = compilations.SelectMany(compilation => compilation!.GetDiagnostics(cancellationToken));
        }
        else
        {
            var document = WorkspaceService.GetDocument(solution, filePath);
            diagnostics = (await document.GetSemanticModelAsync(cancellationToken))!.GetDiagnostics(cancellationToken: cancellationToken);
        }

        return diagnostics
            .Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)
            .OrderByDescending(diagnostic => diagnostic.Severity)
            .Select(diagnostic => new DiagnosticEntry(
                diagnostic.Id,
                diagnostic.Severity.ToString(),
                diagnostic.GetMessage(),
                diagnostic.Location.IsInSource ? SourceLocation.From(diagnostic.Location) : null));
    }

    public sealed record DiagnosticEntry(string Id, string Severity, string Message, SourceLocation? Location);
}
