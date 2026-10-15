using System.Collections.Concurrent;
using System.IO.Enumeration;
using Buildalyzer;
using Buildalyzer.Environment;
using Buildalyzer.IO;
using Buildalyzer.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;

namespace Roslynaut;

/// <summary>A Roslyn solution plus the MSBuild design-time build results it was created from.</summary>
public sealed record LoadedWorkspace(Solution Solution, IReadOnlyList<IAnalyzerResult> Results);

/// <summary>
/// Loads projects and solutions through Buildalyzer and keeps the resulting Roslyn workspaces in sync with the file system:
/// every call picks up changed, added and deleted C# files, and reloads after project or build file changes.
/// </summary>
public sealed class WorkspaceService
{
    private readonly ConcurrentDictionary<string, TrackedWorkspace> _workspaces = new(StringComparer.OrdinalIgnoreCase);

    public Task<LoadedWorkspace> GetAsync(string projectPath)
    {
        var path = Path.GetFullPath(projectPath);
        if (!File.Exists(path))
            throw new McpException($"Project or solution not found: {path}");

        var workspace = _workspaces.GetOrAdd(path, key => new TrackedWorkspace(key));
        return Task.Run(workspace.Sync);
    }

    public async Task<Document> GetDocumentAsync(string projectPath, string filePath) =>
        GetDocument((await GetAsync(projectPath)).Solution, filePath);

    public static Document GetDocument(Solution solution, string filePath)
    {
        var id = solution.GetDocumentIdsWithFilePath(Path.GetFullPath(filePath)).FirstOrDefault()
            ?? throw new McpException($"'{filePath}' is not part of the loaded project or solution.");
        return solution.GetDocument(id)!;
    }

    public static async Task<ISymbol> GetSymbolAtAsync(Document document, int line, int column, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken);
        if (line < 1 || line > text.Lines.Count || column < 1)
            throw new McpException($"Position {line}:{column} is outside '{document.FilePath}'.");

        var position = text.Lines[line - 1].Start + column - 1;
        return await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken)
            ?? throw new McpException($"No symbol found at {document.FilePath}:{line}:{column}.");
    }

    /// <summary>A loaded workspace plus the file timestamps it reflects.</summary>
    private sealed class TrackedWorkspace(string path)
    {
        private readonly Lock _lock = new();
        private LoadedWorkspace? _loaded;
        private string[] _roots = [];
        private Dictionary<string, DateTimeOffset> _files = [];

        // ponytail: rescans the project directories on every call; switch to a FileSystemWatcher if huge repositories make that slow.
        public LoadedWorkspace Sync()
        {
            lock (_lock)
            {
                var files = Scan(_roots);
                var changed = files.Where(file => !_files.TryGetValue(file.Key, out var time) || time != file.Value).Select(file => file.Key)
                    .Concat(_files.Keys.Where(file => !files.ContainsKey(file)))
                    .ToList();

                // ponytail: any project or build file change reloads everything; reload only the affected projects if that gets slow.
                if (_loaded is null || changed.Any(file => !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
                {
                    _loaded = Load(path);
                    _roots = GetRoots(_loaded.Solution);
                    files = Scan(_roots);
                }
                else if (changed.Count > 0)
                {
                    _loaded = _loaded with { Solution = Apply(_loaded.Solution, changed, files) };
                }

                _files = files;
                return _loaded;
            }
        }

        private Solution Apply(Solution solution, List<string> changed, Dictionary<string, DateTimeOffset> files)
        {
            foreach (var file in changed)
            {
                var ids = solution.GetDocumentIdsWithFilePath(file);
                if (!files.ContainsKey(file))
                    solution = solution.RemoveDocuments(ids);
                else if (!ids.IsEmpty)
                    solution = solution.WithDocumentText(ids, Read(file));
                // ponytail: a new file joins the deepest enclosing project, as with default globbing; explicit Compile items need a project file change.
                else if (!_files.ContainsKey(file) && FindOwner(solution, file) is { } project)
                    solution = project.AddDocument(Path.GetFileName(file), Read(file), filePath: file).Project.Solution;
            }

            return solution;
        }

        /// <summary>The directories to scan: the solution's and the projects', without nested duplicates.</summary>
        private string[] GetRoots(Solution solution)
        {
            var directories = solution.Projects.Select(project => Path.GetDirectoryName(project.FilePath))
                .Append(Path.GetDirectoryName(path))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return [.. directories.Where(directory => !directories.Any(other => IsUnder(directory, other)))];
        }

        private static Dictionary<string, DateTimeOffset> Scan(string[] roots)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            var files = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Where(Directory.Exists))
            {
                var entries = new FileSystemEnumerable<(string Path, DateTimeOffset Time)>(root, (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.LastWriteTimeUtc), options)
                {
                    ShouldRecursePredicate = (ref FileSystemEntry entry) => entry.FileName is not ("bin" or "obj" or ".git" or ".vs" or "node_modules"),
                    ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                        !entry.IsDirectory && Path.GetExtension(entry.FileName) is ".cs" or ".csproj" or ".props" or ".targets" or ".sln" or ".slnx",
                };
                foreach (var (file, time) in entries)
                    files[file] = time;
            }

            return files;
        }

        private static Project? FindOwner(Solution solution, string file) => solution.Projects
            .Where(project => project.Language == LanguageNames.CSharp && project.FilePath is not null && IsUnder(file, Path.GetDirectoryName(project.FilePath)!))
            .MaxBy(project => Path.GetDirectoryName(project.FilePath)!.Length);

        private static bool IsUnder(string path, string directory) =>
            path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static SourceText Read(string file) => SourceText.From(File.ReadAllText(file));
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
