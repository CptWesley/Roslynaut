# Roslynaut
Roslyn/MSBuild MCP

## Install

Roslynaut is a .NET tool and needs the .NET 10 runtime or later.

```sh
dotnet tool install -g Roslynaut
roslynaut --install claude   # registers the server with Claude Code
```

Other MCP clients run `roslynaut` (stdio), or `roslynaut --protocol http`.

## Uninstall

```sh
roslynaut --uninstall claude   # removes the server and its hook from Claude Code
dotnet tool uninstall -g Roslynaut
```
