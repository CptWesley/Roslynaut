using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NuGet.Packaging;
using NuGet.Versioning;
using static Roslynaut.Tools.DiagnosticsTool;

namespace Roslynaut.Tools;

public sealed class PackageUpgradeCheckTool(WorkspaceService workspace, NuGetService nuget, ResultCache cache) : IMcpTool
{
    public Delegate Handler => CheckUpgradeAsync;

    [McpServerTool(Name = "package_upgrade_check", ReadOnly = true)]
    [Description("Checks what moving NuGet packages to other versions would break, without changing anything: compiles the projects that reference them "
        + "against the other versions' assemblies and returns the compiler errors and warnings that would be new, such as uses of removed or obsolete APIs. "
        + "Dependencies are not resolved: a version that needs a newer version of another package shows that as an error such as CS1705, "
        + "so include that package too. Paged.")]
    private async Task<PagedResult<DiagnosticEntry>> CheckUpgradeAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Packages as 'Id@Version', or just 'Id' for the latest stable version, e.g. ['Newtonsoft.Json@13.0.3'].")] string[] packages,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var loaded = await workspace.GetAsync(projectPath);
        return await cache.GetPageAsync(loaded.Solution, $"package_upgrade_check|{string.Join(",", packages)}", page, pageSize,
            () => CheckAsync(loaded, nuget, projectPath, packages, cancellationToken));
    }

    internal static async Task<IEnumerable<DiagnosticEntry>> CheckAsync(
        LoadedWorkspace loaded, NuGetService nuget, string projectPath, IReadOnlyList<string> packages, CancellationToken cancellationToken)
    {
        var frameworks = loaded.Results.ToDictionary(result => result.ProjectFilePath, NuGetService.TargetFrameworkOf, StringComparer.OrdinalIgnoreCase);
        var (before, after) = (loaded.Solution, loaded.Solution);
        foreach (var package in packages)
        {
            var (id, version) = package.Split('@', 2) is [var name, var number] ? (name, number) : (package, null);
            var identity = await nuget.ResolveAsync(projectPath, id, version, cancellationToken);
            var directory = await nuget.DownloadAsync(projectPath, identity, cancellationToken);
            using var reader = new PackageFolderReader(directory);

            var referenced = false;
            foreach (var project in after.Projects)
            {
                var old = project.MetadataReferences.Where(reference => IsFrom(reference, id)).ToList();
                if (old.Count == 0)
                    continue;

                referenced = true;
                var framework = frameworks.GetValueOrDefault(project.FilePath ?? "") ?? throw new McpException($"The target framework of {project.Name} is unknown.");
                var (_, paths) = NuGetService.AssembliesFor(reader, directory, framework, ["ref", "lib"])
                    ?? throw new McpException($"{identity} has no assemblies for {framework.GetShortFolderName()}, the target framework of {project.Name}.");
                after = after.WithProjectMetadataReferences(project.Id, project.MetadataReferences.Except(old)
                    .Concat(paths.Select(path => WorkspaceService.WithDocumentation(MetadataReference.CreateFromFile(path)))));
            }

            if (!referenced)
                throw new McpException($"No loaded project references {id}.");
        }

        // Projects that reference changed ones compile against the package's types too.
        var changed = after.GetChanges(before).GetProjectChanges().Select(change => change.ProjectId).ToList();
        var graph = after.GetProjectDependencyGraph();
        var existing = new HashSet<DiagnosticEntry>();
        var diagnostics = new List<Diagnostic>();
        foreach (var project in changed.Concat(changed.SelectMany(graph.GetProjectsThatTransitivelyDependOnThisProject)).Distinct())
        {
            existing.UnionWith((await GetAsync(before.GetProject(project)!, null, analyzers: false, cancellationToken)).Select(EntryOf));
            diagnostics.AddRange(await GetAsync(after.GetProject(project)!, null, analyzers: false, cancellationToken));
        }

        return [.. diagnostics
            .Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)
            .OrderByDescending(diagnostic => diagnostic.Severity)
            .Select(EntryOf)
            .Distinct()
            .Where(entry => !existing.Contains(entry))];
    }

    /// <summary>Whether a reference is an assembly of a package, by its path in a packages folder: ...\{id}\{version}\lib or ref\...</summary>
    private static bool IsFrom(MetadataReference reference, string id)
    {
        if (reference is not PortableExecutableReference { FilePath: { } path })
            return false;

        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Enumerable.Range(0, Math.Max(segments.Length - 2, 0)).Any(index =>
            segments[index].Equals(id, StringComparison.OrdinalIgnoreCase)
            && NuGetVersion.TryParse(segments[index + 1], out _)
            && segments[index + 2].ToLowerInvariant() is "lib" or "ref");
    }
}
