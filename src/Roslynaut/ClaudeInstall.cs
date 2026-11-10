using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Roslynaut;

/// <summary>
/// Registers the server with Claude Code for all projects (<c>--install claude</c>), along with hooks that point Claude to the server's tools:
/// when a session starts in a C# repository, when it reads, finds or greps C# files, after it edits one, and when it builds or tests. The hooks
/// only add context: they never block or approve a tool call.
/// </summary>
internal static partial class ClaudeInstall
{
    public const string HookOption = "--claude-hook";
    private const string Name = "roslynaut";

    // Each event's matcher. Claude Code's shells, and the shell tools of MCP servers, which name them in many ways but take a command too.
    // Anchored, as Claude Code reads a matcher with other characters than letters and | as an unanchored regex. SessionStart has none, so it
    // runs for every start: a new session, a resumed one, and after /clear or compaction, when Claude has forgotten the tools again.
    private static readonly (string Event, string? Matcher)[] Events =
    [
        ("PreToolUse", "^(Read|Grep|Glob|Bash|PowerShell|mcp__.*(shell|terminal|command).*)$"),
        ("PostToolUse", "^(Edit|Write|MultiEdit)$"),
        ("SessionStart", null),
    ];

    /// <summary>A suggestion of some of the server's tools, and the project to pass them, if one was found.</summary>
    private sealed record Suggestion(string Text, string[] Tools, string? Project = null)
    {
        public override string ToString() => Project is null ? Text : $"{Text} Pass projectPath \"{Project}\".";
    }

    private static readonly Suggestion ReadHint = new(
        "This is C# code. The roslynaut MCP server can show less of it: document_symbols outlines a file, symbol_source returns one symbol's "
        + "source, and definition, references and callers navigate by symbol.",
        ["document_symbols", "symbol_source", "definition", "references", "callers"]);

    private static readonly Suggestion GrepHint = new(
        "This searches C# code. The roslynaut MCP server knows symbols where text search does not: find_symbols and workspace_symbols find "
        + "declarations, references finds every use of a symbol, and search_pattern also searches generated code.",
        ["find_symbols", "workspace_symbols", "references", "search_pattern"]);

    private static readonly Suggestion GlobHint = new(
        "This looks for C# files. The roslynaut MCP server finds code by symbol instead: workspace_symbols finds a declaration by name, "
        + "find_symbols by regex and structure (attribute, base type, modifiers), and document_symbols outlines a file.",
        ["workspace_symbols", "find_symbols", "document_symbols"]);

    private static readonly Suggestion EditHint = new(
        "This edited C# code. The roslynaut MCP server's diagnostics, with this filePath, gives the compiler's and analyzers' errors without "
        + "building; replace_symbol_body and rename_symbol edit by symbol instead of by text.",
        ["diagnostics", "replace_symbol_body", "rename_symbol"]);

    private static readonly Suggestion BuildHint = new(
        "The roslynaut MCP server's diagnostics gives compiler and analyzer errors without building, the quick check after an edit; its build "
        + "keeps a binary log (binlogPath) that build_problems and the other build_ tools explain.",
        ["diagnostics", "build", "build_problems"]);

    private static readonly Suggestion TestHint = new(
        "The roslynaut MCP server's affected_tests finds the tests a change can break, with a Filter for its run_tests, which can also add "
        + "coverage, repeat flaky tests and dump hangs.",
        ["affected_tests", "run_tests"]);

