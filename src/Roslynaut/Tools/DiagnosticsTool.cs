using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

/// <summary>The order of <see cref="DiagnosticSeverity"/>, as names in JSON.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    Hidden,
    Info,
    Warning,
    Error,
}

public sealed class DiagnosticsTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => GetDiagnosticsAsync;

    [McpServerTool(Name = "diagnostics", ReadOnly = true)]
    [Description("Returns compiler and analyzer diagnostics (errors first) for the whole project/solution or a single file, "
        + "with the severities the project's .editorconfig and build settings give them. Paged.")]
    private async Task<PagedResult<DiagnosticEntry>> GetDiagnosticsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Optional path of a C# file to limit diagnostics to.")] string? filePath = null,
        [Description("The least severity to return: Hidden, Info, Warning or Error.")] Severity severity = Severity.Warning,
        [Description("Whether to run the project's analyzers too, such as the .NET code style and quality analyzers; slower.")] bool analyzers = true,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        return await cache.GetPageAsync(solution, $"diagnostics|{filePath}|{severity}|{analyzers}", page, pageSize,
            () => CollectAsync(solution, filePath, (DiagnosticSeverity)severity, analyzers, cancellationToken));
    }

    internal static async Task<IEnumerable<DiagnosticEntry>> CollectAsync(
        Solution solution, string? filePath, DiagnosticSeverity severity, bool analyzers, CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        var document = filePath is null ? null : WorkspaceService.GetDocument(solution, filePath);
        foreach (var project in document is null ? solution.Projects : [document.Project])
            diagnostics.AddRange(await GetAsync(project, document, analyzers, cancellationToken));

        return diagnostics
            .Where(diagnostic => diagnostic.Severity >= severity)
            .OrderByDescending(diagnostic => diagnostic.Severity)
            .Select(diagnostic => new DiagnosticEntry(
                diagnostic.Id,
                diagnostic.Severity.ToString(),
                diagnostic.GetMessage(),
                diagnostic.Location.IsInSource ? SourceLocation.From(diagnostic.Location) : null))
            .Distinct();
    }

    /// <summary>The diagnostics of a project, or of one of its documents, of any severity; suppressed ones are left out.</summary>
    internal static async Task<List<Diagnostic>> GetAsync(Project project, Document? document, bool analyzers, CancellationToken cancellationToken)
    {
        var compilation = (await project.GetCompilationAsync(cancellationToken))!;
        var model = document is null ? null : (await document.GetSemanticModelAsync(cancellationToken))!;
        var diagnostics = (model?.GetDiagnostics(cancellationToken: cancellationToken) ?? compilation.GetDiagnostics(cancellationToken)).ToList();
        if (analyzers && Analyzers(project) is { IsEmpty: false } projectAnalyzers)
        {
            var analysis = compilation.WithAnalyzers(projectAnalyzers, project.AnalyzerOptions);
            if (model is null)
            {
                diagnostics.AddRange(await analysis.GetAnalyzerDiagnosticsAsync(cancellationToken));
            }
            else
            {
                diagnostics.AddRange(await analysis.GetAnalyzerSyntaxDiagnosticsAsync(model.SyntaxTree, cancellationToken));
                diagnostics.AddRange(await analysis.GetAnalyzerSemanticDiagnosticsAsync(model, filterSpan: null, cancellationToken));
            }
        }

        return [.. diagnostics.Where(diagnostic => !diagnostic.IsSuppressed)];
    }

    private static ImmutableArray<DiagnosticAnalyzer> Analyzers(Project project) =>
        [.. project.AnalyzerReferences.SelectMany(reference => reference.GetAnalyzers(project.Language))];

    public sealed record DiagnosticEntry(string Id, string Severity, string Message, SourceLocation? Location);
}
