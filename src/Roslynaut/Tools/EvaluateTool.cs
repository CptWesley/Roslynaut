using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class EvaluateTool(WorkspaceService workspace, AssemblyLoader assemblies) : IMcpTool
{
    /// <summary>The environment variable that has to be "true" for evaluate to run code.</summary>
    public const string AllowVariable = "ROSLYNAUT_ALLOW_EVALUATE";

    private static readonly string HostPath = Path.Combine(AppContext.BaseDirectory, "Roslynaut.ScriptHost.dll");

    public Delegate Handler => EvaluateCodeAsync;

    [McpServerTool(Name = "evaluate", Destructive = true, OpenWorld = true)]
    [Description("Runs C# script code, such as an expression or a few statements, in a separate process that references the projects' current "
        + "code and their dependencies; returns what it wrote to the console and the value of its last expression. It runs real code, so it "
        + "only works when the server was started with the environment variable " + AllowVariable + "=true.")]
    private async Task<Evaluation> EvaluateCodeAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("C# script code. A last expression without a semicolon is the value, e.g. 'new MyApp.Parser().Parse(\"1+2\")'. "
            + "System, System.Collections.Generic, System.IO, System.Linq, System.Text and System.Threading.Tasks are imported.")] string code,
        [Description("How long it may run before it is stopped.")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(AllowVariable), "true", StringComparison.OrdinalIgnoreCase))
            throw new McpException($"evaluate runs code, so it is off; start the server with the environment variable {AllowVariable}=true to allow it.");

        var references = new List<string>();
        foreach (var project in (await workspace.GetAsync(projectPath)).Solution.Projects)
        {
            var compiled = await assemblies.CompileAsync((await project.GetCompilationAsync(cancellationToken))!);
            references.Add(compiled.Path.Value);
            references.AddRange(compiled.Dependencies.Value.Values);
        }

        return await RunAsync(references.Distinct(StringComparer.OrdinalIgnoreCase), code, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
    }

    internal static async Task<Evaluation> RunAsync(IEnumerable<string> references, string code, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!File.Exists(HostPath))
            throw new McpException($"The script host is missing: {HostPath}");

        var input = JsonSerializer.Serialize(new { Code = code, References = references.ToArray() });
        var (_, output) = await DotnetCli.RunAsync(["exec", HostPath], timeout, cancellationToken, input);

        // The host ends with one line of JSON; anything else is the runtime failing.
        var result = output.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.StartsWith('{'));
        return result is null
            ? throw new McpException($"The script host failed: {BuildTool.Tail(output)}")
            : JsonSerializer.Deserialize<Evaluation>(result)!;
    }

    /// <param name="Output">What the code wrote to the console.</param>
    /// <param name="Value">The value of the last expression, formatted as the C# interactive window does.</param>
    /// <param name="Error">Compilation errors, or the exception the code threw.</param>
    public sealed record Evaluation(string Output, string? Value, string? Type, string? Error);
}
