using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using Roslynaut.Tools;

namespace Roslynaut;

/// <summary>A source file that the PDB of an assembly lists, and where its text comes from.</summary>
/// <param name="Name">The path the PDB records, such as "/_/src/Lib/Parser.cs" for a deterministic build.</param>
/// <param name="Origin">
/// Where the text comes from: Embedded in the PDB, Local (the file at Name), SourceLink (downloaded from Url), or None.
/// PDBs usually embed the code of source generators, but not the files MSBuild writes to obj\, such as AssemblyInfo.cs.
/// </param>
/// <param name="Url">The SourceLink URL of the file, if the PDB maps it.</param>
/// <param name="FilePath">A local copy of the text: the file at Name, or one cached from the PDB or from Url. Null until fetched.</param>
/// <param name="Verified">Whether the text has the checksum the PDB records, so it is exactly what was compiled. Null until fetched.</param>
/// <param name="Error">Why the text could not be fetched.</param>
/// <param name="Source">The text, where asked for.</param>
public sealed record SourceDocument(string Name, string Origin, string? Url, string? FilePath, bool? Verified, string? Error = null, string? Source = null);

/// <summary>A portable PDB in memory. System.Reflection.Metadata reads it, which is thread-safe.</summary>
public sealed class Pdb
{
    private static readonly Guid SourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");
    private static readonly Guid EmbeddedSourceKind = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    private static readonly Guid TypeDocumentsKind = new("932E74BC-DBA9-4478-8D46-0F32A7BAB3D3");
    private static readonly Guid CSharp = new("3F5162F8-07C6-11D3-9053-00C04FA302A1");
    private static readonly Guid Sha1 = new("FF1816EC-AA5E-4D10-87F7-6F4963833460");
    private static readonly Guid Sha256 = new("8829D00F-11B8-4213-878B-770E8597AC16");

    // Owns the memory Reader reads.
    private readonly MetadataReaderProvider _provider;

    internal Pdb(MetadataReaderProvider provider, string location)
    {
        _provider = provider;
        Reader = provider.GetMetadataReader();
        Location = location;

        var json = Blobs(EntityHandle.ModuleDefinition, SourceLinkKind).Select(Reader.GetBlobBytes).FirstOrDefault();
        try
        {
            using var document = json is null ? null : JsonDocument.Parse(json);
            SourceLink = document?.RootElement.TryGetProperty("documents", out var documents) == true
                ? documents.EnumerateObject().Where(entry => entry.Value.ValueKind == JsonValueKind.String).ToDictionary(entry => entry.Name, entry => entry.Value.GetString()!)
                : new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            SourceLink = new Dictionary<string, string>();
        }
    }

    public MetadataReader Reader { get; }

    /// <summary>The PDB file: next to the assembly, or downloaded from a symbol server into a cache; "embedded" if the assembly contains it.</summary>
    public string Location { get; }

    /// <summary>The SourceLink map: document paths, or path prefixes ending in *, to URLs.</summary>
    public IReadOnlyDictionary<string, string> SourceLink { get; }

    public IEnumerable<DocumentHandle> Documents => Reader.Documents;

    public string NameOf(DocumentHandle document) => Reader.GetString(Reader.GetDocument(document).Name);

    public bool IsCSharp(DocumentHandle document) => Reader.GetGuid(Reader.GetDocument(document).Language) == CSharp;

    public bool Embeds(DocumentHandle document) => Blobs(document, EmbeddedSourceKind).Any();

