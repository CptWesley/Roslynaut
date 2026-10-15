using System.Collections;

namespace Roslynaut.Util;

/// <summary>
/// Wraps a lazily evaluated sequence and caches every element as it is produced,
/// so the source is enumerated at most once, no matter how often the result is sliced or enumerated.
/// </summary>
/// <remarks>Not thread-safe.</remarks>
public class EvaluatedEnumerable<T> : IEnumerable<T>, IDisposable
{
    private readonly List<T> cached = new();
    private readonly int? count;
    private IEnumerator<T>? source;

    public EvaluatedEnumerable(IEnumerable<T>? source)
    {
        this.source = source?.GetEnumerator();

        if (source is IReadOnlyCollection<T> tc)
        {
            count = tc.Count;
        }
        else if (source is ICollection c)
        {
            count = c.Count;
        }
    }

    public int EvaluatedCount => cached.Count;

    public int? TotalCount => source is null
        ? EvaluatedCount
        : count;

    public List<T> Slice(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        // Evaluate only up to the end of the requested range. Computed as long, since start + length may overflow.
        var end = (int)Math.Min((long)start + length, int.MaxValue);
        while (cached.Count < end && TryEvaluateNext())
        {
        }

        if (start >= cached.Count)
        {
            return [];
        }

        return cached.GetRange(start, Math.Min(length, cached.Count - start));
    }

    public IEnumerator<T> GetEnumerator()
    {
        // Index based, so the cache may grow (through Slice or other enumerators) while enumerating.
        for (var i = 0; i < cached.Count || TryEvaluateNext(); i++)
        {
            yield return cached[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    private bool TryEvaluateNext()
    {
        if (source is null)
        {
            return false;
        }

        if (source.MoveNext())
        {
            cached.Add(source.Current);
            return true;
        }

        source.Dispose();
        source = null;
        return false;
    }

    public void Dispose()
    {
        try
        {
            source?.Dispose();
        }
        catch
        {
            // Do nothing.
        }
    }
}
