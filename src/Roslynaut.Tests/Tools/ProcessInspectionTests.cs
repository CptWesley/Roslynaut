using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>dotnet_processes, process_threads and process_heap, against a script host that sleeps in a known method.</summary>
[NonParallelizable]
public class ProcessInspectionTests
{
    private readonly PdbService pdbs = new();
    private Process target = null!;

    [OneTimeSetUp]
    public void StartTarget()
    {
        var start = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", "..", "dotnet.exe"))
        {
            ArgumentList = { "exec", Path.Combine(AppContext.BaseDirectory, "Roslynaut.ScriptHost.dll") },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        target = Process.Start(start)!;

        // A second thread waits for the lock the sleeping one holds, which gives the lock a sync block. A third holds a System.Threading.Lock.
        const string code = """
            class Marker { public int Count = 42; public string Name = "marker"; }
            var kept = new Marker();
            var gate = new System.Threading.Lock();
            new System.Threading.Thread(() => { lock (gate) { System.Threading.Thread.Sleep(60000); } }) { IsBackground = true }.Start();
            lock (kept)
            {
                new System.Threading.Thread(() => { lock (kept) { } }) { IsBackground = true }.Start();
                System.Threading.Thread.Sleep(60000);
            }
            kept
            """;
        target.StandardInput.Write(System.Text.Json.JsonSerializer.Serialize(new { Code = code, References = Array.Empty<string>() }));
        target.StandardInput.Close();
    }

    [OneTimeTearDown]
    public void StopTarget()
    {
        target.Kill(entireProcessTree: true);
        target.Dispose();
    }

    /// <summary>The target's threads once it sleeps in the script.</summary>
    private async Task<IReadOnlyList<ProcessInspection.ThreadEntry>> SleepingThreadsAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            // Right after it starts, the target has not loaded the runtime yet.
            var threads = attempt == 40 ? ProcessInspection.Threads(target.Id, null, maxFrames: 50, pdbs) : TryThreads();
            if (threads?.Any(thread => thread.Stack.Any(frame => frame.Contains("Thread.Sleep"))) == true || attempt == 40)
                return threads!;
            await Task.Delay(250);
        }
    }

    private IReadOnlyList<ProcessInspection.ThreadEntry>? TryThreads()
    {
        try
        {
            return ProcessInspection.Threads(target.Id, null, maxFrames: 50, pdbs);
        }
        catch (ModelContextProtocol.McpException)
        {
            return null;
        }
    }

    [Test]
    public async Task Threads_ShowCallStacks()
    {
        (await SleepingThreadsAsync()).Should().Contain(thread => thread.Stack.Any(frame => frame.Contains("System.Threading.Thread.Sleep")));
    }

    // The script host's PDB sits next to it, so its frames have source lines.
    [Test]
    public async Task Threads_FramesWithPdb_HaveSourceLines()
    {
        (await SleepingThreadsAsync()).SelectMany(thread => thread.Stack)
            .Should().Contain(frame => frame.Contains("Roslynaut.ScriptHost") && frame.Contains($"{Path.DirectorySeparatorChar}Program.cs:line "));
    }

    [Test]
    public async Task Heap_CountsTypes()
    {
        await SleepingThreadsAsync();

        var heap = ProcessInspection.Summarize("1", ProcessInspection.Snapshot(target.Id, null), null, null, top: 1000);
        heap.Objects.Should().BePositive();
        heap.Types.Should().Contain(type => type.Type == "System.String" && type.Count > 0);
        ProcessInspection.Summarize("1", ProcessInspection.Snapshot(target.Id, null), null, new("^System\\.String$"), top: 10).Types.Should().ContainSingle();
    }

    [Test]
    public async Task Objects_ShowFields_AndLockOwner_AndRoots()
    {
        await SleepingThreadsAsync();

        var marker = ProcessInspection.Objects(target.Id, null, null, new("\\+Marker$"), top: 10).Should().ContainSingle().Subject;
        marker.Fields.Should().Contain(new ProcessInspection.FieldValue("Count", "System.Int32", "42"));
        marker.Fields.Should().Contain(field => field.Name == "Name" && field.Value.StartsWith("\"marker\" @0x"));
        marker.LockedBy.Should().NotBeNull();

        ProcessInspection.Objects(target.Id, null, ProcessInspection.ParseAddress(marker.Address), null, top: 1).Should().ContainSingle().Which.Type.Should().EndWith("+Marker");
        ProcessInspection.Roots(target.Id, null, ProcessInspection.ParseAddress(marker.Address), top: 1, CancellationToken.None)
            .Should().ContainSingle().Which.Chain[^1].Should().Contain(marker.Address);
    }

    [Test]
    public async Task Threads_ShowLocksOthersWaitFor()
    {
        await SleepingThreadsAsync();

        // The waiting thread may still be spinning before it blocks.
        for (var attempt = 0; ; attempt++)
        {
            var holder = ProcessInspection.Threads(target.Id, null, maxFrames: 50, pdbs).SingleOrDefault(thread => thread.HeldLocks.Count > 0);
            if (holder is not null || attempt == 20)
            {
                holder.Should().NotBeNull();
                holder!.HeldLocks.Should().ContainSingle().Which.Should().Match("*+Marker @0x*, 1 waiting");
                return;
            }

            await Task.Delay(250);
        }
    }

    [Test]
    public async Task Threads_WithLockObjects_ShowTheLockOwner()
    {
        await SleepingThreadsAsync();

        ProcessInspection.Threads(target.Id, null, maxFrames: 5, pdbs).SelectMany(thread => thread.HeldLocks).Should().NotContain(held => held.StartsWith("System.Threading.Lock"));
        ProcessInspection.Threads(target.Id, null, maxFrames: 5, pdbs, lockObjects: true)
            .Should().ContainSingle(thread => thread.HeldLocks.Any(held => held.StartsWith("System.Threading.Lock @0x")))
            .Which.Stack.Should().Contain(frame => frame.Contains("Thread.Sleep"));
    }

    [Test]
    public void Summarize_AgainstEarlierSnapshot_PutsTheTypesThatGrewFirst()
    {
        var before = new ProcessInspection.HeapSnapshot(new Dictionary<string, (long, long)> { ["A"] = (1, 10), ["B"] = (5, 50), ["Gone"] = (1, 7) });
        var now = new ProcessInspection.HeapSnapshot(new Dictionary<string, (long, long)> { ["A"] = (3, 30), ["B"] = (5, 50), ["New"] = (1, 5) });

        var summary = ProcessInspection.Summarize("2", now, before, null, top: 10);

        (summary.ObjectsChange, summary.SizeChange).Should().Be((2L, 18L));
        summary.Types.Select(type => $"{type.Type} {type.CountChange:+0;-0;0} {type.SizeChange:+0;-0;0}").Should().Equal("A +2 +20", "New +1 +5", "B 0 0", "Gone -1 -7");
    }

    [Test]
    public async Task Dump_HasThreadsAndHeap()
    {
        await SleepingThreadsAsync();
        var dump = Path.Combine(Path.GetTempPath(), $"roslynaut-{Guid.NewGuid():N}.dmp");
        try
        {
            new DiagnosticsClient(target.Id).WriteDump(DumpType.WithHeap, dump);

            ProcessInspection.Threads(null, dump, maxFrames: 50, pdbs)
                .Should().Contain(thread => thread.Stack.Any(frame => frame.Contains("System.Threading.Thread.Sleep")));
            ProcessInspection.Summarize("1", ProcessInspection.Snapshot(null, dump), null, new("^System\\.String$"), top: 10).Types.Should().ContainSingle(type => type.Count > 0);
        }
        finally
        {
            File.Delete(dump);
        }
    }

    [Test]
    public void Processes_IncludeTarget()
    {
        ProcessInspection.List().Should().Contain(process => process.Id == target.Id && process.Name == "dotnet");
    }
}

