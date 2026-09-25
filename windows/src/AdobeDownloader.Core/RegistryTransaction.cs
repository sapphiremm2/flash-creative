using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Text.Json;

namespace AdobeDownloader.Core;

public sealed record RegistryValueSnapshot(RegistryValueKind Kind, string? Text = null, byte[]? Bytes = null,
    string[]? Strings = null, long? Number = null);
public sealed record RegistryReplacement(string Key, string Name, RegistryValueSnapshot? Expected, RegistryValueSnapshot Value);
public sealed record RegistryScope(RegistryHive Hive, RegistryView View, string Root);
public sealed record RegistryUndoJournal(int Version, RegistryScope Scope, string State,
    IReadOnlyList<RegistryReplacement> Entries, IReadOnlyList<string> CreatedKeys);

/// <summary>Caller-owned registry subtree prototype. No elevation or untrusted-journal authorization.</summary>
[SupportedOSPlatform("windows")]
public static class RegistryTransaction
{
    public static RegistryValueSnapshot? Read(RegistryScope scope, string relativeKey, string name)
    {
        Validate(scope); Relative(relativeKey); Name(name);
        using var hive = RegistryKey.OpenBaseKey(scope.Hive, scope.View);
        using var key = hive.OpenSubKey(Full(scope, relativeKey));
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        var kind = key.GetValueKind(name);
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => new(kind, Text: (string)value!),
            RegistryValueKind.Binary or RegistryValueKind.None => new(kind, Bytes: (byte[])value!),
            RegistryValueKind.MultiString => new(kind, Strings: (string[])value!),
            RegistryValueKind.DWord => new(kind, Number: (int)value!),
            RegistryValueKind.QWord => new(kind, Number: (long)value!),
            _ => throw new InvalidDataException("Unsupported existing registry value type.")
        };
    }
    public static async Task ApplyAsync(RegistryScope scope, IReadOnlyList<RegistryReplacement> entries,
        string journalDirectory, CancellationToken ct = default)
    {
        Validate(scope); journalDirectory = Path.GetFullPath(journalDirectory);
        if (entries.Count is < 1 or > 10000 || Directory.Exists(journalDirectory) || File.Exists(journalDirectory))
            throw new InvalidDataException("A bounded transaction and a new journal directory are required.");
        using var transactionLock = Lock(scope, journalDirectory);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var hive = RegistryKey.OpenBaseKey(scope.Hive, scope.View);
        using (var root = hive.OpenSubKey(scope.Root, writable: true))
            if (root is null) throw new InvalidDataException("Caller-owned registry root must already exist.");
        long journalBytes = 1024;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested(); Relative(entry.Key); Name(entry.Name);
            _ = Value(entry.Value); if (entry.Expected is not null) _ = Value(entry.Expected);
            journalBytes += JsonSerializer.SerializeToUtf8Bytes(entry, JsonFiles.Options).Length;
            if (journalBytes > 16 * 1024 * 1024) throw new InvalidDataException("Registry journal exceeds its size limit.");
            if (!targets.Add(entry.Key + "\0" + entry.Name)) throw new InvalidDataException("Duplicate registry target.");
            if (!Equal(Read(scope, entry.Key, entry.Name), entry.Expected)) throw new InvalidDataException("Registry baseline mismatch.");
            var parts = entry.Key.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            for (var length = 1; length <= parts.Length; length++)
            {
                var relative = string.Join('\\', parts.Take(length));
                using var key = hive.OpenSubKey(Full(scope, relative));
                if (key is null && created.Add(relative))
                {
                    journalBytes += System.Text.Encoding.UTF8.GetByteCount(relative) * 2L + 16;
                    if (created.Count > 100000 || journalBytes > 16 * 1024 * 1024) throw new InvalidDataException("Registry journal structure limit exceeded.");
                }
            }
        }
        var journal = new RegistryUndoJournal(1, scope, "Prepared", entries, created.OrderBy(k => k.Count(c => c == '\\')).ToArray());
        // Also bounds serialized snapshots before creating registry state.
        if (JsonSerializer.SerializeToUtf8Bytes(journal, JsonFiles.Options).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Registry journal exceeds its size limit.");
        Directory.CreateDirectory(journalDirectory);
        var path = Path.Combine(journalDirectory, "registry-journal.json");
        await JsonFiles.WriteAsync(path, journal, overwrite: false, ct);
        try
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!Equal(Read(scope, entry.Key, entry.Name), entry.Expected)) throw new InvalidDataException("Registry changed after preparation.");
                using var key = hive.CreateSubKey(Full(scope, entry.Key), writable: true);
                key.SetValue(entry.Name, Value(entry.Value), entry.Value.Kind); key.Flush();
            }
            await JsonFiles.WriteAsync(path, journal with { State = "Committed" }, ct: ct);
        }
        catch { await Recover(scope, journalDirectory, CancellationToken.None); throw; }
    }
    public static async Task RollbackAsync(RegistryScope scope, string journalDirectory, CancellationToken ct = default)
    {
        Validate(scope); journalDirectory = Path.GetFullPath(journalDirectory);
        using var transactionLock = Lock(scope, journalDirectory);
        await Recover(scope, journalDirectory, ct);
    }
    private static async Task Recover(RegistryScope scope, string directory, CancellationToken ct)
    {
        var path = Path.Combine(directory, "registry-journal.json");
        var journal = await JsonFiles.ReadAsync<RegistryUndoJournal>(path, ct);
        if (journal.Version != 1 || journal.Scope.Hive != scope.Hive || journal.Scope.View != scope.View ||
            !journal.Scope.Root.Equals(scope.Root, StringComparison.OrdinalIgnoreCase) || journal.State is not ("Prepared" or "Committed" or "RolledBack") ||
            journal.Entries.Count is < 1 or > 10000 || journal.CreatedKeys.Count > 100000)
            throw new InvalidDataException("Unsupported or mismatched registry journal.");
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in journal.Entries)
        {
            Relative(entry.Key); Name(entry.Name); _ = Value(entry.Value); if (entry.Expected is not null) _ = Value(entry.Expected);
            if (!targets.Add(entry.Key + "\0" + entry.Name)) throw new InvalidDataException("Duplicate journal target.");
            var current = Read(scope, entry.Key, entry.Name);
            if (!Equal(current, entry.Expected) && !Equal(current, entry.Value)) throw new InvalidDataException("Registry rollback conflict; current value was preserved.");
        }
        foreach (var relative in journal.CreatedKeys)
        {
            Relative(relative);
            if (relative.Length == 0 || !journal.Entries.Any(e => e.Key.Equals(relative, StringComparison.OrdinalIgnoreCase) || e.Key.StartsWith(relative + "\\", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Invalid created-key journal entry.");
        }
        using var hive = RegistryKey.OpenBaseKey(scope.Hive, scope.View);
        foreach (var entry in journal.Entries.Reverse())
        {
            ct.ThrowIfCancellationRequested(); var current = Read(scope, entry.Key, entry.Name);
            if (Equal(current, entry.Expected)) continue;
            if (!Equal(current, entry.Value)) throw new InvalidDataException("Registry changed during rollback.");
            using var key = hive.OpenSubKey(Full(scope, entry.Key), writable: true) ?? throw new InvalidDataException("Registry target disappeared.");
            if (entry.Expected is null) key.DeleteValue(entry.Name, throwOnMissingValue: false);
            else key.SetValue(entry.Name, Value(entry.Expected), entry.Expected.Kind);
            key.Flush();
        }
        foreach (var relative in journal.CreatedKeys.OrderByDescending(k => k.Count(c => c == '\\')))
        {
            ct.ThrowIfCancellationRequested();
            using var key = hive.OpenSubKey(Full(scope, relative));
            // Preserve keys containing values/subkeys added by someone else; never recursive-delete.
            if (key is not null && key.ValueCount == 0 && key.SubKeyCount == 0) hive.DeleteSubKey(Full(scope, relative), throwOnMissingSubKey: false);
        }
        await JsonFiles.WriteAsync(path, journal with { State = "RolledBack" }, ct: ct);
    }
    private static object Value(RegistryValueSnapshot snapshot)
    {
        var slots = (snapshot.Text is null ? 0 : 1) + (snapshot.Bytes is null ? 0 : 1) + (snapshot.Strings is null ? 0 : 1) + (snapshot.Number is null ? 0 : 1);
        if (slots != 1) throw new InvalidDataException("Ambiguous registry value payload.");
        return snapshot.Kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString when snapshot.Text is { } text && text.Length <= 1024 * 1024 && !text.Contains('\0') => text,
            RegistryValueKind.Binary or RegistryValueKind.None when snapshot.Bytes is { Length: <= 1024 * 1024 } bytes => bytes,
            RegistryValueKind.MultiString when snapshot.Strings is { Length: <= 10000 } strings && strings.All(s => s is not null && !s.Contains('\0')) && strings.Sum(s => (long)s.Length) <= 1024 * 1024 => strings,
            RegistryValueKind.DWord when snapshot.Number is >= int.MinValue and <= int.MaxValue => (int)snapshot.Number.Value,
            RegistryValueKind.QWord when snapshot.Number is { } number => number,
            _ => throw new InvalidDataException("Unsupported or invalid registry value snapshot.")
        };
    }
    private static void Validate(RegistryScope scope)
    {
        if (scope.Hive is not (RegistryHive.CurrentUser or RegistryHive.LocalMachine) || scope.View is not (RegistryView.Registry32 or RegistryView.Registry64) ||
            !scope.Root.StartsWith("Software\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An explicit Software subtree and registry view are required.");
        Relative(scope.Root);
    }
    private static void Relative(string key)
    {
        if (key.Length > 2048 || key.Count(c => c == '\\') > 63 || key.Contains('\0') || (key.Length > 0 && key.Split('\\').Any(k => k.Length == 0 || k is "." or ".." || k.Length > 255)))
            throw new InvalidDataException("Invalid relative registry key.");
    }
    private static void Name(string name) { if (name.Length > 16383 || name.Contains('\0')) throw new InvalidDataException("Invalid registry value name."); }
    private static string Full(RegistryScope scope, string relative) => relative.Length == 0 ? scope.Root : scope.Root + "\\" + relative;
    private static bool Equal(RegistryValueSnapshot? a, RegistryValueSnapshot? b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    private static FileStream Lock(RegistryScope scope, string directory)
    {
        var parent = Path.GetDirectoryName(directory)!;
        if (!Directory.Exists(parent)) throw new InvalidDataException("Journal parent must already exist.");
        var identity = $"{scope.Hive}|{scope.View}|{scope.Root.ToUpperInvariant()}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        return new FileStream(Path.Combine(parent, ".flash-registry-" + hash + ".lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
    }
}
