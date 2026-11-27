using System.Text.Json.Nodes;

namespace Roslynaut;

/// <summary>
/// Registers the server, for all projects, with the agents that only need an entry in a JSON configuration file
/// (<c>--install vscode</c> and the rest of <see cref="Agents"/>). None of them gets a hook.
/// </summary>
internal static class McpConfigInstall
{
    /// <param name="File">The user-level configuration file.</param>
    /// <param name="Servers">The keys of the object that holds the servers.</param>
    /// <param name="Server">The server's entry, from the command that starts it.</param>
    internal sealed record Agent(Func<string> File, string[] Servers, Func<string[], JsonObject> Server);

    internal static readonly Dictionary<string, Agent> Agents = new()
    {
        // ponytail: the default profile of VS Code stable only; add "Code - Insiders" and named profiles when someone asks.
        ["vscode"] = new(() => Path.Combine(AppData(), "Code", "User", "mcp.json"), ["servers"], Stdio),
        ["visualstudio"] = new(() => Home(".mcp.json"), ["servers"], Stdio),
        // COPILOT_HOME moves the Copilot CLI's configuration directory, as it does for the CLI itself.
        ["copilot"] = new(() => Path.Combine(Variable("COPILOT_HOME") ?? Home(".copilot"), "mcp-config.json"), ["mcpServers"], Copilot),
        ["cursor"] = new(() => Home(".cursor", "mcp.json"), ["mcpServers"], Plain),
        ["gemini"] = new(() => Home(".gemini", "settings.json"), ["mcpServers"], Plain),
        // Junie in JetBrains IDEs and the Junie CLI share it.
        ["junie"] = new(() => Home(".junie", "mcp", "mcp.json"), ["mcpServers"], Plain),
    };

    /// <param name="arguments">What the server starts with, after the command, such as the tools to serve.</param>
    public static void Run(string name, bool uninstall, string[] arguments)
    {
        var agent = Agents[name];
        if (uninstall)
            AgentInstall.Unregister(agent.File(), agent.Servers, Console.Out);
        else
            AgentInstall.Register(agent.File(), agent.Servers, agent.Server([.. AgentInstall.Command(), .. arguments]), Console.Out);
    }

    private static JsonObject Plain(string[] command) => new() { ["command"] = command[0], ["args"] = JsonFile.Strings(command[1..]) };

    private static JsonObject Stdio(string[] command) => new() { ["type"] = "stdio", ["command"] = command[0], ["args"] = JsonFile.Strings(command[1..]) };

    // The Copilot CLI serves none of a server's tools unless it lists them.
    private static JsonObject Copilot(string[] command) => new()
    {
        ["type"] = "local", ["command"] = command[0], ["args"] = JsonFile.Strings(command[1..]), ["tools"] = JsonFile.Strings(["*"]),
    };

    internal static string Home(params string[] path) =>
        Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), .. path]);

    internal static string? Variable(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    // %APPDATA% on Windows, XDG_CONFIG_HOME or ~/.config on Linux; .NET gives ~/.config on macOS too, where VS Code does not look.
    private static string AppData() => OperatingSystem.IsMacOS()
        ? Home("Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
}
