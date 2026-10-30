using System.ComponentModel;
using ModelContextProtocol.Server;
using NuGet.Configuration;

namespace Roslynaut.Tools;

/// <param name="HasCredentials">Whether the configuration holds credentials for the source; never the credentials themselves.</param>
public sealed record NuGetSource(string Name, string Source, bool Enabled, bool HasCredentials);

/// <param name="ConfigFiles">The nuget.config files that apply.</param>
/// <param name="Sources">The package sources, disabled ones too.</param>
/// <param name="SourceMapping">Per source, the package id patterns it provides; empty when source mapping is off, so any source may provide any package.</param>
/// <param name="FallbackFolders">Folders restore takes packages from before it downloads them.</param>
public sealed record NuGetConfig(
    IReadOnlyList<string> ConfigFiles, IReadOnlyList<NuGetSource> Sources, IReadOnlyDictionary<string, IReadOnlyList<string>> SourceMapping,
    string GlobalPackagesFolder, IReadOnlyList<string> FallbackFolders);

public sealed class NuGetConfigTool : IMcpTool
{
    public Delegate Handler => DescribeConfig;

    [McpServerTool(Name = "nuget_config", ReadOnly = true)]
    [Description("Returns the NuGet configuration restore uses for the project: the nuget.config files that apply, the package sources and whether "
        + "they have credentials (never the credentials), package source mapping, the global packages folder and the fallback folders. "
        + "Helps with restore errors such as NU1101 (package not found) or NU1301 (source unreachable).")]
    private static NuGetConfig DescribeConfig([Description(ToolDefaults.ProjectPath)] string projectPath) => Describe(NuGetService.LoadSettings(projectPath));

    internal static NuGetConfig Describe(ISettings settings) => new(
        [.. settings.GetConfigFilePaths()],
        [.. new PackageSourceProvider(settings).LoadPackageSources().Select(source =>
            new NuGetSource(source.Name, source.Source, source.IsEnabled, source.Credentials is not null))],
        new PackageSourceMappingProvider(settings).GetPackageSourceMappingItems().ToDictionary(
            item => item.Key, item => (IReadOnlyList<string>)[.. item.Patterns.Select(pattern => pattern.Pattern)]),
        SettingsUtility.GetGlobalPackagesFolder(settings),
        SettingsUtility.GetFallbackPackageFolders(settings));
}
