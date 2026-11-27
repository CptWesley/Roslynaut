using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using ModelContextProtocol;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Roslynaut.Caching;

namespace Roslynaut;

/// <summary>An assembly read with Mono.Cecil, plus what it takes to load it into a runtime.</summary>
/// <param name="Path">The assembly on disk. Compilations are written to a temporary file when first asked for.</param>
/// <param name="Dependencies">
/// Paths of referenced assemblies, by name, that a runtime would not find next to the assembly. The framework is left out: runtimes have their own.
/// </param>
/// <param name="Compiled">Whether it was emitted from a compilation, rather than read from a file.</param>
public sealed record LoadedAssembly(ModuleDefinition Module, Lazy<string> Path, Lazy<IReadOnlyDictionary<string, string>> Dependencies, bool Compiled = false);

/// <summary>
/// Reads assemblies with Mono.Cecil: files, which are read again once they change, and compilations, which are emitted from current source.
/// Files are read fully into memory, so builds can still overwrite them. Cecil modules are not thread-safe: lock a module while reading it.
/// </summary>
/// <param name="capacity">How many files to keep in memory. Reading another one evicts the least recently used.</param>
public sealed class AssemblyLoader(int capacity = 32) : IDisposable
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> NoDependencies = new(new Dictionary<string, string>());

    private readonly Dictionary<string, CachedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<Compilation, Task<LoadedAssembly>> _compilations = new(), _optimized = new();
    private readonly string _temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Roslynaut", Environment.ProcessId.ToString());
    private long _clock;

    public LoadedAssembly Read(string assemblyPath)
    {
        var path = System.IO.Path.GetFullPath(assemblyPath);
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

    // ponytail: temporary files of compilations stay until the server stops; delete them per compilation if long sessions pile them up.
    public void Dispose()
    {
        if (Directory.Exists(_temporary))
            Directory.Delete(_temporary, recursive: true);
    }

    private static LoadedAssembly ReadFile(string path)
    {
        // Read the PDB into memory too, instead of letting Cecil keep it open.
        var pdb = System.IO.Path.ChangeExtension(path, ".pdb");
        try
        {
            var module = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(path)), new ReaderParameters
            {
                ReadSymbols = true,
                SymbolReaderProvider = new DefaultSymbolReaderProvider(throwIfNoSymbol: false),
                SymbolStream = File.Exists(pdb) ? new MemoryStream(File.ReadAllBytes(pdb)) : null,
            });

            // A file's own .deps.json or directory tells where its dependencies are.
            return new(module, new(path), NoDependencies);
        }
        catch (BadImageFormatException)
        {
            throw new McpException($"Not a .NET assembly: {path}");
        }
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
        return new(module, new(() => Write(image)), new(() => ReferencePaths(compilation, optimize)), Compiled: true);
    }

    /// <summary>
    /// A cache key for what is computed from the assembly as a runtime or decompiler loads it, by content: of a compilation and the
    /// dependencies it is given; of a file and what sits next to it, the assemblies it references and the .deps.json and .runtimeconfig.json
    /// that pick its packages and runtime, which can all change while the file does not. Computed on every call for that reason.
    /// </summary>
    public static string ContentKey(LoadedAssembly assembly)
    {
        var path = assembly.Path.Value;
        // Compilations are written under names of their own each time, so their dependencies go by assembly name.
        var others = assembly.Compiled
            ? assembly.Dependencies.Value.Select(dependency => (dependency.Key, dependency.Value))
            : Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(path)!)
                .Where(file => file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                .Select(file => (System.IO.Path.GetFileName(file), file));
        var hashes = others.Select(other => $"{other.Item1}={CacheExtensions.HashOf(other.Item2)}").Order(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', hashes.Prepend(CacheExtensions.HashOf(path))))));
    }

    private string Write(byte[] image)
    {
        Directory.CreateDirectory(_temporary);
        var path = System.IO.Path.Combine(_temporary, $"{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(path, image);
        return path;
    }

    /// <summary>Referenced projects, compiled from current source too, with their own references; and referenced packages.</summary>
    private IReadOnlyDictionary<string, string> ReferencePaths(Compilation compilation, bool optimize)
    {
        var paths = new Dictionary<string, string>();
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol { Name: var name })
                continue;

            if (reference is CompilationReference project)
            {
                var compiled = CompileAsync(project.Compilation, optimize).GetAwaiter().GetResult();
                paths.TryAdd(name, compiled.Path.Value);
                foreach (var (dependency, path) in compiled.Dependencies.Value)
                    paths.TryAdd(dependency, path);
            }
            else if (reference is PortableExecutableReference { FilePath: { } file } && ImplementationOf(file) is { } path)
            {
                paths.TryAdd(name, path);
            }
        }

        return paths;
    }

    /// <summary>
    /// The file of a referenced assembly, with code: a referenced package's lib\ assembly, or the framework's own of the server's runtime.
    /// Null for assemblies of the solution's own projects, which have no file until they are built.
    /// </summary>
    public static async Task<string?> FindFileAsync(Solution solution, IAssemblySymbol assembly, CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation?.GetMetadataReference(assembly) is not PortableExecutableReference { FilePath: { } path })
                continue;

            var framework = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!, $"{assembly.Name}.dll");
            return ImplementationOf(path) ?? (File.Exists(framework) ? framework : null);
        }

        return null;
    }

    /// <summary>
    /// Reference assemblies cannot run. NuGet packages pair ref\ with lib\; framework reference packs have no lib\, runtimes provide those.
    /// </summary>
    private static string? ImplementationOf(string path)
    {
        var (refs, lib) = ($"{System.IO.Path.DirectorySeparatorChar}ref{System.IO.Path.DirectorySeparatorChar}", $"{System.IO.Path.DirectorySeparatorChar}lib{System.IO.Path.DirectorySeparatorChar}");
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
