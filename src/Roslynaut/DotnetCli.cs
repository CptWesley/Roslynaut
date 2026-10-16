using System.Diagnostics;
using ModelContextProtocol;

namespace Roslynaut;

/// <summary>Runs the dotnet CLI of the installation that runs the server.</summary>
internal static class DotnetCli
{
    public static readonly string Executable = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));

    /// <summary>Returns the exit code and the combined standard output and error.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Output gets parsed, so keep it in English.
            Environment = { ["DOTNET_CLI_UI_LANGUAGE"] = "en", ["DOTNET_NOLOGO"] = "1" },
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var (output, error) = (process.StandardOutput.ReadToEndAsync(timeoutSource.Token), process.StandardError.ReadToEndAsync(timeoutSource.Token));
            await process.WaitForExitAsync(timeoutSource.Token);
            return (process.ExitCode, await output + await error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException($"dotnet {arguments[0]} did not finish within {timeout}.");
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
