using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Diagnostics.NETCore.Client;
using ModelContextProtocol;

namespace Roslynaut.Debugging;

[JsonConverter(typeof(JsonStringEnumConverter<ResumeAction>))]
public enum ResumeAction
{
    Continue,
    StepOver,
    StepInto,
    StepOut,
    Pause,
}

/// <param name="FilePath">With Line: break at that line of this file.</param>
/// <param name="Condition">Break only when this C# expression is true, such as 'i == 3'.</param>
/// <param name="Function">Or else: break where this method starts, such as 'MyApp.Orders.Submit' or 'Submit'.</param>
public sealed record Breakpoint(string? FilePath = null, int Line = 0, string? Condition = null, string? Function = null);

/// <param name="Verified">Whether the debugger could bind the breakpoint to code; Message says why not.</param>
public sealed record BreakpointState(string? FilePath, int Line, bool Verified, string? Message, string? Function = null);

/// <param name="FilePath">The source file, by the path the PDB records.</param>
/// <param name="Source">
/// For a frame in library code, whose FilePath is not on disk, such as "/_/src/Lib/Parser.cs": a local copy of its source, from the PDB or
/// through SourceLink. Breakpoints still take FilePath.
/// </param>
public sealed record Frame(string Name, string? FilePath, int Line, SourceDocument? Source = null);

/// <param name="Reference">Pass to debug_inspect to see what the value contains; 0 if nothing.</param>
public sealed record Variable(string Name, string Value, string? Type, int Reference);

/// <param name="Status">Stopped, Running (still, after the wait) or Exited.</param>
/// <param name="Reason">Why it stopped: breakpoint, step, exception, pause or entry.</param>
/// <param name="Output">What the program wrote since the previous state.</param>
/// <param name="Exception">When it stopped at an exception: its type and message.</param>
public sealed record DebugState(
    string Status, string? Reason, string? Description, IReadOnlyList<Frame> Stack, IReadOnlyList<Variable> Locals, int? ExitCode, string Output,
    string? Exception = null);

/// <summary>
/// One debugging session at a time, with netcoredbg over the Debug Adapter Protocol: launches a program, attaches to a process or a test
/// host, reports where it stops, resumes and steps it, and inspects its variables.
/// </summary>
public sealed partial class DebugService(WorkspaceService workspace, PdbService pdbs) : IAsyncDisposable
{
    public const string AdapterVariable = "NETCOREDBG";

    private const int MaxFrames = 20;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Session? _session;

    public Task<DebugState> StartAsync(
        string programPath, IReadOnlyList<string> arguments, IReadOnlyList<Breakpoint> breakpoints, bool stopAtEntry, IReadOnlyList<string>? stopOnExceptions,
        TimeSpan wait, CancellationToken cancellationToken) =>
        LockedAsync(async () =>
        {
            await EndAsync();
            var adapter = FindAdapter();
            var program = await ProgramOfAsync(programPath, cancellationToken);
            var launch = new
            {
                program = DotnetCli.Executable,
                args = (string[])[program, .. arguments],
                cwd = Path.GetDirectoryName(program),
                stopAtEntry,
                console = "internalConsole",
            };
            return await BeginAsync(adapter, "launch", launch, new Session.Options(), breakpoints, stopOnExceptions, wait, cancellationToken);
        });

    /// <summary>Attaches to a running .NET process; ending the session detaches and leaves it running.</summary>
    public Task<DebugState> AttachAsync(int processId, IReadOnlyList<Breakpoint> breakpoints, IReadOnlyList<string>? stopOnExceptions, TimeSpan wait, CancellationToken cancellationToken) =>
        LockedAsync(async () =>
        {
            await EndAsync();
            return await BeginAsync(FindAdapter(), "attach", new { processId }, new Session.Options(Detach: true), breakpoints, stopOnExceptions, wait, cancellationToken);
        });

    /// <summary>
    /// Runs tests with dotnet test and VSTEST_HOST_DEBUG, which makes the test host wait for a debugger, and attaches to it. The session's output
    /// includes dotnet test's, with the test results once the test host exits. Ending the session ends the test run.
    /// </summary>
    public Task<DebugState> TestAsync(
        string projectPath, string? filter, IReadOnlyList<Breakpoint> breakpoints, IReadOnlyList<string>? stopOnExceptions, TimeSpan wait, CancellationToken cancellationToken) =>
        LockedAsync(async () =>
        {
            await EndAsync();
            var adapter = FindAdapter();
            var (run, output, processId) = await StartTestHostAsync(projectPath, filter, cancellationToken);
            return await BeginAsync(adapter, "attach", new { processId }, new Session.Options(Run: run, Output: output), breakpoints, stopOnExceptions, wait, cancellationToken);
        });