/// <summary>process_cpu and process_counters, against a script host that spins in a known method.</summary>
[NonParallelizable]
public class ProcessTracingTests
{
    private Process target = null!;

    [OneTimeSetUp]
    public async Task StartTarget()
    {
        var start = new ProcessStartInfo(DotnetCli.Executable)
        {
            ArgumentList = { "exec", Path.Combine(AppContext.BaseDirectory, "Roslynaut.ScriptHost.dll") },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        target = Process.Start(start)!;
        const string code = """
            class Spin { public static long Work() { long x = 0; for (long i = 0; ; i++) x += i % 7; } }
            Spin.Work()
            """;
        target.StandardInput.Write(System.Text.Json.JsonSerializer.Serialize(new { Code = code, References = Array.Empty<string>() }));
        target.StandardInput.Close();

        // Its runtime listens for diagnostics once it starts; the script compiles before it spins.
        for (var attempt = 0; attempt < 40 && !DiagnosticsClient.GetPublishedProcesses().Contains(target.Id); attempt++)
            await Task.Delay(250);
        await Task.Delay(3000);
    }

    [OneTimeTearDown]
    public void StopTarget()
    {
        target.Kill(entireProcessTree: true);
        target.Dispose();
    }

    [Test]
    public async Task Cpu_FindsTheSpinningMethod()
    {
        var profile = await ProcessTracing.SampleAsync(target.Id, TimeSpan.FromSeconds(2), top: 10, CancellationToken.None);

        profile.Samples.Should().BePositive();
        profile.Methods[0].Should().Match<ProcessTracing.MethodSamples>(method => method.Method.Contains("Spin.Work") && method.Self > 50);
    }

    [Test]
    public async Task Counters_HaveTheRuntimes()
    {
        var counters = await ProcessTracing.CountersAsync(target.Id, TimeSpan.FromSeconds(3), CancellationToken.None);

        counters.Should().Contain(counter => counter.Name == "cpu-usage" && counter.Value > 0).And.Contain(counter => counter.Name == "working-set");
    }
}
