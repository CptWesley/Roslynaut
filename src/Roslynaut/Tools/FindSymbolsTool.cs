using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

[JsonConverter(typeof(JsonStringEnumConverter<SymbolCategory>))]
public enum SymbolCategory
{
    Any,
    Namespace,
    Type,
    Method,
    Property,
    Field,

    /// <summary>Locals, parameters and query range variables.</summary>
    Variable,
}

public sealed class FindSymbolsTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => FindSymbolsAsync;

    [McpServerTool(Name = "find_symbols", ReadOnly = true)]
    [Description("Searches symbols (namespaces, types, members, parameters, locals) by name and structure (attribute, base type, type, "
        + "accessibility, modifiers) in the whole solution or within a scope. "
        + "Outer symbols come first. Paged.")]
    private async Task<PagedResult<SymbolEntry>> FindSymbolsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Case-insensitive .NET regular expression matched against symbol names, e.g. 'cache', '^Get.*Async$' or 'path|file'. "
            + "A query with a dot also matches full names that end in the symbol's name, e.g. 'Tools.ResultCache' or 'MyNamespace.Outer.Nested'. "
            + "Omit to return everything in scope.")] string? query = null,
        [Description("Optional path of a C# file, or a symbol path to search within. " + ToolDefaults.SymbolPath)] string? scope = null,
        [Description("Kind of symbols to return. Variable means locals and parameters.")] SymbolCategory kind = SymbolCategory.Any,
        [Description("Optional regex for the name of an attribute the symbol has, with or without 'Attribute', e.g. 'Obsolete' or '^(Test|Fact)$'.")]
        string? attribute = null,
        [Description("Optional regex for a base type or interface of the type, e.g. 'IDisposable' or 'ControllerBase'.")] string? baseType = null,
        [Description("Optional regex for the type of a variable, field, property or event, or the return type of a method, e.g. '^Task' or 'List<int>'.")]
        string? type = null,
        [Description("Optional accessibility: Public, Internal, Protected, Private, ProtectedOrInternal or ProtectedAndInternal.")] string? accessibility = null,
        [Description("Optional modifiers that must all apply: " + SymbolFilters.Modifiers + ".")] string[]? modifiers = null,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var (named, filtered) = (ParseQuery(query), SymbolFilters.Parse(attribute, baseType, type, accessibility, modifiers));
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        var key = $"find_symbols|{query}|{scope}|{kind}|{attribute}|{baseType}|{type}|{accessibility}|{string.Join(',', modifiers ?? [])}";
        return await cache.GetPageAsync(solution, key, page, pageSize,
            () => SearchAsync(solution, symbol => named(symbol) && filtered(symbol), scope, kind, cancellationToken));
    }

    internal static async Task<IEnumerable<SymbolEntry>> SearchAsync(
        Solution solution, Func<ISymbol, bool> matches, string? scope, SymbolCategory kind, CancellationToken cancellationToken)
    {
        var includeLocals = kind is SymbolCategory.Any or SymbolCategory.Method or SymbolCategory.Variable;

        IAsyncEnumerable<ISymbol> symbols;
        if (scope is null)
        {
            symbols = SymbolTree.GetDescendantsAsync(solution, await SymbolTree.GetRootNamespacesAsync(solution, cancellationToken), includeLocals, cancellationToken);
        }
        else if (scope.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var root = await WorkspaceService.GetDocument(solution, scope).GetSyntaxRootAsync(cancellationToken);
            symbols = (await SymbolTree.GetDeclaredInAsync(solution, [root!], cancellationToken)).ToAsyncEnumerable();
        }
        else
        {
            var roots = (await SymbolTree.ResolveAsync(solution, scope, cancellationToken)).Select(SymbolTree.TypeOrSelf);
            symbols = SymbolTree.GetDescendantsAsync(solution, roots, includeLocals, cancellationToken);
        }

        return await symbols
            .Where(symbol => IsKind(symbol, kind) && matches(symbol))
            .Select(symbol => SymbolEntry.From(symbol))
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    internal static Func<ISymbol, bool> ParseQuery(string? query)
    {
        if (query is null)
            return _ => true;

        try
        {
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            var name = new Regex(query, options);
            if (!query.Contains('.'))
                return symbol => name.IsMatch(symbol.Name);

            // The match must end in the symbol's own name, so 'Foo.Bar' finds Bar but not the members inside it.
            var fullName = new Regex($"(?:{query})(?=[^.]*$)", options);
            return symbol => name.IsMatch(symbol.Name) || fullName.IsMatch(FullName(symbol));
        }
        catch (ArgumentException e)
        {
            throw new McpException($"Invalid query: {e.Message}");
        }
    }

    /// <summary>The names of the symbol's namespaces, containing types and member, then its own, e.g. "Some.Package.Outer.Nested".</summary>
    private static string FullName(ISymbol symbol) =>
        symbol.ContainingSymbol is { } container and not INamespaceSymbol { IsGlobalNamespace: true }
            ? $"{FullName(container)}.{symbol.Name}"
            : symbol.Name;

    private static bool IsKind(ISymbol symbol, SymbolCategory kind) => kind switch
    {
        SymbolCategory.Namespace => symbol is INamespaceSymbol,
        SymbolCategory.Type => symbol is ITypeSymbol,
        SymbolCategory.Method => symbol is IMethodSymbol,
        SymbolCategory.Property => symbol is IPropertySymbol,
        SymbolCategory.Field => symbol is IFieldSymbol,
        SymbolCategory.Variable => symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol,
        _ => true,
    };
}
