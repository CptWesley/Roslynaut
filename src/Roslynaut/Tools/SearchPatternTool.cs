using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class SearchPatternTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => SearchPatternAsync;

    [McpServerTool(Name = "search_pattern", ReadOnly = true)]
    [Description("Searches the text of the solution's C# files, including generated ones that are not on disk, with a regular expression; "
        + "within a file, a project or a symbol's declaration if asked. Returns each match, once per line it starts on, with its lines and context lines, numbered. Paged.")]
    private async Task<PagedResult<TextMatch>> SearchPatternAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("A .NET regular expression; ^ and $ match at line ends, and it can span lines, e.g. 'async void' or 'catch\\s*\\{\\s*\\}'.")] string pattern,
        [Description(CodeScope.Description)] string? scope = null,
        [Description("How many lines of context to show before and after each match.")] int context = 0,
        [Description("Match case.")] bool caseSensitive = false,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Multiline | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException e)
        {
            throw new McpException($"Invalid pattern: {e.Message}");
        }

        var solution = (await workspace.GetAsync(projectPath)).Solution;
        return await cache.GetPageAsync(solution, $"search_pattern|{pattern}|{scope}|{context}|{caseSensitive}", page, pageSize,
            () => SearchAsync(solution, regex, scope, context, cancellationToken));
    }

    internal static async Task<IEnumerable<TextMatch>> SearchAsync(Solution solution, Regex regex, string? scope, int context, CancellationToken cancellationToken)
    {
        var matches = new List<TextMatch>();
        foreach (var (document, span) in await CodeScope.ResolveAsync(solution, scope, cancellationToken))
        {
            // ^ and $ only match at \n, so search the text with \n line ends, which has the same lines.
            var original = await document.GetTextAsync(cancellationToken);
            var content = original.ToString();
            var text = content.Contains('\r') ? SourceText.From(content.Replace("\r\n", "\n")) : original;
            int Position(int position) => text.Lines.GetPosition(original.Lines.GetLinePosition(position));
            var searched = span is { } scoped ? TextSpan.FromBounds(Position(scoped.Start), Position(scoped.End)) : new(0, text.Length);
            var start = searched.Start;
            foreach (Match match in regex.Matches(text.ToString(searched)))
            {
                var first = text.Lines.GetLineFromPosition(start + match.Index).LineNumber;

                // Each line once, however often it matches.
                if (matches is [.., var previous] && previous.Line == first + 1 && previous.FilePath == (document.FilePath ?? document.Name))
                    continue;
                var last = text.Lines.GetLineFromPosition(start + match.Index + Math.Max(match.Length - 1, 0)).LineNumber;
                var lines = new StringBuilder();
                for (var line = Math.Max(first - context, 0); line <= Math.Min(last + context, text.Lines.Count - 1); line++)
                    lines.Append(line + 1).Append(": ").Append(text.Lines[line]).Append('\n');
                matches.Add(new(document.FilePath ?? document.Name, first + 1, lines.ToString()));
            }
        }

        return matches;
    }

    /// <param name="Line">The 1-based line the match starts on.</param>
    /// <param name="Lines">The lines of the match and its context, each starting with its number.</param>
    public sealed record TextMatch(string FilePath, int Line, string Lines);
}
