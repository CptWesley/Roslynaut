using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Roslynaut.Tools;

public sealed class VerifyPendingTool : IMcpTool
{
    public Delegate Handler => PendingAsync;

    [McpServerTool(Name = "verify_pending", Destructive = true)]
    [Description("Lists the snapshots that Verify tests received and that do not match the verified ones: each *.received.* file beside its "
        + "*.verified.* file, with the diff from the verified file to the received one, or the text of a new snapshot. accept makes received "
        + "files the verified ones, reject deletes them. Run after run_tests, and accept only a snapshot whose diff is the intended change.")]
    private static async Task<PendingSnapshots> PendingAsync(
        [Description("A .csproj, .sln or .slnx file, or a directory: the received files are looked for below its directory, outside bin and obj.")] string projectPath,
        [Description("The received files to accept, as Received names them, or '*' for all.")] string[]? accept = null,
        [Description("The received files to reject, which deletes them, as Received names them, or '*' for all.")] string[]? reject = null,
        CancellationToken cancellationToken = default) =>
        await ReviewAsync(Directory.Exists(projectPath) ? projectPath : Path.GetDirectoryName(Path.GetFullPath(projectPath))!, accept ?? [], reject ?? [], cancellationToken);

    /// <summary>Accepts and rejects received files below a directory, then lists those left with their diffs.</summary>
    internal static async Task<PendingSnapshots> ReviewAsync(string directory, IReadOnlyList<string> accept, IReadOnlyList<string> reject, CancellationToken cancellationToken)
    {
        var received = FilesBelow(Path.GetFullPath(directory), "*.received.*").ToList();
        var (accepted, rejected) = (Picked(received, accept), Picked(received, reject));
        if (accepted.Intersect(rejected, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } both)
            throw new McpException($"{both} is both accepted and rejected.");

        foreach (var file in accepted)
            File.Move(file, VerifiedOf(file), overwrite: true);
        foreach (var file in rejected)
            File.Delete(file);

        var pending = new List<PendingSnapshot>();
        foreach (var file in received.Except(accepted).Except(rejected))
        {
            var verified = VerifiedOf(file);
            pending.Add(new(file, verified, !File.Exists(verified), await DiffAsync(verified, file, cancellationToken)));
        }

        return new(pending, accepted, rejected);
    }

    /// <summary>The received files that a list names, by path, or all of them for '*'. Throws for a name that is not one of them.</summary>
    private static List<string> Picked(List<string> received, IReadOnlyList<string> names)
    {
        if (names.Contains("*"))
            return received;

        return [.. names.Select(name => received.Find(file => string.Equals(file, Path.GetFullPath(name), StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"{name} is not a received file that is pending; verify_pending without accept or reject lists them."))];
    }

    /// <summary>The verified file a received one would replace: Verify names them alike, Type.Method.received.txt and Type.Method.verified.txt.</summary>
    internal static string VerifiedOf(string received)
    {
        var name = Path.GetFileName(received);
        var at = name.LastIndexOf(".received.", StringComparison.OrdinalIgnoreCase);
        return Path.Combine(Path.GetDirectoryName(received)!, $"{name[..at]}.verified.{name[(at + ".received.".Length)..]}");
    }

    /// <summary>The unified diff from the verified file to the received one, as git diff --no-index makes it; a new snapshot's text.</summary>
    private static async Task<string> DiffAsync(string verified, string received, CancellationToken cancellationToken)
    {
        if (!File.Exists(verified))
        {
            var bytes = await File.ReadAllBytesAsync(received, cancellationToken);
            return bytes.Contains((byte)0) ? $"(binary, {bytes.Length} bytes)" : Cut(DotnetCli.Utf8.GetString(bytes));
        }

        try
        {
            // 1 when they differ.
            var (exitCode, output) = await DotnetCli.RunAsync("git", ["diff", "--no-index", "--no-color", "--no-ext-diff", "--", verified, received], TimeSpan.FromMinutes(1), cancellationToken);
            return exitCode is 0 or 1 ? Cut(output.Trim()) : $"(git diff failed: {output.Trim()})";
        }
        catch (McpException e)
        {
            return $"({e.Message})";
        }
    }

    // ponytail: a long diff is cut; read the files if all of it matters.
    private static string Cut(string text) => text.Length > MaxDiff ? text[..MaxDiff] + "\n..." : text;

    private const int MaxDiff = 4000;

    /// <summary>The files below a directory that match a pattern, outside build output, git's and editors' directories and node_modules.</summary>
    internal static IEnumerable<string> FilesBelow(string directory, string pattern)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(directory, pattern, options))
            yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
        {
            if (Path.GetFileName(child).ToLowerInvariant() is "bin" or "obj" or ".git" or ".vs" or ".idea" or "node_modules")
                continue;
            foreach (var file in FilesBelow(child, pattern))
                yield return file;
        }
    }

    /// <param name="Pending">The received files left, after those accepted and rejected.</param>
    /// <param name="Accepted">The received files this call made the verified ones.</param>
    /// <param name="Rejected">The received files this call deleted.</param>
    public sealed record PendingSnapshots(IReadOnlyList<PendingSnapshot> Pending, IReadOnlyList<string> Accepted, IReadOnlyList<string> Rejected);

    /// <param name="Verified">The file accepting it replaces.</param>
    /// <param name="New">Whether there is no verified file yet, as for a new test.</param>
    /// <param name="Diff">The unified diff from the verified file to the received one; for a new snapshot, the received text.</param>
    public sealed record PendingSnapshot(string Received, string Verified, bool New, string Diff);
}
