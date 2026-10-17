using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>
/// build's timings and the build_* tools, on the binary log of a solution built to have their problems: Lib and Lib2 share an output directory and
/// copy two versions of Dep to it, App builds Lib a second time with another global property, and two targets in App run when they should not.
/// </summary>
[NonParallelizable]
public class BuildLogTests
{
    private DirectoryInfo directory = null!;
    private string app = null!;
    private string binlog = null!;
    private BuildTool.BuildResult build = null!;

    [OneTimeSetUp]
    public async Task BuildSolution()
    {
        directory = Directory.CreateTempSubdirectory("roslynaut-binlog-");
        void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory.FullName, path))!);
            File.WriteAllText(Path.Combine(directory.FullName, path), text);
        }

        // Two projects with the same assembly name and different versions.
        foreach (var (folder, version) in new[] { ("Dep1", 1), ("Dep2", 2) })
        {
            Write($"{folder}/Dep.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyVersion>{version}.0.0.0</AssemblyVersion></PropertyGroup>
                </Project>
                """);
            Write($"{folder}/D.cs", $"namespace Dep; public static class D {{ public static int V => {version}; }}");
        }

        foreach (var (name, dep) in new[] { ("Lib", "Dep1"), ("Lib2", "Dep2") })
        {
            Write($"{name}/{name}.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputPath>../shared/</OutputPath></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../{dep}/Dep.csproj" /></ItemGroup>
                </Project>
                """);
            Write($"{name}/L.cs", $"namespace {name}; public static class L {{ public static int V => Dep.D.V; }}");
        }

        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>
                <Marker>start</Marker><DisableTransitiveProjectReferences>true</DisableTransitiveProjectReferences>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Lib/Lib.csproj" />
                <ProjectReference Include="../Lib2/Lib2.csproj" />
              </ItemGroup>
              <Target Name="SetMarker" BeforeTargets="CoreCompile">
                <PropertyGroup><Marker>$(Marker);changed</Marker></PropertyGroup>
                <ItemGroup><Extra Include="one;two" /><Extra Remove="one" /></ItemGroup>
              </Target>
              <Target Name="NeverUpToDate" AfterTargets="Build" Inputs="App.csproj" Outputs="$(IntermediateOutputPath)never.txt">
                <Message Text="ran" />
              </Target>
              <Target Name="BuildLibAgain" AfterTargets="Build">
                <MSBuild Projects="../Lib/Lib.csproj" Properties="Flavor=Other" />
              </Target>
            </Project>
            """);
        Write("App/Program.cs", "System.Console.WriteLine(Lib.L.V + Lib2.L.V);");

        app = Path.Combine(directory.FullName, "App", "App.csproj");
        binlog = Path.Combine(directory.FullName, "build.binlog");
        build = await BuildTool.BuildAsync(app, timings: true, binlogPath: binlog);
        build.Succeeded.Should().BeTrue(build.Output ?? string.Join("\n", build.Messages));
    }

    [OneTimeTearDown]
    public void DeleteSolution() => directory.Delete(recursive: true);

    private string InDir(params string[] path) => Path.Combine([directory.FullName, .. path]);

    [Test]
    public void Build_KeepsTheLog_AndTimesAnalyzersEvaluationAndCriticalPath()
    {
        File.Exists(binlog).Should().BeTrue();
        var timings = build.Timings!;

        timings.Analyzers.Should().NotBeEmpty().And.OnlyContain(analyzer => analyzer.Assembly.Length > 0);
        timings.Generators.Should().NotBeEmpty();
        timings.Evaluations.Should().Contain(evaluation => evaluation.Name == "App.csproj");
        timings.EvaluationHotspots.Should().NotBeEmpty();
        timings.CriticalPath.Should().HaveCountGreaterThanOrEqualTo(2);
        timings.CriticalPath[0].Project.Should().Be("App.csproj (net10.0)");
        timings.CriticalPath[0].OwnMilliseconds.Should().BeLessThan(timings.CriticalPath[0].Milliseconds);
        timings.Nodes.Should().BePositive();
        timings.Parallelism.Should().BePositive();
    }

    // An analyzer's time in the summary is in ms only below a second.
    [TestCase(66)]
    [TestCase(1_234)]
    [TestCase(135_123)]
    [TestCase(3_723_123)]
    public void ParseDuration_ReadsEachFormatOfTheSummary(long milliseconds) =>
        BuildTool.ParseDuration(Microsoft.Build.Logging.StructuredLogger.TextUtilities.DisplayDuration(TimeSpan.FromMilliseconds(milliseconds), showZero: true))
            .Should().Be(milliseconds);

    [Test]
    public void Problems_FindDoubleWrites_DuplicateBuilds_AndReferenceConflicts()
    {
        var problems = BuildProblemsTool.Check(binlog);

        problems.DoubleWrites.Should().ContainSingle(write => write.File == InDir("shared", "net10.0", "Dep.dll"))
            .Which.Sources.Should().Equal(InDir("Dep1", "bin", "Debug", "net10.0", "Dep.dll"), InDir("Dep2", "bin", "Debug", "net10.0", "Dep.dll"));

        problems.DuplicateBuilds.Should().ContainSingle(duplicate => duplicate.Project == "Lib.csproj (net10.0)")
            .Which.Builds.Select(properties => properties["Flavor"]).Should().BeEquivalentTo("", "Other");

        var conflict = problems.ReferenceConflicts.Should().ContainSingle(conflict => conflict.Project == "App.csproj (net10.0)").Subject;
        (conflict.Assembly, conflict.Chosen, conflict.Reason).Should().Be(("Dep", "Dep, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null", "it had a higher version"));
        conflict.Versions.Select(version => (version.Identity.Split(',')[1].Trim(), Path.GetFileName(version.DependedOnBy.Single())))
            .Should().BeEquivalentTo([("Version=2.0.0.0", "Lib2.dll"), ("Version=1.0.0.0", "Lib.dll")]);
    }

    [Test]
    public async Task Incrementality_ReportsWhatTheSecondBuildDid()
    {
        var incrementality = await BuildIncrementalityTool.CheckAsync(app);

        incrementality.Targets.Should().ContainSingle().Which.Should().Match<BuildIncrementalityTool.RebuiltTarget>(target =>
            target.Project == "App.csproj (net10.0)" && target.Target == "NeverUpToDate" && target.Reason.Contains("never.txt"));

        // The double write copies both versions of Dep again on every build.
        incrementality.Copied.Should().Contain(copy => copy.EndsWith(InDir("shared", "net10.0", "Dep.dll")));
    }

    [Test]
    public void FileOrigin_FollowsCopiesBackToTheCompiler()
    {
        var writes = BuildFileOriginTool.Trace(binlog, InDir("App", "bin", "Debug", "net10.0", "Lib2.dll"));

        writes[0].Should().Match<BuildFileOriginTool.FileWrite>(write =>
            write.Source == InDir("shared", "net10.0", "Lib2.dll") && write.Project == "App.csproj (net10.0)" && write.Task == "Copy");
        writes.Should().Contain(write => write.File == InDir("Lib2", "obj", "Debug", "net10.0", "Lib2.dll") && write.Task == "Csc" && write.Source == null);
    }

    [Test]
    public void TargetWhy_GivesTheChain_AndWhyItRan()
    {
        var marker = BuildTargetWhyTool.Explain(binlog, "SetMarker").Should().ContainSingle().Subject;
        marker.Skipped.Should().BeNull();
        marker.Chain.Take(2).Should().Equal("SetMarker (BeforeTargets CoreCompile)", "CoreCompile (in DependsOnTargets of Compile)");
        marker.Chain[^1].Should().Be("Build (DefaultTargets)");

        BuildTargetWhyTool.Explain(binlog, "NeverUpToDate").Should().ContainSingle().Which.Messages.Should().Contain(message => message.Contains("never.txt"));
        BuildTargetWhyTool.Explain(binlog, "CoreCompile", "Lib2").Should().ContainSingle().Which.Project.Should().Be("Lib2.csproj (net10.0)");
        BuildTargetWhyTool.Explain(binlog, "NoSuchTarget").Should().BeEmpty();
    }

    [Test]
    public void PropertyTrace_ShowsEvaluatedValues_AndChangesInTargets()
    {
        var marker = BuildPropertyTraceTool.Trace(binlog, "Marker", "App");
        marker.Evaluated.Should().Contain(new BuildPropertyTraceTool.EvaluatedValue("App.csproj (net10.0)", "start"));
        marker.Changes.Should().ContainSingle(change => change.Kind == "Set").Which.Should().Match<BuildPropertyTraceTool.ValueChange>(change =>
            change.Target == "SetMarker" && change.Value == "start;changed" && change.Location!.Contains("App.csproj"));

        BuildPropertyTraceTool.Trace(binlog, "Extra", "App").Changes.Select(change => (change.Kind, change.Value)).Should().Equal(("Add", "one;two"), ("Remove", "one"));
    }

    [Test]
    public void Search_FindsNodes_WithWhereTheyAre()
    {
        BuildLogSearchTool.Search(binlog, "$target SetMarker").Should().ContainSingle().Which.Should().Match<BuildLogSearchTool.LogMatch>(match =>
            match.Kind == "Target" && match.Project == "App.csproj (net10.0)" && match.Target == "SetMarker");

        BuildLogSearchTool.Search(binlog, "$task Csc project(Lib2)").Should().ContainSingle().Which.Should().Match<BuildLogSearchTool.LogMatch>(match =>
            match.Project == "Lib2.csproj (net10.0)" && match.Target == "CoreCompile" && match.Task == "Csc");

        // App's MSBuild task built Lib2, but is not in Lib2.
        BuildLogSearchTool.Search(binlog, "$target CoreCompile project(Lib2)").Should().Contain(match => match.Target == "CoreCompile")
            .And.OnlyContain(match => match.Project == "Lib2.csproj (net10.0)" && match.Task == null);
    }

    [Test]
    public async Task Diff_ComparesGlobalPropertiesAndEvaluatedProperties()
    {
        var other = Path.Combine(directory.FullName, "other.binlog");
        (await DotnetCli.RunAsync(["build", app, "-p:Marker=other", $"-bl:{other}"], TimeSpan.FromMinutes(5), CancellationToken.None, workingDirectory: Path.GetDirectoryName(app)))
            .ExitCode.Should().Be(0);

        var diff = BuildDiffTool.Diff(binlog, other);

        diff.OnlyInFirst.Should().BeEmpty();
        var project = diff.Projects.Should().ContainSingle(project => project.Project == Path.Combine("App", "App.csproj") + " (net10.0)").Subject;
        project.GlobalProperties.Should().Contain(new BuildDiffTool.ValueDiff("Marker", null, "other"));
        project.Properties.Should().Contain(new BuildDiffTool.ValueDiff("Marker", "start", "other"));
        diff.Work.Should().NotBeEmpty();
    }
}
