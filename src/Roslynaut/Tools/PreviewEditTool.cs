using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed record TextEdit(
    [property: Description("The C# file to edit.")] string FilePath,
    [property: Description("Text that occurs exactly once in the file.")] string OldText,
    [property: Description("The text to put in its place.")] string NewText);

public sealed class PreviewEditTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => PreviewEditAsync;

    [McpServerTool(Name = "preview_edit", ReadOnly = true)]
    [Description("Shows what edits would break before making them: applies text replacements in memory only, and returns the errors and "
        + "warnings they would introduce and resolve, in the edited projects and in the projects that depend on them. Nothing is written.")]
    private async Task<Preview> PreviewEditAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The replacements, applied in order: in the file at filePath, oldText becomes newText.")] TextEdit[] edits,
        [Description("Whether to run the project's analyzers too; slower.")] bool analyzers = false,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        return await PreviewAsync(solution, edits, analyzers, cancellationToken);
    }

    internal static async Task<Preview> PreviewAsync(Solution solution, IReadOnlyList<TextEdit> edits, bool analyzers, CancellationToken cancellationToken)
    {
        var edited = solution;
        var projects = new HashSet<ProjectId>();
        foreach (var edit in edits)
        {
            var document = WorkspaceService.GetDocument(edited, edit.FilePath);
            var text = await document.GetTextAsync(cancellationToken);
            var content = text.ToString();
            var index = edit.OldText.Length == 0 ? -1 : content.IndexOf(edit.OldText, StringComparison.Ordinal);
            if (index < 0 || content.IndexOf(edit.OldText, index + 1, StringComparison.Ordinal) >= 0)
                throw new McpException($"oldText must occur exactly once in {edit.FilePath}: {(index < 0 ? "it does not occur" : "it occurs more than once")}.");

            edited = edited.WithDocumentText(document.Id, text.Replace(new TextSpan(index, edit.OldText.Length), edit.NewText));
            projects.Add(document.Project.Id);
        }

        var dependencies = solution.GetProjectDependencyGraph();
        projects.UnionWith([.. projects.SelectMany(dependencies.GetProjectsThatTransitivelyDependOnThisProject)]);
        var (before, after) = (await DiagnoseAsync(solution, projects, analyzers, cancellationToken), await DiagnoseAsync(edited, projects, analyzers, cancellationToken));
        return new([.. Except(after, before)], [.. Except(before, after)]);
    }

    private static async Task<List<Diagnostic>> DiagnoseAsync(Solution solution, IEnumerable<ProjectId> projects, bool analyzers, CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var project in projects.Select(solution.GetProject).OfType<Project>())
            diagnostics.AddRange((await DiagnosticsTool.GetAsync(project, null, analyzers, cancellationToken)).Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        return diagnostics;
    }

    /// <summary>
    /// The diagnostics of <paramref name="these"/> that <paramref name="others"/> lacks, matched by id, message and file, since edits move lines.
    /// </summary>
    private static IEnumerable<DiagnosticsTool.DiagnosticEntry> Except(List<Diagnostic> these, List<Diagnostic> others)
    {
        var remaining = others.GroupBy(Key).ToDictionary(group => group.Key, group => group.Count());
        foreach (var diagnostic in these)
        {
            if (remaining.TryGetValue(Key(diagnostic), out var count) && count > 0)
                remaining[Key(diagnostic)] = count - 1;
            else
                yield return DiagnosticsTool.EntryOf(diagnostic);
        }
    }

    private static (string, string, string?) Key(Diagnostic diagnostic) => (diagnostic.Id, diagnostic.GetMessage(), diagnostic.Location.SourceTree?.FilePath);

    /// <param name="Introduced">Errors and warnings the edits would add.</param>
    /// <param name="Resolved">Errors and warnings the edits would remove.</param>
    public sealed record Preview(IReadOnlyList<DiagnosticsTool.DiagnosticEntry> Introduced, IReadOnlyList<DiagnosticsTool.DiagnosticEntry> Resolved);
}
