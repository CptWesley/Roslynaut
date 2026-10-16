using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

/// <summary>
/// Inspects .NET processes with ClrMD: a running one from a snapshot, so it only pauses while the snapshot is taken, or a dump file.
/// </summary>
internal static class ProcessInspection
{
    public static IEnumerable<ProcessEntry> List() =>
        DiagnosticsClient.GetPublishedProcesses().Where(id => id != Environment.ProcessId).Select(Describe).OfType<ProcessEntry>().ToList();

    public static IReadOnlyList<ThreadEntry> Threads(int? processId, string? dumpPath, int maxFrames, PdbService pdbs)
    {
        using var target = Open(processId, dumpPath);
        using var runtime = RuntimeOf(target);
        return [.. runtime.Threads.Where(thread => thread.IsAlive).Select(thread => new ThreadEntry(
            thread.ManagedThreadId,
            thread.OSThreadId,
            thread.CurrentException is { } exception ? Describe(exception) : null,
            [.. thread.EnumerateStackTrace().Where(frame => frame.Kind == ClrStackFrameKind.ManagedMethod).Take(maxFrames).Select(frame => Describe(frame, pdbs))]))];
    }

    /// <summary>
    /// A frame's method, and where in source it is if a PDB is at hand without downloading: the file if on disk, else its SourceLink URL,
    /// else the path the PDB records.
    /// </summary>
    private static string Describe(ClrStackFrame frame, PdbService pdbs)
    {
        var signature = frame.Method?.Signature ?? frame.FrameName ?? "?";
        if (frame.Method is not { Type.Module: { IsDynamic: false, Name: { } file } } method || !File.Exists(file))
            return signature;

        try
        {
            // Frames return to the instruction after their call, which can start the next line.
            if (pdbs.FindPdb(file) is not { } pdb || pdb.LineOf(method.MetadataToken, method.GetILOffset(frame.InstructionPointer - 1)) is not (var document, var line))
                return signature;

            var name = pdb.NameOf(document);
            return $"{signature} in {(File.Exists(name) ? name : pdb.UrlOf(name) ?? name)}:line {line}";
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or McpException)
        {
            return signature;
        }
    }

    public static HeapSummary Heap(int? processId, string? dumpPath, Regex? type, int top)
    {
        using var target = Open(processId, dumpPath);
        using var runtime = RuntimeOf(target);
        var heap = runtime.Heap;
        if (!heap.CanWalkHeap)
            throw new McpException(dumpPath is null
                ? "The heap cannot be walked right now, for instance during a garbage collection; try again."
                : "The heap cannot be walked in this dump; take one with the heap, such as with dotnet-dump collect --type Heap.");

        var types = new Dictionary<string, (long Count, ulong Size)>();
        var (objects, size) = (0L, 0UL);
        foreach (var item in heap.EnumerateObjects())
        {
            if (!item.IsValid || item.Type?.Name is not { } name)
                continue;

            objects++;
            size += item.Size;
            if (type is null || type.IsMatch(name))
                types[name] = types.TryGetValue(name, out var stats) ? (stats.Count + 1, stats.Size + item.Size) : (1, item.Size);
        }

        return new(objects, size, [.. types.OrderByDescending(entry => entry.Value.Size).Take(top).Select(entry => new TypeStats(entry.Key, entry.Value.Count, entry.Value.Size))]);
    }

    private static DataTarget Open(int? processId, string? dumpPath)
    {
        try
        {
            return (processId, dumpPath) switch
            {
                ({ } id, null) => DataTarget.CreateSnapshotAndAttach(id),
                (null, { } path) => DataTarget.LoadDump(Path.GetFullPath(path)),
                _ => throw new McpException("Pass either a processId or a dumpPath."),
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or ClrDiagnosticsException or UnauthorizedAccessException
            or IOException or InvalidDataException or NotSupportedException)
        {
            throw new McpException($"Cannot inspect {(dumpPath is null ? $"process {processId}" : $"dump {dumpPath}")}: {e.Message}");
        }
    }

    private static ClrRuntime RuntimeOf(DataTarget target)
    {
        if (target.ClrVersions.FirstOrDefault() is not { } clr)
            throw new McpException("The process does not run .NET.");

        try
        {
            return clr.CreateRuntime();
        }
        catch (Exception e) when (e is ClrDiagnosticsException or IOException or InvalidOperationException or NotSupportedException)
        {
            // A dump from another machine needs the data access library of its runtime version.
            throw new McpException($"Cannot read .NET runtime {clr.Version}: {e.Message}");
        }
    }

    private static ProcessEntry? Describe(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new(processId, process.ProcessName, Protected<DateTime?>(() => process.StartTime), Protected(() => process.MainModule?.FileName));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // Exited meanwhile.
            return null;
        }

        // Processes of other users, or elevated ones, deny access to these.
        static T? Protected<T>(Func<T> get)
        {
            try
            {
                return get();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return default;
            }
        }
    }

