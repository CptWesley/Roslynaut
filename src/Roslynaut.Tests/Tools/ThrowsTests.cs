using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>throws: what a member throws itself, through its callees, and what referenced methods document; less what it catches.</summary>
public class ThrowsTests
{
    private const string Code = """
        using System;
        public class Orders
        {
            public void Submit(string id)
            {
                if (id.Length == 0) throw new ArgumentException("empty");
                Save(id);
                try { Save(id); } catch (InvalidOperationException) { }
                int.Parse(id);
            }
            private void Save(string id) { if (id == "x") throw new InvalidOperationException(); }
            public void Retry() { try { Save(""); } catch (Exception) { throw; } }
        }
        """;

    private static async Task<IReadOnlyList<ThrowsTool.ThrownException>> ThrowsAsync(Solution solution, string member, int depth = 2) =>
        await ThrowsTool.FindAsync(solution, (await SymbolTree.ResolveAsync(solution, member, CancellationToken.None))[0], depth, CancellationToken.None);

    [Test]
    public async Task Throws_OwnAndCallees_LessWhatIsCaught()
    {
        var thrown = await ThrowsAsync(TestSolution.Create(Code), "Orders.Submit");

        thrown.Select(exception => (exception.Type, exception.Site.Line, string.Join(" > ", exception.Chain))).Should().BeEquivalentTo(
            [("System.ArgumentException", 6, ""), ("System.InvalidOperationException", 7, "Orders.Save")]);
        (await ThrowsAsync(TestSolution.Create(Code), "Orders.Submit", depth: 0)).Should().ContainSingle();
    }

    [Test]
    public async Task Rethrow_ThrowsWhatItsCatchCaught()
    {
        (await ThrowsAsync(TestSolution.Create(Code), "Orders.Retry")).Select(exception => exception.Type).Should().Equal("System.Exception");
    }

    // The reference assemblies of the targeting pack come with their documentation.
    [Test]
    public async Task Documented_ExceptionsOfReferencedMethods()
    {
        var packs = Path.Combine(Path.GetDirectoryName(DotnetCli.Executable)!, "packs", "Microsoft.NETCore.App.Ref");
        var runtime = Directory.Exists(packs)
            ? Directory.EnumerateFiles(packs, "System.Runtime.dll", SearchOption.AllDirectories).FirstOrDefault(file => File.Exists(Path.ChangeExtension(file, ".xml")))
            : null;
        Assume.That(runtime, Is.Not.Null, "Needs a targeting pack with documentation.");
        var solution = new AdhocWorkspace().AddProject("Test", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(MetadataReference.CreateFromFile(runtime!, documentation: XmlDocumentationProvider.CreateFromFile(Path.ChangeExtension(runtime!, ".xml"))))
            .AddDocument("Test.cs", SourceText.From(Code, Encoding.UTF8)).Project.Solution;

        (await ThrowsAsync(solution, "Orders.Submit")).Where(exception => exception.Documented).Should().Contain(exception =>
            exception.Type == "System.FormatException" && exception.Site.Line == 9 && exception.Chain.Single() == "int.Parse");
    }
}
