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
    [Fact] public void TrustedSdkSignerIsBoundToWindowsTrustResult()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var path = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        using var embedded = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
        using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(embedded);
        var expected = certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
        using var lease = WindowsSignatureVerifier.VerifyAndHold(path, expected);
        Assert.Equal(certificate.Thumbprint, lease.Verification.CertificateThumbprint);
        Assert.Throws<InvalidDataException>(() => WindowsSignatureVerifier.Verify(path, "Deliberately incorrect test publisher"));
    }
}
