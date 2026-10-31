using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed partial class RunTestsTool(WorkspaceService workspace, PdbService pdbs) : IMcpTool
{
    private const int MaxFailures = 50;
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public Delegate Handler => RunAsync;

    [McpServerTool(Name = "run_tests")]
    [Description("Builds and runs tests with dotnet test, and returns the counts and the failures with their messages and stack traces (at most 50). "
        + "Works with VSTest and, where global.json opts into it, Microsoft.Testing.Platform. When the build fails, BuildErrors says why. Output is "
        + "the end of the log when there is nothing else to go on. With coverage, also the lines of each source file that no test ran. With "
        + "hangTimeout, a test that hangs ends the run with a dump and the test host's threads; with repeat, the tests run several times, to tell "
        + "flaky ones; with mutate, Stryker.NET changes the code of those files and reports the changes no test noticed.")]
    private Task<TestRun> RunAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("A dotnet test --filter expression, such as a Filter from find_tests, 'FullyQualifiedName~Namespace.Class' or 'TestCategory=Fast'. "
            + "Under Microsoft.Testing.Platform, xUnit v3 and TUnit take only test names this way, as find_tests gives them. Omit to run all tests.")] string? filter = null,
        [Description("The configuration, such as Debug or Release.")] string configuration = "Debug",
        [Description("Also measure code coverage: per source file, how many of its lines ran and which did not. Slower. Under "
            + "Microsoft.Testing.Platform it needs the Microsoft.Testing.Extensions.CodeCoverage package, which MSTest.Sdk has.")] bool coverage = false,
        [Description("More arguments for dotnet test, such as --filter-trait for xUnit v3 or --treenode-filter for TUnit.")] string[]? arguments = null,
        [Description("Seconds a test may run before the run counts as hung: the test host is then dumped and ended, and Hang gives the test that "
            + "ran, the dump (for process_threads, process_async or process_heap) and the test host's threads. Under Microsoft.Testing.Platform it "
            + "needs the Microsoft.Testing.Extensions.HangDump package.")] int? hangTimeout = null,
        [Description("How many times to run the tests, to tell flaky ones: FailedRuns then says in how many runs each test failed, with its "
            + "distinct messages. Runs after the first do not build.")] int repeat = 1,
        [Description("C# source files to mutation test, of one project the test project references: Stryker.NET (installed on first use) changes "
            + "their code in small ways, such as > to >=, runs the tests against each change, and Mutation lists the changes that no test failed on. "
            + "VSTest projects; slow, minutes per file. Filter and the other options do not apply.")] string[]? mutate = null,
        CancellationToken cancellationToken = default) =>
        mutate is { Length: > 0 }
            ? MutateAsync(projectPath, mutate, workspace, cancellationToken)
            : RunTestsAsync(projectPath, filter, configuration, coverage, arguments, hangTimeout, repeat, workspace, pdbs, cancellationToken);

    /// <param name="workspace">Tells which test frameworks the projects use, for filters under Microsoft.Testing.Platform.</param>
    internal static async Task<TestRun> RunTestsAsync(
        string projectPath, string? filter = null, string configuration = "Debug", bool coverage = false, IReadOnlyList<string>? arguments = null,
        int? hangTimeout = null, int repeat = 1, WorkspaceService? workspace = null, PdbService? pdbs = null, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(projectPath);
        if (!File.Exists(path))
            throw new McpException($"Project or solution not found: {path}");
        if (repeat < 1)
            throw new McpException("repeat must be at least 1.");

        var runs = new List<TestRun>();
        for (var i = 0; i < repeat; i++)
        {
            var run = await RunOnceAsync(path, filter, configuration, coverage && i == 0, i == 0 ? arguments : [.. arguments ?? [], "--no-build"], hangTimeout, workspace, pdbs, cancellationToken);
            runs.Add(run);
            if (run.BuildErrors.Count > 0 || run.Hang is not null)
                break;
        }

        return runs.Count == 1 ? runs[0] : Combine(runs);
    }

    /// <summary>The runs of the same tests as one: counts summed, each distinct failure once, and per test how many runs it failed in.</summary>
    internal static TestRun Combine(IReadOnlyList<TestRun> runs)
    {
        var failures = runs.SelectMany(run => run.Failures).ToList();
        return runs[^1] with
        {
            Succeeded = runs.All(run => run.Succeeded),
            Total = runs.Sum(run => run.Total),
            Passed = runs.Sum(run => run.Passed),
            Failed = runs.Sum(run => run.Failed),
            Skipped = runs.Sum(run => run.Skipped),
            Failures = [.. failures.DistinctBy(failure => (failure.Test, failure.Message)).Take(MaxFailures)],
            Coverage = runs[0].Coverage,
            Runs = runs.Count,
            FailedRuns = [.. failures.GroupBy(failure => failure.Test).Select(test =>
                new TestRepeat(test.Key, test.Count(), runs.Count, [.. test.Select(failure => failure.Message ?? "").Distinct()]))],
        };
    }

    private static async Task<TestRun> RunOnceAsync(
        string path, string? filter, string configuration, bool coverage, IReadOnlyList<string>? arguments, int? hangTimeout, WorkspaceService? workspace,
        PdbService? pdbs, CancellationToken cancellationToken)
    {
        var results = Directory.CreateTempSubdirectory("roslynaut-tests-");
        try
        {
            TestRun run;
            string log;
            if (UsesTestingPlatform(path))
            {
                // Microsoft.Testing.Platform's dotnet test passes options it does not know on to the test applications.
                var isSolution = Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx";
                List<string> command = ["test", isSolution ? "--solution" : "--project", path, "-c", configuration, "--results-directory", results.FullName];
                if (filter is not null)
                    command.AddRange(FilterArguments(filter, await FilterStyleAsync(workspace ?? new(), path)));
                if (coverage)
                    command.AddRange(["--coverage", "--coverage-output-format", "cobertura"]);
                if (hangTimeout is { } seconds)
                    command.AddRange(["--hangdump", "--hangdump-timeout", $"{seconds}s", "--hangdump-type", "Full"]);
                command.AddRange(arguments ?? []);

                var (exitCode, output) = await DotnetCli.RunAsync(command, TimeSpan.FromMinutes(30), cancellationToken, workingDirectory: Path.GetDirectoryName(path));
                run = SummarizeConsole(exitCode, output);
                log = output;
            }
            else
            {
                List<string> command = ["test", path, "-c", configuration, "-v:q", "--logger", "trx", "--results-directory", results.FullName];
                if (filter is not null)
                    command.AddRange(["--filter", filter]);
                // Microsoft.NET.Test.Sdk brings Microsoft's code coverage along, so test projects need no extra package.
                if (coverage)
                    command.AddRange(["--collect", "Code Coverage;Format=cobertura"]);
                if (hangTimeout is { } seconds)
                    command.AddRange(["--blame-hang-timeout", $"{seconds}s", "--blame-hang-dump-type", "full"]);
                command.AddRange(arguments ?? []);

                var (exitCode, output) = await DotnetCli.RunAsync(command, TimeSpan.FromMinutes(30), cancellationToken, workingDirectory: Path.GetDirectoryName(path));
                run = Summarize(exitCode, output, [.. results.EnumerateFiles("*.trx", SearchOption.AllDirectories).Select(file => XDocument.Load(file.FullName))]);
                log = output;
            }

            if (coverage)
                run = run with { Coverage = Coverage([.. results.EnumerateFiles("*.cobertura.xml", SearchOption.AllDirectories).Select(file => XDocument.Load(file.FullName))]) };
            return hangTimeout is null ? run : run with { Hang = HangOf(results, log, pdbs ?? new()) };
        }
        finally
        {
            results.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The hang a run ended with, if any: its dump, kept beyond the run's results, which are deleted; the tests that ran when it hung, as dotnet
    /// test reports them; and the test host's threads in the dump.
    /// </summary>
    private static HangReport? HangOf(DirectoryInfo results, string output, PdbService pdbs)
    {
        // The blame collector copies its attachments into the run's own directory too.
        if (results.EnumerateFiles("*.dmp", SearchOption.AllDirectories).DistinctBy(file => file.Name).FirstOrDefault() is not { } found)
            return null;

        // ponytail: dumps pile up in the temporary directory; delete old ones if disk space matters.
        var kept = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Roslynaut", "Dumps"));
        var dump = Path.Combine(kept.FullName, found.Name);
        found.MoveTo(dump, overwrite: true);

        IReadOnlyList<ProcessInspection.ThreadEntry> threads;
        try
        {
            threads = [.. ProcessInspection.Threads(null, dump, maxFrames: 30, pdbs).Where(thread => thread.Stack.Count > 0)];
        }
        catch (McpException)
        {
            threads = [];
        }

        return new(dump, RunningTests(output), threads);
    }

    /// <summary>
    /// The tests that ran when the test host crashed or hung, from what dotnet test writes: the lines after "The test running when the crash
    /// occurred:" (VSTest), up to a blank line.
    /// </summary>
    internal static IReadOnlyList<string> RunningTests(string output)
    {
        var lines = output.Split('\n').Select(line => line.Trim()).ToList();
        var start = lines.FindIndex(line => line.Contains("running when the crash occurred", StringComparison.OrdinalIgnoreCase));
        return start < 0 ? [] : [.. lines.Skip(start + 1).SkipWhile(line => line.Length == 0).TakeWhile(line => line.Length > 0)];
    }

    /// <summary>
    /// Mutation tests source files with Stryker.NET, which it installs in the temporary directory on first use: Stryker changes their code
    /// in small ways and runs the test project's tests against each change; a change no test fails on shows code the tests do not check.
    /// </summary>
    internal static async Task<TestRun> MutateAsync(string testProject, IReadOnlyList<string> files, WorkspaceService workspace, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(testProject);
        var solution = (await workspace.GetAsync(path)).Solution;
        var documents = files.Select(file => solution.GetDocumentIdsWithFilePath(Path.GetFullPath(file)).Select(solution.GetDocument).FirstOrDefault()
            ?? throw new McpException($"{file} is not a C# file of the projects that {Path.GetFileName(path)} loads.")).ToList();
        if (documents.Select(document => document.Project.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is not [{ } source])
            throw new McpException("The files to mutate must be of one project.");

        var stryker = await StrykerAsync(cancellationToken);
        var reports = Directory.CreateTempSubdirectory("roslynaut-stryker-");
        try
        {
            List<string> command = ["--test-project", path, "--project", Path.GetFileName(source), "--reporter", "json", "--output", reports.FullName];
            foreach (var document in documents)
                command.AddRange(["--mutate", Path.GetRelativePath(Path.GetDirectoryName(source)!, document.FilePath!).Replace('\\', '/')]);

            var (exitCode, output) = await DotnetCli.RunAsync(stryker, command, TimeSpan.FromHours(2), cancellationToken, workingDirectory: Path.GetDirectoryName(path));
            var report = reports.EnumerateFiles("mutation-report.json", SearchOption.AllDirectories).FirstOrDefault();
            return report is null
                ? new(false, 0, 0, 0, 0, [], [], BuildTool.Tail(output))
                : new(exitCode == 0, 0, 0, 0, 0, [], [], null, Mutation: Mutations(File.ReadAllText(report.FullName)));
        }
        finally
        {
            reports.Delete(recursive: true);
        }
    }

    private static readonly SemaphoreSlim Installing = new(1, 1);

    /// <summary>The dotnet-stryker tool, installed into a directory of its own under the temporary directory, as the user's tools are not touched.</summary>
    private static async Task<string> StrykerAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Roslynaut", "Tools", "Stryker");
        var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet-stryker.exe" : "dotnet-stryker");
        await Installing.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(executable))
                return executable;

            var (exitCode, output) = await DotnetCli.RunAsync(["tool", "install", "dotnet-stryker", "--tool-path", directory], TimeSpan.FromMinutes(10), cancellationToken);
            return exitCode == 0 ? executable : throw new McpException($"Cannot install Stryker.NET (dotnet-stryker): {BuildTool.Tail(output)}");
        }
        finally
        {
            Installing.Release();
        }
    }

    /// <summary>The mutants of a Stryker JSON report (mutation-testing-elements' schema) that no test detected, and the score.</summary>
    internal static MutationReport Mutations(string json)
    {
        using var report = JsonDocument.Parse(json);
        var mutants = report.RootElement.GetProperty("files").EnumerateObject().SelectMany(file => file.Value.GetProperty("mutants").EnumerateArray().Select(mutant =>
        {
            var start = mutant.GetProperty("location").GetProperty("start");
            return new Mutant(file.Name, start.GetProperty("line").GetInt32(), start.GetProperty("column").GetInt32(), mutant.GetProperty("mutatorName").GetString() ?? "?",
                mutant.TryGetProperty("replacement", out var replacement) ? replacement.GetString() : null, mutant.GetProperty("status").GetString() ?? "?");
        })).ToList();

        int Count(string status) => mutants.Count(mutant => mutant.Status == status);
        var (detected, undetected) = (Count("Killed") + Count("Timeout"), Count("Survived") + Count("NoCoverage"));
        return new(
            detected + undetected == 0 ? 0 : Math.Round(100.0 * detected / (detected + undetected), 1),
            Count("Killed"), Count("Timeout"), Count("Survived"), Count("NoCoverage"),
            [.. mutants.Where(mutant => mutant.Status is "Survived" or "NoCoverage").OrderBy(mutant => mutant.File, StringComparer.OrdinalIgnoreCase).ThenBy(mutant => mutant.Line)]);
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
            // Such as a test adapter that failed, or a filter that matched no test.
            (reports.Count == 0 || Count("total") == 0) && buildErrors.Count == 0 ? BuildTool.Tail(output) : null);
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
    /// <param name="Hang">With hangTimeout, when a test hung: what ran, and the test host's dump and threads.</param>
    /// <param name="Runs">With repeat: how many runs there were; the counts are their sums.</param>
    /// <param name="FailedRuns">With repeat: each test that failed, with in how many runs.</param>
    /// <param name="Mutation">With mutate: the mutation score and the mutants no test detected.</param>
    public sealed record TestRun(
        bool Succeeded, int Total, int Passed, int Failed, int Skipped, IReadOnlyList<TestFailure> Failures, IReadOnlyList<BuildTool.BuildMessage> BuildErrors, string? Output,
        IReadOnlyList<FileCoverage>? Coverage = null, HangReport? Hang = null, int? Runs = null, IReadOnlyList<TestRepeat>? FailedRuns = null, MutationReport? Mutation = null);

    public sealed record TestFailure(string Test, string Outcome, string? Message, string? StackTrace);

    /// <param name="Lines">The lines with code.</param>
    /// <param name="Covered">The lines with code that a test ran.</param>
    /// <param name="NotCovered">The lines with code that no test ran, as ranges such as "12-15, 20".</param>
    public sealed record FileCoverage(string File, int Lines, int Covered, string NotCovered);

    /// <param name="Dump">The test host's full dump, kept in the temporary directory, for process_threads, process_async, process_heap and the like.</param>
    /// <param name="Running">The tests that ran when the test host hung, as dotnet test names them.</param>
    /// <param name="Threads">The test host's managed threads in the dump, with their stacks.</param>
    public sealed record HangReport(string Dump, IReadOnlyList<string> Running, IReadOnlyList<ProcessInspection.ThreadEntry> Threads);

    /// <param name="Failed">How many runs the test failed in.</param>
    /// <param name="Messages">Its distinct failure messages.</param>
    public sealed record TestRepeat(string Test, int Failed, int Runs, IReadOnlyList<string> Messages);

    /// <param name="Score">The percentage of the mutants, of those the tests can tell, that a test detected: killed or timed out.</param>
    /// <param name="Undetected">The mutants no test detected: Survived (tests ran it and passed) or NoCoverage (no test ran it).</param>
    public sealed record MutationReport(double Score, int Killed, int Timeout, int Survived, int NoCoverage, IReadOnlyList<Mutant> Undetected);

    /// <param name="File">The file as Stryker names it.</param>
    /// <param name="Mutator">What kind of change, such as Equality or Arithmetic.</param>
    /// <param name="Replacement">The code it put in, such as "a >= b".</param>
    public sealed record Mutant(string File, int Line, int Column, string Mutator, string? Replacement, string Status);
}
