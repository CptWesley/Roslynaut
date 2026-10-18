using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Reflection;
using System.Text;
using Buildalyzer;
using Buildalyzer.Environment;
using Buildalyzer.IO;
using Microsoft.Build.Framework;
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

/// <summary>How to load a project or solution.</summary>
/// <param name="TargetFramework">The framework to load projects with several for, such as net48; those without it load their first.</param>
/// <param name="Configuration">The build configuration, such as Release; by default the projects' own, usually Debug.</param>
public sealed record LoadSettings(string? TargetFramework = null, string? Configuration = null);

/// <summary>A Roslyn solution plus the MSBuild design-time build results it was created from.</summary>
/// <param name="LoadErrors">
/// The MSBuild errors of the projects whose design-time build gave no compiler command line, by project path. Such a project has no sources,
/// so without these errors it just looks empty.
/// </param>
/// <param name="Warnings">
/// What the compiler warns of where this server's compiler sees a project otherwise than the SDK's does, by project path: options of its command
/// line that it does not know, such as a newer language version, and analyzers or generators that do not load, which add theirs as they load.
/// </param>
public sealed record LoadedWorkspace(
    Solution Solution, IReadOnlyList<IAnalyzerResult> Results, IReadOnlyDictionary<string, IReadOnlyList<string>> LoadErrors,
    IReadOnlyDictionary<string, ConcurrentQueue<Diagnostic>> Warnings)
{
    /// <summary>A sentence on the projects that did not load, to add to errors they may explain; empty if all loaded.</summary>
    public string LoadErrorNote => LoadErrors.Count == 0 ? "" :
        $" These projects did not load: {string.Join("; ", LoadErrors.Select(project => $"{project.Key}: {string.Join(" ", project.Value)}"))}";
}

/// <summary>
/// Loads projects and solutions through Buildalyzer and keeps the resulting Roslyn workspaces in sync with the file system:
/// file watchers queue changed, added and deleted C# files for the next call, and project or build file changes trigger a reload.
/// </summary>
public sealed class WorkspaceService
{
    private readonly ConcurrentDictionary<string, TrackedWorkspace> _workspaces = new(StringComparer.OrdinalIgnoreCase);

    public Task<LoadedWorkspace> GetAsync(string projectPath)
    {
        var workspace = _workspaces.GetOrAdd(ProjectOrSolution(projectPath), key => new TrackedWorkspace(key));
        return Task.Run(workspace.Sync);
    }

    /// <summary>Reloads a project or solution with other settings, which later calls with its path then get.</summary>
    public Task<LoadedWorkspace> ConfigureAsync(string projectPath, LoadSettings settings)
    {
        var path = ProjectOrSolution(projectPath);
        _workspaces.GetOrAdd(path, key => new TrackedWorkspace(key)).Configure(settings);
        return GetAsync(path);
    }

    // Anything else, such as a source file or a solution filter, fails to load with no more than an XML error.
    private static string ProjectOrSolution(string projectPath)
    {
        var path = Path.GetFullPath(projectPath);
        if (!File.Exists(path))
            throw new McpException(Directory.Exists(path) ? $"{path} is a directory; pass the .csproj, .sln or .slnx file in it." : $"Project or solution not found: {path}");
        return LanguageOf(path) is not null || Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx"
            ? path
            : throw new McpException($"{path} is not a project or solution; pass a .csproj, .sln or .slnx file.");
    }

    public async Task<Document> GetDocumentAsync(string projectPath, string filePath, CancellationToken cancellationToken = default)
    {
        var loaded = await GetAsync(projectPath);
        try
        {
            return await GetDocumentAsync(loaded.Solution, filePath, cancellationToken);
        }
        catch (McpException e) when (loaded.LoadErrors.Count > 0)
        {
            throw new McpException(e.Message + loaded.LoadErrorNote);
        }
    }

