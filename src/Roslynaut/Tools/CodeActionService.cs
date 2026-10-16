using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;

namespace Roslynaut.Tools;

/// <param name="Id">Identifies the action for apply_code_action, for as long as the code does not change.</param>
/// <param name="Kind">Fix, for a diagnostic, or Refactoring.</param>
public sealed record CodeActionEntry(string Id, string Title, string Kind, string? DiagnosticId);

/// <summary>
/// Finds Roslyn's code fixes and refactorings, the IDE's light bulb, and remembers them per solution snapshot, so they can be applied by id.
/// Providers come from Roslyn's own features and from the project's analyzer packages.
/// </summary>
public sealed class CodeActionService
{
    private static readonly Assembly[] Features = [Assembly.Load("Microsoft.CodeAnalysis.Features"), Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features")];

    private readonly ConditionalWeakTable<Solution, ConcurrentDictionary<string, CodeAction>> _actions = new();
    private readonly ConcurrentDictionary<Assembly, Providers> _providers = new();
    private int _lastId;

    public async Task<List<CodeActionEntry>> FindAsync(Document document, TextSpan span, CancellationToken cancellationToken)
    {
        var providers = ProvidersOf(document.Project).ToList();

        var found = new List<(CodeAction Action, string? DiagnosticId)>();
        var diagnostics = await DiagnosticsTool.GetAsync(document.Project, document, analyzers: true, cancellationToken);
        foreach (var diagnostic in diagnostics.Where(diagnostic => diagnostic.Location.SourceSpan.IntersectsWith(span)))
        {
            foreach (var fixer in providers.SelectMany(provider => provider.Fixers).Where(fixer => fixer.FixableDiagnosticIds.Contains(diagnostic.Id)))
                await Try(fixer.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (action, _) => found.Add((action, diagnostic.Id)), cancellationToken)));
        }

        foreach (var refactoring in providers.SelectMany(provider => provider.Refactorings))
            await Try(refactoring.ComputeRefactoringsAsync(new CodeRefactoringContext(document, span, action => found.Add((action, null)), cancellationToken)));

        var actions = _actions.GetOrCreateValue(document.Project.Solution);
        return
        [
            .. found.SelectMany(entry => Flatten(entry.Action, "").Select(action => (action.Action, action.Title, entry.DiagnosticId))).Select(entry =>
            {
                var id = Interlocked.Increment(ref _lastId).ToString();
                actions[id] = entry.Action;
                return new CodeActionEntry(id, entry.Title, entry.DiagnosticId is null ? "Refactoring" : "Fix", entry.DiagnosticId);
            }),
        ];

        // A provider written for Visual Studio can need services this workspace does not have; its actions are then left out.
        async Task Try(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>The solution with the changes of a code action that <see cref="FindAsync"/> found for this same snapshot.</summary>
    public async Task<(CodeAction Action, Solution Changed)> ApplyAsync(Solution solution, string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(id) || !_actions.TryGetValue(solution, out var actions) || !actions.TryGetValue(id, out var action))
            throw new McpException($"No code action {id} for the current code: the code changed since code_actions found it, or the id is wrong. Ask code_actions again.");

        var operations = await action.GetOperationsAsync(cancellationToken);
        var changed = operations.OfType<ApplyChangesOperation>().LastOrDefault()?.ChangedSolution;
        return (action, changed ?? throw new McpException($"'{action.Title}' does not change any code."));
    }

    /// <summary>
    /// The solution with every diagnostic of an id fixed at once, in a document or in the whole solution, by the first provider that can.
    /// </summary>
    /// <param name="title">Which of the fixes for the diagnostic to apply, when there are several; the first otherwise.</param>
    public async Task<(string Title, Solution Changed)> FixAllAsync(
        Solution solution, Document? document, string diagnosticId, string? title, CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var project in document is null ? solution.Projects : [document.Project])
            diagnostics.AddRange((await DiagnosticsTool.GetAsync(project, document, analyzers: true, cancellationToken)).Where(diagnostic => diagnostic.Id == diagnosticId));

