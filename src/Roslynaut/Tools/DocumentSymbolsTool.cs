using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class DocumentSymbolsTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => GetDocumentSymbolsAsync;

    [McpServerTool(Name = "document_symbols", ReadOnly = true)]
    [Description("Outlines a C# file: its types and their members (methods, properties, fields, events) with signatures and positions.")]
    private async Task<IEnumerable<SymbolEntry>> GetDocumentSymbolsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.FilePath)] string filePath,
        CancellationToken cancellationToken)
    {
        var document = await workspace.GetDocumentAsync(projectPath, filePath);
        var model = (await document.GetSemanticModelAsync(cancellationToken))!;
        var root = await model.SyntaxTree.GetRootAsync(cancellationToken);

        // Only walk declaration containers; member bodies hold nothing worth outlining.
        return root
            .DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax
                or BaseFieldDeclarationSyntax or VariableDeclarationSyntax)
            .Select(node => model.GetDeclaredSymbol(node, cancellationToken))
            .Where(symbol => symbol is INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol)
            .Select(symbol => SymbolEntry.From(symbol!, symbol!.Locations.First(location => location.SourceTree == model.SyntaxTree)));
    }
}
