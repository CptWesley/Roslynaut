using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>resolve_stack_trace: frames of a trace from another machine, compiler-generated ones included, mapped to current source.</summary>
public class StackTraceTests
{
    private static readonly Microsoft.CodeAnalysis.Solution Shop = WithPath(TestSolution.Create("""
        namespace Shop
        {
            public class Orders
            {
                public async System.Threading.Tasks.Task SubmitAsync()
                {
                    await System.Threading.Tasks.Task.Yield();
                    Check();
                    void Check() => throw new System.InvalidOperationException();
                }
                public void Each(int[] items)
                {
                    System.Array.ForEach(items, item => throw new System.Exception());
                }
                public class Line { public Line(int n) { } }
            }
        }
        """));

    private static Microsoft.CodeAnalysis.Solution WithPath(Microsoft.CodeAnalysis.Solution solution) =>
        solution.WithDocumentFilePath(solution.Projects.Single().DocumentIds.Single(), Path.GetFullPath("Orders.cs"));

    [Test]
    public async Task Frames_MapToMembersAndLines()
    {
        var frames = await ResolveStackTraceTool.ResolveAsync(Shop, """
            System.InvalidOperationException: Operation is not valid due to the current state of the object.
               at Shop.Orders.<SubmitAsync>g__Check|0_0() in C:\build\src\Orders.cs:line 9
               at Shop.Orders.<SubmitAsync>d__0.MoveNext() in C:\build\src\Orders.cs:line 8
            --- End of stack trace from previous location ---
               at Shop.Orders.<>c.<Each>b__1_0(Int32 item) in C:\build\src\Orders.cs:line 13
               at System.Array.ForEach[T](T[] array, Action`1 action)
               at Shop.Orders.Line..ctor(Int32 n)
               at Shop.Orders.Each(Int32[] items) in C:\build\src\Orders.cs:line 40
            """, CancellationToken.None);

        frames.Select(frame => (frame.Symbol, frame.Location?.Line, frame.Note is null)).Should().Equal(
            ("Shop.Orders.SubmitAsync().Check", 9, true),
            ("Shop.Orders.SubmitAsync", 8, true),
            ("Shop.Orders.Each", 13, true),
            (null, null, true),
            ("Shop.Orders.Line..ctor", 15, false),
            ("Shop.Orders.Each", 11, false));
        frames[0].Location!.Text.Should().Be("void Check() => throw new System.InvalidOperationException();");
        frames[0].Location!.FilePath.Should().Be(Path.GetFullPath("Orders.cs"));
        frames[5].Note.Should().Contain("past the end");
    }

    [Test]
    public async Task LineOutsideTheMember_IsFlagged()
    {
        var frame = (await ResolveStackTraceTool.ResolveAsync(Shop, "at Shop.Orders.Each(Int32[] items) in Orders.cs:line 5", CancellationToken.None)).Single();

        (frame.Symbol, frame.Location!.Line).Should().Be(("Shop.Orders.Each", 5));
        frame.Note.Should().Contain("not in Shop.Orders.Each");
    }

    [TestCase("Shop.Orders.<<SubmitAsync>b__0>d.MoveNext()", "Shop.Orders.SubmitAsync", null)]
    [TestCase("Program.<<Main>$>g__Local|0_0(Int32 x)", "Program.<Main>$.Local", null)]
    [TestCase("Program.<Main>$(String[] args)", "Program.<Main>$", 1)]
    [TestCase("Shop.Box`1.Get[T](T value, Int32 count)", "Shop.Box.Get", 2)]
    [TestCase("Shop.Orders.<>c__DisplayClass2_0.<Each>b__0()", "Shop.Orders.Each", null)]
    [TestCase("Shop.Orders..cctor()", "Shop.Orders..cctor", 0)]
    public void PathOf_UndoesGeneratedNames(string method, string path, int? parameters)
    {
        ResolveStackTraceTool.PathOf(method).Should().Be((path, parameters));
    }
}
