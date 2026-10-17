using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Build.Logging.StructuredLogger;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using LogTask = Microsoft.Build.Logging.StructuredLogger.Task;

namespace Roslynaut.Tools;

/// <summary>Reads binary logs with MSBuild.StructuredLogger, for build and the build_* tools.</summary>
internal static class BuildLog
{
    public const string BinlogPath = "Path to a binary log (.binlog): one that build wrote with binlogPath, or one from dotnet build -bl elsewhere, such as CI.";
    public const string ProjectFilter = "Only projects whose file name contains this, such as App.csproj.";

    private static readonly Lock Gate = new();
    private static readonly List<(string Path, DateTime Written, Build Build)> Recent = [];

    /// <summary>Reads a binary log, keeping the last two read: agents ask several questions of one log, or compare two.</summary>
    public static Build Read(string binlogPath)
    {
        var path = Path.GetFullPath(binlogPath);
        if (!File.Exists(path))
            throw new McpException($"Binary log not found: {path}");

        var written = File.GetLastWriteTimeUtc(path);
        lock (Gate)
        {
            if (Recent.FirstOrDefault(entry => entry.Path == path && entry.Written == written).Build is { } cached)
                return cached;

            var build = Load(path);
            Recent.RemoveAll(entry => entry.Path == path);
            Recent.Add((path, written, build));
            if (Recent.Count > 2)
                Recent.RemoveAt(0);
            return build;
        }
    }

    /// <summary>Reads a binary log without keeping it, such as a temporary one.</summary>
    public static Build Load(string path)
    {
        Build build;
        try
        {
            build = BinaryLog.ReadBuild(path);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new McpException($"Cannot read the binary log {path}: {e.Message}");
        }

        // The analyses behind double writes, copied files, and the analyzer and generator summaries.
        BuildAnalyzer.AnalyzeBuild(build);

        // Its search for targets throws on some queries.
        foreach (var extension in build.SearchExtensions.OfType<TargetGraphManager>().ToList())
            build.SearchExtensions.Remove(extension);
        return build;
    }

    /// <summary>A project's file name, with its target framework.</summary>
    public static string Name(IProjectOrEvaluation project) =>
        Framework(project) is { Length: > 0 } framework ? $"{Path.GetFileName(project.ProjectFile)} ({framework})" : Path.GetFileName(project.ProjectFile);

    /// <summary>
    /// The target framework a project built for. A project node knows it only when it is a global property, as in the inner builds of several
    /// frameworks; its evaluation always does.
    /// </summary>
    public static string? Framework(IProjectOrEvaluation project) =>
        Path.GetExtension(project.ProjectFile) is ".sln" or ".slnx" or ".metaproj" ? null
        : project is Project { TargetFramework: not { Length: > 0 } } node && node.GetRoot() is Build build ? build.FindEvaluation(node.EvaluationId)?.TargetFramework
        : project.TargetFramework;

    public static bool Matches(IProjectOrEvaluation project, string? filter) =>
        filter is null || Path.GetFileName(project.ProjectFile).Contains(filter, StringComparison.OrdinalIgnoreCase);

    // NuGet restore builds its own instances of every project, with these global properties.
    public static bool IsRestore(Project project) =>
        project.GlobalProperties is { } properties && (properties.ContainsKey("MSBuildIsRestoring") || properties.ContainsKey("ExcludeRestorePackageImports"));

    public static bool IsRestore(ProjectEvaluation evaluation) =>
        GlobalProperties(evaluation).Any(property => property.Name is "MSBuildIsRestoring" or "ExcludeRestorePackageImports");

    public static IEnumerable<Property> GlobalProperties(ProjectEvaluation evaluation) =>
        evaluation.FindChild<Folder>("Properties")?.FindChild<Folder>("Global")?.Children.OfType<Property>() ?? [];

    /// <summary>The project, target and task a node is in, or is.</summary>
    public static (string? Project, string? Target, string? Task) Where(BaseNode node)
    {
        // A target or task above the project, such as the MSBuild task that built it, belongs to another project.
        var project = node.GetNearestParentOrSelf<Project>();
        string? Within(NamedNode? found) => found is not null && found.GetNearestParentOrSelf<Project>() == project ? found.Name : null;
        return (
            project is not null ? Name(project) : node.GetNearestParentOrSelf<ProjectEvaluation>() is { } evaluation ? Name(evaluation) : null,
            Within(node.GetNearestParentOrSelf<Target>()),
            Within(node.GetNearestParentOrSelf<LogTask>()));
    }

