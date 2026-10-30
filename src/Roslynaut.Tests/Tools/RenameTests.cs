using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

public class RenameTests
{
    private static async Task<Solution> RenameAsync(Solution solution, string path, string newName, bool overloads = false) =>
        await RenameSymbolTool.RenameAsync(solution, await SymbolTree.ResolveAsync(solution, path, CancellationToken.None), newName, overloads, CancellationToken.None);

    private static async Task<string> TextOf(Solution solution) => (await solution.Projects.Single().Documents.Single().GetTextAsync()).ToString();

    [Test]
    public async Task Rename_UpdatesDeclarationAndReferences()
    {
        var solution = TestSolution.Create("public class Circle { public double Radius; public double Twice() => Radius * 2; }");

        (await TextOf(await RenameAsync(solution, "Circle.Radius", "Size"))).Should().Be("public class Circle { public double Size; public double Twice() => Size * 2; }");
    }

    [Test]
    public async Task Rename_Overloads_NeedsToBeAskedFor()
    {
        var solution = TestSolution.Create("public class C { void M() { } void M(int x) { } }");

        await FluentActions.Awaiting(() => RenameAsync(solution, "C.M", "N")).Should().ThrowAsync<McpException>().WithMessage("*matches 2 symbols*");
        (await TextOf(await RenameAsync(solution, "C.M", "N", overloads: true))).Should().Be("public class C { void N() { } void N(int x) { } }");
    }

    [Test]
    public async Task Rename_ToInvalidIdentifier_Throws()
    {
        var solution = TestSolution.Create("public class C { }");

        await FluentActions.Awaiting(() => RenameAsync(solution, "C", "not valid")).Should().ThrowAsync<McpException>();
    }

    [Test]
    public async Task WriteChanges_MovesFileOfRenamedType_AndKeepsByteOrderMark()
    {
        await InFileAsync("public class Widget { }", async (solution, directory, path) =>
        {
            var written = await WorkspaceService.WriteChangesAsync(solution, await RenameAsync(solution, "Widget", "Gadget"), CancellationToken.None);

            var gadget = Path.Combine(directory, "Gadget.cs");
            written.Should().Equal(gadget);
            File.Exists(path).Should().BeFalse();
            File.ReadAllBytes(gadget).Should().StartWith(new byte[] { 0xEF, 0xBB, 0xBF });
            File.ReadAllText(gadget).Should().Be("public class Gadget { }");
        });
    }

    // Roslyn formats with the platform's line ends, such as \r\n on Windows.
    [TestCase("\n", "\r\n")]
    [TestCase("\r\n", "\n")]
    public async Task WriteChanges_KeepsTheLineEndsOfTheFile(string fileLineEnd, string otherLineEnd)
    {
        await InFileAsync($"public class Widget{fileLineEnd}{{{fileLineEnd}}}{fileLineEnd}", async (solution, _, path) =>
        {
            var document = solution.Projects.Single().Documents.Single();
            var edited = solution.WithDocumentText(document.Id, Microsoft.CodeAnalysis.Text.SourceText.From(
                $"public class Widget{fileLineEnd}{{{otherLineEnd}    int x;{otherLineEnd}}}{fileLineEnd}"));

            (await WorkspaceService.WriteChangesAsync(solution, edited, CancellationToken.None)).Should().Equal(path);
            File.ReadAllText(path).Should().Be($"public class Widget{fileLineEnd}{{{fileLineEnd}    int x;{fileLineEnd}}}{fileLineEnd}");
        });
    }

    [Test]
    public async Task WriteChanges_NewFile_TakesTheLineEndsOfTheProject()
    {
        await InFileAsync("public class Widget\n{\n}\n", async (solution, directory, _) =>
        {
            var gadget = Path.Combine(directory, "Gadget.cs");
            var added = solution.Projects.Single().AddDocument("Gadget.cs", "public class Gadget\r\n{\r\n}\r\n", filePath: gadget).Project.Solution;

            (await WorkspaceService.WriteChangesAsync(solution, added, CancellationToken.None)).Should().Equal(gadget);
            File.ReadAllText(gadget).Should().Be("public class Gadget\n{\n}\n");
        });
    }

    // Roslyn's own option would rename the file after any symbol.
    [Test]
    public async Task WriteChanges_KeepsFile_WhenRenamingMember()
    {
        await InFileAsync("public class Widget { public void Spin() { } }", async (solution, _, path) =>
        {
            (await WorkspaceService.WriteChangesAsync(solution, await RenameAsync(solution, "Widget.Spin", "Turn"), CancellationToken.None)).Should().Equal(path);
            File.ReadAllText(path).Should().Be("public class Widget { public void Turn() { } }");
        });
    }

    /// <summary>Runs <paramref name="test"/> on a solution whose one document is a file Widget.cs, saved with a byte order mark.</summary>
    private static async Task InFileAsync(string code, Func<Solution, string, string, Task> test)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "Widget.cs");
            File.WriteAllText(path, code, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var solution = new AdhocWorkspace().AddProject("P", LanguageNames.CSharp)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                .AddDocument("Widget.cs", SourceText.From(File.ReadAllText(path), Encoding.Unicode), filePath: path)
                .Project.Solution;

            await test(solution, directory.FullName, path);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
