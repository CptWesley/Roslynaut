using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Roslynaut.Tools;

namespace Roslynaut.Tests.EndToEnd;

/// <summary>The server as MCP clients see it: its tools with their schemas and annotations, results as JSON, errors as error results, and paging.</summary>
public class ProtocolTests : EndToEndTest
{
    private static IEnumerable<(MethodInfo Method, McpServerToolAttribute Tool)> DeclaredTools() => typeof(IMcpTool).Assembly.GetTypes()
        .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        .Select(method => (method, method.GetCustomAttribute<McpServerToolAttribute>()!))
        .Where(tool => tool.Item2 is not null);

    // A tool class that Program.cs does not register is not exposed.
    [Test]
    public async Task Tools_AreAllRegistered_AndDescribeEveryParameter()
    {
        var tools = await Server.Client.ListToolsAsync();

        tools.Select(tool => tool.Name).Should().BeEquivalentTo(DeclaredTools().Select(tool => tool.Tool.Name));
        foreach (var tool in tools)
        {
            tool.Description.Should().NotBeNullOrWhiteSpace(tool.Name);
            tool.JsonSchema.GetProperty("type").GetString().Should().Be("object", tool.Name);
            foreach (var parameter in tool.JsonSchema.TryGetProperty("properties", out var properties) ? properties.EnumerateObject() : default)
                parameter.Value.TryGetProperty("description", out _).Should().BeTrue($"{tool.Name}.{parameter.Name} needs a description");
        }
    }

    // What a tool declares about itself reaches clients, which may ask before running a destructive one; what it leaves out stays unset.
    [Test]
    public async Task Tools_CarryTheirAnnotations()
    {
        var tools = (await Server.Client.ListToolsAsync()).ToDictionary(tool => tool.Name, tool => tool.ProtocolTool.Annotations);

        foreach (var (method, declared) in DeclaredTools())
        {
            var named = method.GetCustomAttributesData().Single(data => data.AttributeType == typeof(McpServerToolAttribute)).NamedArguments
                .ToDictionary(argument => argument.MemberName, argument => argument.TypedValue.Value);
            bool? Declared(string hint) => named.TryGetValue(hint, out var value) ? (bool)value! : null;
            // A tool that declares none, such as run_tests, has no annotations.
            var annotations = tools[declared.Name!];
            (annotations?.ReadOnlyHint, annotations?.DestructiveHint, annotations?.IdempotentHint, annotations?.OpenWorldHint)
                .Should().Be((Declared("ReadOnly"), Declared("Destructive"), Declared("Idempotent"), Declared("OpenWorld")), declared.Name);
        }

        (tools["references"]!.ReadOnlyHint, tools["rename_symbol"]!.DestructiveHint, tools["format"]!.IdempotentHint, tools["definition"]!.OpenWorldHint)
            .Should().Be((true, true, true, true));
    }

    // Records as parameters: their fields need descriptions too, and the ones with defaults must not be required.
    [Test]
    public async Task RecordParameters_DescribeTheirFields_AndKeepDefaultsOptional()
    {
        var tools = (await Server.Client.ListToolsAsync()).ToDictionary(tool => tool.Name);
        JsonElement Items(string tool, string parameter) => tools[tool].JsonSchema.GetProperty("properties").GetProperty(parameter).GetProperty("items");

        foreach (var (tool, parameter, required) in new[] { ("debug_start", "breakpoints", Array.Empty<string>()), ("change_signature", "parameters", ["name"]), ("preview_edit", "edits", ["filePath", "oldText", "newText"]) })
        {
            var items = Items(tool, parameter);
            items.GetProperty("properties").EnumerateObject().Where(field => !field.Value.TryGetProperty("description", out _)).Select(field => field.Name)
                .Should().BeEmpty($"the fields of {tool}.{parameter} need descriptions");
            (items.TryGetProperty("required", out var names) ? names.EnumerateArray().Select(name => name.GetString()) : []).Should().BeEquivalentTo(required, $"{tool}.{parameter}");
        }
    }

