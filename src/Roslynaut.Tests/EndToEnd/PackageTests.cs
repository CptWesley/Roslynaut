using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>
/// The NuGet tools on Shapes.App, which uses Roslynaut.E2E.Greetings 1.0.0 from the local feed, which depends on Roslynaut.E2E.Base 1.0.0.
/// Greetings 2.0.0 makes Hello obsolete and removes Bye, which the app both calls.
/// </summary>
public class PackageTests : EndToEndTest
{
    private const string Greetings = SampleSolution.GreetingsId;
    private const string Base = SampleSolution.BaseId;

    [Test]
    public async Task Packages_DirectTransitiveAndOutdated()
    {
        var all = await Server.CallAsync<PagedResult<PackagesTool.Package>>("packages", new() { ["projectPath"] = Sample.AppProject, ["transitive"] = true });
        var outdated = await Server.CallAsync<PagedResult<PackagesTool.Package>>("packages", new() { ["projectPath"] = Sample.AppProject, ["filter"] = "Outdated" });

        all.Items.Should().BeEquivalentTo(
        [
            new PackagesTool.Package("Shapes.App.csproj", "net10.0", Greetings, "1.0.0", "1.0.0", null, false, [], []),
            new PackagesTool.Package("Shapes.App.csproj", "net10.0", Base, null, "1.0.0", null, true, [], []),
        ]);
        outdated.Items.Should().BeEquivalentTo([new PackagesTool.Package("Shapes.App.csproj", "net10.0", Greetings, "1.0.0", "1.0.0", "2.0.0", false, [], [])]);
    }

    [Test]
    public async Task PackageSearch_OnTheSourcesOfTheConfig()
    {
        var results = await Server.CallAsync<PagedResult<PackageSearchResult>>("package_search", new() { ["projectPath"] = Sample.AppProject, ["query"] = "Roslynaut.E2E" });

        // Base is packed without a description or authors, so NuGet fills them in.
        results.Items.Should().BeEquivalentTo(
        [
            new PackageSearchResult(Greetings, "2.0.0", "Greets people, for Roslynaut's end-to-end tests.", "Roslynaut", null, false, "local"),
            new PackageSearchResult(Base, "1.0.0", "Package Description", Base, null, false, "local"),
        ]);
        results.HasMore.Should().BeFalse();
    }

    [Test]
    public async Task PackageInfo_OfTheLatestVersion_ForTheProjectsFramework()
    {
        var info = await Server.CallAsync<PackageInfo>("package_info", new() { ["projectPath"] = Sample.AppProject, ["id"] = Greetings });

        (info.Id, info.Version, info.Listed, info.Description, info.Authors, info.License, info.ProjectUrl, info.Deprecation)
            .Should().Be((Greetings, "2.0.0", true, "Greets people, for Roslynaut's end-to-end tests.", "Roslynaut", "Apache-2.0", null, null));
        info.Published.Should().NotBeNull();
        info.Vulnerabilities.Should().BeEmpty();
        info.Frameworks.Should().Equal("net10.0");
        info.Nearest.Should().BeEquivalentTo(new Dictionary<string, string?> { ["net10.0"] = "net10.0" });
        info.Dependencies.Should().BeEquivalentTo(new Dictionary<string, string[]> { ["net10.0"] = [$"{Base} (>= 1.0.0)"] });
        info.Versions.Should().Equal("2.0.0", "1.0.0");
    }

    [Test]
    public async Task PackageInfo_OfAnEarlierVersion_AndOfPrereleases()
    {
        var first = await Server.CallAsync<PackageInfo>("package_info", new() { ["projectPath"] = Sample.AppProject, ["id"] = Greetings, ["version"] = "1.0.0" });
        var beta = await Server.CallAsync<PackageInfo>("package_info", new() { ["projectPath"] = Sample.AppProject, ["id"] = Greetings, ["prerelease"] = true });

        (first.Version, first.Versions).Should().BeEquivalentTo(("1.0.0", new[] { "2.0.0", "1.0.0" }), options => options.WithStrictOrdering());
        first.Dependencies.Should().BeEquivalentTo(new Dictionary<string, string[]> { ["net10.0"] = [$"{Base} (>= 1.0.0)"] });
        (beta.Version, beta.Versions).Should().BeEquivalentTo(("3.0.0-beta", new[] { "3.0.0-beta", "2.0.0", "1.0.0" }), options => options.WithStrictOrdering());
        (await Server.CallFailingAsync("package_info", new() { ["projectPath"] = Sample.AppProject, ["id"] = Greetings, ["version"] = "9.9.9" }))
            .Should().Be($"An error occurred invoking 'package_info': Package {Greetings} has no version 9.9.9.");
    }

    // Greetings only has net10.0, which a net8.0 project cannot use.
    [Test]
    public async Task PackageInfo_ForAnotherFramework_ThatItHasNothingFor()
    {
        var info = await Server.CallAsync<PackageInfo>("package_info", new() { ["projectPath"] = Sample.AppProject, ["id"] = Greetings, ["targetFramework"] = "net8.0" });

        info.Frameworks.Should().Equal("net10.0");
        info.Nearest.Should().BeEquivalentTo(new Dictionary<string, string?> { ["net8.0"] = null });
        info.Dependencies.Should().BeEmpty();
    }