    /// <summary>A document of the solution by its path; or a source generator's, by the path that locations in it give, which is not on disk.</summary>
    public static async Task<Document> GetDocumentAsync(Solution solution, string filePath, CancellationToken cancellationToken)
    {
        if (solution.GetDocumentIdsWithFilePath(Path.GetFullPath(filePath)).FirstOrDefault() is { } id)
            return solution.GetDocument(id)!;

        // ponytail: the paths are relative, so one generator's file in several projects has one path, and the first project's wins; give each
        // project a GeneratedFilesOutputDirectory of its own if that matters.
        foreach (var project in solution.Projects)
        {
            foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(cancellationToken))
            {
                if (generated.FilePath is { } path && Path.GetFullPath(path).Equals(Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase))
                    return generated;
            }
        }

        throw new McpException($"'{filePath}' is not part of the loaded project or solution.");
    }

    /// <summary>The document, if code can be edited in it: not a source generator's output.</summary>
    public static Document Editable(Document document) => document is SourceGeneratedDocument
        ? throw new McpException($"'{document.FilePath}' is the output of a source generator, which cannot be edited.")
        : document;

    /// <summary>
    /// Whether a syntax tree is code that people wrote, as Roslyn's analyzers tell it from generated code: a document of the solution, not from a
    /// source generator, not generated_code in .editorconfig, and without the name of a generated file, such as .g.cs or .designer.cs, or an
    /// &lt;auto-generated&gt; comment at its top.
    /// </summary>
    public static bool IsWrittenCode(Solution solution, SyntaxTree? tree)
    {
        if (tree is null || solution.GetDocument(tree) is not { } document || document is SourceGeneratedDocument)
            return false;

        switch (document.Project.CompilationOptions?.SyntaxTreeOptionsProvider?.IsGenerated(tree, CancellationToken.None))
        {
            case GeneratedKind.MarkedGenerated:
                return false;
            case GeneratedKind.NotGenerated:
                return true;
        }

        var name = Path.GetFileNameWithoutExtension(tree.FilePath) ?? "";
        if (name.StartsWith("TemporaryGeneratedFile_", StringComparison.OrdinalIgnoreCase)
            || new[] { ".designer", ".generated", ".g", ".g.i" }.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            return false;

        return !tree.GetRoot().GetLeadingTrivia().Any(trivia => trivia.ToString() is var comment
            && (comment.Contains("<autogenerated", StringComparison.OrdinalIgnoreCase) || comment.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase)));
    }

    // Edits write one at a time, so that no other write comes between an edit's check of the files and its own write.
    private static readonly SemaphoreSlim Writing = new(1, 1);

    /// <summary>
    /// Writes the documents that differ between two snapshots to disk, from where the file watchers bring them into the workspace.
    /// A renamed document moves its file. Returns the paths written or deleted. Writes nothing if a file changed since the first snapshot.
    /// </summary>
    public static async Task<IReadOnlyList<string>> WriteChangesAsync(Solution before, Solution after, CancellationToken cancellationToken)
    {
        var writes = new List<(string Path, string Text, Document? Original)>();
        var removals = new List<Document>();
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

                // A new file takes the line ends of the project's files.
                var text = (await document.GetTextAsync(cancellationToken)).ToString();
                if ((original ?? project.OldProject.Documents.FirstOrDefault()) is { } model)
                    text = WithLineEndsOf((await model.GetTextAsync(cancellationToken)).ToString(), text);
                writes.Add((path, text, original));
            }

            removals.AddRange(project.GetRemovedDocuments().Select(id => before.GetDocument(id)).OfType<Document>().Where(document => document.FilePath is not null));
        }

        await Writing.WaitAsync(cancellationToken);
        try
        {
            // Another edit, or an editor, may have written the files since the snapshot was taken; writing would undo what it wrote.
            foreach (var original in writes.Select(write => write.Original).OfType<Document>().Concat(removals).Where(document => document.FilePath is not null))
            {
                if (!File.Exists(original.FilePath) || !Read(original.FilePath!).ContentEquals(await original.GetTextAsync(cancellationToken)))
                    throw new McpException($"'{original.FilePath}' changed after this edit read it, as by another edit or an editor at the same time, so nothing was written: try again.");
            }

            foreach (var (path, _, original) in writes.Where(write => !write.Path.Equals(write.Original?.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                if (File.Exists(path))
                    throw new McpException($"'{path}' already exists, so nothing was written.");
            }

            var written = new List<string>();
            foreach (var (path, text, original) in writes)
            {
                // A file in Windows-1252 stays so, unless the edit brings in characters it has none of.
                var encoding = original is not null && (await original.GetTextAsync(cancellationToken)).Encoding?.CodePage == 1252 && Windows1252.GetString(Windows1252.GetBytes(text)) == text
                    ? Windows1252
                    : EncodingOf(original?.FilePath ?? path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // A new name that only differs in case would otherwise keep the old one, where file names ignore case.
                if (original?.FilePath is { } renamed && renamed.Equals(path, StringComparison.OrdinalIgnoreCase) && !renamed.Equals(path, StringComparison.Ordinal))
                    File.Move(renamed, path);
                await File.WriteAllTextAsync(path, text, encoding, cancellationToken);
                if (original?.FilePath is { } moved && !moved.Equals(path, StringComparison.OrdinalIgnoreCase))
                    File.Delete(moved);
                written.Add(path);
            }

            foreach (var removed in removals.Select(document => document.FilePath!).Where(File.Exists))
            {
                File.Delete(removed);
                written.Add(removed);
            }

            // A file linked into several projects is a document of each.
            return [.. written.Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        finally
        {
            Writing.Release();
        }
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

    /// <summary>
    /// A document's new text with the line ends its old text had, if those were of one kind: Roslyn writes the platform's line ends into code
    /// it formats or generates, which would mix them into a file with the other kind.
    /// </summary>
    internal static string WithLineEndsOf(string original, string text)
    {
        var crlf = (original.Length - original.Replace("\r\n", "").Length) / 2;
        var lf = original.Count(c => c == '\n') - crlf;
        return (crlf, lf) switch
        {
            (0, > 0) => text.Replace("\r\n", "\n"),
            (> 0, 0) => text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            _ => text,
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
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken)
            ?? await DeclaredAtAsync(document, position, cancellationToken)
            ?? throw new McpException($"No symbol found at {document.FilePath}:{line}:{column}.");

        // A name in a tuple, as in (a, b) = (b, a), also declares the tuple's element of that name; what it refers to is meant.
        if (symbol is IFieldSymbol { ContainingType.IsTupleType: true }
            && (await document.GetSyntaxRootAsync(cancellationToken))!.FindToken(position).Parent is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax { Parent: Microsoft.CodeAnalysis.CSharp.Syntax.ArgumentSyntax } name
            && (await document.GetSemanticModelAsync(cancellationToken))!.GetSymbolInfo(name, cancellationToken).Symbol is { } referenced)
            return referenced;
        return symbol;
    }

    /// <summary>
    /// The declaration whose location is at a position where no name is, as results give a conversion operator's location by its type, such
    /// as the tuple of implicit operator (int, int).
    /// </summary>
    private static async Task<ISymbol?> DeclaredAtAsync(Document document, int position, CancellationToken cancellationToken)
    {
        var model = (await document.GetSemanticModelAsync(cancellationToken))!;
        return (await document.GetSyntaxRootAsync(cancellationToken))!.FindToken(position).Parent?.AncestorsAndSelf()
            .Select(node => model.GetDeclaredSymbol(node, cancellationToken))
            .FirstOrDefault(declared => declared?.Locations.Any(location => location.SourceTree == model.SyntaxTree && location.SourceSpan.Contains(position)) == true);
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
        if (endLine < startLine)
            throw new McpException($"The last line, {endLine}, comes before the first, {startLine}.");
        if (startLine < 1 || endLine > text.Lines.Count)
            throw new McpException($"Lines {startLine} to {endLine} are outside '{filePath}', which has {text.Lines.Count} lines.");

        return TextSpan.FromBounds(text.Lines[startLine - 1].Start, text.Lines[endLine - 1].End);
    }

    /// <summary>A loaded workspace plus the file watchers that queue changes for it.</summary>
    private sealed class TrackedWorkspace(string path)
    {
        private readonly Lock _lock = new();
        private readonly ConcurrentQueue<(string Path, bool Created)> _changes = new();
        private FileSystemWatcher[]? _watchers;
        private LoadedWorkspace? _loaded;
        private LoadSettings _settings = new();
        private int _overflowed;

        /// <summary>Takes other settings, which the next call loads with.</summary>
        public void Configure(LoadSettings settings)
        {
            lock (_lock)
            {
                _settings = settings;
                _loaded = null;
            }
        }

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

            var loaded = Load(path, _settings);
            var previous = _watchers;
            var roots = GetRoots(loaded.Solution);
            _watchers =
            [
                .. roots.Select(root => Watch(root)),
                .. BuildFilesOutside(loaded, roots).Select(file => Watch(Path.GetDirectoryName(file)!, Path.GetFileName(file))),
                .. SourceDirectoriesOutside(loaded.Solution, roots).Select(directory => Watch(directory, "*.cs")),
            ];
            foreach (var watcher in previous)
                watcher.Dispose();

            return _loaded = loaded;
        }

        /// <summary>Watches a directory with its subdirectories, or the files in it that a filter, such as a file name, matches.</summary>
        private FileSystemWatcher Watch(string root, string? filter = null)
        {
            var watcher = new FileSystemWatcher(root, filter ?? "*")
            {
                IncludeSubdirectories = filter is null,
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

        /// <summary>
        /// The build files the projects read from outside the watched directories, usually above them: Directory.Build.props and .targets,
        /// Directory.Packages.props, and analyzer configuration such as a repository's .editorconfig.
        /// ponytail: only those that exist when the workspace loads; one created later above the projects needs another reload to count.
        /// </summary>
        private static IEnumerable<string> BuildFilesOutside(LoadedWorkspace loaded, string[] roots) =>
            loaded.Results.SelectMany(result => new[] { "DirectoryBuildPropsPath", "DirectoryBuildTargetsPath", "DirectoryPackagesPropsPath" }.Select(result.GetProperty))
                .Concat(loaded.Solution.Projects.SelectMany(project => project.AnalyzerConfigDocuments.Select(document => document.FilePath)))
                .OfType<string>()
                .Where(file => file.Length > 0 && File.Exists(file) && !roots.Any(root => IsUnder(file, root)))
                .Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The directories of the source files the projects compile from outside the watched directories, such as files shared between
        /// projects through Compile items with a relative path, or a package's content files.
        /// </summary>
        private static IEnumerable<string> SourceDirectoriesOutside(Solution solution, string[] roots) =>
            solution.Projects.SelectMany(project => project.Documents.Select(document => document.FilePath))
                .OfType<string>()
                .Where(file => !roots.Any(root => IsUnder(file, root)))
                .Select(file => Path.GetDirectoryName(file)!)
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase);

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

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The code page the compiler reads a file in that has no byte order mark and is not UTF-8, as older files often are.</summary>
    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    // From a stream, so the text gets an encoding: compilations cannot emit debug information without one. Sharing writes, so that reading a
    // file does not keep an edit or an editor from writing it; a write while reading changes the file again, which the watchers report.
    // ponytail: a project's CodePage property, which changes what the compiler falls back on, is not read.
    private static SourceText Read(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            return SourceText.From(stream, StrictUtf8);
        }
        catch (DecoderFallbackException)
        {
            stream.Position = 0;
            return SourceText.From(stream, Windows1252);
        }
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
    internal static PortableExecutableReference ReferenceTo(string path, MetadataReferenceProperties properties = default) =>
        MetadataReference.CreateFromFile(path, properties, Path.ChangeExtension(path, ".xml") is var xml && File.Exists(xml) ? XmlDocumentationProvider.CreateFromFile(xml) : null);

    private static LoadedWorkspace Load(string path, LoadSettings settings)
    {
        var isSolution = Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx";
        var manager = isSolution ? new AnalyzerManager(IOPath.Parse(path)) : new AnalyzerManager();

        // Buildalyzer 9 defaults to "Clean;Build", and Clean deletes the analyzed project's build output.
        // Run Compile instead; a missing input file keeps CoreCompile (and so the compiler arguments) from being skipped as up to date.
        var options = new EnvironmentOptions();
        options.TargetsToBuild.Clear();
        options.TargetsToBuild.Add("Compile");
        options.GlobalProperties["NonExistentFile"] = Path.Combine("__NonExistentSubDir__", "__NonExistentFile__");
        if (settings.Configuration is { } configuration)
            options.GlobalProperties["Configuration"] = configuration;

        // Build level by level, following project references (Buildalyzer's addProjectReferences would build them with the defaults above).
        // Like Buildalyzer's own GetWorkspace: builds run in parallel and take the first target framework of each project, or the chosen one.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<IAnalyzerResult>();
        var errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        // A .slnx gives paths as written, such as dir/../Lib/Lib.csproj, which a build of it does not report results for.
        var pending = (isSolution ? manager.Projects.Keys.Select(Path.GetFullPath) : [path]).Where(seen.Add).ToList();
        while (pending.Count > 0)
        {
            var built = pending.AsParallel()
                .Select(project => (Path: project, Build: Build(manager.GetProject(IOPath.Parse(project)), options, settings.TargetFramework)))
                .ToList();
            foreach (var (project, (_, projectErrors)) in built.Where(project => project.Build.Result is not { CompilerArguments.Length: > 0 } && LanguageOf(project.Path) is not null))
                errors[project] = projectErrors.Count > 0 ? projectErrors : [SdkProblemOf(project) ?? "The design-time build gave no compiler command line."];

            var analyzed = built.Select(project => project.Build.Result).OfType<IAnalyzerResult>().ToList();
            results.AddRange(analyzed);
            pending = analyzed.SelectMany(result => result.ProjectReferences).Select(Path.GetFullPath).Where(seen.Add).ToList();
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
        // A new loader per load picks up rebuilt generators after a reload.
        // ponytail: assemblies of earlier loads stay loaded; a collectible load context per load if that memory matters.
        var loader = NonLockingAnalyzerLoader();

        // One reference per file for all projects: each would read the file again, and Roslyn gives a project copies of the symbols of a
        // referenced project whose references are not the same as its own. A new one per load picks up changed files after a reload.
        var references = new Dictionary<string, PortableExecutableReference>(StringComparer.OrdinalIgnoreCase);
        MetadataReference Reference(string path, MetadataReferenceProperties properties)
        {
            if (!references.TryGetValue(path, out var reference))
                references[path] = reference = ReferenceTo(path);
            return reference.WithProperties(properties);
        }

        var warnings = projects.ToDictionary(project => project.Result.ProjectFilePath, _ => new ConcurrentQueue<Diagnostic>(), StringComparer.OrdinalIgnoreCase);
        workspace.Set(projects.Aggregate(workspace.CurrentSolution, (solution, project) =>
            solution.AddProject(ProjectOf(project.Result, project.Id, outputs, loader, Reference, warnings[project.Result.ProjectFilePath]))));
        return new(workspace.CurrentSolution, results, errors, warnings);
    }

    /// <summary>
    /// Why dotnet does not run in a project's directory, such as a global.json that asks for an SDK that is not installed, which keeps MSBuild
    /// from starting at all, so the build has no errors to tell; null if it runs.
    /// </summary>
    private static string? SdkProblemOf(string project)
    {
        var (exitCode, output) = DotnetCli.RunAsync(["--version"], TimeSpan.FromMinutes(1), CancellationToken.None, workingDirectory: Path.GetDirectoryName(project))
            .GetAwaiter().GetResult();
        if (exitCode == 0)
            return null;

        // Such as "Install the [8.0.100] .NET SDK or update [C:\src\global.json] to match an installed SDK."
        return output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("Install the ", StringComparison.Ordinal)) is { } advice
            ? $"A compatible .NET SDK was not found. {advice}"
            : $"dotnet does not run in {Path.GetDirectoryName(project)}: {BuildTool.Tail(output)}";
    }

    private static readonly Version? CompilerVersion = typeof(Compilation).Assembly.GetName().Version;

    /// <summary>
    /// A reference to an analyzer assembly that warns, as the compiler does, when its analyzers and generators do not load, such as when it
    /// is built for a newer compiler than this server's: Roslyn only raises an event as it loads them, and leaves them out.
    /// </summary>
    private static AnalyzerFileReference AnalyzerReferenceTo(string file, IAnalyzerAssemblyLoader loader, ConcurrentQueue<Diagnostic> warnings)
    {
        var reference = new AnalyzerFileReference(file, loader);
        reference.AnalyzerLoadFailed += (_, e) => warnings.Enqueue(e.ErrorCode == AnalyzerLoadFailureEventArgs.FailureErrorCode.ReferencesNewerCompiler
            ? Warning("CS9057", $"The analyzer assembly '{file}' references version '{e.ReferencedCompilerVersion}' of the compiler, which is newer than "
                + $"Roslynaut's, version '{CompilerVersion}': its analyzers and source generators do not run here, so the code they generate is missing.")
            : Warning("CS8034", $"Unable to load analyzer assembly {file}{(e.TypeName is null ? "" : $" ({e.TypeName})")}: {e.Message}"));
        return reference;
    }

    private static Diagnostic Warning(string id, string message) =>
        Diagnostic.Create(new DiagnosticDescriptor(id, id, "{0}", "Compiler", DiagnosticSeverity.Warning, isEnabledByDefault: true), Location.None, message);

    /// <summary>
    /// Roslyn's analyzer loader that shadow-copies analyzers and generators on Windows and loads them from streams elsewhere. The workspace's
    /// IAnalyzerService loader loads them in place and locks them, so builds that write them fail (MSB3027) while the server runs. It is internal.
    /// </summary>
    internal static IAnalyzerAssemblyLoader NonLockingAnalyzerLoader() =>
        (IAnalyzerAssemblyLoader)typeof(AnalyzerFileReference).Assembly.GetType("Microsoft.CodeAnalysis.AnalyzerAssemblyLoader", throwOnError: true)!
            .GetMethod("CreateNonLockingLoader", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [Path.Combine(Path.GetTempPath(), "Roslynaut", "Analyzers"), null, null, null])!;

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
    /// <param name="referenceTo">Makes the reference to a file, with properties.</param>
    /// <param name="warnings">Takes what this server's compiler does not take as the SDK's does.</param>
    private static ProjectInfo ProjectOf(
        IAnalyzerResult result, ProjectId id, Dictionary<string, ProjectId> outputs, IAnalyzerAssemblyLoader loader, Func<string, MetadataReferenceProperties, MetadataReference> referenceTo,
        ConcurrentQueue<Diagnostic> warnings)
    {
        var (name, language) = (Path.GetFileNameWithoutExtension(result.ProjectFilePath), LanguageOf(result.ProjectFilePath)!);
        var targetPath = result.GetProperty("TargetPath") is { Length: > 0 } target ? target : null;
        var project = ProjectInfo.Create(id, VersionStamp.Create(), name, name, language, result.ProjectFilePath, targetPath);
        if (result.CompilerArguments.Length == 0)
            return project;

        var directory = Path.GetDirectoryName(result.ProjectFilePath)!;
        CommandLineParser parser = language == LanguageNames.CSharp ? CSharpCommandLineParser.Default : VisualBasicCommandLineParser.Default;
        var arguments = parser.Parse(result.CompilerArguments, directory, sdkDirectory: null, additionalReferenceDirectories: null);
        // An SDK newer than this server's compiler can pass options it does not know, such as /langversion:15.0, which it leaves out.
        foreach (var error in arguments.Errors.Where(error => error.Severity == DiagnosticSeverity.Error))
            warnings.Enqueue(Warning(error.Id, $"{error.GetMessage(System.Globalization.CultureInfo.InvariantCulture)} Roslynaut's compiler, version '{CompilerVersion}', reads the project without it."));
        IEnumerable<DocumentInfo> Documents(IEnumerable<string> files) => files.Select(file => Path.GetFullPath(file, directory)).Where(File.Exists)
            .Select(file => DocumentInfo.Create(DocumentId.CreateNewId(id, file), Path.GetFileName(file), FoldersOf(result.ProjectFilePath, file),
                loader: TextLoader.From(TextAndVersion.Create(Read(file), VersionStamp.Create(), file)), filePath: file));
        // A command line can name a reference twice, as Visual Basic's does with the VB runtime, which a project cannot have.
        var references = arguments.MetadataReferences.Select(reference => (Path: Path.GetFullPath(reference.Reference, directory), reference.Properties)).Distinct().ToList();

        // Without a documentation file to write, the compiler skips documentation comments; an IDE parses them, for the documentation it shows.
        var parseOptions = arguments.ParseOptions.DocumentationMode == DocumentationMode.None ? arguments.ParseOptions.WithDocumentationMode(DocumentationMode.Parse) : arguments.ParseOptions;

        return project
            .WithAssemblyName(arguments.CompilationName ?? name)
            .WithCompilationOptions(arguments.CompilationOptions.WithStrongNameProvider(new DesktopStrongNameProvider(arguments.KeyFileSearchPaths)))
            .WithParseOptions(parseOptions)
            .WithDocuments(Documents(arguments.SourceFiles.Select(file => file.Path)))
            .WithAdditionalDocuments(Documents(arguments.AdditionalFiles.Select(file => file.Path)))
            .WithAnalyzerConfigDocuments(Documents(arguments.AnalyzerConfigPaths))
            .WithProjectReferences(references.Where(reference => outputs.ContainsKey(reference.Path))
                .Select(reference => new ProjectReference(outputs[reference.Path], reference.Properties.Aliases, reference.Properties.EmbedInteropTypes)))
            .WithMetadataReferences(references.Where(reference => !outputs.ContainsKey(reference.Path) && File.Exists(reference.Path))
                .Select(reference => referenceTo(reference.Path, reference.Properties)))
            .WithAnalyzerReferences(arguments.AnalyzerReferences.Select(reference => Path.GetFullPath(reference.FilePath, directory)).Where(File.Exists)
                .Select(file => AnalyzerReferenceTo(file, loader, warnings)))
            .WithDefaultNamespace(result.GetProperty("RootNamespace"));
    }

    /// <summary>
    /// Builds a project for the chosen target framework if it has that one, else for its first. Buildalyzer builds without one, and then a
    /// project with TargetFrameworks (even just one) builds as the outer build, which only dispatches to a build per framework and has no Compile
    /// target. The frameworks can come from an import, which Buildalyzer's project file reader misses, so take them from that first build.
    /// </summary>
    /// <returns>The result, and the build's errors if it gave no compiler command line, such as when restore did not run.</returns>
    /// <param name="analyzer">Null for a project of a solution that is missing on disk.</param>
    private static (IAnalyzerResult? Result, IReadOnlyList<string> Errors) Build(IProjectAnalyzer? analyzer, EnvironmentOptions options, string? targetFramework)
    {
        if (analyzer is null)
            return (null, ["The project file does not exist."]);

        var results = analyzer.Build(options);
        if (results.FirstOrDefault() is { CompilerArguments.Length: 0 } outer
            && outer.GetProperty("TargetFrameworks")?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is [var first, ..] frameworks)
            results = analyzer.Build(frameworks.FirstOrDefault(framework => string.Equals(framework, targetFramework, StringComparison.OrdinalIgnoreCase)) ?? first, options);

        var result = results.FirstOrDefault();
        return (result, result is { CompilerArguments.Length: > 0 }
            ? []
            : [.. results.BuildEventArguments.OfType<BuildErrorEventArgs>().Select(error => error.Code is { Length: > 0 } ? $"{error.Code}: {error.Message}" : $"{error.Message}").Distinct()]);
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
