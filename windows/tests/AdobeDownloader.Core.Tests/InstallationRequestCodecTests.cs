using System.Text;
namespace AdobeDownloader.Core.Tests;
public class InstallationRequestCodecTests
{
    private static InstallationRequest Request() => new() {
        Version = 1, RequestId = Guid.NewGuid(), Product = "KBRG", ProductVersion = "16.0.6.9", BuildId = Guid.NewGuid(),
        Platform = "win64", Locale = "en_US", Enterprise = false, Modules = [],
        Inputs = [new() { Product = "KBRG", Package = "AdobeBridge16.0-mul-x64", Bytes = 680386232 }] };
    [Fact] public void SelectionAndInputLengthsRoundTripWithoutExecutionAuthority()
    {
        var request = Request(); var read = InstallationRequestCodec.Read(InstallationRequestCodec.Write(request));
        Assert.Equal(request.RequestId, read.RequestId); Assert.Equal(request.Product, read.Product);
        Assert.Equal(request.Inputs, read.Inputs); Assert.Empty(read.Modules);
    }
    [Theory] [InlineData("ExecutablePath")] [InlineData("Publisher")] [InlineData("UserSid")] [InlineData("RegistryOperations")]
    public void PrivilegedInstructionsCannotBeSmuggledAsAdditionalFields(string field)
    {
        var json = Encoding.UTF8.GetString(InstallationRequestCodec.Write(Request()));
        json = json.Insert(1, $"\"{field}\":\"untrusted\",");
        Assert.Throws<InvalidDataException>(() => InstallationRequestCodec.Read(Encoding.UTF8.GetBytes(json)));
    }
    [Theory] [InlineData("Version")] [InlineData("Inputs")]
    public void DuplicateFieldsAreRejectedRatherThanLastValueWinning(string field)
    {
        var json = Encoding.UTF8.GetString(InstallationRequestCodec.Write(Request()));
        json = json.Insert(1, $"\"{field}\":null,");
        Assert.Throws<InvalidDataException>(() => InstallationRequestCodec.Read(Encoding.UTF8.GetBytes(json)));
    }
    [Fact] public void MissingFieldsInvalidVersionsPathsAndDuplicateInputsFail()
    {
        Assert.Throws<InvalidDataException>(() => InstallationRequestCodec.Read("{}"u8));
        Assert.Throws<InvalidDataException>(() => InstallationRequestCodec.Read(new byte[65537]));
        var request = Request();
        Assert.Throws<InvalidDataException>(() => InstallationRequestCodec.Write(request with { Version = 2 }));
        Assert.Throws<InvalidDataException>(() => InstallationRequestCodec.Write(request with { Product = @"C:\target" }));
        Assert.Throws<InvalidDataException>(() => InstallationRequestCodec.Write(request with { Inputs = [request.Inputs[0], request.Inputs[0]] }));
        Assert.Throws<InvalidDataException>(() => InstallationRequestCodec.Write(request with { Modules = ["a", "a"] }));
        Assert.Throws<InvalidDataException>(() => InstallationRequestCodec.Write(request with { Inputs = [request.Inputs[0] with { Bytes = long.MaxValue }] }));
    }
}
