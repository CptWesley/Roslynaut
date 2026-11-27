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

    [Test]
    public void Typed_RoundTripsRecordsThroughJson()
    {
        var cache = new CountingCache(new InMemoryCache(10));
        var layout = new Roslynaut.Tools.TypeLayout("T", 24, 1, ".NET 10", [new("x", "System.Int32", 0, 4, 0), new("(padding)", null, 4, 4, 0)]);

        cache.GetOrAdd("a", () => layout);
        var cached = cache.GetOrAdd<Roslynaut.Tools.TypeLayout>("a", () => throw new InvalidOperationException("Not to be computed."));

        cached.Should().BeEquivalentTo(layout);
        cache.Computed.Should().Be(1);
    }

    // The JIT inlines from the assemblies next to a file, and ILSpy resolves its references there.
    [Test]
    public void ContentKey_OfFile_ChangesWithAssembliesNextToIt()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Roslynaut.Tests.dll");
        System.IO.File.Copy(typeof(CacheTests).Assembly.Location, path);
        var dependency = Path.Combine(directory, "Dependency.dll");
        System.IO.File.WriteAllText(dependency, "one");
        using var assemblies = new AssemblyLoader();
        var key = AssemblyLoader.ContentKey(assemblies.Read(path));

        System.IO.File.WriteAllText(Path.Combine(directory, "notes.txt"), "not loaded");
        AssemblyLoader.ContentKey(assemblies.Read(path)).Should().Be(key);

        System.IO.File.WriteAllText(dependency, "other");
        AssemblyLoader.ContentKey(assemblies.Read(path)).Should().NotBe(key);
    }

    // Each emit writes a file of another name, beside every other compilation's.
    [Test]
    public async Task ContentKey_OfCompilation_IsTheSameForEveryEmit()
    {
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Lib",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("public class C { }")],
            [Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        using var one = new AssemblyLoader();
        using var other = new AssemblyLoader();

        AssemblyLoader.ContentKey(await one.CompileAsync(compilation)).Should().Be(AssemblyLoader.ContentKey(await other.CompileAsync(compilation)));
    }

    private sealed class Unusable : ICache
    {
        public string GetOrAdd(string key, Func<string, string> get) => throw new InvalidOperationException("Not to be asked.");
    }
}

/// <summary>Counts the values its cache computes.</summary>
internal sealed class CountingCache(ICache inner) : ICache
{
    public int Computed { get; private set; }

    public string GetOrAdd(string key, Func<string, string> get) => inner.GetOrAdd(key, k =>
    {
        Computed++;
        return get(k);
    });
}
