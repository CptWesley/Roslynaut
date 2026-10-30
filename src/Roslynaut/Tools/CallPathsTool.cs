using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class CallPathsTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => FindPathsAsync;

    [McpServerTool(Name = "call_paths", ReadOnly = true)]
    [Description("Finds how one member reaches another through calls: the shortest call chains from 'from' to 'to', each hop with the line of its "
        + "call, and the member it calls through where that is an interface or base member, as for 'how does this request reach the database' "
        + "or 'can this code call that method'. Follows callers back from 'to', so calls through interfaces and overrides count; a lambda's "
        + "calls count as its member's.")]
    private async Task<IReadOnlyList<CallPath>> FindPathsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The member the chains start at, by symbol path. " + ToolDefaults.SymbolPath)] string from,
        [Description("The member the chains end at, by symbol path.")] string to,
        [Description("The most calls a chain may have.")] int maxDepth = 6,
        [Description("How many chains to return, shortest first.")] int top = 5,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        return await FindAsync(solution, await SymbolTree.ResolveAsync(solution, from, cancellationToken), await SymbolTree.ResolveAsync(solution, to, cancellationToken),
            maxDepth, top, cancellationToken);
    }

    /// <summary>
    /// Walks the callers back from the targets, breadth first, until the starts are reached and the level that reached them is done, or the
    /// depth runs out; then lists the chains from the starts forward through the calls found, shortest first.
    /// </summary>
    internal static async Task<IReadOnlyList<CallPath>> FindAsync(
        Solution solution, IReadOnlyList<ISymbol> starts, IReadOnlyList<ISymbol> targets, int maxDepth, int top, CancellationToken cancellationToken)
    {
        var comparer = SymbolEqualityComparer.Default;
        var startSet = starts.ToHashSet(comparer);
        // Per caller, the members it calls, each with where.
        var calls = new Dictionary<ISymbol, List<(ISymbol Callee, Location Call)>>(comparer);
        var depthOf = targets.ToDictionary(target => target, _ => 0, comparer);
        IReadOnlyList<ISymbol> frontier = [.. targets];
        for (var depth = 1; depth <= maxDepth && frontier.Count > 0 && !startSet.Overlaps(depthOf.Keys); depth++)
        {
            var next = new List<ISymbol>();
            foreach (var callee in frontier)
            {
                // Code reaches an implementation or override through the interface or base member it calls.
                var found = new List<(ISymbol Caller, Location Call)>();
                foreach (var named in (ISymbol[])[callee, .. Bases(callee)])
                {
                    found.AddRange((await SymbolFinder.FindCallersAsync(named, solution, cancellationToken))
                        .SelectMany(caller => caller.Locations.Where(location => location.IsInSource).Take(1).Select(location => (caller.CallingSymbol, location))));
                    found.AddRange(await CallersTool.MissedCallsAsync(named, solution, cancellationToken));
                }

                foreach (var (caller, call) in found)
                {
                    var member = MemberOf(caller);
                    if (!calls.TryGetValue(member, out var callees))
                        calls[member] = callees = [];
                    if (!callees.Any(existing => comparer.Equals(existing.Callee, callee)))
                        callees.Add((callee, call));
                    if (depthOf.TryAdd(member, depth))
                        next.Add(member);
                }
            }

            frontier = next;
        }

        // Forward from the starts, each step to a callee one level nearer the targets, so every chain is as short as it can be from there.
        var paths = new List<CallPath>();
        foreach (var start in starts.Where(depthOf.ContainsKey))
        {
            var pending = new Stack<(ISymbol Member, List<CallHop> Hops)>([(start, [])]);
            while (pending.Count > 0 && paths.Count < top)
            {
                var (member, hops) = pending.Pop();
                if (depthOf[member] == 0)
                {
                    paths.Add(new(hops));
                    continue;
                }

                foreach (var (callee, call) in (calls.GetValueOrDefault(member) ?? []).Where(edge => depthOf.GetValueOrDefault(edge.Callee, int.MaxValue) == depthOf[member] - 1).Reverse())
                    pending.Push((callee, [.. hops, new(SymbolEntry.From(member), SourceLocation.From(call), SymbolEntry.From(callee), await ViaAsync(solution, call, callee, cancellationToken))]));
            }
        }

        return paths.Count > 0 || startSet.Overlaps(targets) ? [.. paths.OrderBy(path => path.Hops.Count)]
            : throw new McpException($"No chain of at most {maxDepth} calls leads from {starts[0].ToDisplayString()} to {targets[0].ToDisplayString()}.");
    }

    /// <summary>The members a member overrides, all the way up, and the interface members it implements.</summary>
    private static IEnumerable<ISymbol> Bases(ISymbol symbol)
    {
        for (var overridden = Overridden(symbol); overridden is not null; overridden = Overridden(overridden))
            yield return overridden;
        if (symbol.ContainingType is not { } type)
            yield break;

        foreach (var member in type.AllInterfaces.SelectMany(face => face.GetMembers()))
        {
            if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), symbol))
                yield return member;
        }

        static ISymbol? Overridden(ISymbol member) => member switch
        {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            _ => null,
        };
    }

    /// <summary>The member a lambda's code belongs to: callers report the lambda itself.</summary>
    private static ISymbol MemberOf(ISymbol caller)
    {
        while (caller is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } && caller.ContainingSymbol is { } container)
            caller = container;
        return caller;
    }

    /// <summary>The member a call names, if it is not the callee, as when it calls through an interface or a base class.</summary>
    private static async Task<string?> ViaAsync(Solution solution, Location call, ISymbol callee, CancellationToken cancellationToken)
    {
        if (solution.GetDocument(call.SourceTree) is not { } document || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
            return null;

        var node = (await call.SourceTree!.GetRootAsync(cancellationToken)).FindNode(call.SourceSpan, getInnermostNodeForTie: true);
        return model.GetSymbolInfo(node, cancellationToken).Symbol is { } named && !SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, callee.OriginalDefinition)
            ? named.ToDisplayString(SymbolEntry.Format)
            : null;
    }

    /// <param name="Hops">From the first member to the last, each call in turn.</param>
    public sealed record CallPath(IReadOnlyList<CallHop> Hops);

    /// <param name="Caller">The member that calls.</param>
    /// <param name="Call">Where it calls.</param>
    /// <param name="Callee">The member it reaches.</param>
    /// <param name="Via">The member the call names when that is not Callee, such as the interface method an implementation is called through.</param>
    public sealed record CallHop(SymbolEntry Caller, SourceLocation Call, SymbolEntry Callee, string? Via);
}
