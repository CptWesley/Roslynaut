using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>
/// The tools that change code, each on a project of its own: they write the files, and the server's file watchers bring the changes back in.
/// </summary>
public class EditingTests : EndToEndTest
{
    private static string FileOf(string project, string name) => Path.Combine(Path.GetDirectoryName(project)!, name);

    /// <summary>A file as is: the sample's files have \n line ends, which edits have to keep.</summary>
    private static string Read(string file) => File.ReadAllText(file);

    /// <summary>Waits until a condition on what the server sees holds, as its file watchers bring changes in a moment after they happen.</summary>
    private static async Task EventuallyAsync(Func<Task<bool>> condition, string because)
    {
        for (var attempt = 0; !await condition(); attempt++)
        {
            attempt.Should().BeLessThan(60, because);
            await Task.Delay(500);
        }
    }

    private const string Counter = """
        namespace Edits;

        public class Counter
        {
            public int Count { get; private set; }

            public void Add(int amount) => Count += amount;

            public void Add() => Add(1);
        }
        """;

    private const string Uses = """
        namespace Edits;

        public static class Uses
        {
            public static int Twice()
            {
                var counter = new Counter();
                counter.Add(2);
                counter.Add();
                return counter.Count;
            }
        }
        """;

    [Test]
    public async Task RenameSymbol_OfAType_RenamesItsFileAndUses_AndTheServerSeesTheChange()
    {
        var project = Sample.NewProject("RenameType", ("Counter.cs", Counter), ("Uses.cs", Uses));

        var result = await Server.CallAsync<RenameSymbolTool.RenameResult>("rename_symbol", new() { ["projectPath"] = project, ["symbol"] = "Edits.Counter", ["newName"] = "Tally" });

        result.ChangedFiles.Should().BeEquivalentTo(FileOf(project, "Tally.cs"), FileOf(project, "Uses.cs"));
        File.Exists(FileOf(project, "Counter.cs")).Should().BeFalse();
        Read(FileOf(project, "Tally.cs")).Should().Be(Counter.Replace("class Counter", "class Tally").ReplaceLineEndings("\n"));
        Read(FileOf(project, "Uses.cs")).Should().Be(Uses.Replace("new Counter()", "new Tally()").ReplaceLineEndings("\n"));
        await EventuallyAsync(async () =>
        {
            var symbols = await Server.CallAsync<PagedResult<SymbolEntry>>("workspace_symbols", new() { ["projectPath"] = project, ["query"] = "Tally" });
            return symbols.Items.Any(symbol => symbol.Location?.FilePath == FileOf(project, "Tally.cs"));
        }, "the server should see the renamed type in its new file");
    }

