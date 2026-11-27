using System.Security.Cryptography;
using System.Text;
using LiteDB;

namespace Roslynaut.Caching;

/// <summary>
/// Caches values in a LiteDB file, so they outlive the process and are shared by every server that uses the same file.
/// </summary>
/// <remarks>
/// Failures (a file that cannot be created, a corrupt or locked database) are written to stderr, never stdout, which carries the
/// MCP protocol in stdio mode; the value is then computed and not cached.
/// </remarks>
public sealed class LiteDbCache : ICache, IDisposable
{
    // ponytail: grows without limit; a version in the file's path is what invalidates it. Evict by last use if it grows too big.
    private readonly ThreadLocal<LiteDatabase?> databases;

    private sealed class Entry
    {
        [BsonId]
        public string Id { get; set; } = "";

        public string Value { get; set; } = "";
    }

    public LiteDbCache(string fileName)
    {
        var connection = new ConnectionString
        {
            Filename = fileName,
            // Several servers, one per agent session, open the same file. Shared opens it for each operation, under a mutex.
            Connection = ConnectionType.Shared,
            AutoRebuild = true,
            Upgrade = true,
        };

        // A shared engine is not safe to use from several threads at once, so each thread gets its own.
        databases = new(() => Try(() =>
        {
            if (Path.GetDirectoryName(fileName) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            return new LiteDatabase(connection);
        }), trackAllValues: true);
    }

    public string GetOrAdd(string key, Func<string, string> get)
    {
        var entries = databases.Value?.GetCollection<Entry>("entries");
        // An index key holds at most 1023 bytes, so the key is stored by its hash.
        var id = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

        if (entries is not null && Try(() => entries.FindById(id)) is { } found)
        {
            return found.Value;
        }

        var value = get(key);

        if (entries is not null)
        {
            Try(() => entries.Upsert(new Entry { Id = id, Value = value }));
        }

        return value;
    }

    public void Dispose()
    {
        foreach (var database in databases.Values)
        {
            database?.Dispose();
        }

        databases.Dispose();
    }

    private static T? Try<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Roslynaut cache: {ex}");
            return default;
        }
    }
}
