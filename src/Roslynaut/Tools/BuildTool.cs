using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Build.Logging.StructuredLogger;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using LogTask = Microsoft.Build.Logging.StructuredLogger.Task;

namespace Roslynaut.Tools;

public sealed class BuildTool : IMcpTool
{
    private const int MaxMessages = 100;

    // MSBuild's canonical format: "origin[(line,column[,endLine,endColumn])]: error|warning CODE: message [project]".
    private static readonly Regex Message = new(
        @"^\s*(?<origin>.+?)(?:\((?<line>\d+),(?<column>\d+)(?:,\d+,\d+)?\))?\s?: (?<severity>error|warning) (?<code>[^\s:]+): (?<message>.*?)(?: \[(?<project>[^\[\]]+)\])?\s*$");

    // BuildAnalyzer's summaries of what csc reported: "1.234 s   Assembly, Version=..." folders holding "Analyzer (IDs) = 66 ms" items.
    private static readonly Regex AssemblyTime = new(@"^(?<time>.+?) {3}(?<name>[^,]+)");
    private static readonly Regex AnalyzerTime = new(@"^(?<name>.+) = (?<time>.+)$");

    public Delegate Handler => BuildAsync;

    [McpServerTool(Name = "build", Idempotent = true)]
    [Description("Builds a project or solution with dotnet build, and returns whether it succeeded, with its errors and warnings (errors first, "
        + "at most 100). Output is the end of the build log when it failed without any. With timings, also where the build spent its time. "
        + "With binlogPath, keeps the build's binary log for the build_* tools.")]
    internal static async Task<BuildResult> BuildAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The configuration, such as Debug or Release.")] string configuration = "Debug",
        [Description("Also time the build from its binary log: how long each project, target and task took, the analyzers and source generators "
            + "of the compilations that ran, the evaluations and the slowest places in them, and the chain of projects that set the build's length. "
            + "An up-to-date build skips most of the work, compilations included.")] bool timings = false,
        [Description("Where to write the build's binary log (.binlog), to read it with build_problems, build_target_why and the other build_* tools, "
            + "or in the MSBuild Structured Log Viewer.")] string? binlogPath = null,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(projectPath);
        if (!File.Exists(path))
            throw new McpException($"Project or solution not found: {path}");

