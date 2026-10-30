using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed partial class AffectedTestsTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => FindAffectedTestsAsync;

    [McpServerTool(Name = "affected_tests", ReadOnly = true)]
    [Description("Finds the tests a change can affect: maps the lines of C# files that differ from a git commit (uncommitted changes by default, "
        + "new untracked files included) to the members they are in, then finds the tests that use those members, directly or through up to 3 "
        + "calls in between, as find_tests does. Returns the changed members, the tests, and one Filter for run_tests that runs them all; and "
        + "the members added and removed since that commit, as for reviewing a branch or writing its description.")]
    private async Task<AffectedTests> FindAffectedTestsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The git commit, branch or tag to compare the working tree with, such as HEAD, main or HEAD~3.")] string baseRef = "HEAD",
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var changes = await ChangedLinesAsync(directory, baseRef, cancellationToken);
        var (added, removed) = await MemberChangesAsync(directory, baseRef, changes.Keys, cancellationToken);
        return await FindAsync(solution, changes, cancellationToken) with { AddedMembers = added, RemovedMembers = removed };
    }

    /// <summary>
    /// The members declared in the working tree that a commit did not declare, and the other way round, in the C# files that differ from it
    /// and those deleted since. Compared by syntax, by their containers, names and parameter types as written: a member whose signature
    /// changed is removed in its old form and added in its new one.
    /// </summary>
    internal static async Task<(IReadOnlyList<string> Added, IReadOnlyList<string> Removed)> MemberChangesAsync(
        string directory, string baseRef, IEnumerable<string> changedFiles, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath((await GitAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken)).Trim());
        var deleted = (await GitAsync(root, ["diff", "--no-color", "--name-only", "--diff-filter=D", baseRef, "--", "*.cs"], cancellationToken))
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(file => Path.GetFullPath(Path.Combine(root, file)));
        var (added, removed) = (new List<string>(), new List<string>());
        foreach (var file in changedFiles.Concat(deleted).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string before;
            try
            {
                before = await GitAsync(root, ["show", $"{baseRef}:{Path.GetRelativePath(root, file).Replace('\\', '/')}"], cancellationToken);
            }
            catch (McpException)
            {
                // New since the commit.
                before = "";
            }

            var (old, now) = (MembersOf(before), MembersOf(File.Exists(file) ? await File.ReadAllTextAsync(file, cancellationToken) : ""));
            added.AddRange(now.Except(old));
            removed.AddRange(old.Except(now));
        }

        return (added, removed);
    }

    /// <summary>The members a file declares, as "Namespace.Type.Member(ParameterTypes)", by its syntax alone.</summary>
    internal static HashSet<string> MembersOf(string code)
    {
        var members = new HashSet<string>();
        foreach (var member in CSharpSyntaxTree.ParseText(code).GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            var names = member switch
            {
                BaseNamespaceDeclarationSyntax => [],
                BaseFieldDeclarationSyntax field => field.Declaration.Variables.Select(variable => variable.Identifier.ValueText),
                _ => NameOf(member) is { } name ? [name] : Enumerable.Empty<string>(),
            };
            var containers = member.Ancestors().Reverse().Select(NameOf).OfType<string>();
            var parameters = member switch
            {
                BaseMethodDeclarationSyntax method => $"({string.Join(", ", method.ParameterList.Parameters.Select(parameter => parameter.Type?.ToString()))})",
                IndexerDeclarationSyntax indexer => $"[{string.Join(", ", indexer.ParameterList.Parameters.Select(parameter => parameter.Type?.ToString()))}]",
                _ => "",
            };
            foreach (var name in names)
                members.Add(string.Join('.', [.. containers, name]) + parameters);
        }

        return members;

        static string? NameOf(SyntaxNode node) => node switch
        {
            BaseNamespaceDeclarationSyntax space => space.Name.ToString(),
            BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
            DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax => ".ctor",
            DestructorDeclarationSyntax => "Finalize",
            OperatorDeclarationSyntax @operator => $"operator {@operator.OperatorToken.ValueText}",
            ConversionOperatorDeclarationSyntax conversion => $"{conversion.ImplicitOrExplicitKeyword.ValueText} operator {conversion.Type}",
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            EventDeclarationSyntax @event => @event.Identifier.ValueText,
            IndexerDeclarationSyntax => "this",
            EnumMemberDeclarationSyntax member => member.Identifier.ValueText,
            _ => null,
        };
    }

    /// <param name="changes">The changed 1-based lines per file path; null lines for a file that is new as a whole.</param>
    internal static async Task<AffectedTests> FindAsync(Solution solution, IReadOnlyDictionary<string, IReadOnlyCollection<int>?> changes, CancellationToken cancellationToken)
    {
        // ponytail: one caller search per changed member, as find_tests does; a big diff over a big solution takes a while.
        var members = await ChangedMembersAsync(solution, changes, cancellationToken);
        var tests = (await FindTestsTool.FindAsync(solution, members, cancellationToken)).DistinctBy(test => test.Filter).ToList();
        return new(
            [.. members.Select(member => SymbolEntry.From(member))],
            tests,
            tests.Count == 0 ? null : string.Join("|", tests.Select(test => test.Filter)));
    }

    /// <summary>
    /// The innermost members that the changed lines touch; a type only where a line touches its declaration line (attributes, modifiers,
    /// base types), not its body, since a blank line or brace between members changes nothing.
    /// </summary>
    internal static async Task<IReadOnlyList<ISymbol>> ChangedMembersAsync(
        Solution solution, IReadOnlyDictionary<string, IReadOnlyCollection<int>?> changes, CancellationToken cancellationToken)
    {
        var members = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var (path, lines) in changes)
        {
            // A linked file is in several projects, with a symbol in each.
            foreach (var document in solution.GetDocumentIdsWithFilePath(path).Select(solution.GetDocument).OfType<Document>())
            {
                if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
                    continue;

                var text = await document.GetTextAsync(cancellationToken);
                foreach (var number in (lines ?? Enumerable.Range(1, text.Lines.Count)).Where(number => number >= 1 && number <= text.Lines.Count))
                {
                    var span = text.Lines[number - 1].Span;
                    if (string.IsNullOrWhiteSpace(text.ToString(span)))
                        continue;

                    var touched = root.DescendantNodes(span).Where(node => node.Span.IntersectsWith(span) && Touches(node, span)).ToList();
                    foreach (var member in touched.Where(member => !touched.Any(other => other != member && member.Span.Contains(other.Span))))
                    {
                        members.UnionWith((member is BaseFieldDeclarationSyntax field
                            ? field.Declaration.Variables.Select(variable => model.GetDeclaredSymbol(variable, cancellationToken))
                            : [model.GetDeclaredSymbol(member, cancellationToken)]).OfType<ISymbol>());
                    }
                }
            }
        }

        return [.. members];

        static bool Touches(SyntaxNode node, TextSpan span) => node switch
        {
            BaseNamespaceDeclarationSyntax => false,
            BaseTypeDeclarationSyntax type => span.IntersectsWith(TextSpan.FromBounds(
                type.SpanStart, type.OpenBraceToken.Span.IsEmpty ? type.Span.End : type.OpenBraceToken.GetPreviousToken().Span.End)),
            MemberDeclarationSyntax => true,
            _ => false,
        };
    }

    /// <summary>The lines of C# files in the working tree that differ from a commit, with untracked C# files as new as a whole.</summary>
    internal static async Task<Dictionary<string, IReadOnlyCollection<int>?>> ChangedLinesAsync(string directory, string baseRef, CancellationToken cancellationToken)
    {
        var root = (await GitAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken)).Trim();
        // The prefixes ParseDiff reads, whatever diff.noprefix or diff.mnemonicPrefix in the user's configuration say.
        var changes = ParseDiff(root, await GitAsync(root, ["diff", "--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/", "-U0", baseRef, "--", "*.cs"], cancellationToken));
        foreach (var file in (await GitAsync(root, ["ls-files", "--others", "--exclude-standard", "--", "*.cs"], cancellationToken)).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            changes[Path.GetFullPath(Path.Combine(root, file))] = null;

        return changes;
    }

    internal static async Task<string> GitAsync(string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        // Paths come back as they are, not with octal escapes for non-ASCII characters.
        var (exitCode, output) = await DotnetCli.RunAsync("git", ["-C", directory, "-c", "core.quotePath=false", .. arguments], TimeSpan.FromMinutes(1), cancellationToken);
        return exitCode == 0 ? output : throw new McpException($"git {arguments[0]} failed: {output.Trim()}");
    }

    /// <summary>The changed lines per file from a unified diff with no context lines; lines of the new version, 1-based.</summary>
    internal static Dictionary<string, IReadOnlyCollection<int>?> ParseDiff(string root, string diff)
    {
        var changes = new Dictionary<string, IReadOnlyCollection<int>?>(StringComparer.OrdinalIgnoreCase);
        HashSet<int>? lines = null;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                // "+++ /dev/null" for a deleted file, which has no members left.
                lines = null;
                if (line[4..].TrimEnd('\r', '\t') is ['b', '/', .. var path])
                    changes[Path.GetFullPath(Path.Combine(root, path))] = lines = [];
            }
            else if (lines is not null && HunkHeader().Match(line) is { Success: true } hunk)
            {
                var start = int.Parse(hunk.Groups[1].ValueSpan);
                var count = hunk.Groups[2].Success ? int.Parse(hunk.Groups[2].ValueSpan) : 1;

                // Removed lines leave nothing behind; take the lines on either side of where they were.
                lines.UnionWith(count == 0 ? [start, start + 1] : Enumerable.Range(start, count));
            }
        }

        return changes;
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();

    /// <param name="ChangedMembers">The members, or types, that contain changed lines.</param>
    /// <param name="Filter">A dotnet test --filter expression for run_tests that runs all the tests; null if there are none.</param>
    /// <param name="AddedMembers">The members declared now that the commit did not declare, as "Namespace.Type.Member(ParameterTypes)".</param>
    /// <param name="RemovedMembers">The members the commit declared that are gone now, a changed signature included.</param>
    public sealed record AffectedTests(
        IReadOnlyList<SymbolEntry> ChangedMembers, IReadOnlyList<FindTestsTool.TestEntry> Tests, string? Filter,
        IReadOnlyList<string>? AddedMembers = null, IReadOnlyList<string>? RemovedMembers = null);
}
