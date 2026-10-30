using System.Collections.Concurrent;
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
        + "with the severities the project's .editorconfig and build settings give them; for a project that did not load, such as one "
        + "whose restore failed, the MSBuild errors that say why; and warnings where the server's compiler is older than the SDK's: options it "
        + "does not know, such as a newer language version, and analyzers or generators it cannot load. Paged.")]
    private async Task<PagedResult<DiagnosticEntry>> GetDiagnosticsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Optional path of a C# file to limit diagnostics to.")] string? filePath = null,
        [Description("The least severity to return: Hidden, Info, Warning or Error.")] Severity severity = Severity.Warning,
        [Description("Whether to run the project's analyzers too, such as the .NET code style and quality analyzers; slower.")] bool analyzers = true,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var loaded = await workspace.GetAsync(projectPath);
        return await cache.GetPageAsync(loaded.Solution, $"diagnostics|{filePath}|{severity}|{analyzers}", page, pageSize,
            () => CollectAsync(loaded.Solution, filePath, (DiagnosticSeverity)severity, analyzers, loaded.LoadErrors, loaded.Warnings, cancellationToken));
    }

    internal static async Task<IEnumerable<DiagnosticEntry>> CollectAsync(
        Solution solution, string? filePath, DiagnosticSeverity severity, bool analyzers, IReadOnlyDictionary<string, IReadOnlyList<string>> loadErrors,
        IReadOnlyDictionary<string, ConcurrentQueue<Diagnostic>> warnings, CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        var document = filePath is null ? null : await WorkspaceService.GetDocumentAsync(solution, filePath, cancellationToken);

        // A project that did not load has neither sources nor compiler options, so what the compiler makes of it says nothing; its errors do.
        foreach (var project in document is null ? solution.Projects : [document.Project])
        {
            if (project.FilePath is null || !loadErrors.ContainsKey(project.FilePath))
                diagnostics.AddRange(await GetAsync(project, document, analyzers, cancellationToken));

            // The project's own, of no file; once its compilation has loaded its analyzers and generators, with those that did not load.
            if (document is null && project.FilePath is not null && warnings.TryGetValue(project.FilePath, out var projectWarnings))
                diagnostics.AddRange(projectWarnings);
        }

        var unloaded = document is null ? loadErrors.SelectMany(project => project.Value.Select(error => LoadErrorOf(project.Key, error))) : [];
        return unloaded.Concat(diagnostics
            .Where(diagnostic => diagnostic.Severity >= severity)
            .OrderByDescending(diagnostic => diagnostic.Severity)
            .Select(EntryOf))
            .Distinct();
    }

    /// <summary>An MSBuild error that kept a project from loading, such as "NU1101: Unable to find package X.", which has no source location.</summary>
    private static DiagnosticEntry LoadErrorOf(string project, string error)
    {
        var (id, message) = error.Split(": ", 2) is [var code, var rest] && code.Length > 0 && code.All(char.IsAsciiLetterOrDigit) && char.IsAsciiDigit(code[^1]) ? (code, rest) : ("MSBuild", error);
        return new(id, "Error", $"{message} {Path.GetFileName(project)} did not load.", null);
    }

    internal static DiagnosticEntry EntryOf(Diagnostic diagnostic) => new(
        diagnostic.Id,
        diagnostic.Severity.ToString(),
        diagnostic.GetMessage(),
        diagnostic.Location.IsInSource ? SourceLocation.From(diagnostic.Location) : null);

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

    /// <summary>
    /// The compiler errors an edit adds to some projects. Compared by id and file: the messages of errors there before can name what the edit
    /// changed, such as a symbol by its new name.
    /// </summary>
    internal static async Task<List<Diagnostic>> IntroducedErrorsAsync(Solution before, Solution after, IEnumerable<ProjectId> projects, CancellationToken cancellationToken)
    {
        var ids = projects.ToList();
        var (old, now) = (await ErrorsAsync(before, ids, cancellationToken), await ErrorsAsync(after, ids, cancellationToken));
        return [.. now.Where(error => now.Count(other => Key(other) == Key(error)) > old.Count(other => Key(other) == Key(error)))];

        static (string, string?) Key(Diagnostic error) => (error.Id, error.Location.SourceTree?.FilePath);
    }

    private static async Task<List<Diagnostic>> ErrorsAsync(Solution solution, IEnumerable<ProjectId> projects, CancellationToken cancellationToken)
    {
        var errors = new List<Diagnostic>();
        foreach (var project in projects.Select(solution.GetProject).OfType<Project>())
            errors.AddRange((await GetAsync(project, null, analyzers: false, cancellationToken)).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return errors;
    }

    private static ImmutableArray<DiagnosticAnalyzer> Analyzers(Project project) =>
        [.. project.AnalyzerReferences.SelectMany(reference => reference.GetAnalyzers(project.Language))];

    public sealed record DiagnosticEntry(string Id, string Severity, string Message, SourceLocation? Location);
}
