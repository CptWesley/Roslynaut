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

    // An element of a collection expression converts to the element type of the array.
    [Test]
    public async Task ExpressionInfo_GivesTheTypeAnExpressionConvertsTo()
    {
        var expressions = await Server.CallAsync<List<ExpressionInfoTool.ExpressionInfo>>("expression_info", At(Sample.ProgramFile, "new Circle(1)"));

        expressions.Should().BeEquivalentTo(
        [
            new ExpressionInfoTool.ExpressionInfo("new Circle(1)", "Shapes.Circle", "Shapes.IShape", "Implicit reference", null, "NotNull", "Shapes.Circle.Circle(double)", [], null),
            new ExpressionInfoTool.ExpressionInfo("[new Circle(1), new Rectangle(2, 3), new Square(4)]", null, "Shapes.IShape[]", "Implicit conversion", null, "NotNull", null, [], null),
        ], options => options.WithStrictOrdering());
    }

    // largest starts out null and may stay so; ?? throw makes the result not null.
    [Test]
    public async Task ExpressionInfo_GivesTheNullState()
    {
        var expressions = await Server.CallAsync<List<ExpressionInfoTool.ExpressionInfo>>("expression_info", At(Sample.ShapeMathFile, "largest ?? throw"));

        expressions.Select(expression => (expression.Type, expression.NullState)).Should().Equal(("Shapes.IShape?", "MaybeNull"), ("Shapes.IShape", "NotNull"));
    }

    [Test]
    public async Task SignatureHelp_OfAnIndexer_PicksItsOverload()
    {
        var signatures = await Server.CallAsync<List<SignatureHelpTool.Signature>>("signature_help", At(Sample.RegistryFile, "\"size\"]"));

        signatures.Select(signature => (signature.Display, signature.Chosen)).Should().Equal(
            ("System.Text.RegularExpressions.GroupCollection.this[int]", false), ("System.Text.RegularExpressions.GroupCollection.this[string]", true));
    }

    // The documentation of a package's method comes from the XML file next to its assembly.
    [Test]
    public async Task SignatureHelp_OfAConstructor_AndOfAPackagesMethod()
    {
        var circle = await Server.CallAsync<List<SignatureHelpTool.Signature>>("signature_help", At(Sample.ProgramFile, "1), new Rectangle"));
        var hello = await Server.CallAsync<List<SignatureHelpTool.Signature>>("signature_help", At(Sample.ProgramFile, "\"shapes\"));\nConsole.WriteLine(Greeter.Bye"));

        circle.Should().BeEquivalentTo([new SignatureHelpTool.Signature("Shapes.Circle.Circle(double)", "A circle around the origin.", [new SignatureHelpTool.Parameter("radius", "double", null, false)], 0, true)]);
        hello.Should().BeEquivalentTo([new SignatureHelpTool.Signature("Roslynaut.E2E.Greetings.Greeter.Hello(string)", "Says hello to someone.",
            [new SignatureHelpTool.Parameter("name", "string", "Who to greet.", false)], 0, true)]);
    }

    [Test]
    public async Task Completion_ListsTheMembersOfAPackagesType()
    {
        var members = await Server.CallAsync<PagedResult<CompletionTool.CompletionEntry>>("completion", At(Sample.ProgramFile, "Hello(\"shapes\")"));

        members.Items.Select(item => (item.Text, item.Kind)).Should().BeEquivalentTo([("Bye", "Method"), ("Equals", "Method"), ("Hello", "Method"), ("ReferenceEquals", "Method")]);
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

    // The statements of an else, a catch or a lambda sit in the statement around them, which the lines cover whole.
    [Test]
    public async Task DataFlow_OfStatementsWithElseCatchAndLambdaBlocks()
    {
        var project = Sample.NewProject("DataFlow", ("Flow.cs", """
            using System;

            namespace Edits;

            public static class Flow
            {
                public static int Sign(int x)
                {
                    int sign;
                    if (x < 0)
                        sign = -1;
                    else
                        sign = 1;
                    try
                    {
                        sign = checked(sign * x);
                    }
                    catch (OverflowException)
                    {
                        return 0;
                    }

                    Func<int> half = () => { return sign / 2; };
                    return half();
                }
            }
            """));
        var file = Path.Combine(Path.GetDirectoryName(project)!, "Flow.cs");
        async Task<DataFlowTool.DataFlow> AnalyzeAsync(int startLine, int endLine) => await Server.CallAsync<DataFlowTool.DataFlow>("data_flow", new()
        {
            ["projectPath"] = project, ["filePath"] = file, ["startLine"] = startLine, ["endLine"] = endLine,
        });
        int Line(string text) => Sample.LineOf(file, text);

        var branches = await AnalyzeAsync(Line("if (x < 0)"), Line("sign = 1;"));
        var guarded = await AnalyzeAsync(Line("try"), Line("return 0;") + 1);
        var lambda = await AnalyzeAsync(Line("Func<int> half"), Line("return half();"));

        (branches.Statements, branches.DataFlowsIn, branches.DataFlowsOut, branches.AlwaysAssigned, branches.EndPointIsReachable)
            .Should().BeEquivalentTo((1, new[] { "x" }, new[] { "sign" }, new[] { "sign" }, true));
        (guarded.Statements, guarded.DataFlowsIn, guarded.DataFlowsOut, guarded.EndPointIsReachable).Should().BeEquivalentTo((1, new[] { "x", "sign" }, new[] { "sign" }, true));
        guarded.ExitPoints.Should().Equal(Sample.Location(file, "return 0;"));
        (lambda.Statements, lambda.VariablesDeclared, lambda.Captured, lambda.EndPointIsReachable).Should().BeEquivalentTo((2, new[] { "half" }, new[] { "sign" }, false));
        lambda.ReturnStatements.Should().Equal(Sample.Location(file, "return half();"));
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

    // Within base(...) and the base type's arguments of a primary constructor, as within new: the base type's constructors, the protected one too.
    // The project writes no documentation file, which leaves the compiler to skip documentation comments; they still document the constructors.
    [Test]
    public async Task SignatureHelp_OfABaseConstructorCall_ListsTheBaseConstructors()
    {
        var project = Sample.NewProject("BaseSignatures", ("Figures.cs", """
            namespace Edits;

            public class Figure
            {
                /// <summary>A figure with a name.</summary>
                public Figure(string name) { }

                /// <summary>A figure with a name and a size.</summary>
                public Figure(string name, int size) { }

                protected Figure(int size) { }
            }

            public class Dot : Figure
            {
                public Dot() : base("dot", 1) { }
            }

            public class Ring(int size) : Figure("ring", size)
            {
            }
            """));
        var file = Path.Combine(Path.GetDirectoryName(project)!, "Figures.cs");
        async Task<List<SignatureHelpTool.Signature>> HelpAsync(string text)
        {
            var (line, column) = Sample.Find(file, text);
            return await Server.CallAsync<List<SignatureHelpTool.Signature>>("signature_help", new() { ["projectPath"] = project, ["filePath"] = file, ["line"] = line, ["column"] = column });
        }

        SignatureHelpTool.Parameter Parameter(string name, string type) => new(name, type, null, false);
        List<SignatureHelpTool.Signature> constructors =
        [
            new("Edits.Figure.Figure(string)", "A figure with a name.", [Parameter("name", "string")], null, false),
            new("Edits.Figure.Figure(string, int)", "A figure with a name and a size.", [Parameter("name", "string"), Parameter("size", "int")], 1, true),
            new("Edits.Figure.Figure(int)", null, [Parameter("size", "int")], null, false),
        ];
        (await HelpAsync("1) { }")).Should().BeEquivalentTo(constructors, options => options.WithStrictOrdering());
        (await HelpAsync("size)\n")).Should().BeEquivalentTo(constructors, options => options.WithStrictOrdering());
    }

    // A call binds a generic method with the type arguments it infers, Map<int>; the overload listed, Map<TResult>, is the one chosen.
    [Test]
    public async Task SignatureHelp_OfAGenericMethod_ChoosesItsOverload()
    {
        var project = Sample.NewProject("GenericSignatures", ("Kinds.cs", SampleSolution.Kinds));
        var file = Sample.In("edits", "GenericSignatures", "Kinds.cs");
        var (line, column) = Sample.Find(file, "item => item?.Length");

        var signatures = await Server.CallAsync<List<SignatureHelpTool.Signature>>("signature_help", new() { ["projectPath"] = project, ["filePath"] = file, ["line"] = line, ["column"] = column });

        signatures.Select(signature => (signature.Display, signature.ActiveParameter, signature.Chosen)).Should().Equal(("Edits.Box<string>.Map<TResult>(System.Func<string?, TResult>)", (int?)0, true));
    }

    // A call of a delegate, such as a Func parameter, has no method group of overloads: what it calls is the delegate's Invoke method.
    [Test]
    public async Task SignatureHelp_OfADelegatesCall_IsItsInvokeMethod()
    {
        var project = Sample.NewProject("DelegateSignatures", ("Kinds.cs", SampleSolution.Kinds));
        var file = Sample.In("edits", "DelegateSignatures", "Kinds.cs");
        var (line, column) = Sample.Find(file, "map(Item)");

        var signatures = await Server.CallAsync<List<SignatureHelpTool.Signature>>("signature_help", new() { ["projectPath"] = project, ["filePath"] = file, ["line"] = line, ["column"] = column + "map(".Length });

        signatures.Should().BeEquivalentTo([new SignatureHelpTool.Signature("System.Func<T?, TResult>.Invoke(T?)", null, [new("arg", "T?", null, false)], 0, true)]);
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

    // At the start of a statement: the keywords that can start one, and the locals and parameters in scope there.
    [Test]
    public async Task Completion_InAStatement_ListsKeywordsLocalsAndParameters()
    {
        async Task<IEnumerable<(string Text, string? Kind)>> CompleteAsync(string filter)
        {
            var arguments = At(Sample.ShapeMathFile, "if (area");
            arguments["filter"] = filter;
            return (await Server.CallAsync<PagedResult<CompletionTool.CompletionEntry>>("completion", arguments)).Items.Select(item => (item.Text, item.Kind));
        }

        (await CompleteAsync("fo")).Should().Contain([("for", "Keyword"), ("foreach", "Keyword")]);
        (await CompleteAsync("sh")).Should().Contain([("shape", "Local"), ("shapes", "Parameter")]);
        (await CompleteAsync("ar")).Should().Contain(("area", "Local")).And.NotContain(("total", "Local"));
    }

    // The conversion that fails: an int field where the method returns a string.
    [Test]
    public async Task ExpressionInfo_OfAnExpressionThatDoesNotConvert()
    {
        var (line, column) = Sample.Find(Sample.AccountFile, "balance;", 2);
        var expressions = await Server.CallAsync<List<ExpressionInfoTool.ExpressionInfo>>("expression_info", new()
        {
            ["projectPath"] = Sample.ProblemsProject, ["filePath"] = Sample.AccountFile, ["line"] = line, ["column"] = column,
        });

        expressions.Should().BeEquivalentTo([new ExpressionInfoTool.ExpressionInfo("balance", "int", "string", null, null, null, "Problems.Account.balance", [], null)]);
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

    // The app's code outside its top-level statements, which the compiler puts in a Main of its own, is the spinner.
    [Test]
    public async Task CodeMetrics_OfAFile_AndOfAProject()
    {
        async Task<IEnumerable<(string, int)>> MeasureAsync(string scope) =>
            (await Server.CallAsync<PagedResult<CodeMetricsTool.Metrics>>("code_metrics", new() { ["projectPath"] = Sample.Solution, ["scope"] = scope }))
                .Items.Select(member => (member.Symbol.Name, member.Complexity));

        (await MeasureAsync(Sample.ShapeMathFile)).Should().Equal(("Largest", 5), ("TotalArea", 3), ("Classify", 3));
        (await MeasureAsync("Shapes.App")).Should().Equal(("Spin", 3));
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

    // Generated code counts too: the regex generator's code creates no shapes but throws none either. A lambda has no name to show, so code in one
    // is in the member the lambda is written in, such as the field it initializes.
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
            new FindOperationsTool.OperationEntry(Sample.Location(Sample.RegistryFile, "new Circle(size)"), "Shapes.Internal.Registry.Factories", "Shapes.Circle", false),
            new FindOperationsTool.OperationEntry(Sample.Location(Sample.ProgramFile, "new Circle(1)"), "Program.<Main>$", "Shapes.Circle", false),
            new FindOperationsTool.OperationEntry(Sample.Location(Sample.TestsFile, "new Circle(2)"), "Shapes.Tests.ShapeTests.Circle_Area_IsPiTimesRadiusSquared()", "Shapes.Circle", false),
        ]);
        throws.Items.Should().BeEquivalentTo(
        [
            new FindOperationsTool.OperationEntry(Sample.Location(Sample.In("src", "Shapes", "ShapeFactory.cs"), "throw new FormatException"), "Shapes.ShapeFactory.Parse(string)", "System.FormatException", false),
            new FindOperationsTool.OperationEntry(Sample.Location(Sample.ShapeMathFile, "throw new ArgumentException"),
                "Shapes.ShapeMath.Largest(System.Collections.Generic.IReadOnlyList<Shapes.IShape>)", "System.ArgumentException", false),
        ]);
    }

    [Test]
    public async Task FindOperations_WithinAMethod()
    {
        var calls = await Server.CallAsync<PagedResult<FindOperationsTool.OperationEntry>>("find_operations", new()
        {
            ["projectPath"] = Sample.Solution, ["kind"] = "Invocation", ["scope"] = "ShapeMath.Largest",
        });

        const string largest = "Shapes.ShapeMath.Largest(System.Collections.Generic.IReadOnlyList<Shapes.IShape>)";
        calls.Items.Should().Equal(
            new FindOperationsTool.OperationEntry(Sample.Location(Sample.ShapeMathFile, "shape.Area() >"), largest, "Shapes.IShape.Area()", false),
            new FindOperationsTool.OperationEntry(Sample.Location(Sample.ShapeMathFile, "largest.Area()"), largest, "Shapes.IShape.Area()", false));
    }

    // Not the Program of the top-level statements, nor what the regex generator declares and leaves unused.
    [Test]
    public async Task UnusedSymbols_FindsOnlyTheUnusedMethod()
    {
        var unused = await Server.CallAsync<PagedResult<SymbolEntry>>("unused_symbols", new() { ["projectPath"] = Sample.Solution });

        unused.Items.Should().Equal(new SymbolEntry("Unused", "Method", "Shapes.Internal.Registry.Unused()", "Shapes.Internal.Registry", Sample.Location(Sample.RegistryFile, "Unused()")));
    }

    // Nothing in the sample uses ShapeFactory, Largest or one Scale overload. The test class is left out, as the test framework uses it.
    [Test]
    public async Task UnusedSymbols_WithPublicOnes_AndWithinAScope()
    {
        var all = await Server.CallAsync<PagedResult<SymbolEntry>>("unused_symbols", new() { ["projectPath"] = Sample.Solution, ["includePublic"] = true });
        var rectangle = await Server.CallAsync<PagedResult<SymbolEntry>>("unused_symbols", new() { ["projectPath"] = Sample.Solution, ["includePublic"] = true, ["scope"] = "Shapes.Rectangle" });

        all.Items.Select(symbol => symbol.Signature).Should().BeEquivalentTo(
            "Shapes.ShapeFactory",
            "Shapes.ShapeMath.Largest(System.Collections.Generic.IReadOnlyList<Shapes.IShape>)",
            "Shapes.Rectangle.Scale(double)",
            "Shapes.Internal.Registry.Unused()");
        rectangle.Items.Select(symbol => symbol.Signature).Should().Equal("Shapes.Rectangle.Scale(double)");
    }

    // Calls of extension members name neither their class nor their extension block, which are used all the same.
    [Test]
    public async Task UnusedSymbols_TakeCallsOfExtensions_AsUsesOfTheirClass()
    {
        const string extensions = "namespace Edits;\n\ninternal static class Strings\n{\n    public static int Twice(this int value) => value * 2;\n\n"
            + "    extension(string text)\n    {\n        public string Shout() => text + \"!\";\n\n        public bool IsBlank => text.Length == 0;\n    }\n}\n\n"
            + "internal static class Numbers\n{\n    public static int Half(this int value) => value / 2;\n}\n";
        var project = Sample.NewProject("UnusedExtensions", ("Strings.cs", extensions),
            ("Use.cs", "namespace Edits;\n\npublic static class Use\n{\n    public static string Run() => \"a\".Shout() + 2.Twice();\n}\n"));
        var file = Sample.In("edits", "UnusedExtensions", "Strings.cs");

        var unused = await Server.CallAsync<PagedResult<SymbolEntry>>("unused_symbols", new() { ["projectPath"] = project });

        unused.Items.Should().Equal(
            new SymbolEntry("Numbers", "Class", "Edits.Numbers", "Edits", Sample.Location(file, "Numbers")),
            new SymbolEntry("IsBlank", "Property", "Edits.Strings.extension(string).IsBlank", "Edits.Strings.extension(string)", Sample.Location(file, "IsBlank")));
    }

    [Test]
    public async Task UnusedSymbols_WithinAFile()
    {
        async Task<IEnumerable<string>> UnusedAsync(string file) =>
            (await Server.CallAsync<PagedResult<SymbolEntry>>("unused_symbols", new() { ["projectPath"] = Sample.Solution, ["scope"] = file, ["includePublic"] = true }))
                .Items.Select(symbol => symbol.Signature);

        (await UnusedAsync(Sample.RegistryFile)).Should().Equal("Shapes.Internal.Registry.Unused()");
        (await UnusedAsync(Sample.ShapeMathFile)).Should().Equal("Shapes.ShapeMath.Largest(System.Collections.Generic.IReadOnlyList<Shapes.IShape>)");
        (await UnusedAsync(Sample.In("src", "Shapes", "ShapeFactory.cs"))).Should().Equal("Shapes.ShapeFactory");
    }

    // Code under #if that the load leaves out, such as for another configuration, is only text to the compiler; what it names may be in use.
    [Test]
    public async Task UnusedSymbols_LeaveOutWhatCodeUnderIfNames()
    {
        var project = Sample.NewProject("UnusedUnderIf", ("Log.cs", """
            namespace Edits;

            public static class Log
            {
                private static string Stamp() => "now";

                private static string Never() => "never";

                public static string Write(string text)
                {
            #if TIMED
                    text = Stamp() + text;
            #endif
                    return text;
                }
            }
            """));

        var unused = await Server.CallAsync<PagedResult<SymbolEntry>>("unused_symbols", new() { ["projectPath"] = project });

        unused.Items.Select(symbol => symbol.Signature).Should().Equal("Edits.Log.Never()");
    }

    [Test]
    public async Task SearchPattern_IgnoresCase_UnlessAsked()
    {
        async Task<IEnumerable<int>> LinesAsync(bool caseSensitive) =>
            (await Server.CallAsync<PagedResult<SearchPatternTool.TextMatch>>("search_pattern", new()
            {
                ["projectPath"] = Sample.Solution, ["pattern"] = "total", ["scope"] = Sample.ShapeMathFile, ["caseSensitive"] = caseSensitive,
            })).Items.Select(match => match.Line);

        int Line(string text) => Sample.LineOf(Sample.ShapeMathFile, text);
        (await LinesAsync(false)).Should().Equal(Line("TotalArea("), Line("var total"), Line("total +="), Line("return total"));
        (await LinesAsync(true)).Should().Equal(Line("var total"), Line("total +="), Line("return total"));
    }

    // $ matches at the end of each line of a file with CRLF line ends too; a line that matches twice comes once.
    [Test]
    public async Task SearchPattern_InAFileWithCrlfLineEnds_AndEachLineOnce()
    {
        var project = Sample.NewProject("SearchCrlf");
        var file = Path.Combine(Path.GetDirectoryName(project)!, "Lists.cs");
        File.WriteAllText(file, "using System;\n\nnamespace Edits;\n\npublic static class Lists\n{\n    public static int Parse(string text) => Int32.Parse(text);\n\n    public static int Twice(int x) => x * 2;\n}\n".ReplaceLineEndings("\r\n"));
        async Task<List<SearchPatternTool.TextMatch>> SearchAsync(string pattern) =>
            [.. (await Server.CallAsync<PagedResult<SearchPatternTool.TextMatch>>("search_pattern", new() { ["projectPath"] = project, ["pattern"] = pattern, ["scope"] = file })).Items];

        (await SearchAsync(";$")).Select(match => match.Line).Should().Equal(1, 3, 7, 9);
        (await SearchAsync("^}$")).Select(match => match.Line).Should().Equal(10);
        (await SearchAsync("Parse")).Should().Equal(new SearchPatternTool.TextMatch(file, 7, "7:     public static int Parse(string text) => Int32.Parse(text);\n"));
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

    // Locations in generated code give the path of the generator's file, which is not on disk: the tools that take a file take that path too,
    // except to edit it.
    [Test]
    public async Task GeneratedSources_ReadByThePathTheirLocationsGive()
    {
        var generated = (await Server.CallAsync<PagedResult<GeneratedSourcesTool.GeneratedSource>>("generated_sources", new() { ["projectPath"] = Sample.Solution, ["name"] = "RegexGenerator" }))
            .Items.Single();
        var lines = generated.Source!.Split('\n').Select(text => text.TrimEnd('\r')).ToArray();
        var line = Array.FindIndex(lines, text => text.Contains("partial class Registry")) + 1;
        var column = lines[line - 1].IndexOf("Registry", StringComparison.Ordinal) + 1;
        var registry = new SourceLocation(generated.FilePath!, line, column, lines[line - 1].Trim());

        var outline = await Server.CallAsync<PagedResult<SymbolEntry>>("document_symbols", new() { ["projectPath"] = Sample.Solution, ["filePath"] = generated.FilePath, ["depth"] = 0 });
        var definition = await Server.CallAsync<List<DefinitionTool.Definition>>("definition", new()
        {
            ["projectPath"] = Sample.Solution, ["filePath"] = generated.FilePath, ["line"] = line, ["column"] = column,
        });
        var matches = await Server.CallAsync<PagedResult<SearchPatternTool.TextMatch>>("search_pattern", new()
        {
            ["projectPath"] = Sample.Solution, ["pattern"] = "partial class Registry", ["scope"] = generated.FilePath,
        });

        outline.Items.Should().ContainEquivalentOf(new SymbolEntry("Registry", "Class", "Shapes.Internal.Registry", "Shapes.Internal", registry));
        definition.Should().ContainSingle().Which.Locations.Should().Equal(Sample.Location(Sample.RegistryFile, "Registry"), registry);
        matches.Items.Should().Equal(new SearchPatternTool.TextMatch(generated.FilePath!, line, $"{line}: {lines[line - 1]}\n"));
        (await Server.CallFailingAsync("format", new() { ["projectPath"] = Sample.Solution, ["filePath"] = generated.FilePath }))
            .Should().Be($"An error occurred invoking 'format': '{generated.FilePath}' is the output of a source generator, which cannot be edited.");
    }

    // The top-level statements are code of Program, in the global namespace.
    [Test]
    public async Task NamespaceGraph_FindsTheCycle()
    {
        var graph = await Server.CallAsync<NamespaceGraphTool.NamespaceGraph>("namespace_graph", new() { ["projectPath"] = Sample.Solution });

        graph.Dependencies.Should().BeEquivalentTo(
        [
            new NamespaceGraphTool.NamespaceEdge("<global>", "Shapes", 5, ["Program -> Shapes.Circle", "Program -> Shapes.IShape", "Program -> Shapes.Rectangle"]),
            new NamespaceGraphTool.NamespaceEdge("<global>", "Shapes.App", 1, ["Program -> Shapes.App.Spinner"]),
            new NamespaceGraphTool.NamespaceEdge("Shapes", "Shapes.Internal", 1, ["Shapes.ShapeFactory -> Shapes.Internal.Registry"]),
            new NamespaceGraphTool.NamespaceEdge("Shapes.App", "Shapes", 1, ["Shapes.App.Spinner -> Shapes.IShape"]),
            new NamespaceGraphTool.NamespaceEdge("Shapes.Internal", "Shapes", 3,
                ["Shapes.Internal.Registry -> Shapes.Circle", "Shapes.Internal.Registry -> Shapes.Shape", "Shapes.Internal.Registry -> Shapes.Square"]),
            new NamespaceGraphTool.NamespaceEdge("Shapes.Tests", "Shapes", 4,
                ["Shapes.Tests.ShapeTests -> Shapes.Circle", "Shapes.Tests.ShapeTests -> Shapes.Rectangle", "Shapes.Tests.ShapeTests -> Shapes.ShapeMath"]),
        ], options => options.WithStrictOrdering());
        graph.Cycles.Should().BeEquivalentTo([new[] { "Shapes", "Shapes.Internal" }], options => options.WithStrictOrdering());
    }

    // At depth 1 all namespaces are Shapes, which depends on no other namespace; only Program, in the global one, depends on it.
    [Test]
    public async Task NamespaceGraph_ByPrefix_AndToADepth()
    {
        var tests = await Server.CallAsync<NamespaceGraphTool.NamespaceGraph>("namespace_graph", new() { ["projectPath"] = Sample.Solution, ["prefix"] = "Shapes.Tests" });
        var top = await Server.CallAsync<NamespaceGraphTool.NamespaceGraph>("namespace_graph", new() { ["projectPath"] = Sample.Solution, ["depth"] = 1 });

        tests.Dependencies.Should().ContainSingle().Which.Should().Match<NamespaceGraphTool.NamespaceEdge>(edge => edge.From == "Shapes.Tests" && edge.To == "Shapes");
        tests.Cycles.Should().BeEmpty();
        top.Dependencies.Should().ContainSingle().Which.Should().Match<NamespaceGraphTool.NamespaceEdge>(edge => edge.From == "<global>" && edge.To == "Shapes" && edge.Types == 6);
        top.Cycles.Should().BeEmpty();
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

    // In a solution, the project to compare goes by its name. Shapes.dll is the build of the current source, so nothing differs.
    [Test]
    public async Task ApiDiff_InASolution_OfTheProjectNamed()
    {
        var unnamed = await Server.CallFailingAsync("api_diff", new() { ["projectPath"] = Sample.Solution, ["baseline"] = Sample.ShapesAssembly });
        var diff = await Server.CallAsync<ApiDiffTool.ApiDiff>("api_diff", new() { ["projectPath"] = Sample.Solution, ["project"] = "Shapes", ["baseline"] = Sample.ShapesAssembly });

        unnamed.Should().StartWith("An error occurred invoking 'api_diff': Pass the project's name: one of ").And.Contain("Shapes.App").And.Contain("Shapes.Tests");
        diff.Should().BeEquivalentTo(new ApiDiffTool.ApiDiff([], []));
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

    // Clients send line ends as \n, whatever the file has: here CRLF, as on Windows.
    [Test]
    public async Task PreviewEdit_OfLinesOfAFileWithCrlfLineEnds()
    {
        var project = Sample.NewProject("PreviewCrlf");
        var file = Path.Combine(Path.GetDirectoryName(project)!, "Account.cs");
        File.WriteAllText(file, File.ReadAllText(Sample.AccountFile).ReplaceLineEndings("\r\n"));

        var preview = await Server.CallAsync<PreviewEditTool.Preview>("preview_edit", new()
        {
            ["projectPath"] = project,
            ["edits"] = new object[] { new { filePath = file, oldText = "    {\n        int unused;\n", newText = "    {\n" } },
        });

        preview.Resolved.Should().Equal(new DiagnosticsTool.DiagnosticEntry("CS0168", "Warning", "The variable 'unused' is declared but never used", Sample.Location(file, "unused")));
        preview.Introduced.Should().BeEmpty();
        (await Server.CallFailingAsync("preview_edit", new()
        {
            ["projectPath"] = project, ["edits"] = new object[] { new { filePath = file, oldText = "balance", newText = "total" } },
        })).Should().Be($"An error occurred invoking 'preview_edit': oldText must occur exactly once in {file}: it occurs more than once.");
    }

    // Renaming a library's method breaks the app and the tests that call it; the projects that depend on the library are checked too.
    [Test]
    public async Task PreviewEdit_OfALibrary_GivesWhatBreaksInTheProjectsThatUseIt()
    {
        var before = File.ReadAllText(Sample.ShapeMathFile);
        var preview = await Server.CallAsync<PreviewEditTool.Preview>("preview_edit", new()
        {
            ["projectPath"] = Sample.Solution,
            ["edits"] = new object[] { new { filePath = Sample.ShapeMathFile, oldText = "static double TotalArea(", newText = "static double SumArea(" } },
        });

        const string message = "'ShapeMath' does not contain a definition for 'TotalArea'";
        preview.Introduced.Should().BeEquivalentTo(
        [
            new DiagnosticsTool.DiagnosticEntry("CS0117", "Error", message, Sample.Location(Sample.ProgramFile, "TotalArea(shapes)")),
            new DiagnosticsTool.DiagnosticEntry("CS0117", "Error", message, Sample.Location(Sample.TestsFile, "TotalArea([")),
        ]);
        preview.Resolved.Should().BeEmpty();
        File.ReadAllText(Sample.ShapeMathFile).Should().Be(before);
    }

    // The local feed has no such package, so restore fails, and the project loads without sources or compiler options.
    [Test]
    public async Task Diagnostics_OfAProjectThatDidNotLoad_SayWhy()
    {
        var project = Sample.NewProject("Unrestored", ("Code.cs", "namespace Edits;\n\npublic static class Code\n{\n}\n"));
        File.WriteAllText(project, File.ReadAllText(project).Replace("</PropertyGroup>", "</PropertyGroup>\n  <ItemGroup>\n    <PackageReference Include=\"Roslynaut.E2E.Missing\" Version=\"1.0.0\" />\n  </ItemGroup>"));

        var diagnostics = await Server.CallAsync<PagedResult<DiagnosticsTool.DiagnosticEntry>>("diagnostics", new() { ["projectPath"] = project });

        diagnostics.Items.Should().ContainSingle().Which.Should().Match<DiagnosticsTool.DiagnosticEntry>(diagnostic => diagnostic.Id == "NU1101" && diagnostic.Severity == "Error"
            && diagnostic.Message.StartsWith("Unable to find package Roslynaut.E2E.Missing.") && diagnostic.Message.EndsWith(" Unrestored.csproj did not load.") && diagnostic.Location == null);
    }

    // The project's .editorconfig sets the severities, as for the build; a change to it applies at once.
    [Test]
    public async Task Diagnostics_TakeTheirSeveritiesFromEditorConfig()
    {
        var project = Sample.NewProject("Severities", ("Code.cs", "namespace Edits;\n\npublic static class Code\n{\n    public static void Run()\n    {\n        int unused;\n    }\n}\n"));
        var file = Path.Combine(Path.GetDirectoryName(project)!, "Code.cs");
        async Task<IEnumerable<DiagnosticsTool.DiagnosticEntry>> DiagnoseAsync() =>
            (await Server.CallAsync<PagedResult<DiagnosticsTool.DiagnosticEntry>>("diagnostics", new() { ["projectPath"] = project, ["analyzers"] = false })).Items;
        DiagnosticsTool.DiagnosticEntry Unused(string severity) => new("CS0168", severity, "The variable 'unused' is declared but never used", Sample.Location(file, "unused"));
        (await DiagnoseAsync()).Should().Equal(Unused("Warning"));

        Sample.Write("edits/Severities/.editorconfig", "root = true\n\n[*.cs]\ndotnet_diagnostic.CS0168.severity = error\n");

        await EventuallyAsync(async () => (await DiagnoseAsync()).SequenceEqual([Unused("Error")]), "the server should load the project with the .editorconfig");
        var build = await Server.CallAsync<BuildTool.BuildResult>("build", new() { ["projectPath"] = project });
        (build.Succeeded, build.Messages.Single().Severity).Should().Be((false, "Error"));
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

    // A selection that ends at a column, rather than a line, can be just an expression: what takes an expression out applies to it.
    [Test]
    public async Task CodeActions_OfASelectedExpression_OfferToTakeItOut()
    {
        var (line, column) = Sample.Find(Sample.CircleFile, "Math.PI");
        var end = Sample.Find(Sample.CircleFile, "Radius;").Column + "Radius".Length;

        var actions = await Server.CallAsync<PagedResult<CodeActionEntry>>("code_actions", new()
        {
            ["projectPath"] = Sample.Solution, ["filePath"] = Sample.CircleFile, ["line"] = line, ["column"] = column, ["endLine"] = line, ["endColumn"] = end,
        });

        actions.Items.Should().OnlyContain(action => action.Kind == "Refactoring").And.Contain(action => action.Title == "Extract method");
        actions.Items.Select(action => action.Title).Should().Contain(
            "Introduce local: Introduce local for 'Math.PI * Radius * Radius'", "Introduce parameter for 'Math.PI * Radius * Radius': into new overload");
    }
}
