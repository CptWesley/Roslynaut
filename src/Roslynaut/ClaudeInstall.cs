using System.Text.Encodings.Web;
using System.Text.Json;
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

    private const string ReadHint =
        "This is C# code. The roslynaut MCP server can show less of it: document_symbols outlines a file, symbol_source returns one symbol's "
        + "source, and definition, references and callers navigate by symbol.";

    private const string GrepHint =
        "This searches C# code. The roslynaut MCP server knows symbols where text search does not: find_symbols and workspace_symbols find "
        + "declarations, references finds every use of a symbol, and search_pattern also searches generated code.";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Run()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // CLAUDE_CONFIG_DIR moves both files, as it does for Claude Code.
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : null;
        var process = Environment.ProcessPath!;
        // Started as dotnet Roslynaut.dll, the process is dotnet itself.
        string[] command = Path.GetFileNameWithoutExtension(process) == "dotnet" ? [process, typeof(ClaudeInstall).Assembly.Location] : [process];
        Install(Path.Combine(configDir ?? home, ".claude.json"), Path.Combine(configDir ?? Path.Combine(home, ".claude"), "settings.json"), command, Console.Out);
    }

    internal static void Install(string configFile, string settingsFile, string[] command, TextWriter log)
    {
        var config = Read(configFile);
        var servers = (config["mcpServers"] ??= new JsonObject()).AsObject();
        if (servers.Any(server => server.Key.Equals(Name, StringComparison.OrdinalIgnoreCase)))
            log.WriteLine($"Claude Code already has an MCP server named {Name} in {configFile}; left it as it is.");
        else
        {
            servers[Name] = new JsonObject { ["type"] = "stdio", ["command"] = command[0], ["args"] = Strings(command[1..]) };
            Write(configFile, config);
            log.WriteLine($"Added the MCP server {Name} to {configFile}.");
        }

        var settings = Read(settingsFile);
        var preToolUse = (settings["hooks"] ??= new JsonObject())["PreToolUse"] ??= new JsonArray();
        // By the option alone, so the hook of a server that moved, or one the user changed, counts too.
        if (preToolUse.ToJsonString().Contains(HookOption))
            log.WriteLine($"Claude Code already has the Roslynaut hook in {settingsFile}; left it as it is.");
        else
        {
            // Exec form (args), so no shell has to quote the path.
            var hook = new JsonObject { ["type"] = "command", ["command"] = command[0], ["args"] = Strings([.. command[1..], HookOption]) };
            preToolUse.AsArray().Add(new JsonObject { ["matcher"] = "Read|Grep", ["hooks"] = new JsonArray(hook) });
            Write(settingsFile, settings);
            log.WriteLine($"Added the Roslynaut hook, which suggests its tools when Claude reads or greps C# files, to {settingsFile}.");
        }
    }

    /// <summary>The hook: Claude Code passes the tool call on stdin and takes context for Claude from stdout.</summary>
    public static int Hook()
    {
        if (Hint(JsonNode.Parse(Console.OpenStandardInput())) is { } hint)
            Console.Out.Write(hint);
        return 0;
    }

    internal static string? Hint(JsonNode? call)
    {
        var input = call?["tool_input"];
        string? Text(string name) => input?[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        if (Text("type") != "cs" && !new[] { "file_path", "path", "glob" }.Any(name => Text(name) is { } text && CSharpFile().IsMatch(text)))
            return null;
        var context = (string?)call?["tool_name"] == "Read" ? ReadHint : GrepHint;
        return new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PreToolUse", ["additionalContext"] = context } }.ToJsonString();
    }

    // Not .csproj or .cshtml.
    [GeneratedRegex(@"\.cs\b", RegexOptions.IgnoreCase)]
    private static partial Regex CSharpFile();

    private static JsonArray Strings(string[] values) => new([.. values.Select(value => JsonValue.Create(value))]);

    // A file that is not JSON throws here, before anything is written.
    private static JsonNode Read(string path) => (File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null) ?? new JsonObject();

    // Through a temporary file, so a failed write cannot leave the configuration half written.
    private static void Write(string path, JsonNode json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", json.ToJsonString(Indented));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
