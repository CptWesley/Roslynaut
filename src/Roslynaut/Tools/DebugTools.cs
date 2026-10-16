using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Roslynaut.Debugging;

namespace Roslynaut.Tools;

internal static class DebugDefaults
{
    public const string Wait = "How long to wait for the program to stop or exit; it keeps running after that, and the state says Running.";
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
        [Description("Breakpoints: in the file at filePath, the 1-based line, with an optional condition such as 'i == 3'.")] Breakpoint[]? breakpoints = null,
        [Description("Stop at the first line of Main.")] bool stopAtEntry = false,
        [Description("Stop at every exception thrown, not only at unhandled ones.")] bool stopOnAllExceptions = false,
        [Description(DebugDefaults.Wait)] int waitSeconds = 30,
        CancellationToken cancellationToken = default) =>
        debugger.StartAsync(program, args ?? [], breakpoints ?? [], stopAtEntry, stopOnAllExceptions, TimeSpan.FromSeconds(waitSeconds), cancellationToken);
}

public sealed class DebugBreakpointsTool(DebugService debugger) : IMcpTool
{
    public Delegate Handler => SetAsync;

    [McpServerTool(Name = "debug_breakpoints", Idempotent = true)]
    [Description("Replaces the breakpoints in one file of the program being debugged; no lines removes them. Returns whether the debugger "
        + "could bind each one to code.")]
    private Task<IReadOnlyList<BreakpointState>> SetAsync(
        [Description(ToolDefaults.FilePath)] string filePath,
        [Description("1-based lines to break at.")] int[] lines,
        [Description("Optional conditions, one per line in the same order; null or empty for none.")] string?[]? conditions = null,
        CancellationToken cancellationToken = default)
    {
        if (conditions is not null && conditions.Length != lines.Length)
            throw new McpException("Give one condition per line, or none.");

        return debugger.SetBreakpointsAsync(filePath, [.. lines.Select((line, i) => new Breakpoint(filePath, line, conditions?[i] is { Length: > 0 } condition ? condition : null))], cancellationToken);
    }
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
