using ModelContextProtocol;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

public class DecompileTests
{
    private readonly AssemblyLoader assemblies = new();

    // The test assembly serves as a file on disk.
    private LoadedAssembly TestAssembly => assemblies.Read(typeof(DecompileTests).Assembly.Location);

    [Test]
    public void Decompile_Type_ReturnsItWhole()
    {
        var type = DecompileTool.Decompile(TestAssembly, ["Roslynaut.Tests.TestSolution"]).Should().ContainSingle().Subject;

        type.Member.Should().Be("Roslynaut.Tests.TestSolution");
        type.Code.Should().Contain("internal static class TestSolution").And.Contain("public static Solution Create(string code)").And.Contain("PathOf");
    }

    [Test]
    public void Decompile_Method_ReturnsOnlyIt()
    {
        DecompileTool.Decompile(TestAssembly, ["TestSolution.PathOf"]).Should().ContainSingle()
            .Which.Code.Should().Contain("public static string PathOf(SymbolEntry entry)").And.NotContain("Create(");
    }

    [Test]
    public void Decompile_LeavesReferencedAssembliesUnlocked()
    {
        // Copies, so the reference resolves next to the assembly; PathOf takes a SymbolEntry of Roslynaut.dll.
        var directory = Directory.CreateTempSubdirectory("roslynaut-decompile-");
        foreach (var assembly in new[] { typeof(DecompileTests).Assembly, typeof(SymbolEntry).Assembly })
            File.Copy(assembly.Location, Path.Combine(directory.FullName, Path.GetFileName(assembly.Location)));

        DecompileTool.Decompile(assemblies.Read(Path.Combine(directory.FullName, "Roslynaut.Tests.dll")), ["TestSolution.PathOf"]).Should().ContainSingle()
            .Which.Code.Should().Contain("SymbolEntry entry");

        // A build that writes the referenced project's output again must be able to.
        File.WriteAllBytes(Path.Combine(directory.FullName, "Roslynaut.dll"), []);
        directory.Delete(recursive: true);
    }

    [Test]
    public void Decompile_Lowered_ShowsClosuresAndStateMachines()
    {
        var capture = DecompileTool.Decompile(TestAssembly, ["LoweringSample.Capture"], lowered: true);
        capture.Select(member => member.Member).Should().Contain(member => member.Contains("<>c__DisplayClass"));
        capture[0].Code.Should().Contain("<>c__DisplayClass").And.NotContain("=> total");

        var wait = DecompileTool.Decompile(TestAssembly, ["LoweringSample.WaitAsync"], lowered: true);
        wait[0].Code.Should().NotContain("await").And.Contain("<WaitAsync>d__");
        wait.Should().Contain(member => member.Member.Contains("<WaitAsync>d__") && member.Code.Contains("MoveNext"));

        DecompileTool.Decompile(TestAssembly, ["LoweringSample.WaitAsync"]).Should().ContainSingle().Which.Code.Should().Contain("await Task.Yield()");
    }

    // The IL and native code tools take properties too, by their accessors.
    [Test]
    public void FindMethods_OfProperty_AreItsAccessors()
    {
        var module = assemblies.Read(typeof(SymbolEntry).Assembly.Location).Module;

        IlDisassembler.FindMethods(module, ["Roslynaut.Tools.SymbolEntry.Name"]).Select(method => method.Name).Should().Equal("get_Name", "set_Name");
    }

    // Names as reflection, stack traces and Cecil write them, with generic arities and nested type separators.
    [Test]
    public void FindMembers_GenericAndNestedNames_TakeArityAndSeparators()
    {
        var module = assemblies.Read(typeof(object).Assembly.Location).Module;

        IlDisassembler.FindMembers(module, ["System.Runtime.CompilerServices.ConditionalWeakTable`2+Enumerator.MoveNext", "List`1.Add", "Dictionary`2/Entry"])
            .Select(member => member.FullName).Should().BeEquivalentTo(
                "System.Boolean System.Runtime.CompilerServices.ConditionalWeakTable`2/Enumerator::MoveNext()",
                "System.Void System.Collections.Generic.List`1::Add(T)",
                "System.Collections.Generic.Dictionary`2/Entry");
    }

    [Test]
    public void FindMembers_Arity_MatchesWhereGiven()
    {
        var module = assemblies.Read(typeof(object).Assembly.Location).Module;
        List<string> Find(string name) => [.. IlDisassembler.FindMembers(module, [name]).Select(member => member.FullName)];

        Find("System.Action`2").Should().Equal("System.Action`2");
        Find("System.Action<T1, T2, T3>").Should().Equal("System.Action`3");
        Find("System.Func<,>").Should().Equal("System.Func`2");
        Find("System.Action").Should().Contain(["System.Action", "System.Action`1", "System.Action`2"]);
        Find("Interlocked.CompareExchange<T>").Should().ContainSingle().Which.Should().StartWith("T System.Threading.Interlocked::CompareExchange");
    }

    [Test]
    public void FindMembers_OfForwardedType_NamesTheAssemblyThatDefinesIt()
    {
        var facade = assemblies.Read(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Collections.dll")).Module;

        FluentActions.Invoking(() => IlDisassembler.FindMembers(facade, ["System.Collections.Generic.CollectionExtensions.Remove"]))
            .Should().Throw<McpException>().WithMessage("*CollectionExtensions is forwarded to System.Private.CoreLib*");
    }

    [Test]
    public async Task FindFile_OfReferencedAssembly_IsItsFile()
    {
        var solution = TestSolution.Create("class C { System.Text.StringBuilder builder; }");
        var compilation = (await solution.Projects.Single().GetCompilationAsync())!;
        var assembly = compilation.GetTypeByMetadataName("System.Text.StringBuilder")!.ContainingAssembly;

        (await AssemblyLoader.FindFileAsync(solution, assembly, CancellationToken.None)).Should().Be(typeof(object).Assembly.Location);
    }
}

/// <summary>For lowered decompilation: a lambda that captures a local, and an async method.</summary>
internal static class LoweringSample
{
    public static Func<int> Capture(int seed)
    {
        var total = seed;
        return () => total + 1;
    }

    public static async Task<int> WaitAsync()
    {
        await Task.Yield();
        return 1;
    }
}
