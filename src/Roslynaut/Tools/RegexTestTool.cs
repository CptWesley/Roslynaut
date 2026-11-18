using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class RegexTestTool : IMcpTool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public Delegate Handler => Test;

    [McpServerTool(Name = "regex_test", ReadOnly = true, Idempotent = true)]
    [Description("Runs a .NET regular expression against sample inputs, as System.Text.RegularExpressions.Regex does, not another engine's "
        + "dialect: whether each input matches, each match with its position and its groups by number and name, and with replacement what "
        + "Regex.Replace returns. For writing or checking a pattern before it goes into code, such as into [GeneratedRegex]; no evaluate needed.")]
    private static RegexResult Test(
        [Description("The pattern as the string the code passes, after C# unescapes it: \\d+ for @\"\\d+\" or \"\\\\d+\".")] string pattern,
        [Description("The inputs to match against.")] string[] inputs,
        [Description("RegexOptions, comma-separated, e.g. 'IgnoreCase, Multiline'; also Singleline, ExplicitCapture, IgnorePatternWhitespace, "
            + "RightToLeft, ECMAScript, CultureInvariant and NonBacktracking.")] string? options = null,
        [Description("Optional replacement, as Regex.Replace takes it: $1, ${name}, $0 or $& for the whole match, $$ for a dollar sign.")] string? replacement = null,
        [Description("How many matches to return per input.")] int top = 50) =>
        Run(pattern, inputs, options, replacement, top);

    internal static RegexResult Run(string pattern, IReadOnlyList<string> inputs, string? options, string? replacement, int top)
    {
        if (!Enum.TryParse<RegexOptions>(options ?? nameof(RegexOptions.None), ignoreCase: true, out var parsed))
            throw new McpException($"'{options}' is not a combination of RegexOptions, such as 'IgnoreCase, Multiline'.");

        Regex regex;
        try
        {
            regex = new Regex(pattern, parsed, Timeout);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            throw new McpException($"Invalid pattern: {e.Message}");
        }

        // Group 0 is the match itself.
        var groups = regex.GetGroupNames().Skip(1).ToList();
        try
        {
            return new([.. inputs.Select(input =>
            {
                var matches = regex.Matches(input);
                return new InputResult(input, matches.Count, [.. matches.Take(top).Select(match => new RegexMatch(match.Index, match.Length, match.Value,
                    groups.Count == 0 ? null : groups.ToDictionary(name => name, name => match.Groups[name] is { Success: true } group ? group.Value : null)))],
                    replacement is null ? null : regex.Replace(input, replacement));
            })]);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new McpException($"Matching took more than {Timeout.TotalSeconds} seconds, as catastrophic backtracking does, for instance with nested "
                + "quantifiers such as (a+)+; the option NonBacktracking matches in linear time.");
        }
        catch (ArgumentException e)
        {
            throw new McpException($"Invalid replacement: {e.Message}");
        }
    }

    public sealed record RegexResult(IReadOnlyList<InputResult> Inputs);

    /// <param name="Count">How many matches the input has; Matches holds the first of them, up to top.</param>
    /// <param name="Replaced">With replacement: the input with the matches replaced.</param>
    public sealed record InputResult(string Input, int Count, IReadOnlyList<RegexMatch> Matches, string? Replaced);

    /// <param name="Index">The 0-based position in the input where the match starts.</param>
    /// <param name="Groups">The groups by number or name, null for those that took part in no match; none for a pattern without groups.</param>
    public sealed record RegexMatch(int Index, int Length, string Value, IReadOnlyDictionary<string, string?>? Groups);
}
