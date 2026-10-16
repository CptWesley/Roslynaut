using NuGet.Frameworks;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>
/// The NuGet package tools against a local feed with two versions of a package, which depends on another one, and a project that uses the first.
/// </summary>
[NonParallelizable]
public class NuGetTests
{
    private const string Id = "Roslynaut.Sample.Lib";
    private const string BaseId = "Roslynaut.Sample.Base";
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
        var @base = Write($"base/{BaseId}.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageLicenseExpression>MIT</PackageLicenseExpression></PropertyGroup>
            </Project>
            """);
        Write("base/Base.cs", "namespace Sample;\npublic static class Base { }\n");
        await PackAsync(@base, "1.0.0");

        // The lib asks for a version of the base that does not exist, so restore resolves the next one with a warning.
        // Version 2 also brings MSBuild files and an analyzer, which are only listed, so the project does not get them.
        var lib = Write($"lib/{Id}.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
                <NoWarn>CS1591;NU1603</NoWarn>
                <Description>Greets, for Roslynaut's tests.</Description>
                <PackageReadmeFile>README.md</PackageReadmeFile>
                <PackageLicenseFile>LICENSE.txt</PackageLicenseFile>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{{BaseId}}" Version="0.9.0" />
                <None Include="README.md;LICENSE.txt" Pack="true" PackagePath="" />
              </ItemGroup>
              <ItemGroup Condition="'$(Version)' == '2.0.0'">
                <None Include="{{Id}}.props" Pack="true" PackagePath="build/" />
                <None Include="Sample.Analyzer.dll" Pack="true" PackagePath="analyzers/dotnet/cs/;analyzers/dotnet/vb/" />
              </ItemGroup>
            </Project>
            """);
        Write("lib/README.md", "# Greeter");
        Write("lib/LICENSE.txt", "Free to use.");
        Write($"lib/{Id}.props", "<Project />");
        Write("lib/Sample.Analyzer.dll", "Not an assembly: only listed.");

        // Version 2 removes Old and makes Hello obsolete.
        foreach (var (version, members) in new[]
        {
            ("1.0.0", "public static string Hello() => \"Hello\"; public static string Old() => \"Old\";"),
            ("2.0.0", "[System.Obsolete(\"Use Hi.\")] public static string Hello() => \"Hello\"; public static string Hi() => \"Hi\";"),
        })
        {
            Write("lib/Greeter.cs", $"namespace Sample;\n/// <summary>Greets.</summary>\npublic static class Greeter {{ {members} }}\n");
            await PackAsync(lib, version);
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

        async Task PackAsync(string project, string version)
        {
            var (exitCode, output) = await DotnetCli.RunAsync(["pack", project, "-o", feed, $"-p:Version={version}"], TimeSpan.FromMinutes(5), CancellationToken.None);
            exitCode.Should().Be(0, output);
        }
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
        info.Dependencies.Should().ContainKey("net10.0").WhoseValue.Should().Equal($"{BaseId} (>= 0.9.0)");
    }

    [Test]
    public async Task Assembly_DownloadsThePackage_AndPicksAssembliesPerFramework()
    {
        var package = await PackageAssemblyTool.GetAsync(nuget, new PdbService(), app, Id, "1.0.0", [Net10, NuGetFramework.Parse("net48")], CancellationToken.None);

        package.Readme.Should().Match(path => path.EndsWith("README.md") && File.Exists(path));
        package.Frameworks.Should().Equal("net10.0");
        package.Assemblies.Should().HaveCount(2);
        package.Assemblies[0].Should().Match<FrameworkAssemblies>(assemblies => assemblies.TargetFramework == "net10.0" && assemblies.PackageFramework == "net10.0");
        package.Assemblies[0].Paths.Should().ContainSingle().Which.Should().Match(path => File.Exists(path) && File.Exists(Path.ChangeExtension(path, ".xml")));
        package.Assemblies[1].Should().Be(new FrameworkAssemblies("net48", null, []));

        // Packing leaves the PDB out, and no symbol server has it.
        package.Symbols.Should().ContainSingle().Which.Should().Match<AssemblySymbols>(symbols =>
            symbols.Path == package.Assemblies[0].Paths[0] && symbols.Deterministic && symbols.Pdb == null && symbols.SourceLink == null);
    }

