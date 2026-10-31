using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

/// <summary>
/// Inspects .NET processes with ClrMD: a running one from a snapshot, so it only pauses while the snapshot is taken, or a dump file.
/// </summary>
public static partial class ProcessInspection
{
    public static IEnumerable<ProcessEntry> List() =>
        DiagnosticsClient.GetPublishedProcesses().Where(id => id != Environment.ProcessId).Select(Describe).OfType<ProcessEntry>().ToList();

    /// <param name="lockObjects">Also walk the heap for the System.Threading.Lock objects threads hold, which keep their owner in a field.</param>
    public static IReadOnlyList<ThreadEntry> Threads(int? processId, string? dumpPath, int maxFrames, PdbService pdbs, bool lockObjects = false)
    {
        using var target = Open(processId, dumpPath);
        using var runtime = RuntimeOf(target);

        // ponytail: Monitor locks with a sync block only, which every contended one has; uncontended ones live in the object header.
        // process_objects shows the owner of a given object's Monitor lock either way. A dump without the heap still has the threads.
        ClrHeap? heap;
        try
        {
            heap = HeapOf(runtime);
        }
        catch (McpException)
        {
            heap = null;
        }

        var monitors = (heap?.EnumerateSyncBlocks() ?? []).Where(block => block.IsMonitorHeld).ToLookup(
            block => runtime.Threads.FirstOrDefault(thread => thread.Address == block.HoldingThreadAddress)?.ManagedThreadId,
            block => Describe(heap!.GetObject(block.Object)) + (block.WaitingThreadCount > 0 ? $", {block.WaitingThreadCount} waiting" : ""));

        // A Lock records the operating system id of the thread that holds it.
        var locks = (lockObjects && heap is { CanWalkHeap: true } ? heap.EnumerateObjects() : [])
            .Where(item => item.IsValid && item.Type?.Name == "System.Threading.Lock" && item.Type.GetFieldByName("_owningThreadId") is not null)
            .Select(item => (Owner: item.ReadField<uint>("_owningThreadId"), Lock: Describe(item)))
            .Where(item => item.Owner != 0)
            .ToLookup(item => item.Owner, item => item.Lock);

        return [.. runtime.Threads.Where(thread => thread.IsAlive).Select(thread => new ThreadEntry(
            thread.ManagedThreadId,
            thread.OSThreadId,
            thread.CurrentException is { } exception ? Describe(exception) : null,
            [.. thread.EnumerateStackTrace().Where(frame => frame.Kind == ClrStackFrameKind.ManagedMethod).Take(maxFrames).Select(frame => Describe(frame, pdbs))],
            [.. monitors[thread.ManagedThreadId], .. locks[thread.OSThreadId]]))];
    }

    /// <summary>
    /// The async methods that wait, as chains from the innermost awaiter out to the method that started it, as SOS's dumpasync finds them:
    /// each state machine box on the heap whose task has not completed, linked to the box its task's continuation resumes.
    /// </summary>
    public static IReadOnlyList<AsyncChain> AsyncChains(int? processId, string? dumpPath, Regex? method, int top, PdbService pdbs, CancellationToken cancellationToken)
    {
        using var target = Open(processId, dumpPath);
        using var runtime = RuntimeOf(target);
        var heap = HeapOf(runtime);
        RequireWalkable(heap, dumpPath);

        var boxes = new Dictionary<ulong, ClrObject>();
        foreach (var item in heap.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.IsValid && IsBox(item.Type) && (item.ReadField<int>("m_stateFlags") & TaskCompleted) == 0)
                boxes[item.Address] = item;
        }

        // A box's task resumes the boxes that await it; those that nothing awaits is outermost, those that await nothing innermost.
        var resumes = boxes.Values.ToDictionary(box => box.Address, box => Resumed(box.ReadObjectField("m_continuationObject"), depth: 0).Where(boxes.ContainsKey).Distinct().ToList());
        var awaited = resumes.SelectMany(entry => entry.Value).ToHashSet();
        var lines = new Dictionary<(string, int), IReadOnlyList<int>>();
        var chains = boxes.Values.Where(box => !awaited.Contains(box.Address)).Select(innermost =>
        {
            var frames = new List<AsyncFrame>();
            var seen = new HashSet<ulong>();
            for (ulong? address = innermost.Address; address is { } at && seen.Add(at); address = resumes[at].Count > 0 ? resumes[at][0] : null)
                frames.Add(FrameOf(boxes[at], pdbs, lines));
            return (Frames: frames, Awaiting: AwaitedBy(innermost), Task: innermost.Address);
        });

