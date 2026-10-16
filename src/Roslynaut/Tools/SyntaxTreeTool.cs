using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class SyntaxTreeTool(WorkspaceService workspace) : IMcpTool
{
    private const int MaxNodes = 400;

    public Delegate Handler => DumpTreeAsync;

    [McpServerTool(Name = "syntax_tree", ReadOnly = true)]
    [Description("Dumps the syntax tree of some lines of a file, from the smallest node that covers them: each node with its kind, line and text, "
        + "indented by depth. With operations, dumps the semantic operation tree instead, with types, constants and the symbols operations "
        + "refer to. For writing analyzers or generators, or understanding how code binds. At most 400 nodes.")]
    private async Task<string> DumpTreeAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.FilePath)] string filePath,
        [Description("1-based first line.")] int startLine,
        [Description("1-based last line.")] int endLine,
        [Description("Dump operations instead of syntax.")] bool operations = false,
        CancellationToken cancellationToken = default)
    {
        var document = await workspace.GetDocumentAsync(projectPath, filePath);
        return await DumpAsync(document, WorkspaceService.LinesOf(await document.GetTextAsync(cancellationToken), startLine, endLine, filePath), operations, cancellationToken);
    }

    internal static async Task<string> DumpAsync(Document document, TextSpan span, bool operations, CancellationToken cancellationToken)
    {
        var model = (await document.GetSemanticModelAsync(cancellationToken))!;
        var root = await model.SyntaxTree.GetRootAsync(cancellationToken);
        var text = await document.GetTextAsync(cancellationToken);
        var top = root.FindNode(span);
        var dump = new StringBuilder();
        var count = 0;

        string Describe(SyntaxNode node)
        {
            var snippet = node.ToString().ReplaceLineEndings(" ");
            return $"[{text.Lines.GetLineFromPosition(node.SpanStart).LineNumber + 1}] {(snippet.Length > 60 ? snippet[..60] + "…" : snippet)}";
        }

        bool Append(int depth, string line)
        {
            if (++count > MaxNodes)
                return false;
            dump.Append(' ', depth * 2).Append(line).Append('\n');
            return true;
        }

        void Syntax(SyntaxNode node, int depth)
        {
            if (!Append(depth, $"{node.Kind()} {Describe(node)}"))
                return;
            foreach (var child in node.ChildNodes())
                Syntax(child, depth + 1);
        }

        void Operation(IOperation operation, int depth)
        {
            var details = new List<string>();
            if (operation.Type is { } type)
                details.Add($"Type: {type.ToDisplayString()}");
            if (operation.ConstantValue.HasValue)
                details.Add($"Constant: {operation.ConstantValue.Value ?? "null"}");
            if (ReferencedSymbol(operation) is { } symbol)
                details.Add($"Symbol: {symbol.ToDisplayString()}");
            if (operation.IsImplicit)
                details.Add("Implicit");
            if (!Append(depth, $"{operation.Kind} ({string.Join(", ", details)}) {Describe(operation.Syntax)}"))
                return;
            foreach (var child in operation.ChildOperations)
                Operation(child, depth + 1);
        }

        if (!operations)
        {
            Syntax(top, 0);
        }
        else
        {
            // The operations of the node, or of its nearest ancestor that has one, or else of its descendants.
            var start = top.AncestorsAndSelf().Select(node => model.GetOperation(node, cancellationToken)).FirstOrDefault(operation => operation is not null);
            foreach (var operation in start is null ? CalleesTool.OperationsOf(model, top, cancellationToken) : [start])
                Operation(operation, 0);
        }

        if (count > MaxNodes)
            dump.Append($"… cut off at {MaxNodes} nodes: pick fewer lines.\n");
        return dump.ToString();
    }

    private static ISymbol? ReferencedSymbol(IOperation operation) => operation switch
    {
        IInvocationOperation invocation => invocation.TargetMethod,
        IObjectCreationOperation creation => creation.Constructor,
        IMemberReferenceOperation member => member.Member,
        ILocalReferenceOperation local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        IVariableDeclaratorOperation declarator => declarator.Symbol,
        IConversionOperation { OperatorMethod: { } method } => method,
        IBinaryOperation { OperatorMethod: { } method } => method,
        _ => null,
    };
}
