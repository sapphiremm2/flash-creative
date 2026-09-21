using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace AdobeDownloader.Core;

public sealed record AuthenticodeResult(string Path, string Publisher, string Subject, string CertificateThumbprint);

/// <summary>Verifies an embedded Windows signature. Does not execute files or verify ZIP archives.</summary>
public static class WindowsSignatureVerifier
{
    public static AuthenticodeResult Verify(string path, string expectedPublisher)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Authenticode verification requires Windows.");
        if (string.IsNullOrWhiteSpace(expectedPublisher)) throw new ArgumentException("An exact expected publisher is required.");
        path = System.IO.Path.GetFullPath(path);
        // Keep the file open without write/delete sharing throughout trust and identity checks.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
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
            using var embedded = X509Certificate.CreateFromSignedFile(path);
            using var certificate = new X509Certificate2(embedded);
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
