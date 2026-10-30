using System.Diagnostics;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>
/// The server over HTTP, stateless, and over legacy SSE, each in a process of its own on a free port; and the command line it rejects.
/// These servers do not allow evaluate.
/// </summary>
public class TransportTests : EndToEndTest
{
    private static readonly string ServerAssembly = Path.Combine(AppContext.BaseDirectory, "Roslynaut.dll");

    [TestCase("http", HttpTransportMode.StreamableHttp, "")]
    [TestCase("sse", HttpTransportMode.Sse, "/sse")]
    public async Task Http_ServesTheTools(string protocol, HttpTransportMode mode, string path)
    {
        using var server = Process.Start(new ProcessStartInfo(DotnetCli.Executable)
        {
            ArgumentList = { "exec", ServerAssembly, "--protocol", protocol },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment = { ["ASPNETCORE_URLS"] = "http://127.0.0.1:0", [EvaluateTool.AllowVariable] = "false" },
        })!;
        try
        {
            // Kestrel logs where it listens once it does; the rest of the log has to be read too, or the server blocks writing it.
            Uri? url = null;
            while (url is null && await server.StandardOutput.ReadLineAsync() is { } line)
                url = Regex.Match(line, @"Now listening on: (http://\S+)") is { Success: true } match ? new Uri(match.Groups[1].Value + path) : null;
            url.Should().NotBeNull(url is null ? await server.StandardError.ReadToEndAsync() : "");
            _ = server.StandardOutput.ReadToEndAsync();
            _ = server.StandardError.ReadToEndAsync();

            await using var client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = url!, TransportMode = mode }));

            (await client.ListToolsAsync()).Select(tool => tool.Name).Should().BeEquivalentTo((await Server.Client.ListToolsAsync()).Select(tool => tool.Name));
            var graph = await client.CallToolAsync("project_graph", new Dictionary<string, object?> { ["projectPath"] = Sample.Solution });
            graph.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("\"name\":\"Shapes.App\"");
            var evaluate = await client.CallToolAsync("evaluate", new Dictionary<string, object?> { ["projectPath"] = Sample.Solution, ["code"] = "1 + 1" });
            evaluate.IsError.Should().BeTrue();
            evaluate.Content.OfType<TextContentBlock>().Single().Text.Should().Be(
                "An error occurred invoking 'evaluate': evaluate runs code, so it is off; start the server with the environment variable ROSLYNAUT_ALLOW_EVALUATE=true to allow it.");
        }
        finally
        {
            server.Kill(entireProcessTree: true);
        }
    }

    [TestCase("--protocol", "websocket", "'websocket'")]
    [TestCase("--protocol", "1", "'1'")]
    [TestCase("--port", "80", "'--port'")]
    [TestCase("--enable-tools", "no_such_tool", "'no_such_tool'")]
    public async Task UnknownArguments_PrintTheUsage(string option, string value, string rejected)
    {
        var (exitCode, output) = await DotnetCli.RunAsync(["exec", ServerAssembly, option, value], TimeSpan.FromMinutes(1), CancellationToken.None);

        exitCode.Should().Be(1);
        output.Should().Contain(rejected).And.Contain("--protocol <http|sse|stdio>");
    }

    [Test]
    public async Task ToolOptions_PickTheTools()
    {
        await using var server = await ServerProcess.StartAsync(null, "--enable-tools", "project_graph", "find_tests", "--disable-tools", "find_tests");

        (await server.Client.ListToolsAsync()).Select(tool => tool.Name).Should().Equal("project_graph");
    }

    [Test]
    public async Task NoInstructions_LeavesTheInstructionsOut()
    {
        await using var server = await ServerProcess.StartAsync(null, "--no-instructions");

        server.Client.ServerInstructions.Should().BeNull();
    }
}
