using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Roslynaut.Tools;

namespace Roslynaut;

public enum McpProtocol
{
    Http,
    Sse,
    Stdio,
}

/// <summary>Owns the lifetime of the MCP server. Tools are discovered through DI.</summary>
/// <param name="serializerOptions">How tools read their arguments and write their results.</param>
public sealed class McpServerService(IEnumerable<IMcpTool> tools, JsonSerializerOptions serializerOptions)
{
    /// <summary>
    /// What clients put in front of the agent before it calls any tool, such as in its system prompt, where it may only see tool names: which
    /// tool answers which question, and how they work together. The tools' own descriptions say what each one does. Claude Code cuts
    /// instructions off after about 4,000 characters, so they have to stay below <see cref="MaxInstructionsLength"/>.
    /// </summary>
    internal const string Instructions = """
        Roslynaut sees C# code as the compiler does and edits it through Roslyn. Prefer its tools to reading whole files and grepping: they
        tell overloads, partial types, generated code and interface calls apart.

        Every code tool takes projectPath: pass the solution, and the same path each time, as the first call loads it and later calls reuse
        that. Changes on disk show up in the next call. Point at a symbol by path ('OrderService.Submit(Order)') or by filePath, line and
        column.

        - Find: workspace_symbols by name; find_symbols by regex and structure (attribute, base type, modifiers); find_operations by what
          code does (throws, calls, locks, awaits); search_pattern for text, comments too; document_symbols outlines a file, list_symbols a type.
        - Read: symbol_source gives one declaration, from a referenced assembly's PDB too; definition gives signature, docs and the
          AssemblyPath for decompile, assembly_il and assembly_asm; decompile with lowered shows closures, state machines and hidden
          allocations; generated_sources.
        - Relations: references, callers, callees, call_paths, implementations, type_hierarchy, throws, di_registrations (which class an
          interface gets), aspnet_endpoints (which code handles a URL), resolve_stack_trace (logged stack trace to source),
          find_tests, affected_tests (the tests to run after a change, with a run_tests Filter).
        - At a spot: expression_info, signature_help, completion, inlay_hints, document_highlight, control_flow, data_flow, syntax_tree.
        - Edit, all writing to disk: replace_symbol_body (the declaration as symbol_source gave it), insert_before_symbol,
          insert_after_symbol, safe_delete_symbol; rename_symbol, change_signature, extract_interface, move_to_namespace across the
          solution; code_actions and apply_code_action (an IDE's light bulb); fix_all, format, organize_usings. preview_edit shows the
          errors text edits would cause. Retry an edit refused because a file changed.
        - Check: diagnostics without building, after each edit (aot: trim and AOT warnings); build; run_tests (coverage, repeat, hangTimeout,
          mutate); verify_pending for Verify snapshots; workspace_options picks the framework or configuration #if code is seen for.
        - MSBuild: msbuild_evaluate, msbuild_property_trace, project_info, project_graph. build with binlogPath keeps a log for
          build_problems, build_target_why, build_property_trace, build_file_origin, build_incrementality, build_log_search, build_diff.
        - Packages: package_search, package_info, package_why, packages (outdated, vulnerable), package_licenses, package_contents,
          nuget_config (restore errors), package_assembly (a package's API before adding it), package_upgrade_check; public_api and
          api_diff show what breaks callers.
        - Speed: process_cpu profiles a process, program or test; symbol_asm (pgoWorkload for long-running code), symbol_il,
          type_layout; process_calls counts calls; benchmark compares versions.
        - Memory: process_counters, process_allocations, process_heap twice with compareTo (what grows), process_objects, process_roots.
        - Processes: dotnet_processes finds them; process_threads (hangs, deadlocks), process_async (stuck awaits), process_events
          (exceptions, logs, contention, GCs, HTTP), process_dump. Inspection tools read dumps too; process_cpu, process_allocations and
          process_events a .nettrace (trace).
        - Debugger, one session: debug_start, debug_test or debug_attach; debug_inspect, debug_set, debug_resume, debug_breakpoints;
          debug_stop.
        - Quality: unused_symbols, find_duplicates, code_metrics, namespace_graph (layering cycles), resources (.resx gaps, unused ones).
        - regex_test tries a .NET regex. evaluate runs C# against the code, benchmark measures it; both need ROSLYNAUT_ALLOW_EVALUATE=true.
        """;

    /// <summary>What follows <see cref="Instructions"/> when the tools' schemas leave out descriptions.</summary>
    internal const string ExplainInstructions = "\nSome tools' schemas lack descriptions: explain_tool gives them, with their parameters'.";

    /// <summary>
    /// Below where Claude Code cuts <see cref="Instructions"/> and <see cref="ExplainInstructions"/> off (seen at 4,130 characters), with room
    /// for edits.
    /// </summary>
    internal const int MaxInstructionsLength = 3900;

