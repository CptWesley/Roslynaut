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

[NonParallelizable]
public class WorkspaceOptionsTests
{
    // Code under #if for another framework or configuration only exists once the workspace loads with it.
    [Test]
    public async Task Configure_LoadsTheChosenFrameworkAndConfiguration_AndBack()
    {
        var directory = Directory.CreateTempSubdirectory("roslynaut-tfm-");
        try
        {
            var project = Path.Combine(directory.FullName, "Multi.csproj");
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(directory.FullName, "Code.cs"), """
                #if NET10_0_OR_GREATER
                public class Modern { }
                #else
                public class Legacy { }
                #endif
                #if DEBUG
                public class DebugOnly { }
                #endif
                """);

            var service = new WorkspaceService();
            async Task<string[]> TypesAsync(LoadedWorkspace loaded) =>
                [.. (await loaded.Solution.Projects.Single().GetCompilationAsync())!.Assembly.TypeNames.Where(name => name != "<Module>").Order()];

            (await TypesAsync(await service.GetAsync(project))).Should().Equal("DebugOnly", "Legacy");

            var loaded = await service.ConfigureAsync(project, new("net10.0", "Release"));
            (await TypesAsync(loaded)).Should().Equal("Modern");
            Roslynaut.Tools.WorkspaceOptionsTool.Describe(loaded).Should().ContainSingle().Which.Should().Match<Roslynaut.Tools.WorkspaceOptionsTool.LoadedProject>(
                loaded => loaded.TargetFramework == "net10.0" && loaded.TargetFrameworks.SequenceEqual(new[] { "netstandard2.0", "net10.0" }) && loaded.Configuration == "Release");
            (await TypesAsync(await service.GetAsync(project))).Should().Equal("Modern");

            (await TypesAsync(await service.ConfigureAsync(project, new()))).Should().Equal("DebugOnly", "Legacy");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
