namespace AdobeDownloader.Core.Tests;

public sealed class SignatureTests
{
    [Fact] public void UnsignedFileIsRejectedOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not a signed executable");
            Assert.Throws<InvalidDataException>(() => WindowsSignatureVerifier.Verify(path, "Microsoft Corporation"));
        }
        finally { File.Delete(path); }
    }
}