        return [.. chains.Where(chain => method is null || chain.Frames.Any(frame => method.IsMatch(frame.Method)))
            .GroupBy(chain => string.Join('\n', chain.Frames.Select(frame => $"{frame.Method}@{frame.State}")) + chain.Awaiting)
            .Select(group => new AsyncChain(group.First().Frames, group.First().Awaiting, group.Count(), Hex(group.First().Task)))
            .OrderByDescending(chain => chain.Count)
            .Take(top)];
    }

    // Task's m_stateFlags for RanToCompletion, Faulted and Canceled.
    private const int TaskCompleted = 0x1600000;

    private static bool IsBox(ClrType? type) => type?.Name is { } name && name.Contains("AsyncStateMachineBox<", StringComparison.Ordinal);

    /// <summary>
    /// The boxes a continuation resumes: a box itself, a delegate whose target is one, a list of continuations, or a continuation object or
    /// task (as Task.WhenAll's) that leads on to them.
    /// </summary>
    private static IEnumerable<ulong> Resumed(ClrObject continuation, int depth)
    {
        if (continuation.IsNull || !continuation.IsValid || depth > 8)
            yield break;
        if (IsBox(continuation.Type))
        {
            yield return continuation.Address;
            yield break;
        }

        if (continuation.IsArray)
        {
            var array = continuation.AsArray();
            if (array.Type.ComponentType?.IsObjectReference == true)
            {
                for (var i = 0; i < array.Length; i++)
                    foreach (var resumed in Resumed(array.GetObjectValue(i), depth + 1))
                        yield return resumed;
            }

            yield break;
        }

        foreach (var field in (string[])["_target", "m_action", "m_task", "_items", "m_continuationObject", "_stateMachine"])
        {
            if (continuation.Type?.GetFieldByName(field) is { IsObjectReference: true })
                foreach (var resumed in Resumed(continuation.ReadObjectField(field), depth + 1))
                    yield return resumed;
        }
    }

    /// <summary>The method of a box's state machine, its state, and where in source it waits, where the module's PDB is at hand.</summary>
    private static AsyncFrame FrameOf(ClrObject box, PdbService pdbs, Dictionary<(string, int), IReadOnlyList<int>> awaits)
    {
        var field = box.Type!.GetFieldByName("StateMachine");
        var machineType = field?.Type;
        int? state = null;
        if (field is { IsObjectReference: true } && box.ReadObjectField("StateMachine") is { IsNull: false } machine && machine.Type?.GetFieldByName("<>1__state") is not null)
            (state, machineType) = (machine.ReadField<int>("<>1__state"), machine.Type);
        else if (field is { IsValueType: true } && box.ReadValueTypeField("StateMachine") is var value && value.Type?.GetFieldByName("<>1__state") is not null)
            state = value.ReadField<int>("<>1__state");

        var name = machineType?.Name ?? box.Type.Name ?? "?";
        var method = ResolveStackTraceTool.PathOf(name.Replace('+', '.') + ".MoveNext()").Path;
        return new(method, state ?? -1, state is { } waiting and >= 0 ? AwaitLocation(machineType, waiting, pdbs, awaits) : null);
    }

    /// <summary>The file and line of the await a state machine waits at in its state, from its PDB; null without one.</summary>
    private static string? AwaitLocation(ClrType? machine, int state, PdbService pdbs, Dictionary<(string, int), IReadOnlyList<int>> awaits)
    {
        if (machine?.Module is not { IsDynamic: false, Name: { } file } || !File.Exists(file) || machine.Methods.FirstOrDefault(method => method.Name == "MoveNext") is not { } moveNext)
            return null;

        try
        {
            if (pdbs.FindPdb(file) is not { } pdb)
                return null;
            if (!awaits.TryGetValue((file, moveNext.MetadataToken), out var offsets))
                awaits[(file, moveNext.MetadataToken)] = offsets = pdb.AwaitOffsets(moveNext.MetadataToken);
            return state < offsets.Count && pdb.LineOf(moveNext.MetadataToken, offsets[state]) is var (document, line) ? $"{pdb.NameOf(document)}:line {line}" : null;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or McpException)
        {
            return null;
        }
    }

    /// <summary>
    /// What an innermost state machine waits for: the object its awaiter holds, such as a task of a SemaphoreSlim, a TaskCompletionSource or
    /// Task.Delay, or the source of a ValueTask. A completed await clears its awaiter, so the one that holds something is the current one.
    /// </summary>
    private static string? AwaitedBy(ClrObject box)
    {
        var field = box.Type!.GetFieldByName("StateMachine");
        if (field is { IsObjectReference: true } && box.ReadObjectField("StateMachine") is { IsNull: false } machine)
            return machine.Type!.Fields.Where(awaiter => awaiter.Name?.StartsWith("<>u__", StringComparison.Ordinal) == true)
                .Select(awaiter => HeldBy(awaiter, awaiter.IsObjectReference ? machine.ReadObjectField(awaiter.Name!) : default, awaiter.IsValueType ? machine.ReadValueTypeField(awaiter.Name!) : null, 0))
                .FirstOrDefault(held => held is not null);
        if (field is not { IsValueType: true } || box.ReadValueTypeField("StateMachine") is not { Type: { } type } value)
            return null;

        return type.Fields.Where(awaiter => awaiter.Name?.StartsWith("<>u__", StringComparison.Ordinal) == true)
            .Select(awaiter => HeldBy(awaiter, awaiter.IsObjectReference ? value.ReadObjectField(awaiter.Name!) : default, awaiter.IsValueType ? value.ReadValueTypeField(awaiter.Name!) : null, 0))
            .FirstOrDefault(held => held is not null);

        // The first object an awaiter holds, through the structs it nests (ConfiguredTaskAwaiter, ValueTaskAwaiter, ValueTask).
        static string? HeldBy(ClrInstanceField awaiter, ClrObject reference, ClrValueType? nested, int depth)
        {
            if (awaiter.IsObjectReference)
                return reference is { IsNull: false, IsValid: true } ? Describe(reference) : null;
            if (nested is not { Type: { } type } value || depth > 3)
                return null;

            return type.Fields.Select(inner => HeldBy(inner, inner.IsObjectReference ? value.ReadObjectField(inner.Name!) : default,
                inner.IsValueType ? value.ReadValueTypeField(inner.Name!) : null, depth + 1)).FirstOrDefault(held => held is not null);
        }
    }

    /// <summary>Writes a dump of a running process: Heap has what the process tools read; Full also the code, which finding locals' roots needs.</summary>
    public static async Task<DumpFile> WriteDumpAsync(int processId, DumpType type, string? path, CancellationToken cancellationToken)
    {
        var name = Describe(processId)?.Name ?? "process";
        var file = Path.GetFullPath(path ?? Path.Combine(Path.GetTempPath(), "Roslynaut", "Dumps", $"{name}_{processId}_{DateTime.Now:yyyyMMdd_HHmmss}.dmp"));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            await new DiagnosticsClient(processId).WriteDumpAsync(type, file, logDumpGeneration: false, cancellationToken);
        }
        catch (Exception e) when (e is ServerNotAvailableException or UnsupportedCommandException or ServerErrorException or ArgumentException)
        {
            throw new McpException($"Cannot dump process {processId}; is it a running .NET process? {e.Message}");
        }

        return new(file, new FileInfo(file).Length);
    }

    /// <summary>
    /// What started a process and what it loaded: its command line, environment (values of names that look secret masked) and runtime from its
    /// diagnostics port, and its assemblies from a snapshot; or, of a dump, the runtime and assemblies.
    /// </summary>
    public static ProcessEntry Details(int? processId, string? dumpPath, Regex? module)
    {
        using var target = Open(processId, dumpPath);
        using var runtime = RuntimeOf(target);
        var modules = runtime.AppDomains.SelectMany(domain => domain.Modules)
            .Where(item => !item.IsDynamic && item.Name is { Length: > 0 } && (module is null || module.IsMatch(item.Name)))
            .DistinctBy(item => item.Name)
            .Select(item => new ModuleEntry(Path.GetFileNameWithoutExtension(item.Name!), VersionOf(item), item.Name!, item.Layout == ModuleLayout.Flat ? "Flat" : null))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var entry = processId is { } id ? Describe(id) ?? new(id, "?", null, null) : new(0, Path.GetFileName(dumpPath!), null, dumpPath);
        if (processId is not { } running)
            return entry with { Runtime = RuntimeVersionOf(runtime.ClrInfo), Modules = modules };

        var client = new DiagnosticsClient(running);
        IReadOnlyDictionary<string, string>? environment = null;
        try
        {
            environment = new SortedDictionary<string, string>(client.GetProcessEnvironment().ToDictionary(variable => variable.Key,
                variable => Secret().IsMatch(variable.Key) ? "***" : variable.Value), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ServerNotAvailableException or UnsupportedCommandException or ServerErrorException)
        {
            // An old runtime, or one that went away.
        }

        return entry with { Runtime = RuntimeVersionOf(runtime.ClrInfo), CommandLine = CommandLineOf(running), Environment = environment, Modules = modules };
    }

    /// <summary>
    /// An assembly's version: from the display name the runtime reports, "Name, Version=1.2.3.4, ...", or else from the file, if it is on
    /// this machine.
    /// </summary>
    private static string? VersionOf(ClrModule module)
    {
        return Version(() => new System.Reflection.AssemblyName(module.AssemblyName!))
            ?? (File.Exists(module.Name) ? Version(() => System.Reflection.AssemblyName.GetAssemblyName(module.Name)) : null);

        static string? Version(Func<System.Reflection.AssemblyName> name)
        {
            try
            {
                return name().Version?.ToString();
            }
            catch (Exception e) when (e is ArgumentException or IOException or BadImageFormatException)
            {
                return null;
            }
        }
    }

    [GeneratedRegex("KEY|SECRET|TOKEN|PASSWORD|PASSWD|PWD|CONNECTIONSTRING|CREDENTIAL|SIGNATURE|AUTH", RegexOptions.IgnoreCase)]
    private static partial Regex Secret();

    /// <summary>A process's command line, through WMI on Windows and /proc elsewhere; null where it cannot be read.</summary>
    private static string? CommandLineOf(int processId)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return File.Exists($"/proc/{processId}/cmdline") ? File.ReadAllText($"/proc/{processId}/cmdline").Replace('\0', ' ').Trim() : null;

            // ponytail: through PowerShell's CIM, which takes a second; call WMI directly if that matters.
            var (exitCode, output) = DotnetCli.RunAsync("powershell", ["-NoProfile", "-NonInteractive", "-Command", $"(Get-CimInstance Win32_Process -Filter 'ProcessId={processId}').CommandLine"],
                TimeSpan.FromSeconds(30), CancellationToken.None).GetAwaiter().GetResult();
            return exitCode == 0 && output.Trim() is { Length: > 0 } line ? line : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static ClrHeap HeapOf(ClrRuntime runtime)
    {
        try
        {
            return runtime.Heap;
        }
        catch (InvalidDataException e)
        {
            // While the runtime starts, or in a dump without the heap.
            throw new McpException($"The heap cannot be read: {e.Message}");
        }
    }

    /// <summary>Objects with their fields: the one at an address, or else the first ones of the types whose names match.</summary>
    public static IReadOnlyList<ObjectEntry> Objects(int? processId, string? dumpPath, ulong? address, Regex? type, int top)
    {
        using var target = Open(processId, dumpPath);
        using var runtime = RuntimeOf(target);
        var heap = HeapOf(runtime);
        if (address is not null)
        {
            var found = heap.GetObject(address.Value);
            return found.IsValid ? [Inspect(found, runtime)] : throw new McpException($"There is no object at {Hex(address.Value)}.");
        }

        if (type is null)
            throw new McpException("Pass an address, or a type to find objects of.");
        RequireWalkable(heap, dumpPath);

        return [.. heap.EnumerateObjects().Where(item => item.IsValid && item.Type?.Name is { } name && type.IsMatch(name)).Take(top).Select(item => Inspect(item, runtime))];
    }

    /// <summary>The chains of references that keep an object alive, from a root such as a static field, a local or a handle, to the object.</summary>
    public static IReadOnlyList<RootPath> Roots(int? processId, string? dumpPath, ulong address, int top, CancellationToken cancellationToken)
    {
        using var target = Open(processId, dumpPath);
        using var runtime = RuntimeOf(target);
        var heap = HeapOf(runtime);
        if (!heap.GetObject(address).IsValid)
            throw new McpException($"There is no object at {Hex(address)}.");

        return [.. new GCRoot(heap, [address]).EnumerateRootPaths(cancellationToken).Take(top).Select(path => new RootPath(
            path.Root.RootKind.ToString(),
            path.Root is ClrStackRoot { StackFrame: { } frame } ? frame.Method?.Signature ?? frame.FrameName : null,
            [.. Chain(path.Path).Select(link => Describe(heap.GetObject(link)))]))];

        static IEnumerable<ulong> Chain(GCRoot.ChainLink? link)
        {
            for (; link is not null; link = link.Next)
                yield return link.Object;
        }
    }

    private static ObjectEntry Inspect(ClrObject item, ClrRuntime runtime)
    {
        var type = item.Type!;
        string? value = type.IsString ? Quote(item.AsString(MaxString)) : item.IsArray ? $"Length {item.AsArray().Length}" : null;
        var owner = item.SyncBlock is { IsMonitorHeld: true } block
            ? runtime.Threads.FirstOrDefault(thread => thread.Address == block.HoldingThreadAddress)?.ManagedThreadId
            : item.GetThinLock()?.Thread?.ManagedThreadId;
        return new(Hex(item.Address), type.Name ?? "?", item.Size, value, owner,
            [.. type.Fields.Select(field => new FieldValue(field.Name ?? "?", field.Type?.Name ?? field.ElementType.ToString(), ValueOf(item, field)))]);
    }

    private const int MaxString = 200;

    private static string ValueOf(ClrObject item, ClrInstanceField field)
    {
        try
        {
            return field.ElementType switch
            {
                ClrElementType.Boolean => item.ReadField<bool>(field).ToString(),
                ClrElementType.Char => Quote(item.ReadField<char>(field).ToString()),
                ClrElementType.Int8 => item.ReadField<sbyte>(field).ToString(),
                ClrElementType.UInt8 => item.ReadField<byte>(field).ToString(),
                ClrElementType.Int16 => item.ReadField<short>(field).ToString(),
                ClrElementType.UInt16 => item.ReadField<ushort>(field).ToString(),
                ClrElementType.Int32 => item.ReadField<int>(field).ToString(),
                ClrElementType.UInt32 => item.ReadField<uint>(field).ToString(),
                ClrElementType.Int64 => item.ReadField<long>(field).ToString(),
                ClrElementType.UInt64 => item.ReadField<ulong>(field).ToString(),
                ClrElementType.Float => item.ReadField<float>(field).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ClrElementType.Double => item.ReadField<double>(field).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ClrElementType.NativeInt or ClrElementType.Pointer or ClrElementType.FunctionPointer => Hex(item.ReadField<nuint>(field)),
                ClrElementType.NativeUInt => Hex(item.ReadField<nuint>(field)),
                _ when field.IsObjectReference => item.ReadObjectField(field.Name!) is { IsNull: false } reference ? Describe(reference) : "null",
                // ponytail: structs show their type only; read their fields with ClrValueType if agents need them.
                _ => $"{{{field.Type?.Name}}}",
            };
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
        {
            return "?";
        }
    }

    /// <summary>An object as "Type @0x1234", or a string's text.</summary>
    private static string Describe(ClrObject item) =>
        item.Type?.IsString == true ? $"{Quote(item.AsString(MaxString))} @{Hex(item.Address)}" : $"{item.Type?.Name ?? "?"} @{Hex(item.Address)}";

    private static string Quote(string? text) => text is null ? "null" : $"\"{text}\"";

    private static string Hex(ulong value) => $"0x{value:x}";

    /// <summary>An address as process_heap, process_objects or process_roots print it, with or without 0x.</summary>
    public static ulong ParseAddress(string address) =>
        ulong.TryParse(address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..] : address, System.Globalization.NumberStyles.HexNumber, null, out var value)
            ? value
            : throw new McpException($"'{address}' is not a hexadecimal address such as 0x1a2b3c.");

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

    /// <summary>The object count and size of every type on the heap.</summary>
    public static HeapSnapshot Snapshot(int? processId, string? dumpPath)
    {
        using var target = Open(processId, dumpPath);
        using var runtime = RuntimeOf(target);
        var heap = HeapOf(runtime);
        RequireWalkable(heap, dumpPath);

        var types = new Dictionary<string, (long Count, long Size)>();
        foreach (var item in heap.EnumerateObjects())
        {
            if (item.IsValid && item.Type?.Name is { } name)
                types[name] = types.TryGetValue(name, out var stats) ? (stats.Count + 1, stats.Size + (long)item.Size) : (1, (long)item.Size);
        }

        return new(types);
    }

    /// <summary>
    /// Fails unless the heap's objects can be enumerated. A dump without the heap, such as a minidump, still has the heap's segments, but not
    /// their memory, so it walks as if there were no objects at all.
    /// </summary>
    private static void RequireWalkable(ClrHeap heap, string? dumpPath)
    {
        if (dumpPath is null && !heap.CanWalkHeap)
            throw new McpException("The heap cannot be walked right now, for instance during a garbage collection; try again.");
        if (dumpPath is not null && (!heap.CanWalkHeap || !heap.EnumerateObjects().Any(item => item.IsValid)))
            throw new McpException("The heap cannot be walked in this dump; take one with the heap, such as with dotnet-dump collect --type Heap.");
    }

    /// <summary>The largest types of a snapshot; or, against an earlier one, the types that grew the most, with how much.</summary>
    public static HeapSummary Summarize(string id, HeapSnapshot snapshot, HeapSnapshot? baseline, Regex? type, int top)
    {
        var types = snapshot.Types.Keys.Concat(baseline?.Types.Keys ?? []).Distinct().Where(name => type is null || type.IsMatch(name)).Select(name =>
        {
            var (now, before) = (snapshot.Types.GetValueOrDefault(name), baseline?.Types.GetValueOrDefault(name));
            return new TypeStats(name, now.Count, now.Size, now.Count - before?.Count, now.Size - before?.Size);
        });
        return new(
            id,
            snapshot.Objects,
            snapshot.Size,
            snapshot.Objects - baseline?.Objects,
            snapshot.Size - baseline?.Size,
            [.. (baseline is null ? types.OrderByDescending(stats => stats.Size) : types.OrderByDescending(stats => stats.SizeChange)).Take(top)]);
    }

    public static DataTarget Open(int? processId, string? dumpPath)
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
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or ClrDiagnosticsException or UnauthorizedAccessException or DiagnosticsClientException
            or IOException or InvalidDataException or NotSupportedException)
        {
            throw new McpException($"Cannot inspect {(dumpPath is null ? $"process {processId}" : $"dump {dumpPath}")}: {e.Message}");
        }
    }

    // Linux's libcoreclr.so has no version resource, so ClrMD reads 0.0.0.0 there; a shared runtime's directory, such as
    // shared/Microsoft.NETCore.App/10.0.14, still names it.
    private static string RuntimeVersionOf(ClrInfo clr) =>
        clr.Version.Major == 0 && Path.GetFileName(Path.GetDirectoryName(clr.ModuleInfo.FileName)) is { } directory && Version.TryParse(directory.Split('-')[0], out _)
            ? directory
            : clr.Version.ToString();

    public static ClrRuntime RuntimeOf(DataTarget target)
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

    /// <param name="Runtime">With processId or dumpPath: the version of the .NET runtime.</param>
    /// <param name="CommandLine">With processId: how the process was started.</param>
    /// <param name="Environment">With processId: its environment variables; the values of names that look secret, such as *KEY* or *PASSWORD*, show as ***.</param>
    /// <param name="Modules">With processId or dumpPath: the assemblies it loaded.</param>
    public sealed record ProcessEntry(
        int Id, string Name, DateTime? StartTime, string? Path, string? Runtime = null, string? CommandLine = null,
        IReadOnlyDictionary<string, string>? Environment = null, IReadOnlyList<ModuleEntry>? Modules = null);

    /// <param name="Version">The assembly's file version, as the runtime reports it.</param>
    /// <param name="Layout">Flat for an assembly loaded from bytes rather than from its file.</param>

    public sealed record ModuleEntry(string Name, string? Version, string Path, string? Layout);

    /// <param name="Frames">The awaiting methods, innermost first: each awaits the one before it.</param>
    /// <param name="Awaiting">What the innermost method waits for: the object its awaiter holds, such as a SemaphoreSlim's task, a TaskCompletionSource's task or a Task.Delay.</param>
    /// <param name="Count">How many chains of this shape wait, as many requests can wait in the same place.</param>
    /// <param name="Task">The address of the innermost state machine's box, for process_objects or process_roots.</param>
    public sealed record AsyncChain(IReadOnlyList<AsyncFrame> Frames, string? Awaiting, int Count, string Task);

    /// <param name="Method">The async method, as a symbol path.</param>
    /// <param name="State">The state machine's state: from 0, the await it waits at, in order of the method's awaits; -1 while it runs.</param>
    /// <param name="Location">The file and line of that await, where the module's PDB is at hand.</param>
    public sealed record AsyncFrame(string Method, int State, string? Location);

    public sealed record DumpFile(string Path, long Size);

    /// <param name="Exception">The exception the thread is handling, with its inner exceptions.</param>
    /// <param name="Stack">The managed frames, innermost first; with "in file:line n" where the module's PDB is at hand.</param>
    /// <param name="HeldLocks">
    /// The objects whose Monitor lock (C# lock) the thread holds while others wait or have waited for it, with how many wait; with lockObjects,
    /// also the System.Threading.Lock objects it holds.
    /// </param>
    public sealed record ThreadEntry(int ManagedThreadId, uint OSThreadId, string? Exception, IReadOnlyList<string> Stack, IReadOnlyList<string> HeldLocks);

    /// <param name="Address">Pass to process_objects or process_roots.</param>
    /// <param name="Value">A string's text, or an array's length.</param>
    /// <param name="LockedBy">The managed id of the thread that holds the object's Monitor lock, if any.</param>
    public sealed record ObjectEntry(string Address, string Type, ulong Size, string? Value, int? LockedBy, IReadOnlyList<FieldValue> Fields);

    /// <param name="Value">A number, a string's text, "Type @0x1234" for a reference, or null.</param>
    public sealed record FieldValue(string Name, string Type, string Value);

    /// <param name="Kind">What roots the chain, such as Stack, StrongHandle, PinnedHandle or FinalizerQueue; static fields are held by handles.</param>
    /// <param name="Frame">For a Stack root, the method whose local holds the first object.</param>
    /// <param name="Chain">The objects from the root to the object asked about, each referencing the next.</param>
    public sealed record RootPath(string Kind, string? Frame, IReadOnlyList<string> Chain);

    public sealed record HeapSnapshot(IReadOnlyDictionary<string, (long Count, long Size)> Types)
    {
        public long Objects { get; } = Types.Values.Sum(stats => stats.Count);

        public long Size { get; } = Types.Values.Sum(stats => stats.Size);
    }

    /// <param name="Snapshot">Pass to process_heap as compareTo later, to see what grew since.</param>
    /// <param name="ObjectsChange">Against the snapshot compared to: how many objects more (or fewer).</param>
    /// <param name="SizeChange">Against the snapshot compared to: how many bytes more (or fewer).</param>
    public sealed record HeapSummary(string Snapshot, long Objects, long Size, long? ObjectsChange, long? SizeChange, IReadOnlyList<TypeStats> Types);

    public sealed record TypeStats(string Type, long Count, long Size, long? CountChange = null, long? SizeChange = null);
}

