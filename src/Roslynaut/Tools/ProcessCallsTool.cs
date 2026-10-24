using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Roslynaut.Tools;

/// <summary>
/// Counts calls of methods in a program it runs: it instruments copies of the libraries that declare them, so each method calls
/// Roslynaut.CallCounter first thing, and starts the program with that as its startup hook, which loads the copies in place of the build's.
/// </summary>
internal static class CallCounting
{
    private const string CounterAssembly = "Roslynaut.CallCounter";
    private const int OutputLines = 20;

    private static readonly string HookPath = Path.Combine(AppContext.BaseDirectory, $"{CounterAssembly}.dll");

    public static async Task<CallCounts> CountAsync(Solution solution, string program, IReadOnlyList<string> methods, string? per,
        IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan duration, CancellationToken cancellationToken)
    {
        program = Path.GetFullPath(program);
        if (Path.GetExtension(program).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            program = Path.ChangeExtension(program, ".dll");
        var (deps, runtimeConfig) = (Path.ChangeExtension(program, ".deps.json"), Path.ChangeExtension(program, ".runtimeconfig.json"));
        if (!File.Exists(program) || !File.Exists(deps) || !File.Exists(runtimeConfig))
            throw new McpException($"{program} is not a program built for .NET: it needs a .deps.json and a .runtimeconfig.json beside it. Build it first.");

        // Documentation IDs by assembly, which name members the same way in Roslyn and Cecil; the marker's last.
        var ids = new List<(string Assembly, string Id, bool Marker)>();
        foreach (var (path, marker) in methods.Select(path => (path, false)).Concat(per is null ? [] : [(per, true)]))
        {
            var found = (await SymbolTree.ResolveAsync(solution, path, cancellationToken)).SelectMany(Countable).ToList();
            ids.AddRange(found.Select(method => (method.ContainingAssembly.Name, method.GetDocumentationCommentId()!, marker)));
            if (found.Count == 0)
                throw new McpException($"'{path}' has no methods to count: pick a method, property, event or type.");
        }

        var directory = Directory.CreateTempSubdirectory("roslynaut-calls-").FullName;
        // The runtime finds a program's own files beside its deps.json, so the one without the counted libraries goes there too, for a while.
        var countingDeps = Path.Combine(Path.GetDirectoryName(program)!, $"{Path.GetFileNameWithoutExtension(program)}.roslynaut-{Guid.NewGuid():N}.deps.json");
        try
        {
            var programName = System.Reflection.AssemblyName.GetAssemblyName(program).Name;
            var names = new List<string>();
            var markers = new HashSet<int>();
            foreach (var assembly in ids.GroupBy(id => id.Assembly))
            {
                var file = Path.Combine(Path.GetDirectoryName(program)!, $"{assembly.Key}.dll");
                if (assembly.Key == programName)
                    throw new McpException($"{assembly.Key} is the program itself; only methods of the libraries it uses can be counted.");
                if (!File.Exists(file))
                    throw new McpException($"{assembly.Key}.dll is not in the build output of {Path.GetFileName(program)}: count methods of the libraries it uses.");

                Instrument(file, Path.Combine(directory, $"{assembly.Key}.dll"), assembly.ToList(), names, markers);
            }

            File.WriteAllText(countingDeps, WithoutAssemblies(File.ReadAllText(deps), ids.Select(id => $"{id.Assembly}.dll")));
            var counts = Path.Combine(directory, "counts.txt");
            var (exitCode, output) = await RunAsync(program, countingDeps, runtimeConfig, arguments, workingDirectory, new()
            {
                ["DOTNET_STARTUP_HOOKS"] = HookPath,
                ["ROSLYNAUT_CALLS_DIRECTORY"] = directory,
                ["ROSLYNAUT_CALLS_FILE"] = counts,
                ["ROSLYNAUT_CALLS_METHODS"] = names.Count.ToString(CultureInfo.InvariantCulture),
            }, duration, cancellationToken);

            if (!File.Exists(counts))
                throw new McpException($"The program counted nothing; it {(exitCode is null ? "ran" : $"exited with {exitCode}")}, ending with:\n{string.Join('\n', output)}");
            return CountsOf(File.ReadAllLines(counts), names, markers, exitCode, exitCode is null ? null : output);
        }
        finally
        {
            File.Delete(countingDeps);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The program may still hold its files for a moment after it is stopped.
            }
        }
    }

