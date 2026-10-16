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

    // Calc.Twice for a breakpoint at a method; two caught exceptions, of which only one should stop.
    private string CreateThrower()
    {
        var thrower = Path.Combine(directory.FullName, "Thrower", "Thrower.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(thrower)!);
        File.WriteAllText(thrower, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(thrower)!, "Program.cs"), """
            var value = Calc.Twice(21);
            System.Console.WriteLine($"value {value}");
            try { throw new System.InvalidOperationException("first"); } catch (System.InvalidOperationException) { }
            try { throw new System.ArgumentException("second"); } catch (System.ArgumentException) { }

            static class Calc
            {
                public static int Twice(int x) => x * 2;
            }
            """);
        return thrower;
    }

    // global.json in its folder puts it on Microsoft.Testing.Platform.
    [Test]
    public async Task Test_OnTestingPlatform_BreaksInTheTest()
    {
        var folder = Path.Combine(directory.FullName, "Mtp");
        Directory.CreateDirectory(Path.Combine(folder, "Tests"));
        File.WriteAllText(Path.Combine(folder, "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        var project = Path.Combine(folder, "Tests", "MtpTests.csproj");
        var file = Path.Combine(folder, "Tests", "Tests.cs");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
              <ItemGroup><PackageReference Include="xunit.v3" Version="3.1.0" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(file, """
            namespace MtpTests;

            public class Tests
            {
                [Xunit.Fact]
                public void Adds()
                {
                    var sum = 2 + 3;
                    Xunit.Assert.Equal(5, sum);
                }
            }
            """);

        await using var debugger = new DebugService(new WorkspaceService(), new PdbService());
        var wait = TimeSpan.FromMinutes(3);

        var state = await debugger.TestAsync(project, "FullyQualifiedName~MtpTests.Tests.Adds", [new(file, 9)], stopOnExceptions: null, wait, CancellationToken.None);
        state.Should().Match<DebugState>(stopped => stopped.Status == "Stopped" && stopped.Reason == "breakpoint");
        state.Locals.Should().Contain(local => local.Name == "sum" && local.Value == "5");

        state = await debugger.ResumeAsync(ResumeAction.Continue, wait, CancellationToken.None);
        state.Should().Match<DebugState>(exited => exited.Status == "Exited" && exited.Output.Contains("total: 1"));
    }

    [Test]
    public async Task FunctionBreakpoint_SetValue_AndStopAtOneExceptionType()
    {
        await using var debugger = new DebugService(new WorkspaceService(), new PdbService());
        var wait = TimeSpan.FromSeconds(60);

        var state = await debugger.StartAsync(CreateThrower(), [], [new(Function: "Calc.Twice")], stopAtEntry: false, ["System.ArgumentException"], wait, CancellationToken.None);
        state.Should().Match<DebugState>(stopped => stopped.Status == "Stopped" && stopped.Stack[0].Name.Contains("Twice"));

        (await debugger.SetAsync("x", "50", 0, CancellationToken.None)).Value.Should().Be("50");

        state = await debugger.ResumeAsync(ResumeAction.Continue, wait, CancellationToken.None);
        state.Should().Match<DebugState>(stopped => stopped.Reason == "exception" && stopped.Exception!.Contains("System.ArgumentException"));
        var output = state.Output;

        state = await debugger.ResumeAsync(ResumeAction.Continue, wait, CancellationToken.None);
        state.Should().Match<DebugState>(exited => exited.Status == "Exited");
        (output + state.Output).Should().Contain("value 100");
    }

    [OneTimeTearDown]
    public void DeleteProgram() => directory?.Delete(recursive: true);

    [Test]
    public async Task Session_BreaksInspectsStepsAndExits()
    {
        await using var debugger = new DebugService(new WorkspaceService(), new PdbService());
        var wait = TimeSpan.FromSeconds(60);

        var state = await debugger.StartAsync(project, [], [new(program, 4)], stopAtEntry: false, stopOnExceptions: null, wait, CancellationToken.None);
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

            var state = await debugger.AttachAsync(process.Id, [new(Path.Combine(Path.GetDirectoryName(looper)!, "Program.cs"), 4)], stopOnExceptions: null, TimeSpan.FromSeconds(60), CancellationToken.None);

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

        var state = await debugger.TestAsync(tests, "FullyQualifiedName~SampleTests.Adds", [new(testFile, 9)], stopOnExceptions: null, wait, CancellationToken.None);
        state.Should().Match<DebugState>(stopped => stopped.Status == "Stopped" && stopped.Reason == "breakpoint");
        state.Locals.Should().Contain(local => local.Name == "sum" && local.Value == "5");

        state = await debugger.ResumeAsync(ResumeAction.Continue, wait, CancellationToken.None);
        state.Should().Match<DebugState>(exited => exited.Status == "Exited" && exited.Output.Contains("Passed!"));
    }
}
