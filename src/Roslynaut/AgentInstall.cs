namespace Roslynaut;

/// <summary>What the installers share: the command that starts this server, as the agent runs it.</summary>
internal static class AgentInstall
{
    /// <summary>The command that starts this server: its executable, or dotnet and the assembly when dotnet started it.</summary>
    public static string[] Command()
    {
        var process = Environment.ProcessPath!;
        // Started as dotnet Roslynaut.dll, the process is dotnet itself.
        return Path.GetFileNameWithoutExtension(process) == "dotnet" ? [process, typeof(AgentInstall).Assembly.Location] : [process];
    }
}
