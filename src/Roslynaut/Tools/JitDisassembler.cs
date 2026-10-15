using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using JitBuddy;
using ModelContextProtocol;

namespace Roslynaut.Tools;

/// <param name="Method">Cecil's full name of the method, as in <see cref="MethodIL"/>.</param>
/// <param name="Asm">The native code as listed by JitBuddy, after a header naming the runtime; or a comment saying why there is none.</param>
public sealed record MethodAsm(string Method, string Asm);

/// <summary>
/// Disassembles the native code that the server's own JIT generates for methods, with JitBuddy. JitBuddy works in-process only,
/// so assemblies are loaded into a collectible context, which is unloaded again afterwards.
/// </summary>
internal static class JitDisassembler
{
    public const string ResultDescription = "The code comes from the server's own JIT, with full optimization: its .NET version, on this machine's x86 or x64 CPU. "
        + "Assemblies built for debugging stay unoptimized. Methods of generic types are shown for System.Int32 type arguments. "
        + "JitBuddy does not support constructors, generic methods, or virtual methods of value types (such as the state machines of optimized async methods). "
        + IlDisassembler.ResultDescription;

    public static List<MethodAsm> Disassemble(LoadedAssembly assembly, IReadOnlyCollection<string> names)
    {
        // JitBuddy decodes with Iced, which only knows x86 and x64.
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.X86))
            throw new McpException($"JitBuddy only disassembles x86 and x64 code, and this machine runs {RuntimeInformation.ProcessArchitecture}.");

        List<(string Name, int Token)> methods;
        lock (assembly.Module)
            methods = [.. IlDisassembler.FindMethods(assembly.Module, names).Select(method => (method.FullName, method.MetadataToken.ToInt32()))];

        // ponytail: loads and JIT-compiles target code inside the server process; run a separate host process if that must be isolated.
        var context = new JitContext(assembly.ResolveDependency);
        try
        {
            Module module;
            try
            {
                module = context.LoadFromStream(new MemoryStream(assembly.Image)).ManifestModule;
            }
            catch (Exception e) when (e is BadImageFormatException or IOException)
            {
                throw new McpException($"{assembly.Module.Name} cannot be loaded for the JIT: {e.Message}");
            }

            return [.. methods.Select(method => new MethodAsm(method.Name, ToAsm(module, method.Token)))];
        }
        finally
        {
            context.Unload();
        }
    }

    private static string ToAsm(Module module, int token)
    {
        try
        {
            return module.ResolveMethod(token) switch
            {
                MethodInfo { IsAbstract: true } => "; No native code: the method is abstract.",
                MethodInfo method => ToAsm(method),
                _ => "; Not disassembled: JitBuddy only supports methods, not constructors.",
            };
        }
        catch (Exception e)
        {
            // Typically a dependency that cannot be found, or a generic constraint that System.Int32 does not satisfy.
            return $"; Not disassembled: {e.GetType().Name}: {e.Message}";
        }
    }

    private static string ToAsm(MethodInfo method)
    {
        // ClrMD, which JitBuddy uses to find the code, does not find instantiations of generic methods.
        if (method.IsGenericMethodDefinition)
            return "; Not disassembled: JitBuddy does not support generic methods, only methods of generic types.";

        var header = $"; {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}";
        if (method.DeclaringType is { ContainsGenericParameters: true } type)
        {
            // System.Int32 gets code of its own; reference types share code, which JitBuddy cannot find either.
            var closed = type.MakeGenericType([.. Enumerable.Repeat(typeof(int), type.GetGenericArguments().Length)]);
            method = (MethodInfo)MethodBase.GetMethodFromHandle(method.MethodHandle, closed.TypeHandle)!;
            header += ", type arguments System.Int32";
        }

        if (method.Module.Assembly.GetCustomAttribute<DebuggableAttribute>() is { IsJITOptimizerDisabled: true })
            header += ", unoptimized because the assembly is a debug build";

        // JitBuddy is handed the unboxing stub of these, and returns nothing.
        var asm = method.ToAsm();
        return asm.Length > 0
            ? $"{header}\n{asm}"
            : "; Not disassembled: JitBuddy does not support virtual methods of value types, such as the state machines of optimized async methods.";
    }

    /// <summary>Loads dependencies from the target's own references; null falls back to the server's assemblies, which include the framework.</summary>
    private sealed class JitContext(Func<AssemblyName, byte[]?> resolve) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => resolve(name) is { } image ? LoadFromStream(new MemoryStream(image)) : null;
    }
}
