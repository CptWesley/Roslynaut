using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>symbol_source's history, in a git repository of its own.</summary>
public class SymbolHistoryTests
{
    [Test]
    public async Task History_HasTheCommitsThatChangedTheDeclaration_NewestFirst()
    {
        var directory = Directory.CreateTempSubdirectory("roslynaut-history-");
        try
        {
            async Task Git(params string[] arguments) =>
                (await DotnetCli.RunAsync("git", ["-C", directory.FullName, "-c", "user.name=Ann", "-c", "user.email=ann@example.com", .. arguments], TimeSpan.FromMinutes(1), CancellationToken.None))
                    .ExitCode.Should().Be(0);

            var file = Path.Combine(directory.FullName, "Calc.cs");
            File.WriteAllText(file, "class Calc\n{\n    int Add(int a, int b) => a + b;\n\n    int Other() => 0;\n}\n");
            await Git("init", "-q");
            await Git("add", ".");
            await Git("commit", "-q", "-m", "Add Calc");
            File.WriteAllText(file, "class Calc\n{\n    int Add(int a, int b) => checked(a + b);\n\n    int Other() => 0;\n}\n");
            await Git("commit", "-q", "-am", "Check overflow");
            File.WriteAllText(file, "class Calc\n{\n    int Add(int a, int b) => checked(a + b);\n\n    int Other() => 1;\n}\n");
            await Git("commit", "-q", "-am", "Change Other");

            var history = await SymbolSourceTool.HistoryAsync(new("int Calc.Add(int, int)", new(file, 3, 5, ""), "    int Add(int a, int b) => checked(a + b);"), 10, CancellationToken.None);

            history.Select(commit => (commit.Message, commit.Author)).Should().Equal(("Check overflow", "Ann"), ("Add Calc", "Ann"));
            history[0].Diff.Should().Contain("+    int Add(int a, int b) => checked(a + b);");
        }
        finally
        {
            foreach (var entry in directory.EnumerateFiles("*", SearchOption.AllDirectories))
                entry.Attributes = FileAttributes.Normal;
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task History_OutsideARepository_IsEmpty()
    {
        var file = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, $"roslynaut-{Guid.NewGuid():N}.cs");
        (await SymbolSourceTool.HistoryAsync(new("x", new(file, 1, 1, ""), "x"), 5, CancellationToken.None)).Should().BeEmpty();
    }
}
