using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed partial class ResourcesTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => ResourcesAsync;

    [McpServerTool(Name = "resources", ReadOnly = true)]
    [Description("Checks the projects' .resx resource files against their translations, Strings.resx against Strings.de.resx and the like: "
        + "per culture the string resources it lacks, which fall back to the neutral text at run time, and those only it has, which nothing "
        + "reads. With unused, also the resources no C# code uses, through the generated class's property (Strings.Key) or by name in a string "
        + "literal, as localizer[\"Key\"] and GetString(\"Key\") do; Razor code counts through the Razor generator's output. Resources used only "
        + "from XAML or by names built at run time count as unused.")]
    private async Task<IReadOnlyList<ResourceSet>> ResourcesAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Optional .resx file, neutral or of a culture: checks only its set.")] string? filePath = null,
        [Description("Whether to look for the resources no C# code uses; reads all the code.")] bool unused = true,
        CancellationToken cancellationToken = default)
    {
        var solution = (await workspace.GetAsync(projectPath)).Solution;
        var directories = solution.Projects.Select(project => project.FilePath).OfType<string>().Select(Path.GetDirectoryName).OfType<string>()
            .Append(Path.GetDirectoryName(Path.GetFullPath(projectPath))!);
        return await CheckAsync(solution, directories, filePath, unused, cancellationToken);
    }

    /// <summary>The resource sets of the .resx files below the directories, with the gaps of their translations and, with unused, the resources no code uses.</summary>
    internal static async Task<IReadOnlyList<ResourceSet>> CheckAsync(
        Solution solution, IEnumerable<string> directories, string? filePath, bool unused, CancellationToken cancellationToken)
    {
        var files = directories.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists)
            .SelectMany(directory => VerifyPendingTool.FilesBelow(directory, "*.resx")).Distinct(StringComparer.OrdinalIgnoreCase);
        var sets = files.Select(file => (File: file, Name: NameOf(file)))
            .GroupBy(file => (Directory: Path.GetDirectoryName(file.File)!.ToLowerInvariant(), Base: file.Name.Base.ToLowerInvariant()))
            .Select(set => set.ToList())
            .Where(set => filePath is null || set.Any(file => string.Equals(file.File, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (filePath is not null && sets.Count == 0)
            throw new McpException($"{filePath} is not a .resx file below the projects' directories.");

        var read = sets.Select(set => set.Select(file => (file.File, file.Name.Culture, Keys: KeysOf(file.File))).ToList()).ToList();
        var names = read.SelectMany(set => set.SelectMany(file => file.Keys)).ToHashSet(StringComparer.Ordinal);
        (HashSet<string> Literals, HashSet<(string Type, string Property)> Properties)? used = unused
            ? await UsesAsync(solution, [.. sets.Select(set => set[0].Name.Base).Distinct()], names, cancellationToken)
            : null;

        return [.. read.Select((set, index) =>
        {
            var neutral = set.Find(file => file.Culture is null);
            var keys = neutral.File is not null ? neutral.Keys : [.. set.SelectMany(file => file.Keys).Distinct()];
            var type = sets[index][0].Name.Base;
            return new ResourceSet(
                neutral.File ?? set[0].File,
                keys.Count,
                [.. set.Select(file => file.Culture).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)],
                neutral.File is null ? [] : [.. set.Where(file => file.Culture is not null).OrderBy(file => file.Culture, StringComparer.OrdinalIgnoreCase)
                    .Select(file => new CultureGap(file.Culture!, file.File, [.. keys.Except(file.Keys)], [.. file.Keys.Except(keys)]))
                    .Where(gap => gap.Missing.Count + gap.Extra.Count > 0)],
                used is not { } uses ? null : [.. keys.Where(key => !uses.Literals.Contains(key) && !uses.Properties.Contains((type, PropertyOf(key))))]);
        }).OrderBy(set => set.File, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The names of the string resources of a .resx file, in order. Files, images and other serialized objects have a type or a MIME type.
    /// </summary>
    // ponytail: only string resources are checked; other resources are seldom translated, add them with their own list if they are.
    private static List<string> KeysOf(string file)
    {
        try
        {
            return [.. XDocument.Load(file).Root?.Elements("data")
                .Where(data => data.Attribute("type") is null && data.Attribute("mimetype") is null)
                .Select(data => data.Attribute("name")?.Value).OfType<string>().Distinct() ?? []];
        }
        catch (XmlException e)
        {
            throw new McpException($"{file} is not a valid .resx file: {e.Message}");
        }
    }

    /// <summary>A file's set and culture: Strings.de-DE.resx is of the set Strings, for de-DE; Strings.resx is the set's neutral file.</summary>
    internal static (string Base, string? Culture) NameOf(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var dot = name.LastIndexOf('.');
        return dot > 0 && IsCulture(name[(dot + 1)..]) ? (name[..dot], name[(dot + 1)..]) : (name, null);
    }

    private static bool IsCulture(string name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name, predefinedOnly: true).Name.Length > 0;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The property the generated class names a resource by: characters that cannot be in a name become underscores.</summary>
    private static string PropertyOf(string key)
    {
        var name = NotInName().Replace(key, "_");
        return name.Length > 0 && char.IsAsciiDigit(name[0]) ? $"_{name}" : name;
    }

    /// <summary>
    /// The resource names that C# code uses: as string literals, and as properties of the resource classes, by class name. The classes' own
    /// files, which name every resource, do not count.
    /// </summary>
    private static async Task<(HashSet<string> Literals, HashSet<(string Type, string Property)> Properties)> UsesAsync(
        Solution solution, IReadOnlyList<string> classes, HashSet<string> names, CancellationToken cancellationToken)
    {
        var (literals, properties) = (new HashSet<string>(StringComparer.Ordinal), new HashSet<(string, string)>());
        var identifiers = names.Select(PropertyOf).ToHashSet(StringComparer.Ordinal);
        foreach (var project in solution.Projects.Where(project => project.Language == LanguageNames.CSharp))
        {
            if (await project.GetCompilationAsync(cancellationToken) is not { } compilation)
                continue;

            var generated = classes.SelectMany(name => compilation.GetSymbolsWithName(name, SymbolFilter.Type, cancellationToken))
                .Where(IsResourceClass).SelectMany(type => type.DeclaringSyntaxReferences.Select(reference => reference.SyntaxTree)).ToHashSet();
            foreach (var tree in compilation.SyntaxTrees.Where(tree => !generated.Contains(tree)))
            {
                SemanticModel? model = null;
                foreach (var node in (await tree.GetRootAsync(cancellationToken)).DescendantNodes())
                {
                    if (node is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) && names.Contains(literal.Token.ValueText))
                    {
                        literals.Add(literal.Token.ValueText);
                    }
                    else if (node is IdentifierNameSyntax identifier && identifiers.Contains(identifier.Identifier.ValueText))
                    {
                        model ??= compilation.GetSemanticModel(tree);
                        if (model.GetSymbolInfo(identifier, cancellationToken).Symbol is IPropertySymbol { IsStatic: true } property && IsResourceClass(property.ContainingType))
                            properties.Add((property.ContainingType.Name, property.Name));
                    }
                }
            }
        }

        return (literals, properties);

        static bool IsResourceClass(ISymbol type) => type is INamedTypeSymbol named && named.GetMembers("ResourceManager").Any(member => member.IsStatic);
    }

    [GeneratedRegex(@"[^\p{L}\p{Nd}_]")]
    private static partial Regex NotInName();

    /// <param name="File">The set's neutral .resx file, of no culture; for a set without one, its first file.</param>
    /// <param name="Keys">How many string resources the neutral file has.</param>
    /// <param name="Cultures">The cultures it has translations for.</param>
    /// <param name="Gaps">Per culture, the resources its file lacks or only it has; cultures without either are left out.</param>
    /// <param name="Unused">With unused: the resources no C# code uses.</param>
    public sealed record ResourceSet(string File, int Keys, IReadOnlyList<string> Cultures, IReadOnlyList<CultureGap> Gaps, IReadOnlyList<string>? Unused);

    /// <param name="Missing">The resources the neutral file has and this one lacks: they show in the neutral language.</param>
    /// <param name="Extra">The resources only this file has, which nothing reads, as for a resource renamed or removed in the neutral file.</param>
    public sealed record CultureGap(string Culture, string File, IReadOnlyList<string> Missing, IReadOnlyList<string> Extra);
}
