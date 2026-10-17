using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

/// <param name="ChangedFiles">The files written; empty when there was nothing to change.</param>
public sealed record EditResult(IReadOnlyList<string> ChangedFiles);

public sealed class FormatTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => FormatFileAsync;

    [McpServerTool(Name = "format", Destructive = true, Idempotent = true)]
    [Description("Formats a C# file, or some of its lines, by the project's .editorconfig settings, and writes it to disk.")]
    private async Task<EditResult> FormatFileAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.FilePath)] string filePath,
        [Description("Optional 1-based first line to format; without it, the whole file.")] int? startLine = null,
        [Description("Optional 1-based last line to format; without it, only startLine.")] int? endLine = null,
        CancellationToken cancellationToken = default)
    {
        var document = WorkspaceService.Editable(await workspace.GetDocumentAsync(projectPath, filePath, cancellationToken));
        var formatted = await FormatAsync(document, startLine, endLine, cancellationToken);
        return new(await WorkspaceService.WriteChangesAsync(document.Project.Solution, formatted.Project.Solution, cancellationToken));
    }

    internal static async Task<Document> FormatAsync(Document document, int? startLine, int? endLine, CancellationToken cancellationToken)
    {
        if (startLine is null)
            return await Formatter.FormatAsync(document, options: null, cancellationToken);

        // The first line's indentation is in the whitespace after the line before, which the formatter only changes when the span has it.
        var text = await document.GetTextAsync(cancellationToken);
        var lines = WorkspaceService.LinesOf(text, startLine.Value, endLine ?? startLine.Value, document.FilePath);
        var span = TextSpan.FromBounds(startLine > 1 ? text.Lines[startLine.Value - 2].End : 0, lines.End);
        return await Formatter.FormatAsync(document, span, options: null, cancellationToken);
    }
}

public sealed class OrganizeUsingsTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => OrganizeUsingsAsync;

    [McpServerTool(Name = "organize_usings", Destructive = true, Idempotent = true)]
    [Description("Removes the using directives a C# file does not need, sorts the rest as the project's .editorconfig says, and writes it to disk.")]
    private async Task<EditResult> OrganizeUsingsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.FilePath)] string filePath,
        [Description("Remove unneeded usings; otherwise only sort.")] bool removeUnused = true,
        CancellationToken cancellationToken = default)
    {
        var document = WorkspaceService.Editable(await workspace.GetDocumentAsync(projectPath, filePath, cancellationToken));
        var organized = await OrganizeAsync(document, removeUnused, cancellationToken);
        return new(await WorkspaceService.WriteChangesAsync(document.Project.Solution, organized.Project.Solution, cancellationToken));
    }

    internal static async Task<Document> OrganizeAsync(Document document, bool removeUnused, CancellationToken cancellationToken)
    {
        if (removeUnused)
            document = await RemoveUnusedAsync(document, cancellationToken);
        return await Formatter.OrganizeImportsAsync(document, cancellationToken);
    }

    private static async Task<Document> RemoveUnusedAsync(Document document, CancellationToken cancellationToken)
    {
        // The compiler only reports unneeded usings (CS8019) when it parses documentation comments, as those can need usings too.
        var tree = (await document.GetSyntaxTreeAsync(cancellationToken))!;
        var root = await tree.GetRootAsync(cancellationToken);
        var parsed = tree.WithRootAndOptions(root, tree.Options.WithDocumentationMode(DocumentationMode.Diagnose));
        var compilation = (await document.Project.GetCompilationAsync(cancellationToken))!.ReplaceSyntaxTree(tree, parsed);
        var unneeded = compilation.GetSemanticModel(parsed).GetDiagnostics(cancellationToken: cancellationToken)
            .Where(diagnostic => diagnostic.Id == "CS8019")
            .Select(diagnostic => diagnostic.Location.SourceSpan)
            .ToList();

        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(directive => unneeded.Any(span => span.Contains(directive.Span))).ToList();
        return usings.Count == 0 ? document : document.WithSyntaxRoot(root.RemoveNodes(usings, SyntaxRemoveOptions.KeepLeadingTrivia)!);
    }
}
