using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>
/// build, and the build_* tools on the binary log of the first, full build of the solution (see <see cref="EndToEndSetup"/>). Shapes.App's
/// WriteBuildInfo target never writes its output, so it runs in every build.
/// </summary>
public class BuildTests : EndToEndTest
{
    private static string BuildLog => EndToEndSetup.BuildLog;

    [Test]
    public void Build_OfTheSolution_SucceededClean()
    {
        EndToEndSetup.Build.Should().BeEquivalentTo(new BuildTool.BuildResult(true, 0, 0, [], null));
    }

    [Test]
    public async Task Build_WithErrors_ReportsThemErrorsFirst()
    {
        var build = await Server.CallAsync<BuildTool.BuildResult>("build", new() { ["projectPath"] = Sample.ProblemsProject });

        var (error, warning) = (Sample.Find(Sample.AccountFile, "balance;", 2), Sample.Find(Sample.AccountFile, "unused"));
        build.Should().BeEquivalentTo(new BuildTool.BuildResult(false, 1, 1,
        [
            new BuildTool.BuildMessage("Error", "CS0029", "Cannot implicitly convert type 'int' to 'string'", Sample.AccountFile, error.Line, error.Column, Sample.ProblemsProject),
            new BuildTool.BuildMessage("Warning", "CS0168", "The variable 'unused' is declared but never used", Sample.AccountFile, warning.Line, warning.Column, Sample.ProblemsProject),
        ], null), options => options.WithStrictOrdering());
    }

    // The solution is built already, so this build skips most of its work; it still runs and times the projects and their targets.
    [Test]
    public async Task Build_WithTimings_TimesProjectsTargetsAndEvaluation()
    {
        var build = await Server.CallAsync<BuildTool.BuildResult>("build", new() { ["projectPath"] = Sample.ShapesProject, ["timings"] = true });

        build.Succeeded.Should().BeTrue();
        // Restore builds the project too, in builds of its own.
        var timings = build.Timings!;
        timings.Projects.Should().NotBeEmpty().And.OnlyContain(project => project.Name == "Shapes.csproj (net10.0)");
        timings.Targets.Should().OnlyContain(target => target.Project == "Shapes.csproj");
        timings.Tasks.Should().NotBeEmpty();
        timings.Evaluations.Should().ContainSingle().Which.Should().Match<BuildTool.Timing>(evaluation => evaluation.Name == "Shapes.csproj" && evaluation.Count >= 1);
        timings.EvaluationHotspots.Should().NotBeEmpty();
        timings.CriticalPath.Should().ContainSingle().Which.Project.Should().Be("Shapes.csproj (net10.0)");
        (timings.Nodes, timings.Milliseconds > 0).Should().Be((1, true));
    }

    [Test]
    public async Task BuildProblems_OfACleanBuild_AreNone()
    {
        var problems = await Server.CallAsync<BuildProblemsTool.BuildProblems>("build_problems", new() { ["binlogPath"] = BuildLog });

        problems.Should().BeEquivalentTo(new BuildProblemsTool.BuildProblems([], [], []));
    }

    [Test]
    public async Task BuildIncrementality_FindsTheTargetThatAlwaysRuns()
    {
        var incrementality = await Server.CallAsync<BuildIncrementalityTool.Incrementality>("build_incrementality", new() { ["projectPath"] = Sample.AppProject });

        incrementality.Should().BeEquivalentTo(new BuildIncrementalityTool.Incrementality(
            [new BuildIncrementalityTool.RebuiltTarget("Shapes.App.csproj (net10.0)", "WriteBuildInfo",
                $"Output file \"{Path.Combine("obj", "Debug", "net10.0", "buildinfo.txt")}\" does not exist.")],
            []));
    }

    // Shapes.dll in the app's output: copied from the library's output, which was copied from its obj directory, where the compiler wrote it.
    [Test]
    public async Task BuildFileOrigin_FollowsTheCopiesBackToTheCompiler()
    {
        var file = Sample.In("src", "Shapes.App", "bin", "Debug", "net10.0", "Shapes.dll");
        var writes = await Server.CallAsync<List<BuildFileOriginTool.FileWrite>>("build_file_origin", new() { ["binlogPath"] = BuildLog, ["filePath"] = file });

        var (output, intermediate) = (Sample.ShapesAssembly, Sample.In("src", "Shapes", "obj", "Debug", "net10.0", "Shapes.dll"));
        writes.Should().Equal(
            new BuildFileOriginTool.FileWrite(file, output, "Shapes.App.csproj (net10.0)", "_CopyFilesMarkedCopyLocal", "Copy", true),
            new BuildFileOriginTool.FileWrite(output, intermediate, "Shapes.csproj (net10.0)", "CopyFilesToOutputDirectory", "Copy", true),
            new BuildFileOriginTool.FileWrite(intermediate, null, "Shapes.csproj (net10.0)", "CoreCompile", "Csc", true));
    }

