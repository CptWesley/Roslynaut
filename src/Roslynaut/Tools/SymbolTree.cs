using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol;

namespace Roslynaut.Tools;

/// <summary>Navigates symbols by containment: namespace, type, member, then parameters and locals.</summary>
internal static class SymbolTree
{
    /// <summary>
    /// Resolves a dotted symbol path such as "Roslynaut.Tools", "WorkspaceService.GetAsync(string)" or "Program.Main.services".
    /// The first segment may name any source declaration; later segments are looked up with <see cref="GetChildrenAsync"/>.
    /// Segments match case-insensitively; a segment with a parameter or type argument list only matches that overload.
    /// </summary>
    public static async Task<IReadOnlyList<ISymbol>> ResolveAsync(Solution solution, string path, CancellationToken cancellationToken)
    {
        var segments = Split(path);
        var compilations = (await Task.WhenAll(solution.Projects.Select(project => project.GetCompilationAsync(cancellationToken)))).OfType<Compilation>().ToList();
        var own = compilations.Select(compilation => compilation.Assembly).ToHashSet<IAssemblySymbol>(ReferenceEqualityComparer.Instance);
        var name = NameOf(segments[0]);
        // The compilations' declarations, unlike SymbolFinder's index, include what source generators declare. The Program of top-level
        // statements has no declaration of its own to find.
        IEnumerable<ISymbol> symbols = compilations
            .SelectMany(compilation => compilation.GetSymbolsWithName(candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase), SymbolFilter.All, cancellationToken))
            .SelectMany(symbol => OwnParts(symbol, own))
            .Concat(own.SelectMany(assembly => assembly.GlobalNamespace.GetTypeMembers(WellKnownMemberNames.TopLevelStatementsEntryPointTypeName)))
            .Where(symbol => Matches(symbol, segments[0]))
            .Distinct(SymbolEqualityComparer.Default);
        foreach (var segment in segments.Skip(1))
        {
            var children = new List<ISymbol>();
            foreach (var symbol in symbols)
            {
                children.AddRange((await GetChildrenAsync(solution, symbol, cancellationToken)).Where(child => Matches(child, segment)));
                // What the initializer of a field, property or local declares, such as a lambda's parameters, is in it too.
                if (symbol is IFieldSymbol or IPropertySymbol { IsIndexer: false } or ILocalSymbol)
                    children.AddRange((await GetDeclaredChildrenAsync(solution, symbol, cancellationToken)).Where(child => Matches(child, segment)));
            }

            symbols = children;
        }

