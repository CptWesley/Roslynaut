using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Mono.Cecil;

namespace Roslynaut.Tools;

public sealed class SymbolSourceTool(WorkspaceService workspace, AssemblyLoader assemblies, PdbService pdbs) : IMcpTool
{
    public Delegate Handler => GetSourceAsync;

    [McpServerTool(Name = "symbol_source", ReadOnly = true, OpenWorld = true)]
    [Description("Returns the source code of a symbol's declaration, such as a whole method, property or type; one entry per declaration, "
        + "so a partial type has several. Each Source starts at the beginning of the line given by its Location. For a symbol from a referenced "
        + "assembly, the source comes from the files its PDB lists (see assembly_sources), and Document tells where from and whether it is "
        + "verified. That source lacks what the compiler synthesizes or IL weavers add, which decompile shows.")]
    private async Task<IEnumerable<SymbolSource>> GetSourceAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        var sources = await GetSourcesAsync(symbols, cancellationToken);
        if (sources.Count > 0)
            return sources;

        var problems = new List<string>();
        foreach (var target in symbols)
        {
            try
            {
                sources.AddRange(await GetPdbSourcesAsync(solution, assemblies, pdbs, target, cancellationToken));
            }
            catch (McpException e)
            {
                problems.Add(e.Message);
            }
        }

