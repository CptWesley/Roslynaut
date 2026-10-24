using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.Tracing;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

/// <summary>Traces a running .NET process over EventPipe, as dotnet-trace and dotnet-counters do.</summary>
internal static class ProcessTracing
{
    /// <summary>
    /// Where the process spends its time: the runtime samples each thread's stack every millisecond; samples of threads in managed code count,
    /// those waiting in native code (such as for a lock or I/O) do not.
    /// </summary>
    public static Task<CpuSamples> SampleAsync(int processId, TimeSpan duration, CancellationToken cancellationToken)
    {
        EventPipeProvider[] providers =
        [
            new("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
            new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, (long)ClrTraceEventParser.Keywords.Default),
        ];

        return TraceAsync(processId, providers, duration, log =>
        {
            // Its Type is an enum of TraceEvent's, ClrThreadSampleType, whose 2 is Managed. Threads repeat the same stacks, which the log numbers.
            var stacks = new Dictionary<(int Thread, CallStackIndex Stack), int>();
            foreach (var sample in log.Events.Where(item => item.ProviderName == "Microsoft-DotNETCore-SampleProfiler" && Convert.ToInt32(item.PayloadByName("Type")) == 2))
            {
                if (sample.CallStackIndex() is var stack && stack != CallStackIndex.Invalid)
                    stacks[(sample.ThreadID, stack)] = stacks.GetValueOrDefault((sample.ThreadID, stack)) + 1;
            }

            return new CpuSamples(duration, [.. stacks.Select(entry => new StackSamples(entry.Key.Thread, FramesOf(log.CallStacks[entry.Key.Stack]), entry.Value))]);
        }, cancellationToken);
    }

    /// <summary>Counts the samples that pass the filters per thread and method, and with a root or leaf, along the call paths through it.</summary>
    public static CpuProfile Analyze(string id, CpuSamples profile, CpuSamples? baseline, CpuFilters filters, int top)
    {
        var stacks = Counted(profile.Stacks, filters);
        var samples = stacks.Sum(stack => stack.Count);
        var shares = SharesOf(stacks);
        var before = baseline is null ? null : SharesOf(Counted(baseline.Stacks, filters));
        var methods = shares.Keys.Concat(before?.Keys ?? Enumerable.Empty<string>()).Distinct().Select(method =>
        {
            var (self, total) = shares.GetValueOrDefault(method);
            return before?.GetValueOrDefault(method) is var (selfBefore, totalBefore)
                ? new MethodSamples(method, self, total, Math.Round(self - selfBefore, 1), Math.Round(total - totalBefore, 1))
                : new MethodSamples(method, self, total);
        });

        return new(
            id,
            samples,
            [.. stacks.GroupBy(stack => stack.Thread).Select(thread => (Thread: thread, Samples: thread.Sum(stack => stack.Count)))
                .OrderByDescending(thread => thread.Samples)
                .Select(thread => new ThreadSamples(thread.Thread.Key, EntryOf(thread.Thread.Select(stack => (stack.Frames, (long)stack.Count))), thread.Samples,
                    Percent(thread.Samples, (long)profile.Duration.TotalMilliseconds)))],
            [.. (before is null
                ? methods.OrderByDescending(method => method.Self).ThenByDescending(method => method.Total)
                : methods.OrderByDescending(method => Math.Abs(method.SelfChange!.Value)).ThenByDescending(method => Math.Abs(method.TotalChange!.Value))).Take(top)],
            filters.Root is { } root ? TreesOf(stacks, samples, root, inward: true) : null,
            filters.Leaf is { } leaf ? TreesOf(stacks, samples, leaf, inward: false) : null);
    }

    /// <summary>The stacks without those that match Exclude, of the threads that ran a method that matches Threads.</summary>
    private static List<StackSamples> Counted(IEnumerable<StackSamples> stacks, CpuFilters filters)
    {
        var kept = stacks.Where(stack => filters.Exclude is not { } exclude || !stack.Frames.Any(exclude.IsMatch)).ToList();
        if (filters.Threads is not { } threads)
            return kept;

        var picked = kept.Where(stack => stack.Frames.Any(threads.IsMatch)).Select(stack => stack.Thread).ToHashSet();
        return [.. kept.Where(stack => picked.Contains(stack.Thread))];
    }

