using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Roslynaut.Caching;

/// <summary>
/// A cache of string results by string key, for tool calls that are slow and always give the same result for the same key.
/// </summary>
/// <remarks>
/// The key has to name everything the result depends on, such as an assembly's MVID besides its path, as entries can outlive the
/// process. A cache is best effort: when it fails, it computes the value instead.
/// </remarks>
public interface ICache
{
    /// <summary>Returns the value cached for <paramref name="key"/>, or computes it with <paramref name="get"/> and caches it.</summary>
    /// <remarks><paramref name="get"/> can run more than once for a key that threads ask for at the same time.</remarks>
    string GetOrAdd(string key, Func<string, string> get);
}

public static class CacheExtensions
{
    /// <summary>Returns the value cached for <paramref name="key"/>, or computes it with <paramref name="get"/> and caches it, as JSON.</summary>
    public static T GetOrAdd<T>(this ICache cache, string key, Func<T> get) =>
        JsonSerializer.Deserialize<T>(cache.GetOrAdd(key, _ => JsonSerializer.Serialize(get())))!;

    // ponytail: remembers every file it hashed for the life of the process; evict the oldest if long sessions hash a lot of them.
    private static readonly ConcurrentDictionary<(string Path, DateTime Written, long Length), string> Hashes = new();

    /// <summary>
    /// The SHA-256 of a file's content, for keys of values computed from it: unlike its MVID, it changes when an IL weaver rewrites it.
    /// Remembered by the file's time and size, so a key over a framework's directory costs a hash per file once.
    /// </summary>
    public static string HashOf(string path)
    {
        var file = new FileInfo(path);
        return Hashes.GetOrAdd((file.FullName, file.LastWriteTimeUtc, file.Length), _ =>
        {
            using var stream = file.OpenRead();
            return Convert.ToHexString(SHA256.HashData(stream));
        });
    }
}
