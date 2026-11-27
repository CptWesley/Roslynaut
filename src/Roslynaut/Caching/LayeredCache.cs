namespace Roslynaut.Caching;

/// <summary>
/// Looks a key up in each cache in turn, fastest first, and computes it only when none has it.
/// A value found in a later cache, or computed, is added to every cache before it.
/// </summary>
public sealed class LayeredCache(params IEnumerable<ICache> caches) : ICache
{
    private readonly ICache[] caches = caches.ToArray();

    public string GetOrAdd(string key, Func<string, string> get)
    {
        // Each cache computes a miss by asking the caches after it, the last one by calling get.
        for (var i = caches.Length - 1; i >= 0; i--)
        {
            var cache = caches[i];
            var next = get;
            get = k => cache.GetOrAdd(k, next);
        }

        return get(key);
    }
}