    [Test]
    public async Task BuildTargetWhy_GivesTheChainThatAskedForATarget_AndWhyItRan()
    {
        var compile = await Server.CallAsync<List<BuildTargetWhyTool.TargetRun>>("build_target_why", new() { ["binlogPath"] = BuildLog, ["target"] = "CoreCompile", ["project"] = "Shapes.csproj" });
        var info = await Server.CallAsync<List<BuildTargetWhyTool.TargetRun>>("build_target_why", new() { ["binlogPath"] = BuildLog, ["target"] = "WriteBuildInfo" });

        var run = compile.Should().ContainSingle().Subject;
        (run.Project, run.Skipped).Should().Be(("Shapes.csproj (net10.0)", null));
        run.Chain.Should().Equal(
            "CoreCompile (in DependsOnTargets of Compile)",
            "Compile (in DependsOnTargets of CoreBuild)",
            "CoreBuild (in DependsOnTargets of Build)",
            "Build (DefaultTargets)");
        info.Should().ContainSingle().Which.Should().Match<BuildTargetWhyTool.TargetRun>(target => target.Project == "Shapes.App.csproj (net10.0)" && target.Skipped == null);
        info[0].Chain.Should().Equal("WriteBuildInfo (AfterTargets of a target the build started with)");
        info[0].Messages.Should().Equal(
            "Building target \"WriteBuildInfo\" completely.",
            $"Output file \"{Path.Combine("obj", "Debug", "net10.0", "buildinfo.txt")}\" does not exist.");
    }

    [Test]
    public async Task BuildPropertyTrace_FromEvaluationThroughTheTargets()
    {
        var trace = await Server.CallAsync<BuildPropertyTraceTool.PropertyHistory>("build_property_trace", new() { ["binlogPath"] = BuildLog, ["name"] = "ShapesFlavor" });

        trace.Should().BeEquivalentTo(new BuildPropertyTraceTool.PropertyHistory(
            [new BuildPropertyTraceTool.EvaluatedValue("Shapes.App.csproj (net10.0)", "plain")],
            [new BuildPropertyTraceTool.ValueChange("Shapes.App.csproj (net10.0)", "WriteBuildInfo", null, "Set", "plain;built",
                $"{Sample.AppProject}({Sample.LineOf(Sample.AppProject, "<PropertyGroup><ShapesFlavor>")})")]));
    }

    [Test]
    public async Task BuildLogSearch_FindsNodesWithWhereTheyAre()
    {
        var targets = await Server.CallAsync<List<BuildLogSearchTool.LogMatch>>("build_log_search", new() { ["binlogPath"] = BuildLog, ["query"] = "$target WriteBuildInfo" });
        var messages = await Server.CallAsync<List<BuildLogSearchTool.LogMatch>>("build_log_search", new() { ["binlogPath"] = BuildLog, ["query"] = "\"Flavor plain;built\"" });

        targets.Should().Equal(new BuildLogSearchTool.LogMatch("Target", "Target Name=WriteBuildInfo Project=Shapes.App.csproj", "Shapes.App.csproj (net10.0)", "WriteBuildInfo", null));
        // The log names a Message task after its text.
        messages.Should().Equal(new BuildLogSearchTool.LogMatch("Message", "Flavor plain;built", "Shapes.App.csproj (net10.0)", "WriteBuildInfo", "Message: Flavor plain;built"));
    }

    // Compiler arguments only compare where both builds compiled, so a changed source makes the debug build, which is up to date, compile again.
    [Test]
    public async Task BuildDiff_BetweenDebugAndRelease()
    {
        var (debug, release) = (Sample.In("debug.binlog"), Sample.In("release.binlog"));
        File.SetLastWriteTimeUtc(Sample.CircleFile, DateTime.UtcNow);
        foreach (var (configuration, binlog) in new[] { ("Debug", debug), ("Release", release) })
        {
            var build = await Server.CallAsync<BuildTool.BuildResult>("build", new() { ["projectPath"] = Sample.ShapesProject, ["configuration"] = configuration, ["binlogPath"] = binlog });
            build.Succeeded.Should().BeTrue();
        }

        var diff = await Server.CallAsync<BuildDiffTool.BuildDiff>("build_diff", new() { ["binlogPath"] = debug, ["otherBinlogPath"] = release });

        (diff.OnlyInFirst, diff.OnlyInSecond).Should().BeEquivalentTo((Array.Empty<string>(), Array.Empty<string>()));
        var project = diff.Projects.Should().ContainSingle().Subject;
        project.Project.Should().Be("Shapes.csproj (net10.0)");
        project.GlobalProperties.Should().Equal(new BuildDiffTool.ValueDiff("Configuration", "Debug", "Release"));
        project.Properties.Should().Contain([new BuildDiffTool.ValueDiff("Configuration", "Debug", "Release"), new BuildDiffTool.ValueDiff("Optimize", "false", "true")]);
        project.CompilerArguments.Should().Contain([new BuildDiffTool.ValueDiff("/optimize-", "/optimize-", null), new BuildDiffTool.ValueDiff("/optimize+", null, "/optimize+")]);
        diff.Work.Should().ContainSingle().Which.Project.Should().Be("Shapes.csproj (net10.0)");
    }
}
