using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class InlayHintsTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => HintLinesAsync;

    [McpServerTool(Name = "inlay_hints", ReadOnly = true)]
    [Description("Returns lines of a file with the hints an IDE shows inline, as /*...*/ comments: the type inferred for var and for lambda "
        + "parameters, and the parameter names of literal and new arguments. Each line starts with its number.")]
    private async Task<string> HintLinesAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.FilePath)] string filePath,
        [Description("1-based first line.")] int startLine,
        [Description("1-based last line.")] int endLine,
        CancellationToken cancellationToken = default)
    {
        var document = await workspace.GetDocumentAsync(projectPath, filePath);
        var text = await document.GetTextAsync(cancellationToken);
        return await AnnotateAsync(document, startLine, WorkspaceService.LinesOf(text, startLine, endLine, filePath), cancellationToken);
    }

    internal static async Task<string> AnnotateAsync(Document document, int startLine, TextSpan span, CancellationToken cancellationToken)
    {
        var model = (await document.GetSemanticModelAsync(cancellationToken))!;
        var root = await model.SyntaxTree.GetRootAsync(cancellationToken);
        var hints = new List<TextChange>();
        void Hint(int position, string hint) => hints.Add(new(new TextSpan(position, 0), hint));
        string Name(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        foreach (var node in root.DescendantNodes(span).Where(node => span.Contains(node.Span)))
        {
            switch (node)
            {
                case VariableDeclarationSyntax { Type.IsVar: true } declaration when model.GetTypeInfo(declaration.Type, cancellationToken).Type is { } type:
                    Hint(declaration.Type.Span.End, $" /*{Name(type)}*/");
                    break;
                case ForEachStatementSyntax { Type.IsVar: true } loop when model.GetForEachStatementInfo(loop).ElementType is { } element:
                    Hint(loop.Type.Span.End, $" /*{Name(element)}*/");
                    break;
                case DeclarationExpressionSyntax { Type.IsVar: true, Designation: SingleVariableDesignationSyntax } declaration
                    when model.GetTypeInfo(declaration, cancellationToken).Type is { } type:
                    Hint(declaration.Type.Span.End, $" /*{Name(type)}*/");
                    break;
                case ParameterSyntax { Type: null, Parent: ParameterListSyntax { Parent: LambdaExpressionSyntax } or LambdaExpressionSyntax } parameter
                    when model.GetDeclaredSymbol(parameter, cancellationToken) is { } symbol:
                    Hint(parameter.SpanStart, $"/*{Name(symbol.Type)}*/ ");
                    break;
                case ArgumentSyntax { NameColon: null, Expression: LiteralExpressionSyntax or BaseObjectCreationExpressionSyntax } argument
                    when model.GetOperation(argument, cancellationToken) is IArgumentOperation { Parameter: { } parameter }:
                    Hint(argument.SpanStart, $"/*{parameter.Name}:*/ ");
                    break;
            }
        }

        var text = await document.GetTextAsync(cancellationToken);
        var annotated = SourceText.From(text.ToString(span)).WithChanges(hints.Select(hint => new TextChange(new TextSpan(hint.Span.Start - span.Start, 0), hint.NewText!)));
        var lines = new StringBuilder();
        foreach (var line in annotated.Lines)
            lines.Append(startLine + line.LineNumber).Append(": ").Append(line).Append('\n');
        return lines.ToString();
    }
}