    public static string Join(IReadOnlyCollection<string> values, int max = 20) =>
        values.Count > max ? string.Join(";", values.Take(max)) + $";… ({values.Count} in all)" : string.Join(";", values);

    public static string Truncate(string text, int max = 1000) => text.Length > max ? text[..max] + "…" : text;
}

public sealed class BuildProblemsTool : IMcpTool
{
    private static readonly Regex Quoted = new("\"(?<value>[^\"]+)\"");
    private static readonly Regex Chosen = new("^\"(?<identity>[^\"]+)\" was chosen because (?<reason>.+?)\\.?$");

    public Delegate Handler => Check;

    [McpServerTool(Name = "build_problems", ReadOnly = true)]
    [Description("Checks a build's binary log for problems that make outputs wrong, parallel builds flaky or builds slow: files that copies from more "
        + "than one source wrote (double writes: the last copy wins), projects built more than once with different global properties, and assembly "
        + "version conflicts, with what depends on each version and which one ResolveAssemblyReference chose.")]
    internal static BuildProblems Check([Description(BuildLog.BinlogPath)] string binlogPath)
    {
        var build = BuildLog.Read(binlogPath);
        return new(
            [.. DoubleWritesAnalyzer.GetDoubleWrites(build).Select(write => new DoubleWrite(write.Key, [.. write.Value.Order()]))],
            DuplicateBuilds(build),
            ReferenceConflicts(build));
    }

    private static IReadOnlyList<DuplicateBuild> DuplicateBuilds(Build build) =>
    [
        .. build.FindChildrenRecursive<Project>(project => !BuildLog.IsRestore(project))
            .GroupBy(project => (project.ProjectFile, BuildLog.Framework(project)))
            .Select(group => (Project: group.First(), Builds: group.Select(project => project.GlobalProperties ?? new Dictionary<string, string>()).DistinctBy(Signature).ToList()))
            .Where(group => group.Builds.Count > 1)
            .Select(group =>
            {
                var differing = group.Builds.SelectMany(properties => properties.Keys).Distinct()
                    .Where(key => group.Builds.Select(properties => Value(properties, key)).Distinct().Count() > 1)
                    .Order()
                    .ToList();
                return new DuplicateBuild(
                    BuildLog.Name(group.Project),
                    [.. group.Builds.Select(properties => (IReadOnlyDictionary<string, string>)differing.ToDictionary(key => key, key => Value(properties, key) ?? ""))]);
            }),
    ];

    private static string Signature(IDictionary<string, string> properties) =>
        string.Join(";", properties.OrderBy(property => property.Key, StringComparer.OrdinalIgnoreCase).Select(property => $"{property.Key}={property.Value}"));

    private static string? Value(IDictionary<string, string> properties, string key) => properties.TryGetValue(key, out var value) ? value : null;

    // ResolveAssemblyReference's results hold each conflict as "There was a conflict between "A, Version=1" and "A, Version=2"." with what it chose
    // and, per version, the references that depend on it.
    private static IReadOnlyList<ReferenceConflict> ReferenceConflicts(Build build) =>
    [
        .. build.FindChildrenRecursive<ResolveAssemblyReferenceTask>()
            .SelectMany(task => (task.Results?.Children.OfType<Parameter>() ?? []).Where(result => result.Name.StartsWith("There was a conflict between")).Select(conflict =>
            {
                var items = conflict.Children.OfType<Item>().ToList();
                var chosen = items.Select(item => Chosen.Match(item.Text)).FirstOrDefault(match => match.Success);
                var assembly = Quoted.Match(conflict.Name).Groups["value"].Value.Split(',')[0];
                var project = task.GetNearestParent<Project>();
                return new ReferenceConflict(
                    project is null ? "" : BuildLog.Name(project),
                    assembly,
                    chosen?.Groups["identity"].Value,
                    chosen?.Groups["reason"].Value,
                    [.. items.Where(item => item.Text.StartsWith("References which depend on")).Select(item =>
                        new ConflictingVersion(Quoted.Match(item.Text).Groups["value"].Value, [.. item.Children.OfType<Item>().Select(reference => reference.Text)]))],
                    project?.FindChildrenRecursive<Warning>(warning => warning.Code == "MSB3277" && warning.Text.Contains($"\"{assembly}\"")).FirstOrDefault()?.Text);
            })),
    ];

