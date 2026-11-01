using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

/// <summary>Edits by symbol, as Serena's tools do: replace a declaration, insert before or after it, or delete it when unused.</summary>
internal static class SymbolEdits
{
    public enum Placement
    {
        Replace,
        Before,
        After,
    }

    /// <summary>
    /// Replaces the declaration (from the start of its first line, as symbol_source returns it), or inserts code on the lines before it
    /// (and before its documentation comment) or after it.
    /// </summary>
    public static async Task<Solution> EditAsync(
        Solution solution, IReadOnlyList<ISymbol> symbols, Placement placement, string code, string? filePath, int? line, CancellationToken cancellationToken)
    {
        var (document, node) = await DeclarationAsync(solution, symbols, filePath, line, cancellationToken);
        var text = await document.GetTextAsync(cancellationToken);
        var newLine = text.Lines.Count > 1 && text.ToString(TextSpan.FromBounds(text.Lines[0].End, text.Lines[0].EndIncludingLineBreak)) is { Length: > 0 } lineBreak ? lineBreak : "\n";
        var lines = code.EndsWith('\n') ? code : code + newLine;

        TextChange change = placement switch
        {
            Placement.Replace => new(TextSpan.FromBounds(text.Lines.GetLineFromPosition(node.SpanStart).Start, node.Span.End), code.TrimEnd('\r', '\n')),
            Placement.Before => new(new TextSpan(text.Lines.GetLineFromPosition(StartIncludingComments(node)).Start, 0), lines),
            _ => new(new TextSpan(text.Lines.GetLineFromPosition(node.Span.End).EndIncludingLineBreak, 0), lines),
        };

        // The last member of an enum may go without a comma, which a member inserted after it needs.
        if (placement == Placement.After && node is EnumMemberDeclarationSyntax { Parent: EnumDeclarationSyntax @enum } member
            && @enum.Members.IndexOf(member) >= @enum.Members.SeparatorCount)
            return solution.WithDocumentText(document.Id, text.WithChanges(new TextChange(new TextSpan(node.Span.End, 0), ","), change));
        return solution.WithDocumentText(document.Id, text.WithChanges(change));
    }

