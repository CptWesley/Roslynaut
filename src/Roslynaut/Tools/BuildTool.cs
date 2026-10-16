using System.ComponentModel;
using System.Text.RegularExpressions;
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
        + "at most 100). Output is the end of the build log when it failed without any.")]
    internal static async Task<BuildResult> BuildAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The configuration, such as Debug or Release.")] string configuration = "Debug",
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(projectPath);
        if (!File.Exists(path))
            throw new McpException($"Project or solution not found: {path}");

        var (exitCode, output) = await DotnetCli.RunAsync(["build", path, "-c", configuration, "-v:q", "-clp:NoSummary"], TimeSpan.FromMinutes(15), cancellationToken);
        var messages = ParseMessages(output);
        return new(
            exitCode == 0,
            messages.Count(message => message.Severity == "Error"),
            messages.Count(message => message.Severity == "Warning"),
            [.. messages.Take(MaxMessages)],
            exitCode != 0 && messages.Count == 0 ? Tail(output) : null);
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

    public sealed record BuildResult(bool Succeeded, int Errors, int Warnings, IReadOnlyList<BuildMessage> Messages, string? Output);

    /// <param name="Origin">The file, or the tool that reported it, such as CSC or MSBUILD.</param>
    public sealed record BuildMessage(string Severity, string Code, string Message, string Origin, int? Line, int? Column, string? Project);
}
