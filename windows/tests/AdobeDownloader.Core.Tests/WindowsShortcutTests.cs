using System.Runtime.Versioning;
using System.Security.Cryptography;
namespace AdobeDownloader.Core.Tests;

[SupportedOSPlatform("windows")]
public class WindowsShortcutTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FlashCreativeTests-" + Guid.NewGuid().ToString("N"));
    public WindowsShortcutTests() => Directory.CreateDirectory(root);
    [Theory] [InlineData("App.exe")] [InlineData("Test space.exe")] [InlineData("application-\u00e9.exe")]
    public async Task StagesRealLinkAndRecoversPublishedLink(string name)
    {
        var target = Path.Combine(root, name); // Future installed target need not exist; never execute it.
        var artifact = await WindowsShortcut.StageAsync(target, Path.Combine(root, "staged.lnk"));
        Assert.Equal(target, artifact.Target);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(artifact.Path))), artifact.Sha256);
        var links = Directory.CreateDirectory(Path.Combine(root, "links")).FullName;
        var journal = Path.Combine(root, "journal");
        await FileTransaction.ApplyAsync(links, [new("App.lnk", artifact.Path, artifact.Sha256, null)], journal, 1024 * 1024);
        Assert.True(File.Exists(Path.Combine(links, "App.lnk")));
        await FileTransaction.RollbackAsync(links, journal); Assert.False(File.Exists(Path.Combine(links, "App.lnk")));
    }
    [Fact] public async Task ExistingDestinationAndCancellationPreserveFiles()
    {
        var link = Path.Combine(root, "existing.lnk"); await File.WriteAllTextAsync(link, "existing");
        await Assert.ThrowsAsync<IOException>(() => WindowsShortcut.StageAsync(Path.Combine(root, "app.exe"), link));
        Assert.Equal("existing", await File.ReadAllTextAsync(link));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsShortcut.StageAsync(Path.Combine(root, "app.exe"), Path.Combine(root, "new.lnk"), new CancellationToken(true)));
        Assert.False(File.Exists(Path.Combine(root, "new.lnk")));
    }
    [Theory] [InlineData("https://example.com")] [InlineData(@"C:\Apps\..\bad.exe")]
    [InlineData(@"C:\Apps\app.exe:stream")] [InlineData(@"\\server\share\app.exe")]
    public async Task RejectsNonlocalAndUnsafeTargets(string target) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => WindowsShortcut.StageAsync(target, Path.Combine(root, "app.lnk")));
    public void Dispose() => Directory.Delete(root, true);
}
