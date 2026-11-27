using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Roslynaut.Tools;

namespace Roslynaut.Tests;

/// <summary>What the server puts in agents' context: its instructions and the tools' schemas.</summary>
public class McpServerServiceTests
{
    // Claude Code drops what follows the cut, and with it the guidance for whole groups of tools.
    [Test]
    public void Instructions_FitBeforeClaudeCodeCutsThemOff()
    {
        (McpServerService.Instructions + McpServerService.ExplainInstructions).Length.Should().BeLessThanOrEqualTo(McpServerService.MaxInstructionsLength);
    }

    // A description without its caveman text would be served in full, and one whose text changed would keep the old one's.
    [Test]
    public async Task CavemanDescriptions_CoverEveryDescription_AndNoOthers()
    {
        await using var services = Program.Services();
        var texts = new HashSet<string>();
        foreach (var (tool, _) in services.GetRequiredService<McpServerService>().Tools)
        {
            if (tool.ProtocolTool.Name == ExplainToolTool.Name)
                continue;
            texts.Add(tool.ProtocolTool.Description!);
            McpServerService.WithDescriptions(tool.ProtocolTool.InputSchema, text =>
            {
                texts.Add(text);
                return text;
            });
        }

        texts.Except(CavemanDescriptions.Text.Keys).Should().BeEmpty("these descriptions need their caveman text in CavemanDescriptions");
        CavemanDescriptions.Text.Keys.Except(texts).Should().BeEmpty("no description has this text any more");
    }

    [TestCase("Finds references. Then more.", ExpectedResult = "Finds references.")]
    [TestCase("A name, e.g. 'Order.Submit'. More.", ExpectedResult = "A name, e.g. 'Order.Submit'.")]
    [TestCase("One sentence, i.e. no more", ExpectedResult = "One sentence, i.e. no more")]
    public string FirstSentence_EndsAtThePeriodOfASentence(string text) => McpServerService.FirstSentence(text);

    // A parameter may be called description, and is no description to remove.
    [Test]
    public void WithDescriptions_RewritesThoseOfParametersAndTheirFields()
    {
        var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "description": { "type": "string", "description": "A name." },
                "edits": { "type": "array", "description": "Edits.", "items": { "type": "object", "properties": { "note": { "type": "string", "description": "A note." } } } }
              },
              "required": ["description"]
            }
            """).RootElement;

        JsonSerializer.Serialize(McpServerService.WithDescriptions(schema, text => text == "Edits." ? "E." : null)).Should().Be(JsonSerializer.Serialize(JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "description": { "type": "string" },
                "edits": { "type": "array", "description": "E.", "items": { "type": "object", "properties": { "note": { "type": "string" } } } }
              },
              "required": ["description"]
            }
            """).RootElement));
    }

    [Test]
    public void WithoutNulls_DropsNullFromOptionalParameters_AndKeepsRequiredOnes()
    {
        var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "path": { "type": ["string", "null"] },
                "line": { "type": ["integer", "null"], "default": null },
                "kind": { "type": ["string", "null"], "enum": ["A", "B", null], "default": null },
                "page": { "type": "integer", "default": 0 },
                "edits": { "type": "array", "items": { "type": "object", "properties": { "note": { "type": ["string", "null"], "default": null } } } }
              },
              "required": ["path"]
            }
            """).RootElement;

        JsonSerializer.Serialize(McpServerService.WithoutNulls(schema)).Should().Be(JsonSerializer.Serialize(JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "path": { "type": ["string", "null"] },
                "line": { "type": "integer" },
                "kind": { "type": "string", "enum": ["A", "B"] },
                "page": { "type": "integer", "default": 0 },
                "edits": { "type": "array", "items": { "type": "object", "properties": { "note": { "type": "string" } } } }
              },
              "required": ["path"]
            }
            """).RootElement));
    }
}
