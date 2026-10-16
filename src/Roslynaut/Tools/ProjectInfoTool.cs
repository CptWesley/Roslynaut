using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class ProjectInfoTool(WorkspaceService workspace) : IMcpTool
{
    private static readonly string[] Properties = ["AssemblyName", "OutputType", "LangVersion", "Nullable", "TargetPath", "IsTestProject", "TargetFrameworks", "Configuration"];

    public Delegate Handler => GetProjectInfoAsync;

    [McpServerTool(Name = "project_info", ReadOnly = true)]
    [Description("Returns MSBuild information per project: target framework, key properties, project and package references; and for a "
        + "project that did not load, the MSBuild errors that explain why, such as a restore that did not run.")]
    private async Task<IEnumerable<ProjectSummary>> GetProjectInfoAsync([Description(ToolDefaults.ProjectPath)] string projectPath) =>
        Summarize(await workspace.GetAsync(projectPath));

    internal static IEnumerable<ProjectSummary> Summarize(LoadedWorkspace loaded) =>
        loaded.Results.Select(result => new ProjectSummary(
            result.ProjectFilePath,
            result.Succeeded,
            result.TargetFramework,
            Properties.ToDictionary(name => name, name => (string?)result.GetProperty(name)),
            result.ProjectReferences,
            result.PackageReferences.ToDictionary(package => package.Key, package => package.Value.GetValueOrDefault("Version")),
            result.SourceFiles.Length,
            loaded.LoadErrors.GetValueOrDefault(result.ProjectFilePath)));

    /// <param name="LoadErrors">For a project that did not load, and so has no sources, the MSBuild errors of its design-time build.</param>
    public sealed record ProjectSummary(
        string ProjectPath,
        bool BuildSucceeded,
        string TargetFramework,
        Dictionary<string, string?> Properties,
        IEnumerable<string> ProjectReferences,
        Dictionary<string, string?> PackageReferences,
        int SourceFileCount,
        IReadOnlyList<string>? LoadErrors);
}