    public sealed record BuildProblems(IReadOnlyList<DoubleWrite> DoubleWrites, IReadOnlyList<DuplicateBuild> DuplicateBuilds, IReadOnlyList<ReferenceConflict> ReferenceConflicts);

    /// <param name="Sources">The files copied to it.</param>
    public sealed record DoubleWrite(string File, IReadOnlyList<string> Sources);

    /// <param name="Builds">
    /// Per build of the project, the global properties that differ between them, such as Configuration, RuntimeIdentifier or a property that a
    /// ProjectReference or MSBuild task sets; an empty value means not set. Each build of a project with other global properties builds it again.
    /// </param>
    public sealed record DuplicateBuild(string Project, IReadOnlyList<IReadOnlyDictionary<string, string>> Builds);

    /// <param name="Assembly">The assembly's name.</param>
    /// <param name="Chosen">The version ResolveAssemblyReference resolved the conflict to, with why.</param>
    /// <param name="Versions">Each version, with the references that depend on it.</param>
    /// <param name="Warning">The MSB3277 warning about it, if it could not resolve it.</param>
    public sealed record ReferenceConflict(string Project, string Assembly, string? Chosen, string? Reason, IReadOnlyList<ConflictingVersion> Versions, string? Warning);

    /// <param name="Identity">The assembly's full name, with the version.</param>
    public sealed record ConflictingVersion(string Identity, IReadOnlyList<string> DependedOnBy);
}

public sealed class BuildIncrementalityTool : IMcpTool
{
    public Delegate Handler => CheckAsync;

    [McpServerTool(Name = "build_incrementality", Idempotent = true)]
    [Description("Checks that a build is incremental: builds the project twice and reports what the second build, which should have nothing to do, "
        + "did anyway: the targets with Inputs and Outputs that ran, each with the output that was missing or the input that was newer, and the "
        + "files it copied. Or reads the binary log of such a second build. Empty lists mean the build is incremental.")]
    internal static async Task<Incrementality> CheckAsync(
        [Description("The project or solution to build twice; or leave this out and give binlogPath.")] string? projectPath = null,
        [Description("Instead of projectPath: the binary log of a build that ran right after another one.")] string? binlogPath = null,
        [Description("The configuration, such as Debug or Release.")] string configuration = "Debug",
        CancellationToken cancellationToken = default)
    {
        if (projectPath is null)
            return binlogPath is not null ? Check(BuildLog.Read(binlogPath)) : throw new McpException("Give projectPath or binlogPath.");

        var first = await BuildTool.BuildAsync(projectPath, configuration, cancellationToken: cancellationToken);
        if (!first.Succeeded)
            throw new McpException($"The build failed: {first.Messages.FirstOrDefault()?.Message ?? first.Output}");

        var binlog = Path.Combine(Path.GetTempPath(), $"roslynaut-{Guid.NewGuid():N}.binlog");
        try
        {
            await BuildTool.BuildAsync(projectPath, configuration, binlogPath: binlog, cancellationToken: cancellationToken);
            return Check(BuildLog.Load(binlog));
        }
        finally
        {
            File.Delete(binlog);
        }
    }

    // MSBuild explains each target with Inputs and Outputs that runs: "Building target "X" completely." and then why. A target with no inputs
    // always runs, and says so; that is by design.
    internal static Incrementality Check(Build build) => new(
        [
            .. build.FindChildrenRecursive<Target>(target => !target.Skipped)
                .Select(target => (Target: target, Messages: Messages(target)))
                .Where(target => target.Messages is [var building, var reason, ..] && building.StartsWith("Building target") && reason != "No input files were specified.")
                .Select(target => new RebuiltTarget(BuildLog.Name(target.Target.Project), target.Target.Name, string.Join(" ", target.Messages.Skip(1).Take(3))))
                .Distinct(),
        ],
        [
            .. build.FindChildrenRecursive<CopyTask>().SelectMany(task => task.FileCopyOperations)
                .Where(copy => copy.Copied)
                .Select(copy => $"{copy.Source} -> {copy.Destination}")
                .Distinct()
                .Take(100),
        ]);

    /// <summary>What MSBuild said about a target itself, such as why it ran or skipped, without the property changes it logs as messages too.</summary>
    internal static List<string> Messages(Target target) => [.. target.Children.OfType<Message>().Where(message => message is not MessageWithLocation).Select(message => message.Text)];

