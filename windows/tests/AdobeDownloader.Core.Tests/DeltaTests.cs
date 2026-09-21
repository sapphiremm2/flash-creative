using System.Net;
using System.Security.Cryptography;

namespace AdobeDownloader.Core.Tests;

public class DeltaTests
{
    private const string Instruction = """
        [{"source":"1/Application/bin.dll.diff","destination":"[INSTALLDIR]\\bin.dll",
          "deltaAction":"PATCH","fileType":"FILE","size":5,"hash":"HASH","externalAttributes":0}]
        """;
    private static string Json => Instruction.Replace("HASH", new string('a', 64));

    [Fact] public void PreservesMetadataWithoutClaimingBaselineTrust()
    {
        var entry = Assert.Single(DeltaInspector.Parse(Json));
        Assert.Equal("PATCH", entry.Action);
        Assert.Equal("[INSTALLDIR]\\bin.dll", entry.Destination);
        Assert.Equal(new string('a', 64), entry.Hash);
        Assert.Contains("externalAttributes", entry.ExtraFields);
    }

    [Theory]
    [InlineData("1/Application/bin.dll.diff", "../bin.dll.diff")]
    [InlineData("1/Application/bin.dll.diff", "C:/bin.dll.diff")]
    [InlineData("1/Application/bin.dll.diff", "//server/file")]
    [InlineData("1/Application/bin.dll.diff", "1/CON.diff")]
    [InlineData("1/Application/bin.dll.diff", "1/bin.dll:stream")]
    [InlineData("[INSTALLDIR]\\\\bin.dll", "[INSTALLDIR]\\\\..\\\\bin.dll")]
    [InlineData("[INSTALLDIR]\\\\bin.dll", "C:/bin.dll")]
    [InlineData("\"size\":5", "\"size\":-1")]
    [InlineData("\"size\":5", "\"size\":5,\"size\":1")]
    public void RejectsAmbiguousOrUnsafeMetadata(string from, string to) =>
        Assert.Throws<InvalidDataException>(() => DeltaInspector.Parse(Json.Replace(from, to)));

    [Fact] public void UnknownActionFailsClosed() =>
        Assert.Throws<NotSupportedException>(() => DeltaInspector.Parse(Json.Replace("PATCH", "EXECUTE")));

    [Fact] public void DeltaValidationMayOmitKeyButFullManifestKeyCannotBeDropped()
    {
        var xml = """
            <validationInfo><version>1.0</version><algorithm>TYPE2</algorithm><segmentSize>5</segmentSize>
            <lastSegmentSize>5</lastSegmentSize><segmentCount>1</segmentCount><segments>
            <segment segmentNumber="1">HASH</segment></segments></validationInfo>
            """.Replace("HASH", new string('a', 64));
        Assert.Empty(AdobePackageVerifier.Parse(xml, 5, "").PackageHashKey);
        Assert.Throws<InvalidDataException>(() => AdobePackageVerifier.Parse(xml, 5, "required-key"));
    }

    [Fact] public async Task InspectionFetchesOnlyMetadataAndDoesNotChangeFullPlan()
    {
        var root = PlannerTests.Build("APP");
        var delta = new DeltaAsset("delta", "0.9", 5, new("https://ccmdls.adobe.com/delta.zip"),
            new("https://ccmdls.adobe.com/diff.json"), "https://cdn-ffc.oobesaas.adobe.com/validation");
        var full = PlannerTests.Package() with { Deltas = [delta] };
        var plan = await new DownloadPlanner((b, _) => Task.FromResult(PlannerTests.Manifest(b, [full])))
            .CreateAsync(root, [root], "en_US", "10.0");
        var calls = new List<string>();
        using var http = new HttpClient(new DownloadTests.Handler((request, _) =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            var payload = request.RequestUri.AbsolutePath switch
            {
                "/diff.json" => Json,
                "/validation" => "<validationInfo><version>1.0</version><algorithm>TYPE2</algorithm><segmentSize>5</segmentSize><lastSegmentSize>5</lastSegmentSize><segmentCount>1</segmentCount><segments><segment segmentNumber=\"1\">" + new string('a', 64) + "</segment></segments></validationInfo>",
                _ => throw new Exception("Payload download must not be requested.")
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) };
        }));
        var report = await new DeltaInspector(new AdobeTransport(http)).InspectAsync(plan, "APP", full.Name, "0.9");
        Assert.Equal(new[] { "/diff.json", "/validation" }, calls);
        Assert.False(report.CanApply);
        Assert.False(report.InstalledBaselineVerified);
        Assert.Equal(1, report.Actions["PATCH"]);
        Assert.Equal(full.Url, Assert.Single(plan.Downloads).Package.Url);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Json))).ToLowerInvariant(), report.MetadataSha256);
    }
    [Theory] [InlineData("valid")] [InlineData("mismatch")] [InlineData("tampered")] [InlineData("duplicate")]
    public async Task ArchiveChecksRequireBothAdobeSegmentsAndMatchingEmbeddedMetadata(string scenario)
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var archive = new System.IO.Compression.ZipArchive(new FileStream(path, FileMode.Create), System.IO.Compression.ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("test_diff.json").Open()))
                    writer.Write(scenario == "mismatch" ? Json.Replace("PATCH", "EXISTS") : Json);
                if (scenario == "duplicate")
                {
                    using var writer = new StreamWriter(archive.CreateEntry("test_diff.json").Open());
                    writer.Write(Json);
                }
            }
            var bytes = await File.ReadAllBytesAsync(path);
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            if (scenario == "tampered") { bytes[0] ^= 1; await File.WriteAllBytesAsync(path, bytes); }
            var root = PlannerTests.Build("APP");
            var full = PlannerTests.Package() with { Deltas = [new("delta", "0.9", bytes.Length,
                new("https://ccmdls.adobe.com/delta.zip"), new("https://ccmdls.adobe.com/diff.json"), "https://cdn-ffc.oobesaas.adobe.com/validation")] };
            var plan = await new DownloadPlanner((b, _) => Task.FromResult(PlannerTests.Manifest(b, [full])))
                .CreateAsync(root, [root], "en_US", "10.0");
            using var http = new HttpClient(new DownloadTests.Handler((request, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath == "/diff.json" ? Json :
                    $"<validationInfo><version>1.0</version><algorithm>TYPE2</algorithm><segmentSize>{bytes.Length}</segmentSize><lastSegmentSize>{bytes.Length}</lastSegmentSize><segmentCount>1</segmentCount><segments><segment segmentNumber=\"1\">{digest}</segment></segments></validationInfo>")
            }));
            var inspector = new DeltaInspector(new AdobeTransport(http));
            if (scenario != "valid")
                await Assert.ThrowsAsync<InvalidDataException>(() => inspector.InspectAsync(plan, "APP", "test", "0.9", archivePath: path));
            else
            {
                var report = await inspector.InspectAsync(plan, "APP", "test", "0.9", archivePath: path);
                Assert.Equal("AdobeHttpsSegmentSha256", report.PayloadVerification!.Method);
                Assert.True(report.EmbeddedMetadataMatched);
                Assert.False(report.CanApply);
                Assert.False(report.InstalledBaselineVerified);
            }
        }
        finally { File.Delete(path); }
    }
}
