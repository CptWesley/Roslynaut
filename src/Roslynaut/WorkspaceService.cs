using System.Collections.Concurrent;
using Buildalyzer;
using Buildalyzer.Environment;
using Buildalyzer.IO;
using Buildalyzer.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol;

namespace Roslynaut;

/// <summary>A Roslyn solution plus the MSBuild design-time build results it was created from.</summary>
public sealed record LoadedWorkspace(Solution Solution, IReadOnlyList<IAnalyzerResult> Results);

/// <summary>Loads projects and solutions through Buildalyzer and caches the resulting Roslyn workspaces.</summary>
public sealed class WorkspaceService
{
    // ponytail: cached until restart, so file edits after the first load are not seen. Add a file watcher or reload tool when that matters.
    private readonly ConcurrentDictionary<string, Lazy<Task<LoadedWorkspace>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<LoadedWorkspace> GetAsync(string projectPath)
    {
        var path = Path.GetFullPath(projectPath);
        if (!File.Exists(path))
            throw new McpException($"Project or solution not found: {path}");

        return _cache.GetOrAdd(path, key => new(() => Task.Run(() => Load(key)))).Value;
    }

    public async Task<Document> GetDocumentAsync(string projectPath, string filePath)
    {
        var solution = (await GetAsync(projectPath)).Solution;
        var id = solution.GetDocumentIdsWithFilePath(Path.GetFullPath(filePath)).FirstOrDefault()
            ?? throw new McpException($"'{filePath}' is not part of '{projectPath}'.");
        return solution.GetDocument(id)!;
    }

    public async Task<ISymbol> GetSymbolAtAsync(string projectPath, string filePath, int line, int column, CancellationToken cancellationToken)
    {
        var document = await GetDocumentAsync(projectPath, filePath);
        var text = await document.GetTextAsync(cancellationToken);
        if (line < 1 || line > text.Lines.Count || column < 1)
            throw new McpException($"Position {line}:{column} is outside '{filePath}'.");

        var position = text.Lines[line - 1].Start + column - 1;
        return await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken)
            ?? throw new McpException($"No symbol found at {filePath}:{line}:{column}.");
    }

    private static LoadedWorkspace Load(string path)
    {
        var isSolution = Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx";
        var manager = isSolution ? new AnalyzerManager(IOPath.Parse(path)) : new AnalyzerManager();

        // Buildalyzer 9 defaults to "Clean;Build", and Clean deletes the analyzed project's build output.
        // Run Compile instead; a missing input file keeps CoreCompile (and so the compiler arguments) from being skipped as up to date.
        var options = new EnvironmentOptions();
        options.TargetsToBuild.Clear();
        options.TargetsToBuild.Add("Compile");
        options.GlobalProperties["NonExistentFile"] = Path.Combine("__NonExistentSubDir__", "__NonExistentFile__");

        // Build level by level, following project references (Buildalyzer's addProjectReferences would build them with the defaults above).
        // Like Buildalyzer's own GetWorkspace: builds run in parallel and take the first target framework of each project.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<IAnalyzerResult>();
        var pending = (isSolution ? manager.Projects.Keys : [path]).Where(seen.Add).ToList();
        while (pending.Count > 0)
        {
            var built = pending.AsParallel()
                .Select(project => manager.GetProject(IOPath.Parse(project))?.Build(options).FirstOrDefault())
                .OfType<IAnalyzerResult>()
                .ToList();
            results.AddRange(built);
            pending = built.SelectMany(result => result.ProjectReferences).Select(Path.GetFullPath).Where(seen.Add).ToList();
        }

        var workspace = new AdhocWorkspace();
        foreach (var result in results)
        {
            var project = result.AddToWorkspace(workspace);
            if (project?.Language != LanguageNames.CSharp || result.CompilerArguments.Length == 0)
                continue;

            // Buildalyzer ignores NoWarn and WarningsAsErrors; take those from the real compiler command line.
            var compiler = CSharpCommandLineParser.Default.Parse(result.CompilerArguments, Path.GetDirectoryName(result.ProjectFilePath), sdkDirectory: null).CompilationOptions;
            var compilationOptions = project.CompilationOptions!
                .WithSpecificDiagnosticOptions(compiler.SpecificDiagnosticOptions)
                .WithGeneralDiagnosticOption(compiler.GeneralDiagnosticOption);
            workspace.TryApplyChanges(workspace.CurrentSolution.WithProjectCompilationOptions(project.Id, compilationOptions));
        }

        return new(workspace.CurrentSolution, results);
    }
}