        var binlog = binlogPath is not null ? Path.GetFullPath(binlogPath) : timings ? Path.Combine(Path.GetTempPath(), $"roslynaut-{Guid.NewGuid():N}.binlog") : null;
        var profile = timings ? Path.Combine(Path.GetTempPath(), $"roslynaut-{Guid.NewGuid():N}.md") : null;
        try
        {
            List<string> arguments = ["build", path, "-c", configuration, "-v:q", "-clp:NoSummary"];
            if (binlog is not null)
                arguments.Add($"-bl:{binlog}");

            // csc reports its analyzers' and generators' times, and MSBuild where evaluations spend theirs; both end up in the binary log.
            if (profile is not null)
                arguments.AddRange(["-p:ReportAnalyzer=true", $"-profileevaluation:{profile}"]);

            var (exitCode, output) = await DotnetCli.RunAsync(arguments, TimeSpan.FromMinutes(15), cancellationToken, workingDirectory: Path.GetDirectoryName(path));
            var messages = ParseMessages(output);
            return new(
                exitCode == 0,
                messages.Count(message => message.Severity == "Error"),
                messages.Count(message => message.Severity == "Warning"),
                [.. messages.Take(MaxMessages)],
                exitCode != 0 && messages.Count == 0 ? Tail(output) : null,
                timings && binlog is not null && File.Exists(binlog) ? Timings(binlogPath is null ? BuildLog.Load(binlog) : BuildLog.Read(binlog)) : null);
        }
        finally
        {
            if (binlogPath is null && binlog is not null)
                File.Delete(binlog);
            if (profile is not null)
                File.Delete(profile);
        }
    }

    /// <summary>Where a build spent its time, from its binary log.</summary>
    internal static BuildTimings Timings(Build build, int top = 15)
    {
        var (projects, targets, tasks) = (new List<Timing>(), new List<Timing>(), new Dictionary<string, (int Count, TimeSpan Duration)>());

        // A project builds once per target framework, and once more as the outer build of several.
        build.VisitAllChildren<Project>(project => projects.Add(new(BuildLog.Name(project), Milliseconds(project.Duration), 1, null)));
        build.VisitAllChildren<Target>(target => targets.Add(new(target.Name, Milliseconds(target.Duration), 1, target.GetNearestParent<Project>()?.Name)));
        build.VisitAllChildren<LogTask>(task =>
            tasks[task.Name] = tasks.TryGetValue(task.Name, out var total) ? (total.Count + 1, total.Duration + task.Duration) : (1, task.Duration));

        var evaluations = build.FindChildrenRecursive<ProjectEvaluation>()
            .GroupBy(evaluation => evaluation.ProjectFile)
            .Select(group => new Timing(Path.GetFileName(group.Key), Milliseconds(group.Aggregate(TimeSpan.Zero, (sum, evaluation) => sum + evaluation.Duration)), group.Count(), null));

        // Work is the time tasks ran, except those that only run other targets, which count on their own; divided by the build's time, how many
        // nodes were busy on average.
        var work = build.FindChildrenRecursive<LogTask>(task => task is not MSBuildTask && task.Name != "CallTarget").Aggregate(TimeSpan.Zero, (sum, task) => sum + task.Duration);

        return new(
            Milliseconds(build.Duration),
            [.. projects.OrderByDescending(timing => timing.Milliseconds).Take(top)],
            [.. targets.OrderByDescending(timing => timing.Milliseconds).Take(top)],
            [.. tasks.OrderByDescending(task => task.Value.Duration).Take(top).Select(task => new Timing(task.Key, Milliseconds(task.Value.Duration), task.Value.Count, null))],
            Analyzers(build, "Analyzer Summary", top),
            Analyzers(build, "Generator Summary", top),
            [.. evaluations.OrderByDescending(timing => timing.Milliseconds).Take(top)],
            EvaluationHotspots(build, top),
            CriticalPath(build),
            build.FindChildrenRecursive<Project>().Select(project => project.NodeId).Distinct().Count(),
            build.Duration > TimeSpan.Zero ? Math.Round(work / build.Duration, 1) : 0);

        static long Milliseconds(TimeSpan duration) => (long)duration.TotalMilliseconds;
    }

    private static IReadOnlyList<AnalyzerTiming> Analyzers(Build build, string summary, int top) =>
    [
        .. (build.FindChild<Folder>(summary)?.Children.OfType<Folder>() ?? [])
            .SelectMany(assembly => assembly.Children.OfType<Item>().Select(analyzer => (Assembly: AssemblyTime.Match(assembly.Name), Analyzer: AnalyzerTime.Match(analyzer.Text))))
            .Where(timing => timing.Assembly.Success && timing.Analyzer.Success)
            .Select(timing => new AnalyzerTiming(timing.Analyzer.Groups["name"].Value, timing.Assembly.Groups["name"].Value, ParseDuration(timing.Analyzer.Groups["time"].Value)))
            .OrderByDescending(timing => timing.Milliseconds)
            .Take(top),
    ];

    /// <summary>A duration as StructuredLogger's TextUtilities.DisplayDuration writes it: "66 ms", "1.234 s", "2:15.123", or from an hour on "01:02:03.1230000".</summary>
    internal static long ParseDuration(string duration) => duration.EndsWith(" ms", StringComparison.Ordinal)
        ? long.Parse(duration[..^3], CultureInfo.InvariantCulture)
        : (long)TimeSpan.ParseExact(duration, [@"s\.fff' s'", @"m\:ss\.fff", "c"], CultureInfo.InvariantCulture).TotalMilliseconds;

    /// <summary>The elements, conditions and globs that evaluation spent the most time in, over all evaluations, from -profileevaluation.</summary>
    private static IReadOnlyList<Timing> EvaluationHotspots(Build build, int top) =>
    [
        .. build.FindChildrenRecursive<EvaluationProfileEntry>(entry => entry.LineNumber > 0)
            .GroupBy(entry => $"{entry.SourceFilePath}({entry.LineNumber}): {entry.ShortenedElementDescription}")
            .Select(group => new Timing(group.Key, (long)group.Sum(entry => entry.ProfiledLocation.ExclusiveTime.TotalMilliseconds), group.Count(), null))
            .OrderByDescending(timing => timing.Milliseconds)
            .Take(top),
    ];

    /// <summary>
    /// From the top-level project down, the project each one waited for the longest: the last to finish in its longest call of the MSBuild task.
    /// </summary>
    // ponytail: follows only the longest wait of each project; a project that waits on several references in turn has more than one path that counts.
    private static IReadOnlyList<PathStep> CriticalPath(Build build)
    {
        // A project that several others ask for builds once; the others' requests wait for that build, which may have started elsewhere.
        var builds = build.FindChildrenRecursive<Project>().GroupBy(project => project.ProjectInstanceId).ToDictionary(group => group.Key, group => group.MaxBy(project => project.Duration)!);
        var (path, seen) = (new List<PathStep>(), new HashSet<int>());
        var project = build.Children.OfType<Project>().Where(project => !project.EntryTargets.Contains("Restore")).MaxBy(project => project.Duration);
        while (project is not null && seen.Add(project.ProjectInstanceId))
        {
            var waits = project.FindChildrenRecursive<MSBuildTask>(task => task.GetNearestParent<Project>() == project);
            var own = project.Duration - waits.Aggregate(TimeSpan.Zero, (sum, task) => sum + task.Duration);
            path.Add(new(BuildLog.Name(project), (long)project.Duration.TotalMilliseconds, (long)Math.Max(0, own.TotalMilliseconds)));
            project = waits.MaxBy(task => task.Duration)?.Children.OfType<Project>().MaxBy(child => child.EndTime) is { } next ? builds[next.ProjectInstanceId] : null;
        }

        return path;
    }

    /// <summary>The errors and warnings in build output, each once, errors first.</summary>
    internal static List<BuildMessage> ParseMessages(string output) =>
    [
        .. output.Split('\n')
            .Select(line => Message.Match(line.TrimEnd('\r')))
            .Where(match => match.Success)
            .Select(match => new BuildMessage(
                match.Groups["severity"].Value == "error" ? "Error" : "Warning",
                match.Groups["code"].Value,
                match.Groups["message"].Value,
                match.Groups["origin"].Value,
                match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value) : null,
                match.Groups["column"].Success ? int.Parse(match.Groups["column"].Value) : null,
                match.Groups["project"].Success ? match.Groups["project"].Value : null))
            .Distinct()
            .OrderBy(message => message.Severity != "Error"),
    ];

    internal static string Tail(string output) => output.Trim() is { Length: > 4000 } text ? "…" + text[^4000..] : output.Trim();

    public sealed record BuildResult(bool Succeeded, int Errors, int Warnings, IReadOnlyList<BuildMessage> Messages, string? Output, BuildTimings? Timings = null);

    /// <param name="Tasks">The tasks that took the most time in all, such as Csc or ResolveAssemblyReference, with how often they ran.</param>
    /// <param name="Analyzers">The analyzers that took the most time, summed over the compilations that ran, with their assembly.</param>
    /// <param name="Generators">The same for source generators.</param>
    /// <param name="Evaluations">The projects whose evaluations took the most time in all, with how often they were evaluated.</param>
    /// <param name="EvaluationHotspots">
    /// The places in the project and its imports that evaluation spent the most time in, over all evaluations: elements, imports, conditions,
    /// and item globs that walk large directories; with how many evaluations spent time there.
    /// </param>
    /// <param name="CriticalPath">
    /// From the top-level project down, each project with the reference it waited for the longest: shortening this chain shortens the build.
    /// </param>
    /// <param name="Nodes">How many MSBuild nodes built projects.</param>
    /// <param name="Parallelism">On average, how many nodes were working rather than waiting for other projects.</param>
    public sealed record BuildTimings(
        long Milliseconds, IReadOnlyList<Timing> Projects, IReadOnlyList<Timing> Targets, IReadOnlyList<Timing> Tasks,
        IReadOnlyList<AnalyzerTiming> Analyzers, IReadOnlyList<AnalyzerTiming> Generators, IReadOnlyList<Timing> Evaluations, IReadOnlyList<Timing> EvaluationHotspots,
        IReadOnlyList<PathStep> CriticalPath, int Nodes, double Parallelism);

    /// <param name="Project">For a target, the project it ran in.</param>
    public sealed record Timing(string Name, long Milliseconds, int Count, string? Project);

    /// <param name="Name">The analyzer or generator type, with the diagnostic ids it reports.</param>
    public sealed record AnalyzerTiming(string Name, string Assembly, long Milliseconds);

    /// <param name="OwnMilliseconds">The time it worked, not counting the time it waited for other projects.</param>
    public sealed record PathStep(string Project, long Milliseconds, long OwnMilliseconds);

    /// <param name="Origin">The file, or the tool that reported it, such as CSC or MSBUILD.</param>
    public sealed record BuildMessage(string Severity, string Code, string Message, string Origin, int? Line, int? Column, string? Project);
}
