using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

public class IlTests
{
    private static readonly Solution Calculator = TestSolution.Create("""
        using System;
        using System.Threading.Tasks;

        namespace Calc
        {
            public class Calculator
            {
                public int Total { get; set; }

                public int Add(int a, int b) => a + b;
                public double Add(double a, double b) => a + b;

                public T Echo<T>(T value) => value;

                public async Task<int> AddLaterAsync(int a)
                {
                    await Task.Yield();
                    return a + 1;
                }

                public Func<int, int> Adder(int a) => b => a + b;

                public int Safe(int a)
                {
                    try { return checked(a * a); }
                    catch (OverflowException) { return 0; }
                }

                public class Nested { public void Run() { } }
            }
        }
        """);

    private readonly AssemblyLoader assemblies = new();

    private async Task<List<MethodIL>> SymbolIlAsync(string path, Solution? solution = null) =>
        (await SymbolIlTool.DisassembleAsync(solution ?? Calculator, assemblies, path, CancellationToken.None)).ToList();

    [Test]
    public async Task SymbolIl_Method_ReturnsInstructionsWithSourceLines()
    {
        var method = (await SymbolIlAsync("Calculator.Add(int, int)")).Should().ContainSingle().Subject;

        method.Method.Should().Be("System.Int32 Calc.Calculator::Add(System.Int32,System.Int32)");
        method.IL.Should().MatchRegex(@"// .*:10\nIL_\w{4}: ldarg\.1\nIL_\w{4}: ldarg\.2\nIL_\w{4}: add\nIL_\w{4}: ret");
    }

    [Test]
    public async Task SymbolIl_OverloadedName_ReturnsAllOverloads()
    {
        (await SymbolIlAsync("Calculator.Add")).Select(method => method.Method).Should().BeEquivalentTo(
            "System.Int32 Calc.Calculator::Add(System.Int32,System.Int32)",
            "System.Double Calc.Calculator::Add(System.Double,System.Double)");
    }

    // Roslyn and Cecil must agree on documentation IDs, which encode generics differently from names.
    [Test]
    public async Task SymbolIl_GenericMethod_IsFound()
    {
        (await SymbolIlAsync("Calculator.Echo")).Should().ContainSingle().Which.Method.Should().Be("T Calc.Calculator::Echo(T)");
    }

    [Test]
    public async Task SymbolIl_AsyncMethod_IncludesStateMachine()
    {
        (await SymbolIlAsync("Calculator.AddLaterAsync")).Select(method => method.Method)
            .Should().Contain(name => name.Contains("/<AddLaterAsync>d__") && name.EndsWith("::MoveNext()"));
    }

    [Test]
    public async Task SymbolIl_Lambda_IncludesClosure()
    {
        (await SymbolIlAsync("Calculator.Adder")).Select(method => method.Method).Should().Contain(name => name.Contains("::<Adder>b__"));
    }

    [Test]
    public async Task SymbolIl_Property_ReturnsAccessors()
    {
        (await SymbolIlAsync("Calculator.Total")).Select(method => method.Method).Should().BeEquivalentTo(
            "System.Int32 Calc.Calculator::get_Total()",
            "System.Void Calc.Calculator::set_Total(System.Int32)");
    }

    [Test]
    public async Task SymbolIl_TryCatch_ListsExceptionHandler()
    {
        (await SymbolIlAsync("Calculator.Safe")).Should().ContainSingle().Which.IL
            .Should().MatchRegex(@"\.try IL_\w{4} to IL_\w{4} catch System\.OverflowException handler IL_\w{4} to IL_\w{4}");
    }

    [Test]
    public async Task SymbolIl_Type_ReturnsItsMethodsAndHelpers_ButNotNestedTypes()
    {
        var names = (await SymbolIlAsync("Calc.Calculator")).Select(method => method.Method).ToList();

        names.Should().Contain("System.Void Calc.Calculator::.ctor()")
            .And.Contain(name => name.Contains("<AddLaterAsync>d__"))
            .And.NotContain(name => name.Contains("Nested"));
    }

    [Test]
    public async Task SymbolIl_NestedType_IsFound()
    {
        (await SymbolIlAsync("Calculator.Nested")).Select(method => method.Method).Should().BeEquivalentTo(
            "System.Void Calc.Calculator/Nested::Run()",
            "System.Void Calc.Calculator/Nested::.ctor()");
    }

