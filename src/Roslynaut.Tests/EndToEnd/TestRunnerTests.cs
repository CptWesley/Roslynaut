using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>find_tests, affected_tests and run_tests on the NUnit project: three tests pass, Classify_UnitSquare_IsSmall fails, and one is ignored.</summary>
public class TestRunnerTests : EndToEndTest
{
    private static FindTestsTool.TestEntry Test(string name) => new(
        new SymbolEntry(name, "Method", $"Shapes.Tests.ShapeTests.{name}()", "Shapes.Tests.ShapeTests", Sample.Location(Sample.TestsFile, $"{name}()")),
        Sample.TestsProject,
        $"FullyQualifiedName=Shapes.Tests.ShapeTests.{name}");

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

    // Code uses a type through its members, here its constructor: these tests construct squares.
    [Test]
    public async Task FindTests_ThatUseAType_ThroughItsMembers()
    {
        var tests = await Server.CallAsync<PagedResult<FindTestsTool.TestEntry>>("find_tests", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Shapes.Square" });

        tests.Items.Should().BeEquivalentTo([Test("Square_Area_IsSideSquared"), Test("TotalArea_AddsTheAreas"), Test("Classify_UnitSquare_IsSmall")]);
    }

    // TotalArea_AddsTheAreas constructs a rectangle; the tests that construct squares call Rectangle's constructor through Square's.
    [Test]
    public async Task FindTests_ThatUseABaseConstructor_ThroughADerivedPrimaryConstructor()
    {
        var tests = await Server.CallAsync<PagedResult<FindTestsTool.TestEntry>>("find_tests", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Shapes.Rectangle..ctor" });

        tests.Items.Should().BeEquivalentTo([Test("Square_Area_IsSideSquared"), Test("TotalArea_AddsTheAreas"), Test("Classify_UnitSquare_IsSmall")]);
    }

    // A filter by the name a test's name starts with would also run Add_Negative for Add. NUnit names each case of a test with parameters by its
    // arguments, as in Twice(1), so the filter of such a test takes the name the cases start with.
    [Test]
    public async Task FindTests_GiveFiltersThatRunOnlyTheirTest()
    {
        var project = Sample.Write("edits/Filters/Filters.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.11.0" />
                <PackageReference Include="NUnit" Version="5.0.0" />
                <PackageReference Include="NUnit3TestAdapter" Version="6.3.1" />
              </ItemGroup>
            </Project>
            """);
        Sample.Write("edits/Filters/CalcTests.cs", """
            using NUnit.Framework;

            namespace Edits;

            public class CalcTests
            {
                [Test]
                public void Add() => Assert.That(1 + 1, Is.EqualTo(2));

                [Test]
                public void Add_Negative() => Assert.That(-1 + -1, Is.EqualTo(-2));

                [TestCase(1)]
                [TestCase(2)]
                public void Twice(int x) => Assert.That(x * 2, Is.EqualTo(x + x));
            }
            """);
        await Sample.DotnetAsync("restore", project);
        async Task<RunTestsTool.TestRun> RunAsync(string filter) => await Server.CallAsync<RunTestsTool.TestRun>("run_tests", new() { ["projectPath"] = project, ["filter"] = filter });

        var filters = (await Server.CallAsync<PagedResult<FindTestsTool.TestEntry>>("find_tests", new() { ["projectPath"] = project })).Items.ToDictionary(test => test.Test.Name, test => test.Filter);
        var add = await RunAsync(filters["Add"]);
        var twice = await RunAsync(filters["Twice"]);
        var none = await RunAsync("FullyQualifiedName=Edits.CalcTests.Subtract");

        filters.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["Add"] = "FullyQualifiedName=Edits.CalcTests.Add", ["Add_Negative"] = "FullyQualifiedName=Edits.CalcTests.Add_Negative", ["Twice"] = "FullyQualifiedName~Edits.CalcTests.Twice",
        });
        (add.Succeeded, add.Total, add.Passed).Should().Be((true, 1, 1));
        (twice.Succeeded, twice.Total, twice.Passed).Should().Be((true, 2, 2));
        // Without a test to report, the end of the log tells what ran, as when the filter matched none or a test adapter failed.
        none.Total.Should().Be(0);
        none.Output.Should().Contain("A total of 1 test files matched the specified pattern.");
    }

    // A test runs under the name of each class that inherits it, and of its own class unless that is abstract. NUnit names a generic class by its
    // type arguments, as in GenericTests<Int32>. Each filter runs its test in two classes: six tests of the eight.
    [Test]
    public async Task FindTests_OfInheritedAndGenericTests_RunThemInEachClass()
    {
        var project = Sample.Write("edits/Inherited/Inherited.csproj", File.ReadAllText(Sample.TestsProject).Replace(@"<ProjectReference Include=""..\..\src\Shapes\Shapes.csproj"" />", ""));
        Sample.Write("edits/Inherited/InheritedTests.cs", """
            using NUnit.Framework;

            namespace Edits;

            public abstract class ContractTests
            {
                [Test]
                public void Holds() => Assert.Pass();
            }

            public class ListContract : ContractTests
            {
            }

            public class ArrayContract : ContractTests
            {
            }

            public class BaseTests
            {
                [Test]
                public void Runs() => Assert.Pass();
            }

            public sealed class MoreTests : BaseTests
            {
            }

            [TestFixture(typeof(int))]
            [TestFixture(typeof(string))]
            public class GenericTests<T>
            {
                [Test]
                public void Generic() => Assert.Pass();

                [Test]
                public void Generic_Other() => Assert.Pass();
            }
            """);
        await Sample.DotnetAsync("restore", project);

        var filters = (await Server.CallAsync<PagedResult<FindTestsTool.TestEntry>>("find_tests", new() { ["projectPath"] = project })).Items.ToDictionary(test => test.Test.Name, test => test.Filter);
        var run = await Server.CallAsync<RunTestsTool.TestRun>("run_tests", new() { ["projectPath"] = project, ["filter"] = $"{filters["Holds"]}|{filters["Runs"]}|{filters["Generic"]}" });

        filters["Holds"].Split('|').Should().BeEquivalentTo("FullyQualifiedName=Edits.ListContract.Holds", "FullyQualifiedName=Edits.ArrayContract.Holds");
        filters["Runs"].Split('|').Should().BeEquivalentTo("FullyQualifiedName=Edits.BaseTests.Runs", "FullyQualifiedName=Edits.MoreTests.Runs");
        filters["Generic"].Should().Be("(FullyQualifiedName~Edits.GenericTests<&Name=Generic)");
        (run.Succeeded, run.Total, run.Passed).Should().Be((true, 6, 6));
    }

    [Test]
    public async Task RunTests_OfTheSolution_RunItsTestProject()
    {
        var run = await Server.CallAsync<RunTestsTool.TestRun>("run_tests", new() { ["projectPath"] = Sample.Solution, ["filter"] = "FullyQualifiedName~Shapes.Tests.ShapeTests.Square_Area_IsSideSquared" });

        (run.Succeeded, run.Total, run.Passed, run.Failures.Count, run.BuildErrors.Count).Should().Be((true, 1, 1, 0, 0));
    }

    // A filter among the arguments works as one passed as filter does; the Release build goes to its own output directory.
    [Test]
    public async Task RunTests_InAnotherConfiguration_WithMoreArguments()
    {
        var run = await Server.CallAsync<RunTestsTool.TestRun>("run_tests", new()
        {
            ["projectPath"] = Sample.TestsProject, ["configuration"] = "Release", ["arguments"] = new[] { "--filter", "Name=Square_Area_IsSideSquared" },
        });

        (run.Succeeded, run.Total, run.Passed).Should().Be((true, 1, 1));
        File.Exists(Sample.In("tests", "Shapes.Tests", "bin", "Release", "net10.0", "Shapes.Tests.dll")).Should().BeTrue();
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
            affected.Filter.Should().Be("FullyQualifiedName=Shapes.Tests.ShapeTests.Classify_UnitSquare_IsSmall");

            var run = await Server.CallAsync<RunTestsTool.TestRun>("run_tests", new() { ["projectPath"] = Sample.TestsProject, ["filter"] = affected.Filter });
            (run.Succeeded, run.Total, run.Passed).Should().Be((true, 1, 1));
        }
        finally
        {
            File.WriteAllText(Sample.ShapeMathFile, original);
        }
    }

    // A repository of its own, with NUnit's attribute declared in it: a commit changes Add, then Sub is changed without a commit, and a file with
    // a name that is not ASCII is added without one, which git names in UTF-8. Its configuration changes the prefixes of diff's paths.
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
        // As some people configure git: diff then writes "+++ w/Calc.cs" rather than "+++ b/Calc.cs".
        await GitAsync("config", "diff.mnemonicPrefix", "true");
        await GitAsync("add", ".");
        await GitAsync("commit", "-q", "-m", "First");
        File.WriteAllText(file, calc.Replace("a + b", "b + a"));
        await GitAsync("commit", "-q", "-am", "Second");
        File.WriteAllText(file, calc.Replace("a + b", "b + a").Replace("a - b", "-b + a"));
        File.WriteAllText(size, "namespace Edits;\n\npublic static class Size\n{\n    public static int Of(int a) => a;\n}\n");

        var head = await Server.CallAsync<AffectedTestsTool.AffectedTests>("affected_tests", new() { ["projectPath"] = project });
        var since = await Server.CallAsync<AffectedTestsTool.AffectedTests>("affected_tests", new() { ["projectPath"] = project, ["baseRef"] = "HEAD~1" });

        FindTestsTool.TestEntry Test(string name) =>
            new(new SymbolEntry(name, "Method", $"Edits.CalcTests.{name}()", "Edits.CalcTests", Sample.Location(tests, $"{name}()")), project, $"FullyQualifiedName=Edits.CalcTests.{name}");
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
