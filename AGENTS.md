# AGENTS.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Roslynaut is an MCP server that exposes Roslyn and MSBuild to agents: navigation, analysis, refactoring and symbol edits, builds and tests, IL/JIT disassembly, decompilation, script evaluation, process inspection and debugging of C# projects. It targets `net10.0`.

## Commands

```sh
dotnet build src/Roslynaut.slnx
dotnet test src/Roslynaut.Tests                                              # all tests
dotnet test src/Roslynaut.Tests --filter "FullyQualifiedName~NavigationTests" # one class
dotnet test src/Roslynaut.Tests --filter "Name=References_HaveContainerAndAccess"
dotnet run --project src/Roslynaut -- --protocol stdio|http|sse              # no args = stdio
```

Environment variables:
- `ROSLYNAUT_ALLOW_EVALUATE=true` enables the `evaluate` tool, which runs arbitrary code. It is off by default.
- `NETCOREDBG` is the path to netcoredbg, which the debug tools need (or put it on the PATH). `DebugTests` are skipped through `Assume` when it is not set.

Tests use NUnit with AwesomeAssertions (`.Should()`). Both are global usings.

## Architecture

**Projects.** `Roslynaut` is the server. `Roslynaut.JitHost` and `Roslynaut.ScriptHost` are small helper executables. `Roslynaut.csproj` references them so their DLLs land in the server's output directory. The server runs them with `dotnet exec` through `DotnetCli`, which uses the `dotnet` of the runtime that runs the server.
- JitHost JIT-compiles methods without running them, with `DOTNET_JitDisasm` set (`JitDisassembler`). The runtime reads that setting at process start, so this needs a separate process.
- ScriptHost runs C# scripts for `evaluate`, isolated from the server. JSON goes in on stdin and comes back on stdout.

**Tool pattern.** Every MCP tool is a class that implements `IMcpTool` and is registered by hand in `Program.cs` (`.AddSingleton<IMcpTool, XTool>()`). A new tool that is not registered there is not exposed. `Handler` returns a private method. That method's `[McpServerTool(Name = "snake_case", ReadOnly/Destructive = ...)]` and `[Description]` attributes define the tool's MCP name and schema. Reuse the shared parameter descriptions in `ToolDefaults` (`IMcpTool.cs`). Keep handlers thin: put the logic in an `internal static` method that takes a `Solution`, for example `ReferencesTool.FindAsync`. Tests call that method directly (`InternalsVisibleTo`). Throw `McpException` for errors the client should see.

**Targeting symbols.** Most tools accept either a symbol path (`symbol`) or `filePath` + `line` + `column` (1-based). `WorkspaceService.ResolveAsync` handles both. Symbol paths are resolved by `SymbolTree`: dotted, case-insensitive, optionally with a parameter list to pick an overload, and they reach down to parameters and locals.

**WorkspaceService.** It loads a `.csproj`/`.sln`/`.slnx` through Buildalyzer design-time builds and caches one tracked workspace per path.
- It builds the `Compile` target, not Buildalyzer's default `Clean;Build`, which would delete build output. It follows project references level by level.
- Buildalyzer drops some compiler settings: NoWarn, WarningsAsErrors, .editorconfig/.globalconfig, the default namespace and document folders. `Load` re-applies them from the real compiler command line.
- `FileSystemWatcher`s queue `.cs` changes, and `GetAsync` applies them on the next call. Any change to a build file (`.csproj`, `.props`, `.targets`, `.sln(x)`, `.editorconfig`, `.globalconfig`) reloads everything.
- Editing tools never mutate the workspace directly. They compute a new `Solution` and call `WorkspaceService.WriteChangesAsync(before, after)`, which writes files to disk and keeps each file's BOM encoding. The watchers then bring the changes back in.

**ResultCache.** Paged tools (`PagedResult<T>`) cache results per snapshot (a `Solution` or a Cecil module) in a `ConditionalWeakTable`. The cache key is the tool name plus the arguments that shape the results. Results are evaluated lazily through `EvaluatedEnumerable`, as far as the requested page needs. Later requests continue that evaluation, so lazy parts must not capture the cancellation token of the request that created them.

**Other services** (DI singletons):
- `AssemblyLoader` reads assemblies with Mono.Cecil and emits compilations to temporary files. Cecil modules are not thread-safe: lock the module while reading it.
- `CodeActionService` collects Roslyn code fixes and refactorings from the Features assemblies and the project's analyzer packages. It remembers them per snapshot so `apply_code_action` can apply them by id.
- `DebugService` runs one netcoredbg session at a time over DAP (`DapClient`).

**Transport.** In stdio mode, stdout carries the MCP protocol, so all logging goes to stderr. Never write to `Console.Out` in the server.

## Testing

`TestSolution.Create(code)` builds an in-memory `AdhocWorkspace` solution, so most tests run without MSBuild. Tests that need real builds or processes (`TestToolsTests`, `ProcessInspectionTests`, `DebugTests`) are `[NonParallelizable]`. They write a temporary project in `[OneTimeSetUp]`.

## Conventions

- A `// ponytail:` comment marks a deliberate simplification. It names the limit and how to upgrade, for example a full reload on any build file change. Keep that convention.
- Comments explain why, not what. XML doc comments describe tool and record semantics, and the `[Description]` text is what MCP clients read.
