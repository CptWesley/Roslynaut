using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using NuGet.Versioning;

namespace Roslynaut.Tools;

/// <param name="Method">Cecil's full name of the method, as in <see cref="MethodIL"/>.</param>
/// <param name="Asm">The listing of the JIT, after a line naming the runtime; or a comment saying why there is none.</param>
public sealed record MethodAsm(string Method, string Asm);

/// <summary>
/// Disassembles the native code the JIT generates, with the runtime's own DOTNET_JitDisasm. That setting is read when a process starts,
/// so a separate host process loads the assembly and JIT-compiles the methods, without running them.
/// </summary>
internal static class JitDisassembler
{
    public const string ResultDescription = "The code comes from the JIT of the .NET the assembly is built for (its newest installed patch; for a "
        + "framework assembly, its own framework), or else of the nearest installed .NET, fully optimized, for this machine's CPU; the listing names it. "
        + "Assemblies built for debugging compile unoptimized (MinOpts). Generic code is shown for reference-type arguments, as the code they all share "
        + "(System.__Canon). " + IlDisassembler.ResultDescription;

    private const string ListingStart = "; Assembly listing for method ";
    private const int CacheLine = 64;

    private static readonly string HostPath = Path.Combine(AppContext.BaseDirectory, "Roslynaut.JitHost.dll");
    private static readonly string FrameworkDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", ".."));

    /// <param name="options">Type arguments for generic code, and a workload to compile the methods with Dynamic PGO after.</param>
    public static List<MethodAsm> Disassemble(LoadedAssembly assembly, IReadOnlyCollection<string> names, JitOptions? options = null)
    {
        List<Method> methods;
        lock (assembly.Module)
        {
            methods = [.. IlDisassembler.FindMethods(assembly.Module, names).Select(method => new Method(
                method.MetadataToken.ToInt32(),
                method.FullName,
                // As the JIT names methods in its listings, without generic arguments.
                $"{method.DeclaringType.FullName.Replace('/', '+')}:{method.Name}",
                $"*{method.DeclaringType.Name}*:{method.Name}"))];
        }

        var listingFile = Path.GetTempFileName();
        try
        {
            var response = RunHost(assembly, methods, listingFile, options ?? new([], null), layout: null);

            // Each listing starts with the method's name and signature, then its tier, as in "Ns.Type:Method(int):int (Tier1)". Methods first
            // compile in the order given, so overloads, which share a key, get their first listings in that order; a workload compiles them
            // again at higher tiers, and the last listing is the code that runs.
            var listings = File.ReadAllText(listingFile).Split(ListingStart).Skip(1).Select(listing => ListingStart + listing.TrimEnd()).ToList();
            var byKey = listings.GroupBy(KeyOf).ToDictionary(group => group.Key, group => new Queue<string>(group.Select(NameOf).Distinct()));
            var last = listings.GroupBy(NameOf).ToDictionary(group => group.Key, group => group.Last());
            return [.. methods.Select(method => new MethodAsm(method.Name, response.Errors.GetValueOrDefault(method.Token) is { Length: > 0 } error
                ? $"; Not compiled: {error}"
                : byKey.GetValueOrDefault(method.Key)?.TryDequeue(out var name) == true
                    ? $"; {response.Runtime}\n{NotesOf(last[name])}{last[name]}"
                    : "; No listing: the JIT did not compile this method."))];

            string NotesOf(string listing) => options?.Workload is null ? "" : string.Concat(
                response.Workload is { } stopped ? $"; {stopped}\n" : "",
                NameOf(listing) is var name && listing.StartsWith($"{name} (Tier1", StringComparison.Ordinal) ? "" : "; The workload did not run this method often enough for Tier1.\n");
        }
        finally
        {
            File.Delete(listingFile);
        }
    }