internal static class ProcessDefaults
{
    public const string ProcessId = "The id of a running process, e.g. from dotnet_processes; or else pass dumpPath.";
    public const string DumpPath = "The path of a dump file to read instead of a running process, such as one from dotnet-dump collect or a crash.";

    public static Regex? Pattern(string? pattern, string parameter)
    {
        try
        {
            return pattern is null ? null : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException e)
        {
            throw new McpException($"Invalid {parameter}: {e.Message}");
        }
    }
}

public sealed class DotnetProcessesTool : IMcpTool
{
    public Delegate Handler => List;

    [McpServerTool(Name = "dotnet_processes", ReadOnly = true)]
    [Description("Lists the running .NET processes, to inspect with process_threads or process_heap: their id, name, start time and executable. "
        + "With processId (or dumpPath), only that one, with how it was started and what it loaded: its command line, environment variables, "
        + "runtime version and assemblies with their versions and paths, for a FileLoadException or a version conflict, or to check that it "
        + "runs the build it should.")]
    private static Task<IEnumerable<ProcessInspection.ProcessEntry>> List(
        [Description("The id of one process to describe in detail, e.g. from this list.")] int? processId = null,
        [Description("Or else a dump file to describe: its runtime and assemblies.")] string? dumpPath = null,
        [Description("Optional regex for the paths of the assemblies to list, e.g. 'Newtonsoft|MyApp'.")] string? module = null) =>
        Task.Run(() => processId is null && dumpPath is null
            ? ProcessInspection.List()
            : [ProcessInspection.Details(processId, dumpPath, ProcessDefaults.Pattern(module, "module"))]);
}

