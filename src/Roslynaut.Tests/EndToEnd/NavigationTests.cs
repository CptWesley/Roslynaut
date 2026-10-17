using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>Finding symbols and moving between them: symbol searches, definitions, references, sources, implementations and call hierarchies.</summary>
public class NavigationTests : EndToEndTest
{
    private static SymbolEntry Entry(string name, string kind, string signature, string? container, string file, string text, int occurrence = 1) =>
        new(name, kind, signature, container, Sample.Location(file, text, occurrence));

    [Test]
    public async Task WorkspaceSymbols_MatchSubstringsAcrossProjects()
    {
        var symbols = await Server.CallAsync<PagedResult<SymbolEntry>>("workspace_symbols", new() { ["projectPath"] = Sample.Solution, ["query"] = "Area" });

        symbols.Items.Should().BeEquivalentTo(
        [
            Entry("Area", "Method", "Shapes.IShape.Area()", "Shapes.IShape", Sample.IShapeFile, "Area();"),
            Entry("Area", "Method", "Shapes.Shape.Area()", "Shapes.Shape", Sample.ShapeFile, "Area();"),
            Entry("Area", "Method", "Shapes.Circle.Area()", "Shapes.Circle", Sample.CircleFile, "Area()"),
            Entry("Area", "Method", "Shapes.Rectangle.Area()", "Shapes.Rectangle", Sample.RectangleFile, "Area()"),
            Entry("TotalArea", "Method", "Shapes.ShapeMath.TotalArea(System.Collections.Generic.IEnumerable<Shapes.IShape>)", "Shapes.ShapeMath", Sample.ShapeMathFile, "TotalArea("),
            Entry("Circle_Area_IsPiTimesRadiusSquared", "Method", "Shapes.Tests.ShapeTests.Circle_Area_IsPiTimesRadiusSquared()", "Shapes.Tests.ShapeTests", Sample.TestsFile, "Circle_Area_IsPiTimesRadiusSquared"),
            Entry("Square_Area_IsSideSquared", "Method", "Shapes.Tests.ShapeTests.Square_Area_IsSideSquared()", "Shapes.Tests.ShapeTests", Sample.TestsFile, "Square_Area_IsSideSquared"),
            Entry("TotalArea_AddsTheAreas", "Method", "Shapes.Tests.ShapeTests.TotalArea_AddsTheAreas()", "Shapes.Tests.ShapeTests", Sample.TestsFile, "TotalArea_AddsTheAreas"),
        ]);
        (symbols.Page, symbols.PageSize, symbols.HasMore, symbols.TotalCount).Should().Be((0, 100, false, 8));
    }

    [Test]
    public async Task WorkspaceSymbols_MatchCamelCaseHumps()
    {
        var symbols = await Server.CallAsync<PagedResult<SymbolEntry>>("workspace_symbols", new() { ["projectPath"] = Sample.Solution, ["query"] = "TAr" });

        symbols.Items.Select(symbol => symbol.Name).Should().BeEquivalentTo("TotalArea", "TotalArea_AddsTheAreas");
    }

    [Test]
    public async Task DocumentSymbols_OutlineAFile_ToADepth()
    {
        var all = await Server.CallAsync<PagedResult<SymbolEntry>>("document_symbols", new() { ["projectPath"] = Sample.Solution, ["filePath"] = Sample.RectangleFile });
        var types = await Server.CallAsync<PagedResult<SymbolEntry>>("document_symbols", new() { ["projectPath"] = Sample.Solution, ["filePath"] = Sample.RectangleFile, ["depth"] = 0 });

        var rectangle = Entry("Rectangle", "Class", "Shapes.Rectangle", "Shapes", Sample.RectangleFile, "Rectangle(double");
        all.Items.Should().Equal(
            rectangle,
            Entry("Width", "Property", "Shapes.Rectangle.Width", "Shapes.Rectangle", Sample.RectangleFile, "Width {"),
            Entry("Height", "Property", "Shapes.Rectangle.Height", "Shapes.Rectangle", Sample.RectangleFile, "Height {"),
            Entry("Area", "Method", "Shapes.Rectangle.Area()", "Shapes.Rectangle", Sample.RectangleFile, "Area()"),
            Entry("Scale", "Method", "Shapes.Rectangle.Scale(double)", "Shapes.Rectangle", Sample.RectangleFile, "Scale(double factor)"),
            Entry("Scale", "Method", "Shapes.Rectangle.Scale(double, double)", "Shapes.Rectangle", Sample.RectangleFile, "Scale(double x"));
        types.Items.Should().Equal(rectangle);
    }

