using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace Roslynaut;

/// <summary>
/// Registers the server with Codex for all projects (<c>--install codex</c>), as the table <c>[mcp_servers.roslynaut]</c> in its
/// <c>config.toml</c>, which the Codex CLI, IDE extension and app share. It gets no hook.
/// </summary>
/// <remarks>It edits the file's syntax tree, so the rest of it keeps its comments and layout.</remarks>
internal static class CodexInstall
{
    public static void Run(bool uninstall)
    {
        // CODEX_HOME moves Codex's configuration directory, as it does for Codex itself.
        var configFile = Path.Combine(McpConfigInstall.Variable("CODEX_HOME") ?? McpConfigInstall.Home(".codex"), "config.toml");
        if (uninstall)
            Uninstall(configFile, Console.Out);
        else
            Install(configFile, AgentInstall.Command(), Console.Out);
    }

    internal static void Install(string configFile, string[] command, TextWriter log)
    {
        var (document, found) = Remove(configFile);
        var table = Table(command);
        if (found == table.ToString().Trim())
        {
            log.WriteLine($"The MCP server {AgentInstall.Name} in {configFile} is up to date.");
            return;
        }

        document.Tables.Add(table);
        JsonFile.WriteText(configFile, document.ToString().TrimStart());
        log.WriteLine($"{(found is "" ? "Added" : "Updated")} the MCP server {AgentInstall.Name} in {configFile}.");
    }

    internal static void Uninstall(string configFile, TextWriter log)
    {
        var (document, found) = Remove(configFile);
        if (found is "")
        {
            log.WriteLine($"There is no MCP server {AgentInstall.Name} in {configFile}.");
            return;
        }

        JsonFile.WriteText(configFile, document.ToString());
        log.WriteLine($"Removed the MCP server {AgentInstall.Name} from {configFile}.");
    }

    /// <summary>
    /// Reads the file and takes the server out of it: its tables, [mcp_servers.roslynaut] and subtables such as [mcp_servers.roslynaut.env],
    /// and dotted keys at the root or in [mcp_servers]. Found is their text.
    /// </summary>
    /// <remarks>ponytail: leaves an inline table (mcp_servers = { roslynaut = ... }) alone; nobody writes Codex servers that way.</remarks>
    private static (DocumentSyntax Document, string Found) Remove(string configFile)
    {
        var text = File.Exists(configFile) ? File.ReadAllText(configFile) : "";
        // A last line without a line break would run into the table added after it.
        if (text.Length > 0 && !text.EndsWith('\n'))
            text += "\n";
        // A file that is not TOML throws here, before anything is written.
        var document = SyntaxParser.ParseStrict(text, configFile, validate: true);

        List<SyntaxNode> found = [];
        foreach (var table in document.Tables)
        {
            var name = Parts(table.Name);
            if (Ours(name))
                found.Add(table);
            else if (name is ["mcp_servers"])
                foreach (var item in table.Items)
                    if (Ours(["mcp_servers", .. Parts(item.Key)]))
                        found.Add(item);
        }

        foreach (var item in document.KeyValues)
            if (Ours(Parts(item.Key)))
                found.Add(item);

        var foundText = string.Concat(found.Select(node => node.ToString())).Trim();
        foreach (var node in found)
        {
            switch (node.Parent)
            {
                case SyntaxList<TableSyntaxBase> tables: tables.RemoveChild((TableSyntaxBase)node); break;
                case SyntaxList<KeyValueSyntax> items: items.RemoveChild((KeyValueSyntax)node); break;
            }
        }

        return (document, foundText);
    }

    private static bool Ours(string?[] path) =>
        path is ["mcp_servers", var name, ..] && string.Equals(name, AgentInstall.Name, StringComparison.OrdinalIgnoreCase);

    private static string?[] Parts(KeySyntax? key) => key is null ? [] : [Part(key.Key), .. key.DotKeys.Select(dotted => Part(dotted.Key))];

    private static string? Part(BareKeyOrStringValueSyntax? key) => key switch
    {
        BareKeySyntax bare => bare.Key?.Text,
        StringValueSyntax quoted => quoted.Value,
        _ => null,
    };

    // Parsed rather than built, which sets the layout: a blank line before it, as between the other tables. Builds and tests through the
    // server take longer than Codex's default of 60 seconds a tool call.
    internal static TableSyntax Table(string[] command)
    {
        var document = SyntaxParser.ParseStrict($"""

            [mcp_servers.{AgentInstall.Name}]
            command = {new StringValueSyntax(command[0])}
            args = {new ArraySyntax(command[1..])}
            tool_timeout_sec = 1800

            """, validate: true);
        var table = (TableSyntax)document.Tables.GetChild(0)!;
        document.Tables.RemoveChildAt(0);
        return table;
    }
}