    /// <summary>How deep the JSON of tool results may nest, instead of the 64 levels of System.Text.Json's default.</summary>
    internal const int MaxJsonDepth = 256;

    /// <summary>Every tool, as the server serves it, with why it cannot work in this server (<see cref="IMcpTool.Unavailable"/>).</summary>
    public IReadOnlyList<(McpServerTool Tool, string? Unavailable)> Tools { get; } =
    [
        .. tools.Select(tool => ((McpServerTool)new ExplainingTool(
            Create(tool.Handler, serializerOptions), tool.Handler.Method, serializerOptions), tool.Unavailable)),
    ];

    private static McpServerTool Create(Delegate handler, JsonSerializerOptions serializerOptions)
    {
        var tool = McpServerTool.Create(handler, new() { SerializerOptions = serializerOptions });
        tool.ProtocolTool.InputSchema = WithoutNulls(tool.ProtocolTool.InputSchema);
        return tool;
    }

    /// <summary>
    /// The schema without null in the types of optional parameters, and without their "default": null. Leaving a parameter out of required
    /// already says it may be left out, and agents read every schema they load into their context: these were 5,000 characters over all tools.
    /// Null still binds, for a client that passes it.
    /// </summary>
    internal static JsonElement WithoutNulls(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText());
        Strip(node);
        return JsonSerializer.SerializeToElement(node);

        static void Strip(JsonNode? node)
        {
            if (node is JsonArray array)
            {
                foreach (var item in array)
                    Strip(item);
                return;
            }
            if (node is not JsonObject obj)
                return;

            if (obj["properties"] is JsonObject properties)
            {
                var required = (obj["required"] as JsonArray)?.Select(name => (string?)name).ToHashSet() ?? [];
                foreach (var (name, value) in properties)
                {
                    if (required.Contains(name) || value is not JsonObject property)
                        continue;
                    if (property.TryGetPropertyValue("default", out var @default) && @default is null)
                        property.Remove("default");
                    if (property["type"] is JsonArray types && types.Any(type => (string?)type == "null"))
                    {
                        var rest = types.Select(type => (string?)type).Where(type => type != "null").ToList();
                        property["type"] = rest is [var only] ? only : new JsonArray([.. rest.Select(type => (JsonNode?)type)]);
                    }
                    if (property["enum"] is JsonArray values && values.Any(item => item is null))
                        property["enum"] = new JsonArray([.. values.Where(item => item is not null).Select(item => item!.DeepClone())]);
                }
            }

            foreach (var (_, child) in obj)
                Strip(child);
        }
    }

    /// <summary>
    /// The tool as served, without its description or its parameters' when those are false, which <see cref="ExplainToolTool"/> gives
    /// instead. That one keeps its own, as agents would not know what it is for.
    /// </summary>
    internal static McpServerTool Described(McpServerTool tool, bool toolDescription, bool fieldDescriptions)
    {
        if (toolDescription && fieldDescriptions || tool.ProtocolTool.Name == ExplainToolTool.Name)
            return tool;
        // A copy, as explain_tool reads the descriptions from the original.
        var served = JsonSerializer.Deserialize<Tool>(JsonSerializer.SerializeToElement(tool.ProtocolTool, McpJsonUtilities.DefaultOptions), McpJsonUtilities.DefaultOptions)!;
        if (!toolDescription)
            served.Description = null;
        if (!fieldDescriptions)
            served.InputSchema = WithoutDescriptions(served.InputSchema);
        return new ServedTool(tool, served);
    }

    /// <summary>The schema without the descriptions of its parameters, and of the fields of parameters that are records.</summary>
    internal static JsonElement WithoutDescriptions(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText());
        Strip(node);
        return JsonSerializer.SerializeToElement(node);

        static void Strip(JsonNode? node)
        {
            if (node is JsonArray array)
            {
                foreach (var item in array)
                    Strip(item);
                return;
            }
            if (node is not JsonObject obj)
                return;

            obj.Remove("description");
            foreach (var (key, child) in obj)
            {
                // Its keys are parameters' names, one of which may be description.
                if (key == "properties" && child is JsonObject properties)
                    foreach (var (_, property) in properties)
                        Strip(property);
                else
                    Strip(child);
            }
        }
    }

    /// <summary>Starts the server with some of <see cref="Tools"/>; the returned task completes when the server shuts down.</summary>
    public async Task StartAsync(McpProtocol protocol, IReadOnlyList<McpServerTool> serverTools, string? instructions)
    {
        if (protocol == McpProtocol.Stdio)
        {
            // stdout carries the protocol, so log to stderr only.
            var host = Host.CreateEmptyApplicationBuilder(settings: null);
            host.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            host.Services.AddMcpServer(options => options.ServerInstructions = instructions).WithStdioServerTransport().WithTools(serverTools);
            await host.Build().RunAsync();
            return;
        }

        var builder = WebApplication.CreateBuilder();
        // Kestrel listens on localhost, but takes any Host header: a web page whose name resolves to 127.0.0.1 (DNS rebinding) could call the
        // tools, which run builds. The environment variable AllowedHosts overrides this, for a server that listens on another address.
        builder.Configuration["AllowedHosts"] ??= "localhost;127.0.0.1;[::1]";
        builder.Services.AddMcpServer(options => options.ServerInstructions = instructions)
            .WithHttpTransport(options =>
            {
                // Legacy SSE needs in-memory sessions, so it cannot run stateless.
                // It also lacks request backpressure (MCP9004): only expose it to trusted clients.
#pragma warning disable MCP9004
                options.EnableLegacySse = protocol == McpProtocol.Sse;
#pragma warning restore MCP9004
                options.Stateless = protocol == McpProtocol.Http;
            })
            .WithTools(serverTools);

        var app = builder.Build();
        app.MapMcp();
        await app.RunAsync();
    }
}

