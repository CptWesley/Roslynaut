using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>
/// resolve_stack_trace, call_paths, control_flow, throws, find_duplicates, aspnet_endpoints and di_registrations through MCP, on the sample:
/// Spinner.Spin calls Circle.Area through IShape.
/// </summary>
public class InsightToolTests : EndToEndTest
{
    // A trace from a build machine: its paths are not the sample's, so lines map into the files the members are in now.
    [Test]
    public async Task ResolveStackTrace_MapsFramesToTheSample()
    {
        var line = Sample.LineOf(Sample.CircleFile, "Math.PI");
        var frames = await Server.CallAsync<List<ResolveStackTraceTool.ResolvedFrame>>("resolve_stack_trace", new()
        {
            ["projectPath"] = Sample.Solution,
            ["stackTrace"] = $"""
                System.OverflowException: Arithmetic operation resulted in an overflow.
                   at Shapes.Circle.Area() in /home/build/src/Shapes/Circle.cs:line {line}
                   at Shapes.App.Spinner.Spin(IShape[] shapes)
                   at System.Threading.Thread.StartCallback()
                """,
        });

        frames.Select(frame => frame.Symbol).Should().Equal("Shapes.Circle.Area", "Shapes.App.Spinner.Spin", null);
        frames[0].Location.Should().Be(Sample.Location(Sample.CircleFile, "public override double Area()") with { Column = 1 });
        frames[0].Note.Should().BeNull();
        frames[1].Location!.FilePath.Should().Be(Sample.SpinnerFile);
        frames[1].Note.Should().Contain("no line");
    }

    [Test]
    public async Task CallPaths_RunThroughTheInterface()
    {
        var paths = await Server.CallAsync<List<CallPathsTool.CallPath>>("call_paths", new()
        {
            ["projectPath"] = Sample.Solution, ["from"] = "Shapes.App.Spinner.Spin", ["to"] = "Shapes.Circle.Area",
        });

        var hop = paths.Should().ContainSingle().Which.Hops.Should().ContainSingle().Subject;
        (hop.Caller.Name, hop.Callee.Name, hop.Via).Should().Be(("Spin", "Area", "Shapes.IShape.Area()"));
        hop.Call.Should().Be(Sample.Location(Sample.SpinnerFile, "Area()"));
    }

    [Test]
    public async Task ControlFlow_AndThrows_OfSpin()
    {
        var flow = await Server.CallAsync<ControlFlowTool.ControlFlow>("control_flow", new()
        {
            ["projectPath"] = Sample.Solution, ["symbol"] = "Shapes.App.Spinner.Spin", ["toLine"] = Sample.LineOf(Sample.SpinnerFile, "sum += shape.Area();"),
        });
        var thrown = await Server.CallAsync<List<ThrowsTool.ThrownException>>("throws", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Shapes.App.Spinner.Spin" });

        (flow.Blocks[0].Kind, flow.Blocks[^1].Kind).Should().Be(("Entry", "Exit"));
        flow.PathsTo.Should().NotBeEmpty();
        thrown.Should().BeEmpty();
    }

    // The sample has no copies, no web endpoints and no service registrations; the calls still answer.
    [Test]
    public async Task Duplicates_Endpoints_AndRegistrations_OfTheSample()
    {
        (await Server.CallAsync<List<FindDuplicatesTool.DuplicateGroup>>("find_duplicates", new() { ["projectPath"] = Sample.Solution })).Should().BeEmpty();
        (await Server.CallAsync<List<EndpointsTool.Endpoint>>("aspnet_endpoints", new() { ["projectPath"] = Sample.Solution })).Should().BeEmpty();
        (await Server.CallAsync<List<DiRegistrationsTool.ServiceRegistrations>>("di_registrations", new() { ["projectPath"] = Sample.Solution })).Should().BeEmpty();
    }

    // A real run takes a minute; BenchmarkTests runs one. This server allows code to run, as evaluate's tests need.
    [Test]
    public async Task Benchmark_TakesCasesByName_AndChecksTheJob()
    {
        (await Server.CallFailingAsync("benchmark", new() { ["projectPath"] = Sample.Solution, ["cases"] = new Dictionary<string, string> { ["One"] = "1" }, ["job"] = "Long" }))
            .Should().Be("An error occurred invoking 'benchmark': job is Short or Default.");
    }
}
