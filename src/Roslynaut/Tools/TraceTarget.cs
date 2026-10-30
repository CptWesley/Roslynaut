using System.Diagnostics;
using System.Security;
using Microsoft.Diagnostics.NETCore.Client;
using ModelContextProtocol;

namespace Roslynaut.Tools;

/// <summary>
/// What the tracing tools trace: a running process, or a program they start, whose runtime waits at startup until the trace begins, so the
/// trace starts at its first instruction. A test project runs its tests, and the trace is of the process that runs them.
/// </summary>
public sealed record TraceTarget(int? ProcessId, TraceTarget.Launch? Program = null)
{
    public static implicit operator TraceTarget(int processId) => new(processId);

    /// <param name="Path">A .dll to run, or a project to build and run; a test project runs its tests.</param>
    /// <param name="TestFilter">For a test project, which tests to run, as run_tests takes a filter.</param>
    /// <param name="Workspace">Gives the project's output and test framework.</param>
    public sealed record Launch(string Path, IReadOnlyList<string> Arguments, string? TestFilter, WorkspaceService Workspace);

    public const string LaunchDescription = "Instead of processId: a .dll to start, or a project to build and start, whose runtime waits at startup "
        + "until the trace begins, so it covers the program from its first instruction, startup included, until it exits or seconds pass, when it "
        + "is ended. For a test project, its tests run (testFilter picks them) and the trace is of the process that runs them.";

    public const string ArgumentsDescription = "With launch: the program's command-line arguments, or more arguments for the test application.";

    public const string TestFilterDescription = "With launch of a test project: the tests to run, as run_tests' filter takes them.";

    /// <summary>The target the tools' parameters name: a process id, or else a program to launch.</summary>
    public static TraceTarget Of(int? processId, string? launch, IReadOnlyList<string>? arguments, string? testFilter, WorkspaceService workspace) => (processId, launch) switch
    {
        ({ } id, null) => new(id),
        (null, { } path) => new(null, new(path, arguments ?? [], testFilter, workspace)),
        _ => throw new McpException("Pass either a processId or a program to launch."),
    };

    /// <summary>
    /// Starts the program with its runtime pointed at a diagnostic port the server listens on, and waits for that runtime to connect. It stays
    /// paused until <see cref="Started.Client"/> resumes it. Building comes first; a test project under VSTest runs through dotnet test, which
    /// passes the port to the test host alone through runsettings, as the CLI and MSBuild are .NET processes too.
    /// </summary>
    internal static async Task<Started> StartAsync(Launch program, CancellationToken cancellationToken)
    {
        var port = OperatingSystem.IsWindows() ? $"roslynaut-{Guid.NewGuid():N}" : System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"roslynaut-{Guid.NewGuid():N}.sock");
        var scratch = Directory.CreateTempSubdirectory("roslynaut-launch-");
        try
        {
            var start = await StartInfoAsync(program, port, scratch.FullName, cancellationToken);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromMinutes(5));
            var connecting = DiagnosticsClientConnector.FromDiagnosticPort(port, connectTimeout.Token);

            var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var process = new Process { StartInfo = start };
            void Keep(object sender, DataReceivedEventArgs line)
            {
                if (line.Data is null)
                    return;
                output.Enqueue(line.Data);
                while (output.Count > 50)
                    output.TryDequeue(out _);
            }

            process.OutputDataReceived += Keep;
            process.ErrorDataReceived += Keep;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                var exited = process.WaitForExitAsync(connectTimeout.Token);
                if (await Task.WhenAny(connecting, exited) != connecting)
                    throw new McpException($"The program ended before its runtime started, for instance because no test matched:{Environment.NewLine}{string.Join(Environment.NewLine, output)}");