/// <summary>A tool that clients see as <paramref name="served"/>, such as without its descriptions, and that runs as before.</summary>
internal sealed class ServedTool(McpServerTool tool, Tool served) : DelegatingMcpServerTool(tool)
{
    public override Tool ProtocolTool => served;
}

/// <summary>
/// A tool whose failures say what went wrong. The SDK passes on only the message of an <see cref="McpException"/>, and turns any other
/// exception into "An error occurred invoking 'tool'.", such as those of binding the arguments to the handler's parameters.
/// </summary>
/// <param name="serializerOptions">The options the tool binds its arguments with.</param>
internal sealed class ExplainingTool(McpServerTool tool, MethodInfo handler, JsonSerializerOptions serializerOptions) : DelegatingMcpServerTool(tool)
{
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var arguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        if (Missing(ProtocolTool.InputSchema, arguments) is [_, ..] missing)
            throw new McpException($"Missing required argument{(missing.Count > 1 ? "s" : "")}: {string.Join(", ", missing)}.");

        try
        {
            return await base.InvokeAsync(request, cancellationToken);
        }
        catch (Exception e) when (e is not (McpException or OperationCanceledException or InputRequiredException))
        {
            throw new McpException(Explain(e, ProtocolTool.InputSchema, handler, arguments, serializerOptions), e);
        }
    }

    /// <summary>The required arguments the call leaves out, or gives as null.</summary>
    internal static List<string> Missing(JsonElement schema, IDictionary<string, JsonElement> arguments) =>
    [
        .. (schema.TryGetProperty("required", out var required) ? required.EnumerateArray().Select(name => name.GetString()!) : [])
            .Where(name => !arguments.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null),
    ];

    /// <summary>
    /// What went wrong: binding throws a <see cref="JsonException"/> that does not name the argument which does not convert to its parameter's
    /// type, so that argument is looked for; the tool's own exceptions keep their type and message.
    /// </summary>
    internal static string Explain(Exception exception, JsonElement schema, MethodInfo handler, IDictionary<string, JsonElement> arguments, JsonSerializerOptions options)
    {
        if (exception is JsonException)
        {
            foreach (var parameter in handler.GetParameters())
            {
                if (parameter.Name is { } name && arguments.TryGetValue(name, out var value) && !Converts(value, parameter.ParameterType, options))
                    return $"Argument {name} has to be {TypeOf(schema, name)}, not {Shorten(value.GetRawText())}.";
            }
        }

        return $"{exception.GetType().Name}: {exception.Message}";
    }

    private static bool Converts(JsonElement value, Type type, JsonSerializerOptions options)
    {
        try
        {
            JsonSerializer.Deserialize(value, type, options);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>What the schema allows for a parameter, such as "integer or null", or "one of Supertypes, Subtypes".</summary>
    private static string TypeOf(JsonElement schema, string name)
    {
        if (!schema.TryGetProperty("properties", out var properties) || !properties.TryGetProperty(name, out var property))
            return "?";
        if (property.TryGetProperty("enum", out var values))
            return $"one of {string.Join(", ", values.EnumerateArray().Select(value => value.ToString()))}";
        return property.TryGetProperty("type", out var type)
            ? type.ValueKind == JsonValueKind.Array ? string.Join(" or ", type.EnumerateArray().Select(kind => kind.GetString())) : type.GetString() ?? "?"
            : "?";
    }

    private static string Shorten(string json) => json.Length > 100 ? json[..100] + "…" : json;
}
