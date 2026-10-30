using System.Reflection;
using System.Text.Json;

namespace Roslynaut.Tests;

/// <summary>The messages of failed tool calls; the end-to-end tests cover the arguments that are missing or do not convert.</summary>
public class ExplainingToolTests
{
    private static readonly MethodInfo Handler = typeof(ExplainingToolTests).GetMethod(nameof(Tool), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        { "type": "object", "properties": { "path": { "type": "string" }, "line": { "type": "integer" } }, "required": ["path"] }
        """).RootElement;

    private static void Tool(string path, int line = 1)
    {
    }

    private static Dictionary<string, JsonElement> Arguments(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Test]
    public void Explain_AnExceptionOfTheTool_GivesItsTypeAndMessage()
    {
        ExplainingTool.Explain(new InvalidOperationException("Boom."), Schema, Handler, Arguments("""{ "path": "a", "line": 2 }"""))
            .Should().Be("InvalidOperationException: Boom.");
    }

    // The tool can throw a JsonException of its own, with arguments that convert.
    [Test]
    public void Explain_AJsonExceptionOfTheTool_GivesItsTypeAndMessage()
    {
        ExplainingTool.Explain(new JsonException("Bad file."), Schema, Handler, Arguments("""{ "path": "a", "line": 2 }"""))
            .Should().Be("JsonException: Bad file.");
    }

    [Test]
    public void Explain_AnArgumentThatDoesNotConvert_NamesIt()
    {
        ExplainingTool.Explain(new JsonException("The JSON value could not be converted."), Schema, Handler, Arguments("""{ "path": "a", "line": "two" }"""))
            .Should().Be("Argument line has to be integer, not \"two\".");
    }

    [Test]
    public void Missing_RequiredArgumentsLeftOutOrNull()
    {
        ExplainingTool.Missing(Schema, Arguments("""{ "line": 2 }""")).Should().Equal("path");
        ExplainingTool.Missing(Schema, Arguments("""{ "path": null }""")).Should().Equal("path");
        ExplainingTool.Missing(Schema, Arguments("""{ "path": "a" }""")).Should().BeEmpty();
    }
}
