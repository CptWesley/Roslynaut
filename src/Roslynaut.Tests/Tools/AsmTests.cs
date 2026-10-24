using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

public class AsmTests
{
    private static readonly Solution Calculator = TestSolution.Create("""
        using System.Threading.Tasks;

        namespace Calc
        {
            public class Calculator
            {
                public int Add(int a, int b) => a + b;
                public double Add(double a, double b) => a + b;

                public T Echo<T>(T value) => value;

                public async Task<int> AddLaterAsync(int a)
                {
                    await Task.Yield();
                    return a + 1;
                }
            }

            public abstract class Shape { public abstract double Area(); }
        }
        """);

    private readonly AssemblyLoader assemblies = new();

    [OneTimeTearDown]
    public void DeleteTemporaryFiles() => assemblies.Dispose();

    private async Task<List<MethodAsm>> SymbolAsmAsync(string path, Solution? solution = null, string[]? typeArguments = null, string? workload = null) =>
        (await SymbolAsmTool.DisassembleAsync(solution ?? Calculator, assemblies, path, typeArguments ?? [], workload, CancellationToken.None)).ToList();

    [Test]
    public async Task SymbolAsm_Method_ReturnsOptimizedListing()
    {
        var method = (await SymbolAsmAsync("Calculator.Add(int, int)")).Should().ContainSingle().Subject;

        method.Method.Should().Be("System.Int32 Calc.Calculator::Add(System.Int32,System.Int32)");
        method.Asm.Should().StartWith($"; {RuntimeInformation.FrameworkDescription}\n; Assembly listing for method Calc.Calculator:Add(int,int):int:this (FullOpts)")
            .And.Contain("ret");
    }

    // Overloads share a name in listings, so they are told apart by the order they are compiled in.
    [Test]
    public async Task SymbolAsm_Overloads_GetTheirOwnListings()
    {
        var listings = (await SymbolAsmAsync("Calculator.Add")).ToDictionary(method => method.Method, method => method.Asm);

        listings["System.Int32 Calc.Calculator::Add(System.Int32,System.Int32)"].Should().Contain("Add(int,int):int");
        listings["System.Double Calc.Calculator::Add(System.Double,System.Double)"].Should().Contain("Add(double,double):double");
    }

    [Test]
    public async Task SymbolAsm_GenericMethod_ShowsSharedCode()
    {
        (await SymbolAsmAsync("Calculator.Echo")).Should().ContainSingle().Which.Asm.Should().Contain("Calc.Calculator:Echo[System.__Canon]");
    }

    // The state machine of an optimized async method is a struct, whose interface methods are compiled behind an unboxing stub.
    private static readonly Solution Generics = TestSolution.Create("""
        using System.Collections.Generic;

        namespace Gen
        {
            public struct Vec2 { public float X, Y; }

            public class Box<T>
            {
                private T value = default!;
                public T Get() => value;
                public U Pick<U>(U other) => other;
            }

            public class Outer<T> { public class Inner { public T Item = default!; public T Get() => Item; } }
        }
        """);

    // Value types get code of their own; the type's arguments come first, then the method's.
    [Test]
    public async Task SymbolAsm_TypeArguments_CompileTheirOwnCode()
    {
        (await SymbolAsmAsync("Box.Get", Generics, ["float"])).Should().ContainSingle().Which.Asm.Should().Contain("Gen.Box`1[float]:Get():float:this (FullOpts)");
        (await SymbolAsmAsync("Box.Pick", Generics, ["Vec2?", "List<int>"])).Should().ContainSingle()
            .Which.Asm.Should().Contain("Gen.Box`1[System.Nullable`1[Gen.Vec2]]:Pick[System.__Canon]");
        (await SymbolAsmAsync("Outer.Inner.Get", Generics, ["(int, Vec2)"])).Should().ContainSingle()
            .Which.Asm.Should().Contain("Gen.Outer`1+Inner[System.ValueTuple`2[int,Gen.Vec2]]:Get()");
    }

    [Test]
    public async Task SymbolAsm_UnknownTypeArgument_IsReported()
    {
        await FluentActions.Awaiting(() => SymbolAsmAsync("Box.Get", Generics, ["Missing"])).Should().ThrowAsync<ModelContextProtocol.McpException>()
            .WithMessage("'Missing' is not a type where Get is declared; qualify it*");
    }

    private static readonly Solution Shapes = TestSolution.Create("""
        namespace Pgo
        {
            public interface IShape { double Area(); }
            public sealed class Circle : IShape { public double Area() => 3.0; }
            public sealed class Square : IShape { public double Area() => 4.0; }

            public static class Work
            {
                public static double Total(IShape[] shapes)
                {
                    var total = 0.0;
                    foreach (var shape in shapes)
                        total += shape.Area();
                    return total;
                }

                public static double Unused() => 1;
            }

            public class Bench
            {
                private readonly IShape[] shapes = [new Circle(), new Circle(), new Circle(), new Square()];

                public void Run()
                {
                    for (var i = 0; i < 100; i++)
                        Work.Total(shapes);
                }

                public static void Fail() => throw new System.InvalidOperationException("No data");
            }
        }
        """);

