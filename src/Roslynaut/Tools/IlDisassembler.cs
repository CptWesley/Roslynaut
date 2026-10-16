using System.Text;
using ModelContextProtocol;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Roslynaut.Tools;

/// <param name="Method">Cecil's full name of the method, e.g. "System.Void Ns.Type::Method(System.Int32)".</param>
/// <param name="IL">Max stack, locals, exception handlers and instructions, with source lines as comments when a PDB is available.</param>
public sealed record MethodIL(string Method, string IL);

/// <summary>Finds methods in a Cecil module and disassembles them.</summary>
internal static class IlDisassembler
{
    public const string ResultDescription = "A type stands for all its methods. Every method comes with its compiler-generated helpers "
        + "(async and iterator state machines, lambdas, local functions). Paged per method.";

    // IL is verbose, so pages are smaller than for other tools.
    public const int DefaultPageSize = 10;

    public const string AssemblyPath = "Path to the .dll or .exe.";

    public const string Name = "Type or method name, optionally partially qualified, e.g. 'System.String.Concat', 'Dictionary.TryGetValue' or 'Outer.Nested'; "
        + "or a documentation ID such as 'M:Ns.Type.Method(System.Int32)'. A method name returns all its overloads. "
        + "A generic arity, as in 'List`1' or 'List<T>', must match; without one, any arity does.";

    public static List<MethodIL> Disassemble(ModuleDefinition module, IReadOnlyCollection<string> names)
    {
        lock (module)
            return [.. FindMethods(module, names).Select(Format)];
    }

    /// <summary>
    /// The types and members named by <paramref name="names"/>: documentation IDs (such as "T:..." or "M:...") or names of types, methods,
    /// properties, events or fields, optionally partially qualified. Parameter lists are ignored, so a name matches all overloads; a generic
    /// arity, as in "List`1" or "List&lt;T&gt;", must match where a name gives one. Compiler-generated members are left out. Lock the module while calling this.
    /// </summary>
    public static List<IMemberDefinition> FindMembers(ModuleDefinition module, IReadOnlyCollection<string> names)
    {
        var types = module.GetTypes().Where(type => !IsGenerated(type)).ToList();
        var members = names.SelectMany(name => Find(types, name)).Distinct().ToList();
        if (members.Count > 0)
            return members;

        // Facades, such as System.Collections.dll of a runtime, forward types to the assembly that defines them. A name is of the type or a member.
        var queries = names.Select(name => Parse(name.Length > 2 && name[1] == ':' ? name[2..] : name)).ToList();
        var forwarded = module.ExportedTypes.FirstOrDefault(type => type.IsForwarder && Parse(type.FullName) is var typeName
            && queries.Any(query => IsNamed(typeName, query) || IsNamed(typeName, [.. query.SkipLast(1)])));
        throw new McpException($"No type or member matches {string.Join(", ", names)} in {module.Name}."
            + (forwarded is null ? "" : $" {forwarded.FullName} is forwarded to {forwarded.Scope.Name}: pass {forwarded.Scope.Name}.dll instead."));
    }

    /// <summary>
    /// The methods of what <see cref="FindMembers"/> finds: all methods of a type, the accessors of a property or event, and a method itself;
    /// each with its compiler-generated helpers. Lock the module while calling this.
    /// </summary>
    public static List<MethodDefinition> FindMethods(ModuleDefinition module, IReadOnlyCollection<string> names)
    {
        var methods = FindMembers(module, names).SelectMany(member => member switch
        {
            TypeDefinition type => type.Methods.Concat(type.NestedTypes.Where(IsGenerated).SelectMany(nested => nested.Methods)),
            MethodDefinition method => WithGenerated(method),
            PropertyDefinition property => new[] { property.GetMethod, property.SetMethod }.OfType<MethodDefinition>().SelectMany(WithGenerated),
            EventDefinition @event => new[] { @event.AddMethod, @event.RemoveMethod }.OfType<MethodDefinition>().SelectMany(WithGenerated),
            _ => [],
        }).Distinct().ToList();
        return methods.Count > 0 ? methods : throw new McpException($"{string.Join(", ", names)} has no code in {module.Name}: fields have none.");
    }

    private static IEnumerable<IMemberDefinition> Find(List<TypeDefinition> types, string name)
    {
        var isDocId = name.Length > 2 && name[1] == ':';
        var query = Parse(name);
        bool Matches(IMemberDefinition member, List<Part> typeName) => isDocId
            ? DocCommentId.GetDocCommentId(member) == name
            : IsNamed(member is TypeDefinition ? typeName : [.. typeName, .. Parse(member.Name, (member as MethodDefinition)?.GenericParameters.Count)], query);

        foreach (var type in types)
        {
            var typeName = Parse(type.FullName);
            if (Matches(type, typeName))
            {
                yield return type;
                continue;
            }

            IEnumerable<IMemberDefinition> members = [.. type.Methods, .. type.Properties, .. type.Events, .. type.Fields];
            foreach (var member in members.Where(member => !IsGenerated(member) && Matches(member, typeName)))
                yield return member;
        }
    }

