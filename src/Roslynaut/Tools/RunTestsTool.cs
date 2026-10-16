using System.ComponentModel;
using System.Xml.Linq;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class RunTestsTool : IMcpTool
{
    private const int MaxFailures = 50;
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public Delegate Handler => RunTestsAsync;

    [McpServerTool(Name = "run_tests")]
    [Description("Builds and runs tests with dotnet test, and returns the counts and the failures with their messages and stack traces (at most 50). "
        + "When the build fails, BuildErrors says why. Output is the end of the log when there is nothing else to go on. With coverage, also the "
        + "lines of each source file that no test ran.")]
    internal static async Task<TestRun> RunTestsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("A dotnet test --filter expression, such as a Filter from find_tests, 'FullyQualifiedName~Namespace.Class' or 'TestCategory=Fast'. "
            + "Omit to run all tests.")] string? filter = null,
        [Description("The configuration, such as Debug or Release.")] string configuration = "Debug",
        [Description("Also measure code coverage: per source file, how many of its lines ran and which did not. Slower.")] bool coverage = false,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(projectPath);
        if (!File.Exists(path))
            throw new McpException($"Project or solution not found: {path}");

        // ponytail: assumes the VSTest runner, the default; projects on Microsoft.Testing.Platform's dotnet test take --report-trx instead.
        var results = Directory.CreateTempSubdirectory("roslynaut-tests-");
        try
        {
            List<string> arguments = ["test", path, "-c", configuration, "-v:q", "--logger", "trx", "--results-directory", results.FullName];
            if (filter is not null)
                arguments.AddRange(["--filter", filter]);
            // Microsoft.NET.Test.Sdk brings Microsoft's code coverage along, so test projects need no extra package.
            if (coverage)
                arguments.AddRange(["--collect", "Code Coverage;Format=cobertura"]);

            var (exitCode, output) = await DotnetCli.RunAsync(arguments, TimeSpan.FromMinutes(30), cancellationToken);
            var run = Summarize(exitCode, output, [.. results.EnumerateFiles("*.trx", SearchOption.AllDirectories).Select(file => XDocument.Load(file.FullName))]);
            return coverage
                ? run with { Coverage = Coverage([.. results.EnumerateFiles("*.cobertura.xml", SearchOption.AllDirectories).Select(file => XDocument.Load(file.FullName))]) }
                : run;
        }
        finally
        {
            results.Delete(recursive: true);
        }
    }

    /// <summary>Totals the test result files, one per test project.</summary>
    internal static TestRun Summarize(int exitCode, string output, IReadOnlyList<XDocument> reports)
    {
        var counters = reports.SelectMany(report => report.Descendants(Trx + "Counters")).ToList();
        int Count(params string[] outcomes) => counters.Sum(counter => outcomes.Sum(outcome => (int?)counter.Attribute(outcome) ?? 0));

        var failures = reports.SelectMany(report => report.Descendants(Trx + "UnitTestResult"))
            .Where(result => (string?)result.Attribute("outcome") is "Failed" or "Error" or "Timeout" or "Aborted")
            .Take(MaxFailures)
            .Select(result => new TestFailure(
                (string)result.Attribute("testName")!,
                (string)result.Attribute("outcome")!,
                result.Descendants(Trx + "Message").FirstOrDefault()?.Value.Trim(),
                result.Descendants(Trx + "StackTrace").FirstOrDefault()?.Value.Trim()));

        var buildErrors = BuildTool.ParseMessages(output).Where(message => message.Severity == "Error").ToList();
        return new(
            exitCode == 0,
            Count("total"),
            Count("passed"),
            Count("failed", "error", "timeout", "aborted"),
            // VSTest leaves notExecuted at 0 for ignored tests, but does not count them as executed.
            Count("total") - Count("executed"),
            [.. failures],
            buildErrors,
            reports.Count == 0 && buildErrors.Count == 0 ? BuildTool.Tail(output) : null);
    }

    /// <summary>The line coverage per source file from Cobertura reports, one per test project; a line counts as run if any test project ran it.</summary>
    internal static IReadOnlyList<FileCoverage> Coverage(IReadOnlyList<XDocument> reports) =>
    [
        .. reports.SelectMany(report => report.Descendants("class"))
            // The lines directly under a class repeat those under its methods.
            .SelectMany(type => type.Elements("lines").Elements("line").Select(line => (File: (string)type.Attribute("filename")!, Line: (int)line.Attribute("number")!, Hits: (long)line.Attribute("hits")!)))
            .GroupBy(line => line.File, StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file.Key, StringComparer.OrdinalIgnoreCase)
            .Select(file =>
            {
                var lines = file.GroupBy(line => line.Line).Select(line => (Number: line.Key, Run: line.Any(hit => hit.Hits > 0))).OrderBy(line => line.Number).ToList();
                return new FileCoverage(file.Key, lines.Count, lines.Count(line => line.Run), Ranges([.. lines.Where(line => !line.Run).Select(line => line.Number)]));
            }),
    ];

    /// <summary>Line numbers in order as ranges, such as "3-5, 9".</summary>
    private static string Ranges(IReadOnlyList<int> numbers)
    {
        var ranges = new List<string>();
        for (var i = 0; i < numbers.Count;)
        {
            var start = i;
            while (i + 1 < numbers.Count && numbers[i + 1] == numbers[i] + 1)
                i++;
            ranges.Add(i == start ? $"{numbers[start]}" : $"{numbers[start]}-{numbers[i]}");
            i++;
        }

        return string.Join(", ", ranges);
    }

    /// <param name="Skipped">Tests that did not run, such as ignored ones.</param>
    /// <param name="Coverage">With coverage, the line coverage of each source file the tests loaded.</param>
    public sealed record TestRun(
        bool Succeeded, int Total, int Passed, int Failed, int Skipped, IReadOnlyList<TestFailure> Failures, IReadOnlyList<BuildTool.BuildMessage> BuildErrors, string? Output,
        IReadOnlyList<FileCoverage>? Coverage = null);

    public sealed record TestFailure(string Test, string Outcome, string? Message, string? StackTrace);

    /// <param name="Lines">The lines with code.</param>
    /// <param name="Covered">The lines with code that a test ran.</param>
    /// <param name="NotCovered">The lines with code that no test ran, as ranges such as "12-15, 20".</param>
    public sealed record FileCoverage(string File, int Lines, int Covered, string NotCovered);
}
