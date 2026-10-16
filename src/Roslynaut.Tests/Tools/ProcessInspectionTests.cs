using System.Diagnostics;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>dotnet_processes, process_threads and process_heap, against a script host that sleeps in a known method.</summary>
[NonParallelizable]
public class ProcessInspectionTests
{
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
            var threads = attempt == 40 ? ProcessInspection.Threads(target.Id, maxFrames: 50) : TryThreads();
            if (threads?.Any(thread => thread.Stack.Any(frame => frame.Contains("Thread.Sleep"))) == true || attempt == 40)
                return threads!;
            await Task.Delay(250);
        }
    }

    private IReadOnlyList<ProcessInspection.ThreadEntry>? TryThreads()
    {
        try
        {
            return ProcessInspection.Threads(target.Id, maxFrames: 50);
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

    [Test]
    public async Task Heap_CountsTypes()
    {
        await SleepingThreadsAsync();

        var heap = ProcessInspection.Heap(target.Id, null, top: 1000);
        heap.Objects.Should().BePositive();
        heap.Types.Should().Contain(type => type.Type == "System.String" && type.Count > 0);
        ProcessInspection.Heap(target.Id, new("^System\\.String$"), top: 10).Types.Should().ContainSingle();
    }

    [Test]
    public void Processes_IncludeTarget()
    {
        ProcessInspection.List().Should().Contain(process => process.Id == target.Id && process.Name == "dotnet");
    }
}
