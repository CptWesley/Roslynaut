using Buildalyzer;
using ModelContextProtocol;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.ProjectModel;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Roslynaut;

/// <summary>
/// Talks to NuGet package sources as restore does for a project: with the package sources, package source mapping and global packages folder
/// of the nuget.config files above it and the user's, read again on every call. Packages download into the global packages folder,
/// where restore finds them later.
/// </summary>
/// <remarks>
/// Private feeds get their credentials from nuget.config, or else from credential providers, such as Azure Artifacts' plugin, as restore does.
/// The server cannot sign anyone in, so providers run non-interactively, on what an interactive restore (dotnet restore --interactive) cached.
/// </remarks>
public sealed class NuGetService(WorkspaceService workspace)
{
    private const string SignInHint = " The feed needs a credential: add it to nuget.config, or install its credential provider (such as Azure Artifacts' "
        + "artifacts-credprovider) and sign in once with dotnet restore --interactive, which leaves a credential the provider reuses.";

    static NuGetService()
    {
        // NuGet starts plugins that are .NET assemblies with the dotnet this names; the server may run from its own executable instead.
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is null)
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", DotnetCli.Executable);
        DefaultCredentialServiceUtility.SetupDefaultCredentialService(NullLogger.Instance, nonInteractive: true);
    }

    public const string PackageId = "Package id, e.g. 'Newtonsoft.Json'.";
    public const string Version = "Package version, e.g. '13.0.3'; the latest stable one by default.";
    public const string TargetFramework = "Target framework to pick for, e.g. 'net8.0'; by default those of the project, or of the solution's projects.";

    private static readonly FrameworkReducer Reducer = new();

    /// <summary>One page of search results from each source, by source name.</summary>
    public Task<List<(string Source, IReadOnlyList<IPackageSearchMetadata> Results)>> SearchAsync(
        string projectPath, string query, bool prerelease, int skip, int take, CancellationToken cancellationToken) => AskAsync(async () =>
    {
        var results = new List<(string, IReadOnlyList<IPackageSearchMetadata>)>();
        foreach (var source in ConfigOf(projectPath).Sources)
        {
            if (await source.GetResourceAsync<PackageSearchResource>(cancellationToken) is { } search)
                results.Add((source.PackageSource.Name, [.. await search.SearchAsync(query, new SearchFilter(prerelease), skip, take, NullLogger.Instance, cancellationToken)]));
        }

        return results;
    });

    /// <summary>The metadata of every version of a package, unlisted ones too, from the sources its id maps to.</summary>
    public Task<List<IPackageSearchMetadata>> GetMetadataAsync(string projectPath, string id, CancellationToken cancellationToken) =>
        AskAsync<List<IPackageSearchMetadata>>(async () =>
        {
            using var cache = new SourceCacheContext();
            var versions = new List<IPackageSearchMetadata>();
            foreach (var source in ConfigOf(projectPath).SourcesFor(id))
            {
                if (await source.GetResourceAsync<PackageMetadataResource>(cancellationToken) is { } metadata)
                    versions.AddRange(await metadata.GetMetadataAsync(id, includePrerelease: true, includeUnlisted: true, cache, NullLogger.Instance, cancellationToken));
            }

            // A version on several sources is the same package.
            return versions.Count > 0 ? [.. versions.DistinctBy(version => version.Identity.Version)] : throw new McpException($"Package {id} is not on any package source.");
        });

    /// <summary>The version asked for, or else the latest stable one.</summary>
    public async Task<PackageIdentity> ResolveAsync(string projectPath, string id, string? version, CancellationToken cancellationToken)
    {
        if (version is not null)
            return NuGetVersion.TryParse(version, out var parsed) ? new(id, parsed) : throw new McpException($"'{version}' is not a NuGet version.");

        return Latest(await GetMetadataAsync(projectPath, id, cancellationToken), prerelease: false)?.Identity
            ?? throw new McpException($"Package {id} has no listed versions.");
    }

    /// <summary>The directory of a package in the global packages folder, downloading it there first from the sources its id maps to.</summary>
    public Task<string> DownloadAsync(string projectPath, PackageIdentity identity, CancellationToken cancellationToken) => AskAsync(async () =>
    {
        var config = ConfigOf(projectPath);
        var directory = new VersionFolderPathResolver(config.GlobalPackagesFolder).GetInstallPath(identity.Id, identity.Version);
        using (var installed = GlobalPackagesFolderUtility.GetPackage(identity, config.GlobalPackagesFolder))
        {
            if (installed is not null)
                return directory;
        }

        using var cache = new SourceCacheContext();
        foreach (var source in config.SourcesFor(identity.Id))
        {
            // Through a temporary file rather than memory: packages can be large.
            await using var nupkg = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            if (await source.GetResourceAsync<FindPackageByIdResource>(cancellationToken) is not { } finder
                || !await finder.CopyNupkgToStreamAsync(identity.Id, identity.Version, nupkg, cache, NullLogger.Instance, cancellationToken))
                continue;

            nupkg.Position = 0;
            var policy = ClientPolicyContext.GetClientPolicy(config.Settings, NullLogger.Instance);
            using var _ = await GlobalPackagesFolderUtility.AddPackageAsync(
                source.PackageSource.Source, identity, nupkg, config.GlobalPackagesFolder, Guid.Empty, policy, NullLogger.Instance, cancellationToken);
            return directory;
        }

        throw new McpException($"{identity} is not on any package source.");
    });

    /// <summary>The target framework asked for, or else those of the loaded project, or of the solution's projects.</summary>
    public async Task<IReadOnlyList<NuGetFramework>> TargetFrameworksAsync(string projectPath, string? targetFramework)
    {
        if (targetFramework is not null)
            return [NuGetFramework.Parse(targetFramework)];

        return [.. ProjectsOf(await workspace.GetAsync(projectPath), projectPath).Select(TargetFrameworkOf).OfType<NuGetFramework>().Distinct()];
    }

    public static NuGetFramework? TargetFrameworkOf(IAnalyzerResult result) =>
        (result.TargetFramework ?? result.GetProperty("TargetFrameworkMoniker")) is { Length: > 0 } framework ? NuGetFramework.Parse(framework) : null;

    /// <summary>
    /// What restore last wrote (project.assets.json) for the loaded project, or for the solution's projects, by project file.
    /// Loading the workspace restores, so it matches the loaded snapshot.
    /// </summary>
    public static IReadOnlyList<(string Project, LockFile Assets)> AssetsOf(LoadedWorkspace loaded, string projectPath)
    {
        List<(string, LockFile)> assets = [.. ProjectsOf(loaded, projectPath)
            .Select(result => (result.ProjectFilePath, Path: result.GetProperty("ProjectAssetsFile")))
            .Where(project => File.Exists(project.Path))
            .Select(project => (project.ProjectFilePath, LockFileUtilities.GetLockFile(project.Path, NullLogger.Instance)))];
        return assets.Count > 0 ? assets : throw new McpException("No loaded project has restore results (project.assets.json): none uses PackageReference.");
    }

    /// <summary>The settings of the nuget.config files above a project or solution, then the user's.</summary>
    public static ISettings LoadSettings(string projectPath)
    {
        var path = Path.GetFullPath(projectPath);
        if (!File.Exists(path))
            throw new McpException($"Project or solution not found: {path}");

        return Settings.LoadDefaultSettings(Path.GetDirectoryName(path));
    }

    /// <summary>The newest listed version, stable unless asked for; the newest prerelease if there is no stable one.</summary>
    public static IPackageSearchMetadata? Latest(IEnumerable<IPackageSearchMetadata> versions, bool prerelease)
    {
        var listed = versions.Where(version => version.IsListed).ToList();
        return listed.Where(version => prerelease || !version.Identity.Version.IsPrerelease).MaxBy(version => version.Identity.Version)
            ?? listed.MaxBy(version => version.Identity.Version);
    }

    /// <summary>The framework of a package nearest to a target framework, which restore would pick; null if none fits.</summary>
    public static NuGetFramework? Nearest(NuGetFramework target, IEnumerable<NuGetFramework> frameworks) => Reducer.GetNearest(target, frameworks);

    /// <summary>
    /// The files a target framework gets from a package, relative to it: those of the nearest framework, as restore picks it,
    /// in the first of the folders (such as lib, ref) that has a compatible one. Null if no folder has one.
    /// </summary>
    public static (NuGetFramework Framework, IReadOnlyList<string> Items)? NearestItems(PackageFolderReader package, NuGetFramework target, IEnumerable<string> folders)
    {
        foreach (var folder in folders)
        {
            var groups = package.GetItems(folder).ToList();
            if (Nearest(target, groups.Select(group => group.TargetFramework)) is { } nearest)
                return (nearest, [.. groups.Where(group => group.TargetFramework.Equals(nearest)).SelectMany(group => group.Items)]);
        }

        return null;
    }

    /// <summary>The full paths of the assemblies a target framework gets from a package, as <see cref="NearestItems"/> picks them.</summary>
    public static (NuGetFramework Framework, IReadOnlyList<string> Paths)? AssembliesFor(
        PackageFolderReader package, string directory, NuGetFramework target, IEnumerable<string> folders) =>
        NearestItems(package, target, folders) is var (nearest, items)
            // Placeholder files (_._) make a framework compatible without assemblies, as for packages the framework provides.
            ? (nearest, [.. items.Where(item => item.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(item => Path.GetFullPath(Path.Combine(directory, item)))])
            : null;

    /// <summary>The loaded project, or the solution's projects: a project's workspace holds the projects it references as well.</summary>
    private static IEnumerable<IAnalyzerResult> ProjectsOf(LoadedWorkspace loaded, string projectPath)
    {
        var path = Path.GetFullPath(projectPath);
        var own = loaded.Results.Where(result => result.ProjectFilePath.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
        return own.Count > 0 ? own : loaded.Results;
    }

    private static Config ConfigOf(string projectPath)
    {
        var settings = LoadSettings(projectPath);
        return new(
            settings,
            PackageSourceMapping.GetPackageSourceMapping(settings),
            [.. new PackageSourceProvider(settings).LoadPackageSources().Where(source => source.IsEnabled).Select(Repository.Factory.GetCoreV3)],
            SettingsUtility.GetGlobalPackagesFolder(settings));
    }

    /// <summary>Runs requests to package sources, with their failures as errors the client sees.</summary>
    private static async Task<T> AskAsync<T>(Func<Task<T>> request)
    {
        try
        {
            return await request();
        }
        catch (Exception e) when (e is FatalProtocolException or SignatureException)
        {
            var message = e.InnerException is { } inner ? $"{e.Message} {inner.Message}" : e.Message;
            throw new McpException(message.Contains("401") || message.Contains("403") ? message + SignInHint : message, e);
        }
    }

    private sealed record Config(ISettings Settings, PackageSourceMapping Mapping, IReadOnlyList<SourceRepository> Sources, string GlobalPackagesFolder)
    {
        public IEnumerable<SourceRepository> SourcesFor(string id) => Mapping.IsEnabled
            ? Sources.Where(source => Mapping.GetConfiguredPackageSources(id).Contains(source.PackageSource.Name, StringComparer.OrdinalIgnoreCase))
            : Sources;
    }
}