    /// <summary>Per method, the percentage of the samples it runs itself in, and that it is on the stack in.</summary>
    private static Dictionary<string, (double Self, double Total)> SharesOf(List<StackSamples> stacks)
    {
        var (self, total) = (new Dictionary<string, int>(), new Dictionary<string, int>());
        foreach (var stack in stacks)
        {
            if (stack.Frames is [var innermost, ..])
                self[innermost] = self.GetValueOrDefault(innermost) + stack.Count;
            foreach (var method in stack.Frames.Distinct())
                total[method] = total.GetValueOrDefault(method) + stack.Count;
        }

        var samples = stacks.Sum(stack => stack.Count);
        return total.ToDictionary(entry => entry.Key, entry => (Percent(self.GetValueOrDefault(entry.Key), samples), Percent(entry.Value, samples)));
    }

    /// <summary>
    /// The call paths from the frames that match, inward to what they call from the outermost match, or outward to their callers from the
    /// innermost, merged into trees.
    /// </summary>
    private static List<CallTree> TreesOf(List<StackSamples> stacks, int samples, Regex match, bool inward)
    {
        var roots = new Dictionary<string, Node>();
        foreach (var stack in stacks)
        {
            var frames = inward ? stack.Frames.Reverse().ToList() : [.. stack.Frames];
            var start = frames.FindIndex(match.IsMatch);
            if (start < 0)
                continue;

            var (nodes, node) = (roots, (Node?)null);
            foreach (var frame in frames.Skip(start))
            {
                node = nodes.TryGetValue(frame, out var existing) ? existing : nodes[frame] = new();
                node.Total += stack.Count;
                nodes = node.Children;
            }

            node!.Self += stack.Count;
        }

        return [.. roots.OrderByDescending(root => root.Value.Total).Select(root => TreeOf(root.Key, root.Value, root.Value.Total))];

        // ponytail: paths under 1% of their root's samples are left out; take the share as a parameter if agents need finer trees.
        CallTree TreeOf(string method, Node node, int rootSamples) => new(
            method,
            Percent(node.Total, samples),
            inward ? Percent(node.Self, samples) : null,
            [.. node.Children.Where(child => child.Value.Total * 100 >= rootSamples).OrderByDescending(child => child.Value.Total)
                .Select(child => TreeOf(child.Key, child.Value, rootSamples))]);
    }

    private sealed class Node
    {
        public int Total { get; set; }

        public int Self { get; set; }

        public Dictionary<string, Node> Children { get; } = [];
    }

    /// <summary>What a running process allocates, as the runtime's allocation ticks sample it, one per about 100 KB.</summary>
    public static async Task<AllocationProfile> AllocationsAsync(int processId, TimeSpan duration, Regex? threads, int top, int maxFrames, CancellationToken cancellationToken)
    {
        // Allocation ticks are verbose GC events.
        EventPipeProvider[] providers =
        [
            new("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose,
                (long)(ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.Jit | ClrTraceEventParser.Keywords.Loader)),
        ];
        var ticks = await TraceAsync(processId, providers, duration, log => log.Events.OfType<GCAllocationTickTraceData>()
            .Select(tick => new AllocationTick(tick.ThreadID, tick.TypeName, (ulong)tick.TypeID, tick.AllocationAmount64, FramesOf(tick.CallStack())))
            .ToList(), cancellationToken);

        if (threads is not null)
        {
            var picked = ticks.Where(tick => tick.Frames.Any(threads.IsMatch)).Select(tick => tick.Thread).ToHashSet();
            ticks = [.. ticks.Where(tick => picked.Contains(tick.Thread))];
        }

        // Ticks name a nested type without the type it is in; the runtime itself names it in full.
        var described = Describe(processId, ticks.Select(tick => tick.TypeId).Distinct());
        string NameOf(AllocationTick tick) => described.GetValueOrDefault(tick.TypeId).Name ?? tick.Type;
        var bytes = ticks.Sum(tick => tick.Bytes);
        var types = ticks.GroupBy(NameOf).Select(type => (Allocations: new TypeAllocations(type.Key, type.Sum(tick => tick.Bytes), Percent(type.Sum(tick => tick.Bytes), bytes)),
                Finalizable: type.Any(tick => described.GetValueOrDefault(tick.TypeId).Finalizable)))
            .OrderByDescending(type => type.Allocations.Bytes).ToList();
        return new(
            bytes,
            [.. ticks.GroupBy(tick => tick.Thread).Select(thread => new ThreadAllocations(thread.Key, EntryOf(thread.Select(tick => (tick.Frames, tick.Bytes))), thread.Sum(tick => tick.Bytes)))
                .OrderByDescending(thread => thread.Bytes)],
            [.. types.Take(top).Select(type => type.Allocations)],
            [.. ticks.GroupBy(tick => (Type: NameOf(tick), Stack: string.Join('\n', tick.Frames.Take(maxFrames))))
                .Select(stack => new StackAllocations(stack.Key.Type, stack.Sum(tick => tick.Bytes), Percent(stack.Sum(tick => tick.Bytes), bytes), [.. stack.First().Frames.Take(maxFrames)]))
                .OrderByDescending(stack => stack.Bytes).Take(top)],
            [.. types.Where(type => type.Finalizable).Select(type => type.Allocations)]);
    }

