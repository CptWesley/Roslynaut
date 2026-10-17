using Microsoft.CodeAnalysis;

namespace Roslynaut.Tools;

/// <summary>
/// An MCP tool discovered through DI. <see cref="Handler"/> is registered with <c>McpServerTool.Create</c>,
/// so the handler method's <c>[McpServerTool]</c> and <c>[Description]</c> attributes define the tool's name and schema.
/// </summary>
public interface IMcpTool
{
    Delegate Handler { get; }
}

internal static class ToolDefaults
{
    public const string ProjectPath = "Path to a .csproj, .sln or .slnx file.";
    public const string FilePath = "Path to a C# source file in the project.";
    public const string Line = "1-based line number.";
    public const string Column = "1-based column number of the symbol.";
    public const string SymbolPath = "Symbol path: a dotted name, optionally partially qualified, with a parameter list to pick an overload, "
        + "e.g. 'Roslynaut.Tools', 'WorkspaceService', 'WorkspaceService.GetAsync(string)' or 'WorkspaceService.GetAsync.path'; "
        + "a constructor is 'WorkspaceService..ctor' or 'WorkspaceService.WorkspaceService', an indexer 'Type.this[]' or 'Type.this[int]'. "
        + "A result's Container plus '.' plus its Name is a valid path.";
    public const string Target = "The symbol, by symbol path; or leave this out and point at it with filePath, line and column. " + SymbolPath;
    public const string TargetFilePath = "With line and column, instead of a symbol path: the C# file that contains the symbol.";
    public const string Page ="0-based page number; hasMore in the result tells whether a next page exists.";
    public const string PageSize = "Number of results per page.";
    public const int DefaultPageSize = 100;
}

public sealed record SourceLocation(string FilePath, int Line, int Column, string Text)
{
    public static SourceLocation From(Location location)
    {
        var start = location.GetLineSpan().StartLinePosition;
        var text = location.SourceTree!.GetText().Lines[start.Line].ToString().Trim();
        return new(location.SourceTree.FilePath, start.Line + 1, start.Character + 1, text);
    }
}

public sealed record SymbolEntry(string Name, string Kind, string Signature, string? Container, SourceLocation? Location)
{
    // The default format omits the type of locals.
    private static readonly SymbolDisplayFormat Format = SymbolDisplayFormat.CSharpErrorMessageFormat.AddLocalOptions(SymbolDisplayLocalOptions.IncludeType);

    public static SymbolEntry From(ISymbol symbol, Location? location = null) => new(
        symbol.Name,
        KindOf(symbol),
        symbol.ToDisplayString(Format),
        symbol.ContainingSymbol is { } container and not INamespaceSymbol { IsGlobalNamespace: true } ? PathOf(container) : null,
        (location ?? symbol.Locations.FirstOrDefault(l => l.IsInSource)) is { } source ? SourceLocation.From(source) : null);

    /// <summary>
    /// A container as a symbol path names it, so that the path plus a name is one: a lambda by the member it is in, a local function within
    /// its member, and top-level statements as Program.&lt;Main&gt;$.
    /// </summary>
    private static string PathOf(ISymbol container) => container switch
    {
        IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } lambda => PathOf(lambda.ContainingSymbol),
        IMethodSymbol { MethodKind: MethodKind.LocalFunction } function => $"{PathOf(function.ContainingSymbol)}.{function.ToDisplayString()}",
        IMethodSymbol { Name: WellKnownMemberNames.TopLevelStatementsEntryPointMethodName } main => $"{main.ContainingType.ToDisplayString()}.{main.Name}",
        _ => container.ToDisplayString(),
    };

    public static string KindOf(ISymbol symbol) => symbol is INamedTypeSymbol type ? type.TypeKind.ToString() : symbol.Kind.ToString();
}
