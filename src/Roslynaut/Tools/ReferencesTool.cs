using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class ReferencesTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => FindReferencesAsync;

    [McpServerTool(Name = "references", ReadOnly = true)]
    [Description("Finds all references to a symbol across the project or solution, with the member that contains each reference "
        + "and whether it reads, writes or calls the symbol. Paged.")]
    private async Task<PagedResult<ReferenceEntry>> FindReferencesAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        return await cache.GetPageAsync(solution, $"references|{symbol}|{filePath}|{line}|{column}", page, pageSize,
            () => FindAsync(solution, symbols, cancellationToken));
    }

    internal static async Task<IEnumerable<ReferenceEntry>> FindAsync(Solution solution, IReadOnlyList<ISymbol> symbols, CancellationToken cancellationToken)
    {
        var entries = new List<ReferenceEntry>();
        foreach (var symbol in symbols)
        {
            foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
            {
                foreach (var location in reference.Locations)
                    entries.Add(await EntryOfAsync(location, reference.Definition, cancellationToken));
            }
        }

        return entries.Distinct();
    }

    internal static async Task<ReferenceEntry> EntryOfAsync(ReferenceLocation location, ISymbol definition, CancellationToken cancellationToken)
    {
        var model = (await location.Document.GetSemanticModelAsync(cancellationToken))!;
        var container = ContainerOf(model, location.Location.SourceSpan, definition, cancellationToken);
        var access = IsWrite(model, location.Location, cancellationToken) ? "Write" : definition is IMethodSymbol ? "Call" : "Read";
        return new(SourceLocation.From(location.Location), container?.ToDisplayString(), access);
    }

    /// <summary>
    /// The member or type code is in. In a type's base list, where the semantic model gives the namespace, that is the type; or its primary
    /// constructor for a call of a base constructor, as in "class Square(double side) : Rectangle(side, side)", which runs in it.
    /// </summary>
    internal static ISymbol? ContainerOf(SemanticModel model, TextSpan span, ISymbol definition, CancellationToken cancellationToken)
    {
        var node = model.SyntaxTree.GetRoot(cancellationToken).FindNode(span, getInnermostNodeForTie: true);
        if (node.FirstAncestorOrSelf<BaseListSyntax>()?.Parent is not TypeDeclarationSyntax declaration || model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol type)
            return EnclosingMemberOf(model, span.Start, cancellationToken);

        return definition is IMethodSymbol { MethodKind: MethodKind.Constructor } ? PrimaryConstructorOf(type, declaration) ?? (ISymbol)type : type;
    }

    /// <summary>The symbol code at a position is in; for a lambda, which has no name to show, the member it is written in.</summary>
    internal static ISymbol? EnclosingMemberOf(SemanticModel model, int position, CancellationToken cancellationToken)
    {
        var symbol = model.GetEnclosingSymbol(position, cancellationToken);
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction })
            symbol = symbol.ContainingSymbol;
        return symbol;
    }

    /// <summary>The primary constructor a type declaration declares, if any.</summary>
    internal static IMethodSymbol? PrimaryConstructorOf(INamedTypeSymbol type, TypeDeclarationSyntax declaration) =>
        type.InstanceConstructors.FirstOrDefault(constructor => constructor.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == declaration.SyntaxTree && reference.Span == declaration.Span));

    private static bool IsWrite(SemanticModel model, Location location, CancellationToken cancellationToken)
    {
        var node = model.SyntaxTree.GetRoot(cancellationToken).FindNode(location.SourceSpan, getInnermostNodeForTie: true);

        // A name in a member access has no operation of its own; the member access has.
        var operation = model.GetOperation(node, cancellationToken) ?? model.GetOperation(node.Parent!, cancellationToken);
        return operation?.Parent switch
        {
            IAssignmentOperation assignment => assignment.Target == operation,
            IIncrementOrDecrementOperation => true,
            IArgumentOperation { Parameter.RefKind: RefKind.Out or RefKind.Ref } => true,
            _ => false,
        };
    }

    /// <param name="Container">The member or type the reference is in.</param>
    /// <param name="Access">Read, Write or Call; Declaration in document_highlight.</param>
    public sealed record ReferenceEntry(SourceLocation Location, string? Container, string Access);
}
