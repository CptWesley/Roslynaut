using System.Collections;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

/// <summary>A parameter of the new signature: an existing one by its name, or a new one with its type and what call sites pass.</summary>
public sealed record ParameterSpec(
    [property: Description("An existing parameter's name, or a new one's.")] string Name,
    [property: Description("For a new parameter: its type, such as 'int' or 'List<string>'.")] string? Type = null,
    [property: Description("For a new parameter: the expression call sites pass, such as '0' or 'null'.")] string? Value = null,
    [property: Description("For a new parameter: makes it optional with this default; call sites then pass nothing unless Value is given.")] string? DefaultValue = null);

/// <summary>
/// Roslyn's refactorings that an IDE runs with options from a dialog, run with options built here instead and passed to
/// <see cref="CodeActionWithOptions.GetOperationsAsync(object?, CancellationToken)"/>. The options types are internal to Roslyn, so they are
/// built by reflection; the tests of these tools catch a Roslyn update that changes them.
/// </summary>
internal static class DialogRefactorings
{
    private static readonly Assembly Features = Assembly.Load("Microsoft.CodeAnalysis.Features");
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static async Task<Solution> ChangeSignatureAsync(Solution solution, ISymbol symbol, IReadOnlyList<ParameterSpec> parameters, CancellationToken cancellationToken)
    {
        var (document, action) = await ActionAsync(solution, symbol, "ChangeSignature.ChangeSignatureCodeRefactoringProvider", "ChangeSignatureCodeAction", cancellationToken);
        var context = Field(action, "_context");
        var original = Field(context, "ParameterConfiguration");
        var target = (ISymbol)Field(context, "Symbol");
        var position = (int)Field(context, "PositionForTypeBinding");
        var model = (await document.GetSemanticModelAsync(cancellationToken))!;

        var existing = ((IEnumerable)original.GetType().GetMethod("ToListOfParameters", Any)!.Invoke(original, null)!).Cast<object>()
            .ToDictionary(parameter => (ISymbol)Property(parameter, "Symbol"), parameter => parameter, SymbolEqualityComparer.Default!);
        var byName = existing.Keys.Cast<IParameterSymbol>().ToDictionary(parameter => parameter.Name);
        if (parameters.GroupBy(parameter => parameter.Name).FirstOrDefault(group => group.Count() > 1) is { } duplicate)
            throw new McpException($"{duplicate.Key} is in the list twice.");

        var list = new List<object>();
        foreach (var (spec, index) in parameters.Select((spec, index) => (spec, index)))
        {
            if (byName.TryGetValue(spec.Name, out var kept))
            {
                if (spec.Type is not null || spec.Value is not null || spec.DefaultValue is not null)
                    throw new McpException($"{spec.Name} is an existing parameter, so give only its name.");
                if (kept.IsParams && index != parameters.Count - 1)
                    throw new McpException($"The params parameter {kept.Name} has to stay last.");
                if (kept.IsThis && index != 0)
                    throw new McpException($"The this parameter {kept.Name} of an extension method has to stay first.");
                list.Add(existing[kept]);
                continue;
            }

            if (spec.Type is null)
                throw new McpException($"{spec.Name} is not a parameter of {target.ToDisplayString()}; give it a Type to add it as a new one.");
            if (spec.Value is null && spec.DefaultValue is null)
                throw new McpException($"Give the new parameter {spec.Name} a Value for the call sites to pass, or a DefaultValue.");
            if (!SyntaxFacts.IsValidIdentifier(spec.Name))
                throw new McpException($"'{spec.Name}' is not a valid C# identifier.");

            var type = model.GetSpeculativeTypeInfo(position, SyntaxFactory.ParseTypeName(spec.Type), SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
            if (type is null or IErrorTypeSymbol)
                throw new McpException($"The type {spec.Type} is not known where {target.Name} is declared; qualify it with its namespace.");

            list.Add(Activator.CreateInstance(TypeOf("ChangeSignature.AddedParameter"), Any, null,
                [type, spec.Type, spec.Name, Enum.Parse(TypeOf("ChangeSignature.CallSiteKind"), spec.Value is null ? "Omitted" : "Value"), spec.Value ?? "",
                 spec.DefaultValue is null, spec.DefaultValue ?? "", true], null)!);
        }

        var parameterType = TypeOf("ChangeSignature.Parameter");
        var array = Array.CreateInstance(parameterType, list.Count);
        for (var i = 0; i < list.Count; i++)
            array.SetValue(list[i], i);
        var immutable = typeof(ImmutableArray).GetMethods()
            .Single(method => method is { Name: nameof(ImmutableArray.Create), IsGenericMethodDefinition: true } && method.GetParameters() is [{ ParameterType.IsArray: true }])
            .MakeGenericMethod(parameterType).Invoke(null, [array]);

        var updated = TypeOf("ChangeSignature.ParameterConfiguration").GetMethod("Create", Any)!.Invoke(null, [immutable, target is IMethodSymbol { IsExtensionMethod: true }, 0]);
        var change = Activator.CreateInstance(TypeOf("ChangeSignature.SignatureChange"), Any, null, [original, updated], null);
        return await ApplyAsync(action, Activator.CreateInstance(TypeOf("ChangeSignature.ChangeSignatureOptionsResult"), Any, null, [change, false], null)!, cancellationToken);
    }

    public static async Task<Solution> ExtractInterfaceAsync(
        Solution solution, INamedTypeSymbol type, string? name, IReadOnlyList<string>? members, bool sameFile, CancellationToken cancellationToken)
    {
        var (_, action) = await ActionAsync(solution, type, "ExtractInterface.ExtractInterfaceCodeRefactoringProvider", "ExtractInterfaceCodeAction", cancellationToken);
        var extractable = (ImmutableArray<ISymbol>)Field(Field(action, "_typeAnalysisResult"), "ExtractableMembers");
        if (members?.FirstOrDefault(member => !extractable.Any(symbol => symbol.Name == member)) is { } unknown)
            throw new McpException($"{type.Name} has no public member {unknown} to extract; it has {string.Join(", ", extractable.Select(symbol => symbol.Name).Distinct())}.");

        // Static members would become static abstract ones, which only generic code constrained to the interface can call.
        var included = members is null ? [.. extractable.Where(symbol => !symbol.IsStatic)] : extractable.Where(symbol => members.Contains(symbol.Name)).ToImmutableArray();
        var interfaceName = name ?? $"I{type.Name}";
        if (!SyntaxFacts.IsValidIdentifier(interfaceName))
            throw new McpException($"'{interfaceName}' is not a valid C# identifier.");

        var location = Enum.Parse(TypeOf("ExtractInterface.ExtractInterfaceOptionsResult+ExtractLocation"), sameFile ? "SameFile" : "NewFile");
        var options = Activator.CreateInstance(TypeOf("ExtractInterface.ExtractInterfaceOptionsResult"), Any, null, [false, included, interfaceName, $"{interfaceName}.cs", location], null)!;
        return await ApplyAsync(action, options, cancellationToken);
    }

    /// <summary>
    /// Whether this class can give an action with options what its dialog would ask for: the members to generate from or override, as for
    /// "Generate overrides...".
    /// </summary>
    public static bool CanBuildOptions(CodeActionWithOptions action) => ValueOf<ImmutableArray<ISymbol>>(action) is not null;

    /// <param name="members">The members to pick, by name; all that the action offers by default.</param>
    public static async Task<Solution> ApplyWithOptionsAsync(CodeActionWithOptions action, IReadOnlyList<string>? members, CancellationToken cancellationToken)
    {
        var offered = ValueOf<ImmutableArray<ISymbol>>(action) ?? throw new McpException($"'{action.Title}' asks for options Roslynaut cannot give.");
        if (members?.FirstOrDefault(member => !offered.Any(symbol => symbol.Name == member)) is { } unknown)
            throw new McpException($"'{action.Title}' offers no member {unknown}; it offers {string.Join(", ", offered.Select(symbol => symbol.Name).Distinct())}.");

        // Its other choices, such as generating operators along with Equals, keep their defaults.
        var optionType = TypeOf("PickMembers.PickMembersOption");
        var options = action.GetType().GetProperties(Any).Select(property => property.GetValue(action))
            .Concat(FieldsOf(action.GetType()).Select(field => field.GetValue(action)))
            .FirstOrDefault(value => value?.GetType() == typeof(ImmutableArray<>).MakeGenericType(optionType))
            ?? typeof(ImmutableArray<>).MakeGenericType(optionType).GetField(nameof(ImmutableArray<int>.Empty))!.GetValue(null);
        var picked = members is null ? offered : offered.Where(symbol => members.Contains(symbol.Name)).ToImmutableArray();
        return await ApplyAsync(action, Activator.CreateInstance(TypeOf("PickMembers.PickMembersResult"), Any, null, [picked, options, members is null], null)!, cancellationToken);
    }

    /// <summary>
    /// Moves a type, or the types of a namespace declaration, to another namespace, and updates the using directives that reach them. Its code
    /// action is not offered here, since its provider takes Roslyn's service from MEF; the service does the work directly.
    /// </summary>
    public static async Task<Solution> MoveToNamespaceAsync(Solution solution, ISymbol symbol, string targetNamespace, CancellationToken cancellationToken)
    {
        var location = symbol.Locations.FirstOrDefault(location => location.IsInSource)
            ?? throw new McpException($"{symbol.ToDisplayString()} is not declared in source.");
        var document = solution.GetDocument(location.SourceTree) ?? throw new McpException($"{symbol.ToDisplayString()} is not in the loaded solution.");

        var serviceType = TypeOf("MoveToNamespace.IMoveToNamespaceService");
        var service = typeof(Microsoft.CodeAnalysis.Host.LanguageServices).GetMethod(nameof(Microsoft.CodeAnalysis.Host.LanguageServices.GetService))!
            .MakeGenericMethod(serviceType).Invoke(document.Project.Services, null) ?? throw new McpException("Roslyn has no Move to namespace for this language.");
        var analysis = await ResultOfAsync(serviceType.GetMethod("AnalyzeTypeAtPositionAsync")!.Invoke(service, [document, location.SourceSpan.Start, cancellationToken])!);
        if (!(bool)Property(analysis, "CanPerform"))
            throw new McpException($"Roslyn cannot move {symbol.ToDisplayString()}: point at a type, or a namespace declaration, in a namespace.");

        var result = await ResultOfAsync(serviceType.GetMethod("MoveToNamespaceAsync")!.Invoke(service, [analysis, targetNamespace, cancellationToken])!);
        return (bool)Property(result, "Succeeded") ? (Solution)Property(result, "UpdatedSolution") : throw new McpException($"Roslyn could not move it to {targetNamespace}.");

        static async Task<object> ResultOfAsync(object task)
        {
            await (Task)task;
            return task.GetType().GetProperty(nameof(Task<int>.Result))!.GetValue(task)!;
        }
    }

    /// <summary>The first field or property of a type, such as the members an action offers, which it may keep in a field its primary constructor made.</summary>
    private static T? ValueOf<T>(object target) where T : struct =>
        FieldsOf(target.GetType()).Where(field => field.FieldType == typeof(T)).Select(field => (T?)field.GetValue(target))
            .Concat(target.GetType().GetProperties(Any).Where(property => property.PropertyType == typeof(T) && property.GetIndexParameters().Length == 0).Select(property => (T?)property.GetValue(target)))
            .FirstOrDefault();

    private static IEnumerable<FieldInfo> FieldsOf(Type? type)
    {
        for (; type is not null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return field;
        }
    }

    /// <summary>The action of a refactoring at a symbol's declaration, with the document it is in.</summary>
    private static async Task<(Document Document, CodeActionWithOptions Action)> ActionAsync(
        Solution solution, ISymbol symbol, string provider, string action, CancellationToken cancellationToken)
    {
        var location = symbol.Locations.FirstOrDefault(location => location.IsInSource)
            ?? throw new McpException($"{symbol.ToDisplayString()} is not declared in source.");
        var document = solution.GetDocument(location.SourceTree) ?? throw new McpException($"{symbol.ToDisplayString()} is not in the loaded solution.");

        var refactoring = (CodeRefactoringProvider)Activator.CreateInstance(TypeOf(provider), nonPublic: true)!;
        var actions = new List<CodeAction>();
        await refactoring.ComputeRefactoringsAsync(new CodeRefactoringContext(document, new TextSpan(location.SourceSpan.Start, 0), actions.Add, cancellationToken));
        return (document, actions.OfType<CodeActionWithOptions>().FirstOrDefault(found => found.GetType().Name == action)
            ?? throw new McpException($"Roslyn offers no {action[..^"CodeAction".Length]} for {symbol.ToDisplayString()}."));
    }

    private static async Task<Solution> ApplyAsync(CodeActionWithOptions action, object options, CancellationToken cancellationToken) =>
        (await action.GetOperationsAsync(options, cancellationToken))?.Select(ChangedSolutionOf).LastOrDefault(changed => changed is not null)
            ?? throw new McpException($"'{action.Title}' changed nothing.");

    /// <summary>The solution an operation applies; Change signature has an operation of its own for it, which an IDE confirms first.</summary>
    private static Solution? ChangedSolutionOf(CodeActionOperation operation) => operation switch
    {
        ApplyChangesOperation apply => apply.ChangedSolution,
        _ => operation.GetType().GetProperty("ChangedSolution", Any)?.GetValue(operation) as Solution,
    };

    private static Type TypeOf(string name) => Features.GetType($"Microsoft.CodeAnalysis.{name}", throwOnError: true)!;

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, Any)?.GetValue(target) ?? throw new McpException($"Roslyn changed: {target.GetType().Name} has no {name}.");

