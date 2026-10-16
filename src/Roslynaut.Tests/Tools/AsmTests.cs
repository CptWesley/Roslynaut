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

    private async Task<List<MethodAsm>> SymbolAsmAsync(string path, Solution? solution = null) =>
        (await SymbolIlTool.DisassembleAsync(solution ?? Calculator, assemblies, path, JitDisassembler.Disassemble, optimize: true, CancellationToken.None)).ToList();

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