    /// <summary>The methods with code of a symbol: a method, a property's or event's accessors, or all methods of a type and its nested types.</summary>
    private static IEnumerable<IMethodSymbol> Countable(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { IsAbstract: false, IsExtern: false, MethodKind: not (MethodKind.LocalFunction or MethodKind.AnonymousFunction) } method =>
            [method.AssociatedExtensionImplementation ?? method],
        IPropertySymbol property => new[] { property.GetMethod, property.SetMethod }.OfType<IMethodSymbol>().SelectMany(Countable),
        IEventSymbol @event => new[] { @event.AddMethod, @event.RemoveMethod }.OfType<IMethodSymbol>().SelectMany(Countable),
        INamedTypeSymbol type => type.GetMembers().Where(member => member is not IPropertySymbol and not IEventSymbol).SelectMany(Countable),
        _ => [],
    };

    /// <summary>Writes a copy of an assembly whose methods call Counter.Hit with their index in <paramref name="names"/> first thing.</summary>
    private static void Instrument(string source, string target, List<(string Assembly, string Id, bool Marker)> ids, List<string> names, HashSet<int> markers)
    {
        using var module = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(source)));
        var counter = new AssemblyNameReference(CounterAssembly, new Version(0, 0, 0, 0));
        module.AssemblyReferences.Add(counter);
        var hit = new MethodReference("Hit", module.TypeSystem.Void, new TypeReference(CounterAssembly, "Counter", module, counter)) { Parameters = { new(module.TypeSystem.Int32) } };

        var marked = ids.Where(id => id.Marker).Select(id => id.Id).ToHashSet();
        List<IMemberDefinition> members;
        try
        {
            members = IlDisassembler.FindMembers(module, [.. ids.Select(id => id.Id).Distinct()]);
        }
        catch (McpException e)
        {
            throw new McpException($"{e.Message} The build is older than the source: build again.");
        }

        foreach (var method in members.OfType<MethodDefinition>().Where(method => method.HasBody))
        {
            // Before the first instruction, so branches back to it and exception handlers that start there leave the call out.
            var (il, first) = (method.Body.GetILProcessor(), method.Body.Instructions[0]);
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4, names.Count));
            il.InsertBefore(first, il.Create(OpCodes.Call, hit));
            if (marked.Contains(DocCommentId.GetDocCommentId(method)))
                markers.Add(names.Count);
            names.Add(method.FullName);
        }

        module.Write(target);
    }

    /// <summary>A deps.json without the files of these assemblies, so that the runtime asks the startup hook for them.</summary>
    private static string WithoutAssemblies(string deps, IEnumerable<string> files)
    {
        var left = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var json = JsonNode.Parse(deps)!;
        foreach (var (_, target) in json["targets"]?.AsObject() ?? [])
        {
            foreach (var (_, library) in target?.AsObject() ?? [])
            {
                if (library?["runtime"] is not JsonObject runtime)
                    continue;

                foreach (var asset in runtime.Select(asset => asset.Key).Where(asset => left.Contains(Path.GetFileName(asset))).ToList())
                {
                    runtime.Remove(asset);
                    left.Remove(Path.GetFileName(asset));
                }
            }
        }

        return left.Count == 0 ? json.ToJsonString() : throw new McpException($"The program's deps.json does not list {string.Join(", ", left)}: count methods of the libraries it uses.");
    }

    /// <summary>Runs the program until it exits or the time is up, when it is stopped. Returns its exit code if it exited, and its last lines.</summary>
    private static async Task<(int? ExitCode, IReadOnlyList<string> Output)> RunAsync(string program, string deps, string runtimeConfig, IReadOnlyList<string> arguments,
        string? workingDirectory, Dictionary<string, string> environment, TimeSpan duration, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(DotnetCli.Executable)
        {
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(program)!,
            // Its input stays open, so a program that reads it waits rather than sees it end.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in (string[])["exec", "--depsfile", deps, "--runtimeconfig", runtimeConfig, program, .. arguments])
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment)
            start.Environment[name] = value;

        var output = new ConcurrentQueue<string>();
        void Keep(object sender, DataReceivedEventArgs line)
        {
            if (line.Data is null)
                return;

            output.Enqueue(line.Data);
            while (output.Count > OutputLines)
                output.TryDequeue(out _);
        }

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += Keep;
        process.ErrorDataReceived += Keep;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, [.. output]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, [.. output]);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>Reads what Roslynaut.CallCounter wrote: the seconds counted, then per thread its id, name and counts.</summary>
    private static CallCounts CountsOf(string[] lines, List<string> names, HashSet<int> markers, int? exitCode, IReadOnlyList<string>? output)
    {
        var seconds = double.Parse(lines[0], CultureInfo.InvariantCulture);
        var threads = lines.Skip(1).Select(line => line.Split('\t'))
            .Select(parts => (Id: int.Parse(parts[0], CultureInfo.InvariantCulture), Name: parts[1] is { Length: > 0 } name ? name : null,
                Counts: parts[2].Split(' ').Select(count => long.Parse(count, CultureInfo.InvariantCulture)).ToArray()))
            .ToList();
        var marks = markers.Count == 0 ? (long?)null : threads.Sum(thread => markers.Sum(marker => thread.Counts[marker]));
        return new(Math.Round(seconds, 2), exitCode, output, [.. names.Select((name, index) =>
        {
            var calls = threads.Sum(thread => thread.Counts[index]);
            return new MethodCalls(name, calls, Math.Round(calls / seconds, 1), marks > 0 ? Math.Round((double)calls / marks.Value, 3) : null,
                [.. threads.Where(thread => thread.Counts[index] > 0).OrderByDescending(thread => thread.Counts[index]).Select(thread => new ThreadCalls(thread.Id, thread.Name, thread.Counts[index]))]);
        }).OrderByDescending(method => method.Calls)]);
    }
}

