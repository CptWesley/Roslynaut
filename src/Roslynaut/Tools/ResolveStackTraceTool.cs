using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed partial class ResolveStackTraceTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => ResolveTraceAsync;

    [McpServerTool(Name = "resolve_stack_trace", ReadOnly = true)]
    [Description("Maps a .NET stack trace, such as from an exception's ToString(), a log, a test failure or CI output, to the solution's current source: "
        + "per frame, the symbol path of the member it ran in, where compiler-generated frames name their method (an async or iterator MoveNext, "
        + "a lambda's <>c__DisplayClass, a local function's <Method>g__Local), and its file and line with the text of that line. A frame without "
        + "a line, as from a Release build without PDBs, points at the method's declaration; a line that is no longer in that member is flagged, "
        + "as when the file changed since the build or the trace comes from a build machine. Frames of code outside the solution have no symbol.")]
    private async Task<IReadOnlyList<ResolvedFrame>> ResolveTraceAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description("The stack trace text: lines such as '   at MyApp.Orders.<SubmitAsync>d__4.MoveNext() in C:\\src\\Orders.cs:line 42'; other lines are skipped.")] string stackTrace,
        CancellationToken cancellationToken = default) =>
        await ResolveAsync((await workspace.GetAsync(projectPath)).Solution, stackTrace, cancellationToken);

    internal static async Task<IReadOnlyList<ResolvedFrame>> ResolveAsync(Solution solution, string stackTrace, CancellationToken cancellationToken)
    {
        var frames = new List<ResolvedFrame>();
        foreach (var line in stackTrace.Split('\n').Select(line => line.Trim()))
        {
            if (FrameLine().Match(line) is not { Success: true } frame)
                continue;

            var (path, parameters) = PathOf(frame.Groups["method"].Value);
            int? number = frame.Groups["line"].Success ? int.Parse(frame.Groups["line"].ValueSpan) : null;
            var file = frame.Groups["file"].Success ? frame.Groups["file"].Value : null;
            frames.Add(await ResolveFrameAsync(solution, line, path, parameters, file, number, cancellationToken));
        }

        return frames.Count > 0 ? frames : throw new McpException("The text has no stack frames, lines such as '   at Namespace.Type.Method() in File.cs:line 12'.");
    }

    private static async Task<ResolvedFrame> ResolveFrameAsync(
        Solution solution, string text, string path, int? parameters, string? file, int? line, CancellationToken cancellationToken)
    {
        IReadOnlyList<ISymbol> symbols;
        try
        {
            symbols = [.. (await SymbolTree.ResolveAsync(solution, path, cancellationToken)).Where(symbol => symbol.Locations.Any(location => location.IsInSource))];
        }
        catch (McpException)
        {
            symbols = [];
        }

        if (symbols.Count == 0)
            return new(text, null, null, null);

        // The document the trace names, if it is the solution's; else, as for a trace from a build machine, the file the member is in now.
        var named = file is null ? null : solution.GetDocumentIdsWithFilePath(Path.GetFullPath(file)).Select(solution.GetDocument).FirstOrDefault();
        var candidates = symbols.Where(symbol => parameters is null || symbol is not IMethodSymbol method || method.Parameters.Length == parameters).ToList();
        var symbol = candidates.FirstOrDefault(candidate => line is not null && DeclarationAt(candidate, named?.FilePath, line.Value) is not null)
            ?? candidates.FirstOrDefault() ?? symbols[0];
        var declaration = SymbolTree.DeclarationsOf(symbol).First();
        var tree = named is not null && await named.GetSyntaxTreeAsync(cancellationToken) is { } namedTree ? namedTree : declaration.SyntaxTree;
        var symbolPath = $"{SymbolEntry.PathOf(symbol.ContainingSymbol)}.{symbol.Name}";

        if (line is not { } number)
            return new(text, symbolPath, SourceLocation.From(Location.Create(declaration.SyntaxTree, declaration.Span)), "The trace has no line: this is where the member is declared.");

        var lines = (await tree.GetTextAsync(cancellationToken)).Lines;
        if (number < 1 || number > lines.Count)
            return new(text, symbolPath, SourceLocation.From(Location.Create(declaration.SyntaxTree, declaration.Span)),
                $"Line {number} is past the end of {Path.GetFileName(tree.FilePath)} now: the file changed since the build. This is where the member is declared.");

        var location = new SourceLocation(tree.FilePath, number, 1, lines[number - 1].ToString().Trim());
        return DeclarationAt(symbol, tree.FilePath, number) is null
            ? new(text, symbolPath, location, $"Line {number} is not in {symbolPath} now: the file changed since the build, or the trace is of another version.")
            : new(text, symbolPath, location, null);
    }

    /// <summary>The declaration of a symbol that spans a line of a file, if any.</summary>
    private static SyntaxReference? DeclarationAt(ISymbol symbol, string? filePath, int line) => SymbolTree.DeclarationsOf(symbol).FirstOrDefault(reference =>
        (filePath is null || string.Equals(reference.SyntaxTree.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
        && reference.SyntaxTree.GetLineSpan(reference.Span) is var span && span.StartLinePosition.Line + 1 <= line && line <= span.EndLinePosition.Line + 1);

    /// <summary>
    /// A frame's method as a symbol path, and how many parameters it has: generic arities and the type and method parameters go, and the
    /// compiler's generated names become the members they stand for, as "Shop.<>c__DisplayClass2_0.<Submit>b__0" is a lambda in Shop.Submit.
    /// </summary>
    internal static (string Path, int? Parameters) PathOf(string method)
    {
        var open = method.IndexOf('(');
        var name = open < 0 ? method : method[..open];
        int? parameters = open < 0 ? null : method[(open + 1)..].TrimEnd(')').Trim() is { Length: > 0 } list ? list.Split(',').Length : 0;

        // Constructors are .ctor and .cctor, the one name with a dot of its own; a character no name has stands in for it.
        name = GenericArity().Replace(MethodTypeParameters().Replace(name, ""), "").Replace("..ctor", ".\u0001ctor").Replace("..cctor", ".\u0001cctor");
        var segments = name.Split('.').Select(segment => segment.Replace('\u0001', '.')).ToList();
        if (segments is [.., var machine, "MoveNext"] && StateMachine().IsMatch(machine))
            segments.RemoveAt(segments.Count - 1);

        var path = new List<string>();
        foreach (var segment in segments)
        {
            // A lambda's closure class or the class of a type's static lambdas.
            if (segment.StartsWith("<>c", StringComparison.Ordinal))
                continue;

            path.AddRange(Generated(segment));
        }

        // A generated method's parameters are not its member's: a lambda's are its own, a local function's add its captures.
        if (segments.Any(segment => segment.StartsWith('<') && segment != "<Main>$"))
            parameters = null;

        // A lambda in a method is named after it: Submit then Submit.
        return (string.Join('.', path.Where((segment, i) => i == 0 || segment != path[i - 1])), parameters);
    }

    /// <summary>
    /// The members a generated name stands for: &lt;Submit&gt;d__4 (state machine) and &lt;Submit&gt;b__0 (lambda) for Submit,
    /// &lt;Submit&gt;g__Check|4_0 for the local function Submit.Check; nested, as an async lambda's &lt;&lt;Submit&gt;b__0&gt;d.
    /// </summary>
    private static IEnumerable<string> Generated(string segment)
    {
        if (GeneratedName().Match(segment) is not { Success: true } generated)
            return [segment];

        // Top-level statements' method keeps its name: Program.<Main>$.
        var inner = generated.Groups["inner"].Value;
        if (generated.Groups["kind"].Value == "$")
            return [segment];

        var outer = inner.StartsWith('<') ? Generated(inner) : [inner];
        return generated.Groups["kind"].Value == "g" && generated.Groups["local"].Success ? [.. outer, generated.Groups["local"].Value] : outer;
    }

    [GeneratedRegex(@"^at (?<method>.+?)(?: in (?<file>.+):line (?<line>\d+))?$")]
    private static partial Regex FrameLine();

    [GeneratedRegex(@"`\d+")]
    private static partial Regex GenericArity();

    [GeneratedRegex(@"\[[^\[\]]*\]")]
    private static partial Regex MethodTypeParameters();

    [GeneratedRegex(@"^<.+>d(__\d+)?$")]
    private static partial Regex StateMachine();

    [GeneratedRegex(@"^<(?<inner>.+)>(?<kind>[a-z$])(?:__(?:(?<local>[^|]+)\|)?[\d_]*)?$")]
    private static partial Regex GeneratedName();

    /// <param name="Text">The frame as the trace has it.</param>
    /// <param name="Symbol">The symbol path of the member the frame ran in, for the other tools; null for code outside the solution.</param>
    /// <param name="Location">The line in current source, with its text.</param>
    /// <param name="Note">Why the location is not simply the trace's line: it has none, or that line is no longer in the member.</param>
    public sealed record ResolvedFrame(string Text, string? Symbol, SourceLocation? Location, string? Note);
}
