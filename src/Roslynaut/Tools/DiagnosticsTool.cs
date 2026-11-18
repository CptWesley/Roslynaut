using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
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

public sealed class DiagnosticsTool(WorkspaceService workspace, ResultCache cache, NuGetService nuget) : IMcpTool
{
    public Delegate Handler => GetDiagnosticsAsync;

    [McpServerTool(Name = "diagnostics", ReadOnly = true, OpenWorld = true)]
    [Description("Returns compiler and analyzer diagnostics (errors first) for the whole project/solution or a single file, "
        + "with the severities the project's .editorconfig and build settings give them; for a project that did not load, such as one "
        + "whose restore failed, the MSBuild errors that say why; and warnings where the server's compiler is older than the SDK's: options it "
        + "does not know, such as a newer language version, and analyzers or generators it cannot load. With analyzerPackages, also the rules of "
        + "analyzer packages the project does not reference, without changing it; with allRules, every rule, also those turned off; with aot, the "
        + "trim, native AOT and single-file warnings (IL2xxx, IL3xxx) that publishing with PublishTrimmed or PublishAot would give. Paged.")]
    private async Task<PagedResult<DiagnosticEntry>> GetDiagnosticsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Optional path of a C# file to limit diagnostics to.")] string? filePath = null,
        [Description("The least severity to return: Hidden, Info, Warning or Error.")] Severity severity = Severity.Warning,
        [Description("Whether to run the project's analyzers too, such as the .NET code style and quality analyzers; slower.")] bool analyzers = true,
        [Description("NuGet packages of analyzers to run as well, such as 'Roslynator.Analyzers', 'Meziantou.Analyzer' or "
            + "'Microsoft.CodeAnalysis.NetAnalyzers@10.0.100' (id, optionally @version, else the latest); downloaded into the global packages "
            + "folder from the project's package sources. code_actions knows only the project's own analyzers.")] string[]? analyzerPackages = null,
        [Description("Turns on every rule of the analyzers, also those off by default or turned off by .editorconfig, a global config or the "
            + "SDK's analysis level, as warnings (or errors, if that is their default); combine with ids to keep the result short.")] bool allRules = false,
        [Description("Also runs the trim, native AOT and single-file analyzers, as IsAotCompatible turns them on, without changing the project: "
            + "the code a trimmed or native AOT publish would break, such as reflection, Type.GetType by name and dynamic code. Downloads "
            + "Microsoft.NET.ILLink.Tasks into the global packages folder.")] bool aot = false,
        [Description("Optional regex for the diagnostic ids to return, e.g. '^CA18' or '^(RCS|MA)'.")] string? ids = null,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var loaded = await workspace.GetAsync(projectPath);
        var idPattern = ProcessDefaults.Pattern(ids, "ids");
        var packages = analyzerPackages ?? [];
        return await cache.GetPageAsync(loaded.Solution, $"diagnostics|{filePath}|{severity}|{analyzers}|{string.Join(',', packages)}|{allRules}|{aot}|{ids}", page, pageSize, async () =>
        {
            var solution = packages.Length == 0 ? loaded.Solution : await WithAnalyzerPackagesAsync(loaded.Solution, projectPath, packages, nuget, cancellationToken);
            if (aot)
                solution = await WithAotAnalyzersAsync(solution, projectPath, nuget, cancellationToken);
            return (await CollectAsync(solution, filePath, (DiagnosticSeverity)severity, analyzers || packages.Length > 0 || aot, loaded.LoadErrors, loaded.Warnings, cancellationToken, allRules))
                .Where(entry => idPattern is null || idPattern.IsMatch(entry.Id));
        });
    }

    // One loader for the packages' analyzers, which do not change: a package version's files never do.
    private static readonly Lazy<IAnalyzerAssemblyLoader> PackageLoader = new(WorkspaceService.NonLockingAnalyzerLoader);
    private static readonly Version? CompilerVersion = typeof(Compilation).Assembly.GetName().Version;

    /// <summary>
    /// The solution with the analyzers of NuGet packages added to its C# projects, as a reference to the package would add them: those under
    /// analyzers/dotnet, in the folder of the newest Roslyn version the server's compiler can load (roslyn4.8 and the like), language-neutral or C#.
    /// </summary>
    internal static async Task<Solution> WithAnalyzerPackagesAsync(Solution solution, string projectPath, IReadOnlyList<string> packages, NuGetService nuget, CancellationToken cancellationToken)
    {
        var references = new List<AnalyzerReference>();
        foreach (var package in packages)
        {
            var (id, version) = package.Split('@', 2) is [var name, var number] ? (name.Trim(), number.Trim()) : (package.Trim(), null);
            var directory = await nuget.DownloadAsync(projectPath, await nuget.ResolveAsync(projectPath, id, version, cancellationToken), cancellationToken);
            var files = AnalyzerFilesOf(directory);
            if (files.Count == 0)
                throw new McpException($"{package} has no analyzers for C# (in analyzers/dotnet).");
            references.AddRange(files.Select(file => new AnalyzerFileReference(file, PackageLoader.Value)));
        }

        return solution.Projects.Where(project => project.Language == LanguageNames.CSharp)
            .Aggregate(solution, (current, project) => current.AddAnalyzerReferences(project.Id, references));
    }

    /// <summary>
    /// The solution with the trim, AOT and single-file analyzers turned on in its C# projects, as IsAotCompatible turns them on: the analyzers
    /// of the package the SDK takes them from, and a global config with the MSBuild properties they read. Its level is above the default of
    /// the config the build generates, which may set them otherwise.
    /// </summary>
    internal static async Task<Solution> WithAotAnalyzersAsync(Solution solution, string projectPath, NuGetService nuget, CancellationToken cancellationToken)
    {
        solution = await WithAnalyzerPackagesAsync(solution, projectPath, ["Microsoft.NET.ILLink.Tasks"], nuget, cancellationToken);
        var config = SourceText.From("""
            is_global = true
            global_level = 1000
            build_property.EnableTrimAnalyzer = true
            build_property.EnableAotAnalyzer = true
            build_property.EnableSingleFileAnalyzer = true
            """, System.Text.Encoding.UTF8);
        return solution.Projects.Where(project => project.Language == LanguageNames.CSharp).Select(project => project.Id).Aggregate(solution, (current, id) =>
        {
            var directory = Path.GetDirectoryName(current.GetProject(id)!.FilePath ?? Path.GetFullPath(projectPath))!;
            return current.GetProject(id)!.AddAnalyzerConfigDocument("roslynaut-aot.globalconfig", config, filePath: Path.Combine(directory, "roslynaut-aot.globalconfig")).Project.Solution;
        });
    }

    internal static IReadOnlyList<string> AnalyzerFilesOf(string packageDirectory)
    {
        var root = Path.Combine(packageDirectory, "analyzers", "dotnet");
        if (!Directory.Exists(root))
            return [];

        var newest = Directory.EnumerateDirectories(root, "roslyn*")
            .Select(folder => (Folder: folder, Version: Version.TryParse(Path.GetFileName(folder)["roslyn".Length..], out var version) ? version : null))
            .Where(folder => folder.Version is not null && folder.Version <= CompilerVersion)
            .OrderByDescending(folder => folder.Version).Select(folder => folder.Folder).FirstOrDefault() ?? root;
        return [.. ((string[])[newest, Path.Combine(newest, "cs")]).Where(Directory.Exists).SelectMany(folder => Directory.EnumerateFiles(folder, "*.dll"))];
    }

    /// <param name="allRules">Turns on every rule of the analyzers, whatever their defaults and the project's configuration say.</param>
    internal static async Task<IEnumerable<DiagnosticEntry>> CollectAsync(
        Solution solution, string? filePath, DiagnosticSeverity severity, bool analyzers, IReadOnlyDictionary<string, IReadOnlyList<string>> loadErrors,
        IReadOnlyDictionary<string, ConcurrentQueue<Diagnostic>> warnings, CancellationToken cancellationToken, bool allRules = false)
    {
        var diagnostics = new List<Diagnostic>();
        var document = filePath is null ? null : await WorkspaceService.GetDocumentAsync(solution, filePath, cancellationToken);

        // A project that did not load has neither sources nor compiler options, so what the compiler makes of it says nothing; its errors do.
        foreach (var project in document is null ? solution.Projects : [document.Project])
        {
            if (project.FilePath is null || !loadErrors.ContainsKey(project.FilePath))
                diagnostics.AddRange(await GetAsync(project, document, analyzers, cancellationToken, allRules));

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
    /// <param name="allRules">
    /// Turns on every rule of the analyzers: as warnings, or errors where that is their default, without the severities the project's
    /// configuration files give them, which also turn rules off.
    /// </param>
    internal static async Task<List<Diagnostic>> GetAsync(Project project, Document? document, bool analyzers, CancellationToken cancellationToken, bool allRules = false)
    {
        var compilation = (await project.GetCompilationAsync(cancellationToken))!;
        var model = document is null ? null : (await document.GetSemanticModelAsync(cancellationToken))!;
        var diagnostics = (model?.GetDiagnostics(cancellationToken: cancellationToken) ?? compilation.GetDiagnostics(cancellationToken)).ToList();
        if (analyzers && Analyzers(project) is { IsEmpty: false } projectAnalyzers)
        {
            if (allRules)
            {
                var rules = projectAnalyzers.SelectMany(analyzer => analyzer.SupportedDiagnostics).GroupBy(rule => rule.Id)
                    .ToDictionary(rule => rule.Key, rule => rule.Any(descriptor => descriptor.DefaultSeverity == DiagnosticSeverity.Error) ? ReportDiagnostic.Error : ReportDiagnostic.Warn);
                compilation = compilation.WithOptions(compilation.Options.WithSyntaxTreeOptionsProvider(null).WithSpecificDiagnosticOptions(rules));
                model = model is null ? null : compilation.GetSemanticModel(compilation.SyntaxTrees.First(tree => tree.FilePath == model.SyntaxTree.FilePath));
            }

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
