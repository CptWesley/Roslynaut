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
            "FullyQualifiedName~App.Tests.CalcTests.AddsDirectly",
            "FullyQualifiedName~App.Tests.CalcTests.AddsThroughHelper",
            "FullyQualifiedName~App.Tests.CalcTests.Unrelated",
            "FullyQualifiedName~App.Tests.CalcTests+Nested.Inner");
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
    public async Task RunTests_CountsOutcomes_AndReportsFailures()
    {
        var run = await RunTestsTool.RunTestsAsync(project);

        (run.Succeeded, run.Total, run.Passed, run.Failed, run.Skipped).Should().Be((false, 3, 1, 1, 1));
        run.Failures.Should().ContainSingle().Which.Should().Match<RunTestsTool.TestFailure>(failure =>
            failure.Test == "Fails" && failure.Message!.Contains("Expected: 3") && failure.StackTrace!.Contains("SampleTests.cs"));
    }

    [Test]
    public async Task RunTests_WithFilter_RunsOnlyThose()
    {
        var run = await RunTestsTool.RunTestsAsync(project, "FullyQualifiedName~Sample.SampleTests.Passes");

        (run.Succeeded, run.Total, run.Passed).Should().Be((true, 1, 1));
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
