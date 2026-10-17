# AGENTS.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Roslynaut is an MCP server that exposes Roslyn and MSBuild to agents: navigation, analysis, refactoring and symbol edits, builds and tests, IL/JIT disassembly, decompilation, script evaluation, process inspection and debugging of C# projects. It targets `net10.0`.

## Commands

```sh
dotnet build src/Roslynaut.slnx
dotnet test src/Roslynaut.Tests                                              # all tests
dotnet test src/Roslynaut.Tests --filter "FullyQualifiedName~NavigationTests" # one class
dotnet test src/Roslynaut.Tests --filter "Name=References_HaveContainerAndAccess"
dotnet test src/Roslynaut.Tests --filter "TestCategory!=EndToEnd"                    # without the end-to-end tests
dotnet run --project src/Roslynaut -- --protocol stdio|http|sse              # no args = stdio
```

Environment variables:
- `ROSLYNAUT_ALLOW_EVALUATE=true` enables the `evaluate` tool, which runs arbitrary code. It is off by default.
- `NETCOREDBG` is the path to netcoredbg, which the debug tools need (or put it on the PATH). `DebugTests` are skipped through `Assume` when it is not set.

Tests use NUnit with AwesomeAssertions (`.Should()`). Both are global usings.

## Architecture

**Projects.** `Roslynaut` is the server. `Roslynaut.JitHost` and `Roslynaut.ScriptHost` are small helper executables. `Roslynaut.csproj` references them so their DLLs land in the server's output directory. The server runs them with `dotnet exec` through `DotnetCli`, which uses the `dotnet` of the runtime that runs the server. Commands on a project (build, test, restore) run from its directory, because `dotnet` reads global.json (SDK version, test runner) from the working directory.
- JitHost JIT-compiles methods without running them, with `DOTNET_JitDisasm` set (`JitDisassembler`). The runtime reads that setting at process start, so this needs a separate process. JitHost targets the oldest supported .NET and runs with a generated runtimeconfig.json on the runtime the assembly is built for.
- ScriptHost runs C# scripts for `evaluate`, isolated from the server. JSON goes in on stdin and comes back on stdout.
- Child processes' standard streams are UTF-8 (`DotnetCli.Utf8`): the dotnet CLI, MSBuild and git write it when redirected, and the hosts read and write it. Windows would otherwise use the console's code page, which garbles paths outside ASCII.

**Tool pattern.** Every MCP tool is a class that implements `IMcpTool` and is registered by hand in `Program.cs` (`.AddSingleton<IMcpTool, XTool>()`). A new tool that is not registered there is not exposed. `Handler` returns a private method. That method's `[McpServerTool(Name = "snake_case", ReadOnly/Destructive = ...)]` and `[Description]` attributes define the tool's MCP name and schema. Reuse the shared parameter descriptions in `ToolDefaults` (`IMcpTool.cs`). Keep handlers thin: put the logic in an `internal static` method that takes a `Solution`, for example `ReferencesTool.FindAsync`. Tests call that method directly (`InternalsVisibleTo`). Throw `McpException` for errors the client should see as they are. `McpServerService` wraps every tool in `ExplainingTool`, which reports missing arguments and arguments of the wrong type by name, and other exceptions with their type and message, where the SDK would only say that an error occurred.

**Targeting symbols.** Most tools accept either a symbol path (`symbol`) or `filePath` + `line` + `column` (1-based). `WorkspaceService.ResolveAsync` handles both. Symbol paths are resolved by `SymbolTree`: dotted, case-insensitive, optionally with a parameter list to pick an overload, and they reach down to parameters and locals. A constructor is `Type..ctor`, as results name it, or `Type.Type`.

**WorkspaceService.** It loads a `.csproj`/`.sln`/`.slnx` through Buildalyzer design-time builds and caches one tracked workspace per path.
- It builds the `Compile` target, not Buildalyzer's default `Clean;Build`, which would delete build output. It follows project references level by level.
- A project with `TargetFrameworks` (even one, even from an import) builds as the outer build, which has no `Compile` target. `Build` then builds it again for the first framework, or the one `workspace_options` chose (`LoadSettings`, kept per path along with the configuration).
- Buildalyzer only runs the builds. `ProjectOf` makes each project from its compiler command line (options, sources, additional files, analyzer configuration, references, analyzers), not with Buildalyzer.Workspaces, which drops most of that. A reference to another project's output (`TargetRefPath` or `TargetPath`) becomes a project reference, so analyzer-only references (`ReferenceOutputAssembly="false"`) do not. The projects of a load share one metadata reference per file: else every project reads each assembly again, and Roslyn gives a project copies of the symbols of the projects it references.
- Analyzers and generators load through Roslyn's internal non-locking loader (`NonLockingAnalyzerLoader`, by reflection; shadow copies on Windows), so builds can still write them while the server runs.
- `FileSystemWatcher`s queue `.cs` changes, and `GetAsync` applies them on the next call. Any change to a build file (`.csproj`, `.props`, `.targets`, `.sln(x)`, `.editorconfig`, `.globalconfig`) reloads everything. Besides the solution and project directories, they watch the build files the projects read from above them, such as a repository's `Directory.Build.props` and `.editorconfig`, and the directories of source files compiled from outside the project directories, such as a shared file linked with a Compile item.
- Editing tools never mutate the workspace directly. They compute a new `Solution` and call `WorkspaceService.WriteChangesAsync(before, after)`, which writes files to disk and keeps each file's BOM encoding. The watchers then bring the changes back in.
- `GetDocumentAsync` also finds a source generator's documents, by the path their locations give, which is not on disk. Tools that edit a file refuse those (`WorkspaceService.Editable`).

