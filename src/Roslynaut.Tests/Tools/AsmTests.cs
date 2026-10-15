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

                public T Echo<T>(T value) => value;

                public async Task<int> AddLaterAsync(int a)
                {
                    await Task.Yield();
                    return a + 1;
                }
            }
        }
        """);

    private readonly AssemblyLoader assemblies = new();

    [SetUp]
    public void RequireX86OrX64() =>
        Assume.That(RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86, "JitBuddy only disassembles x86 and x64 code.");

    private async Task<List<MethodAsm>> SymbolAsmAsync(string path, Solution? solution = null) =>
        (await SymbolIlTool.DisassembleAsync(solution ?? Calculator, assemblies, path, JitDisassembler.Disassemble, optimize: true, CancellationToken.None)).ToList();

    [Test]
    public async Task SymbolAsm_Method_ReturnsOptimizedNativeCode()
    {
        var method = (await SymbolAsmAsync("Calculator.Add")).Should().ContainSingle().Subject;

        method.Method.Should().Be("System.Int32 Calc.Calculator::Add(System.Int32,System.Int32)");
        method.Asm.Should().StartWith($"; {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}\n").And.Contain("ret");

        // Unoptimized (tier 0) code sets up an rbp frame and spills the arguments to it.
        method.Asm.Should().NotContain("rbp");
    }

    [Test]
    public async Task SymbolAsm_MethodOfGenericType_IsInstantiatedOverInt32()
    {
        var solution = TestSolution.Create("public class Box<T> { public T Id(T value) => value; }");

        (await SymbolAsmAsync("Box.Id", solution)).Should().ContainSingle().Which.Asm.Should().Contain("type arguments System.Int32").And.Contain("ret");
    }

    // The limits of JitBuddy are reported, not returned as empty listings.
    [Test]
    public async Task SymbolAsm_GenericMethod_IsReportedAsUnsupported()
    {
        (await SymbolAsmAsync("Calculator.Echo")).Should().ContainSingle().Which.Asm.Should().Contain("does not support generic methods");
    }

    [Test]
    public async Task SymbolAsm_AsyncMethod_DisassemblesStub_AndReportsStructStateMachine()
    {
        var methods = await SymbolAsmAsync("Calculator.AddLaterAsync");

        methods.Should().Contain(method => method.Method.EndsWith("::AddLaterAsync(System.Int32)") && method.Asm.Contains("call"))
            .And.Contain(method => method.Method.EndsWith("::MoveNext()") && method.Asm.Contains("virtual methods of value types"));
    }

    [Test]
    public async Task SymbolAsm_Constructor_IsReportedAsUnsupported()
    {
        (await SymbolAsmAsync("Calc.Calculator")).Should().Contain(method => method.Method.EndsWith("::.ctor()") && method.Asm.Contains("not constructors"));
    }

    // The JIT has to load the referenced project, which is compiled from source as well.
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

        (await SymbolAsmAsync("App.Call", solution)).Should().ContainSingle().Which.Asm.Should().NotContain("Not disassembled").And.Contain("ret");
    }

    // The test assembly serves as a file on disk; its dependencies come from its .deps.json.
    [Test]
    public void AssemblyAsm_DisassemblesFileWithDependencies()
    {
        var assembly = assemblies.Read(typeof(AsmTests).Assembly.Location);

        JitDisassembler.Disassemble(assembly, ["TestSolution.Create"]).Should().ContainSingle()
            .Which.Asm.Should().Contain("unoptimized because the assembly is a debug build").And.Contain("call").And.NotContain("Not disassembled");
    }
}