    // Top-level statements compile into a method with a compiler's name, which names leave out but documentation IDs reach.
    [Test]
    public async Task SymbolIl_TopLevelStatements_AreTheEntryPoint()
    {
        var program = TestSolution.Create("System.Func<int> one = () => 1;\nreturn one();\n").Projects.Single()
            .WithCompilationOptions(new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.ConsoleApplication)).Solution;

        (await SymbolIlAsync("Program.<Main>$", program)).Select(method => method.Method)
            .Should().Contain("System.Int32 Program::<Main>$(System.String[])").And.Contain(name => name.Contains("::<<Main>$>b__"));
    }

    [Test]
    public async Task SymbolIl_SymbolWithoutIL_Throws()
    {
        await this.Awaiting(test => test.SymbolIlAsync("Calculator.Add.a")).Should().ThrowAsync<McpException>().WithMessage("*has no IL*");
    }

    [Test]
    public async Task SymbolIl_CompileErrors_Throw()
    {
        var broken = TestSolution.Create("""class A { void M() { int x = "s"; } }""");

        await this.Awaiting(test => test.SymbolIlAsync("A.M", broken)).Should().ThrowAsync<McpException>().WithMessage("*does not compile*CS0029*");
    }

    // The test assembly itself serves as a dll on disk, with a PDB next to it.
    [TestCase("Roslynaut.Tests.TestSolution.Create")]
    [TestCase("TestSolution.Create")]
    [TestCase("testsolution.create(string code)")]
    [TestCase("TestSolution::Create")]
    [TestCase("M:Roslynaut.Tests.TestSolution.Create(System.String)")]
    public void AssemblyIl_FindsMethodByNameOrDocumentationId(string name)
    {
        var module = assemblies.Read(typeof(IlTests).Assembly.Location).Module;

        IlDisassembler.Disassemble(module, [name]).Should().ContainSingle().Which.IL.Should().MatchRegex(@"// TestSolution\.cs:\d+");
    }

    [Test]
    public void AssemblyIl_UnknownName_Throws()
    {
        var module = assemblies.Read(typeof(IlTests).Assembly.Location).Module;

        FluentActions.Invoking(() => IlDisassembler.Disassemble(module, ["NoSuchType"])).Should().Throw<McpException>();
    }

    [Test]
    public void AssemblyLoader_DoesNotLockFiles_AndRereadsThemOnceChanged()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var dll = Path.Combine(directory.FullName, "Copy.dll");
            File.Copy(typeof(IlTests).Assembly.Location, dll);

            var first = assemblies.Read(dll);
            assemblies.Read(dll).Should().BeSameAs(first);

            File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(1));
            assemblies.Read(dll).Should().NotBeSameAs(first);

            File.Delete(dll);
            File.Exists(dll).Should().BeFalse();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public void AssemblyLoader_WhenFull_EvictsLeastRecentlyUsedFile()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var (a, b, c) = (Copy("A"), Copy("B"), Copy("C"));
            var loader = new AssemblyLoader(capacity: 2);
            var (moduleA, moduleB) = (loader.Read(a), loader.Read(b));
            loader.Read(a);
            loader.Read(c);

            loader.Read(a).Should().BeSameAs(moduleA);
            loader.Read(b).Should().NotBeSameAs(moduleB);
        }
        finally
        {
            directory.Delete(recursive: true);
        }

        string Copy(string name)
        {
            var path = Path.Combine(directory.FullName, $"{name}.dll");
            File.Copy(typeof(IlTests).Assembly.Location, path);
            return path;
        }
    }

    [Test]
    public void AssemblyLoader_WhenFull_KeepsFilesIfReadingAnotherFails()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var (dll, broken) = (Path.Combine(directory.FullName, "A.dll"), Path.Combine(directory.FullName, "Broken.dll"));
            File.Copy(typeof(IlTests).Assembly.Location, dll);
            File.WriteAllText(broken, "not a dll");
            var loader = new AssemblyLoader(capacity: 1);
            var module = loader.Read(dll);

            loader.Invoking(l => l.Read(broken)).Should().Throw<McpException>();
            loader.Read(dll).Should().BeSameAs(module);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public void AssemblyLoader_NotAnAssembly_Throws()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "not a dll");
            assemblies.Invoking(loader => loader.Read(file)).Should().Throw<McpException>().WithMessage("Not a .NET assembly*");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
