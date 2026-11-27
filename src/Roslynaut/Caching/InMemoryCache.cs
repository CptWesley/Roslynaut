using BitFaster.Caching.Lru;

namespace Roslynaut.Caching;

/// <summary>Caches the <paramref name="capacity"/> most recently used values for the lifetime of the process.</summary>
public sealed class InMemoryCache(int capacity) : ICache
{
    // ponytail: bounded by count, not size; weigh values by length if a few big ones crowd memory.
    private readonly ConcurrentLru<string, string> entries = new(capacity);

    public string GetOrAdd(string key, Func<string, string> get)
        => entries.GetOrAdd(key, get);
}