    /// <summary>The URL SourceLink maps a document path to: the longest matching path or prefix wins.</summary>
    public string? UrlOf(string name)
    {
        foreach (var (key, url) in SourceLink.OrderByDescending(entry => entry.Key.Length))
        {
            if (!key.EndsWith('*'))
            {
                if (name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return url;
            }
            else if (name.StartsWith(key[..^1], StringComparison.OrdinalIgnoreCase))
            {
                return url.Replace("*", name[(key.Length - 1)..].Replace('\\', '/'));
            }
        }

        return null;
    }

    /// <summary>The text the PDB embeds for a document, as the compiler read it.</summary>
    public byte[]? EmbeddedSource(DocumentHandle document)
    {
        foreach (var blob in Blobs(document, EmbeddedSourceKind))
        {
            var reader = Reader.GetBlobReader(blob);

            // 0 for uncompressed text; else the size of the deflated text.
            var format = reader.ReadInt32();
            var content = reader.ReadBytes(reader.RemainingBytes);
            if (format == 0)
                return content;

            using var deflate = new DeflateStream(new MemoryStream(content), CompressionMode.Decompress);
            var text = new byte[format];
            deflate.ReadExactly(text);
            return text;
        }

        return null;
    }

    /// <summary>Whether content has the checksum the PDB records for a document.</summary>
    public bool Matches(DocumentHandle document, byte[] content)
    {
        var info = Reader.GetDocument(document);
        var algorithm = Reader.GetGuid(info.HashAlgorithm);
        var hash = algorithm == Sha256 ? SHA256.HashData(content) : algorithm == Sha1 ? SHA1.HashData(content) : null;
        return hash is not null && Reader.GetBlobBytes(info.Hash).AsSpan().SequenceEqual(hash);
    }

    /// <summary>The checksum the PDB records for a document, in hex; null if none.</summary>
    public string? ChecksumOf(DocumentHandle document) =>
        Reader.GetBlobBytes(Reader.GetDocument(document).Hash) is { Length: > 0 } hash ? Convert.ToHexStringLower(hash) : null;

    /// <summary>The sequence points of a method by metadata token, hidden ones included; none for a method without code or debug information.</summary>
    public IEnumerable<SequencePoint> SequencePoints(int methodToken)
    {
        var row = MetadataTokens.GetRowNumber(MetadataTokens.EntityHandle(methodToken));
        if (row > Reader.MethodDebugInformation.Count)
            return [];
        return Reader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(row)).GetSequencePoints();
    }

    /// <summary>The 1-based lines of code of methods, per document, from their visible sequence points.</summary>
    public Dictionary<DocumentHandle, HashSet<int>> LinesOf(IEnumerable<int> methodTokens) =>
        methodTokens.SelectMany(SequencePoints).Where(point => !point.IsHidden)
            .GroupBy(point => point.Document).ToDictionary(group => group.Key, group => group.Select(point => point.StartLine).ToHashSet());

    /// <summary>
    /// The documents that declare a type or member: those its code is in. For a type, or a member without code, those of all the type's code,
    /// plus those the compiler records for parts of the type without code.
    /// </summary>
    internal List<DocumentHandle> DocumentsOf(MemberTokens member)
    {
        var own = LinesOf(member.Methods).Keys;
        if (!member.IsType && own.Count > 0)
            return [.. own];

        var documents = LinesOf(member.TypeMethods).Keys.ToList();
        foreach (var blob in Blobs(MetadataTokens.EntityHandle(member.Type), TypeDocumentsKind))
        {
            var reader = Reader.GetBlobReader(blob);
            while (reader.RemainingBytes > 0)
                documents.Add(MetadataTokens.DocumentHandle(reader.ReadCompressedInteger()));
        }

        return [.. documents.Distinct()];
    }

    /// <summary>The document and line of the code at an IL offset of a method: those of the last visible sequence point at or before it.</summary>
    public (DocumentHandle Document, int Line)? LineOf(int methodToken, int offset) => SequencePoints(methodToken)
        .Where(point => !point.IsHidden && point.Offset <= offset)
        .Select(point => ((DocumentHandle, int)?)(point.Document, point.StartLine))
        .LastOrDefault();

