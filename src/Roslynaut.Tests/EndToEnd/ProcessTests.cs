using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>
/// The process tools on the running Shapes.App: its main thread waits in Console.ReadLine, holding a Circle, a Rectangle and a Square, while
/// another thread adds up their areas in a loop, without allocating.
/// </summary>
public class ProcessTests : EndToEndTest
{
    private Process app = null!;

    [OneTimeSetUp]
    public void StartApp() => app = Sample.StartWaitingApp();

    [OneTimeTearDown]
    public void StopApp()
    {
        app.Kill(entireProcessTree: true);
        app.Dispose();
    }

    private Dictionary<string, object?> Process(params (string Name, object? Value)[] arguments)
    {
        var all = arguments.ToDictionary(argument => argument.Name, argument => argument.Value);
        all["processId"] = app.Id;
        return all;
    }

    [Test]
    public async Task DotnetProcesses_ListTheApp()
    {
        var processes = await Server.CallAsync<List<ProcessInspection.ProcessEntry>>("dotnet_processes");

        processes.Should().ContainSingle(process => process.Id == app.Id).Which.Should().Match<ProcessInspection.ProcessEntry>(process =>
            process.Name == "dotnet" && process.Path == DotnetCli.Executable && process.StartTime != null);
    }

    // Each managed frame of the app's code has its file and line, from the PDB next to the build output.
    [Test]
    public async Task ProcessThreads_ShowWhereEachThreadIs()
    {
        var threads = await Server.CallAsync<List<ProcessInspection.ThreadEntry>>("process_threads", Process());

        var main = threads.Should().ContainSingle(thread => thread.Stack.Any(frame => frame.StartsWith("Program.<Main>$"))).Subject;
        main.Stack.Should().Contain($"Program.<Main>$(System.String[]) in {Sample.ProgramFile}:line {Sample.LineOf(Sample.ProgramFile, "Console.ReadLine();")}")
            .And.Contain(frame => frame.StartsWith("System.Console.ReadLine()"));
        (main.Exception, main.HeldLocks).Should().BeEquivalentTo(((string?)null, Array.Empty<string>()));

        // A snapshot can catch the spinning thread in a stub of an interface call, which the runtime cannot walk the stack from; so take a few.
        var loop = Enumerable.Range(Sample.LineOf(Sample.SpinnerFile, "while (true)"), 4).Select(line => $"Shapes.App.Spinner.Spin(Shapes.IShape[]) in {Sample.SpinnerFile}:line {line}").ToList();
        for (var attempt = 1; !threads.Any(thread => thread.Stack.Any(loop.Contains)); attempt++)
        {
            attempt.Should().BeLessThan(20, string.Join("\n\n", threads.Select(thread => string.Join("\n", thread.Stack))));
            threads = await Server.CallAsync<List<ProcessInspection.ThreadEntry>>("process_threads", Process());
        }

        threads.Should().ContainSingle(thread => thread.Stack.Any(loop.Contains));
    }

    // Each shape once, and the array that holds them. Sizes are those of a 64-bit process: a header and a method table pointer, then the
    // doubles; for the array, its length and three references.
    [Test]
    public async Task ProcessHeap_CountsTheShapes_AndComparesWithAnEarlierSnapshot()
    {
        var first = await Server.CallAsync<ProcessInspection.HeapSummary>("process_heap", Process(("type", @"^Shapes\.")));
        var second = await Server.CallAsync<ProcessInspection.HeapSummary>("process_heap", Process(("type", @"^Shapes\."), ("compareTo", first.Snapshot)));

        first.Types.Should().BeEquivalentTo(
        [
            new ProcessInspection.TypeStats("Shapes.IShape[]", 1, 48),
            new ProcessInspection.TypeStats("Shapes.Circle", 1, 24),
            new ProcessInspection.TypeStats("Shapes.Rectangle", 1, 32),
            new ProcessInspection.TypeStats("Shapes.Square", 1, 32),
        ]);
        first.Types.Select(type => type.Size).Should().BeInDescendingOrder();
        (first.Objects > 0, first.Size > 0, first.ObjectsChange, first.SizeChange).Should().Be((true, true, null, null));
        second.Snapshot.Should().NotBe(first.Snapshot);
        second.Types.Should().BeEquivalentTo(
        [
            new ProcessInspection.TypeStats("Shapes.IShape[]", 1, 48, 0, 0),
            new ProcessInspection.TypeStats("Shapes.Circle", 1, 24, 0, 0),
            new ProcessInspection.TypeStats("Shapes.Rectangle", 1, 32, 0, 0),
            new ProcessInspection.TypeStats("Shapes.Square", 1, 32, 0, 0),
        ]);
    }

