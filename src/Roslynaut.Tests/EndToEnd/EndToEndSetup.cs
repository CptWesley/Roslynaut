using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>
/// Writes the sample solution, starts one server for all end-to-end tests, and builds the solution through it with a binary log, so the
/// solution has its build output and the build_* tools have a full build to read. Then commits the sample, for affected_tests.
/// </summary>
[SetUpFixture]
public class EndToEndSetup
{
    internal static SampleSolution Sample { get; private set; } = null!;

    internal static ServerProcess Server { get; private set; } = null!;

    /// <summary>What build returned for the first build of the solution.</summary>
    internal static BuildTool.BuildResult Build { get; private set; } = null!;

    internal static string BuildLog => Sample.In("build.binlog");

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        Sample = await SampleSolution.CreateAsync();
        Server = await ServerProcess.StartAsync(new() { [EvaluateTool.AllowVariable] = "true" });
        Build = await Server.CallAsync<BuildTool.BuildResult>("build", new() { ["projectPath"] = Sample.Solution, ["binlogPath"] = BuildLog });
        Build.Succeeded.Should().BeTrue(Build.Output ?? string.Join("\n", Build.Messages));
        await Sample.CommitAsync();
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (Server is not null)
            await Server.DisposeAsync();
        Sample?.Delete();
    }
}

/// <summary>
/// The base of the end-to-end tests: they share the server and the sample, so they run one at a time, and only change copies of the sample.
/// Exclude them with --filter "TestCategory!=EndToEnd".
/// </summary>
[Category("EndToEnd")]
[NonParallelizable]
public abstract class EndToEndTest
{
    internal static SampleSolution Sample => EndToEndSetup.Sample;

    internal static ServerProcess Server => EndToEndSetup.Server;

    /// <summary>Waits until a condition on what the server sees holds, as its file watchers bring changes in a moment after they happen.</summary>
    internal static async Task EventuallyAsync(Func<Task<bool>> condition, string because)
    {
        for (var attempt = 0; !await condition(); attempt++)
        {
            attempt.Should().BeLessThan(60, because);
            await Task.Delay(500);
        }
    }
}