    [Test]
    public async Task PackageSearch_WithPrereleases()
    {
        var results = await Server.CallAsync<PagedResult<PackageSearchResult>>("package_search", new()
        {
            ["projectPath"] = Sample.AppProject, ["query"] = "Roslynaut.E2E.Greetings", ["prerelease"] = true,
        });

        results.Items.Should().ContainSingle().Which.Should().Match<PackageSearchResult>(result => result.Id == Greetings && result.Version == "3.0.0-beta");
    }

    // Greetings embeds its PDB, with all its sources; packing sets the paths to obj of a Release build.
    [Test]
    public async Task PackageAssembly_PerFramework_WithItsSymbols()
    {
        var package = await Server.CallAsync<PackageAssemblies>("package_assembly", new() { ["projectPath"] = Sample.AppProject, ["id"] = Greetings, ["version"] = "1.0.0" });

        package.Should().BeEquivalentTo(new PackageAssemblies(
            Greetings, "1.0.0", Path.Combine(Sample.Packages, Greetings.ToLowerInvariant(), "1.0.0"), null, ["net10.0"],
            [new FrameworkAssemblies("net10.0", "net10.0", [Sample.GreetingsAssembly])],
            [new AssemblySymbols(Sample.GreetingsAssembly, "embedded", true, null, 3, 3)]));
    }

    [Test]
    public async Task PackageContents_ListsFiles_AndWhatReachesTheBuild()
    {
        var contents = await Server.CallAsync<PackageContents>("package_contents", new() { ["projectPath"] = Sample.AppProject, ["id"] = Greetings, ["version"] = "2.0.0" });
        var lib = await Server.CallAsync<PackageContents>("package_contents", new()
        {
            ["projectPath"] = Sample.AppProject, ["id"] = Greetings, ["version"] = "2.0.0", ["folder"] = "lib/net10.0",
        });

        contents.Should().BeEquivalentTo(new PackageContents(Greetings, "2.0.0", Path.Combine(Sample.Packages, Greetings.ToLowerInvariant(), "2.0.0"),
            [$"lib/net10.0/{Greetings}.dll", $"lib/net10.0/{Greetings}.xml", $"{Greetings.ToLowerInvariant()}.nuspec"],
            new Dictionary<string, IReadOnlyList<string>> { ["net10.0"] = [] }, []));
        lib.Files.Should().Equal($"lib/net10.0/{Greetings}.dll", $"lib/net10.0/{Greetings}.xml");
        File.Exists(Path.Combine(contents.Directory, "lib", "net10.0", $"{Greetings}.dll")).Should().BeTrue();
    }

    [Test]
    public async Task PackageWhy_FollowsTheChainToATransitivePackage()
    {
        var why = await Server.CallAsync<PackageWhy>("package_why", new() { ["projectPath"] = Sample.AppProject, ["id"] = Base });
        var direct = await Server.CallAsync<PackageWhy>("package_why", new() { ["projectPath"] = Sample.AppProject, ["id"] = Greetings });

        why.Should().BeEquivalentTo(new PackageWhy([], [new DependencyPath("Shapes.App.csproj", "net10.0", $"{Greetings} 1.0.0 > {Base} 1.0.0")], false));
        direct.Paths.Should().Equal(new DependencyPath("Shapes.App.csproj", "net10.0", $"{Greetings} 1.0.0"));
    }

    [Test]
    public async Task PackageLicenses_OfEveryPackageResolved()
    {
        var licenses = await Server.CallAsync<PagedResult<PackageLicense>>("package_licenses", new() { ["projectPath"] = Sample.AppProject });

        licenses.Items.Should().Equal(new PackageLicense(Base, "1.0.0", false, "MIT", "Expression"), new PackageLicense(Greetings, "1.0.0", true, "Apache-2.0", "Expression"));
    }

    [Test]
    public async Task PackageUpgradeCheck_GivesTheNewErrorsAndWarnings()
    {
        var upgrade = await Server.CallAsync<PagedResult<DiagnosticsTool.DiagnosticEntry>>("package_upgrade_check", new()
        {
            ["projectPath"] = Sample.AppProject, ["packages"] = new[] { $"{Greetings}@2.0.0" },
        });
        var same = await Server.CallAsync<PagedResult<DiagnosticsTool.DiagnosticEntry>>("package_upgrade_check", new()
        {
            ["projectPath"] = Sample.AppProject, ["packages"] = new[] { $"{Greetings}@1.0.0" },
        });

        upgrade.Items.Should().Equal(
            new DiagnosticsTool.DiagnosticEntry("CS0117", "Error", "'Greeter' does not contain a definition for 'Bye'", Sample.Location(Sample.ProgramFile, "Bye(")),
            new DiagnosticsTool.DiagnosticEntry("CS0618", "Warning", "'Greeter.Hello(string)' is obsolete: 'Use Hi.'", Sample.Location(Sample.ProgramFile, "Greeter.Hello")));
        same.Items.Should().BeEmpty();
    }
}
