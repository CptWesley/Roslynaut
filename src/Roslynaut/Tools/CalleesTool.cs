using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class CalleesTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => FindCalleesAsync;

    [McpServerTool(Name = "callees", ReadOnly = true)]
    [Description("Finds what a member uses (outgoing calls): the methods, constructors, properties, events, method groups and user-defined "
        + "operators it refers to, each with the locations it refers to them at. Paged.")]
    private async Task<PagedResult<CallersTool.Caller>> FindCalleesAsync(
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
        return await cache.GetPageAsync(solution, $"callees|{symbol}|{filePath}|{line}|{column}", page, pageSize,
            () => FindAsync(solution, symbols, cancellationToken));
    }

    internal static async Task<IEnumerable<CallersTool.Caller>> FindAsync(Solution solution, IReadOnlyList<ISymbol> symbols, CancellationToken cancellationToken)
    {
        var calls = new Dictionary<ISymbol, List<SourceLocation>>(SymbolEqualityComparer.Default);
        foreach (var reference in symbols.SelectMany(symbol => symbol.DeclaringSyntaxReferences))
        {
            var node = await reference.GetSyntaxAsync(cancellationToken);
            if (solution.GetDocument(node.SyntaxTree) is not { } document)
                continue;

            var model = (await document.GetSemanticModelAsync(cancellationToken))!;
            foreach (var operation in CodeOf(node).SelectMany(code => OperationsOf(model, code, cancellationToken)).SelectMany(operation => operation.DescendantsAndSelf()))
            {
                if (CalleeOf(operation) is not { } callee)
                    continue;
                if (!calls.TryGetValue(callee, out var locations))
                    calls[callee] = locations = [];
                locations.Add(SourceLocation.From(operation.Syntax.GetLocation()));
            }
        }

        return calls.Select(call => new CallersTool.Caller(SymbolEntry.From(call.Key), call.Value));
    }

    /// <summary>
    /// The syntax that holds a member's code. A primary constructor is declared by its type, but its code is only in the parameter list
    /// (default values) and the arguments to the base constructor.
    /// </summary>
    internal static IEnumerable<SyntaxNode> CodeOf(SyntaxNode declaration) => declaration is TypeDeclarationSyntax type
        ? [.. type.ParameterList is { } parameters ? [parameters] : Array.Empty<SyntaxNode>(), .. type.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>() ?? []]
        : [declaration];

    /// <summary>The operations in a declaration: of its body, or else of its parts, such as the accessors of a property.</summary>
    internal static IEnumerable<IOperation> OperationsOf(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken) =>
        model.GetOperation(node, cancellationToken) is { } operation
            ? [operation]
            : node.ChildNodes().SelectMany(child => OperationsOf(model, child, cancellationToken));

    private static ISymbol? CalleeOf(IOperation operation) => operation switch
    {
        IInvocationOperation invocation => invocation.TargetMethod,
        IObjectCreationOperation creation => creation.Constructor,
        IPropertyReferenceOperation property => property.Property,
        IEventReferenceOperation @event => @event.Event,
        IMethodReferenceOperation method => method.Method,
        IBinaryOperation { OperatorMethod: { } method } => method,
        IUnaryOperation { OperatorMethod: { } method } => method,
        IConversionOperation { OperatorMethod: { } method } => method,
        _ => null,
    };
}
