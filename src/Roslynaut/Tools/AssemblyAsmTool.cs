using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class AssemblyAsmTool(AssemblyLoader assemblies, ResultCache cache) : IMcpTool
{
    public Delegate Handler => GetAsmAsync;

    [McpServerTool(Name = "assembly_asm", ReadOnly = true)]
    [Description("Disassembles the native code the JIT generates for types or methods of any .NET assembly, such as a build output or a dependency. "
        + JitDisassembler.ResultDescription)]
    private Task<PagedResult<MethodAsm>> GetAsmAsync(
        [Description(IlDisassembler.AssemblyPath)] string assemblyPath,
        [Description(IlDisassembler.Name)] string name,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = IlDisassembler.DefaultPageSize)
    {
        var assembly = assemblies.Read(assemblyPath);
        return cache.GetPageAsync(assembly, $"assembly_asm|{name}", page, pageSize,
            () => Task.Run<IEnumerable<MethodAsm>>(() => JitDisassembler.Disassemble(assembly, [name])));
    }
}
