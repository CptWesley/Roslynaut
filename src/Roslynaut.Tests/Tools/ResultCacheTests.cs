using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

public class ResultCacheTests
{
    private readonly Solution solution = new AdhocWorkspace().CurrentSolution;
    private ResultCache cache = null!;
    private int evaluated;
    private int computed;

    [SetUp]
    public void SetUp()
    {
        cache = new ResultCache();
        evaluated = 0;
        computed = 0;
    }

    /// <summary>Yields 0 to 24, counting every evaluated item.</summary>
    private IEnumerable<int> Source()
    {
        for (var i = 0; i < 25; i++)
        {
            evaluated++;
            yield return i;
        }
    }

    private Task<PagedResult<int>> GetPageAsync(int page, int pageSize) =>
        cache.GetPageAsync(solution, "key", page, pageSize, () =>
        {
            computed++;
            return Task.FromResult(Source());
        });

    [TestCase(0, 10, 0, 10, true)]
    [TestCase(1, 10, 10, 10, true)]
    [TestCase(2, 10, 20, 5, false)]
    [TestCase(4, 5, 20, 5, false)]
    [TestCase(100, 10, 0, 0, false)]
    [TestCase(1, int.MaxValue, 0, 0, false)]
    public async Task GetPage_ReturnsItemsOfPage(int page, int pageSize, int first, int count, bool hasMore)
    {
        var result = await GetPageAsync(page, pageSize);

        Assert.That(result.Items, Is.EqualTo(Enumerable.Range(first, count)));
        Assert.That(result.HasMore, Is.EqualTo(hasMore));
    }

    [TestCase(-1, 10)]
    [TestCase(0, 0)]
    public async Task GetPage_InvalidArgument_Throws(int page, int pageSize)
    {
        await Assert.ThrowsAsync<McpException>(() => GetPageAsync(page, pageSize));
    }

    [Test]
    public async Task GetPage_EvaluatesOnlyOneItemPastPage()
    {
        await GetPageAsync(0, 10);

        Assert.That(evaluated, Is.EqualTo(11));
    }

    [Test]
    public async Task GetPage_ReusesResultsAcrossPages()
    {
        await GetPageAsync(2, 10);
        var result = await GetPageAsync(1, 10);

        Assert.That(result.Items, Is.EqualTo(Enumerable.Range(10, 10)));
        Assert.That(computed, Is.EqualTo(1));
        Assert.That(evaluated, Is.EqualTo(25));
    }

    [Test]
    public async Task GetPage_FailedEvaluation_IsNotCached()
    {
        var fail = true;
        IEnumerable<int> Flaky()
        {
            yield return 1;
            if (fail)
                throw new InvalidOperationException();
            yield return 2;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetPageAsync(solution, "flaky", 0, 10, () => Task.FromResult(Flaky())));
        fail = false;
        var result = await cache.GetPageAsync(solution, "flaky", 0, 10, () => Task.FromResult(Flaky()));

        Assert.That(result.Items, Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public async Task TotalCount_LazySource_IsKnownOnceExhausted()
    {
        var first = await GetPageAsync(0, 10);
        var last = await GetPageAsync(2, 10);

        Assert.That(first.TotalCount, Is.Null);
        Assert.That(last.TotalCount, Is.EqualTo(25));
    }

    private static IEnumerable<TestCaseData> CountedSources()
    {
        yield return new TestCaseData(Enumerable.Range(0, 25).ToList(), 25).SetArgDisplayNames("List");
        yield return new TestCaseData(Enumerable.Range(0, 25).ToList().Select(i => i * 2), 25).SetArgDisplayNames("Select over List");
        yield return new TestCaseData(Enumerable.Range(0, 25).ToList().Where(i => i % 2 == 0), null).SetArgDisplayNames("Where over List");
    }

    [TestCaseSource(nameof(CountedSources))]
    public async Task TotalCount_IsKnownUpFrontWhenCheap(IEnumerable<int> source, int? totalCount)
    {
        var result = await cache.GetPageAsync(solution, "counted", 0, 10, () => Task.FromResult(source));

        Assert.That(result.TotalCount, Is.EqualTo(totalCount));
    }
}
