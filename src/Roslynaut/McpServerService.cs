using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        var serverTools = tools.Select(tool => McpServerTool.Create(tool.Handler)).ToList();

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
