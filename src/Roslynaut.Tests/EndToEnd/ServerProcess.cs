using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>The server as an MCP client starts it, in a process of its own spoken to over stdio, and calls to its tools as JSON.</summary>
internal sealed class ServerProcess : IAsyncDisposable
{
    /// <summary>Reads results into the server's own records, as the server writes them.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The server logs to standard error; the end of that explains a crash.
    private readonly ConcurrentQueue<string> _log;

    private ServerProcess(McpClient client, ConcurrentQueue<string> log) => (Client, _log) = (client, log);

    public McpClient Client { get; }

    public static async Task<ServerProcess> StartAsync(Dictionary<string, string?>? environment = null, params string[] arguments)
    {
        var log = new ConcurrentQueue<string>();
        var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Name = "roslynaut",
            Command = DotnetCli.Executable,
            Arguments = ["exec", Path.Combine(AppContext.BaseDirectory, "Roslynaut.dll"), "--protocol", "stdio", .. arguments],
            EnvironmentVariables = environment,
            StandardErrorLines = line =>
            {
                log.Enqueue(line);
                while (log.Count > 50)
                    log.TryDequeue(out _);
            },
        }));
        return new ServerProcess(client, log);
    }

    /// <summary>Calls a tool, which has to succeed, and returns the text it returns.</summary>
    public async Task<string> CallTextAsync(string tool, Dictionary<string, object?>? arguments = null)
    {
        var (isError, text) = await CallRawAsync(tool, arguments);
        isError.Should().BeFalse($"{tool} should succeed, but returned: {text}");
        return text;
    }

    /// <summary>Calls a tool, which has to succeed, and parses the JSON it returns.</summary>
    public async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?>? arguments = null) =>
        JsonDocument.Parse(await CallTextAsync(tool, arguments)).RootElement.Clone();

    /// <summary>Calls a tool, which has to succeed, and reads the JSON it returns as one of the server's types.</summary>
    public async Task<T> CallAsync<T>(string tool, Dictionary<string, object?>? arguments = null) =>
        JsonSerializer.Deserialize<T>(await CallTextAsync(tool, arguments), Json)!;

    /// <summary>Calls a tool that has to fail, and returns the error it gives.</summary>
    public async Task<string> CallFailingAsync(string tool, Dictionary<string, object?>? arguments = null)
    {
        var (isError, text) = await CallRawAsync(tool, arguments);
        isError.Should().BeTrue($"{tool} should fail, but returned: {text}");
        return text;
    }

    /// <summary>Calls a tool that may fail, and returns whether it did with the text it returns.</summary>
    public async Task<(bool IsError, string Text)> CallRawAsync(string tool, Dictionary<string, object?>? arguments)
    {
        // A tool that hangs fails its test rather than the run.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        CallToolResult result;
        try
        {
            result = await Client.CallToolAsync(tool, arguments, cancellationToken: timeout.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Calling {tool} failed: {e.Message}\nThe server's last log lines:\n{string.Join("\n", _log)}", e);
        }

        return (result.IsError == true, result.Content.OfType<TextContentBlock>().Single().Text);
    }

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
