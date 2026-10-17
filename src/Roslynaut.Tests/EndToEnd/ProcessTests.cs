using System.Diagnostics;
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

        var loop = Enumerable.Range(Sample.LineOf(Sample.SpinnerFile, "while (true)"), 4).Select(line => $"Shapes.App.Spinner.Spin(Shapes.IShape[]) in {Sample.SpinnerFile}:line {line}");
        threads.Should().ContainSingle(thread => thread.Stack.Any(frame => loop.Contains(frame)));
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
    }
}