public sealed class ProcessAsyncTool(PdbService pdbs) : IMcpTool
{
    public Delegate Handler => GetChainsAsync;

    [McpServerTool(Name = "process_async", ReadOnly = true)]
    [Description("Shows the async methods of a running .NET process or a dump that wait, as SOS's dumpasync does: chains of awaiting methods from "
        + "the innermost out to the one that started them, each with the await it waits at (a line, where the PDB is at hand), and what the "
        + "innermost waits for, such as a SemaphoreSlim, a TaskCompletionSource or a Task.Delay; chains of the same shape once, with their count. "
        + "For hangs in async code, where process_threads shows idle threads, since an awaiting method has no thread. Walks the heap, so it "
        + "takes a while on a big one.")]
    private Task<IReadOnlyList<ProcessInspection.AsyncChain>> GetChainsAsync(
        [Description(ProcessDefaults.ProcessId)] int? processId = null,
        [Description(ProcessDefaults.DumpPath)] string? dumpPath = null,
        [Description("Optional regex: only the chains with a method whose path matches, e.g. 'Orders\\.Submit'.")] string? method = null,
        [Description("How many chains to return, those that wait most often first.")] int top = 20,
        CancellationToken cancellationToken = default)
    {
        var regex = ProcessDefaults.Pattern(method, "method");
        return Task.Run(() => ProcessInspection.AsyncChains(processId, dumpPath, regex, top, pdbs, cancellationToken), cancellationToken);
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<DumpKind>))]
public enum DumpKind
{
    /// <summary>Threads and stacks only.</summary>
    Mini,

