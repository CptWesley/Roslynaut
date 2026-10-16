using System.ComponentModel;
using ModelContextProtocol.Server;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.ProjectModel;

namespace Roslynaut.Tools;

/// <param name="Direct">Whether a project references the package itself, rather than through other packages.</param>
/// <param name="License">
/// An SPDX license expression such as "MIT" (Kind Expression), the full path of the license file in the package (File), or a license URL (Url);
/// null if the package declares none, or is not in a packages folder.
/// </param>
public sealed record PackageLicense(string Id, string Version, bool Direct, string? License, string? Kind);

public sealed class PackageLicensesTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => ListLicensesAsync;

    [McpServerTool(Name = "package_licenses", ReadOnly = true)]
    [Description("Lists the license of every NuGet package the projects resolved, transitive ones included, from what restore wrote and the packages' "
        + "own metadata in the packages folders, without network access. Paged.")]
    private async Task<PagedResult<PackageLicense>> ListLicensesAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize)
    {
        var loaded = await workspace.GetAsync(projectPath);
        return await cache.GetPageAsync(loaded.Solution, "package_licenses", page, pageSize,
            () => Task.FromResult(Collect(NuGetService.AssetsOf(loaded, projectPath))));
    }

    /// <summary>The packages by id and version; each reads its license only when a page reaches it.</summary>
    internal static IEnumerable<PackageLicense> Collect(IReadOnlyList<(string Project, LockFile Assets)> projects)
    {
        // Direct dependencies read like "Newtonsoft.Json >= 13.0.3".
        var direct = projects.SelectMany(project => project.Assets.ProjectFileDependencyGroups)
            .SelectMany(group => group.Dependencies)
            .Select(dependency => dependency.Split(' ')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return projects
            .SelectMany(project => project.Assets.Libraries.Where(library => library.Type == LibraryType.Package)
                .Select(library => (Library: library, Folders: project.Assets.PackageFolders)))
            .DistinctBy(package => (package.Library.Name.ToLowerInvariant(), package.Library.Version))
            .OrderBy(package => package.Library.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Library.Version)
            .ToList()
            .Select(package =>
            {
                var directory = package.Folders.Select(folder => Path.Combine(folder.Path, package.Library.Path)).FirstOrDefault(Directory.Exists);
                var (license, kind) = directory is null ? default : LicenseOf(directory);
                return new PackageLicense(package.Library.Name, package.Library.Version.ToNormalizedString(), direct.Contains(package.Library.Name), license, kind);
            });
    }

    private static (string? License, string? Kind) LicenseOf(string directory)
    {
        if (Directory.EnumerateFiles(directory, "*.nuspec").FirstOrDefault() is not { } nuspec)
            return default;

        var reader = new NuspecReader(nuspec);
        return reader.GetLicenseMetadata() switch
        {
            { Type: LicenseType.Expression, License: var expression } => (expression, "Expression"),
            { Type: LicenseType.File, License: var file } => (Path.GetFullPath(Path.Combine(directory, file)), "File"),
            _ => reader.GetLicenseUrl() is { Length: > 0 } url ? (url, "Url") : default,
        };
    }
}