                return new(process, await connecting, scratch);
            }
            catch
            {
                // Stops the server listening on the port, unless a runtime connected meanwhile, whose connection goes too.
                Started.Kill(process);
                await connectTimeout.CancelAsync();
                _ = connecting.ContinueWith(connector => connector.Result.DisposeAsync(), TaskContinuationOptions.OnlyOnRanToCompletion);
                throw;
            }
        }
        catch
        {
            scratch.Delete(recursive: true);
            throw;
        }
    }

    private static async Task<ProcessStartInfo> StartInfoAsync(Launch program, string port, string scratch, CancellationToken cancellationToken)
    {
        var path = System.IO.Path.GetFullPath(program.Path);
        if (!File.Exists(path))
            throw new McpException($"Program or project not found: {path}");

        var start = new ProcessStartInfo(DotnetCli.Executable)
        {
            // Its input stays open, so a program that reads it waits rather than sees it end.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = DotnetCli.Utf8,
            StandardErrorEncoding = DotnetCli.Utf8,
            Environment = { ["DOTNET_CLI_UI_LANGUAGE"] = "en", ["DOTNET_NOLOGO"] = "1" },
        };
        DotnetCli.ForgetParentSdk(start);

        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return Exec(start, path, program.Arguments, port);

        var build = await BuildTool.BuildAsync(path, cancellationToken: cancellationToken);
        if (!build.Succeeded)
            throw new McpException($"The build failed: {string.Join("; ", build.Messages.Where(message => message.Severity == "Error").Take(5).Select(message => $"{message.Code} {message.Message}"))}{build.Output}");

        var result = (await program.Workspace.GetAsync(path)).Results.FirstOrDefault(result => string.Equals(result.ProjectFilePath, path, StringComparison.OrdinalIgnoreCase));
        var target = result?.GetProperty("TargetPath") is { Length: > 0 } built ? built : throw new McpException($"{path} has no TargetPath to run.");
        if (program.TestFilter is null && !string.Equals(result.GetProperty("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase))
            return Exec(start, target, program.Arguments, port);

        // Microsoft.Testing.Platform's test application is the program; it takes the options dotnet test would pass it.
        if (RunTestsTool.UsesTestingPlatform(path))
        {
            var filter = program.TestFilter is null ? [] : RunTestsTool.FilterArguments(program.TestFilter, await RunTestsTool.FilterStyleAsync(program.Workspace, path));
            return Exec(start, target, [.. filter, .. program.Arguments], port);
        }

        var settings = System.IO.Path.Combine(scratch, "trace.runsettings");
        File.WriteAllText(settings, $"""
            <RunSettings>
              <RunConfiguration>
                <EnvironmentVariables>
                  <DOTNET_DiagnosticPorts>{SecurityElement.Escape(port)}</DOTNET_DiagnosticPorts>
                </EnvironmentVariables>
              </RunConfiguration>
            </RunSettings>
            """);
        start.WorkingDirectory = System.IO.Path.GetDirectoryName(path)!;
        foreach (var argument in (string[])["test", path, "--no-build", "-v:q", "--settings", settings, .. program.TestFilter is null ? [] : (string[])["--filter", program.TestFilter], .. program.Arguments])
            start.ArgumentList.Add(argument);
        return start;
    }

    private static ProcessStartInfo Exec(ProcessStartInfo start, string program, IReadOnlyList<string> arguments, string port)
    {
        start.WorkingDirectory = System.IO.Path.GetDirectoryName(program)!;
        start.Environment["DOTNET_DiagnosticPorts"] = port;
        foreach (var argument in (string[])["exec", program, .. arguments])
            start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>A started program, paused until its runtime is resumed; disposing it ends the program.</summary>
    internal sealed class Started(Process process, DiagnosticsClientConnector connector, DirectoryInfo scratch) : IAsyncDisposable
    {
        public DiagnosticsClient Client => connector.Instance;

        public Task Exited => process.WaitForExitAsync();

        public async ValueTask DisposeAsync()
        {
            Kill(process);
            await connector.DisposeAsync();
            try
            {
                scratch.Delete(recursive: true);
            }
            catch (IOException)
            {
                // The test host may still hold the runsettings for a moment.
            }
        }

        public static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited meanwhile.
            }

            process.Dispose();
        }
    }
}
