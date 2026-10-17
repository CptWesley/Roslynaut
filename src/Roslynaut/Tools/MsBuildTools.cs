using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class MsBuildEvaluateTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => EvaluateAsync;

    [McpServerTool(Name = "msbuild_evaluate", ReadOnly = true)]
    [Description("Returns MSBuild properties and items per project, as its design-time build evaluated them, such as TargetPath, "
        + "DefineConstants, Compile or PackageReference. Names may contain * wildcards.")]
    private async Task<IEnumerable<Evaluation>> EvaluateAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Property or item names, e.g. 'TargetPath', '*Version*' or 'PackageReference'.")] string[] names,
        CancellationToken cancellationToken = default) =>
        Evaluate(await workspace.GetAsync(projectPath), names);

    internal static IEnumerable<Evaluation> Evaluate(LoadedWorkspace workspace, IReadOnlyCollection<string> names)
    {
        var patterns = names.Select(name => new Regex($"^{Regex.Escape(name).Replace(@"\*", ".*")}$", RegexOptions.IgnoreCase)).ToList();
        bool Matches(string name) => patterns.Any(pattern => pattern.IsMatch(name));

        return workspace.Results.Select(result => new Evaluation(
            result.ProjectFilePath,
            result.Properties.Where(property => Matches(property.Key)).OrderBy(property => property.Key).ToDictionary(),
            result.Items.Where(item => Matches(item.Key)).ToDictionary(
                item => item.Key,
                item => (IReadOnlyList<Item>)[.. item.Value.Select(value => new Item(value.ItemSpec, value.Metadata))])));
    }

    public sealed record Evaluation(string Project, IReadOnlyDictionary<string, string> Properties, IReadOnlyDictionary<string, IReadOnlyList<Item>> Items);

    /// <param name="Include">The item's value, such as a file path or a package id.</param>
    public sealed record Item(string Include, IReadOnlyDictionary<string, string> Metadata);
}

public sealed class MsBuildPropertyTraceTool : IMcpTool
{
    public Delegate Handler => TraceAsync;

    [McpServerTool(Name = "msbuild_property_trace", ReadOnly = true)]
    [Description("Explains where an MSBuild property gets its value: every place in the project and its imports, such as Directory.Build.props "
        + "and the SDK's props and targets, that sets it, in evaluation order, each with its file, condition and the text it sets; and the value it "
        + "ends with. Settings inside a target apply only when that target runs. Global properties and environment variables can set it too.")]
    internal static async Task<PropertyTrace> TraceAsync(
        [Description("Path to a .csproj file.")] string projectPath,
        [Description("The property, such as LangVersion or OutputPath.")] string name,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(projectPath);
        if (!File.Exists(path) || Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx")
            throw new McpException($"Not a project file: {path}");

        // The preprocessed project: the project with every import inlined, in evaluation order.
        var preprocessed = Path.GetTempFileName();
        try
        {
            var directory = Path.GetDirectoryName(path);
            var (exitCode, output) = await DotnetCli.RunAsync(["msbuild", path, $"-pp:{preprocessed}", "-nologo"], TimeSpan.FromMinutes(5), cancellationToken, workingDirectory: directory);
            if (exitCode != 0)
                throw new McpException($"dotnet msbuild could not read the project: {BuildTool.Tail(output)}");

            var (_, value) = await DotnetCli.RunAsync(["msbuild", path, $"-getProperty:{name}", "-nologo"], TimeSpan.FromMinutes(5), cancellationToken, workingDirectory: directory);
            return new(name, value.Trim(), Definitions(XDocument.Load(preprocessed), name));
        }
        finally
        {
            File.Delete(preprocessed);
        }
    }

    /// <summary>
    /// The definitions of a property in a preprocessed project, in order. Comments between imports name the file that follows; within them
    /// MSBuild writes "--" as "__", since XML comments cannot hold it.
    /// </summary>
    internal static IReadOnlyList<PropertyDefinition> Definitions(XDocument preprocessed, string name)
    {
        var definitions = new List<PropertyDefinition>();
        string? file = null;
        foreach (var node in preprocessed.DescendantNodes())
        {
            if (node is XComment comment && comment.Value.Contains("=========="))
            {
                // ponytail: a path with "--" in it comes back with "__".
                file = comment.Value.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0 && Path.IsPathRooted(line)) ?? file;
                continue;
            }

            if (node is not XElement { Parent: { Name.LocalName: "PropertyGroup" } group } element || !element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            // Conditions of Choose/When around the group count too.
            var conditions = element.AncestorsAndSelf().Select(ancestor => ancestor.Attribute("Condition")?.Value).OfType<string>().Reverse().ToList();
            definitions.Add(new(
                file,
                conditions.Count == 0 ? null : string.Join(" and ", conditions),
                element.Value.Trim(),
                group.Ancestors().FirstOrDefault(ancestor => ancestor.Name.LocalName == "Target")?.Attribute("Name")?.Value));
        }

        return definitions;
    }

    /// <param name="Value">The value the project's evaluation ends with; for a project with several target frameworks, before it picks one.</param>
    /// <param name="Definitions">Every place that sets it, in evaluation order: later ones whose conditions hold win.</param>
    public sealed record PropertyTrace(string Property, string Value, IReadOnlyList<PropertyDefinition> Definitions);

    /// <param name="Value">The text it sets, before $(...) and conditions are evaluated.</param>
    /// <param name="Target">The target it is in, if any: then it applies only when that target runs.</param>
    public sealed record PropertyDefinition(string? File, string? Condition, string Value, string? Target);
}

public sealed class WorkspaceOptionsTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => ConfigureAsync;