    [Test]
    public async Task ProcessObjects_ByType_AndByAddress()
    {
        var squares = await Server.CallAsync<List<ProcessInspection.ObjectEntry>>("process_objects", Process(("type", @"^Shapes\.Square$")));

        var square = squares.Should().ContainSingle().Subject;
        square.Should().BeEquivalentTo(new ProcessInspection.ObjectEntry(square.Address, "Shapes.Square", 32, null, null,
        [
            new ProcessInspection.FieldValue("<Width>k__BackingField", "System.Double", "4"),
            new ProcessInspection.FieldValue("<Height>k__BackingField", "System.Double", "4"),
        ]));
        square.Address.Should().MatchRegex("^0x[0-9a-f]+$");
        (await Server.CallAsync<List<ProcessInspection.ObjectEntry>>("process_objects", Process(("address", square.Address)))).Should().BeEquivalentTo([square]);
    }

    // In a debug build, Main's frame still holds the Circle it created for the array.
    [Test]
    public async Task ProcessRoots_LeadFromARootToTheObject()
    {
        var circle = (await Server.CallAsync<List<ProcessInspection.ObjectEntry>>("process_objects", Process(("type", @"^Shapes\.Circle$")))).Single();

        var roots = await Server.CallAsync<List<ProcessInspection.RootPath>>("process_roots", new() { ["processId"] = app.Id, ["address"] = circle.Address });

        roots.Should().ContainEquivalentOf(new ProcessInspection.RootPath("Stack", "Program.<Main>$(System.String[])", [$"Shapes.Circle @{circle.Address}"]))
            .And.OnlyContain(root => root.Chain.Last() == $"Shapes.Circle @{circle.Address}");
    }

    // A dump with the heap reads as the process does. It leaves out the code the JIT compiled, which maps a frame to its IL offset, so frames
    // whose start is not near the stack have no source line; a full dump has them. That code also says where a frame keeps references, so
    // the stack roots of the shapes, the only roots they have, are there only in some dumps.
    [Test]
    public async Task Dump_WithTheHeap_HasTheThreadsObjectsAndRoots()
    {
        var dump = Sample.In("app-heap.dmp");
        new DiagnosticsClient(app.Id).WriteDump(DumpType.WithHeap, dump);

        var threads = await Server.CallAsync<List<ProcessInspection.ThreadEntry>>("process_threads", new() { ["dumpPath"] = dump });
        var heap = await Server.CallAsync<ProcessInspection.HeapSummary>("process_heap", new() { ["dumpPath"] = dump, ["type"] = @"^Shapes\." });
        var square = (await Server.CallAsync<List<ProcessInspection.ObjectEntry>>("process_objects", new() { ["dumpPath"] = dump, ["type"] = @"^Shapes\.Square$" })).Single();
        var roots = await Server.CallAsync<List<ProcessInspection.RootPath>>("process_roots", new() { ["dumpPath"] = dump, ["address"] = square.Address });

        threads.Should().ContainSingle(thread => thread.Stack.Any(frame => frame.StartsWith("Program.<Main>$"))).Which.Stack.Should().Contain("System.Console.ReadLine()");
        heap.Types.Should().BeEquivalentTo(
        [
            new ProcessInspection.TypeStats("Shapes.IShape[]", 1, 48),
            new ProcessInspection.TypeStats("Shapes.Circle", 1, 24),
            new ProcessInspection.TypeStats("Shapes.Rectangle", 1, 32),
            new ProcessInspection.TypeStats("Shapes.Square", 1, 32),
        ]);
        square.Fields.Select(field => field.Value).Should().Equal("4", "4");
        roots.Should().OnlyContain(root => root.Kind == "Stack" && root.Chain.Last() == $"Shapes.Square @{square.Address}");
    }

    // A minidump has the threads' stacks but not the heap.
    [Test]
    public async Task Dump_WithoutTheHeap_HasTheThreadsOnly()
    {
        var dump = Sample.In("app-mini.dmp");
        new DiagnosticsClient(app.Id).WriteDump(DumpType.Normal, dump);

        var threads = await Server.CallAsync<List<ProcessInspection.ThreadEntry>>("process_threads", new() { ["dumpPath"] = dump });

        threads.Should().ContainSingle(thread => thread.Stack.Any(frame => frame.StartsWith("Program.<Main>$"))).Which.Stack.Should().Contain("System.Console.ReadLine()");
        (await Server.CallFailingAsync("process_heap", new() { ["dumpPath"] = dump }))
            .Should().Be("An error occurred invoking 'process_heap': The heap cannot be walked in this dump; take one with the heap, such as with dotnet-dump collect --type Heap.");
        (await Server.CallFailingAsync("process_objects", new() { ["dumpPath"] = dump, ["type"] = "Square" }))
            .Should().StartWith("An error occurred invoking 'process_objects': The heap cannot be walked");
    }

