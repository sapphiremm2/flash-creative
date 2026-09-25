using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace AdobeDownloader.Core.Tests;

public class InstallInspectorTests
{
    private const string Xml = """
        <Package><Type>core</Type><PackageName>test</PackageName><ProcessorFamily>64-bit</ProcessorFamily>
        <PackageScheme>hd-standard</PackageScheme><Assets><Asset source="[StagingFolder]" target="[INSTALLDIR]" recursive="true"/></Assets>
        <Commands><Registry><Path>HKEY_LOCAL_MACHINE\Software\Test</Path><LocalizedValue><Language locale="en_US">[INSTALLDIR]</Language></LocalizedValue></Registry>
        <RunProgram><InstallCommand isThirdParty="true"><Path>[StagingFolder]/setup.exe</Path><Arguments><Argument>/q</Argument></Arguments></InstallCommand></RunProgram></Commands></Package>
        """;
    [Fact] public void InventoryPreservesNestedValuesWithoutAuthorizingExecution()
    {
        var report = InstallInspector.Parse(Encoding.UTF8.GetBytes(Xml), "test", "64-bit");
        Assert.False(report.CanInstall); Assert.Empty(report.UnknownElements);
        Assert.Equal(3, report.Operations.Count); Assert.Equal(1, report.OperationCounts["Commands/RunProgram"]);
        Assert.Contains("isThirdParty=\"true\"", report.Operations[2].Xml);
        Assert.Contains("LocalizedValue", report.Operations[1].Xml);
        Assert.Equal(new[] { "INSTALLDIR", "StagingFolder" }, report.Variables);
    }
    [Fact] public void UnknownInstructionsAndSectionsRemainVisible()
    {
        var xml = Xml.Replace("</Commands>", "<LaunchMystery flag=\"x\"/></Commands>").Replace("</Package>", "<NewSection/></Package>");
        var report = InstallInspector.Parse(Encoding.UTF8.GetBytes(xml), "test", "64-bit");
        Assert.Contains("Commands/LaunchMystery", report.UnknownElements); Assert.Contains("Package/NewSection", report.UnknownElements);
        Assert.False(report.CanInstall); Assert.Contains(report.Operations, o => o.Kind == "Commands/LaunchMystery");
    }
    [Theory] [InlineData("identity")] [InlineData("family")] [InlineData("duplicate")]
    [InlineData("namespace")] [InlineData("section")] [InlineData("depth")]
    public void RejectsAmbiguousOrUnsupportedStructure(string scenario)
    {
        var xml = scenario switch
        {
            "identity" => Xml.Replace("<PackageName>test", "<PackageName>other"),
            "family" => Xml.Replace("64-bit", "32-bit"),
            "duplicate" => Xml.Replace("</Package>", "<PackageName>test</PackageName></Package>"),
            "namespace" => Xml.Replace("<Commands>", "<Commands xmlns=\"urn:unknown\">"),
            "section" => Xml.Replace("</Package>", "<Commands/></Package>"),
            "depth" => Xml.Replace("</Package>", string.Concat(Enumerable.Repeat("<x>", 70)) + string.Concat(Enumerable.Repeat("</x>", 70)) + "</Package>"), _ => Xml
        };
        Assert.Throws<InvalidDataException>(() => InstallInspector.Parse(Encoding.UTF8.GetBytes(xml), "test", "64-bit"));
    }
    [Fact] public void RejectsDtdBeforeResolvingEntities() => Assert.Throws<XmlException>(() => InstallInspector.Parse(
        Encoding.UTF8.GetBytes("<!DOCTYPE Package [<!ENTITY external SYSTEM 'file:///not-read'>]>" + Xml.Replace("<Type>core", "<Type>&external;")), "test", "64-bit"));
    [Fact] public void ReadsUtf16ManifestVariables()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Xml)).ToArray();
        Assert.Contains("INSTALLDIR", InstallInspector.Parse(bytes, "test", "64-bit").Variables);
    }
    [Theory] [InlineData("plain")] [InlineData("lzma")]
    [InlineData("tampered")] [InlineData("duplicate")]
    [InlineData("oversized")] [InlineData("expansion")] [InlineData("malformed")]
    public async Task InspectionRequiresFreshManifestAndArchiveVerification(string scenario)
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            {
                using (var entry = zip.CreateEntry("test.pimx").Open())
                    entry.Write(scenario switch { "lzma" => CompressedManifest, "oversized" => new byte[8 * 1024 * 1024 + 1],
                        "expansion" => ExpansionFixture, "malformed" => new byte[] { 16, 255 }, _ => Encoding.UTF8.GetBytes(Xml) });
                if (scenario == "duplicate") { using var duplicate = zip.CreateEntry("TEST.PIMX").Open(); duplicate.Write(Encoding.UTF8.GetBytes(Xml)); }
            }
            var original = await File.ReadAllBytesAsync(path); var hash = Convert.ToHexString(SHA256.HashData(original));
            var metadata = JsonSerializer.Serialize(new { SAPCode = "APP", ProductVersion = "1.0", Platform = "win64",
                Packages = new { Package = new[] { new { PackageName = "test", Path = "test.zip", Type = "core", ProcessorFamily = "64-bit", DownloadSize = original.Length,
                    ValidationURL = "https://ccmdls.adobe.com/validation" } } } });
            var calls = new List<string>();
            using var http = new HttpClient(new DownloadTests.Handler((request, _) =>
            {
                calls.Add(request.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.AbsolutePath switch
                {
                    "/core/v3/applications" => metadata,
                    "/validation" => $"<validationInfo><version>1.0</version><algorithm>TYPE2</algorithm><segmentSize>{original.Length}</segmentSize><lastSegmentSize>{original.Length}</lastSegmentSize><segmentCount>1</segmentCount><segments><segment segmentNumber=\"1\">{hash}</segment></segments></validationInfo>",
                    _ => throw new Exception("No payload or execution request expected")
                }) };
            }));
            var build = PlannerTests.Build("APP");
            var plan = await new DownloadPlanner((b, _) => Task.FromResult(ManifestClient.Parse(metadata, b))).CreateAsync(build, [build], "en_US", "10.0");
            if (scenario == "tampered") { var altered = original.ToArray(); altered[^1] ^= 1; await File.WriteAllBytesAsync(path, altered); }
            var inspector = new InstallInspector(new AdobeTransport(http));
            if (scenario is "tampered" or "duplicate" or "oversized" or "expansion" or "malformed") await Assert.ThrowsAsync<InvalidDataException>(() => inspector.InspectAsync(plan, "APP", "test", path));
            else
            {
                var result = await inspector.InspectAsync(plan, "APP", "test", path);
                Assert.Equal("AdobeHttpsSegmentSha256", result.Verification.Method);
                Assert.False(result.DetachedSignatureVerified); Assert.False(result.Manifest.CanInstall);
                Assert.Equal(original, await File.ReadAllBytesAsync(path));
            }
            Assert.Equal(new[] { "/core/v3/applications", "/validation" }, calls);
        }
        finally { File.Delete(path); }
    }
    // Independent LZMA2 vector expands beyond the 8 MiB metadata limit.
    private static byte[] ExpansionFixture => Convert.FromBase64String("EP//EQFsXQA8b/u//qOxXuX4P7KqJlX4aHBBcBUPjf0eTBuKQrcZ9GkYca5mI4qKTS+jDdl/puOMIxFT4FkYxXWK4nf4tpR/DGrA3nRJZOLpXFOyBNj3RAyrXw1tRunlw3aIt5ZXrLZN4Wkdb/tLiBBsQsuIP1wAj9BOryYolHEfPY8k4XCepyNf7CjLhdGVmIp+KpHyJ3X3GcAGmE2Y/div1ZAPxCVT+PWRNjEFpbDub8FwTUcM0ZERqq1gHbrOsScYXFmG6WZSWL7pdqxZ5OVbBQj5x9qt/PtSK3TNHlsgQvndUz34KWQJO4DLKmzftTvwxL0uX6oPPktmQpATDv8Qk/hxeFn4C83/lShGD6n8fN77mjAuVsCPhfODgcBlxCVT+PWRNjEFpbDub8FwTUcM0ZERqq1gHbrOsScYXFmG6WZSWL7pdqxZ5OVbBQj5x9qt/PtSK3TNHlsgQvndUz34KWQJO4DLKmzftTvwxLxIJ+ZYn/8QASsA7HNTp/2+rnwxGp+3jTFucJ6nI1/sKMuF0ZWYin4qkfIndfcZwAaYTZj92K/VkA/EJVP49ZE2MQWlsO5vwXBNRwzRkRGqrWAdus6xJxhcWYbpZlJYvul2rFnk5VsFCPnH2q38+1IrdM0eWyBC+d1TPfgpZAk7gMsqbN+1O/DEvS5fqg8+S2ZCkBMO/xCT+HF4WfgLzf+VKEYPqfx83vuaMC5WwI+F84OBwGXEJVP49ZE2MQWlsO5vwXBNRwzRkRGqrWAdus6xJxhcWYbpZlJYvul2rFnk5VsFCPnH2q38+1IrdM0eWyBC+d1TPfgpZAk7gMsqbN+1O/DEvS5fqg8+S2ZCkBMO/xCT+HF4WfgLzf+VKEYPqfx83vuaMC5WwI+F84OBwGXEJVEPP7Kf/xABKwDsc1On/b6ufDEan7eNMW5wnqcjX+woy4XRlZiKfiqR8id19xnABphNmP3Yr9WQD8QlU/j1kTYxBaWw7m/BcE1HDNGREaqtYB26zrEnGFxZhulmUli+6XasWeTlWwUI+cfarfz7Uit0zR5bIEL53VM9+ClkCTuAyyps37U78MS9Ll+qDz5LZkKQEw7/EJP4cXhZ+AvN/5UoRg+p/Hze+5owLlbAj4Xzg4HAZcQlU/j1kTYxBaWw7m/BcE1HDNGREaqtYB26zrEnGFxZhulmUli+6XasWeTlWwUI+cfarfz7Uit0zR5bIEL53VM9+ClkCTuAyyps37U78MS9Ll+qDz5LZkKQEw7/EJP4cXhZ+AvN/5UoRg+p/Hze+5owLlbAj4Xzg4HAZcQlUQ8/sp//EAErAOxzU6f9vq58MRqft40xbnCepyNf7CjLhdGVmIp+KpHyJ3X3GcAGmE2Y/div1ZAPxCVT+PWRNjEFpbDub8FwTUcM0ZERqq1gHbrOsScYXFmG6WZSWL7pdqxZ5OVbBQj5x9qt/PtSK3TNHlsgQvndUz34KWQJO4DLKmzftTvwxL0uX6oPPktmQpATDv8Qk/hxeFn4C83/lShGD6n8fN77mjAuVsCPhfODgcBlxCVT+PWRNjEFpbDub8FwTUcM0ZERqq1gHbrOsScYXFmG6WZSWL7pdqxZ5OVbBQj5x9qt/PtSK3TNHlsgQvndUz34KWQJO4DLKmzftTvwxL0uX6oPPktmQpATDv8Qk/hxeFn4C83/lShGD6n8fN77mjAuVsCPhfODgcBlxCVRDz+ygAO7AAUAe2WyCwAA");
    private static byte[] CompressedManifest => Convert.FromBase64String("EOAAlQBcXQAeFAgmM4lcOhK5U8tT6SyxGvkqift3lcoWCH0fj5jKhIfp7xPv1Qyggfop8uyEDJOIpLkuiqSrn+e0+mSn/9wCcouwUjYbtE6x/nZtKE4v+oV7DU7aw14sZIGozAA=");
}
