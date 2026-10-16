using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace Roslynaut.Tools;

/// <param name="Alternative">The package to use instead, with its version range.</param>
public sealed record PackageDeprecation(IReadOnlyList<string> Reasons, string? Message, string? Alternative);

/// <param name="Listed">False for a version its owner unlisted.</param>
/// <param name="Vulnerabilities">"Severity: advisory URL" for each known vulnerability.</param>
/// <param name="Frameworks">
/// The target frameworks of the version's dependency groups. SDK-style packages have one per framework they support;
/// for packages without, package_assembly tells from their content.
/// </param>
/// <param name="Nearest">Per target framework of the project, the nearest of Frameworks, which restore would pick; null if none fits.</param>
/// <param name="Dependencies">Per framework in Nearest, the packages the version depends on there, as "Id (>= 1.0.0)".</param>
/// <param name="Versions">The listed versions, newest first; prereleases only if asked for.</param>
public sealed record PackageInfo(
    string Id, string Version, bool Listed, string? Description, string? Authors, string? License, string? ProjectUrl, DateTimeOffset? Published,
    PackageDeprecation? Deprecation, IReadOnlyList<string> Vulnerabilities, IReadOnlyList<string> Frameworks,
    IReadOnlyDictionary<string, string?> Nearest, IReadOnlyDictionary<string, IReadOnlyList<string>> Dependencies, IReadOnlyList<string> Versions);

public sealed class PackageInfoTool(NuGetService nuget) : IMcpTool
{
    public Delegate Handler => DescribePackageAsync;

    [McpServerTool(Name = "package_info", ReadOnly = true)]
    [Description("Describes a NuGet package from the project's package sources without downloading it: its versions, newest first, and for one version "
        + "its description, license, deprecation, known vulnerabilities, the target frameworks it has dependencies for, the nearest of those "
        + "for each of the project's target frameworks, and its dependencies there.")]
    private async Task<PackageInfo> DescribePackageAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(NuGetService.PackageId)] string id,
        [Description(NuGetService.Version)] string? version = null,
        [Description("Include prerelease versions, also in picking the latest.")] bool prerelease = false,
        [Description(NuGetService.TargetFramework)] string? targetFramework = null,
        CancellationToken cancellationToken = default) =>
        await DescribeAsync(nuget, projectPath, id, version, prerelease, await nuget.TargetFrameworksAsync(projectPath, targetFramework), cancellationToken);

    internal static async Task<PackageInfo> DescribeAsync(
        NuGetService nuget, string projectPath, string id, string? version, bool prerelease, IReadOnlyList<NuGetFramework> frameworks,
        CancellationToken cancellationToken)
    {
        var versions = await nuget.GetMetadataAsync(projectPath, id, cancellationToken);
        var package = version is null
            ? NuGetService.Latest(versions, prerelease) ?? throw new McpException($"Package {id} has no listed versions.")
            : versions.FirstOrDefault(candidate => NuGetVersion.TryParse(version, out var parsed) && candidate.Identity.Version == parsed)
                ?? throw new McpException($"Package {id} has no version {version}.");

        var groups = package.DependencySets.ToList();
        var nearest = frameworks.ToDictionary(
            framework => framework.GetShortFolderName(),
            framework => NuGetService.Nearest(framework, groups.Select(group => group.TargetFramework)));
        var deprecation = await package.GetDeprecationMetadataAsync();

        return new(
            package.Identity.Id,
            package.Identity.Version.ToNormalizedString(),
            package.IsListed,
            package.Description,
            package.Authors,
            package.LicenseMetadata?.License ?? package.LicenseUrl?.ToString(),
            package.ProjectUrl?.ToString(),
            package.Published,
            deprecation is null ? null : new(
                [.. deprecation.Reasons ?? []],
                deprecation.Message,
                deprecation.AlternatePackage is { } alternative ? $"{alternative.PackageId} {alternative.Range?.PrettyPrint()}".Trim() : null),
            [.. (package.Vulnerabilities ?? []).Select(vulnerability => $"{SeverityOf(vulnerability.Severity)}: {vulnerability.AdvisoryUrl}")],
            [.. groups.Select(group => group.TargetFramework.GetShortFolderName())],
            nearest.ToDictionary(entry => entry.Key, entry => entry.Value?.GetShortFolderName()),
            nearest.Values.OfType<NuGetFramework>().Distinct().ToDictionary(
                framework => framework.GetShortFolderName(),
                framework => (IReadOnlyList<string>)[.. groups.First(group => group.TargetFramework.Equals(framework)).Packages
                    .Select(dependency => $"{dependency.Id} {dependency.VersionRange.PrettyPrint()}")]),
            [.. versions
                .Where(candidate => candidate.IsListed && (prerelease || !candidate.Identity.Version.IsPrerelease))
                .Select(candidate => candidate.Identity.Version)
                .OrderDescending()
                .Select(candidate => candidate.ToNormalizedString())]);
    }

    // The severities of NuGet's vulnerability data.
    private static string SeverityOf(int severity) => severity switch
    {
        0 => "Low",
        1 => "Moderate",
        2 => "High",
        3 => "Critical",
        _ => "Unknown",
    };
}
