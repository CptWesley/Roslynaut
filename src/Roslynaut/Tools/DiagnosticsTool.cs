using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class DiagnosticsTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => GetDiagnosticsAsync;

    [McpServerTool(Name = "diagnostics", ReadOnly = true)]
    [Description("Returns compiler errors and warnings (errors first, at most 100) for the whole project/solution or a single file.")]
    private async Task<IEnumerable<DiagnosticEntry>> GetDiagnosticsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Optional path of a C# file to limit diagnostics to.")] string? filePath = null,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<Diagnostic> diagnostics;
        if (filePath is null)
        {
            var solution = (await workspace.GetAsync(projectPath)).Solution;
            var compilations = await Task.WhenAll(solution.Projects.Select(project => project.GetCompilationAsync(cancellationToken)));
            diagnostics = compilations.SelectMany(compilation => compilation!.GetDiagnostics(cancellationToken));
        }
        else
        {
            var document = await workspace.GetDocumentAsync(projectPath, filePath);
            diagnostics = (await document.GetSemanticModelAsync(cancellationToken))!.GetDiagnostics(cancellationToken: cancellationToken);
        }

        return diagnostics
            .Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)
            .OrderByDescending(diagnostic => diagnostic.Severity)
            .Take(ToolDefaults.MaxResults)
            .Select(diagnostic => new DiagnosticEntry(
                diagnostic.Id,
                diagnostic.Severity.ToString(),
                diagnostic.GetMessage(),
                diagnostic.Location.IsInSource ? SourceLocation.From(diagnostic.Location) : null));
    }

    public sealed record DiagnosticEntry(string Id, string Severity, string Message, SourceLocation? Location);
}
