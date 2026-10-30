using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>benchmark: cases written into a BenchmarkDotNet program that references the project, run with a short job.</summary>
[NonParallelizable]
public class BenchmarkTests
{
    [Test]
    public void Program_KeepsExpressionsAndRunsStatements_FirstIsTheBaseline()
    {
        var code = BenchmarkTool.ProgramOf(new Dictionary<string, string> { ["Concat"] = "string.Concat(a, b)", ["Loop"] = "for (var i = 0; i < 3; i++) { }" },
            "string a = \"x\", b = \"y\";", ["System.Text"]);

        code.Should().Contain("using System.Text;").And.Contain("string a = \"x\", b = \"y\";")
            .And.Contain("[Benchmark(Baseline = true, Description = \"Concat\")]")
            .And.Contain("public void Case_Concat() => DeadCodeEliminationHelper.KeepAliveWithoutBoxing(string.Concat(a, b));")
            .And.Contain("[Benchmark(Baseline = false, Description = \"Loop\")]")
            .And.Contain("public void Case_Loop() { for (var i = 0; i < 3; i++) { } }");
    }

    // Downloads BenchmarkDotNet; takes a minute or two.
    [Test]
    public async Task Cases_AreMeasured_WithRatioAndAllocations()
    {
        var directory = Directory.CreateTempSubdirectory("roslynaut-bench-");
        try
        {
            var project = Path.Combine(directory.FullName, "Lib", "Lib.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
            File.WriteAllText(Path.Combine(directory.FullName, "Lib", "Words.cs"), """
                namespace Lib;
                public static class Words
                {
                    public static string Join(string[] words) => string.Join(" ", words);
                    public static string Add(string[] words) { var text = ""; foreach (var word in words) text += word + " "; return text; }
                }
                """);
            var workspace = new WorkspaceService();

            var run = await BenchmarkTool.RunAsync(project, new Dictionary<string, string> { ["Join"] = "Words.Join(words)", ["Add"] = "Words.Add(words)" },
                "string[] words = Enumerable.Repeat(\"word\", 50).ToArray();", ["Lib"], "*", shortJob: true, workspace, new NuGetService(workspace), CancellationToken.None);

            run.Output.Should().BeNull();
            run.Cases.Select(@case => @case.Name).Should().BeEquivalentTo("Join", "Add");
            run.Cases.Single(@case => @case.Name == "Join").Ratio.Should().Be(1);
            var add = run.Cases.Single(@case => @case.Name == "Add");
            add.MeanNs.Should().BePositive();
            add.Ratio.Should().BeGreaterThan(1);
            add.AllocatedBytes.Should().BeGreaterThan(run.Cases.Single(@case => @case.Name == "Join").AllocatedBytes!.Value);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
