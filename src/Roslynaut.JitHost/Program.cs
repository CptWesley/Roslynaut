using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Roslynaut.JitHost;

/// <summary>
/// JIT-compiles methods of an assembly without running them, so the JIT lists their native code when the caller sets DOTNET_JitDisasm.
/// Arguments: the assembly, then the metadata tokens of the methods. Standard input: dependencies that are not next to the assembly,
/// one "name&lt;tab&gt;path" per line. Standard output: the runtime, then per token a line with the token, a tab, and why it was not compiled, if so.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var dependencies = new Dictionary<string, string>();
        while (Console.ReadLine()?.Split('\t') is [var name, var path])
            dependencies[name] = path;

        Module module;
        try
        {
            module = Load(args[0], dependencies).ManifestModule;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{Path.GetFileName(args[0])} cannot be loaded: {e.Message}");
            return 1;
        }

        Console.WriteLine(RuntimeInformation.FrameworkDescription);
        foreach (var token in args.Skip(1).Select(int.Parse))
            Console.WriteLine($"{token}\t{Compile(module, token)}");
        return 0;
    }

    private static Assembly Load(string path, Dictionary<string, string> dependencies)
    {
        // A framework assembly cannot be loaded a second time, System.Private.CoreLib least of all: use the framework's own.
        var framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        if (framework.Contains(path, StringComparer.OrdinalIgnoreCase))
            return Assembly.Load(AssemblyName.GetAssemblyName(path));

        // From bytes, so files stay unlocked and their ReadyToRun code is ignored: the JIT compiles every method.
        var resolver = new AssemblyDependencyResolver(path);
        var context = new AssemblyLoadContext("Target");
        context.Resolving += (_, name) =>
            (dependencies.GetValueOrDefault(name.Name!) ?? resolver.ResolveAssemblyToPath(name)) is { } file ? FromBytes(context, file) : null;
        return FromBytes(context, path);
    }

    private static Assembly FromBytes(AssemblyLoadContext context, string path) => context.LoadFromStream(new MemoryStream(File.ReadAllBytes(path)));

    private static string Compile(Module module, int token)
    {
        try
        {
            var method = module.ResolveMethod(token)!;
            if (method.IsAbstract)
                return "The method is abstract.";

            // Open generics get System.Object, which compiles the code that all reference types share; System.Int32 where constraints demand it.
            try
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle, Instantiation(method, typeof(object)));
            }
            catch (ArgumentException)
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle, Instantiation(method, typeof(int)));
            }

            return "";
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}".ReplaceLineEndings(" ");
        }
    }

    private static RuntimeTypeHandle[] Instantiation(MethodBase method, Type argument)
    {
        var count = (method.DeclaringType!.IsGenericTypeDefinition ? method.DeclaringType.GetGenericArguments().Length : 0)
            + (method.IsGenericMethodDefinition ? method.GetGenericArguments().Length : 0);
        return [.. Enumerable.Repeat(argument.TypeHandle, count)];
    }
}
