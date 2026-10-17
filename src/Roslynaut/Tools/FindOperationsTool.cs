using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class FindOperationsTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => FindOperationsAsync;

    [McpServerTool(Name = "find_operations", ReadOnly = true)]
    [Description("Finds code by what it does rather than by name: the operations of a kind, such as Invocation, ObjectCreation, Throw, Await, "
        + "Lock, Using, Conversion, Literal or Coalesce, and optionally only those whose target matches a regex: the method called, the type "
        + "created or thrown, the type converted to, or else the operation's type. Compiler-generated ones, such as implicit conversions, count too. Paged.")]
    private async Task<PagedResult<OperationEntry>> FindOperationsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The kind of operation, a name of Roslyn's OperationKind, e.g. 'Invocation' or 'ObjectCreation'.")] string kind,
        [Description("Optional case-insensitive regex for the target, e.g. 'HttpClient' or 'Task\\.Result'.")] string? target = null,
        [Description(CodeScope.Description)] string? scope = null,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<OperationKind>(kind, ignoreCase: true, out var operationKind) || operationKind == OperationKind.None)
            throw new McpException($"Unknown operation kind '{kind}'; for example: Invocation, ObjectCreation, Throw, Await, Lock, Using, Conversion, Literal, Coalesce.");

        Regex? regex;
        try
        {
            regex = target is null ? null : new Regex(target, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException e)
        {
            throw new McpException($"Invalid target: {e.Message}");
        }

        var solution = (await workspace.GetAsync(projectPath)).Solution;
        return await cache.GetPageAsync(solution, $"find_operations|{operationKind}|{target}|{scope}", page, pageSize,
            () => FindAsync(solution, operationKind, regex, scope, cancellationToken));
    }

    internal static async Task<IEnumerable<OperationEntry>> FindAsync(Solution solution, OperationKind kind, Regex? target, string? scope, CancellationToken cancellationToken)
    {
        var found = new List<OperationEntry>();
        foreach (var (document, span) in await CodeScope.ResolveAsync(solution, scope, cancellationToken))
        {
            var model = (await document.GetSemanticModelAsync(cancellationToken))!;
            var root = await model.SyntaxTree.GetRootAsync(cancellationToken);
            var operations = CalleesTool.OperationsOf(model, root, cancellationToken).SelectMany(operation => operation.DescendantsAndSelf())
                .Where(operation => operation.Kind == kind && (span is null || span.Value.Contains(operation.Syntax.Span)));
            foreach (var operation in operations)
            {
                var targetName = TargetOf(operation);
                if (target is not null && (targetName is null || !target.IsMatch(targetName)))
                    continue;

                var container = ReferencesTool.EnclosingMemberOf(model, operation.Syntax.SpanStart, cancellationToken);
                found.Add(new(SourceLocation.From(operation.Syntax.GetLocation()), container?.ToDisplayString(), targetName, operation.IsImplicit));
            }
        }

        return found.Distinct();
    }

    private static string? TargetOf(IOperation operation) => operation switch
    {
        IInvocationOperation invocation => invocation.TargetMethod.ToDisplayString(),
        IObjectCreationOperation creation => creation.Type?.ToDisplayString(),
        IThrowOperation { Exception: { } exception } => (exception is IConversionOperation conversion ? conversion.Operand : exception).Type?.ToDisplayString(),
        IConversionOperation conversion => $"{conversion.Operand.Type?.ToDisplayString()} -> {conversion.Type?.ToDisplayString()}",
        IMemberReferenceOperation member => member.Member.ToDisplayString(),
        _ => operation.Type?.ToDisplayString(),
    };

    /// <param name="Container">The member the operation is in.</param>
    /// <param name="Target">What the operation calls, creates, throws or converts, or else its type.</param>
    /// <param name="Implicit">Whether the compiler added it, such as an implicit conversion.</param>
    public sealed record OperationEntry(SourceLocation Location, string? Container, string? Target, bool Implicit);
}
