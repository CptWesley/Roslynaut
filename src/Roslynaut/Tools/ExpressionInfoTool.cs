using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class ExpressionInfoTool(WorkspaceService workspace) : IMcpTool
{
    // From the expression at the position outward, to its statement or member at most.
    private const int MaxExpressions = 5;

    public Delegate Handler => DescribeExpressionAsync;

    [McpServerTool(Name = "expression_info", ReadOnly = true)]
    [Description("Describes the expression at a position and up to 4 expressions around it, innermost first: its type, the type it converts to "
        + "and how, its constant value, its null state and the symbol it binds to; when binding fails, the candidates and why they failed.")]
    private async Task<IEnumerable<ExpressionInfo>> DescribeExpressionAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.FilePath)] string filePath,
        [Description(ToolDefaults.Line)] int line,
        [Description("1-based column within the expression.")] int column,
        CancellationToken cancellationToken = default)
    {
        var document = await workspace.GetDocumentAsync(projectPath, filePath);
        var position = WorkspaceService.PositionOf(await document.GetTextAsync(cancellationToken), line, column, filePath);
        return await DescribeAsync(document, position, cancellationToken);
    }

    internal static async Task<IEnumerable<ExpressionInfo>> DescribeAsync(Document document, int position, CancellationToken cancellationToken)
    {
        var model = (await document.GetSemanticModelAsync(cancellationToken))!;
        var root = await model.SyntaxTree.GetRootAsync(cancellationToken);
        return root.FindToken(position).Parent!.AncestorsAndSelf()
            .TakeWhile(node => node is not (StatementSyntax or MemberDeclarationSyntax))
            .OfType<ExpressionSyntax>()
            .Take(MaxExpressions)
            .Select(expression => Describe(model, expression, cancellationToken))
            .ToList();
    }

    private static ExpressionInfo Describe(SemanticModel model, ExpressionSyntax expression, CancellationToken cancellationToken)
    {
        var type = model.GetTypeInfo(expression, cancellationToken);
        var symbol = model.GetSymbolInfo(expression, cancellationToken);
        var constant = model.GetConstantValue(expression, cancellationToken);
        var conversion = model.GetConversion(expression, cancellationToken);
        var text = expression.ToString();
        return new(
            text.Length > 200 ? text[..200] + "…" : text,
            type.Type?.ToDisplayString(),
            SymbolEqualityComparer.Default.Equals(type.Type, type.ConvertedType) ? null : type.ConvertedType?.ToDisplayString(),
            conversion.IsIdentity || !conversion.Exists ? null : $"{(conversion.IsImplicit ? "Implicit" : "Explicit")} {Kind(conversion)}",
            constant.HasValue ? FormattableString.Invariant($"{constant.Value ?? "null"}") : null,
            type.Nullability.FlowState == NullableFlowState.None ? null : type.Nullability.FlowState.ToString(),
            symbol.Symbol?.ToDisplayString(),
            [.. symbol.CandidateSymbols.Select(candidate => candidate.ToDisplayString())],
            symbol.CandidateReason == CandidateReason.None ? null : symbol.CandidateReason.ToString());
    }

    private static string Kind(Conversion conversion) => conversion switch
    {
        { IsUserDefined: true } => $"user-defined ({conversion.MethodSymbol?.ToDisplayString()})",
        { IsNumeric: true } => "numeric",
        { IsNullable: true } => "nullable",
        { IsBoxing: true } => "boxing",
        { IsUnboxing: true } => "unboxing",
        { IsReference: true } => "reference",
        { IsInterpolatedString: true } => "interpolated string",
        { IsConstantExpression: true } => "constant",
        { IsMethodGroup: true } => "method group",
        { IsAnonymousFunction: true } => "anonymous function",
        { IsTupleConversion: true } => "tuple",
        { IsNullLiteral: true } => "null literal",
        { IsDefaultLiteral: true } => "default literal",
        _ => "conversion",
    };

    /// <param name="ConvertedType">The type the context converts the expression to, if not its own.</param>
    /// <param name="NullState">NotNull or MaybeNull, in a nullable context.</param>
    /// <param name="CandidateReason">Why binding to the candidates failed, such as OverloadResolutionFailure or Inaccessible.</param>
    public sealed record ExpressionInfo(
        string Expression, string? Type, string? ConvertedType, string? Conversion, string? ConstantValue, string? NullState,
        string? Symbol, IReadOnlyList<string> CandidateSymbols, string? CandidateReason);
}
