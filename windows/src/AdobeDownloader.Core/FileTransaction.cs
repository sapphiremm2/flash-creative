using System.Security.Cryptography;

namespace AdobeDownloader.Core;

public sealed record FileReplacement(string RelativePath, string Source, string Sha256, string? ExpectedPreviousSha256);
public sealed record FileUndoEntry(string RelativePath, string NewSha256, string? OldSha256, string Backup);
public sealed record FileUndoJournal(int Version, string Root, string State, IReadOnlyList<FileUndoEntry> Entries);

/// <summary>Unprivileged file-transaction foundation. Caller must authenticate its plan and own both directories.
/// Parent directories must already exist. Not an elevated installation API or a registry transaction.</summary>
public static class FileTransaction
{
    public static async Task ApplyAsync(string root, IReadOnlyList<FileReplacement> replacements, string journalDirectory,
        long maxBytes, CancellationToken ct = default)
    {
        root = ValidateRoot(root); journalDirectory = Path.GetFullPath(journalDirectory);
        if (maxBytes <= 0 || replacements.Count is < 1 or > 10000) throw new InvalidDataException("Invalid transaction limits.");
        if (Directory.Exists(journalDirectory) || File.Exists(journalDirectory)) throw new IOException("Journal directory must be new.");
        if (Within(root, journalDirectory) || Within(journalDirectory, root)) throw new InvalidDataException("Journal and target must not overlap.");
        NoLinks(Path.GetDirectoryName(journalDirectory)!);
        using var transactionLock = Lock(root);
        var paths = replacements.Select(r => Target(root, r.RelativePath)).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw new InvalidDataException("Duplicate transaction targets.");
        foreach (var replacement in replacements)
        { HashFormat(replacement.Sha256); if (replacement.ExpectedPreviousSha256 is not null) HashFormat(replacement.ExpectedPreviousSha256); }
        Directory.CreateDirectory(journalDirectory);
        var entries = new List<FileUndoEntry>(); var journalPath = Path.Combine(journalDirectory, "journal.json");
        long used = 0;
        // Complete and flush every backup and replacement before publishing a recoverable journal.
        for (var index = 0; index < replacements.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var replacement = replacements[index]; var path = paths[index]; NoLinks(path); NoLinks(replacement.Source);
            var oldHash = File.Exists(path) ? await Digest(path, ct) : null;
            if (!Equal(oldHash, replacement.ExpectedPreviousSha256)) throw new InvalidDataException("Target differs from the expected baseline.");
            var backup = index + ".old";
            if (oldHash is not null) await CopyVerified(path, Path.Combine(journalDirectory, backup), oldHash);
            await CopyVerified(replacement.Source, Path.Combine(journalDirectory, index + ".new"), replacement.Sha256);
            entries.Add(new(replacement.RelativePath, replacement.Sha256.ToLowerInvariant(), oldHash, backup));
        }
        var journal = new FileUndoJournal(1, root, "Prepared", entries);
        await JsonFiles.WriteAsync(journalPath, journal, overwrite: false, ct);
        try
        {
            for (var index = 0; index < entries.Count; index++)
            {
                ct.ThrowIfCancellationRequested(); var entry = entries[index]; var path = Target(root, entry.RelativePath);
                NoLinks(path);
                if (!Equal(File.Exists(path) ? await Digest(path, ct) : null, entry.OldSha256)) throw new InvalidDataException("Target changed after transaction preparation.");
                await Publish(Path.Combine(journalDirectory, index + ".new"), path, entry.NewSha256, ct);
            }
            await JsonFiles.WriteAsync(journalPath, journal with { State = "Committed" }, ct: ct);
        }
        catch
        {
            await RollbackCore(root, journalDirectory, CancellationToken.None);
            throw;
        }
        async Task CopyVerified(string source, string destination, string expected)
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            used = checked(used + input.Length); if (used > maxBytes) throw new InvalidDataException("Transaction storage budget exceeded.");
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, ct); await output.FlushAsync(ct); output.Flush(true);
            input.Position = 0;
            if (!Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Transaction source hash mismatch.");
        }
    }

    public static async Task RollbackAsync(string root, string journalDirectory, CancellationToken ct = default)
    {
        root = ValidateRoot(root);
        using var transactionLock = Lock(root);
        await RollbackCore(root, journalDirectory, ct);
    }
    private static async Task RollbackCore(string root, string journalDirectory, CancellationToken ct)
    {
        journalDirectory = Path.GetFullPath(journalDirectory); NoLinks(journalDirectory);
        if (Within(root, journalDirectory) || Within(journalDirectory, root)) throw new InvalidDataException("Journal and target must not overlap.");
        var journalPath = Path.Combine(journalDirectory, "journal.json"); NoLinks(journalPath);
        var journal = await JsonFiles.ReadAsync<FileUndoJournal>(journalPath, ct);
        if (journal.Version != 1 || !root.Equals(journal.Root, StringComparison.OrdinalIgnoreCase) || journal.State is not ("Prepared" or "Committed" or "RolledBack") || journal.Entries.Count is < 1 or > 10000)
            throw new InvalidDataException("Unsupported or mismatched transaction journal.");
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Validate ALL backups and current files before changing any target during rollback.
        foreach (var entry in journal.Entries)
        {
            var path = Target(root, entry.RelativePath); NoLinks(path); HashFormat(entry.NewSha256);
            if (!targets.Add(path)) throw new InvalidDataException("Duplicate journal targets.");
            PackageDownloader.ValidateFileName(entry.Backup);
            if (entry.OldSha256 is not null)
            {
                HashFormat(entry.OldSha256); var backup = Path.Combine(journalDirectory, entry.Backup); NoLinks(backup);
                if (!Equal(await Digest(backup, ct), entry.OldSha256)) throw new InvalidDataException("Rollback backup is corrupt.");
            }
            var current = File.Exists(path) ? await Digest(path, ct) : null;
            if (!Equal(current, entry.OldSha256) && !Equal(current, entry.NewSha256)) throw new InvalidDataException("Rollback conflict: target changed; user data was preserved.");
        }
        foreach (var entry in journal.Entries.Reverse())
        {
            ct.ThrowIfCancellationRequested(); var path = Target(root, entry.RelativePath); NoLinks(path);
            var current = File.Exists(path) ? await Digest(path, ct) : null;
            if (Equal(current, entry.OldSha256)) continue;
            if (!Equal(current, entry.NewSha256)) throw new InvalidDataException("Rollback conflict: target changed during recovery.");
            if (entry.OldSha256 is null) File.Delete(path);
            else await Publish(Path.Combine(journalDirectory, entry.Backup), path, entry.OldSha256, ct);
        }
        await JsonFiles.WriteAsync(journalPath, journal with { State = "RolledBack" }, ct: ct);
    }
    private static async Task Publish(string source, string path, string hash, CancellationToken ct)
    {
        NoLinks(source); NoLinks(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".flash-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output, ct); await output.FlushAsync(ct); output.Flush(true);
            }
            if (!Equal(await Digest(temporary, ct), hash)) throw new InvalidDataException("Prepared transaction content changed.");
            ct.ThrowIfCancellationRequested(); NoLinks(path); File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string ValidateRoot(string root)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root) || root.Length <= 3) throw new InvalidDataException("Transaction root must be an existing directory below a drive root.");
        NoLinks(root); return root;
    }
    private static string Target(string root, string relative)
    {
        foreach (var component in relative.Replace('\\', '/').Split('/')) PackageDownloader.ValidateFileName(component);
        if (relative.Equals(".flash-transaction.lock", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Reserved transaction path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(root, path) || path == root || !Directory.Exists(Path.GetDirectoryName(path))) throw new InvalidDataException("Target parent must already exist inside transaction root.");
        return path;
    }
    private static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Transaction paths cannot contain reparse points."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    private static FileStream Lock(string root) => new(Path.Combine(root, ".flash-transaction.lock"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
    private static bool Within(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void HashFormat(string hash)
    { if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Expected SHA-256."); }
    private static bool Equal(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static async Task<string> Digest(string path, CancellationToken ct)
    { await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant(); }
}
