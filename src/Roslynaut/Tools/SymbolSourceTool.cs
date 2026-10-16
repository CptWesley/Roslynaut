using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class SymbolSourceTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => GetSourceAsync;

    [McpServerTool(Name = "symbol_source", ReadOnly = true)]
    [Description("Returns the source code of a symbol's declaration, such as a whole method, property or type; one entry per declaration, "
        + "so a partial type has several. Each Source starts at the beginning of the line given by its Location.")]
    private async Task<IEnumerable<SymbolSource>> GetSourceAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        CancellationToken cancellationToken = default)
    {
        var (_, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        var sources = await GetSourcesAsync(symbols, cancellationToken);
        return sources.Count > 0 ? sources : throw new McpException("The symbol is not declared in source: use decompile or definition for referenced assemblies.");
    }

    internal static async Task<List<SymbolSource>> GetSourcesAsync(IEnumerable<ISymbol> symbols, CancellationToken cancellationToken)
    {
        var sources = new List<SymbolSource>();
        foreach (var symbol in symbols)
        {
            foreach (var reference in symbol.DeclaringSyntaxReferences)
            {
                var node = await reference.GetSyntaxAsync(cancellationToken);

                // A field or event declares its symbols in variable declarators; show the whole declaration.
                if (node is VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax declaration })
                    node = declaration;

                var text = await node.SyntaxTree.GetTextAsync(cancellationToken);
                var start = text.Lines.GetLineFromPosition(node.SpanStart).Start;
                sources.Add(new(symbol.ToDisplayString(), SourceLocation.From(node.GetLocation()), text.ToString(TextSpan.FromBounds(start, node.Span.End))));
            }
        }

        return sources;
    }

    public sealed record SymbolSource(string Signature, SourceLocation Location, string Source);
}