    /// <summary>The names of a method's local variables by slot, from all its scopes; compiler temporaries are left out.</summary>
    public IEnumerable<(int Index, string Name)> LocalsOf(MethodDefinitionHandle method) => Reader.GetLocalScopes(method)
        .SelectMany(scope => Reader.GetLocalScope(scope).GetLocalVariables())
        .Select(Reader.GetLocalVariable)
        .Where(local => (local.Attributes & LocalVariableAttributes.DebuggerHidden) == 0)
        .Select(local => (local.Index, Reader.GetString(local.Name)));

    /// <summary>
    /// The IL offsets of the awaits of an async method's MoveNext, in the order of the states the state machine waits in at them: state 0 at
    /// the first. From the compiler's async stepping information, blob: catch handler offset, then per await its yield offset, resume offset
    /// and resume method.
    /// </summary>
    public IReadOnlyList<int> AwaitOffsets(int moveNextToken)
    {
        if (MetadataTokens.EntityHandle(moveNextToken) is not { Kind: HandleKind.MethodDefinition } method
            || Blobs(method, AsyncSteppingKind).FirstOrDefault() is not { IsNil: false } blob)
            return [];

        var reader = Reader.GetBlobReader(blob);
        reader.ReadUInt32();
        var offsets = new List<int>();
        while (reader.RemainingBytes > 0)
        {
            offsets.Add((int)reader.ReadUInt32());
            reader.ReadUInt32();
            reader.ReadCompressedInteger();
        }

        return offsets;
    }

    private static readonly Guid AsyncSteppingKind = new("54FD2AC5-E925-401A-9C2A-F94F171072F8");

    private IEnumerable<BlobHandle> Blobs(EntityHandle parent, Guid kind) => Reader.GetCustomDebugInformation(parent)
        .Select(Reader.GetCustomDebugInformation).Where(info => Reader.GetGuid(info.Kind) == kind).Select(info => info.Value);
}

/// <summary>
/// Finds the portable PDBs of assemblies, and the source files they list. A PDB comes from next to its assembly, from inside it, or from the
/// Microsoft or NuGet symbol server, which have those of the framework and of packages that publish symbols. A source file comes from the PDB,
/// from disk, or through SourceLink from the repository at the commit it was built from. Downloads are cached on disk by PDB id or checksum,
/// so they never go stale.
/// </summary>
public sealed class PdbService : IDisposable
{
    // ponytail: no credentials for symbol servers either, so a private one has to serve anonymously or be a file share.
    private static readonly string[] PublicSymbolServers = ["https://msdl.microsoft.com/download/symbols", "https://symbols.nuget.org/download/symbols"];

