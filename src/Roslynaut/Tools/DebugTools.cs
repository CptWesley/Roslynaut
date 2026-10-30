using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Roslynaut.Debugging;

namespace Roslynaut.Tools;

internal static class DebugDefaults
{
    public const string Wait = "How long to wait for the program to stop or exit; it keeps running after that, and the state says Running.";
    public const string Breakpoints = "Breakpoints: at the 1-based Line of the file at FilePath, or where the method Function starts, such as "
        + "'MyApp.Orders.Submit'; each with an optional Condition such as 'i == 3'.";
    public const string Exceptions = "Exception types to stop at where they are thrown, even when caught, such as 'System.InvalidOperationException'; "
        + "'*' for every exception. Unhandled exceptions always stop.";
}

public sealed class DebugAttachTool(DebugService debugger) : IMcpTool
{
    public Delegate Handler => AttachAsync;

    [McpServerTool(Name = "debug_attach", Destructive = true)]
    [Description("Attaches the debugger to a running .NET process, such as a service that misbehaves, sets breakpoints, and waits until it stops "
        + "or exits. Returns its state as debug_start does. debug_stop detaches and leaves the process running. Ends an earlier session.")]
    private Task<DebugState> AttachAsync(
        [Description("The process id, e.g. from dotnet_processes.")] int processId,
        [Description(DebugDefaults.Breakpoints)] Breakpoint[]? breakpoints = null,
        [Description(DebugDefaults.Exceptions)] string[]? stopOnExceptions = null,
        [Description(DebugDefaults.Wait)] int waitSeconds = 30,
        CancellationToken cancellationToken = default) =>
        debugger.AttachAsync(processId, breakpoints ?? [], stopOnExceptions, TimeSpan.FromSeconds(waitSeconds), cancellationToken);
}

public sealed class DebugTestTool(DebugService debugger) : IMcpTool
{
    public Delegate Handler => TestAsync;

    [McpServerTool(Name = "debug_test", Destructive = true, OpenWorld = true)]
    [Description("Debugs tests: builds and runs them with dotnet test, attaches the debugger to the test host, sets breakpoints, and waits until "
        + "it stops or exits. Returns its state as debug_start does; once the test host exits, Output has the test results. Ends an earlier "
        + "session; debug_stop ends the test run.")]
    private Task<DebugState> TestAsync(
        [Description("The test project (.csproj).")] string projectPath,
        [Description("A dotnet test --filter expression for the tests to run, such as a Filter from find_tests; omit to run all of them.")] string? filter = null,
        [Description(DebugDefaults.Breakpoints)] Breakpoint[]? breakpoints = null,
        [Description(DebugDefaults.Exceptions)] string[]? stopOnExceptions = null,
        [Description(DebugDefaults.Wait + " The build comes first.")] int waitSeconds = 60,
        CancellationToken cancellationToken = default) =>
        debugger.TestAsync(projectPath, filter, breakpoints ?? [], stopOnExceptions, TimeSpan.FromSeconds(waitSeconds), cancellationToken);
}

public sealed class DebugStartTool(DebugService debugger) : IMcpTool
{
    public Delegate Handler => StartAsync;

    [McpServerTool(Name = "debug_start", Destructive = true, OpenWorld = true)]
    [Description("Starts debugging a program with netcoredbg: builds a project (or takes a .dll), sets breakpoints, runs it, and waits until it "
        + "stops at a breakpoint, an unhandled exception or its entry, or exits. Returns its state: why it stopped, the call stack, the locals of "
        + "the top frame, and its output so far. Ends an earlier session. Runs the program for real.")]
    private Task<DebugState> StartAsync(
        [Description("The project (.csproj) to build and run, or a .dll to run.")] string program,
        [Description("Command-line arguments for the program.")] string[]? args = null,
        [Description(DebugDefaults.Breakpoints)] Breakpoint[]? breakpoints = null,
        [Description("Stop at the first line of Main.")] bool stopAtEntry = false,
        [Description(DebugDefaults.Exceptions)] string[]? stopOnExceptions = null,
        [Description(DebugDefaults.Wait)] int waitSeconds = 30,
        CancellationToken cancellationToken = default) =>
        debugger.StartAsync(program, args ?? [], breakpoints ?? [], stopAtEntry, stopOnExceptions, TimeSpan.FromSeconds(waitSeconds), cancellationToken);
}

public sealed class DebugBreakpointsTool(DebugService debugger) : IMcpTool
{
    public Delegate Handler => SetAsync;

