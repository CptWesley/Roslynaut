using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
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

public sealed record Breakpoint(string FilePath, int Line, string? Condition = null);

/// <param name="Verified">Whether the debugger could bind the breakpoint to code; Message says why not.</param>
public sealed record BreakpointState(string FilePath, int Line, bool Verified, string? Message);

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
public sealed record DebugState(
    string Status, string? Reason, string? Description, IReadOnlyList<Frame> Stack, IReadOnlyList<Variable> Locals, int? ExitCode, string Output);

/// <summary>
/// One debugging session at a time, with netcoredbg over the Debug Adapter Protocol: launches a program, reports where it stops,
/// resumes and steps it, and inspects its variables.
/// </summary>
public sealed class DebugService(WorkspaceService workspace, PdbService pdbs) : IAsyncDisposable
{
    public const string AdapterVariable = "NETCOREDBG";

    private const int MaxFrames = 20;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Session? _session;

    public Task<DebugState> StartAsync(
        string programPath, IReadOnlyList<string> arguments, IReadOnlyList<Breakpoint> breakpoints, bool stopAtEntry, bool stopOnAllExceptions,
        TimeSpan wait, CancellationToken cancellationToken) =>
        LockedAsync(async () =>
        {
            await EndAsync();
            var program = await ProgramOfAsync(programPath, cancellationToken);
            var client = new DapClient(FindAdapter(), "--interpreter=vscode");
            var session = _session = new Session(client, pdbs);

            await client.RequestAsync("initialize", new { clientID = "roslynaut", adapterID = "coreclr", linesStartAt1 = true, columnsStartAt1 = true, pathFormat = "path" }, cancellationToken);
            var launch = client.RequestAsync("launch", new
            {
                program = DotnetCli.Executable,
                args = (string[])[program, .. arguments],
                cwd = Path.GetDirectoryName(program),
                stopAtEntry,
                console = "internalConsole",
            }, cancellationToken);
            await session.Initialized.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

            foreach (var file in breakpoints.GroupBy(breakpoint => Path.GetFullPath(breakpoint.FilePath), StringComparer.OrdinalIgnoreCase))
                await SetBreakpointsAsync(session, file.Key, [.. file], cancellationToken);
            await client.RequestAsync("setExceptionBreakpoints", new { filters = stopOnAllExceptions ? new[] { "all" } : ["user-unhandled"] }, cancellationToken);
            await client.RequestAsync("configurationDone", null, cancellationToken);
            await launch;
            return await session.WaitAsync(wait, cancellationToken);
        });

    public Task<IReadOnlyList<BreakpointState>> SetBreakpointsAsync(string filePath, IReadOnlyList<Breakpoint> breakpoints, CancellationToken cancellationToken) =>
        LockedAsync(() => SetBreakpointsAsync(Current, Path.GetFullPath(filePath), breakpoints, cancellationToken));

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

    private Session Current => _session ?? throw new McpException("No program is being debugged; start one with debug_start.");

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
            await session.Client.RequestAsync("disconnect", new { terminateDebuggee = true }, timeout.Token);
        }
        catch (Exception e) when (e is McpException or OperationCanceledException)
        {
        }

        await session.Client.DisposeAsync();
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
        private readonly StringBuilder _output = new();

        // The paths of loaded modules by id, which frames refer to.
        private readonly ConcurrentDictionary<string, string> _modules = new();
        private readonly PdbService _pdbs;
        private JsonElement? _exited;

        public Session(DapClient client, PdbService pdbs)
        {
            Client = client;
            _pdbs = pdbs;
            _ = Task.Run(PumpAsync);
        }

        public DapClient Client { get; }

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
                    stop = IsStopped || _exited is not null ? default : await _stops.Reader.ReadAsync(timeout.Token);
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
                return new("Exited", null, null, [], [], exited.ValueKind == JsonValueKind.Object && exited.TryGetProperty("exitCode", out var code) ? code.GetInt32() : null, TakeOutput());

            var reason = stop.ValueKind == JsonValueKind.Object ? DapClient.Text(stop, "reason") : null;
            var description = stop.ValueKind == JsonValueKind.Object ? DapClient.Text(stop, "text") ?? DapClient.Text(stop, "description") : null;
            var stack = await StackAsync(cancellationToken);
            var locals = stack.Count == 0 ? [] : await LocalsAsync(await FrameIdAsync(0, cancellationToken), cancellationToken);
            var frames = await Task.WhenAll(stack.Select(frame => WithSourceAsync(frame.Frame, frame.Module, cancellationToken)));
            return new("Stopped", reason, description, frames, locals, null, TakeOutput());
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
                        ThreadId = body.TryGetProperty("threadId", out var thread) ? thread.GetInt32() : ThreadId;
                        IsStopped = true;
                        _stops.Writer.TryWrite(body);
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
