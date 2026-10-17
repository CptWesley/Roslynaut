using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>What the code means and how it is built: semantic details at positions, metrics, searches, diagnostics and the public API.</summary>
public class CodeInsightTests : EndToEndTest
{
    private static Dictionary<string, object?> At(string file, string text, int offset = 0)
    {
        var (line, column) = Sample.Find(file, text);
        return new() { ["projectPath"] = Sample.Solution, ["filePath"] = file, ["line"] = line, ["column"] = column + offset };
    }

    [Test]
    public async Task ExpressionInfo_DescribesTheExpressionsAroundAPosition()
    {
        var expressions = await Server.CallAsync<List<ExpressionInfoTool.ExpressionInfo>>("expression_info", At(Sample.CircleFile, "Radius *"));

        expressions.Should().BeEquivalentTo(
        [
            new ExpressionInfoTool.ExpressionInfo("Radius", "double", null, null, null, "NotNull", "Shapes.Circle.Radius", [], null),
            new ExpressionInfoTool.ExpressionInfo("Math.PI * Radius", "double", null, null, null, "NotNull", "double.operator *(double, double)", [], null),
            new ExpressionInfoTool.ExpressionInfo("Math.PI * Radius * Radius", "double", null, null, null, "NotNull", "double.operator *(double, double)", [], null),
        ], options => options.WithStrictOrdering());
    }

    // Formatted the same on every machine, not with the decimal comma of some cultures.
    [Test]
    public async Task ExpressionInfo_GivesConstantsInvariantly()
    {
        var expressions = await Server.CallAsync<List<ExpressionInfoTool.ExpressionInfo>>("expression_info", At(Sample.ShapeMathFile, "0.0"));

        expressions.Should().ContainSingle().Which.Should().Match<ExpressionInfoTool.ExpressionInfo>(expression =>
            expression.Expression == "0.0" && expression.Type == "double" && expression.ConstantValue == "0");
    }

    [Test]
    public async Task DataFlow_OfALoop_AsForExtractingAMethod()
    {
        var flow = await Server.CallAsync<DataFlowTool.DataFlow>("data_flow", new()
        {
            ["projectPath"] = Sample.Solution,
            ["filePath"] = Sample.ShapeMathFile,
            ["startLine"] = Sample.LineOf(Sample.ShapeMathFile, "foreach (var shape in shapes)"),
            ["endLine"] = Sample.LineOf(Sample.ShapeMathFile, "return total;") - 2,
        });

        flow.Should().BeEquivalentTo(new DataFlowTool.DataFlow(
            Statements: 1,
            VariablesDeclared: ["shape", "area"],
            DataFlowsIn: ["shapes", "total"],
            DataFlowsOut: ["total"],
            ReadInside: ["shapes", "total", "shape", "area"],
            WrittenInside: ["total", "shape", "area"],
            ReadOutside: ["total"],
            WrittenOutside: ["shapes", "total"],
            Captured: [],
            AlwaysAssigned: [],
            EndPointIsReachable: true,
            ExitPoints: [],
            ReturnStatements: []));
    }

    [Test]
    public async Task SignatureHelp_PicksTheOverloadAndParameter()
    {
        var signatures = await Server.CallAsync<List<SignatureHelpTool.Signature>>("signature_help", At(Sample.RectangleFile, "factor);"));

        signatures.Should().BeEquivalentTo(
        [
            new SignatureHelpTool.Signature("Shapes.Rectangle.Scale(double)", "Scales both sides by one factor.",
                [new SignatureHelpTool.Parameter("factor", "double", "What to multiply both sides by.", false)], null, false),
            new SignatureHelpTool.Signature("Shapes.Rectangle.Scale(double, double)", "Scales each side by its own factor.",
                [new SignatureHelpTool.Parameter("x", "double", "What to multiply the width by.", false), new SignatureHelpTool.Parameter("y", "double", "What to multiply the height by.", false)],
                1, true),
        ], options => options.WithStrictOrdering());
    }

