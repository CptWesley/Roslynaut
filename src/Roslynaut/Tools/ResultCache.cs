using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using Roslynaut.Util;

namespace Roslynaut.Tools;

/// <summary>One page of a tool's results.</summary>
/// <param name="TotalCount">Null while the number of results is not known yet.</param>
public sealed record PagedResult<T>(IEnumerable<T> Items, int Page, int PageSize, bool HasMore, int? TotalCount);

/// <summary>
/// Caches the results of tools per solution snapshot. Every file change produces a new snapshot, so cached results
/// always match the current code, and results for older snapshots are collected together with them.
/// Results are evaluated lazily, only as far as the requested pages need.
/// </summary>
public sealed class ResultCache
{
    private readonly ConditionalWeakTable<Solution, ConcurrentDictionary<string, object>> _results = new();

    /// <summary>
    /// Returns one page of the results stored under <paramref name="key"/>: the tool name plus the arguments that shape its results.
    /// Computes and stores them first on a miss; failures are not stored. Evaluation of the results continues in later requests,
    /// so their lazy parts should not observe the cancellation token of the request that computed them.
    /// </summary>
    public async Task<PagedResult<T>> GetPageAsync<T>(Solution solution, string key, int page, int pageSize, Func<Task<IEnumerable<T>>> compute)
    {
        if (page < 0 || pageSize < 1)
            throw new McpException("page must be at least 0 and pageSize at least 1.");

        var results = _results.GetOrCreateValue(solution);
        if (!results.TryGetValue(key, out var cached))
            results[key] = cached = new EvaluatedEnumerable<T>(await compute());

        var items = (EvaluatedEnumerable<T>)cached;
        var start = (int)Math.Min((long)page * pageSize, int.MaxValue);
        List<T> slice;
        int? totalCount;

        // Concurrent requests can share the same results, and EvaluatedEnumerable is not thread-safe.
        lock (items)
        {
            try
            {
                // One extra item tells whether a next page exists, and on the last page exhausts the source, so the total is known.
                slice = items.Slice(start, (int)Math.Min(pageSize + 1L, int.MaxValue));
                totalCount = items.TotalCount;
            }
            catch
            {
                results.TryRemove(new(key, cached));
                items.Dispose();
                throw;
            }
        }

        var hasMore = slice.Count > pageSize;
        if (hasMore)
            slice.RemoveAt(pageSize);

        return new(slice, page, pageSize, hasMore, totalCount);
    }
}
