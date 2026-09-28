namespace AdobeDownloader.Core.Tests;
public class LockedWindowsFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FlashCreativeTests-" + Guid.NewGuid().ToString("N"));
    public LockedWindowsFileTests() => Directory.CreateDirectory(root);
    [Fact] public void LeasePreventsFileReplacementAndAncestorRenameUntilDisposed()
    {
        var parent = Directory.CreateDirectory(Path.Combine(root, "parent")).FullName; var path = Path.Combine(parent, "file"); File.WriteAllText(path, "data");
        using (var lease = LockedWindowsFile.Open(path))
        {
            Assert.Equal(4, lease.Stream.Length);
            Assert.Throws<IOException>(() => File.WriteAllText(path, "changed"));
            Assert.Throws<IOException>(() => File.Delete(path));
            Assert.Throws<IOException>(() => Directory.Move(parent, Path.Combine(root, "renamed")));
        }
        File.WriteAllText(path, "changed"); Directory.Move(parent, Path.Combine(root, "renamed"));
    }
    [Fact] public void UnsignedFailureReleasesAllHandles()
    {
        var path = Path.Combine(root, "unsigned.exe"); File.WriteAllText(path, "not an executable");
        Assert.Throws<InvalidDataException>(() => WindowsSignatureVerifier.VerifyAndHold(path, "Microsoft Corporation"));
        File.Delete(path); Directory.Move(root, root + "-moved"); Directory.Move(root + "-moved", root);
    }
    public void Dispose() => Directory.Delete(root, true);
}
