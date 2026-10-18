using System.Text.RegularExpressions;
using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>What assemblies contain: IL, the JIT's code, decompiled C#, the sources their PDBs list; and running code with evaluate.</summary>
public class AssemblyTests : EndToEndTest
{
    private static readonly string AreaIL = $"""
        .maxstack 8
        // Circle.cs:{Sample.LineOf(Sample.CircleFile, "Area()")}
        IL_0000: ldc.r8 3.141592653589793
        IL_0009: ldarg.0
        IL_000a: call System.Double Shapes.Circle::get_Radius()
        IL_000f: mul
        IL_0010: ldarg.0
        IL_0011: call System.Double Shapes.Circle::get_Radius()
        IL_0016: mul
        IL_0017: ret

        """.ReplaceLineEndings("\n");

    // The build output and the current source compile to the same IL.
    [Test]
    public async Task Il_OfTheBuildOutput_AndOfTheSource()
    {
        var built = await Server.CallAsync<PagedResult<MethodIL>>("assembly_il", new() { ["assemblyPath"] = Sample.ShapesAssembly, ["name"] = "Circle.Area" });
        var compiled = await Server.CallAsync<PagedResult<MethodIL>>("symbol_il", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Shapes.Circle.Area" });

        built.Items.Should().Equal(new MethodIL("System.Double Shapes.Circle::Area()", AreaIL));
        compiled.Items.Should().Equal(built.Items);
    }

    [Test]
    public async Task Il_OfAType_ListsEachMethod()
    {
        var methods = await Server.CallAsync<PagedResult<MethodIL>>("assembly_il", new() { ["assemblyPath"] = Sample.ShapesAssembly, ["name"] = "Shapes.Rectangle" });

        // In metadata order: the primary constructor comes first.
        methods.Items.Select(method => method.Method).Should().Equal(
            "System.Void Shapes.Rectangle::.ctor(System.Double,System.Double)",
            "System.Double Shapes.Rectangle::get_Width()",
            "System.Double Shapes.Rectangle::get_Height()",
            "System.Double Shapes.Rectangle::Area()",
            "Shapes.Rectangle Shapes.Rectangle::Scale(System.Double)",
            "Shapes.Rectangle Shapes.Rectangle::Scale(System.Double,System.Double)");
    }

    // A name without parameters takes every overload; a property stands for its accessors; a type for its methods, its constructor included.
    [Test]
    public async Task Il_OfOverloads_OfAProperty_AndOfAType()
    {
        async Task<IEnumerable<string>> BuiltAsync(string name) =>
            (await Server.CallAsync<PagedResult<MethodIL>>("assembly_il", new() { ["assemblyPath"] = Sample.ShapesAssembly, ["name"] = name })).Items.Select(method => method.Method);
        async Task<IEnumerable<string>> CompiledAsync(string symbol) =>
            (await Server.CallAsync<PagedResult<MethodIL>>("symbol_il", new() { ["projectPath"] = Sample.Solution, ["symbol"] = symbol })).Items.Select(method => method.Method);

        (await BuiltAsync("Rectangle.Scale")).Should().Equal("Shapes.Rectangle Shapes.Rectangle::Scale(System.Double)", "Shapes.Rectangle Shapes.Rectangle::Scale(System.Double,System.Double)");
        (await CompiledAsync("Rectangle.Scale(double)")).Should().Equal("Shapes.Rectangle Shapes.Rectangle::Scale(System.Double)");
        (await BuiltAsync("Circle.Radius")).Should().Equal("System.Double Shapes.Circle::get_Radius()");
        (await CompiledAsync("Shapes.Square")).Should().Equal("System.Void Shapes.Square::.ctor(System.Double)", "System.String Shapes.Square::get_Name()");
        (await Server.CallFailingAsync("symbol_il", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "ShapeMath.TotalArea.total" }))
            .Should().Be("An error occurred invoking 'symbol_il': 'ShapeMath.TotalArea.total' has no IL: pick a type, method, property or event declared in the project.");
    }