    private static readonly Suggestion SessionHint = new(
        "This is a C# repository. The roslynaut MCP server sees its code as the compiler does: prefer document_symbols, symbol_source, "
        + "find_symbols, workspace_symbols, references and callers to reading and grepping .cs files, and diagnostics to building after an edit.",
        ["document_symbols", "symbol_source", "find_symbols", "workspace_symbols", "references", "callers", "diagnostics"]);

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
        var events = (settings["hooks"] ??= new JsonObject()).AsObject();
        var (upToDate, installed) = (true, false);
        foreach (var (name, matcher) in Events)
        {
            // Exec form (args), so no shell has to quote the path.
            var hook = new JsonObject { ["type"] = "command", ["command"] = command[0], ["args"] = JsonFile.Strings([.. command[1..], HookOption]) };
            var group = matcher is null ? new JsonObject { ["hooks"] = new JsonArray(hook) } : new JsonObject { ["matcher"] = matcher, ["hooks"] = new JsonArray(hook) };
            var groups = (events[name] ??= new JsonArray()).AsArray();
            var ours = groups.Where(Ours).ToList();
            installed |= ours is not [];
            if (ours is [var existing] && JsonNode.DeepEquals(existing, group))
                continue;
            upToDate = false;
            RemoveHooks(groups, ours);
            groups.Add(group);
        }