    public Task<IReadOnlyList<BreakpointState>> SetBreakpointsAsync(string filePath, IReadOnlyList<Breakpoint> breakpoints, CancellationToken cancellationToken) =>
        LockedAsync(() => SetBreakpointsAsync(Current, Path.GetFullPath(filePath), breakpoints, cancellationToken));

    /// <summary>Replaces all the breakpoints at methods.</summary>
    public Task<IReadOnlyList<BreakpointState>> SetFunctionBreakpointsAsync(IReadOnlyList<Breakpoint> breakpoints, CancellationToken cancellationToken) =>
        LockedAsync(() => SetFunctionBreakpointsAsync(Current, breakpoints, cancellationToken));

    /// <summary>Sets a variable, field or property of the stopped program, by evaluating the value in a frame.</summary>
    public Task<Variable> SetAsync(string expression, string value, int frame, CancellationToken cancellationToken) =>
        LockedAsync(async () =>
        {
            var session = Current;
            if (!session.IsStopped)
                throw new McpException("The program is running; set values once it stops, or pause it with debug_resume.");

            var result = await session.Client.RequestAsync("setExpression", new { expression, value, frameId = await session.FrameIdAsync(frame, cancellationToken) }, cancellationToken);
            return new Variable(expression, DapClient.Text(result, "value") ?? "", DapClient.Text(result, "type"), Reference(result));
        });

    public Task<DebugState> ResumeAsync(ResumeAction action, TimeSpan wait, CancellationToken cancellationToken) =>
        LockedAsync(async () =>
        {
            var session = Current;
            if (action == ResumeAction.Pause && !session.IsStopped)
            {
                await session.Client.RequestAsync("pause", new { threadId = session.ThreadId ?? 0 }, cancellationToken);
            }
            else if (action != ResumeAction.Pause && session.IsStopped)
            {
                var command = action switch { ResumeAction.StepOver => "next", ResumeAction.StepInto => "stepIn", ResumeAction.StepOut => "stepOut", _ => "continue" };
                session.Resumed();
                await session.Client.RequestAsync(command, new { threadId = session.ThreadId }, cancellationToken);
            }

            return await session.WaitAsync(wait, cancellationToken);
        });

    /// <summary>Evaluates an expression in a frame; or else lists what a variable reference contains, or the locals of a frame.</summary>
    public Task<IReadOnlyList<Variable>> InspectAsync(string? expression, int frame, int? reference, CancellationToken cancellationToken) =>
        LockedAsync(async () =>
        {
            var session = Current;
            if (!session.IsStopped)
                throw new McpException("The program is running; inspect it once it stops, or pause it with debug_resume.");
            if (reference is not null)
                return await session.VariablesAsync(reference.Value, cancellationToken);

            var frameId = await session.FrameIdAsync(frame, cancellationToken);
            if (expression is null)
                return await session.LocalsAsync(frameId, cancellationToken);

            var result = await session.Client.RequestAsync("evaluate", new { expression, frameId, context = "watch" }, cancellationToken);
            return [new Variable(expression, DapClient.Text(result, "result") ?? "", DapClient.Text(result, "type"), Reference(result))];
        });

    public Task StopAsync() => LockedAsync(async () => { await EndAsync(); return true; });

    public async ValueTask DisposeAsync()
    {
        await EndAsync();
        _lock.Dispose();
    }

    private Session Current => _session ?? throw new McpException("No program is being debugged; start one with debug_start, debug_attach or debug_test.");