    /// <summary>Where the runtime the assembly is built for puts the fields of a type, which the assembly or its dependencies declare.</summary>
    /// <param name="type">The assembly-qualified name of the type, with its type arguments.</param>
    public static TypeLayout LayOut(LoadedAssembly assembly, string type)
    {
        var response = RunHost(assembly, [], listingFile: null, new([], null), type);
        var layout = response.Layout!;
        var pointer = IntPtr.Size;

        // Objects start with a header, before where references point, then the method table pointer.
        var (start, end) = layout.IsValueType ? (0, layout.Size) : (-pointer, layout.Size - pointer);
        List<FieldSlot> slots = layout.IsValueType ? [] : [new("(object header)", null, -pointer, pointer, 0), new("(method table)", null, 0, pointer, 0)];
        var position = layout.IsValueType ? 0 : pointer;
        // A nameless field at the end, for the padding after the last one.
        foreach (var field in layout.Fields.Append(new("", "", null, end, 0)))
        {
            if (field.Offset > position)
                slots.Add(new("(padding)", null, position, field.Offset - position, (position - start) / CacheLine));
            if (field.Name.Length > 0)
                slots.Add(new(field.Name, field.Type, field.Offset, field.Size, (field.Offset - start) / CacheLine, field.DeclaringType));
            position = Math.Max(position, field.Offset + field.Size);
        }

        return new(layout.Type, layout.Size, (layout.Size + CacheLine - 1) / CacheLine, response.Runtime, slots);
    }

