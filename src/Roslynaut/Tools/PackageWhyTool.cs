using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.ProjectModel;

namespace Roslynaut.Tools;

/// <param name="Chain">From a direct dependency of the project down to the package, as "Id Version" or "Name (project)" entries joined by " > ".</param>
public sealed record DependencyPath(string Project, string Framework, string Chain);

/// <param name="Code">A NuGet code, such as NU1605 (downgrade), NU1107 (version conflict) or NU1903 (vulnerability).</param>
/// <param name="Frameworks">The target frameworks it applies to.</param>
public sealed record RestoreMessage(string Project, string Code, string Level, string Message, IReadOnlyList<string> Frameworks);

/// <param name="Messages">Restore's warnings and errors about the package.</param>
/// <param name="Paths">Each way the projects depend on the package, per target framework.</param>
/// <param name="Truncated">Whether there are more paths than maxPaths.</param>
public sealed record PackageWhy(IReadOnlyList<RestoreMessage> Messages, IReadOnlyList<DependencyPath> Paths, bool Truncated);

public sealed class PackageWhyTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => ExplainPackageAsync;

    [McpServerTool(Name = "package_why", ReadOnly = true)]
    [Description("Explains why a NuGet package is in the project's dependency graph, from what restore resolved: each chain of dependencies "
        + "from a direct dependency of the project down to the package, per target framework, with resolved versions; and restore's warnings "
        + "and errors about it, such as downgrades (NU1605), version conflicts (NU1107) or vulnerabilities (NU1901-NU1904). Empty if nothing depends on it.")]
    private async Task<PackageWhy> ExplainPackageAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(NuGetService.PackageId)] string id,
        [Description("Most dependency paths to return.")] int maxPaths = 50) =>
        Explain(NuGetService.AssetsOf(await workspace.GetAsync(projectPath), projectPath), id, maxPaths);

    internal static PackageWhy Explain(IReadOnlyList<(string Project, LockFile Assets)> projects, string id, int maxPaths)
    {
        if (maxPaths < 1)
            throw new McpException("maxPaths must be at least 1.");

        var messages = projects.SelectMany(project => project.Assets.LogMessages
            .Where(message => id.Equals(message.LibraryId, StringComparison.OrdinalIgnoreCase))
            .Select(message => new RestoreMessage(
                Path.GetFileName(project.Project), message.Code.ToString(), message.Level.ToString(), message.Message, [.. message.TargetGraphs])));

        // One more than asked for tells whether there are more.
        var paths = projects.SelectMany(project => PathsOf(project.Project, project.Assets, id)).Take(maxPaths + 1).ToList();
        return new([.. messages], [.. paths.Take(maxPaths)], paths.Count > maxPaths);
    }

    /// <summary>The dependency chains from the project to a package, per target framework, depth first.</summary>
    private static IEnumerable<DependencyPath> PathsOf(string project, LockFile assets, string id)
    {
        // Runtime-specific targets repeat the graph of their framework.
        foreach (var target in assets.Targets.Where(target => string.IsNullOrEmpty(target.RuntimeIdentifier)))
        {
            var libraries = target.Libraries.DistinctBy(library => library.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(library => library.Name!, StringComparer.OrdinalIgnoreCase);
            if (!libraries.ContainsKey(id))
                continue;

            // Only libraries that lead to the package, so the search walks no dead ends.
            var leads = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
            for (var grown = true; grown;)
            {
                grown = false;
                foreach (var library in target.Libraries)
                    grown |= library.Dependencies.Any(dependency => leads.Contains(dependency.Id)) && leads.Add(library.Name!);
            }

            // Direct dependencies read like "Newtonsoft.Json >= 13.0.3"; project references are among them.
            var direct = assets.ProjectFileDependencyGroups
                .Where(group => !string.IsNullOrEmpty(group.FrameworkName) && NuGetFramework.Parse(group.FrameworkName).Equals(target.TargetFramework))
                .SelectMany(group => group.Dependencies)
                .Select(dependency => dependency.Split(' ')[0])
                .Where(leads.Contains);
            foreach (var chain in direct.SelectMany(name => Walk(libraries, leads, id, name, [])))
                yield return new(Path.GetFileName(project), target.TargetFramework.GetShortFolderName(), string.Join(" > ", chain));
        }
    }

    private static IEnumerable<List<string>> Walk(
        Dictionary<string, LockFileTargetLibrary> libraries, HashSet<string> leads, string id, string name, List<string> chain)
    {
        var library = libraries[name];
        var entry = library.Type == LibraryType.Project ? $"{library.Name} (project)" : $"{library.Name} {library.Version}";

        // Restore rejects cycles (NU1108), but a damaged assets file should not recurse forever.
        if (chain.Contains(entry))
            yield break;

        chain.Add(entry);
        if (name.Equals(id, StringComparison.OrdinalIgnoreCase))
        {
            yield return [.. chain];
        }
        else
        {
            foreach (var dependency in library.Dependencies.Where(dependency => leads.Contains(dependency.Id)))
            {
                foreach (var found in Walk(libraries, leads, id, dependency.Id, chain))
                    yield return found;
            }
        }

        chain.RemoveAt(chain.Count - 1);
    }
}
