using NuGet.Configuration;
using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>What MSBuild makes of the projects: their properties, references and graph, where a property comes from, other frameworks, and NuGet's configuration.</summary>
public class ProjectTests : EndToEndTest
{
    [Test]
    public async Task ProjectInfo_OfEachProjectOfTheSolution()
    {
        var projects = await Server.CallAsync<List<ProjectInfoTool.ProjectSummary>>("project_info", new() { ["projectPath"] = Sample.Solution });

        projects.Should().BeEquivalentTo(
        [
            Summary(Sample.ShapesProject, "Shapes", "Library", "enable", null, [], [], 10),
            Summary(Sample.AppProject, "Shapes.App", "Exe", "enable", null, [Sample.ShapesProject], new() { [SampleSolution.GreetingsId] = "1.0.0" }, 5),
            Summary(Sample.TestsProject, "Shapes.Tests", "Exe", null, "true", [Sample.ShapesProject],
                new() { ["Microsoft.NET.Test.Sdk"] = "18.11.0", ["NUnit"] = "5.0.0", ["NUnit3TestAdapter"] = "6.3.1" }, 4),
        ]);

        // The sources include what the SDK generates in obj, such as the assembly attributes.
        static ProjectInfoTool.ProjectSummary Summary(
            string project, string name, string outputType, string? nullable, string? isTestProject, string[] references, Dictionary<string, string?> packages, int sources) =>
            new(project, true, "net10.0", new()
            {
                ["AssemblyName"] = name,
                ["OutputType"] = outputType,
                ["LangVersion"] = "14.0",
                ["Nullable"] = nullable,
                ["TargetPath"] = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Debug", "net10.0", $"{name}.dll"),
                ["IsTestProject"] = isTestProject,
                ["TargetFrameworks"] = null,
                ["Configuration"] = "Debug",
            }, references, packages, sources, null);
    }

    [Test]
    public async Task ProjectGraph_InBuildOrder()
    {
        var graph = await Server.CallAsync<List<ProjectGraphTool.ProjectNode>>("project_graph", new() { ["projectPath"] = Sample.Solution });

        graph[0].Should().BeEquivalentTo(new ProjectGraphTool.ProjectNode("Shapes", Sample.ShapesProject, "net10.0", "Library", [], ["Shapes.App", "Shapes.Tests"]));
        graph.Skip(1).Should().BeEquivalentTo(
        [
            new ProjectGraphTool.ProjectNode("Shapes.App", Sample.AppProject, "net10.0", "Exe", ["Shapes"], []),
            new ProjectGraphTool.ProjectNode("Shapes.Tests", Sample.TestsProject, "net10.0", "Exe", ["Shapes"], []),
        ]);
    }

