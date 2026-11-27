using System.Diagnostics;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>
/// The server over HTTP, stateless, and over legacy SSE, each in a process of its own on a free port; and the command line it rejects.
/// These servers do not allow evaluate, so they leave it out.
/// </summary>
public class TransportTests : EndToEndTest
{
    private static readonly string ServerAssembly = Path.Combine(AppContext.BaseDirectory, "Roslynaut.dll");

    [TestCase("http", HttpTransportMode.StreamableHttp, "")]
    [TestCase("sse", HttpTransportMode.Sse, "/sse")]
    public async Task Http_ServesTheTools(string protocol, HttpTransportMode mode, string path)
    {
        var start = new ProcessStartInfo(DotnetCli.Executable)
        {
            ArgumentList = { "exec", ServerAssembly, "--protocol", protocol },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var (name, value) in EndToEndSetup.AllTools())
            start.Environment[name] = value;
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment[EvaluateTool.AllowVariable] = "false";
        using var server = Process.Start(start)!;
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

            // Without evaluate allowed, the tools that run code are left out.
            (await client.ListToolsAsync()).Select(tool => tool.Name).Should().BeEquivalentTo(
                (await Server.Client.ListToolsAsync()).Select(tool => tool.Name).Except(["evaluate", "benchmark"]));
            var graph = await client.CallToolAsync("project_graph", new Dictionary<string, object?> { ["projectPath"] = Sample.Solution });
            graph.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("\"name\":\"Shapes.App\"");

            // As a page on another name that resolves to this machine would ask.
            using var http = new HttpClient();
            var rebound = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Host = "rebound.example" } });
            rebound.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
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
    public async Task ToolOptions_LeaveOutToolsThatCannotWork()
    {
        await using var server = await ServerProcess.StartAsync(new() { [EvaluateTool.AllowVariable] = "false" }, "--enable-tools", "project_graph", "evaluate");

        (await server.Client.ListToolsAsync()).Select(tool => tool.Name).Should().Equal("project_graph");
    }

    [Test]
    public async Task NoInstructions_LeavesTheInstructionsOut()
    {
        await using var server = await ServerProcess.StartAsync(null, "--no-instructions");

        server.Client.ServerInstructions.Should().BeNull();
    }

    [Test]
    public async Task Install_PassesTheDescriptionOptionsOn()
    {
        var config = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("dotnet", ["exec", ServerAssembly, "--install", "claude", "--no-hooks", "--tool-descriptions", "none", "--field-descriptions", "caveman"])
            {
                RedirectStandardOutput = true, Environment = { ["CLAUDE_CONFIG_DIR"] = config },
            };
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(0, output);
            System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(config, ".claude.json")))!["mcpServers"]!["roslynaut"]!["args"]!.ToJsonString()
                .Should().EndWith("""
                    "--tool-descriptions","none","--field-descriptions","caveman"]
                    """);
        }
        finally
        {
            Directory.Delete(config, recursive: true);
        }
    }

    // The agent gets what was left out from explain_tool, which keeps its own descriptions so that the agent knows to call it.
    [Test]
    public async Task NoDescriptions_LeaveThemToExplainTool()
    {
        await using var server = await ServerProcess.StartAsync(null, "--enable-tools", "references", "explain_tool", "--tool-descriptions", "none", "--field-descriptions", "none");

        var tools = (await server.Client.ListToolsAsync()).ToDictionary(tool => tool.Name);
        tools["references"].Description.Should().BeNullOrEmpty();
        tools["references"].JsonSchema.GetRawText().Should().NotContain("\"description\"");
        tools["explain_tool"].Description.Should().NotBeNullOrEmpty();
        tools["explain_tool"].JsonSchema.GetRawText().Should().Contain("\"description\"");
        server.Client.ServerInstructions.Should().Contain("explain_tool");

        var explained = (await server.CallAsync<ToolExplanation[]>("explain_tool", new() { ["tools"] = new[] { "mcp__roslynaut__references" } })).Single();
        explained.Name.Should().Be("references");
        explained.Description.Should().StartWith("Finds all references");
        explained.InputSchema.GetProperty("properties").GetProperty("symbol").GetProperty("description").GetString().Should().Contain("Symbol path");
    }
}