    [McpServerTool(Name = "workspace_options", Idempotent = true)]
    [Description("Reloads a project or solution for another target framework or configuration, such as net48 or Release, so that code under "
        + "#if for it shows, with its diagnostics; every later call with this path uses them. Omit both to go back to the defaults: each "
        + "project's first target framework, and its own configuration. Returns each project with the framework it loaded and the ones it has.")]
    private async Task<IEnumerable<LoadedProject>> ConfigureAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The target framework, such as net48 or net10.0, for the projects that have it; the others keep their first.")] string? targetFramework = null,
        [Description("The configuration, such as Release.")] string? configuration = null)
    {
        // A framework no project has would load each with its first, as if none was asked for.
        var frameworks = Describe(await workspace.GetAsync(projectPath)).SelectMany(project => project.TargetFrameworks).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targetFramework is not null && !frameworks.Contains(targetFramework, StringComparer.OrdinalIgnoreCase))
            throw new McpException($"No project targets {targetFramework}; they target {string.Join(", ", frameworks)}.");

        return Describe(await workspace.ConfigureAsync(projectPath, new(targetFramework, configuration)));
    }

    internal static IEnumerable<LoadedProject> Describe(LoadedWorkspace loaded) =>
        loaded.Results.Select(result => new LoadedProject(
            result.ProjectFilePath,
            result.TargetFramework,
            (result.GetProperty("TargetFrameworks") is { Length: > 0 } frameworks ? frameworks : result.TargetFramework)
                .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            result.GetProperty("Configuration")));

    /// <param name="TargetFrameworks">The frameworks the project targets, for targetFramework.</param>
    public sealed record LoadedProject(string Project, string TargetFramework, IReadOnlyList<string> TargetFrameworks, string? Configuration);
}

public sealed class ProjectGraphTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => GetGraphAsync;

    [McpServerTool(Name = "project_graph", ReadOnly = true)]
    [Description("Lists the loaded projects in build order, each with its target framework, output type, the projects it references "
        + "and the projects that reference it.")]
    private async Task<IEnumerable<ProjectNode>> GetGraphAsync([Description(ToolDefaults.ProjectPath)] string projectPath) =>
        Graph(await workspace.GetAsync(projectPath));

    internal static IEnumerable<ProjectNode> Graph(LoadedWorkspace workspace)
    {
        var solution = workspace.Solution;
        var graph = solution.GetProjectDependencyGraph();
        var results = workspace.Results.ToDictionary(result => result.ProjectFilePath, StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> Names(IEnumerable<ProjectId> projects) => [.. projects.Select(id => solution.GetProject(id)!.Name).Order()];

        return graph.GetTopologicallySortedProjects().Select(solution.GetProject).OfType<Project>().Select(project =>
        {
            var result = results.GetValueOrDefault(project.FilePath ?? "");
            return new ProjectNode(
                project.Name,
                project.FilePath,
                result?.TargetFramework,
                result?.GetProperty("OutputType"),
                Names(graph.GetProjectsThatThisProjectDirectlyDependsOn(project.Id)),
                Names(graph.GetProjectsThatDirectlyDependOnThisProject(project.Id)));
        }).ToList();
    }

    public sealed record ProjectNode(string Name, string? Path, string? TargetFramework, string? OutputType, IReadOnlyList<string> References, IReadOnlyList<string> ReferencedBy);
}

