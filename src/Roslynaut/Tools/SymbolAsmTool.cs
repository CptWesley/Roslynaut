using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class SymbolAsmTool(WorkspaceService workspace, AssemblyLoader assemblies, ResultCache cache) : IMcpTool
{
    public Delegate Handler => GetAsmAsync;

    [McpServerTool(Name = "symbol_asm", ReadOnly = true)]
    [Description("Compiles the declaring project from its current source and disassembles the native code the JIT generates for a type, method, "
        + "property or event declared in it. " + JitDisassembler.ResultDescription)]
    private async Task<PagedResult<MethodAsm>> GetAsmAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.SymbolPath)] string symbol,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = IlDisassembler.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        return await cache.GetPageAsync(solution, $"symbol_asm|{symbol}", page, pageSize,
            () => SymbolIlTool.DisassembleAsync(solution, assemblies, symbol, JitDisassembler.Disassemble, optimize: true, cancellationToken));
    }
}
