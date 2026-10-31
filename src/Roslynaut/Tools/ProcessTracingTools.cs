using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.Tracing;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Analysis;
using TraceLog = Microsoft.Diagnostics.Tracing.Etlx.TraceLog;
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
    public static Task<CpuSamples> SampleAsync(TraceTarget target, TimeSpan duration, CancellationToken cancellationToken)
    {
        EventPipeProvider[] providers =
        [
            new("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
            new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, (long)ClrTraceEventParser.Keywords.Default),
        ];

        return TraceAsync(target, providers, duration, (log, elapsed) =>
        {
            // Its Type is an enum of TraceEvent's, ClrThreadSampleType, whose 2 is Managed. Threads repeat the same stacks, which the log numbers.
            var stacks = new Dictionary<(int Thread, CallStackIndex Stack), int>();
            foreach (var sample in log.Events.Where(item => item.ProviderName == "Microsoft-DotNETCore-SampleProfiler" && Convert.ToInt32(item.PayloadByName("Type")) == 2))
            {
                if (sample.CallStackIndex() is var stack && stack != CallStackIndex.Invalid)
                    stacks[(sample.ThreadID, stack)] = stacks.GetValueOrDefault((sample.ThreadID, stack)) + 1;
            }

            return new CpuSamples(elapsed, [.. stacks.Select(entry => new StackSamples(entry.Key.Thread, FramesOf(log.CallStacks[entry.Key.Stack]), entry.Value))]);
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
    public static async Task<AllocationProfile> AllocationsAsync(TraceTarget target, TimeSpan duration, Regex? threads, int top, int maxFrames, CancellationToken cancellationToken)
    {
        // Allocation ticks are verbose GC events.
        EventPipeProvider[] providers =
        [
            new("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose,
                (long)(ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.Jit | ClrTraceEventParser.Keywords.Loader)),
        ];
        var ticks = await TraceAsync(target, providers, duration, (log, _) => log.Events.OfType<GCAllocationTickTraceData>()
            .Select(tick => new AllocationTick(tick.ThreadID, tick.TypeName, (ulong)tick.TypeID, tick.AllocationAmount64, FramesOf(tick.CallStack())))
            .ToList(), cancellationToken);

        if (threads is not null)
        {
            var picked = ticks.Where(tick => tick.Frames.Any(threads.IsMatch)).Select(tick => tick.Thread).ToHashSet();
            ticks = [.. ticks.Where(tick => picked.Contains(tick.Thread))];
        }

        // Ticks name a nested type without the type it is in; the runtime itself names it in full.
        // ponytail: a launched program has ended by now, so its types keep the ticks' names and finalizers go unnoticed; describe them before it ends if that matters.
        var described = target.ProcessId is { } processId ? Describe(processId, ticks.Select(tick => tick.TypeId).Distinct()) : [];
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
    public static async Task<IReadOnlyList<CounterValue>> CountersAsync(TraceTarget target, TimeSpan duration, CancellationToken cancellationToken)
    {
        EventPipeProvider[] providers = [new("System.Runtime", EventLevel.Informational, -1, new Dictionary<string, string> { ["EventCounterIntervalSec"] = "1" })];
        var values = new ConcurrentDictionary<string, CounterValue>();
        await RecordAsync(target, providers, requestRundown: false, duration, stream =>
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

    /// <summary>The exceptions thrown while it listened, by type and the method that threw them, with where they were caught.</summary>
    public static Task<ProcessEvents> ExceptionsAsync(TraceTarget target, TimeSpan duration, Regex? filter, int top, int maxFrames, CancellationToken cancellationToken)
    {
        EventPipeProvider[] providers = [new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, (long)ClrTraceEventParser.Keywords.Default)];
        return TraceAsync(target, providers, duration, (log, elapsed) =>
        {
            var thrown = new List<(string Type, string Message, IReadOnlyList<string> Stack, string? CaughtIn)>();
            var pending = new Dictionary<int, int>();
            foreach (var item in log.Events)
            {
                if (item.EventName == "Exception/Start")
                {
                    pending[item.ThreadID] = thrown.Count;
                    // The stack starts in the runtime's exception dispatch, which throw calls.
                    thrown.Add((item.PayloadByName("ExceptionType") as string ?? "?", item.PayloadByName("ExceptionMessage") as string ?? "",
                        [.. FramesOf(item.CallStack()).SkipWhile(frame => frame.StartsWith("System.Runtime.EH.", StringComparison.Ordinal))], null));
                }
                else if (item.EventName == "ExceptionCatch/Start" && pending.Remove(item.ThreadID, out var index))
                {
                    thrown[index] = thrown[index] with { CaughtIn = item.PayloadByName("MethodName") as string };
                }
            }

            return new ProcessEvents(Math.Round(elapsed.TotalSeconds, 1), Exceptions: [.. thrown
                .Where(exception => filter is null || filter.IsMatch(exception.Type) || exception.Stack.Any(filter.IsMatch))
                .GroupBy(exception => (exception.Type, ThrownIn: exception.Stack.FirstOrDefault() ?? "?"))
                .Select(group => new ExceptionGroup(group.Key.Type, group.Key.ThrownIn, group.Count(), group.First().Message,
                    [.. group.Select(exception => exception.CaughtIn ?? "(not caught)").Distinct()], [.. group.First().Stack.Take(maxFrames)]))
                .OrderByDescending(group => group.Count)
                .Take(top)]);
        }, cancellationToken);
    }

    /// <summary>
    /// What the program logs through Microsoft.Extensions.Logging, from Debug up, through its EventSource logger, which the default hosts add.
    /// Each message comes as a formatted message, then as JSON with the exception, if any.
    /// </summary>
    public static async Task<ProcessEvents> LogsAsync(TraceTarget target, TimeSpan duration, Regex? category, int level, int top, CancellationToken cancellationToken)
    {
        // FormattedMessage and JsonMessage.
        EventPipeProvider[] providers = [new("Microsoft-Extensions-Logging", EventLevel.Verbose, 0x4 | 0x8)];
        var logs = new List<LogEntry>();
        var elapsed = await RecordAsync(target, providers, requestRundown: false, duration, stream =>
        {
            using var source = new EventPipeEventSource(stream);
            source.Dynamic.All += item =>
            {
                if (item.PayloadByName("LoggerName") is not string logger || item.PayloadByName("Level") is not { } value || Convert.ToInt32(value) < level
                    || (category is not null && !category.IsMatch(logger)))
                    return;

                lock (logs)
                {
                    if (item.EventName == "FormattedMessage")
                        logs.Add(new(item.TimeStamp, LogLevels.ElementAtOrDefault(Convert.ToInt32(value)) ?? value.ToString()!, logger,
                            Convert.ToInt32(item.PayloadByName("EventId")), item.PayloadByName("FormattedMessage") as string ?? "", null));
                    else if (item.EventName == "MessageJson" && item.PayloadByName("ExceptionJson") is string { Length: > 2 } exception && logs.Count > 0 && logs[^1].Category == logger)
                        logs[^1] = logs[^1] with { Exception = exception };
                }
            };
            source.Process();
            return Task.CompletedTask;
        }, cancellationToken);

        return new(Math.Round(elapsed.TotalSeconds, 1), Logs: [.. logs.TakeLast(top)]);
    }

    private static readonly string[] LogLevels = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];

    /// <summary>
    /// The time threads waited for locks the runtime knows (Monitor, System.Threading.Lock), by the stack that waited, with the threads that
    /// held them where the runtime reports them (from .NET 8).
    /// </summary>
    public static Task<ProcessEvents> ContentionAsync(TraceTarget target, TimeSpan duration, Regex? filter, int top, int maxFrames, CancellationToken cancellationToken)
    {
        EventPipeProvider[] providers = [new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, (long)ClrTraceEventParser.Keywords.Default)];
        return TraceAsync(target, providers, duration, (log, elapsed) =>
        {
            var waits = new List<(IReadOnlyList<string> Stack, double Milliseconds, string? Owner)>();
            var started = new Dictionary<int, (TraceEvent Start, IReadOnlyList<string> Stack)>();
            foreach (var item in log.Events)
            {
                if (item.EventName == "Contention/Start")
                {
                    started[item.ThreadID] = (item, FramesOf(item.CallStack()));
                }
                else if (item.EventName == "Contention/Stop" && started.Remove(item.ThreadID, out var start))
                {
                    var milliseconds = item.PayloadByName("DurationNs") is { } nanoseconds ? Convert.ToDouble(nanoseconds) / 1e6 : item.TimeStampRelativeMSec - start.Start.TimeStampRelativeMSec;
                    var owner = start.Start.PayloadByName("LockOwnerThreadID") is { } id && Convert.ToUInt64(id) != 0 ? $"thread 0x{Convert.ToUInt64(id):x}" : null;
                    waits.Add((start.Stack, milliseconds, owner));
                }
            }

            var kept = waits.Where(wait => filter is null || wait.Stack.Any(filter.IsMatch)).ToList();
            return new ProcessEvents(Math.Round(elapsed.TotalSeconds, 1), Contention: new(kept.Count, Math.Round(kept.Sum(wait => wait.Milliseconds), 1), [.. kept
                .GroupBy(wait => string.Join('\n', wait.Stack.Take(maxFrames)))
                .Select(group => new ContentionStack(group.Count(), Math.Round(group.Sum(wait => wait.Milliseconds), 1), Math.Round(group.Max(wait => wait.Milliseconds), 1),
                    [.. group.Select(wait => wait.Owner).OfType<string>().Distinct()], [.. group.First().Stack.Take(maxFrames)]))
                .OrderByDescending(stack => stack.WaitMs)
                .Take(top)]));
        }, cancellationToken);
    }

    /// <summary>The garbage collections, with how long each paused the program, and the stacks that induced them with GC.Collect.</summary>
    public static Task<ProcessEvents> CollectionsAsync(TraceTarget target, TimeSpan duration, int top, int maxFrames, CancellationToken cancellationToken)
    {
        EventPipeProvider[] providers = [new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, (long)ClrTraceEventParser.Keywords.Default)];
        return TraceAsync(target, providers, duration, (log, elapsed) =>
        {
            var collections = new List<Microsoft.Diagnostics.Tracing.Analysis.GC.TraceGC>();
            var source = log.Events.GetSource();
            source.NeedLoadedDotNetRuntimes();
            source.AddCallbackOnProcessStart(process => process.AddCallbackOnDotNetRuntimeLoad(runtime => runtime.GCEnd += (_, gc) => collections.Add(gc)));
            source.Process();

            // GCStart comes from the GC's own thread; GCTriggered from the one that asked for it, with its stack. Reason 1 is Induced.
            var induced = log.Events.Where(item => item.EventName == "GC/Triggered" && Convert.ToInt32(item.PayloadByName("Reason")) == (int)GCReason.Induced)
                .Select(triggered => FramesOf(triggered.CallStack()))
                .GroupBy(stack => string.Join('\n', stack.Take(maxFrames)))
                .Select(group => new InducedCollections(group.Count(), [.. group.First().Take(maxFrames)]))
                .OrderByDescending(stack => stack.Count)
                .ToList();
            var paused = collections.Sum(gc => gc.PauseDurationMSec);
            return new ProcessEvents(Math.Round(elapsed.TotalSeconds, 1), Collections: new(
                collections.Count,
                Math.Round(paused, 1),
                Percent((long)(paused * 1000), (long)elapsed.TotalMicroseconds),
                Math.Round(collections.Select(gc => gc.PauseDurationMSec).DefaultIfEmpty().Max(), 1),
                [.. collections.OrderByDescending(gc => gc.PauseDurationMSec).Take(top).OrderBy(gc => gc.Number).Select(gc => new Collection(
                    gc.Number, gc.Generation, gc.Reason.ToString(), gc.Type.ToString(), Math.Round(gc.PauseDurationMSec, 2), Math.Round(gc.HeapSizeBeforeMB, 1), Math.Round(gc.HeapSizeAfterMB, 1)))],
                induced));
        }, cancellationToken);
    }

    /// <summary>
    /// The durations of the HTTP requests the program served (ASP.NET Core) and made (HttpClient), by route or host, from their metrics (from
    /// .NET 8): the runtime publishes each histogram's percentiles every second, which are weighed by their counts.
    /// </summary>
    public static async Task<ProcessEvents> RequestsAsync(TraceTarget target, TimeSpan duration, Regex? filter, int top, CancellationToken cancellationToken)
    {
        var session = Guid.NewGuid().ToString();
        EventPipeProvider[] providers = [new("System.Diagnostics.Metrics", EventLevel.Informational, 0x2, new Dictionary<string, string>
        {
            ["SessionId"] = session,
            ["Metrics"] = "Microsoft.AspNetCore.Hosting\\http.server.request.duration,System.Net.Http\\http.client.request.duration",
            ["RefreshInterval"] = "1",
            ["MaxTimeSeries"] = "1000",
            ["MaxHistograms"] = "1000",
        })];
        var intervals = new List<(string Kind, string Key, string Method, string Status, long Count, IReadOnlyDictionary<string, double> Quantiles, double Scale)>();
        var elapsed = await RecordAsync(target, providers, requestRundown: false, duration, stream =>
        {
            using var source = new EventPipeEventSource(stream);
            source.Dynamic.All += item =>
            {
                if (item.EventName != "HistogramValuePublished" || item.PayloadByName("sessionId") as string != session || item.PayloadByName("count") is not { } count
                    || Convert.ToInt64(count) == 0)
                    return;

                var tags = Pairs(item.PayloadByName("tags") as string, ',');
                var server = item.PayloadByName("instrumentName") as string == "http.server.request.duration";
                var key = server ? tags.GetValueOrDefault("http.route") ?? "?" : $"{tags.GetValueOrDefault("server.address")}:{tags.GetValueOrDefault("server.port")}";
                var quantiles = Pairs(item.PayloadByName("quantiles") as string, ';').ToDictionary(pair => pair.Key, pair => double.TryParse(pair.Value,
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0);
                var scale = item.PayloadByName("unit") as string == "s" ? 1000 : 1;
                lock (intervals)
                    intervals.Add((server ? "Served" : "Sent", key, tags.GetValueOrDefault("http.request.method") ?? "?", tags.GetValueOrDefault("http.response.status_code") ?? "-",
                        Convert.ToInt64(count), quantiles, scale));
            };
            source.Process();
            return Task.CompletedTask;
        }, cancellationToken);

        return new(Math.Round(elapsed.TotalSeconds, 1), Requests: [.. intervals
            .Where(interval => filter is null || filter.IsMatch(interval.Key))
            .GroupBy(interval => (interval.Kind, interval.Key, interval.Method, interval.Status))
            .Select(group =>
            {
                var count = group.Sum(interval => interval.Count);
                double Weighed(string quantile) => Math.Round(group.Sum(interval => interval.Quantiles.GetValueOrDefault(quantile) * interval.Scale * interval.Count) / count, 1);
                return new RequestStats(group.Key.Kind, group.Key.Key, group.Key.Method, group.Key.Status, count, Weighed("0.5"), Weighed("0.95"),
                    Math.Round(group.Max(interval => interval.Quantiles.GetValueOrDefault("0.99") * interval.Scale), 1));
            })
            .OrderByDescending(stats => stats.Count * stats.P95Ms)
            .Take(top)]);

        static Dictionary<string, string> Pairs(string? text, char separator) => (text ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)).Where(pair => pair.Length == 2).GroupBy(pair => pair[0]).ToDictionary(pair => pair.Key, pair => pair.First()[1]);
    }

    /// <summary>Records a trace, with the rundown at its end that names the methods that ran, and reads it with how long it ran.</summary>
    private static async Task<T> TraceAsync<T>(TraceTarget target, EventPipeProvider[] providers, TimeSpan duration, Func<TraceLog, TimeSpan, T> read, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("roslynaut-trace-");
        try
        {
            var trace = Path.Combine(directory.FullName, "trace.nettrace");
            var elapsed = await RecordAsync(target, providers, requestRundown: true, duration, async stream =>
            {
                await using var file = File.Create(trace);
                await stream.CopyToAsync(file);
            }, cancellationToken);

            using var log = new TraceLog(TraceLog.CreateFromEventPipeDataFile(trace, Path.Combine(directory.FullName, "trace.etlx"), new() { ContinueOnError = target.Program is not null }));
            return read(log, elapsed);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Runs an EventPipe session for a while, or for a launched program until it exits if that comes first, and hands its stream to a reader,
    /// which ends when the session stops. Returns how long it ran.
    /// </summary>
    private static async Task<TimeSpan> RecordAsync(
        TraceTarget target, EventPipeProvider[] providers, bool requestRundown, TimeSpan duration, Func<Stream, Task> read, CancellationToken cancellationToken)
    {
        await using var launched = target.Program is { } program ? await TraceTarget.StartAsync(program, cancellationToken) : null;
        var client = launched?.Client ?? new DiagnosticsClient(target.ProcessId!.Value);
        EventPipeSession session;
        try
        {
            session = client.StartEventPipeSession(providers, requestRundown);
            launched?.Client.ResumeRuntime();
        }
        catch (Exception e) when (e is ServerNotAvailableException or UnsupportedCommandException or ArgumentException)
        {
            throw new McpException($"Cannot trace process {target.ProcessId}; is it a running .NET process? {e.Message}");
        }

        using (session)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var reading = Task.Run(() => read(session.EventStream), CancellationToken.None);
            try
            {
                // A launched program's stream ends when it exits.
                await Task.WhenAny(Task.Delay(duration, cancellationToken), launched is null ? Task.Delay(Timeout.Infinite, cancellationToken) : reading);
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                // Stopping ends the stream, after the rundown if there is one; a program that exited has ended it already.
                try
                {
                    session.Stop();
                }
                catch (Exception e) when (launched is not null && e is ServerNotAvailableException or IOException or EndOfStreamException or TimeoutException)
                {
                }

                try
                {
                    await reading;
                }
                catch (FormatException) when (launched is not null)
                {
                    // A program that was killed, as VSTest ends its test host, cuts its stream off mid-block; the events before count.
                }
            }

            return watch.Elapsed;
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

    /// <param name="Seconds">How long it listened.</param>
    /// <param name="Exceptions">The exceptions thrown, by type and the method that threw them, most first.</param>
    /// <param name="Logs">The last log messages, oldest first.</param>
    /// <param name="Contention">The waits for locks.</param>
    /// <param name="Collections">The garbage collections.</param>
    /// <param name="Requests">The HTTP requests served and sent.</param>
    public sealed record ProcessEvents(
        double Seconds, IReadOnlyList<ExceptionGroup>? Exceptions = null, IReadOnlyList<LogEntry>? Logs = null, ContentionSummary? Contention = null,
        CollectionSummary? Collections = null, IReadOnlyList<RequestStats>? Requests = null);

    /// <param name="ThrownIn">The method that threw them.</param>
    /// <param name="Message">The message of the first of them.</param>
    /// <param name="CaughtIn">The methods whose catch handled them, or "(not caught)" for those that were not while it listened.</param>
    /// <param name="Stack">The stack of the first of them, innermost first.</param>
    public sealed record ExceptionGroup(string Type, string ThrownIn, int Count, string Message, IReadOnlyList<string> CaughtIn, IReadOnlyList<string> Stack);

    /// <param name="Category">The logger's name, usually its class.</param>
    /// <param name="Exception">The exception logged with the message, as JSON.</param>
    public sealed record LogEntry(DateTime Time, string Level, string Category, int EventId, string Message, string? Exception);

    /// <param name="WaitMs">How long threads waited for locks in all.</param>
    /// <param name="Stacks">The stacks that waited, longest in all first.</param>
    public sealed record ContentionSummary(int Waits, double WaitMs, IReadOnlyList<ContentionStack> Stacks);

    /// <param name="Owners">The threads that held the lock, by operating system id, where the runtime reports them.</param>
    /// <param name="Stack">Innermost first: where the thread waited.</param>
    public sealed record ContentionStack(int Waits, double WaitMs, double MaxMs, IReadOnlyList<string> Owners, IReadOnlyList<string> Stack);

    /// <param name="PausedMs">How long the collections paused the program in all.</param>
    /// <param name="PausedShare">The percentage of the time listened that the program was paused.</param>
    /// <param name="Longest">The longest pauses, in order.</param>
    /// <param name="Induced">The stacks that called GC.Collect, most first.</param>
    public sealed record CollectionSummary(int Count, double PausedMs, double PausedShare, double MaxPauseMs, IReadOnlyList<Collection> Longest, IReadOnlyList<InducedCollections> Induced);

    /// <param name="Reason">Why it ran, such as AllocSmall, Induced or LowMemory.</param>
    /// <param name="Type">NonConcurrentGC (blocking), BackgroundGC or ForegroundGC.</param>
    public sealed record Collection(int Number, int Generation, string Reason, string Type, double PauseMs, double HeapBeforeMb, double HeapAfterMb);

    /// <param name="Stack">Innermost first, from GC.Collect out.</param>
    public sealed record InducedCollections(int Count, IReadOnlyList<string> Stack);

    /// <param name="Kind">Served: requests the program handled, by route; Sent: requests it made, by host and port.</param>
    /// <param name="Target">The route template, such as "orders/{id}", or the host and port.</param>
    /// <param name="Status">The HTTP status code; "-" for a request that got none, such as one that failed to connect.</param>
    /// <param name="P99Ms">The highest 99th percentile of any second.</param>
    public sealed record RequestStats(string Kind, string Target, string Method, string Status, long Count, double P50Ms, double P95Ms, double P99Ms);
}

[JsonConverter(typeof(JsonStringEnumConverter<EventKind>))]
public enum EventKind
{
    Exceptions,
    Logs,
    Contention,
    Collections,
    Requests,
}

public sealed class ProcessEventsTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => ListenAsync;

    [McpServerTool(Name = "process_events", Destructive = true, OpenWorld = true)]
    [Description("Listens to a running .NET process, or a program or test project it starts with launch, for a while, and summarizes one kind of "
        + "runtime event. Exceptions: those thrown, by type and the method that threw them, with a message, the stack and where they were caught, "
        + "also those caught and logged nowhere. Logs: the Microsoft.Extensions.Logging messages from Debug up, without a restart (needs the "
        + "EventSource logger, which the default hosts add). Contention: the time threads waited for locks, by the stack that waited. Collections: "
        + "the garbage collections with their pauses and reasons, and who called GC.Collect. Requests: the HTTP requests served (by route) and "
        + "sent (by host) with their p50, p95 and p99 durations, from .NET 8. The process keeps running.")]
    private Task<ProcessTracing.ProcessEvents> ListenAsync(
        [Description("Exceptions, Logs, Contention, Collections or Requests.")] EventKind kind,
        [Description("The process id, e.g. from dotnet_processes; or else pass launch.")] int? processId = null,
        [Description(TraceTarget.LaunchDescription)] string? launch = null,
        [Description(TraceTarget.ArgumentsDescription)] string[]? arguments = null,
        [Description(TraceTarget.TestFilterDescription)] string? testFilter = null,
        [Description("How long to listen; the process should do the work to look at meanwhile. With launch, at most this long.")] int seconds = 5,
        [Description("Optional regex: the exception types or stacks, the log categories, the waiting stacks or the routes and hosts to keep, e.g. 'MyApp\\.'.")] string? filter = null,
        [Description("For Logs: the least level, Debug, Information, Warning, Error or Critical.")] string level = "Debug",
        [Description("How many groups, messages, stacks or collections to return.")] int top = 20,
        [Description("How many frames to return per stack, from the innermost.")] int maxFrames = 10,
        CancellationToken cancellationToken = default)
    {
        var target = TraceTarget.Of(processId, launch, arguments, testFilter, workspace);
        var (duration, pattern) = (TimeSpan.FromSeconds(seconds), ProcessDefaults.Pattern(filter, "filter"));
        return kind switch
        {
            EventKind.Exceptions => ProcessTracing.ExceptionsAsync(target, duration, pattern, top, maxFrames, cancellationToken),
            EventKind.Logs => ProcessTracing.LogsAsync(target, duration, pattern, LevelOf(level), top, cancellationToken),
            EventKind.Contention => ProcessTracing.ContentionAsync(target, duration, pattern, top, maxFrames, cancellationToken),
            EventKind.Collections => ProcessTracing.CollectionsAsync(target, duration, top, maxFrames, cancellationToken),
            _ => ProcessTracing.RequestsAsync(target, duration, pattern, top, cancellationToken),
        };
    }

    private static int LevelOf(string level) => Array.FindIndex(["Trace", "Debug", "Information", "Warning", "Error", "Critical"], name => name.Equals(level, StringComparison.OrdinalIgnoreCase)) switch
    {
        < 0 => throw new McpException($"'{level}' is not a log level: Debug, Information, Warning, Error or Critical."),
        var index => index,
    };
}

public sealed class ProcessCpuTool(WorkspaceService workspace) : IMcpTool
{
    private const int MaxProfiles = 10;

    // ponytail: profiles live in memory, the last 10; each holds its distinct stacks with their counts.
    private readonly ConcurrentDictionary<string, ProcessTracing.CpuSamples> _profiles = new();
    private int _lastId;

    public Delegate Handler => SampleAsync;

    [McpServerTool(Name = "process_cpu", Destructive = true, OpenWorld = true)]
    [Description("Profiles where a running .NET process spends its time, as dotnet-trace's CPU sampling does: samples managed thread stacks for a "
        + "while and returns the threads by how busy they were, and the methods that run most, with their own share of the samples and their "
        + "share including what they call. Filters pick the threads that run a method and leave out samples, such as of waits; root gives the call "
        + "tree under a method, leaf the call paths into one. Each result has a Profile, to analyze the same samples again with other filters, or "
        + "to compare a later profile with, as before and after a change. For slow code and high CPU; symbol_asm shows the code of a hot method. "
        + "The process keeps running. With launch, it starts a program or a test project instead and profiles it from its start, for code "
        + "that ends before it could be attached to, such as a slow test.")]
    private async Task<ProcessTracing.CpuProfile> SampleAsync(
        [Description("The process id, e.g. from dotnet_processes; or else pass launch or profile.")] int? processId = null,
        [Description(TraceTarget.LaunchDescription)] string? launch = null,
        [Description(TraceTarget.ArgumentsDescription)] string[]? arguments = null,
        [Description(TraceTarget.TestFilterDescription)] string? testFilter = null,
        [Description("How long to sample; the process should be busy with the work to look at meanwhile. With launch, at most this long.")] int seconds = 5,
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
        if ((processId, launch, profile) is (null, null, { } id))
            return ProcessTracing.Analyze(id, Stored(id), baseline, filters, top);
        if (profile is not null)
            throw new McpException("Pass one of processId, launch or profile.");

        var target = TraceTarget.Of(processId, launch, arguments, testFilter, workspace);
        var samples = await ProcessTracing.SampleAsync(target, TimeSpan.FromSeconds(seconds), cancellationToken);
        var stored = Interlocked.Increment(ref _lastId).ToString();
        _profiles[stored] = samples;
        _profiles.TryRemove((int.Parse(stored) - MaxProfiles).ToString(), out _);
        return ProcessTracing.Analyze(stored, samples, baseline, filters, top);
    }

    private ProcessTracing.CpuSamples Stored(string id) => _profiles.TryGetValue(id, out var samples)
        ? samples
        : throw new McpException($"There is no profile {id}; only the last {MaxProfiles} are kept, until the server restarts.");
}

public sealed class ProcessAllocationsTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => ProfileAsync;

    [McpServerTool(Name = "process_allocations", Destructive = true, OpenWorld = true)]
    [Description("Profiles what a running .NET process allocates, as dotnet-trace's gc-verbose profile does: for a while, samples allocations "
        + "with the runtime's allocation ticks, one about every 100 KB, and estimates the bytes by thread, by type and by the stack that allocated "
        + "them; and lists the types allocated that have a finalizer, which cost more than their bytes. For GC pressure: process_counters gives the "
        + "allocation rate and GC counts, process_heap what stays alive. The process keeps running. With launch, it starts a program or a test "
        + "project instead and profiles it from its start; finalizers then go unnoticed.")]
    private Task<ProcessTracing.AllocationProfile> ProfileAsync(
        [Description("The process id, e.g. from dotnet_processes; or else pass launch.")] int? processId = null,
        [Description(TraceTarget.LaunchDescription)] string? launch = null,
        [Description(TraceTarget.ArgumentsDescription)] string[]? arguments = null,
        [Description(TraceTarget.TestFilterDescription)] string? testFilter = null,
        [Description("How long to sample; the process should be busy with the work to look at meanwhile. With launch, at most this long.")] int seconds = 5,
        [Description("Optional regex: counts only the threads that allocated from a method whose name matches, e.g. 'WorldLoop\\.OnDay'.")] string? threads = null,
        [Description("How many types and stacks to return, those that allocate the most first.")] int top = 20,
        [Description("How many frames to return per stack, from the innermost.")] int maxFrames = 10,
        CancellationToken cancellationToken = default) =>
        ProcessTracing.AllocationsAsync(TraceTarget.Of(processId, launch, arguments, testFilter, workspace), TimeSpan.FromSeconds(seconds),
            ProcessDefaults.Pattern(threads, "threads"), top, maxFrames, cancellationToken);
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
