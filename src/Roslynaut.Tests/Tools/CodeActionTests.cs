using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

public class CodeActionTests
{
    private readonly CodeActionService codeActions = new();

    /// <summary>The actions at the first occurrence of <paramref name="at"/> in the code.</summary>
    private async Task<(Solution Solution, List<CodeActionEntry> Actions)> FindAsync(string code, string at)
    {
        var solution = TestSolution.Create(code);
        var document = solution.Projects.Single().Documents.Single();
        return (solution, await codeActions.FindAsync(document, new TextSpan(code.IndexOf(at, StringComparison.Ordinal), 0), CancellationToken.None));
    }

    [Test]
    public async Task Fix_AddsMissingUsing()
    {
        const string code = "class C { void M() { var list = new List<int>(); } }";
        var (solution, actions) = await FindAsync(code, "List<int>");

        var addUsing = actions.Should().Contain(action => action.Title == "using System.Collections.Generic;").Subject;
        addUsing.Should().Match<CodeActionEntry>(action => action.Kind == "Fix" && action.DiagnosticId == "CS0246");

        var (_, changed) = await codeActions.ApplyAsync(solution, addUsing.Id, CancellationToken.None);
        (await changed.Projects.Single().Documents.Single().GetTextAsync()).ToString().Should().StartWith("using System.Collections.Generic;");
    }

    [Test]
    public async Task Refactoring_IsOfferedAtPosition_AndApplies()
    {
        var (solution, actions) = await FindAsync("class C { void M(bool b) { if (b) { M(true); } else { M(false); } } }", "if (b)");

        var invert = actions.Should().ContainSingle(action => action.Title == "Invert if").Subject;
        invert.Kind.Should().Be("Refactoring");

        var (_, changed) = await codeActions.ApplyAsync(solution, invert.Id, CancellationToken.None);
        (await changed.Projects.Single().Documents.Single().GetTextAsync()).ToString().Should().Contain("if (!b)");
    }

    // Ids belong to the snapshot they were found for; once the code changes, they are gone.
    [Test]
    public async Task Apply_OnOtherSnapshot_Throws()
    {
        var (_, actions) = await FindAsync("class C { void M(bool b) { if (b) { M(true); } } }", "if (b)");

        await codeActions.Awaiting(service => service.ApplyAsync(TestSolution.Create("class C { }"), actions[0].Id, CancellationToken.None))
            .Should().ThrowAsync<McpException>().WithMessage("*Ask code_actions again*");
    }
}
