using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed partial class DiRegistrationsTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => ListAsync;

    [McpServerTool(Name = "di_registrations", ReadOnly = true)]
    [Description("Lists the Microsoft.Extensions.DependencyInjection registrations in source, per service type: each AddSingleton, AddScoped, "
        + "AddTransient, TryAdd*, AddKeyed* and AddHostedService call with its implementation (a type, or the factory or instance it gives), "
        + "lifetime and location; which registration wins, the last in source order, as a resolve takes the last; the constructors the service "
        + "is injected into; and captive dependencies, a scoped or transient service injected into a singleton. Answers which class runs when "
        + "an interface is injected. Registrations made by scanning assemblies, or through helpers that take the types as parameters, are not seen.")]
    private async Task<IReadOnlyList<ServiceRegistrations>> ListAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("Optional regex for the service types to list, e.g. 'IOrderStore' or '^MyApp\\.'.")] string? service = null,
        CancellationToken cancellationToken = default)
    {
        var pattern = ProcessDefaults.Pattern(service, "service");
        return [.. (await FindAsync((await workspace.GetAsync(projectPath)).Solution, cancellationToken)).Where(entry => pattern is null || pattern.IsMatch(entry.Service))];
    }

    internal static async Task<IReadOnlyList<ServiceRegistrations>> FindAsync(Solution solution, CancellationToken cancellationToken)
    {
        var found = new List<(ITypeSymbol Service, ITypeSymbol? Implementation, string Shown, string Lifetime, string Method, string? Key, Location Location)>();
        var constructors = new List<IMethodSymbol>();
        foreach (var project in solution.Projects.Where(project => project.Language == LanguageNames.CSharp))
        {
            var compilation = (await project.GetCompilationAsync(cancellationToken))!;
            constructors.AddRange(AllTypes(compilation.Assembly.GlobalNamespace).SelectMany(type => type.InstanceConstructors).Where(constructor => constructor.Parameters.Length > 0));
            foreach (var document in project.Documents)
            {
                var model = (await document.GetSemanticModelAsync(cancellationToken))!;
                foreach (var call in (await document.GetSyntaxRootAsync(cancellationToken))!.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetOperation(call, cancellationToken) is IInvocationOperation invocation && RegistrationOf(invocation) is { } registration)
                        found.Add(registration);
                }
            }
        }

        // What each implementation is registered as, to tell a singleton that takes a shorter-lived service.
        var lifetimes = found.Where(registration => registration.Implementation is not null)
            .GroupBy(registration => registration.Implementation!, SymbolEqualityComparer.Default)
            .ToDictionary(group => (ITypeSymbol)group.Key!, group => group.Last().Lifetime, SymbolEqualityComparer.Default);
        return [.. found.GroupBy(registration => registration.Service, SymbolEqualityComparer.Default).Select(group =>
        {
            var service = (ITypeSymbol)group.Key!;
            var registrations = group.OrderBy(registration => registration.Location.SourceTree?.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(registration => registration.Location.SourceSpan.Start).ToList();
            // A TryAdd adds nothing after a registration of the service; the last of the others is what a resolve gets, per key.
            var winners = registrations.GroupBy(registration => registration.Key)
                .Select(key => key.LastOrDefault(registration => !registration.Method.StartsWith("TryAdd", StringComparison.Ordinal)) is { Method: not null } last ? last : key.First())
                .ToList();
            var winner = winners.FirstOrDefault(registration => registration.Key is null) is { Method: not null } unkeyed ? unkeyed : winners[0];
            var consumers = constructors.Where(constructor => constructor.Parameters.Any(parameter => Takes(parameter.Type, service))).ToList();
            var lifetime = winner.Lifetime;
            var captive = lifetime is "Scoped" or "Transient"
                ? consumers.Select(constructor => constructor.ContainingType).Where(consumer => lifetimes.GetValueOrDefault(consumer) == "Singleton")
                    .Select(consumer => $"Captive dependency: {lifetime} {service.ToDisplayString()} is injected into {consumer.ToDisplayString()}, a Singleton, which keeps one instance for good.")
                : [];
            return new ServiceRegistrations(
                service.ToDisplayString(),
                [.. registrations.Select(registration => new Registration(registration.Shown, registration.Lifetime, registration.Method, registration.Key,
                    SourceLocation.From(registration.Location), winners.Contains(registration)))],
                [.. consumers.Select(constructor => SymbolEntry.From(constructor))],
                [.. captive]);
        }).OrderBy(entry => entry.Service, StringComparer.Ordinal)];
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol space) =>
        space.GetTypeMembers().SelectMany(Nested).Concat(space.GetNamespaceMembers().SelectMany(AllTypes));

    private static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type) => type.GetTypeMembers().SelectMany(Nested).Prepend(type);

    /// <summary>Whether a constructor parameter takes the service: as itself, or all of its registrations as IEnumerable.</summary>
    private static bool Takes(ITypeSymbol parameter, ITypeSymbol service) =>
        SymbolEqualityComparer.Default.Equals(parameter, service)
        || parameter is INamedTypeSymbol { Name: "IEnumerable", TypeArguments: [var item] } && SymbolEqualityComparer.Default.Equals(item, service);

    private static (ITypeSymbol Service, ITypeSymbol? Implementation, string Shown, string Lifetime, string Method, string? Key, Location Location)? RegistrationOf(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (Registering().Match(method.Name) is not { Success: true } name || method.ContainingNamespace?.ToDisplayString() != "Microsoft.Extensions.DependencyInjection.Extensions"
            && method.ContainingNamespace?.ToDisplayString() != "Microsoft.Extensions.DependencyInjection")
            return null;

        IOperation? Argument(string parameter) => invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == parameter)?.Value;
        static ITypeSymbol? TypeOf(IOperation? operation) => operation is ITypeOfOperation type ? type.TypeOperand : null;
        static IOperation? Unwrap(IOperation? operation) => operation switch
        {
            IConversionOperation conversion => Unwrap(conversion.Operand),
            IDelegateCreationOperation creation => Unwrap(creation.Target),
            _ => operation,
        };

        var lifetime = name.Groups["lifetime"].Success ? name.Groups["lifetime"].Value : "Singleton";
        var arguments = method.TypeArguments;
        var service = arguments.Length > 0 ? arguments[0] : TypeOf(Argument("serviceType"));
        if (service is null)
            return null;

        var factory = Unwrap(Argument("implementationFactory"));
        var instance = Unwrap(Argument("implementationInstance"));
        ITypeSymbol? implementation = arguments.Length > 1 ? arguments[1] : TypeOf(Argument("implementationType")) ?? instance?.Type;
        string shown;
        if (factory is not null)
        {
            var line = factory.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            // What a lambda factory returns, before the conversion to the service type.
            implementation = factory is IAnonymousFunctionOperation lambda
                ? lambda.Body.Descendants().OfType<IReturnOperation>().Select(returned => Unwrap(returned.ReturnedValue)?.Type).FirstOrDefault(type => type is not null)
                : null;
            shown = $"factory at line {line}" + (implementation is null ? "" : $", returning {implementation.ToDisplayString()}");
        }
        else
        {
            implementation ??= service;
            shown = (instance is null ? "" : "instance of ") + implementation.ToDisplayString();
        }

        var key = Unwrap(Argument("serviceKey"))?.ConstantValue is { HasValue: true } constant ? constant.Value?.ToString() ?? "null" : null;
        return (service, implementation, shown, lifetime, method.Name, key, invocation.Syntax.GetLocation());
    }

    [GeneratedRegex("^(TryAdd|Add)(Keyed)?((?<lifetime>Singleton|Scoped|Transient)|HostedService)$")]
    private static partial Regex Registering();

    /// <param name="Registrations">The calls that register it, in source order.</param>
    /// <param name="InjectedInto">The constructors that take it, or all its registrations as an IEnumerable.</param>
    /// <param name="Problems">Captive dependencies: a shorter-lived registration injected into a singleton.</param>
    public sealed record ServiceRegistrations(string Service, IReadOnlyList<Registration> Registrations, IReadOnlyList<SymbolEntry> InjectedInto, IReadOnlyList<string> Problems);

    /// <param name="Implementation">The implementation type, or the factory or instance that provides it.</param>
    /// <param name="Method">The call, such as AddScoped or TryAddSingleton.</param>
    /// <param name="Key">A keyed registration's key.</param>
    /// <param name="Wins">Whether a resolve of the service gets this one: the last registration in source order, TryAdd ones aside.</param>
    public sealed record Registration(string Implementation, string Lifetime, string Method, string? Key, SourceLocation Location, bool Wins);
}
