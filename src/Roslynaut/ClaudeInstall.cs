using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Roslynaut;

/// <summary>
/// Registers the server with Claude Code for all projects (<c>--install claude</c>), along with a PreToolUse hook that points Claude to the
/// server's tools when it reads or greps C# files. The hook only adds context: it never blocks or approves a tool call.
/// </summary>
internal static partial class ClaudeInstall
{
    public const string HookOption = "--claude-hook";
    private const string Name = "roslynaut";

    // Claude Code's shells, and the shell tools of MCP servers, which name them in many ways but take a command too. Anchored, as Claude Code
    // reads a matcher with other characters than letters and | as an unanchored regex.
    private const string Matcher = "^(Read|Grep|Bash|PowerShell|mcp__.*(shell|terminal|command).*)$";

    private const string ReadHint =
        "This is C# code. The roslynaut MCP server can show less of it: document_symbols outlines a file, symbol_source returns one symbol's "
        + "source, and definition, references and callers navigate by symbol.";

    private const string GrepHint =
        "This searches C# code. The roslynaut MCP server knows symbols where text search does not: find_symbols and workspace_symbols find "
        + "declarations, references finds every use of a symbol, and search_pattern also searches generated code.";

    public static void Run(bool uninstall)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // CLAUDE_CONFIG_DIR moves both files, as it does for Claude Code.
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : null;
        var (configFile, settingsFile) = (Path.Combine(configDir ?? home, ".claude.json"), Path.Combine(configDir ?? Path.Combine(home, ".claude"), "settings.json"));
        if (uninstall)
        {
            Uninstall(configFile, settingsFile, Console.Out);
            return;
        }

