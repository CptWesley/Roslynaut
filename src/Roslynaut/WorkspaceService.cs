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
/// file watchers queue changed, added and deleted C# files for the next call, and project or build file changes trigger a reload.
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

    /// <summary>A loaded workspace plus the file watchers that queue changes for it.</summary>
    private sealed class TrackedWorkspace(string path)
    {
        private readonly Lock _lock = new();
        private readonly ConcurrentQueue<(string Path, bool Created)> _changes = new();
        private FileSystemWatcher[]? _watchers;
        private LoadedWorkspace? _loaded;
        private int _overflowed;

        /// <summary>Applies the changes queued since the previous call, loading or reloading the workspace when needed.</summary>
        public LoadedWorkspace Sync()
        {
            lock (_lock)
            {
                if (_loaded is null || Interlocked.Exchange(ref _overflowed, 0) == 1)
                    return Reload();

                var changes = TakeChanges(_loaded.Solution);

                // ponytail: any project or build file change reloads everything; reload only the affected projects if that gets slow.
                if (changes.Keys.Any(IsBuildFile))
                    return Reload();

                if (changes.Count == 0)
                    return _loaded;

                try
                {
                    return _loaded = _loaded with { Solution = Apply(_loaded.Solution, changes) };
                }
                catch
                {
                    // Typically a file still locked by its writer: keep the changes for the next call.
                    foreach (var (file, created) in changes)
                        _changes.Enqueue((file, created));
                    throw;
                }
            }
        }

        private LoadedWorkspace Reload()
        {
            // On the first load, already watch the project or solution directory so edits made while loading are not lost.
            _watchers ??= [Watch(Path.GetDirectoryName(path)!)];
            _changes.Clear();
            _overflowed = 0;

            var loaded = Load(path);
            var previous = _watchers;
            _watchers = [.. GetRoots(loaded.Solution).Select(Watch)];
            foreach (var watcher in previous)
                watcher.Dispose();

            return _loaded = loaded;
        }

        private FileSystemWatcher Watch(string root)
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            watcher.Created += (_, e) => Enqueue(root, e.FullPath, created: true);
            watcher.Changed += (_, e) => Enqueue(root, e.FullPath, created: false);
            watcher.Deleted += (_, e) => Enqueue(root, e.FullPath, created: false);
            watcher.Renamed += (_, e) =>
            {
                Enqueue(root, e.OldFullPath, created: false);
                Enqueue(root, e.FullPath, created: true);
            };

            // ponytail: a buffer overflow loses events, so everything is reloaded.
            watcher.Error += (_, _) => Interlocked.Exchange(ref _overflowed, 1);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }

        private void Enqueue(string root, string file, bool created)
        {
            var ignored = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar).Any(segment => IsSkipped(segment))
                || (!IsTracked(file) && File.Exists(file));
            if (!ignored)
                _changes.Enqueue((file, created));
        }

        /// <summary>Drains the queue into tracked files and whether each was created.</summary>
        private Dictionary<string, bool> TakeChanges(Solution solution)
        {
            var changes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            void Mark(string file, bool created) => changes[file] = created || changes.GetValueOrDefault(file);

            while (_changes.TryDequeue(out var change))
            {
                if (Directory.Exists(change.Path))
                {
                    // A directory that was created or moved in brings its files without events of their own.
                    if (change.Created)
                        foreach (var file in Enumerate(change.Path))
                            Mark(file, created: true);
                }
                else if (IsTracked(change.Path))
                {
                    Mark(change.Path, change.Created);
                }
                else
                {
                    // A directory that was deleted or moved out only reports itself.
                    var known = solution.Projects.SelectMany(project => project.Documents.Select(document => document.FilePath).Append(project.FilePath));
                    foreach (var file in known.OfType<string>().Where(file => IsUnder(file, change.Path)))
                        Mark(file, created: false);
                }
            }

            return changes;
        }

        private static Solution Apply(Solution solution, Dictionary<string, bool> changes)
        {
            foreach (var (file, created) in changes)
            {
                var ids = solution.GetDocumentIdsWithFilePath(file);
                if (!File.Exists(file))
                    solution = solution.RemoveDocuments(ids);
                else if (!ids.IsEmpty)
                    solution = solution.WithDocumentText(ids, Read(file));
                // ponytail: a new file joins the deepest enclosing project, as with default globbing; explicit Compile items need a project file change.
                else if (created && FindOwner(solution, file) is { } project)
                    solution = project.AddDocument(Path.GetFileName(file), Read(file), filePath: file).Project.Solution;
            }

            // Apply to the workspace too, so it does not keep the previous snapshot alive.
            solution.Workspace.TryApplyChanges(solution);
            return solution.Workspace.CurrentSolution;
        }

        /// <summary>The directories to watch: the solution's and the projects', without nested duplicates.</summary>
        private string[] GetRoots(Solution solution)
        {
            var directories = solution.Projects.Select(project => Path.GetDirectoryName(project.FilePath))
                .Append(Path.GetDirectoryName(path))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return [.. directories.Where(directory => !directories.Any(other => IsUnder(directory, other)))];
        }

        private static IEnumerable<string> Enumerate(string directory) =>
            new FileSystemEnumerable<string>(directory, (ref FileSystemEntry entry) => entry.ToFullPath(), new() { RecurseSubdirectories = true, IgnoreInaccessible = true })
            {
                ShouldRecursePredicate = (ref FileSystemEntry entry) => !IsSkipped(entry.FileName),
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && IsTracked(entry.ToFullPath()),
            };

        private static bool IsSkipped(ReadOnlySpan<char> directory) => directory is "bin" or "obj" or ".git" or ".vs" or "node_modules";

        private static bool IsTracked(string file) => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || IsBuildFile(file);

        private static bool IsBuildFile(string file) => Path.GetExtension(file).ToLowerInvariant() is ".csproj" or ".props" or ".targets" or ".sln" or ".slnx";

        private static Project? FindOwner(Solution solution, string file) => solution.Projects
            .Where(project => project.Language == LanguageNames.CSharp && project.FilePath is not null && IsUnder(file, Path.GetDirectoryName(project.FilePath)!))
            .MaxBy(project => Path.GetDirectoryName(project.FilePath)!.Length);

        private static bool IsUnder(string path, string directory) =>
            path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        // From a stream, so the text gets an encoding: compilations cannot emit debug information without one.
        private static SourceText Read(string file)
        {
            using var stream = File.OpenRead(file);
            return SourceText.From(stream);
        }
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
