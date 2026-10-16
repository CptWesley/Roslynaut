using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Text;
using Buildalyzer;
using Buildalyzer.Environment;
using Buildalyzer.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using ProjectInfo = Microsoft.CodeAnalysis.ProjectInfo;
using ModelContextProtocol;
using Roslynaut.Tools;

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

    /// <summary>
    /// Writes the documents that differ between two snapshots to disk, from where the file watchers bring them into the workspace.
    /// A renamed document moves its file. Returns the paths written or deleted.
    /// </summary>
    public static async Task<IReadOnlyList<string>> WriteChangesAsync(Solution before, Solution after, CancellationToken cancellationToken)
    {
        var written = new List<string>();
        foreach (var project in after.GetChanges(before).GetProjectChanges())
        {
            foreach (var id in project.GetChangedDocuments().Concat(project.GetAddedDocuments()))
            {
                var (original, document) = (before.GetDocument(id), after.GetDocument(id)!);
                var path = original?.FilePath is { } oldPath
                    ? Path.Combine(Path.GetDirectoryName(oldPath)!, document.Name)
                    : document.FilePath ?? (project.NewProject.FilePath is { } projectPath ? Path.Combine([Path.GetDirectoryName(projectPath)!, .. document.Folders, document.Name]) : null);
                if (path is null)
                    continue;

                var text = await document.GetTextAsync(cancellationToken);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, text.ToString(), EncodingOf(original?.FilePath ?? path), cancellationToken);
                if (original?.FilePath is { } moved && !moved.Equals(path, StringComparison.OrdinalIgnoreCase))
                    File.Delete(moved);
                written.Add(path);
            }

            foreach (var id in project.GetRemovedDocuments())
            {
                if (before.GetDocument(id)?.FilePath is { } removed && File.Exists(removed))
                {
                    File.Delete(removed);
                    written.Add(removed);
                }
            }
        }

        return written;
    }

    /// <summary>
    /// The encoding of a file by its byte order mark, so rewriting it keeps that; UTF-8 without one otherwise.
    /// Not the encoding of the document's text, which documents that tools add have none of.
    /// </summary>
    private static Encoding EncodingOf(string path)
    {
        var bom = new byte[3];
        var length = 0;
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            length = stream.Read(bom);
        }

        return bom.AsSpan(0, length) switch
        {
            [0xEF, 0xBB, 0xBF] => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            [0xFF, 0xFE, ..] => Encoding.Unicode,
            [0xFE, 0xFF, ..] => Encoding.BigEndianUnicode,
            _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
    }

    /// <summary>The symbols a symbol path names, or else the symbol at a position, with the solution snapshot they come from.</summary>
    public async Task<(Solution Solution, IReadOnlyList<ISymbol> Symbols)> ResolveAsync(
        string projectPath, string? symbol, string? filePath, int? line, int? column, CancellationToken cancellationToken)
    {
        if (symbol is not null)
        {
            var solution = (await GetAsync(projectPath)).Solution;
            return (solution, await SymbolTree.ResolveAsync(solution, symbol, cancellationToken));
        }

        if (filePath is null || line is null || column is null)
            throw new McpException("Pass a symbol path, or a filePath with a line and column.");

        var document = await GetDocumentAsync(projectPath, filePath);
        return (document.Project.Solution, [await GetSymbolAtAsync(document, line.Value, column.Value, cancellationToken)]);
    }

    public static async Task<ISymbol> GetSymbolAtAsync(Document document, int line, int column, CancellationToken cancellationToken)
    {
        var position = PositionOf(await document.GetTextAsync(cancellationToken), line, column, document.FilePath);
        return await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken)
            ?? throw new McpException($"No symbol found at {document.FilePath}:{line}:{column}.");
    }

    /// <summary>The offset of a 1-based line and column in a text.</summary>
    public static int PositionOf(SourceText text, int line, int column, string? filePath)
    {
        if (line < 1 || line > text.Lines.Count || column < 1 || column > text.Lines[line - 1].SpanIncludingLineBreak.Length + 1)
            throw new McpException($"Position {line}:{column} is outside '{filePath}'.");

        return text.Lines[line - 1].Start + column - 1;
    }

    /// <summary>The span from the start of one 1-based line to the end of another.</summary>
    public static TextSpan LinesOf(SourceText text, int startLine, int endLine, string? filePath)
    {
        if (startLine < 1 || endLine < startLine || endLine > text.Lines.Count)
            throw new McpException($"Lines {startLine} to {endLine} are outside '{filePath}'.");

        return TextSpan.FromBounds(text.Lines[startLine - 1].Start, text.Lines[endLine - 1].End);
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
                    solution = project.AddDocument(Path.GetFileName(file), Read(file), FoldersOf(project.FilePath!, file), file).Project.Solution;
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

        // Analyzer configuration counts as well: it feeds the diagnostic severities of the compiler arguments.
        private static bool IsBuildFile(string file) =>
            Path.GetExtension(file).ToLowerInvariant() is ".csproj" or ".props" or ".targets" or ".sln" or ".slnx" or ".editorconfig" or ".globalconfig";

        private static Project? FindOwner(Solution solution, string file) => solution.Projects
            .Where(project => project.Language == LanguageNames.CSharp && project.FilePath is not null && IsUnder(file, Path.GetDirectoryName(project.FilePath)!))
            .MaxBy(project => Path.GetDirectoryName(project.FilePath)!.Length);

        private static bool IsUnder(string path, string directory) =>
            path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // From a stream, so the text gets an encoding: compilations cannot emit debug information without one.
    private static SourceText Read(string file)
    {
        using var stream = File.OpenRead(file);
        return SourceText.From(stream);
    }

    /// <summary>The folders of a file below its project's directory, such as ["Models", "Orders"]; none for build output in bin and obj.</summary>
    private static string[] FoldersOf(string projectPath, string? filePath)
    {
        var relative = filePath is null ? ".." : Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, Path.GetDirectoryName(filePath)!);
        string[] folders = relative == "." ? [] : relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return folders is [] or [".." or "bin" or "obj", ..] || Path.IsPathRooted(relative) ? [] : folders;
    }

    /// <summary>
    /// A reference to an assembly, with the documentation comments of the XML file next to it, as NuGet packages and reference packs ship them;
    /// without those, referenced symbols are undocumented.
    /// </summary>
    internal static MetadataReference ReferenceTo(string path, MetadataReferenceProperties properties = default) =>
        MetadataReference.CreateFromFile(path, properties, Path.ChangeExtension(path, ".xml") is var xml && File.Exists(xml) ? XmlDocumentationProvider.CreateFromFile(xml) : null);

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
                .Select(project => manager.GetProject(IOPath.Parse(project)) is { } analyzer ? Build(analyzer, options) : null)
                .OfType<IAnalyzerResult>()
                .ToList();
            results.AddRange(built);
            pending = built.SelectMany(result => result.ProjectReferences).Select(Path.GetFullPath).Where(seen.Add).ToList();
        }

        var projects = results.Where(result => LanguageOf(result.ProjectFilePath) is not null)
            .Select(result => (Result: result, Id: ProjectId.CreateNewId(result.ProjectFilePath)))
            .ToList();

        // The compiler gets a referenced project's reference assembly, or the assembly itself when it has none.
        var outputs = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
        foreach (var (result, id) in projects)
        {
            foreach (var output in new[] { result.GetProperty("TargetRefPath"), result.GetProperty("TargetPath") }.Where(output => !string.IsNullOrEmpty(output)))
                outputs.TryAdd(output, id);
        }

        var workspace = new SettableWorkspace();
        var loader = workspace.Services.GetRequiredService<IAnalyzerService>().GetLoader();
        workspace.Set(projects.Aggregate(workspace.CurrentSolution, (solution, project) => solution.AddProject(ProjectOf(project.Result, project.Id, outputs, loader))));
        return new(workspace.CurrentSolution, results);
    }

    private static string? LanguageOf(string projectPath) => Path.GetExtension(projectPath).ToLowerInvariant() switch
    {
        ".csproj" => LanguageNames.CSharp,
        ".vbproj" => LanguageNames.VisualBasic,
        _ => null,
    };

    /// <summary>
    /// A project as the compiler sees it, from the compiler command line of its build; empty if the build did not get that far.
    /// Not Buildalyzer.Workspaces' AddToWorkspace, which keeps few compiler options (not even AllowUnsafeBlocks or NoWarn), no analyzer
    /// configuration, folders or default namespace, reads files as UTF-16, and references projects the compiler does not get, such as
    /// source generators (ReferenceOutputAssembly="false").
    /// </summary>
    /// <param name="outputs">The projects by the paths of their outputs, which their references to each other point to.</param>
    private static ProjectInfo ProjectOf(IAnalyzerResult result, ProjectId id, Dictionary<string, ProjectId> outputs, IAnalyzerAssemblyLoader loader)
    {
        var (name, language) = (Path.GetFileNameWithoutExtension(result.ProjectFilePath), LanguageOf(result.ProjectFilePath)!);
        var targetPath = result.GetProperty("TargetPath") is { Length: > 0 } target ? target : null;
        var project = ProjectInfo.Create(id, VersionStamp.Create(), name, name, language, result.ProjectFilePath, targetPath);
        if (result.CompilerArguments.Length == 0)
            return project;

        var directory = Path.GetDirectoryName(result.ProjectFilePath)!;
        CommandLineParser parser = language == LanguageNames.CSharp ? CSharpCommandLineParser.Default : VisualBasicCommandLineParser.Default;
        var arguments = parser.Parse(result.CompilerArguments, directory, sdkDirectory: null, additionalReferenceDirectories: null);
        IEnumerable<DocumentInfo> Documents(IEnumerable<string> files) => files.Select(file => Path.GetFullPath(file, directory)).Where(File.Exists)
            .Select(file => DocumentInfo.Create(DocumentId.CreateNewId(id, file), Path.GetFileName(file), FoldersOf(result.ProjectFilePath, file),
                loader: TextLoader.From(TextAndVersion.Create(Read(file), VersionStamp.Create(), file)), filePath: file));
        var references = arguments.MetadataReferences.Select(reference => (Path: Path.GetFullPath(reference.Reference, directory), reference.Properties)).ToList();

        return project
            .WithAssemblyName(arguments.CompilationName ?? name)
            .WithCompilationOptions(arguments.CompilationOptions.WithStrongNameProvider(new DesktopStrongNameProvider(arguments.KeyFileSearchPaths)))
            .WithParseOptions(arguments.ParseOptions)
            .WithDocuments(Documents(arguments.SourceFiles.Select(file => file.Path)))
            .WithAdditionalDocuments(Documents(arguments.AdditionalFiles.Select(file => file.Path)))
            .WithAnalyzerConfigDocuments(Documents(arguments.AnalyzerConfigPaths))
            .WithProjectReferences(references.Where(reference => outputs.ContainsKey(reference.Path))
                .Select(reference => new ProjectReference(outputs[reference.Path], reference.Properties.Aliases, reference.Properties.EmbedInteropTypes)))
            .WithMetadataReferences(references.Where(reference => !outputs.ContainsKey(reference.Path) && File.Exists(reference.Path))
                .Select(reference => ReferenceTo(reference.Path, reference.Properties)))
            .WithAnalyzerReferences(arguments.AnalyzerReferences.Select(reference => Path.GetFullPath(reference.FilePath, directory)).Where(File.Exists)
                .Select(file => new AnalyzerFileReference(file, loader)))
            .WithDefaultNamespace(result.GetProperty("RootNamespace"));
    }

    /// <summary>
    /// Builds a project for its first target framework. Buildalyzer builds without one, and then a project with TargetFrameworks (even just one)
    /// builds as the outer build, which only dispatches to a build per framework and has no Compile target. The frameworks can come from an
    /// import, which Buildalyzer's project file reader misses, so take them from that first build.
    /// </summary>
    private static IAnalyzerResult? Build(IProjectAnalyzer analyzer, EnvironmentOptions options)
    {
        var result = analyzer.Build(options).FirstOrDefault();
        return result is { CompilerArguments.Length: 0 }
            && result.GetProperty("TargetFrameworks")?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is [var framework, ..]
            ? analyzer.Build(framework, options).FirstOrDefault()
            : result;
    }
}

/// <summary>
/// Like <see cref="AdhocWorkspace"/>, which is sealed, an in-memory workspace that takes any change; and also changes TryApplyChanges
/// ignores, such as a project's default namespace.
/// </summary>
internal sealed class SettableWorkspace() : Workspace(MefHostServices.DefaultHost, "Custom")
{
    public override bool CanApplyChange(ApplyChangesKind feature) => true;

    public void Set(Solution solution) => SetCurrentSolution(solution);
}
