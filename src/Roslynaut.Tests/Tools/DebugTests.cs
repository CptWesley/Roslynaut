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
        Directory.CreateDirectory(Path.Combine(directory.FullName, "Counter"));
        project = Path.Combine(directory.FullName, "Counter", "Counter.csproj");
        program = Path.Combine(directory.FullName, "Counter", "Program.cs");
        File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(program, """
            var numbers = new System.Collections.Generic.List<int> { 1, 2, 3 };
            var total = 0;
            foreach (var n in numbers)
                total += n;
            System.Console.WriteLine($"total {total}");
            """);

        Directory.CreateDirectory(Path.Combine(directory.FullName, "Looper"));
        looper = Path.Combine(directory.FullName, "Looper", "Looper.csproj");
        File.WriteAllText(looper, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(directory.FullName, "Looper", "Program.cs"), """
            var i = 0;
            while (true)
            {
                i++;
                System.Threading.Thread.Sleep(50);
            }
            """);

        Directory.CreateDirectory(Path.Combine(directory.FullName, "Tests"));
        tests = Path.Combine(directory.FullName, "Tests", "Sample.Tests.csproj");
        testFile = Path.Combine(directory.FullName, "Tests", "SampleTests.cs");
        File.WriteAllText(tests, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.11.0" />
                <PackageReference Include="NUnit" Version="5.0.0" />
                <PackageReference Include="NUnit3TestAdapter" Version="6.3.1" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(testFile, """
            using NUnit.Framework;

            public class SampleTests
            {
                [Test]
                public void Adds()
                {
                    var sum = 2 + 3;
                    Assert.That(sum, Is.EqualTo(5));
                }
            }
            """);
    }

    private string looper = null!;
    private string tests = null!;
    private string testFile = null!;

    [OneTimeTearDown]
    public void DeleteProgram() => directory?.Delete(recursive: true);

    [Test]
    public async Task Session_BreaksInspectsStepsAndExits()
    {
        await using var debugger = new DebugService(new WorkspaceService(), new PdbService());
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

    [Test]
    public async Task Attach_Breaks_AndDetachLeavesItRunning()
    {
        (await Roslynaut.Tools.BuildTool.BuildAsync(looper)).Succeeded.Should().BeTrue();
        var start = new System.Diagnostics.ProcessStartInfo(DotnetCli.Executable) { ArgumentList = { "exec", Path.Combine(Path.GetDirectoryName(looper)!, "bin", "Debug", "net10.0", "Looper.dll") } };
        using var process = System.Diagnostics.Process.Start(start)!;
        try
        {
            await using var debugger = new DebugService(new WorkspaceService(), new PdbService());

            var state = await debugger.AttachAsync(process.Id, [new(Path.Combine(Path.GetDirectoryName(looper)!, "Program.cs"), 4)], stopOnAllExceptions: false, TimeSpan.FromSeconds(60), CancellationToken.None);

            state.Should().Match<DebugState>(stopped => stopped.Status == "Stopped" && stopped.Reason == "breakpoint");
            state.Locals.Should().Contain(local => local.Name == "i");
            await debugger.StopAsync();
            await Task.Delay(500);
            process.HasExited.Should().BeFalse();
        }
        finally
        {
            process.Kill();
        }
    }

    [Test]
    public async Task Test_BreaksInTheTest_AndReportsTheResults()
    {
        await using var debugger = new DebugService(new WorkspaceService(), new PdbService());
        var wait = TimeSpan.FromMinutes(3);

        var state = await debugger.TestAsync(tests, "FullyQualifiedName~SampleTests.Adds", [new(testFile, 9)], stopOnAllExceptions: false, wait, CancellationToken.None);
        state.Should().Match<DebugState>(stopped => stopped.Status == "Stopped" && stopped.Reason == "breakpoint");
        state.Locals.Should().Contain(local => local.Name == "sum" && local.Value == "5");

        state = await debugger.ResumeAsync(ResumeAction.Continue, wait, CancellationToken.None);
        state.Should().Match<DebugState>(exited => exited.Status == "Exited" && exited.Output.Contains("Passed!"));
    }
}
