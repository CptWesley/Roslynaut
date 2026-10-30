using System.Reflection;
using System.Text.Json;
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
public sealed class McpServerService(IEnumerable<IMcpTool> tools)
{
    /// <summary>
    /// What clients put in front of the agent before it calls any tool, such as in its system prompt, where it may only see tool names: which
    /// tool answers which question, and how they work together. The tools' own descriptions say what each one does.
    /// </summary>
    private const string Instructions = """
        Roslynaut sees C# code as the compiler does, and edits it through Roslyn. Prefer its tools to reading whole files and searching text:
        they tell overloads, partial types, generated code and calls through interfaces apart, which text search does not.

        Every tool on code takes projectPath, a .csproj, .sln or .slnx. Pass the solution, so results cover all its projects, and keep passing
        the same path: the first call loads it, which takes a while, and later calls reuse that load. Files that change on disk, by these tools
        or by anything else, show up in the next call. Point at a symbol by path, such as 'OrderService.Submit' or 'OrderService.Submit(Order)',
        or by filePath, line and column; a result's Container plus '.' plus its Name is a path. Paged results say whether more follow (hasMore).

        Which tool for what:
        - Find code: workspace_symbols by name as an IDE's Go to symbol; find_symbols by regex and structure (attribute, base type, modifiers);
          find_operations by what code does (every throw of a type, call to a method, lock, await); search_pattern for text, comments included;
          document_symbols outlines a file, list_symbols a type or namespace.
        - Read code: symbol_source returns one declaration instead of a whole file, from a referenced assembly's PDB too; definition gives
          signature and documentation, and for a referenced symbol the AssemblyPath that decompile, assembly_il and assembly_asm take;
          decompile shows what an assembly really contains, and with lowered the closures, state machines and hidden allocations;
          generated_sources shows source generators' output.
        - Relations: references (reads, writes, calls), callers, callees, call_paths (how one member reaches another), implementations,
          type_hierarchy, throws, find_tests and affected_tests (the tests to run after a change, with a Filter for run_tests),
          di_registrations (which class an injected interface gets), aspnet_endpoints (which code handles a URL), resolve_stack_trace (a stack
          trace from a log or CI mapped to current source).
        - Understand a spot: expression_info, signature_help, completion, inlay_hints, document_highlight, control_flow (which conditions reach
          a line), data_flow, syntax_tree.
        - Edit: replace_symbol_body (the declaration as symbol_source returned it), insert_before_symbol, insert_after_symbol and
          safe_delete_symbol edit by symbol, not by line. rename_symbol, change_signature, extract_interface and move_to_namespace update
          the whole solution. code_actions lists an IDE's light bulb (add using, implement interface, extract method), apply_code_action applies
          one; fix_all fixes one diagnostic id everywhere; format and organize_usings tidy a file. All of them write to disk. preview_edit
          shows the errors text edits would cause, without writing. An edit refused because a file changed meanwhile can be called again.
        - Check: diagnostics gives compiler and analyzer errors without building, the quick check after an edit. build and run_tests run the
          dotnet CLI; run_tests can add coverage, repeat (flaky tests), hangTimeout (hangs) and mutate (tests that miss bugs).
          workspace_options switches the target framework or configuration that code under #if is seen for.
        - MSBuild: msbuild_evaluate and msbuild_property_trace read the project as evaluated; project_info and project_graph describe it.
          build with binlogPath keeps a binary log for build_target_why, build_property_trace, build_file_origin, build_problems,
          build_incrementality, build_log_search and build_diff (a passing and a failing build).
        - Packages: package_search, package_info, package_why, packages (outdated, vulnerable), package_licenses, package_contents (what
          reaches the build), nuget_config (restore errors).
          package_assembly downloads a package so decompile or assembly_sources can read its API before adding it; package_upgrade_check
          shows what an upgrade would break; public_api and api_diff show what a change breaks for callers.
        - Speed: process_cpu profiles a running process, or a program or test it launches; symbol_asm shows a hot method's machine code (with
          pgoWorkload, as long-running code gets it); symbol_il, type_layout; process_calls counts calls exactly; benchmark compares versions.
        - Memory: process_counters (rates), process_allocations (what allocates), process_heap twice with compareTo (what grows), then
          process_objects and process_roots (what keeps it alive).
        - Running processes: dotnet_processes finds them. process_threads for hangs and deadlocks, process_async for awaits that never finish,
          process_events for exceptions, logs, lock contention, garbage collections and HTTP requests. process_dump keeps a moment; the
          inspection tools read dumps too.
        - Debugger: debug_start, debug_test or debug_attach, then debug_inspect, debug_set, debug_resume and debug_breakpoints; debug_stop
          ends it. One session at a time; needs netcoredbg.
        - Quality: unused_symbols, find_duplicates, code_metrics, namespace_graph (layering cycles).
        - Run code: evaluate runs C# against the projects' code, and benchmark measures it; both need the server started with
          ROSLYNAUT_ALLOW_EVALUATE=true.
        """;

    /// <summary>Every tool, as the server serves it.</summary>
    public IReadOnlyList<McpServerTool> Tools { get; } =
        [.. tools.Select(tool => (McpServerTool)new ExplainingTool(McpServerTool.Create(tool.Handler), tool.Handler.Method))];

    /// <summary>Starts the server with some of <see cref="Tools"/>; the returned task completes when the server shuts down.</summary>
    public async Task StartAsync(McpProtocol protocol, IReadOnlyList<McpServerTool> serverTools, bool instructions)
    {
        if (protocol == McpProtocol.Stdio)
        {
            // stdout carries the protocol, so log to stderr only.
            var host = Host.CreateEmptyApplicationBuilder(settings: null);
            host.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            host.Services.AddMcpServer(options => options.ServerInstructions = instructions ? Instructions : null).WithStdioServerTransport().WithTools(serverTools);
            await host.Build().RunAsync();
            return;
        }

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddMcpServer(options => options.ServerInstructions = instructions ? Instructions : null)
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

/// <summary>
/// A tool whose failures say what went wrong. The SDK passes on only the message of an <see cref="McpException"/>, and turns any other
/// exception into "An error occurred invoking 'tool'.", such as those of binding the arguments to the handler's parameters.
/// </summary>
internal sealed class ExplainingTool(McpServerTool tool, MethodInfo handler) : DelegatingMcpServerTool(tool)
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
            throw new McpException(Explain(e, ProtocolTool.InputSchema, handler, arguments), e);
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
    internal static string Explain(Exception exception, JsonElement schema, MethodInfo handler, IDictionary<string, JsonElement> arguments)
    {
        if (exception is JsonException)
        {
            foreach (var parameter in handler.GetParameters())
            {
                if (parameter.Name is { } name && arguments.TryGetValue(name, out var value) && !Converts(value, parameter.ParameterType))
                    return $"Argument {name} has to be {TypeOf(schema, name)}, not {Shorten(value.GetRawText())}.";
            }
        }

        return $"{exception.GetType().Name}: {exception.Message}";
    }

    private static bool Converts(JsonElement value, Type type)
    {
        try
        {
            JsonSerializer.Deserialize(value, type, McpJsonUtilities.DefaultOptions);
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
