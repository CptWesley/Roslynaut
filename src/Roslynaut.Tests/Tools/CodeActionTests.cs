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

    [Test]
    public async Task Selection_OffersExtractMethod()
    {
        const string code = """
            class C
            {
                int M(int a)
                {
                    var b = a + 1;
                    var c = b * 2;
                    return c;
                }
            }
            """;
        var solution = TestSolution.Create(code);
        var document = solution.Projects.Single().Documents.Single();
        var span = CodeActionsTool.SpanOf(await document.GetTextAsync(), "Test.cs", line: 5, column: null, endLine: 6, endColumn: null);

        var extract = (await codeActions.FindAsync(document, span, CancellationToken.None)).Should().Contain(action => action.Title == "Extract method").Subject;
        var (_, changed) = await codeActions.ApplyAsync(solution, extract.Id, CancellationToken.None);
        (await changed.Projects.Single().Documents.Single().GetTextAsync()).ToString().Should().Contain("NewMethod(a)");
    }

    // They need an IDE dialog for their options, so applying them would fail.
    [Test]
    public async Task ActionsWithOptions_AreLeftOut()
    {
        var (_, actions) = await FindAsync("class C { public void M(int a, int b) { } void N() { M(1, 2); } }", "M(int");

        actions.Should().NotBeEmpty().And.NotContain(action => action.Title.StartsWith("Change signature"));
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
