using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class SignatureHelpTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => GetSignatureHelpAsync;

    [McpServerTool(Name = "signature_help", ReadOnly = true)]
    [Description("At a position between the parentheses (or brackets) of a call, constructor or indexer: the overloads it could call, "
        + "with their documentation, which one the arguments pick, and which parameter the argument at the position goes to.")]
    private async Task<IEnumerable<Signature>> GetSignatureHelpAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.FilePath)] string filePath,
        [Description(ToolDefaults.Line)] int line,
        [Description("1-based column within the argument list.")] int column,
        CancellationToken cancellationToken = default)
    {
        var document = await workspace.GetDocumentAsync(projectPath, filePath);
        return await GetAsync(document, WorkspaceService.PositionOf(await document.GetTextAsync(cancellationToken), line, column, filePath), cancellationToken);
    }

    internal static async Task<IEnumerable<Signature>> GetAsync(Document document, int position, CancellationToken cancellationToken)
    {
        var model = (await document.GetSemanticModelAsync(cancellationToken))!;
        var root = await model.SyntaxTree.GetRootAsync(cancellationToken);
        var arguments = root.FindToken(position).Parent!.AncestorsAndSelf().OfType<BaseArgumentListSyntax>()
            .FirstOrDefault(list => list.SpanStart < position && position <= list.Span.End)
            ?? throw new McpException("The position is not inside the argument list of a call, constructor or indexer.");

        var call = arguments.Parent!;
        var bound = model.GetSymbolInfo(call, cancellationToken);
        IEnumerable<ISymbol> overloads = call switch
        {
            InvocationExpressionSyntax invocation => model.GetMemberGroup(invocation.Expression, cancellationToken),
            BaseObjectCreationExpressionSyntax creation when model.GetTypeInfo(creation, cancellationToken).Type is INamedTypeSymbol type =>
                type.InstanceConstructors.Where(constructor => model.IsAccessible(position, constructor)),
            ElementAccessExpressionSyntax access => model.GetIndexerGroup(access.Expression, cancellationToken),
            _ => bound.Symbol is { } symbol ? [symbol] : bound.CandidateSymbols,
        };

        // The argument at the position: by its name, or else by the commas before it.
        var argument = arguments.Arguments.FirstOrDefault(argument => argument.FullSpan.Contains(position) || argument.FullSpan.End == position);
        var index = arguments.Arguments.GetSeparators().Count(separator => separator.SpanStart < position);
        var chosen = bound.Symbol ?? (bound.CandidateSymbols.Length == 1 ? bound.CandidateSymbols[0] : null);

        return overloads.Select(overload =>
        {
            var parameters = overload switch { IMethodSymbol method => method.Parameters, IPropertySymbol indexer => indexer.Parameters, _ => [] };
            var (summary, parameterDocs) = Documentation(overload, cancellationToken);

            // A named argument goes to its parameter; others by position, extra ones into a params array.
            int? active = argument?.NameColon is { } name
                ? parameters.Select((parameter, i) => (parameter.Name, i)).Where(entry => entry.Name == name.Name.Identifier.ValueText).Select(entry => (int?)entry.i).FirstOrDefault()
                : index < parameters.Length ? index : parameters is [.., { IsParams: true }] ? parameters.Length - 1 : null;
            return new Signature(
                overload.ToDisplayString(),
                summary,
                [.. parameters.Select(parameter => new Parameter(parameter.Name, parameter.Type.ToDisplayString(), parameterDocs.GetValueOrDefault(parameter.Name), parameter.IsOptional))],
                active,
                SymbolEqualityComparer.Default.Equals(overload, chosen));
        }).ToList();
    }

    /// <summary>The summary and parameter descriptions of a documentation comment, as plain text.</summary>
    private static (string? Summary, Dictionary<string, string> Parameters) Documentation(ISymbol symbol, CancellationToken cancellationToken)
    {
        var xml = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(xml))
            return (null, []);

        try
        {
            var member = XElement.Parse(xml);
            return (
                member.Element("summary") is { } summary ? Text(summary) : null,
                member.Elements("param").Where(param => param.Attribute("name") is not null).ToDictionary(param => (string)param.Attribute("name")!, Text));
        }
        catch (System.Xml.XmlException)
        {
            return (null, []);
        }
    }

    private static string Text(XElement element) => Regex.Replace(string.Concat(element.Nodes().Select(node => node switch
    {
        XText text => text.Value,
        XElement { Name.LocalName: "see" or "seealso" } reference => ((string?)reference.Attribute("cref"))?.Split(':').Last() ?? (string?)reference.Attribute("langword") ?? reference.Value,
        XElement { Name.LocalName: "paramref" or "typeparamref" } reference => (string?)reference.Attribute("name") ?? "",
        XElement other => Text(other),
        _ => "",
    })), @"\s+", " ").Trim();

    /// <param name="ActiveParameter">The index of the parameter the argument at the position goes to.</param>
    /// <param name="Chosen">Whether the arguments so far pick this overload.</param>
    public sealed record Signature(string Display, string? Summary, IReadOnlyList<Parameter> Parameters, int? ActiveParameter, bool Chosen);

    public sealed record Parameter(string Name, string Type, string? Documentation, bool IsOptional);
}
