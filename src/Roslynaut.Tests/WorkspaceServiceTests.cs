using Microsoft.CodeAnalysis;

namespace Roslynaut.Tests;

[NonParallelizable]
public class WorkspaceServiceTests
{
    // Real builds, loaded through Buildalyzer: it builds a project with TargetFrameworks as the outer build, which has no Compile target,
    // keeps few compiler options, and references projects that only serve as analyzers.
    [Test]
    public async Task Load_ImportedTargetFrameworks_TakesTheCompilerCommandLine()
    {
        var directory = Directory.CreateTempSubdirectory("roslynaut-load-");
        try
        {
            void Write(string path, string text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory.FullName, path))!);
                File.WriteAllText(Path.Combine(directory.FullName, path), text);
            }

            Write("Directory.Build.props", """
                <Project>
                  <PropertyGroup><TargetFrameworks>net10.0</TargetFrameworks><AllowUnsafeBlocks>true</AllowUnsafeBlocks></PropertyGroup>
                </Project>
                """);
            Write("Generator/Generator.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
            Write("Generator/Hidden.cs", "public class Hidden { }");
            Write("App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup><ProjectReference Include="../Generator/Generator.csproj" ReferenceOutputAssembly="false" /></ItemGroup>
                </Project>
                """);
            Write("App/Pointer.cs", "public unsafe class Pointer { public int* Address; }");

            var solution = (await new WorkspaceService().GetAsync(Path.Combine(directory.FullName, "App", "App.csproj"))).Solution;
            var app = solution.Projects.Single(project => project.Name == "App");
            var compilation = (await app.GetCompilationAsync())!;

            app.Documents.Select(document => document.Name).Should().Contain("Pointer.cs");
            app.ProjectReferences.Should().BeEmpty();
            compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

[NonParallelizable]
public class WorkspaceLoadErrorTests
{
    // A project whose design-time build fails before the compiler loads with no sources; its errors say why.
    [Test]
    public async Task Load_FailingProject_ReportsItsErrors()
    {
        var directory = Directory.CreateTempSubdirectory("roslynaut-broken-");
        try
        {
            var project = Path.Combine(directory.FullName, "Broken.csproj");
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <Target Name="Break" BeforeTargets="CoreCompile"><Error Code="BRK001" Text="Broken on purpose." /></Target>
                </Project>
                """);
            var file = Path.Combine(directory.FullName, "Code.cs");
            File.WriteAllText(file, "class Code { }");

            var service = new WorkspaceService();
            var loaded = await service.GetAsync(project);

            Roslynaut.Tools.ProjectInfoTool.Summarize(loaded).Should().ContainSingle().Which.LoadErrors.Should().Equal("BRK001: Broken on purpose.");
            await service.Awaiting(service => service.GetDocumentAsync(project, file))
                .Should().ThrowAsync<ModelContextProtocol.McpException>().WithMessage("*did not load*Broken on purpose*");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
