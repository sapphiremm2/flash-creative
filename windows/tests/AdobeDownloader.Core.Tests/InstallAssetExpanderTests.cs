using System.IO.Compression;

namespace AdobeDownloader.Core.Tests;

public class InstallAssetExpanderTests
{
    private static WindowsInstallPlan Plan(params PlannedAsset[] assets) => new("APP", "1.0", "test", new string('a', 64), "en_US", true, assets, [],
        [new(null, "Assets/Overlap", "Requires expansion")]);
    private static PlannedAsset Asset(string source, string target, bool recursive = true, bool ignored = false) =>
        new(@"C:\Stage" + source, @"C:\Target" + target, recursive, ignored);
    private static MemoryStream Zip(params string[] entries)
    {
        var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in entries) { using var entry = archive.CreateEntry(name).Open(); if (!name.EndsWith('/')) entry.WriteByte(1); }
        output.Position = 0; return output;
    }
    [Fact] public void OverlappingDirectoriesResolveToDistinctFilesAndKeepEmptyDirectories()
    {
        using var zip = Zip("test.pimx", "1/AMT/app.xml", "1/Application/main.exe", "1/Application/Empty/");
        var result = InstallAssetExpander.Expand(Plan(Asset(@"\AMT", @"\AMT"), Asset(@"\Application", "")), zip, @"C:\Stage");
        Assert.Empty(result.Blockers); Assert.Equal(2, result.Files!.Count); Assert.Contains(@"C:\Target\Empty", result.Directories!);
        Assert.False(result.CanExecute); Assert.Equal(@"C:\Target\AMT\app.xml", result.Files[0].Target);
    }
    [Theory] [InlineData("same-target")] [InlineData("parent-file")] [InlineData("unmapped")]
    [InlineData("missing")] [InlineData("layout")] [InlineData("nonrecursive")] [InlineData("direct-file")]
    public void IncompleteOrCollidingMapsRemainBlocked(string scenario)
    {
        using var zip = scenario switch
        {
            "same-target" => Zip("1/A/file", "1/B/file"), "parent-file" => Zip("1/A/path", "1/B/path/child"),
            "layout" => Zip("2/A/file"), "direct-file" => Zip("1/A"), _ => Zip("1/A/sub/file", "1/Other/file")
        };
        var assets = scenario switch
        {
            "same-target" or "parent-file" => new[] { Asset(@"\A", ""), Asset(@"\B", "") },
            "missing" => [Asset(@"\Missing", "")], "nonrecursive" => [Asset(@"\A", "", recursive: false)],
            _ => [Asset(@"\A", "")]
        };
        Assert.NotEmpty(InstallAssetExpander.Expand(Plan(assets), zip, @"C:\Stage").Blockers);
    }
    [Fact] public void IgnoredPayloadsAreAccountedForWithoutInstallWrites()
    {
        using var zip = Zip("test.pimx", "1/setup.exe");
        var result = InstallAssetExpander.Expand(Plan(Asset("", "", ignored: true)), zip, @"C:\Stage");
        Assert.Empty(result.Files!); Assert.Empty(result.Blockers); Assert.Empty(result.Directories!); Assert.False(result.CanExecute);
    }
    [Theory] [InlineData("1/../escape")] [InlineData("1/CON")]
    [InlineData("1/file:stream")] [InlineData("/absolute")]
    public void UnsafeArchivePathsAreRejected(string name)
    {
        using var zip = Zip(name);
        Assert.Throws<InvalidDataException>(() => InstallAssetExpander.Expand(Plan(Asset("", "")), zip, @"C:\Stage"));
    }
    [Fact] public void CaseCollidingArchiveEntriesAreRejected()
    {
        using var zip = Zip("1/File", "1/file");
        Assert.Throws<InvalidDataException>(() => InstallAssetExpander.Expand(Plan(Asset("", "")), zip, @"C:\Stage"));
    }
    [Fact] public void LinkEntriesAreRejected()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) zip.CreateEntry("1/link").ExternalAttributes = unchecked((int)0xa0000000);
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => InstallAssetExpander.Expand(Plan(Asset("", "")), stream, @"C:\Stage"));
    }
}