    // In the classic format, with a solution folder, in a directory of its own, away from the projects; it lists the app first.
    [Test]
    public async Task ProjectGraph_OfAClassicSolution()
    {
        var solution = Sample.Write("edits/Classic/Classic.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            VisualStudioVersion = 17.0.31903.59
            MinimumVisualStudioVersion = 10.0.40219.1
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{3C3D4F7A-0000-4000-8000-000000000001}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Shapes.App", "..\..\src\Shapes.App\Shapes.App.csproj", "{3C3D4F7A-0000-4000-8000-000000000002}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Shapes", "..\..\src\Shapes\Shapes.csproj", "{3C3D4F7A-0000-4000-8000-000000000003}"
            EndProject
            Global
            	GlobalSection(SolutionConfigurationPlatforms) = preSolution
            		Debug|Any CPU = Debug|Any CPU
            	EndGlobalSection
            	GlobalSection(ProjectConfigurationPlatforms) = postSolution
            		{3C3D4F7A-0000-4000-8000-000000000002}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{3C3D4F7A-0000-4000-8000-000000000002}.Debug|Any CPU.Build.0 = Debug|Any CPU
            		{3C3D4F7A-0000-4000-8000-000000000003}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{3C3D4F7A-0000-4000-8000-000000000003}.Debug|Any CPU.Build.0 = Debug|Any CPU
            	EndGlobalSection
            	GlobalSection(NestedProjects) = preSolution
            		{3C3D4F7A-0000-4000-8000-000000000002} = {3C3D4F7A-0000-4000-8000-000000000001}
            		{3C3D4F7A-0000-4000-8000-000000000003} = {3C3D4F7A-0000-4000-8000-000000000001}
            	EndGlobalSection
            EndGlobal
            """);

        var graph = await Server.CallAsync<List<ProjectGraphTool.ProjectNode>>("project_graph", new() { ["projectPath"] = solution });

        graph.Should().BeEquivalentTo(
        [
            new ProjectGraphTool.ProjectNode("Shapes", Sample.ShapesProject, "net10.0", "Library", [], ["Shapes.App"]),
            new ProjectGraphTool.ProjectNode("Shapes.App", Sample.AppProject, "net10.0", "Exe", ["Shapes"], []),
        ], options => options.WithStrictOrdering());
    }

    // Loading the app loads the library it references too.
    [Test]
    public async Task MsBuildEvaluate_PropertiesAndItems_ByNameOrWildcard()
    {
        var evaluations = await Server.CallAsync<List<MsBuildEvaluateTool.Evaluation>>("msbuild_evaluate", new()
        {
            ["projectPath"] = Sample.AppProject, ["names"] = new[] { "Shapes*", "TargetPath", "PackageReference", "ProjectReference" },
        });

        evaluations.Select(evaluation => evaluation.Project).Should().Equal(Sample.AppProject, Sample.ShapesProject);
        var app = evaluations[0];
        app.Properties.Should().BeEquivalentTo(new Dictionary<string, string> { ["ShapesFlavor"] = "plain", ["TargetPath"] = Sample.AppAssembly });
        app.Items["PackageReference"].Should().BeEquivalentTo([new MsBuildEvaluateTool.Item(SampleSolution.GreetingsId, new Dictionary<string, string> { ["Version"] = "1.0.0" })]);
        app.Items["ProjectReference"].Should().ContainSingle().Which.Include.Should().Be(Path.Combine("..", "Shapes", "Shapes.csproj"));
        evaluations[1].Properties.Should().BeEquivalentTo(new Dictionary<string, string> { ["TargetPath"] = Sample.ShapesAssembly });
    }

    [Test]
    public async Task MsBuildPropertyTrace_EveryPlaceThatSetsAProperty()
    {
        var trace = await Server.CallAsync<MsBuildPropertyTraceTool.PropertyTrace>("msbuild_property_trace", new() { ["projectPath"] = Sample.AppProject, ["name"] = "ShapesFlavor" });

        trace.Should().BeEquivalentTo(new MsBuildPropertyTraceTool.PropertyTrace("ShapesFlavor", "plain",
        [
            new MsBuildPropertyTraceTool.PropertyDefinition(Sample.AppProject, null, "plain", null),
            new MsBuildPropertyTraceTool.PropertyDefinition(Sample.AppProject, null, "$(ShapesFlavor);built", "WriteBuildInfo"),
        ]), options => options.WithStrictOrdering());
    }

    // Targets has code for each framework under #if; the server shows the one it loads, until told otherwise.
    [Test]
    public async Task WorkspaceOptions_LoadAnotherTargetFramework()
    {
        async Task<string> NameAsync() =>
            (await Server.CallAsync<List<SymbolSourceTool.SymbolSource>>("symbol_source", new() { ["projectPath"] = Sample.TargetsProject, ["symbol"] = "Platform.Name" })).Single().Source;

        var before = await NameAsync();
        var standard = await Server.CallAsync<List<WorkspaceOptionsTool.LoadedProject>>("workspace_options", new() { ["projectPath"] = Sample.TargetsProject, ["targetFramework"] = "netstandard2.0" });
        var during = await NameAsync();
        var info = await Server.CallAsync<List<ProjectInfoTool.ProjectSummary>>("project_info", new() { ["projectPath"] = Sample.TargetsProject });
        var reset = await Server.CallAsync<List<WorkspaceOptionsTool.LoadedProject>>("workspace_options", new() { ["projectPath"] = Sample.TargetsProject });

        before.Should().Be("    public static string Name => \"core\";");
        standard.Should().BeEquivalentTo([new WorkspaceOptionsTool.LoadedProject(Sample.TargetsProject, "netstandard2.0", ["net10.0", "netstandard2.0"], "Debug")]);
        during.Should().Be("    public static string Name => \"standard\";");
        info.Should().ContainSingle().Which.Should().Match<ProjectInfoTool.ProjectSummary>(project =>
            project.TargetFramework == "netstandard2.0" && project.Properties["LangVersion"] == "7.3" && project.PackageReferences["NETStandard.Library"] == "2.0.3");
        reset.Should().BeEquivalentTo([new WorkspaceOptionsTool.LoadedProject(Sample.TargetsProject, "net10.0", ["net10.0", "netstandard2.0"], "Debug")]);
        (await NameAsync()).Should().Be(before);
    }

    // Code under #if DEBUG shows by default; in Release, the other branch does.
    [Test]
    public async Task WorkspaceOptions_LoadAnotherConfiguration()
    {
        var project = Sample.NewProject("Configured", ("Mode.cs", "namespace Edits;\n\npublic static class Mode\n{\n#if DEBUG\n    public static string Name => \"debug\";\n#else\n    public static string Name => \"release\";\n#endif\n}\n"));
        async Task<string> NameAsync() =>
            (await Server.CallAsync<List<SymbolSourceTool.SymbolSource>>("symbol_source", new() { ["projectPath"] = project, ["symbol"] = "Mode.Name" })).Single().Source;

        var before = await NameAsync();
        var release = await Server.CallAsync<List<WorkspaceOptionsTool.LoadedProject>>("workspace_options", new() { ["projectPath"] = project, ["configuration"] = "Release" });
        var during = await NameAsync();
        var info = (await Server.CallAsync<List<ProjectInfoTool.ProjectSummary>>("project_info", new() { ["projectPath"] = project })).Single();
        var reset = await Server.CallAsync<List<WorkspaceOptionsTool.LoadedProject>>("workspace_options", new() { ["projectPath"] = project });

        before.Should().Be("    public static string Name => \"debug\";");
        release.Should().BeEquivalentTo([new WorkspaceOptionsTool.LoadedProject(project, "net10.0", ["net10.0"], "Release")]);
        during.Should().Be("    public static string Name => \"release\";");
        (info.Properties["Configuration"], info.Properties["TargetPath"])
            .Should().Be(("Release", Path.Combine(Path.GetDirectoryName(project)!, "bin", "Release", "net10.0", "Configured.dll")));
        reset.Should().BeEquivalentTo([new WorkspaceOptionsTool.LoadedProject(project, "net10.0", ["net10.0"], "Debug")]);
        (await NameAsync()).Should().Be(before);
    }

    [Test]
    public async Task NuGetConfig_OfTheSample()
    {
        var config = await Server.CallAsync<NuGetConfig>("nuget_config", new() { ["projectPath"] = Sample.AppProject });

        // NuGet spells the file name its own way.
        config.ConfigFiles[0].Should().BeEquivalentTo(Sample.In("nuget.config"));
        config.Sources.Should().Equal(new NuGetSource("local", Sample.Feed, true, false), new NuGetSource("nuget.org", "https://api.nuget.org/v3/index.json", true, false));
        config.SourceMapping.Should().BeEquivalentTo(new Dictionary<string, string[]> { ["local"] = ["Roslynaut.E2E.*"], ["nuget.org"] = ["*"] });
        config.GlobalPackagesFolder.Should().Be(Sample.Packages);
        config.FallbackFolders.Select(Path.TrimEndingDirectorySeparator)
            .Should().Equal(Path.TrimEndingDirectorySeparator(SettingsUtility.GetGlobalPackagesFolder(Settings.LoadDefaultSettings(null))));
    }
}
