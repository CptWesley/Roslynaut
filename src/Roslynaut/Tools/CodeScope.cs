using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Roslynaut.Tools;

/// <summary>Where a text or operation search looks: a file, a project, a symbol's declarations, or everywhere.</summary>
internal static class CodeScope
{
    public const string Description = "Optional scope: a C# file, a project name, or a symbol path to search only its declaration; "
        + "without it, every file of the solution, generated ones included. " + ToolDefaults.SymbolPath;

    /// <summary>The documents to search, each with the span to search in, or null for the whole document.</summary>
    public static async Task<List<(Document Document, TextSpan? Span)>> ResolveAsync(Solution solution, string? scope, CancellationToken cancellationToken)
    {
        if (scope is not null && scope.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return [(await WorkspaceService.GetDocumentAsync(solution, scope, cancellationToken), null)];

        var projects = scope is null ? solution.Projects : solution.Projects.Where(project => project.Name.Equals(scope, StringComparison.OrdinalIgnoreCase)).ToList();
        if (scope is null || projects.Any())
        {
            var documents = new List<(Document, TextSpan?)>();
            foreach (var project in projects)
            {
                documents.AddRange(project.Documents.Select(document => (document, (TextSpan?)null)));
                documents.AddRange((await project.GetSourceGeneratedDocumentsAsync(cancellationToken)).Select(document => ((Document)document, (TextSpan?)null)));
            }

            return documents;
        }

        var declarations = new List<(Document, TextSpan?)>();
        foreach (var reference in (await SymbolTree.ResolveAsync(solution, scope, cancellationToken)).SelectMany(SymbolTree.DeclarationsOf))
        {
            if (solution.GetDocument(reference.SyntaxTree) is { } document)
                declarations.Add((document, (await reference.GetSyntaxAsync(cancellationToken)).FullSpan));
        }

        return declarations;
    }
}