    [Test]
    public async Task UpgradeCheck_ReturnsOnlyTheNewErrorsAndWarnings()
    {
        var loaded = await workspace.GetAsync(app);

        (await nuget.TargetFrameworksAsync(app, null)).Should().Equal(Net10);
        (await PackageUpgradeCheckTool.CheckAsync(loaded, nuget, app, [Id], CancellationToken.None)).Select(diagnostic => diagnostic.Id).Should().Equal("CS0117", "CS0618");
        (await PackageUpgradeCheckTool.CheckAsync(loaded, nuget, app, [$"{Id}@1.0.0"], CancellationToken.None)).Should().BeEmpty();
    }

    [Test]
    public async Task Why_FollowsTheChain_AndReportsRestoreMessages()
    {
        var assets = NuGetService.AssetsOf(await workspace.GetAsync(app), app);

        var why = PackageWhyTool.Explain(assets, BaseId, maxPaths: 10);

        why.Paths.Should().Equal(new DependencyPath("App.csproj", "net10.0", $"{Id} 1.0.0 > {BaseId} 1.0.0"));
        why.Messages.Should().ContainSingle().Which.Should().Match<RestoreMessage>(message => message.Code == "NU1603" && message.Level == "Warning");
        why.Truncated.Should().BeFalse();
        PackageWhyTool.Explain(assets, Id, maxPaths: 10).Paths.Should().Equal(new DependencyPath("App.csproj", "net10.0", $"{Id} 1.0.0"));
    }

