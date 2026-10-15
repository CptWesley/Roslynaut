using System.Collections.Concurrent;
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
public sealed class AssemblyLoader
{
    // ponytail: keeps every file it read until restart; evict the least recently used modules if memory matters.
    private readonly ConcurrentDictionary<string, (DateTime Timestamp, ModuleDefinition Module)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<Compilation, Task<ModuleDefinition>> _compilations = new();

    public ModuleDefinition Read(string assemblyPath)
    {
        var path = Path.GetFullPath(assemblyPath);
        if (!File.Exists(path))
            throw new McpException($"Assembly not found: {path}");

        var timestamp = File.GetLastWriteTimeUtc(path);
        return _files.AddOrUpdate(
            path,
            key => (timestamp, ReadFile(key)),
            (key, cached) => cached.Timestamp == timestamp ? cached : (timestamp, ReadFile(key))).Module;
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
}
