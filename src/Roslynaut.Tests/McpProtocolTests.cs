using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Roslynaut.Tools;

namespace Roslynaut.Tests;

/// <summary>
/// The server as MCP clients see it: started over stdio, its tools listed with their schemas, and called with arguments as JSON, which the
/// other tests, calling the tools' methods directly, skip.
/// </summary>
[NonParallelizable]
public class McpProtocolTests
{
    private McpClient client = null!;
    private DirectoryInfo directory = null!;
    private string project = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        directory = Directory.CreateTempSubdirectory("roslynaut-mcp-");
        project = Path.Combine(directory.FullName, "App.csproj");
        File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(directory.FullName, "Greeter.cs"), """
            namespace App;

            public class Greeter
            {
                public string Greet(string name, int times) => name + times;

                public string Twice() => Greet("a", 2);
            }
            """);
        File.WriteAllText(Path.Combine(directory.FullName, "Shape.cs"), """
            namespace App;

            public class Shape
            {
            }
            """);

        client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Name = "roslynaut",
            Command = DotnetCli.Executable,
            Arguments = ["exec", Path.Combine(AppContext.BaseDirectory, "Roslynaut.dll"), "--protocol", "stdio"],
        }));
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        await client.DisposeAsync();
        directory.Delete(recursive: true);
    }

    /// <summary>Calls a tool, which has to succeed, and parses the JSON it returns.</summary>
    private async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        result.IsError.Should().NotBe(true, text);
        return JsonDocument.Parse(text).RootElement;
    }

    // A tool class that Program.cs does not register is not exposed.
    [Test]
    public async Task Tools_AreAllRegistered_AndDescribeEveryParameter()
    {
        var declared = typeof(IMcpTool).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>();

        var tools = await client.ListToolsAsync();

        tools.Select(tool => tool.Name).Should().BeEquivalentTo(declared);
        foreach (var tool in tools)
        {
            tool.Description.Should().NotBeNullOrWhiteSpace(tool.Name);
            tool.JsonSchema.GetProperty("type").GetString().Should().Be("object", tool.Name);
            foreach (var parameter in tool.JsonSchema.TryGetProperty("properties", out var properties) ? properties.EnumerateObject() : default)
                parameter.Value.TryGetProperty("description", out _).Should().BeTrue($"{tool.Name}.{parameter.Name} needs a description");
        }
    }

    // Records as parameters: their fields need descriptions too, and the ones with defaults must not be required.
    [Test]
    public async Task RecordParameters_DescribeTheirFields_AndKeepDefaultsOptional()
    {
        var tools = (await client.ListToolsAsync()).ToDictionary(tool => tool.Name);
        JsonElement Items(string tool, string parameter) => tools[tool].JsonSchema.GetProperty("properties").GetProperty(parameter).GetProperty("items");

        foreach (var (tool, parameter, required) in new[] { ("debug_start", "breakpoints", Array.Empty<string>()), ("change_signature", "parameters", ["name"]), ("preview_edit", "edits", ["filePath", "oldText", "newText"]) })
        {
            var items = Items(tool, parameter);
            items.GetProperty("properties").EnumerateObject().Where(field => !field.Value.TryGetProperty("description", out _)).Select(field => field.Name)
                .Should().BeEmpty($"the fields of {tool}.{parameter} need descriptions");
            (items.TryGetProperty("required", out var names) ? names.EnumerateArray().Select(name => name.GetString()) : []).Should().BeEquivalentTo(required, $"{tool}.{parameter}");
        }
    }

    [Test]
    public async Task ToolErrors_ComeBackAsErrorResults()
    {
        var result = await client.CallToolAsync("debug_set", new Dictionary<string, object?> { ["expression"] = "x", ["value"] = "1" });

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("No program is being debugged");
    }

    [Test]
    public async Task ChangeSignature_TakesItsParameterList()
    {
        var changed = await CallAsync("change_signature", new()
        {
            ["projectPath"] = project,
            ["symbol"] = "App.Greeter.Greet",
            ["parameters"] = new object[] { new { name = "times" }, new { name = "name" }, new { name = "loud", type = "bool", value = "false" } },
        });

        changed.GetProperty("files").EnumerateArray().Should().ContainSingle();
        var text = File.ReadAllText(Path.Combine(directory.FullName, "Greeter.cs"));
        text.Should().Contain("public string Greet(int times, string name, bool loud)").And.Contain("Greet(2, \"a\", false)");
    }

    [Test]
    public async Task ApplyCodeAction_PicksMembers()
    {
        var file = Path.Combine(directory.FullName, "Shape.cs");
        var actions = await CallAsync("code_actions", new() { ["projectPath"] = project, ["filePath"] = file, ["line"] = 3, ["column"] = 14 });
        var overrides = actions.GetProperty("items").EnumerateArray().Single(action => action.GetProperty("title").GetString() == "Generate overrides...");

        await CallAsync("apply_code_action", new() { ["projectPath"] = project, ["id"] = overrides.GetProperty("id").GetString(), ["members"] = new[] { "ToString" } });

        File.ReadAllText(file).Should().Contain("public override string ToString()").And.NotContain("GetHashCode");
    }
}
