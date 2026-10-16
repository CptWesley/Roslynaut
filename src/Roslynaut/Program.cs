using Microsoft.Extensions.DependencyInjection;
using Roslynaut.Debugging;
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
            .AddSingleton<IMcpTool, DecompileTool>()
            .AddSingleton<IMcpTool, AssemblySourcesTool>()
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
