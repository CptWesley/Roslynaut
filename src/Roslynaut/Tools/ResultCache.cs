using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using ModelContextProtocol;

namespace Roslynaut.Tools;

/// <summary>One page of a tool's results.</summary>
public sealed record PagedResult<T>(IEnumerable<T> Items, int Page, int PageSize, int TotalCount)
{
    public bool HasMore => (long)(Page + 1) * PageSize < TotalCount;
}

/// <summary>
/// Caches the full result lists of tools per solution snapshot. Every file change produces a new snapshot, so cached results
/// always match the current code, and results for older snapshots are collected together with them.
/// </summary>
public sealed class ResultCache
{
    private readonly ConditionalWeakTable<Solution, ConcurrentDictionary<string, object>> _results = new();

    /// <summary>
    /// Returns one page of the results stored under <paramref name="key"/>: the tool name plus the arguments that shape its results.
    /// Computes and stores them first on a miss; failures are not stored.
    /// </summary>
    public async Task<PagedResult<T>> GetPageAsync<T>(Solution solution, string key, int page, int pageSize, Func<Task<IEnumerable<T>>> compute)
    {
        if (page < 0 || pageSize < 1)
            throw new McpException("page must be at least 0 and pageSize at least 1.");

        var results = _results.GetOrCreateValue(solution);
        if (!results.TryGetValue(key, out var cached))
            results[key] = cached = (await compute()).ToList();

        var items = (List<T>)cached;
        return new(items.Skip((int)Math.Min((long)page * pageSize, int.MaxValue)).Take(pageSize), page, pageSize, items.Count);
    }
}