    private readonly IReadOnlyList<string> _symbolServers;
    private readonly int _capacity;
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "Roslynaut");
    private readonly Dictionary<string, CachedPdb> _pdbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<bool>> _downloads = new();

    // No credentials: the URLs come from assemblies, which need not be trusted. Private repositories stay out of reach.
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 256 << 20 };
    private long _clock;

    /// <param name="capacity">How many PDBs to keep in memory. Finding another one evicts the least recently used.</param>
    /// <param name="symbolServers">
    /// URLs of symbol servers, or directories such as file shares; by default those that _NT_SYMBOL_PATH names, then Microsoft's and NuGet's.
    /// </param>
    public PdbService(int capacity = 32, IReadOnlyList<string>? symbolServers = null)
    {
        _capacity = capacity;
        _symbolServers = symbolServers ?? [.. SymbolServersOf(Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH")).Concat(PublicSymbolServers).Distinct(StringComparer.OrdinalIgnoreCase)];
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Roslynaut");
    }

    /// <summary>
    /// The servers in a symbol path such as "srv*C:\Symbols*https://symbols.example.com;\\share\symbols": the last part of each srv* or symsrv*
    /// entry, and plain directories. Cache entries, and the local caches inside srv* entries, only hold what the servers had.
    /// </summary>
    internal static IEnumerable<string> SymbolServersOf(string? symbolPath) =>
        (symbolPath ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split('*'))
            .Select(parts => parts switch
            {
                [var kind, .., var server] when kind.Equals("srv", StringComparison.OrdinalIgnoreCase) || kind.Equals("symsrv", StringComparison.OrdinalIgnoreCase) => server,
                [var directory] => directory,
                _ => "",
            })
            .Where(server => server.Length > 0);

    /// <summary>
    /// The PDB of an assembly: next to it, embedded in it, or downloaded from a symbol server earlier; with download, also from a symbol server now.
    /// Null if there is none.
    /// </summary>
    public async Task<Pdb?> FindPdbAsync(string assemblyPath, bool download, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(assemblyPath);
        if (!File.Exists(path))
            throw new McpException($"Assembly not found: {path}");

        var timestamp = File.GetLastWriteTimeUtc(path);
        lock (_pdbs)
        {
            if (_pdbs.TryGetValue(path, out var cached) && cached.Timestamp == timestamp)
            {
                cached.LastUsed = ++_clock;
                return cached.Pdb;
            }
        }

        var (pdb, symbols) = ReadLocal(path);
        if (pdb is null && symbols is not null)
        {
            var file = Path.Combine(_cache, "symbols", symbols.Key);
            if (File.Exists(file) || download && await DownloadAsync(symbols, file, cancellationToken))
                pdb = new(MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(file), MetadataStreamOptions.PrefetchMetadata), file);
        }

        if (pdb is null)
            return null;

        // Found outside the lock, as downloads take a while; finding fails without evicting another.
        lock (_pdbs)
        {
            if (!_pdbs.ContainsKey(path) && _pdbs.Count >= _capacity)
                _pdbs.Remove(_pdbs.MinBy(entry => entry.Value.LastUsed).Key);
            _pdbs[path] = new(timestamp, pdb) { LastUsed = ++_clock };
        }

        return pdb;
    }

    /// <summary>The PDB of an assembly, without downloading it; see <see cref="FindPdbAsync"/>.</summary>
    public Pdb? FindPdb(string assemblyPath) => FindPdbAsync(assemblyPath, download: false, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Where a document's text would come from, without fetching it.</summary>
    public static SourceDocument Describe(Pdb pdb, DocumentHandle document)
    {
        var name = pdb.NameOf(document);
        var url = pdb.UrlOf(name);
        var origin = pdb.Embeds(document) ? "Embedded" : File.Exists(name) ? "Local" : url is not null ? "SourceLink" : "None";
        return new(name, origin, url, origin == "Local" ? name : null, null);
    }

    /// <summary>
    /// Fetches a document's text: from the PDB, from disk if it has the PDB's checksum, or else through SourceLink. Text with another checksum,
    /// such as a file changed since the build, only comes back if nothing better does, with Verified false.
    /// </summary>
    public async Task<SourceDocument> FetchAsync(Pdb pdb, DocumentHandle document, CancellationToken cancellationToken)
    {
        var name = pdb.NameOf(document);
        var url = pdb.UrlOf(name);
        string? error = null;
        if (pdb.EmbeddedSource(document) is { } embedded)
        {
            var file = CachePathOf(pdb, document, name, url);
            if (!File.Exists(file))
                Write(file, embedded);
            return Found("Embedded", file, embedded, pdb.Matches(document, embedded));
        }

        var local = File.Exists(name) ? await File.ReadAllBytesAsync(name, cancellationToken) : null;
        if (local is not null && pdb.Matches(document, local))
            return Found("Local", name, local, true);

        if (url is not null)
        {
            try
            {
                var (file, content, verified) = await DownloadSourceAsync(pdb, document, name, url, cancellationToken);
                if (verified || local is null)
                    return Found("SourceLink", file, content, verified);
            }
            catch (McpException e)
            {
                error = e.Message;
            }
        }

        return local is not null
            ? Found("Local", name, local, false)
            : new(name, "None", url, null, null, error ?? "The PDB neither embeds this file nor maps it to a URL with SourceLink.");

        SourceDocument Found(string origin, string file, byte[] content, bool verified) => new(name, origin, url, file, verified, error, Decode(content));
    }

    /// <summary>
    /// Fetches a document of an assembly's PDB by the path the PDB records, without its text; null if the assembly has no PDB, or the PDB no such document.
    /// </summary>
    public async Task<SourceDocument?> FetchAsync(string assemblyPath, string name, CancellationToken cancellationToken)
    {
        if (await FindPdbAsync(assemblyPath, download: true, cancellationToken) is not { } pdb)
            return null;

        var path = name.Replace('\\', '/');
        var document = pdb.Documents.FirstOrDefault(candidate => pdb.NameOf(candidate).Replace('\\', '/').Equals(path, StringComparison.OrdinalIgnoreCase));
        return document.IsNil ? null : (await FetchAsync(pdb, document, cancellationToken)) with { Source = null };
    }

    /// <summary>Whether an assembly comes from a deterministic build, which makes the same binary from the same source.</summary>
    public static bool IsDeterministic(string assemblyPath)
    {
        using var pe = new PEReader(File.OpenRead(assemblyPath));
        return pe.ReadDebugDirectory().Any(entry => entry.Type == DebugDirectoryEntryType.Reproducible);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The PDB next to or inside an assembly; else how a symbol server knows it.</summary>
    private static (Pdb? Pdb, SymbolKey? Symbols) ReadLocal(string path)
    {
        try
        {
            using var pe = new PEReader(File.OpenRead(path));
            if (!pe.HasMetadata)
                throw new McpException($"Not a .NET assembly: {path}");

            // Read into memory, so the file stays unlocked. This checks that the PDB's id matches the assembly.
            if (pe.TryOpenAssociatedPortablePdb(path, file => File.Exists(file) ? new MemoryStream(File.ReadAllBytes(file)) : null, out var provider, out var pdbPath))
                return (new(provider!, pdbPath ?? "embedded"), null);

            var entries = pe.ReadDebugDirectory();
            var codeView = entries.FirstOrDefault(entry => entry.Type == DebugDirectoryEntryType.CodeView && entry.IsPortableCodeView);
            if (codeView.Type != DebugDirectoryEntryType.CodeView)
                return (null, null);

            var data = pe.ReadCodeViewDebugDirectoryData(codeView);
            var name = data.Path[(data.Path.LastIndexOfAny(['/', '\\']) + 1)..].ToLowerInvariant();
            var checksums = string.Join(';', entries.Where(entry => entry.Type == DebugDirectoryEntryType.PdbChecksum)
                .Select(pe.ReadPdbChecksumDebugDirectoryData)
                .Select(checksum => $"{checksum.AlgorithmName}:{Convert.ToHexStringLower(checksum.Checksum.AsSpan())}"));
            return (null, new($"{name}/{data.Guid:N}FFFFFFFF/{name}", new(data.Guid, codeView.Stamp), checksums.Length > 0 ? checksums : null));
        }
        catch (BadImageFormatException)
        {
            throw new McpException($"Not a .NET assembly: {path}");
        }
    }

    private async Task<bool> DownloadAsync(SymbolKey symbols, string file, CancellationToken cancellationToken)
    {
        // Requests share downloads, so these run without the token of the request that started them. Failures are retried later; misses are not.
        var download = _downloads.GetOrAdd(symbols.Key, _ => DownloadPdbAsync(symbols, file));
        try
        {
            return await download.WaitAsync(cancellationToken);
        }
        catch (McpException)
        {
            _downloads.TryRemove(new(symbols.Key, download));
            throw;
        }
    }

    private async Task<bool> DownloadPdbAsync(SymbolKey symbols, string file)
    {
        var errors = new List<string>();
        foreach (var server in _symbolServers)
        {
            byte[] content;
            if (!server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // A symbol store has the server's layout; a plain directory has the PDBs side by side.
                var stored = new[] { Path.Combine(server, symbols.Key), Path.Combine(server, Path.GetFileName(symbols.Key)) }.FirstOrDefault(File.Exists);
                try
                {
                    if (stored is null)
                        continue;
                    content = await File.ReadAllBytesAsync(stored);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{server}: {e.Message}");
                    continue;
                }
            }
            else
            {
                try
                {
                    content = await DownloadAsync(server);
                    if (content.Length == 0)
                        continue;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    errors.Add($"{server}: {e.Message}");
                    continue;
                }
            }

            // The id ties a PDB to its assembly build.
            try
            {
                using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(content));
                if (provider.GetMetadataReader().DebugMetadataHeader is not { } header || new BlobContentId(header.Id) != symbols.Id)
                    continue;
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            Write(file, content);
            return true;
        }

        return errors.Count == 0 ? false : throw new McpException($"Could not reach the symbol servers: {string.Join("; ", errors)}");

        // Empty for a PDB the server does not have.
        async Task<byte[]> DownloadAsync(string server)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{server}/{symbols.Key}");

            // nuget.org only serves a PDB to whoever knows its checksum.
            if (symbols.Checksums is not null)
                request.Headers.Add("SymbolChecksum", symbols.Checksums);
            using var response = await _http.SendAsync(request);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : [];
        }
    }

    private async Task<(string File, byte[] Content, bool Verified)> DownloadSourceAsync(
        Pdb pdb, DocumentHandle document, string name, string url, CancellationToken cancellationToken)
    {
        var file = CachePathOf(pdb, document, name, url);
        if (File.Exists(file))
        {
            var cached = await File.ReadAllBytesAsync(file, cancellationToken);
            return (file, cached, pdb.Matches(document, cached));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new McpException($"Only https SourceLink URLs are fetched, not {url}.");

        byte[] content;
        try
        {
            using var response = await _http.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new McpException($"{url} answered {(int)response.StatusCode} {response.ReasonPhrase}"
                    + (response.StatusCode == System.Net.HttpStatusCode.NotFound ? ": the file is not in the repository, as for files that MSBuild generates, or the repository is private." : "."));
            }

            content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new McpException($"Could not download {url}: {e.Message}");
        }

        // ponytail: only LF to CRLF is tried; add other normalizations if checksums keep failing.
        // Repositories usually store LF, which a checkout on Windows turns into the CRLF that the compiler read.
        if (!pdb.Matches(document, content) && Array.IndexOf(content, (byte)'\r') < 0 && WithCrLf(content) is var crlf && pdb.Matches(document, crlf))
            content = crlf;

        Write(file, content);
        return (file, content, pdb.Matches(document, content));
    }

    /// <summary>Where the text of a document is cached: by its checksum, so a cached copy never goes stale; else by where it comes from.</summary>
    private string CachePathOf(Pdb pdb, DocumentHandle document, string name, string? url) => Path.Combine(
        _cache,
        "sources",
        pdb.ChecksumOf(document) ?? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url ?? name))),
        name[(name.LastIndexOfAny(['/', '\\']) + 1)..]);

    // Through a temporary file, so readers never see half a file.
    private static void Write(string file, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = $"{file}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temporary, content);
        try
        {
            File.Move(temporary, file, overwrite: true);
        }
        catch (IOException)
        {
            // Another request wrote the same content, and someone reads it now.
            File.Delete(temporary);
        }
    }

    private static byte[] WithCrLf(byte[] content)
    {
        var text = new List<byte>(content.Length + content.Length / 16);
        foreach (var b in content)
        {
            if (b == '\n')
                text.Add((byte)'\r');
            text.Add(b);
        }

        return [.. text];
    }

    private static string Decode(byte[] content)
    {
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>How a symbol server knows a PDB: by file name and id; plus the checksums that nuget.org asks for.</summary>
    private sealed record SymbolKey(string Key, BlobContentId Id, string? Checksums);

    private sealed record CachedPdb(DateTime Timestamp, Pdb Pdb)
    {
        public long LastUsed { get; set; }
    }
}
