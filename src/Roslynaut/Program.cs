using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Roslynaut.Debugging;
using Roslynaut.Tools;

namespace Roslynaut;

public static class Program
{
    public static async Task<int> Main(params string[] args)
    {
        await using var services = Services();
        var server = services.GetRequiredService<McpServerService>();
        var names = server.Tools.Select(tool => tool.ProtocolTool.Name).ToArray();

        // A string, not the enum, which would take numbers too.
        var protocol = new Option<string>("--protocol") { Description = "The transport that carries MCP.", DefaultValueFactory = _ => "stdio" }
            .AcceptOnlyFromAmong("http", "sse", "stdio");
        var enable = new Option<string[]>("--enable-tools") { Description = "Serve only these tools.", HelpName = "tool", AllowMultipleArgumentsPerToken = true }
            .AcceptOnlyFromAmong(names);
        var disable = new Option<string[]>("--disable-tools")
        {
            Description = "Leave these tools out, also when --enable-tools names them.", HelpName = "tool", AllowMultipleArgumentsPerToken = true,
        }.AcceptOnlyFromAmong(names);
        var noInstructions = new Option<bool>("--no-instructions") { Description = "Leave out the server instructions, which tell agents which tool to use for what." };
        var root = new RootCommand("An MCP server that exposes Roslyn and MSBuild to agents.") { protocol, enable, disable, noInstructions };
        root.SetAction(async parsed =>
        {
            var (only, without) = (parsed.GetValue(enable), parsed.GetValue(disable) ?? []);
            var tools = server.Tools.Where(tool => (only is not [_, ..] || only.Contains(tool.ProtocolTool.Name)) && !without.Contains(tool.ProtocolTool.Name));
            await server.StartAsync(Enum.Parse<McpProtocol>(parsed.GetValue(protocol)!, ignoreCase: true), [.. tools], !parsed.GetValue(noInstructions));
        });

        // The host handles Ctrl+C and SIGTERM itself and shuts down gracefully; System.CommandLine would give up on it after 2 seconds.
        return await root.Parse(args).InvokeAsync(new() { ProcessTerminationTimeout = null });
    }

