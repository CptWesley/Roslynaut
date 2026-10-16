using System.Text;
using System.Text.RegularExpressions;
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
        + "or a documentation ID such as 'M:Ns.Type.Method(System.Int32)'. A method name returns all its overloads.";

    public static List<MethodIL> Disassemble(ModuleDefinition module, IReadOnlyCollection<string> names)
    {
        lock (module)
            return [.. FindMethods(module, names).Select(Format)];
    }

    /// <summary>
    /// The types and members named by <paramref name="names"/>: documentation IDs (such as "T:..." or "M:...") or names of types, methods,
    /// properties, events or fields, optionally partially qualified. Parameter and type argument lists are ignored, so a name matches all
    /// overloads. Compiler-generated members are left out. Lock the module while calling this.
    /// </summary>
    public static List<IMemberDefinition> FindMembers(ModuleDefinition module, IReadOnlyCollection<string> names)
    {
        var types = module.GetTypes().Where(type => !IsGenerated(type)).ToList();
        var members = names.SelectMany(name => Find(types, name)).Distinct().ToList();
        return members.Count > 0 ? members : throw new McpException($"No type or member matches {string.Join(", ", names)} in {module.Name}.");
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
        var query = isDocId ? name : Simplify(name);
        bool Matches(IMemberDefinition member, string fullName) => isDocId
            ? DocCommentId.GetDocCommentId(member) == query
            : fullName.Equals(query, StringComparison.OrdinalIgnoreCase) || fullName.EndsWith("." + query, StringComparison.OrdinalIgnoreCase);

        foreach (var type in types)
        {
            var typeName = FullName(type);
            if (Matches(type, typeName))
            {
                yield return type;
                continue;
            }

            IEnumerable<IMemberDefinition> members = [.. type.Methods, .. type.Properties, .. type.Events, .. type.Fields];
            foreach (var member in members.Where(member => !IsGenerated(member) && Matches(member, $"{typeName}.{member.Name}")))
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

    /// <summary>The C#-style name of a type: nested types joined with dots, without generic arity, e.g. "Ns.Outer.Nested".</summary>
    private static string FullName(TypeDefinition type) => Regex.Replace(type.FullName.Replace('/', '.'), @"`\d+", "");

    /// <summary>Drops whitespace and parameter and type argument lists, and accepts Cecil's "Type::Method".</summary>
    private static string Simplify(string name)
    {
        var simple = new StringBuilder();
        var depth = 0;
        foreach (var c in name.Replace("::", "."))
        {
            depth += c switch { '(' or '<' => 1, ')' or '>' => -1, _ => 0 };
            if (depth == 0 && c is not (')' or '>') && !char.IsWhiteSpace(c))
                simple.Append(c);
        }

        return simple.ToString();
    }

    private static bool IsGenerated(IMemberDefinition member) => member.Name.StartsWith('<');
}
