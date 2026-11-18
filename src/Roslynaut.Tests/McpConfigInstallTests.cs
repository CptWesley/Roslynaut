using System.Text.Json.Nodes;

namespace Roslynaut.Tests;

public class McpConfigInstallTests
{
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
