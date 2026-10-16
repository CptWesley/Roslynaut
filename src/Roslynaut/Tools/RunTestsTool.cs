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
        + "When the build fails, BuildErrors says why. Output is the end of the log when there is nothing else to go on.")]
    internal static async Task<TestRun> RunTestsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("A dotnet test --filter expression, such as a Filter from find_tests, 'FullyQualifiedName~Namespace.Class' or 'TestCategory=Fast'. "
            + "Omit to run all tests.")] string? filter = null,
        [Description("The configuration, such as Debug or Release.")] string configuration = "Debug",
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

            var (exitCode, output) = await DotnetCli.RunAsync(arguments, TimeSpan.FromMinutes(30), cancellationToken);
            return Summarize(exitCode, output, [.. results.EnumerateFiles("*.trx", SearchOption.AllDirectories).Select(file => XDocument.Load(file.FullName))]);
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

    /// <param name="Skipped">Tests that did not run, such as ignored ones.</param>
    public sealed record TestRun(
        bool Succeeded, int Total, int Passed, int Failed, int Skipped, IReadOnlyList<TestFailure> Failures, IReadOnlyList<BuildTool.BuildMessage> BuildErrors, string? Output);

    public sealed record TestFailure(string Test, string Outcome, string? Message, string? StackTrace);
}
