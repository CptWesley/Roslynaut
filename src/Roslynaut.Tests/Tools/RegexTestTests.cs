using ModelContextProtocol;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

public class RegexTestTests
{
    [Test]
    public void Matches_HaveTheirPositions_AndGroupsByNumberAndName()
    {
        var result = RegexTestTool.Run(@"(\w+)@(?<host>\w+)(\.com)?", ["ann@example and bob@shop.com", "none"], null, null, top: 10);

        result.Inputs[0].Count.Should().Be(2);
        result.Inputs[0].Matches[1].Should().BeEquivalentTo(new { Index = 16, Length = 12, Value = "bob@shop.com" });
        result.Inputs[0].Matches[1].Groups.Should().Equal(new Dictionary<string, string?> { ["1"] = "bob", ["2"] = ".com", ["host"] = "shop" });
        result.Inputs[0].Matches[0].Groups!["2"].Should().BeNull();
        result.Inputs[1].Should().BeEquivalentTo(new { Count = 0, Matches = Array.Empty<RegexTestTool.RegexMatch>() });
    }

    [Test]
    public void Options_AndReplacement_ApplyAsInCode()
    {
        var result = RegexTestTool.Run("^b(?<n>\\d)", ["A1\nb2\nB3"], "IgnoreCase, Multiline", "[${n}]", top: 10);

        result.Inputs[0].Matches.Select(match => match.Value).Should().Equal("b2", "B3");
        result.Inputs[0].Replaced.Should().Be("A1\n[2]\n[3]");
    }

    [Test]
    public void Top_LimitsTheMatches_NotTheCount()
    {
        var input = RegexTestTool.Run("a", ["aaaa"], null, null, top: 2).Inputs[0];

        (input.Count, input.Matches.Count).Should().Be((4, 2));
        input.Matches[0].Groups.Should().BeNull();
    }

    [TestCase("(", null, "Invalid pattern")]
    [TestCase("a", "IgnoreCase, Sideways", "not a combination of RegexOptions")]
    [TestCase("(?<=a)b", "NonBacktracking", "Invalid pattern")]
    public void Mistakes_AreExplained(string pattern, string? options, string message)
    {
        FluentActions.Invoking(() => RegexTestTool.Run(pattern, ["ab"], options, null, top: 10)).Should().Throw<McpException>().WithMessage($"*{message}*");
    }

    // Nested quantifiers backtrack exponentially on an input that almost matches; the timeout ends it.
    [Test]
    public void CatastrophicBacktracking_TimesOut()
    {
        FluentActions.Invoking(() => RegexTestTool.Run("^(a+)+$", [new string('a', 40) + "!"], null, null, top: 10))
            .Should().Throw<McpException>().WithMessage("*catastrophic backtracking*NonBacktracking*");
    }
}