    // Top-level statements compile into Program.<Main>$, as stack traces name it too, with the lambda they start a thread with.
    [Test]
    public async Task Il_OfTopLevelStatements()
    {
        var methods = await Server.CallAsync<PagedResult<MethodIL>>("symbol_il", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Program.<Main>$" });

        methods.Items.Should().HaveCount(2);
        var main = methods.Items.First();
        main.Method.Should().Be("System.Void Program::<Main>$(System.String[])");
        main.IL.Should().Contain($"// Program.cs:{Sample.LineOf(Sample.ProgramFile, "Console.ReadLine();")}\n").And.Contain("call System.String System.Console::ReadLine()");
        methods.Items.Last().Method.Should().MatchRegex(@"^System\.Void Program/<>c__DisplayClass\w+::<<Main>\$>b__\w+\(\)$");
    }

    // The members of an extension block compile to skeletons that only throw; their code is in static methods of the class, which take the
    // block's parameter first.
    [Test]
    public async Task Il_OfExtensionMembers_IsTheirImplementations()
    {
        var project = Sample.NewProject("ExtensionIl", ("Kinds.cs", SampleSolution.Kinds));
        async Task<IEnumerable<string>> MethodsAsync(string symbol) =>
            (await Server.CallAsync<PagedResult<MethodIL>>("symbol_il", new() { ["projectPath"] = project, ["symbol"] = symbol })).Items.Select(method => method.Method);

        var shout = await Server.CallAsync<PagedResult<MethodIL>>("symbol_il", new() { ["projectPath"] = project, ["symbol"] = "Text.Shout" });

        shout.Items.Should().ContainSingle().Which.IL.Should().Contain("callvirt System.String System.String::ToUpperInvariant()");
        shout.Items.Single().Method.Should().Be("System.String Edits.Text::Shout(System.String)");
        (await MethodsAsync("Text.IsBlank")).Should().Equal("System.Boolean Edits.Text::get_IsBlank(System.String)");
        (await MethodsAsync("Text.extension(string)")).Should().BeEquivalentTo("System.Boolean Edits.Text::get_IsBlank(System.String)", "System.String Edits.Text::Shout(System.String)");
    }

    // A type nested in a generic one is Outer`1/Inner in metadata; a path finds it in the source, and its C# name in the build output.
    [Test]
    public async Task IlAndDecompile_OfATypeNestedInAGenericOne()
    {
        var project = Sample.NewProject("NestedIl", ("Outer.cs", "namespace Edits;\n\npublic class Outer<T>\n{\n    public class Inner\n    {\n        public int Count() => 2;\n    }\n}\n"));
        var build = await Server.CallAsync<BuildTool.BuildResult>("build", new() { ["projectPath"] = project });
        var assembly = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Debug", "net10.0", "NestedIl.dll");

        var compiled = await Server.CallAsync<PagedResult<MethodIL>>("symbol_il", new() { ["projectPath"] = project, ["symbol"] = "Outer.Inner.Count" });
        var built = await Server.CallAsync<PagedResult<MethodIL>>("assembly_il", new() { ["assemblyPath"] = assembly, ["name"] = "Outer.Inner.Count" });
        var decompiled = await Server.CallAsync<PagedResult<DecompiledMember>>("decompile", new() { ["assemblyPath"] = assembly, ["name"] = "Edits.Outer`1+Inner" });

        build.Succeeded.Should().BeTrue();
        compiled.Items.Select(method => method.Method).Should().Equal("System.Int32 Edits.Outer`1/Inner::Count()");
        built.Items.Should().Equal(compiled.Items);
        decompiled.Items.Should().ContainSingle().Which.Code.Should().Contain("public class Inner").And.Contain("return 2;");
    }

    // A local function compiles to a helper named after the member it is in, which has no symbol path of its own: it comes with that member.
    [Test]
    public async Task IlAndAsm_OfALocalFunction_AreThoseOfTheMemberItIsIn()
    {
        var project = Sample.NewProject("LocalIl", ("Local.cs", """
            namespace Edits;

            public static class Local
            {
                public static int Twice(int value)
                {
                    return Double(value);

                    static int Double(int x) => x * 2;
                }
            }
            """));

        var il = await Server.CallAsync<PagedResult<MethodIL>>("symbol_il", new() { ["projectPath"] = project, ["symbol"] = "Local.Twice.Double" });
        var asm = await Server.CallAsync<PagedResult<MethodAsm>>("symbol_asm", new() { ["projectPath"] = project, ["symbol"] = "Local.Twice.Double" });

        il.Items.Select(method => method.Method).Should().Equal("System.Int32 Edits.Local::Twice(System.Int32)", "System.Int32 Edits.Local::<Twice>g__Double|0_0(System.Int32)");
        il.Items.Last().IL.Should().Contain("mul");
        asm.Items.Select(method => method.Method).Should().Equal(il.Items.Select(method => method.Method));
    }

    // The compiler names a file-local type after its file, as in <Ones>F1A2...__Helper, where code and symbol paths say Helper. Two files declare
    // a Helper each, so the path finds both, as the name does in the build output.
    [Test]
    public async Task IlAsmAndDecompile_OfFileLocalTypes()
    {
        static string Declaring(string type, int value) =>
            $"namespace Edits;\n\nfile static class Helper\n{{\n    public static int Run() => {value};\n}}\n\npublic static class {type}\n{{\n    public static int Get() => Helper.Run();\n}}\n";
        var project = Sample.NewProject("FileLocalIl", ("Ones.cs", Declaring("Ones", 1)), ("Twos.cs", Declaring("Twos", 2)));

        var il = await Server.CallAsync<PagedResult<MethodIL>>("symbol_il", new() { ["projectPath"] = project, ["symbol"] = "Edits.Helper.Run" });
        var asm = await Server.CallAsync<PagedResult<MethodAsm>>("symbol_asm", new() { ["projectPath"] = project, ["symbol"] = "Helper" });
        (await Server.CallAsync<BuildTool.BuildResult>("build", new() { ["projectPath"] = project })).Succeeded.Should().BeTrue();
        var decompiled = await Server.CallAsync<PagedResult<DecompiledMember>>("decompile", new()
        {
            ["assemblyPath"] = Sample.In("edits", "FileLocalIl", "bin", "Debug", "net10.0", "FileLocalIl.dll"), ["name"] = "Helper",
        });

        il.Items.Select(method => Regex.Replace(method.Method, "F[0-9A-F]+__", "F__")).Should().BeEquivalentTo(
            "System.Int32 Edits.<Ones>F__Helper::Run()", "System.Int32 Edits.<Twos>F__Helper::Run()");
        il.Items.Single(method => method.Method.Contains("<Twos>")).IL.Should().Contain("ldc.i4.2");
        asm.Items.Select(method => method.Method).Should().BeEquivalentTo(il.Items.Select(method => method.Method));
        decompiled.Items.Select(member => Regex.Replace(member.Member, "F[0-9A-F]+__", "F__")).Should().BeEquivalentTo("Edits.<Ones>F__Helper", "Edits.<Twos>F__Helper");
    }

    // The build output is a debug build, which the JIT does not optimize; symbol_asm compiles a release build, which inlines the getters.
    [Test]
    public async Task Asm_OfTheBuildOutput_AndOfTheSource()
    {
        var built = await Server.CallAsync<PagedResult<MethodAsm>>("assembly_asm", new() { ["assemblyPath"] = Sample.ShapesAssembly, ["name"] = "Rectangle.Area" });
        var compiled = await Server.CallAsync<PagedResult<MethodAsm>>("symbol_asm", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Rectangle.Area" });

        var debug = built.Items.Should().ContainSingle().Subject;
        debug.Method.Should().Be("System.Double Shapes.Rectangle::Area()");
        debug.Asm.Should().StartWith($"; .NET {Environment.Version.Major}.").And.Contain("; Assembly listing for method Shapes.Rectangle:Area():double:this (MinOpts)")
            .And.Contain("call     [Shapes.Rectangle:get_Width():double:this]");
        var release = compiled.Items.Should().ContainSingle().Subject;
        release.Asm.Should().Contain("; Assembly listing for method Shapes.Rectangle:Area():double:this (FullOpts)").And.NotContain("get_Width");
    }

    [Test]
    public async Task Decompile_APackageType_AndAMethodOfTheBuild()
    {
        var greeter = await Server.CallAsync<PagedResult<DecompiledMember>>("decompile", new() { ["assemblyPath"] = Sample.GreetingsAssembly, ["name"] = "Greeter" });
        var create = await Server.CallAsync<PagedResult<DecompiledMember>>("decompile", new() { ["assemblyPath"] = Sample.ShapesAssembly, ["name"] = "Registry.Create" });

        // The constant from Base is compiled in.
        greeter.Items.Should().ContainSingle().Which.Should().Match<DecompiledMember>(member => member.Member == "Roslynaut.E2E.Greetings.Greeter");
        greeter.Items.Single().Code.ReplaceLineEndings("\n").Should().Be("""
            /// <summary>Greets people.</summary>
            public static class Greeter
            {
            	/// <summary>Says hello to someone.</summary>
            	/// <param name="name">Who to greet.</param>
            	public static string Hello(string name)
            	{
            		return "Hello, " + name + "!";
            	}

            	/// <summary>Says goodbye to someone.</summary>
            	/// <param name="name">Who to say goodbye to.</param>
            	public static string Bye(string name)
            	{
            		return "Bye, " + name + "!";
            	}
            }

            """.ReplaceLineEndings("\n"));

        // Local variable names come from the PDB next to the assembly.
        create.Items.Should().ContainSingle().Which.Member.Should().Be("Shapes.Shape Shapes.Internal.Registry::Create(System.String)");
        create.Items.Single().Code.Should().Contain("Match match = Description().Match(description);").And.Contain("double size = double.Parse(");
    }

    [Test]
    public async Task AssemblySources_ListsThePdbsDocuments_AndFetchesThem()
    {
        var documents = await Server.CallAsync<PagedResult<SourceDocument>>("assembly_sources", new() { ["assemblyPath"] = Sample.GreetingsAssembly });
        var greeter = await Server.CallAsync<PagedResult<SourceDocument>>("assembly_sources", new() { ["assemblyPath"] = Sample.GreetingsAssembly, ["member"] = "Greeter.Hello" });

        var obj = Sample.In("packages-src", "Greetings", "obj", "Release", "net10.0");
        documents.Items.Should().BeEquivalentTo(
        [
            new SourceDocument(Sample.In("packages-src", "Greetings", "Greeter.cs"), "Embedded", null, null, null),
            new SourceDocument(Path.Combine(obj, ".NETCoreApp,Version=v10.0.AssemblyAttributes.cs"), "Embedded", null, null, null),
            new SourceDocument(Path.Combine(obj, $"{SampleSolution.GreetingsId}.AssemblyInfo.cs"), "Embedded", null, null, null),
        ]);
        var source = greeter.Items.Should().ContainSingle().Subject;
        source.Should().Match<SourceDocument>(document => document.Name == Sample.In("packages-src", "Greetings", "Greeter.cs") && document.Origin == "Embedded"
            && document.Verified == true && File.Exists(document.FilePath));
        source.Source.Should().Be(SampleSolution.GreeterSource(SampleSolution.GreetingsV1));
    }

    // The code runs against the current source of the solution's projects.
    [Test]
    public async Task Evaluate_RunsCodeAgainstTheProjects()
    {
        var evaluation = await Server.CallAsync<EvaluateTool.Evaluation>("evaluate", new()
        {
            ["projectPath"] = Sample.Solution,
            ["code"] = "Console.WriteLine(Shapes.ShapeFactory.Parse(\"square 3\").Name); new Shapes.Square(3).Area() + Shapes.ShapeMath.Classify(new Shapes.Circle(10)).Length",
        });
        var failure = await Server.CallAsync<EvaluateTool.Evaluation>("evaluate", new() { ["projectPath"] = Sample.Solution, ["code"] = "Shapes.ShapeFactory.Parse(\"triangle 1\")" });

        evaluation.Should().Be(new EvaluateTool.Evaluation($"Square{Environment.NewLine}", "14", "System.Double", null));
        failure.Error.Should().Contain("System.FormatException").And.Contain("Unknown shape: triangle 1");
    }

    // The app's package comes along with its project, and code can await.
    [Test]
    public async Task Evaluate_UsesThePackagesOfTheProjects_AndAwaits()
    {
        var evaluation = await Server.CallAsync<EvaluateTool.Evaluation>("evaluate", new()
        {
            ["projectPath"] = Sample.Solution, ["code"] = "await System.Threading.Tasks.Task.Yield(); Roslynaut.E2E.Greetings.Greeter.Hello(\"you\")",
        });

        evaluation.Should().Be(new EvaluateTool.Evaluation("", "\"Hello, you!\"", "System.String", null));
    }

    [Test]
    public async Task Evaluate_StopsCodeThatRunsTooLong_AndReportsCompileErrors()
    {
        var endless = await Server.CallFailingAsync("evaluate", new() { ["projectPath"] = Sample.Solution, ["code"] = "while (true) { }", ["timeoutSeconds"] = 5 });
        var wrong = await Server.CallAsync<EvaluateTool.Evaluation>("evaluate", new() { ["projectPath"] = Sample.Solution, ["code"] = "new Shapes.Hexagon()" });

        endless.Should().Be("An error occurred invoking 'evaluate': The code did not finish within 5 seconds, so it was stopped.");
        wrong.Should().Match<EvaluateTool.Evaluation>(evaluation => evaluation.Value == null && evaluation.Error!.Contains("CS0234"));
    }

    // Code that ends the process leaves no result, nor what it wrote, to say why; and code that writes a lot would flood the client.
    [Test]
    public async Task Evaluate_OfCodeThatExits_OrWritesALot()
    {
        var exits = await Server.CallFailingAsync("evaluate", new() { ["projectPath"] = Sample.ShapesProject, ["code"] = "Environment.Exit(3);" });
        var writes = await Server.CallAsync<EvaluateTool.Evaluation>("evaluate", new() { ["projectPath"] = Sample.ShapesProject, ["code"] = "Console.Write(new string('x', 1_000_000)); 1" });

        exits.Should().Be("An error occurred invoking 'evaluate': The script host ended with exit code 3 before it gave a result, as when the code calls Environment.Exit.");
        writes.Output.Should().Be(new string('x', EvaluateTool.MaxOutput) + "…");
        writes.Value.Should().Be("1");
    }

    // Text that no single-byte code page holds, both ways.
    [Test]
    public async Task Evaluate_KeepsTextThatIsNotAscii()
    {
        var evaluation = await Server.CallAsync<EvaluateTool.Evaluation>("evaluate", new() { ["projectPath"] = Sample.Solution, ["code"] = "Console.Write(\"Жук €\"); \"Größe\" + \"✓\"" });

        evaluation.Should().Be(new EvaluateTool.Evaluation("Жук €", "\"Größe✓\"", "System.String", null));
    }
}