    /// <summary>
    /// The installed runtime to compile with, and its ASP.NET Core if installed: for a framework assembly, the framework it is part of; else the
    /// newest patch of the .NET it is built for; else the oldest newer .NET (as rolling forward picks), or the newest one for a later .NET; and
    /// for an assembly built for .NET Standard or .NET Framework, the server's.
    /// </summary>
    internal static (NuGetVersion NetCore, NuGetVersion? AspNetCore) RuntimeFor(string assemblyPath, int? major)
    {
        var shared = Path.Combine(Path.GetDirectoryName(DotnetCli.Executable)!, "shared");
        var netCore = Installed(Path.Combine(shared, "Microsoft.NETCore.App"));
        var aspNetCore = Installed(Path.Combine(shared, "Microsoft.AspNetCore.App"));
        if (netCore.Count == 0)
            throw new McpException($"No .NET runtime is installed under {shared}.");

        // shared/<framework>/<version>/<file>
        var framework = Path.GetRelativePath(shared, assemblyPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is [var name, var version, _]
            && NuGetVersion.TryParse(version, out var frameworkVersion) ? (Name: name, Version: frameworkVersion) : default;
        var target = framework.Version?.Major ?? major ?? Environment.Version.Major;

        var chosen = framework.Name == "Microsoft.NETCore.App" && netCore.Contains(framework.Version!)
            ? framework.Version!
            : netCore.Where(version => version.Major == target).DefaultIfEmpty(null).Max()
                ?? netCore.Where(version => version.Major > target).GroupBy(version => version.Major).MinBy(group => group.Key)?.Max()
                ?? netCore.Max()!;
        var web = framework.Name == "Microsoft.AspNetCore.App" && aspNetCore.Contains(framework.Version!)
            ? framework.Version
            : aspNetCore.Where(version => version.Major == chosen.Major).DefaultIfEmpty(null).Max();
        return (chosen, web);

        static List<NuGetVersion> Installed(string directory) =>
            Directory.Exists(directory)
                ? [.. Directory.EnumerateDirectories(directory).Select(Path.GetFileName).Select(name => NuGetVersion.TryParse(name, out var version) ? version : null).OfType<NuGetVersion>()]
                : [];
    }

    /// <summary>A runtimeconfig.json that runs the host on exactly these frameworks; the host is built for the oldest supported .NET.</summary>
    private static string RuntimeConfig((NuGetVersion NetCore, NuGetVersion? AspNetCore) runtime) => JsonSerializer.Serialize(new
    {
        runtimeOptions = new
        {
            tfm = $"net{runtime.NetCore.Major}.0",
            rollForward = "Disable",
            frameworks = (object[])[
                new { name = "Microsoft.NETCore.App", version = runtime.NetCore.ToNormalizedString() },
                .. runtime.AspNetCore is { } web ? [new { name = "Microsoft.AspNetCore.App", version = web.ToNormalizedString() }] : Array.Empty<object>()],
        },
    });

    private static Response RunHost(LoadedAssembly assembly, List<Method> methods, string? listingFile, JitOptions options, string? layout)
    {
        if (!File.Exists(HostPath))
            throw new McpException($"The JIT host is missing: {HostPath}");

        int? runtimeVersion;
        lock (assembly.Module)
        {
            // The .NET an assembly is built for: the version of the System.Runtime it references. Framework assemblies reference
            // System.Private.CoreLib instead, or are it.
            var module = assembly.Module;
            runtimeVersion = (module.Assembly.Name.Name == "System.Private.CoreLib"
                ? module.Assembly.Name
                : module.AssemblyReferences.FirstOrDefault(reference => reference.Name is "System.Runtime" or "System.Private.CoreLib"))?.Version.Major;
        }

        var path = assembly.Path.Value;
        var runtimeConfig = Path.Combine(Path.GetTempPath(), $"roslynaut-jithost-{Guid.NewGuid():N}.runtimeconfig.json");
        try
        {
            File.WriteAllText(runtimeConfig, RuntimeConfig(RuntimeFor(path, runtimeVersion)));
            var start = new ProcessStartInfo(DotnetCli.Executable)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = DotnetCli.Utf8,
                StandardOutputEncoding = DotnetCli.Utf8,
                StandardErrorEncoding = DotnetCli.Utf8,
                Environment =
                {
                    // A workload runs the methods up through the tiers, with Dynamic PGO; else they compile once, fully optimized.
                    ["DOTNET_TieredCompilation"] = options.Workload is null ? "0" : "1",
                    ["DOTNET_TieredPGO"] = "1",
                    // Counts calls towards Tier1 from the first one, rather than after 100 ms without new Tier0 code.
                    ["DOTNET_TC_CallCountingDelayMs"] = "0",
                },
            };
            foreach (var argument in (string[])["exec", "--runtimeconfig", runtimeConfig, HostPath])
                start.ArgumentList.Add(argument);
            if (listingFile is not null)
            {
                start.Environment["DOTNET_JitDisasm"] = string.Join(' ', methods.Select(method => method.Pattern).Distinct());
                start.Environment["DOTNET_JitStdOutFile"] = listingFile;
            }

            // The host runs on that very framework, so it uses its own copy of framework assemblies, which are precompiled.
            if (path.StartsWith(FrameworkDirectory, StringComparison.OrdinalIgnoreCase))
                start.Environment["DOTNET_ReadyToRun"] = "0";

            var dependencies = new Dictionary<string, string>(assembly.Dependencies.Value);
            foreach (var (name, file) in options.Workload?.Assembly.Dependencies.Value ?? new Dictionary<string, string>())
                dependencies.TryAdd(name, file);
            var request = new Request(path, dependencies, [.. methods.Select(method => method.Token)], options.TypeArguments,
                options.Workload is var (workload, token) ? new(workload.Path.Value, token) : null, layout);

            using var host = Process.Start(start)!;
            host.StandardInput.Write(JsonSerializer.Serialize(request));
            host.StandardInput.Close();
            var error = host.StandardError.ReadToEndAsync();
            var output = host.StandardOutput.ReadToEnd();
            if (!host.WaitForExit(TimeSpan.FromMinutes(1)))
            {
                host.Kill();
                throw new McpException("The JIT host did not finish within a minute.");
            }

            return host.ExitCode == 0 ? JsonSerializer.Deserialize<Response>(output)! : throw new McpException(error.Result.Trim());
        }
        finally
        {
            File.Delete(runtimeConfig);
        }
    }


