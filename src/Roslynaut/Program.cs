using Microsoft.Extensions.DependencyInjection;
using Roslynaut.Tools;

namespace Roslynaut;

public static class Program
{
    public static async Task<int> Main(params string[] args)
    {
        if (ParseProtocol(args) is not { } protocol)
        {
            await Console.Error.WriteLineAsync("Usage: Roslynaut [--protocol http|sse|stdio]");
            return 1;
        }

        await using var services = new ServiceCollection()
            .AddSingleton<WorkspaceService>()
            .AddSingleton<ResultCache>()
            .AddSingleton<IMcpTool, ProjectInfoTool>()
            .AddSingleton<IMcpTool, WorkspaceSymbolsTool>()
            .AddSingleton<IMcpTool, DocumentSymbolsTool>()
            .AddSingleton<IMcpTool, ListSymbolsTool>()
            .AddSingleton<IMcpTool, FindSymbolsTool>()
            .AddSingleton<IMcpTool, DefinitionTool>()
            .AddSingleton<IMcpTool, ReferencesTool>()
            .AddSingleton<IMcpTool, DiagnosticsTool>()
            .AddSingleton<McpServerService>()
            .BuildServiceProvider();

        await services.GetRequiredService<McpServerService>().StartAsync(protocol);
        return 0;
    }

    private static McpProtocol? ParseProtocol(string[] args) => args switch
    {
        [] => McpProtocol.Stdio,
        ["--protocol", var value] => value.ToLowerInvariant() switch
        {
            "http" => McpProtocol.Http,
            "sse" => McpProtocol.Sse,
            "stdio" => McpProtocol.Stdio,
            _ => null,
        },
        _ => null,
    };
}
