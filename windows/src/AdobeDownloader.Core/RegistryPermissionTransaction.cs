using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AdobeDownloader.Core;

public sealed record RegistryPermissionJournal(int Version, RegistryScope Scope, string State, string BeforeDacl, string AfterDacl);

/// <summary>Additive, key-only Everyone/ReadKey permission prototype with trusted local recovery journals.</summary>
[SupportedOSPlatform("windows")]
public static class RegistryPermissionTransaction
{
    public static async Task ApplyReadAsync(RegistryScope scope, string journalDirectory, CancellationToken ct = default)
    {
        RegistryTransaction.Validate(scope); journalDirectory = Path.GetFullPath(journalDirectory);
        if (Directory.Exists(journalDirectory) || File.Exists(journalDirectory)) throw new IOException("Journal must be new.");
        using var transactionLock = RegistryTransaction.Lock(scope, journalDirectory);
        using var hive = RegistryKey.OpenBaseKey(scope.Hive, scope.View);
        using var key = Open(hive, scope);
        var security = key.GetAccessControl(AccessControlSections.Access);
        if (!security.AreAccessRulesCanonical) throw new InvalidDataException("Noncanonical registry ACL is unsupported.");
        var before = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        // A null DACL already grants full access; adding a rule would narrow access unexpectedly.
        if (new RawSecurityDescriptor(before).DiscretionaryAcl is null) throw new InvalidDataException("Null registry DACL is unsupported.");
        security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), RegistryRights.ReadKey,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        var after = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var journal = new RegistryPermissionJournal(1, scope, "Prepared", before, after);
        Directory.CreateDirectory(journalDirectory);
        var path = Path.Combine(journalDirectory, "permission.json");
        await JsonFiles.WriteAsync(path, journal, overwrite: false, ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!Equivalent(Read(key), before)) throw new InvalidDataException("Registry ACL changed during preparation.");
            key.SetAccessControl(security); key.Flush();
            if (!Equivalent(Read(key), after)) throw new InvalidDataException("Registry ACL did not match the requested update.");
            await JsonFiles.WriteAsync(path, journal with { State = "Committed" }, ct: ct);
        }
        catch { await Recover(scope, journalDirectory, CancellationToken.None); throw; }
    }
    public static async Task RollbackAsync(RegistryScope scope, string journalDirectory, CancellationToken ct = default)
    {
        RegistryTransaction.Validate(scope); journalDirectory = Path.GetFullPath(journalDirectory);
        using var transactionLock = RegistryTransaction.Lock(scope, journalDirectory);
        await Recover(scope, journalDirectory, ct);
    }
    private static async Task Recover(RegistryScope scope, string directory, CancellationToken ct)
    {
        var path = Path.Combine(directory, "permission.json");
        var journal = await JsonFiles.ReadAsync<RegistryPermissionJournal>(path, ct);
        if (journal.Version != 1 || journal.Scope != scope || journal.State is not ("Prepared" or "Committed" or "RolledBack") ||
            journal.BeforeDacl.Length > 1024 * 1024 || journal.AfterDacl.Length > 1024 * 1024)
            throw new InvalidDataException("Invalid permission journal.");
        using var hive = RegistryKey.OpenBaseKey(scope.Hive, scope.View); using var key = Open(hive, scope);
        var current = Read(key);
        if (!Equivalent(current, journal.BeforeDacl) && !Equivalent(current, journal.AfterDacl)) throw new InvalidDataException("Registry ACL changed; preserving user permissions.");
        if (!Equivalent(current, journal.BeforeDacl))
        {
            var original = new RegistrySecurity(); original.SetSecurityDescriptorSddlForm(journal.BeforeDacl, AccessControlSections.Access);
            ct.ThrowIfCancellationRequested(); key.SetAccessControl(original); key.Flush();
            if (!Equivalent(Read(key), journal.BeforeDacl)) throw new InvalidDataException("Registry ACL restoration did not match the journal.");
        }
        await JsonFiles.WriteAsync(path, journal with { State = "RolledBack" }, ct: ct);
    }
    private static bool Equivalent(string left, string right)
    {
        // Windows marks a DACL auto-inherited after SetAccessControl, even when the ACEs are unchanged.
        // Ignore only that bookkeeping bit; protection, ACE order, identities, masks and inheritance must match.
        string Normalize(string text)
        {
            var descriptor = new RawSecurityDescriptor(text);
            var normalized = new RawSecurityDescriptor(descriptor.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited,
                descriptor.Owner, descriptor.Group, descriptor.SystemAcl, descriptor.DiscretionaryAcl);
            return normalized.GetSddlForm(AccessControlSections.Access);
        }
        return Normalize(left) == Normalize(right);
    }
    private static RegistryKey Open(RegistryKey hive, RegistryScope scope) => hive.OpenSubKey(scope.Root,
        RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadPermissions | RegistryRights.ChangePermissions)
        ?? throw new InvalidDataException("Owned permission target must already exist.");
    private static string Read(RegistryKey key) => key.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
}
