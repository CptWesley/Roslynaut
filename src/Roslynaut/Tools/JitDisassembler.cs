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

    private static readonly string HostPath = Path.Combine(AppContext.BaseDirectory, "Roslynaut.JitHost.dll");
    private static readonly string FrameworkDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", ".."));

    public static List<MethodAsm> Disassemble(LoadedAssembly assembly, IReadOnlyCollection<string> names)
    {
        List<Method> methods;
        int? runtimeVersion;
        lock (assembly.Module)
        {
            methods = [.. IlDisassembler.FindMethods(assembly.Module, names).Select(method => new Method(
                method.MetadataToken.ToInt32(),
                method.FullName,
                // As the JIT names methods in its listings, without generic arguments.
                $"{method.DeclaringType.FullName.Replace('/', '+')}:{method.Name}",
                $"*{method.DeclaringType.Name}*:{method.Name}"))];

            // The .NET an assembly is built for: the version of the System.Runtime it references. Framework assemblies reference
            // System.Private.CoreLib instead, or are it.
            var module = assembly.Module;
            runtimeVersion = (module.Assembly.Name.Name == "System.Private.CoreLib"
                ? module.Assembly.Name
                : module.AssemblyReferences.FirstOrDefault(reference => reference.Name is "System.Runtime" or "System.Private.CoreLib"))?.Version.Major;
        }

        var listingFile = Path.GetTempFileName();
        var runtimeConfig = Path.Combine(Path.GetTempPath(), $"roslynaut-jithost-{Guid.NewGuid():N}.runtimeconfig.json");
        try
        {
            File.WriteAllText(runtimeConfig, RuntimeConfig(RuntimeFor(assembly.Path.Value, runtimeVersion)));
            var (runtime, errors) = RunHost(assembly, methods, listingFile, runtimeConfig);

            // Every method is compiled once, in the order given, so overloads get their listings in that order too.
            var listings = File.ReadAllText(listingFile).Split(ListingStart).Skip(1)
                .Select(listing => ListingStart + listing.TrimEnd())
                .GroupBy(KeyOf)
                .ToDictionary(group => group.Key, group => new Queue<string>(group));
            return [.. methods.Select(method => new MethodAsm(method.Name, errors.GetValueOrDefault(method.Token) is { Length: > 0 } error
                ? $"; Not compiled: {error}"
                : listings.GetValueOrDefault(method.Key)?.TryDequeue(out var listing) == true
                    ? $"; {runtime}\n{listing}"
                    : "; No listing: the JIT did not compile this method."))];
        }
        finally
        {
            File.Delete(listingFile);
            File.Delete(runtimeConfig);
        }
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

    private static (string Runtime, Dictionary<int, string> Errors) RunHost(LoadedAssembly assembly, List<Method> methods, string listingFile, string runtimeConfig)
    {
        if (!File.Exists(HostPath))
            throw new McpException($"The JIT host is missing: {HostPath}");

        var path = assembly.Path.Value;
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
                ["DOTNET_JitDisasm"] = string.Join(' ', methods.Select(method => method.Pattern).Distinct()),
                ["DOTNET_JitStdOutFile"] = listingFile,
                ["DOTNET_TieredCompilation"] = "0",
            },
        };
        foreach (var argument in (string[])["exec", "--runtimeconfig", runtimeConfig, HostPath, path, .. methods.Select(method => method.Token.ToString())])
            start.ArgumentList.Add(argument);

        // The host runs on that very framework, so it uses its own copy of framework assemblies, which are precompiled.
        if (path.StartsWith(FrameworkDirectory, StringComparison.OrdinalIgnoreCase))
            start.Environment["DOTNET_ReadyToRun"] = "0";

        using var host = Process.Start(start)!;
        host.StandardInput.Write(string.Concat(assembly.Dependencies.Value.Select(dependency => $"{dependency.Key}\t{dependency.Value}\n")));
        host.StandardInput.Close();
        var error = host.StandardError.ReadToEndAsync();
        var output = host.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!host.WaitForExit(TimeSpan.FromMinutes(1)))
        {
            host.Kill();
            throw new McpException("The JIT host did not finish within a minute.");
        }

        if (host.ExitCode != 0)
            throw new McpException(error.Result.Trim());

        var errors = output.Skip(1).Select(line => line.Split('\t', 2)).ToDictionary(parts => int.Parse(parts[0]), parts => parts.ElementAtOrDefault(1) ?? "");
        return (output[0], errors);
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

    /// <param name="Key">How the JIT names the method, without generic arguments.</param>
    /// <param name="Pattern">A DOTNET_JitDisasm pattern that matches the method.</param>
    private sealed record Method(int Token, string Name, string Key, string Pattern);
}
