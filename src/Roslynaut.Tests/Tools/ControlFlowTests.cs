using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>control_flow: a method's blocks, branches, exits and regions, and the conditions on the paths to a line.</summary>
public class ControlFlowTests
{
    private static readonly Microsoft.CodeAnalysis.Solution Gate = TestSolution.Create("""
        public class Gate
        {
            public int Check(int x, string? name)
            {
                if (x < 0)
                    throw new System.ArgumentException();
                if (name == null)
                    return 0;
                try { return x / name.Length; }
                catch (System.DivideByZeroException) { return -1; }
            }
        }
        """);

    private static async Task<ControlFlowTool.ControlFlow> FlowAsync(int? toLine = null) =>
        await ControlFlowTool.AnalyzeAsync(Gate, (await SymbolTree.ResolveAsync(Gate, "Gate.Check", CancellationToken.None))[0], toLine, CancellationToken.None);

    [Test]
    public async Task Blocks_HaveConditionsExitsAndRegions()
    {
        var flow = await FlowAsync();

        flow.Blocks[0].Kind.Should().Be("Entry");
        flow.Blocks[^1].Kind.Should().Be("Exit");
        var check = flow.Blocks.Single(block => block.Condition == "x < 0");
        flow.Blocks[check.IfTrue!.Value].Should().Match<ControlFlowTool.FlowBlock>(block => block.Exit == "Throw" && block.Lines == "6");
        flow.Blocks.Should().Contain(block => block.Region == "Catch (System.DivideByZeroException)" && block.Exit == "Return" && block.Returns == "-1");
        flow.Blocks.Should().Contain(block => block.Region == "Try" && block.Returns == "x / name.Length");
        flow.PathsTo.Should().BeNull();
    }

    [TestCase(6, "x < 0 is true")]
    [TestCase(8, "x < 0 is false", "name == null is true")]
    public async Task PathsTo_ALine_AreTheConditionsOnTheWay(int line, params string[] conditions)
    {
        (await FlowAsync(line)).PathsTo.Should().ContainSingle().Which.Should().Equal(conditions);
    }
}
