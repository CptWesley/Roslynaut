using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Roslynaut.Tests.EndToEnd;
using Roslynaut.Tools;

namespace Roslynaut.Tests;

/// <summary>
/// The server as dotnet tool install puts it on a machine: packed, installed from the package into a tool path and started by the tool's
/// command, so it runs from the package's files alone. The helper hosts have to come along in the package, beside the server.
/// </summary>
[Category("EndToEnd")]
[NonParallelizable]
public class ToolPackageTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private string _directory = null!;
    private string _command = null!;

    private string In(params string[] path) => Path.Combine([_directory, .. path]);

    [OneTimeSetUp]
    public async Task InstallAsync()
    {
        _directory = Directory.CreateTempSubdirectory("roslynaut-tool-").FullName;
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(source.FullName, "Roslynaut", "Roslynaut.csproj")))
            source = source.Parent ?? throw new InvalidOperationException($"No Roslynaut/Roslynaut.csproj above {AppContext.BaseDirectory}.");

        var server = Path.Combine(source.FullName, "Roslynaut");
        var (packed, packOutput) = await DotnetCli.RunAsync(["pack", "-o", In("packages")], Timeout, CancellationToken.None, workingDirectory: server);
        packed.Should().Be(0, packOutput);

        // Only the package just packed, not one of the same version from nuget.org.
        File.WriteAllText(In("nuget.config"), """<configuration><packageSources><clear /><add key="packages" value="packages" /></packageSources></configuration>""");
        var (installed, installOutput) = await DotnetCli.RunAsync(
            ["tool", "install", "Roslynaut", "--tool-path", In("tools"), "--configfile", In("nuget.config")], Timeout, CancellationToken.None);
        installed.Should().Be(0, installOutput);
        _command = In("tools", OperatingSystem.IsWindows() ? "roslynaut.exe" : "roslynaut");
    }

    [OneTimeTearDown]
    public void Delete()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A build node that loaded Buildalyzer's logger from the tool can hold it for a while.
            TestContext.Progress.WriteLine($"Could not delete {_directory}: {e.Message}");
        }
    }

    // build loads Buildalyzer's logger, evaluate runs the script host and assembly_asm the JIT host, all from the tool's directory.
    [Test]
    public async Task InstalledTool_RunsItsHelpers()
    {
        var project = In("Lib", "Lib.csproj");
        Directory.CreateDirectory(In("Lib"));
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(In("Lib", "Numbers.cs"), "namespace Lib;\n\npublic static class Numbers\n{\n    public static int Twice(int value) => value * 2;\n}\n");

        await using var server = await ServerProcess.StartAsync(_command, ["--protocol", "stdio"], new() { [EvaluateTool.AllowVariable] = "true" });
        var build = await server.CallAsync<BuildTool.BuildResult>("build", new() { ["projectPath"] = project });
        build.Succeeded.Should().BeTrue(build.Output ?? string.Join("\n", build.Messages));
        var evaluation = await server.CallAsync<EvaluateTool.Evaluation>("evaluate", new() { ["projectPath"] = project, ["code"] = "Lib.Numbers.Twice(21)" });
        var asm = await server.CallAsync<PagedResult<MethodAsm>>("assembly_asm", new()
        {
            ["assemblyPath"] = In("Lib", "bin", "Debug", "net10.0", "Lib.dll"),
            ["name"] = "Numbers.Twice",
        });

        evaluation.Should().Be(new EvaluateTool.Evaluation("", "42", "System.Int32", null));
        asm.Items.Should().ContainSingle().Which.Asm.Should().Contain("; Assembly listing for method Lib.Numbers:Twice(int):int");
        Directory.GetFiles(In("tools", ".store"), "Roslynaut.CallCounter.dll", SearchOption.AllDirectories).Should().ContainSingle();
    }

    // A package the tool starts to ship, as a new dependency or a dependency's, needs its license in THIRD-PARTY-NOTICES.txt.
    [Test]
    public void Package_NoticesEveryPackageItShips()
    {
        var store = In("tools", ".store");
        var notices = File.ReadAllText(Directory.GetFiles(store, "THIRD-PARTY-NOTICES.txt", SearchOption.AllDirectories).Single());
        var shipped = Directory.GetFiles(store, "*.deps.json", SearchOption.AllDirectories)
            .Select(file => JsonNode.Parse(File.ReadAllText(file))!)
            .SelectMany(deps => deps["targets"]!.AsObject().SelectMany(target => target.Value!.AsObject())
                .Where(library => (string?)deps["libraries"]![library.Key]!["type"] == "package"
                    && library.Value!.AsObject().Any(assets => assets.Key is "runtime" or "runtimeTargets" or "native" or "resources")))
            .Select(library => library.Key.Split('/')[0])
            .Distinct()
            .ToList();

        shipped.Should().NotBeEmpty();
        // Whole names in the lists of names, so neither Buildalyzer.Logger nor a URL stands in for Buildalyzer.
        shipped.Where(id => !Regex.IsMatch(notices, $@"(?m)(?:^|, ){Regex.Escape(id)}(?:,|\r?$)")).Should().BeEmpty();
    }

    // The tool's command, not dotnet with the server's path in the tool store, which changes with every update.
    [Test]
    public async Task InstalledTool_InstallsItsCommand()
    {
        var start = new ProcessStartInfo(_command, ["--install", "claude"]) { RedirectStandardOutput = true, Environment = { ["CLAUDE_CONFIG_DIR"] = In("claude") } };
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.Should().Be(0, output);
        var server = JsonNode.Parse(File.ReadAllText(In("claude", ".claude.json")))!["mcpServers"]!["roslynaut"]!;
        server["command"]!.GetValue<string>().Should().Be(_command);
        server["args"]!.AsArray().Should().BeEmpty();
    }
}
