using ModelContextProtocol;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

public class EvaluateTests
{
    private static Task<EvaluateTool.Evaluation> RunAsync(string code, params string[] references) =>
        EvaluateTool.RunAsync(references, code, TimeSpan.FromSeconds(30), CancellationToken.None);

    [Test]
    public async Task Evaluate_ReturnsValueAndOutput()
    {
        var result = await RunAsync("Console.WriteLine(\"hi\"); new[] { 1, 2 }.Sum() + 2");

        result.Should().Match<EvaluateTool.Evaluation>(evaluation =>
            evaluation.Value == "5" && evaluation.Type == "System.Int32" && evaluation.Output.Trim() == "hi" && evaluation.Error == null);
    }

    [Test]
    public async Task Evaluate_ReportsCompilationErrorsAndExceptions()
    {
        (await RunAsync("missing + 1")).Error.Should().Contain("CS0103");
        (await RunAsync("throw new InvalidOperationException(\"boom\");")).Error.Should().Contain("InvalidOperationException: boom");
    }

    [Test]
    public async Task Evaluate_UsesProjectCode()
    {
        using var assemblies = new AssemblyLoader();
        var compilation = (await TestSolution.Create("namespace Calc { public class Adder { public int Add(int a, int b) => a + b; } }").Projects.Single().GetCompilationAsync())!;
        var compiled = await assemblies.CompileAsync(compilation);

        (await RunAsync("new Calc.Adder().Add(2, 3)", compiled.Path.Value)).Value.Should().Be("5");
    }

    [Test]
    public async Task Evaluate_StopsAtTimeout()
    {
        await FluentActions.Awaiting(() => EvaluateTool.RunAsync([], "while (true) { }", TimeSpan.FromSeconds(3), CancellationToken.None))
            .Should().ThrowAsync<McpException>().WithMessage("*did not finish*");
    }
}
