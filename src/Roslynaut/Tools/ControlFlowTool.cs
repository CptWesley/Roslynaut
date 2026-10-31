using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class ControlFlowTool(WorkspaceService workspace) : IMcpTool
{
    private const int MaxPaths = 20;

    public Delegate Handler => GetFlowAsync;

    [McpServerTool(Name = "control_flow", ReadOnly = true)]
    [Description("The control flow graph of a method, constructor or accessor, as the compiler builds it: its basic blocks with their lines, the "
        + "condition each one branches on and where each outcome goes, where it returns or throws, which blocks cannot be reached, and the "
        + "try, catch and finally each is in. With toLine, the conditions that hold on each path from the start to that line, as for what input "
        + "reaches a throw, or which branch a test has to take to run a line coverage reports as not run. Lambdas and local functions inside "
        + "are not followed.")]
    private async Task<ControlFlow> GetFlowAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        [Description("A 1-based line in the member: also the paths to it from the start, as the conditions along each.")] int? toLine = null,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        return await AnalyzeAsync(solution, symbols[0], toLine, cancellationToken);
    }

    internal static async Task<ControlFlow> AnalyzeAsync(Solution solution, ISymbol symbol, int? toLine, CancellationToken cancellationToken)
    {
        var declaration = SymbolTree.DeclarationsOf(symbol).FirstOrDefault()
            ?? throw new McpException($"{symbol.ToDisplayString()} is not declared in source.");
        var node = await declaration.GetSyntaxAsync(cancellationToken);
        var model = solution.GetDocument(node.SyntaxTree) is { } document ? await document.GetSemanticModelAsync(cancellationToken) : null;
        var graph = model is null ? null : ControlFlowGraph.Create(node, model, cancellationToken);
        if (graph is null)
            throw new McpException($"{symbol.ToDisplayString()} has no code of its own to build a control flow graph of: pick a method, constructor or accessor.");

        var blocks = graph.Blocks.Select(Describe).ToList();
        IReadOnlyList<IReadOnlyList<string>>? paths = null;
        if (toLine is { } target)
        {
            var targets = graph.Blocks.Where(block => LinesOf(block) is var (start, end) && start <= target && target <= end).Select(block => block.Ordinal).ToHashSet();
            if (targets.Count == 0)
                throw new McpException($"Line {target} has no code of {symbol.ToDisplayString()}.");
            paths = PathsTo(graph, targets);
        }

        return new(blocks, paths);
    }

    private static FlowBlock Describe(BasicBlock block)
    {
        var (lines, condition) = (LinesOf(block), block.BranchValue?.Syntax.ToString());
        var (conditional, fallThrough) = (block.ConditionalSuccessor, block.FallThroughSuccessor);
        var (ifTrue, ifFalse) = block.ConditionKind switch
        {
            ControlFlowConditionKind.WhenTrue => (conditional?.Destination?.Ordinal, fallThrough?.Destination?.Ordinal),
            ControlFlowConditionKind.WhenFalse => (fallThrough?.Destination?.Ordinal, conditional?.Destination?.Ordinal),
            _ => ((int?)null, (int?)null),
        };
        var exit = fallThrough?.Semantics is ControlFlowBranchSemantics.Return or ControlFlowBranchSemantics.Throw or ControlFlowBranchSemantics.Rethrow
            or ControlFlowBranchSemantics.ProgramTermination
            ? fallThrough.Semantics.ToString()
            : null;
        var region = RegionOf(block.EnclosingRegion);
        return new(
            block.Ordinal,
            block.Kind.ToString(),
            lines is var (start, end) ? start == end ? $"{start}" : $"{start}-{end}" : null,
            block.IsReachable,
            region,
            block.ConditionKind == ControlFlowConditionKind.None ? null : condition,
            ifTrue,
            ifFalse,
            block.ConditionKind == ControlFlowConditionKind.None && exit is null ? fallThrough?.Destination?.Ordinal : null,
            exit,
            exit == "Return" ? condition : null);
    }

    /// <summary>The innermost try, catch, finally or filter a block is in, as "Try" or "Catch (IOException)".</summary>
    private static string? RegionOf(ControlFlowRegion? region)
    {
        for (; region is not null; region = region.EnclosingRegion)
        {
            switch (region.Kind)
            {
                case ControlFlowRegionKind.Try or ControlFlowRegionKind.Finally or ControlFlowRegionKind.Filter:
                    return region.Kind.ToString();
                case ControlFlowRegionKind.Catch:
                    return region.ExceptionType is { } type ? $"Catch ({type.ToDisplayString()})" : "Catch";
            }
        }

        return null;
    }

    /// <summary>The first and last 1-based lines of a block's code, its branch condition included.</summary>
    private static (int Start, int End)? LinesOf(BasicBlock block)
    {
        var spans = block.Operations.Select(operation => operation.Syntax).Append(block.BranchValue?.Syntax).OfType<SyntaxNode>()
            .Select(syntax => syntax.SyntaxTree.GetLineSpan(syntax.Span)).ToList();
        return spans.Count == 0 ? null : (spans.Min(span => span.StartLinePosition.Line) + 1, spans.Max(span => span.EndLinePosition.Line) + 1);
    }

    /// <summary>
    /// The paths from the entry to the target blocks without going round a loop twice, each as the outcomes of the conditions on it.
    /// ponytail: depth first over every path, capped at 20; enough for a method's branches, not for a state machine of them.
    /// </summary>
    private static List<IReadOnlyList<string>> PathsTo(ControlFlowGraph graph, IReadOnlySet<int> targets)
    {
        var paths = new List<IReadOnlyList<string>>();
        var visited = new HashSet<int>();
        void Walk(BasicBlock block, List<string> conditions)
        {
            if (paths.Count >= MaxPaths || !visited.Add(block.Ordinal))
                return;

            if (targets.Contains(block.Ordinal))
            {
                paths.Add([.. conditions]);
            }
            else
            {
                var condition = block.BranchValue?.Syntax.ToString();
                foreach (var (branch, taken) in Successors(block))
                {
                    if (branch.Destination is not { } next)
                        continue;
                    if (taken is { } holds && condition is not null)
                        conditions.Add($"{condition} is {(holds ? "true" : "false")}");
                    Walk(next, conditions);
                    if (taken is not null && condition is not null)
                        conditions.RemoveAt(conditions.Count - 1);
                }
            }

            visited.Remove(block.Ordinal);
        }

        Walk(graph.Blocks[0], []);
        return [.. paths.Distinct(new SequenceComparer())];
    }

    /// <summary>A block's successors, with whether its condition holds to take each; null for a block without one.</summary>
    private static IEnumerable<(ControlFlowBranch Branch, bool? Holds)> Successors(BasicBlock block)
    {
        if (block.ConditionalSuccessor is { } conditional)
            yield return (conditional, block.ConditionKind == ControlFlowConditionKind.WhenTrue);
        if (block.FallThroughSuccessor is { } fallThrough)
            yield return (fallThrough, block.ConditionKind == ControlFlowConditionKind.None ? null : block.ConditionKind == ControlFlowConditionKind.WhenFalse);
    }

    private sealed class SequenceComparer : IEqualityComparer<IReadOnlyList<string>>
    {
        public bool Equals(IReadOnlyList<string>? x, IReadOnlyList<string>? y) => x!.SequenceEqual(y!);

        public int GetHashCode(IReadOnlyList<string> obj) => string.Join('\n', obj).GetHashCode();
    }

    /// <param name="Blocks">The basic blocks in order: the first is the entry, the last the exit.</param>
    /// <param name="PathsTo">With toLine: per path from the entry to the line, the conditions on it and whether each holds.</param>
    public sealed record ControlFlow(IReadOnlyList<FlowBlock> Blocks, IReadOnlyList<IReadOnlyList<string>>? PathsTo);

    /// <param name="Kind">Entry, Exit or Block.</param>
    /// <param name="Lines">The lines of its code, such as "12-14"; none for a block without code.</param>
    /// <param name="Reachable">Whether any path from the entry reaches it.</param>
    /// <param name="Region">The innermost Try, Catch (with its exception type), Finally or Filter it is in.</param>
    /// <param name="Condition">The condition it branches on.</param>
    /// <param name="IfTrue">The block it goes to when Condition holds.</param>
    /// <param name="IfFalse">The block it goes to when Condition does not hold.</param>
    /// <param name="Next">The block it goes on to, without a condition.</param>
    /// <param name="Exit">How it leaves the member: Return, Throw, Rethrow or ProgramTermination.</param>
    /// <param name="Returns">For a Return, the value it returns.</param>
    public sealed record FlowBlock(
        int Id, string Kind, string? Lines, bool Reachable, string? Region, string? Condition, int? IfTrue, int? IfFalse, int? Next, string? Exit, string? Returns);
}
