using System.Text.Json.Nodes;

namespace Roslynaut;

/// <summary>Registers the server with VS Code for all workspaces (<c>--install vscode</c>), in the user profile's <c>mcp.json</c>.</summary>
internal static class VsCodeInstall
{
    private static readonly string[] Servers = ["servers"];

    public static void Run(bool uninstall)
    {
        // ponytail: the default profile of VS Code stable only; add "Code - Insiders" and named profiles when someone asks.
        var appData = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); // %APPDATA%, or XDG_CONFIG_HOME or ~/.config
        var configFile = Path.Combine(appData, "Code", "User", "mcp.json");
        if (uninstall)
            AgentInstall.Unregister(configFile, Servers, Console.Out);
        else
            AgentInstall.Register(configFile, Servers, Server(AgentInstall.Command()), Console.Out);
    }

    internal static JsonObject Server(string[] command) =>
        new() { ["type"] = "stdio", ["command"] = command[0], ["args"] = JsonFile.Strings(command[1..]) };
}
