using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

/// <summary>
/// The API an assembly offers to others: its public and protected types and members, one line each, the same for source and for metadata,
/// so two versions compare line by line.
/// </summary>
internal static class PublicApi
{
    private static readonly SymbolDisplayFormat Format = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeConstantValue
            | SymbolDisplayMemberOptions.IncludeRef,
        delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
        extensionMethodStyle: SymbolDisplayExtensionMethodStyle.StaticMethod,
        parameterOptions: SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeExtensionThis,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword | SymbolDisplayKindOptions.IncludeMemberKeyword,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    // A type's constraints go after its supertypes, as code declares them; a supertype's are the declaring type's business.
    private static readonly SymbolDisplayFormat HeaderFormat = Format.RemoveGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeConstraints);

    private static readonly SymbolDisplayFormat SupertypeFormat = HeaderFormat.RemoveKindOptions(SymbolDisplayKindOptions.IncludeTypeKeyword);

    public static IEnumerable<string> Of(IAssemblySymbol assembly) => Of(assembly.GlobalNamespace).Order(StringComparer.Ordinal);

    private static IEnumerable<string> Of(INamespaceOrTypeSymbol container)
    {
        foreach (var member in container.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol ns:
                    foreach (var line in Of(ns))
                        yield return line;
                    break;
                case INamedTypeSymbol type when IsVisible(type):
                    yield return Describe(type);

                    // A delegate's line has its signature; its members are the runtime's.
                    if (type.TypeKind == TypeKind.Delegate)
                        break;
                    foreach (var line in Of(type))
                        yield return line;
                    break;
                case INamedTypeSymbol:
                    break;

                // Accessors show in their property's or event's line; names with < are the compiler's own, such as a record's <Clone>$.
                case IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise }:
                    break;
                case { Name: var name } when name.Contains('<'):
                    break;

                // Every struct and enum has one, declared or not.
                case IMethodSymbol { MethodKind: MethodKind.Constructor, Parameters.IsEmpty: true, IsImplicitlyDeclared: true, ContainingType.IsValueType: true }:
                    break;
                case var visible when IsVisible(visible):
                    yield return visible.ToDisplayString(Format);
                    break;
            }
        }
    }

    /// <summary>Reachable from another assembly: public or protected, in types that are too.</summary>
    private static bool IsVisible(ISymbol symbol)
    {
        for (; symbol is not null and not INamespaceSymbol; symbol = symbol.ContainingSymbol)
        {
            if (symbol.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
                return false;
        }

        return true;
    }

    /// <summary>A type with its modifiers, base type and interfaces, which others depend on as much as on its members.</summary>
    private static string Describe(INamedTypeSymbol type)
    {
        var modifiers = type.TypeKind switch
        {
            TypeKind.Class when type.IsStatic => "static ",
            TypeKind.Class when type.IsAbstract => "abstract ",
            TypeKind.Class when type.IsSealed => "sealed ",
            // The type keyword brings a struct's readonly and ref.
            _ => "",
        };
        IEnumerable<INamedTypeSymbol> bases = type.TypeKind is TypeKind.Class && type.BaseType is { SpecialType: not SpecialType.System_Object } baseType ? [baseType] : [];
        // Metadata lists the interfaces that declared ones inherit too, so all of them, in one order, read the same either way; less those
        // the base type brings, such as an enum's IComparable.
        var interfaces = type.AllInterfaces.Except(type.BaseType?.AllInterfaces ?? [], SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>();
        var supertypes = bases.Select(supertype => supertype.ToDisplayString(SupertypeFormat))
            .Concat(interfaces.Select(supertype => supertype.ToDisplayString(SupertypeFormat)).Order(StringComparer.Ordinal))
            .ToList();
        var header = type.ToDisplayString(HeaderFormat);
        var constraints = type.ToDisplayString(Format)[header.Length..];
        // Metadata does not tell a record struct from a struct, as it does a record class by its <Clone>$.
        if (type is { IsRecord: true, TypeKind: TypeKind.Struct })
            header = header.Replace("record struct ", "struct ");
        return $"{SyntaxFacts.GetText(type.DeclaredAccessibility)} {modifiers}{header}" + (supertypes.Count == 0 ? "" : " : " + string.Join(", ", supertypes)) + constraints;
    }

    /// <summary>The project a tool works on: by name in a solution, or the one at the project path.</summary>
    public static Project ProjectOf(Solution solution, string projectPath, string? name)
    {
        var path = Path.GetFullPath(projectPath);
        return name is not null
            ? solution.Projects.FirstOrDefault(project => string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new McpException($"No project named {name}; there are {string.Join(", ", solution.Projects.Select(project => project.Name))}.")
            : solution.Projects.FirstOrDefault(project => string.Equals(project.FilePath, path, StringComparison.OrdinalIgnoreCase))
                ?? throw new McpException($"Pass the project's name: one of {string.Join(", ", solution.Projects.Select(project => project.Name))}.");
    }

    public static async Task<IAssemblySymbol> AssemblyOfAsync(Project project, CancellationToken cancellationToken) =>
        (await project.GetCompilationAsync(cancellationToken))?.Assembly ?? throw new McpException($"{project.Name} does not compile to an assembly.");

    /// <summary>
    /// An assembly read from a file, with the project's references to resolve the types it uses; in a compilation of its own, so it does not
    /// clash with the project's assembly when it is another version of it.
    /// </summary>
    public static async Task<IAssemblySymbol> AssemblyOfAsync(Project project, string assemblyPath, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(assemblyPath);
        if (!File.Exists(path))
            throw new McpException($"Assembly not found: {path}");

        var references = (await project.GetCompilationAsync(cancellationToken))?.References ?? [];
        var baseline = WorkspaceService.ReferenceTo(path);
        var compilation = CSharpCompilation.Create("Roslynaut.Baseline", references: [.. references, baseline]);
        return compilation.GetAssemblyOrModuleSymbol(baseline) as IAssemblySymbol ?? throw new McpException($"{path} is not a .NET assembly.");
    }
}

public sealed class PublicApiTool(WorkspaceService workspace, ResultCache cache) : IMcpTool
{
    public Delegate Handler => GetPublicApiAsync;

    [McpServerTool(Name = "public_api", ReadOnly = true)]
    [Description("Lists the public API of a project, as other assemblies see it: its public and protected types, with their base types and "
        + "interfaces, and members, with their full signatures, one line each and sorted. To compare it with an earlier version, use api_diff. Paged.")]
    private async Task<PagedResult<string>> GetPublicApiAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ApiDefaults.Project)] string? project = null,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        var target = PublicApi.ProjectOf(solution, projectPath, project);
        return await cache.GetPageAsync(solution, $"public_api|{target.Id.Id}", page, pageSize,
            async () => PublicApi.Of(await PublicApi.AssemblyOfAsync(target, cancellationToken)));
    }
}