        return sources.Count > 0 ? sources : throw new McpException(string.Join(" ", problems.Distinct()));
    }

    internal static async Task<List<SymbolSource>> GetSourcesAsync(IEnumerable<ISymbol> symbols, CancellationToken cancellationToken)
    {
        var sources = new List<SymbolSource>();
        foreach (var symbol in symbols)
        {
            foreach (var reference in SymbolTree.DeclarationsOf(symbol))
            {
                var node = await reference.GetSyntaxAsync(cancellationToken);

                // A field or event declares its symbols in variable declarators; show the whole declaration.
                if (node is VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax declaration })
                    node = declaration;

                sources.Add(await SourceOfAsync(symbol, node, null, cancellationToken));
            }
        }

        return sources;
    }

    /// <summary>
    /// The declarations of a symbol from a referenced assembly, in the source files its PDB lists. The PDB places code, so the symbol's code
    /// picks the files, and among overloads the declarations; a symbol without code is looked for by name in the files of its type.
    /// Throws why there is none.
    /// </summary>
    internal static async Task<List<SymbolSource>> GetPdbSourcesAsync(
        Solution solution, AssemblyLoader assemblies, PdbService pdbs, ISymbol symbol, CancellationToken cancellationToken)
    {
        // Accessors are declared by their property or event, and extension methods called as instance methods as static ones.
        symbol = (symbol is IMethodSymbol method ? method.AssociatedSymbol ?? method.ReducedFrom ?? symbol : symbol).OriginalDefinition;
        if ((symbol as INamedTypeSymbol ?? symbol.ContainingType) is not { } type || symbol.GetDocumentationCommentId() is not { } id
            || symbol.ContainingAssembly is not { } assembly || await AssemblyLoader.FindFileAsync(solution, assembly, cancellationToken) is not { } path)
            throw new McpException($"{symbol.ToDisplayString()} is neither declared in source nor a type or member of a referenced assembly.");

        path = DefinerOf(assemblies, path, type);
        var module = assemblies.Read(path).Module;
        MemberTokens tokens;
        lock (module)
            tokens = IlDisassembler.TokensOf(IlDisassembler.FindMembers(module, [id])[0]);

        var pdb = await pdbs.FindPdbAsync(path, download: true, cancellationToken)
            ?? throw new McpException($"{Path.GetFileName(path)} has no PDB next to it, inside it or on the symbol servers, so no source: use decompile.");
        var lines = pdb.LinesOf(tokens.Methods);

        var sources = new List<SymbolSource>();
        var problems = new List<string>();
        foreach (var document in pdb.DocumentsOf(tokens))
        {
            if (!pdb.IsCSharp(document))
            {
                problems.Add($"{pdb.NameOf(document)} is not C#");
                continue;
            }

            var file = await pdbs.FetchAsync(pdb, document, cancellationToken);
            if (file.Source is null)
            {
                problems.Add($"{file.Name}: {file.Error}");
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(file.Source, path: file.FilePath ?? file.Name, cancellationToken: cancellationToken);
            foreach (var node in Declarations(await tree.GetRootAsync(cancellationToken), symbol, lines.GetValueOrDefault(document) ?? []))
                sources.Add(await SourceOfAsync(symbol, node, file with { Source = null }, cancellationToken));
        }

        return sources.Count > 0 ? sources : throw new McpException(problems.Count > 0
            ? $"No source of {symbol.ToDisplayString()}: {string.Join("; ", problems)}. Use decompile."
            : $"The PDB of {Path.GetFileName(path)} places no declaration of {symbol.ToDisplayString()}, as for code the compiler generates: use decompile.");
    }

    private static async Task<SymbolSource> SourceOfAsync(ISymbol symbol, SyntaxNode node, SourceDocument? document, CancellationToken cancellationToken)
    {
        var text = await node.SyntaxTree.GetTextAsync(cancellationToken);
        var start = text.Lines.GetLineFromPosition(node.SpanStart).Start;
        return new(symbol.ToDisplayString(), SourceLocation.From(node.GetLocation()), text.ToString(TextSpan.FromBounds(start, node.Span.End)), document);
    }

    /// <summary>The assembly that defines a type, past type forwarders, such as from System.Runtime.dll to System.Private.CoreLib.dll.</summary>
    private static string DefinerOf(AssemblyLoader assemblies, string path, INamedTypeSymbol type)
    {
        while (type.ContainingType is { } outer)
            type = outer;

        var name = type.ContainingNamespace.IsGlobalNamespace ? type.MetadataName : $"{type.ContainingNamespace.ToDisplayString()}.{type.MetadataName}";
        for (var hops = 0; hops < 8; hops++)
        {
            var module = assemblies.Read(path).Module;
            ExportedType? forwarder;
            lock (module)
                forwarder = module.ExportedTypes.FirstOrDefault(exported => exported.IsForwarder && exported.FullName == name);
            if (forwarder is null)
                return path;

            path = Path.Combine(Path.GetDirectoryName(path)!, $"{forwarder.Scope.Name}.dll");
        }

        return path;
    }

    /// <summary>
    /// The declarations of a type or member in a file. Overloads share a name, so where the PDB gives lines of the member's code, those pick them.
    /// </summary>
    private static List<MemberDeclarationSyntax> Declarations(SyntaxNode root, ISymbol symbol, HashSet<int> lines)
    {
        var declarations = root.DescendantNodes().OfType<MemberDeclarationSyntax>().ToList();
        if (symbol is INamedTypeSymbol type)
            return [.. declarations.Where(node => Declares(node, type))];

        var members = declarations.Where(node => node.Parent is MemberDeclarationSyntax parent && Declares(parent, symbol.ContainingType) && Declares(node, symbol)).ToList();
        if (lines.Count == 0)
            return members;

        var placed = members.Where(node => Spans(node, lines)).ToList();

        // Code without a declaration of its own, such as a property of a record's primary constructor, belongs to the innermost declaration around it.
        return placed.Count > 0 ? placed : [.. declarations.Where(node => Spans(node, lines)).TakeLast(1)];
    }

    private static bool Spans(SyntaxNode node, HashSet<int> lines)
    {
        var span = node.GetLocation().GetLineSpan();
        return lines.Any(line => line > span.StartLinePosition.Line && line <= span.EndLinePosition.Line + 1);
    }

    /// <summary>Whether a declaration declares the type or member, by its name, its type's names, arities and namespace.</summary>
    private static bool Declares(MemberDeclarationSyntax node, ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol type)
        {
            var (name, arity) = node switch
            {
                TypeDeclarationSyntax declaration => (declaration.Identifier.ValueText, declaration.Arity),
                EnumDeclarationSyntax declaration => (declaration.Identifier.ValueText, 0),
                DelegateDeclarationSyntax declaration => (declaration.Identifier.ValueText, declaration.Arity),
                _ => (null, 0),
            };
            if (name != type.Name || arity != type.Arity)
                return false;
            if (type.ContainingType is { } outer)
                return node.Parent is MemberDeclarationSyntax parent && Declares(parent, outer);

            var space = string.Join('.', node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(declaration => declaration.Name.ToString()));
            return space == (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString());
        }

        // Metadata names explicit implementations after their interface, as in System.IDisposable.Dispose.
        var member = symbol switch
        {
            IMethodSymbol { ExplicitInterfaceImplementations: [var implemented, ..] } => implemented.Name,
            IPropertySymbol { ExplicitInterfaceImplementations: [var implemented, ..] } => implemented.Name,
            IEventSymbol { ExplicitInterfaceImplementations: [var implemented, ..] } => implemented.Name,
            _ => symbol.Name,
        };
        return node switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText == member,
            ConstructorDeclarationSyntax constructor => member == (constructor.Modifiers.Any(SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor"),
            DestructorDeclarationSyntax => member == "Finalize",
            OperatorDeclarationSyntax @operator => SyntaxFacts.GetOperatorKind(member) == @operator.OperatorToken.Kind(),
            ConversionOperatorDeclarationSyntax conversion => member == (conversion.ImplicitOrExplicitKeyword.IsKind(SyntaxKind.ImplicitKeyword) ? "op_Implicit" : "op_Explicit"),
            PropertyDeclarationSyntax property => property.Identifier.ValueText == member,
            IndexerDeclarationSyntax => member == "this[]",
            EventDeclarationSyntax @event => @event.Identifier.ValueText == member,
            BaseFieldDeclarationSyntax field => field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == member),
            EnumMemberDeclarationSyntax enumMember => enumMember.Identifier.ValueText == member,
            _ => false,
        };
    }

    /// <param name="Document">For a symbol from a referenced assembly: the source file, from its PDB, that Location points into a local copy of.</param>
    public sealed record SymbolSource(string Signature, SourceLocation Location, string Source, SourceDocument? Document = null);
}
