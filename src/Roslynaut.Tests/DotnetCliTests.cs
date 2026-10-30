using System.Diagnostics;
using System.Text.Json;

namespace Roslynaut.Tests;

public class DotnetCliTests
{
    private static readonly string ScriptHost = Path.Combine(AppContext.BaseDirectory, "Roslynaut.ScriptHost.dll");

    // As dotnet build leaves an MSBuild node for reuse: the command starts a process that inherits its output pipes and outlives it.
    [Test]
    public async Task Run_ReturnsWhenTheCommandExits_ThoughAProcessItStartedHoldsItsOutput()
    {
        var sleep = JsonSerializer.Serialize(new { Code = "System.Threading.Thread.Sleep(30000)", References = Array.Empty<string>() });
        var code = $$"""
            var start = new System.Diagnostics.ProcessStartInfo({{JsonSerializer.Serialize(DotnetCli.Executable)}}) { RedirectStandardInput = true };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add({{JsonSerializer.Serialize(ScriptHost)}});
            var sleeper = System.Diagnostics.Process.Start(start);
            sleeper.StandardInput.Write({{JsonSerializer.Serialize(sleep)}});
            sleeper.StandardInput.Close();
            sleeper.Id
            """;

        var watch = Stopwatch.StartNew();
        var (exitCode, output) = await DotnetCli.RunAsync(["exec", ScriptHost], TimeSpan.FromMinutes(1), CancellationToken.None, JsonSerializer.Serialize(new { Code = code, References = Array.Empty<string>() }));
        watch.Stop();

        var result = JsonDocument.Parse(output).RootElement;
        using (var process = Process.GetProcessById(int.Parse(result.GetProperty("Value").GetString()!)))
            process.Kill();
        exitCode.Should().Be(0);
        result.GetProperty("Error").ValueKind.Should().Be(JsonValueKind.Null, output);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }
}