    /// <summary>The types, by method table, with their names and whether they have a finalizer; none if the process cannot be read, as after it exited.</summary>
    private static Dictionary<ulong, (string? Name, bool Finalizable)> Describe(int processId, IEnumerable<ulong> types)
    {
        try
        {
            using var target = ProcessInspection.Open(processId, null);
            using var runtime = ProcessInspection.RuntimeOf(target);
            return types.Select(id => (Id: id, Type: runtime.GetTypeByMethodTable(id))).Where(type => type.Type is not null)
                .ToDictionary(type => type.Id, type => (type.Type!.Name, type.Type.IsFinalizable));
        }
        catch (McpException)
        {
            return [];
        }
    }

    private sealed record AllocationTick(int Thread, string Type, ulong TypeId, long Bytes, IReadOnlyList<string> Frames);

    /// <summary>A stack's methods, innermost first.</summary>
    private static string[] FramesOf(TraceCallStack? stack)
    {
        var frames = new List<string>();
        for (var frame = stack; frame is not null; frame = frame.Caller)
            frames.Add(frame.CodeAddress.FullMethodName is { Length: > 0 } name ? name : $"{frame.CodeAddress.ModuleName}!?");
        return [.. frames];
    }

    /// <summary>
    /// The outermost method of the program's own code that a thread's stacks start from most, by weight, to tell threads apart: framework
    /// methods, such as the thread pool's, start many threads.
    /// </summary>
    private static string EntryOf(IEnumerable<(IReadOnlyList<string> Frames, long Weight)> stacks) => stacks
        .Select(stack => (Entry: stack.Frames.LastOrDefault(frame => !frame.StartsWith("System.", StringComparison.Ordinal)
            && !frame.StartsWith("Microsoft.", StringComparison.Ordinal) && !frame.EndsWith("!?", StringComparison.Ordinal)) ?? stack.Frames.LastOrDefault() ?? "?", stack.Weight))
        .GroupBy(stack => stack.Entry)
        .MaxBy(entry => entry.Sum(stack => stack.Weight))?.Key ?? "?";

    private static double Percent(long count, long of) => of == 0 ? 0 : Math.Round(100.0 * count / of, 1);

    /// <summary>The runtime's counters, such as CPU usage, GC heap size and thread pool queue length, as dotnet-counters shows them.</summary>
    public static async Task<IReadOnlyList<CounterValue>> CountersAsync(int processId, TimeSpan duration, CancellationToken cancellationToken)
    {
        EventPipeProvider[] providers = [new("System.Runtime", EventLevel.Informational, -1, new Dictionary<string, string> { ["EventCounterIntervalSec"] = "1" })];
        var values = new ConcurrentDictionary<string, CounterValue>();
        await RecordAsync(processId, providers, requestRundown: false, duration, stream =>
        {
            using var source = new EventPipeEventSource(stream);
            source.Dynamic.All += item =>
            {
                if (item.EventName != "EventCounters" || item.PayloadValue(0) is not IDictionary<string, object> { } outer || outer["Payload"] is not IDictionary<string, object> payload)
                    return;

                // Each interval reports the mean of a gauge, or the increment of a count; the last interval wins.
                string? Text(string key) => payload.TryGetValue(key, out var value) && value is string { Length: > 0 } text ? text : null;
                var rate = Text("CounterType") == "Sum";
                var name = Text("Name") ?? "?";
                values[name] = new(name, Text("DisplayName") ?? name, Convert.ToDouble(payload[rate ? "Increment" : "Mean"]),
                    rate ? $"{Text("DisplayUnits") ?? "count"} per second" : Text("DisplayUnits"));
            };
            source.Process();
            return Task.CompletedTask;
        }, cancellationToken);

        return [.. values.Values.OrderBy(value => value.Name, StringComparer.Ordinal)];
    }

