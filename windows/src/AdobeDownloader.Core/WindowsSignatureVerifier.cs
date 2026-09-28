using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace AdobeDownloader.Core;

public sealed record AuthenticodeResult(string Path, string Publisher, string Subject, string CertificateThumbprint);

public sealed class VerifiedExecutableLease : IDisposable
{
    internal LockedWindowsFile File { get; }
    public AuthenticodeResult Verification { get; }
    internal VerifiedExecutableLease(LockedWindowsFile file, AuthenticodeResult verification) { File = file; Verification = verification; }
    public void Dispose() => File.Dispose();
}

/// <summary>Verifies an embedded Windows signature. Does not execute files or verify ZIP archives.</summary>
public static class WindowsSignatureVerifier
{
    public static AuthenticodeResult Verify(string path, string expectedPublisher)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Authenticode verification requires Windows.");
        if (string.IsNullOrWhiteSpace(expectedPublisher)) throw new ArgumentException("An exact expected publisher is required.");
        using var lease = VerifyAndHold(path, expectedPublisher);
        return lease.Verification;
    }
    public static VerifiedExecutableLease VerifyAndHold(string path, string expectedPublisher)
    {
        if (string.IsNullOrWhiteSpace(expectedPublisher)) throw new ArgumentException("An exact expected publisher is required.");
        var file = LockedWindowsFile.Open(path);
        try { return new(file, VerifyLocked(file, expectedPublisher)); }
        catch { file.Dispose(); throw; }
    }
    private static AuthenticodeResult VerifyLocked(LockedWindowsFile locked, string expectedPublisher)
    {
        var path = locked.Path; var file = locked.Stream;
        var pathPointer = Marshal.StringToCoTaskMemUni(path);
        var infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1,
            File = infoPointer, StateAction = 1,
            // Check revocation for the chain except its trusted root; permit online retrieval.
            ProviderFlags = 0x80 | 0x2000, UiContext = 0
        };
        try
        {
            Marshal.StructureToPtr(new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(),
                Path = pathPointer, Handle = file.SafeFileHandle.DangerousGetHandle() }, infoPointer, false);
            var status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (status != 0) throw new InvalidDataException($"Windows signature verification failed (0x{unchecked((uint)status):X8}).");
            // Read the certificate from the successful trust evaluation itself, rather than independently
            // extracting a certificate from the file (which may contain multiple signatures).
            var provider = WTHelperProvDataFromStateData(data.StateData);
            var signerPointer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signerPointer == IntPtr.Zero) throw new InvalidDataException("Windows did not return a verified primary signer.");
            var signer = Marshal.PtrToStructure<ProviderSigner>(signerPointer);
            if (signer.Size < Marshal.SizeOf<ProviderSigner>() || signer.Error != 0 || signer.CertificateCount == 0 || signer.Certificates == IntPtr.Zero)
                throw new InvalidDataException("Windows returned an invalid verified signer chain.");
            var trusted = Marshal.PtrToStructure<ProviderCertificatePrefix>(signer.Certificates);
            if (trusted.Size < Marshal.SizeOf<ProviderCertificatePrefix>() || trusted.Certificate == IntPtr.Zero)
                throw new InvalidDataException("Windows did not return the verified signer certificate.");
            using var certificate = new X509Certificate2(trusted.Certificate);
            var publisher = certificate.GetNameInfo(X509NameType.SimpleName, false);
            if (!publisher.Equals(expectedPublisher, StringComparison.Ordinal))
                throw new InvalidDataException($"Signed publisher '{publisher}' differs from expected '{expectedPublisher}'.");
            return new AuthenticodeResult(path, publisher, certificate.Subject, certificate.Thumbprint);
        }
        finally
        {
            data.StateAction = 2;
            _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.FreeHGlobal(infoPointer);
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderSigner
    {
        public uint Size, VerifyTimeLow, VerifyTimeHigh, CertificateCount;
        public IntPtr Certificates;
        public uint SignerType;
        public IntPtr SignerInfo;
        public uint Error, CounterSignerCount;
        public IntPtr CounterSigners, ChainContext;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificatePrefix { public uint Size; public IntPtr Certificate; }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo { public uint Size; public IntPtr Path, Handle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallback, SipClient;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