    private static object Property(object target, string name) =>
        target.GetType().GetProperty(name, Any)?.GetValue(target) ?? throw new McpException($"Roslyn changed: {target.GetType().Name} has no {name}.");
}

public sealed class ChangeSignatureTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => ChangeSignatureAsync;

    [McpServerTool(Name = "change_signature", Destructive = true)]
    [Description("Changes the parameters of a method, constructor, indexer or delegate and updates every call site, override, implementation and "
        + "documentation comment across the solution, as an IDE's Change signature does: reorders, removes and adds parameters. Writes the changed "
        + "files to disk and returns their paths.")]
    private async Task<ChangedFiles> ChangeSignatureAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The new parameter list in order: existing parameters by Name alone, so leaving one out removes it; new ones with Name, Type "
            + "and a Value for the call sites to pass, or a DefaultValue to make them optional.")] ParameterSpec[] parameters,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        if (symbols is not [var target])
            throw new McpException($"The symbol path matches {symbols.Count} symbols; pick one by adding a parameter list, or by position.");

        return new(await WorkspaceService.WriteChangesAsync(solution, await DialogRefactorings.ChangeSignatureAsync(solution, target, parameters, cancellationToken), cancellationToken));
    }
}

public sealed class ExtractInterfaceTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => ExtractInterfaceAsync;

    [McpServerTool(Name = "extract_interface", Destructive = true)]
    [Description("Extracts an interface from a class, struct or interface: declares it with the chosen public instance members, in a new file by "
        + "default, and makes the type implement it. Writes the changed files to disk and returns their paths.")]
    private async Task<ChangedFiles> ExtractInterfaceAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        [Description("The interface's name; I plus the type's name by default.")] string? name = null,
        [Description("The members to include, by name; all public instance members by default. A static one becomes static abstract.")] string[]? members = null,
        [Description("Declare the interface in the type's file rather than in a new one.")] bool sameFile = false,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        if (symbols is not [INamedTypeSymbol type])
            throw new McpException("Point at one class, struct or interface.");

        return new(await WorkspaceService.WriteChangesAsync(solution, await DialogRefactorings.ExtractInterfaceAsync(solution, type, name, members, sameFile, cancellationToken), cancellationToken));
    }
}

public sealed class MoveToNamespaceTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => MoveAsync;

    [McpServerTool(Name = "move_to_namespace", Destructive = true)]
    [Description("Moves a type, or all the types of a namespace declaration, to another namespace, as an IDE's Move to namespace does: changes the "
        + "declaration and updates the using directives of the code that uses them. Writes the changed files to disk and returns their paths.")]
    private async Task<ChangedFiles> MoveAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The namespace to move to, such as 'MyApp.Orders'.")] string targetNamespace,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        if (symbols is not [var target and (INamedTypeSymbol or INamespaceSymbol)])
            throw new McpException("Point at one type or namespace.");

        return new(await WorkspaceService.WriteChangesAsync(solution, await DialogRefactorings.MoveToNamespaceAsync(solution, target, targetNamespace, cancellationToken), cancellationToken));
    }
}

public sealed record ChangedFiles(IReadOnlyList<string> Files);
