using System.Text;

namespace AdobeDownloader.Core.Tests;

public class FolderIconTransactionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FlashCreativeTests-" + Guid.NewGuid().ToString("N"));
    private string Folder => Path.Combine(root, "app");
    private string Icon => Path.Combine(Folder, "app.ico");
    private string Journal => Path.Combine(root, "journal");
    public FolderIconTransactionTests() { Directory.CreateDirectory(Folder); File.WriteAllBytes(Icon, [0, 0, 1, 0]); }
    [Fact] public async Task CreatesUnicodeIniAndRestoresOriginalAttributesIdempotently()
    {
        var before = File.GetAttributes(Folder);
        await FolderIconTransaction.ApplyAsync(Folder, Icon, Journal);
        var target = Path.Combine(Folder, "desktop.ini");
        var bytes = await File.ReadAllBytesAsync(target);
        Assert.Equal(Encoding.Unicode.GetPreamble(), bytes[..2]);
        Assert.Contains("IconFile=app.ico\r\nIconIndex=0", Encoding.Unicode.GetString(bytes));
        Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.Hidden | FileAttributes.System));
        Assert.True(File.GetAttributes(Folder).HasFlag(FileAttributes.ReadOnly));
        await FolderIconTransaction.RollbackAsync(Folder, Journal);
        Assert.False(File.Exists(target)); Assert.Equal(before, File.GetAttributes(Folder));
        await FolderIconTransaction.RollbackAsync(Folder, Journal);
    }
    [Fact] public async Task ExistingDesktopIniIsNeverReplaced()
    {
        var target = Path.Combine(Folder, "desktop.ini"); await File.WriteAllTextAsync(target, "user settings");
        await Assert.ThrowsAsync<IOException>(() => FolderIconTransaction.ApplyAsync(Folder, Icon, Journal));
        Assert.Equal("user settings", await File.ReadAllTextAsync(target)); Assert.False(Directory.Exists(Journal));
    }
    [Fact] public async Task RecoveryPreservesChangedContentAndFolderAttributes()
    {
        await FolderIconTransaction.ApplyAsync(Folder, Icon, Journal);
        var target = Path.Combine(Folder, "desktop.ini");
        File.SetAttributes(target, FileAttributes.Normal); await File.WriteAllTextAsync(target, "user edit");
        await Assert.ThrowsAsync<InvalidDataException>(() => FolderIconTransaction.RollbackAsync(Folder, Journal));
        Assert.Equal("user edit", await File.ReadAllTextAsync(target));
        Assert.True(File.GetAttributes(Folder).HasFlag(FileAttributes.ReadOnly));
    }
    [Fact] public async Task ChangedFolderAttributesBlockBeforeDeletingIni()
    {
        await FolderIconTransaction.ApplyAsync(Folder, Icon, Journal);
        File.SetAttributes(Folder, File.GetAttributes(Folder) | FileAttributes.Hidden);
        await Assert.ThrowsAsync<InvalidDataException>(() => FolderIconTransaction.RollbackAsync(Folder, Journal));
        Assert.True(File.Exists(Path.Combine(Folder, "desktop.ini")));
    }
    [Fact] public async Task PreparedJournalRecoversPartiallyAppliedAttributes()
    {
        var before = File.GetAttributes(Folder);
        await FolderIconTransaction.ApplyAsync(Folder, Icon, Journal);
        var path = Path.Combine(Journal, "icon.json"); var journal = await JsonFiles.ReadAsync<FolderIconJournal>(path);
        await JsonFiles.WriteAsync(path, journal with { State = "Prepared" });
        File.SetAttributes(Folder, before);
        await FolderIconTransaction.RollbackAsync(Folder, Journal);
        Assert.Equal(before, File.GetAttributes(Folder)); Assert.False(File.Exists(Path.Combine(Folder, "desktop.ini")));
    }
    [Fact] public async Task OutsideIconAndOverlappingJournalAreRejected()
    {
        var outside = Path.Combine(root, "outside.ico"); File.WriteAllBytes(outside, [0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => FolderIconTransaction.ApplyAsync(Folder, outside, Journal));
        await Assert.ThrowsAsync<InvalidDataException>(() => FolderIconTransaction.ApplyAsync(Folder, Icon, Path.Combine(Folder, "journal")));
    }
    [Fact] public async Task CancellationDoesNotChangeFolder()
    {
        var before = File.GetAttributes(Folder);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FolderIconTransaction.ApplyAsync(Folder, Icon, Journal, new CancellationToken(true)));
        Assert.Equal(before, File.GetAttributes(Folder)); Assert.False(File.Exists(Path.Combine(Folder, "desktop.ini")));
    }
    public void Dispose()
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Directory);
        Directory.Delete(root, true);
    }
}
