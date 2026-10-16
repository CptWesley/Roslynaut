using Roslynaut.Debugging;

namespace Roslynaut.Tests.Tools;

/// <summary>A debugging session through netcoredbg, which has to be on the PATH or in NETCOREDBG; skipped otherwise.</summary>
[NonParallelizable]
public class DebugTests
{
    private DirectoryInfo directory = null!;
    private string project = null!;
    private string program = null!;

    [OneTimeSetUp]
    public void CreateProgram()
    {
        var adapter = Environment.GetEnvironmentVariable(DebugService.AdapterVariable);
        Assume.That(adapter is not null && File.Exists(adapter), $"Set {DebugService.AdapterVariable} to netcoredbg to run the debugger tests.");

        directory = Directory.CreateTempSubdirectory("roslynaut-debug-");
        project = Path.Combine(directory.FullName, "Counter.csproj");
        program = Path.Combine(directory.FullName, "Program.cs");
        File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(program, """
            var numbers = new System.Collections.Generic.List<int> { 1, 2, 3 };
            var total = 0;
            foreach (var n in numbers)
                total += n;
            System.Console.WriteLine($"total {total}");
            """);
    }

    [OneTimeTearDown]
    public void DeleteProgram() => directory?.Delete(recursive: true);

    [Test]
    public async Task Session_BreaksInspectsStepsAndExits()
    {
        await using var debugger = new DebugService(new WorkspaceService());
        var wait = TimeSpan.FromSeconds(60);

        var state = await debugger.StartAsync(project, [], [new(program, 4)], stopAtEntry: false, stopOnAllExceptions: false, wait, CancellationToken.None);
        state.Should().Match<DebugState>(stopped => stopped.Status == "Stopped" && stopped.Reason == "breakpoint");
        state.Stack[0].Should().Match<Frame>(frame => frame.Line == 4 && frame.FilePath!.EndsWith("Program.cs"));
        state.Locals.Should().Contain(local => local.Name == "total" && local.Value == "0").And.Contain(local => local.Name == "n" && local.Value == "1");

        (await debugger.InspectAsync("total + 100", 0, null, CancellationToken.None)).Should().ContainSingle().Which.Value.Should().Be("100");
        var numbers = state.Locals.Single(local => local.Name == "numbers");
        (await debugger.InspectAsync(null, 0, numbers.Reference, CancellationToken.None)).Should().Contain(item => item.Name == "Count" && item.Value == "3");

        state = await debugger.ResumeAsync(ResumeAction.Continue, wait, CancellationToken.None);
        state.Locals.Should().Contain(local => local.Name == "total" && local.Value == "1");

        state = await debugger.ResumeAsync(ResumeAction.StepOver, wait, CancellationToken.None);
        state.Should().Match<DebugState>(stopped => stopped.Status == "Stopped" && stopped.Reason == "step");

        (await debugger.SetBreakpointsAsync(program, [], CancellationToken.None)).Should().BeEmpty();
        state = await debugger.ResumeAsync(ResumeAction.Continue, wait, CancellationToken.None);
        state.Should().Match<DebugState>(exited => exited.Status == "Exited" && exited.ExitCode == 0 && exited.Output.Contains("total 6"));
    }
}