    // Dynamic PGO sees mostly circles, so Tier1 checks for one and inlines its Area, with the interface call as the fallback.
    [Test]
    public async Task SymbolAsm_Workload_GivesTier1CodeWithDynamicPgo()
    {
        var listing = (await SymbolAsmAsync("Work.Total", Shapes, workload: "Bench.Run")).Should().ContainSingle().Subject.Asm;

        listing.Should().Contain("Pgo.Work:Total(Pgo.IShape[]):double (Tier1)").And.Contain("optimized using Dynamic PGO").And.Contain("Pgo.IShape:Area():double:this");
        (await SymbolAsmAsync("Work.Unused", Shapes, workload: "Bench.Run")).Should().ContainSingle()
            .Which.Asm.Should().Contain("; The workload did not run this method often enough for Tier1.").And.Contain("(Tier0)");
    }

    [Test]
    public async Task SymbolAsm_FailingWorkload_SaysWhy()
    {
        (await SymbolAsmAsync("Work.Total", Shapes, workload: "Bench.Fail")).Should().ContainSingle()
            .Which.Asm.Should().Contain("; The workload threw InvalidOperationException: No data");
        await FluentActions.Awaiting(() => SymbolAsmAsync("Work.Total", Shapes, workload: "Work.Total")).Should().ThrowAsync<ModelContextProtocol.McpException>()
            .WithMessage("The workload 'Work.Total' must be one method of the solution, without parameters*");
    }

    private static readonly Solution Layouts = TestSolution.Create("""
        namespace Lay
        {
            public struct Mixed { public byte A; public long B; public byte C; }
            public class Base { public int X; }
            public class Derived : Base { public byte Y; public Mixed M; public object? Ref; }
            public class Holder<T> { public bool Flag; public T Value = default!; public long Version; static Holder() => throw new System.Exception("Ran"); }
            public interface INothing { }
        }
        """);

    private Task<TypeLayout> LayOutAsync(string path, params string[] typeArguments) =>
        TypeLayoutTool.LayOutAsync(Layouts, assemblies, path, typeArguments, CancellationToken.None);

    private static string Describe(TypeLayout layout) => string.Join("\n", layout.Fields.Select(field => $"{field.Offset} {field.Size} {field.Name} {field.Type} {field.DeclaringType}".TrimEnd()));

