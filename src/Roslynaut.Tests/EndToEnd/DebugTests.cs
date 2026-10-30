using Roslynaut.Debugging;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>
/// Debugging sessions through the server, with netcoredbg, which has to be on the PATH or in NETCOREDBG when the tests start; skipped otherwise.
/// The server allows one session at a time.
/// </summary>
public class DebugTests : EndToEndTest
{
    [OneTimeSetUp]
    public void RequireTheDebugger()
    {
        var adapter = Environment.GetEnvironmentVariable(DebugService.AdapterVariable);
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(directory => File.Exists(Path.Combine(directory, OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg")));
        Assume.That((adapter is not null && File.Exists(adapter)) || onPath, $"Put netcoredbg on the PATH or in {DebugService.AdapterVariable} to run the debugger tests.");
    }

    [TearDown]
    public async Task StopDebugging() => await Server.CallTextAsync("debug_stop");

    private static Task<DebugState> ResumeAsync(string action = "Continue") =>
        Server.CallAsync<DebugState>("debug_resume", new() { ["action"] = action, ["waitSeconds"] = 60 });

    [Test]
    public async Task DebugStart_BreaksInspectsSetsAndRunsToTheEnd()
    {
        var line = Sample.LineOf(Sample.ProgramFile, "Total area");

        var state = await Server.CallAsync<DebugState>("debug_start", new()
        {
            ["program"] = Sample.AppProject, ["breakpoints"] = new[] { new { filePath = Sample.ProgramFile, line } }, ["waitSeconds"] = 120,
        });

        (state.Status, state.Reason).Should().Be(("Stopped", "breakpoint"));
        state.Stack[0].Should().Match<Frame>(frame => frame.FilePath == Sample.ProgramFile && frame.Line == line && frame.Name.Contains("<Main>$"));
        // netcoredbg shows doubles with 15 significant digits.
        state.Locals.Should().Contain(local => local.Name == "total" && local.Value == "25.14159265358979" && local.Type == "double");
        state.Output.Should().Contain("Hello, shapes!").And.Contain("Bye, shapes!");

        var shapes = state.Locals.Single(local => local.Name == "shapes");
        (await Server.CallAsync<List<Variable>>("debug_inspect", new() { ["reference"] = shapes.Reference }))
            .Select(item => (item.Name, item.Value)).Should().Equal(("[0]", "{Shapes.Circle}"), ("[1]", "{Shapes.Rectangle}"), ("[2]", "{Shapes.Square}"));
        (await Server.CallAsync<List<Variable>>("debug_inspect", new() { ["expression"] = "shapes[2].Area()" })).Should().ContainSingle().Which.Value.Should().Be("16");

        (await Server.CallAsync<Variable>("debug_set", new() { ["expression"] = "total", ["value"] = "7" })).Value.Should().Be("7");
        state = await ResumeAsync();
        state.Should().Match<DebugState>(exited => exited.Status == "Exited" && exited.ExitCode == 0 && exited.Output.Contains("Total area: 7"));
    }

    [Test]
    public async Task DebugBreakpoints_AtAMethod_ThenStepping()
    {
        var state = await Server.CallAsync<DebugState>("debug_start", new() { ["program"] = Sample.AppProject, ["stopAtEntry"] = true, ["waitSeconds"] = 120 });
        (state.Status, state.Reason).Should().Be(("Stopped", "entry"));

        var breakpoints = await Server.CallAsync<List<BreakpointState>>("debug_breakpoints", new() { ["functions"] = new[] { "Shapes.ShapeMath.TotalArea" } });
        breakpoints.Should().ContainSingle().Which.Should().Match<BreakpointState>(breakpoint => breakpoint.Verified && breakpoint.Function == "Shapes.ShapeMath.TotalArea");

        state = await ResumeAsync();
        (state.Reason, state.Stack[0].FilePath, state.Stack[0].Line).Should().Be(("breakpoint", Sample.ShapeMathFile, Sample.LineOf(Sample.ShapeMathFile, "TotalArea(") + 1));
        state.Stack[1].Name.Should().Contain("<Main>$");

        state = await ResumeAsync("StepOver");
        (state.Reason, state.Stack[0].Line).Should().Be(("step", Sample.LineOf(Sample.ShapeMathFile, "var total = 0.0;")));
        state = await ResumeAsync("StepOut");
        (state.Reason, state.Stack[0].FilePath).Should().Be(("step", Sample.ProgramFile));

        (await Server.CallAsync<List<BreakpointState>>("debug_breakpoints", new() { ["functions"] = Array.Empty<string>() })).Should().BeEmpty();
        (await ResumeAsync()).Status.Should().Be("Exited");
    }

    // Detaching leaves the process running.
    [Test]
    public async Task DebugAttach_ToTheRunningApp()
    {
        using var app = Sample.StartWaitingApp();
        try
        {
            var line = Sample.LineOf(Sample.SpinnerFile, "sum += shape.Area();");
            var state = await Server.CallAsync<DebugState>("debug_attach", new()
            {
                ["processId"] = app.Id, ["breakpoints"] = new[] { new { filePath = Sample.SpinnerFile, line } }, ["waitSeconds"] = 60,
            });

            (state.Status, state.Reason, state.Stack[0].FilePath, state.Stack[0].Line).Should().Be(("Stopped", "breakpoint", Sample.SpinnerFile, line));
            state.Locals.Select(local => local.Name).Should().Contain(["shapes", "sum", "shape"]);

            (await Server.CallTextAsync("debug_stop")).Should().Be("Stopped.");
            await Task.Delay(500);
            app.HasExited.Should().BeFalse();
        }
        finally
        {
            app.Kill(entireProcessTree: true);
        }
    }

    [Test]
    public async Task DebugTest_BreaksInATest_AndReportsItsResult()
    {
        var line = Sample.LineOf(Sample.TestsFile, "Assert.That(total, Is.EqualTo(7));");

        var state = await Server.CallAsync<DebugState>("debug_test", new()
        {
            ["projectPath"] = Sample.TestsProject,
            ["filter"] = "FullyQualifiedName~Shapes.Tests.ShapeTests.TotalArea_AddsTheAreas",
            ["breakpoints"] = new[] { new { filePath = Sample.TestsFile, line } },
            ["waitSeconds"] = 180,
        });

        (state.Status, state.Reason, state.Stack[0].FilePath, state.Stack[0].Line).Should().Be(("Stopped", "breakpoint", Sample.TestsFile, line));
        state.Locals.Should().Contain(local => local.Name == "total" && local.Value == "7");
        state = await Server.CallAsync<DebugState>("debug_resume", new() { ["waitSeconds"] = 180 });
        state.Should().Match<DebugState>(exited => exited.Status == "Exited" && exited.Output.Contains("Passed!"));
    }
}
