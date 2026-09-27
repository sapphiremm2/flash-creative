using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
namespace AdobeDownloader.Core.Tests;

[SupportedOSPlatform("windows")]
public class RegistryPermissionTransactionTests : IDisposable
{
    private readonly RegistryScope scope = new(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\FlashCreativeTests\" + Guid.NewGuid().ToString("N"));
    private readonly string root = Path.Combine(Path.GetTempPath(), "FlashCreativeTests-" + Guid.NewGuid().ToString("N"));
    public RegistryPermissionTransactionTests() { Registry.CurrentUser.CreateSubKey(scope.Root).Dispose(); Directory.CreateDirectory(root); }
    private string Dacl() { using var key = Registry.CurrentUser.OpenSubKey(scope.Root)!; return key.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access); }
    [Fact] public async Task AddsKeyOnlyReadAndRestoresExactDacl()
    {
        var before = Dacl(); var journal = Path.Combine(root, "journal");
        await RegistryPermissionTransaction.ApplyReadAsync(scope, journal);
        using (var key = Registry.CurrentUser.OpenSubKey(scope.Root)!)
        {
            var rules = key.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<RegistryAccessRule>();
            Assert.Contains(rules, r => r.IdentityReference.Value == "S-1-1-0" && r.AccessControlType == AccessControlType.Allow &&
                (r.RegistryRights & RegistryRights.ReadKey) == RegistryRights.ReadKey && r.InheritanceFlags == InheritanceFlags.None);
        }
        await RegistryPermissionTransaction.RollbackAsync(scope, journal); await RegistryPermissionTransaction.RollbackAsync(scope, journal);
        AssertRestored(before, Dacl());
    }
    [Fact] public async Task EditedPermissionsArePreserved()
    {
        var journal = Path.Combine(root, "journal"); await RegistryPermissionTransaction.ApplyReadAsync(scope, journal);
        using (var key = Registry.CurrentUser.OpenSubKey(scope.Root, true)!)
        {
            var acl = key.GetAccessControl();
            acl.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), RegistryRights.SetValue, AccessControlType.Allow));
            key.SetAccessControl(acl);
        }
        var changed = Dacl();
        await Assert.ThrowsAsync<InvalidDataException>(() => RegistryPermissionTransaction.RollbackAsync(scope, journal));
        Assert.Equal(changed, Dacl());
    }
    [Fact] public async Task PreparedJournalRecoveryRestoresPermissions()
    {
        var before = Dacl(); var journal = Path.Combine(root, "journal"); await RegistryPermissionTransaction.ApplyReadAsync(scope, journal);
        var path = Path.Combine(journal, "permission.json"); var record = await JsonFiles.ReadAsync<RegistryPermissionJournal>(path);
        await JsonFiles.WriteAsync(path, record with { State = "Prepared" });
        await RegistryPermissionTransaction.RollbackAsync(scope, journal); AssertRestored(before, Dacl());
    }
    private static void AssertRestored(string before, string after)
    {
        var old = new RawSecurityDescriptor(before); var restored = new RawSecurityDescriptor(after);
        Assert.Equal(old.ControlFlags & ControlFlags.DiscretionaryAclProtected, restored.ControlFlags & ControlFlags.DiscretionaryAclProtected);
        byte[] Bytes(RawAcl acl) { var bytes = new byte[acl.BinaryLength]; acl.GetBinaryForm(bytes, 0); return bytes; }
        Assert.Equal(Bytes(old.DiscretionaryAcl!), Bytes(restored.DiscretionaryAcl!));
    }
    public void Dispose() { Registry.CurrentUser.DeleteSubKeyTree(scope.Root); Directory.Delete(root, true); }
}
