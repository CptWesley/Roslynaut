using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NuGet.Frameworks;
using NuGet.Packaging;

namespace Roslynaut.Tools;

/// <param name="Directory">Where the package is, in the global packages folder.</param>
/// <param name="Readme">The package's readme file, if it has one.</param>
/// <param name="Frameworks">The target frameworks the package has assemblies for, in lib\ or ref\.</param>
/// <param name="Assemblies">Per target framework of the project, the package's assemblies for it.</param>
/// <param name="Symbols">Per assembly in Assemblies, what there is to debug it and read its source with.</param>
public sealed record PackageAssemblies(
    string Id, string Version, string Directory, string? Readme, IReadOnlyList<string> Frameworks, IReadOnlyList<FrameworkAssemblies> Assemblies,
    IReadOnlyList<AssemblySymbols> Symbols);

/// <param name="PackageFramework">The nearest framework the package has assemblies for; null if none fits.</param>
/// <param name="Paths">The assemblies, from lib\, which has the implementation; from ref\ if the package has no lib\ for the framework.</param>
public sealed record FrameworkAssemblies(string TargetFramework, string? PackageFramework, IReadOnlyList<string> Paths);

/// <param name="Pdb">
/// The assembly's PDB: next to it, or downloaded from a symbol server into a cache; "embedded" if the assembly contains it.
/// Null if there is none, so only decompile reads its code; Error says why, if a symbol server could not be reached.
/// </param>
/// <param name="Deterministic">Whether the build is deterministic, so the same source gives the same binary.</param>
/// <param name="SourceLink">URLs of the source files, per path or path prefix ending in *; null if the PDB has none, so only embedded files can be read.</param>
/// <param name="Documents">How many source files the PDB lists.</param>
/// <param name="Embedded">How many of those the PDB contains, such as the code of source generators.</param>
public sealed record AssemblySymbols(
    string Path, string? Pdb, bool Deterministic, IReadOnlyDictionary<string, string>? SourceLink, int Documents, int Embedded, string? Error = null);

public sealed class PackageAssemblyTool(NuGetService nuget, PdbService pdbs) : IMcpTool
{
    public Delegate Handler => GetPackageAssembliesAsync;

    [McpServerTool(Name = "package_assembly", ReadOnly = true)]
    [Description("Downloads a NuGet package into the global packages folder, where restore finds it later, and returns the paths of its assemblies "
        + "for each of the project's target frameworks, and of its readme. decompile, assembly_sources, assembly_il and assembly_asm take those paths, "
        + "so a package's API can be read before adding it. Its documentation comments, if any, sit next to each assembly in a .xml file. "
        + "Symbols tells per assembly whether it has a PDB (looking on the symbol servers too) and SourceLink, so its original source can be read.")]
    private async Task<PackageAssemblies> GetPackageAssembliesAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(NuGetService.PackageId)] string id,
        [Description(NuGetService.Version)] string? version = null,
        [Description(NuGetService.TargetFramework)] string? targetFramework = null,
        CancellationToken cancellationToken = default) =>
        await GetAsync(nuget, pdbs, projectPath, id, version, await nuget.TargetFrameworksAsync(projectPath, targetFramework), cancellationToken);

    internal static async Task<PackageAssemblies> GetAsync(
        NuGetService nuget, PdbService pdbs, string projectPath, string id, string? version, IReadOnlyList<NuGetFramework> frameworks,
        CancellationToken cancellationToken)
    {
        var identity = await nuget.ResolveAsync(projectPath, id, version, cancellationToken);
        var directory = await nuget.DownloadAsync(projectPath, identity, cancellationToken);
        using var package = new PackageFolderReader(directory);

        var readme = package.NuspecReader.GetReadme() is { Length: > 0 } file ? Path.GetFullPath(Path.Combine(directory, file)) : null;
        List<FrameworkAssemblies> assemblies = [.. frameworks.Select(framework => NuGetService.AssembliesFor(package, directory, framework, ["lib", "ref"]) is var (nearest, paths)
            ? new FrameworkAssemblies(framework.GetShortFolderName(), nearest.GetShortFolderName(), paths)
            : new FrameworkAssemblies(framework.GetShortFolderName(), null, []))];
        return new(
            package.NuspecReader.GetId(),
            identity.Version.ToNormalizedString(),
            directory,
            File.Exists(readme) ? readme : null,
            [.. new[] { "lib", "ref" }.SelectMany(package.GetItems).Select(group => group.TargetFramework.GetShortFolderName()).Distinct()],
            assemblies,
            await Task.WhenAll(assemblies.SelectMany(framework => framework.Paths).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => SymbolsOfAsync(pdbs, path, cancellationToken))));
    }

    private static async Task<AssemblySymbols> SymbolsOfAsync(PdbService pdbs, string path, CancellationToken cancellationToken)
    {
        var deterministic = PdbService.IsDeterministic(path);
        try
        {
            if (await pdbs.FindPdbAsync(path, download: true, cancellationToken) is not { } pdb)
                return new(path, null, deterministic, null, 0, 0);

            var documents = pdb.Documents.ToList();
            return new(path, pdb.Location, deterministic, pdb.SourceLink.Count > 0 ? pdb.SourceLink : null, documents.Count, documents.Count(pdb.Embeds));
        }
        catch (McpException e)
        {
            return new(path, null, deterministic, null, 0, 0, e.Message);
        }
    }
}