    [Test]
    public async Task ProcessTools_SayWhatTheyNeed()
    {
        (await Server.CallFailingAsync("process_threads")).Should().Be("An error occurred invoking 'process_threads': Pass either a processId or a dumpPath.");
        (await Server.CallFailingAsync("process_heap", Process(("dumpPath", Sample.In("app.dmp")))))
            .Should().Be("An error occurred invoking 'process_heap': Pass either a processId or a dumpPath.");
        (await Server.CallFailingAsync("process_objects", Process())).Should().Be("An error occurred invoking 'process_objects': Pass an address, or a type to find objects of.");
        (await Server.CallFailingAsync("process_objects", Process(("address", "0xzz"))))
            .Should().Be("An error occurred invoking 'process_objects': '0xzz' is not a hexadecimal address such as 0x1a2b3c.");
        (await Server.CallFailingAsync("process_heap", Process(("compareTo", "none"))))
            .Should().Be("An error occurred invoking 'process_heap': There is no snapshot none; only the last 20 are kept, until the server restarts.");
        (await Server.CallFailingAsync("process_threads", new() { ["dumpPath"] = Sample.In("missing.dmp") }))
            .Should().StartWith($"An error occurred invoking 'process_threads': Cannot inspect dump {Sample.In("missing.dmp")}: ");
    }

    [Test]
    public async Task ProcessTools_TakeTheirLimits()
    {
        var threads = await Server.CallAsync<List<ProcessInspection.ThreadEntry>>("process_threads", Process(("maxFrames", 1)));
        var heap = await Server.CallAsync<ProcessInspection.HeapSummary>("process_heap", Process(("type", @"^Shapes\."), ("top", 1)));

        threads.Should().NotBeEmpty().And.OnlyContain(thread => thread.Stack.Count <= 1).And.Contain(thread => thread.Stack.Count == 1);
        heap.Types.Should().Equal(new ProcessInspection.TypeStats("Shapes.IShape[]", 1, 48));
    }

    [Test]
    public async Task ProcessCounters_IncludeCpuAndMemory()
    {
        var counters = await Server.CallAsync<List<ProcessTracing.CounterValue>>("process_counters", new() { ["processId"] = app.Id, ["seconds"] = 3 });

        counters.Select(counter => counter.Name).Should().Contain(["cpu-usage", "working-set", "gc-heap-size", "threadpool-thread-count"]).And.BeInAscendingOrder(StringComparer.Ordinal);
        counters.Single(counter => counter.Name == "cpu-usage").Value.Should().BePositive();
        counters.Single(counter => counter.Name == "working-set").Value.Should().BePositive();
    }

    // Only the spinning thread runs managed code, so nearly every sample is in its loop.
    [Test]
    public async Task ProcessCpu_FindsTheHotLoop()
    {
        var profile = await Server.CallAsync<ProcessTracing.CpuProfile>("process_cpu", new() { ["processId"] = app.Id, ["seconds"] = 3 });

        profile.Samples.Should().BePositive();
        profile.Methods.Should().Contain(method => method.Method.Contains("Shapes.App.Spinner.Spin") && method.Total >= 90);
        // The spinning thread starts in a lambda of the top-level statements.
        profile.Threads.Should().Contain(thread => thread.Entry.StartsWith("Program"));
    }

