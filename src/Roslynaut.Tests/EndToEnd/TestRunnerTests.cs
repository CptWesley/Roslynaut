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

    // A copy of the test project, with a test that does not compile: no test runs, and the build errors say why.
    [Test]
    public async Task RunTests_ThatDoNotCompile_GiveTheBuildErrors()
    {
        var project = Sample.Write("edits/BrokenTests/BrokenTests.csproj", File.ReadAllText(Sample.TestsProject));
        var file = Sample.Write("edits/BrokenTests/BrokenTests.cs",
            "using NUnit.Framework;\n\nnamespace Edits;\n\npublic class BrokenTests\n{\n    [Test]\n    public void Breaks() => Assert.That(new Shapes.Hexagon(), Is.Not.Null);\n}\n");

        var run = await Server.CallAsync<RunTestsTool.TestRun>("run_tests", new() { ["projectPath"] = project });

        var (line, column) = Sample.Find(file, "Hexagon");
        (run.Succeeded, run.Total, run.Failures.Count).Should().Be((false, 0, 0));
        run.BuildErrors.Should().Equal(new BuildTool.BuildMessage(
            "Error", "CS0234", "The type or namespace name 'Hexagon' does not exist in the namespace 'Shapes' (are you missing an assembly reference?)", file, line, column, project));
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

    // A repository of its own, with NUnit's attribute declared in it: a commit changes Add, then Sub is changed without a commit, and a file with
    // a name that is not ASCII is added without one, which git names in UTF-8.
    [Test]
    public async Task AffectedTests_SinceAnEarlierCommit()
    {
        const string calc = "namespace Edits;\n\npublic static class Calc\n{\n    public static int Add(int a, int b) => a + b;\n\n    public static int Sub(int a, int b) => a - b;\n}\n";
        var project = Sample.NewProject("AffectedSince",
            (".gitignore", "bin/\nobj/\n"),
            ("TestAttribute.cs", "namespace NUnit.Framework;\n\npublic sealed class TestAttribute : System.Attribute\n{\n}\n"),
            ("Calc.cs", calc),
            ("CalcTests.cs", "using NUnit.Framework;\n\nnamespace Edits;\n\npublic class CalcTests\n{\n    [Test]\n    public void Adds() => Calc.Add(1, 2);\n\n    [Test]\n    public void Subtracts() => Calc.Sub(2, 1);\n\n"
                + "    [Test]\n    public void Measures() => Size.Of(3);\n}\n"));
        var (directory, file, tests) = (Path.GetDirectoryName(project)!, Sample.In("edits", "AffectedSince", "Calc.cs"), Sample.In("edits", "AffectedSince", "CalcTests.cs"));
        var size = Sample.In("edits", "AffectedSince", "Größe.cs");
        async Task GitAsync(params string[] arguments)
        {
            var (exitCode, output) = await DotnetCli.RunAsync("git", ["-C", directory, "-c", "user.name=Roslynaut", "-c", "user.email=roslynaut@example.com", "-c", "commit.gpgsign=false", .. arguments],
                TimeSpan.FromMinutes(1), CancellationToken.None);
            exitCode.Should().Be(0, output);
        }

        await GitAsync("init", "-q");
        await GitAsync("add", ".");
        await GitAsync("commit", "-q", "-m", "First");
        File.WriteAllText(file, calc.Replace("a + b", "b + a"));
        await GitAsync("commit", "-q", "-am", "Second");
        File.WriteAllText(file, calc.Replace("a + b", "b + a").Replace("a - b", "-b + a"));
        File.WriteAllText(size, "namespace Edits;\n\npublic static class Size\n{\n    public static int Of(int a) => a;\n}\n");

        var head = await Server.CallAsync<AffectedTestsTool.AffectedTests>("affected_tests", new() { ["projectPath"] = project });
        var since = await Server.CallAsync<AffectedTestsTool.AffectedTests>("affected_tests", new() { ["projectPath"] = project, ["baseRef"] = "HEAD~1" });

        FindTestsTool.TestEntry Test(string name) =>
            new(new SymbolEntry(name, "Method", $"Edits.CalcTests.{name}()", "Edits.CalcTests", Sample.Location(tests, $"{name}()")), project, $"FullyQualifiedName~Edits.CalcTests.{name}");
        SymbolEntry Member(string name) => new(name, "Method", $"Edits.Calc.{name}(int, int)", "Edits.Calc", Sample.Location(file, $"{name}("));
        SymbolEntry[] added = [new("Size", "Class", "Edits.Size", "Edits", Sample.Location(size, "Size")), new("Of", "Method", "Edits.Size.Of(int)", "Edits.Size", Sample.Location(size, "Of("))];
        head.ChangedMembers.Should().BeEquivalentTo([Member("Sub"), .. added]);
        head.Tests.Should().BeEquivalentTo([Test("Subtracts"), Test("Measures")]);
        since.ChangedMembers.Should().BeEquivalentTo([Member("Add"), Member("Sub"), .. added]);
        since.Tests.Should().BeEquivalentTo([Test("Adds"), Test("Subtracts"), Test("Measures")]);
        (await Server.CallFailingAsync("affected_tests", new() { ["projectPath"] = project, ["baseRef"] = "no-such-branch" }))
            .Should().StartWith("An error occurred invoking 'affected_tests': git diff failed: ");
    }

    [Test]
    public async Task AffectedTests_WithoutChanges_AreNone()
    {
        var affected = await Server.CallAsync<AffectedTestsTool.AffectedTests>("affected_tests", new() { ["projectPath"] = Sample.Solution });

        affected.Should().BeEquivalentTo(new AffectedTestsTool.AffectedTests([], [], null));
    }
}