        var resolved = symbols.Distinct(SymbolEqualityComparer.Default).ToList();
        return resolved.Count > 0 ? resolved : throw new McpException($"No symbol found for '{path}'.");
    }

    /// <summary>
    /// For a namespace, the parts of it that the projects' own assemblies declare. A project's namespace also holds what its references declare
    /// there. For a referenced project whose references are not the project's own, such as one for another framework, those are copies of its
    /// symbols, which the path resolves to besides the originals; and copies of namespaces have whole files as locations.
    /// </summary>
    private static IEnumerable<ISymbol> OwnParts(ISymbol symbol, HashSet<IAssemblySymbol> own) =>
        symbol is INamespaceSymbol ns ? ns.ConstituentNamespaces.Where(part => own.Contains(part.ContainingAssembly)) : [symbol];

    /// <summary>The symbols declared in a symbol; for a variable, field, property or event the members of its type, but an indexer's parameters.</summary>
    public static Task<IEnumerable<ISymbol>> GetChildrenAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken) =>
        GetDeclaredChildrenAsync(solution, symbol is IPropertySymbol { IsIndexer: true } ? symbol : TypeOrSelf(symbol), cancellationToken);

    /// <summary>All symbols declared inside the roots, breadth first. Without locals, members are not looked into.</summary>
    public static async IAsyncEnumerable<ISymbol> GetDescendantsAsync(
        Solution solution, IEnumerable<ISymbol> roots, bool includeLocals, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var pending = new Queue<ISymbol>(roots);
        while (pending.TryDequeue(out var symbol))
        {
            if (!includeLocals && symbol is not INamespaceOrTypeSymbol)
                continue;

            foreach (var child in await GetDeclaredChildrenAsync(solution, symbol, cancellationToken))
            {
                if (!seen.Add(child))
                    continue;

                yield return child;
                pending.Enqueue(child);
            }
        }
    }

    /// <summary>The global namespace of every project's own assembly, to search the whole solution.</summary>
    public static async Task<IEnumerable<ISymbol>> GetRootNamespacesAsync(Solution solution, CancellationToken cancellationToken) =>
        (await Task.WhenAll(solution.Projects.Select(project => project.GetCompilationAsync(cancellationToken))))
            .Select(compilation => compilation!.Assembly.GlobalNamespace);

    /// <summary>The symbols declared inside syntax nodes: for a member its parameters and locals, for a file everything in it.</summary>
    public static async Task<IEnumerable<ISymbol>> GetDeclaredInAsync(Solution solution, IEnumerable<SyntaxNode> nodes, CancellationToken cancellationToken)
    {
        var symbols = new List<ISymbol>();
        foreach (var node in nodes)
        {
            if (solution.GetDocument(node.SyntaxTree) is not { } document)
                continue;

            var model = (await document.GetSemanticModelAsync(cancellationToken))!;
            symbols.AddRange(node.DescendantNodes().Select(child => model.GetDeclaredSymbol(child, cancellationToken)).OfType<ISymbol>().Where(IsDeclared));
        }

        return symbols;
    }

    /// <summary>
    /// A symbol, or both parts of a partial method, property or event: each part is a symbol of its own that declares only itself, and a path
    /// or a reference gives the definition part, without the code.
    /// </summary>
    public static IEnumerable<ISymbol> PartsOf(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { PartialDefinitionPart: { } definition } => [definition, symbol],
        IMethodSymbol { PartialImplementationPart: { } implementation } => [symbol, implementation],
        IPropertySymbol { PartialDefinitionPart: { } definition } => [definition, symbol],
        IPropertySymbol { PartialImplementationPart: { } implementation } => [symbol, implementation],
        IEventSymbol { PartialDefinitionPart: { } definition } => [definition, symbol],
        IEventSymbol { PartialImplementationPart: { } implementation } => [symbol, implementation],
        _ => [symbol],
    };

    /// <summary>The declarations of a symbol, with both parts of a partial member.</summary>
    public static IEnumerable<SyntaxReference> DeclarationsOf(ISymbol symbol) => PartsOf(symbol).SelectMany(part => part.DeclaringSyntaxReferences);

    public static ISymbol TypeOrSelf(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => local.Type,
        IParameterSymbol parameter => parameter.Type,
        IFieldSymbol field => field.Type,
        IPropertySymbol property => property.Type,
        IEventSymbol @event => @event.Type,
        _ => symbol,
    };

    private static async Task<IEnumerable<ISymbol>> GetDeclaredChildrenAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken) => symbol switch
    {
        // Namespaces also hold every referenced assembly's types; keep the ones from source.
        INamespaceSymbol ns => ns.GetMembers().Where(member => member.Locations.Any(location => location.IsInSource)),
        ITypeSymbol type => type.GetMembers().Where(IsDeclared),
        // The entry point of top-level statements is declared by their files, which can declare types too. Of a partial member, the
        // implementation, its last part, declares the locals; both parts declare the parameters.
        _ => await GetDeclaredInAsync(solution, (await Task.WhenAll(PartsOf(symbol).Last().DeclaringSyntaxReferences.Select(reference => reference.GetSyntaxAsync(cancellationToken))))
            .SelectMany(node => node is CompilationUnitSyntax file ? file.Members.OfType<GlobalStatementSyntax>() : (IEnumerable<SyntaxNode>)[node]), cancellationToken),
    };

    /// <summary>Excludes compiler-generated symbols and property/event accessors.</summary>
    private static bool IsDeclared(ISymbol symbol) => !symbol.IsImplicitlyDeclared && symbol is not IMethodSymbol { AssociatedSymbol: not null };

    /// <summary>Splits on dots outside parameter and type argument lists; a constructor's name keeps its dot, as in "Point..ctor(int)".</summary>
    private static List<string> Split(string path)
    {
        var segments = new List<string>();
        var (depth, start) = (0, 0);
        for (var i = 0; i < path.Length; i++)
        {
            depth += path[i] switch { '(' or '<' or '[' => 1, ')' or '>' or ']' => -1, _ => 0 };
            if (path[i] == '.' && depth == 0 && i > start)
            {
                segments.Add(path[start..i].Trim());
                start = i + 1;
            }
        }

        segments.Add(path[start..].Trim());
        return segments;
    }

    // A compiler's name can start with '<', as <Main>$ does.
    private static string NameOf(string segment) => segment.IndexOfAny(['(', '<'], Math.Min(1, segment.Length)) is var end and >= 0 ? segment[..end].Trim() : segment.Trim();

    // Types by their names only, as code usually writes them: Classify(IShape) for Classify(Shapes.IShape).
    private static readonly SymbolDisplayFormat ShortFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>
    /// Whether a symbol has a segment's name, and its parameter or type argument list if any, with types qualified fully or not at all.
    /// A constructor goes by its metadata name, as results give it (.ctor), or by its type's, as code writes it.
    /// </summary>
    private static bool Matches(ISymbol symbol, string segment)
    {
        // An indexer is this[] by name, or this[string] with its parameter types.
        if (symbol is IPropertySymbol { IsIndexer: true } && segment.StartsWith("this[", StringComparison.OrdinalIgnoreCase))
        {
            return Compact(segment).Equals("this[]", StringComparison.OrdinalIgnoreCase) || new[] { symbol.ToDisplayString(), symbol.ToDisplayString(ShortFormat) }
                .Any(display => Compact(display).EndsWith(Compact(segment), StringComparison.OrdinalIgnoreCase));
        }

        var name = NameOf(segment);
        var named = symbol.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } && symbol.ContainingType.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
        return named && (name.Length == segment.Length || new[] { symbol.ToDisplayString(), symbol.ToDisplayString(ShortFormat) }
            .Any(display => Compact(display).EndsWith(Compact(segment[name.Length..]), StringComparison.OrdinalIgnoreCase)));
    }

    private static string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