[JsonConverter(typeof(JsonStringEnumConverter<PackageFilter>))]
public enum PackageFilter
{
    All,
    Outdated,
    Vulnerable,
    Deprecated,
}

public sealed class PackagesTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => ListPackagesAsync;

    [McpServerTool(Name = "packages", ReadOnly = true)]
    [Description("Lists the NuGet packages of the projects with dotnet list package: all of them, or only the outdated ones (with their latest "
        + "version), the vulnerable ones (with severities and advisories) or the deprecated ones; transitive packages too, if asked. "
        + "Outdated, Vulnerable and Deprecated ask the package sources. Paged.")]
    private async Task<PagedResult<Package>> ListPackagesAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("All, Outdated, Vulnerable or Deprecated.")] PackageFilter filter = PackageFilter.All,
        [Description("Include transitive packages.")] bool transitive = false,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        // The snapshot changes with the project files, so it keys the results as well.
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        return await cache.GetPageAsync(solution, $"packages|{filter}|{transitive}", page, pageSize,
            () => ListAsync(Path.GetFullPath(projectPath), filter, transitive, cancellationToken));
    }

    internal static async Task<IEnumerable<Package>> ListAsync(string path, PackageFilter filter, bool transitive, CancellationToken cancellationToken)
    {
        // dotnet list package reads what restore wrote.
        var (restored, restoreOutput) = await DotnetCli.RunAsync(["restore", path, "-v:q"], TimeSpan.FromMinutes(10), cancellationToken, workingDirectory: Path.GetDirectoryName(path));
        if (restored != 0)
            throw new McpException($"dotnet restore failed: {BuildTool.Tail(restoreOutput)}");

        List<string> arguments = ["list", path, "package", "--format", "json"];
        if (filter != PackageFilter.All)
            arguments.Add($"--{filter.ToString().ToLowerInvariant()}");
        if (transitive)
            arguments.Add("--include-transitive");

        var (_, output) = await DotnetCli.RunAsync(arguments, TimeSpan.FromMinutes(5), cancellationToken, workingDirectory: Path.GetDirectoryName(path));
        var (start, end) = (output.IndexOf('{'), output.LastIndexOf('}'));
        if (start < 0 || end < start)
            throw new McpException($"dotnet list package failed: {BuildTool.Tail(output)}");

        using var json = JsonDocument.Parse(output[start..(end + 1)]);
        if (json.RootElement.TryGetProperty("problems", out var problems) && problems.EnumerateArray().FirstOrDefault(problem => Text(problem, "level") == "error") is { ValueKind: JsonValueKind.Object } problem)
            throw new McpException($"dotnet list package failed: {Text(problem, "text")}");

        var packages = new List<Package>();
        foreach (var project in Array(json.RootElement, "projects"))
        {
            foreach (var framework in Array(project, "frameworks"))
            {
                foreach (var (list, isTransitive) in new[] { ("topLevelPackages", false), ("transitivePackages", true) })
                {
                    packages.AddRange(Array(framework, list).Select(package => new Package(
                        Path.GetFileName(Text(project, "path") ?? ""),
                        Text(framework, "framework"),
                        Text(package, "id")!,
                        Text(package, "requestedVersion"),
                        Text(package, "resolvedVersion"),
                        Text(package, "latestVersion"),
                        isTransitive,
                        [.. Array(package, "vulnerabilities").Select(vulnerability => $"{Text(vulnerability, "severity")}: {Text(vulnerability, "advisoryurl")}")],
                        [.. Array(package, "deprecationReasons").Select(reason => reason.GetString()!)])));
                }
            }
        }

        return packages;
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <param name="Latest">The latest version, when listing outdated packages.</param>
    /// <param name="Vulnerabilities">"Severity: advisory URL", when listing vulnerable packages.</param>
    public sealed record Package(
        string Project, string? Framework, string Id, string? Requested, string? Resolved, string? Latest, bool Transitive,
        IReadOnlyList<string> Vulnerabilities, IReadOnlyList<string> DeprecationReasons);
}
