using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
namespace AdobeDownloader.Core.Tests;

[SupportedOSPlatform("windows")]
public class PrivateStorageTests
{
    [Fact] public void JournalsHaveProtectedUserSystemAdministratorAclAndDoNotAdoptExistingFolders()
    {
        var path = Path.Combine(Path.GetTempPath(), "FlashCreativeTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrivateStorage.CreateNewDirectory(path);
            using var identity = WindowsIdentity.GetCurrent();
            var acl = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            Assert.True(acl.AreAccessRulesProtected); Assert.Equal(identity.User, acl.GetOwner(typeof(SecurityIdentifier)));
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            Assert.All(rules, r => Assert.Contains(r.IdentityReference.Value, new[] { identity.User!.Value, "S-1-5-18", "S-1-5-32-544" }));
            var file = Path.Combine(path, "journal.json"); File.WriteAllText(file, "private");
            Assert.All(new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
                r => Assert.Contains(r.IdentityReference.Value, new[] { identity.User!.Value, "S-1-5-18", "S-1-5-32-544" }));
            Assert.Throws<IOException>(() => PrivateStorage.CreateNewDirectory(path)); Assert.Equal("private", File.ReadAllText(file));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
