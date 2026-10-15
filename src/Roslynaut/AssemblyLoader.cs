using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using ModelContextProtocol;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Roslynaut;

/// <summary>An assembly read with Mono.Cecil, plus what it takes to load it into a runtime.</summary>
/// <param name="Image">The bytes of the assembly.</param>
/// <param name="ResolveDependency">The bytes of a referenced assembly, or null to use the server's own, as for framework assemblies.</param>
public sealed record LoadedAssembly(ModuleDefinition Module, byte[] Image, Func<AssemblyName, byte[]?> ResolveDependency);

/// <summary>
/// Reads assemblies with Mono.Cecil: files, which are read again once they change, and compilations, which are emitted from current source.
/// Files are read fully into memory, so builds can still overwrite them. Cecil modules are not thread-safe: lock a module while reading it.
/// </summary>
/// <param name="capacity">How many files to keep in memory. Reading another one evicts the least recently used.</param>
public sealed class AssemblyLoader(int capacity = 32)
{
    private readonly Dictionary<string, CachedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<Compilation, Task<LoadedAssembly>> _compilations = new(), _optimized = new();
    private long _clock;

    public LoadedAssembly Read(string assemblyPath)
    {
        var path = Path.GetFullPath(assemblyPath);
        if (!File.Exists(path))
            throw new McpException($"Assembly not found: {path}");

        var timestamp = File.GetLastWriteTimeUtc(path);
        lock (_files)
        {
            if (!_files.TryGetValue(path, out var file) || file.Timestamp != timestamp)
            {
                // Read first, so a file that fails to read does not evict another.
                var assembly = ReadFile(path);
                if (file is null && _files.Count >= capacity)
                    _files.Remove(_files.MinBy(entry => entry.Value.LastUsed).Key);
                _files[path] = file = new(timestamp, assembly);
            }

            file.LastUsed = ++_clock;
            return file.Assembly;
        }
    }

    /// <summary>Emits the compilation with a PDB, so its IL maps to source lines. Each compilation is emitted once per optimization level.</summary>
    /// <param name="optimize">
    /// Emits a release build, as the JIT does not optimize debug builds; referenced projects too. Otherwise the project's own setting applies.
    /// </param>
    public Task<LoadedAssembly> CompileAsync(Compilation compilation, bool optimize = false) =>
        (optimize ? _optimized : _compilations).GetValue(compilation, key => Task.Run(() => Emit(key, optimize)));

    private static LoadedAssembly ReadFile(string path)
    {
        // Read the PDB into memory too, instead of letting Cecil keep it open.
        var image = File.ReadAllBytes(path);
        var pdb = Path.ChangeExtension(path, ".pdb");
        ModuleDefinition module;
        try
        {
            module = ModuleDefinition.ReadModule(new MemoryStream(image), new ReaderParameters
            {
                ReadSymbols = true,
                SymbolReaderProvider = new DefaultSymbolReaderProvider(throwIfNoSymbol: false),
                SymbolStream = File.Exists(pdb) ? new MemoryStream(File.ReadAllBytes(pdb)) : null,
            });
        }
        catch (BadImageFormatException)
        {
            throw new McpException($"Not a .NET assembly: {path}");
        }

        // Dependencies come from the .deps.json next to the file, or else from its directory.
        var resolver = new AssemblyDependencyResolver(path);
        return new(module, image, name => resolver.ResolveAssemblyToPath(name) is { } dependency ? File.ReadAllBytes(dependency) : null);
    }

    private LoadedAssembly Emit(Compilation compilation, bool optimize)
    {
        var (pe, pdb) = (new MemoryStream(), new MemoryStream());
        var emitted = optimize ? compilation.WithOptions(compilation.Options.WithOptimizationLevel(OptimizationLevel.Release)) : compilation;
        var result = emitted.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!result.Success)
        {
            var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Take(5);
            throw new McpException($"{compilation.AssemblyName} does not compile: {string.Join("; ", errors)}");
        }

        var image = pe.ToArray();
        pdb.Position = 0;
        var module = ModuleDefinition.ReadModule(new MemoryStream(image), new ReaderParameters { ReadSymbols = true, SymbolStream = pdb });
        return new(module, image, name => ResolveReference(compilation, name, optimize));
    }

    /// <summary>A referenced project, compiled from current source too, or a referenced package.</summary>
    private byte[]? ResolveReference(Compilation compilation, AssemblyName name, bool optimize)
    {
        var reference = compilation.References.FirstOrDefault(reference =>
            compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly && assembly.Name == name.Name);
        return reference switch
        {
            CompilationReference project => CompileAsync(project.Compilation, optimize).GetAwaiter().GetResult().Image,
            PortableExecutableReference { FilePath: { } path } when ImplementationOf(path) is { } file => File.ReadAllBytes(file),
            _ => null,
        };
    }

    /// <summary>
    /// Reference assemblies cannot run. NuGet packages pair ref\ with lib\; framework reference packs have no lib\, the server's runtime provides those.
    /// </summary>
    private static string? ImplementationOf(string path)
    {
        var (refs, lib) = ($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}");
        var index = path.LastIndexOf(refs, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return path;

        var implementation = path[..index] + lib + path[(index + refs.Length)..];
        return File.Exists(implementation) ? implementation : null;
    }

    private sealed record CachedFile(DateTime Timestamp, LoadedAssembly Assembly)
    {
        public long LastUsed { get; set; }
    }
}
