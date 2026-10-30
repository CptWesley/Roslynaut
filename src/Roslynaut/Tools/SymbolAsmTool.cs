using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Mono.Cecil;

namespace Roslynaut.Tools;

public sealed class SymbolAsmTool(WorkspaceService workspace, AssemblyLoader assemblies, ResultCache cache) : IMcpTool
{
    public const string TypeArguments = "Type arguments for generic code, as code where the symbol is declared would write them, such as ['float'] "
        + "or ['int', 'Vec2?']: the outermost type's parameters first, then a generic method's own.";

    public Delegate Handler => GetAsmAsync;

    // Not read-only: a workload runs code of the project.
    [McpServerTool(Name = "symbol_asm")]
    [Description("Compiles the declaring project from its current source and disassembles the native code the JIT generates for a type, method, "
        + "property or event declared in it. " + JitDisassembler.ResultDescription + " With typeArguments, generic code compiles for those instead, "
        + "as each value type gets code of its own. With pgoWorkload, the code is what long-running code gets: Tier1, with Dynamic PGO's guarded "
        + "devirtualization and block layout, and static readonly fields as constants.")]
    private async Task<PagedResult<MethodAsm>> GetAsmAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.SymbolPath)] string symbol,
        [Description(TypeArguments + " Those left out are reference types, which share one code.")] string[]? typeArguments = null,
        [Description("A parameterless method of the solution, by symbol path, such as a test or a benchmark, to run first: again and again, until "
            + "the JIT has compiled what it calls often at Tier1 with what Dynamic PGO saw, for up to 20 seconds. Each listing is then of the last "
            + "tier its method reached. A method that is not static runs on an instance from its type's parameterless constructor.")] string? pgoWorkload = null,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = IlDisassembler.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        return await cache.GetPageAsync(solution, $"symbol_asm|{symbol}|{string.Join(';', typeArguments ?? [])}|{pgoWorkload}", page, pageSize,
            () => DisassembleAsync(solution, assemblies, symbol, typeArguments ?? [], pgoWorkload, cancellationToken));
    }

    internal static async Task<IEnumerable<MethodAsm>> DisassembleAsync(
        Solution solution, AssemblyLoader assemblies, string symbol, IReadOnlyList<string> typeArguments, string? pgoWorkload, CancellationToken cancellationToken)
    {
        var options = new JitOptions(
            typeArguments.Count > 0 ? await BindTypeArgumentsAsync(solution, (await SymbolTree.ResolveAsync(solution, symbol, cancellationToken))[0], typeArguments, cancellationToken) : [],
            pgoWorkload is null ? null : await WorkloadAsync(solution, assemblies, pgoWorkload, cancellationToken));
        return await SymbolIlTool.DisassembleAsync(solution, assemblies, symbol, (assembly, ids) => JitDisassembler.Disassemble(assembly, ids, options),
            optimize: true, cancellationToken);
    }

    /// <summary>
    /// Binds type names as code where the symbol is declared would write them, with that file's usings, such as "float", "List&lt;int&gt;" or
    /// "Vec2?", to the assembly-qualified names the runtime resolves.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> BindTypeArgumentsAsync(Solution solution, ISymbol symbol, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        if (names.Count == 0)
            return [];

        if (symbol.Locations.FirstOrDefault(location => location.IsInSource) is not { SourceTree: { } tree } location
            || symbol.ContainingAssembly is not { } assembly || solution.GetProject(assembly, cancellationToken) is not { } project)
            throw new McpException($"{symbol.ToDisplayString()} takes no type arguments: pick a type or method declared in the solution.");

        var model = (await project.GetCompilationAsync(cancellationToken))!.GetSemanticModel(tree);
        return [.. names.Select(name => model.GetSpeculativeTypeInfo(location.SourceSpan.Start, SyntaxFactory.ParseTypeName(name), SpeculativeBindingOption.BindAsTypeOrNamespace).Type
            is { TypeKind: not TypeKind.Error } type
                ? AssemblyQualifiedName(type)
                : throw new McpException($"'{name}' is not a type where {symbol.Name} is declared; qualify it, as in 'System.Single' or 'MyGame.Vec2'."))];
    }

    /// <summary>The name that Type.GetType resolves, as in "System.Collections.Generic.List`1[[System.Int32, System.Runtime]], System.Runtime".</summary>
    internal static string AssemblyQualifiedName(ITypeSymbol type) => type switch
    {
        // Before the assembly name, which follows the last ", ".
        IArrayTypeSymbol array => AssemblyQualifiedName(array.ElementType) switch { var element => element.Insert(element.LastIndexOf(", ", StringComparison.Ordinal), $"[{new string(',', array.Rank - 1)}]") },
        INamedTypeSymbol { TypeKind: not TypeKind.Error } named => AssemblyQualifiedName(named.OriginalDefinition, ArgumentsOf(named.TupleUnderlyingType ?? named).Select(AssemblyQualifiedName)),
        _ => throw new McpException($"{type.ToDisplayString()} cannot be a type argument here."),
    };

    /// <summary>
    /// A generic type definition with type arguments, given as assembly-qualified names, the outermost type's first. Reference assemblies, such
    /// as System.Runtime, forward their types to where the runtime has them.
    /// </summary>
    internal static string AssemblyQualifiedName(INamedTypeSymbol definition, IEnumerable<string> arguments)
    {
        var list = arguments.ToList();
        return $"{FullName(definition)}{(list.Count > 0 ? $"[{string.Join(",", list.Select(argument => $"[{argument}]"))}]" : "")}, {definition.ContainingAssembly.Identity.Name}";

        static string FullName(INamedTypeSymbol type) => type.ContainingType is { } outer ? $"{FullName(outer)}+{type.MetadataName}" : Prefix(type.ContainingNamespace) + type.MetadataName;
        static string Prefix(INamespaceSymbol space) => space.IsGlobalNamespace ? "" : $"{Prefix(space.ContainingNamespace)}{space.MetadataName}.";
    }

    /// <summary>The type arguments of a type and the types it is nested in, the outermost's first, as the runtime takes them.</summary>
    internal static IEnumerable<ITypeSymbol> ArgumentsOf(INamedTypeSymbol type) => (type.ContainingType is { } outer ? ArgumentsOf(outer) : []).Concat(type.TypeArguments);

    /// <summary>A parameterless method to run, compiled from current source: its assembly and metadata token.</summary>
    internal static async Task<(LoadedAssembly Assembly, int Token)> WorkloadAsync(Solution solution, AssemblyLoader assemblies, string path, CancellationToken cancellationToken)
    {
        if (await SymbolTree.ResolveAsync(solution, path, cancellationToken) is not [IMethodSymbol { Parameters: [], IsGenericMethod: false, ContainingType.IsGenericType: false } method]
            || solution.GetProject(method.ContainingAssembly, cancellationToken) is not { } project)
            throw new McpException($"The workload '{path}' must be one method of the solution, without parameters or type parameters, such as a test.");

        var compiled = await assemblies.CompileAsync((await project.GetCompilationAsync(cancellationToken))!, optimize: true);
        lock (compiled.Module)
            return (compiled, IlDisassembler.FindMembers(compiled.Module, [method.GetDocumentationCommentId()!]).OfType<MethodDefinition>().Single().MetadataToken.ToInt32());
    }
}
