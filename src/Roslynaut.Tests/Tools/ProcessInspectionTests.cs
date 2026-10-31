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
        var start = new ProcessStartInfo(DotnetCli.Executable)
        {
            ArgumentList = { "exec", Path.Combine(AppContext.BaseDirectory, "Roslynaut.ScriptHost.dll") },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        target = Process.Start(start)!;

        // A second thread waits for the lock the sleeping one holds, which gives the lock a sync block. A third holds a System.Threading.Lock.
        const string code = """
            class Marker { public int Count = 42; public string Name = "marker"; }
            static async System.Threading.Tasks.Task Inner(System.Threading.SemaphoreSlim gate) => await gate.WaitAsync();
            static async System.Threading.Tasks.Task Outer(System.Threading.SemaphoreSlim gate) { await System.Threading.Tasks.Task.Yield(); await Inner(gate); }
            var pending = Outer(new System.Threading.SemaphoreSlim(0));
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

    // Outer awaits Inner, which waits for a semaphore nobody releases; Outer's second await is its state 1.
    [Test]
    public async Task AsyncChains_RunFromTheInnermostAwaiter_ToItsCallers()
    {
        await SleepingThreadsAsync();

        var chain = ProcessInspection.AsyncChains(target.Id, null, new("Inner"), top: 10, pdbs, CancellationToken.None).Should().ContainSingle().Subject;

        chain.Frames.Select(frame => frame.Method.Split('.')[^1]).Should().Equal("Inner", "Outer");
        chain.Frames.Select(frame => frame.State).Should().Equal(0, 1);
        chain.Awaiting.Should().Contain("SemaphoreSlim");
        chain.Count.Should().Be(1);
        ProcessInspection.Objects(target.Id, null, ProcessInspection.ParseAddress(chain.Task), null, top: 1).Single().Type.Should().Contain("AsyncStateMachineBox");
    }

    [Test]
    public async Task Dump_WrittenByTheServer_DescribesTheProcess()
    {
        await SleepingThreadsAsync();
        var dump = await ProcessInspection.WriteDumpAsync(target.Id, DumpType.WithHeap, null, CancellationToken.None);
        try
        {
            dump.Size.Should().BePositive();
            ProcessInspection.Details(null, dump.Path, new("ScriptHost")).Modules.Should().ContainSingle().Which.Name.Should().Be("Roslynaut.ScriptHost");
            ProcessInspection.AsyncChains(null, dump.Path, new("Inner"), top: 10, pdbs, CancellationToken.None).Should().ContainSingle();
        }
        finally
        {
            File.Delete(dump.Path);
        }
    }

    [Test]
    public async Task Details_HaveCommandLineEnvironmentAndAssemblies()
    {
        await SleepingThreadsAsync();

        var details = ProcessInspection.Details(target.Id, null, null);

        details.CommandLine.Should().Contain("Roslynaut.ScriptHost.dll");
        details.Runtime.Should().StartWith($"{Environment.Version.Major}.");
        details.Environment.Should().ContainKey("PATH");
        details.Modules.Should().Contain(module => module.Name == "System.Private.CoreLib" && module.Version != null);
    }
}

/// <summary>
/// process_cpu, process_allocations and process_counters, against a script host that spins in a known method on one thread, and allocates
/// byte arrays and finalizable objects on another, between sleeps.
/// </summary>
[NonParallelizable]
public class ProcessTracingTests
{
    private static readonly ProcessTracing.CpuFilters NoFilters = new(null, null, null, null);

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
            class Fin { ~Fin() { } }
            class Churn
            {
                public static object Kept;
                public static void Run()
                {
                    while (true)
                    {
                        for (var i = 0; i < 100; i++)
                            Kept = new byte[1024];
                        for (var i = 0; i < 1000; i++)
                            Kept = new Fin();
                        System.Threading.Thread.Sleep(1);
                    }
                }
            }
            new System.Threading.Thread(Churn.Run) { IsBackground = true }.Start();
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

    // The spinning thread starts in the script host's Main, the outermost method that is not the framework's.
    [Test]
    public async Task Cpu_FindsTheSpinningMethod_AndItsThread()
    {
        var samples = await ProcessTracing.SampleAsync(target.Id, TimeSpan.FromSeconds(2), CancellationToken.None);
        var profile = ProcessTracing.Analyze("1", samples, null, NoFilters, top: 10);

        profile.Samples.Should().BePositive();
        profile.Methods[0].Should().Match<ProcessTracing.MethodSamples>(method => method.Method.Contains("Spin.Work") && method.Self > 50);
        profile.Threads[0].Should().Match<ProcessTracing.ThreadSamples>(thread => thread.Entry.StartsWith("Roslynaut.ScriptHost.Program.") && thread.Busy > 20);
        ProcessTracing.Analyze("1", samples, null, NoFilters with { Threads = new(@"Spin\.Work") }, top: 10).Threads.Should().ContainSingle();
        ProcessTracing.Analyze("1", samples, null, NoFilters with { Exclude = new(@"Spin\.Work") }, top: 10).Methods.Should().NotContain(method => method.Method.Contains("Spin.Work"));
    }

    private static ProcessTracing.StackSamples Stack(int thread, int count, params string[] frames) => new(thread, frames, count);

    private static readonly ProcessTracing.CpuSamples Profile = new(TimeSpan.FromSeconds(1),
    [
        Stack(1, 60, "Leaf()", "Mid()", "Root()", "Main()"),
        Stack(1, 20, "Mid()", "Root()", "Main()"),
        Stack(1, 10, "System.Threading.Monitor.Wait()", "Root()", "Main()"),
        Stack(2, 10, "Leaf()", "Other()", "System.Threading.Thread.StartCallback()"),
    ]);

    [Test]
    public void Analyze_CountsThreadsAndMethods()
    {
        var profile = ProcessTracing.Analyze("1", Profile, null, NoFilters, top: 2);

        (profile.Profile, profile.Samples).Should().Be(("1", 100));
        profile.Threads.Should().Equal(new ProcessTracing.ThreadSamples(1, "Main()", 90, 9), new ProcessTracing.ThreadSamples(2, "Other()", 10, 1));
        profile.Methods.Should().Equal(new ProcessTracing.MethodSamples("Leaf()", 70, 70), new ProcessTracing.MethodSamples("Mid()", 20, 80));
        (profile.Callees, profile.Callers).Should().Be((null, null));
    }

    // Shares are of the samples that pass the filters; the tree under Root ends where samples ran a method itself.
    [Test]
    public void Analyze_FiltersAndBuildsCallTrees()
    {
        var profile = ProcessTracing.Analyze("1", Profile, null, new(new("^Root"), new(@"Monitor\.Wait"), new("^Root"), new("^Leaf")), top: 10);

        (profile.Samples, profile.Threads.Single().Id).Should().Be((80, 1));
        profile.Callees.Should().BeEquivalentTo([Tree("Root()", 100, 0, Tree("Mid()", 100, 25, Tree("Leaf()", 75, 75)))], options => options.WithStrictOrdering());
        profile.Callers.Should().BeEquivalentTo([Tree("Leaf()", 75, null, Tree("Mid()", 75, null, Tree("Root()", 75, null, Tree("Main()", 75, null))))], options => options.WithStrictOrdering());
    }

    private static ProcessTracing.CallTree Tree(string method, double total, double? self, params ProcessTracing.CallTree[] children) => new(method, total, self, children);

    // Ties on Self go by Total.
    [Test]
    public void Analyze_AgainstABaseline_PutsTheBiggestChangesFirst()
    {
        var baseline = new ProcessTracing.CpuSamples(TimeSpan.FromSeconds(1), [Stack(1, 50, "Leaf()", "Main()"), Stack(1, 50, "Gone()", "Main()")]);

        var profile = ProcessTracing.Analyze("2", Profile, baseline, NoFilters, top: 3);

        profile.Methods.Select(method => $"{method.Method} {method.SelfChange} {method.TotalChange}").Should().Equal("Gone() -50 -50", "Mid() 20 80", "Leaf() 20 20");
    }

    // The churning thread allocates the most; starting the session allocates a little on another. The byte arrays outweigh the objects with
    // a finalizer, which are listed apart.
    [Test]
    public async Task Allocations_ByTypeStackAndFinalizer()
    {
        var profile = await ProcessTracing.AllocationsAsync(target.Id, TimeSpan.FromSeconds(3), null, top: 10, maxFrames: 5, CancellationToken.None);

        profile.Bytes.Should().BePositive();
        profile.Types[0].Type.Should().Be("System.Byte[]");
        profile.Threads[0].Entry.Should().Be("Submission#0+Churn.Run()");
        profile.Threads[0].Bytes.Should().BeGreaterThan(profile.Bytes / 2);
        profile.Stacks.Should().Contain(stack => stack.Type == "System.Byte[]" && stack.Stack.Any(frame => frame.Contains("Churn.Run")) && stack.Stack.Count <= 5);
        // The allocation ticks name the nested type Fin, the runtime Submission#0+Fin.
        profile.Finalizable.Should().ContainSingle().Which.Type.Should().Be("Submission#0+Fin");
        (await ProcessTracing.AllocationsAsync(target.Id, TimeSpan.FromSeconds(1), new(@"Churn\.Run"), top: 10, maxFrames: 5, CancellationToken.None))
            .Threads.Should().ContainSingle().Which.Entry.Should().Be("Submission#0+Churn.Run()");
    }

    [Test]
    public async Task Counters_HaveTheRuntimes()
    {
        var counters = await ProcessTracing.CountersAsync(target.Id, TimeSpan.FromSeconds(3), CancellationToken.None);

        counters.Should().Contain(counter => counter.Name == "cpu-usage" && counter.Value > 0).And.Contain(counter => counter.Name == "working-set");
    }
}