/// <param name="Seconds">How long the program ran while counted, from its start.</param>
/// <param name="ExitCode">If the program exited by itself in time, its exit code; it was stopped otherwise.</param>
/// <param name="Output">If it exited by itself, the last lines it wrote.</param>
/// <param name="Methods">The methods counted, most called first.</param>
public sealed record CallCounts(double Seconds, int? ExitCode, IReadOnlyList<string>? Output, IReadOnlyList<MethodCalls> Methods);

/// <param name="Method">Cecil's full name of the method, as symbol_il gives it.</param>
/// <param name="PerMarker">With per: how many calls per call of that method, such as per frame.</param>
/// <param name="Threads">The threads that called it, most calls first.</param>
public sealed record MethodCalls(string Method, long Calls, double PerSecond, double? PerMarker, IReadOnlyList<ThreadCalls> Threads);

/// <param name="Thread">The managed thread id.</param>
public sealed record ThreadCalls(int Thread, string? Name, long Calls);

public sealed class ProcessCallsTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => CountAsync;

    [McpServerTool(Name = "process_calls", Destructive = true, OpenWorld = true)]
    [Description("Runs a built program for a while and counts how often methods of the libraries it uses are called, per thread: in all, per "
        + "second, and per call of a marker method, such as one that runs a frame or a game day. Where process_cpu estimates time, this counts "
        + "calls exactly; each counted call costs a few nanoseconds more. It runs copies of the libraries' build output with a counter added "
        + "to each method, so build first; the build stays as it is, but for a deps.json beside the program while it runs. Methods of the "
        + "program's own assembly cannot be counted.")]
    private async Task<CallCounts> CountAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The program's .dll in its build output, or its .exe beside it, e.g. 'src/Game/bin/Release/net10.0/Game.dll'.")] string program,
        [Description("The methods to count, by symbol path: a property counts its accessors, a type all its methods. " + ToolDefaults.SymbolPath)] string[] methods,
        [Description("Optional: a method to count the others per call of, by symbol path, such as the one that runs a frame.")] string? per = null,
        [Description("The program's command-line arguments.")] string[]? arguments = null,
        [Description("The directory to run the program in; by default the program's own.")] string? workingDirectory = null,
        [Description("How long to run the program for; it is stopped then, unless it exited before.")] int seconds = 10,
        CancellationToken cancellationToken = default) =>
        await CallCounting.CountAsync((await workspace.GetAsync(projectPath)).Solution, program, methods, per, arguments ?? [], workingDirectory,
            TimeSpan.FromSeconds(seconds), cancellationToken);
}