    // Results are camel-cased JSON, without the properties that are null, and enums by name.
    [Test]
    public async Task Results_AreCamelCaseJson()
    {
        var definition = (await Server.CallAsync("definition", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Shapes.Circle" })).EnumerateArray().Single();
        var paged = await Server.CallAsync("find_symbols", new() { ["projectPath"] = Sample.Solution, ["query"] = "^Circle$", ["kind"] = "Type" });

        definition.EnumerateObject().Select(property => property.Name).Should().Equal("kind", "signature", "assembly", "documentation", "locations", "sources");
        definition.GetProperty("locations")[0].EnumerateObject().Select(property => property.Name).Should().Equal("filePath", "line", "column", "text");
        paged.EnumerateObject().Select(property => property.Name).Should().Equal("items", "page", "pageSize", "hasMore", "totalCount");
    }

    [Test]
    public async Task ToolErrors_ComeBackAsErrorResults()
    {
        (await Server.CallFailingAsync("debug_set", new() { ["expression"] = "x", ["value"] = "1" })).Should().Contain("No program is being debugged");
        (await Server.CallFailingAsync("definition", new() { ["projectPath"] = Sample.Solution, ["symbol"] = "Shapes.Hexagon" }))
            .Should().Be("An error occurred invoking 'definition': No symbol found for 'Shapes.Hexagon'.");
        (await Server.CallFailingAsync("project_info", new() { ["projectPath"] = Sample.In("Missing.csproj") }))
            .Should().Be($"An error occurred invoking 'project_info': Project or solution not found: {Sample.In("Missing.csproj")}");
        (await Server.CallFailingAsync("search_pattern", new() { ["projectPath"] = Sample.Solution, ["pattern"] = "(" })).Should().Contain("Invalid pattern:");
        (await Server.CallTextAsync("debug_stop")).Should().Be("Stopped.");
    }

    // The SDK does not say which argument is missing; the schema lists the required ones.
    [Test]
    public async Task ToolCalls_WithoutTheirRequiredArguments_Fail()
    {
        (await Server.CallFailingAsync("references", new() { ["symbol"] = "Circle" })).Should().Be("An error occurred invoking 'references'.");
        (await Server.Client.ListToolsAsync()).Single(tool => tool.Name == "references").JsonSchema.GetProperty("required").EnumerateArray()
            .Select(name => name.GetString()).Should().Equal("projectPath");
    }

    [Test]
    public async Task UnknownTools_Fail()
    {
        await Server.Client.Awaiting(client => client.CallToolAsync("no_such_tool", new Dictionary<string, object?>())).Should().ThrowAsync<Exception>();
    }

    // Pages slice one evaluation of the results, which later pages continue.
    [Test]
    public async Task PagedResults_ComeAPageAtATime()
    {
        async Task<PagedResult<SymbolEntry>> PageAsync(int page, int pageSize) => await Server.CallAsync<PagedResult<SymbolEntry>>("workspace_symbols", new()
        {
            ["projectPath"] = Sample.Solution, ["query"] = "Area", ["page"] = page, ["pageSize"] = pageSize,
        });

        var pages = new[] { await PageAsync(0, 3), await PageAsync(1, 3), await PageAsync(2, 3), await PageAsync(3, 3) };
        var all = await PageAsync(0, 100);

        pages.Select(page => (page.Items.Count(), page.Page, page.PageSize, page.HasMore, page.TotalCount))
            .Should().Equal((3, 0, 3, true, 8), (3, 1, 3, true, 8), (2, 2, 3, false, 8), (0, 3, 3, false, 8));
        pages.SelectMany(page => page.Items).Should().Equal(all.Items);
        (await Server.CallFailingAsync("workspace_symbols", new() { ["projectPath"] = Sample.Solution, ["query"] = "Area", ["pageSize"] = 0 }))
            .Should().Be("An error occurred invoking 'workspace_symbols': page must be at least 0 and pageSize at least 1.");
    }
}
