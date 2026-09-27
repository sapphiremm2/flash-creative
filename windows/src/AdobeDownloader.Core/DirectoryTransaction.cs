namespace AdobeDownloader.Core;

public sealed record DirectoryUndoJournal(int Version, string Root, string State, IReadOnlyList<string> Created);

/// <summary>Creates directories below an owned root. Recovery removes only recorded empty directories.</summary>
public static class DirectoryTransaction
{
    public static async Task ApplyAsync(string root, IReadOnlyList<string> directories, string journalDirectory, CancellationToken ct = default)
    {
        root = FileTransaction.ValidateRoot(root); journalDirectory = Journal(root, journalDirectory);
        if (directories.Count > 20000) throw new InvalidDataException("Directory limit exceeded.");
        if (Directory.Exists(journalDirectory) || File.Exists(journalDirectory)) throw new IOException("Journal must be new.");
        using var transactionLock = FileTransaction.Lock(root);
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in directories)
        {
            var path = Target(root, relative);
            for (var current = path; current != root; current = Path.GetDirectoryName(current)!)
            {
                FileTransaction.NoLinks(current);
                if (File.Exists(current)) throw new InvalidDataException("File occupies a required directory.");
                if (Directory.Exists(current)) break;
                created.Add(Path.GetRelativePath(root, current));
                if (created.Count > 20000) throw new InvalidDataException("Expanded directory limit exceeded.");
            }
        }
        var journal = new DirectoryUndoJournal(1, root, "Prepared", created.OrderBy(p => p.Count(c => c == '\\')).ToArray());
        Directory.CreateDirectory(journalDirectory);
        var journalPath = Path.Combine(journalDirectory, "directories.json");
        await JsonFiles.WriteAsync(journalPath, journal, overwrite: false, ct);
        try
        {
            foreach (var relative in journal.Created)
            {
                ct.ThrowIfCancellationRequested(); var path = Target(root, relative); FileTransaction.NoLinks(path);
                if (Directory.Exists(path) || File.Exists(path)) throw new InvalidDataException("Directory target changed after preparation.");
                Directory.CreateDirectory(path);
            }
            await JsonFiles.WriteAsync(journalPath, journal with { State = "Committed" }, ct: ct);
        }
        catch { await Recover(root, journalDirectory, CancellationToken.None); throw; }
    }
    public static async Task RollbackAsync(string root, string journalDirectory, CancellationToken ct = default)
    {
        root = FileTransaction.ValidateRoot(root); journalDirectory = Journal(root, journalDirectory);
        using var transactionLock = FileTransaction.Lock(root);
        await Recover(root, journalDirectory, ct);
    }
    private static async Task Recover(string root, string directory, CancellationToken ct)
    {
        var path = Path.Combine(directory, "directories.json"); FileTransaction.NoLinks(path);
        var journal = await JsonFiles.ReadAsync<DirectoryUndoJournal>(path, ct);
        if (journal.Version != 1 || !root.Equals(journal.Root, StringComparison.OrdinalIgnoreCase) || journal.State is not ("Prepared" or "Committed" or "RolledBack") ||
            journal.Created.Count > 20000 || journal.Created.Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Created.Count)
            throw new InvalidDataException("Invalid directory journal.");
        foreach (var relative in journal.Created)
        { var target = Target(root, relative); FileTransaction.NoLinks(target); if (File.Exists(target)) throw new InvalidDataException("Directory replaced by a file; preserving user data."); }
        foreach (var relative in journal.Created.OrderByDescending(p => p.Count(c => c == '\\')))
        {
            ct.ThrowIfCancellationRequested(); var target = Target(root, relative); FileTransaction.NoLinks(target);
            if (Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any()) Directory.Delete(target, recursive: false);
        }
        await JsonFiles.WriteAsync(path, journal with { State = "RolledBack" }, ct: ct);
    }
    private static string Journal(string root, string journal)
    {
        journal = Path.GetFullPath(journal); FileTransaction.NoLinks(journal);
        if (FileTransaction.Within(root, journal) || FileTransaction.Within(journal, root)) throw new InvalidDataException("Journal and target overlap.");
        return journal;
    }
    private static string Target(string root, string relative)
    {
        if (relative.Length > 32000) throw new InvalidDataException("Directory path exceeds limit.");
        foreach (var part in relative.Replace('/', '\\').Split('\\')) PackageDownloader.ValidateFileName(part);
        if (relative.Equals(".flash-transaction.lock", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Reserved transaction path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase) || !FileTransaction.Within(root, path)) throw new InvalidDataException("Directory escapes owned root.");
        return path;
    }
}
