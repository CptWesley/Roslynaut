using Roslynaut.Caching;

namespace Roslynaut.Tests.Caching;

public class CacheTests
{
    private readonly List<string> computed = new();
    private string directory = null!;

    [SetUp]
    public void SetUp()
    {
        computed.Clear();
        directory = Path.Combine(Path.GetTempPath(), "Roslynaut.Tests", Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private string Compute(string key)
    {
        lock (computed)
        {
            computed.Add(key);
        }

        return $"value of {key}";
    }

    private string File => Path.Combine(directory, "cache.db");

    [Test]
    public void InMemory_ComputesEachKeyOnce()
    {
        var cache = new InMemoryCache(10);

        cache.GetOrAdd("a", Compute).Should().Be("value of a");
        cache.GetOrAdd("a", Compute).Should().Be("value of a");
        cache.GetOrAdd("b", Compute).Should().Be("value of b");

        computed.Should().Equal("a", "b");
    }

    [Test]
    public void InMemory_EvictsLeastRecentlyUsedBeyondCapacity()
    {
        var cache = new InMemoryCache(3);
        cache.GetOrAdd("a", Compute);

        for (var i = 0; i < 100; i++)
        {
            cache.GetOrAdd($"key {i}", Compute);
        }

        cache.GetOrAdd("a", Compute);

        computed.Should().HaveCount(102).And.StartWith("a").And.EndWith("a");
    }

    [Test]
    public void Layered_WithoutCaches_Computes()
    {
        var cache = new LayeredCache();

        cache.GetOrAdd("a", Compute).Should().Be("value of a");
        cache.GetOrAdd("a", Compute).Should().Be("value of a");

        computed.Should().Equal("a", "a");
    }

    [Test]
    public void Layered_Miss_AddsToEveryCache()
    {
        var first = new InMemoryCache(10);
        var second = new InMemoryCache(10);

        new LayeredCache(first, second).GetOrAdd("a", Compute).Should().Be("value of a");

        first.GetOrAdd("a", _ => "other").Should().Be("value of a");
        second.GetOrAdd("a", _ => "other").Should().Be("value of a");
        computed.Should().Equal("a");
    }

    [Test]
    public void Layered_HitInLaterCache_AddsToEarlierWithoutComputing()
    {
        var first = new InMemoryCache(10);
        var second = new InMemoryCache(10);
        second.GetOrAdd("a", _ => "cached");

        new LayeredCache(first, second).GetOrAdd("a", Compute).Should().Be("cached");

        first.GetOrAdd("a", _ => "other").Should().Be("cached");
        computed.Should().BeEmpty();
    }

    [Test]
    public void Layered_HitInFirstCache_SkipsLaterCaches()
    {
        var first = new InMemoryCache(10);
        first.GetOrAdd("a", _ => "cached");
        var later = new LayeredCache(new InMemoryCache(10), new Unusable());

        new LayeredCache(first, later).GetOrAdd("a", Compute).Should().Be("cached");

        computed.Should().BeEmpty();
    }

    [Test]
    public void LiteDb_KeepsValuesAcrossInstances()
    {
        using (var cache = new LiteDbCache(File))
        {
            cache.GetOrAdd("a", Compute).Should().Be("value of a");
            cache.GetOrAdd("a", Compute).Should().Be("value of a");
        }

        using (var cache = new LiteDbCache(File))
        {
            cache.GetOrAdd("a", Compute).Should().Be("value of a");
            cache.GetOrAdd("b", Compute).Should().Be("value of b");
        }

        computed.Should().Equal("a", "b");
    }

    // Beyond the 1023 bytes an index key can hold.
    [Test]
    public void LiteDb_CachesLongKeysAndValues()
    {
        var key = new string('k', 10_000);
        var value = new string('v', 1_000_000);
        using var cache = new LiteDbCache(File);

        cache.GetOrAdd(key, _ => value).Should().Be(value);
        cache.GetOrAdd(key, Compute).Should().Be(value);

        computed.Should().BeEmpty();
    }

    [Test]
    public void LiteDb_SharesFileBetweenInstancesAndThreads()
    {
        using var one = new LiteDbCache(File);
        using var other = new LiteDbCache(File);

        Parallel.For(0, 100, i => (i % 2 == 0 ? one : other).GetOrAdd($"key {i % 10}", Compute).Should().Be($"value of key {i % 10}"));

        one.GetOrAdd("key 3", _ => "other").Should().Be("value of key 3");
        other.GetOrAdd("key 4", _ => "other").Should().Be("value of key 4");
    }

    // The directory cannot be created where a file is.
    [Test]
    public void LiteDb_Unusable_ComputesInstead()
    {
        Directory.CreateDirectory(directory);
        var blocked = Path.Combine(directory, "blocked");
        System.IO.File.WriteAllText(blocked, "");
        using var cache = new LiteDbCache(Path.Combine(blocked, "cache.db"));

        cache.GetOrAdd("a", Compute).Should().Be("value of a");
        cache.GetOrAdd("a", Compute).Should().Be("value of a");

        computed.Should().Equal("a", "a");
    }

    private sealed class Unusable : ICache
    {
        public string GetOrAdd(string key, Func<string, string> get) => throw new InvalidOperationException("Not to be asked.");
    }
}