    /// <param name="request">launch or attach, with its arguments.</param>
    private async Task<DebugState> BeginAsync(
        string adapter, string request, object arguments, Session.Options options, IReadOnlyList<Breakpoint> breakpoints, IReadOnlyList<string>? stopOnExceptions,
        TimeSpan wait, CancellationToken cancellationToken)
    {
        Session session;
        try
        {
            session = _session = new Session(new DapClient(adapter, "--interpreter=vscode"), pdbs, options);
        }
        catch
        {
            if (options.Run is { } run)
                Kill(run);
            throw;
        }

        // A session that fails to start ends at once, rather than linger for the next request.
        try
        {
            var client = session.Client;
            await client.RequestAsync("initialize", new { clientID = "roslynaut", adapterID = "coreclr", linesStartAt1 = true, columnsStartAt1 = true, pathFormat = "path" }, cancellationToken);
            var start = client.RequestAsync(request, arguments, cancellationToken);
            await session.Initialized.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

            foreach (var file in breakpoints.Where(breakpoint => breakpoint.Function is null).GroupBy(breakpoint => Path.GetFullPath(breakpoint.FilePath
                ?? throw new McpException("A breakpoint needs a FilePath and a Line, or a Function.")), StringComparer.OrdinalIgnoreCase))
                await SetBreakpointsAsync(session, file.Key, [.. file], cancellationToken);
            if (breakpoints.Where(breakpoint => breakpoint.Function is not null).ToList() is { Count: > 0 } functions)
                await SetFunctionBreakpointsAsync(session, functions, cancellationToken);

            // Unhandled exceptions always stop; others stop where thrown when asked for, by type or all of them.
            await client.RequestAsync("setExceptionBreakpoints", stopOnExceptions switch
            {
                null or [] => new { filters = new[] { "user-unhandled" } },
                ["*"] => new { filters = new[] { "all" } },
                _ => (object)new { filters = new[] { "user-unhandled" }, filterOptions = new[] { new { filterId = "all", condition = string.Join(", ", stopOnExceptions) } } },
            }, cancellationToken);
            await client.RequestAsync("configurationDone", null, cancellationToken);
            await start;
        }
        catch
        {
            await EndAsync();
            throw;
        }

        return await session.WaitAsync(wait, cancellationToken);
    }

