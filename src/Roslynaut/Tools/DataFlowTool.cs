using System.Collections.Immutable;
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class DataFlowTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => AnalyzeDataFlowAsync;

    [McpServerTool(Name = "data_flow", ReadOnly = true)]
    [Description("Analyzes the statements between two lines, which must be in one block, as for extracting a method: the variables declared, "
        + "flowing in and out, read and written inside and outside, and captured by lambdas; and the exits (return, break, continue, goto) "
        + "and whether the end can be reached.")]
    private async Task<DataFlow> AnalyzeDataFlowAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.FilePath)] string filePath,
        [Description("1-based first line.")] int startLine,
        [Description("1-based last line.")] int endLine,
        CancellationToken cancellationToken = default)
    {
        var document = await workspace.GetDocumentAsync(projectPath, filePath);
        return await AnalyzeAsync(document, WorkspaceService.LinesOf(await document.GetTextAsync(cancellationToken), startLine, endLine, filePath), cancellationToken);
    }

    internal static async Task<DataFlow> AnalyzeAsync(Document document, Microsoft.CodeAnalysis.Text.TextSpan span, CancellationToken cancellationToken)
    {
        var model = (await document.GetSemanticModelAsync(cancellationToken))!;
        var root = await model.SyntaxTree.GetRootAsync(cancellationToken);

        // The outermost statements within the lines; top-level statements each sit in a global statement of their own.
        var statements = root.DescendantNodes(span).OfType<StatementSyntax>()
            .Where(statement => span.Contains(statement.Span) && !(statement.Parent is StatementSyntax parent && span.Contains(parent.Span)))
            .ToList();
        if (statements.Count == 0)
            throw new McpException("The lines contain no whole statement.");
        if (statements.Select(statement => statement.Parent is GlobalStatementSyntax global ? global.Parent : statement.Parent).Distinct().Count() > 1)
            throw new McpException("The lines cover statements of different blocks: pick lines within one block.");

        var data = model.AnalyzeDataFlow(statements[0], statements[^1]);
        var control = model.AnalyzeControlFlow(statements[0], statements[^1]);
        if (data is not { Succeeded: true } || control is not { Succeeded: true })
            throw new McpException("The statements cannot be analyzed together.");

        return new(
            statements.Count,
            Names(data.VariablesDeclared),
            Names(data.DataFlowsIn),
            Names(data.DataFlowsOut),
            Names(data.ReadInside),
            Names(data.WrittenInside),
            Names(data.ReadOutside),
            Names(data.WrittenOutside),
            Names(data.Captured),
            Names(data.AlwaysAssigned),
            control.EndPointIsReachable,
            [.. control.ExitPoints.Select(exit => SourceLocation.From(exit.GetLocation()))],
            [.. control.ReturnStatements.Select(statement => SourceLocation.From(statement.GetLocation()))]);
    }

    private static IReadOnlyList<string> Names(ImmutableArray<ISymbol> symbols) => [.. symbols.Select(symbol => symbol.Name)];

    /// <param name="DataFlowsIn">Variables whose values from before are read inside: the parameters an extracted method would need.</param>
    /// <param name="DataFlowsOut">Variables written inside and read after: what an extracted method would have to return.</param>
    /// <param name="Captured">Variables captured by lambdas or local functions, inside or outside.</param>
    /// <param name="AlwaysAssigned">Variables assigned on every path through the statements.</param>
    /// <param name="ExitPoints">Statements that jump out: return, break, continue, goto, yield break.</param>
    public sealed record DataFlow(
        int Statements,
        IReadOnlyList<string> VariablesDeclared,
        IReadOnlyList<string> DataFlowsIn,
        IReadOnlyList<string> DataFlowsOut,
        IReadOnlyList<string> ReadInside,
        IReadOnlyList<string> WrittenInside,
        IReadOnlyList<string> ReadOutside,
        IReadOnlyList<string> WrittenOutside,
        IReadOnlyList<string> Captured,
        IReadOnlyList<string> AlwaysAssigned,
        bool EndPointIsReachable,
        IReadOnlyList<SourceLocation> ExitPoints,
        IReadOnlyList<SourceLocation> ReturnStatements);
}
