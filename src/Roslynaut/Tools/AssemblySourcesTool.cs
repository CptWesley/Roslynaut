using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class AssemblySourcesTool(AssemblyLoader assemblies, PdbService pdbs, ResultCache cache) : IMcpTool
{
    public Delegate Handler => ListAsync;

    [McpServerTool(Name = "assembly_sources", ReadOnly = true, OpenWorld = true)]
    [Description("Lists the source files of any .NET assembly from its PDB, which comes from next to the assembly, from inside it, or from the "
        + "Microsoft or NuGet symbol server. With a name or member, also returns their original source: embedded in the PDB, from disk, or through "
        + "SourceLink from the repository at the commit it was built from; Verified tells whether a file has the checksum the PDB records. "
        + "Unlike decompile, this keeps comments and original names, but lacks what the compiler synthesizes (records, async, lambdas) or IL "
        + "weavers add; PDBs usually embed the code of source generators. Paged.")]
    private async Task<PagedResult<SourceDocument>> ListAsync(
        [Description(IlDisassembler.AssemblyPath)] string assemblyPath,
        [Description("Optional path, or part of it, of the files to return the source of, e.g. 'Parser.cs' or 'src/Json/'.")] string? name = null,
        [Description("Optional type or member whose files to return the source of, named as for decompile, e.g. 'JsonConvert' or 'JsonConvert.SerializeObject'.")]
        string? member = null,
        [Description(ToolDefaults.Page)] int page = 0,
        [Description(ToolDefaults.PageSize)] int pageSize = ToolDefaults.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(assemblyPath);
        var pdb = await pdbs.FindPdbAsync(path, download: true, cancellationToken)
            ?? throw new McpException($"{path} has no PDB next to it, inside it or on the symbol servers: use decompile.");
        return await cache.GetPageAsync(pdb, $"assembly_sources|{name}|{member}", page, pageSize,
            () => Task.FromResult(List(assemblies, pdbs, path, pdb, name, member)));
    }

    /// <summary>The PDB's documents, described; those matching name or declaring member fetched, with their text.</summary>
    internal static IEnumerable<SourceDocument> List(AssemblyLoader assemblies, PdbService pdbs, string path, Pdb pdb, string? name, string? member)
    {
        var documents = pdb.Documents.Where(document => name is null || pdb.NameOf(document).Contains(name, StringComparison.OrdinalIgnoreCase));
        if (member is not null)
        {
            var module = assemblies.Read(path).Module;
            List<MemberTokens> members;
            lock (module)
                members = [.. IlDisassembler.FindMembers(module, [member]).Select(IlDisassembler.TokensOf)];
            var declaring = members.SelectMany(pdb.DocumentsOf).ToHashSet();
            documents = documents.Where(declaring.Contains);
        }

        if (name is null && member is null)
            return documents.Select(document => PdbService.Describe(pdb, document));

        // Evaluation continues in later requests, so it runs without a request's token.
        // ponytail: downloads block a thread while a page evaluates; make EvaluatedEnumerable async if that gets slow.
        return documents.Select(document => pdbs.FetchAsync(pdb, document, CancellationToken.None).GetAwaiter().GetResult());
    }
}
