using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using ModelContextProtocol;

namespace Roslynaut.Tools;

/// <summary>Structural conditions on symbols, for find_symbols. Every condition given must hold.</summary>
internal static class SymbolFilters
{
    public const string Modifiers = "static, abstract, virtual, override, sealed, extern, async, readonly, const, extension or generic";

    /// <param name="attribute">Regex for the name of an attribute on the symbol, with or without "Attribute".</param>
    /// <param name="baseType">Regex for a base type or interface of a type.</param>
    /// <param name="type">Regex for the type of a variable, field, property or event, or the return type of a method.</param>
    public static Func<ISymbol, bool> Parse(string? attribute, string? baseType, string? type, string? accessibility, IReadOnlyCollection<string>? modifiers)
    {
        var conditions = new List<Func<ISymbol, bool>>();
        if (attribute is not null)
        {
            var regex = ParseRegex(attribute, nameof(attribute));
            conditions.Add(symbol => symbol.GetAttributes().Any(applied => applied.AttributeClass is { Name: var name }
                && (regex.IsMatch(name) || regex.IsMatch(name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name))));
        }

        if (baseType is not null)
        {
            var regex = ParseRegex(baseType, nameof(baseType));
            conditions.Add(symbol => symbol is ITypeSymbol self && Supertypes(self).Any(super => Matches(regex, super)));
        }

        if (type is not null)
        {
            var regex = ParseRegex(type, nameof(type));
            conditions.Add(symbol => TypeOf(symbol) is { } symbolType && Matches(regex, symbolType));
        }

        if (accessibility is not null)
        {
            if (!Enum.TryParse<Accessibility>(accessibility, ignoreCase: true, out var required) || required == Accessibility.NotApplicable)
                throw new McpException($"Unknown accessibility '{accessibility}': use Public, Internal, Protected, Private, ProtectedOrInternal or ProtectedAndInternal.");
            conditions.Add(symbol => symbol.DeclaredAccessibility == required);
        }

        conditions.AddRange((modifiers ?? []).Select(Modifier));
        return symbol => conditions.All(condition => condition(symbol));
    }

    /// <summary>The type of a variable, field, property or event, or the return type of a method; null for others.</summary>
    public static ITypeSymbol? TypeOf(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.ReturnType,
        ITypeSymbol or INamespaceSymbol => null,
        _ => SymbolTree.TypeOrSelf(symbol) as ITypeSymbol,
    };

    private static Func<ISymbol, bool> Modifier(string modifier) => modifier.ToLowerInvariant() switch
    {
        "static" => symbol => symbol.IsStatic,
        "abstract" => symbol => symbol.IsAbstract,
        "virtual" => symbol => symbol.IsVirtual,
        "override" => symbol => symbol.IsOverride,
        "sealed" => symbol => symbol.IsSealed,
        "extern" => symbol => symbol.IsExtern,
        "async" => symbol => symbol is IMethodSymbol { IsAsync: true },
        "readonly" => symbol => symbol is IFieldSymbol { IsReadOnly: true } or IPropertySymbol { IsReadOnly: true } or ITypeSymbol { IsReadOnly: true } or IMethodSymbol { IsReadOnly: true },
        "const" => symbol => symbol is IFieldSymbol { IsConst: true },
        // A this-parameter method, or a member of an extension block.
        "extension" => symbol => symbol is IMethodSymbol { IsExtensionMethod: true } || symbol is IMethodSymbol or IPropertySymbol && symbol.ContainingSymbol is INamedTypeSymbol { IsExtension: true },
        "generic" => symbol => symbol is INamedTypeSymbol { IsGenericType: true } or IMethodSymbol { IsGenericMethod: true },
        _ => throw new McpException($"Unknown modifier '{modifier}': use {Modifiers}."),
    };

    private static IEnumerable<ITypeSymbol> Supertypes(ITypeSymbol type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
            yield return baseType;
        foreach (var implemented in type.AllInterfaces)
            yield return implemented;
    }

    /// <summary>Matches the short name, such as "List&lt;int&gt;", or the full one.</summary>
    private static bool Matches(Regex regex, ITypeSymbol type) =>
        regex.IsMatch(type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)) || regex.IsMatch(type.ToDisplayString());

    private static Regex ParseRegex(string pattern, string parameter)
    {
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException e)
        {
            throw new McpException($"Invalid {parameter}: {e.Message}");
        }
    }
}