    /// <summary>Starts dotnet test with VSTEST_HOST_DEBUG, and waits for the id of the test host it starts, which waits for a debugger.</summary>
    /// <summary>
    /// Under Microsoft.Testing.Platform, the test application waits for a debugger with TESTINGPLATFORM_WAIT_ATTACH_DEBUGGER, but dotnet test does
    /// not pass on what it writes; so it is found by its name instead.
    /// </summary>
    private async Task<(Process Run, StringBuilder Output, int ProcessId)> StartTestHostAsync(string projectPath, string? filter, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(projectPath);
        if (!File.Exists(path))
            throw new McpException($"Project not found: {path}");

        var platform = Tools.RunTestsTool.UsesTestingPlatform(path);
        IReadOnlyList<string> arguments = platform
            ? ["test", "--project", path, .. filter is null ? [] : Tools.RunTestsTool.FilterArguments(filter, await Tools.RunTestsTool.FilterStyleAsync(workspace, path))]
            : ["test", path, "-v:q", .. filter is null ? (string[])[] : ["--filter", filter]];
        var application = platform
            ? (await workspace.GetAsync(path)).Results.FirstOrDefault(result => string.Equals(result.ProjectFilePath, path, StringComparison.OrdinalIgnoreCase))?.GetProperty("AssemblyName")
                ?? throw new McpException($"{path} has no AssemblyName to find its test application by.")
            : null;

        var start = new ProcessStartInfo(DotnetCli.Executable)
        {
            // dotnet reads global.json from the working directory.
            WorkingDirectory = Path.GetDirectoryName(path)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment =
            {
                ["VSTEST_HOST_DEBUG"] = "1", ["TESTINGPLATFORM_WAIT_ATTACH_DEBUGGER"] = "1", ["DOTNET_CLI_UI_LANGUAGE"] = "en", ["DOTNET_NOLOGO"] = "1",
            },
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        var output = new StringBuilder();
        var processId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Process.Start(start)!;
        var started = run.StartTime;
        using var stopSearch = new CancellationTokenSource();
        if (application is not null)
            _ = FindApplicationAsync(application, started, processId, stopSearch.Token);
        DataReceivedEventHandler received = (_, line) =>
        {
            if (line.Data is null)
                return;

            lock (output)
                output.AppendLine(line.Data);
            if (TestHostId().Match(line.Data) is { Success: true } match)
                processId.TrySetResult(int.Parse(match.Groups[1].ValueSpan));
        };
        run.OutputDataReceived += received;
        run.ErrorDataReceived += received;
        run.BeginOutputReadLine();
        run.BeginErrorReadLine();

        try
        {
            // The build comes first.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            if (await Task.WhenAny(processId.Task, run.WaitForExitAsync(timeout.Token)) != processId.Task)
            {
                lock (output)
                    throw new McpException($"dotnet test started no test host to debug, for instance because the build failed or no test matched:{Environment.NewLine}{Tools.BuildTool.Tail(output.ToString())}");
            }

            return (run, output, await processId.Task);
        }
        catch
        {
            Kill(run);
            throw;
        }
        finally
        {
            await stopSearch.CancelAsync();
        }
    }

    /// <summary>
    /// The test application dotnet test starts: a process by the name of the test project's app host, started after dotnet test, whose runtime
    /// is up to attach to.
    /// </summary>
    // ponytail: by name, so it needs the app host (UseAppHost, the default for test applications); Linux cuts process names to 15 characters.
    private static async Task FindApplicationAsync(string name, DateTime after, TaskCompletionSource<int> found, CancellationToken cancellationToken)
    {
        try
        {
            while (!found.Task.IsCompleted)
            {
                var runtimes = DiagnosticsClient.GetPublishedProcesses().ToHashSet();
                foreach (var process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        if (runtimes.Contains(process.Id) && StartedAfter(process, after))
                            found.TrySetResult(process.Id);
                    }
                }

                await Task.Delay(250, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }

        static bool StartedAfter(Process process, DateTime after)
        {
            try
            {
                return process.StartTime >= after;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }

    private static void Kill(Process process)
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

    [GeneratedRegex(@"Process Id: (\d+)")]
    private static partial Regex TestHostId();

    private async Task<T> LockedAsync<T>(Func<Task<T>> action)
    {
        await _lock.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EndAsync()
    {
        if (_session is not { } session)
            return;

        _session = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await session.Client.RequestAsync("disconnect", new { terminateDebuggee = !session.Settings.Detach }, timeout.Token);
        }
        catch (Exception e) when (e is McpException or OperationCanceledException)
        {
        }

        await session.Client.DisposeAsync();
        if (session.Settings.Run is { } run)
            Kill(run);
    }

    private static async Task<IReadOnlyList<BreakpointState>> SetFunctionBreakpointsAsync(Session session, IReadOnlyList<Breakpoint> breakpoints, CancellationToken cancellationToken)
    {
        var response = await session.Client.RequestAsync("setFunctionBreakpoints", new
        {
            breakpoints = breakpoints.Select(breakpoint => new { name = breakpoint.Function, condition = breakpoint.Condition }).ToArray(),
        }, cancellationToken);
        return [.. response.GetProperty("breakpoints").EnumerateArray().Zip(breakpoints, (state, breakpoint) => new BreakpointState(
            state.TryGetProperty("source", out var source) ? DapClient.Text(source, "path") : null,
            state.TryGetProperty("line", out var line) ? line.GetInt32() : 0,
            state.GetProperty("verified").GetBoolean(),
            DapClient.Text(state, "message"),
            breakpoint.Function))];
    }

    private static async Task<IReadOnlyList<BreakpointState>> SetBreakpointsAsync(Session session, string filePath, IReadOnlyList<Breakpoint> breakpoints, CancellationToken cancellationToken)
    {
        var response = await session.Client.RequestAsync("setBreakpoints", new
        {
            source = new { path = filePath },
            breakpoints = breakpoints.Select(breakpoint => new { line = breakpoint.Line, condition = breakpoint.Condition }).ToArray(),
        }, cancellationToken);
        return [.. response.GetProperty("breakpoints").EnumerateArray().Zip(breakpoints, (state, breakpoint) => new BreakpointState(
            filePath,
            state.TryGetProperty("line", out var line) ? line.GetInt32() : breakpoint.Line,
            state.GetProperty("verified").GetBoolean(),
            DapClient.Text(state, "message")))];
    }

    /// <summary>The assembly to run: a .dll as is, or the output of a project after building it.</summary>
    private async Task<string> ProgramOfAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return File.Exists(fullPath) ? fullPath : throw new McpException($"Program not found: {fullPath}");

        var build = await Tools.BuildTool.BuildAsync(fullPath, cancellationToken: cancellationToken);
        if (!build.Succeeded)
            throw new McpException($"The build failed: {string.Join("; ", build.Messages.Where(message => message.Severity == "Error").Take(5).Select(message => $"{message.Code} {message.Message} ({message.Origin}:{message.Line})"))}{build.Output}");

        var result = (await workspace.GetAsync(fullPath)).Results.FirstOrDefault(result => string.Equals(result.ProjectFilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        return result?.GetProperty("TargetPath") is { Length: > 0 } target ? target : throw new McpException($"{fullPath} has no TargetPath to run.");
    }

    private static string FindAdapter()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "netcoredbg.exe" } : ["netcoredbg"];
        var candidates = new[] { Environment.GetEnvironmentVariable(AdapterVariable) }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).SelectMany(directory => names.Select(name => Path.Combine(directory, name))));
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new McpException($"Debugging needs netcoredbg, from https://github.com/Samsung/netcoredbg/releases: put it on the PATH, or set {AdapterVariable} to its path, and restart the server.");
    }

    private static int Reference(JsonElement element) => element.TryGetProperty("variablesReference", out var reference) ? reference.GetInt32() : 0;

    /// <summary>A running debug adapter: the events it sends, and the state they leave the program in.</summary>
    private sealed class Session
    {
        private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<JsonElement> _stops = Channel.CreateUnbounded<JsonElement>();
        private readonly StringBuilder _output;

        // The paths of loaded modules by id, which frames refer to.
        private readonly ConcurrentDictionary<string, string> _modules = new();
        private readonly PdbService _pdbs;
        private JsonElement? _exited;

        public Session(DapClient client, PdbService pdbs, Options settings)
        {
            Client = client;
            Settings = settings;
            _pdbs = pdbs;
            _output = settings.Output ?? new();
            _ = Task.Run(PumpAsync);
        }

        /// <param name="Detach">Leave the program running when the session ends, as for an attached process.</param>
        /// <param name="Run">A process that runs the program, such as dotnet test, to end with the session.</param>
        /// <param name="Output">Where that process writes its output, which joins the program's.</param>
        public sealed record Options(bool Detach = false, Process? Run = null, StringBuilder? Output = null);

        public DapClient Client { get; }

        public Options Settings { get; }

        public Task Initialized => _initialized.Task;

        public bool IsStopped { get; private set; }

        public int? ThreadId { get; private set; }

        public void Resumed() => IsStopped = false;

        /// <summary>Waits for the program to stop or exit, and describes where it is.</summary>
        public async Task<DebugState> WaitAsync(TimeSpan wait, CancellationToken cancellationToken)
        {
            JsonElement stop;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(wait);
                try
                {
                    // A stop before this wait, as when a breakpoint hits while the session starts, is in the channel already: take the latest.
                    stop = default;
                    if (IsStopped || _exited is not null)
                    {
                        while (_stops.Reader.TryRead(out var pending))
                            stop = pending;
                    }
                    else
                    {
                        stop = await _stops.Reader.ReadAsync(timeout.Token);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new("Running", null, null, [], [], null, TakeOutput());
                }
                catch (ChannelClosedException)
                {
                    stop = default;
                }
            }

            if (_exited is { } exited)
            {
                // dotnet test reports the results once its test host exits.
                if (Settings.Run is { } run)
                {
                    try
                    {
                        await run.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                    }
                    catch (TimeoutException)
                    {
                    }
                }

                return new("Exited", null, null, [], [], exited.ValueKind == JsonValueKind.Object && exited.TryGetProperty("exitCode", out var code) ? code.GetInt32() : null, TakeOutput());
            }

            var reason = stop.ValueKind == JsonValueKind.Object ? DapClient.Text(stop, "reason") : null;
            var description = stop.ValueKind == JsonValueKind.Object ? DapClient.Text(stop, "text") ?? DapClient.Text(stop, "description") : null;
            var stack = await StackAsync(cancellationToken);
            var locals = stack.Count == 0 ? [] : await LocalsAsync(await FrameIdAsync(0, cancellationToken), cancellationToken);
            var frames = await Task.WhenAll(stack.Select(frame => WithSourceAsync(frame.Frame, frame.Module, cancellationToken)));
            return new("Stopped", reason, description, frames, locals, null, TakeOutput(), reason == "exception" ? await ExceptionAsync(cancellationToken) : null);
        }

        /// <summary>The exception the stopped thread is at, as "Type: message".</summary>
        private async Task<string?> ExceptionAsync(CancellationToken cancellationToken)
        {
            try
            {
                var info = await Client.RequestAsync("exceptionInfo", new { threadId = ThreadId }, cancellationToken);
                return $"{DapClient.Text(info, "exceptionId")}: {DapClient.Text(info, "description")}";
            }
            catch (McpException)
            {
                return null;
            }
        }

        public async Task<int> FrameIdAsync(int frame, CancellationToken cancellationToken)
        {
            var stack = await StackAsync(cancellationToken);
            return frame >= 0 && frame < stack.Count ? stack[frame].Id : throw new McpException($"There is no frame {frame}; the stack has {stack.Count}.");
        }

        public async Task<IReadOnlyList<Variable>> LocalsAsync(int frameId, CancellationToken cancellationToken)
        {
            var scopes = await Client.RequestAsync("scopes", new { frameId }, cancellationToken);
            var locals = scopes.GetProperty("scopes").EnumerateArray().FirstOrDefault();
            return locals.ValueKind == JsonValueKind.Object ? await VariablesAsync(Reference(locals), cancellationToken) : [];
        }

        public async Task<IReadOnlyList<Variable>> VariablesAsync(int reference, CancellationToken cancellationToken)
        {
            var variables = await Client.RequestAsync("variables", new { variablesReference = reference }, cancellationToken);
            return [.. variables.GetProperty("variables").EnumerateArray().Select(variable =>
                new Variable(DapClient.Text(variable, "name") ?? "", DapClient.Text(variable, "value") ?? "", DapClient.Text(variable, "type"), Reference(variable)))];
        }

        /// <summary>A frame in a file not on disk, with a local copy of the source its module's PDB leads to.</summary>
        private async Task<Frame> WithSourceAsync(Frame frame, string? module, CancellationToken cancellationToken)
        {
            if (frame.FilePath is not { } path || File.Exists(path) || module is null || !_modules.TryGetValue(module, out var assembly))
                return frame;

            try
            {
                return await _pdbs.FetchAsync(assembly, path, cancellationToken) is { } source ? frame with { Source = source } : frame;
            }
            catch (McpException)
            {
                return frame;
            }
        }

        private async Task<List<(int Id, Frame Frame, string? Module)>> StackAsync(CancellationToken cancellationToken)
        {
            if (ThreadId is null)
                return [];

            var stack = await Client.RequestAsync("stackTrace", new { threadId = ThreadId, startFrame = 0, levels = MaxFrames }, cancellationToken);
            return [.. stack.GetProperty("stackFrames").EnumerateArray().Select(frame => (
                frame.GetProperty("id").GetInt32(),
                new Frame(DapClient.Text(frame, "name") ?? "?", frame.TryGetProperty("source", out var source) ? DapClient.Text(source, "path") : null, frame.GetProperty("line").GetInt32()),
                frame.TryGetProperty("moduleId", out var module) ? module.ToString() : null))];
        }

        private string TakeOutput()
        {
            lock (_output)
            {
                var output = _output.ToString();
                _output.Clear();
                return output;
            }
        }

        private async Task PumpAsync()
        {
            await foreach (var message in Client.Events.ReadAllAsync())
            {
                var body = message.TryGetProperty("body", out var value) ? value : default;
                switch (DapClient.Text(message, "event"))
                {
                    case "initialized":
                        _initialized.TrySetResult();
                        break;
                    case "output" when DapClient.Text(body, "category") is not "telemetry":
                        lock (_output)
                            _output.Append(DapClient.Text(body, "output"));
                        break;
                    case "stopped":
                        // Into the channel before IsStopped, which tells WaitAsync it is there.
                        ThreadId = body.TryGetProperty("threadId", out var thread) ? thread.GetInt32() : ThreadId;
                        _stops.Writer.TryWrite(body);
                        IsStopped = true;
                        break;
                    case "module" when body.TryGetProperty("module", out var module) && DapClient.Text(module, "path") is { } path:
                        _modules[module.GetProperty("id").ToString()] = path;
                        break;
                    case "continued":
                        IsStopped = false;
                        break;
                    case "exited":
                        _exited = body;
                        break;
                    case "terminated":
                        _exited ??= default(JsonElement);
                        _stops.Writer.TryComplete();
                        break;
                }
            }

            _exited ??= default(JsonElement);
            _initialized.TrySetException(new McpException("The debugger exited before it was ready."));
            _stops.Writer.TryComplete();
        }
    }
}
