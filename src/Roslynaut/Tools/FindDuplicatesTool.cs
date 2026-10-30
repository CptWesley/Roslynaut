using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class FindDuplicatesTool(WorkspaceService workspace) : IMcpTool
{
    // The fewest tokens a copy has to have to count.
    private const int MinTokens = 40;

    public Delegate Handler => FindCopiesAsync;

    [McpServerTool(Name = "find_duplicates", ReadOnly = true)]
    [Description("Finds copied code in the solution's C# files: runs of at least minStatements statements in a row that occur more than once, "
        + "word for word or with other names and literals (Renamed), each run as long as the copies agree, with the files, lines and members "
        + "of every copy, the most code first. Before a refactoring, or to find every copy of code a bug was just fixed in. Generated files "
        + "are left out.")]
    private async Task<IReadOnlyList<DuplicateGroup>> FindCopiesAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The fewest statements in a row that count as a copy.")] int minStatements = 5,
        [Description("How many groups of copies to return.")] int top = 20,
        CancellationToken cancellationToken = default) =>
        await FindAsync((await workspace.GetAsync(projectPath)).Solution, minStatements, top, cancellationToken);

    private sealed record Run(SyntaxList<StatementSyntax> Block, int Start);

    /// <summary>
    /// Hashes every window of minStatements statements of every block, with names and literals made alike; windows that hash alike in several
    /// places are copies. A copy that goes on is reported once, from its first window, as long as all its places go on alike.
    /// </summary>
    internal static async Task<IReadOnlyList<DuplicateGroup>> FindAsync(Solution solution, int minStatements, int top, CancellationToken cancellationToken)
    {
        minStatements = Math.Max(1, minStatements);
        var windows = new Dictionary<string, List<Run>>();
        var trees = new HashSet<SyntaxTree>();
        foreach (var document in solution.Projects.Where(project => project.Language == LanguageNames.CSharp).SelectMany(project => project.Documents))
        {
            // A file linked into several projects is one file.
            if (await document.GetSyntaxTreeAsync(cancellationToken) is not { } tree || !trees.Add(tree) || IsGenerated(tree))
                continue;

            foreach (var block in (await tree.GetRootAsync(cancellationToken)).DescendantNodes().Select(StatementsOf).Where(statements => statements.Count >= minStatements))
            {
                for (var start = 0; start + minStatements <= block.Count; start++)
                {
                    var run = block.Skip(start).Take(minStatements).ToList();
                    var tokens = run.SelectMany(statement => statement.DescendantTokens()).ToList();

                    var key = string.Join(" ", tokens.Select(Normalized));
                    if (!windows.TryGetValue(key, out var places))
                        windows[key] = places = [];
                    places.Add(new(block, start));
                }
            }
        }

        // Where each window's copies are, to tell a window that only continues the copy before it.
        var copies = windows.Values.Where(places => places.Count > 1).ToList();
        var groupOf = new Dictionary<(SyntaxList<StatementSyntax>, int), List<Run>>();
        foreach (var places in copies)
            foreach (var place in places)
                groupOf[(place.Block, place.Start)] = places;

        bool Continues(List<Run> places, int offset) => places.All(place => groupOf.TryGetValue((place.Block, place.Start + offset), out var other)
            && other.Count == places.Count && places.All(sibling => other.Any(next => next.Block == sibling.Block && next.Start == sibling.Start + offset)));

        var groups = new List<(List<Run> Places, int Length)>();
        foreach (var places in copies.Where(places => !Continues(places, -1)))
        {
            var extra = 0;
            while (Continues(places, extra + 1))
                extra++;
            // Trivial copies, such as a few assignments in a row, are not worth a refactoring.
            var length = minStatements + extra;
            if (places[0].Block.Skip(places[0].Start).Take(length).Sum(statement => statement.DescendantTokens().Count()) >= MinTokens)
                groups.Add((places, length));
        }

        var result = new List<DuplicateGroup>();
        foreach (var (places, length) in groups.OrderByDescending(group => group.Length * group.Places.Count).Take(top))
        {
            var ranges = new List<CopyRange>();
            foreach (var place in places)
                ranges.Add(await RangeOfAsync(solution, place.Block, place.Start, length, cancellationToken));
            var texts = places.Select(place => string.Join(" ", place.Block.Skip(place.Start).Take(length).SelectMany(statement => statement.DescendantTokens()).Select(token => token.Text)));
            result.Add(new(length, texts.Distinct().Count() == 1 ? "Identical" : "Renamed", ranges));
        }

        return result;
    }

    private static SyntaxList<StatementSyntax> StatementsOf(SyntaxNode node) => node switch
    {
        BlockSyntax block => block.Statements,
        SwitchSectionSyntax section => section.Statements,
        _ => default,
    };

    /// <summary>A token with names and literals made alike, so that copies with other names match.</summary>
    private static string Normalized(SyntaxToken token) => token.Kind() switch
    {
        SyntaxKind.IdentifierToken => "id",
        SyntaxKind.NumericLiteralToken or SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken or SyntaxKind.InterpolatedStringTextToken => "lit",
        _ => token.Text,
    };

    private static bool IsGenerated(SyntaxTree tree) =>
        tree.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || tree.FilePath.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
        || tree.GetRoot().GetLeadingTrivia().ToString().Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);

    private static async Task<CopyRange> RangeOfAsync(Solution solution, SyntaxList<StatementSyntax> block, int start, int length, CancellationToken cancellationToken)
    {
        var (first, last) = (block[start], block[Math.Min(start + length, block.Count) - 1]);
        var span = first.SyntaxTree.GetLineSpan(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(first.SpanStart, last.Span.End));
        var member = solution.GetDocument(first.SyntaxTree) is { } document && await document.GetSemanticModelAsync(cancellationToken) is { } model
            ? ReferencesTool.EnclosingMemberOf(model, first.SpanStart, cancellationToken)
            : null;
        return new(first.SyntaxTree.FilePath, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, member is null ? null : SymbolEntry.PathOf(member));
    }

    /// <param name="Statements">How many statements in a row the copies share.</param>
    /// <param name="Kind">Identical, word for word; or Renamed, alike but for names and literals.</param>
    public sealed record DuplicateGroup(int Statements, string Kind, IReadOnlyList<CopyRange> Copies);

    /// <param name="Member">The member the copy is in, by symbol path.</param>
    public sealed record CopyRange(string FilePath, int StartLine, int EndLine, string? Member);
}
