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

        sequence.Slice(2, 3).Should().Equal(2, 3, 4);
    }

    [Test]
    public void Slice_EvaluatesOnlyUpToEndOfRange()
    {
        var sequence = new EvaluatedEnumerable<int>(Source());

        sequence.Slice(2, 3);

        evaluated.Should().Be(5);
    }

    [Test]
    public void Slice_ReusesEvaluatedItems()
    {
        var sequence = new EvaluatedEnumerable<int>(Source());
        sequence.Slice(2, 3);

        var slice = sequence.Slice(0, 2);

        slice.Should().Equal(0, 1);
        evaluated.Should().Be(5);
    }

    [TestCase(8, 100, new[] { 8, 9 })]
    [TestCase(20, 5, new int[0])]
    public void Slice_PastEnd_ReturnsAvailableItems(int start, int length, int[] expected)
    {
        var sequence = new EvaluatedEnumerable<int>(Source());

        sequence.Slice(start, length).Should().Equal(expected);
    }

    [Test]
    public void Slice_LengthUpToIntMaxValue_DoesNotOverflow()
    {
        var sequence = new EvaluatedEnumerable<int>(Source());

        sequence.Slice(1, int.MaxValue).Should().HaveCount(9);
    }

    [TestCase(-1, 1)]
    [TestCase(0, -1)]
    public void Slice_NegativeArgument_Throws(int start, int length)
    {
        var sequence = new EvaluatedEnumerable<int>(Source());

        sequence.Invoking(s => s.Slice(start, length)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void Enumerate_EvaluatesEveryItemOnce()
    {
        var sequence = new EvaluatedEnumerable<int>(Source());
        sequence.Slice(0, 5);

        sequence.Should().Equal(Enumerable.Range(0, 10));
        sequence.Should().Equal(Enumerable.Range(0, 10));
        evaluated.Should().Be(10);
    }

    [Test]
    public void Enumerate_PartiallyThenFully_EvaluatesEveryItemOnce()
    {
        var sequence = new EvaluatedEnumerable<int>(Source());

        sequence.Take(3).Should().Equal(0, 1, 2);
        evaluated.Should().Be(3);
        sequence.Count().Should().Be(10);
        evaluated.Should().Be(10);
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

        first.Current.Should().Be(1);
        second.Current.Should().Be(0);
        evaluated.Should().Be(2);
    }

    [Test]
    public void NullSource_IsEmpty()
    {
        var sequence = new EvaluatedEnumerable<int>(null);

        sequence.Should().BeEmpty();
        sequence.Slice(0, 10).Should().BeEmpty();
    }
}
