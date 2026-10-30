using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ModelContextProtocol;

namespace Roslynaut.Debugging;

/// <summary>A client of the Debug Adapter Protocol, over the standard input and output of a debug adapter such as netcoredbg.</summary>
internal sealed class DapClient : IAsyncDisposable
{
    // Adapters expect absent fields rather than nulls: netcoredbg rejects a null breakpoint condition.
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly Process _adapter;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly Task _reading;
    private int _lastSeq;

    public DapClient(string adapterPath, params string[] arguments)
    {
        var start = new ProcessStartInfo(adapterPath) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        _adapter = Process.Start(start)!;
        _reading = Task.Run(ReadAsync);
    }

    /// <summary>The events the adapter sends, such as stopped, output and exited; completed when it exits.</summary>
    public ChannelReader<JsonElement> Events => _events.Reader;

    /// <summary>Sends a request and returns the body of the response; throws if the adapter reports failure.</summary>
    public async Task<JsonElement> RequestAsync(string command, object? arguments, CancellationToken cancellationToken)
    {
        var seq = Interlocked.Increment(ref _lastSeq);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = response;

        var body = JsonSerializer.SerializeToUtf8Bytes(new { seq, type = "request", command, arguments }, Options);
        await _writing.WaitAsync(cancellationToken);
        try
        {
            var input = _adapter.StandardInput.BaseStream;
            await input.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), cancellationToken);
            await input.WriteAsync(body, cancellationToken);
            await input.FlushAsync(cancellationToken);
        }
        finally
        {
            _writing.Release();
        }

        await using (cancellationToken.Register(() => response.TrySetCanceled(cancellationToken)))
            return await response.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_adapter.HasExited)
            _adapter.Kill(entireProcessTree: true);
        await _reading;
        _adapter.Dispose();
        _writing.Dispose();
    }

    private async Task ReadAsync()
    {
        var output = _adapter.StandardOutput.BaseStream;
        try
        {
            while (await ReadContentLengthAsync(output) is { } length)
            {
                var buffer = new byte[length];
                await output.ReadExactlyAsync(buffer);
                using var json = JsonDocument.Parse(buffer);
                var message = json.RootElement.Clone();
                switch (message.GetProperty("type").GetString())
                {
                    case "response" when _pending.TryRemove(message.GetProperty("request_seq").GetInt32(), out var response):
                        if (message.GetProperty("success").GetBoolean())
                            response.TrySetResult(message.TryGetProperty("body", out var body) ? body : default);
                        else
                            response.TrySetException(new McpException($"The debugger failed to {message.GetProperty("command").GetString()}: {Text(message, "message")}"));
                        break;
                    case "event":
                        _events.Writer.TryWrite(message);
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or EndOfStreamException or JsonException)
        {
        }
        finally
        {
            _events.Writer.TryComplete();
            foreach (var response in _pending.Values)
                response.TrySetException(new McpException("The debugger exited."));
        }
    }

    /// <summary>Reads the headers of a message, up to their empty line; null at the end of the stream.</summary>
    private static async Task<int?> ReadContentLengthAsync(Stream stream)
    {
        var headers = new StringBuilder();
        var one = new byte[1];
        while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one) == 0)
                return null;
            headers.Append((char)one[0]);
        }

        var line = headers.ToString().Split("\r\n").First(header => header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        return int.Parse(line["Content-Length:".Length..].Trim());
    }

    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