public sealed class ApiDiffTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => DiffAsync;

    [McpServerTool(Name = "api_diff", ReadOnly = true)]
    [Description("Compares the public API of a project's current source with an earlier build of it, such as the last released version from "
        + "package_assembly or a build output: the lines of public_api that were removed or changed, which can break callers, and those that were "
        + "added. A changed signature shows as one removed and one added line.")]
    private async Task<ApiDiff> DiffAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The path of the earlier assembly (.dll) to compare with.")] string baseline,
        [Description(ApiDefaults.Project)] string? project = null,
        CancellationToken cancellationToken = default)
    {
        var target = PublicApi.ProjectOf((await workspace.GetAsync(projectPath)).Solution, projectPath, project);
        return Diff(
            PublicApi.Of(await PublicApi.AssemblyOfAsync(target, baseline, cancellationToken)),
            PublicApi.Of(await PublicApi.AssemblyOfAsync(target, cancellationToken)));
    }

    internal static ApiDiff Diff(IEnumerable<string> before, IEnumerable<string> after)
    {
        var (old, current) = (before.ToHashSet(StringComparer.Ordinal), after.ToHashSet(StringComparer.Ordinal));
        return new([.. old.Except(current).Order(StringComparer.Ordinal)], [.. current.Except(old).Order(StringComparer.Ordinal)]);
    }

    /// <param name="Removed">Lines of the earlier API that the current one lacks: removed or changed types and members, which can break callers.</param>
    /// <param name="Added">Lines of the current API that the earlier one lacks.</param>
    public sealed record ApiDiff(IReadOnlyList<string> Removed, IReadOnlyList<string> Added);
}

internal static class ApiDefaults
{
    public const string Project = "For a solution, the name of the project; a .csproj path picks itself.";
}
