using System.Text.Json.Nodes;

namespace Roslynaut.Tests;

public class McpConfigInstallTests
{
    [Test]
    public void VsCode_AddsUpdatesAndRemovesServer_KeepingTheRest()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory().FullName, "mcp.json");
        File.WriteAllText(file, """{ "servers": { "other": { "command": "x" }, "Roslynaut": { "command": "old" } }, "inputs": [] }""");
        try
        {
            AgentInstall.Register(file, ["servers"], VsCodeInstall.Server(["dotnet", "Roslynaut.dll"]), TextWriter.Null);
            var again = new StringWriter();
            AgentInstall.Register(file, ["servers"], VsCodeInstall.Server(["dotnet", "Roslynaut.dll"]), again);

            JsonNode.Parse(File.ReadAllText(file))!.ToJsonString().Should().Be(
                """{"servers":{"other":{"command":"x"},"roslynaut":{"type":"stdio","command":"dotnet","args":["Roslynaut.dll"]}},"inputs":[]}""");
            again.ToString().Should().Contain("up to date");

            AgentInstall.Unregister(file, ["servers"], TextWriter.Null);
            JsonNode.Parse(File.ReadAllText(file))!.ToJsonString().Should().Be("""{"servers":{"other":{"command":"x"}},"inputs":[]}""");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Test]
    public void Copilot_AddsServerWithAllTools_ToANewFile()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory().FullName, ".copilot", "mcp-config.json");
        try
        {
            AgentInstall.Register(file, ["mcpServers"], CopilotInstall.Server(["roslynaut"]), TextWriter.Null);

            JsonNode.Parse(File.ReadAllText(file))!.ToJsonString().Should().Be(
                """{"mcpServers":{"roslynaut":{"type":"local","command":"roslynaut","args":[],"tools":["*"]}}}""");
            var log = new StringWriter();
            AgentInstall.Unregister(Path.Combine(Path.GetDirectoryName(file)!, "missing.json"), ["mcpServers"], log);
            log.ToString().Should().StartWith("There is no");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(file))!, recursive: true);
        }
    }
}
