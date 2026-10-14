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
    public const int MaxResults = 100;
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

public sealed record SymbolEntry(string Name, string Kind, string Signature, SourceLocation? Location)
{
    public static SymbolEntry From(ISymbol symbol, Location? location = null) => new(
        symbol.Name,
        KindOf(symbol),
        symbol.ToDisplayString(),
        (location ?? symbol.Locations.FirstOrDefault(l => l.IsInSource)) is { } source ? SourceLocation.From(source) : null);

    public static string KindOf(ISymbol symbol) => symbol is INamedTypeSymbol type ? type.TypeKind.ToString() : symbol.Kind.ToString();
}
