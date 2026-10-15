using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class SymbolIlTool(WorkspaceService workspace, AssemblyLoader assemblies, ResultCache cache) : IMcpTool
{
    public Delegate Handler => GetIlAsync;

    [McpServerTool(Name = "symbol_il", ReadOnly = true)]
    [Description("Compiles the declaring project from its current source and disassembles a type, method, property or event declared in it to IL. "
        + IlDisassembler.ResultDescription)]
    private async Task<PagedResult<MethodIL>> GetIlAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.SymbolPath)] string symbol,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = IlDisassembler.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        return await cache.GetPageAsync(solution, $"symbol_il|{symbol}", page, pageSize,
            () => DisassembleAsync(solution, assemblies, symbol, cancellationToken));
    }

    internal static Task<IEnumerable<MethodIL>> DisassembleAsync(Solution solution, AssemblyLoader assemblies, string path, CancellationToken cancellationToken) =>
        DisassembleAsync(solution, assemblies, path, (assembly, ids) => IlDisassembler.Disassemble(assembly.Module, ids), optimize: false, cancellationToken);

    /// <summary>Resolves the symbol path, compiles the declaring projects, and disassembles the symbols with <paramref name="disassemble"/>.</summary>
    /// <param name="optimize">Compiles as a release build, see <see cref="AssemblyLoader.CompileAsync"/>.</param>
    internal static async Task<IEnumerable<T>> DisassembleAsync<T>(
        Solution solution, AssemblyLoader assemblies, string path, Func<LoadedAssembly, IReadOnlyCollection<string>, List<T>> disassemble,
        bool optimize,
        CancellationToken cancellationToken)
    {
        var methods = new List<T>();
        var symbols = await SymbolTree.ResolveAsync(solution, path, cancellationToken);
        foreach (var group in symbols.GroupBy(symbol => symbol.ContainingAssembly, SymbolEqualityComparer.Default))
        {
            // Documentation IDs identify members the same way in Roslyn and Cecil.
            var ids = group.SelectMany(MembersWithIL).Select(member => member.GetDocumentationCommentId()).OfType<string>().ToList();
            if (ids.Count == 0 || group.Key is not IAssemblySymbol assembly || solution.GetProject(assembly, cancellationToken) is not { } project)
                continue;

            var compiled = await assemblies.CompileAsync((await project.GetCompilationAsync(cancellationToken))!, optimize);
            methods.AddRange(disassemble(compiled, ids));
        }

        return methods.Count > 0 ? methods : throw new McpException($"'{path}' has no IL: pick a type, method, property or event declared in the project.");
    }

    private static IEnumerable<ISymbol> MembersWithIL(ISymbol symbol)
    {
        ISymbol?[] members = symbol switch
        {
            INamedTypeSymbol or IMethodSymbol => [symbol],
            IPropertySymbol property => [property.GetMethod, property.SetMethod],
            IEventSymbol @event => [@event.AddMethod, @event.RemoveMethod],
            _ => [],
        };
        return members.OfType<ISymbol>();
    }
}
