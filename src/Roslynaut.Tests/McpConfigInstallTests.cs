using System.Text.Json.Nodes;

namespace Roslynaut.Tests;

public class McpConfigInstallTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "x"));

    private static string Under(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string Slashed(string path) => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    [Test]
    public void Command_OfATool_IsItsExecutable()
    {
        var shim = Under(".dotnet", "tools", "roslynaut.exe");

        AgentInstall.Command(shim, Under(".dotnet", "tools", ".store", "roslynaut", "0.3.2", "roslynaut", "0.3.2", "tools", "net10.0", "any", "Roslynaut.dll"))
            .Should().Equal(Slashed(shim));
    }

    [Test]
    public void Command_OfABuild_IsDotnetAndTheAssembly()
    {
        var (dotnet, assembly) = (Under("dotnet", "dotnet.exe"), Under("src", "Roslynaut", "bin", "Debug", "net10.0", "Roslynaut.dll"));

        AgentInstall.Command(dotnet, assembly).Should().Equal(Slashed(dotnet), Slashed(assembly));
    }

    [TestCase("roslynaut.win-x64", "0.3.2", "win-x64")]
    [TestCase("roslynaut", "0.4.0-preview.1", "any")]
    public void Command_OfAPackageThatDnxRuns_IsDnxOfThatVersion(string id, string version, string platform)
    {
        var dotnet = Under("dotnet", "dotnet");

        AgentInstall.Command(dotnet, Under(".nuget", "packages", id, version, "tools", "net10.0", platform, "Roslynaut.dll"))
            .Should().Equal(Slashed(dotnet), "dnx", $"roslynaut@{version}", "--yes", "--");
    }

    [Test]
    [Platform("Win")]
    public void Command_OnWindows_HasForwardSlashes_ForShells()
    {
        AgentInstall.Command(@"C:\Program Files\dotnet\dotnet.exe", @"C:\Users\me\src\Roslynaut\bin\Debug\net10.0\Roslynaut.dll")
            .Should().Equal("C:/Program Files/dotnet/dotnet.exe", "C:/Users/me/src/Roslynaut/bin/Debug/net10.0/Roslynaut.dll");
    }

    [Test]
    public void Register_AddsUpdatesAndRemovesServer_KeepingTheRest()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory().FullName, "mcp.json");
        File.WriteAllText(file, """{ "servers": { "other": { "command": "x" }, "Roslynaut": { "command": "old" } }, "inputs": [] }""");
        var vscode = McpConfigInstall.Agents["vscode"];
        try
        {
            AgentInstall.Register(file, vscode.Servers, vscode.Server(["dotnet", "Roslynaut.dll"]), TextWriter.Null);
            var again = new StringWriter();
            AgentInstall.Register(file, vscode.Servers, vscode.Server(["dotnet", "Roslynaut.dll"]), again);

            JsonNode.Parse(File.ReadAllText(file))!.ToJsonString().Should().Be(
                """{"servers":{"other":{"command":"x"},"roslynaut":{"type":"stdio","command":"dotnet","args":["Roslynaut.dll"]}},"inputs":[]}""");
            again.ToString().Should().Contain("up to date");

            AgentInstall.Unregister(file, vscode.Servers, TextWriter.Null);
            JsonNode.Parse(File.ReadAllText(file))!.ToJsonString().Should().Be("""{"servers":{"other":{"command":"x"}},"inputs":[]}""");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [TestCase("visualstudio", """{"servers":{"roslynaut":{"type":"stdio","command":"roslynaut","args":[]}}}""")]
    [TestCase("copilot", """{"mcpServers":{"roslynaut":{"type":"local","command":"roslynaut","args":[],"tools":["*"]}}}""")]
    [TestCase("cursor", """{"mcpServers":{"roslynaut":{"command":"roslynaut","args":[]}}}""")]
    [TestCase("gemini", """{"mcpServers":{"roslynaut":{"command":"roslynaut","args":[]}}}""")]
    [TestCase("junie", """{"mcpServers":{"roslynaut":{"command":"roslynaut","args":[]}}}""")]
    public void Register_WritesTheAgentsShape_ToANewFile(string name, string expected)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var (file, agent) = (Path.Combine(dir, "sub", "config.json"), McpConfigInstall.Agents[name]);
        try
        {
            AgentInstall.Register(file, agent.Servers, agent.Server(["roslynaut"]), TextWriter.Null);

            JsonNode.Parse(File.ReadAllText(file))!.ToJsonString().Should().Be(expected);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void Codex_ReplacesItsTables_KeepingTheRest()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var file = Path.Combine(dir, "config.toml");
        File.WriteAllText(file, """
            # My settings.
            model = "x" # a comment

            [mcp_servers]
            roslynaut.cwd = "old"

            [mcp_servers.other]
            command = "x"

            [mcp_servers."Roslynaut"]
            command = "old"

            [mcp_servers.roslynaut.env]
            A = "1"

            [profiles.fast]
            model = "y"
            """.ReplaceLineEndings("\n"));
        try
        {
            CodexInstall.Install(file, [@"C:\tools\roslynaut.exe", "é"], TextWriter.Null);
            var again = new StringWriter();
            CodexInstall.Install(file, [@"C:\tools\roslynaut.exe", "é"], again);

            File.ReadAllText(file).Should().Be("""
                # My settings.
                model = "x" # a comment

                [mcp_servers]
                [mcp_servers.other]
                command = "x"

                [profiles.fast]
                model = "y"

                [mcp_servers.roslynaut]
                command = "C:\\tools\\roslynaut.exe"
                args = ["é"]
                tool_timeout_sec = 1800

                """.ReplaceLineEndings("\n"));
            again.ToString().Should().Contain("up to date");

            CodexInstall.Uninstall(file, TextWriter.Null);
            var log = new StringWriter();
            CodexInstall.Uninstall(file, log);

            File.ReadAllText(file).Should().NotContain("roslynaut").And.Contain("[profiles.fast]");
            log.ToString().Should().StartWith("There is no");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void Codex_LeavesAFileThatIsNotToml()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var file = Path.Combine(dir, "config.toml");
        File.WriteAllText(file, "model = ");
        try
        {
            var install = () => CodexInstall.Install(file, ["roslynaut"], TextWriter.Null);

            install.Should().Throw<Exception>();
            File.ReadAllText(file).Should().Be("model = ");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
