using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

[JsonConverter(typeof(JsonStringEnumConverter<MetricsLevel>))]
public enum MetricsLevel
{
    Members,
    Types,
}

public sealed class CodeMetricsTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => MeasureCodeAsync;

    [McpServerTool(Name = "code_metrics", ReadOnly = true)]
    [Description("Measures code, most complex first. Members: the cyclomatic complexity, lines, parameters and deepest nesting of each method, "
        + "constructor, operator, or property or event with code. Types: their member count, lines, total and highest member complexity, and "
        + "class coupling: how many other types they use. In the whole solution, a file or a symbol. Generated code, such as source generators' "
        + "output and files marked auto-generated, is left out unless asked for, and so are the generated parts of partial types. Paged.")]
    private async Task<PagedResult<Metrics>> MeasureCodeAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Optional path of a C# file, or a symbol path, to measure within. " + ToolDefaults.SymbolPath)] string? scope = null,
        [Description("Members or Types.")] MetricsLevel level = MetricsLevel.Members,
        [Description("Measure generated code too, and count the generated parts of partial types.")] bool includeGenerated = false,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        return await cache.GetPageAsync(solution, $"code_metrics|{scope}|{level}|{includeGenerated}", page, pageSize,
            () => MeasureAsync(solution, scope, level, includeGenerated, cancellationToken));
    }

    internal static async Task<IEnumerable<Metrics>> MeasureAsync(Solution solution, string? scope, MetricsLevel level, bool includeGenerated, CancellationToken cancellationToken)
    {
        var roots = scope is null
            ? (await SymbolTree.GetRootNamespacesAsync(solution, cancellationToken)).ToList()
            : scope.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? await TypesInAsync(await WorkspaceService.GetDocumentAsync(solution, scope, cancellationToken), cancellationToken)
                : [.. (await SymbolTree.ResolveAsync(solution, scope, cancellationToken)).Select(SymbolTree.TypeOrSelf)];

        var symbols = new List<ISymbol>(roots.Where(root => root is not INamespaceSymbol));
        await foreach (var symbol in SymbolTree.GetDescendantsAsync(solution, roots, includeLocals: false, cancellationToken))
            symbols.Add(symbol);

        // The declarations that count: a partial type, method or property can have some in generated code.
        bool Counts(SyntaxReference reference) => includeGenerated || WorkspaceService.IsWrittenCode(solution, reference.SyntaxTree);

        // The declarations with the code: those of a partial type, but only the implementation of a partial member, its last part.
        IEnumerable<SyntaxReference> CodeOf(ISymbol symbol) => SymbolTree.PartsOf(symbol).Last().DeclaringSyntaxReferences.Where(Counts);

        var members = new Dictionary<ISymbol, Metrics>(SymbolEqualityComparer.Default);
        foreach (var member in symbols.Where(symbol => symbol is IMethodSymbol or IPropertySymbol or IEventSymbol).Distinct(SymbolEqualityComparer.Default))
        {
            if (await MeasureMemberAsync(solution, member!, CodeOf, cancellationToken) is { } metrics)
                members[member!] = metrics;
        }

        if (level == MetricsLevel.Members)
            return members.Values.OrderByDescending(metrics => metrics.Complexity).ToList();

        var types = new List<Metrics>();
        foreach (var type in symbols.OfType<INamedTypeSymbol>().Where(type => type.DeclaringSyntaxReferences.Any(Counts)).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default))
        {
            var own = members.Where(member => SymbolEqualityComparer.Default.Equals(member.Key.ContainingType, type)).Select(member => member.Value).ToList();
            types.Add(new(
                SymbolEntry.From(type),
                own.Sum(metrics => metrics.Complexity),
                await LinesAsync(type, CodeOf, cancellationToken),
                Parameters: null,
                Nesting: null,
                type.GetMembers().Count(member => !member.IsImplicitlyDeclared && member is not IMethodSymbol { AssociatedSymbol: not null } && member.DeclaringSyntaxReferences.Any(Counts)),
                own.Select(metrics => metrics.Complexity).DefaultIfEmpty(0).Max(),
                await CouplingAsync(solution, type, CodeOf, cancellationToken)));
        }

        return types.OrderByDescending(metrics => metrics.Complexity).ToList();
    }

    private static async Task<List<ISymbol>> TypesInAsync(Document document, CancellationToken cancellationToken)
    {
        var model = (await document.GetSemanticModelAsync(cancellationToken))!;
        var root = await model.SyntaxTree.GetRootAsync(cancellationToken);
        return [.. root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Select(type => model.GetDeclaredSymbol(type, cancellationToken)).OfType<ISymbol>()];
    }

    /// <summary>Null for members without code, such as abstract methods or auto-properties.</summary>
    private static async Task<Metrics?> MeasureMemberAsync(
        Solution solution, ISymbol member, Func<ISymbol, IEnumerable<SyntaxReference>> declarations, CancellationToken cancellationToken)
    {
        var operations = new List<IOperation>();
        foreach (var reference in declarations(member))
        {
            var node = await reference.GetSyntaxAsync(cancellationToken);
            if (solution.GetDocument(node.SyntaxTree) is { } document && await document.GetSemanticModelAsync(cancellationToken) is { } model)
            {
                // Without a body, the operations are of the arguments of attributes, which are no code to measure.
                operations.AddRange(CalleesTool.CodeOf(node).SelectMany(code => CalleesTool.OperationsOf(model, code, cancellationToken))
                    .Where(operation => operation.Syntax.FirstAncestorOrSelf<AttributeListSyntax>() is null));
            }
        }

        if (operations.Count == 0)
            return null;

        return new(
            SymbolEntry.From(member),
            1 + operations.SelectMany(operation => operation.DescendantsAndSelf()).Sum(Decisions),
            await LinesAsync(member, declarations, cancellationToken),
            member is IMethodSymbol method ? method.Parameters.Length : null,
            operations.Max(Nesting),
            Members: null,
            MaxComplexity: null,
            Coupling: null);
    }

    /// <summary>The branches an operation adds to cyclomatic complexity.</summary>
    private static int Decisions(IOperation operation) => operation switch
    {
        IConditionalOperation or ILoopOperation or ICatchClauseOperation or ICoalesceOperation or IConditionalAccessOperation => 1,
        IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr } => 1,
        ISwitchCaseOperation @case => @case.Clauses.Count(clause => clause is not IDefaultCaseClauseOperation),
        ISwitchExpressionArmOperation arm => arm.Pattern is IDiscardPatternOperation ? 0 : 1,
        _ => 0,
    };

    private static int Nesting(IOperation operation) =>
        (operation is IConditionalOperation or ILoopOperation or ITryOperation or IUsingOperation or ILockOperation or ISwitchOperation ? 1 : 0)
        + operation.ChildOperations.Select(Nesting).DefaultIfEmpty(0).Max();

    /// <summary>The lines of a type's declarations, or of the code of a member, see <see cref="CalleesTool.CodeOf"/>.</summary>
    private static async Task<int> LinesAsync(ISymbol symbol, Func<ISymbol, IEnumerable<SyntaxReference>> declarations, CancellationToken cancellationToken)
    {
        var lines = new HashSet<(SyntaxTree, int)>();
        foreach (var reference in declarations(symbol))
        {
            var declaration = await reference.GetSyntaxAsync(cancellationToken);
            foreach (var code in symbol is ITypeSymbol ? [declaration] : CalleesTool.CodeOf(declaration))
            {
                var span = code.GetLocation().GetLineSpan();
                for (var line = span.StartLinePosition.Line; line <= span.EndLinePosition.Line; line++)
                    lines.Add((code.SyntaxTree, line));
            }
        }

        return lines.Count;
    }

    /// <summary>The other named types a type's code refers to, not counting built-in ones such as int or string.</summary>
    private static async Task<int> CouplingAsync(
        Solution solution, INamedTypeSymbol type, Func<ISymbol, IEnumerable<SyntaxReference>> declarations, CancellationToken cancellationToken)
    {
        var used = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var reference in declarations(type))
        {
            var node = await reference.GetSyntaxAsync(cancellationToken);
            if (solution.GetDocument(node.SyntaxTree) is not { } document)
                continue;

            var model = (await document.GetSemanticModelAsync(cancellationToken))!;
            foreach (var name in node.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
                if ((symbol as INamedTypeSymbol ?? symbol?.ContainingType) is { SpecialType: SpecialType.None } usedType && !SymbolEqualityComparer.Default.Equals(usedType.OriginalDefinition, type))
                    used.Add(usedType.OriginalDefinition);
            }
        }

        return used.Count;
    }

    /// <param name="Complexity">Cyclomatic complexity: for a member 1 plus its branches, for a type the sum over its members.</param>
    /// <param name="Nesting">How deep conditions, loops, try, using, lock and switch nest in a member.</param>
    /// <param name="Coupling">For a type: how many other types its code uses.</param>
    public sealed record Metrics(
        SymbolEntry Symbol, int Complexity, int Lines, int? Parameters, int? Nesting, int? Members, int? MaxComplexity, int? Coupling);
}
