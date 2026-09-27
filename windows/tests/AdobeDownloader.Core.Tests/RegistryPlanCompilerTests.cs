using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security.Principal;
namespace AdobeDownloader.Core.Tests;

[SupportedOSPlatform("windows")]
public class RegistryPlanCompilerTests : IDisposable
{
    private readonly RegistryScope scope = new(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\FlashCreativeTests\" + Guid.NewGuid().ToString("N"));
    private readonly string root = Path.Combine(Path.GetTempPath(), "FlashCreativeTests-" + Guid.NewGuid().ToString("N"));
    public RegistryPlanCompilerTests() { Registry.CurrentUser.CreateSubKey(scope.Root).Dispose(); Directory.CreateDirectory(root); }
    private PlannedRegistryValue Value(string name, bool preference = false)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new("HKEY_CURRENT_USER", scope.Root, "Registry64", name, "REG_SZ", "installed", identity.User!.Value, preference, preference, preference);
    }
    [Fact] public async Task PreferencesPreserveExistingAndSurviveUninstallButRollbackRemovesNewDefaults()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(scope.Root, true)!) key.SetValue("existing", "user setting");
        var entries = RegistryPlanCompiler.Prepare([Value("existing", true), Value("new", true), Value("ordinary")], scope);
        Assert.Equal(2, entries.Count);
        var journal = Path.Combine(root, "journal");
        await RegistryTransaction.ApplyAsync(scope, entries, journal);
        using (var key = Registry.CurrentUser.OpenSubKey(scope.Root, true)!) key.SetValue("new", "user changed preference");
        await RegistryTransaction.UninstallAsync(scope, journal); await RegistryTransaction.UninstallAsync(scope, journal);
        Assert.Equal("user setting", RegistryTransaction.Read(scope, "", "existing")!.Text);
        Assert.Equal("user changed preference", RegistryTransaction.Read(scope, "", "new")!.Text);
        Assert.Null(RegistryTransaction.Read(scope, "", "ordinary"));
        var rollbackJournal = Path.Combine(root, "rollback");
        await RegistryTransaction.ApplyAsync(scope, RegistryPlanCompiler.Prepare([Value("rollback", true)], scope), rollbackJournal);
        await RegistryTransaction.RollbackAsync(scope, rollbackJournal);
        Assert.Null(RegistryTransaction.Read(scope, "", "rollback"));
    }
    [Fact] public void WrongIdentityScopeAndUnprotectedDeletionAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => RegistryPlanCompiler.Prepare([Value("x") with { OwnerSid = "S-1-0-0" }], scope));
        Assert.Throws<InvalidDataException>(() => RegistryPlanCompiler.Prepare([Value("x") with { Key = scope.Root + "outside" }], scope));
        Assert.Throws<InvalidDataException>(() => RegistryPlanCompiler.Prepare([Value("x") with { RecursiveDeleteRequested = true }], scope));
    }
    [Fact] public async Task UninstallRejectsUserEditsToOrdinaryValues()
    {
        var journal = Path.Combine(root, "journal");
        await RegistryTransaction.ApplyAsync(scope, RegistryPlanCompiler.Prepare([Value("ordinary")], scope), journal);
        using (var key = Registry.CurrentUser.OpenSubKey(scope.Root, true)!) key.SetValue("ordinary", "edited");
        await Assert.ThrowsAsync<InvalidDataException>(() => RegistryTransaction.UninstallAsync(scope, journal));
        Assert.Equal("edited", RegistryTransaction.Read(scope, "", "ordinary")!.Text);
    }
    public void Dispose() { Registry.CurrentUser.DeleteSubKeyTree(scope.Root); Directory.Delete(root, true); }
}
