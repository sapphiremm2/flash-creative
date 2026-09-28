using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AdobeDownloader.Core;

/// <summary>Private storage for trusted, unprivileged journals. Does not authenticate their contents.
/// The current user remains able to modify them; never use this as an elevated trust boundary.</summary>
public static class PrivateStorage
{
    public static void CreateNewDirectory(string path)
    {
        path = Path.GetFullPath(path); FileTransaction.NoLinks(path);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Private storage must be new.");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new InvalidDataException("Private storage parent must already exist.");
        if (OperatingSystem.IsWindows()) CreateWindows(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    [SupportedOSPlatform("windows")]
    private static void CreateWindows(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidDataException("Storage owner is unavailable.");
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); security.SetOwner(owner);
        foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
        FileTransaction.NoLinks(path);
        var actual = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!actual.AreAccessRulesProtected || !Equals(actual.GetOwner(typeof(SecurityIdentifier)), owner))
            throw new InvalidDataException("Private storage owner or protection differs from the requested policy.");
        var allowed = new HashSet<string> { owner.Value, "S-1-5-18", "S-1-5-32-544" };
        if (actual.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(rule =>
            rule.AccessControlType == AccessControlType.Allow && !allowed.Contains(rule.IdentityReference.Value)))
            throw new InvalidDataException("Private storage grants unexpected access.");
    }
}

