using Microsoft.CodeAnalysis;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>find_tests, and the parsing of build output for build and run_tests.</summary>
public class TestToolsTests
{
    // Stand-ins for the test frameworks' attributes; xUnit's [Theory] derives from [Fact], as in xUnit.
    private static readonly Solution Tests = TestSolution.Create("""
        namespace NUnit.Framework { public class TestAttribute : System.Attribute { } }
        namespace Xunit { public class FactAttribute : System.Attribute { } public class TheoryAttribute : FactAttribute { } }
        namespace App { public static class Calc { public static int Add(int a, int b) => a + b; public static int Twice(int a) => Add(a, a); } }
        namespace App.Tests
        {
            public class CalcTests
            {
                [NUnit.Framework.Test] public void AddsDirectly() { App.Calc.Add(1, 2); }
                [Xunit.Theory] public void AddsThroughHelper() { Helper(); }
                private void Helper() { App.Calc.Twice(1); }
                [NUnit.Framework.Test] public void Unrelated() { }
                public class Nested { [Xunit.Fact] public void Inner() { } }
            }
        }
        """);

    [Test]
    public async Task FindTests_All_WithFilters()
    {
        (await FindTestsTool.FindAsync(Tests, null, CancellationToken.None)).Select(test => test.Filter).Should().BeEquivalentTo(
            "FullyQualifiedName=App.Tests.CalcTests.AddsDirectly",
            "FullyQualifiedName=App.Tests.CalcTests.AddsThroughHelper",
            "FullyQualifiedName=App.Tests.CalcTests.Unrelated",
            "FullyQualifiedName=App.Tests.CalcTests+Nested.Inner");
    }

    // Line 3 holds both Calc members, line 10 Helper, and lines 7 and 13 the braces of the test class, which change no member.
    [TestCase(new[] { 10 }, new[] { "Helper" }, "FullyQualifiedName=App.Tests.CalcTests.AddsThroughHelper")]
    [TestCase(new[] { 3 }, new[] { "Add", "Twice" }, "FullyQualifiedName=App.Tests.CalcTests.AddsDirectly|FullyQualifiedName=App.Tests.CalcTests.AddsThroughHelper")]
    [TestCase(new[] { 7, 13 }, new string[0], null)]
    public async Task AffectedTests_MapChangedLinesToMembersAndTests(int[] lines, string[] members, string? filter)
    {
        var path = Path.GetFullPath("Test.cs");
        var solution = Tests.WithDocumentFilePath(Tests.Projects.Single().DocumentIds.Single(), path);

        var affected = await AffectedTestsTool.FindAsync(solution, new Dictionary<string, IReadOnlyCollection<int>?> { [path] = lines }, CancellationToken.None);

        affected.ChangedMembers.Select(member => member.Name).Should().BeEquivalentTo(members);
        affected.Filter?.Split('|').Should().BeEquivalentTo(filter?.Split('|'));
        (affected.Filter is null).Should().Be(filter is null);
    }