    private static ServiceProvider Services() =>
        new ServiceCollection()
            .AddSingleton<WorkspaceService>()
            .AddSingleton<ResultCache>()
            .AddSingleton<AssemblyLoader>()
            .AddSingleton<CodeActionService>()
            .AddSingleton<DebugService>()
            .AddSingleton<NuGetService>()
            .AddSingleton<PdbService>()
            .AddSingleton<IMcpTool, ProjectInfoTool>()
            .AddSingleton<IMcpTool, WorkspaceSymbolsTool>()
            .AddSingleton<IMcpTool, DocumentSymbolsTool>()
            .AddSingleton<IMcpTool, ListSymbolsTool>()
            .AddSingleton<IMcpTool, FindSymbolsTool>()
            .AddSingleton<IMcpTool, DefinitionTool>()
            .AddSingleton<IMcpTool, ReferencesTool>()
            .AddSingleton<IMcpTool, SymbolSourceTool>()
            .AddSingleton<IMcpTool, ImplementationsTool>()
            .AddSingleton<IMcpTool, TypeHierarchyTool>()
            .AddSingleton<IMcpTool, CallersTool>()
            .AddSingleton<IMcpTool, CalleesTool>()
            .AddSingleton<IMcpTool, ExpressionInfoTool>()
            .AddSingleton<IMcpTool, DataFlowTool>()
            .AddSingleton<IMcpTool, UnusedSymbolsTool>()
            .AddSingleton<IMcpTool, GeneratedSourcesTool>()
            .AddSingleton<IMcpTool, DocumentHighlightTool>()
            .AddSingleton<IMcpTool, SignatureHelpTool>()
            .AddSingleton<IMcpTool, CompletionTool>()
            .AddSingleton<IMcpTool, InlayHintsTool>()
            .AddSingleton<IMcpTool, SyntaxTreeTool>()
            .AddSingleton<IMcpTool, CodeMetricsTool>()
            .AddSingleton<IMcpTool, SearchPatternTool>()
            .AddSingleton<IMcpTool, FindOperationsTool>()
            .AddSingleton<IMcpTool, EvaluateTool>()
            .AddSingleton<IMcpTool, DotnetProcessesTool>()
            .AddSingleton<IMcpTool, ProcessThreadsTool>()
            .AddSingleton<IMcpTool, ProcessHeapTool>()
            .AddSingleton<IMcpTool, ProcessObjectsTool>()
            .AddSingleton<IMcpTool, ProcessRootsTool>()
            .AddSingleton<IMcpTool, ProcessCpuTool>()
            .AddSingleton<IMcpTool, ProcessAllocationsTool>()
            .AddSingleton<IMcpTool, ProcessCallsTool>()
            .AddSingleton<IMcpTool, ProcessCountersTool>()
            .AddSingleton<IMcpTool, DebugStartTool>()
            .AddSingleton<IMcpTool, DebugAttachTool>()
            .AddSingleton<IMcpTool, DebugTestTool>()
            .AddSingleton<IMcpTool, DebugSetTool>()
            .AddSingleton<IMcpTool, DebugBreakpointsTool>()
            .AddSingleton<IMcpTool, DebugResumeTool>()
            .AddSingleton<IMcpTool, DebugInspectTool>()
            .AddSingleton<IMcpTool, DebugStopTool>()
            .AddSingleton<IMcpTool, RenameSymbolTool>()
            .AddSingleton<IMcpTool, CodeActionsTool>()
            .AddSingleton<IMcpTool, ApplyCodeActionTool>()
            .AddSingleton<IMcpTool, FixAllTool>()
            .AddSingleton<IMcpTool, FormatTool>()
            .AddSingleton<IMcpTool, OrganizeUsingsTool>()
            .AddSingleton<IMcpTool, ReplaceSymbolBodyTool>()
            .AddSingleton<IMcpTool, InsertBeforeSymbolTool>()
            .AddSingleton<IMcpTool, InsertAfterSymbolTool>()
            .AddSingleton<IMcpTool, SafeDeleteSymbolTool>()
            .AddSingleton<IMcpTool, PreviewEditTool>()
            .AddSingleton<IMcpTool, BuildTool>()
            .AddSingleton<IMcpTool, BuildProblemsTool>()
            .AddSingleton<IMcpTool, BuildIncrementalityTool>()
            .AddSingleton<IMcpTool, BuildFileOriginTool>()
            .AddSingleton<IMcpTool, BuildTargetWhyTool>()
            .AddSingleton<IMcpTool, BuildPropertyTraceTool>()
            .AddSingleton<IMcpTool, BuildLogSearchTool>()
            .AddSingleton<IMcpTool, BuildDiffTool>()
            .AddSingleton<IMcpTool, FindTestsTool>()
            .AddSingleton<IMcpTool, AffectedTestsTool>()
            .AddSingleton<IMcpTool, PublicApiTool>()
            .AddSingleton<IMcpTool, ApiDiffTool>()
            .AddSingleton<IMcpTool, NamespaceGraphTool>()
            .AddSingleton<IMcpTool, ChangeSignatureTool>()
            .AddSingleton<IMcpTool, ExtractInterfaceTool>()
            .AddSingleton<IMcpTool, MoveToNamespaceTool>()
            .AddSingleton<IMcpTool, RunTestsTool>()
            .AddSingleton<IMcpTool, MsBuildEvaluateTool>()
            .AddSingleton<IMcpTool, ProjectGraphTool>()
            .AddSingleton<IMcpTool, WorkspaceOptionsTool>()
            .AddSingleton<IMcpTool, MsBuildPropertyTraceTool>()
            .AddSingleton<IMcpTool, PackagesTool>()
            .AddSingleton<IMcpTool, PackageSearchTool>()
            .AddSingleton<IMcpTool, PackageInfoTool>()
            .AddSingleton<IMcpTool, PackageAssemblyTool>()
            .AddSingleton<IMcpTool, PackageUpgradeCheckTool>()
            .AddSingleton<IMcpTool, PackageWhyTool>()
            .AddSingleton<IMcpTool, PackageContentsTool>()
            .AddSingleton<IMcpTool, PackageLicensesTool>()
            .AddSingleton<IMcpTool, NuGetConfigTool>()
            .AddSingleton<IMcpTool, DiagnosticsTool>()
            .AddSingleton<IMcpTool, AssemblyIlTool>()
            .AddSingleton<IMcpTool, SymbolIlTool>()
            .AddSingleton<IMcpTool, AssemblyAsmTool>()
            .AddSingleton<IMcpTool, SymbolAsmTool>()
            .AddSingleton<IMcpTool, TypeLayoutTool>()
            .AddSingleton<IMcpTool, DecompileTool>()
            .AddSingleton<IMcpTool, AssemblySourcesTool>()
            .AddSingleton<IMcpTool, ResolveStackTraceTool>()
            .AddSingleton<IMcpTool, ProcessAsyncTool>()
            .AddSingleton<IMcpTool, ProcessDumpTool>()
            .AddSingleton<IMcpTool, ProcessEventsTool>()
            .AddSingleton<IMcpTool, CallPathsTool>()
            .AddSingleton<IMcpTool, ControlFlowTool>()
            .AddSingleton<IMcpTool, ThrowsTool>()
            .AddSingleton<IMcpTool, BenchmarkTool>()
            .AddSingleton<IMcpTool, EndpointsTool>()
            .AddSingleton<IMcpTool, DiRegistrationsTool>()
            .AddSingleton<IMcpTool, FindDuplicatesTool>()
            .AddSingleton<McpServerService>()
            .BuildServiceProvider();
}
