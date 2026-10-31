using System.ComponentModel;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
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
        + "Local variables keep their names if a PDB is at hand. For the original source with comments, use assembly_sources or symbol_source. "
        + "With lowered, it shows the C# the compiler lowered the code to instead: lambdas as closure classes, async and iterator methods as "
        + "state machines (with their generated types), foreach, using, lock and string interpolation spelled out: where hidden allocations, "
        + "boxing and captures come from. Paged.")]
    private Task<PagedResult<DecompiledMember>> DecompileAsync(
        [Description(IlDisassembler.AssemblyPath)] string assemblyPath,
        [Description("Type or member name, optionally partially qualified, e.g. 'System.Linq.Enumerable', 'Dictionary.TryGetValue' or 'Outer.Nested'; "
            + "or a documentation ID such as 'M:Ns.Type.Method(System.Int32)'. A method name returns all its overloads. "
            + "A generic arity, as in 'List`1' or 'List<T>', must match; without one, any arity does.")] string name,
        [Description("Show the lowered code, as the compiler emits it, with the compiler-generated types of a method's lambdas and state machines; "
            + "build the project first to see its current source this way.")] bool lowered = false,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = IlDisassembler.DefaultPageSize)
    {
        var assembly = assemblies.Read(assemblyPath);
        return cache.GetPageAsync(assembly, $"decompile|{name}|{lowered}", page, pageSize,
            () => Task.Run<IEnumerable<DecompiledMember>>(() => Decompile(assembly, [name], pdbs.FindPdb(assembly.Path.Value), lowered)));
    }

    /// <param name="lowered">Decompiles without rebuilding what the compiler lowered, and adds the generated types a method's code lives in.</param>
    internal static List<DecompiledMember> Decompile(LoadedAssembly assembly, IReadOnlyCollection<string> names, Pdb? pdb = null, bool lowered = false)
    {
        List<(string Name, int Token)> members;
        lock (assembly.Module)
        {
            var found = IlDisassembler.FindMembers(assembly.Module, names).ToList();
            members = [.. found.Select(member => (member.FullName, member.MetadataToken.ToInt32()))];
            if (lowered)
                members.AddRange(found.OfType<Mono.Cecil.MethodDefinition>().SelectMany(GeneratedTypesOf).Distinct().Select(type => (type.FullName, type.MetadataToken.ToInt32())));
        }

        // From bytes, so the file stays unlocked. References resolve next to it, from the NuGet cache, or from installed frameworks, read whole
        // into memory too: by default the resolver keeps them open, and a build cannot write a referenced project's output.
        var path = assembly.Path.Value;
        using var file = new ICSharpCode.Decompiler.Metadata.PEFile(path, new MemoryStream(File.ReadAllBytes(path)));
        var resolver = new UniversalAssemblyResolver(path, throwOnError: false, file.DetectTargetFrameworkId(), streamOptions: PEStreamOptions.PrefetchEntireImage);
        var settings = lowered
            ? new DecompilerSettings
            {
                AnonymousMethods = false, AsyncAwait = false, YieldReturn = false, LocalFunctions = false, ExpressionTrees = false, LockStatement = false,
                UsingStatement = false, ForEachStatement = false, StringInterpolation = false, SwitchStatementOnString = false, AutomaticProperties = false,
                AutomaticEvents = false, NullPropagation = false, ShowXmlDocumentation = false,
            }
            : new DecompilerSettings();
        var decompiler = new CSharpDecompiler(file, resolver, settings)
        {
            DebugInfoProvider = pdb is null ? null : new DebugInfo(pdb),
        };
        return [.. members.Select(member => new DecompiledMember(member.Name, Decompile(decompiler, member.Token)))];
    }

    /// <summary>
    /// The compiler-generated nested types a method's code lives in: its state machine, as &lt;Method&gt;d__4, and the closure classes of the
    /// type's lambdas, &lt;&gt;c and &lt;&gt;c__DisplayClass, which the method's IL names.
    /// </summary>
    private static IEnumerable<Mono.Cecil.TypeDefinition> GeneratedTypesOf(Mono.Cecil.MethodDefinition method)
    {
        var named = method.Body?.Instructions.Select(instruction => instruction.Operand switch
        {
            Mono.Cecil.MemberReference member => member.DeclaringType?.Resolve(),
            _ => null,
        }).OfType<Mono.Cecil.TypeDefinition>().ToHashSet() ?? [];
        return method.DeclaringType.NestedTypes.Where(nested => nested.Name.StartsWith('<')
            && (nested.Name.StartsWith($"<{method.Name}>", StringComparison.Ordinal) || named.Contains(nested)));
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
