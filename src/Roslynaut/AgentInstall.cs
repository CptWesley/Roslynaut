using System.Text.Json.Nodes;

namespace Roslynaut;

/// <summary>What the installers share: the command that starts this server, as the agent runs it, and its entry in a JSON configuration.</summary>
internal static class AgentInstall
{
    public const string Name = "roslynaut";

    /// <summary>The command that starts this server: its executable, or dotnet and the assembly when dotnet started it.</summary>
    public static string[] Command()
    {
        var process = Environment.ProcessPath!;
        // Started as dotnet Roslynaut.dll, the process is dotnet itself.
        return Path.GetFileNameWithoutExtension(process) == "dotnet" ? [process, typeof(AgentInstall).Assembly.Location] : [process];
    }

    /// <summary>
    /// Adds <paramref name="server"/> as the MCP server <see cref="Name"/> to the object at <paramref name="path"/> in a JSON configuration,
    /// replacing an earlier entry, and writes nothing when it is up to date.
    /// </summary>
    public static void Register(string configFile, string[] path, JsonObject server, TextWriter log)
    {
        var config = JsonFile.Read(configFile);
        var servers = path.Aggregate(config, (node, key) => node[key] ??= new JsonObject()).AsObject();
        var found = Servers(servers);
        if (found is [{ Key: Name, Value: var value }] && JsonNode.DeepEquals(value, server))
        {
            log.WriteLine($"The MCP server {Name} in {configFile} is up to date.");
            return;
        }

        foreach (var entry in found)
            servers.Remove(entry.Key);
        servers[Name] = server;
        JsonFile.Write(configFile, config);
        log.WriteLine($"{(found is [] ? "Added" : "Updated")} the MCP server {Name} in {configFile}.");
    }

    /// <summary>Removes what <see cref="Register"/> adds, by any version.</summary>
    public static void Unregister(string configFile, string[] path, TextWriter log)
    {
        var config = JsonFile.Read(configFile);
        if (path.Aggregate((JsonNode?)config, (node, key) => node?[key]) is JsonObject servers && Servers(servers) is [_, ..] found)
        {
            foreach (var entry in found)
                servers.Remove(entry.Key);
            JsonFile.Write(configFile, config);
            log.WriteLine($"Removed the MCP server {Name} from {configFile}.");
        }
        else
            log.WriteLine($"There is no MCP server {Name} in {configFile}.");
    }

    // Whatever the case of its name, as the agents would read both.
    private static List<KeyValuePair<string, JsonNode?>> Servers(JsonObject servers) =>
        [.. servers.Where(entry => entry.Key.Equals(Name, StringComparison.OrdinalIgnoreCase))];
}
