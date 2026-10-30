using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>change_signature and extract_interface, which run Roslyn's refactorings with options built by reflection.</summary>
public class DialogRefactoringTests
{
    private const string Greeter = """
        namespace App
        {
            public interface IGreeter { string Greet(string name, int times); }
            public class Greeter : IGreeter
            {
                public string Greet(string name, int times) => name + times;
            }
            public static class Use
            {
                public static string Run(IGreeter greeter) => greeter.Greet("a", 2) + new Greeter().Greet(times: 3, name: "b");
            }
        }
        """;

    private static async Task<string> TextAfterChangeAsync(params ParameterSpec[] parameters)
    {
        var solution = TestSolution.Create(Greeter);
        var greet = (await SymbolTree.ResolveAsync(solution, "App.Greeter.Greet", CancellationToken.None)).Single();
        var changed = await DialogRefactorings.ChangeSignatureAsync(solution, greet, parameters, CancellationToken.None);
        return (await changed.Projects.Single().Documents.Single().GetTextAsync()).ToString();
    }

    [Test]
    public async Task ChangeSignature_ReordersAndAdds_EverywhereItIsUsed()
    {
        var text = await TextAfterChangeAsync(new("times"), new("name"), new("loud", "bool", "false"));

        text.Should().Contain("string Greet(int times, string name, bool loud);")
            .And.Contain("public string Greet(int times, string name, bool loud) =>")
            .And.Contain("greeter.Greet(2, \"a\", false)")
            .And.Contain("Greet(times: 3, name: \"b\", loud: false)");
    }

    [Test]
    public async Task ChangeSignature_RemovesAndAddsOptional()
    {
        var text = await TextAfterChangeAsync(new("name"), new("suffix", "string", DefaultValue: "\"!\""));

        text.Should().Contain("string Greet(string name, string suffix = \"!\");").And.Contain("greeter.Greet(\"a\")");
    }

    [Test]
    public async Task ChangeSignature_RejectsUnknownParameterWithoutType()
    {
        await FluentActions.Awaiting(() => TextAfterChangeAsync(new("name"), new("other")))
            .Should().ThrowAsync<McpException>().WithMessage("*not a parameter*give it a Type*");
    }

    [TestCase(null, "int Count { get; set; }", "void Add(string item);")]
    [TestCase(new[] { "Add" }, "void Add(string item);", null)]
    public async Task ExtractInterface_DeclaresItAndImplementsIt(string[]? members, string included, string? alsoIncluded)
    {
        var solution = TestSolution.Create("""
            namespace App
            {
                public class Store
                {
                    public int Count { get; set; }
                    public void Add(string item) { }
                    public static void Helper() { }
                    private void Hidden() { }
                }
            }
            """);
        var store = (INamedTypeSymbol)(await SymbolTree.ResolveAsync(solution, "App.Store", CancellationToken.None)).Single();

        var changed = await DialogRefactorings.ExtractInterfaceAsync(solution, store, null, members, sameFile: false, CancellationToken.None);

        var documents = changed.Projects.Single().Documents.ToList();
        var declared = (await documents.Single(document => document.Name == "IStore.cs").GetTextAsync()).ToString();
        declared.Should().Contain("public interface IStore").And.Contain(included).And.NotContain("Helper").And.NotContain("Hidden");
        if (alsoIncluded is not null)
            declared.Should().Contain(alsoIncluded);
        else
            declared.Should().NotContain("Count");
        (await documents.Single(document => document.Name != "IStore.cs").GetTextAsync()).ToString().Should().Contain("public class Store : IStore");
    }
}
