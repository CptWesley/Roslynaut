using System.ComponentModel;
using ModelContextProtocol.Server;
using NuGet.Frameworks;
using NuGet.Packaging;

namespace Roslynaut.Tools;

/// <param name="Directory">Where the package is, in the global packages folder. The other paths are relative to it.</param>
/// <param name="Files">The files of the package, or of the folder asked for.</param>
/// <param name="MsBuild">
/// Per target framework of the project, the .props and .targets files restore imports into the build of a project that references the package itself.
/// </param>
/// <param name="Analyzers">The analyzer and source generator assemblies a C# project gets.</param>
public sealed record PackageContents(
    string Id, string Version, string Directory, IReadOnlyList<string> Files, IReadOnlyDictionary<string, IReadOnlyList<string>> MsBuild,
    IReadOnlyList<string> Analyzers);

public sealed class PackageContentsTool(NuGetService nuget) : IMcpTool
{
    public Delegate Handler => GetPackageContentsAsync;

    [McpServerTool(Name = "package_contents", ReadOnly = true)]
    [Description("Downloads a NuGet package into the global packages folder and lists its files, with what reaches the build of a project that "
        + "references it: the MSBuild .props and .targets files restore imports, per target framework of the project, and its analyzers and "
        + "source generators. The files can be read from the package's directory.")]
    private async Task<PackageContents> GetPackageContentsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(NuGetService.PackageId)] string id,
        [Description(NuGetService.Version)] string? version = null,
        [Description(NuGetService.TargetFramework)] string? targetFramework = null,
        [Description("Only list files in this folder of the package, e.g. 'build' or 'lib/net8.0'; packages can hold hundreds.")] string? folder = null,
        CancellationToken cancellationToken = default) =>
        await GetAsync(nuget, projectPath, id, version, await nuget.TargetFrameworksAsync(projectPath, targetFramework), folder, cancellationToken);

    internal static async Task<PackageContents> GetAsync(
        NuGetService nuget, string projectPath, string id, string? version, IReadOnlyList<NuGetFramework> frameworks, string? folder,
        CancellationToken cancellationToken)
    {
        var identity = await nuget.ResolveAsync(projectPath, id, version, cancellationToken);
        var directory = await nuget.DownloadAsync(projectPath, identity, cancellationToken);
        using var package = new PackageFolderReader(directory);

        // Leave out what NuGet adds around the content: the .nupkg, its hash, signature and metadata.
        var files = package.GetFiles()
            .Where(file => !file.StartsWith('.') && !file.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".sha512", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Package paths use forward slashes.
        var prefix = folder?.Replace('\\', '/').TrimEnd('/') + "/";
        return new(
            package.NuspecReader.GetId(),
            identity.Version.ToNormalizedString(),
            directory,
            [.. folder is null ? files : files.Where(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))],
            frameworks.ToDictionary(framework => framework.GetShortFolderName(), framework => MsBuildFiles(package, framework)),
            [.. files.Where(IsAnalyzer)]);
    }

    /// <summary>
    /// As restore picks them: {id}.props and {id}.targets of the nearest framework in buildTransitive\, or else in build\.
    /// Projects that get the package through other packages only import those from buildTransitive\.
    /// </summary>
    private static IReadOnlyList<string> MsBuildFiles(PackageFolderReader package, NuGetFramework framework)
    {
        var id = package.NuspecReader.GetId();
        return NuGetService.NearestItems(package, framework, ["buildTransitive", "build"]) is var (_, items)
            ? [.. items.Where(item => Path.GetFileNameWithoutExtension(item).Equals(id, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(item).ToLowerInvariant() is ".props" or ".targets")]
            : [];
    }

    // ponytail: all Roslyn-version folders (analyzers\dotnet\roslyn4.8\cs) are listed, not only the one the compiler picks; narrow them if that misleads.
    private static bool IsAnalyzer(string file) =>
        file.Split('/', '\\') is ["analyzers", .. var folders, var name]
        && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        && !name.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)
        && !folders.Any(folder => folder is "vb" or "fs");
}
