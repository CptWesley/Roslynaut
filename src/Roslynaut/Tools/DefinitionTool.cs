using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class DefinitionTool(WorkspaceService workspace, AssemblyLoader assemblies, PdbService pdbs) : IMcpTool
{
    public Delegate Handler => GetDefinitionsAsync;

    [McpServerTool(Name = "definition", ReadOnly = true, OpenWorld = true)]
    [Description("Resolves a symbol: its signature, documentation and declaration locations (go to definition + hover). "
        + "For a symbol from a referenced assembly, AssemblyPath is the file to pass to assembly_il, assembly_asm or decompile; "
        + "and if its PDB leads to source, Locations point into local copies of the files that Sources describe.")]
    private async Task<IEnumerable<Definition>> GetDefinitionsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        [Description("Resolve the symbol's type instead (go to type definition): the type of a variable, parameter, field, property or event, "
            + "or the return type of a method, or for a constructor its type; for an array its element type.")] bool type = false,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        if (type)
            symbols = [.. symbols.Select(TypeDefinitionOf).OfType<ISymbol>().Distinct(SymbolEqualityComparer.Default)];
        return await DefineAsync(solution, assemblies, pdbs, symbols, cancellationToken);
    }

    internal static async Task<List<Definition>> DefineAsync(
        Solution solution, AssemblyLoader assemblies, PdbService pdbs, IEnumerable<ISymbol> symbols, CancellationToken cancellationToken)
    {
        var definitions = new List<Definition>();
        foreach (var target in symbols)
        {
            var locations = SymbolTree.PartsOf(target).SelectMany(part => part.Locations).Where(location => location.IsInSource).Select(SourceLocation.From).ToList();
            var assemblyPath = locations.Count == 0 && target.ContainingAssembly is { } assembly ? await AssemblyLoader.FindFileAsync(solution, assembly, cancellationToken) : null;
            List<SymbolSourceTool.SymbolSource> sources = [];
            if (assemblyPath is not null)
            {
                try
                {
                    sources = await SymbolSourceTool.GetPdbSourcesAsync(solution, assemblies, pdbs, target, cancellationToken);
                }
                catch (McpException)
                {
                    // No source to go to; decompile still works.
                }
            }

            definitions.Add(new(
                SymbolEntry.KindOf(target),
                target.ToDisplayString(),
                target.ContainingAssembly?.Name,
                assemblyPath,
                target.GetDocumentationCommentXml(cancellationToken: cancellationToken),
                locations.Count > 0 ? locations : [.. sources.Select(source => source.Location)],
                [.. sources.Select(source => source.Document!).DistinctBy(document => document.Name)]));
        }

        return definitions;
    }

    /// <summary>The declared type behind a symbol: List&lt;T&gt; for a List&lt;int&gt;, Item for an Item[], Circle for new Circle(1).</summary>
    internal static ISymbol? TypeDefinitionOf(ISymbol symbol)
    {
        var type = symbol switch
        {
            ITypeSymbol self => self,
            IMethodSymbol { MethodKind: MethodKind.Constructor } constructor => constructor.ContainingType,
            _ => SymbolFilters.TypeOf(symbol),
        };
        while (type is IArrayTypeSymbol or IPointerTypeSymbol)
            type = type is IArrayTypeSymbol array ? array.ElementType : ((IPointerTypeSymbol)type).PointedAtType;
        return type?.OriginalDefinition;
    }

    /// <param name="Sources">For a symbol from a referenced assembly, the source files from its PDB that declare it: where they come from, and whether verified.</param>
    public sealed record Definition(
        string Kind, string Signature, string? Assembly, string? AssemblyPath, string? Documentation, IEnumerable<SourceLocation> Locations,
        IEnumerable<SourceDocument> Sources);
}