    /// <summary>With the managed heap, which process_heap, process_objects, process_roots and process_async need.</summary>
    Heap,

    /// <summary>The least that explains a crash, without data that could be private.</summary>
    Triage,

    /// <summary>All the process's memory, code included, which process_roots needs to find locals.</summary>
    Full,
}

public sealed class ProcessDumpTool : IMcpTool
{
    public Delegate Handler => WriteAsync;

    [McpServerTool(Name = "process_dump", Destructive = false, Idempotent = false)]
    [Description("Writes a dump of a running .NET process to a file, as dotnet-dump collect does, to read with process_threads, process_async, "
        + "process_heap, process_objects, process_roots and dotnet_processes after the process has moved on, exited or restarted, or to compare "
        + "two moments. The process pauses while it is written. Returns the file's path and size.")]
    private static Task<ProcessInspection.DumpFile> WriteAsync(
        [Description("The process id, e.g. from dotnet_processes.")] int processId,
        [Description("Mini (threads only), Heap (what the process tools read), Triage or Full (the whole memory, larger).")] DumpKind kind = DumpKind.Heap,
        [Description("Where to write it; by default a new file under the temporary directory's Roslynaut/Dumps.")] string? path = null,
        CancellationToken cancellationToken = default) =>
        ProcessInspection.WriteDumpAsync(processId, kind switch
        {
            DumpKind.Mini => DumpType.Normal,
            DumpKind.Triage => DumpType.Triage,
            DumpKind.Full => DumpType.Full,
            _ => DumpType.WithHeap,
        }, path, cancellationToken);
}

