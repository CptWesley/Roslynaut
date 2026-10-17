using System.Diagnostics;
using System.Text;
using ModelContextProtocol;

namespace Roslynaut;

/// <summary>Runs the dotnet CLI of the installation that runs the server.</summary>
internal static class DotnetCli
{
    public static readonly string Executable = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));

    /// <summary>
    /// What the dotnet CLI, MSBuild and git write when their output is redirected, and what the helper hosts read and write; without a byte
    /// order mark, which would start standard input. Windows otherwise reads child processes in the console's code page, such as 850.
    /// </summary>
    public static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Returns the exit code and the combined standard output and error.</summary>
    /// <param name="input">What to write to standard input, if anything.</param>
    /// <param name="workingDirectory">
    /// Where to run, such as the directory of the project or solution: dotnet reads global.json, which picks the SDK and the test runner, from there.
    /// </param>
    public static Task<(int ExitCode, string Output)> RunAsync(
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, string? input = null, string? workingDirectory = null) =>
        RunAsync(Executable, arguments, timeout, cancellationToken, input, workingDirectory);

    /// <summary>Runs another executable, such as git, the same way.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, string? input = null, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory ?? "",

            // Not the server's own standard input, which carries the protocol over stdio: a process could read it, and Git for Windows
            // blocks on it while the server waits to read it.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,

            // Output gets parsed, so keep it in English.
            Environment = { ["DOTNET_CLI_UI_LANGUAGE"] = "en", ["DOTNET_NOLOGO"] = "1" },
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Start(start);
        if (input is not null)
            await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var (output, error) = (new StringBuilder(), new StringBuilder());
            var reading = Task.WhenAll(CopyAsync(process.StandardOutput, output), CopyAsync(process.StandardError, error));
            await process.WaitForExitAsync(timeoutSource.Token);

            // A process it starts can inherit the pipes and keep them open after it exits, such as the MSBuild nodes that dotnet build keeps for
            // reuse; what it wrote itself is in the pipes by then.
            await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(2), timeoutSource.Token));
            lock (output)
            lock (error)
                return (process.ExitCode, output.ToString() + error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException($"{Path.GetFileNameWithoutExtension(executable)} {arguments[0]} did not finish within {timeout}.");
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }

        static async Task CopyAsync(StreamReader reader, StringBuilder text)
        {
            var buffer = new char[4096];
            for (int read; (read = await reader.ReadAsync(buffer)) > 0;)
            {
                lock (text)
                    text.Append(buffer, 0, read);
            }
        }

        // An executable that is not on the PATH.
        static Process Start(ProcessStartInfo start)
        {
            try
            {
                return Process.Start(start)!;
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                throw new McpException($"Cannot run {start.FileName}: {e.Message}");
            }
        }
    }
}
