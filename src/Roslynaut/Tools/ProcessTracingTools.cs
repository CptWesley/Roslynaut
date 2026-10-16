using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
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
    public static async Task<CpuProfile> SampleAsync(int processId, TimeSpan duration, int top, CancellationToken cancellationToken)
    {
        EventPipeProvider[] providers =
        [
            new("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
            new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, (long)ClrTraceEventParser.Keywords.Default),
        ];

        var directory = Directory.CreateTempSubdirectory("roslynaut-trace-");
        try
        {
            // The rundown at the end of the session names the methods that ran.
            var trace = Path.Combine(directory.FullName, "trace.nettrace");
            await RecordAsync(processId, providers, requestRundown: true, duration, async stream =>
            {
                await using var file = File.Create(trace);
                await stream.CopyToAsync(file);
            }, cancellationToken);

            using var log = new TraceLog(TraceLog.CreateFromEventPipeDataFile(trace, Path.Combine(directory.FullName, "trace.etlx")));
            var (self, total, samples) = (new Dictionary<string, int>(), new Dictionary<string, int>(), 0);
            // Its Type is an enum of TraceEvent's, ClrThreadSampleType, whose 2 is Managed.
            foreach (var sample in log.Events.Where(item => item.ProviderName == "Microsoft-DotNETCore-SampleProfiler" && Convert.ToInt32(item.PayloadByName("Type")) == 2))
            {
                if (sample.CallStack() is not { } stack)
                    continue;

                samples++;
                var counted = new HashSet<string>();
                for (var (frame, innermost) = (stack, true); frame is not null; (frame, innermost) = (frame.Caller, false))
                {
                    var method = frame.CodeAddress.FullMethodName is { Length: > 0 } name ? name : $"{frame.CodeAddress.ModuleName}!?";
                    if (innermost)
                        self[method] = self.GetValueOrDefault(method) + 1;
                    if (counted.Add(method))
                        total[method] = total.GetValueOrDefault(method) + 1;
                }
            }

            return new(samples, [.. total.OrderByDescending(entry => self.GetValueOrDefault(entry.Key)).ThenByDescending(entry => entry.Value).Take(top)
                .Select(entry => new MethodSamples(entry.Key, Percent(self.GetValueOrDefault(entry.Key), samples), Percent(entry.Value, samples)))]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }

        static double Percent(int count, int of) => of == 0 ? 0 : Math.Round(100.0 * count / of, 1);
    }

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

    /// <param name="Samples">How many stack samples of threads in managed code the profile has, one per thread per millisecond.</param>
    /// <param name="Methods">The methods that take the most time, with their own (Self) and their callees' (Total) share of the samples.</param>
    public sealed record CpuProfile(int Samples, IReadOnlyList<MethodSamples> Methods);

    /// <param name="Self">The percentage of samples in which the method itself was running.</param>
    /// <param name="Total">The percentage of samples in which the method was on the stack, itself or through what it called.</param>
    public sealed record MethodSamples(string Method, double Self, double Total);

    /// <param name="Value">For a gauge, its mean over the last second; for a count, how much it grew in the last second.</param>
    public sealed record CounterValue(string Name, string DisplayName, double Value, string? Units);
}

public sealed class ProcessCpuTool : IMcpTool
{
    public Delegate Handler => SampleAsync;

    [McpServerTool(Name = "process_cpu", ReadOnly = true)]
    [Description("Profiles where a running .NET process spends its time, as dotnet-trace's CPU sampling does: samples managed thread stacks for a "
        + "while and returns the methods that run most, with their own share of the samples and their share including what they call. For slow "
        + "code and high CPU; symbol_asm shows the code of a hot method. The process keeps running.")]
    private static Task<ProcessTracing.CpuProfile> SampleAsync(
        [Description("The process id, e.g. from dotnet_processes.")] int processId,
        [Description("How long to sample; the process should be busy with the work to look at meanwhile.")] int seconds = 5,
        [Description("How many methods to return, those that run themselves the most first.")] int top = 30,
        CancellationToken cancellationToken = default) =>
        ProcessTracing.SampleAsync(processId, TimeSpan.FromSeconds(seconds), top, cancellationToken);
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