    [Test]
    public async Task Contents_ListsFiles_AndWhatReachesTheBuild()
    {
        var contents = await PackageContentsTool.GetAsync(nuget, app, Id, "2.0.0", [Net10], folder: null, CancellationToken.None);

        contents.Files.Should().Contain(["README.md", "LICENSE.txt", $"lib/net10.0/{Id}.dll", $"build/{Id}.props"])
            .And.NotContain(file => file.StartsWith('.') || file.EndsWith(".nupkg"));
        contents.MsBuild.Should().ContainKey("net10.0").WhoseValue.Should().Equal($"build/{Id}.props");
        contents.Analyzers.Should().Equal("analyzers/dotnet/cs/Sample.Analyzer.dll");
        (await PackageContentsTool.GetAsync(nuget, app, Id, "2.0.0", [Net10], @"lib\net10.0\", CancellationToken.None)).Files
            .Should().Equal($"lib/net10.0/{Id}.dll", $"lib/net10.0/{Id}.xml");
    }

    [Test]
    public void Config_HasTheSources_AndTheMapping()
    {
        var config = NuGetConfigTool.Describe(NuGetService.LoadSettings(app));

        // NuGet spells the file name its own way.
        config.ConfigFiles.Should().Contain(file => file.Equals(Path.Combine(directory.FullName, "nuget.config"), StringComparison.OrdinalIgnoreCase));
        config.Sources.Should().Equal(
            new NuGetSource("local", Path.Combine(directory.FullName, "feed"), true, false),
            new NuGetSource("nuget.org", "https://api.nuget.org/v3/index.json", true, false));
        config.SourceMapping["local"].Should().Equal("Roslynaut.*");
        config.GlobalPackagesFolder.Should().StartWith(Path.Combine(directory.FullName, "packages"));
    }

    [Test]
    public async Task Licenses_ReadEachPackage_DirectOrNot()
    {
        var licenses = PackageLicensesTool.Collect(NuGetService.AssetsOf(await workspace.GetAsync(app), app)).ToList();

        licenses.Should().Equal(
            new PackageLicense(BaseId, "1.0.0", false, "MIT", "Expression"),
            new PackageLicense(Id, "1.0.0", true, Path.Combine(directory.FullName, "packages", Id.ToLowerInvariant(), "1.0.0", "LICENSE.txt"), "File"));
    }
}

/// <summary>A private feed that needs a credential, served locally: with none, NuGet asks the credential providers without prompting.</summary>
[NonParallelizable]
public class NuGetCredentialTests
{
    private const string User = "reader";
    private const string Password = "s3cret";

    private System.Net.HttpListener listener = null!;
    private string feed = null!;

    [OneTimeSetUp]
    public void StartFeed()
    {
        // A free port.
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        feed = $"http://localhost:{port}/";
        listener = new System.Net.HttpListener { Prefixes = { feed } };
        listener.Start();
        _ = Task.Run(ServeAsync);
    }

    [OneTimeTearDown]
    public void StopFeed() => listener.Close();

    // A service index with a search endpoint that finds nothing, behind Basic authentication.
    private async Task ServeAsync()
    {
        var expected = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{User}:{Password}"));
        while (listener.IsListening)
        {
            System.Net.HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception e) when (e is System.Net.HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            var response = context.Response;
            if (context.Request.Headers["Authorization"] != expected)
            {
                response.StatusCode = 401;
                response.AddHeader("WWW-Authenticate", "Basic realm=\"feed\"");
            }
            else
            {
                var body = context.Request.Url!.AbsolutePath.EndsWith("index.json")
                    ? $$"""{ "version": "3.0.0", "resources": [ { "@id": "{{feed}}query", "@type": "SearchQueryService" }, { "@id": "{{feed}}query", "@type": "SearchQueryService/3.0.0-rc" }, { "@id": "{{feed}}query", "@type": "SearchQueryService/3.0.0-beta" }, { "@id": "{{feed}}query", "@type": "SearchQueryService/3.5.0" } ] }"""
                    : """{ "totalHits": 0, "data": [] }""";
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                response.ContentType = "application/json";
                await response.OutputStream.WriteAsync(bytes);
            }

            response.Close();
        }
    }

    private async Task<string> ProjectWithFeedAsync(string credentials)
    {
        var directory = Directory.CreateTempSubdirectory("roslynaut-feed-");
        File.WriteAllText(Path.Combine(directory.FullName, "nuget.config"), $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="private" value="{feed}index.json" allowInsecureConnections="true" />
              </packageSources>
              {credentials}
            </configuration>
            """);
        var project = Path.Combine(directory.FullName, "App.csproj");
        await File.WriteAllTextAsync(project, """<Project Sdk="Microsoft.NET.Sdk" />""");
        return project;
    }

    [Test]
    public async Task Search_WithoutCredential_FailsWithHowToSignIn()
    {
        var project = await ProjectWithFeedAsync("");

        await new NuGetService(new WorkspaceService()).Awaiting(nuget => nuget.SearchAsync(project, "anything", false, 0, 10, CancellationToken.None))
            .Should().ThrowAsync<ModelContextProtocol.McpException>().WithMessage("*401*dotnet restore --interactive*");

        // The credential providers were asked, through the service restore uses too.
        NuGet.Protocol.HttpHandlerResourceV3.CredentialService!.Value.Should().BeOfType<NuGet.Credentials.CredentialService>();
    }

    [Test]
    public async Task Search_WithCredentialInNuGetConfig_SignsIn()
    {
        var project = await ProjectWithFeedAsync($"""
            <packageSourceCredentials>
              <private>
                <add key="Username" value="{User}" />
                <add key="ClearTextPassword" value="{Password}" />
              </private>
            </packageSourceCredentials>
            """);

        (await new NuGetService(new WorkspaceService()).SearchAsync(project, "anything", false, 0, 10, CancellationToken.None))
            .Should().ContainSingle().Which.Source.Should().Be("private");
    }
}
