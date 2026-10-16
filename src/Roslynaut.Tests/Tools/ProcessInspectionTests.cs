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
        target.StandardInput.Write("""{"Code":"var kept = new string('x', 1000); System.Threading.Thread.Sleep(60000); kept","References":[]}""");
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

        var heap = ProcessInspection.Heap(target.Id, null, null, top: 1000);
        heap.Objects.Should().BePositive();
        heap.Types.Should().Contain(type => type.Type == "System.String" && type.Count > 0);
        ProcessInspection.Heap(target.Id, null, new("^System\\.String$"), top: 10).Types.Should().ContainSingle();
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
            ProcessInspection.Heap(null, dump, new("^System\\.String$"), top: 10).Types.Should().ContainSingle(type => type.Count > 0);
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