    [Test]
    public async Task Completion_AfterADot_ListsTheMembers()
    {
        var arguments = At(Sample.ProgramFile, "TotalArea(shapes)");
        var all = await Server.CallAsync<PagedResult<CompletionTool.CompletionEntry>>("completion", arguments);
        arguments["filter"] = "t";
        var filtered = await Server.CallAsync<PagedResult<CompletionTool.CompletionEntry>>("completion", arguments);

        all.Items.Select(item => (item.Text, item.Kind)).Should().BeEquivalentTo(
            [("Classify", "Method"), ("Equals", "Method"), ("Largest", "Method"), ("ReferenceEquals", "Method"), ("TotalArea", "Method")]);
        filtered.Items.Should().Equal(new CompletionTool.CompletionEntry("TotalArea", "Method", null));
    }

    [Test]
    public async Task InlayHints_ShowInferredTypesAndParameterNames()
    {
        var first = Sample.LineOf(Sample.RegistryFile, "Factories = new()");
        var hints = await Server.CallTextAsync("inlay_hints", new()
        {
            ["projectPath"] = Sample.Solution, ["filePath"] = Sample.RegistryFile, ["startLine"] = first, ["endLine"] = first + 13,
        });

        string[] lines =
        [
            "    private static readonly Dictionary<string, Func<double, Shape>> Factories = new()",
            "    {",
            "        [/*key:*/ \"circle\"] = /*double*/ size => new Circle(size),",
            "        [/*key:*/ \"square\"] = /*double*/ size => new Square(size),",
            "    };",
            "",
            "    public static Shape? Create(string description)",
            "    {",
            "        var /*Match?*/ match = Description().Match(description);",
            "        if (!match.Success)",
            "            return null;",
            "",
            "        var /*double*/ size = double.Parse(match.Groups[/*groupname:*/ \"size\"].Value, CultureInfo.InvariantCulture);",
            "        return Factories.TryGetValue(match.Groups[/*groupname:*/ \"kind\"].Value, out var /*Func<double, Shape>*/ factory) ? factory(size) : null;",
        ];
        hints.Should().Be(string.Concat(lines.Select((line, i) => $"{first + i}: {line}\n")));
    }