    // Base types' fields come first; the runtime fills the gap after them with a small field, then puts references before structs.
    [Test]
    public async Task TypeLayout_Class_HasHeaderFieldsAndPadding()
    {
        var layout = await LayOutAsync("Derived");

        (layout.Type, layout.Size, layout.CacheLines, layout.Runtime).Should().Be(("Lay.Derived", 56, 1, RuntimeInformation.FrameworkDescription));
        Describe(layout).Should().Be("""
            -8 8 (object header)
            0 8 (method table)
            8 4 X System.Int32 Lay.Base
            12 1 Y System.Byte
            13 3 (padding)
            16 8 Ref System.Object
            24 24 M Lay.Mixed
            """.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task TypeLayout_Struct_KeepsItsOrder()
    {
        var layout = await LayOutAsync("Mixed");

        (layout.Size, Describe(layout)).Should().Be((24, """
            0 1 A System.Byte
            1 7 (padding)
            8 8 B System.Int64
            16 1 C System.Byte
            17 7 (padding)
            """.ReplaceLineEndings("\n")));
    }

    // The static constructor would throw; laying out does not run it.
    [Test]
    public async Task TypeLayout_Generic_UsesTheTypeArguments()
    {
        var layout = await LayOutAsync("Holder", "Mixed");

        layout.Type.Should().Be("Lay.Holder<Lay.Mixed>");
        layout.Fields.Select(field => (field.Name, field.Offset, field.CacheLine)).Should().Equal(
            ("(object header)", -8, 0), ("(method table)", 0, 0), ("Version", 8, 0), ("Flag", 16, 0), ("(padding)", 17, 0), ("Value", 24, 0));
        (await LayOutAsync("Holder")).Fields.Should().Contain(field => field.Name == "Value" && field.Type == "System.Object" && field.Offset == 8);
    }

    [Test]
    public async Task TypeLayout_OfAnInterface_IsRefused()
    {
        await FluentActions.Awaiting(() => LayOutAsync("INothing")).Should().ThrowAsync<ModelContextProtocol.McpException>().WithMessage("*interface*");
        await FluentActions.Awaiting(() => LayOutAsync("Holder", "int", "int")).Should().ThrowAsync<ModelContextProtocol.McpException>()
            .WithMessage("Lay.Holder<T> takes 1 type arguments, not 2.");
    }

    [Test]
    public async Task SymbolAsm_AsyncMethod_IncludesStateMachine()
    {
        (await SymbolAsmAsync("Calculator.AddLaterAsync")).Should().Contain(method => method.Method.EndsWith("::MoveNext()") && method.Asm.Contains(":MoveNext():this (FullOpts)"));
    }

    [Test]
    public async Task SymbolAsm_Constructor_IsListed()
    {
        (await SymbolAsmAsync("Calc.Calculator")).Should().Contain(method => method.Method.EndsWith("::.ctor()") && method.Asm.Contains("Calc.Calculator:.ctor():this"));
    }

    [Test]
    public async Task SymbolAsm_AbstractMethod_IsReported()
    {
        (await SymbolAsmAsync("Shape.Area")).Should().ContainSingle().Which.Asm.Should().Be("; Not compiled: The method is abstract.");
    }

    // The host has to load the referenced project, which is compiled from source as well.
    [Test]
    public async Task SymbolAsm_LoadsReferencedProjects()
    {
        var library = TestSolution.Create("namespace L { public static class Lib { public static int Twice(int x) => x * 2; } }").Projects.Single();
        var solution = library.Solution.AddProject("App", "App", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddProjectReference(new ProjectReference(library.Id))
            .AddDocument("App.cs", SourceText.From("public class App { public int Call(int x) => L.Lib.Twice(x) + 1; }", Encoding.UTF8))
            .Project.Solution;

        (await SymbolAsmAsync("App.Call", solution)).Should().ContainSingle().Which.Asm.Should().Contain("App:Call(int):int:this (FullOpts)");
    }

    // The test assembly serves as a file on disk; its dependencies come from its .deps.json. It is a debug build.
    [Test]
    public void AssemblyAsm_DisassemblesFileWithDependencies()
    {
        var assembly = assemblies.Read(typeof(AsmTests).Assembly.Location);

        JitDisassembler.Disassemble(assembly, ["TestSolution.Create"]).Should().ContainSingle()
            .Which.Asm.Should().Contain("Roslynaut.Tests.TestSolution:Create(System.String):Microsoft.CodeAnalysis.Solution (MinOpts)");
    }

    private static readonly string NetCoreDirectory = Path.Combine(Path.GetDirectoryName(DotnetCli.Executable)!, "shared", "Microsoft.NETCore.App");

    private static List<NuGet.Versioning.NuGetVersion> InstalledRuntimes() =>
        [.. Directory.EnumerateDirectories(NetCoreDirectory).Select(Path.GetFileName).Select(name => NuGet.Versioning.NuGetVersion.TryParse(name, out var version) ? version : null).OfType<NuGet.Versioning.NuGetVersion>()];

    [Test]
    public void RuntimeFor_PicksTheTargetsDotNet_OrTheNearestInstalled()
    {
        var installed = InstalledRuntimes();
        var path = typeof(AsmTests).Assembly.Location;

        JitDisassembler.RuntimeFor(path, Environment.Version.Major).NetCore.Should().Be(installed.Where(version => version.Major == Environment.Version.Major).Max()!);
        JitDisassembler.RuntimeFor(path, 1).NetCore.Major.Should().Be(installed.Min(version => version.Major), "the oldest newer one, as rolling forward picks");
        JitDisassembler.RuntimeFor(path, 99).NetCore.Should().Be(installed.Max()!);
        JitDisassembler.RuntimeFor(path, null).NetCore.Major.Should().Be(Environment.Version.Major, "as the server's, for .NET Standard");
    }

    // A framework assembly of an older .NET compiles on that very runtime, with its own framework.
    [Test]
    public void AssemblyAsm_OfOlderFramework_UsesItsRuntime()
    {
        var older = InstalledRuntimes().Where(version => version.Major < Environment.Version.Major && version.Major >= 8 && !version.IsPrerelease).Max();
        Assume.That(older, Is.Not.Null, "Needs an installed .NET from 8 on, older than the server's.");
        var linq = Path.Combine(NetCoreDirectory, older!.ToNormalizedString(), "System.Linq.dll");

        JitDisassembler.RuntimeFor(linq, older.Major).NetCore.Should().Be(older);
        JitDisassembler.Disassemble(assemblies.Read(linq), ["System.Linq.Enumerable.Count"]).Should().NotBeEmpty()
            .And.Contain(method => method.Asm.StartsWith($"; .NET {older.Major}.{older.Minor}.{older.Patch}") && method.Asm.Contains("(FullOpts)"));
    }

    // Framework assemblies are precompiled, and System.Private.CoreLib cannot be loaded twice.
    [Test]
    public void AssemblyAsm_DisassemblesCoreLib()
    {
        var assembly = assemblies.Read(typeof(object).Assembly.Location);

        JitDisassembler.Disassemble(assembly, ["System.Math.Max"]).Should().NotBeEmpty()
            .And.OnlyContain(method => method.Asm.Contains("; Assembly listing for method System.Math:Max(") && method.Asm.Contains("(FullOpts)"));
    }
}