    /// <param name="Targets">The targets that ran, with why.</param>
    /// <param name="Copied">The files copied, as "source -> destination"; at most 100.</param>
    public sealed record Incrementality(IReadOnlyList<RebuiltTarget> Targets, IReadOnlyList<string> Copied);

    /// <param name="Reason">What MSBuild said, such as which output did not exist or which input was newer than which output.</param>
    public sealed record RebuiltTarget(string Project, string Target, string Reason);
}

public sealed class BuildFileOriginTool : IMcpTool
{
    public Delegate Handler => Trace;

    [McpServerTool(Name = "build_file_origin", ReadOnly = true)]
    [Description("Explains where a file that a build wrote came from: each copy to it, with its source, project, target and task; then where each "
        + "source came from in turn, back to the compilation that wrote it. Copies from different sources to one file are a double write: the last "
        + "one wins. Empty if no copy or compilation in the log wrote it; other tasks are not tracked.")]
    internal static IReadOnlyList<FileWrite> Trace(
        [Description(BuildLog.BinlogPath)] string binlogPath,
        [Description("The file, such as a DLL in a bin directory.")] string filePath)
    {
        var build = BuildLog.Read(binlogPath);
        var compiled = build.FindChildrenRecursive<ManagedCompilerTask>()
            .SelectMany(task => Outputs(task).Select(file => (File: Path.GetFullPath(file, task.GetNearestParent<Project>()?.ProjectDirectory ?? ""), Task: task)))
            .ToLookup(output => output.File, output => output.Task, StringComparer.OrdinalIgnoreCase);

        var writes = new List<FileWrite>();
        var (queue, seen) = (new Queue<string>([Path.GetFullPath(filePath)]), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        while (writes.Count < 100 && queue.TryDequeue(out var file))
        {
            if (!seen.Add(file))
                continue;

            foreach (var copy in build.FileCopyMap.GetFile(file, create: false)?.Incoming ?? [])
            {
                writes.Add(new(file, copy.FileCopyOperation.Source, BuildLog.Name(copy.Project), copy.Target.Name, copy.Task.Name, copy.FileCopyOperation.Copied));
                queue.Enqueue(copy.FileCopyOperation.Source);
            }

            foreach (var task in compiled[file])
                writes.Add(new(file, null, task.GetNearestParent<Project>() is { } project ? BuildLog.Name(project) : "", task.GetNearestParent<Target>()?.Name ?? "", task.Name, true));
        }

        return writes;
    }

    private static IEnumerable<string> Outputs(ManagedCompilerTask task) =>
        task.CompilationWrites is { } writes ? new[] { writes.Assembly, writes.RefAssembly, writes.Pdb, writes.XmlDocumentation }.Where(file => !string.IsNullOrEmpty(file)) : [];

    /// <param name="Source">The file it was copied from; null when the compiler wrote it.</param>
    /// <param name="Copied">False when the copy skipped it because it was up to date already.</param>
    public sealed record FileWrite(string File, string? Source, string Project, string Target, string Task, bool Copied);
}

public sealed class BuildTargetWhyTool : IMcpTool
{
    public Delegate Handler => Explain;

    [McpServerTool(Name = "build_target_why", ReadOnly = true)]
    [Description("Explains why a target ran or was skipped, in each project of a build's binary log where it appears: the chain of targets that asked "
        + "for it, through DependsOnTargets, BeforeTargets or AfterTargets, up to the target the build started with; and for a target with Inputs "
        + "and Outputs, the output that was missing or the input that was newer, or that its outputs were up to date. A target that is not in the "
        + "log never ran: nothing asked for it, or its condition was false.")]
    internal static IReadOnlyList<TargetRun> Explain(
        [Description(BuildLog.BinlogPath)] string binlogPath,
        [Description("The target, such as CoreCompile.")] string target,
        [Description(BuildLog.ProjectFilter)] string? project = null)
    {
        var build = BuildLog.Read(binlogPath);
        var instances = build.FindChildrenRecursive<Project>().ToLookup(node => node.ProjectInstanceId);

        // A target runs once per project instance; MSBuild logs the later requests for it as skipped.
        return
        [
            .. build.FindChildrenRecursive<Target>(node => node.Name.Equals(target, StringComparison.OrdinalIgnoreCase) && BuildLog.Matches(node.Project, project))
                .GroupBy(node => node.Project.ProjectInstanceId)
                .Select(group => group.FirstOrDefault(node => !node.Skipped) ?? group.First())
                .Take(50)
                .Select(node => new TargetRun(
                    BuildLog.Name(node.Project),
                    !node.Skipped ? null : node.OriginalNode is not null ? "Built before in this project." : BuildIncrementalityTool.Messages(node).FirstOrDefault() ?? "Skipped.",
                    [.. BuildIncrementalityTool.Messages(node).Take(5)],
                    Chain(node, instances),
                    (long)node.Duration.TotalMilliseconds)),
        ];
    }

