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
