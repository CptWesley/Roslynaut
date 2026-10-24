using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class TypeLayoutTool(WorkspaceService workspace, AssemblyLoader assemblies) : IMcpTool
{
    public Delegate Handler => GetLayoutAsync;

    [McpServerTool(Name = "type_layout", ReadOnly = true)]
    [Description("Shows where the runtime puts the fields of a class or struct declared in the solution, compiled from current source: each "
        + "field's offset, as the JIT's code addresses it (symbol_asm), and size, base types' fields included; the padding between them; the size "
        + "of an object or struct; and the 64-byte cache line each field starts in. Laid out by the .NET the project is built for, as symbol_asm "
        + "compiles with, without running any of its code.")]
    private async Task<TypeLayout> GetLayoutAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The class or struct, by symbol path. " + ToolDefaults.SymbolPath)] string symbol,
        [Description(SymbolAsmTool.TypeArguments + " Those left out are System.Object, as all reference types lay out alike.")] string[]? typeArguments = null,
        CancellationToken cancellationToken = default) =>
        await LayOutAsync((await workspace.GetAsync(projectPath)).Solution, assemblies, symbol, typeArguments ?? [], cancellationToken);

    internal static async Task<TypeLayout> LayOutAsync(Solution solution, AssemblyLoader assemblies, string path, IReadOnlyList<string> typeArguments, CancellationToken cancellationToken)
    {
        if ((await SymbolTree.ResolveAsync(solution, path, cancellationToken)).OfType<INamedTypeSymbol>().FirstOrDefault() is not { } type
            || solution.GetProject(type.ContainingAssembly, cancellationToken) is not { } project)
            throw new McpException($"'{path}' is not a class or struct declared in the solution.");

        var parameters = SymbolAsmTool.ArgumentsOf(type).Count();
        if (typeArguments.Count > parameters)
            throw new McpException($"{type.ToDisplayString()} takes {parameters} type arguments, not {typeArguments.Count}.");

        var compilation = (await project.GetCompilationAsync(cancellationToken))!;
        var arguments = (await SymbolAsmTool.BindTypeArgumentsAsync(solution, type, typeArguments, cancellationToken))
            .Concat(Enumerable.Repeat(SymbolAsmTool.AssemblyQualifiedName(compilation.GetSpecialType(SpecialType.System_Object)), parameters - typeArguments.Count));
        var compiled = await assemblies.CompileAsync(compilation, optimize: true);
        return await Task.Run(() => JitDisassembler.LayOut(compiled, SymbolAsmTool.AssemblyQualifiedName(type, arguments)), cancellationToken);
    }
}
