using System.Collections.Concurrent;

namespace Roslynaut.Caching;

/// <summary>Caches values for the lifetime of the process.</summary>
public sealed class InMemoryCache : ICache
{
    // ponytail: unbounded, an LRU with a size limit if a session caches enough to matter.
    private readonly ConcurrentDictionary<string, string> entries = new();

    public string GetOrAdd(string key, Func<string, string> get)
        => entries.GetOrAdd(key, get);
}