    [Test]
    public async Task ChangedLines_FromGit_IncludeUntrackedFiles()
    {
        var directory = Directory.CreateTempSubdirectory("roslynaut-git-");
        try
        {
            async Task Git(params string[] arguments) =>
                (await DotnetCli.RunAsync("git", ["-C", directory.FullName, .. arguments], TimeSpan.FromMinutes(1), CancellationToken.None)).ExitCode.Should().Be(0);

            var (changed, added) = (Path.Combine(directory.FullName, "Changed.cs"), Path.Combine(directory.FullName, "Added.cs"));
            File.WriteAllText(changed, "class A\n{\n    int X;\n}\n");
            await Git("init", "-q");
            await Git("add", ".");
            await Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "Initial");
            File.WriteAllText(changed, "class A\n{\n    int Y;\n}\n");
            File.WriteAllText(added, "class B { }\n");

            var changes = await AffectedTestsTool.ChangedLinesAsync(directory.FullName, "HEAD", CancellationToken.None);

            changes.Should().HaveCount(2);
            changes[changed].Should().Equal(3);
            changes[added].Should().BeNull();
        }
        finally
        {
            // git marks its object files read-only.
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
                file.Attributes = FileAttributes.Normal;
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task MemberChanges_SinceACommit_AddedAndRemoved()
    {
        var directory = Directory.CreateTempSubdirectory("roslynaut-git-");
        try
        {
            async Task Git(params string[] arguments) =>
                (await DotnetCli.RunAsync("git", ["-C", directory.FullName, .. arguments], TimeSpan.FromMinutes(1), CancellationToken.None)).ExitCode.Should().Be(0);

            var (changed, deleted) = (Path.Combine(directory.FullName, "Changed.cs"), Path.Combine(directory.FullName, "Deleted.cs"));
            File.WriteAllText(changed, "namespace N;\nclass A\n{\n    int X;\n    void M(int a) { }\n    void Same() { }\n}\n");
            File.WriteAllText(deleted, "class D { }\n");
            await Git("init", "-q");
            await Git("add", ".");
            await Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "Initial");
            File.WriteAllText(changed, "namespace N;\nclass A\n{\n    int Y;\n    void M(long a) { }\n    void Same() { }\n}\n");
            File.Delete(deleted);
            File.WriteAllText(Path.Combine(directory.FullName, "Added.cs"), "class B { }\n");

            var changes = await AffectedTestsTool.ChangedLinesAsync(directory.FullName, "HEAD", CancellationToken.None);
            var (added, removed) = await AffectedTestsTool.MemberChangesAsync(directory.FullName, "HEAD", changes.Keys, CancellationToken.None);

            added.Should().BeEquivalentTo("N.A.Y", "N.A.M(long)", "B");
            removed.Should().BeEquivalentTo("N.A.X", "N.A.M(int)", "D");
        }
        finally
        {
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
                file.Attributes = FileAttributes.Normal;
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public void ParseDiff_TakesTheNewLines_AndSkipsDeletedFiles()
    {
        var root = Path.GetTempPath();
        var changes = AffectedTestsTool.ParseDiff(root, """
            diff --git a/src/A.cs b/src/A.cs
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -3,0 +4,2 @@ class A
            +x
            +y
            @@ -10 +12 @@
            -a
            +b
            @@ -20,2 +21,0 @@
            -c
            -d
            diff --git a/Old.cs b/Old.cs
            deleted file mode 100644
            --- a/Old.cs
            +++ /dev/null
            @@ -1 +0,0 @@
            -gone
            """);

        changes.Should().ContainSingle().Which.Should().Match<KeyValuePair<string, IReadOnlyCollection<int>?>>(change =>
            change.Key == Path.GetFullPath(Path.Combine(root, "src", "A.cs")) && change.Value!.Order().SequenceEqual(new[] { 4, 5, 12, 21, 22 }));
    }

    [Test]
    public async Task FindTests_UsingSymbol_FollowsCallers()
    {
        var add = await SymbolTree.ResolveAsync(Tests, "App.Calc.Add", CancellationToken.None);

        (await FindTestsTool.FindAsync(Tests, add, CancellationToken.None)).Select(test => test.Test.Name).Should().BeEquivalentTo("AddsDirectly", "AddsThroughHelper");
    }

    [Test]
    public void ParseMessages_ReadsMsBuildFormat_ErrorsFirst_EachOnce()
    {
        const string output = """
            C:\src\App\Program.cs(4,13): warning CS0168: The variable 'x' is declared but never used [C:\src\App\App.csproj]
            C:\src\App\Program.cs(5,11): error CS0029: Cannot implicitly convert type 'string' to 'int' [C:\src\App\App.csproj]
            C:\src\App\Program.cs(5,11): error CS0029: Cannot implicitly convert type 'string' to 'int' [C:\src\App\App.csproj]
            MSBUILD : error MSB1009: Project file does not exist.
            Build FAILED.
            """;

        BuildTool.ParseMessages(output).Should().Equal(
            new BuildTool.BuildMessage("Error", "CS0029", "Cannot implicitly convert type 'string' to 'int'", @"C:\src\App\Program.cs", 5, 11, @"C:\src\App\App.csproj"),
            new BuildTool.BuildMessage("Error", "MSB1009", "Project file does not exist.", "MSBUILD", null, null, null),
            new BuildTool.BuildMessage("Warning", "CS0168", "The variable 'x' is declared but never used", @"C:\src\App\Program.cs", 4, 13, @"C:\src\App\App.csproj"));
    }
}

/// <summary>build and run_tests against a real project, built and run with dotnet.</summary>
[NonParallelizable]
public class BuildAndRunTestsTests
{
    private DirectoryInfo directory = null!;
    private string project = null!;

    [OneTimeSetUp]
    public void CreateProject()
    {
        directory = Directory.CreateTempSubdirectory("roslynaut-sample-");
        project = Path.Combine(directory.FullName, "Sample.Tests.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.11.0" />
                <PackageReference Include="NUnit" Version="5.0.0" />
                <PackageReference Include="NUnit3TestAdapter" Version="6.3.1" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory.FullName, "SampleTests.cs"), """
            using NUnit.Framework;

            namespace Sample;

            public class SampleTests
            {
                [Test] public void Passes() => Assert.That(1 + 1, Is.EqualTo(2));
                [Test] public void Fails() => Assert.That(1 + 1, Is.EqualTo(3));
                [Test, Ignore("Not now")] public void Ignored() { }
            }
            """);

        // Hangs only while a file next to the test assembly says so.
        File.WriteAllText(Path.Combine(directory.FullName, "HangTests.cs"), """
            using NUnit.Framework;

            namespace Sample;

            public class HangTests
            {
                [Test] public void Hangs()
                {
                    if (System.IO.File.Exists(System.IO.Path.Combine(System.AppContext.BaseDirectory, "hang")))
                        System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
                }
            }
            """);
    }

    [OneTimeTearDown]
    public void DeleteProject() => directory.Delete(recursive: true);

    [Test]
    public async Task Build_Succeeds()
    {
        var result = await BuildTool.BuildAsync(project);

        result.Should().Match<BuildTool.BuildResult>(build => build.Succeeded && build.Errors == 0);
    }

    [Test]
    public async Task Build_WithTimings_SaysWhereTheTimeWent()
    {
        var timings = (await BuildTool.BuildAsync(project, timings: true)).Timings;

        timings.Should().NotBeNull();
        timings!.Milliseconds.Should().BePositive();
        timings.Projects.Should().Contain(timing => timing.Name.StartsWith("Sample.Tests"));
        timings.Targets.Should().NotBeEmpty().And.OnlyContain(timing => timing.Project != null);
        timings.Tasks.Should().NotBeEmpty();
    }

    [Test]
    public async Task RunTests_CountsOutcomes_AndReportsFailures()
    {
        var run = await RunTestsTool.RunTestsAsync(project);

        (run.Succeeded, run.Total, run.Passed, run.Failed, run.Skipped).Should().Be((false, 4, 2, 1, 1));
        run.Failures.Should().ContainSingle().Which.Should().Match<RunTestsTool.TestFailure>(failure =>
            failure.Test == "Fails" && failure.Message!.Contains("Expected: 3") && failure.StackTrace!.Contains("SampleTests.cs"));
    }

    [Test]
    public async Task RunTests_Repeated_CountsTheRunsEachTestFailed()
    {
        var run = await RunTestsTool.RunTestsAsync(project, "FullyQualifiedName~Sample.SampleTests.Fails|FullyQualifiedName~Sample.SampleTests.Passes", repeat: 2);

        (run.Runs, run.Total, run.Failed).Should().Be((2, 4, 2));
        run.Failures.Should().ContainSingle();
        run.FailedRuns.Should().ContainSingle().Which.Should().Match<RunTestsTool.TestRepeat>(test =>
            test.Test == "Fails" && test.Failed == 2 && test.Runs == 2 && test.Messages.Count == 1);
    }

    [Test]
    public async Task RunTests_WithHangTimeout_DumpsTheHungTestHost()
    {
        await BuildTool.BuildAsync(project);
        var marker = Directory.EnumerateFiles(directory.FullName, "Sample.Tests.dll", SearchOption.AllDirectories)
            .Select(file => Path.Combine(Path.GetDirectoryName(file)!, "hang")).First(file => file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        File.WriteAllText(marker, "");
        RunTestsTool.TestRun run;
        try
        {
            run = await RunTestsTool.RunTestsAsync(project, "FullyQualifiedName~Sample.HangTests", hangTimeout: 10);
        }
        finally
        {
            File.Delete(marker);
        }

        try
        {
            run.Hang.Should().NotBeNull();
            run.Hang!.Running.Should().Equal("Sample.HangTests.Hangs");
            run.Hang.Threads.Should().Contain(thread => thread.Stack.Any(frame => frame.Contains("Sample.HangTests.Hangs")));
            File.Exists(run.Hang.Dump).Should().BeTrue();
        }
        finally
        {
            if (run.Hang is not null)
                File.Delete(run.Hang.Dump);
        }
    }

    [Test]
    public void Mutations_ReadStrykersReport()
    {
        var report = RunTestsTool.Mutations("""
            {"files": {"C:\\src\\Calc.cs": {"mutants": [
              {"id": "0", "mutatorName": "Arithmetic mutation", "replacement": "a - b", "status": "Killed", "location": {"start": {"line": 4, "column": 44}, "end": {"line": 4, "column": 49}}},
              {"id": "1", "mutatorName": "Equality mutation", "replacement": "a >= b", "status": "Survived", "location": {"start": {"line": 5, "column": 44}, "end": {"line": 5, "column": 49}}},
              {"id": "2", "mutatorName": "Equality mutation", "replacement": "a < b", "status": "NoCoverage", "location": {"start": {"line": 5, "column": 44}, "end": {"line": 5, "column": 49}}},
              {"id": "3", "mutatorName": "String mutation", "status": "CompileError", "location": {"start": {"line": 6, "column": 1}, "end": {"line": 6, "column": 2}}}
            ]}}}
            """);

        (report.Score, report.Killed, report.Survived, report.NoCoverage).Should().Be((33.3, 1, 1, 1));
        report.Undetected.Should().Equal(
            new RunTestsTool.Mutant("C:\\src\\Calc.cs", 5, 44, "Equality mutation", "a >= b", "Survived"),
            new RunTestsTool.Mutant("C:\\src\\Calc.cs", 5, 44, "Equality mutation", "a < b", "NoCoverage"));
    }

    [Test]
    public async Task RunTests_WithFilter_RunsOnlyThose()
    {
        var run = await RunTestsTool.RunTestsAsync(project, "FullyQualifiedName~Sample.SampleTests.Passes");

        (run.Succeeded, run.Total, run.Passed).Should().Be((true, 1, 1));
    }

    [Test]
    public async Task RunTests_WithCoverage_ReportsLinesNotRun()
    {
        var run = await RunTestsTool.RunTestsAsync(project, coverage: true);

        run.Coverage.Should().ContainSingle(file => file.File.EndsWith("SampleTests.cs")).Which.NotCovered.Should().Be("9");
    }

    [Test]
    public async Task Packages_ListsTopLevel_AndTransitiveWhenAsked()
    {
        (await PackagesTool.ListAsync(project, PackageFilter.All, transitive: false, CancellationToken.None))
            .Should().Contain(package => package.Id == "NUnit" && package.Requested == "5.0.0" && !package.Transitive);
        (await PackagesTool.ListAsync(project, PackageFilter.All, transitive: true, CancellationToken.None))
            .Should().Contain(package => package.Transitive);
    }

    [Test]
    public async Task MsBuildEvaluate_AndProjectGraph_ReadTheDesignTimeBuild()
    {
        var loaded = await new WorkspaceService().GetAsync(project);

        var evaluation = MsBuildEvaluateTool.Evaluate(loaded, ["TargetFramework", "Package*"]).Should().ContainSingle().Subject;
        evaluation.Properties["TargetFramework"].Should().Be("net10.0");
        evaluation.Items["PackageReference"].Select(item => item.Include).Should().Contain("NUnit");
        ProjectGraphTool.Graph(loaded).Should().ContainSingle().Which.Should().Match<ProjectGraphTool.ProjectNode>(node =>
            node.Name == "Sample.Tests" && node.TargetFramework == "net10.0" && node.References.Count == 0);
    }

    [Test]
    public async Task ReferencedSymbols_HaveDocumentation()
    {
        var solution = (await new WorkspaceService().GetAsync(project)).Solution;
        var compilation = await solution.Projects.Single().GetCompilationAsync();

        compilation!.GetTypeByMetadataName("System.String")!.GetDocumentationCommentXml().Should().Contain("<summary>");
    }

    [Test]
    public async Task Build_WithCompileError_ReturnsIt()
    {
        var broken = Path.Combine(directory.FullName, "Broken.cs");
        File.WriteAllText(broken, "namespace Sample;\npublic class Broken { int x = \"s\"; }\n");
        try
        {
            var result = await BuildTool.BuildAsync(project);

            result.Succeeded.Should().BeFalse();
            result.Messages.Should().Contain(message => message.Code == "CS0029" && message.Origin == broken && message.Line == 2);
        }
        finally
        {
            File.Delete(broken);
        }
    }
}

/// <summary>run_tests under Microsoft.Testing.Platform: what its dotnet test writes, and filters per test framework.</summary>
public class TestingPlatformTests
{
    // As TUnit writes a failure: the message has lines that are not indented.
    private const string TUnitOutput = """
        failed Fails (37ms)
          AssertionException: Expected 1 + 1 to be equal to 3

        but found 2

        at Assert.That(1 + 1).IsEqualTo(3)
          from C:\src\Tu\bin\Debug\net10.0\Tu.dll (net10.0|x64)
          TUnit.Engine.Exceptions.TestFailedException: AssertionException: Expected 1 + 1 to be equal to 3
        C:\src\Tu\bin\Debug\net10.0\Tu.dll (net10.0|x64) failed with 1 error(s) [+1/x1/?1] (266ms)
        Exit code: 2

        Test run summary: Failed!
          total: 3
          failed: 1
          succeeded: 1
          skipped: 1
          duration: 549ms
        """;

    private const string XUnitOutput = """
        Running tests from C:\src\Xu\bin\Debug\net10.0\Xu.dll (net10.0|x64)
        failed Xu.Tests.Fails (19ms)
          Assert.Equal() Failure: Values differ
        Expected: 3
        Actual:   2
          from C:\src\Xu\bin\Debug\net10.0\Xu.dll (net10.0|x64)
          Assert.Equal() Failure: Values differ
            at Xu.Tests.Fails() in C:\src\Xu\T.cs:5
            at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
        skipped Xu.Tests.Skipped
          Not now
          from C:\src\Xu\bin\Debug\net10.0\Xu.dll (net10.0|x64)

        Test run summary: Failed!
          total: 3
          failed: 1
          succeeded: 1
          skipped: 1
        """;

    [Test]
    public void SummarizeConsole_ReadsCountsAndFailures()
    {
        var run = RunTestsTool.SummarizeConsole(2, XUnitOutput);
        (run.Succeeded, run.Total, run.Passed, run.Failed, run.Skipped, run.Output).Should().Be((false, 3, 1, 1, 1, (string?)null));
        run.Failures.Should().ContainSingle().Which.Should().Be(new RunTestsTool.TestFailure("Xu.Tests.Fails", "Failed",
            "Assert.Equal() Failure: Values differ\nExpected: 3\nActual:   2",
            @"at Xu.Tests.Fails() in C:\src\Xu\T.cs:5" + "\nat System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)"));

        RunTestsTool.SummarizeConsole(2, TUnitOutput).Failures.Should().ContainSingle().Which.Message.Should()
            .Be("AssertionException: Expected 1 + 1 to be equal to 3\n\nbut found 2\n\nat Assert.That(1 + 1).IsEqualTo(3)");
    }

    [Test]
    public void SummarizeConsole_WithoutTests_KeepsTheOutput()
    {
        RunTestsTool.SummarizeConsole(5, "  Standard output: Unknown option '--coverage'\n  total: 0\n").Output.Should().Contain("Unknown option");
    }

    [Test]
    public void FilterArguments_TranslateTestNames()
    {
        const string filter = "FullyQualifiedName~App.Tests.CalcTests.Adds|FullyQualifiedName~App.Tests.OtherTests.Subtracts";

        RunTestsTool.FilterArguments(filter, RunTestsTool.FilterStyle.VSTest).Should().Equal("--filter", filter);
        RunTestsTool.FilterArguments(filter, RunTestsTool.FilterStyle.XUnit).Should().Equal(
            "--filter-method", "*App.Tests.CalcTests.Adds*", "--filter-method", "*App.Tests.OtherTests.Subtracts*");
        RunTestsTool.FilterArguments("FullyQualifiedName=App.Tests.CalcTests.Adds", RunTestsTool.FilterStyle.XUnit).Should().Equal("--filter-method", "App.Tests.CalcTests.Adds");
        RunTestsTool.FilterArguments(filter, RunTestsTool.FilterStyle.TUnit).Should().Equal("--treenode-filter", "/*/App.Tests/(CalcTests|OtherTests)/(Adds|Subtracts)");
        FluentActions.Invoking(() => RunTestsTool.FilterArguments("TestCategory=Fast", RunTestsTool.FilterStyle.TUnit))
            .Should().Throw<ModelContextProtocol.McpException>().WithMessage("*--treenode-filter*");
    }
}

/// <summary>run_tests against real xUnit v3 and TUnit projects that global.json puts on Microsoft.Testing.Platform.</summary>
[NonParallelizable]
public class TestingPlatformRunTests
{
    private DirectoryInfo directory = null!;

    [OneTimeSetUp]
    public void CreateProjects()
    {
        directory = Directory.CreateTempSubdirectory("roslynaut-mtp-");
        void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory.FullName, path))!);
            File.WriteAllText(Path.Combine(directory.FullName, path), text);
        }

        Write("global.json", """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        foreach (var (name, package, version, code) in new[]
        {
            ("Xu", "xunit.v3", "3.1.0", """
                namespace Xu;
                public class Tests
                {
                    [Xunit.Fact] public void Passes() => Xunit.Assert.Equal(2, 1 + 1);
                    [Xunit.Fact] public void Fails() => Xunit.Assert.Equal(3, 1 + 1);
                }
                """),
            ("Tu", "TUnit", "0.57.24", """
                namespace Tu;
                public class Tests
                {
                    [Test] public async Task Passes() => await Assert.That(1 + 1).IsEqualTo(2);
                    [Test] public async Task Fails() => await Assert.That(1 + 1).IsEqualTo(3);
                }
                """),
        })
        {
            Write($"{name}/{name}.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
                  <ItemGroup><PackageReference Include="{package}" Version="{version}" /></ItemGroup>
                </Project>
                """);
            Write($"{name}/Tests.cs", code);
        }
    }

    [OneTimeTearDown]
    public void DeleteProjects() => directory.Delete(recursive: true);

    [TestCase("Xu")]
    [TestCase("Tu")]
    public async Task RunTests_CountsAndFilters(string name)
    {
        var project = Path.Combine(directory.FullName, name, $"{name}.csproj");
        RunTestsTool.UsesTestingPlatform(project).Should().BeTrue();

        var run = await RunTestsTool.RunTestsAsync(project);
        (run.Succeeded, run.Total, run.Passed, run.Failed).Should().Be((false, 2, 1, 1));
        run.Failures.Should().ContainSingle().Which.Message.Should().Contain("3");

        var filtered = await RunTestsTool.RunTestsAsync(project, $"FullyQualifiedName~{name}.Tests.Passes", workspace: new WorkspaceService());
        (filtered.Succeeded, filtered.Total, filtered.Passed).Should().Be((true, 1, 1));
    }
}