    // A profile's samples stay, to analyze again with filters, or to compare a later profile with.
    [Test]
    public async Task ProcessCpu_AnalyzesAProfileAgain_AndComparesProfiles()
    {
        var profile = await Server.CallAsync<ProcessTracing.CpuProfile>("process_cpu", new() { ["processId"] = app.Id, ["seconds"] = 2 });
        var tree = await Server.CallAsync<ProcessTracing.CpuProfile>("process_cpu", new() { ["profile"] = profile.Profile, ["root"] = @"Spinner\.Spin", ["leaf"] = @"\.Area\(" });
        var compared = await Server.CallAsync<ProcessTracing.CpuProfile>("process_cpu", new() { ["processId"] = app.Id, ["seconds"] = 2, ["compareTo"] = profile.Profile });

        (tree.Profile, tree.Samples).Should().Be((profile.Profile, profile.Samples));
        tree.Callees.Should().ContainSingle().Which.Method.Should().StartWith("Shapes.App.Spinner.Spin");
        tree.Callers.Should().NotBeEmpty().And.OnlyContain(caller => caller.Method.Contains(".Area("));
        compared.Profile.Should().NotBe(profile.Profile);
        compared.Methods.Should().NotBeEmpty().And.OnlyContain(method => method.SelfChange != null && method.TotalChange != null);
        (await Server.CallFailingAsync("process_cpu", new() { ["profile"] = "none" }))
            .Should().Be("An error occurred invoking 'process_cpu': There is no profile none; only the last 10 are kept, until the server restarts.");
        (await Server.CallFailingAsync("process_cpu", new() { ["processId"] = app.Id, ["profile"] = profile.Profile }))
            .Should().Be("An error occurred invoking 'process_cpu': Pass either a processId or a profile.");
    }

    // The app allocates nothing once it waits, so what shows is little; the call still sorts it.
    [Test]
    public async Task ProcessAllocations_SortsWhatWasAllocated()
    {
        var profile = await Server.CallAsync<ProcessTracing.AllocationProfile>("process_allocations", new() { ["processId"] = app.Id, ["seconds"] = 2, ["top"] = 5 });

        profile.Bytes.Should().Be(profile.Threads.Sum(thread => thread.Bytes));
        profile.Types.Select(type => type.Bytes).Should().BeInDescendingOrder();
        profile.Types.Count.Should().BeLessThanOrEqualTo(5);
        (await Server.CallFailingAsync("process_allocations", new() { ["processId"] = app.Id, ["threads"] = "(" }))
            .Should().StartWith("An error occurred invoking 'process_allocations': Invalid threads: ");
    }

    // Without arguments the app adds up the areas once and exits; Square has Rectangle's Area.
    [Test]
    public async Task ProcessCalls_CountCallsPerMarker()
    {
        var counts = await Server.CallAsync<CallCounts>("process_calls", new()
        {
            ["projectPath"] = Sample.Solution, ["program"] = Sample.AppAssembly, ["methods"] = new[] { "Shapes.Circle.Area", "Rectangle.Area" },
            ["per"] = "ShapeMath.TotalArea", ["seconds"] = 60,
        });

        (counts.ExitCode, counts.Seconds > 0).Should().Be((0, true));
        counts.Output.Should().Contain("Total area: 25");
        counts.Methods.Select(method => (method.Method, method.Calls, method.PerMarker)).Should().BeEquivalentTo(
        [
            ("System.Double Shapes.Rectangle::Area()", 2L, (double?)2),
            ("System.Double Shapes.Circle::Area()", 1L, (double?)1),
            ("System.Double Shapes.ShapeMath::TotalArea(System.Collections.Generic.IEnumerable`1<Shapes.IShape>)", 1L, (double?)1),
        ]);
        counts.Methods[0].Threads.Should().ContainSingle().Which.Calls.Should().Be(2);
        Directory.GetFiles(Path.GetDirectoryName(Sample.AppAssembly)!, "*.roslynaut-*").Should().BeEmpty("the deps.json for counting goes when done");
    }

    // Waiting, the app spins on a thread of its own until it is stopped.
    [Test]
    public async Task ProcessCalls_StopTheProgramInTime()
    {
        var counts = await Server.CallAsync<CallCounts>("process_calls", new()
        {
            ["projectPath"] = Sample.Solution, ["program"] = Sample.AppAssembly, ["methods"] = new[] { "Circle" }, ["arguments"] = new[] { "wait" }, ["seconds"] = 3,
        });

        (counts.ExitCode, counts.Output).Should().Be((null, null));
        var area = counts.Methods.Single(method => method.Method == "System.Double Shapes.Circle::Area()");
        area.Calls.Should().BeGreaterThan(1000);
        area.PerSecond.Should().BePositive();
        area.Threads.Should().HaveCount(2).And.Contain(thread => thread.Calls == 1);
        counts.Methods.Should().Contain(method => method.Method == "System.Void Shapes.Circle::.ctor(System.Double)" && method.Calls == 1);
        (await Server.CallFailingAsync("process_calls", new() { ["projectPath"] = Sample.Solution, ["program"] = Sample.AppAssembly, ["methods"] = new[] { "Spinner.Spin" } }))
            .Should().Be("An error occurred invoking 'process_calls': Shapes.App is the program itself; only methods of the libraries it uses can be counted.");
    }
}
