using System.Security.Cryptography;
using System.Text;

namespace AdobeDownloader.Core;

public sealed record FolderIconJournal(int Version, string Folder, string State, FileAttributes OriginalAttributes, string ContentSha256);

/// <summary>Unprivileged, caller-owned folder prototype. Trusted local journal; not an elevated boundary.
/// Existing desktop.ini files are preserved by refusing to replace them.</summary>
public static class FolderIconTransaction
{
    public static async Task ApplyAsync(string folder, string icon, string journalDirectory, CancellationToken ct = default)
    {
        folder = ValidateFolder(folder); icon = Path.GetFullPath(icon);
        NoLinks(icon);
        if (!File.Exists(icon) || !icon.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
            icon.Any(c => char.IsControl(c) || c is '"' or '[' or ']'))
            throw new InvalidDataException("Expected an existing ICO path without INI control characters.");
        var relative = Path.GetRelativePath(folder, icon);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Icon must be inside the owned folder.");
        journalDirectory = ValidateJournal(folder, journalDirectory);
        using var transactionLock = Lock(folder);
        var target = Path.Combine(folder, "desktop.ini"); NoLinks(target);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Existing desktop.ini must be preserved; merging is not implemented.");
        if (Directory.Exists(journalDirectory) || File.Exists(journalDirectory)) throw new IOException("Journal directory must be new.");
        ct.ThrowIfCancellationRequested();
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("[.ShellClassInfo]\r\nIconFile=" + relative + "\r\nIconIndex=0\r\n")).ToArray();
        var journal = new FolderIconJournal(1, folder, "Prepared", File.GetAttributes(folder), Convert.ToHexString(SHA256.HashData(bytes)));
        PrivateStorage.CreateNewDirectory(journalDirectory);
        var prepared = Path.Combine(journalDirectory, "desktop.ini.new");
        await using (var output = new FileStream(prepared, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { await output.WriteAsync(bytes, ct); await output.FlushAsync(ct); output.Flush(true); }
        await JsonFiles.WriteAsync(Path.Combine(journalDirectory, "icon.json"), journal, overwrite: false, ct);
        var published = false;
        try
        {
            ct.ThrowIfCancellationRequested(); NoLinks(target);
            // Publish complete content without an overwrite window. Never adopt a pre-existing INI.
            var temporary = Path.Combine(folder, ".flash-icon-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { await output.WriteAsync(bytes, ct); await output.FlushAsync(ct); output.Flush(true); }
                ct.ThrowIfCancellationRequested(); File.Move(temporary, target, overwrite: false); published = true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            File.SetAttributes(target, FileAttributes.Hidden | FileAttributes.System);
            File.SetAttributes(folder, journal.OriginalAttributes | FileAttributes.ReadOnly);
            await JsonFiles.WriteAsync(Path.Combine(journalDirectory, "icon.json"), journal with { State = "Committed" }, ct: ct);
        }
        catch { if (published) await RollbackCore(folder, journalDirectory, CancellationToken.None); throw; }
    }

    public static async Task RollbackAsync(string folder, string journalDirectory, CancellationToken ct = default)
    {
        folder = ValidateFolder(folder); journalDirectory = ValidateJournal(folder, journalDirectory);
        using var transactionLock = Lock(folder);
        await RollbackCore(folder, journalDirectory, ct);
    }

    private static async Task RollbackCore(string folder, string journalDirectory, CancellationToken ct)
    {
        var journalPath = Path.Combine(journalDirectory, "icon.json"); NoLinks(journalPath);
        var journal = await JsonFiles.ReadAsync<FolderIconJournal>(journalPath, ct);
        if (journal.Version != 1 || !folder.Equals(journal.Folder, StringComparison.OrdinalIgnoreCase) ||
            journal.State is not ("Prepared" or "Committed" or "RolledBack") || journal.ContentSha256.Length != 64 ||
            !journal.ContentSha256.All(Uri.IsHexDigit) || (journal.OriginalAttributes & FileAttributes.Directory) == 0 ||
            (journal.OriginalAttributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid folder icon journal.");
        var attributes = File.GetAttributes(folder);
        if (attributes != journal.OriginalAttributes && attributes != (journal.OriginalAttributes | FileAttributes.ReadOnly))
            throw new InvalidDataException("Folder attributes changed; preserving user changes.");
        var target = Path.Combine(folder, "desktop.ini"); NoLinks(target);
        if (Directory.Exists(target)) throw new InvalidDataException("desktop.ini became a directory.");
        if (File.Exists(target))
        {
            await using (var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(journal.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("desktop.ini changed; preserving user data.");
            var fileAttributes = File.GetAttributes(target);
            if ((fileAttributes & ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.Normal)) != 0)
                throw new InvalidDataException("desktop.ini attributes changed; preserving user data.");
            ct.ThrowIfCancellationRequested(); File.Delete(target);
        }
        File.SetAttributes(folder, journal.OriginalAttributes);
        await JsonFiles.WriteAsync(journalPath, journal with { State = "RolledBack" }, ct: ct);
    }
    private static string ValidateFolder(string folder)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        folder = Path.GetFullPath(folder).TrimEnd('\\'); NoLinks(folder);
        if (folder.Length <= 3 || !Directory.Exists(folder)) throw new InvalidDataException("Expected an owned directory below a drive root.");
        return folder;
    }
    private static string ValidateJournal(string folder, string journal)
    {
        journal = Path.GetFullPath(journal).TrimEnd('\\'); NoLinks(journal);
        if (folder.Equals(journal, StringComparison.OrdinalIgnoreCase) || journal.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase) || folder.StartsWith(journal + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Journal and target cannot overlap.");
        return journal;
    }
    private static void NoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse points are not supported."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    private static FileStream Lock(string folder) => new(Path.Combine(folder, ".flash-transaction.lock"), FileMode.CreateNew,
        FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
}