    private static IReadOnlyList<string> Chain(Target target, ILookup<int, Project> instances)
    {
        var chain = new List<string>();
        for (var current = target; current is not null && chain.Count < 50;)
        {
            // TargetBuiltReason: InitialTargets, DefaultTargets and EntryTargets (4 to 6) are newer than this StructuredLogger's copy of the enum.
            chain.Add((int)current.TargetBuiltReason switch
            {
                1 => $"{current.Name} (BeforeTargets {current.ParentTarget})",
                2 => $"{current.Name} (in DependsOnTargets of {current.ParentTarget})",
                3 => $"{current.Name} (AfterTargets {current.ParentTarget})",
                4 => $"{current.Name} (InitialTargets)",
                5 => $"{current.Name} (DefaultTargets)",
                _ => $"{current.Name} (entry target)",
            });

            var parent = (int)current.TargetBuiltReason is >= 1 and <= 3 ? current.ParentTarget : null;
            current = parent is null ? null : instances[current.Project.ProjectInstanceId]
                .SelectMany(project => project.Children.OfType<Target>())
                .FirstOrDefault(node => node.Name == parent && !node.Skipped);
        }

        return chain;
    }

    /// <param name="Skipped">Why it did not run; null when it ran.</param>
    /// <param name="Messages">What MSBuild said about it, such as which output did not exist.</param>
    /// <param name="Chain">From this target up: each target, with how it was asked for by the next one.</param>
    public sealed record TargetRun(string Project, string? Skipped, IReadOnlyList<string> Messages, IReadOnlyList<string> Chain, long Milliseconds);
}

public sealed class BuildPropertyTraceTool : IMcpTool
{
    private const int MaxChanges = 300;

    public Delegate Handler => Trace;

    [McpServerTool(Name = "build_property_trace", ReadOnly = true)]
    [Description("Traces an MSBuild property or item type through a build's binary log: its value in each project after evaluation; then each change "
        + "in order: reassignments during evaluation with file and line, and while targets ran, properties set in a target or output by a task, and "
        + "items added, removed or output by a task, each with project, target and task. Unlike msbuild_property_trace, which reads the project, this "
        + "shows what happened in that build. At most 300 changes.")]
    internal static PropertyHistory Trace(
        [Description(BuildLog.BinlogPath)] string binlogPath,
        [Description("The property or item type, such as DefineConstants or Compile.")] string name,
        [Description(BuildLog.ProjectFilter)] string? project = null)
    {
        var build = BuildLog.Read(binlogPath);
        bool Named(string? node) => string.Equals(node, name, StringComparison.OrdinalIgnoreCase);

        var (evaluated, changes) = (new List<EvaluatedValue>(), new List<ValueChange>());
        foreach (var evaluation in build.FindChildrenRecursive<ProjectEvaluation>(evaluation => BuildLog.Matches(evaluation, project) && !BuildLog.IsRestore(evaluation)))
        {
            var evaluationName = BuildLog.Name(evaluation);
            if (evaluation.FindChild<Folder>("Properties")?.Children.OfType<Property>().FirstOrDefault(property => Named(property.Name)) is { } value)
                evaluated.Add(new(evaluationName, value.Value));
            if (evaluation.FindChild<Folder>("Items")?.Children.OfType<AddItem>().Where(items => Named(items.Name)).SelectMany(Items).ToList() is { Count: > 0 } items)
                evaluated.Add(new(evaluationName, BuildLog.Join(items)));

            foreach (var reassignment in (evaluation.PropertyReassignmentFolder?.Children.OfType<Folder>() ?? []).Where(folder => Named(folder.Name)).SelectMany(folder => folder.Children.OfType<PropertyReassignmentMessage>()))
                changes.Add(new(evaluationName, null, null, "Evaluation", reassignment.NewValue, $"{reassignment.FilePath}({reassignment.Line})"));
        }

        foreach (var target in build.FindChildrenRecursive<Target>(target => BuildLog.Matches(target.Project, project) && !BuildLog.IsRestore(target.Project)))
        {
            var projectName = BuildLog.Name(target.Project);
            string? location = null;
            foreach (var node in target.Children)
            {
                switch (node)
                {
                    // Comes right before the property it sets, when it had a value already.
                    case MessageWithLocation message when message.Text.StartsWith($"Property reassignment: $({name})", StringComparison.OrdinalIgnoreCase):
                        location = $"{message.FilePath}({message.Line})";
                        break;
                    case Property property when Named(property.Name):
                        changes.Add(new(projectName, target.Name, null, "Set", property.Value, location));
                        location = null;
                        break;
                    case AddItem items when Named(items.Name):
                        changes.Add(new(projectName, target.Name, null, "Add", BuildLog.Join([.. Items(items)]), null));
                        break;
                    case RemoveItem items when Named(items.Name):
                        changes.Add(new(projectName, target.Name, null, "Remove", BuildLog.Join([.. Items(items)]), null));
                        break;
                    case LogTask task:
                        foreach (var output in task.Children.OfType<Folder>().Where(folder => folder.Name is "OutputItems" or "OutputProperties").SelectMany(folder => folder.Children))
                        {
                            if (output is TaskParameterProperty property && Named(property.Name))
                                changes.Add(new(projectName, target.Name, task.Name, "Output", property.Value, null));
                            else if (output is TaskParameterItem outputItems && Named(outputItems.Name))
                                changes.Add(new(projectName, target.Name, task.Name, "Output", BuildLog.Join([.. Items(outputItems)]), null));
                        }

                        break;
                }
            }
        }

        return new([.. evaluated.Distinct()], [.. changes.Take(MaxChanges)]);
    }

