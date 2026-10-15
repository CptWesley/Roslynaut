using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using ModelContextProtocol;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Roslynaut;

/// <summary>
/// Reads assemblies with Mono.Cecil: files, which are read again once they change, and compilations, which are emitted from current source.
/// Files are read fully into memory, so builds can still overwrite them. Cecil modules are not thread-safe: lock a module while reading it.
/// </summary>
/// <param name="capacity">How many files to keep in memory. Reading another one evicts the least recently used.</param>
public sealed class AssemblyLoader(int capacity = 32)
{
    private readonly Dictionary<string, CachedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<Compilation, Task<ModuleDefinition>> _compilations = new();
    private long _clock;

    public ModuleDefinition Read(string assemblyPath)
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
                var module = ReadFile(path);
                if (file is null && _files.Count >= capacity)
                    _files.Remove(_files.MinBy(entry => entry.Value.LastUsed).Key);
                _files[path] = file = new(timestamp, module);
            }

            file.LastUsed = ++_clock;
            return file.Module;
        }
    }

    /// <summary>Emits the compilation with a PDB, so its IL maps to source lines. Each compilation is emitted once.</summary>
    public Task<ModuleDefinition> CompileAsync(Compilation compilation) =>
        _compilations.GetValue(compilation, key => Task.Run(() => Emit(key)));

    private static ModuleDefinition ReadFile(string path)
    {
        // Read the PDB into memory too, instead of letting Cecil keep it open.
        var pdb = Path.ChangeExtension(path, ".pdb");
        try
        {
            return ModuleDefinition.ReadModule(path, new ReaderParameters
            {
                InMemory = true,
                ReadSymbols = true,
                SymbolReaderProvider = new DefaultSymbolReaderProvider(throwIfNoSymbol: false),
                SymbolStream = File.Exists(pdb) ? new MemoryStream(File.ReadAllBytes(pdb)) : null,
            });
        }
        catch (BadImageFormatException)
        {
            throw new McpException($"Not a .NET assembly: {path}");
        }
    }

    private static ModuleDefinition Emit(Compilation compilation)
    {
        var (pe, pdb) = (new MemoryStream(), new MemoryStream());
        var result = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!result.Success)
        {
            var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Take(5);
            throw new McpException($"{compilation.AssemblyName} does not compile: {string.Join("; ", errors)}");
        }

        pe.Position = pdb.Position = 0;
        return ModuleDefinition.ReadModule(pe, new ReaderParameters { ReadSymbols = true, SymbolStream = pdb });
    }

    private sealed record CachedFile(DateTime Timestamp, ModuleDefinition Module)
    {
        public long LastUsed { get; set; }
    }
}