public sealed class ProcessThreadsTool(PdbService pdbs) : IMcpTool
{
    public Delegate Handler => GetThreadsAsync;

    [McpServerTool(Name = "process_threads", ReadOnly = true)]
    [Description("Snapshots a running .NET process and returns its managed threads, each with its call stack, the exception it is handling, "
        + "if any, and the locks it holds that other threads wait for: for hangs, deadlocks and errors in progress; an async method that waits has no thread, so process_async shows those. Frames give their source file and line where a PDB is at hand, also one that "
        + "assembly_sources or symbol_source downloaded earlier. The process pauses only while the snapshot is taken. Reads a dump file instead, "
        + "such as one of a crash or a hang.")]
    private Task<IReadOnlyList<ProcessInspection.ThreadEntry>> GetThreadsAsync(
        [Description(ProcessDefaults.ProcessId)] int? processId = null,
        [Description(ProcessDefaults.DumpPath)] string? dumpPath = null,
        [Description("How many frames to return per thread, from the innermost.")] int maxFrames = 30,
        [Description("Also find the System.Threading.Lock objects each thread holds, which takes a walk of the heap; Monitor locks (C# lock on an "
            + "object) show without it.")] bool lockObjects = false) =>
        Task.Run(() => ProcessInspection.Threads(processId, dumpPath, maxFrames, pdbs, lockObjects));
}