        if (upToDate)
            log.WriteLine($"The Roslynaut hooks in {settingsFile} are up to date.");
        else
        {
            JsonFile.Write(settingsFile, settings);
            log.WriteLine(
                $"{(installed ? "Updated" : "Added")} the Roslynaut hooks, which suggest its tools when Claude starts in a C# repository, reads, "
                + $"searches or edits C# files, or builds and tests, in {settingsFile}.");
        }
    }

    /// <summary>
    /// Removes what <see cref="Install"/> adds, by any version, before the tool is uninstalled: else Claude Code fails to start the hooks on
    /// every read and grep.
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
        var removed = false;
        // From any event, as an older version may have used others.
        if (settings["hooks"] is JsonObject events)
            foreach (var (name, node) in events.ToList())
                if (node is JsonArray groups && groups.Where(Ours).ToList() is [_, ..] ours)
                {
                    RemoveHooks(groups, ours);
                    if (groups.Count == 0)
                        events.Remove(name);
                    removed = true;
                }

        if (removed)
        {
            JsonFile.Write(settingsFile, settings);
            log.WriteLine($"Removed the Roslynaut hooks from {settingsFile}.");
        }
        else
            log.WriteLine($"There are no Roslynaut hooks in {settingsFile}.");
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

    /// <summary>The hooks: Claude Code passes the event on stdin and takes context for Claude from stdout.</summary>
    public static int Hook()
    {
        if (Hint(JsonNode.Parse(Console.OpenStandardInput())) is { } hint)
            Console.Out.Write(hint);
        return 0;
    }

    /// <summary>
    /// The context for an event that Claude Code passes. It names the tools in full, as Claude Code may defer their schemas, and then Claude only
    /// sees their names until it loads them.
    /// </summary>
    internal static string? Hint(JsonNode? call)
    {
        if (Suggest(call) is not { } suggestion)
            return null;
        var load = $" If their schemas are not loaded, load them with ToolSearch: \"select:{string.Join(",", suggestion.Tools.Select(tool => $"mcp__{Name}__{tool}"))}\".";
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = Text(call?["hook_event_name"]) ?? "PreToolUse", ["additionalContext"] = suggestion + load },
        }.ToJsonString();
    }

    /// <summary>
    /// The suggestion for a session's start in a C# repository, or for a read, find, grep, edit or shell tool call that touches C# code or runs
    /// <c>dotnet build</c> or <c>test</c>, in the shapes Claude Code (<c>tool_name</c> and <c>tool_input</c>) and OpenCode (<c>tool</c> and
    /// <c>input</c>) pass. Null otherwise.
    /// </summary>
    internal static string? Context(JsonNode? call) => Suggest(call)?.ToString();

    private static Suggestion? Suggest(JsonNode? call)
    {
        var input = call?["tool_input"] ?? call?["input"];
        string? Arg(string name) => Text(input?[name]);
        bool CSharp(string name) => Arg(name) is { } text && CSharpFile().IsMatch(text);
        var file = Arg("file_path") ?? Arg("filePath");
        // Claude Code's tool names are capitalized, OpenCode's are not.
        var tool = Text(call?["hook_event_name"]) == "SessionStart" ? "" : (Text(call?["tool_name"]) ?? Text(call?["tool"]))?.ToLowerInvariant();
        var (suggestion, from) = tool switch
        {
            "" => (SessionHint, null),
            "read" when CSharp("file_path") || CSharp("filePath") => (ReadHint, file),
            "edit" or "write" or "multiedit" when CSharp("file_path") || CSharp("filePath") => (EditHint, file),
            "grep" when Arg("type") == "cs" || CSharp("path") || CSharp("glob") || CSharp("include") => (GrepHint, Arg("path")),
            "glob" when CSharp("pattern") => (GlobHint, Arg("path")),
            _ when Arg("command") is { } command => (Command(command), null),
            _ => ((Suggestion?)null, (string?)null),
        };
        if (suggestion is null)
            return null;
        var project = Project(from, Text(call?["cwd"]) ?? Environment.CurrentDirectory);
        // Not a C# repository, as far as the hook can tell.
        if (suggestion == SessionHint && project is null)
            return null;
        return suggestion with { Project = project };
    }

    // ponytail: guesses from the command text which programs run on which files; parse the command line if the guesses miss too often.
    private static Suggestion? Command(string command) =>
        DotnetCommand().Match(command) is { Success: true } dotnet ? (dotnet.Groups[1].Value.Equals("test", StringComparison.OrdinalIgnoreCase) ? TestHint : BuildHint)
        : !CSharpFile().IsMatch(command) ? null
        : SearchCommand().IsMatch(command) ? GrepHint
        : ReadCommand().IsMatch(command) ? ReadHint
        : null;

    /// <summary>
    /// The project to pass the tools: the solution nearest above <paramref name="from"/> (a file or directory, else the working directory),
    /// else the shallowest one below the working directory, else a project file found the same way.
    /// </summary>
    // ponytail: the search below goes two levels deep, so a session started in a home directory may find someone's solution; bound it by the
    // repository if that misleads.
    internal static string? Project(string? from, string cwd)
    {
        try
        {
            cwd = Path.GetFullPath(cwd);
            var start = Path.GetFullPath(from ?? ".", cwd);
            string? project = null;
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                // A file, or a path that does not exist yet, such as a new file's.
                if (!dir.Exists)
                    continue;
                foreach (var path in dir.EnumerateFiles().Select(file => file.FullName).Order(StringComparer.OrdinalIgnoreCase))
                    switch (Rank(path))
                    {
                        case 0: return path;
                        case 1: project ??= path; break;
                    }

                // The repository's root ends the search, so a solution elsewhere in the user's directories does not count.
                if (Path.Exists(Path.Combine(dir.FullName, ".git")))
                    break;
            }

            var below = Directory.EnumerateFiles(cwd, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 })
                .Where(path => Rank(path) >= 0)
                .OrderBy(Rank).ThenBy(path => path.Count(c => c == Path.DirectorySeparatorChar)).ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            return below is not null && (Rank(below) == 0 || project is null) ? below : project;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A hook that fails would fail on every call it matches.
            return null;
        }
    }

    private static int Rank(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".slnx" or ".sln" => 0,
        ".csproj" => 1,
        _ => -1,
    };

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    // Not .csproj or .cshtml. The type options are ripgrep's.
    [GeneratedRegex(@"\.cs\b|(?:-t ?|--type[= ])cs\b", RegexOptions.IgnoreCase)]
    private static partial Regex CSharpFile();

    [GeneratedRegex(@"\bdotnet\s+(build|test)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DotnetCommand();

    // PowerShell's commands and aliases too, whose case does not matter.
    [GeneratedRegex(@"\b(?:e?grep|rg|ag|ack|Select-String|sls|findstr)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SearchCommand();

    [GeneratedRegex(@"\b(?:cat|head|tail|less|more|sed|awk|Get-Content|gc|type)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReadCommand();
}
