using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

/// <summary>Inspects running .NET processes with ClrMD, from a snapshot, so a process only pauses while the snapshot is taken.</summary>
internal static class ProcessInspection
{
    public static IEnumerable<ProcessEntry> List() =>
        DiagnosticsClient.GetPublishedProcesses().Where(id => id != Environment.ProcessId).Select(Describe).OfType<ProcessEntry>().ToList();

    public static IReadOnlyList<ThreadEntry> Threads(int processId, int maxFrames, PdbService pdbs)
    {
        using var target = Attach(processId);
        using var runtime = RuntimeOf(target, processId);
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

    public static HeapSummary Heap(int processId, Regex? type, int top)
    {
        using var target = Attach(processId);
        using var runtime = RuntimeOf(target, processId);
        var heap = runtime.Heap;
        if (!heap.CanWalkHeap)
            throw new McpException("The heap cannot be walked right now, for instance during a garbage collection; try again.");

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

    private static DataTarget Attach(int processId)
    {
        try
        {
            return DataTarget.CreateSnapshotAndAttach(processId);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or ClrDiagnosticsException or UnauthorizedAccessException)
        {
            throw new McpException($"Cannot inspect process {processId}: {e.Message}");
        }
    }

    private static ClrRuntime RuntimeOf(DataTarget target, int processId) =>
        target.ClrVersions.FirstOrDefault()?.CreateRuntime() ?? throw new McpException($"Process {processId} does not run .NET.");

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
        + "assembly_sources or symbol_source downloaded earlier. The process pauses only while the snapshot is taken.")]
    private Task<IReadOnlyList<ProcessInspection.ThreadEntry>> GetThreadsAsync(
        [Description("The process id, e.g. from dotnet_processes.")] int processId,
        [Description("How many frames to return per thread, from the innermost.")] int maxFrames = 30) =>
        Task.Run(() => ProcessInspection.Threads(processId, maxFrames, pdbs));
}

public sealed class ProcessHeapTool : IMcpTool
{
    public Delegate Handler => GetHeapAsync;

    [McpServerTool(Name = "process_heap", ReadOnly = true)]
    [Description("Snapshots a running .NET process and summarizes its managed heap: the object count and size, and the types that take the most "
        + "memory with their object counts and sizes: for memory leaks and growth. Walking a big heap takes a while.")]
    private static Task<ProcessInspection.HeapSummary> GetHeapAsync(
        [Description("The process id, e.g. from dotnet_processes.")] int processId,
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

        return Task.Run(() => ProcessInspection.Heap(processId, regex, top));
    }
}