    private static IEnumerable<string> Items(TreeNode items) => items.Children.OfType<Item>().Select(item => item.Text);

    /// <param name="Evaluated">Per project, its value after evaluation; for an item type, its items.</param>
    /// <param name="Changes">Each change, in the order the log has them per project.</param>
    public sealed record PropertyHistory(IReadOnlyList<EvaluatedValue> Evaluated, IReadOnlyList<ValueChange> Changes);

    public sealed record EvaluatedValue(string Project, string Value);

    /// <param name="Target">The target that changed it; null during evaluation.</param>
    /// <param name="Task">The task that output it, if one did.</param>
    /// <param name="Kind">Evaluation (a reassignment), Set (in a target), Add or Remove (items in a target), or Output (by a task).</param>
    /// <param name="Value">The new value; for items, the ones added, removed or output.</param>
    /// <param name="Location">The file and line that set it, where the log has it.</param>
    public sealed record ValueChange(string Project, string? Target, string? Task, string Kind, string Value, string? Location);
}

public sealed class BuildLogSearchTool : IMcpTool
{
    public Delegate Handler => Search;

    [McpServerTool(Name = "build_log_search", ReadOnly = true)]
    [Description("Searches a build's binary log as the MSBuild Structured Log Viewer does, and returns each match with the project, target and task it "
        + "is in. Words match the text of any node: messages, properties, items, metadata, targets and tasks; \"quotes\" match a phrase. A $kind keeps "
        + "one kind of node, such as '$task Csc', '$target CoreCompile', '$project App' or '$property name=LangVersion'; project(App) keeps the "
        + "matches in that project, and under($project App) those in it and in the projects it built.")]
    internal static IReadOnlyList<LogMatch> Search(
        [Description(BuildLog.BinlogPath)] string binlogPath,
        [Description("The query, such as '$task Csc project(App)'.")] string query,
        [Description("At most this many matches.")] int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        var build = BuildLog.Read(binlogPath);
        var search = new StructuredLogViewer.Search([build], build.StringTable.Instances, maxResults, markResultsInTree: false);
        return
        [
            .. search.FindNodes(query, cancellationToken).Select(result => result.Node).OfType<BaseNode>().Take(maxResults).Select(node =>
            {
                var (project, target, task) = BuildLog.Where(node);
                return new LogMatch(node.TypeName, BuildLog.Truncate(node.ToString() ?? ""), project, target, task);
            }),
        ];
    }

