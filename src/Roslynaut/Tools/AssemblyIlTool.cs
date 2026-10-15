using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class AssemblyIlTool(AssemblyLoader assemblies, ResultCache cache) : IMcpTool
{
    public Delegate Handler => GetIlAsync;

    [McpServerTool(Name = "assembly_il", ReadOnly = true)]
    [Description("Disassembles types or methods of any .NET assembly, such as a build output or a dependency, to IL. " + IlDisassembler.ResultDescription)]
    private Task<PagedResult<MethodIL>> GetIlAsync(
        [Description(IlDisassembler.AssemblyPath)] string assemblyPath,
        [Description(IlDisassembler.Name)] string name,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = IlDisassembler.DefaultPageSize)
    {
        var assembly = assemblies.Read(assemblyPath);
        return cache.GetPageAsync(assembly, $"assembly_il|{name}", page, pageSize,
            () => Task.FromResult<IEnumerable<MethodIL>>(IlDisassembler.Disassemble(assembly.Module, [name])));
    }
}