**ResultCache.** Paged tools (`PagedResult<T>`) cache results per snapshot (a `Solution` or a Cecil module) in a `ConditionalWeakTable`. The cache key is the tool name plus the arguments that shape the results. Results are evaluated lazily through `EvaluatedEnumerable`, as far as the requested page needs. Later requests continue that evaluation, so lazy parts must not capture the cancellation token of the request that created them.

**Other services** (DI singletons):
- `AssemblyLoader` reads assemblies with Mono.Cecil and emits compilations to temporary files. Cecil modules are not thread-safe: lock the module while reading it.
- `CodeActionService` collects Roslyn code fixes and refactorings from the Features assemblies and the project's analyzer packages. It remembers them per snapshot so `apply_code_action` can apply them by id.
- `DialogRefactorings` runs the refactorings an IDE takes options for from a dialog (`change_signature`, `extract_interface`): it builds Roslyn's internal options types by reflection and passes them to `CodeActionWithOptions.GetOperationsAsync(options)`. A Roslyn update can change those types; their tests catch it. `code_actions` leaves the other dialog refactorings out, since this host has no options services.
- `DebugService` runs one netcoredbg session at a time over DAP (`DapClient`): a launched program, an attached process, or a test host that `dotnet test` with `VSTEST_HOST_DEBUG=1` starts and keeps waiting for the debugger. Under Microsoft.Testing.Platform, `dotnet test` does not pass on the test application's output, so the application (waiting through `TESTINGPLATFORM_WAIT_ATTACH_DEBUGGER=1`) is found by its app host's process name.
- `NuGetService` reads a project's nuget.config files (sources, source mapping, global packages folder) the way restore does, signs in to private feeds with nuget.config credentials or else credential providers (non-interactively, on what `dotnet restore --interactive` cached), downloads packages into the global packages folder, and reads restore results (project.assets.json) for the `package_*` and `nuget_config` tools.
- `PdbService` finds an assembly's portable PDB: next to the assembly, embedded in it, or downloaded from the symbol servers or directories `_NT_SYMBOL_PATH` names, then the Microsoft and NuGet symbol servers. nuget.org needs the `SymbolChecksum` header. It also fetches the source files that the PDB lists. It tries the source embedded in the PDB first (source generators' output is usually there), then the local file, then the SourceLink URL (https only, no credentials). Each file is checked against the PDB's checksum. Downloads are cached under `%TEMP%/Roslynaut` by PDB id or checksum.
  - PDB source omits code the compiler synthesizes, IL weaving, and usually the files MSBuild writes to `obj/`. `decompile` stays the view that matches the IL.
  - `symbol_source`, `definition`, `assembly_sources`, `decompile` (local variable names), `process_threads`, the debug frames and `package_assembly` use it.
  - Tools without the network (`decompile`, `process_threads`) call `FindPdb`, which only finds PDBs on disk or downloaded earlier.

**Binary logs.** `build` writes a binary log with `binlogPath` (and always with `timings`, adding `-p:ReportAnalyzer=true` and `-profileevaluation`). The `build_*` tools read one through `BuildLog`, which runs MSBuild.StructuredLogger's `BuildAnalyzer` (double writes, file copies, analyzer summaries) and keeps the last two logs read. The log has no record of targets skipped for a false condition.

**Transport.** In stdio mode, stdout carries the MCP protocol, so all logging goes to stderr. Never write to `Console.Out` in the server.

## Testing

`TestSolution.Create(code)` builds an in-memory `AdhocWorkspace` solution, so most tests run without MSBuild. Tests that need real builds or processes (`TestToolsTests`, `ProcessInspectionTests`, `DebugTests`) are `[NonParallelizable]`. They write a temporary project in `[OneTimeSetUp]`.

The end-to-end tests (`EndToEnd/`, category `EndToEnd`, about three minutes) run the server as a client does and call every tool through MCP.
- `EndToEndSetup` writes `SampleSolution` to a temporary git repository: a library, a console app and an NUnit project, plus a local feed with two versions of a package that embeds its PDB and sources. It starts one stdio server for all of them and builds the sample through the server's `build` tool, whose binary log the `build_*` tests read.
- Packages restore into the sample's own packages folder, falling back on the machine's global packages folder, so no package is downloaded that the test project's own restore did not already download.
- Tests read results into the server's own records (`ServerProcess.CallAsync<T>`) and compare them with what `SampleSolution.Location` finds in the files, so a change to the sample moves the expected positions along.
- Tests that change code do so in a project of their own (`SampleSolution.NewProject`); `EventuallyAsync` waits for the file watchers to bring a change in. `WorkspaceSyncTests` change files behind the server's back, as editors and git do. `TransportTests` start HTTP and SSE servers of their own. `DebugTests` need netcoredbg, as above.
- Run only them with `--filter "TestCategory=EndToEnd"`, or leave them out with `--filter "TestCategory!=EndToEnd"`.

## Conventions

- A `// ponytail:` comment marks a deliberate simplification. It names the limit and how to upgrade, for example a full reload on any build file change. Keep that convention.
- Comments explain why, not what. XML doc comments describe tool and record semantics, and the `[Description]` text is what MCP clients read.
