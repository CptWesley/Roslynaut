# Changelog

Every release of the [Roslynaut](https://www.nuget.org/packages/Roslynaut/) package, newest first. The entry of the latest version is also its `PackageReleaseNotes` in `src/Roslynaut/Roslynaut.csproj`, which nuget.org shows.

## 0.3.0 (unreleased)

- --install and --uninstall take more agents: codex (Codex CLI, IDE extension and app, in ~/.codex/config.toml or under CODEX_HOME, with a tool timeout of 30 minutes for builds and tests), vscode (the user profile's mcp.json), visualstudio (~/.mcp.json), copilot (GitHub Copilot CLI, in ~/.copilot/mcp-config.json or under COPILOT_HOME), cursor (~/.cursor/mcp.json), gemini (Gemini CLI, ~/.gemini/settings.json) and junie (~/.junie/mcp/mcp.json). They get the MCP server for all projects, without a hook.
- --install codex edits config.toml in place, keeping the rest of the file, comments included.
- The README describes running Roslynaut through dnx, without installing the tool, where dotnet tool install -g fails.

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
