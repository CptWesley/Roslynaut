using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Build.Logging.StructuredLogger;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class BuildTool : IMcpTool
{
    private const int MaxMessages = 100;

    // MSBuild's canonical format: "origin[(line,column[,endLine,endColumn])]: error|warning CODE: message [project]".
    private static readonly Regex Message = new(
        @"^\s*(?<origin>.+?)(?:\((?<line>\d+),(?<column>\d+)(?:,\d+,\d+)?\))?\s?: (?<severity>error|warning) (?<code>[^\s:]+): (?<message>.*?)(?: \[(?<project>[^\[\]]+)\])?\s*$");

    public Delegate Handler => BuildAsync;

    [McpServerTool(Name = "build", Idempotent = true)]
    [Description("Builds a project or solution with dotnet build, and returns whether it succeeded, with its errors and warnings (errors first, "
        + "at most 100). Output is the end of the build log when it failed without any. With timings, also where the build spent its time.")]
    internal static async Task<BuildResult> BuildAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The configuration, such as Debug or Release.")] string configuration = "Debug",
        [Description("Also time the build from its binary log: how long each project took, and the slowest targets and tasks. An up-to-date build "
            + "skips most of them.")] bool timings = false,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(projectPath);
        if (!File.Exists(path))
            throw new McpException($"Project or solution not found: {path}");

        var binlog = timings ? Path.Combine(Path.GetTempPath(), $"roslynaut-{Guid.NewGuid():N}.binlog") : null;
        try
        {
            List<string> arguments = ["build", path, "-c", configuration, "-v:q", "-clp:NoSummary"];
            if (binlog is not null)
                arguments.Add($"-bl:{binlog}");

            var (exitCode, output) = await DotnetCli.RunAsync(arguments, TimeSpan.FromMinutes(15), cancellationToken, workingDirectory: Path.GetDirectoryName(path));
            var messages = ParseMessages(output);
            return new(
                exitCode == 0,
                messages.Count(message => message.Severity == "Error"),
                messages.Count(message => message.Severity == "Warning"),
                [.. messages.Take(MaxMessages)],
                exitCode != 0 && messages.Count == 0 ? Tail(output) : null,
                binlog is not null && File.Exists(binlog) ? Timings(binlog) : null);
        }
        finally
        {
            if (binlog is not null)
                File.Delete(binlog);
        }
    }

    /// <summary>Where a build spent its time, from its binary log.</summary>
    internal static BuildTimings Timings(string binlog, int top = 15)
    {
        var build = BinaryLog.ReadBuild(binlog);
        var (projects, targets, tasks) = (new List<Timing>(), new List<Timing>(), new Dictionary<string, (int Count, TimeSpan Duration)>());

        // A project builds once per target framework, and once more as the outer build of several.
        build.VisitAllChildren<Project>(project => projects.Add(new(
            project.TargetFramework is { Length: > 0 } framework ? $"{project.Name} ({framework})" : project.Name, Milliseconds(project.Duration), 1, null)));
        build.VisitAllChildren<Target>(target => targets.Add(new(target.Name, Milliseconds(target.Duration), 1, target.GetNearestParent<Project>()?.Name)));
        build.VisitAllChildren<Microsoft.Build.Logging.StructuredLogger.Task>(task =>
            tasks[task.Name] = tasks.TryGetValue(task.Name, out var total) ? (total.Count + 1, total.Duration + task.Duration) : (1, task.Duration));

        return new(
            Milliseconds(build.Duration),
            [.. projects.OrderByDescending(timing => timing.Milliseconds).Take(top)],
            [.. targets.OrderByDescending(timing => timing.Milliseconds).Take(top)],
            [.. tasks.OrderByDescending(task => task.Value.Duration).Take(top).Select(task => new Timing(task.Key, Milliseconds(task.Value.Duration), task.Value.Count, null))]);

        static long Milliseconds(TimeSpan duration) => (long)duration.TotalMilliseconds;
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
    public sealed record BuildTimings(long Milliseconds, IReadOnlyList<Timing> Projects, IReadOnlyList<Timing> Targets, IReadOnlyList<Timing> Tasks);

    /// <param name="Project">For a target, the project it ran in.</param>
    public sealed record Timing(string Name, long Milliseconds, int Count, string? Project);

    /// <param name="Origin">The file, or the tool that reported it, such as CSC or MSBUILD.</param>
    public sealed record BuildMessage(string Severity, string Code, string Message, string Origin, int? Line, int? Column, string? Project);
}
