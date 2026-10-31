# Roslynaut

An MCP server that gives coding agents what an IDE knows about C# code. It loads your solution with Roslyn and MSBuild, so an agent can navigate by symbol, refactor, build and test, read IL, JIT output and decompiled code, inspect and trace running processes, and debug, instead of reading and grepping files.

## Requirements

- The .NET 10 runtime or later, to run the tool.
- The .NET SDK your projects build with, as their `global.json` picks it. Roslynaut loads projects through MSBuild design-time builds.
- For the debug tools, [netcoredbg](https://github.com/Samsung/netcoredbg), on the `PATH` or at the path in `NETCOREDBG`.

## Install

```sh
dotnet tool install -g Roslynaut
roslynaut --install claude
```

`--install claude` registers Roslynaut with Claude Code for all your projects, as the user-scope MCP server `roslynaut` in `~/.claude.json`. It also adds a hook to `~/.claude/settings.json`. When Claude reads or greps C# files, the hook suggests Roslynaut's tools for the job. The hook only adds that suggestion: it never blocks or approves a tool call. Both files go under `CLAUDE_CONFIG_DIR` when that is set. Restart Claude Code afterwards. `dotnet tool update -g Roslynaut` keeps the registration, as the command stays the same.

### Other MCP clients

Run the command `roslynaut`, which serves MCP over stdio. For example, in VS Code's `.vscode/mcp.json`:

```json
{
  "servers": {
    "roslynaut": { "type": "stdio", "command": "roslynaut" }
  }
}
```

`roslynaut --protocol http` serves streamable HTTP at `http://localhost:5000/`, and `--protocol sse` serves legacy SSE at `http://localhost:5000/sse`. Set `ASPNETCORE_URLS` to listen elsewhere. The server only answers requests addressed to `localhost`, `127.0.0.1` or `[::1]`; to serve another host name, set `AllowedHosts` to it. The HTTP server has no authentication.

## Usage

Most tools take `projectPath`, the path of a `.csproj`, `.sln` or `.slnx`. Roslynaut loads it on first use and keeps it in sync with the files on disk. Tools that work on a symbol take either a symbol path, such as `MyApp.Orders.OrderService.Submit(int)`, or a `filePath` with a 1-based `line` and `column`.

The server tells agents which tool to use for what, so an agent picks them up without further instructions.

On Linux, each loaded project or solution watches its files through a few inotify instances, and many distributions allow 128 per user, for all programs together. If tools fail with "The configured user limit (128) on the number of inotify instances has been reached", raise the limit: `sudo sysctl fs.inotify.max_user_instances=1024`.

## Tools

- **Navigation**: `definition`, `references`, `implementations`, `callers`, `callees`, `call_paths`, `type_hierarchy`, `document_symbols`, `list_symbols`, `find_symbols`, `workspace_symbols`, `symbol_source`, `document_highlight`, `search_pattern`, `find_operations`, `generated_sources`
- **Code analysis**: `diagnostics`, `expression_info`, `signature_help`, `completion`, `inlay_hints`, `syntax_tree`, `control_flow`, `data_flow`, `throws`, `code_metrics`, `find_duplicates`, `unused_symbols`, `public_api`, `api_diff`, `namespace_graph`, `di_registrations`, `aspnet_endpoints`
- **Code actions and refactoring**: `code_actions`, `apply_code_action`, `fix_all`, `format`, `organize_usings`, `rename_symbol`, `change_signature`, `extract_interface`, `move_to_namespace`, `safe_delete_symbol`
- **Symbol edits**: `replace_symbol_body`, `insert_before_symbol`, `insert_after_symbol`, `preview_edit`
- **Projects and MSBuild**: `project_graph`, `project_info`, `workspace_options`, `msbuild_evaluate`, `msbuild_property_trace`
- **Builds and binary logs**: `build`, `build_problems`, `build_diff`, `build_file_origin`, `build_incrementality`, `build_log_search`, `build_property_trace`, `build_target_why`
- **Tests**: `find_tests`, `run_tests` (also mutation testing with Stryker.NET), `affected_tests`
- **NuGet packages**: `packages`, `package_info`, `package_search`, `package_why`, `package_upgrade_check`, `package_licenses`, `package_contents`, `package_assembly`, `nuget_config`
- **IL, JIT and decompilation**: `symbol_il`, `symbol_asm`, `assembly_il`, `assembly_asm`, `type_layout`, `decompile`, `assembly_sources`, `resolve_stack_trace`
- **Running code**: `evaluate`, `benchmark` (off by default, see below)
- **Processes and dumps**: `dotnet_processes`, `process_threads`, `process_heap`, `process_objects`, `process_roots`, `process_async`, `process_dump`, `process_cpu`, `process_allocations`, `process_counters`, `process_events`, `process_calls`
- **Debugging**: `debug_start`, `debug_attach`, `debug_test`, `debug_breakpoints`, `debug_inspect`, `debug_set`, `debug_resume`, `debug_stop`

## Options

| Option | Effect |
| --- | --- |
| `--protocol stdio\|http\|sse` | The transport. The default is `stdio`. |
| `--enable-tools <tool>...` | Serve only these tools. |
| `--disable-tools <tool>...` | Leave these tools out. |
| `--no-instructions` | Leave out the server instructions that tell agents which tool to use for what. |
| `--install claude` | Register with Claude Code, then exit. |
| `--uninstall claude` | Remove that registration, then exit. |

| Environment variable | Effect |
| --- | --- |
| `ROSLYNAUT_ALLOW_EVALUATE=true` | Turns on `evaluate` and `benchmark`, which run arbitrary code. |
| `NETCOREDBG` | The path of netcoredbg, for the debug tools. |
| `_NT_SYMBOL_PATH` | Symbol servers and directories to find PDBs in, before the Microsoft and NuGet symbol servers. |
| `ASPNETCORE_URLS`, `AllowedHosts` | Where the HTTP server listens, and the host names it answers to. |

## Security

Use Roslynaut on code you trust, as you would `dotnet build`:

- Loading a project runs its MSBuild targets, and loads its analyzers and source generators into the server.
- `build`, `run_tests`, the debug tools and the process tools that launch a program run the project's code.
- `evaluate` and `benchmark` run code that the agent writes. They are off unless `ROSLYNAUT_ALLOW_EVALUATE=true`.
- Over HTTP, anyone who can reach the port can use all of this.

Some tools download:

- packages from your NuGet feeds, with the credentials of your `nuget.config` or credential providers;
- PDBs from symbol servers, and source files through SourceLink;
- Stryker.NET, for `run_tests` with mutation testing;
- BenchmarkDotNet, for `benchmark`.

## Uninstall

```sh
roslynaut --uninstall claude
dotnet tool uninstall -g Roslynaut
```

Run `--uninstall claude` first. It removes the server and its hook from Claude Code. Once the tool is gone, Claude Code could no longer start the hook.

## License

MIT. The package also ships third-party libraries under their own licenses: see [THIRD-PARTY-NOTICES.txt](https://github.com/CptWesley/Roslynaut/blob/main/THIRD-PARTY-NOTICES.txt).