    private static string Describe(ClrException exception) =>
        $"{exception.Type?.Name}: {exception.Message}" + (exception.Inner is { } inner ? $" ---> {Describe(inner)}" : "");

    public sealed record ProcessEntry(int Id, string Name, DateTime? StartTime, string? Path);

    /// <param name="Exception">The exception the thread is handling, with its inner exceptions.</param>
    /// <param name="Stack">The managed frames, innermost first; with "in file:line n" where the module's PDB is at hand.</param>
    public sealed record ThreadEntry(int ManagedThreadId, uint OSThreadId, string? Exception, IReadOnlyList<string> Stack);

    public sealed record HeapSummary(long Objects, ulong Size, IReadOnlyList<TypeStats> Types);

    public sealed record TypeStats(string Type, long Count, ulong Size);
}

internal static class ProcessDefaults
{
    public const string ProcessId = "The id of a running process, e.g. from dotnet_processes; or else pass dumpPath.";
    public const string DumpPath = "The path of a dump file to read instead of a running process, such as one from dotnet-dump collect or a crash.";
}

public sealed class DotnetProcessesTool : IMcpTool
{
    public Delegate Handler => List;

    [McpServerTool(Name = "dotnet_processes", ReadOnly = true)]
    [Description("Lists the running .NET processes, to inspect with process_threads or process_heap: their id, name, start time and executable.")]
    private static IEnumerable<ProcessInspection.ProcessEntry> List() => ProcessInspection.List();
}

public sealed class ProcessThreadsTool(PdbService pdbs) : IMcpTool
{
    public Delegate Handler => GetThreadsAsync;

    [McpServerTool(Name = "process_threads", ReadOnly = true)]
    [Description("Snapshots a running .NET process and returns its managed threads, each with its call stack and the exception it is handling, "
        + "if any: for hangs, deadlocks and errors in progress. Frames give their source file and line where a PDB is at hand, also one that "
        + "assembly_sources or symbol_source downloaded earlier. The process pauses only while the snapshot is taken. Reads a dump file instead, "
        + "such as one of a crash or a hang.")]
    private Task<IReadOnlyList<ProcessInspection.ThreadEntry>> GetThreadsAsync(
        [Description(ProcessDefaults.ProcessId)] int? processId = null,
        [Description(ProcessDefaults.DumpPath)] string? dumpPath = null,
        [Description("How many frames to return per thread, from the innermost.")] int maxFrames = 30) =>
        Task.Run(() => ProcessInspection.Threads(processId, dumpPath, maxFrames, pdbs));
}

public sealed class ProcessHeapTool : IMcpTool
{
    public Delegate Handler => GetHeapAsync;

    [McpServerTool(Name = "process_heap", ReadOnly = true)]
    [Description("Snapshots a running .NET process and summarizes its managed heap: the object count and size, and the types that take the most "
        + "memory with their object counts and sizes: for memory leaks and growth. Walking a big heap takes a while. Reads a dump file instead, "
        + "if it has the heap.")]
    private static Task<ProcessInspection.HeapSummary> GetHeapAsync(
        [Description(ProcessDefaults.ProcessId)] int? processId = null,
        [Description(ProcessDefaults.DumpPath)] string? dumpPath = null,
        [Description("Optional regex for the type names to count, e.g. '^MyApp\\.' or 'Cache'.")] string? type = null,
        [Description("How many types to return, largest first.")] int top = 50)
    {
        Regex? regex;
        try
        {
            regex = type is null ? null : new Regex(type, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException e)
        {
            throw new McpException($"Invalid type: {e.Message}");
        }

        return Task.Run(() => ProcessInspection.Heap(processId, dumpPath, regex, top));
    }
}