    private static IEnumerable<MethodDefinition> WithGenerated(MethodDefinition method)
    {
        // Helpers are named after their method: <Main>b__0_0 for a lambda, <Main>g__Local|0_0 for a local function, <MainAsync>d__1 for a state machine.
        var prefix = $"<{method.Name}>";
        var type = method.DeclaringType;
        return type.Methods.Concat(type.NestedTypes.Where(IsGenerated).SelectMany(nested => nested.Methods))
            .Where(helper => helper.Name.StartsWith(prefix, StringComparison.Ordinal) || helper.DeclaringType.Name.StartsWith(prefix, StringComparison.Ordinal))
            .Prepend(method);
    }

    private static MethodIL Format(MethodDefinition method)
    {
        if (!method.HasBody)
            return new(method.FullName, "// No body: abstract, extern or implemented by the runtime.");

        var body = method.Body;
        var il = new StringBuilder($".maxstack {body.MaxStackSize}\n");
        if (body.HasVariables)
            il.Append($".locals {(body.InitLocals ? "init " : "")}({string.Join(", ", body.Variables.Select(local => $"{local.VariableType.FullName} V_{local.Index}"))})\n");

        foreach (var handler in body.ExceptionHandlers)
        {
            var kind = handler.HandlerType switch
            {
                ExceptionHandlerType.Catch => $"catch {handler.CatchType.FullName}",
                ExceptionHandlerType.Filter => $"filter {Label(handler.FilterStart)}",
                _ => handler.HandlerType.ToString().ToLowerInvariant(),
            };
            il.Append($".try {Label(handler.TryStart)} to {Label(handler.TryEnd)} {kind} handler {Label(handler.HandlerStart)} to {Label(handler.HandlerEnd)}\n");
        }

        foreach (var instruction in body.Instructions)
        {
            if (method.DebugInformation.GetSequencePoint(instruction) is { IsHidden: false } point)
                il.Append($"// {Path.GetFileName(point.Document.Url)}:{point.StartLine}\n");
            il.Append(instruction).Append('\n');
        }

        return new(method.FullName, il.ToString());
    }

    private static string Label(Instruction? instruction) => instruction is null ? "end" : $"IL_{instruction.Offset:x4}";

    /// <summary>
    /// Whether a full name ends with the parts of a query, by name and, where the query gives one, by arity. A part of a full name without
    /// an arity has none.
    /// </summary>
    private static bool IsNamed(List<Part> fullName, List<Part> query) =>
        query.Count > 0 && query.Count <= fullName.Count && fullName.TakeLast(query.Count).Zip(query).All(parts =>
            parts.First.Name.Equals(parts.Second.Name, StringComparison.OrdinalIgnoreCase) && (parts.Second.Arity is null || parts.Second.Arity == (parts.First.Arity ?? 0)));

    /// <summary>
    /// The dotted parts of a name, each with its generic arity where the name gives one: "Dictionary`2", "Dictionary&lt;TKey, TValue&gt;" and
    /// "Dictionary&lt;,&gt;" all give 2, as "Method``1" of a documentation ID gives 1. Drops whitespace, parameter lists and reflection's type
    /// argument lists ("List`1[System.Int32]"); takes Cecil's "Type::Method" and "Outer/Nested", and reflection's "Outer+Nested".
    /// </summary>
    /// <param name="arity">The arity of the last part, such as the type parameters of a method, which Cecil leaves out of its name.</param>
    private static List<Part> Parse(string name, int? arity = null)
    {
        var parts = new List<Part>();
        var text = new StringBuilder();
        int? arguments = null;
        var (depth, generic) = (0, false);
        foreach (var c in name.Replace("::", "."))
        {
            if (c is '(' or '<' or '[')
            {
                if (depth++ == 0 && (generic = c == '<'))
                    arguments = 1;
            }
            else if (c is ')' or '>' or ']')
                depth--;
            else if (depth > 0)
                arguments += depth == 1 && generic && c == ',' ? 1 : 0;
            else if (c is '.' or '+' or '/')
                Add();
            else if (!char.IsWhiteSpace(c))
                text.Append(c);
        }

        Add();
        if (arity is not null && parts.Count > 0)
            parts[^1] = parts[^1] with { Arity = arity };
        return parts;

        void Add()
        {
            var part = text.ToString();
            text.Clear();
            if (part.IndexOf('`') is >= 0 and var tick && int.TryParse(part.AsSpan(tick).TrimStart('`'), out var count))
                (part, arguments) = (part[..tick], count);

            // Empty for the dot of ".ctor".
            if (part.Length > 0)
                parts.Add(new(part, arguments));
            arguments = null;
        }
    }

    /// <param name="Arity">The number of type parameters, if known.</param>
    private readonly record struct Part(string Name, int? Arity);

    private static bool IsGenerated(IMemberDefinition member) => member.Name.StartsWith('<');
}
