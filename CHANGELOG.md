# Changelog

Every release of the [Roslynaut](https://www.nuget.org/packages/Roslynaut/) package, newest first. The entry of the latest version is also its `PackageReleaseNotes` in `src/Roslynaut/Roslynaut.csproj`, which nuget.org shows.

## 0.4.0 (unreleased)

- decompile, assembly_asm, symbol_asm (without pgoWorkload) and type_layout keep their results in a cache under %TEMP%/Roslynaut/Cache, by the content of the assembly and of the assemblies, .deps.json and .runtimeconfig.json next to it (or the dependencies of code compiled from source), and for listings the runtime that compiles them. A later call for the same code, from this or another server, answers at once instead of decompiling again or starting the JIT host.
- --install run through dnx registers dotnet dnx with the version that runs (such as Roslynaut@0.4.0 --yes), which downloads it again when the NuGet caches were cleared. It registered the copy in the NuGet packages folder, which clearing the caches deleted, so the agent could no longer start the server or its hooks.
- --install on Windows writes the paths of its commands with forward slashes. Agents that run a command through a shell, as Claude Code runs its hooks through Git Bash, read the backslashes as escapes, and the hooks failed.
- The server leaves out the tools that cannot work, and says why on standard error: evaluate and benchmark without ROSLYNAUT_ALLOW_EVALUATE=true, and the debug tools without netcoredbg. Agents saw them, and calling them failed.
- --install with --enable-tools or --disable-tools registers the server with those options, so the agent gets only those tools. It registered all tools.
- --install with --no-hooks registers the server alone: Claude Code gets no hooks and OpenCode no plugin, and those an earlier installation added are removed. The other agents get no hook either way.
- The server instructions fit in the 4,000 characters Claude Code keeps of them. It cut them off after the packages tools, so agents lacked the guidance for the profiling, memory, process, debugger and quality tools.
- Tool schemas take about 7% less of agents' context: optional parameters leave null out of their types and defaults, and the symbol path description they share is shorter.
- The Claude Code hooks give each hint once per session, and again after it starts anew or compacts. They repeated it on every read, grep or edit of C# code.
- --tool-descriptions and --field-descriptions serve the tools' descriptions or their parameters' in full, as their first sentence (short), in caveman style (terse, keeping identifiers and search keywords) or not at all (none), for less of agents' context, and the new tool explain_tool gives them in full for the tools an agent calls. --install registers the server with them.
- definition and signature_help expand <inheritdoc/> in the documentation they return: with its cref, path, or the member the symbol overrides or implements, its base types and interfaces. An undocumented method, property or event that overrides or implements another gets that one's documentation. They returned the tag as it was, and nothing for an undocumented override or implementation.

## 0.3.2 (2026-11-23)

- A solution whose projects reference each other loads all of them when none was restored yet. The design-time builds restored in parallel, each also restoring the projects it references, and could break one another's restore (NETSDK1004): a project then loaded without its code, so tools missed it, as change_signature missed the calls in a referencing project.

## 0.3.1 (2026-11-20)

- Projects that build with the .NET 11 SDK load with their C# 15 code and the analyzers and source generators built for its compiler, such as the Razor generator. Roslynaut's compiler (Roslyn 5.9) left those out, with warnings CS1617 and CS9057 from diagnostics; it is now Roslyn 5.12. Projects on older SDKs load as before.
- The README's requirements say which SDKs Roslynaut needs to install and run, and up to which C# version it reads code.

## 0.3.0 (2026-11-18)

- --install and --uninstall take more agents: codex (Codex CLI, IDE extension and app, in ~/.codex/config.toml or under CODEX_HOME, with a tool timeout of 30 minutes for builds and tests), vscode (the user profile's mcp.json), visualstudio (~/.mcp.json), copilot (GitHub Copilot CLI, in ~/.copilot/mcp-config.json or under COPILOT_HOME), cursor (~/.cursor/mcp.json), gemini (Gemini CLI, ~/.gemini/settings.json) and junie (~/.junie/mcp/mcp.json). They get the MCP server for all projects, without a hook.
- --install codex edits config.toml in place, keeping the rest of the file, comments included.
- The README describes running Roslynaut through dnx, without installing the tool, where dotnet tool install -g fails.
- process_cpu, process_allocations and process_events take trace: a .nettrace file recorded earlier, such as by dotnet-trace collect on CI or another machine, to analyze instead of tracing a process.
- diagnostics takes aot: the trim, native AOT and single-file warnings (IL2xxx, IL3xxx) that PublishTrimmed or PublishAot would give, without changing the project or publishing.
- verify_pending lists the snapshots Verify tests received with their diffs from the verified ones, and accepts or rejects them.
- regex_test runs a .NET regular expression against sample inputs, with each match's groups and the result of a replacement, without evaluate.
- resources checks .resx files against their translations (resources a culture lacks or only it has) and finds the resources no C# code uses.
- The Claude Code and OpenCode hooks suggest resources for .resx files and verify_pending for received snapshots and after dotnet test.
- diagnostics with analyzerPackages loads packages whose analyzers are not in a roslyn* folder, where it failed with "Sequence contains no elements".
- The package is precompiled (ReadyToRun) for Windows, Linux and macOS on x64 and Arm64, so the server starts and answers its first calls sooner. It comes as a package per platform, which dotnet tool install and dnx pick, and a framework-dependent one for other platforms.
- process_events with kind Contention reports the threads that held a lock on Linux too, where it failed with "Value was either too large or too small for a UInt64".

## 0.2.1 (2026-11-13)

- Projects load with the .NET SDK's MSBuild, as dotnet build uses, instead of Visual Studio's MSBuild.exe, which failed with MSB4236 where Visual Studio Build Tools lack the .NET SDK component.
- build_problems lists each version of a conflicting assembly again, with the references that depend on it, for builds with MSBuild 18, which no longer logs them under the conflict.
- process_cpu, process_allocations and process_events stop when a process they attached to exits, and return what they recorded until then, instead of waiting out the seconds and failing with "Process is not running".
- process_cpu returns call trees up to 126 calls deep, where root and leaf failed with "A possible object cycle was detected" on trees deeper than about 30.

## 0.2.0 (2026-11-10)

- --install opencode and --uninstall opencode register Roslynaut with OpenCode, with a plugin that suggests its tools when OpenCode reads or greps C# files.
- The Claude Code hooks give better suggestions of which tools to use.
- The GitHub release carries the package with a build provenance attestation.

## 0.1.2-preview.1 (2026-11-01)

- No changes to the package: a release of the publishing workflow.

## 0.1.1 (2026-11-01)

- process_threads and the other process tools take a process's snapshot once, without a retry.

## 0.1.0 (2026-11-01)

- First release, tagged only: nuget.org starts at 0.1.1.