    [Test]
    public async Task RenameSymbol_OfAnOverload_WithItsOverloads()
    {
        var project = Sample.NewProject("RenameOverloads", ("Counter.cs", Counter), ("Uses.cs", Uses));

        var result = await Server.CallAsync<RenameSymbolTool.RenameResult>("rename_symbol", new()
        {
            ["projectPath"] = project, ["symbol"] = "Counter.Add(int)", ["newName"] = "Increase", ["overloads"] = true,
        });

        result.ChangedFiles.Should().BeEquivalentTo(FileOf(project, "Counter.cs"), FileOf(project, "Uses.cs"));
        Read(FileOf(project, "Counter.cs")).Should().Be(Counter.Replace("Add(", "Increase(").ReplaceLineEndings("\n"));
        Read(FileOf(project, "Uses.cs")).Should().Be(Uses.Replace("Add(", "Increase(").ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task RenameSymbol_ToAnInvalidName_ChangesNothing()
    {
        var project = Sample.NewProject("RenameInvalid", ("Counter.cs", Counter));

        var error = await Server.CallFailingAsync("rename_symbol", new() { ["projectPath"] = project, ["symbol"] = "Counter", ["newName"] = "1st" });

        error.Should().Be("An error occurred invoking 'rename_symbol': '1st' is not a valid C# identifier.");
        Read(FileOf(project, "Counter.cs")).Should().Be(Counter.ReplaceLineEndings("\n"));
    }

    // Reorders, and adds a parameter that call sites pass a value for.
    [Test]
    public async Task ChangeSignature_UpdatesTheCallSites()
    {
        var project = Sample.NewProject("ChangeSignature", ("Greeter.cs", """
            namespace Edits;

            public class Greeter
            {
                public string Greet(string name, int times) => name + times;

                public string Twice() => Greet("a", 2);
            }
            """));

        var result = await Server.CallAsync<ChangedFiles>("change_signature", new()
        {
            ["projectPath"] = project,
            ["symbol"] = "Edits.Greeter.Greet",
            ["parameters"] = new object[] { new { name = "times" }, new { name = "name" }, new { name = "loud", type = "bool", value = "false" } },
        });

        result.Files.Should().Equal(FileOf(project, "Greeter.cs"));
        Read(FileOf(project, "Greeter.cs")).Should().Be("""
            namespace Edits;

            public class Greeter
            {
                public string Greet(int times, string name, bool loud) => name + times;

                public string Twice() => Greet(2, "a", false);
            }
            """.ReplaceLineEndings("\n"));
    }

    // An action whose title ends in '...' asks an IDE for members; apply_code_action takes them as an argument.
    [Test]
    public async Task ApplyCodeAction_GenerateOverrides_ForTheMembersPicked()
    {
        var project = Sample.NewProject("GenerateOverrides", ("Shape.cs", "namespace Edits;\n\npublic class Shape\n{\n}\n"));
        var file = FileOf(project, "Shape.cs");

        var actions = await Server.CallAsync<PagedResult<CodeActionEntry>>("code_actions", new() { ["projectPath"] = project, ["filePath"] = file, ["line"] = 3, ["column"] = 14 });
        var overrides = actions.Items.Should().ContainSingle(action => action.Title == "Generate overrides...").Subject;
        var applied = await Server.CallAsync<ApplyCodeActionTool.AppliedCodeAction>("apply_code_action", new()
        {
            ["projectPath"] = project, ["id"] = overrides.Id, ["members"] = new[] { "ToString" },
        });

        applied.Should().BeEquivalentTo(new ApplyCodeActionTool.AppliedCodeAction("Generate overrides...", [file]));
        Read(file).Should().Be("""
            namespace Edits;

            public class Shape
            {
                public override string? ToString()
                {
                    return base.ToString();
                }
            }

            """.ReplaceLineEndings("\n"));
    }

    // Extract method takes a selection of statements.
    [Test]
    public async Task ApplyCodeAction_ExtractMethod_FromASelection()
    {
        var project = Sample.NewProject("ExtractMethod", ("Prices.cs", """
            namespace Edits;

            public static class Prices
            {
                public static int Total(int price, int count)
                {
                    var net = price * count;
                    var tax = net / 5;
                    return net + tax;
                }
            }
            """));
        var file = FileOf(project, "Prices.cs");
        var line = Sample.LineOf(file, "var net");

        var actions = await Server.CallAsync<PagedResult<CodeActionEntry>>("code_actions", new() { ["projectPath"] = project, ["filePath"] = file, ["line"] = line, ["endLine"] = line + 1 });
        var extract = actions.Items.Should().ContainSingle(action => action.Title == "Extract method").Subject;
        extract.Kind.Should().Be("Refactoring");
        var applied = await Server.CallAsync<ApplyCodeActionTool.AppliedCodeAction>("apply_code_action", new() { ["projectPath"] = project, ["id"] = extract.Id });

        applied.ChangedFiles.Should().Equal(file);
        Read(file).Should().Be("""
            namespace Edits;

            public static class Prices
            {
                public static int Total(int price, int count)
                {
                    int net, tax;
                    NewMethod(price, count, out net, out tax);
                    return net + tax;
                }

                private static void NewMethod(int price, int count, out int net, out int tax)
                {
                    net = price * count;
                    tax = net / 5;
                }
            }
            """.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task FixAll_FixesEveryOccurrenceInTheProject()
    {
        var project = Sample.NewProject("FixAll", ("Leftovers.cs", """
            namespace Edits;

            public static class Leftovers
            {
                public static void First()
                {
                    int a;
                }

                public static void Second()
                {
                    int b;
                }
            }
            """));

        var applied = await Server.CallAsync<ApplyCodeActionTool.AppliedCodeAction>("fix_all", new() { ["projectPath"] = project, ["diagnosticId"] = "CS0168" });

        applied.Should().BeEquivalentTo(new ApplyCodeActionTool.AppliedCodeAction("Remove unused variable", [FileOf(project, "Leftovers.cs")]));
        Read(FileOf(project, "Leftovers.cs")).Should().Be("""
            namespace Edits;

            public static class Leftovers
            {
                public static void First()
                {
                }

                public static void Second()
                {
                }
            }
            """.ReplaceLineEndings("\n"));
    }

    private const string Messy = """
        namespace Edits;
        public class Messy
        {
        public int Add(int a,int b)
        {
        return a+b;
        }
        }
        """;

    [Test]
    public async Task Format_SomeLinesOrTheWholeFile()
    {
        var project = Sample.NewProject("Format", ("Messy.cs", Messy));
        var file = FileOf(project, "Messy.cs");
        var line = Sample.LineOf(file, "return");

        var some = await Server.CallAsync<EditResult>("format", new() { ["projectPath"] = project, ["filePath"] = file, ["startLine"] = line });
        var lines = Read(file);
        var all = await Server.CallAsync<EditResult>("format", new() { ["projectPath"] = project, ["filePath"] = file });

        some.ChangedFiles.Should().Equal(file);
        lines.Should().Be(Messy.Replace("return a+b;", "        return a + b;").ReplaceLineEndings("\n"));
        all.ChangedFiles.Should().Equal(file);
        Read(file).Should().Be("""
            namespace Edits;

            public class Messy
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task OrganizeUsings_RemovesTheUnneededAndSortsTheRest()
    {
        var project = Sample.NewProject("OrganizeUsings", ("Lists.cs", """
            using System.Text;
            using System.Linq;
            using System;
            using System.Collections.Generic;

            namespace Edits;

            public static class Lists
            {
                public static List<int> Sorted(IEnumerable<int> values) => values.Order().ToList();

                public static int Parse(string text) => Int32.Parse(text);
            }
            """));

        var result = await Server.CallAsync<EditResult>("organize_usings", new() { ["projectPath"] = project, ["filePath"] = FileOf(project, "Lists.cs") });

        result.ChangedFiles.Should().Equal(FileOf(project, "Lists.cs"));
        Read(FileOf(project, "Lists.cs")).Should().Be("""
            using System;
            using System.Collections.Generic;
            using System.Linq;

            namespace Edits;

            public static class Lists
            {
                public static List<int> Sorted(IEnumerable<int> values) => values.Order().ToList();

                public static int Parse(string text) => Int32.Parse(text);
            }
            """.ReplaceLineEndings("\n"));
    }

    private const string Widget = """
        namespace Edits;

        public class Widget
        {
            /// <summary>Spins.</summary>
            public void Spin() { }

            public int Speed;

            public int Faster() => Speed + 1;
        }
        """;

    [Test]
    public async Task ReplaceSymbolBody_ReplacesTheDeclaration_KeepingItsComment()
    {
        var project = Sample.NewProject("ReplaceBody", ("Widget.cs", Widget));

        var result = await Server.CallAsync<EditResult>("replace_symbol_body", new() { ["projectPath"] = project, ["symbol"] = "Widget.Spin", ["code"] = "    public void Spin(int times) { }" });

        result.ChangedFiles.Should().Equal(FileOf(project, "Widget.cs"));
        Read(FileOf(project, "Widget.cs")).Should().Be(Widget.Replace("public void Spin() { }", "public void Spin(int times) { }").ReplaceLineEndings("\n"));
        await EventuallyAsync(async () =>
            (await Server.CallAsync<List<SymbolSourceTool.SymbolSource>>("symbol_source", new() { ["projectPath"] = project, ["symbol"] = "Widget.Spin" }))[0].Source == "    public void Spin(int times) { }",
            "the server should see the new declaration");
    }

    [Test]
    public async Task InsertBeforeSymbol_GoesAboveItsComment()
    {
        var project = Sample.NewProject("InsertBefore", ("Widget.cs", Widget));

        await Server.CallAsync<EditResult>("insert_before_symbol", new() { ["projectPath"] = project, ["symbol"] = "Widget.Spin", ["code"] = "    public void Stop() { }\n" });

        Read(FileOf(project, "Widget.cs")).Should().Be(Widget.Replace("    /// <summary>Spins.</summary>", "    public void Stop() { }\n    /// <summary>Spins.</summary>").ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task InsertAfterSymbol_GoesOnTheNextLine()
    {
        var project = Sample.NewProject("InsertAfter", ("Widget.cs", Widget));

        await Server.CallAsync<EditResult>("insert_after_symbol", new() { ["projectPath"] = project, ["symbol"] = "Widget.Speed", ["code"] = "    public int Weight;" });

        Read(FileOf(project, "Widget.cs")).Should().Be(Widget.Replace("    public int Speed;", "    public int Speed;\n    public int Weight;").ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task SafeDeleteSymbol_RefusesWhileReferenced_AndDeletesOnceNot()
    {
        var project = Sample.NewProject("SafeDelete", ("Widget.cs", Widget));
        var file = FileOf(project, "Widget.cs");
        var use = Sample.Location(file, "Speed + 1");

        var refused = await Server.CallAsync<SafeDeleteSymbolTool.SafeDeleteResult>("safe_delete_symbol", new() { ["projectPath"] = project, ["symbol"] = "Widget.Speed" });
        var deleted = await Server.CallAsync<SafeDeleteSymbolTool.SafeDeleteResult>("safe_delete_symbol", new() { ["projectPath"] = project, ["symbol"] = "Widget.Faster" });

        refused.Should().BeEquivalentTo(new SafeDeleteSymbolTool.SafeDeleteResult(false, [], [use]));
        deleted.Should().BeEquivalentTo(new SafeDeleteSymbolTool.SafeDeleteResult(true, [file], []));
        Read(file).Should().Be(Widget.Replace("\n\n    public int Faster() => Speed + 1;", "").ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task ExtractInterface_IntoAFileOfItsOwn()
    {
        var project = Sample.NewProject("ExtractInterface", ("Greeter.cs", """
            namespace Edits;

            public class Greeter
            {
                public string Hello(string name) => "Hello, " + name;

                public static Greeter Default { get; } = new();
            }
            """));

        var result = await Server.CallAsync<ChangedFiles>("extract_interface", new() { ["projectPath"] = project, ["symbol"] = "Edits.Greeter" });

        // Roslyn declares a new file's namespace in a block unless .editorconfig asks for file-scoped namespaces; it has the line ends of the project.
        result.Files.Should().BeEquivalentTo(FileOf(project, "Greeter.cs"), FileOf(project, "IGreeter.cs"));
        Read(FileOf(project, "IGreeter.cs")).Should().Be("""
            namespace Edits
            {
                public interface IGreeter
                {
                    string Hello(string name);
                }
            }
            """.ReplaceLineEndings("\n"));
        Read(FileOf(project, "Greeter.cs")).Should().Be("""
            namespace Edits;

            public class Greeter : IGreeter
            {
                public string Hello(string name) => "Hello, " + name;

                public static Greeter Default { get; } = new();
            }
            """.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task MoveToNamespace_UpdatesTheUses()
    {
        var project = Sample.NewProject("MoveToNamespace",
            ("Order.cs", "namespace Edits;\n\npublic class Order\n{\n}\n"),
            ("Shop.cs", "namespace Edits;\n\npublic static class Shop\n{\n    public static Order Create() => new();\n}\n"));

        var result = await Server.CallAsync<ChangedFiles>("move_to_namespace", new() { ["projectPath"] = project, ["symbol"] = "Edits.Order", ["targetNamespace"] = "Edits.Orders" });

        result.Files.Should().BeEquivalentTo(FileOf(project, "Order.cs"), FileOf(project, "Shop.cs"));
        Read(FileOf(project, "Order.cs")).Should().Be("namespace Edits.Orders;\n\npublic class Order\n{\n}\n");
        Read(FileOf(project, "Shop.cs")).Should().Be("using Edits.Orders;\n\nnamespace Edits;\n\npublic static class Shop\n{\n    public static Order Create() => new();\n}\n");
    }

    // The build output is the earlier version; then a method is renamed in source.
    [Test]
    public async Task ApiDiff_AgainstAnEarlierBuild()
    {
        var project = Sample.NewProject("ApiDiff", ("Calc.cs", "namespace Edits;\n\npublic static class Calc\n{\n    public static int Sub(int a, int b) => a - b;\n}\n"));
        (await Server.CallAsync<BuildTool.BuildResult>("build", new() { ["projectPath"] = project })).Succeeded.Should().BeTrue();
        var baseline = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Debug", "net10.0", "ApiDiff.dll");

        await Server.CallAsync<RenameSymbolTool.RenameResult>("rename_symbol", new() { ["projectPath"] = project, ["symbol"] = "Calc.Sub", ["newName"] = "Minus" });
        ApiDiffTool.ApiDiff diff = null!;
        await EventuallyAsync(async () =>
        {
            diff = await Server.CallAsync<ApiDiffTool.ApiDiff>("api_diff", new() { ["projectPath"] = project, ["baseline"] = baseline });
            return diff.Added.Count > 0;
        }, "the server should see the rename");

        diff.Removed.Should().Equal("public static int Edits.Calc.Sub(int a, int b)");
        diff.Added.Should().Equal("public static int Edits.Calc.Minus(int a, int b)");
    }
}
