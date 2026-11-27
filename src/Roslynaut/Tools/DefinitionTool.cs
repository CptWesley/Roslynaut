using System.ComponentModel;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class DefinitionTool(WorkspaceService workspace, AssemblyLoader assemblies, PdbService pdbs) : IMcpTool
{
    public Delegate Handler => GetDefinitionsAsync;

    [McpServerTool(Name = "definition", ReadOnly = true, OpenWorld = true)]
    [Description("Resolves a symbol: its signature, documentation (with <inheritdoc/> expanded) and declaration locations (go to definition + hover). "
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
                target.ToDisplayString(SymbolEntry.Format),
                target.ContainingAssembly?.Name,
                assemblyPath,
                await DocumentationOfAsync(solution, target, cancellationToken),
                locations.Count > 0 ? locations : [.. sources.Select(source => source.Location)],
                [.. sources.Select(source => source.Document!).DistinctBy(document => document.Name)]));
        }

        return definitions;
    }

    /// <summary>
    /// The symbol's documentation XML, with each &lt;inheritdoc/&gt; replaced by the documentation it inherits: from its cref, else from the
    /// member it overrides or implements, the base type's constructor with the same parameters, or a type's base types and interfaces.
    /// At the top level it inherits the sections the symbol does not write itself, within a section that section; path picks by XPath.
    /// An &lt;inheritdoc/&gt; that leads nowhere stays as it is.
    /// </summary>
    internal static async Task<string?> DocumentationOfAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken)
    {
        var xml = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken);
        if (xml is null || !xml.Contains("<inheritdoc", StringComparison.Ordinal))
            return xml;
        return Expand(symbol, await CompilationOfAsync(solution, symbol.ContainingAssembly, cancellationToken), [], cancellationToken)?.ToString() ?? xml;
    }

    /// <summary>A compilation that sees the assembly, to resolve its crefs: its own project's, else the first that references it.</summary>
    private static async Task<Compilation?> CompilationOfAsync(Solution solution, IAssemblySymbol? assembly, CancellationToken cancellationToken)
    {
        if (assembly is null)
            return null;
        if (solution.GetProject(assembly, cancellationToken) is { } declaring)
            return await declaring.GetCompilationAsync(cancellationToken);
        // ponytail: compiles projects in order until one references the assembly; fine as only documentation with an inheritdoc gets here.
        foreach (var project in solution.Projects)
        {
            if (await project.GetCompilationAsync(cancellationToken) is { } compilation
                && compilation.References.Any(reference => SymbolEqualityComparer.Default.Equals(compilation.GetAssemblyOrModuleSymbol(reference), assembly)))
                return compilation;
        }

        return null;
    }

    private static XElement? Expand(ISymbol symbol, Compilation? compilation, HashSet<ISymbol> expanding, CancellationToken cancellationToken)
    {
        XElement root;
        try
        {
            root = XElement.Parse(symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken) ?? "");
        }
        catch (XmlException)
        {
            return null;
        }

        // A cycle of inheritdocs leads nowhere.
        if (!expanding.Add(symbol))
            return null;
        foreach (var inheritdoc in root.Descendants("inheritdoc").ToList())
        {
            var cref = (string?)inheritdoc.Attribute("cref");
            var sources = cref is null ? InheritedFrom(symbol)
                : compilation is null ? []
                : [DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, compilation)];
            if (sources.OfType<ISymbol>().Select(source => Expand(source, compilation, expanding, cancellationToken)).FirstOrDefault(found => found is not null) is not { } inherited)
                continue;
            try
            {
                inheritdoc.ReplaceWith(Inherited(root, inheritdoc, inherited).ToList());
            }
            catch (XPathException)
            {
                // A path that is not XPath: keep the inheritdoc.
            }
        }

        expanding.Remove(symbol);
        return root;
    }

    private static IEnumerable<XNode> Inherited(XElement root, XElement inheritdoc, XElement inherited)
    {
        if ((string?)inheritdoc.Attribute("path") is { } path)
        {
            // An absolute path starts at the documentation's own element, <member>.
            var document = new XDocument(inherited);
            return ((System.Collections.IEnumerable)document.Root!.XPathEvaluate(path.StartsWith('/') ? "/*" + path : path)).OfType<XNode>();
        }

        var section = inheritdoc.AncestorsAndSelf().First(element => element.Parent == root);
        if (section == inheritdoc)
            return inherited.Nodes().Where(node => node is not XElement element
                || element.Name != "overloads" && !root.Elements(element.Name).Any(own => SameSection(own, element)));
        return inherited.Elements(section.Name).Where(element => SameSection(element, section)).SelectMany(element => element.Nodes());
    }

    private static bool SameSection(XElement one, XElement other) =>
        one.Name == other.Name && (string?)one.Attribute("name") == (string?)other.Attribute("name") && (string?)one.Attribute("cref") == (string?)other.Attribute("cref");

    /// <summary>Where an &lt;inheritdoc/&gt; without cref looks, nearest first: the first of these with documentation is the one it inherits.</summary>
    private static IEnumerable<ISymbol?> InheritedFrom(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => [.. BaseTypes(type), .. type.AllInterfaces],
        IMethodSymbol { MethodKind: MethodKind.Constructor } constructor => [constructor.ContainingType.BaseType?.InstanceConstructors.FirstOrDefault(other =>
            other.Parameters.Select(parameter => parameter.Type).SequenceEqual(constructor.Parameters.Select(parameter => parameter.Type), SymbolEqualityComparer.Default))],
        IMethodSymbol method => [method.OverriddenMethod, .. method.ExplicitInterfaceImplementations, .. Implemented(method)],
        IPropertySymbol property => [property.OverriddenProperty, .. property.ExplicitInterfaceImplementations, .. Implemented(property)],
        IEventSymbol @event => [@event.OverriddenEvent, .. @event.ExplicitInterfaceImplementations, .. Implemented(@event)],
        _ => [],
    };

    private static IEnumerable<INamedTypeSymbol> BaseTypes(INamedTypeSymbol type)
    {
        for (var baseType = type.BaseType; baseType is { SpecialType: not (SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum or SpecialType.System_MulticastDelegate) }; baseType = baseType.BaseType)
            yield return baseType;
    }

    private static IEnumerable<ISymbol> Implemented(ISymbol member) =>
        member.ContainingType.AllInterfaces.SelectMany(@interface => @interface.GetMembers(member.Name))
            .Where(candidate => SymbolEqualityComparer.Default.Equals(member.ContainingType.FindImplementationForInterfaceMember(candidate), member));

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
