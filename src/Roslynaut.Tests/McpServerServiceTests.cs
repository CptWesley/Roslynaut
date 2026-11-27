using System.Text.Json;

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

    // A parameter may be called description, and is no description to remove.
    [Test]
    public void WithoutDescriptions_RemovesThoseOfParametersAndTheirFields()
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

        JsonSerializer.Serialize(McpServerService.WithoutDescriptions(schema)).Should().Be(JsonSerializer.Serialize(JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "description": { "type": "string" },
                "edits": { "type": "array", "items": { "type": "object", "properties": { "note": { "type": "string" } } } }
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
