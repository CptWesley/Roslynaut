using Roslynaut.Util;

namespace Roslynaut.Tests.Util;

public class EvaluatedEnumerableTests
{
    private int evaluated;

    [SetUp]
    public void SetUp() => evaluated = 0;

    /// <summary>Yields 0 to 9, counting every evaluated item.</summary>
    private IEnumerable<int> Source()
    {
        for (var i = 0; i < 10; i++)
        {
            evaluated++;
            yield return i;
        }
    }

    [Test]
    public void Slice_ReturnsItemsFromStart()
    {
        var sequence = new EvaluatedEnumerable<int>(Source());

        Assert.That(sequence.Slice(2, 3), Is.EqualTo(new[] { 2, 3, 4 }));
    }

    [Test]
    public void Slice_EvaluatesOnlyUpToEndOfRange()
    {
        var sequence = new EvaluatedEnumerable<int>(Source());

        sequence.Slice(2, 3);

        Assert.That(evaluated, Is.EqualTo(5));
    }

    [Test]
    public void Slice_ReusesEvaluatedItems()
    {
        var sequence = new EvaluatedEnumerable<int>(Source());
        sequence.Slice(2, 3);

        var slice = sequence.Slice(0, 2);

        Assert.That(slice, Is.EqualTo(new[] { 0, 1 }));
        Assert.That(evaluated, Is.EqualTo(5));
    }

    [TestCase(8, 100, new[] { 8, 9 })]
    [TestCase(20, 5, new int[0])]
    public void Slice_PastEnd_ReturnsAvailableItems(int start, int length, int[] expected)
    {
        var sequence = new EvaluatedEnumerable<int>(Source());

        Assert.That(sequence.Slice(start, length), Is.EqualTo(expected));
    }

    [Test]
    public void Slice_LengthUpToIntMaxValue_DoesNotOverflow()
    {
        var sequence = new EvaluatedEnumerable<int>(Source());

        Assert.That(sequence.Slice(1, int.MaxValue), Has.Count.EqualTo(9));
    }

    [TestCase(-1, 1)]
    [TestCase(0, -1)]
    public void Slice_NegativeArgument_Throws(int start, int length)
    {
        var sequence = new EvaluatedEnumerable<int>(Source());

        Assert.That(() => sequence.Slice(start, length), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Enumerate_EvaluatesEveryItemOnce()
    {
        var sequence = new EvaluatedEnumerable<int>(Source());
        sequence.Slice(0, 5);

        Assert.That(sequence, Is.EqualTo(Enumerable.Range(0, 10)));
        Assert.That(sequence, Is.EqualTo(Enumerable.Range(0, 10)));
        Assert.That(evaluated, Is.EqualTo(10));
    }

    [Test]
    public void Enumerate_PartiallyThenFully_EvaluatesEveryItemOnce()
    {
        var sequence = new EvaluatedEnumerable<int>(Source());

        Assert.That(sequence.Take(3), Is.EqualTo(new[] { 0, 1, 2 }));
        Assert.That(evaluated, Is.EqualTo(3));
        Assert.That(sequence.Count(), Is.EqualTo(10));
        Assert.That(evaluated, Is.EqualTo(10));
    }

    [Test]
    public void Enumerate_InterleavedEnumerators_ShareEvaluation()
    {
        var sequence = new EvaluatedEnumerable<int>(Source());
        using var first = sequence.GetEnumerator();
        using var second = sequence.GetEnumerator();

        first.MoveNext();
        first.MoveNext();
        second.MoveNext();

        Assert.That(first.Current, Is.EqualTo(1));
        Assert.That(second.Current, Is.EqualTo(0));
        Assert.That(evaluated, Is.EqualTo(2));
    }

    [Test]
    public void NullSource_IsEmpty()
    {
        var sequence = new EvaluatedEnumerable<int>(null);

        Assert.That(sequence, Is.Empty);
        Assert.That(sequence.Slice(0, 10), Is.Empty);
    }
}
