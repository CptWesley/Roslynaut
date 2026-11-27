using System.Text.Json;

namespace Roslynaut.Tests;

/// <summary>What the server puts in agents' context: its instructions and the tools' schemas.</summary>
public class McpServerServiceTests
{
    // Claude Code drops what follows the cut, and with it the guidance for whole groups of tools.
    [Test]
    public void Instructions_FitBeforeClaudeCodeCutsThemOff()
    {
        McpServerService.Instructions.Length.Should().BeLessThanOrEqualTo(McpServerService.MaxInstructionsLength);
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
