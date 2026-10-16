using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed partial class RunTestsTool(WorkspaceService workspace) : IMcpTool
{
    private const int MaxFailures = 50;
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public Delegate Handler => RunAsync;

    [McpServerTool(Name = "run_tests")]
    [Description("Builds and runs tests with dotnet test, and returns the counts and the failures with their messages and stack traces (at most 50). "
        + "Works with VSTest and, where global.json opts into it, Microsoft.Testing.Platform. When the build fails, BuildErrors says why. Output is "
        + "the end of the log when there is nothing else to go on. With coverage, also the lines of each source file that no test ran.")]
    private Task<TestRun> RunAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("A dotnet test --filter expression, such as a Filter from find_tests, 'FullyQualifiedName~Namespace.Class' or 'TestCategory=Fast'. "
            + "Under Microsoft.Testing.Platform, xUnit v3 and TUnit take only test names this way, as find_tests gives them. Omit to run all tests.")] string? filter = null,
        [Description("The configuration, such as Debug or Release.")] string configuration = "Debug",
        [Description("Also measure code coverage: per source file, how many of its lines ran and which did not. Slower. Under "
            + "Microsoft.Testing.Platform it needs the Microsoft.Testing.Extensions.CodeCoverage package, which MSTest.Sdk has.")] bool coverage = false,
        [Description("More arguments for dotnet test, such as --filter-trait for xUnit v3 or --treenode-filter for TUnit.")] string[]? arguments = null,
        CancellationToken cancellationToken = default) =>
        RunTestsAsync(projectPath, filter, configuration, coverage, arguments, workspace, cancellationToken);

    /// <param name="workspace">Tells which test frameworks the projects use, for filters under Microsoft.Testing.Platform.</param>
    internal static async Task<TestRun> RunTestsAsync(
        string projectPath, string? filter = null, string configuration = "Debug", bool coverage = false, IReadOnlyList<string>? arguments = null,
        WorkspaceService? workspace = null, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(projectPath);
        if (!File.Exists(path))
            throw new McpException($"Project or solution not found: {path}");

        var results = Directory.CreateTempSubdirectory("roslynaut-tests-");
        try
        {
            TestRun run;
            if (UsesTestingPlatform(path))
            {
                // Microsoft.Testing.Platform's dotnet test passes options it does not know on to the test applications.
                var isSolution = Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx";
                List<string> command = ["test", isSolution ? "--solution" : "--project", path, "-c", configuration, "--results-directory", results.FullName];
                if (filter is not null)
                    command.AddRange(FilterArguments(filter, await FilterStyleAsync(workspace ?? new(), path)));
                if (coverage)
                    command.AddRange(["--coverage", "--coverage-output-format", "cobertura"]);
                command.AddRange(arguments ?? []);

                var (exitCode, output) = await DotnetCli.RunAsync(command, TimeSpan.FromMinutes(30), cancellationToken, workingDirectory: Path.GetDirectoryName(path));
                run = SummarizeConsole(exitCode, output);
            }
            else
            {
                List<string> command = ["test", path, "-c", configuration, "-v:q", "--logger", "trx", "--results-directory", results.FullName];
                if (filter is not null)
                    command.AddRange(["--filter", filter]);
                // Microsoft.NET.Test.Sdk brings Microsoft's code coverage along, so test projects need no extra package.
                if (coverage)
                    command.AddRange(["--collect", "Code Coverage;Format=cobertura"]);
                command.AddRange(arguments ?? []);

                var (exitCode, output) = await DotnetCli.RunAsync(command, TimeSpan.FromMinutes(30), cancellationToken, workingDirectory: Path.GetDirectoryName(path));
                run = Summarize(exitCode, output, [.. results.EnumerateFiles("*.trx", SearchOption.AllDirectories).Select(file => XDocument.Load(file.FullName))]);
            }

            return coverage
                ? run with { Coverage = Coverage([.. results.EnumerateFiles("*.cobertura.xml", SearchOption.AllDirectories).Select(file => XDocument.Load(file.FullName))]) }
                : run;
        }
        finally
        {
            results.Delete(recursive: true);
        }
    }

    /// <summary>Whether dotnet test runs Microsoft.Testing.Platform for a path: the nearest global.json above it says so, as dotnet reads it.</summary>
    internal static bool UsesTestingPlatform(string path)
    {
        for (var directory = Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var file = Path.Combine(directory, "global.json");
            if (!File.Exists(file))
                continue;

            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file), new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return json.RootElement.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.Object
                    && test.TryGetProperty("runner", out var runner) && string.Equals(runner.GetString(), "Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return false;
    }

    internal enum FilterStyle
    {
        /// <summary>--filter with VSTest's syntax, as MSTest and NUnit take it.</summary>
        VSTest,

        /// <summary>--filter-method per test, with * as a wildcard.</summary>
        XUnit,

        /// <summary>One --treenode-filter, /assembly/namespace/class/method, with (a|b) for alternatives.</summary>
        TUnit,
    }

    /// <summary>How the test projects take filters under Microsoft.Testing.Platform, from the test framework packages they reference.</summary>
    internal static async Task<FilterStyle> FilterStyleAsync(WorkspaceService workspace, string path)
    {
        var styles = (await workspace.GetAsync(path)).Results.Select(result => result.PackageReferences.Keys.ToList()).Select(packages =>
            packages.Any(package => package.StartsWith("xunit.v3", StringComparison.OrdinalIgnoreCase)) ? FilterStyle.XUnit
            : packages.Any(package => package.Equals("TUnit", StringComparison.OrdinalIgnoreCase) || package.StartsWith("TUnit.", StringComparison.OrdinalIgnoreCase)) ? FilterStyle.TUnit
            : packages.Any(package => package.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase) || package.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase)) ? FilterStyle.VSTest
            : (FilterStyle?)null).OfType<FilterStyle>().Distinct().ToList();
        return styles switch
        {
            [] => FilterStyle.VSTest,
            [var style] => style,
            _ => throw new McpException("The test projects use test frameworks that filter tests differently; run them one project at a time."),
        };
    }

    /// <summary>
    /// A filter as the test framework takes it under Microsoft.Testing.Platform: as is for --filter; for xUnit v3 and TUnit, which have their own
    /// options, the test names of a filter such as find_tests gives, "FullyQualifiedName~A.B.Test|FullyQualifiedName~A.B.Other".
    /// </summary>
    internal static IReadOnlyList<string> FilterArguments(string filter, FilterStyle style)
    {
        if (style == FilterStyle.VSTest)
            return ["--filter", filter];

        var names = filter.Split('|', StringSplitOptions.TrimEntries).Select(term => NameFilter().Match(term)).ToList();
        if (names.Any(name => !name.Success))
            throw new McpException($"Under Microsoft.Testing.Platform, {style} takes only test names as a filter, such as 'FullyQualifiedName~MyApp.Tests.Adds'; "
                + "pass other filters through arguments, such as --filter-trait for xUnit v3 or --treenode-filter for TUnit.");

        if (style == FilterStyle.XUnit)
            return [.. names.SelectMany(name => (string[])["--filter-method", name.Groups["contains"].Success ? $"*{name.Groups["name"].Value}*" : name.Groups["name"].Value])];

        // ponytail: one path with alternatives per level, so tests from several classes also run the other combinations of those names.
        var parts = names.Select(name => name.Groups["name"].Value.Split('.')).Select(name => (
            Namespace: name.Length > 2 ? string.Join('.', name[..^2]) : "*",
            Type: name.Length > 1 ? name[^2] : "*",
            Method: name[^1])).ToList();
        string Level(IEnumerable<string> values) => values.Distinct().ToList() is [var single] ? single : $"({string.Join('|', values.Distinct())})";
        return ["--treenode-filter", $"/*/{Level(parts.Select(part => part.Namespace))}/{Level(parts.Select(part => part.Type))}/{Level(parts.Select(part => part.Method))}"];
    }

    [GeneratedRegex(@"^(FullyQualifiedName|Name)(?:(?<contains>~)|=)(?<name>[^()&!|~=]+)$")]
    private static partial Regex NameFilter();

    /// <summary>
    /// The counts and failures from what Microsoft.Testing.Platform's dotnet test writes, which has no result files without an extension for them:
    /// a "failed Name (12ms)" line per failure with its message and, after its "from" line, its stack trace; then the summary's counts.
    /// </summary>
    internal static TestRun SummarizeConsole(int exitCode, string output)
    {
        var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var counts = lines.Select(line => SummaryCount().Match(line)).Where(match => match.Success).GroupBy(match => match.Groups[1].Value)
            .ToDictionary(count => count.Key, count => int.Parse(count.Last().Groups[2].ValueSpan));

        var failures = new List<TestFailure>();
        for (var i = 0; i < lines.Count && failures.Count < MaxFailures; i++)
        {
            if (FailedTest().Match(lines[i]) is not { Success: true } failed)
                continue;

            // A message's lines need not be indented; the next test, assembly or summary line ends it.
            var block = lines.Skip(i + 1).TakeWhile(line => !EndsTest().IsMatch(line)).ToList();
            var from = block.FindIndex(line => line.StartsWith("  from ", StringComparison.Ordinal));
            var message = string.Join('\n', (from < 0 ? block : block[..from]).Select(line => line.Trim())).Trim();
            var stack = from < 0 ? [] : block[(from + 1)..].Where(line => line.StartsWith("    at ", StringComparison.Ordinal)).Select(line => line.Trim()).ToList();
            failures.Add(new(failed.Groups[1].Value, "Failed", message.Length > 0 ? message : null, stack.Count > 0 ? string.Join('\n', stack) : null));
        }

        var buildErrors = BuildTool.ParseMessages(output).Where(message => message.Severity == "Error").ToList();
        var total = counts.GetValueOrDefault("total");
        return new(
            exitCode == 0,
            total,
            counts.GetValueOrDefault("succeeded"),
            counts.GetValueOrDefault("failed"),
            counts.GetValueOrDefault("skipped"),
            [.. failures],
            buildErrors,
            // Such as an option the test application does not know, or a filter that matched no test.
            buildErrors.Count == 0 && (counts.Count == 0 || (total == 0 && exitCode != 0)) ? BuildTool.Tail(output) : null);
    }

    [GeneratedRegex(@"^\s+(total|failed|succeeded|skipped): (\d+)$")]
    private static partial Regex SummaryCount();

    [GeneratedRegex(@"^failed (.+) \([^()]*\)$")]
    private static partial Regex FailedTest();

    [GeneratedRegex(@"^(failed |skipped |passed |Running tests from |Test run summary|Exit code:|\S.*\.dll \(.*\) (failed|passed))")]
    private static partial Regex EndsTest();

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
