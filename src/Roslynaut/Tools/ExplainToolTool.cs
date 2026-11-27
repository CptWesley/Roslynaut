using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

/// <summary>
/// The descriptions that --no-tool-descriptions and --no-field-descriptions leave out of the tools' schemas, for the tools an agent calls.
/// </summary>
/// <param name="services">Gives the server's tools when called, as the server is made of the tools, this one included.</param>
public sealed class ExplainToolTool(IServiceProvider services) : IMcpTool
{
    public const string Name = "explain_tool";

    public Delegate Handler => Explain;

    [McpServerTool(Name = Name, ReadOnly = true, Idempotent = true)]
    [Description("Describes Roslynaut tools in full: what each does, and its parameters with their types, defaults and descriptions. Call it "
        + "before a tool whose schema has no descriptions.")]
    private IReadOnlyList<ToolExplanation> Explain(
        [Description("The tools' names, such as 'references' or 'mcp__roslynaut__references'.")] string[] tools) =>
        Find(services.GetRequiredService<McpServerService>().Tools.Select(tool => tool.Tool.ProtocolTool), tools);

    internal static IReadOnlyList<ToolExplanation> Find(IEnumerable<Tool> all, IReadOnlyList<string> names)
    {
        var byName = all.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
        return
        [
            .. names.Select(name =>
            {
                // Agents may pass the name they call the tool by, which has the server's name in front.
                var bare = name.LastIndexOf("__", StringComparison.Ordinal) is >= 0 and var at ? name[(at + 2)..] : name;
                return byName.TryGetValue(bare, out var tool)
                    ? new ToolExplanation(tool.Name, tool.Description, tool.InputSchema)
                    : throw new McpException($"Roslynaut has no tool {name}.");
            }),
        ];
    }
}

/// <param name="InputSchema">The parameters as JSON Schema, with each one's description.</param>
public sealed record ToolExplanation(string Name, string? Description, JsonElement InputSchema);
