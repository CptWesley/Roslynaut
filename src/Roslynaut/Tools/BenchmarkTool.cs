using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed partial class BenchmarkTool(WorkspaceService workspace, NuGetService nuget) : IMcpTool
{
    public Delegate Handler => BenchmarkAsync;

    [McpServerTool(Name = "benchmark", Destructive = true, OpenWorld = true)]
    [Description("Measures how fast pieces of C# are with BenchmarkDotNet, in Release, against the project's current code: per case, the mean "
        + "time with its error and standard deviation, the ratio to the first case, and the bytes allocated per operation with the Gen0 "
        + "collections. Pass cases to compare implementations, as before keeping an optimization; or leave them out to run the project's own "
        + "[Benchmark] classes, if it is a BenchmarkDotNet project. A short job takes about a minute per case; differences within the error "
        + "are noise. It runs real code, so it only works when the server was started with " + EvaluateTool.AllowVariable + "=true.")]
    private async Task<BenchmarkRun> BenchmarkAsync(
        [Description("Path to a .csproj: the project whose code the cases use, or a BenchmarkDotNet project to run.")] string projectPath,
        [Description("The cases by name, each C# code to measure: an expression whose value is kept, such as 'Parser.Parse(text)', or statements "
            + "ending in ';'. The first is the baseline. They can use the project's code and what setup declares.")] Dictionary<string, string>? cases = null,
        [Description("C# members of the benchmark class that the cases use, set up once, such as 'string text = File.ReadAllText(\"in.txt\");' or a "
            + "[GlobalSetup] method.")] string? setup = null,
        [Description("Using directives' namespaces for the cases, such as 'MyApp.Parsing'.")] string[]? usings = null,
        [Description("Without cases: which of the project's benchmarks to run, as BenchmarkDotNet's --filter takes it, such as '*Parser*'.")] string filter = "*",
        [Description("Short (3 iterations, quick and rough) or Default (until the results are stable, minutes per case).")] string job = "Short",
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EvaluateTool.AllowVariable), "true", StringComparison.OrdinalIgnoreCase))
            throw new McpException($"benchmark runs code, so it is off; start the server with the environment variable {EvaluateTool.AllowVariable}=true to allow it.");
        if (job is not ("Short" or "Default"))
            throw new McpException("job is Short or Default.");

        return await RunAsync(projectPath, cases, setup, usings ?? [], filter, job == "Short", workspace, nuget, cancellationToken);
    }

    internal static async Task<BenchmarkRun> RunAsync(
        string projectPath, IReadOnlyDictionary<string, string>? cases, string? setup, IReadOnlyList<string> usings, string filter, bool shortJob,
        WorkspaceService workspace, NuGetService nuget, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(projectPath);
        var result = (await workspace.GetAsync(path)).Results.FirstOrDefault(result => string.Equals(result.ProjectFilePath, path, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"{path} is not a project that loaded; pass a .csproj.");
        var scratch = Directory.CreateTempSubdirectory("roslynaut-benchmark-");
        try
        {
            var artifacts = Path.Combine(scratch.FullName, "artifacts");
            string project;
            List<string> arguments = ["--artifacts", artifacts, "--exporters", "json", "--memory", "--job", shortJob ? "short" : "default"];
            if (cases is { Count: > 0 })
            {
                var version = await nuget.ResolveAsync(path, "BenchmarkDotNet", null, cancellationToken);
                project = Path.Combine(scratch.FullName, "Benchmarks.csproj");
                await File.WriteAllTextAsync(project, $"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <OutputType>Exe</OutputType>
                        <TargetFramework>{result.TargetFramework ?? result.GetProperty("TargetFramework")}</TargetFramework>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                        <NoWarn>$(NoWarn);CS1591;CA1822</NoWarn>
                      </PropertyGroup>
                      <ItemGroup>
                        <PackageReference Include="BenchmarkDotNet" Version="{version.Version}" />
                        <ProjectReference Include="{System.Security.SecurityElement.Escape(path)}" />
                      </ItemGroup>
                    </Project>
                    """, cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(scratch.FullName, "Program.cs"), ProgramOf(cases, setup, usings), cancellationToken);
                arguments.AddRange(["--filter", "*"]);
            }
            else
            {
                project = path;
                arguments.AddRange(["--filter", filter]);
            }

            var (exitCode, output) = await DotnetCli.RunAsync(["run", "-c", "Release", "--project", project, "--", .. arguments], TimeSpan.FromMinutes(60), cancellationToken,
                workingDirectory: Path.GetDirectoryName(project));
            var reports = Directory.Exists(artifacts) ? Directory.EnumerateFiles(artifacts, "*-report*.json", SearchOption.AllDirectories).ToList() : [];
            if (reports.Count == 0)
                return new([], $"BenchmarkDotNet gave no results (exit code {exitCode}): {BuildTool.Tail(output)}");

            return new([.. reports.SelectMany(report => CasesOf(File.ReadAllText(report), cases?.Keys.First()))], null);
        }
        finally
        {
            try
            {
                scratch.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A benchmark process may hold its files a moment longer.
            }
        }
    }

    /// <summary>A program that runs the cases as one benchmark class, the first as the baseline, keeping each expression's value without boxing it.</summary>
    internal static string ProgramOf(IReadOnlyDictionary<string, string> cases, string? setup, IReadOnlyList<string> usings)
    {
        var code = new StringBuilder();
        foreach (var name in usings.Prepend("BenchmarkDotNet.Running").Prepend("BenchmarkDotNet.Engines").Prepend("BenchmarkDotNet.Attributes"))
            code.AppendLine($"using {name};");
        code.AppendLine("BenchmarkSwitcher.FromAssembly(typeof(Cases).Assembly).Run(args);");
        code.AppendLine("public class Cases");
        code.AppendLine("{");
        if (setup is not null)
            code.AppendLine(setup);
        var first = true;
        foreach (var (name, body) in cases)
        {
            var trimmed = body.Trim();
            code.AppendLine($"    [Benchmark(Baseline = {(first ? "true" : "false")}, Description = \"{name.Replace("\"", "'")}\")]");
            code.AppendLine(trimmed.EndsWith(';') || trimmed.EndsWith('}')
                ? $"    public void {Identifier(name)}() {{ {trimmed} }}"
                : $"    public void {Identifier(name)}() => DeadCodeEliminationHelper.KeepAliveWithoutBoxing({trimmed});");
            first = false;
        }

        code.AppendLine("}");
        return code.ToString();
    }

    private static string Identifier(string name) => "Case_" + NotIdentifier().Replace(name, "_");

    [GeneratedRegex(@"[^A-Za-z0-9_]")]
    private static partial Regex NotIdentifier();

    /// <summary>The cases of a BenchmarkDotNet JSON report (JsonExporter.Full), with their ratio to the baseline of the report.</summary>
    internal static IEnumerable<BenchmarkCase> CasesOf(string json, string? baselineName)
    {
        using var report = JsonDocument.Parse(json);
        var benchmarks = report.RootElement.GetProperty("Benchmarks").EnumerateArray().Select(benchmark =>
        {
            var statistics = benchmark.TryGetProperty("Statistics", out var found) && found.ValueKind == JsonValueKind.Object ? found : default;
            double Number(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.NaN;
            var memory = benchmark.TryGetProperty("Memory", out var allocations) ? allocations : default;
            var operations = Number(memory, "TotalOperations");
            // MethodTitle is the Description a case has, quoted.
            var name = benchmark.TryGetProperty("MethodTitle", out var title) && title.GetString() is { Length: > 0 } text
                ? text.Trim('\'')
                : benchmark.GetProperty("Method").GetString() ?? "?";
            return (Name: name, Baseline: name == baselineName,
                Mean: Number(statistics, "Mean"),
                Error: statistics.ValueKind == JsonValueKind.Object && statistics.TryGetProperty("ConfidenceInterval", out var interval) ? Number(interval, "Margin") : double.NaN,
                StandardDeviation: Number(statistics, "StandardDeviation"),
                Bytes: Number(memory, "BytesAllocatedPerOperation"),
                Gen0: operations > 0 ? Number(memory, "Gen0Collections") * 1000 / operations : double.NaN);
        }).ToList();

        var baselineMean = benchmarks.FirstOrDefault(benchmark => benchmark.Baseline).Mean;
        return benchmarks.Select(benchmark => new BenchmarkCase(
            benchmark.Name, Round(benchmark.Mean), Round(benchmark.Error), Round(benchmark.StandardDeviation),
            double.IsNaN(baselineMean) || baselineMean == 0 ? null : Math.Round(benchmark.Mean / baselineMean, 2),
            double.IsNaN(benchmark.Bytes) ? null : (long)benchmark.Bytes,
            double.IsNaN(benchmark.Gen0) ? null : Math.Round(benchmark.Gen0, 4)));

        static double? Round(double value) => double.IsNaN(value) ? null : Math.Round(value, value < 10 ? 3 : 1);
    }

    /// <param name="Cases">The results, as BenchmarkDotNet reports them.</param>
    /// <param name="Output">When there are none: why, from the end of BenchmarkDotNet's output, such as a compile error in a case.</param>
    public sealed record BenchmarkRun(IReadOnlyList<BenchmarkCase> Cases, string? Output);

    /// <param name="MeanNs">The mean time per operation, in nanoseconds.</param>
    /// <param name="ErrorNs">Half the 99.9% confidence interval of the mean.</param>
    /// <param name="Ratio">The mean against the baseline's, the first case's.</param>
    /// <param name="AllocatedBytes">The bytes allocated per operation.</param>
    /// <param name="Gen0PerThousand">The Gen0 collections per 1000 operations.</param>
    public sealed record BenchmarkCase(string Name, double? MeanNs, double? ErrorNs, double? StdDevNs, double? Ratio, long? AllocatedBytes, double? Gen0PerThousand);
}
