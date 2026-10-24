using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Roslynaut.JitHost;

/// <summary>
/// Loads an assembly on the runtime the caller picked, and JIT-compiles methods of it without running them, so the JIT lists their native code
/// when the caller sets DOTNET_JitDisasm; or runs a workload first, so the JIT compiles them again at Tier1 with what Dynamic PGO saw; or lays out
/// a type. Standard input: a <see cref="Request"/>. Standard output: a <see cref="Response"/>. Both are JSON.
/// </summary>
public static class Program
{
    public static int Main()
    {
        // In UTF-8, as the server writes and reads it, rather than the console's code page, which cannot spell every path.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var request = JsonSerializer.Deserialize<Request>(new StreamReader(Console.OpenStandardInput(), utf8).ReadToEnd())!;
        var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
        Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        // A workload may write to the console; the response must stay alone on standard output.
        Console.SetOut(TextWriter.Null);

        AssemblyLoadContext context;
        Module module;
        try
        {
            (context, module) = Load(request.Assembly, request.Dependencies);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{Path.GetFileName(request.Assembly)} cannot be loaded: {e.Message}");
            return 1;
        }

        try
        {
            Type[] arguments = [.. request.TypeArguments.Select(name => TypeOf(context, name))];
            var errors = request.Methods.ToDictionary(token => token, token => Compile(module, token, arguments));
            var workload = request.Workload is { } work ? Run(context, work) : null;
            var layout = request.Layout is { } type ? LayOut(TypeOf(context, type)) : null;
            output.Write(JsonSerializer.Serialize(new Response(RuntimeInformation.FrameworkDescription, errors, workload, layout)));
            return 0;
        }
        catch (Exception e) when (e is TypeLoadException or FileNotFoundException or FileLoadException or ArgumentException)
        {
            // Type arguments or a type to lay out that do not resolve, or that do not fit.
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static (AssemblyLoadContext Context, Module Module) Load(string path, Dictionary<string, string> dependencies)
    {
        // A framework assembly cannot be loaded a second time, System.Private.CoreLib least of all: use the framework's own.
        var framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        if (framework.Contains(path, StringComparer.OrdinalIgnoreCase))
            return (AssemblyLoadContext.Default, Assembly.Load(AssemblyName.GetAssemblyName(path)).ManifestModule);

        // From bytes, so files stay unlocked and their ReadyToRun code is ignored: the JIT compiles every method.
        var resolver = new AssemblyDependencyResolver(path);
        var context = new AssemblyLoadContext("Target");
        // A workload's assembly references the target, which is loaded already.
        context.Resolving += (_, name) => context.Assemblies.FirstOrDefault(loaded => loaded.GetName().Name == name.Name)
            ?? ((dependencies.GetValueOrDefault(name.Name!) ?? resolver.ResolveAssemblyToPath(name)) is { } file ? FromBytes(context, file) : null);
        return (context, FromBytes(context, path).ManifestModule);
    }

    private static Assembly FromBytes(AssemblyLoadContext context, string path) => context.LoadFromStream(new MemoryStream(File.ReadAllBytes(path)));

    /// <summary>A type by its assembly-qualified name, with its assemblies as the target sees them; type arguments too.</summary>
    private static Type TypeOf(AssemblyLoadContext context, string name) =>
        Type.GetType(name, assemblyName => context.Assemblies.FirstOrDefault(loaded => loaded.GetName().Name == assemblyName.Name) ?? context.LoadFromAssemblyName(assemblyName),
            typeResolver: null, throwOnError: true)!;

    private static string Compile(Module module, int token, Type[] arguments)
    {
        try
        {
            var method = module.ResolveMethod(token)!;
            if (method.IsAbstract)
                return "The method is abstract.";

            // Open generics get the arguments given, then System.Object, which compiles the code that all reference types share; without
            // arguments given, System.Int32 where constraints demand it.
            try
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle, Instantiation(method, arguments, typeof(object)));
            }
            catch (ArgumentException) when (arguments.Length == 0)
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle, Instantiation(method, arguments, typeof(int)));
            }

