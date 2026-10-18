using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class NamespaceGraphTool(WorkspaceService workspace) : IMcpTool
{
    private const int MaxExamples = 3;

    public Delegate Handler => GetGraphAsync;

    [McpServerTool(Name = "namespace_graph", ReadOnly = true)]
    [Description("Maps which namespaces of the solution's source use which others, as an architecture view: each dependency with how many types "
        + "of one use types of the other, and examples; and the cycles, groups of namespaces that depend on each other, which break layering. "
        + "project_graph shows the same between projects.")]
    private async Task<NamespaceGraph> GetGraphAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Optional number of leading name parts to group namespaces by, such as 2 to count MyApp.Orders.Domain as MyApp.Orders.")] int? depth = null,
        [Description("Optional namespace prefix to keep, such as 'MyApp.Orders'; dependencies on other namespaces of the solution still show.")] string? prefix = null,
        CancellationToken cancellationToken = default) =>
        await BuildAsync((await workspace.GetAsync(projectPath)).Solution, depth, prefix, cancellationToken);

    internal static async Task<NamespaceGraph> BuildAsync(Solution solution, int? depth, string? prefix, CancellationToken cancellationToken)
    {
        // The pairs of types that use each other, by the pair of namespaces they are in.
        var uses = new Dictionary<(string From, string To), HashSet<(string From, string To)>>();
        foreach (var document in solution.Projects.Where(project => project.Language == LanguageNames.CSharp).SelectMany(project => project.Documents))
        {
            if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
                continue;

            foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
                if ((symbol as INamedTypeSymbol ?? symbol?.ContainingType)?.OriginalDefinition is not { } used || !used.Locations.Any(location => location.IsInSource)
                    || UserOf(name, model, cancellationToken) is not { } user)
                    continue;

                var (from, to) = (NamespaceOf(user, depth), NamespaceOf(used, depth));
                if (from == to || (prefix is not null && !from.StartsWith(prefix, StringComparison.Ordinal)))
                    continue;

                if (!uses.TryGetValue((from, to), out var pairs))
                    uses[(from, to)] = pairs = [];
                pairs.Add((user.ToDisplayString(), used.ToDisplayString()));
            }
        }

        return new(
            [.. uses.OrderBy(edge => edge.Key.From, StringComparer.Ordinal).ThenBy(edge => edge.Key.To, StringComparer.Ordinal).Select(edge => new NamespaceEdge(
                edge.Key.From,
                edge.Key.To,
                edge.Value.Count,
                [.. edge.Value.Order().Take(MaxExamples).Select(pair => $"{pair.From} -> {pair.To}")]))],
            Cycles(uses.Keys));
    }

    /// <summary>The type whose code a name is in: its type declaration, a delegate, or for top-level statements their Program.</summary>
    private static INamedTypeSymbol? UserOf(SimpleNameSyntax name, SemanticModel model, CancellationToken cancellationToken) =>
        name.FirstAncestorOrSelf<SyntaxNode>(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or GlobalStatementSyntax) switch
        {
            GlobalStatementSyntax => model.GetEnclosingSymbol(name.SpanStart, cancellationToken)?.ContainingType,
            { } declaration => model.GetDeclaredSymbol(declaration, cancellationToken) as INamedTypeSymbol,
            null => null,
        };

    private static string NamespaceOf(INamedTypeSymbol type, int? depth)
    {
        var ns = type.ContainingNamespace.IsGlobalNamespace ? "<global>" : type.ContainingNamespace.ToDisplayString();
        return depth is > 0 ? string.Join('.', ns.Split('.').Take(depth.Value)) : ns;
    }

    /// <summary>The strongly connected components with more than one namespace, found with Tarjan's algorithm.</summary>
    internal static IReadOnlyList<IReadOnlyList<string>> Cycles(IEnumerable<(string From, string To)> edges)
    {
        var next = edges.GroupBy(edge => edge.From, edge => edge.To).ToDictionary(group => group.Key, group => group.ToList());
        var (index, low, onStack, stack, cycles) = (new Dictionary<string, int>(), new Dictionary<string, int>(), new HashSet<string>(), new Stack<string>(), new List<IReadOnlyList<string>>());

        // ponytail: recursive, so a dependency chain thousands of namespaces deep would overflow the stack.
        void Visit(string node)
        {
            index[node] = low[node] = index.Count;
            stack.Push(node);
            onStack.Add(node);
            foreach (var target in next.GetValueOrDefault(node) ?? [])
            {
                if (!index.ContainsKey(target))
                {
                    Visit(target);
                    low[node] = Math.Min(low[node], low[target]);
                }
                else if (onStack.Contains(target))
                {
                    low[node] = Math.Min(low[node], index[target]);
                }
            }

            if (low[node] != index[node])
                return;

            var component = new List<string>();
            string member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component.Add(member);
            }
            while (member != node);

            if (component.Count > 1)
                cycles.Add([.. component.Order(StringComparer.Ordinal)]);
        }

        foreach (var node in next.Keys.Order(StringComparer.Ordinal).Where(node => !index.ContainsKey(node)))
            Visit(node);

        return [.. cycles.OrderBy(cycle => cycle[0], StringComparer.Ordinal)];
    }

    /// <param name="Cycles">Groups of namespaces that all depend on each other, directly or through others in the group.</param>
    public sealed record NamespaceGraph(IReadOnlyList<NamespaceEdge> Dependencies, IReadOnlyList<IReadOnlyList<string>> Cycles);

    /// <param name="Types">How many distinct pairs of types, one in From using one in To, make up the dependency.</param>
    /// <param name="Examples">Some of those pairs, as "From.Type -> To.Type".</param>
    public sealed record NamespaceEdge(string From, string To, int Types, IReadOnlyList<string> Examples);
}
