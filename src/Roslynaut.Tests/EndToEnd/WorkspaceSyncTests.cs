using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>
/// How the server keeps up with changes made outside it, as by an editor or git: its file watchers bring them into the next call. Each test has
/// a project of its own.
/// </summary>
public class WorkspaceSyncTests : EndToEndTest
{
    private const string Order = "namespace Edits;\n\npublic class Order\n{\n}\n";

    /// <summary>The types the server sees in a project, with their files.</summary>
    private static async Task<List<(string Type, string? File)>> TypesAsync(string project) =>
    [
        .. (await Server.CallAsync<PagedResult<SymbolEntry>>("find_symbols", new() { ["projectPath"] = project, ["kind"] = "Type" })).Items
            .Select(type => (type.Signature, type.Location?.FilePath)),
    ];

    private static Task SeesAsync(string project, params (string Type, string File)[] types) =>
        EventuallyAsync(async () => (await TypesAsync(project)).OrderBy(type => type.Type).SequenceEqual(types.OrderBy(type => type.Type).Select(type => (type.Type, (string?)type.File))),
            $"the server should see {string.Join(", ", types)}");

    [Test]
    public async Task NewFiles_JoinTheProject_AndDeletedOnesLeaveIt()
    {
        var project = Sample.NewProject("NewFiles", ("Order.cs", Order));
        var order = Path.Combine(Path.GetDirectoryName(project)!, "Order.cs");
        (await TypesAsync(project)).Should().Equal(("Edits.Order", order));

        var line = Sample.Write("edits/NewFiles/Lines/Line.cs", "namespace Edits.Lines;\n\npublic class Line\n{\n    public Order? Order { get; set; }\n}\n");
        await SeesAsync(project, ("Edits.Order", order), ("Edits.Lines.Line", line));
        File.Delete(order);
        await SeesAsync(project, ("Edits.Lines.Line", line));

        // The new file compiles with the project, which no longer has Order.
        (await Server.CallAsync<PagedResult<DiagnosticsTool.DiagnosticEntry>>("diagnostics", new() { ["projectPath"] = project, ["analyzers"] = false })).Items
            .Should().ContainSingle().Which.Id.Should().Be("CS0246");
    }

    // A project can leave files out, as with Compile Remove, and a new file beside them stays out too, as MSBuild has it.
    [Test]
    public async Task NewFiles_BesideOnesTheProjectLeavesOut_StayOut()
    {
        var project = Sample.Write("edits/LeftOut/LeftOut.csproj", File.ReadAllText(Sample.ProblemsProject).Replace("</Project>", "  <ItemGroup>\n    <Compile Remove=\"Templates/**\" />\n  </ItemGroup>\n</Project>"));
        var order = Sample.Write("edits/LeftOut/Order.cs", Order);
        Sample.Write("edits/LeftOut/Templates/Template.cs", "namespace Edits;\n\npublic class Template : Missing\n{\n}\n");
        (await TypesAsync(project)).Should().Equal(("Edits.Order", order));

        Sample.Write("edits/LeftOut/Templates/Other.cs", "namespace Edits;\n\npublic class Other : Missing\n{\n}\n");
        var line = Sample.Write("edits/LeftOut/Line.cs", "namespace Edits;\n\npublic class Line\n{\n}\n");

        await SeesAsync(project, ("Edits.Order", order), ("Edits.Line", line));
        (await Server.CallAsync<PagedResult<DiagnosticsTool.DiagnosticEntry>>("diagnostics", new() { ["projectPath"] = project, ["analyzers"] = false })).Items.Should().BeEmpty();
    }

