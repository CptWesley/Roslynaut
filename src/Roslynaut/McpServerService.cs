using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Roslynaut.Tools;

namespace Roslynaut;

public enum McpProtocol
{
    Http,
    Sse,
    Stdio,
}

/// <summary>Owns the lifetime of the MCP server. Tools are discovered through DI.</summary>
public sealed class McpServerService(IEnumerable<IMcpTool> tools)
{
    /// <summary>Starts the server; the returned task completes when the server shuts down.</summary>
    public async Task StartAsync(McpProtocol protocol)
    {
        var serverTools = tools.Select(tool => (McpServerTool)new ExplainingTool(McpServerTool.Create(tool.Handler), tool.Handler.Method)).ToList();

        if (protocol == McpProtocol.Stdio)
        {
            // stdout carries the protocol, so log to stderr only.
            var host = Host.CreateEmptyApplicationBuilder(settings: null);
            host.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            host.Services.AddMcpServer().WithStdioServerTransport().WithTools(serverTools);
            await host.Build().RunAsync();
            return;
        }

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options =>
            {
                // Legacy SSE needs in-memory sessions, so it cannot run stateless.
                // It also lacks request backpressure (MCP9004): only expose it to trusted clients.
#pragma warning disable MCP9004
                options.EnableLegacySse = protocol == McpProtocol.Sse;
#pragma warning restore MCP9004
                options.Stateless = protocol == McpProtocol.Http;
            })
            .WithTools(serverTools);

        var app = builder.Build();
        app.MapMcp();
        await app.RunAsync();
    }
}

/// <summary>
/// A tool whose failures say what went wrong. The SDK passes on only the message of an <see cref="McpException"/>, and turns any other
/// exception into "An error occurred invoking 'tool'.", such as those of binding the arguments to the handler's parameters.
/// </summary>
internal sealed class ExplainingTool(McpServerTool tool, MethodInfo handler) : DelegatingMcpServerTool(tool)
{
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var arguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        if (Missing(ProtocolTool.InputSchema, arguments) is [_, ..] missing)
            throw new McpException($"Missing required argument{(missing.Count > 1 ? "s" : "")}: {string.Join(", ", missing)}.");

        try
        {
            return await base.InvokeAsync(request, cancellationToken);
        }
        catch (Exception e) when (e is not (McpException or OperationCanceledException or InputRequiredException))
        {
            throw new McpException(Explain(e, ProtocolTool.InputSchema, handler, arguments), e);
        }
    }

    /// <summary>The required arguments the call leaves out, or gives as null.</summary>
    internal static List<string> Missing(JsonElement schema, IDictionary<string, JsonElement> arguments) =>
    [
        .. (schema.TryGetProperty("required", out var required) ? required.EnumerateArray().Select(name => name.GetString()!) : [])
            .Where(name => !arguments.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null),
    ];

    /// <summary>
    /// What went wrong: binding throws a <see cref="JsonException"/> that does not name the argument which does not convert to its parameter's
    /// type, so that argument is looked for; the tool's own exceptions keep their type and message.
    /// </summary>
    internal static string Explain(Exception exception, JsonElement schema, MethodInfo handler, IDictionary<string, JsonElement> arguments)
    {
        if (exception is JsonException)
        {
            foreach (var parameter in handler.GetParameters())
            {
                if (parameter.Name is { } name && arguments.TryGetValue(name, out var value) && !Converts(value, parameter.ParameterType))
                    return $"Argument {name} has to be {TypeOf(schema, name)}, not {Shorten(value.GetRawText())}.";
            }
        }

        return $"{exception.GetType().Name}: {exception.Message}";
    }

    private static bool Converts(JsonElement value, Type type)
    {
        try
        {
            JsonSerializer.Deserialize(value, type, McpJsonUtilities.DefaultOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>What the schema allows for a parameter, such as "integer or null", or "one of Supertypes, Subtypes".</summary>
    private static string TypeOf(JsonElement schema, string name)
    {
        if (!schema.TryGetProperty("properties", out var properties) || !properties.TryGetProperty(name, out var property))
            return "?";
        if (property.TryGetProperty("enum", out var values))
            return $"one of {string.Join(", ", values.EnumerateArray().Select(value => value.ToString()))}";
        return property.TryGetProperty("type", out var type)
            ? type.ValueKind == JsonValueKind.Array ? string.Join(" or ", type.EnumerateArray().Select(kind => kind.GetString())) : type.GetString() ?? "?"
            : "?";
    }

    private static string Shorten(string json) => json.Length > 100 ? json[..100] + "…" : json;
}
