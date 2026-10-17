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

    // As an editor on Windows saves them: CRLF line ends and a UTF-8 byte order mark. Edits keep both, also in the code they add.
    [Test]
    public async Task Edits_KeepTheFilesLineEndsAndByteOrderMark()
    {
        var project = Sample.NewProject("WindowsFiles");
        var (counter, uses) = (FileOf(project, "Counter.cs"), FileOf(project, "Uses.cs"));
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(counter, Counter.ReplaceLineEndings("\r\n"), utf8);
        File.WriteAllText(uses, Uses.ReplaceLineEndings("\r\n"), utf8);

        await Server.CallAsync<RenameSymbolTool.RenameResult>("rename_symbol", new() { ["projectPath"] = project, ["symbol"] = "Edits.Counter.Count", ["newName"] = "Total" });
        await Server.CallAsync<EditResult>("insert_after_symbol", new() { ["projectPath"] = project, ["symbol"] = "Edits.Counter.Total", ["code"] = "\n    public int Peak { get; private set; }" });

        File.ReadAllBytes(counter).Take(3).Should().Equal(utf8.Preamble.ToArray());
        File.ReadAllBytes(uses).Take(3).Should().Equal(utf8.Preamble.ToArray());
        File.ReadAllText(counter).Should().Be(Counter.Replace(" Count ", " Total ").Replace("set; }\n", "set; }\n\n    public int Peak { get; private set; }\n").ReplaceLineEndings("\r\n"));
        File.ReadAllText(uses).Should().Be(Uses.Replace(".Count;", ".Total;").ReplaceLineEndings("\r\n"));
    }

    // Spaces and letters outside ASCII in the paths, through MSBuild, the compiler's messages and an edit.
    [Test]
    public async Task PathsWithSpacesAndAccents_BuildAndEdit()
    {
        var project = Sample.NewProject("Spaced Ünïcode", ("Café Menu.cs", "namespace Edits;\n\npublic static class Menu\n{\n    public static void Order()\n    {\n        int unused;\n    }\n}\n"));
        var file = FileOf(project, "Café Menu.cs");

        var build = await Server.CallAsync<BuildTool.BuildResult>("build", new() { ["projectPath"] = project });
        var renamed = await Server.CallAsync<RenameSymbolTool.RenameResult>("rename_symbol", new() { ["projectPath"] = project, ["symbol"] = "Edits.Menu.Order", ["newName"] = "Serve" });

        var (line, column) = Sample.Find(file, "unused");
        build.Should().BeEquivalentTo(new BuildTool.BuildResult(true, 0, 1,
            [new BuildTool.BuildMessage("Warning", "CS0168", "The variable 'unused' is declared but never used", file, line, column, project)], null));
        renamed.ChangedFiles.Should().Equal(file);
        Read(file).Should().Contain("public static void Serve()");
    }

    [Test]
    public async Task RenameSymbol_ToAnInvalidName_ChangesNothing()
    {
        var project = Sample.NewProject("RenameInvalid", ("Counter.cs", Counter));

        var error = await Server.CallFailingAsync("rename_symbol", new() { ["projectPath"] = project, ["symbol"] = "Counter", ["newName"] = "1st" });

        error.Should().Be("An error occurred invoking 'rename_symbol': '1st' is not a valid C# identifier.");
        Read(FileOf(project, "Counter.cs")).Should().Be(Counter.ReplaceLineEndings("\n"));
    }

    // A keyword as a name needs an @ in code, which the rename adds; a name given with its @ is the same name.
    [Test]
    public async Task RenameSymbol_ToKeywords_EscapesThem()
    {
        var project = Sample.NewProject("RenameKeywords", ("Greeter.cs", "namespace Edits;\n\npublic static class Greeter\n{\n    public static string Greet(string name, string kind) => kind + name;\n}\n"));

        await Server.CallAsync<RenameSymbolTool.RenameResult>("rename_symbol", new() { ["projectPath"] = project, ["symbol"] = "Greeter.Greet.name", ["newName"] = "class" });
        await Server.CallAsync<RenameSymbolTool.RenameResult>("rename_symbol", new() { ["projectPath"] = project, ["symbol"] = "Greeter.Greet.kind", ["newName"] = "@event" });

        Read(FileOf(project, "Greeter.cs")).Should().Contain("public static string Greet(string @class, string @event) => @event + @class;");
    }

    // Roslyn would rename Width over Height, which Box already has, and leave two properties of one name.
    [Test]
    public async Task RenameSymbol_ToANameThatConflicts_ChangesNothing()
    {
        const string box = "namespace Edits;\n\npublic class Box\n{\n    public int Width { get; set; }\n\n    public int Height { get; set; }\n\n    public int Area() => Width * Height;\n}\n";
        var project = Sample.NewProject("RenameConflict", ("Box.cs", box));

        var error = await Server.CallFailingAsync("rename_symbol", new() { ["projectPath"] = project, ["symbol"] = "Box.Width", ["newName"] = "Height" });

        error.Should().StartWith("An error occurred invoking 'rename_symbol': Renaming Edits.Box.Width to Height would leave code that does not compile, so nothing changed: CS0102 ")
            .And.Contain("(Box.cs:7)");
        Read(FileOf(project, "Box.cs")).Should().Be(box);
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
    public async Task ApplyCodeAction_AfterTheCodeChanged_AsksForTheActionsAgain()
    {
        var project = Sample.NewProject("StaleAction", ("Leftover.cs", "namespace Edits;\n\npublic static class Leftover\n{\n    public static void Run()\n    {\n        int a;\n    }\n}\n"));
        var file = FileOf(project, "Leftover.cs");
        var actions = await Server.CallAsync<PagedResult<CodeActionEntry>>("code_actions", new() { ["projectPath"] = project, ["filePath"] = file, ["line"] = Sample.LineOf(file, "int a;") });
        var remove = actions.Items.Should().ContainSingle(action => action.Title == "Remove unused variable").Subject;

        File.WriteAllText(file, Read(file).Replace("int a;", "int a, b;"));
        await EventuallyAsync(async () => (await Server.CallAsync<List<SymbolSourceTool.SymbolSource>>("symbol_source", new() { ["projectPath"] = project, ["symbol"] = "Leftover.Run" }))
            .Single().Source.Contains("int a, b;"), "the server should see the change");

        (await Server.CallFailingAsync("apply_code_action", new() { ["projectPath"] = project, ["id"] = remove.Id })).Should().Be(
            $"An error occurred invoking 'apply_code_action': No code action {remove.Id} for the current code: the code changed since code_actions found it, or the id is wrong. Ask code_actions again.");
        Read(file).Should().Contain("int a, b;");
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

    [Test]
    public async Task FixAll_InOneFile_LeavesTheOthers()
    {
        const string leftover = "namespace Edits;\n\npublic static class {0}\n{{\n    public static void Run()\n    {{\n        int a;\n    }}\n}}\n";
        var project = Sample.NewProject("FixAllFile", ("First.cs", string.Format(leftover, "First")), ("Second.cs", string.Format(leftover, "Second")));

        var applied = await Server.CallAsync<ApplyCodeActionTool.AppliedCodeAction>("fix_all", new()
        {
            ["projectPath"] = project, ["diagnosticId"] = "CS0168", ["filePath"] = FileOf(project, "First.cs"),
        });

        applied.Should().BeEquivalentTo(new ApplyCodeActionTool.AppliedCodeAction("Remove unused variable", [FileOf(project, "First.cs")]));
        Read(FileOf(project, "First.cs")).Should().NotContain("int a;");
        Read(FileOf(project, "Second.cs")).Should().Be(string.Format(leftover, "Second"));
        (await Server.CallFailingAsync("fix_all", new() { ["projectPath"] = project, ["diagnosticId"] = "CS0168", ["title"] = "Make it so" }))
            .Should().Be("An error occurred invoking 'fix_all': No fix can fix all CS0168 diagnostics as 'Make it so'.");
    }

    [Test]
    public async Task RenameSymbol_ByPosition_RenamesAParameter()
    {
        var project = Sample.NewProject("RenameParameter", ("Counter.cs", Counter));
        var file = FileOf(project, "Counter.cs");
        var (line, column) = Sample.Find(file, "amount)");

        var result = await Server.CallAsync<RenameSymbolTool.RenameResult>("rename_symbol", new()
        {
            ["projectPath"] = project, ["filePath"] = file, ["line"] = line, ["column"] = column, ["newName"] = "by",
        });

        result.ChangedFiles.Should().Equal(file);
        Read(file).Should().Be(Counter.Replace("amount", "by").ReplaceLineEndings("\n"));
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

    // The lines of the method; the class around it keeps its own lack of indentation.
    [Test]
    public async Task Format_ARangeOfLines()
    {
        var project = Sample.NewProject("FormatRange", ("Messy.cs", Messy));
        var file = FileOf(project, "Messy.cs");

        var result = await Server.CallAsync<EditResult>("format", new()
        {
            ["projectPath"] = project, ["filePath"] = file, ["startLine"] = Sample.LineOf(file, "public int Add"), ["endLine"] = Sample.LineOf(file, "return") + 1,
        });

        result.ChangedFiles.Should().Equal(file);
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

    private const string Lists = """
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
        """;

    [Test]
    public async Task OrganizeUsings_WithoutRemoving_OnlySorts()
    {
        var project = Sample.NewProject("SortUsings", ("Lists.cs", Lists));

        await Server.CallAsync<EditResult>("organize_usings", new() { ["projectPath"] = project, ["filePath"] = FileOf(project, "Lists.cs"), ["removeUnused"] = false });

        Read(FileOf(project, "Lists.cs")).Should().StartWith("using System;\nusing System.Collections.Generic;\nusing System.Linq;\nusing System.Text;\n\nnamespace Edits;\n");
    }

    [Test]
    public async Task OrganizeUsings_RemovesTheUnneededAndSortsTheRest()
    {
        var project = Sample.NewProject("OrganizeUsings", ("Lists.cs", Lists));

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

    // The blank line after the usings goes with them, so the file does not start with one.
    [Test]
    public async Task OrganizeUsings_RemovingEveryUsing_LeavesNoBlankLineFirst()
    {
        var project = Sample.NewProject("OrganizeNoUsings", ("Plain.cs", "using System;\nusing System.Text;\n\nnamespace Edits;\n\npublic class Plain\n{\n}\n"));

        await Server.CallAsync<EditResult>("organize_usings", new() { ["projectPath"] = project, ["filePath"] = FileOf(project, "Plain.cs") });

        Read(FileOf(project, "Plain.cs")).Should().Be("namespace Edits;\n\npublic class Plain\n{\n}\n");
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

    // A partial type has a declaration per part; a position picks one.
    [Test]
    public async Task InsertAfterSymbol_OfAPartialType_AfterThePartAtAPosition()
    {
        const string order = "namespace Edits;\n\npublic partial class Order\n{\n    public int Id { get; set; }\n}\n";
        const string lines = "namespace Edits;\n\npublic partial class Order\n{\n    public int Lines { get; set; }\n}\n";
        var project = Sample.NewProject("InsertAfterPartial", ("Order.cs", order), ("Order.Lines.cs", lines));
        var file = FileOf(project, "Order.Lines.cs");
        var (line, column) = Sample.Find(file, "Order");

        (await Server.CallFailingAsync("insert_after_symbol", new() { ["projectPath"] = project, ["symbol"] = "Edits.Order", ["code"] = "\npublic class Line\n{\n}" }))
            .Should().Be("An error occurred invoking 'insert_after_symbol': Edits.Order has 2 declarations; edit one by a position in it.");
        var result = await Server.CallAsync<EditResult>("insert_after_symbol", new()
        {
            ["projectPath"] = project, ["filePath"] = file, ["line"] = line, ["column"] = column, ["code"] = "\npublic class Line\n{\n}",
        });

        result.ChangedFiles.Should().Equal(file);
        Read(file).Should().Be(lines + "\npublic class Line\n{\n}\n");
        Read(FileOf(project, "Order.cs")).Should().Be(order);
    }

    // An agent edits in a row, each edit on what the one before wrote, without waiting for the file watchers.
    [Test]
    public async Task ConsecutiveEdits_EachSeeTheOneBefore([Range(1, 3)] int run)
    {
        var project = Sample.NewProject($"Consecutive{run}", ("Widget.cs", Widget));
        var file = FileOf(project, "Widget.cs");

        await Server.CallAsync<EditResult>("insert_after_symbol", new() { ["projectPath"] = project, ["symbol"] = "Widget.Speed", ["code"] = "    public int Weight;" });
        await Server.CallAsync<EditResult>("insert_after_symbol", new() { ["projectPath"] = project, ["symbol"] = "Widget.Weight", ["code"] = "    public int Height;" });
        await Server.CallAsync<RenameSymbolTool.RenameResult>("rename_symbol", new() { ["projectPath"] = project, ["symbol"] = "Widget.Speed", ["newName"] = "Pace" });
        await Server.CallAsync<EditResult>("replace_symbol_body", new() { ["projectPath"] = project, ["symbol"] = "Widget.Faster", ["code"] = "    public int Faster() => Pace + Weight;" });

        Read(file).Should().Be(Widget
            .Replace("    public int Speed;", "    public int Speed;\n    public int Weight;\n    public int Height;")
            .Replace("Speed", "Pace")
            .Replace("Pace + 1", "Pace + Weight")
            .ReplaceLineEndings("\n"));
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

    // Nothing calls these members, but overrides, explicit implementations and the types that implement abstract or interface members need them.
    // An override of a virtual member is not needed: the base member takes its place.
    [Test]
    public async Task SafeDeleteSymbol_KeepsWhatOverridesAndImplementationsNeed()
    {
        const string animals = """
            namespace Edits;

            public abstract class Animal
            {
                public abstract string Sound();

                public virtual string Name() => "animal";
            }

            public class Dog : Animal
            {
                public override string Sound() => "Woof";

                public override string Name() => "dog";
            }

            public interface IWalker
            {
                void Walk();
            }

            public class Cat : IWalker
            {
                public void Walk() { }
            }

            public class Robot : IWalker
            {
                void IWalker.Walk() { }
            }
            """;
        var project = Sample.NewProject("SafeDeleteOverrides", ("Animals.cs", animals));
        var file = FileOf(project, "Animals.cs");
        async Task<SafeDeleteSymbolTool.SafeDeleteResult> DeleteAsync(string symbol) =>
            await Server.CallAsync<SafeDeleteSymbolTool.SafeDeleteResult>("safe_delete_symbol", new() { ["projectPath"] = project, ["symbol"] = symbol });

        (await DeleteAsync("Animal.Sound")).Should().BeEquivalentTo(new SafeDeleteSymbolTool.SafeDeleteResult(false, [], [Sample.Location(file, "Sound() => \"Woof\"")]));
        (await DeleteAsync("Dog.Sound")).Should().BeEquivalentTo(new SafeDeleteSymbolTool.SafeDeleteResult(false, [], [Sample.Location(file, "Sound();")]));
        (await DeleteAsync("Cat.Walk")).Should().BeEquivalentTo(new SafeDeleteSymbolTool.SafeDeleteResult(false, [], [Sample.Location(file, "Walk();")]));
        (await DeleteAsync("IWalker.Walk")).Should().BeEquivalentTo(new SafeDeleteSymbolTool.SafeDeleteResult(false, [], [Sample.Location(file, "Walk() { }", 2)]));
        Read(file).Should().Be(animals.ReplaceLineEndings("\n"));

        (await DeleteAsync("Dog.Name")).Should().BeEquivalentTo(new SafeDeleteSymbolTool.SafeDeleteResult(true, [file], []));
        Read(file).Should().Be(animals.Replace("\n\n    public override string Name() => \"dog\";", "").ReplaceLineEndings("\n"));
    }

    // Dot's primary constructor calls Shape's in its base list; and deleting Dot's would delete Dot, which declares it.
    [Test]
    public async Task SafeDeleteSymbol_OfConstructors_KeepsABaseOneAndRefusesAPrimaryOne()
    {
        const string shapes = "namespace Edits;\n\npublic class Shape\n{\n    public Shape(string name) { }\n}\n\npublic class Dot() : Shape(\"dot\");\n";
        var project = Sample.NewProject("SafeDeleteConstructors", ("Shapes.cs", shapes));
        var file = FileOf(project, "Shapes.cs");

        var refused = await Server.CallAsync<SafeDeleteSymbolTool.SafeDeleteResult>("safe_delete_symbol", new() { ["projectPath"] = project, ["symbol"] = "Shape..ctor" });
        var primary = await Server.CallFailingAsync("safe_delete_symbol", new() { ["projectPath"] = project, ["symbol"] = "Dot..ctor" });

        refused.Should().BeEquivalentTo(new SafeDeleteSymbolTool.SafeDeleteResult(false, [], [Sample.Location(file, "Shape(\"dot\")")]));
        primary.Should().Be("An error occurred invoking 'safe_delete_symbol': Edits.Dot.Dot() is the primary constructor of Edits.Dot: edit the type's parameter list instead.");
        Read(file).Should().Be(shapes);
    }

    // Calls pass an argument for a parameter, and uses name a type argument for a type parameter, without referencing either: deleting one breaks them.
    [Test]
    public async Task SafeDeleteSymbol_OfParameters_RefusesEvenWhenUnused()
    {
        const string calc = "namespace Edits;\n\npublic static class Calc\n{\n    public static int Inc(int a, int unused) => a + 1;\n\n    public static T First<T, TUnused>(T a) => a;\n}\n";
        var project = Sample.NewProject("SafeDeleteParameters", ("Calc.cs", calc),
            ("Use.cs", "namespace Edits;\n\npublic static class Use\n{\n    public static int Run() => Calc.Inc(1, 2) + Calc.First<int, string>(3);\n}\n"));

        var parameter = await Server.CallFailingAsync("safe_delete_symbol", new() { ["projectPath"] = project, ["symbol"] = "Calc.Inc.unused" });
        var typeParameter = await Server.CallFailingAsync("safe_delete_symbol", new() { ["projectPath"] = project, ["symbol"] = "Calc.First.TUnused" });

        parameter.Should().Be("An error occurred invoking 'safe_delete_symbol': unused is a parameter, which the calls of Edits.Calc.Inc(int, int) pass: "
            + "remove it with change_signature, which updates them.");
        typeParameter.Should().Be("An error occurred invoking 'safe_delete_symbol': TUnused is a type parameter, which the uses of Edits.Calc.First<T, TUnused>(T) name: "
            + "edit its declaration and uses instead.");
        Read(FileOf(project, "Calc.cs")).Should().Be(calc);
    }

    // A partial method's definition and implementation are symbols of their own, each declaring only itself, and its path names the definition:
    // the tools take both parts, so that the code, which is in the implementation, is there to see and edit.
    [Test]
    public async Task SymbolTools_OfAPartialMethod_TakeBothParts()
    {
        const string job = "namespace Edits;\n\npublic partial class Job\n{\n    public void Run() => Log(\"run\");\n\n    partial void Log(string message);\n\n    partial void Skip();\n}\n";
        const string log = "namespace Edits;\n\npublic partial class Job\n{\n    partial void Log(string message)\n    {\n        var line = message.Trim();\n"
            + "        System.Console.WriteLine(line);\n    }\n\n    partial void Skip() { }\n}\n";
        var project = Sample.NewProject("PartialMethods", ("Job.cs", job), ("Job.Log.cs", log));
        var (jobFile, logFile) = (FileOf(project, "Job.cs"), FileOf(project, "Job.Log.cs"));
        Dictionary<string, object?> Log(params (string Name, object Value)[] more)
        {
            var arguments = new Dictionary<string, object?> { ["projectPath"] = project, ["symbol"] = "Job.Log" };
            foreach (var (name, value) in more)
                arguments[name] = value;
            return arguments;
        }

        var sources = await Server.CallAsync<List<SymbolSourceTool.SymbolSource>>("symbol_source", Log());
        var definitions = await Server.CallAsync<List<DefinitionTool.Definition>>("definition", Log());
        var callees = await Server.CallAsync<PagedResult<CallersTool.Caller>>("callees", Log());
        var variables = await Server.CallAsync<PagedResult<SymbolEntry>>("list_symbols", Log());
        var matches = await Server.CallAsync<PagedResult<SearchPatternTool.TextMatch>>("search_pattern", new() { ["projectPath"] = project, ["pattern"] = "Trim", ["scope"] = "Job.Log" });
        var unpicked = await Server.CallFailingAsync("replace_symbol_body", Log(("code", "    partial void Log(string message) { }")));

        sources.Select(source => source.Location).Should().Equal(Sample.Location(jobFile, "partial void Log"), Sample.Location(logFile, "partial void Log"));
        definitions.Should().ContainSingle().Which.Locations.Should().Equal(Sample.Location(jobFile, "Log(string"), Sample.Location(logFile, "Log(string"));
        callees.Items.Select(callee => callee.Symbol.Signature).Should().BeEquivalentTo("string.Trim()", "System.Console.WriteLine(string?)");
        variables.Items.Select(variable => (variable.Name, variable.Kind)).Should().Equal(("message", "Parameter"), ("line", "Local"));
        matches.Items.Select(match => (match.FilePath, match.Line)).Should().Equal((logFile, Sample.LineOf(logFile, "Trim")));
        unpicked.Should().Be("An error occurred invoking 'replace_symbol_body': Edits.Job.Log(string) has 2 declarations; edit one by a position in it.");

        var (line, column) = Sample.Find(logFile, "Log(string");
        await Server.CallAsync<EditResult>("replace_symbol_body", new()
        {
            ["projectPath"] = project, ["filePath"] = logFile, ["line"] = line, ["column"] = column, ["code"] = "    partial void Log(string message) => System.Console.WriteLine(message);",
        });
        var skipped = await Server.CallAsync<SafeDeleteSymbolTool.SafeDeleteResult>("safe_delete_symbol", new() { ["projectPath"] = project, ["symbol"] = "Job.Skip" });

        skipped.Should().BeEquivalentTo(new SafeDeleteSymbolTool.SafeDeleteResult(true, [jobFile, logFile], []));
        Read(jobFile).Should().Be(job.Replace("\n\n    partial void Skip();", ""));
        Read(logFile).Should().Be("namespace Edits;\n\npublic partial class Job\n{\n    partial void Log(string message) => System.Console.WriteLine(message);\n}\n");
    }

    // A type alone in its file takes the file along; a file with another type, or a global using, stays.
    [Test]
    public async Task SafeDeleteSymbol_OfATypeAloneInItsFile_DeletesTheFile()
    {
        var project = Sample.NewProject("SafeDeleteFiles",
            ("Lonely.cs", "namespace Edits;\n\n/// <summary>Alone.</summary>\npublic class Lonely\n{\n}\n"),
            ("Pair.cs", "namespace Edits;\n\npublic class Kept\n{\n}\n\npublic class Gone\n{\n}\n"),
            ("Global.cs", "global using System.Text;\n\nnamespace Edits;\n\npublic class Global\n{\n}\n"));
        async Task<SafeDeleteSymbolTool.SafeDeleteResult> DeleteAsync(string symbol) =>
            await Server.CallAsync<SafeDeleteSymbolTool.SafeDeleteResult>("safe_delete_symbol", new() { ["projectPath"] = project, ["symbol"] = symbol });

        (await DeleteAsync("Edits.Lonely")).Should().BeEquivalentTo(new SafeDeleteSymbolTool.SafeDeleteResult(true, [FileOf(project, "Lonely.cs")], []));
        (await DeleteAsync("Edits.Gone")).Should().BeEquivalentTo(new SafeDeleteSymbolTool.SafeDeleteResult(true, [FileOf(project, "Pair.cs")], []));
        (await DeleteAsync("Edits.Global")).Should().BeEquivalentTo(new SafeDeleteSymbolTool.SafeDeleteResult(true, [FileOf(project, "Global.cs")], []));

        File.Exists(FileOf(project, "Lonely.cs")).Should().BeFalse();
        Read(FileOf(project, "Pair.cs")).Should().Be("namespace Edits;\n\npublic class Kept\n{\n}\n");
        Read(FileOf(project, "Global.cs")).Should().Be("global using System.Text;\n\nnamespace Edits;\n");
    }

    // The regex generator writes a part of each partial type with a [GeneratedRegex] method. That part cannot be edited, so edits go to the part
    // people wrote without a position to pick it; and deleting that part takes the generator's along.
    [Test]
    public async Task SymbolEdits_OfAPartialTypeWithAGeneratedPart_EditThePartPeopleWrote()
    {
        const string pattern = "using System.Text.RegularExpressions;\n\nnamespace Edits;\n\npublic static partial class Pattern\n{\n"
            + "    public static bool Check(string text) => Digits().IsMatch(text);\n\n    [GeneratedRegex(\"[0-9]+\")]\n    private static partial Regex Digits();\n}\n";
        const string letters = "using System.Text.RegularExpressions;\n\nnamespace Edits;\n\ninternal static partial class Letters\n{\n    [GeneratedRegex(\"[a-z]+\")]\n    private static partial Regex Word();\n}\n";
        var project = Sample.NewProject("GeneratedParts", ("Pattern.cs", pattern), ("Letters.cs", letters));

        var moved = await Server.CallFailingAsync("move_to_namespace", new() { ["projectPath"] = project, ["symbol"] = "Edits.Pattern", ["targetNamespace"] = "Edits.Moved" });
        var inserted = await Server.CallAsync<EditResult>("insert_after_symbol", new() { ["projectPath"] = project, ["symbol"] = "Edits.Pattern", ["code"] = "\npublic static class After\n{\n}" });
        var deleted = await Server.CallAsync<SafeDeleteSymbolTool.SafeDeleteResult>("safe_delete_symbol", new() { ["projectPath"] = project, ["symbol"] = "Edits.Letters" });

        inserted.ChangedFiles.Should().Equal(FileOf(project, "Pattern.cs"));
        Read(FileOf(project, "Pattern.cs")).Should().Be(pattern + "\npublic static class After\n{\n}\n");
        moved.Should().Be("An error occurred invoking 'move_to_namespace': Roslyn cannot move Edits.Pattern: it is partial, with 2 declarations, and Roslyn only moves a type declared once.");
        deleted.Should().BeEquivalentTo(new SafeDeleteSymbolTool.SafeDeleteResult(true, [FileOf(project, "Letters.cs")], []));
        await EventuallyAsync(async () =>
            !(await Server.CallAsync<PagedResult<SymbolEntry>>("workspace_symbols", new() { ["projectPath"] = project, ["query"] = "Letters" })).Items.Any(),
            "the generator should no longer write a part of Letters");
    }

    // A type that only a source generator declares cannot be edited; a position in the generator's file, as its locations give, finds it.
    [Test]
    public async Task SymbolEdits_OfATypeOnlyAGeneratorDeclares_ChangeNothing()
    {
        var generated = (await Server.CallAsync<PagedResult<GeneratedSourcesTool.GeneratedSource>>("generated_sources", new() { ["projectPath"] = Sample.Solution, ["name"] = "RegexGenerator" }))
            .Items.Single();
        var lines = generated.Source!.Split('\n');
        var line = Array.FindIndex(lines, text => text.Contains(" class Description_0")) + 1;
        var target = new Dictionary<string, object?>
        {
            ["projectPath"] = Sample.Solution, ["filePath"] = generated.FilePath, ["line"] = line, ["column"] = lines[line - 1].IndexOf("Description_0") + 1,
        };

        (await Server.CallFailingAsync("replace_symbol_body", new(target) { ["code"] = "class Description_0 { }" })).Should().Be("An error occurred invoking 'replace_symbol_body': "
            + "System.Text.RegularExpressions.Generated.Description_0 is declared only in the output of a source generator, which cannot be edited.");
        (await Server.CallFailingAsync("safe_delete_symbol", target)).Should().Be("An error occurred invoking 'safe_delete_symbol': "
            + "System.Text.RegularExpressions.Generated.Description_0 is declared only in the output of a source generator, which cannot be edited.");
    }

    // Each edit by a position in the name of what it edits, as editors give one, instead of a symbol path.
    [Test]
    public async Task SymbolEdits_ByPosition()
    {
        const string gadget = "namespace Edits;\n\npublic class Gadget\n{\n    public int Speed { get; set; }\n\n    public void Spin(int times, string label) { }\n\n    public void Stop() { }\n}\n";
        const string use = "namespace Edits;\n\npublic static class Use\n{\n    public static void Run(Gadget gadget) => gadget.Spin(1, \"a\");\n}\n";
        var project = Sample.NewProject("EditsByPosition", ("Gadget.cs", gadget), ("Use.cs", use));
        var file = FileOf(project, "Gadget.cs");
        Dictionary<string, object?> At(string text, params (string Name, object Value)[] more)
        {
            var (line, column) = Sample.Find(file, text);
            var arguments = new Dictionary<string, object?> { ["projectPath"] = project, ["filePath"] = file, ["line"] = line, ["column"] = column };
            foreach (var (name, value) in more)
                arguments[name] = value;
            return arguments;
        }

        await Server.CallAsync<SafeDeleteSymbolTool.SafeDeleteResult>("safe_delete_symbol", At("Stop"));
        await Server.CallAsync<EditResult>("replace_symbol_body", At("Speed", ("code", "    public int Speed { get; private set; }")));
        await Server.CallAsync<EditResult>("insert_before_symbol", At("Speed", ("code", "    public int Weight { get; set; }\n")));
        await Server.CallAsync<ChangedFiles>("change_signature", At("Spin", ("parameters", new[] { new { name = "label" }, new { name = "times" } })));
        await Server.CallAsync<ChangedFiles>("move_to_namespace", At("Gadget", ("targetNamespace", "Edits.Moved")));

        Read(file).Should().Be("namespace Edits.Moved;\n\npublic class Gadget\n{\n    public int Weight { get; set; }\n    public int Speed { get; private set; }\n\n"
            + "    public void Spin(string label, int times) { }\n}\n");
        Read(FileOf(project, "Use.cs")).Should().Be("using Edits.Moved;\n\n" + use.Replace("Spin(1, \"a\")", "Spin(\"a\", 1)"));
    }

    // A solution in a folder of its own, beside its projects; the library's interface method is renamed with its implementation and their uses.
    [Test]
    public async Task RenameSymbol_OfAnInterfaceMember_AcrossTheProjectsOfASolution()
    {
        var calc = Sample.Write("edits/RenameLib/Calc.cs", "namespace Lib;\n\npublic interface ICalc\n{\n    int Add(int a, int b);\n}\n\npublic class Calc : ICalc\n{\n    public int Add(int a, int b) => a + b;\n}\n");
        Sample.Write("edits/RenameLib/RenameLib.csproj", File.ReadAllText(Sample.ProblemsProject));
        var use = Sample.Write("edits/RenameUse/Use.cs", "namespace Use;\n\npublic static class Sums\n{\n    public static int Run(Lib.ICalc calc) => calc.Add(1, 2) + new Lib.Calc().Add(3, 4);\n}\n");
        Sample.Write("edits/RenameUse/RenameUse.csproj", File.ReadAllText(Sample.ProblemsProject).Replace("</Project>", "  <ItemGroup>\n    <ProjectReference Include=\"..\\RenameLib\\RenameLib.csproj\" />\n  </ItemGroup>\n</Project>"));
        var solution = Sample.Write("edits/RenameSolution/Rename.slnx", "<Solution>\n  <Project Path=\"../RenameLib/RenameLib.csproj\" />\n  <Project Path=\"../RenameUse/RenameUse.csproj\" />\n</Solution>\n");

        var result = await Server.CallAsync<RenameSymbolTool.RenameResult>("rename_symbol", new() { ["projectPath"] = solution, ["symbol"] = "Lib.ICalc.Add", ["newName"] = "Plus" });

        result.ChangedFiles.Should().BeEquivalentTo(calc, use);
        Read(calc).Should().Be("namespace Lib;\n\npublic interface ICalc\n{\n    int Plus(int a, int b);\n}\n\npublic class Calc : ICalc\n{\n    public int Plus(int a, int b) => a + b;\n}\n");
        Read(use).Should().Be("namespace Use;\n\npublic static class Sums\n{\n    public static int Run(Lib.ICalc calc) => calc.Plus(1, 2) + new Lib.Calc().Plus(3, 4);\n}\n");
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
    public async Task ExtractInterface_NamedWithSomeMembers_InTheSameFile()
    {
        var project = Sample.NewProject("ExtractInterfaceOptions", ("Greeter.cs", """
            namespace Edits;

            public class Greeter
            {
                public string Hello(string name) => "Hello, " + name;

                public string Bye(string name) => "Bye, " + name;
            }
            """));

        var result = await Server.CallAsync<ChangedFiles>("extract_interface", new()
        {
            ["projectPath"] = project, ["symbol"] = "Edits.Greeter", ["name"] = "IHello", ["members"] = new[] { "Hello" }, ["sameFile"] = true,
        });

        result.Files.Should().Equal(FileOf(project, "Greeter.cs"));
        Read(FileOf(project, "Greeter.cs")).Should().Be("""
            namespace Edits;

            public interface IHello
            {
                string Hello(string name);
            }

            public class Greeter : IHello
            {
                public string Hello(string name) => "Hello, " + name;

                public string Bye(string name) => "Bye, " + name;
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

    // Roslyn's move finds new Order() and [Tag] as uses of the types' constructors, not of the types, and leaves them; they get a using too.
    [Test]
    public async Task MoveToNamespace_UpdatesUsesThatOnlyCallAConstructor()
    {
        var project = Sample.NewProject("MoveToNamespaceConstructors",
            ("Order.cs", "namespace Edits;\n\npublic class Order\n{\n}\n"),
            ("TagAttribute.cs", "namespace Edits;\n\npublic class TagAttribute : System.Attribute\n{\n}\n"),
            ("Shop.cs", "namespace Edits;\n\n[Tag]\npublic static class Shop\n{\n    public static object Create() => new Order();\n}\n"));

        var order = await Server.CallAsync<ChangedFiles>("move_to_namespace", new() { ["projectPath"] = project, ["symbol"] = "Edits.Order", ["targetNamespace"] = "Edits.Orders" });
        var tag = await Server.CallAsync<ChangedFiles>("move_to_namespace", new() { ["projectPath"] = project, ["symbol"] = "Edits.TagAttribute", ["targetNamespace"] = "Edits.Tags" });

        order.Files.Should().BeEquivalentTo(FileOf(project, "Order.cs"), FileOf(project, "Shop.cs"));
        tag.Files.Should().BeEquivalentTo(FileOf(project, "TagAttribute.cs"), FileOf(project, "Shop.cs"));
        Read(FileOf(project, "Shop.cs")).Should().Be("using Edits.Orders;\nusing Edits.Tags;\n\nnamespace Edits;\n\n[Tag]\npublic static class Shop\n{\n    public static object Create() => new Order();\n}\n");
    }

    // Out of Edits, the moved type no longer sees its types without a using: it keeps one where it needs them, if only in a documentation comment.
    [Test]
    public async Task MoveToNamespace_KeepsTheUsingsThatAreNeeded()
    {
        var code = Sample.NewProject("MoveToNamespaceCode",
            ("Order.cs", "namespace Edits;\n\npublic class Order\n{\n}\n"),
            ("Line.cs", "namespace Edits;\n\npublic class Line\n{\n    public Order? Order { get; set; }\n}\n"));
        var cref = Sample.NewProject("MoveToNamespaceCref",
            ("Order.cs", "namespace Edits;\n\n/// <summary>Made by <see cref=\"Shop\"/>.</summary>\npublic class Order\n{\n}\n"),
            ("Shop.cs", "namespace Edits;\n\npublic static class Shop\n{\n    public static Order Create() => new();\n}\n"));

        var line = await Server.CallAsync<ChangedFiles>("move_to_namespace", new() { ["projectPath"] = code, ["symbol"] = "Edits.Line", ["targetNamespace"] = "Sales" });
        var order = await Server.CallAsync<ChangedFiles>("move_to_namespace", new() { ["projectPath"] = cref, ["symbol"] = "Edits.Order", ["targetNamespace"] = "Sales" });

        line.Files.Should().Equal(FileOf(code, "Line.cs"));
        Read(FileOf(code, "Line.cs")).Should().Be("using Edits;\n\nnamespace Sales;\n\npublic class Line\n{\n    public Order? Order { get; set; }\n}\n");
        order.Files.Should().BeEquivalentTo(FileOf(cref, "Order.cs"), FileOf(cref, "Shop.cs"));
        Read(FileOf(cref, "Order.cs")).Should().Be("using Edits;\n\nnamespace Sales;\n\n/// <summary>Made by <see cref=\"Shop\"/>.</summary>\npublic class Order\n{\n}\n");
        Read(FileOf(cref, "Shop.cs")).Should().Be("using Sales;\n\nnamespace Edits;\n\npublic static class Shop\n{\n    public static Order Create() => new();\n}\n");
    }

    // Roslyn would put Line in a namespace block beside the file-scoped namespace, which does not compile.
    [Test]
    public async Task MoveToNamespace_OfATypeBesideOthersInAFileScopedNamespace_ChangesNothing()
    {
        const string types = "namespace Edits;\n\npublic class Order\n{\n}\n\npublic class Line\n{\n    public Order? Order { get; set; }\n}\n";
        var project = Sample.NewProject("MoveToNamespaceShared", ("Types.cs", types));

        (await Server.CallFailingAsync("move_to_namespace", new() { ["projectPath"] = project, ["symbol"] = "Edits.Line", ["targetNamespace"] = "Edits.Lines" })).Should().Be(
            "An error occurred invoking 'move_to_namespace': Edits.Line shares its file-scoped namespace with other declarations; move it to a file of its own first, with the code action 'Move type to Line.cs'.");
        Read(FileOf(project, "Types.cs")).Should().Be(types);
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
