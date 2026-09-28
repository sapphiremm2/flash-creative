using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace AdobeDownloader.Core;

public sealed record InstallFileBatch(string Root, IReadOnlyList<string> Directories, IReadOnlyList<FileReplacement> Files, long MaxBytes);
public sealed record InstallRegistryBatch(RegistryScope Scope, IReadOnlyList<RegistryReplacement> Values);
public sealed record InstallationWork(IReadOnlyList<InstallFileBatch> FileBatches, IReadOnlyList<InstallRegistryBatch> RegistryBatches,
    IReadOnlyList<RegistryScope> Permissions, IReadOnlyList<PlannedFolderIcon> Icons);
public sealed record InstallationJournal(int Version, string WorkSha256, string State, int StartedSteps, int CompletedSteps);

/// <summary>Coordinates unprivileged new-file installation fixtures and trusted local child journals.
/// Not an elevated API: only current-user registry scopes, caller-owned folders, no executable launches.</summary>
[SupportedOSPlatform("windows")]
public static class InstallationTransaction
{
    public static Task ApplyAsync(InstallationWork work, string journalDirectory, CancellationToken ct = default) =>
        InstallationSessionGate.RunAsync(() => ApplyCore(work, journalDirectory, ct), ct);
    private static async Task ApplyCore(InstallationWork work, string journalDirectory, CancellationToken ct)
    {
        journalDirectory = Validate(work, journalDirectory);
        if (Directory.Exists(journalDirectory) || File.Exists(journalDirectory)) throw new IOException("Installation journal must be new.");
        using var sessionLock = Lock(journalDirectory);
        var steps = Steps(work, journalDirectory); var path = Path.Combine(journalDirectory, "installation.json");
        var journal = new InstallationJournal(1, Fingerprint(work), "Prepared", 0, 0);
        PrivateStorage.CreateNewDirectory(journalDirectory); await JsonFiles.WriteAsync(path, journal, overwrite: false, ct);
        try
        {
            for (var index = 0; index < steps.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                journal = journal with { State = "Applying", StartedSteps = index + 1 };
                await JsonFiles.WriteAsync(path, journal, ct: ct);
                await steps[index].Apply(ct);
                journal = journal with { CompletedSteps = index + 1 };
                await JsonFiles.WriteAsync(path, journal, ct: ct);
            }
            await JsonFiles.WriteAsync(path, journal with { State = "Committed" }, ct: ct);
        }
        catch { await Recover(work, journalDirectory, false, CancellationToken.None); throw; }
    }
    public static Task RollbackAsync(InstallationWork work, string journalDirectory, CancellationToken ct = default) => InstallationSessionGate.RunAsync(() => RecoverLocked(work, journalDirectory, false, ct), ct);
    public static Task UninstallAsync(InstallationWork work, string journalDirectory, CancellationToken ct = default) => InstallationSessionGate.RunAsync(() => RecoverLocked(work, journalDirectory, true, ct), ct);
    private static async Task RecoverLocked(InstallationWork work, string directory, bool uninstall, CancellationToken ct)
    {
        directory = Validate(work, directory); using var sessionLock = Lock(directory);
        await Recover(work, directory, uninstall, ct);
    }
    private static async Task Recover(InstallationWork work, string directory, bool uninstall, CancellationToken ct)
    {
        var path = Path.Combine(directory, "installation.json"); FileTransaction.NoLinks(path);
        var journal = await JsonFiles.ReadAsync<InstallationJournal>(path, ct); var steps = Steps(work, directory);
        if (journal.Version != 1 || journal.WorkSha256 != Fingerprint(work) || journal.State is not ("Prepared" or "Applying" or "Committed" or "RollingBack" or "RolledBack" or "Uninstalling" or "Uninstalled") ||
            journal.StartedSteps < 0 || journal.StartedSteps > steps.Count || journal.CompletedSteps < 0 || journal.CompletedSteps > journal.StartedSteps || journal.StartedSteps - journal.CompletedSteps > 1)
            throw new InvalidDataException("Installation journal does not match the expected work.");
        if (uninstall && journal.State is not ("Committed" or "Uninstalling" or "Uninstalled")) throw new InvalidDataException("Only a committed installation can be uninstalled.");
        if (journal.State is "RolledBack" or "Uninstalled") return;
        uninstall |= journal.State == "Uninstalling";
        await JsonFiles.WriteAsync(path, journal with { State = uninstall ? "Uninstalling" : "RollingBack" }, ct: ct);
        for (var index = journal.StartedSteps - 1; index >= 0; index--)
        {
            ct.ThrowIfCancellationRequested(); var step = steps[index]; FileTransaction.NoLinks(step.Journal);
            if (!File.Exists(step.Journal))
            {
                if (index < journal.CompletedSteps) throw new InvalidDataException("Completed installation step is missing its recovery journal.");
                continue; // Child operations publish a journal before their first target mutation.
            }
            var child = await JsonFiles.ReadAsync<JsonElement>(step.Journal, ct);
            if (child.TryGetProperty("State", out var state) && state.GetString() is "RolledBack" or "Uninstalled") continue;
            await step.Recover(uninstall, ct);
        }
        await JsonFiles.WriteAsync(path, journal with { State = uninstall ? "Uninstalled" : "RolledBack" }, ct: ct);
    }
    private sealed record Step(string Journal, Func<CancellationToken, Task> Apply, Func<bool, CancellationToken, Task> Recover);
    private static List<Step> Steps(InstallationWork work, string directory)
    {
        var steps = new List<Step>();
        string Next() => Path.Combine(directory, steps.Count.ToString("D4"));
        foreach (var batch in work.FileBatches)
        {
            var child = Next();
            steps.Add(new(Path.Combine(child, "directories.json"), ct => DirectoryTransaction.ApplyAsync(batch.Root, batch.Directories, child, ct),
                (_, ct) => DirectoryTransaction.RollbackAsync(batch.Root, child, ct)));
        }
        foreach (var batch in work.FileBatches.Where(b => b.Files.Count > 0))
        {
            var child = Next();
            steps.Add(new(Path.Combine(child, "journal.json"), ct => FileTransaction.ApplyAsync(batch.Root, batch.Files, child, batch.MaxBytes, ct),
                (_, ct) => FileTransaction.RollbackAsync(batch.Root, child, ct)));
        }
        foreach (var batch in work.RegistryBatches.Where(b => b.Values.Count > 0))
        {
            var child = Next();
            steps.Add(new(Path.Combine(child, "registry-journal.json"), ct => RegistryTransaction.ApplyAsync(batch.Scope, batch.Values, child, ct),
                (uninstall, ct) => uninstall ? RegistryTransaction.UninstallAsync(batch.Scope, child, ct) : RegistryTransaction.RollbackAsync(batch.Scope, child, ct)));
        }
        foreach (var scope in work.Permissions)
        {
            var child = Next();
            steps.Add(new(Path.Combine(child, "permission.json"), ct => RegistryPermissionTransaction.ApplyReadAsync(scope, child, ct),
                (_, ct) => RegistryPermissionTransaction.RollbackAsync(scope, child, ct)));
        }
        foreach (var icon in work.Icons)
        {
            var child = Next();
            steps.Add(new(Path.Combine(child, "icon.json"), ct => FolderIconTransaction.ApplyAsync(icon.FolderPath, icon.IconPath, child, ct),
                (_, ct) => FolderIconTransaction.RollbackAsync(icon.FolderPath, child, ct)));
        }
        return steps;
    }
    private static string Validate(InstallationWork work, string directory)
    {
        directory = Path.GetFullPath(directory); FileTransaction.NoLinks(directory);
        if (!Directory.Exists(Path.GetDirectoryName(directory))) throw new InvalidDataException("Journal parent must exist.");
        if (work.FileBatches.Count is < 1 or > 32 || work.RegistryBatches.Count > 32 || work.Permissions.Count > 128 || work.Icons.Count > 128)
            throw new InvalidDataException("Invalid installation operation counts.");
        var roots = new List<string>();
        foreach (var batch in work.FileBatches)
        {
            var root = FileTransaction.ValidateRoot(batch.Root);
            if (FileTransaction.Within(root, directory) || FileTransaction.Within(directory, root) || roots.Any(r => FileTransaction.Within(r, root) || FileTransaction.Within(root, r)))
                throw new InvalidDataException("File roots and journal must not overlap.");
            if (batch.Files.Any(f => f.ExpectedPreviousSha256 is not null)) throw new InvalidDataException("Coordinator currently supports new files only.");
            roots.Add(root);
        }
        foreach (var scope in work.RegistryBatches.Select(b => b.Scope).Concat(work.Permissions))
        {
            RegistryTransaction.Validate(scope);
            if (scope.Hive != RegistryHive.CurrentUser) throw new InvalidDataException("Coordinator has no machine registry or elevated execution boundary.");
        }
        foreach (var icon in work.Icons)
            if (!roots.Any(r => FileTransaction.Within(r, Path.GetFullPath(icon.FolderPath)))) throw new InvalidDataException("Icon is outside the owned installation roots.");
        return directory;
    }
    private static string Fingerprint(InstallationWork work) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(work, JsonFiles.Options)));
    private static FileStream Lock(string directory) => new(directory + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
}
