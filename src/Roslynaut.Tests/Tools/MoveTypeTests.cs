using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>Moving a type to its own file is a refactoring that code_actions offers; the new file has to land next to the old one.</summary>
public class MoveTypeTests
{
    [Test]
    public async Task MoveTypeToFile_WritesNewFileNextToTheOld()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(directory.FullName, "Shapes")).FullName;
            var path = Path.Combine(folder, "Circle.cs");
            const string code = "namespace P.Shapes;\n\npublic class Circle { }\n\npublic class Square { }\n";
            File.WriteAllText(path, code);
            var solution = new AdhocWorkspace().AddProject(ProjectInfo.Create(
                    ProjectId.CreateNewId(), VersionStamp.Create(), "P", "P", LanguageNames.CSharp, filePath: Path.Combine(directory.FullName, "P.csproj"),
                    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                    metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
                .AddDocument("Circle.cs", SourceText.From(code, Encoding.UTF8), folders: ["Shapes"], filePath: path)
                .Project.Solution;
            var document = solution.Projects.Single().Documents.Single();
            var codeActions = new CodeActionService();

            var actions = await codeActions.FindAsync(document, new TextSpan(code.IndexOf("Square", StringComparison.Ordinal), 0), CancellationToken.None);
            var move = actions.Should().ContainSingle(action => action.Title == "Move type to Square.cs").Subject;
            var (_, changed) = await codeActions.ApplyAsync(solution, move.Id, CancellationToken.None);
            var written = await WorkspaceService.WriteChangesAsync(solution, changed, CancellationToken.None);

            var square = Path.Combine(folder, "Square.cs");
            written.Should().BeEquivalentTo(path, square);
            File.ReadAllText(square).Should().Contain("public class Square");
            File.ReadAllText(path).Should().NotContain("Square");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // A real project, loaded through Buildalyzer, which leaves folders and the default namespace out.
    [Test]
    public async Task ChangeNamespace_MatchesFolder()
    {
        var directory = Directory.CreateTempSubdirectory("roslynaut-namespace-");
        try
        {
            var project = Path.Combine(directory.FullName, "Sample.csproj");
            File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
            var models = Directory.CreateDirectory(Path.Combine(directory.FullName, "Models")).FullName;
            File.WriteAllText(Path.Combine(models, "Item.cs"), "namespace Wrong;\n\npublic class Item { }\n");
            var document = await new WorkspaceService().GetDocumentAsync(project, Path.Combine(models, "Item.cs"));

            var actions = await new CodeActionService().FindAsync(document, new TextSpan("namespace ".Length, 0), CancellationToken.None);

            document.Folders.Should().Equal("Models");
            actions.Select(action => action.Title).Should().Contain("Change namespace to 'Sample.Models'");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
