using System.Xml.Linq;
namespace AdobeDownloader.Core.Tests;
public class RuntimeInstallerPolicyTests
{
    private const string Xml = "<RunProgram><InstallCommand isThirdParty=\"true\"><Path>[StagingFolder]/VC_redist.x64.exe</Path><Arguments><Argument>/q</Argument><Argument>/norestart</Argument></Arguments></InstallCommand></RunProgram>";
    [Fact] public void OnlyReviewedIdentityAndExactCommandGetCodeOwnedPublisher()
    {
        var policy = RuntimeInstallerPolicy.Compile("VC14win64", "2.0.0.2", "VCRedist14-64", XElement.Parse(Xml));
        Assert.Equal("Microsoft Corporation", policy.Publisher); Assert.Equal("1/VC_redist.x64.exe", policy.ArchiveEntry);
        Assert.Throws<InvalidDataException>(() => RuntimeInstallerPolicy.Compile("VC14win64", "future", "VCRedist14-64", XElement.Parse(Xml)));
    }
    [Theory] [InlineData("/q", "/uninstall")] [InlineData("/norestart", "/forcerestart")]
    [InlineData("VC_redist.x64.exe", "other.exe")] [InlineData("isThirdParty=\"true\"", "isThirdParty=\"false\"")]
    [InlineData("</Arguments>", "<Argument>/extra</Argument></Arguments>")]
    public void MetadataCannotChangeArgumentsOrExecutable(string old, string replacement) =>
        Assert.Throws<InvalidDataException>(() => RuntimeInstallerPolicy.Compile("VC14win64", "2.0.0.2", "VCRedist14-64", XElement.Parse(Xml.Replace(old, replacement))));
    [Theory] [InlineData(0, true, false)] [InlineData(3010, true, true)] [InlineData(1641, true, true)]
    [InlineData(1603, false, false)] [InlineData(1638, false, false)]
    public void ExitCodesDoNotSilentlyAcceptErrors(int code, bool success, bool reboot)
    {
        var result = RuntimeInstallerPolicy.ClassifyExit(code); Assert.Equal(success, result.Succeeded); Assert.Equal(reboot, result.RebootRequired);
        Assert.Equal(code == 1641, result.RebootInitiated);
    }
}