        var first = diagnostics.FirstOrDefault(diagnostic => diagnostic.Location.IsInSource)
            ?? throw new McpException($"There are no {diagnosticId} diagnostics to fix.");
        var firstDocument = solution.GetDocument(first.Location.SourceTree)!;
        foreach (var fixer in ProvidersOf(firstDocument.Project).SelectMany(provider => provider.Fixers).Where(fixer => fixer.FixableDiagnosticIds.Contains(diagnosticId)))
        {
            if (fixer.GetFixAllProvider() is not { } fixAll)
                continue;

            var actions = new List<CodeAction>();
            try
            {
                await fixer.RegisterCodeFixesAsync(new CodeFixContext(firstDocument, first, (action, _) => actions.Add(action), cancellationToken));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            if (actions.SelectMany(action => Flatten(action, "")).FirstOrDefault(action => title is null || action.Title == title) is not ({ } chosen, var chosenTitle))
                continue;

            var scope = document is null ? FixAllScope.Solution : FixAllScope.Document;
            var context = new FixAllContext(firstDocument, fixer, scope, chosen.EquivalenceKey, [diagnosticId], new KnownDiagnostics(diagnostics), cancellationToken);
            if (await fixAll.GetFixAsync(context) is not { } fix)
                continue;

            var operations = await fix.GetOperationsAsync(cancellationToken);
            if (operations.OfType<ApplyChangesOperation>().LastOrDefault()?.ChangedSolution is { } changed)
                return (chosenTitle, changed);
        }

        throw new McpException($"No fix can fix all {diagnosticId} diagnostics{(title is null ? "" : $" as '{title}'")}.");
    }

    private IEnumerable<Providers> ProvidersOf(Project project) =>
        Features.Concat(project.AnalyzerReferences.OfType<AnalyzerFileReference>().Select(reference => reference.GetAssembly()))
            .Distinct()
            .Select(assembly => _providers.GetOrAdd(assembly, Discover));

    /// <summary>Hands a fix-all provider the diagnostics found already.</summary>
    private sealed class KnownDiagnostics(IReadOnlyList<Diagnostic> diagnostics) : FixAllContext.DiagnosticProvider
    {
        public override async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken)
        {
            var tree = await document.GetSyntaxTreeAsync(cancellationToken);
            return diagnostics.Where(diagnostic => diagnostic.Location.SourceTree == tree);
        }

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult(Enumerable.Empty<Diagnostic>());

        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult(diagnostics.Where(diagnostic => diagnostic.Location.SourceTree is { } tree && project.GetDocument(tree) is not null));
    }

    /// <summary>Actions that group others, such as "Suppress or configure issues", stand for their nested actions.</summary>
    private static IEnumerable<(CodeAction Action, string Title)> Flatten(CodeAction action, string prefix) =>
        action.NestedActions.IsEmpty
            ? [(action, prefix + action.Title)]
            : action.NestedActions.SelectMany(nested => Flatten(nested, $"{prefix}{action.Title}: "));

    /// <summary>Creates the C# code fix and refactoring providers of an assembly, as dotnet format does: by their parameterless constructors.</summary>
    private static Providers Discover(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = [.. e.Types.OfType<Type>()];
        }

        return new(
            [.. Create<CodeFixProvider>(types.Where(type => type.GetCustomAttribute<ExportCodeFixProviderAttribute>()?.Languages.Contains(LanguageNames.CSharp) == true))],
            [.. Create<CodeRefactoringProvider>(types.Where(type => type.GetCustomAttribute<ExportCodeRefactoringProviderAttribute>()?.Languages.Contains(LanguageNames.CSharp) == true))]);

        static IEnumerable<T> Create<T>(IEnumerable<Type> types) where T : class
        {
            foreach (var type in types.Where(type => !type.IsAbstract && !type.ContainsGenericParameters && typeof(T).IsAssignableFrom(type)))
            {
                T? provider = null;
                try
                {
                    provider = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes)?.Invoke(null) as T;
                }
                catch (TargetInvocationException)
                {
                }

                if (provider is not null)
                    yield return provider;
            }
        }
    }

    private sealed record Providers(ImmutableArray<CodeFixProvider> Fixers, ImmutableArray<CodeRefactoringProvider> Refactorings);
}
