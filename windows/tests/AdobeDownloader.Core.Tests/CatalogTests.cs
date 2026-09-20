using System.Xml;

namespace AdobeDownloader.Core.Tests;

public class CatalogTests
{
    internal const string Xml = """
        <response><channels><channel name="ccm"><cdn><secure>https://ccmdls.adobe.com</secure></cdn>
        <products><product id="TEST" version="1.2"><displayName>Test</displayName><platforms>
        <platform id="osx10-64"><languageSet productVersion="1.2.3" /></platform>
        <platform id="win64"><languageSet name="LS20" packageType="hdPackage" productVersion="1.2.3" baseVersion="1.0" buildGuid="test-guid">
        <locales><locale name="en_US"/><locale name="fr_FR"/></locales>
        <dependencies><dependency><sapCode>RUNTIME</sapCode><baseVersion>2.0</baseVersion></dependency></dependencies>
        </languageSet></platform></platforms></product></products></channel></channels></response>
        """;

    internal static ProductBuild Build => Assert.Single(CatalogClient.Parse(Xml, "win64"));

    [Fact] public void KeepsWindowsBuildLocaleAndDependency()
    {
        var build = Build;
        Assert.Equal("win64", build.Platform);
        Assert.Equal("1.2.3", build.ProductVersion);
        Assert.Equal("test-guid", build.BuildGuid);
        Assert.Contains("fr_FR", build.Locales);
        Assert.Equal(new Dependency("RUNTIME", "2.0"), Assert.Single(build.Dependencies));
    }

    [Fact] public void DoesNotSubstituteX64ForArm64() => Assert.Empty(CatalogClient.Parse(Xml, "winarm64"));
    [Fact] public void RejectsMissingProductVersion() => Assert.Throws<InvalidDataException>(() =>
        CatalogClient.Parse(Xml.Replace("productVersion=\"1.2.3\"", ""), "win64"));
    [Fact] public void PreservesLegacyEntryWithoutInventingBuildVersion()
    {
        var legacy = Assert.Single(CatalogClient.Parse(Xml.Replace("hdPackage", "RIBS")
            .Replace("productVersion=\"1.2.3\"", ""), "win64"));
        Assert.Equal("RIBS", legacy.PackageType);
        Assert.Empty(legacy.ProductVersion);
        Assert.Equal("1.2", legacy.Version);
    }
    [Fact] public void RejectsErrorPage() => Assert.Throws<InvalidDataException>(() => CatalogClient.Parse("<error/>", "win64"));
    [Fact] public void RejectsDtd() => Assert.Throws<XmlException>(() =>
        CatalogClient.Parse("<!DOCTYPE response [<!ENTITY foo SYSTEM 'file:///C:/secret'>]>" + Xml, "win64"));
    [Fact] public void RequestIsWindowsOnly()
    {
        var query = CatalogClient.CatalogUrl("win64").Query;
        Assert.Contains("platform=win64", query);
        Assert.Contains("channel=sti", query);
        Assert.DoesNotContain("mac", query);
        Assert.Throws<ArgumentException>(() => CatalogClient.CatalogUrl("osx10-64"));
    }
}
