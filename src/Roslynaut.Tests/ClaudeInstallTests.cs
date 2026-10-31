using System.Text.Json.Nodes;

namespace Roslynaut.Tests;

public class ClaudeInstallTests
{
    [Test]
    public void Install_AddsServerAndHook_KeepingTheRest()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var (config, settings) = (Path.Combine(dir, ".claude.json"), Path.Combine(dir, ".claude", "settings.json"));
        File.WriteAllText(config, """{ "numStartups": 3, "mcpServers": { "other": { "command": "x" } } }""");
        try
        {
            ClaudeInstall.Install(config, settings, ["dotnet", "Roslynaut.dll"], TextWriter.Null);

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

    [Test]
    public void Install_ReplacesAnOlderInstallation_ThenLeavesItAlone()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var (config, settings) = (Path.Combine(dir, ".claude.json"), Path.Combine(dir, "settings.json"));
        File.WriteAllText(config, """{ "mcpServers": { "other": { "command": "x" }, "Roslynaut": { "command": "old/Roslynaut" } } }""");
        File.WriteAllText(settings, """
            { "hooks": { "PreToolUse": [
                { "matcher": "Read|Grep", "hooks": [ { "type": "command", "command": "old/Roslynaut", "args": ["--claude-hook"] }, { "type": "command", "command": "mine" } ] },
                { "matcher": "Bash", "hooks": [ { "type": "command", "command": "old/Roslynaut", "args": ["--claude-hook"] } ] } ] } }
            """);
        try
        {
            ClaudeInstall.Install(config, settings, ["dotnet", "Roslynaut.dll"], TextWriter.Null);
            var again = new StringWriter();
            ClaudeInstall.Install(config, settings, ["dotnet", "Roslynaut.dll"], again);

            JsonNode.Parse(File.ReadAllText(config))!["mcpServers"]!.ToJsonString().Should().Be(
                """{"other":{"command":"x"},"roslynaut":{"type":"stdio","command":"dotnet","args":["Roslynaut.dll"]}}""");
            JsonNode.Parse(File.ReadAllText(settings))!["hooks"]!["PreToolUse"]!.ToJsonString().Should().Be(
                """[{"matcher":"Read|Grep","hooks":[{"type":"command","command":"mine"}]},"""
                + """{"matcher":"Read|Grep|Bash","hooks":[{"type":"command","command":"dotnet","args":["Roslynaut.dll","--claude-hook"]}]}]""");
            again.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().AllSatisfy(line => line.Should().Contain("up to date"));
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
    [TestCase("Bash", """{ "command": "grep -rn 'class Order' --include=*.cs src" }""", true)]
    [TestCase("Bash", """{ "command": "rg -t cs Submit" }""", true)]
    [TestCase("Bash", """{ "command": "sed -n '10,40p' src/Program.cs" }""", true)]
    [TestCase("Bash", """{ "command": "git add src/Program.cs" }""", false)]
    [TestCase("Bash", """{ "command": "grep -r Version src/App.csproj" }""", false)]
    public void Hint_OnlyForCSharpFiles(string tool, string input, bool hints)
    {
        var hint = ClaudeInstall.Hint(JsonNode.Parse($$"""{ "tool_name": "{{tool}}", "tool_input": {{input}} }"""));

        if (hints)
            JsonNode.Parse(hint!)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>().Should().Contain("roslynaut");
        else
            hint.Should().BeNull();
    }
}
