using System.Text.Json.Nodes;

namespace Roslynaut;

/// <summary>What the installers share: the command that starts this server, as the agent runs it, and its entry in a JSON configuration.</summary>
internal static class AgentInstall
{
    public const string Name = "roslynaut";

    /// <summary>
    /// The command that starts this server: its executable, or dotnet and the assembly when dotnet started it, or dotnet dnx and this version
    /// when that assembly is a package in the NuGet packages folder.
    /// </summary>
    public static string[] Command() => Command(Environment.ProcessPath!, typeof(AgentInstall).Assembly.Location);

    internal static string[] Command(string process, string assembly)
    {
        // Started as dotnet Roslynaut.dll, the process is dotnet itself.
        if (Path.GetFileNameWithoutExtension(process) != "dotnet")
            return [Portable(process)];

        // dnx (and dotnet tool run) runs the package from the NuGet packages folder, which clearing the NuGet caches empties: dnx downloads
        // the version again. dotnet dnx, as the dnx script is not on every shell's PATH; -- so dnx passes the hook options on.
        return PackageVersion(assembly) is { } version
            ? [Portable(process), "dnx", $"{Name}@{version}", "--yes", "--"]
            : [Portable(process), Portable(assembly)];
    }

    // A package's assembly is at <id>/<version>/tools/<framework>/<platform>/Roslynaut.dll, where the id is roslynaut or roslynaut.<platform>,
    // the package for one platform that dnx picks.
    private static string? PackageVersion(string assembly) =>
        assembly.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) is [.., var id, var version, "tools", _, _, _]
            && (id.Equals(Name, StringComparison.OrdinalIgnoreCase) || id.StartsWith(Name + ".", StringComparison.OrdinalIgnoreCase))
            ? version
            : null;

    // Agents can run the command through a shell, such as Claude Code's hooks through Git Bash on Windows, which reads a backslash as an
    // escape. Windows takes forward slashes as well.
    private static string Portable(string path) => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

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
