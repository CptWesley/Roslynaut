using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class ProjectInfoTool(WorkspaceService workspace) : IMcpTool
{
    private static readonly string[] Properties = ["AssemblyName", "OutputType", "LangVersion", "Nullable", "TargetPath", "IsTestProject"];

    public Delegate Handler => GetProjectInfoAsync;

    [McpServerTool(Name = "project_info", ReadOnly = true)]
    [Description("Returns MSBuild information per project: target framework, key properties, project and package references.")]
    private async Task<IEnumerable<ProjectSummary>> GetProjectInfoAsync([Description(ToolDefaults.ProjectPath)] string projectPath) =>
        (await workspace.GetAsync(projectPath)).Results.Select(result => new ProjectSummary(
            result.ProjectFilePath,
            result.Succeeded,
            result.TargetFramework,
            Properties.ToDictionary(name => name, name => (string?)result.GetProperty(name)),
            result.ProjectReferences,
            result.PackageReferences.ToDictionary(package => package.Key, package => package.Value.GetValueOrDefault("Version")),
            result.SourceFiles.Length));

    public sealed record ProjectSummary(
        string ProjectPath,
        bool BuildSucceeded,
        string TargetFramework,
        Dictionary<string, string?> Properties,
        IEnumerable<string> ProjectReferences,
        Dictionary<string, string?> PackageReferences,
        int SourceFileCount);
}
