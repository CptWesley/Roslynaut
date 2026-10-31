using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>find_duplicates: runs of statements copied with other names, reported once, as long as the copies agree.</summary>
public class FindDuplicatesTests
{
    private static readonly Microsoft.CodeAnalysis.Solution Copies = TestSolution.Create("""
        public class Report
        {
            public string Orders(int[] amounts)
            {
                var total = 0;
                foreach (var amount in amounts)
                    total += amount;
                var average = amounts.Length == 0 ? 0 : total / amounts.Length;
                var text = "Orders: " + total + " in all, " + average + " on average";
                System.Console.WriteLine(text);
                return text;
            }

            public string Refunds(int[] values)
            {
                var sum = 0;
                foreach (var value in values)
                    sum += value;
                var mean = values.Length == 0 ? 0 : sum / values.Length;
                var line = "Refunds: " + sum + " in all, " + mean + " on average";
                System.Console.WriteLine(line);
                return line;
            }

            public int Other(int x)
            {
                var y = x * 2;
                return y;
            }
        }
        """);

    [Test]
    public async Task Copies_WithOtherNames_AreOneGroup_AsLongAsTheyAgree()
    {
        var groups = await FindDuplicatesTool.FindAsync(Copies, minStatements: 3, top: 10, CancellationToken.None);

        var group = groups.Should().ContainSingle().Subject;
        (group.Statements, group.Kind).Should().Be((6, "Renamed"));
        group.Copies.Select(copy => (copy.Member, copy.StartLine, copy.EndLine)).Should().BeEquivalentTo([
            ("Report.Orders(int[])", 5, 11), ("Report.Refunds(int[])", 16, 22)]);
    }

    [Test]
    public async Task LongerThanAnyCopy_FindsNone()
    {
        (await FindDuplicatesTool.FindAsync(Copies, minStatements: 7, top: 10, CancellationToken.None)).Should().BeEmpty();
    }
}