    /// <summary>"Ns.Type+Nested`1:Method" from a listing that starts "; Assembly listing for method Ns.Type+Nested`1[System.__Canon]:Method(...)".</summary>
    private static string KeyOf(string listing)
    {
        var key = new StringBuilder();
        var depth = 0;
        foreach (var c in listing.AsSpan(ListingStart.Length, listing.IndexOf('(') - ListingStart.Length))
        {
            depth += c switch { '[' => 1, ']' => -1, _ => 0 };
            if (depth == 0 && c != ']')
                key.Append(c);
        }

        return key.ToString();
    }

    /// <summary>A listing's first line up to the tier: "; Assembly listing for method Ns.Type:Method(int):int" of "... (Tier1)".</summary>
    private static string NameOf(string listing)
    {
        var line = listing.Split('\n', 2)[0].TrimEnd();
        return line.LastIndexOf(" (", StringComparison.Ordinal) is var end and > 0 ? line[..end] : line;
    }

    /// <param name="Key">How the JIT names the method, without generic arguments.</param>
    /// <param name="Pattern">A DOTNET_JitDisasm pattern that matches the method.</param>
    private sealed record Method(int Token, string Name, string Key, string Pattern);

    // As Roslynaut.JitHost reads and writes them.
    private sealed record Request(string Assembly, IReadOnlyDictionary<string, string> Dependencies, int[] Methods, IReadOnlyList<string> TypeArguments, HostWorkload? Workload, string? Layout);

    private sealed record HostWorkload(string Assembly, int Token);

    private sealed record Response(string Runtime, Dictionary<int, string> Errors, string? Workload, HostLayout? Layout);

    private sealed record HostLayout(string Type, bool IsValueType, int Size, HostField[] Fields);

    private sealed record HostField(string Name, string Type, string? DeclaringType, int Offset, int Size);
}

/// <param name="TypeArguments">
/// Assembly-qualified names of the type arguments to compile generic code with: the outermost type's parameters first, then the method's own;
/// System.Object, which compiles the code reference types share, for those left out.
/// </param>
/// <param name="Workload">
/// A parameterless method to run after the methods compile, until the JIT has compiled what it makes hot again at Tier1, with what Dynamic PGO
/// saw: its assembly, and its metadata token there.
/// </param>
internal sealed record JitOptions(IReadOnlyList<string> TypeArguments, (LoadedAssembly Assembly, int Token)? Workload);

/// <param name="Type">The type, with its type arguments.</param>
/// <param name="Size">
/// The bytes each object of a class takes on the heap, its header and method table pointer included; for a struct, its size as a field, array
/// element or local.
/// </param>
/// <param name="CacheLines">How many 64-byte cache lines that size takes at least; the heap aligns objects to 8 bytes only, so one can straddle one more.</param>
/// <param name="Runtime">The .NET that laid the type out: the one the assembly is built for, as for symbol_asm.</param>
/// <param name="Fields">The fields by offset, base types' included, with the padding between them; for a class, after its header and method table pointer.</param>
public sealed record TypeLayout(string Type, int Size, int CacheLines, string Runtime, IReadOnlyList<FieldSlot> Fields);

/// <param name="Name">The field's name; or (object header), (method table) or (padding).</param>
/// <param name="Offset">
/// Where it starts, as the JIT's code addresses it: for a class, from where references to the object point, its method table pointer, so that
/// [rcx+0x10] is offset 16 and the header is at -8 on 64-bit; for a struct, from its start.
/// </param>
/// <param name="CacheLine">The 64-byte cache line it starts in, counting from the start of the object (its header) or struct, as if that started a line.</param>
/// <param name="DeclaringType">The base type that declares the field, if not the type itself.</param>
public sealed record FieldSlot(string Name, string? Type, int Offset, int Size, int CacheLine, string? DeclaringType = null);