    [Test]
    public async Task SyntaxTree_DumpsSyntaxOrOperations()
    {
        var line = Sample.LineOf(Sample.CircleFile, "Area()");
        var arguments = new Dictionary<string, object?> { ["projectPath"] = Sample.Solution, ["filePath"] = Sample.CircleFile, ["startLine"] = line, ["endLine"] = line };
        var syntax = await Server.CallTextAsync("syntax_tree", arguments);
        arguments["operations"] = true;
        var operations = await Server.CallTextAsync("syntax_tree", arguments);

        syntax.Should().Be($"""
            MethodDeclaration [{line}] public override double Area() => Math.PI * Radius * Radius;
              PredefinedType [{line}] double
              ParameterList [{line}] ()
              ArrowExpressionClause [{line}] => Math.PI * Radius * Radius
                MultiplyExpression [{line}] Math.PI * Radius * Radius
                  MultiplyExpression [{line}] Math.PI * Radius
                    SimpleMemberAccessExpression [{line}] Math.PI
                      IdentifierName [{line}] Math
                      IdentifierName [{line}] PI
                    IdentifierName [{line}] Radius
                  IdentifierName [{line}] Radius

            """.ReplaceLineEndings("\n"));
        operations.Should().Be($"""
            MethodBodyOperation () [{line}] public override double Area() => Math.PI * Radius * Radius;
              Block () [{line}] => Math.PI * Radius * Radius
                Return (Implicit) [{line}] Math.PI * Radius * Radius
                  Binary (Type: double) [{line}] Math.PI * Radius * Radius
                    Binary (Type: double) [{line}] Math.PI * Radius
                      FieldReference (Type: double, Constant: 3.141592653589793, Symbol: System.Math.PI) [{line}] Math.PI
                      PropertyReference (Type: double, Symbol: Shapes.Circle.Radius) [{line}] Radius
                        InstanceReference (Type: Shapes.Circle, Implicit) [{line}] Radius
                    PropertyReference (Type: double, Symbol: Shapes.Circle.Radius) [{line}] Radius
                      InstanceReference (Type: Shapes.Circle, Implicit) [{line}] Radius

            """.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task CodeMetrics_OfMembers_MostComplexFirst()
    {
        var metrics = await Server.CallAsync<PagedResult<CodeMetricsTool.Metrics>>("code_metrics", new() { ["projectPath"] = Sample.Solution, ["scope"] = "ShapeMath" });

        metrics.Items.Select(member => (member.Symbol.Name, member.Complexity, member.Lines, member.Parameters, member.Nesting)).Should().Equal(
            ("Largest", 5, 11, 1, 2),
            ("TotalArea", 3, 12, 1, 2),
            ("Classify", 3, 6, 1, 0));
    }

    // Each type of the namespace once, though three projects declare the namespace.
    [Test]
    public async Task CodeMetrics_OfTypes_InANamespaceOfSeveralProjects()
    {
        var metrics = await Server.CallAsync<PagedResult<CodeMetricsTool.Metrics>>("code_metrics", new()
        {
            ["projectPath"] = Sample.Solution, ["scope"] = "Shapes", ["level"] = "Types",
        });

        // Registry only counts its own part, not the regex generator's: its Description has no code there.
        metrics.Items.Select(type => (type.Symbol.Signature, type.Complexity, type.Lines, type.Members, type.MaxComplexity, type.Coupling)).Should().BeEquivalentTo(
        [
            ("Shapes.ShapeMath", 11, 35, 3, 5, 4),
            ("Shapes.Rectangle", 5, 17, 6, 1, 1),
            ("Shapes.Tests.ShapeTests", 5, 25, 5, 1, 10),
            ("Shapes.Internal.Registry", 4, 23, 4, 3, 11),
            ("Shapes.App.Spinner", 3, 12, 1, 3, 1),
            ("Shapes.Circle", 2, 6, 3, 1, 2),
            ("Shapes.Shape", 2, 8, 3, 1, 2),
            ("Shapes.ShapeFactory", 2, 6, 1, 2, 3),
            ("Shapes.Square", 2, 4, 2, 1, 1),
            ("Shapes.IShape", 0, 9, 2, 0, 0),
        ]);
        metrics.Items.Select(type => type.Complexity).Should().BeInDescendingOrder();
    }

    // The regex generator's matching code is more complex than anything in the sample, and adds a part to Registry.
    [Test]
    public async Task CodeMetrics_LeaveOutGeneratedCode_UnlessAsked()
    {
        async Task<List<CodeMetricsTool.Metrics>> MeasureAsync(string level, bool includeGenerated) =>
        [
            .. (await Server.CallAsync<PagedResult<CodeMetricsTool.Metrics>>("code_metrics", new()
            {
                ["projectPath"] = Sample.Solution, ["level"] = level, ["includeGenerated"] = includeGenerated,
            })).Items,
        ];

        var (members, allMembers) = (await MeasureAsync("Members", false), await MeasureAsync("Members", true));
        var (types, allTypes) = (await MeasureAsync("Types", false), await MeasureAsync("Types", true));

        const string runner = "System.Text.RegularExpressions.Generated.Description_0.RunnerFactory.Runner";
        members.Should().OnlyContain(member => member.Symbol.Location!.FilePath.StartsWith(Sample.Root));
        members[0].Symbol.Signature.Should().Be("Shapes.ShapeMath.Largest(System.Collections.Generic.IReadOnlyList<Shapes.IShape>)");
        allMembers.Should().Contain(member => member.Symbol.Container == runner && member.Complexity > 5)
            .And.Contain(member => member.Symbol.Signature == "Shapes.Internal.Registry.Description()");
        types.Should().NotContain(type => type.Symbol.Signature == runner);
        allTypes.Should().Contain(type => type.Symbol.Signature == runner);
        var (own, all) = (types.Single(type => type.Symbol.Name == "Registry"), allTypes.Single(type => type.Symbol.Name == "Registry"));
        (all.Lines > own.Lines, all.Complexity, all.Coupling > own.Coupling).Should().Be((true, own.Complexity + 1, true));
    }

    [Test]
    public async Task SearchPattern_WithinAProject_WithContext()
    {
        var matches = await Server.CallAsync<PagedResult<SearchPatternTool.TextMatch>>("search_pattern", new()
        {
            ["projectPath"] = Sample.Solution, ["pattern"] = @"Area\(\) =>", ["scope"] = "Shapes", ["context"] = 1,
        });

        var (circle, rectangle) = (Sample.LineOf(Sample.CircleFile, "Area()"), Sample.LineOf(Sample.RectangleFile, "Area()"));
        matches.Items.Should().BeEquivalentTo(
        [
            new SearchPatternTool.TextMatch(Sample.CircleFile, circle, $"{circle - 1}: \n{circle}:     public override double Area() => Math.PI * Radius * Radius;\n{circle + 1}: }}\n"),
            new SearchPatternTool.TextMatch(Sample.RectangleFile, rectangle, $"{rectangle - 1}: \n{rectangle}:     public override double Area() => Width * Height;\n{rectangle + 1}: \n"),
        ]);
    }

    // Generated code counts too: the regex generator's code creates no shapes but throws none either.
    [Test]
    public async Task FindOperations_ByKindAndTarget()
    {
        var creations = await Server.CallAsync<PagedResult<FindOperationsTool.OperationEntry>>("find_operations", new()
        {
            ["projectPath"] = Sample.Solution, ["kind"] = "ObjectCreation", ["target"] = "^Shapes\\.Circle$",
        });
        var throws = await Server.CallAsync<PagedResult<FindOperationsTool.OperationEntry>>("find_operations", new() { ["projectPath"] = Sample.Solution, ["kind"] = "Throw" });

        creations.Items.Should().BeEquivalentTo(
        [
            new FindOperationsTool.OperationEntry(Sample.Location(Sample.RegistryFile, "new Circle(size)"), "lambda expression", "Shapes.Circle", false),
            new FindOperationsTool.OperationEntry(Sample.Location(Sample.ProgramFile, "new Circle(1)"), "<top-level-statements-entry-point>", "Shapes.Circle", false),
            new FindOperationsTool.OperationEntry(Sample.Location(Sample.TestsFile, "new Circle(2)"), "Shapes.Tests.ShapeTests.Circle_Area_IsPiTimesRadiusSquared()", "Shapes.Circle", false),
        ]);
        throws.Items.Should().BeEquivalentTo(
        [
            new FindOperationsTool.OperationEntry(Sample.Location(Sample.In("src", "Shapes", "ShapeFactory.cs"), "throw new FormatException"), "Shapes.ShapeFactory.Parse(string)", "System.FormatException", false),
            new FindOperationsTool.OperationEntry(Sample.Location(Sample.ShapeMathFile, "throw new ArgumentException"),
                "Shapes.ShapeMath.Largest(System.Collections.Generic.IReadOnlyList<Shapes.IShape>)", "System.ArgumentException", false),
        ]);
    }

    // Not the Program of the top-level statements, nor what the regex generator declares and leaves unused.
    [Test]
    public async Task UnusedSymbols_FindsOnlyTheUnusedMethod()
    {
        var unused = await Server.CallAsync<PagedResult<SymbolEntry>>("unused_symbols", new() { ["projectPath"] = Sample.Solution });

        unused.Items.Should().Equal(new SymbolEntry("Unused", "Method", "Shapes.Internal.Registry.Unused()", "Shapes.Internal.Registry", Sample.Location(Sample.RegistryFile, "Unused()")));
    }

    [Test]
    public async Task GeneratedSources_ListsTheRegexGeneratorsOutput()
    {
        var list = await Server.CallAsync<PagedResult<GeneratedSourcesTool.GeneratedSource>>("generated_sources", new() { ["projectPath"] = Sample.Solution });
        var named = await Server.CallAsync<PagedResult<GeneratedSourcesTool.GeneratedSource>>("generated_sources", new() { ["projectPath"] = Sample.Solution, ["name"] = "regexgen" });

        var path = Path.Combine("System.Text.RegularExpressions.Generator", "System.Text.RegularExpressions.Generator.RegexGenerator", "RegexGenerator.g.cs");
        var source = named.Items.Should().ContainSingle().Subject;
        source.Should().Match<GeneratedSourcesTool.GeneratedSource>(generated => generated.Project == "Shapes" && generated.Name == "RegexGenerator.g.cs" && generated.FilePath == path);
        source.Source.Should().Contain("partial class Registry").And.Contain("private static partial global::System.Text.RegularExpressions.Regex Description()");
        source.Lines.Should().Be(source.Source!.Split('\n').Length);
        list.Items.Should().Equal(source with { Source = null });
    }

    [Test]
    public async Task NamespaceGraph_FindsTheCycle()
    {
        var graph = await Server.CallAsync<NamespaceGraphTool.NamespaceGraph>("namespace_graph", new() { ["projectPath"] = Sample.Solution });

        graph.Dependencies.Should().BeEquivalentTo(
        [
            new NamespaceGraphTool.NamespaceEdge("Shapes", "Shapes.Internal", 1, ["Shapes.ShapeFactory -> Shapes.Internal.Registry"]),
            new NamespaceGraphTool.NamespaceEdge("Shapes.App", "Shapes", 1, ["Shapes.App.Spinner -> Shapes.IShape"]),
            new NamespaceGraphTool.NamespaceEdge("Shapes.Internal", "Shapes", 3,
                ["Shapes.Internal.Registry -> Shapes.Circle", "Shapes.Internal.Registry -> Shapes.Shape", "Shapes.Internal.Registry -> Shapes.Square"]),
            new NamespaceGraphTool.NamespaceEdge("Shapes.Tests", "Shapes", 4,
                ["Shapes.Tests.ShapeTests -> Shapes.Circle", "Shapes.Tests.ShapeTests -> Shapes.Rectangle", "Shapes.Tests.ShapeTests -> Shapes.ShapeMath"]),
        ], options => options.WithStrictOrdering());
        graph.Cycles.Should().BeEquivalentTo([new[] { "Shapes", "Shapes.Internal" }], options => options.WithStrictOrdering());
    }

    [Test]
    public async Task Diagnostics_ErrorsFirst_ByFileOrProject()
    {
        var solution = await Server.CallAsync<PagedResult<DiagnosticsTool.DiagnosticEntry>>("diagnostics", new() { ["projectPath"] = Sample.Solution });
        var problems = await Server.CallAsync<PagedResult<DiagnosticsTool.DiagnosticEntry>>("diagnostics", new() { ["projectPath"] = Sample.ProblemsProject });
        var errors = await Server.CallAsync<PagedResult<DiagnosticsTool.DiagnosticEntry>>("diagnostics", new()
        {
            ["projectPath"] = Sample.ProblemsProject, ["filePath"] = Sample.AccountFile, ["severity"] = "Error",
        });

        solution.Items.Should().BeEmpty();
        var error = new DiagnosticsTool.DiagnosticEntry("CS0029", "Error", "Cannot implicitly convert type 'int' to 'string'", Sample.Location(Sample.AccountFile, "balance;", 2));
        problems.Items.Should().Equal(
            error,
            new DiagnosticsTool.DiagnosticEntry("CS0168", "Warning", "The variable 'unused' is declared but never used", Sample.Location(Sample.AccountFile, "unused")));
        errors.Items.Should().Equal(error);
    }

    [Test]
    public async Task PublicApi_OneLinePerTypeAndMember_Sorted()
    {
        var api = await Server.CallAsync<PagedResult<string>>("public_api", new() { ["projectPath"] = Sample.Solution, ["project"] = "Shapes" });

        api.Items.Should().Equal(
            "double Shapes.IShape.Area()",
            "protected Shapes.Shape.Shape()",
            "public Shapes.Circle.Circle(double radius)",
            "public Shapes.Rectangle Shapes.Rectangle.Scale(double factor)",
            "public Shapes.Rectangle Shapes.Rectangle.Scale(double x, double y)",
            "public Shapes.Rectangle.Rectangle(double width, double height)",
            "public Shapes.Square.Square(double side)",
            "public abstract class Shapes.Shape : Shapes.IShape",
            "public abstract double Shapes.Shape.Area()",
            "public class Shapes.Rectangle : Shapes.Shape",
            "public double Shapes.Circle.Radius { get; }",
            "public double Shapes.Rectangle.Height { get; }",
            "public double Shapes.Rectangle.Width { get; }",
            "public interface Shapes.IShape",
            "public override double Shapes.Circle.Area()",
            "public override double Shapes.Rectangle.Area()",
            "public override string Shapes.Shape.ToString()",
            "public override string Shapes.Square.Name { get; }",
            "public sealed class Shapes.Circle : Shapes.Shape",
            "public sealed class Shapes.Square : Shapes.Rectangle",
            "public static Shapes.IShape Shapes.ShapeMath.Largest(System.Collections.Generic.IReadOnlyList<Shapes.IShape> shapes)",
            "public static Shapes.Shape Shapes.ShapeFactory.Parse(string description)",
            "public static class Shapes.ShapeFactory",
            "public static class Shapes.ShapeMath",
            "public static double Shapes.ShapeMath.TotalArea(System.Collections.Generic.IEnumerable<Shapes.IShape> shapes)",
            "public static string Shapes.ShapeMath.Classify(Shapes.IShape shape)",
            "public virtual string Shapes.Shape.Name { get; }",
            "string Shapes.IShape.Name { get; }");
    }

    // Nothing is written: the file keeps its error.
    [Test]
    public async Task PreviewEdit_TellsWhatAnEditWouldFixAndBreak()
    {
        var before = File.ReadAllText(Sample.AccountFile);
        var preview = await Server.CallAsync<PreviewEditTool.Preview>("preview_edit", new()
        {
            ["projectPath"] = Sample.ProblemsProject,
            ["edits"] = new object[]
            {
                new { filePath = Sample.AccountFile, oldText = "=> balance;", newText = "=> balance.ToString();" },
                new { filePath = Sample.AccountFile, oldText = "balance += amount;", newText = "balance += amount;\n        Missing();" },
            },
        });

        preview.Resolved.Should().Equal(
            new DiagnosticsTool.DiagnosticEntry("CS0029", "Error", "Cannot implicitly convert type 'int' to 'string'", Sample.Location(Sample.AccountFile, "balance;", 2)));
        preview.Introduced.Should().ContainSingle().Which.Should().Match<DiagnosticsTool.DiagnosticEntry>(diagnostic =>
            diagnostic.Id == "CS0103" && diagnostic.Severity == "Error" && diagnostic.Message == "The name 'Missing' does not exist in the current context");
        File.ReadAllText(Sample.AccountFile).Should().Be(before);
    }

    [Test]
    public async Task CodeActions_OfferTheFixesForALine()
    {
        var actions = await Server.CallAsync<PagedResult<CodeActionEntry>>("code_actions", new()
        {
            ["projectPath"] = Sample.ProblemsProject, ["filePath"] = Sample.AccountFile, ["line"] = Sample.LineOf(Sample.AccountFile, "int unused;"),
        });

        actions.Items.Should().ContainSingle().Which.Should().Match<CodeActionEntry>(action =>
            action.Title == "Remove unused variable" && action.Kind == "Fix" && action.DiagnosticId == "CS0168" && action.Id.Length > 0);
    }
}
