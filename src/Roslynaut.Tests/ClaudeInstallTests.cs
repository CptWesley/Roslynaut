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
            var events = JsonNode.Parse(File.ReadAllText(settings))!["hooks"]!.AsObject();
            events.Select(entry => entry.Key).Should().Equal("PreToolUse", "PostToolUse", "SessionStart");
            foreach (var (_, groups) in events)
            {
                groups!.AsArray().Should().ContainSingle();
                groups[0]!["hooks"]![0]!["command"]!.GetValue<string>().Should().Be("dotnet");
                groups[0]!["hooks"]![0]!["args"]!.ToJsonString().Should().Be("""["Roslynaut.dll","--claude-hook"]""");
            }
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
                + """{"matcher":"^(Read|Grep|Glob|Bash|PowerShell|mcp__.*(shell|terminal|command).*)$","hooks":[{"type":"command","command":"dotnet","args":["Roslynaut.dll","--claude-hook"]}]}]""");
            again.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().AllSatisfy(line => line.Should().Contain("up to date"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void Uninstall_RemovesServerAndHook_KeepingTheRest()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var (config, settings) = (Path.Combine(dir, ".claude.json"), Path.Combine(dir, "settings.json"));
        File.WriteAllText(config, """{ "mcpServers": { "other": { "command": "x" }, "Roslynaut": { "command": "old/Roslynaut" } } }""");
        File.WriteAllText(settings, """
            { "hooks": { "PreToolUse": [
                { "matcher": "Read|Grep", "hooks": [ { "type": "command", "command": "old/Roslynaut", "args": ["--claude-hook"] }, { "type": "command", "command": "mine" } ] } ] } }
            """);
        try
        {
            ClaudeInstall.Install(config, settings, ["dotnet", "Roslynaut.dll"], TextWriter.Null);
            ClaudeInstall.Uninstall(config, settings, TextWriter.Null);
            var again = new StringWriter();
            ClaudeInstall.Uninstall(config, settings, again);

            JsonNode.Parse(File.ReadAllText(config))!["mcpServers"]!.ToJsonString().Should().Be("""{"other":{"command":"x"}}""");
            JsonNode.Parse(File.ReadAllText(settings))!["hooks"]!.ToJsonString().Should().Be(
                """{"PreToolUse":[{"matcher":"Read|Grep","hooks":[{"type":"command","command":"mine"}]}]}""");
            again.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().AllSatisfy(line => line.Should().StartWith("There "));
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
    [TestCase("PowerShell", """{ "command": "Get-Content src/Program.cs -TotalCount 40" }""", true)]
    [TestCase("PowerShell", """{ "command": "Select-String -Path src/*.cs -Pattern 'class Order'" }""", true)]
    [TestCase("PowerShell", """{ "command": "Get-Content src/App.csproj" }""", false)]
    [TestCase("mcp__serena__execute_shell_command", """{ "command": "findstr /s Order *.cs" }""", true)]
    [TestCase("Glob", """{ "pattern": "**/*.cs" }""", true)]
    [TestCase("Glob", """{ "pattern": "**/*.json" }""", false)]
    [TestCase("Edit", """{ "file_path": "C:/src/Program.cs", "old_string": "a", "new_string": "b" }""", true)]
    [TestCase("Write", """{ "file_path": "C:/src/App.csproj", "content": "" }""", false)]
    [TestCase("Bash", """{ "command": "dotnet build src/App.slnx" }""", true)]
    [TestCase("PowerShell", """{ "command": "dotnet test src/App.Tests --filter Name=X" }""", true)]
    [TestCase("Bash", """{ "command": "dotnet run --project src/App" }""", false)]
    public void Hint_OnlyForCSharpFiles(string tool, string input, bool hints)
    {
        var hint = ClaudeInstall.Hint(JsonNode.Parse($$"""{ "tool_name": "{{tool}}", "tool_input": {{input}} }"""));

        if (hints)
            JsonNode.Parse(hint!)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>().Should().Contain("select:mcp__roslynaut__");
        else
            hint.Should().BeNull();
    }

    [Test]
    public void Hints_NameTheSolution_AndTheEvent()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        Directory.CreateDirectory(Path.Combine(dir, "src", "Lib"));
        var solution = Path.Combine(dir, "src", "App.slnx");
        File.WriteAllText(solution, "<Solution />");
        File.WriteAllText(Path.Combine(dir, "src", "Lib", "Lib.csproj"), "<Project />");
        var file = Path.Combine(dir, "src", "Lib", "Order.cs");
        File.WriteAllText(file, "class Order;");
        string Context(string hint) => JsonNode.Parse(hint)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        string? Hint(string call) => ClaudeInstall.Hint(JsonNode.Parse(call.Replace("DIR", dir.Replace("\\", "/")).Replace("FILE", file.Replace("\\", "/"))));
        try
        {
            var read = Hint("""{ "hook_event_name": "PreToolUse", "cwd": "DIR", "tool_name": "Read", "tool_input": { "file_path": "FILE" } }""")!;
            Context(read).Should().Contain($"projectPath \"{solution}\"").And.Contain("select:mcp__roslynaut__document_symbols,mcp__roslynaut__symbol_source");

            var edit = Hint("""{ "hook_event_name": "PostToolUse", "cwd": "DIR", "tool_name": "Edit", "tool_input": { "file_path": "FILE" } }""")!;
            JsonNode.Parse(edit)!["hookSpecificOutput"]!["hookEventName"]!.GetValue<string>().Should().Be("PostToolUse");
            Context(edit).Should().Contain("diagnostics").And.Contain(solution);

            // A session starts at the repository's root, above the solution.
            var start = Hint("""{ "hook_event_name": "SessionStart", "source": "compact", "cwd": "DIR" }""")!;
            JsonNode.Parse(start)!["hookSpecificOutput"]!["hookEventName"]!.GetValue<string>().Should().Be("SessionStart");
            Context(start).Should().Contain(solution);

            ClaudeInstall.Hint(JsonNode.Parse($$"""{ "hook_event_name": "SessionStart", "cwd": "{{Path.Combine(dir, ".git").Replace("\\", "/")}}" }"""))
                .Should().BeNull("there is no project in or below .git, and the search above stops at the repository");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