            return "";
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}".ReplaceLineEndings(" ");
        }
    }

    private static RuntimeTypeHandle[] Instantiation(MethodBase method, Type[] arguments, Type rest)
    {
        var count = (method.DeclaringType!.IsGenericTypeDefinition ? method.DeclaringType.GetGenericArguments().Length : 0)
            + (method.IsGenericMethodDefinition ? method.GetGenericArguments().Length : 0);
        return [.. arguments.Concat(Enumerable.Repeat(rest, Math.Max(0, count - arguments.Length))).Take(count).Select(type => type.TypeHandle)];
    }

    /// <summary>
    /// Calls the workload until the JIT has had nothing to compile for half a second, so the methods it makes hot have gone through
    /// instrumented Tier0 to Tier1; for at most 20 seconds. Returns why it stopped early, if it did.
    /// </summary>
    private static string? Run(AssemblyLoadContext context, Workload workload)
    {
        var name = AssemblyName.GetAssemblyName(workload.Assembly).Name;
        var method = (MethodInfo)(context.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == name) ?? FromBytes(context, workload.Assembly))
            .ManifestModule.ResolveMethod(workload.Token)!;
        var (total, quiet) = (Stopwatch.StartNew(), Stopwatch.StartNew());
        var compiled = JitInfo.GetCompiledMethodCount();
        try
        {
            var target = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!);
            while (quiet.ElapsedMilliseconds < 500)
            {
                if (total.Elapsed > TimeSpan.FromSeconds(20))
                    return "The JIT was still compiling after 20 seconds of the workload.";

                (method.Invoke(target, null) as Task)?.GetAwaiter().GetResult();
                if (JitInfo.GetCompiledMethodCount() is var count && count != compiled)
                {
                    compiled = count;
                    quiet.Restart();
                }
            }

            return null;
        }
        catch (Exception e)
        {
            var thrown = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
            return $"The workload threw {thrown.GetType().Name}: {thrown.Message}".ReplaceLineEndings(" ");
        }
    }

    /// <summary>
    /// The instance fields of a type, its base types' included, where the runtime put them, without creating an instance or running its
    /// static constructor.
    /// </summary>
    private static Layout LayOut(Type type)
    {
        if (type.IsInterface)
            throw new ArgumentException($"{type} is an interface, which has no fields.");

        List<FieldLayout> fields = [];
        for (var declaring = type; declaring is not null; declaring = declaring.BaseType)
        {
            fields.AddRange(declaring.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Select(field => new FieldLayout(field.Name, NameOf(field.FieldType), declaring == type ? null : NameOf(declaring), OffsetOf(field),
                    field.FieldType.IsValueType ? SizeOf(field.FieldType) : IntPtr.Size)));
        }

        // The size of a class's objects is their method table's base size, header included; the runtime's own MethodTable struct keeps it at 4.
        var size = type.IsValueType ? SizeOf(type) : Marshal.ReadInt32(type.TypeHandle.Value, 4);
        return new(NameOf(type), type.IsValueType, size, [.. fields.OrderBy(field => field.Offset)]);
    }

    /// <summary>
    /// A field's offset as the JIT's code addresses it: from where a reference to an object points, its method table pointer; or from the start
    /// of a struct. The JIT compiles ldflda to add exactly that to whatever address it gets, here one of a local, so nothing is read.
    /// </summary>
    private static int OffsetOf(FieldInfo field)
    {
        var method = new DynamicMethod("", typeof(byte).MakeByRefType(), [typeof(byte).MakeByRefType()], field.Module, skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, field);
        il.Emit(OpCodes.Ret);
        long local = 0;
        ref var start = ref Unsafe.As<long, byte>(ref local);
        return (int)Unsafe.ByteOffset(ref start, ref method.CreateDelegate<FieldAddress>()(ref start));
    }

    private delegate ref byte FieldAddress(ref byte address);

    private static int SizeOf(Type type) => (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(type).Invoke(null, null)!;

    /// <summary>A type's name with its namespace and type arguments, as in System.Collections.Generic.List&lt;System.Int32&gt;.</summary>
    private static string NameOf(Type type) => type switch
    {
        { IsArray: true } => $"{NameOf(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]",
        { IsGenericType: true } => $"{Regex.Replace(type.GetGenericTypeDefinition().FullName!, "`[0-9]+", "").Replace('+', '.')}<{string.Join(", ", type.GetGenericArguments().Select(NameOf))}>",
        _ => (type.FullName ?? type.Name).Replace('+', '.'),
    };
}

/// <param name="Assembly">The assembly whose methods to compile.</param>
/// <param name="Dependencies">Paths of dependencies that are not next to the assembly, by name.</param>
/// <param name="Methods">Metadata tokens of the methods to compile, in this order.</param>
/// <param name="TypeArguments">
/// Assembly-qualified names of the type arguments to compile generic methods with: the outermost type's parameters first, then the method's own.
/// </param>
/// <param name="Workload">A method to run after compiling the methods, as many times as it takes the JIT to compile them again with Dynamic PGO.</param>
/// <param name="Layout">The assembly-qualified name of a type to lay out.</param>
public sealed record Request(string Assembly, Dictionary<string, string> Dependencies, int[] Methods, string[] TypeArguments, Workload? Workload, string? Layout);

/// <param name="Assembly">The assembly of the method; it is loaded along with the target, with the same dependencies.</param>
/// <param name="Token">The metadata token of a parameterless method; one that is not static runs on an instance its parameterless constructor makes.</param>
public sealed record Workload(string Assembly, int Token);

/// <param name="Errors">Per method token, why it was not compiled; empty if it was.</param>
/// <param name="Workload">Why the workload stopped before the JIT was done, if it did.</param>
public sealed record Response(string Runtime, Dictionary<int, string> Errors, string? Workload, Layout? Layout);

/// <param name="Size">The bytes an object takes on the heap, header included; for a struct, its size as a field or local.</param>
/// <param name="Fields">By offset.</param>
public sealed record Layout(string Type, bool IsValueType, int Size, FieldLayout[] Fields);

/// <param name="DeclaringType">The base type that declares the field, if it is not the type laid out.</param>
public sealed record FieldLayout(string Name, string Type, string? DeclaringType, int Offset, int Size);
