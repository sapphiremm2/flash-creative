using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security.Cryptography;
namespace AdobeDownloader.Core.Tests;

[SupportedOSPlatform("windows")]
public class InstallationTransactionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FlashCreativeTests-" + Guid.NewGuid().ToString("N"));
    private readonly RegistryScope scope = new(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\FlashCreativeTests\" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(root, "target");
    private string Journal => Path.Combine(root, "journal");
    public InstallationTransactionTests() { Directory.CreateDirectory(Target); Registry.CurrentUser.CreateSubKey(scope.Root).Dispose(); }
    private async Task<InstallationWork> Work()
    {
        var source = Path.Combine(root, "source.ico"); await File.WriteAllBytesAsync(source, [0, 0, 1, 0]);
        var link = await WindowsShortcut.StageAsync(Path.Combine(Target, "app", "app.exe"), Path.Combine(root, "staged.lnk"));
        return new([new(Target, ["app", "links"], [new(@"app\app.ico", source, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))), null),
            new(@"links\App.lnk", link.Path, link.Sha256, null)], 1024 * 1024)],
            [new(scope, [new("", "ordinary", null, new(RegistryValueKind.String, Text: "installed")), new("", "preference", null, new(RegistryValueKind.String, Text: "default"), true)])],
            [scope], [new(Path.Combine(Target, "app"), Path.Combine(Target, "app", "app.ico"))]);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CoordinatedRollbackAndUninstallReverseAllOperations(bool uninstall)
    {
        var work = await Work(); await InstallationTransaction.ApplyAsync(work, Journal);
        Assert.True(File.Exists(Path.Combine(Target, "app", "desktop.ini"))); Assert.NotNull(RegistryTransaction.Read(scope, "", "ordinary"));
        if (uninstall) { await InstallationTransaction.UninstallAsync(work, Journal); await InstallationTransaction.UninstallAsync(work, Journal); }
        else { await InstallationTransaction.RollbackAsync(work, Journal); await InstallationTransaction.RollbackAsync(work, Journal); }
        Assert.Empty(Directory.EnumerateFileSystemEntries(Target)); Assert.Null(RegistryTransaction.Read(scope, "", "ordinary"));
        Assert.Equal(uninstall, RegistryTransaction.Read(scope, "", "preference") is not null);
    }
    [Fact] public async Task LateFailureRollsBackEarlierFilesAndDirectories()
    {
        var work = await Work(); using (var key = Registry.CurrentUser.OpenSubKey(scope.Root, true)!) key.SetValue("ordinary", "unexpected");
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallationTransaction.ApplyAsync(work, Journal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Target)); Assert.Equal("unexpected", RegistryTransaction.Read(scope, "", "ordinary")!.Text);
        Assert.Equal("RolledBack", (await JsonFiles.ReadAsync<InstallationJournal>(Path.Combine(Journal, "installation.json"))).State);
    }
    [Fact] public async Task InterruptedParentRecoverySkipsAlreadyRecoveredChildren()
    {
        var work = await Work(); await InstallationTransaction.ApplyAsync(work, Journal); await InstallationTransaction.RollbackAsync(work, Journal);
        var path = Path.Combine(Journal, "installation.json"); var journal = await JsonFiles.ReadAsync<InstallationJournal>(path);
        await JsonFiles.WriteAsync(path, journal with { State = "RollingBack" });
        await InstallationTransaction.RollbackAsync(work, Journal); Assert.Empty(Directory.EnumerateFileSystemEntries(Target));
    }
    [Fact] public async Task DifferentRecoveryWorkAndMissingCompletedJournalsAreRejected()
    {
        var work = await Work(); await InstallationTransaction.ApplyAsync(work, Journal);
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallationTransaction.RollbackAsync(work with { Permissions = [] }, Journal));
        var child = Path.Combine(Journal, "0004", "icon.json"); File.Move(child, child + ".saved");
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallationTransaction.RollbackAsync(work, Journal));
        File.Move(child + ".saved", child); await InstallationTransaction.RollbackAsync(work, Journal);
    }
    [Theory] [InlineData("Committed")] [InlineData("Prepared")]
    public async Task ContradictoryParentStateCannotTriggerRecovery(string state)
    {
        var work = await Work(); await InstallationTransaction.ApplyAsync(work, Journal);
        var path = Path.Combine(Journal, "installation.json"); var journal = await JsonFiles.ReadAsync<InstallationJournal>(path);
        await JsonFiles.WriteAsync(path, journal with { State = state, StartedSteps = 1, CompletedSteps = 1 });
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallationTransaction.RollbackAsync(work, Journal));
        Assert.True(File.Exists(Path.Combine(Target, "app", "desktop.ini"))); Assert.NotNull(RegistryTransaction.Read(scope, "", "ordinary"));
        await JsonFiles.WriteAsync(path, journal); await InstallationTransaction.RollbackAsync(work, Journal);
    }
    [Fact] public async Task MissingEarlyChildIsDetectedBeforeAnyReverseStepRuns()
    {
        var work = await Work(); await InstallationTransaction.ApplyAsync(work, Journal);
        var path = Path.Combine(Journal, "0000", "directories.json"); File.Move(path, path + ".saved");
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallationTransaction.RollbackAsync(work, Journal));
        Assert.True(File.Exists(Path.Combine(Target, "app", "desktop.ini"))); Assert.NotNull(RegistryTransaction.Read(scope, "", "ordinary"));
        File.Move(path + ".saved", path); await InstallationTransaction.RollbackAsync(work, Journal);
    }
    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(scope.Root);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)) File.SetAttributes(directory, FileAttributes.Directory);
        Directory.Delete(root, true);
    }
}
