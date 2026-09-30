using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace AdobeDownloader.Core;

/// <summary>Read-only diagnostics. CPU flags describe the running process, including emulation;
/// they are not a complete Adobe application compatibility or installation decision.</summary>
public sealed record WindowsHostInformation(bool IsWindows, string OsVersion, string OsArchitecture,
    string ProcessArchitecture, int LogicalProcessors, bool Avx2AvailableToProcess, string RuntimeVersion)
{
    public static WindowsHostInformation Capture() => new(
        OperatingSystem.IsWindows(), Environment.OSVersion.Version.ToString(),
        RuntimeInformation.OSArchitecture.ToString(), RuntimeInformation.ProcessArchitecture.ToString(),
        Environment.ProcessorCount, Avx2.IsSupported, RuntimeInformation.FrameworkDescription);
}
