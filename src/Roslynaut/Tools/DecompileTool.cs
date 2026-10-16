using System.ComponentModel;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

/// <param name="Member">Cecil's full name of the type or member.</param>
/// <param name="Code">The decompiled C#, or a comment saying why there is none.</param>
public sealed record DecompiledMember(string Member, string Code);

public sealed class DecompileTool(AssemblyLoader assemblies, ResultCache cache) : IMcpTool
{
    public Delegate Handler => DecompileAsync;

    [McpServerTool(Name = "decompile", ReadOnly = true)]
    [Description("Decompiles types or members of any .NET assembly to C# with ILSpy's decompiler, such as a dependency without source. "
        + "definition gives the AssemblyPath of a symbol from a referenced assembly. A type comes out whole, with its nested types. Paged.")]
    private Task<PagedResult<DecompiledMember>> DecompileAsync(
        [Description(IlDisassembler.AssemblyPath)] string assemblyPath,
        [Description("Type or member name, optionally partially qualified, e.g. 'System.Linq.Enumerable', 'Dictionary.TryGetValue' or 'Outer.Nested'; "
            + "or a documentation ID such as 'M:Ns.Type.Method(System.Int32)'. A method name returns all its overloads.")] string name,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = IlDisassembler.DefaultPageSize)
    {
        var assembly = assemblies.Read(assemblyPath);
        return cache.GetPageAsync(assembly, $"decompile|{name}", page, pageSize,
            () => Task.Run<IEnumerable<DecompiledMember>>(() => Decompile(assembly, [name])));
    }

    internal static List<DecompiledMember> Decompile(LoadedAssembly assembly, IReadOnlyCollection<string> names)
    {
        List<(string Name, int Token)> members;
        lock (assembly.Module)
            members = [.. IlDisassembler.FindMembers(assembly.Module, names).Select(member => (member.FullName, member.MetadataToken.ToInt32()))];

        // From bytes, so the file stays unlocked. References resolve next to it, from the NuGet cache, or from installed frameworks.
        var path = assembly.Path.Value;
        using var file = new PEFile(path, new MemoryStream(File.ReadAllBytes(path)));
        var decompiler = new CSharpDecompiler(file, new UniversalAssemblyResolver(path, throwOnError: false, file.DetectTargetFrameworkId()), new DecompilerSettings());
        return [.. members.Select(member => new DecompiledMember(member.Name, Decompile(decompiler, member.Token)))];
    }

    private static string Decompile(CSharpDecompiler decompiler, int token)
    {
        try
        {
            return decompiler.DecompileAsString(MetadataTokens.EntityHandle(token));
        }
        catch (Exception e)
        {
            return $"// Not decompiled: {e.GetType().Name}: {e.Message}";
        }
    }
}
