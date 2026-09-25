namespace AdobeDownloader.Core.Tests;

public class BsdiffTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void AppliesIndependentVectorsWithOptionalAdobePadding(bool padded)
    {
        using var old = new MemoryStream("abc"u8.ToArray()); using var output = new MemoryStream();
        BsdiffPatch.Apply(old, padded ? PatchFixtures.Padded : PatchFixtures.Simple, output, 4);
        Assert.Equal("abd!"u8.ToArray(), output.ToArray());
    }
    [Fact] public void NegativeSeekTreatsBytesOutsideBaselineAsZero()
    {
        using var old = new MemoryStream("abc"u8.ToArray()); using var output = new MemoryStream();
        BsdiffPatch.Apply(old, PatchFixtures.NegativeSeek, output, 4);
        Assert.Equal(new byte[] { 97, 0, 97, 98 }, output.ToArray());
    }
    [Theory] [InlineData("padding")] [InlineData("excess")] [InlineData("size")]
    [InlineData("header")] [InlineData("truncated")] [InlineData("control")]
    public void RejectsInvalidPatches(string scenario)
    {
        var patch = scenario switch { "padding" => PatchFixtures.BadPadding, "excess" => PatchFixtures.ExcessPadding,
            "control" => PatchFixtures.BeyondTarget, _ => PatchFixtures.Simple };
        if (scenario == "header") patch[0] = 0;
        if (scenario == "truncated") patch = patch[..^8];
        using var old = new MemoryStream("abc"u8.ToArray()); using var output = new MemoryStream();
        Assert.ThrowsAny<Exception>(() => BsdiffPatch.Apply(old, patch, output, scenario == "size" ? 5 : 4));
    }
    [Fact] public void CancellationStopsBeforeWriting()
    {
        using var old = new MemoryStream("abc"u8.ToArray()); using var output = new MemoryStream();
        Assert.Throws<OperationCanceledException>(() => BsdiffPatch.Apply(old, PatchFixtures.Simple, output, 4, new(true)));
        Assert.Empty(output.ToArray());
    }
}
