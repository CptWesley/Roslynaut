using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class DocumentSymbolsTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => GetDocumentSymbolsAsync;

    [McpServerTool(Name = "document_symbols", ReadOnly = true)]
    [Description("Outlines a C# file: its types and their members (methods, properties, fields, events) with signatures and positions. Paged.")]
    private async Task<PagedResult<SymbolEntry>> GetDocumentSymbolsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.FilePath)] string filePath,
        [Description("Optional nesting depth: 0 for the top-level types only, 1 to add their members and nested types, and so on; "
            + "without it, everything.")] int? depth = null,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var document = await workspace.GetDocumentAsync(projectPath, filePath);
        return await cache.GetPageAsync(document.Project.Solution, $"document_symbols|{filePath}|{depth}", page, pageSize,
            () => OutlineAsync(document, depth, cancellationToken));
    }

    internal static async Task<IEnumerable<SymbolEntry>> OutlineAsync(Document document, int? depth, CancellationToken cancellationToken)
    {
        var model = (await document.GetSemanticModelAsync(cancellationToken))!;
        var root = await model.SyntaxTree.GetRootAsync(cancellationToken);

        // Only walk declaration containers; member bodies hold nothing worth outlining. A file declares the entry point of its top-level statements.
        return root
            .DescendantNodesAndSelf(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax
                or BaseFieldDeclarationSyntax or VariableDeclarationSyntax)
            .Select(node => model.GetDeclaredSymbol(node, cancellationToken))
            .Where(symbol => symbol is INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol && (depth is null || DepthOf(symbol) <= depth))
            .Select(symbol => SymbolEntry.From(symbol!, symbol!.Locations.First(location => location.SourceTree == model.SyntaxTree)))
            .ToList();
    }

    /// <summary>How many types contain the symbol: 0 for a top-level type.</summary>
    private static int DepthOf(ISymbol symbol)
    {
        var depth = 0;
        for (var type = symbol.ContainingType; type is not null; type = type.ContainingType)
            depth++;
        return depth;
    }
}