        Install(configFile, settingsFile, AgentInstall.Command(), Console.Out);
    }

    internal static void Install(string configFile, string settingsFile, string[] command, TextWriter log)
    {
        var config = JsonFile.Read(configFile);
        var servers = (config["mcpServers"] ??= new JsonObject()).AsObject();
        var server = new JsonObject { ["type"] = "stdio", ["command"] = command[0], ["args"] = JsonFile.Strings(command[1..]) };
        var found = Servers(servers);
        if (found is [{ Key: Name, Value: var value }] && JsonNode.DeepEquals(value, server))
            log.WriteLine($"The MCP server {Name} in {configFile} is up to date.");
        else
        {
            foreach (var entry in found)
                servers.Remove(entry.Key);
            servers[Name] = server;
            JsonFile.Write(configFile, config);
            log.WriteLine($"{(found is [] ? "Added" : "Updated")} the MCP server {Name} in {configFile}.");
        }

        var settings = JsonFile.Read(settingsFile);
        var groups = ((settings["hooks"] ??= new JsonObject())["PreToolUse"] ??= new JsonArray()).AsArray();
        // Exec form (args), so no shell has to quote the path.
        var hook = new JsonObject { ["type"] = "command", ["command"] = command[0], ["args"] = JsonFile.Strings([.. command[1..], HookOption]) };
        var group = new JsonObject { ["matcher"] = Matcher, ["hooks"] = new JsonArray(hook) };
        var ours = groups.Where(Ours).ToList();
        if (ours is [var installed] && JsonNode.DeepEquals(installed, group))
            log.WriteLine($"The Roslynaut hook in {settingsFile} is up to date.");
        else
        {
            RemoveHooks(groups, ours);
            groups.Add(group);
            JsonFile.Write(settingsFile, settings);
            log.WriteLine($"{(ours is [] ? "Added" : "Updated")} the Roslynaut hook, which suggests its tools when Claude reads or greps C# files, in {settingsFile}.");
        }
    }

    /// <summary>
    /// Removes what <see cref="Install"/> adds, by any version, before the tool is uninstalled: else Claude Code fails to start the hook on every
    /// read and grep.
    /// </summary>
    internal static void Uninstall(string configFile, string settingsFile, TextWriter log)
    {
        var config = JsonFile.Read(configFile);
        if (config["mcpServers"] is JsonObject servers && Servers(servers) is [_, ..] found)
        {
            foreach (var entry in found)
                servers.Remove(entry.Key);
            JsonFile.Write(configFile, config);
            log.WriteLine($"Removed the MCP server {Name} from {configFile}.");
        }
        else
            log.WriteLine($"There is no MCP server {Name} in {configFile}.");

        var settings = JsonFile.Read(settingsFile);
        if (settings["hooks"]?["PreToolUse"] is JsonArray groups && groups.Where(Ours).ToList() is [_, ..] ours)
        {
            RemoveHooks(groups, ours);
            JsonFile.Write(settingsFile, settings);
            log.WriteLine($"Removed the Roslynaut hook from {settingsFile}.");
        }
        else
            log.WriteLine($"There is no Roslynaut hook in {settingsFile}.");
    }

    // Whatever the case of its name, as Claude Code would run both.
    private static List<KeyValuePair<string, JsonNode?>> Servers(JsonObject servers) =>
        [.. servers.Where(entry => entry.Key.Equals(Name, StringComparison.OrdinalIgnoreCase))];

    // By the option alone, so the hook of a server that moved, or of an older version, counts too.
    private static bool Ours(JsonNode? node) => node?.ToJsonString().Contains(HookOption) == true;

    // Other hooks that share a group with ours stay.
    private static void RemoveHooks(JsonArray groups, List<JsonNode?> ours)
    {
        foreach (var old in ours)
        {
            var hooks = old!["hooks"]!.AsArray();
            hooks.RemoveAll(Ours);
            if (hooks.Count == 0)
                groups.Remove(old);
        }
    }

    /// <summary>The hook: Claude Code passes the tool call on stdin and takes context for Claude from stdout.</summary>
    public static int Hook()
    {
        if (Hint(JsonNode.Parse(Console.OpenStandardInput())) is { } hint)
            Console.Out.Write(hint);
        return 0;
    }

    internal static string? Hint(JsonNode? call) => Context(call) is { } context
        ? new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PreToolUse", ["additionalContext"] = context } }.ToJsonString()
        : null;

    /// <summary>
    /// The suggestion for a read, grep or shell tool call that touches C# code, in the shapes Claude Code (<c>tool_name</c> and
    /// <c>tool_input</c>) and OpenCode (<c>tool</c> and <c>input</c>) pass. Null when the call does not touch C# code.
    /// </summary>
    internal static string? Context(JsonNode? call)
    {
        var input = call?["tool_input"] ?? call?["input"];
        string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        string? Arg(string name) => Text(input?[name]);
        bool CSharp(string name) => Arg(name) is { } text && CSharpFile().IsMatch(text);
        // Claude Code's tool names are capitalized, OpenCode's are not.
        return (Text(call?["tool_name"]) ?? Text(call?["tool"]))?.ToLowerInvariant() switch
        {
            "read" when CSharp("file_path") || CSharp("filePath") => ReadHint,
            "grep" when Arg("type") == "cs" || CSharp("path") || CSharp("glob") || CSharp("include") => GrepHint,
            // ponytail: guesses from the command text which programs run on which files; parse the command line if the guesses miss too often.
            _ when CSharp("command") => SearchCommand().IsMatch(Arg("command")!) ? GrepHint : ReadCommand().IsMatch(Arg("command")!) ? ReadHint : null,
            _ => null,
        };
    }

    // Not .csproj or .cshtml. The type options are ripgrep's.
    [GeneratedRegex(@"\.cs\b|(?:-t ?|--type[= ])cs\b", RegexOptions.IgnoreCase)]
    private static partial Regex CSharpFile();

    // PowerShell's commands and aliases too, whose case does not matter.
    [GeneratedRegex(@"\b(?:e?grep|rg|ag|ack|Select-String|sls|findstr)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SearchCommand();

    [GeneratedRegex(@"\b(?:cat|head|tail|less|more|sed|awk|Get-Content|gc|type)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReadCommand();
}
