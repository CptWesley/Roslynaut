using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class CallersTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => FindCallersAsync;

    [McpServerTool(Name = "callers", ReadOnly = true)]
    [Description("Finds the members that call or use a symbol (incoming calls), each with the locations of its calls. "
        + "Calls through an interface or base member are included. Paged.")]
    private async Task<PagedResult<Caller>> FindCallersAsync(
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
        return await cache.GetPageAsync(solution, $"callers|{symbol}|{filePath}|{line}|{column}", page, pageSize,
            () => FindAsync(solution, symbols, cancellationToken));
    }

    internal static async Task<IEnumerable<Caller>> FindAsync(Solution solution, IReadOnlyList<ISymbol> symbols, CancellationToken cancellationToken)
    {
        var callers = new List<Caller>();
        foreach (var symbol in symbols)
        {
            foreach (var caller in await SymbolFinder.FindCallersAsync(symbol, solution, cancellationToken))
                callers.Add(new(SymbolEntry.From(caller.CallingSymbol), [.. caller.Locations.Where(location => location.IsInSource).Select(SourceLocation.From)]));
            foreach (var (caller, call) in await BaseConstructorCallsAsync(symbol, solution, cancellationToken))
                callers.Add(new(SymbolEntry.From(caller), [SourceLocation.From(call)]));
        }

        return callers;
    }

    /// <summary>
    /// The calls of a constructor in the base lists of derived types' primary constructors, as in "class Square(double side) : Rectangle(side, side)",
    /// each with the primary constructor it runs in. FindCallers leaves them out, as the semantic model puts a base list in no member.
    /// </summary>
    internal static async Task<List<(IMethodSymbol Caller, Location Call)>> BaseConstructorCallsAsync(ISymbol symbol, Solution solution, CancellationToken cancellationToken)
    {
        var calls = new List<(IMethodSymbol, Location)>();
        if (symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
            return calls;

        foreach (var derived in await SymbolFinder.FindDerivedClassesAsync(constructor.ContainingType, solution, transitive: false, cancellationToken: cancellationToken))
        {
            foreach (var reference in derived.DeclaringSyntaxReferences)
            {
                if (await reference.GetSyntaxAsync(cancellationToken) is not TypeDeclarationSyntax { BaseList: { } bases } declaration
                    || bases.Types.OfType<PrimaryConstructorBaseTypeSyntax>().FirstOrDefault() is not { } call
                    || ReferencesTool.PrimaryConstructorOf(derived, declaration) is not { } caller
                    || solution.GetDocument(declaration.SyntaxTree) is not { } document)
                    continue;

                var model = (await document.GetSemanticModelAsync(cancellationToken))!;
                if (SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(call, cancellationToken).Symbol?.OriginalDefinition, constructor.OriginalDefinition))
                    calls.Add((caller, call.Type.GetLocation()));
            }
        }

        return calls;
    }

    public sealed record Caller(SymbolEntry Symbol, IReadOnlyList<SourceLocation> Calls);
}