    // A file of top-level statements declares their entry point, a member of a synthesized Program.
    [Test]
    public async Task DocumentSymbols_OfTopLevelStatements_OutlineTheirEntryPoint()
    {
        var outline = await Server.CallAsync<PagedResult<SymbolEntry>>("document_symbols", new() { ["projectPath"] = Sample.Solution, ["filePath"] = Sample.ProgramFile });

        outline.Items.Should().Equal(new SymbolEntry("<Main>$", "Method", "<top-level-statements-entry-point>", "Program", Sample.Location(Sample.ProgramFile, "using Roslynaut")));
    }

    // The namespace is declared in all three projects; each of its types and namespaces comes once, from the project that declares it.
    [Test]
    public async Task ListSymbols_OfANamespaceOfSeveralProjects_ListsEachMemberOnce()
    {
        var members = await Server.CallAsync<PagedResult<SymbolEntry>>("list_symbols", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Shapes" });

        members.Items.Should().BeEquivalentTo(
        [
            Entry("IShape", "Interface", "Shapes.IShape", "Shapes", Sample.IShapeFile, "IShape"),
            Entry("Shape", "Class", "Shapes.Shape", "Shapes", Sample.ShapeFile, "Shape :"),
            Entry("Circle", "Class", "Shapes.Circle", "Shapes", Sample.CircleFile, "Circle("),
            Entry("Rectangle", "Class", "Shapes.Rectangle", "Shapes", Sample.RectangleFile, "Rectangle("),
            Entry("Square", "Class", "Shapes.Square", "Shapes", Sample.SquareFile, "Square("),
            Entry("ShapeMath", "Class", "Shapes.ShapeMath", "Shapes", Sample.ShapeMathFile, "ShapeMath"),
            Entry("ShapeFactory", "Class", "Shapes.ShapeFactory", "Shapes", Sample.In("src", "Shapes", "ShapeFactory.cs"), "ShapeFactory"),
            Entry("Internal", "Namespace", "Shapes.Internal", "Shapes", Sample.RegistryFile, "Internal;"),
            Entry("App", "Namespace", "Shapes.App", "Shapes", Sample.SpinnerFile, "App;"),
            Entry("Tests", "Namespace", "Shapes.Tests", "Shapes", Sample.TestsFile, "Tests;"),
        ]);
    }

    [Test]
    public async Task ListSymbols_OfAMethod_ListsItsParametersAndLocals()
    {
        var variables = await Server.CallAsync<PagedResult<SymbolEntry>>("list_symbols", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Shapes.ShapeMath.TotalArea" });

        const string method = "Shapes.ShapeMath.TotalArea(System.Collections.Generic.IEnumerable<Shapes.IShape>)";
        variables.Items.Should().Equal(
            Entry("shapes", "Parameter", "System.Collections.Generic.IEnumerable<Shapes.IShape> shapes", method, Sample.ShapeMathFile, "shapes)"),
            Entry("total", "Local", "double total", method, Sample.ShapeMathFile, "total ="),
            Entry("shape", "Local", "Shapes.IShape? shape", method, Sample.ShapeMathFile, "shape in shapes"),
            Entry("area", "Local", "double area", method, Sample.ShapeMathFile, "area ="));
    }

    // What a property or field holds: the members of its type, from the framework too.
    [Test]
    public async Task ListSymbols_OfAPropertyAndAField_ListTheMembersOfTheirTypes()
    {
        var radius = await Server.CallAsync<PagedResult<SymbolEntry>>("list_symbols", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Circle.Radius" });
        var factories = await Server.CallAsync<PagedResult<SymbolEntry>>("list_symbols", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Registry.Factories" });

        radius.Items.Should().ContainEquivalentOf(new SymbolEntry("MaxValue", "Field", "double.MaxValue", "double", null)).And.OnlyContain(member => member.Container == "double");
        factories.Items.Should().Contain(member => member.Name == "TryGetValue" && member.Kind == "Method")
            .And.OnlyContain(member => member.Container == "System.Collections.Generic.Dictionary<string, System.Func<double, Shapes.Shape>>");
    }

    [Test]
    public async Task FindSymbols_FiltersByStructure()
    {
        async Task<IEnumerable<string>> FindAsync(Dictionary<string, object?> arguments)
        {
            arguments["projectPath"] = Sample.Solution;
            return (await Server.CallAsync<PagedResult<SymbolEntry>>("find_symbols", arguments)).Items.Select(symbol => $"{symbol.Container}.{symbol.Name}");
        }

        (await FindAsync(new() { ["query"] = "^(Area|Name|ToString)$", ["modifiers"] = new[] { "override" } }))
            .Should().BeEquivalentTo("Shapes.Circle.Area", "Shapes.Rectangle.Area", "Shapes.Shape.ToString", "Shapes.Square.Name");
        (await FindAsync(new() { ["baseType"] = "^Rectangle$" })).Should().Equal("Shapes.Square");
        (await FindAsync(new() { ["attribute"] = "GeneratedRegex" })).Should().Equal("Shapes.Internal.Registry.Description");
        (await FindAsync(new() { ["scope"] = "Shapes", ["kind"] = "Type", ["accessibility"] = "Internal" })).Should().BeEquivalentTo("Shapes.Internal.Registry", "Shapes.App.Spinner");
        (await FindAsync(new() { ["scope"] = "ShapeMath.TotalArea", ["kind"] = "Variable", ["type"] = "^double$" }))
            .Should().Equal("Shapes.ShapeMath.TotalArea(System.Collections.Generic.IEnumerable<Shapes.IShape>).total", "Shapes.ShapeMath.TotalArea(System.Collections.Generic.IEnumerable<Shapes.IShape>).area");
    }

    [Test]
    public async Task Definition_OfASourceSymbol_ByItsQualifiedPath()
    {
        var definitions = await Server.CallAsync<List<DefinitionTool.Definition>>("definition", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Shapes.Circle" });

        var definition = definitions.Should().ContainSingle().Subject;
        (definition.Kind, definition.Signature, definition.Assembly, definition.AssemblyPath).Should().Be(("Class", "Shapes.Circle", "Shapes", null));
        definition.Documentation!.ReplaceLineEndings("\n").Should().Be("<member name=\"T:Shapes.Circle\">\n    <summary>A circle around the origin.</summary>\n</member>\n");
        definition.Locations.Should().Equal(Sample.Location(Sample.CircleFile, "Circle("));
        definition.Sources.Should().BeEmpty();
    }

    [Test]
    public async Task Definition_OfTheType_OfAMethod()
    {
        var definitions = await Server.CallAsync<List<DefinitionTool.Definition>>("definition", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "ShapeMath.Largest", ["type"] = true });

        definitions.Should().ContainSingle().Which.Should().Match<DefinitionTool.Definition>(definition =>
            definition.Signature == "Shapes.IShape" && definition.Kind == "Interface" && definition.Documentation!.Contains("<summary>A two-dimensional shape.</summary>"));
        definitions[0].Locations.Should().Equal(Sample.Location(Sample.IShapeFile, "IShape"));
    }

    // Greetings embeds its PDB and sources, so the definition leads to a local copy of the source the package was built from.
    [Test]
    public async Task Definition_OfAPackageSymbol_LeadsToItsEmbeddedSource()
    {
        var (line, column) = Sample.Find(Sample.ProgramFile, "Hello(");
        var definitions = await Server.CallAsync<List<DefinitionTool.Definition>>("definition", new()
        {
            ["projectPath"] = Sample.Solution, ["filePath"] = Sample.ProgramFile, ["line"] = line, ["column"] = column,
        });

        var definition = definitions.Should().ContainSingle().Subject;
        (definition.Kind, definition.Signature, definition.Assembly, definition.AssemblyPath)
            .Should().Be(("Method", "Roslynaut.E2E.Greetings.Greeter.Hello(string)", SampleSolution.GreetingsId, Sample.GreetingsAssembly));
        definition.Documentation.Should().Contain("<summary>Says hello to someone.</summary>").And.Contain("<param name=\"name\">Who to greet.</param>");
        var source = definition.Sources.Should().ContainSingle().Subject;
        source.Should().Match<SourceDocument>(document => document.Name == Sample.In("packages-src", "Greetings", "Greeter.cs")
            && document.Origin == "Embedded" && document.Url == null && document.Verified == true && document.Error == null);
        File.ReadAllText(source.FilePath!).Should().Be(SampleSolution.GreeterSource(SampleSolution.GreetingsV1));
        definition.Locations.Should().Equal(Sample.Location(source.FilePath!, "public static string Hello"));
    }

    [Test]
    public async Task References_OfAnInterfaceMember_IncludeCallsThroughImplementations()
    {
        var references = await Server.CallAsync<PagedResult<ReferencesTool.ReferenceEntry>>("references", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "IShape.Area" });

        const string largest = "Shapes.ShapeMath.Largest(System.Collections.Generic.IReadOnlyList<Shapes.IShape>)";
        references.Items.Should().BeEquivalentTo(
        [
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.ShapeMathFile, "Area();"), "Shapes.ShapeMath.TotalArea(System.Collections.Generic.IEnumerable<Shapes.IShape>)", "Call"),
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.ShapeMathFile, "Area() switch"), "Shapes.ShapeMath.Classify(Shapes.IShape)", "Call"),
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.ShapeMathFile, "Area() >"), largest, "Call"),
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.ShapeMathFile, "Area())"), largest, "Call"),
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.ShapeFile, "Area():F2"), "Shapes.Shape.ToString()", "Call"),
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.SpinnerFile, "Area()"), "Shapes.App.Spinner.Spin(Shapes.IShape[])", "Call"),
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.TestsFile, "Area(), Is.EqualTo(Math"), "Shapes.Tests.ShapeTests.Circle_Area_IsPiTimesRadiusSquared()", "Call"),
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.TestsFile, "Area(), Is.EqualTo(9"), "Shapes.Tests.ShapeTests.Square_Area_IsSideSquared()", "Call"),
        ]);
    }

    [Test]
    public async Task References_TellReadsFromWrites()
    {
        var references = await Server.CallAsync<PagedResult<ReferencesTool.ReferenceEntry>>("references", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "ShapeMath.TotalArea.total" });

        const string method = "Shapes.ShapeMath.TotalArea(System.Collections.Generic.IEnumerable<Shapes.IShape>)";
        references.Items.Should().BeEquivalentTo(
        [
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.ShapeMathFile, "total += area"), method, "Write"),
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.ShapeMathFile, "total;"), method, "Read"),
        ]);
    }

    [Test]
    public async Task SymbolSource_OfATypeAndOfAnOverload()
    {
        var square = await Server.CallAsync<List<SymbolSourceTool.SymbolSource>>("symbol_source", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Shapes.Square" });
        var scale = await Server.CallAsync<List<SymbolSourceTool.SymbolSource>>("symbol_source", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Rectangle.Scale(double)" });

        square.Should().Equal(new SymbolSourceTool.SymbolSource("Shapes.Square", Sample.Location(Sample.SquareFile, "public sealed class Square"),
            "public sealed class Square(double side) : Rectangle(side, side)\n{\n    public override string Name => \"Square\";\n}"));
        scale.Should().Equal(new SymbolSourceTool.SymbolSource("Shapes.Rectangle.Scale(double)", Sample.Location(Sample.RectangleFile, "public Rectangle Scale(double factor)"),
            "    public Rectangle Scale(double factor) => Scale(factor, factor);"));
    }

    // Parameter types by their names as code writes them, or fully qualified as results give them.
    [Test]
    public async Task SymbolPaths_PickOverloadsByTheirParameterTypes()
    {
        async Task<IEnumerable<string>> SignaturesAsync(string symbol) =>
            (await Server.CallAsync<List<SymbolSourceTool.SymbolSource>>("symbol_source", new() { ["projectPath"] = Sample.Solution, ["symbol"] = symbol })).Select(source => source.Signature);

        const string totalArea = "Shapes.ShapeMath.TotalArea(System.Collections.Generic.IEnumerable<Shapes.IShape>)";
        (await SignaturesAsync("ShapeMath.Classify(IShape)")).Should().Equal("Shapes.ShapeMath.Classify(Shapes.IShape)");
        (await SignaturesAsync("ShapeMath.TotalArea(IEnumerable<IShape>)")).Should().Equal(totalArea);
        (await SignaturesAsync(totalArea)).Should().Equal(totalArea);
        (await SignaturesAsync("rectangle.scale(Double, Double)")).Should().Equal("Shapes.Rectangle.Scale(double, double)");
        (await Server.CallAsync<PagedResult<ReferencesTool.ReferenceEntry>>("references", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "ShapeMath.TotalArea(IEnumerable<IShape>).total" }))
            .Items.Select(reference => reference.Access).Should().Equal("Write", "Read");
        (await Server.CallFailingAsync("symbol_source", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "ShapeMath.Classify(Circle)" }))
            .Should().Be("An error occurred invoking 'symbol_source': No symbol found for 'ShapeMath.Classify(Circle)'.");
    }

    [Test]
    public async Task SymbolSource_OfAPackageSymbol_ComesFromItsPdb()
    {
        var (line, column) = Sample.Find(Sample.ProgramFile, "Bye(");
        var sources = await Server.CallAsync<List<SymbolSourceTool.SymbolSource>>("symbol_source", new()
        {
            ["projectPath"] = Sample.Solution, ["filePath"] = Sample.ProgramFile, ["line"] = line, ["column"] = column,
        });

        var source = sources.Should().ContainSingle().Subject;
        source.Signature.Should().Be("Roslynaut.E2E.Greetings.Greeter.Bye(string)");
        source.Source.Should().Be("    public static string Bye(string name) => \"Bye, \" + name + Punctuation.Exclamation;");
        source.Document.Should().Match<SourceDocument>(document => document.Origin == "Embedded" && document.Verified == true);
        source.Location.Should().Be(Sample.Location(source.Document!.FilePath!, "public static string Bye"));
    }

    [Test]
    public async Task Implementations_OfAnInterface_AndOfAnAbstractMember()
    {
        async Task<IEnumerable<SymbolEntry>> ImplementationsAsync(string symbol) =>
            (await Server.CallAsync<PagedResult<SymbolEntry>>("implementations", new() { ["projectPath"] = Sample.Solution, ["symbol"] = symbol })).Items;

        (await ImplementationsAsync("IShape")).Select(type => type.Signature).Should().BeEquivalentTo("Shapes.Shape", "Shapes.Circle", "Shapes.Rectangle", "Shapes.Square");
        (await ImplementationsAsync("IShape.Area")).Should().Equal(Entry("Area", "Method", "Shapes.Shape.Area()", "Shapes.Shape", Sample.ShapeFile, "Area();"));
        (await ImplementationsAsync("Shape.Area")).Should().BeEquivalentTo(
        [
            Entry("Area", "Method", "Shapes.Circle.Area()", "Shapes.Circle", Sample.CircleFile, "Area()"),
            Entry("Area", "Method", "Shapes.Rectangle.Area()", "Shapes.Rectangle", Sample.RectangleFile, "Area()"),
        ]);
    }

    [Test]
    public async Task TypeHierarchy_UpAndDown()
    {
        var supertypes = await Server.CallAsync<PagedResult<SymbolEntry>>("type_hierarchy", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Square", ["direction"] = "Supertypes" });
        var subtypes = await Server.CallAsync<PagedResult<SymbolEntry>>("type_hierarchy", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "IShape", ["direction"] = "Subtypes" });

        supertypes.Items.Should().Equal(
            Entry("Rectangle", "Class", "Shapes.Rectangle", "Shapes", Sample.RectangleFile, "Rectangle("),
            Entry("Shape", "Class", "Shapes.Shape", "Shapes", Sample.ShapeFile, "Shape :"),
            new SymbolEntry("Object", "Class", "object", "System", null),
            Entry("IShape", "Interface", "Shapes.IShape", "Shapes", Sample.IShapeFile, "IShape"));
        subtypes.Items.Select(type => type.Signature).Should().BeEquivalentTo("Shapes.Shape", "Shapes.Circle", "Shapes.Rectangle", "Shapes.Square");
    }

    [Test]
    public async Task Callers_ComeWithTheirCalls_FromEveryProject()
    {
        var callers = await Server.CallAsync<PagedResult<CallersTool.Caller>>("callers", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Shapes.ShapeMath.TotalArea" });

        callers.Items.Should().BeEquivalentTo(
        [
            new CallersTool.Caller(
                Entry("TotalArea_AddsTheAreas", "Method", "Shapes.Tests.ShapeTests.TotalArea_AddsTheAreas()", "Shapes.Tests.ShapeTests", Sample.TestsFile, "TotalArea_AddsTheAreas"),
                [Sample.Location(Sample.TestsFile, "TotalArea([")]),

            // Top-level statements are in the synthesized Main of a synthesized Program, which starts with the file.
            new CallersTool.Caller(
                new SymbolEntry("<Main>$", "Method", "<top-level-statements-entry-point>", "Program", Sample.Location(Sample.ProgramFile, "using Roslynaut")),
                [Sample.Location(Sample.ProgramFile, "TotalArea(shapes)")]),
        ]);
    }

    [Test]
    public async Task Callees_IncludeConstructorsOfOtherAssemblies()
    {
        var callees = await Server.CallAsync<PagedResult<CallersTool.Caller>>("callees", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "ShapeMath.Largest" });

        callees.Items.Should().BeEquivalentTo(
        [
            new CallersTool.Caller(
                Entry("Area", "Method", "Shapes.IShape.Area()", "Shapes.IShape", Sample.IShapeFile, "Area();"),
                [Sample.Location(Sample.ShapeMathFile, "shape.Area() >"), Sample.Location(Sample.ShapeMathFile, "largest.Area()")]),
            new CallersTool.Caller(
                new SymbolEntry(".ctor", "Method", "System.ArgumentException.ArgumentException(string?, string?)", "System.ArgumentException", null),
                [Sample.Location(Sample.ShapeMathFile, "new ArgumentException")]),
        ]);
    }

    // Reading a property calls its getter; Math.PI is a field and * a built-in operator, so neither counts.
    [Test]
    public async Task Callees_ByPosition_IncludePropertyReads()
    {
        var (line, column) = Sample.Find(Sample.CircleFile, "Area()");
        var callees = await Server.CallAsync<PagedResult<CallersTool.Caller>>("callees", new()
        {
            ["projectPath"] = Sample.Solution, ["filePath"] = Sample.CircleFile, ["line"] = line, ["column"] = column,
        });

        callees.Items.Should().BeEquivalentTo(
        [
            new CallersTool.Caller(
                Entry("Radius", "Property", "Shapes.Circle.Radius", "Shapes.Circle", Sample.CircleFile, "Radius {"),
                [Sample.Location(Sample.CircleFile, "Radius *"), Sample.Location(Sample.CircleFile, "Radius;")]),
        ]);
    }

    // Results name top-level statements <Main>$ in Program, so that path finds them again, and their locals below them.
    [Test]
    public async Task SymbolPaths_ReachTopLevelStatements_AndTheirLocals()
    {
        var callers = await Server.CallAsync<PagedResult<CallersTool.Caller>>("callers", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "ShapeMath.TotalArea" });
        var entryPoint = callers.Items.Select(caller => caller.Symbol).Single(caller => caller.Container == "Program");
        var path = $"{entryPoint.Container}.{entryPoint.Name}";

        var members = await Server.CallAsync<PagedResult<SymbolEntry>>("list_symbols", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Program" });
        var locals = await Server.CallAsync<PagedResult<SymbolEntry>>("list_symbols", new() { ["projectPath"] = Sample.Solution, ["symbol"] = path });
        var references = await Server.CallAsync<PagedResult<ReferencesTool.ReferenceEntry>>("references", new() { ["projectPath"] = Sample.Solution, ["symbol"] = $"{path}.shapes" });

        members.Items.Should().Equal(entryPoint);
        locals.Items.Should().Equal(
            Entry("shapes", "Local", "Shapes.IShape[] shapes", "<top-level-statements-entry-point>", Sample.ProgramFile, "shapes ="),
            Entry("total", "Local", "double total", "<top-level-statements-entry-point>", Sample.ProgramFile, "total ="));

        // A lambda has no name to show, so a reference in one is in the member the lambda is written in.
        references.Items.Should().Equal(
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.ProgramFile, "shapes);"), "<top-level-statements-entry-point>", "Read"),
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.ProgramFile, "shapes))"), "<top-level-statements-entry-point>", "Read"));
    }

    // A position on a declaration or on a use targets what the symbol's path does: the interface in a base list, a base type, a call.
    [Test]
    public async Task Positions_OfDeclarationsAndUses_TargetWhatTheirPathsDo()
    {
        var cases = new (string Tool, string File, string Text, string Symbol)[]
        {
            ("implementations", Sample.ShapeFile, "IShape", "Shapes.IShape"),
            ("type_hierarchy", Sample.CircleFile, "Shape\n", "Shapes.Shape"),
            ("callers", Sample.ProgramFile, "TotalArea(", "ShapeMath.TotalArea"),
            ("callees", Sample.ShapeMathFile, "Largest(", "ShapeMath.Largest"),
            ("find_tests", Sample.ShapeMathFile, "Classify(", "ShapeMath.Classify"),
            ("references", Sample.CircleFile, "Radius *", "Circle.Radius"),
        };

        foreach (var (tool, file, text, symbol) in cases)
        {
            var (line, column) = Sample.Find(file, text);
            Dictionary<string, object?> Target(params (string Name, object? Value)[] target)
            {
                var arguments = new Dictionary<string, object?> { ["projectPath"] = Sample.Solution };
                if (tool == "type_hierarchy")
                    arguments["direction"] = "Subtypes";
                foreach (var (name, value) in target)
                    arguments[name] = value;
                return arguments;
            }

            var byPath = Items(await Server.CallAsync(tool, Target(("symbol", symbol))));
            var byPosition = Items(await Server.CallAsync(tool, Target(("filePath", file), ("line", line), ("column", column))));

            // In the order Roslyn finds them, which can start from another project.
            byPath.Should().NotBeEmpty(tool);
            byPosition.Should().BeEquivalentTo(byPath, $"{tool} at {text}");
        }

        static List<string> Items(System.Text.Json.JsonElement result) =>
            [.. (result.ValueKind == System.Text.Json.JsonValueKind.Array ? result : result.GetProperty("items")).EnumerateArray().Select(item => item.GetRawText())];
    }

    // Results name a constructor .ctor, so a result's Container plus '.' plus its Name finds it again; code names it after its type.
    [Test]
    public async Task SymbolPaths_ReachConstructors()
    {
        async Task<IEnumerable<string>> CallersAsync(string symbol) =>
            (await Server.CallAsync<PagedResult<CallersTool.Caller>>("callers", new() { ["projectPath"] = Sample.Solution, ["symbol"] = symbol })).Items.Select(caller => caller.Symbol.Signature);
        var callees = await Server.CallAsync<PagedResult<CallersTool.Caller>>("callees", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Registry.Factories" });
        var callee = callees.Items.Select(callee => callee.Symbol).Single(callee => callee.Container == "Shapes.Circle");

        (callee.Name, callee.Signature).Should().Be((".ctor", "Shapes.Circle.Circle(double)"));
        var callers = await CallersAsync($"{callee.Container}.{callee.Name}");
        callers.Should().BeEquivalentTo("Shapes.Internal.Registry.Factories", "<top-level-statements-entry-point>", "Shapes.Tests.ShapeTests.Circle_Area_IsPiTimesRadiusSquared()");
        (await CallersAsync("Circle.Circle(double)")).Should().BeEquivalentTo(callers);
        (await CallersAsync("Shapes.Circle..ctor(Double)")).Should().BeEquivalentTo(callers);
    }

    // Square's primary constructor calls Rectangle's in its base list: Roslyn's callers leave that call out, and its references put it in the namespace.
    [Test]
    public async Task Callers_AndReferences_OfABaseConstructor_IncludeTheBaseListOfADerivedPrimaryConstructor()
    {
        var callers = await Server.CallAsync<PagedResult<CallersTool.Caller>>("callers", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Shapes.Rectangle..ctor" });
        var references = await Server.CallAsync<PagedResult<ReferencesTool.ReferenceEntry>>("references", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Rectangle.Rectangle" });
        var (line, column) = Sample.Find(Sample.SquareFile, "Rectangle(side");
        var highlights = await Server.CallAsync<List<ReferencesTool.ReferenceEntry>>("document_highlight", new()
        {
            ["projectPath"] = Sample.Solution, ["filePath"] = Sample.SquareFile, ["line"] = line, ["column"] = column,
        });

        var square = Entry(".ctor", "Method", "Shapes.Square.Square(double)", "Shapes.Square", Sample.SquareFile, "Square(");
        var call = new ReferencesTool.ReferenceEntry(Sample.Location(Sample.SquareFile, "Rectangle(side"), "Shapes.Square.Square(double)", "Call");
        callers.Items.Should().ContainEquivalentOf(new CallersTool.Caller(square, [call.Location]));
        callers.Items.Select(caller => caller.Symbol.Signature).Should().BeEquivalentTo(
            "Shapes.Square.Square(double)", "Shapes.Rectangle.Scale(double, double)", "<top-level-statements-entry-point>", "Shapes.Tests.ShapeTests.TotalArea_AddsTheAreas()");
        references.Items.Should().ContainEquivalentOf(call).And.HaveCount(4);
        highlights.Should().Equal(call);
    }

    // At new Circle(1) the symbol is the constructor, which stands for the type it constructs.
    [Test]
    public async Task AConstructorCall_LeadsToTheHierarchyAndDefinitionOfItsType()
    {
        var (line, column) = Sample.Find(Sample.ProgramFile, "Circle(1)");
        Dictionary<string, object?> At(string name, object value) => new()
        {
            ["projectPath"] = Sample.Solution, ["filePath"] = Sample.ProgramFile, ["line"] = line, ["column"] = column, [name] = value,
        };

        var supertypes = await Server.CallAsync<PagedResult<SymbolEntry>>("type_hierarchy", At("direction", "Supertypes"));
        var type = await Server.CallAsync<List<DefinitionTool.Definition>>("definition", At("type", true));

        supertypes.Items.Select(supertype => supertype.Signature).Should().Equal("Shapes.Shape", "object", "Shapes.IShape");
        type.Should().ContainSingle().Which.Locations.Should().Equal(Sample.Location(Sample.CircleFile, "Circle("));
    }

    [Test]
    public async Task Implementations_OfAVirtualMember_AreItsOverrides()
    {
        var implementations = await Server.CallAsync<PagedResult<SymbolEntry>>("implementations", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Shape.Name" });

        implementations.Items.Should().Equal(Entry("Name", "Property", "Shapes.Square.Name", "Shapes.Square", Sample.SquareFile, "Name =>"));
    }

    [Test]
    public async Task References_ByPosition_OfAProperty()
    {
        var (line, column) = Sample.Find(Sample.CircleFile, "Radius *");
        var references = await Server.CallAsync<PagedResult<ReferencesTool.ReferenceEntry>>("references", new()
        {
            ["projectPath"] = Sample.Solution, ["filePath"] = Sample.CircleFile, ["line"] = line, ["column"] = column,
        });

        references.Items.Should().Equal(
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.CircleFile, "Radius *"), "Shapes.Circle.Area()", "Read"),
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.CircleFile, "Radius;"), "Shapes.Circle.Area()", "Read"));
    }

    [Test]
    public async Task DocumentHighlight_ListsTheDeclarationAndEachUse_InOrder()
    {
        var (line, column) = Sample.Find(Sample.ShapeMathFile, "total = 0.0");
        var highlights = await Server.CallAsync<List<ReferencesTool.ReferenceEntry>>("document_highlight", new()
        {
            ["projectPath"] = Sample.Solution, ["filePath"] = Sample.ShapeMathFile, ["line"] = line, ["column"] = column,
        });

        const string method = "Shapes.ShapeMath.TotalArea(System.Collections.Generic.IEnumerable<Shapes.IShape>)";
        highlights.Should().Equal(
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.ShapeMathFile, "total = 0.0"), method, "Declaration"),
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.ShapeMathFile, "total += area"), method, "Write"),
            new ReferencesTool.ReferenceEntry(Sample.Location(Sample.ShapeMathFile, "total;"), method, "Read"));
    }
}
