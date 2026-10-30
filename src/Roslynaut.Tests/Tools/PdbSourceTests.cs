using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>
/// PDBs and the sources they lead to: of a library built in a git repository, which gives it SourceLink, with a regex from a source generator;
/// and, over the network, of the framework from Microsoft's symbol server and GitHub, and of a package from NuGet's symbol server.
/// </summary>
[NonParallelizable]
public class PdbSourceTests
{
    private readonly AssemblyLoader assemblies = new();
    private readonly PdbService pdbs = new();
    private DirectoryInfo directory = null!;
    private string library = null!;

    [OneTimeSetUp]
    public async Task BuildLibrary()
    {
        directory = Directory.CreateTempSubdirectory("roslynaut-pdb-");
        File.WriteAllText(Path.Combine(directory.FullName, "Lib.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(directory.FullName, "Lib.cs"), """
            using System.Text.RegularExpressions;

            namespace Lib;

            public static partial class Patterns
            {
                /// <summary>Matches a's, then b.</summary>
                [GeneratedRegex("a+b")]
                public static partial Regex Ab();

                public static bool IsAb(string text) => Ab().IsMatch(text);

                public static bool IsAb(char c) => c is 'a' or 'b';
            }
            """);

        Git("init", "-q");
        Git("remote", "add", "origin", "https://github.com/roslynaut/lib.git");
        Git("add", "-A");
        Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "Library");

        var (exitCode, output) = await DotnetCli.RunAsync(["build", Path.Combine(directory.FullName, "Lib.csproj"), "-c", "Release"], TimeSpan.FromMinutes(5), CancellationToken.None);
        exitCode.Should().Be(0, output);
        library = Path.Combine(directory.FullName, "bin", "Release", "net10.0", "Lib.dll");

        void Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = directory.FullName, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var git = Process.Start(start)!;
            git.WaitForExit();
            git.ExitCode.Should().Be(0, git.StandardError.ReadToEnd());
        }
    }

    [OneTimeTearDown]
    public void DeleteLibrary()
    {
        assemblies.Dispose();
        pdbs.Dispose();

        // Git makes its objects read-only.
        foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        directory.Delete(recursive: true);
    }

    private async Task<Pdb> LibraryPdbAsync() => (await pdbs.FindPdbAsync(library, download: false, CancellationToken.None))!;

    /// <summary>A symbol of an assembly that a project references.</summary>
    private static async Task<(Solution Solution, INamedTypeSymbol Type)> ReferenceAsync(string assembly, string type)
    {
        var solution = TestSolution.Create("class C { }").Projects.Single().AddMetadataReference(MetadataReference.CreateFromFile(assembly)).Solution;
        var compilation = (await solution.Projects.Single().GetCompilationAsync())!;
        return (solution, compilation.GetTypeByMetadataName(type)!);
    }

    [Test]
    public async Task AssemblySources_ListsFilesOnDisk_WithTheirSourceLink_AndEmbeddedGeneratedCode()
    {
        var documents = AssemblySourcesTool.List(assemblies, pdbs, library, await LibraryPdbAsync(), null, null).ToList();

        documents.Should().Contain(document => document.Name.EndsWith("Lib.cs") && document.Origin == "Local"
            && document.Url!.StartsWith("https://raw.githubusercontent.com/roslynaut/lib/") && document.Url.EndsWith("/Lib.cs"));
        documents.Should().Contain(document => document.Name.EndsWith("RegexGenerator.g.cs") && document.Origin == "Embedded");
        documents.Should().OnlyContain(document => document.Source == null && document.Verified == null);
    }

    [Test]
    public async Task AssemblySources_OfGeneratedMethod_IsTheEmbeddedFile()
    {
        var document = AssemblySourcesTool.List(assemblies, pdbs, library, await LibraryPdbAsync(), null, "Patterns.Ab").Should().ContainSingle().Subject;

        document.Should().Match<SourceDocument>(generated => generated.Origin == "Embedded" && generated.Verified == true && File.Exists(generated.FilePath));
        document.Source.Should().Contain(" Ab()").And.Contain("a+b");
    }

    [Test]
    public async Task AssemblySources_ByName_ReadsTheFile()
    {
        AssemblySourcesTool.List(assemblies, pdbs, library, await LibraryPdbAsync(), "Lib.cs", null).Should().ContainSingle()
            .Which.Should().Match<SourceDocument>(document => document.Origin == "Local" && document.Verified == true && document.Source!.Contains("IsAb(char c)"));
    }

    [Test]
    public async Task SymbolSource_OfLibraryType_HasItsPartsFromDiskAndFromThePdb()
    {
        var (solution, patterns) = await ReferenceAsync(library, "Lib.Patterns");

        var sources = await SymbolSourceTool.GetPdbSourcesAsync(solution, assemblies, pdbs, patterns, CancellationToken.None);

        sources.Select(source => source.Document!.Origin).Should().BeEquivalentTo("Local", "Embedded");
        sources.Should().OnlyContain(source => source.Source.Contains("partial class Patterns"));
    }

    [Test]
    public async Task SymbolSource_OfOverload_IsOnlyThatOne()
    {
        var (solution, patterns) = await ReferenceAsync(library, "Lib.Patterns");
        var isAb = patterns.GetMembers("IsAb").OfType<IMethodSymbol>().Single(method => method.Parameters[0].Type.SpecialType == SpecialType.System_Char);

        (await SymbolSourceTool.GetPdbSourcesAsync(solution, assemblies, pdbs, isAb, CancellationToken.None)).Should().ContainSingle()
            .Which.Source.Should().Be("    public static bool IsAb(char c) => c is 'a' or 'b';");
    }

    [Test]
    public async Task SymbolSource_OfGeneratedRegex_IsTheGeneratedImplementation()
    {
        var (solution, patterns) = await ReferenceAsync(library, "Lib.Patterns");

        var source = (await SymbolSourceTool.GetPdbSourcesAsync(solution, assemblies, pdbs, patterns.GetMembers("Ab").Single(), CancellationToken.None))
            .Should().ContainSingle().Subject;
        source.Document!.Origin.Should().Be("Embedded");
        source.Source.Should().Contain(" Ab()");
    }

    [Test]
    public async Task Definition_OfLibrarySymbol_PointsIntoItsSource()
    {
        var (solution, patterns) = await ReferenceAsync(library, "Lib.Patterns");
        var isAb = patterns.GetMembers("IsAb").OfType<IMethodSymbol>().Single(method => method.Parameters[0].Type.SpecialType == SpecialType.System_String);

        var definition = (await DefinitionTool.DefineAsync(solution, assemblies, pdbs, [isAb], CancellationToken.None)).Should().ContainSingle().Subject;

        definition.AssemblyPath.Should().Be(library);
        definition.Locations.Should().ContainSingle().Which.Should().Match<SourceLocation>(location => location.FilePath.EndsWith("Lib.cs") && location.Line == 11);
        definition.Sources.Should().ContainSingle().Which.Verified.Should().BeTrue();
    }

    [Test]
    public async Task PdbService_WhenFull_EvictsLeastRecentlyUsedPdb()
    {
        var copies = Directory.CreateTempSubdirectory();
        try
        {
            var (a, b, c) = (Copy("A"), Copy("B"), Copy("C"));
            using var service = new PdbService(capacity: 2);
            var (pdbA, pdbB) = (service.FindPdb(a), service.FindPdb(b));
            service.FindPdb(a);
            await service.FindPdbAsync(c, download: false, CancellationToken.None);

            service.FindPdb(a).Should().NotBeNull().And.BeSameAs(pdbA);
            service.FindPdb(b).Should().NotBeNull().And.NotBeSameAs(pdbB);
        }
        finally
        {
            copies.Delete(recursive: true);
        }

        // The PDB keeps its name, which the assembly records.
        string Copy(string name)
        {
            var assembly = typeof(PdbSourceTests).Assembly.Location;
            var folder = copies.CreateSubdirectory(name).FullName;
            File.Copy(Path.ChangeExtension(assembly, ".pdb"), Path.Combine(folder, Path.GetFileName(Path.ChangeExtension(assembly, ".pdb"))));
            File.Copy(assembly, Path.Combine(folder, Path.GetFileName(assembly)));
            return Path.Combine(folder, Path.GetFileName(assembly));
        }
    }

    [Test]
    public void Decompile_WithPdb_KeepsLocalNames()
    {
        var assembly = assemblies.Read(typeof(PdbSourceTests).Assembly.Location);
        var pdb = pdbs.FindPdb(assembly.Path.Value);

        DecompileTool.Decompile(assembly, ["PdbSourceTests.SumOfSquares"], pdb).Should().ContainSingle().Which.Code.Should().Contain("runningTotal");
        DecompileTool.Decompile(assembly, ["PdbSourceTests.SumOfSquares"]).Should().ContainSingle().Which.Code.Should().NotContain("runningTotal");
    }

    internal static int SumOfSquares(int[] values)
    {
        var runningTotal = 0;
        foreach (var value in values)
            runningTotal += value * value;
        return runningTotal;
    }

    // The framework's PDBs are on Microsoft's symbol server, and lead through SourceLink to GitHub, at line endings other than the build's.
    [Test]
    public async Task SymbolSource_OfFrameworkMethod_ComesThroughSymbolServerAndSourceLink()
    {
        var collections = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Collections.dll");
        var (solution, linkedList) = await ReferenceAsync(collections, "System.Collections.Generic.LinkedList`1");
        var addFirst = linkedList.GetMembers("AddFirst").OfType<IMethodSymbol>().Single(method => method.Parameters[0].Type is ITypeParameterSymbol);

        var source = (await SymbolSourceTool.GetPdbSourcesAsync(solution, assemblies, pdbs, addFirst, CancellationToken.None)).Should().ContainSingle().Subject;

        source.Source.Should().Contain("public LinkedListNode<T> AddFirst(T value)");
        source.Document.Should().Match<SourceDocument>(document => document.Origin == "SourceLink" && document.Verified == true && document.Url!.StartsWith("https://"));
    }

    [Test]
    public async Task FindPdb_OfPackageWithSymbolPackage_DownloadsItFromNuGet()
    {
        var buildalyzer = Path.Combine(AppContext.BaseDirectory, "Buildalyzer.dll");

        var pdb = await pdbs.FindPdbAsync(buildalyzer, download: true, CancellationToken.None);

        pdb.Should().NotBeNull();
        pdb!.SourceLink.Should().NotBeEmpty();
        pdbs.FindPdb(buildalyzer).Should().BeSameAs(pdb);
    }
}

public class SymbolPathTests
{
    [Test]
    public void SymbolServersOf_TakesServersAndDirectories_NotCaches()
    {
        PdbService.SymbolServersOf(@"srv*C:\Symbols*https://symbols.example.com;cache*C:\Cache;\share\symbols;SRV*https://other.example.com;symsrv*symsrv.dll*C:\S*https://third.example.com")
            .Should().Equal("https://symbols.example.com", @"\share\symbols", "https://other.example.com", "https://third.example.com");
    }

    // An assembly without its PDB, which a directory of PDBs has, as a file share of symbols would.
    [Test]
    public async Task FindPdb_ComesFromSymbolDirectory()
    {
        var (assembly, store) = (Directory.CreateTempSubdirectory("roslynaut-nopdb-"), Directory.CreateTempSubdirectory("roslynaut-symbols-"));
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Roslynaut.JitHost.dll"), Path.Combine(assembly.FullName, "Roslynaut.JitHost.dll"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Roslynaut.JitHost.pdb"), Path.Combine(store.FullName, "Roslynaut.JitHost.pdb"));

            // Downloads are cached by PDB id, so an earlier run must not answer for this one.
            var cache = Path.Combine(Path.GetTempPath(), "Roslynaut");
            foreach (var cached in Directory.Exists(cache) ? Directory.EnumerateFiles(cache, "Roslynaut.JitHost.pdb", SearchOption.AllDirectories) : [])
                File.Delete(cached);

            using var pdbs = new PdbService(symbolServers: [store.FullName]);
            (await pdbs.FindPdbAsync(Path.Combine(assembly.FullName, "Roslynaut.JitHost.dll"), download: true, CancellationToken.None)).Should().NotBeNull();
        }
        finally
        {
            assembly.Delete(recursive: true);
            store.Delete(recursive: true);
        }
    }
}
