using System.Text.Json.Nodes;

namespace Roslynaut;

/// <summary>Registers the server with the GitHub Copilot CLI for all projects (<c>--install copilot</c>), in its <c>mcp-config.json</c>.</summary>
internal static class CopilotInstall
{
    private static readonly string[] Servers = ["mcpServers"];

    public static void Run(bool uninstall)
    {
        // COPILOT_HOME moves the Copilot CLI's configuration directory, as it does for the CLI itself.
        var configDir = Environment.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
        var configFile = Path.Combine(configDir, "mcp-config.json");
        if (uninstall)
            AgentInstall.Unregister(configFile, Servers, Console.Out);
        else
            AgentInstall.Register(configFile, Servers, Server(AgentInstall.Command()), Console.Out);
    }

    // The CLI serves none of a server's tools unless it lists them.
    internal static JsonObject Server(string[] command) => new()
    {
        ["type"] = "local", ["command"] = command[0], ["args"] = JsonFile.Strings(command[1..]), ["tools"] = JsonFile.Strings(["*"]),
    };
}