    /// <param name="Kind">The kind of node, such as Message, Property, Item, Target or Task.</param>
    public sealed record LogMatch(string Kind, string Text, string? Project, string? Target, string? Task);
}

public sealed class BuildDiffTool : IMcpTool
{
    private const int MaxDifferences = 100;
    private static readonly Regex Argument = new("\"[^\"]*\"|\\S+");

    // The dotnet CLI sets it anew for every command, and the environment variable shows up as a property too.
    private const string TelemetrySession = "DOTNET_CLI_TELEMETRY_SESSIONID";

    public Delegate Handler => Diff;

    [McpServerTool(Name = "build_diff", ReadOnly = true)]
    [Description("Compares two binary logs of a build, such as a passing and a failing one, or a local and a CI one: the environment variables that "
        + "differ; per project, the global properties, the properties and items after evaluation, and the compiler arguments (where both compiled) "
        + "that differ; the projects in only one log; and the projects whose work took the most more or less time. Projects match by their path "
        + "relative to the directory that holds a log's projects, which values show as <root>, so logs from different machines compare.")]
    internal static BuildDiff Diff(
        [Description(BuildLog.BinlogPath)] string binlogPath,
        [Description("The other binary log.")] string otherBinlogPath,
        [Description(BuildLog.ProjectFilter)] string? project = null)
    {
        var (first, second) = (Snapshot.Of(BuildLog.Read(binlogPath), project), Snapshot.Of(BuildLog.Read(otherBinlogPath), project));
        var shared = first.Projects.Keys.Intersect(second.Projects.Keys).Order().ToList();
        return new(
            Compare(first.Environment, second.Environment),
            [
                .. shared.Select(key => (Key: key, First: first.Projects[key], Second: second.Projects[key]))
                    .Select(pair => new ProjectDiff(
                        pair.Key,
                        Compare(pair.First.GlobalProperties, pair.Second.GlobalProperties),
                        Compare(pair.First.Properties, pair.Second.Properties),
                        Compare(pair.First.Items, pair.Second.Items),
                        pair.First.CompilerArguments is { } arguments && pair.Second.CompilerArguments is { } otherArguments ? Compare(arguments, otherArguments) : []))
                    .Where(diff => diff.GlobalProperties.Count + diff.Properties.Count + diff.Items.Count + diff.CompilerArguments.Count > 0),
            ],
            [.. first.Projects.Keys.Except(second.Projects.Keys).Order()],
            [.. second.Projects.Keys.Except(first.Projects.Keys).Order()],
            [
                .. shared.Select(key => new WorkChange(key, first.Projects[key].WorkMilliseconds, second.Projects[key].WorkMilliseconds))
                    .OrderByDescending(change => Math.Abs(change.SecondMilliseconds - change.FirstMilliseconds))
                    .Take(10),
            ]);
    }

    private static IReadOnlyList<ValueDiff> Compare(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second) =>
    [
        .. first.Keys.Union(second.Keys).Order()
            .Select(key => new ValueDiff(key, first.GetValueOrDefault(key), second.GetValueOrDefault(key)))
            .Where(diff => diff.First != diff.Second)
            .Take(MaxDifferences),
    ];

    // Sets of values, such as items or arguments, by the name they are listed under.
    private static IReadOnlyList<ValueDiff> Compare(ILookup<string, string> first, ILookup<string, string> second) =>
    [
        .. first.SelectMany(group => group.Except(second[group.Key]).Select(value => new ValueDiff(group.Key, value, null)))
            .Concat(second.SelectMany(group => group.Except(first[group.Key]).Select(value => new ValueDiff(group.Key, null, value))))
            .OrderBy(diff => diff.Name)
            .Take(MaxDifferences),
    ];

