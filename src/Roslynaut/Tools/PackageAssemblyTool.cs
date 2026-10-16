using System.ComponentModel;
using ModelContextProtocol.Server;
using NuGet.Frameworks;
using NuGet.Packaging;

namespace Roslynaut.Tools;

/// <param name="Directory">Where the package is, in the global packages folder.</param>
/// <param name="Readme">The package's readme file, if it has one.</param>
/// <param name="Frameworks">The target frameworks the package has assemblies for, in lib\ or ref\.</param>
/// <param name="Assemblies">Per target framework of the project, the package's assemblies for it.</param>
public sealed record PackageAssemblies(
    string Id, string Version, string Directory, string? Readme, IReadOnlyList<string> Frameworks, IReadOnlyList<FrameworkAssemblies> Assemblies);

/// <param name="PackageFramework">The nearest framework the package has assemblies for; null if none fits.</param>
/// <param name="Paths">The assemblies, from lib\, which has the implementation; from ref\ if the package has no lib\ for the framework.</param>
public sealed record FrameworkAssemblies(string TargetFramework, string? PackageFramework, IReadOnlyList<string> Paths);

public sealed class PackageAssemblyTool(NuGetService nuget) : IMcpTool
{
    public Delegate Handler => GetPackageAssembliesAsync;

    [McpServerTool(Name = "package_assembly", ReadOnly = true)]
    [Description("Downloads a NuGet package into the global packages folder, where restore finds it later, and returns the paths of its assemblies "
        + "for each of the project's target frameworks, and of its readme. decompile, assembly_il and assembly_asm take those paths, "
        + "so a package's API can be read before adding it. Its documentation comments, if any, sit next to each assembly in a .xml file.")]
    private async Task<PackageAssemblies> GetPackageAssembliesAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(NuGetService.PackageId)] string id,
        [Description(NuGetService.Version)] string? version = null,
        [Description(NuGetService.TargetFramework)] string? targetFramework = null,
        CancellationToken cancellationToken = default) =>
        await GetAsync(nuget, projectPath, id, version, await nuget.TargetFrameworksAsync(projectPath, targetFramework), cancellationToken);

    internal static async Task<PackageAssemblies> GetAsync(
        NuGetService nuget, string projectPath, string id, string? version, IReadOnlyList<NuGetFramework> frameworks, CancellationToken cancellationToken)
    {
        var identity = await nuget.ResolveAsync(projectPath, id, version, cancellationToken);
        var directory = await nuget.DownloadAsync(projectPath, identity, cancellationToken);
        using var package = new PackageFolderReader(directory);

        var readme = package.NuspecReader.GetReadme() is { Length: > 0 } file ? Path.GetFullPath(Path.Combine(directory, file)) : null;
        return new(
            package.NuspecReader.GetId(),
            identity.Version.ToNormalizedString(),
            directory,
            File.Exists(readme) ? readme : null,
            [.. new[] { "lib", "ref" }.SelectMany(package.GetItems).Select(group => group.TargetFramework.GetShortFolderName()).Distinct()],
            [.. frameworks.Select(framework => NuGetService.AssembliesFor(package, directory, framework, ["lib", "ref"]) is var (nearest, paths)
                ? new FrameworkAssemblies(framework.GetShortFolderName(), nearest.GetShortFolderName(), paths)
                : new FrameworkAssemblies(framework.GetShortFolderName(), null, []))]);
    }
}
