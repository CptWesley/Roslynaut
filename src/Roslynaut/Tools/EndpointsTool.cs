using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class EndpointsTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => ListAsync;

    [McpServerTool(Name = "aspnet_endpoints", ReadOnly = true)]
    [Description("Lists the HTTP endpoints of ASP.NET Core projects from their source: controller actions, with their [Route] and [Http*] templates "
        + "combined with the controller's, and minimal API Map* calls, with the prefixes of the MapGroup calls they hang off; each with its "
        + "HTTP method, route, handler and the attributes or calls that add to it, such as [Authorize] or RequireAuthorization. Maps a URL from "
        + "a log or a bug report to the code that handles it. Routes built at run time, not from constants, show as '?'.")]
    private async Task<IReadOnlyList<Endpoint>> ListAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Optional regex for the routes to list, e.g. '^/api/orders'.")] string? route = null,
        CancellationToken cancellationToken = default)
    {
        var pattern = ProcessDefaults.Pattern(route, "route");
        return [.. (await FindAsync((await workspace.GetAsync(projectPath)).Solution, cancellationToken)).Where(endpoint => pattern is null || pattern.IsMatch(endpoint.Route))];
    }

    private static readonly Dictionary<string, string> Verbs = new()
    {
        ["HttpGetAttribute"] = "GET", ["HttpPostAttribute"] = "POST", ["HttpPutAttribute"] = "PUT", ["HttpDeleteAttribute"] = "DELETE",
        ["HttpPatchAttribute"] = "PATCH", ["HttpHeadAttribute"] = "HEAD", ["HttpOptionsAttribute"] = "OPTIONS",
    };

    private static readonly Dictionary<string, string> MapVerbs = new()
    {
        ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT", ["MapDelete"] = "DELETE", ["MapPatch"] = "PATCH", ["Map"] = "ANY", ["MapMethods"] = "?",
    };

    internal static async Task<IReadOnlyList<Endpoint>> FindAsync(Solution solution, CancellationToken cancellationToken)
    {
        var endpoints = new List<Endpoint>();
        foreach (var project in solution.Projects.Where(project => project.Language == LanguageNames.CSharp))
        {
            var compilation = (await project.GetCompilationAsync(cancellationToken))!;
            foreach (var type in AllTypes(compilation.Assembly.GlobalNamespace))
            {
                if (IsController(type))
                    endpoints.AddRange(ActionsOf(type));
            }

            foreach (var document in project.Documents)
            {
                var model = (await document.GetSemanticModelAsync(cancellationToken))!;
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                foreach (var call in root!.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetOperation(call, cancellationToken) is IInvocationOperation invocation && MapOf(invocation, model, cancellationToken) is { } endpoint)
                        endpoints.Add(endpoint);
                }
            }
        }

        return [.. endpoints.OrderBy(endpoint => endpoint.Route, StringComparer.OrdinalIgnoreCase).ThenBy(endpoint => endpoint.Method)];
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol space) =>
        space.GetTypeMembers().SelectMany(Nested).Concat(space.GetNamespaceMembers().SelectMany(AllTypes));

    private static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type) => type.GetTypeMembers().SelectMany(Nested).Prepend(type);

    /// <summary>A controller as MVC finds one: a public class, not abstract, that derives from ControllerBase or says [ApiController] or [Controller].</summary>
    private static bool IsController(INamedTypeSymbol type)
    {
        if (type is not { TypeKind: TypeKind.Class, IsAbstract: false, DeclaredAccessibility: Accessibility.Public } || !type.Locations.Any(location => location.IsInSource))
            return false;
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.Name is "ControllerBase" or "Controller" && current.ContainingNamespace.ToDisplayString() == "Microsoft.AspNetCore.Mvc")
                return true;
            if (current.GetAttributes().Any(attribute => attribute.AttributeClass?.Name is "ApiControllerAttribute" or "ControllerAttribute"))
                return true;
        }

        return false;
    }

    private static IEnumerable<Endpoint> ActionsOf(INamedTypeSymbol controller)
    {
        var name = controller.Name.EndsWith("Controller", StringComparison.Ordinal) ? controller.Name[..^"Controller".Length] : controller.Name;
        var prefixes = RoutesOf(controller.GetAttributes()).DefaultIfEmpty("").ToList();
        var classMetadata = MetadataOf(controller.GetAttributes()).ToList();
        foreach (var action in controller.GetMembers().OfType<IMethodSymbol>().Where(method => method is
            { MethodKind: MethodKind.Ordinary, DeclaredAccessibility: Accessibility.Public, IsStatic: false }
            && !method.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == "NonActionAttribute")))
        {
            var attributes = action.GetAttributes();
            var verbs = attributes.Where(attribute => attribute.AttributeClass?.Name is { } kind && Verbs.ContainsKey(kind))
                .Select(attribute => (Verb: Verbs[attribute.AttributeClass!.Name], Template: TemplateOf(attribute))).ToList();
            var routes = RoutesOf(attributes).ToList();
            var templates = verbs.Count > 0
                ? verbs.SelectMany(verb => verb.Template is null && routes.Count > 0 ? routes.Select(route => (verb.Verb, (string?)route)) : [(verb.Verb, verb.Template)])
                : routes.Count > 0 ? routes.Select(route => ("ANY", (string?)route)) : [("ANY", (string?)null)];
            foreach (var (verb, template) in templates)
            {
                foreach (var prefix in prefixes)
                {
                    var route = Combine(prefix, template).Replace("[controller]", name, StringComparison.OrdinalIgnoreCase).Replace("[action]", action.Name, StringComparison.OrdinalIgnoreCase);
                    yield return new(verb, route, $"{SymbolEntry.PathOf(controller)}.{action.Name}",
                        action.Locations.FirstOrDefault(location => location.IsInSource) is { } location ? SourceLocation.From(location) : null,
                        [.. classMetadata, .. MetadataOf(attributes)]);
                }
            }
        }
    }

    private static IEnumerable<string> RoutesOf(IEnumerable<AttributeData> attributes) =>
        attributes.Where(attribute => attribute.AttributeClass?.Name == "RouteAttribute").Select(attribute => TemplateOf(attribute) ?? "");

    private static string? TemplateOf(AttributeData attribute) => attribute.ConstructorArguments.FirstOrDefault().Value as string;

    /// <summary>The attributes that say more about an endpoint than its route: authorization, filters, produced types; not the routing ones.</summary>
    private static IEnumerable<string> MetadataOf(IEnumerable<AttributeData> attributes) => attributes
        .Select(attribute => attribute.AttributeClass?.Name)
        .OfType<string>()
        .Where(name => !Verbs.ContainsKey(name) && name is not ("RouteAttribute" or "ApiControllerAttribute" or "ControllerAttribute" or "NonActionAttribute"))
        .Select(name => name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name);

    /// <summary>A template within another, as routing combines them: one that starts with / or ~/ stands alone.</summary>
    private static string Combine(string prefix, string? template)
    {
        if (template is ['/', ..] or ['~', '/', ..])
            return "/" + template.TrimStart('~', '/');
        var parts = new[] { prefix.Trim('/'), template?.Trim('/') ?? "" }.Where(part => part.Length > 0);
        return "/" + string.Join('/', parts);
    }

    /// <summary>A minimal API endpoint: a Map* call on a route builder, with the prefixes of the groups it was made on and the calls chained on it.</summary>
    private static Endpoint? MapOf(IInvocationOperation invocation, SemanticModel model, CancellationToken cancellationToken)
    {
        if (!MapVerbs.TryGetValue(invocation.TargetMethod.Name, out var verb) || invocation.TargetMethod.ContainingType.Name != "EndpointRouteBuilderExtensions"
            || invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == "pattern") is not { } pattern)
            return null;

        if (invocation.TargetMethod.Name == "MapMethods" && invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == "httpMethods")?.Value is { } methods)
            verb = string.Join(",", methods.Descendants().OfType<ILiteralOperation>().Select(literal => literal.ConstantValue.Value as string).OfType<string>());

        var receiver = invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == 0)?.Value;
        var route = Combine(GroupPrefix(receiver, model, cancellationToken, 0), pattern.Value.ConstantValue.Value as string ?? "?");
        var handler = invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == "handler")?.Value;
        var handlerName = Unwrap(handler) switch
        {
            IMethodReferenceOperation method => $"{SymbolEntry.PathOf(method.Method.ContainingSymbol)}.{method.Method.Name}",
            IAnonymousFunctionOperation lambda => $"lambda in {(ReferencesTool.EnclosingMemberOf(model, lambda.Syntax.SpanStart, cancellationToken) is { } member ? SymbolEntry.PathOf(member) : "?")}",
            _ => "?",
        };

        // Calls chained on the endpoint, such as .RequireAuthorization() or .WithName("x").
        var chained = new List<string>();
        for (var node = invocation.Syntax.Parent; node is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax next } access; node = next.Parent)
            chained.Add(access.Name.Identifier.ValueText);

        return new(verb, route, handlerName, SourceLocation.From(invocation.Syntax.GetLocation()), chained);
    }

    private static IOperation? Unwrap(IOperation? operation) => operation switch
    {
        IConversionOperation conversion => Unwrap(conversion.Operand),
        IDelegateCreationOperation creation => Unwrap(creation.Target),
        _ => operation,
    };

    /// <summary>
    /// The route prefix of the builder a Map* call is made on: a MapGroup call's, through the local it was stored in, nested groups included.
    /// ponytail: follows locals and direct calls only; a group passed through a parameter or field adds nothing.
    /// </summary>
    private static string GroupPrefix(IOperation? builder, SemanticModel model, CancellationToken cancellationToken, int depth)
    {
        builder = Unwrap(builder);
        if (depth > 8)
            return "";
        if (builder is ILocalReferenceOperation local && local.Local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is VariableDeclaratorSyntax { Initializer.Value: { } value }
            && model.SyntaxTree == value.SyntaxTree)
            builder = Unwrap(model.GetOperation(value, cancellationToken));
        if (builder is IInvocationOperation { TargetMethod.Name: "MapGroup" } group
            && group.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == "prefix")?.Value.ConstantValue.Value is string prefix)
            return Combine(GroupPrefix(group.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == 0)?.Value, model, cancellationToken, depth + 1), prefix);
        if (builder is IInvocationOperation chained && chained.Instance is null && chained.Arguments.FirstOrDefault()?.Value is { } inner && chained.TargetMethod.ReturnType.Name == "RouteGroupBuilder")
            return GroupPrefix(inner, model, cancellationToken, depth + 1);
        return "";
    }

    /// <param name="Method">GET, POST and so on; ANY for a route without a verb.</param>
    /// <param name="Route">The route template, from the root, such as "/api/orders/{id}".</param>
    /// <param name="Handler">The action or method that handles it, by symbol path; or the member a lambda handler is in.</param>
    /// <param name="Metadata">Attributes on the action and its controller, or calls chained on the Map* call, such as Authorize or RequireAuthorization.</param>
    public sealed record Endpoint(string Method, string Route, string Handler, SourceLocation? Location, IReadOnlyList<string> Metadata);
}
