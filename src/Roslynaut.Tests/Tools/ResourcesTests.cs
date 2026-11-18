using System.Text;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using Roslynaut.Tools;

namespace Roslynaut.Tests.Tools;

/// <summary>resources, on .resx files in a directory of their own and code in memory.</summary>
public class ResourcesTests
{
    private DirectoryInfo directory = null!;

    private string In(params string[] parts) => Path.Combine([directory.FullName, .. parts]);

    private static string Resx(params (string Name, string Value)[] strings) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <root>
          <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
          {string.Concat(strings.Select(data => $"<data name=\"{data.Name}\" xml:space=\"preserve\"><value>{data.Value}</value></data>"))}
          <data name="Logo" type="System.Resources.ResXFileRef, System.Windows.Forms"><value>logo.png;System.Byte[]</value></data>
        </root>
        """;

    [SetUp]
    public void WriteResources()
    {
        directory = Directory.CreateTempSubdirectory("roslynaut-resources-");
        Directory.CreateDirectory(In("obj"));
        File.WriteAllText(In("Strings.resx"), Resx(("Hello", "Hello"), ("Bye", "Bye"), ("Not.Used", "Unused"), ("Total", "Total")));
        File.WriteAllText(In("Strings.de.resx"), Resx(("Hello", "Hallo"), ("Bye", "Tschüss"), ("Not.Used", "Unbenutzt"), ("Total", "Summe")));
        File.WriteAllText(In("Strings.fr-FR.resx"), Resx(("Hello", "Bonjour"), ("Old", "Ancien")));
        // Build output is not where resources are kept.
        File.WriteAllText(In("obj", "Strings.resx"), Resx(("Elsewhere", "x")));
    }

    [TearDown]
    public void Delete() => directory.Delete(recursive: true);

    // The generated class names every resource by its property and by its name; its own file does not count as a use.
    private const string Designer = """
        public class Strings
        {
            public static System.Resources.ResourceManager ResourceManager => null;
            public static string Hello => ResourceManager.GetString("Hello");
            public static string Bye => ResourceManager.GetString("Bye");
            public static string Not_Used => ResourceManager.GetString("Not.Used");
            public static string Total => ResourceManager.GetString("Total");
        }
        """;

    private const string Code = """
        class Page
        {
            string Title() => Strings.Hello;
            string Leave(System.Resources.ResourceManager resources) => resources.GetString("Bye");
            int Total => 3;
        }
        """;

    private Task<IReadOnlyList<ResourcesTool.ResourceSet>> CheckAsync(string? filePath = null, bool unused = true)
    {
        var solution = TestSolution.Create(Code).Projects.Single().AddDocument("Strings.Designer.cs", SourceText.From(Designer, Encoding.UTF8)).Project.Solution;
        return ResourcesTool.CheckAsync(solution, [directory.FullName], filePath, unused, CancellationToken.None);
    }

    [Test]
    public async Task Gaps_ArePerCulture_WithWhatItLacksAndWhatOnlyItHas()
    {
        var set = (await CheckAsync()).Single();

        (set.File, set.Keys).Should().Be((In("Strings.resx"), 4));
        set.Cultures.Should().Equal("de", "fr-FR");
        set.Gaps.Should().ContainSingle().Which.Should().BeEquivalentTo(new ResourcesTool.CultureGap("fr-FR", In("Strings.fr-FR.resx"), ["Bye", "Not.Used", "Total"], ["Old"]));
    }

    // Total is a property of another class there, and Not.Used only in the generated class.
    [Test]
    public async Task Unused_AreTheResourcesNoCodeUses_ByPropertyOrName()
    {
        (await CheckAsync()).Single().Unused.Should().Equal("Not.Used", "Total");
        (await CheckAsync(unused: false)).Single().Unused.Should().BeNull();
    }

    [Test]
    public async Task AFile_PicksItsSet()
    {
        (await CheckAsync(In("Strings.de.resx"))).Single().File.Should().Be(In("Strings.resx"));
        await FluentActions.Awaiting(() => CheckAsync(In("Other.resx"))).Should().ThrowAsync<McpException>();
    }

    [TestCase("Strings.resx", "Strings", null)]
    [TestCase("Strings.de-DE.resx", "Strings", "de-DE")]
    [TestCase("Form1.Designer.resx", "Form1.Designer", null)]
    [TestCase("Errors.zh-Hans.resx", "Errors", "zh-Hans")]
    public void Name_SplitsTheSetFromTheCulture(string file, string set, string? culture)
    {
        ResourcesTool.NameOf(file).Should().Be((set, culture));
    }
}