    /// <summary>
    /// Deletes the symbol's declarations if nothing outside them references it and the code compiles without them; otherwise returns those
    /// references, or where code would no longer compile.
    /// </summary>
    public static async Task<(Solution? Deleted, IReadOnlyList<SourceLocation> References)> SafeDeleteAsync(
        Solution solution, IReadOnlyList<ISymbol> symbols, CancellationToken cancellationToken)
    {
        if (symbols is not [var symbol])
            throw new McpException($"The symbol path matches {symbols.Count} symbols; pick one by adding a parameter list, or by position.");

        // Calls pass an argument for a parameter and uses name a type argument for a type parameter, which are no references to them.
        if (symbol is IParameterSymbol)
            throw new McpException($"{symbol.Name} is a parameter, which the calls of {symbol.ContainingSymbol.ToDisplayString()} pass: remove it with change_signature, which updates them.");
        if (symbol is ITypeParameterSymbol)
            throw new McpException($"{symbol.Name} is a type parameter, which the uses of {symbol.ContainingSymbol.ToDisplayString()} name: edit its declaration and uses instead.");

        var declarations = await Task.WhenAll(SymbolTree.DeclarationsOf(symbol).Select(reference => reference.GetSyntaxAsync(cancellationToken)));
        if (declarations.Length == 0)
            throw new McpException($"{symbol.ToDisplayString()} is not declared in source.");

        // Only the parts people wrote are deleted: a generator's part of a partial declaration goes once the generator no longer sees them.
        var editable = await Task.WhenAll(EditableDeclarations(solution, symbol).Select(reference => reference.GetSyntaxAsync(cancellationToken)));

        // A primary constructor is declared by its type, which deleting its declaration would delete.
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } && declarations.Any(declaration => declaration is TypeDeclarationSyntax))
            throw new McpException($"{symbol.ToDisplayString()} is the primary constructor of {symbol.ContainingType.ToDisplayString()}: edit the type's parameter list instead.");
        // So is a record's positional property, by a parameter that calls pass too.
        if (declarations.Any(declaration => declaration is ParameterSyntax))
            throw new McpException($"{symbol.ToDisplayString()} is declared by a parameter of the primary constructor of {symbol.ContainingType.ToDisplayString()}, which calls pass: remove it with change_signature, which updates them.");

        // Overrides and explicit implementations of it, and the interface or abstract members it implements, need it as much as references do:
        // without it, they do not compile.
        var overrides = await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cancellationToken);
        var explicitImplementations = (await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken))
            .Where(implementation => ExplicitlyImplemented(implementation).Contains(symbol, SymbolEqualityComparer.Default));
        var dependents = overrides.Concat(explicitImplementations).Concat(Implemented(symbol))
            .Select(member => member.Locations.FirstOrDefault(location => location.IsInSource) ?? symbol.ContainingType.Locations.First(location => location.IsInSource));
        var references = (await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
            .SelectMany(reference => reference.Locations)
            .Select(location => location.Location)
            .Where(location => !declarations.Any(declaration => declaration.SyntaxTree == location.SourceTree && declaration.FullSpan.Contains(location.SourceSpan)))
            .Concat(dependents)
            .Select(SourceLocation.From)
            .Distinct()
            .ToList();

        // So may code that #if leaves out of this load, such as another target framework's.
        references.AddRange((await RenameSymbolTool.InactiveMentionsAsync(solution, symbol, cancellationToken)).Where(mention => !declarations.Any(declaration =>
            declaration.SyntaxTree.FilePath == mention.FilePath && declaration.SyntaxTree.GetLineSpan(declaration.Span) is var span
            && span.StartLinePosition.Line < mention.Line && mention.Line <= span.EndLinePosition.Line + 1)));
        if (references.Count > 0)
            return (null, references);

        var editor = new SolutionEditor(solution);
        foreach (var declaration in editable)
        {
            var document = await editor.GetDocumentEditorAsync(solution.GetDocumentId(declaration.SyntaxTree)!, cancellationToken);
            if (ForeignTrivia(declaration) is 0)
            {
                document.RemoveNode(declaration);
                continue;
            }

            // Removing a node keeps only the directives of its trivia, which would delete the disabled code between them.
            document.ReplaceNode(declaration, (current, _) => current.WithLeadingTrivia(current.GetLeadingTrivia().Take(ForeignTrivia(current))));
            document.RemoveNode(declaration, SyntaxRemoveOptions.KeepLeadingTrivia | SyntaxRemoveOptions.KeepUnbalancedDirectives | SyntaxRemoveOptions.AddElasticMarker);
        }

        // A file that declared only a deleted type goes with it, unless it still says something, such as a global using.
        var deleted = editor.GetChangedSolution();
        foreach (var id in editable.Select(declaration => solution.GetDocumentId(declaration.SyntaxTree)!).Distinct())
        {
            if (await deleted.GetDocument(id)!.GetSyntaxRootAsync(cancellationToken) is CompilationUnitSyntax { AttributeLists.Count: 0 } root
                && root.Usings.All(directive => directive.GlobalKeyword.IsKind(SyntaxKind.None))
                && root.DescendantNodes().OfType<MemberDeclarationSyntax>().All(member => member is BaseNamespaceDeclarationSyntax))
                deleted = deleted.RemoveDocument(id);
        }

        // Code can use a member without naming it, as with an implicit conversion, which finding references misses: what would no longer compile,
        // here or in the projects that depend on these, needs it too.
        var changed = deleted.GetChanges(solution).GetProjectChanges().Select(change => change.ProjectId).ToList();
        var users = changed.SelectMany(solution.GetProjectDependencyGraph().GetProjectsThatTransitivelyDependOnThisProject);
        var introduced = await DiagnosticsTool.IntroducedErrorsAsync(solution, deleted, changed.Concat(users).Distinct(), cancellationToken);
        if (introduced.Any(error => !error.Location.IsInSource))
            throw new McpException($"Deleting {symbol.ToDisplayString()} would leave code that does not compile, so nothing changed: {string.Join(" ", introduced.Take(5).Select(error => $"{error.Id} {error.GetMessage()}"))}");
        return introduced.Count > 0 ? (null, [.. introduced.Select(error => SourceLocation.From(error.Location)).Distinct()]) : (deleted, []);
    }

    /// <summary>The abstract member a member overrides, and the interface members it implements.</summary>
    private static IEnumerable<ISymbol> Implemented(ISymbol symbol)
    {
        if (symbol.ContainingType is not { } type || symbol is ITypeSymbol)
            return [];

        var overridden = symbol switch
        {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            _ => (ISymbol?)null,
        };
        return type.AllInterfaces.SelectMany(face => face.GetMembers())
            .Where(member => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), symbol))
            .Concat(overridden is { IsAbstract: true } ? [overridden] : []);
    }

    private static IEnumerable<ISymbol> ExplicitlyImplemented(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.ExplicitInterfaceImplementations,
        IPropertySymbol property => property.ExplicitInterfaceImplementations,
        IEventSymbol @event => @event.ExplicitInterfaceImplementations,
        _ => [],
    };

    /// <summary>The declaration to edit; of a symbol with several, such as a partial type, the one at the 1-based line of a file.</summary>
    private static async Task<(Document Document, SyntaxNode Node)> DeclarationAsync(
        Solution solution, IReadOnlyList<ISymbol> symbols, string? filePath, int? line, CancellationToken cancellationToken)
    {
        if (symbols is not [var symbol])
            throw new McpException($"The symbol path matches {symbols.Count} symbols; pick one by adding a parameter list, or by position.");

        // A source generator's part of a partial declaration cannot be edited, so it needs no position to tell the parts apart.
        var references = EditableDeclarations(solution, symbol);
        var declared = references.Count;
        if (declared > 1 && filePath is not null && line is { } at)
        {
            references = [.. references.Where(reference => reference.SyntaxTree.FilePath.Equals(Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase)
                && reference.SyntaxTree.GetLineSpan(reference.Span) is var span && span.StartLinePosition.Line < at && at <= span.EndLinePosition.Line + 1)];
        }

        if (references is not [var reference])
            throw new McpException($"{symbol.ToDisplayString()} has {declared} declarations; edit one by a position in it.");

        var node = await reference.GetSyntaxAsync(cancellationToken);

        // A field, event or local declares its symbols in variable declarators; edit the whole declaration.
        if (node is VariableDeclaratorSyntax { Parent.Parent: (BaseFieldDeclarationSyntax or LocalDeclarationStatementSyntax) and { } declaration })
            node = declaration;
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } && node is TypeDeclarationSyntax)
            throw new McpException($"{symbol.ToDisplayString()} is the primary constructor of {symbol.ContainingType.ToDisplayString()}: edit the type's parameter list instead.");

        // The edits go by whole lines, which would cut through code beside the declaration on its lines.
        var text = await node.SyntaxTree.GetTextAsync(cancellationToken);
        var (first, last) = (text.Lines.GetLineFromPosition(node.SpanStart), text.Lines.GetLineFromPosition(node.Span.End));
        var after = text.ToString(TextSpan.FromBounds(node.Span.End, last.End)).TrimStart(',', ';').Trim();
        if (!string.IsNullOrWhiteSpace(text.ToString(TextSpan.FromBounds(first.Start, node.SpanStart))) || after.Length > 0 && !after.StartsWith("//", StringComparison.Ordinal))
        {
            throw new McpException(node is ParameterSyntax
                ? $"{symbol.ToDisplayString()} is declared in a parameter list: change it with change_signature."
                : $"{symbol.ToDisplayString()} shares its lines with other code, which edits by line would cut through: edit the declaration around it instead.");
        }

        return (solution.GetDocument(node.SyntaxTree)!, node);
    }

    /// <summary>The declarations of a symbol outside the output of source generators, which cannot be edited.</summary>
    private static List<SyntaxReference> EditableDeclarations(Solution solution, ISymbol symbol)
    {
        var declarations = SymbolTree.DeclarationsOf(symbol).Where(reference => solution.GetDocument(reference.SyntaxTree) is { } document and not SourceGeneratedDocument).ToList();
        return declarations.Count > 0 || !SymbolTree.DeclarationsOf(symbol).Any() ? declarations
            : throw new McpException($"{symbol.ToDisplayString()} is declared only in the output of a source generator, which cannot be edited.");
    }

    /// <summary>Where a declaration starts with its documentation and other comments.</summary>
    private static int StartIncludingComments(SyntaxNode node) =>
        node.GetLeadingTrivia().Skip(ForeignTrivia(node)).FirstOrDefault(trivia => !trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia)) is { RawKind: not 0 } first
            ? first.SpanStart
            : node.SpanStart;

    /// <summary>
    /// How many of a declaration's leading trivia go with the code before it: those up to its last directive or disabled code, such as the
    /// #else branch of an #if around the member before it, or the #endregion of a region before it.
    /// </summary>
    private static int ForeignTrivia(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia();
        for (var index = trivia.Count - 1; index >= 0; index--)
        {
            if (trivia[index].IsDirective || trivia[index].IsKind(SyntaxKind.DisabledTextTrivia))
                return index + 1;
        }

        return 0;
    }
}

public sealed class ReplaceSymbolBodyTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => ReplaceAsync;

    [McpServerTool(Name = "replace_symbol_body", Destructive = true)]
    [Description("Replaces a symbol's whole declaration, such as a method with its signature, with new code; symbol_source returns the code "
        + "this replaces, from the start of its first line. Writes the file to disk.")]
    private async Task<EditResult> ReplaceAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The new declaration, indented as it should be.")] string code,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        var edited = await SymbolEdits.EditAsync(solution, symbols, SymbolEdits.Placement.Replace, code, filePath, line, cancellationToken);
        return new(await WorkspaceService.WriteChangesAsync(solution, edited, cancellationToken));
    }
}

public sealed class InsertBeforeSymbolTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => InsertAsync;

    [McpServerTool(Name = "insert_before_symbol", Destructive = true)]
    [Description("Inserts code on new lines before a symbol's declaration and its documentation comment, such as a new member or a using. "
        + "Writes the file to disk.")]
    private async Task<EditResult> InsertAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The code to insert, indented as it should be.")] string code,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        var edited = await SymbolEdits.EditAsync(solution, symbols, SymbolEdits.Placement.Before, code, filePath, line, cancellationToken);
        return new(await WorkspaceService.WriteChangesAsync(solution, edited, cancellationToken));
    }
}

