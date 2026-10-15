using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class AssemblyIlTool(AssemblyLoader assemblies, ResultCache cache) : IMcpTool
{
    public Delegate Handler => GetIlAsync;

    [McpServerTool(Name = "assembly_il", ReadOnly = true)]
    [Description("Disassembles types or methods of any .NET assembly, such as a build output or a dependency, to IL. " + IlDisassembler.ResultDescription)]
    private Task<PagedResult<MethodIL>> GetIlAsync(
        [Description("Path to the .dll or .exe.")] string assemblyPath,
        [Description("Type or method name, optionally partially qualified, e.g. 'System.String.Concat', 'Dictionary.TryGetValue' or 'Outer.Nested'; "
            + "or a documentation ID such as 'M:Ns.Type.Method(System.Int32)'. A method name returns all its overloads.")] string name,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = IlDisassembler.DefaultPageSize)
    {
        var module = assemblies.Read(assemblyPath);
        return cache.GetPageAsync(module, $"assembly_il|{name}", page, pageSize,
            () => Task.FromResult<IEnumerable<MethodIL>>(IlDisassembler.Disassemble(module, [name])));
    }
}