    // A source generator of the solution runs once a build writes it, and runs as rebuilt after the next.
    [Test]
    public async Task AGeneratorOfTheSolution_RunsOnceBuilt_AndAsRebuilt()
    {
        Sample.Write("edits/Generator/Gen/Gen.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>netstandard2.0</TargetFramework>
                <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.14.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """);
        var generator = Sample.Write("edits/Generator/Gen/HelloGenerator.cs", """
            using Microsoft.CodeAnalysis;

            namespace Gen
            {
                [Generator]
                public sealed class HelloGenerator : IIncrementalGenerator
                {
                    public void Initialize(IncrementalGeneratorInitializationContext context) =>
                        context.RegisterPostInitializationOutput(output => output.AddSource("Hello.g.cs", "namespace App { public static class Hello { public static string Say() => \"hi\"; } }"));
                }
            }
            """);
        Sample.Write("edits/Generator/App/App.csproj", File.ReadAllText(Sample.ProblemsProject).Replace("</Project>",
            "  <ItemGroup>\n    <ProjectReference Include=\"../Gen/Gen.csproj\" OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\" />\n  </ItemGroup>\n</Project>"));
        Sample.Write("edits/Generator/App/Use.cs", "namespace App;\n\npublic static class Use\n{\n    public static string Run() => Hello.Say();\n}\n");
        var solution = Sample.Write("edits/Generator/Generator.slnx", "<Solution>\n  <Project Path=\"Gen/Gen.csproj\" />\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        async Task<string?> GeneratedAsync() =>
            (await Server.CallAsync<PagedResult<GeneratedSourcesTool.GeneratedSource>>("generated_sources", new() { ["projectPath"] = solution, ["name"] = "Hello" })).Items.SingleOrDefault()?.Source;
        async Task<List<string>> ErrorsAsync() =>
            [.. (await Server.CallAsync<PagedResult<DiagnosticsTool.DiagnosticEntry>>("diagnostics", new() { ["projectPath"] = solution, ["analyzers"] = false })).Items.Select(error => error.Id)];
        (await GeneratedAsync()).Should().BeNull();
        (await ErrorsAsync()).Should().Equal("CS0103");

        (await Server.CallAsync<BuildTool.BuildResult>("build", new() { ["projectPath"] = solution })).Succeeded.Should().BeTrue();
        await EventuallyAsync(async () => await GeneratedAsync() is { } source && source.Contains("Say()"), "the server should run the generator it built");
        (await ErrorsAsync()).Should().BeEmpty();

        File.WriteAllText(generator, File.ReadAllText(generator).Replace("Say()", "Speak()"));
        (await Server.CallAsync<BuildTool.BuildResult>("build", new() { ["projectPath"] = Sample.In("edits", "Generator", "Gen", "Gen.csproj") })).Succeeded.Should().BeTrue();
        await EventuallyAsync(async () => await GeneratedAsync() is { } source && source.Contains("Speak()"), "the server should run the generator as rebuilt");
        (await ErrorsAsync()).Should().Equal("CS0117");
    }

    [Test]
    public async Task MovedFolders_TakeTheirFilesAlong()
    {
        var project = Sample.NewProject("MovedFolder", ("Models/Order.cs", Order), ("Models/Deep/Line.cs", "namespace Edits;\n\npublic class Line\n{\n}\n"));
        var directory = Path.GetDirectoryName(project)!;
        (await TypesAsync(project)).Should().HaveCount(2);

        Directory.Move(Path.Combine(directory, "Models"), Path.Combine(directory, "Domain"));

        await SeesAsync(project, ("Edits.Order", Path.Combine(directory, "Domain", "Order.cs")), ("Edits.Line", Path.Combine(directory, "Domain", "Deep", "Line.cs")));
    }

    // A change to the project file reloads the project, here with a constant that switches the code under #if.
    [Test]
    public async Task ProjectFileChanges_ReloadTheProject()
    {
        var project = Sample.NewProject("Reloaded", ("Flag.cs", """
            namespace Edits;

            public static class Flag
            {
            #if SHOUT
                public static string Say() => "HI";
            #else
                public static string Say() => "hi";
            #endif
            }
            """));
        async Task<string> SayAsync() =>
            (await Server.CallAsync<List<SymbolSourceTool.SymbolSource>>("symbol_source", new() { ["projectPath"] = project, ["symbol"] = "Flag.Say" })).Single().Source;
        (await SayAsync()).Should().Be("    public static string Say() => \"hi\";");

        File.WriteAllText(project, File.ReadAllText(project).Replace("</Nullable>", "</Nullable>\n    <DefineConstants>$(DefineConstants);SHOUT</DefineConstants>"));

        await EventuallyAsync(async () => await SayAsync() == "    public static string Say() => \"HI\";", "the server should load the project again");
    }

    // A project added to the solution file loads with it, though it is in no folder the server watched.
    [Test]
    public async Task ProjectsAddedToTheSolution_Load()
    {
        var first = Sample.NewProject("SolutionFirst", ("Order.cs", Order));
        var solution = Sample.Write("edits/Growing/Growing.slnx", "<Solution>\n  <Project Path=\"../SolutionFirst/SolutionFirst.csproj\" />\n</Solution>\n");
        (await TypesAsync(solution)).Should().Equal(("Edits.Order", Path.Combine(Path.GetDirectoryName(first)!, "Order.cs")));

        var second = Sample.NewProject("SolutionSecond", ("Line.cs", "namespace Edits;\n\npublic class Line\n{\n}\n"));
        File.WriteAllText(solution, File.ReadAllText(solution).Replace("</Solution>", "  <Project Path=\"../SolutionSecond/SolutionSecond.csproj\" />\n</Solution>"));

        await SeesAsync(solution, ("Edits.Order", Path.Combine(Path.GetDirectoryName(first)!, "Order.cs")), ("Edits.Line", Path.Combine(Path.GetDirectoryName(second)!, "Line.cs")));
    }

    // Directory.Build.props usually sits above the project, outside the folders the server watches for code.
    [Test]
    public async Task DirectoryBuildPropsAboveTheProject_ReloadsTheProjectWhenItChanges()
    {
        var props = Sample.Write("edits/Above/Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <DefineConstants>$(DefineConstants);SHOUT</DefineConstants>\n  </PropertyGroup>\n</Project>\n");
        var project = Sample.Write("edits/Above/Inner/Inner.csproj", File.ReadAllText(Sample.ProblemsProject));
        Sample.Write("edits/Above/Inner/Flag.cs", "namespace Edits;\n\npublic static class Flag\n{\n#if SHOUT\n    public static string Say() => \"HI\";\n#else\n    public static string Say() => \"hi\";\n#endif\n}\n");
        async Task<string> SayAsync() =>
            (await Server.CallAsync<List<SymbolSourceTool.SymbolSource>>("symbol_source", new() { ["projectPath"] = project, ["symbol"] = "Flag.Say" })).Single().Source;
        (await SayAsync()).Should().Be("    public static string Say() => \"HI\";");

        File.WriteAllText(props, File.ReadAllText(props).Replace(";SHOUT", ""));

        await EventuallyAsync(async () => await SayAsync() == "    public static string Say() => \"hi\";", "the server should load the project again");
    }

    // A file that projects share sits outside their folders, and a Compile item with a relative path brings it in.
    [Test]
    public async Task LinkedFilesOutsideTheProject_ShowTheirChanges()
    {
        var project = Sample.Write("edits/Linked/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="..\Shared\Flag.cs" />
              </ItemGroup>
            </Project>
            """);
        var flag = Sample.Write("edits/Linked/Shared/Flag.cs", "namespace Edits;\n\npublic static class Flag\n{\n    public static string Say() => \"hi\";\n}\n");
        async Task<string> SayAsync() =>
            (await Server.CallAsync<List<SymbolSourceTool.SymbolSource>>("symbol_source", new() { ["projectPath"] = project, ["symbol"] = "Flag.Say" })).Single().Source;
        (await SayAsync()).Should().Be("    public static string Say() => \"hi\";");

        File.WriteAllText(flag, File.ReadAllText(flag).Replace("\"hi\"", "\"HI\""));

        await EventuallyAsync(async () => await SayAsync() == "    public static string Say() => \"HI\";", "the server should see the change to the linked file");
    }

    // An editor that saves through a temporary file renames it over the original.
    [Test]
    public async Task FilesReplacedByARename_ShowTheirNewText()
    {
        var project = Sample.NewProject("Replaced", ("Order.cs", Order));
        var order = Path.Combine(Path.GetDirectoryName(project)!, "Order.cs");
        (await TypesAsync(project)).Should().Equal(("Edits.Order", order));

        var temporary = order + ".tmp";
        File.WriteAllText(temporary, Order.Replace("Order", "Purchase"));
        File.Move(temporary, order, overwrite: true);

        await SeesAsync(project, ("Edits.Purchase", order));
    }
}