    /// <summary>Records a trace, with the rundown at its end that names the methods that ran, and reads it.</summary>
    private static async Task<T> TraceAsync<T>(int processId, EventPipeProvider[] providers, TimeSpan duration, Func<TraceLog, T> read, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("roslynaut-trace-");
        try
        {
            var trace = Path.Combine(directory.FullName, "trace.nettrace");
            await RecordAsync(processId, providers, requestRundown: true, duration, async stream =>
            {
                await using var file = File.Create(trace);
                await stream.CopyToAsync(file);
            }, cancellationToken);

            using var log = new TraceLog(TraceLog.CreateFromEventPipeDataFile(trace, Path.Combine(directory.FullName, "trace.etlx")));
            return read(log);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Runs an EventPipe session for a while and hands its stream to a reader, which ends when the session stops.</summary>
    private static async Task RecordAsync(
        int processId, EventPipeProvider[] providers, bool requestRundown, TimeSpan duration, Func<Stream, Task> read, CancellationToken cancellationToken)
    {
        EventPipeSession session;
        try
        {
            session = new DiagnosticsClient(processId).StartEventPipeSession(providers, requestRundown);
        }
        catch (Exception e) when (e is ServerNotAvailableException or UnsupportedCommandException or ArgumentException)
        {
            throw new McpException($"Cannot trace process {processId}; is it a running .NET process? {e.Message}");
        }

        using (session)
        {
            var reading = Task.Run(() => read(session.EventStream), CancellationToken.None);
            try
            {
                await Task.Delay(duration, cancellationToken);
            }
            finally
            {
                // Stopping ends the stream, after the rundown if there is one.
                session.Stop();
                await reading;
            }
        }
    }

    /// <summary>A profile's samples: per thread, each distinct stack and how many samples it had.</summary>
    public sealed record CpuSamples(TimeSpan Duration, IReadOnlyList<StackSamples> Stacks);

    /// <param name="Frames">Innermost first.</param>
    public sealed record StackSamples(int Thread, IReadOnlyList<string> Frames, int Count);

    /// <param name="Threads">Counts only the threads that ran a method that matches.</param>
    /// <param name="Exclude">Leaves out the samples with a method that matches.</param>
    /// <param name="Root">Makes call trees from the methods that match to what they call.</param>
    /// <param name="Leaf">Makes call trees from the methods that match out to their callers.</param>
    public sealed record CpuFilters(Regex? Threads, Regex? Exclude, Regex? Root, Regex? Leaf);

    /// <param name="Profile">Pass to process_cpu as profile, to analyze these samples again with other filters, or as compareTo for a later profile.</param>
    /// <param name="Samples">How many stack samples of threads in managed code count, after the filters: one per thread per millisecond.</param>
    /// <param name="Threads">The threads that ran managed code, busiest first.</param>
    /// <param name="Methods">
    /// The methods that take the most time, with their own (Self) and their callees' (Total) share of the samples; against compareTo, those whose
    /// share changed the most first.
    /// </param>
    /// <param name="Callees">With root: what the methods that match call, as trees.</param>
    /// <param name="Callers">With leaf: how the methods that match are called, as trees from them out to their callers.</param>
    public sealed record CpuProfile(
        string Profile, int Samples, IReadOnlyList<ThreadSamples> Threads, IReadOnlyList<MethodSamples> Methods, IReadOnlyList<CallTree>? Callees = null, IReadOnlyList<CallTree>? Callers = null);

    /// <param name="Id">The operating system's id of the thread.</param>
    /// <param name="Entry">The outermost method of the program's own code that the thread's samples start from most, to tell threads apart.</param>
    /// <param name="Busy">The percentage of the time sampled that the thread ran managed code.</param>
    public sealed record ThreadSamples(int Id, string Entry, int Samples, double Busy);

    /// <param name="Self">The percentage of samples in which the method itself was running.</param>
    /// <param name="Total">The percentage of samples in which the method was on the stack, itself or through what it called.</param>
    /// <param name="SelfChange">Against compareTo: how many percentage points Self grew, or shrank if negative.</param>
    /// <param name="TotalChange">Against compareTo: how many percentage points Total grew, or shrank if negative.</param>
    public sealed record MethodSamples(string Method, double Self, double Total, double? SelfChange = null, double? TotalChange = null);

    /// <param name="Total">The percentage of samples whose stack runs through this path.</param>
    /// <param name="Self">In Callees: the percentage of samples in which the path ends here, with this method running itself.</param>
    /// <param name="Children">What it calls, in Callees; its callers, in Callers. Paths under 1% of their root's samples are left out.</param>
    public sealed record CallTree(string Method, double Total, double? Self, IReadOnlyList<CallTree> Children);

    /// <param name="Bytes">
    /// What the threads allocated meanwhile, as the runtime's allocation ticks estimate it: one about every 100 KB, which counts those bytes for
    /// the object it happened to fall on, so types and stacks that allocate little can be missing.
    /// </param>
    /// <param name="Threads">The threads that allocated, most first.</param>
    /// <param name="Types">The types allocated the most, with their share of the bytes.</param>
    /// <param name="Stacks">The stacks that allocated the most, per type.</param>
    /// <param name="Finalizable">The types allocated that have a finalizer: the GC finalizes their objects before freeing them, which costs more than their bytes.</param>
    public sealed record AllocationProfile(
        long Bytes, IReadOnlyList<ThreadAllocations> Threads, IReadOnlyList<TypeAllocations> Types, IReadOnlyList<StackAllocations> Stacks, IReadOnlyList<TypeAllocations> Finalizable);

    /// <param name="Id">The operating system's id of the thread.</param>
    /// <param name="Entry">The outermost method of the program's own code that the thread's allocations come from most, to tell threads apart.</param>
    public sealed record ThreadAllocations(int Id, string Entry, long Bytes);

    public sealed record TypeAllocations(string Type, long Bytes, double Share);

    /// <param name="Stack">Innermost first: the method that allocated, then its callers.</param>
    public sealed record StackAllocations(string Type, long Bytes, double Share, IReadOnlyList<string> Stack);

    /// <param name="Value">For a gauge, its mean over the last second; for a count, how much it grew in the last second.</param>
    public sealed record CounterValue(string Name, string DisplayName, double Value, string? Units);
}

public sealed class ProcessCpuTool : IMcpTool
{
    private const int MaxProfiles = 10;

    // ponytail: profiles live in memory, the last 10; each holds its distinct stacks with their counts.
    private readonly ConcurrentDictionary<string, ProcessTracing.CpuSamples> _profiles = new();
    private int _lastId;

    public Delegate Handler => SampleAsync;

    [McpServerTool(Name = "process_cpu", ReadOnly = true)]
    [Description("Profiles where a running .NET process spends its time, as dotnet-trace's CPU sampling does: samples managed thread stacks for a "
        + "while and returns the threads by how busy they were, and the methods that run most, with their own share of the samples and their "
        + "share including what they call. Filters pick the threads that run a method and leave out samples, such as of waits; root gives the call "
        + "tree under a method, leaf the call paths into one. Each result has a Profile, to analyze the same samples again with other filters, or "
        + "to compare a later profile with, as before and after a change. For slow code and high CPU; symbol_asm shows the code of a hot method. "
        + "The process keeps running.")]
    private async Task<ProcessTracing.CpuProfile> SampleAsync(
        [Description("The process id, e.g. from dotnet_processes; or else pass profile.")] int? processId = null,
        [Description("How long to sample; the process should be busy with the work to look at meanwhile.")] int seconds = 5,
        [Description("The Profile of an earlier result, to analyze its samples again instead of sampling.")] string? profile = null,
        [Description("The Profile of an earlier result to compare with: methods come in order of how much their share changed.")] string? compareTo = null,
        [Description("Optional regex: counts only the threads that ran a method whose name matches, e.g. 'WorldLoop\\.OnDay'.")] string? threads = null,
        [Description("Optional regex: leaves out the samples with a method that matches, such as waiting for a lock or for a GC, e.g. "
            + "'Monitor\\.Wait|WaitOneNoCheck|PollGC'.")] string? exclude = null,
        [Description("Optional regex: also returns the call trees under the methods that match, e.g. 'Reactive\\.Propagate'.")] string? root = null,
        [Description("Optional regex: also returns the call paths into the methods that match, out to their callers, e.g. 'Lock\\.TryEnterSlow'.")] string? leaf = null,
        [Description("How many methods to return, those that run themselves the most first.")] int top = 30,
        CancellationToken cancellationToken = default)
    {
        var filters = new ProcessTracing.CpuFilters(
            ProcessDefaults.Pattern(threads, "threads"), ProcessDefaults.Pattern(exclude, "exclude"), ProcessDefaults.Pattern(root, "root"), ProcessDefaults.Pattern(leaf, "leaf"));
        var baseline = compareTo is null ? null : Stored(compareTo);
        if ((processId, profile) is (null, { } id))
            return ProcessTracing.Analyze(id, Stored(id), baseline, filters, top);
        if ((processId, profile) is not ({ } process, null))
            throw new McpException("Pass either a processId or a profile.");

        var samples = await ProcessTracing.SampleAsync(process, TimeSpan.FromSeconds(seconds), cancellationToken);
        var stored = Interlocked.Increment(ref _lastId).ToString();
        _profiles[stored] = samples;
        _profiles.TryRemove((int.Parse(stored) - MaxProfiles).ToString(), out _);
        return ProcessTracing.Analyze(stored, samples, baseline, filters, top);
    }

    private ProcessTracing.CpuSamples Stored(string id) => _profiles.TryGetValue(id, out var samples)
        ? samples
        : throw new McpException($"There is no profile {id}; only the last {MaxProfiles} are kept, until the server restarts.");
}

public sealed class ProcessAllocationsTool : IMcpTool
{
    public Delegate Handler => ProfileAsync;

    [McpServerTool(Name = "process_allocations", ReadOnly = true)]
    [Description("Profiles what a running .NET process allocates, as dotnet-trace's gc-verbose profile does: for a while, samples allocations "
        + "with the runtime's allocation ticks, one about every 100 KB, and estimates the bytes by thread, by type and by the stack that allocated "
        + "them; and lists the types allocated that have a finalizer, which cost more than their bytes. For GC pressure: process_counters gives the "
        + "allocation rate and GC counts, process_heap what stays alive. The process keeps running.")]
    private static Task<ProcessTracing.AllocationProfile> ProfileAsync(
        [Description("The process id, e.g. from dotnet_processes.")] int processId,
        [Description("How long to sample; the process should be busy with the work to look at meanwhile.")] int seconds = 5,
        [Description("Optional regex: counts only the threads that allocated from a method whose name matches, e.g. 'WorldLoop\\.OnDay'.")] string? threads = null,
        [Description("How many types and stacks to return, those that allocate the most first.")] int top = 20,
        [Description("How many frames to return per stack, from the innermost.")] int maxFrames = 10,
        CancellationToken cancellationToken = default) =>
        ProcessTracing.AllocationsAsync(processId, TimeSpan.FromSeconds(seconds), ProcessDefaults.Pattern(threads, "threads"), top, maxFrames, cancellationToken);
}

public sealed class ProcessCountersTool : IMcpTool
{
    public Delegate Handler => CountersAsync;

    [McpServerTool(Name = "process_counters", ReadOnly = true)]
    [Description("Reads the runtime counters of a running .NET process, as dotnet-counters does: CPU usage, working set, GC heap size and "
        + "collection counts, allocation rate, thread pool threads and queue length, lock contention, exceptions per second and more.")]
    private static Task<IReadOnlyList<ProcessTracing.CounterValue>> CountersAsync(
        [Description("The process id, e.g. from dotnet_processes.")] int processId,
        [Description("How long to listen; the counters report once a second.")] int seconds = 3,
        CancellationToken cancellationToken = default) =>
        ProcessTracing.CountersAsync(processId, TimeSpan.FromSeconds(seconds), cancellationToken);
}
