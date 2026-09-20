namespace AdobeDownloader.Core.Tests;

public class ManifestTests
{
    private const string Package = """
        {"PackageName":"Test64","fullPackageName":"Test64.zip","Type":"core","ProcessorFamily":"64-bit",
        "Condition":"locale == en_US","DownloadSize":"3","Path":"/AdobeProducts/Test64.zip","packageHashKey":"opaque"}
        """;
    private static string Json(string package) => """
        {"SAPCode":"TEST","ProductVersion":"1.2.3","Platform":"win64","Packages":{"Package":PACKAGE},
        "Dependencies":{"Dependency":{"SAPCode":"RUNTIME","BaseVersion":"2.0"}}}
        """.Replace("PACKAGE", package);

    [Theory] [InlineData(false)] [InlineData(true)]
    public void HandlesObjectAndArrayPackages(bool array)
    {
        var manifest = ManifestClient.Parse(Json(array ? $"[{Package}]" : Package), CatalogTests.Build);
        var p = Assert.Single(manifest.Packages);
        Assert.Equal("https://ccmdls.adobe.com/AdobeProducts/Test64.zip", p.Url.AbsoluteUri);
        Assert.Equal(3, p.DownloadSize);
        Assert.Equal("opaque", p.OpaqueHashKey);
        Assert.Equal("locale == en_US", p.Condition);
        Assert.Equal("RUNTIME", Assert.Single(manifest.Dependencies).SapCode);
    }

    [Fact] public void RejectsWrongPlatform() => Assert.Throws<InvalidDataException>(() =>
        ManifestClient.Parse(Json(Package).Replace("win64", "osx10-64"), CatalogTests.Build));
    [Fact] public void RejectsWrongVersion() => Assert.Throws<InvalidDataException>(() =>
        ManifestClient.Parse(Json(Package).Replace("1.2.3", "9.0"), CatalogTests.Build));
    [Fact] public void RejectsForeignCdn() => Assert.Throws<InvalidDataException>(() =>
        ManifestClient.Parse(Json(Package).Replace("/AdobeProducts/Test64.zip", "https://example.com/Test64.zip"), CatalogTests.Build));
    [Fact] public void RejectsMissingSize() => Assert.Throws<InvalidDataException>(() =>
        ManifestClient.Parse(Json(Package).Replace("\"DownloadSize\":\"3\",", ""), CatalogTests.Build));
    [Fact] public void RejectsDuplicatePackages() => Assert.Throws<InvalidDataException>(() =>
        ManifestClient.Parse(Json($"[{Package},{Package}]"), CatalogTests.Build));
    [Fact] public void RejectsEmptyPackages() => Assert.Throws<InvalidDataException>(() =>
        ManifestClient.Parse(Json("[]"), CatalogTests.Build));
}
