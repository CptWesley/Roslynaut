using System.Text.Json.Nodes;

namespace Roslynaut.Tests;

public class ClaudeInstallTests
{
    [Test]
    public void Install_AddsServerAndHookOnce_KeepingTheRest()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var (config, settings) = (Path.Combine(dir, ".claude.json"), Path.Combine(dir, ".claude", "settings.json"));
        File.WriteAllText(config, """{ "numStartups": 3, "mcpServers": { "other": { "command": "x" } } }""");
        try
        {
            ClaudeInstall.Install(config, settings, ["dotnet", "Roslynaut.dll"], TextWriter.Null);
            ClaudeInstall.Install(config, settings, ["elsewhere/Roslynaut"], TextWriter.Null);

            var written = JsonNode.Parse(File.ReadAllText(config))!;
            written["numStartups"]!.GetValue<int>().Should().Be(3);
            written["mcpServers"]!.AsObject().Select(server => server.Key).Should().Equal("other", "roslynaut");
            written["mcpServers"]!["roslynaut"]!["args"]!.ToJsonString().Should().Be("""["Roslynaut.dll"]""");
            var groups = JsonNode.Parse(File.ReadAllText(settings))!["hooks"]!["PreToolUse"]!.AsArray();
            groups.Should().ContainSingle();
            groups[0]!["hooks"]![0]!["command"]!.GetValue<string>().Should().Be("dotnet");
            groups[0]!["hooks"]![0]!["args"]!.ToJsonString().Should().Be("""["Roslynaut.dll","--claude-hook"]""");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestCase("Read", """{ "file_path": "C:/src/Program.cs" }""", true)]
    [TestCase("Read", """{ "file_path": "C:/src/App.csproj" }""", false)]
    [TestCase("Grep", """{ "pattern": "class", "glob": "*.cs" }""", true)]
    [TestCase("Grep", """{ "pattern": "class", "type": "cs" }""", true)]
    [TestCase("Grep", """{ "pattern": "class", "path": "docs" }""", false)]
    public void Hint_OnlyForCSharpFiles(string tool, string input, bool hints)
    {
        var hint = ClaudeInstall.Hint(JsonNode.Parse($$"""{ "tool_name": "{{tool}}", "tool_input": {{input}} }"""));

        if (hints)
            JsonNode.Parse(hint!)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>().Should().Contain("roslynaut");
        else
            hint.Should().BeNull();
    }
}