public sealed class ProcessHeapTool : IMcpTool
{
    private const int MaxSnapshots = 20;

    // ponytail: snapshots live in memory, the last 20; each holds a count and size per type, not the objects.
    private readonly ConcurrentDictionary<string, ProcessInspection.HeapSnapshot> _snapshots = new();
    private int _lastId;

    public Delegate Handler => GetHeapAsync;

    [McpServerTool(Name = "process_heap", ReadOnly = true)]
    [Description("Snapshots a running .NET process and summarizes its managed heap: the object count and size, and the types that take the most "
        + "memory with their object counts and sizes: for memory leaks and growth. With compareTo, the Snapshot of an earlier call, the types that "
        + "grew the most since, with how much, which points at a leak. Walking a big heap takes a while. Reads a dump file instead, if it has the heap.")]
    private Task<ProcessInspection.HeapSummary> GetHeapAsync(
        [Description(ProcessDefaults.ProcessId)] int? processId = null,
        [Description(ProcessDefaults.DumpPath)] string? dumpPath = null,
        [Description("Optional regex for the type names to show, e.g. '^MyApp\\.' or 'Cache'.")] string? type = null,
        [Description("How many types to return, largest first, or that grew most first with compareTo.")] int top = 50,
        [Description("The Snapshot of an earlier process_heap result to compare with.")] string? compareTo = null) =>
        Task.Run(() =>
        {
            var regex = ProcessDefaults.Pattern(type, "type");
            ProcessInspection.HeapSnapshot? baseline = null;
            if (compareTo is not null && !_snapshots.TryGetValue(compareTo, out baseline))
                throw new McpException($"There is no snapshot {compareTo}; only the last {MaxSnapshots} are kept, until the server restarts.");

            var snapshot = ProcessInspection.Snapshot(processId, dumpPath);
            var id = Interlocked.Increment(ref _lastId).ToString();
            _snapshots[id] = snapshot;
            _snapshots.TryRemove((int.Parse(id) - MaxSnapshots).ToString(), out _);
            return ProcessInspection.Summarize(id, snapshot, baseline, regex, top);
        });
}

