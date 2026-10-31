using System.ComponentModel;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class ThrowsTool(WorkspaceService workspace) : IMcpTool
{
    public Delegate Handler => FindThrowsAsync;

    [McpServerTool(Name = "throws", ReadOnly = true)]
    [Description("Finds the exceptions that can leave a member: those it throws itself, those that members it calls throw (followed into source "
        + "up to depth calls deep), and those that methods of referenced assemblies document with <exception>; each with where in the member "
        + "it comes from and the chain of calls to the throw. Exceptions a catch on the way handles are left out. For error handling and tests, "
        + "and what an API throws. It does not see exceptions the runtime raises, such as NullReferenceException, nor follow calls through "
        + "interfaces or virtual members to their implementations.")]
    private async Task<IReadOnlyList<ThrownException>> FindThrowsAsync(
        [Description(ToolDefaults.ProjectPath)] string projectPath,
        [Description(ToolDefaults.Target)] string? symbol = null,
        [Description(ToolDefaults.TargetFilePath)] string? filePath = null,
        [Description(ToolDefaults.Line)] int? line = null,
        [Description(ToolDefaults.Column)] int? column = null,
        [Description("How many calls deep to follow members declared in source.")] int depth = 2,
        CancellationToken cancellationToken = default)
    {
        var (solution, symbols) = await workspace.ResolveAsync(projectPath, symbol, filePath, line, column, cancellationToken);
        return await FindAsync(solution, symbols[0], depth, cancellationToken);
    }

    internal static async Task<IReadOnlyList<ThrownException>> FindAsync(Solution solution, ISymbol member, int depth, CancellationToken cancellationToken)
    {
        var escaping = await EscapingAsync(solution, member, depth, new HashSet<ISymbol>(SymbolEqualityComparer.Default), cancellationToken);
        return [.. escaping
            .Select(thrown => new ThrownException(thrown.Type.ToDisplayString(), SourceLocation.From(thrown.Site), thrown.Chain, thrown.Documented))
            .DistinctBy(thrown => (thrown.Type, thrown.Site.Line, thrown.Site.Column, string.Join('>', thrown.Chain)))];
    }

    private sealed record Escape(ITypeSymbol Type, Location Site, IReadOnlyList<string> Chain, bool Documented);

    /// <summary>The exceptions that leave a member: its throws and its callees', less those its catches handle.</summary>
    private static async Task<List<Escape>> EscapingAsync(Solution solution, ISymbol member, int depth, HashSet<ISymbol> visiting, CancellationToken cancellationToken)
    {
        var escaping = new List<Escape>();
        if (!visiting.Add(member))
            return escaping;

        foreach (var reference in SymbolTree.DeclarationsOf(member))
        {
            var node = await reference.GetSyntaxAsync(cancellationToken);
            if (solution.GetDocument(node.SyntaxTree) is not { } document || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
                continue;

            foreach (var operation in CalleesTool.CodeOf(node).SelectMany(code => CalleesTool.OperationsOf(model, code, cancellationToken)).SelectMany(operation => operation.DescendantsAndSelf()))
            {
                var found = new List<Escape>();
                if (operation is IThrowOperation thrown && TypeThrown(thrown) is { } type)
                {
                    found.Add(new(type, operation.Syntax.GetLocation(), [], false));
                }
                else if (CalleeOf(operation) is { } callee)
                {
                    var name = SymbolEntry.PathOf(callee.ContainingSymbol) + "." + callee.Name;
                    if (callee.Locations.Any(location => location.IsInSource))
                    {
                        if (depth > 0)
                            found.AddRange((await EscapingAsync(solution, callee.OriginalDefinition, depth - 1, visiting, cancellationToken))
                                .Select(inner => inner with { Site = operation.Syntax.GetLocation(), Chain = [name, .. inner.Chain] }));
                    }
                    else
                    {
                        found.AddRange(Documented(callee, model.Compilation).Select(type => new Escape(type, operation.Syntax.GetLocation(), [name], true)));
                    }
                }

                escaping.AddRange(found.Where(escape => !IsHandled(operation, escape.Type, model.Compilation)));
            }
        }

        visiting.Remove(member);
        return escaping;
    }

    /// <summary>The type a throw throws: of its operand, before the conversion to Exception; for a rethrow, what its catch caught.</summary>
    private static ITypeSymbol? TypeThrown(IThrowOperation thrown)
    {
        switch (thrown.Exception)
        {
            case IConversionOperation { Operand.Type: { } type }:
                return type;
            case { Type: { } type }:
                return type;
            case null:
                for (var parent = thrown.Parent; parent is not null; parent = parent.Parent)
                {
                    if (parent is ICatchClauseOperation @catch)
                        return @catch.ExceptionType;
                }

                return null;
            default:
                return null;
        }
    }

    private static ISymbol? CalleeOf(IOperation operation) => operation switch
    {
        IInvocationOperation invocation => invocation.TargetMethod,
        IObjectCreationOperation { Constructor: { } constructor } => constructor,
        IPropertyReferenceOperation property => property.Property,
        _ => null,
    };

    /// <summary>The exceptions a member's documentation says it throws, &lt;exception cref="T:System.ArgumentNullException"&gt;.</summary>
    private static IEnumerable<ITypeSymbol> Documented(ISymbol callee, Compilation compilation)
    {
        XElement documentation;
        try
        {
            documentation = XElement.Parse($"<doc>{callee.OriginalDefinition.GetDocumentationCommentXml()}</doc>");
        }
        catch (System.Xml.XmlException)
        {
            yield break;
        }

        foreach (var cref in documentation.Descendants("exception").Select(exception => (string?)exception.Attribute("cref")).OfType<string>())
        {
            if (DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, compilation) is ITypeSymbol type)
                yield return type;
        }
    }

    /// <summary>
    /// Whether a try around the operation, between it and the member, has a catch without a filter that takes the type: that catch ends it.
    /// What a catch or finally itself throws goes on to the try around that.
    /// </summary>
    private static bool IsHandled(IOperation operation, ITypeSymbol type, Compilation compilation)
    {
        for (IOperation? inner = operation, outer = operation.Parent; outer is not null; inner = outer, outer = outer.Parent)
        {
            if (outer is ITryOperation @try && inner == @try.Body
                && @try.Catches.Any(@catch => @catch.Filter is null && compilation.ClassifyCommonConversion(type, @catch.ExceptionType).IsImplicit))
                return true;
        }

        return false;
    }

    /// <param name="Site">Where in the member the exception comes from: the throw, or the call that leads to it.</param>
    /// <param name="Chain">The members called on the way to where it is thrown, outermost first; empty for a throw in the member itself.</param>
    /// <param name="Documented">Whether a method of a referenced assembly documents it, rather than code that throws it.</param>
    public sealed record ThrownException(string Type, SourceLocation Site, IReadOnlyList<string> Chain, bool Documented);
}