    [McpServerTool(Name = "debug_breakpoints", Idempotent = true)]
    [Description("Replaces the breakpoints in one file of the program being debugged, or else all the breakpoints at methods; none removes them. "
        + "Returns whether the debugger could bind each one to code.")]
    private Task<IReadOnlyList<BreakpointState>> SetAsync(
        [Description("The file whose breakpoints to replace.")] string? filePath = null,
        [Description("1-based lines of the file to break at.")] int[]? lines = null,
        [Description("Or else: the methods to break at, such as 'MyApp.Orders.Submit'; replaces all the breakpoints at methods.")] string[]? functions = null,
        [Description("Optional conditions, one per line or method in the same order; null or empty for none.")] string?[]? conditions = null,
        CancellationToken cancellationToken = default)
    {
        if ((filePath is null) == (functions is null))
            throw new McpException("Pass a filePath with lines, or functions.");

        var count = functions?.Length ?? lines?.Length ?? 0;
        if (conditions is not null && conditions.Length != count)
            throw new McpException("Give one condition per line or method, or none.");

        string? ConditionOf(int i) => conditions?[i] is { Length: > 0 } condition ? condition : null;
        return functions is not null
            ? debugger.SetFunctionBreakpointsAsync([.. functions.Select((function, i) => new Breakpoint(Condition: ConditionOf(i), Function: function))], cancellationToken)
            : debugger.SetBreakpointsAsync(filePath!, [.. (lines ?? []).Select((line, i) => new Breakpoint(filePath, line, ConditionOf(i)))], cancellationToken);
    }
}

public sealed class DebugSetTool(DebugService debugger) : IMcpTool
{
    public Delegate Handler => SetAsync;

    [McpServerTool(Name = "debug_set", Destructive = true)]
    [Description("Sets a variable, field or property of the stopped program to the value of a C# expression, such as order.Total to 0, to try a "
        + "fix or a case before resuming. Returns the new value.")]
    private Task<Variable> SetAsync(
        [Description("What to set, such as 'total' or 'order.Total'.")] string expression,
        [Description("The new value, as a C# expression such as '0', '\"text\"' or 'other.Total'.")] string value,
        [Description("The frame, by its index in the stack: 0 is the innermost.")] int frame = 0,
        CancellationToken cancellationToken = default) =>
        debugger.SetAsync(expression, value, frame, cancellationToken);
}

public sealed class DebugResumeTool(DebugService debugger) : IMcpTool
{
    public Delegate Handler => ResumeAsync;

    [McpServerTool(Name = "debug_resume", Destructive = true)]
    [Description("Resumes the program being debugged: Continue to the next breakpoint, StepOver, StepInto or StepOut; or Pause it while it runs. "
        + "Waits until it stops or exits, and returns its state as debug_start does. Continue on a running program just waits again.")]
    private Task<DebugState> ResumeAsync(
        [Description("Continue, StepOver, StepInto, StepOut or Pause.")] ResumeAction action = ResumeAction.Continue,
        [Description(DebugDefaults.Wait)] int waitSeconds = 30,
        CancellationToken cancellationToken = default) =>
        debugger.ResumeAsync(action, TimeSpan.FromSeconds(waitSeconds), cancellationToken);
}

public sealed class DebugInspectTool(DebugService debugger) : IMcpTool
{
    public Delegate Handler => InspectAsync;

    [McpServerTool(Name = "debug_inspect", ReadOnly = true)]
    [Description("Inspects the stopped program: evaluates an expression in a frame, such as 'order.Total * 2' or 'items.Count'; or lists what a "
        + "variable contains, by the Reference of a variable from an earlier result; or else lists the locals of a frame.")]
    private Task<IReadOnlyList<Variable>> InspectAsync(
        [Description("Optional C# expression to evaluate.")] string? expression = null,
        [Description("The frame, by its index in the stack: 0 is the innermost.")] int frame = 0,
        [Description("Optional Reference of a variable, to list its fields, properties or items.")] int? reference = null,
        CancellationToken cancellationToken = default) =>
        debugger.InspectAsync(expression, frame, reference, cancellationToken);
}

public sealed class DebugStopTool(DebugService debugger) : IMcpTool
{
    public Delegate Handler => StopAsync;

    [McpServerTool(Name = "debug_stop", Destructive = true, Idempotent = true)]
    [Description("Stops debugging: ends the program and the debugger.")]
    private async Task<string> StopAsync()
    {
        await debugger.StopAsync();
        return "Stopped.";
    }
}