/// <summary>run_tests' mutate, with Stryker.NET (installed from nuget.org on first use) on a library whose tests check only some of its code.</summary>
[NonParallelizable]
public class MutationTests
{
    private DirectoryInfo directory = null!;

    [OneTimeSetUp]
    public void CreateProjects()
    {
        directory = Directory.CreateTempSubdirectory("roslynaut-mutation-");
        void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory.FullName, path))!);
            File.WriteAllText(Path.Combine(directory.FullName, path), text);
        }

        Write("Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        Write("Lib/Calc.cs", """
            namespace Lib;
            public static class Calc
            {
                public static int Add(int a, int b) => a + b;
                public static int Max(int a, int b) => a > b ? a : b;
            }
            """);
        Write("Lib.Tests/Lib.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.11.0" />
                <PackageReference Include="NUnit" Version="5.0.0" />
                <PackageReference Include="NUnit3TestAdapter" Version="6.3.1" />
                <ProjectReference Include="../Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        Write("Lib.Tests/CalcTests.cs", """
            using NUnit.Framework;
            namespace Lib.Tests;
            public class CalcTests
            {
                [Test] public void Adds() => Assert.That(Lib.Calc.Add(2, 3), Is.EqualTo(5));
                [Test] public void Max() => Assert.That(Lib.Calc.Max(1, 1), Is.EqualTo(1));
            }
            """);
    }

    [OneTimeTearDown]
    public void DeleteProjects() => directory.Delete(recursive: true);

    // Max(1, 1) cannot tell > from >=, nor either branch from the other.
    [Test]
    public async Task Mutate_ReportsTheChangesNoTestNoticed()
    {
        var calc = Path.Combine(directory.FullName, "Lib", "Calc.cs");

        var run = await RunTestsTool.MutateAsync(Path.Combine(directory.FullName, "Lib.Tests", "Lib.Tests.csproj"), [calc], new WorkspaceService(), CancellationToken.None);

        run.Output.Should().BeNull();
        run.Mutation.Should().NotBeNull();
        run.Mutation!.Killed.Should().BePositive();
        run.Mutation.Undetected.Should().NotBeEmpty().And.OnlyContain(mutant => mutant.Line == 5 && mutant.File == calc);
    }
}