public sealed class InsertAfterSymbolTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => InsertAsync;

    [McpServerTool(Name = "insert_after_symbol", Destructive = true)]
    [Description("Inserts code on new lines after a symbol's declaration, such as a new member after an existing one. Writes the file to disk.")]
    private async Task<EditResult> InsertAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The code to insert, indented as it should be.")] string code,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        var edited = await SymbolEdits.EditAsync(solution, symbols, SymbolEdits.Placement.After, code, filePath, line, cancellationToken);
        return new(await WorkspaceService.WriteChangesAsync(solution, edited, cancellationToken));
    }
}

public sealed class SafeDeleteSymbolTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => DeleteAsync;

    [McpServerTool(Name = "safe_delete_symbol", Destructive = true)]
    [Description("Deletes a symbol's declaration, but only if nothing else references it; otherwise deletes nothing and returns the references, "
        + "or the code that would no longer compile without it, such as the uses of an implicit conversion, or the lines under #if that the load "
        + "leaves out and that have its name. "
        + "Writes the changed files to disk, and deletes a file that declared nothing but a deleted type.")]
    private async Task<SafeDeleteResult> DeleteAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        var (deleted, references) = await SymbolEdits.SafeDeleteAsync(solution, symbols, cancellationToken);
        return deleted is null ? new(false, [], references) : new(true, await WorkspaceService.WriteChangesAsync(solution, deleted, cancellationToken), []);
    }

    /// <param name="References">Why it was not deleted.</param>
    public sealed record SafeDeleteResult(bool Deleted, IReadOnlyList<string> ChangedFiles, IReadOnlyList<SourceLocation> References);
}
