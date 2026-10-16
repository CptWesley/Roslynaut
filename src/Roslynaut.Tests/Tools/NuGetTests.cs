using NuGet.Frameworks;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>The NuGet package tools against a local feed with two versions of a package, and a project that uses the first.</summary>
[NonParallelizable]
public class NuGetTests
{
    private const string Id = "Roslynaut.Sample.Lib";
    private static readonly NuGetFramework Net10 = NuGetFramework.Parse("net10.0");

    private DirectoryInfo directory = null!;
    private string app = null!;
    private WorkspaceService workspace = null!;
    private NuGetService nuget = null!;

    [OneTimeSetUp]
    public async Task CreateFeed()
    {
        directory = Directory.CreateTempSubdirectory("roslynaut-nuget-");
        var feed = Directory.CreateDirectory(Path.Combine(directory.FullName, "feed")).FullName;

        // The sample package comes from the local feed only, and everything lands in a global packages folder of its own.
        Write("nuget.config", """
            <configuration>
              <config><add key="globalPackagesFolder" value="packages" /></config>
              <packageSources>
                <clear />
                <add key="local" value="feed" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="local"><package pattern="Roslynaut.*" /></packageSource>
                <packageSource key="nuget.org"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var lib = Write($"lib/{Id}.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
                <NoWarn>CS1591</NoWarn>
                <Description>Greets, for Roslynaut's tests.</Description>
                <PackageReadmeFile>README.md</PackageReadmeFile>
              </PropertyGroup>
              <ItemGroup><None Include="README.md" Pack="true" PackagePath="" /></ItemGroup>
            </Project>
            """);
        Write("lib/README.md", "# Greeter");

        // Version 2 removes Old and makes Hello obsolete.
        foreach (var (version, members) in new[]
        {
            ("1.0.0", "public static string Hello() => \"Hello\"; public static string Old() => \"Old\";"),
            ("2.0.0", "[System.Obsolete(\"Use Hi.\")] public static string Hello() => \"Hello\"; public static string Hi() => \"Hi\";"),
        })
        {
            Write("lib/Greeter.cs", $"namespace Sample;\n/// <summary>Greets.</summary>\npublic static class Greeter {{ {members} }}\n");
            var (exitCode, output) = await DotnetCli.RunAsync(["pack", lib, "-o", feed, $"-p:Version={version}"], TimeSpan.FromMinutes(5), CancellationToken.None);
            exitCode.Should().Be(0, output);
        }

        app = Write("app/App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="{Id}" Version="1.0.0" /></ItemGroup>
            </Project>
            """);
        Write("app/Uses.cs", "public static class Uses { public static string Both() => Sample.Greeter.Hello() + Sample.Greeter.Old(); }\n");
        workspace = new WorkspaceService();
        nuget = new NuGetService(workspace);
    }

    [OneTimeTearDown]
    public void DeleteFeed() => directory.Delete(recursive: true);

    private string Write(string relativePath, string text)
    {
        var path = Path.Combine(directory.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Test]
    public async Task Search_FindsThePackage_OnItsSource()
    {
        var results = await PackageSearchTool.SearchAsync(nuget, app, "Roslynaut.Sample", prerelease: false, page: 0, pageSize: 20, CancellationToken.None);

        results.Items.Should().Contain(new PackageSearchResult(Id, "2.0.0", "Greets, for Roslynaut's tests.", Id, null, false, "local"));
    }

    [Test]
    public async Task Info_DescribesTheLatestVersion_ForTheTargetFramework()
    {
        var info = await PackageInfoTool.DescribeAsync(nuget, app, Id, version: null, prerelease: false, [Net10], CancellationToken.None);

        (info.Version, info.Description, info.Deprecation).Should().Be(("2.0.0", "Greets, for Roslynaut's tests.", null));
        info.Versions.Should().Equal("2.0.0", "1.0.0");
        info.Frameworks.Should().Equal("net10.0");
        info.Nearest.Should().Equal(new Dictionary<string, string?> { ["net10.0"] = "net10.0" });
        info.Dependencies.Should().ContainKey("net10.0").WhoseValue.Should().BeEmpty();
    }

    [Test]
    public async Task Assembly_DownloadsThePackage_AndPicksAssembliesPerFramework()
    {
        var package = await PackageAssemblyTool.GetAsync(nuget, app, Id, "1.0.0", [Net10, NuGetFramework.Parse("net48")], CancellationToken.None);

        package.Readme.Should().Match(path => path.EndsWith("README.md") && File.Exists(path));
        package.Frameworks.Should().Equal("net10.0");
        package.Assemblies.Should().HaveCount(2);
        package.Assemblies[0].Should().Match<FrameworkAssemblies>(assemblies => assemblies.TargetFramework == "net10.0" && assemblies.PackageFramework == "net10.0");
        package.Assemblies[0].Paths.Should().ContainSingle().Which.Should().Match(path => File.Exists(path) && File.Exists(Path.ChangeExtension(path, ".xml")));
        package.Assemblies[1].Should().Be(new FrameworkAssemblies("net48", null, []));
    }

    [Test]
    public async Task UpgradeCheck_ReturnsOnlyTheNewErrorsAndWarnings()
    {
        var loaded = await workspace.GetAsync(app);

        (await nuget.TargetFrameworksAsync(app, null)).Should().Equal(Net10);
        (await PackageUpgradeCheckTool.CheckAsync(loaded, nuget, app, [Id], CancellationToken.None)).Select(diagnostic => diagnostic.Id).Should().Equal("CS0117", "CS0618");
        (await PackageUpgradeCheckTool.CheckAsync(loaded, nuget, app, [$"{Id}@1.0.0"], CancellationToken.None)).Should().BeEmpty();
    }
}