    /// <summary>What a log says about the build, with the directory of its projects as &lt;root&gt;.</summary>
    private sealed record Snapshot(IReadOnlyDictionary<string, string> Environment, IReadOnlyDictionary<string, ProjectState> Projects)
    {
        public static Snapshot Of(Build build, string? filter)
        {
            var evaluations = build.FindChildrenRecursive<ProjectEvaluation>(evaluation => !BuildLog.IsRestore(evaluation) && BuildLog.Matches(evaluation, filter));
            var root = CommonDirectory(evaluations.Select(evaluation => evaluation.ProjectFile));
            string Normal(string value) => root is null ? value : BuildLog.Truncate(value.Replace(root, "<root>", StringComparison.OrdinalIgnoreCase), 500);
            string Key(IProjectOrEvaluation project) =>
                (root is null ? project.ProjectFile : Path.GetRelativePath(root, project.ProjectFile)) + (BuildLog.Framework(project) is { Length: > 0 } framework ? $" ({framework})" : "");

            var projects = build.FindChildrenRecursive<Project>(node => !BuildLog.IsRestore(node) && BuildLog.Matches(node, filter)).ToLookup(Key);
            return new(
                ToDictionary((build.EnvironmentFolder?.Children.OfType<Property>() ?? []).Where(variable => variable.Name != TelemetrySession), Normal),
                evaluations.GroupBy(Key).ToDictionary(group => group.Key, group =>
                {
                    // The last evaluation of a project is the one its build used.
                    var evaluation = group.Last();
                    var compiler = projects[group.Key].SelectMany(node => node.FindChildrenRecursive<ManagedCompilerTask>(task => task.GetNearestParent<Project>() == node)).LastOrDefault();
                    return new ProjectState(
                        ToDictionary(BuildLog.GlobalProperties(evaluation), Normal),
                        ToDictionary((evaluation.FindChild<Folder>("Properties")?.Children.OfType<Property>() ?? []).Where(property => property.Name != TelemetrySession), Normal),
                        (evaluation.FindChild<Folder>("Items")?.Children.OfType<AddItem>() ?? [])
                            .SelectMany(items => items.Children.OfType<Item>().Select(item => (Type: items.Name, Include: Normal(item.Text))))
                            .ToLookup(item => item.Type, item => item.Include),
                        compiler?.CommandLineArguments is { } arguments
                            ? Argument.Matches(arguments).Skip(1).Select(argument => Normal(argument.Value)).ToLookup(argument => argument.Split(':')[0])
                            : null,
                        (long)projects[group.Key]
                            .SelectMany(node => node.FindChildrenRecursive<LogTask>(task => task is not MSBuildTask && task.Name != "CallTarget" && task.GetNearestParent<Project>() == node))
                            .Sum(task => task.Duration.TotalMilliseconds));
                }));
        }

        private static Dictionary<string, string> ToDictionary(IEnumerable<Property> properties, Func<string, string> normal)
        {
            // A later setting wins, as in MSBuild.
            var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties)
                dictionary[property.Name] = normal(property.Value ?? "");
            return dictionary;
        }

        private static string? CommonDirectory(IEnumerable<string> files)
        {
            string[]? common = null;
            foreach (var directory in files.Select(Path.GetDirectoryName).OfType<string>())
            {
                var parts = directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                common = common is null ? parts : [.. common.Zip(parts).TakeWhile(pair => pair.First.Equals(pair.Second, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.First)];
            }

            return common is { Length: > 0 } ? string.Join(Path.DirectorySeparatorChar, common) : null;
        }
    }

    private sealed record ProjectState(
        IReadOnlyDictionary<string, string> GlobalProperties, IReadOnlyDictionary<string, string> Properties, ILookup<string, string> Items,
        ILookup<string, string>? CompilerArguments, long WorkMilliseconds);

    /// <param name="Environment">The environment variables the builds read that differ.</param>
    /// <param name="Projects">The projects in both logs that differ.</param>
    /// <param name="Work">The projects whose tasks took the most more or less time.</param>
    public sealed record BuildDiff(
        IReadOnlyList<ValueDiff> Environment, IReadOnlyList<ProjectDiff> Projects, IReadOnlyList<string> OnlyInFirst, IReadOnlyList<string> OnlyInSecond,
        IReadOnlyList<WorkChange> Work);

    /// <param name="Items">Items in only one log: Name is the item type.</param>
    /// <param name="CompilerArguments">Compiler arguments in only one log: Name is the option, such as /reference.</param>
    public sealed record ProjectDiff(
        string Project, IReadOnlyList<ValueDiff> GlobalProperties, IReadOnlyList<ValueDiff> Properties, IReadOnlyList<ValueDiff> Items, IReadOnlyList<ValueDiff> CompilerArguments);

    /// <param name="First">The value in the first log; null if it has none.</param>
    /// <param name="Second">The value in the other log; null if it has none.</param>
    public sealed record ValueDiff(string Name, string? First, string? Second);

    public sealed record WorkChange(string Project, long FirstMilliseconds, long SecondMilliseconds);
}
