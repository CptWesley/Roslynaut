using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

/// <param name="Verified">Whether the package's id prefix is reserved for its owner.</param>
public sealed record PackageSearchResult(string Id, string Version, string? Description, string? Authors, long? Downloads, bool Verified, string Source);

public sealed class PackageSearchTool(NuGetService nuget) : IMcpTool
{
    public Delegate Handler => SearchPackagesAsync;

    [McpServerTool(Name = "package_search", ReadOnly = true)]
    [Description("Searches the project's NuGet package sources, as its nuget.config sets them, for packages by keywords or id. "
        + "Returns each package's latest version, description, authors, total downloads and whether its id prefix is verified. Paged.")]
    private Task<PagedResult<PackageSearchResult>> SearchPackagesAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Search terms, e.g. 'json serializer', or a package id.")] string query,
        [Description("Include prerelease versions.")] bool prerelease = false,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        SearchAsync(nuget, projectPath, query, prerelease, page, pageSize, cancellationToken);

    internal static async Task<PagedResult<PackageSearchResult>> SearchAsync(
        NuGetService nuget, string projectPath, string query, bool prerelease, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 0 || pageSize < 1)
            throw new McpException("page must be at least 0 and pageSize at least 1.");

        // ponytail: each source pages on its own and a page holds them one after another, so with several sources it can exceed pageSize;
        // rank across sources if that matters.
        var sources = await nuget.SearchAsync(projectPath, query, prerelease, page * pageSize, pageSize + 1, cancellationToken);
        var results = sources
            .SelectMany(source => source.Results.Take(pageSize).Select(package => (Package: package, source.Source)))
            // A local feed can list several versions of a package, in the order of its files, which differs between file systems.
            .GroupBy(result => result.Package.Identity.Id, StringComparer.OrdinalIgnoreCase)
            .Select(versions => versions.MaxBy(result => result.Package.Identity.Version))
            .Select(result => new PackageSearchResult(
                result.Package.Identity.Id, result.Package.Identity.Version.ToNormalizedString(), result.Package.Description, result.Package.Authors,
                result.Package.DownloadCount, result.Package.PrefixReserved, result.Source))
            .ToList();
        return new(results, page, pageSize, sources.Any(source => source.Results.Count > pageSize), null);
    }
}
