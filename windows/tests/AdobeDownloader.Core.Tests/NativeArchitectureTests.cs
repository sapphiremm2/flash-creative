using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace AdobeDownloader.Core.Tests;

public sealed class NativeArchitectureTests(ITestOutputHelper output)
{
    [Fact] public void CiRunsNativelyOnItsDeclaredArchitecture()
    {
        var expected = Environment.GetEnvironmentVariable("FLASH_EXPECTED_ARCH");
        output.WriteLine($"OS: {RuntimeInformation.OSDescription}; OS architecture: {RuntimeInformation.OSArchitecture}; process: {RuntimeInformation.ProcessArchitecture}; runtime: {RuntimeInformation.FrameworkDescription}");
        if (string.IsNullOrEmpty(expected)) return;
        Assert.True(OperatingSystem.IsWindows());
        Assert.Equal(expected, RuntimeInformation.OSArchitecture.ToString());
        Assert.Equal(expected, RuntimeInformation.ProcessArchitecture.ToString());
    }
}