public sealed class ProcessObjectsTool : IMcpTool
{
    public Delegate Handler => GetObjectsAsync;

    [McpServerTool(Name = "process_objects", ReadOnly = true)]
    [Description("Shows objects on the managed heap of a running .NET process or a dump, with their field values: the object at an address, or "
        + "else the first objects of a type, such as the ones process_heap counts. A field that references an object shows its address, to "
        + "follow with another call. Also says which thread holds the object's lock, if one does.")]
    private static Task<IReadOnlyList<ProcessInspection.ObjectEntry>> GetObjectsAsync(
        [Description(ProcessDefaults.ProcessId)] int? processId = null,
        [Description(ProcessDefaults.DumpPath)] string? dumpPath = null,
        [Description("The address of an object, such as 0x1f2e3d4c5b6a from a field or a root chain.")] string? address = null,
        [Description("Or else a regex for the type names of the objects to show, e.g. '^MyApp\\.OrderCache$'.")] string? type = null,
        [Description("How many objects of the type to show.")] int top = 10)
    {
        var (parsed, regex) = (address is null ? (ulong?)null : ProcessInspection.ParseAddress(address), ProcessDefaults.Pattern(type, "type"));
        return Task.Run(() => ProcessInspection.Objects(processId, dumpPath, parsed, regex, top));
    }
}

public sealed class ProcessRootsTool : IMcpTool
{
    public Delegate Handler => GetRootsAsync;

    [McpServerTool(Name = "process_roots", ReadOnly = true)]
    [Description("Explains why an object in a running .NET process or a dump stays alive, as gcroot does: the chains of references from roots, "
        + "such as locals, static fields and handles, to the object. For memory leaks, after process_heap and process_objects. Slow on big heaps. "
        + "Finding locals needs the code the JIT compiled, which only a full dump has: in other dumps, chains from locals can be missing.")]
    private static Task<IReadOnlyList<ProcessInspection.RootPath>> GetRootsAsync(
        [Description("The address of the object, e.g. from process_objects.")] string address,
        [Description(ProcessDefaults.ProcessId)] int? processId = null,
        [Description(ProcessDefaults.DumpPath)] string? dumpPath = null,
        [Description("How many chains to return.")] int top = 5,
        CancellationToken cancellationToken = default)
    {
        var parsed = ProcessInspection.ParseAddress(address);
        return Task.Run(() => ProcessInspection.Roots(processId, dumpPath, parsed, top, cancellationToken), cancellationToken);
    }
}
