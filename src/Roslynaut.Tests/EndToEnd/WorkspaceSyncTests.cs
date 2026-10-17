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
