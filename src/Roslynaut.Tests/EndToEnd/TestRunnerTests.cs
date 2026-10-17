using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>find_tests, affected_tests and run_tests on the NUnit project: three tests pass, Classify_UnitSquare_IsSmall fails, and one is ignored.</summary>
public class TestRunnerTests : EndToEndTest
{
    private static FindTestsTool.TestEntry Test(string name) => new(
        new SymbolEntry(name, "Method", $"Shapes.Tests.ShapeTests.{name}()", "Shapes.Tests.ShapeTests", Sample.Location(Sample.TestsFile, $"{name}()")),
        Sample.TestsProject,
        $"FullyQualifiedName~Shapes.Tests.ShapeTests.{name}");

    [Test]
    public async Task FindTests_All_WithAFilterEach()
    {
        var tests = await Server.CallAsync<PagedResult<FindTestsTool.TestEntry>>("find_tests", new() { ["projectPath"] = Sample.Solution });

        tests.Items.Should().BeEquivalentTo(
        [
            Test("Circle_Area_IsPiTimesRadiusSquared"),
            Test("Square_Area_IsSideSquared"),
            Test("TotalArea_AddsTheAreas"),
            Test("Classify_UnitSquare_IsSmall"),
            Test("Largest_PicksTheBiggest"),
        ]);
    }

    // Rectangle.Area is reached directly through Square, and through the interface by TotalArea and Classify; Circle has its own Area.
    [Test]
    public async Task FindTests_ThatUseASymbol_ThroughCallsAndOverrides()
    {
        var tests = await Server.CallAsync<PagedResult<FindTestsTool.TestEntry>>("find_tests", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Rectangle.Area" });

        tests.Items.Should().BeEquivalentTo([Test("Square_Area_IsSideSquared"), Test("TotalArea_AddsTheAreas"), Test("Classify_UnitSquare_IsSmall")]);
    }

    [Test]
    public async Task RunTests_All_ReportsTheFailure()
    {
        var run = await Server.CallAsync<RunTestsTool.TestRun>("run_tests", new() { ["projectPath"] = Sample.TestsProject });

        (run.Succeeded, run.Total, run.Passed, run.Failed, run.Skipped, run.Output).Should().Be((false, 5, 3, 1, 1, null));
        run.BuildErrors.Should().BeEmpty();
        var failure = run.Failures.Should().ContainSingle().Subject;
        (failure.Test, failure.Outcome).Should().Be(("Classify_UnitSquare_IsSmall", "Failed"));
        failure.Message.Should().Contain("Expected: \"small\"").And.Contain("But was:  \"medium\"");
        failure.StackTrace.Should().Contain($"Shapes.Tests.ShapeTests.Classify_UnitSquare_IsSmall() in {Sample.TestsFile}:line {Sample.LineOf(Sample.TestsFile, "Classify_UnitSquare_IsSmall()")}");
    }

    // The test covers TotalArea, and through it Square's and Rectangle's Area, but not Classify or Largest.
    [Test]
    public async Task RunTests_WithAFilter_AndCoverage()
    {
        var run = await Server.CallAsync<RunTestsTool.TestRun>("run_tests", new()
        {
            ["projectPath"] = Sample.TestsProject, ["filter"] = "FullyQualifiedName~Shapes.Tests.ShapeTests.TotalArea_AddsTheAreas", ["coverage"] = true,
        });

        (run.Succeeded, run.Total, run.Passed, run.Failed, run.Skipped).Should().Be((true, 1, 1, 0, 0));
        run.Failures.Should().BeEmpty();
        // Lines with code: Classify's expression; Largest's body, from its opening brace; its return and closing brace.
        var coverage = run.Coverage!.ToDictionary(file => file.File);
        var (classify, largest, @return) = (Line("Classify("), Line("Largest("), Line("return largest"));
        coverage[Sample.ShapeMathFile].NotCovered.Should().Be($"{classify}-{classify + 5}, {largest + 1}-{@return - 2}, {@return}-{@return + 1}");
        coverage[Sample.CircleFile].Covered.Should().Be(0);

        static int Line(string text) => Sample.LineOf(Sample.ShapeMathFile, text);
    }

    // Making a unit square small changes Classify, so its test is affected, and passes now.
    [Test]
    public async Task AffectedTests_OfAChange_ThenRunThem()
    {
        var original = File.ReadAllText(Sample.ShapeMathFile);
        try
        {
            File.WriteAllText(Sample.ShapeMathFile, original.Replace("< 1 => \"small\"", "<= 1 => \"small\""));

            var affected = await Server.CallAsync<AffectedTestsTool.AffectedTests>("affected_tests", new() { ["projectPath"] = Sample.Solution });

            affected.ChangedMembers.Should().Equal(new SymbolEntry("Classify", "Method", "Shapes.ShapeMath.Classify(Shapes.IShape)", "Shapes.ShapeMath",
                Sample.Location(Sample.ShapeMathFile, "Classify(")));
            affected.Tests.Should().BeEquivalentTo([Test("Classify_UnitSquare_IsSmall")]);
            affected.Filter.Should().Be("FullyQualifiedName~Shapes.Tests.ShapeTests.Classify_UnitSquare_IsSmall");

            var run = await Server.CallAsync<RunTestsTool.TestRun>("run_tests", new() { ["projectPath"] = Sample.TestsProject, ["filter"] = affected.Filter });
            (run.Succeeded, run.Total, run.Passed).Should().Be((true, 1, 1));
        }
        finally
        {
            File.WriteAllText(Sample.ShapeMathFile, original);
        }
    }

    [Test]
    public async Task AffectedTests_WithoutChanges_AreNone()
    {
        var affected = await Server.CallAsync<AffectedTestsTool.AffectedTests>("affected_tests", new() { ["projectPath"] = Sample.Solution });

        affected.Should().BeEquivalentTo(new AffectedTestsTool.AffectedTests([], [], null));
    }
}
