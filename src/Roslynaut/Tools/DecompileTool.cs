using System.ComponentModel;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.Metadata;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

/// <param name="Member">Cecil's full name of the type or member.</param>
/// <param name="Code">The decompiled C#, or a comment saying why there is none.</param>
public sealed record DecompiledMember(string Member, string Code);

public sealed class DecompileTool(AssemblyLoader assemblies, PdbService pdbs, ResultCache cache) : IMcpTool
{
    public Delegate Handler => DecompileAsync;

    [McpServerTool(Name = "decompile", ReadOnly = true)]
    [Description("Decompiles types or members of any .NET assembly to C# with ILSpy's decompiler, such as a dependency without source. "
        + "definition gives the AssemblyPath of a symbol from a referenced assembly. A type comes out whole, with its nested types. "
        + "This shows what the assembly really contains: code from source generators, members the compiler synthesizes and code IL weavers add. "
        + "Local variables keep their names if a PDB is at hand. For the original source with comments, use assembly_sources or symbol_source. Paged.")]
    private Task<PagedResult<DecompiledMember>> DecompileAsync(
        [Description(IlDisassembler.AssemblyPath)] string assemblyPath,
        [Description("Type or member name, optionally partially qualified, e.g. 'System.Linq.Enumerable', 'Dictionary.TryGetValue' or 'Outer.Nested'; "
            + "or a documentation ID such as 'M:Ns.Type.Method(System.Int32)'. A method name returns all its overloads. "
            + "A generic arity, as in 'List`1' or 'List<T>', must match; without one, any arity does.")] string name,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = IlDisassembler.DefaultPageSize)
    {
        var assembly = assemblies.Read(assemblyPath);
        return cache.GetPageAsync(assembly, $"decompile|{name}", page, pageSize,
            () => Task.Run<IEnumerable<DecompiledMember>>(() => Decompile(assembly, [name], pdbs.FindPdb(assembly.Path.Value))));
    }

    internal static List<DecompiledMember> Decompile(LoadedAssembly assembly, IReadOnlyCollection<string> names, Pdb? pdb = null)
    {
        List<(string Name, int Token)> members;
        lock (assembly.Module)
            members = [.. IlDisassembler.FindMembers(assembly.Module, names).Select(member => (member.FullName, member.MetadataToken.ToInt32()))];

        // From bytes, so the file stays unlocked. References resolve next to it, from the NuGet cache, or from installed frameworks.
        var path = assembly.Path.Value;
        using var file = new ICSharpCode.Decompiler.Metadata.PEFile(path, new MemoryStream(File.ReadAllBytes(path)));
        var decompiler = new CSharpDecompiler(file, new UniversalAssemblyResolver(path, throwOnError: false, file.DetectTargetFrameworkId()), new DecompilerSettings())
        {
            DebugInfoProvider = pdb is null ? null : new DebugInfo(pdb),
        };
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

    /// <summary>Gives ILSpy the names of local variables, and sequence points, from a PDB.</summary>
    private sealed class DebugInfo(Pdb pdb) : IDebugInfoProvider
    {
        public string Description => $"PDB {pdb.Location}";

        public string SourceFileName => pdb.Location;

        public IList<ICSharpCode.Decompiler.DebugInfo.SequencePoint> GetSequencePoints(MethodDefinitionHandle method)
        {
            var points = pdb.SequencePoints(MetadataTokens.GetToken(method)).Select(point => new ICSharpCode.Decompiler.DebugInfo.SequencePoint
            {
                Offset = point.Offset,
                EndOffset = point.Offset + 1,
                StartLine = point.StartLine,
                StartColumn = point.StartColumn,
                EndLine = point.EndLine,
                EndColumn = point.EndColumn,
                DocumentUrl = point.Document.IsNil ? "" : pdb.NameOf(point.Document),
            }).ToList();

            // A point ends where the next starts, as ILSpy's own provider has it.
            for (var i = 0; i < points.Count - 1; i++)
                points[i].EndOffset = points[i + 1].Offset;
            return points;
        }

        public IList<Variable> GetVariables(MethodDefinitionHandle method) => [.. pdb.LocalsOf(method).Select(local => new Variable(local.Index, local.Name))];

        public bool TryGetName(MethodDefinitionHandle method, int index, out string name)
        {
            name = pdb.LocalsOf(method).FirstOrDefault(local => local.Index == index).Name;
            return name is not null;
        }

        public bool TryGetExtraTypeInfo(MethodDefinitionHandle method, int index, out PdbExtraTypeInfo extraTypeInfo)
        {
            extraTypeInfo = default;
            return false;
        }
    }
}
