using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace AdobeDownloader.Core.Tests;

public class InstallStagerTests
{
    [Theory] [InlineData("zip")] [InlineData("zip-lzma2")] [InlineData("limit")] [InlineData("tampered")]
    [InlineData("zip-deflated")] [InlineData("ignored")] [InlineData("traversal")] [InlineData("dictionary")] [InlineData("unsupported")]
    public async Task FreshVerificationAndBoundedDecodingAreRequired(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "FlashCreativeTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "test.zip"); var destination = Path.Combine(root, "stage");
            var plain = Encoding.UTF8.GetBytes("test payload");
            var compression = scenario is "zip-lzma2" or "dictionary" ? "zip-lzma2" : scenario == "unsupported" ? "unknown" : scenario == "zip-deflated" ? "zip-deflated" : "zip";
            using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            {
                using (var manifest = zip.CreateEntry("test.pimx").Open()) manifest.Write(Encoding.UTF8.GetBytes("""
                    <Package><Type>core</Type><PackageName>test</PackageName><ProcessorFamily>64-bit</ProcessorFamily><PackageScheme>hd-standard</PackageScheme>
                    <Assets><Asset source="[StagingFolder]" target="[INSTALLDIR]" recursive="true"/></Assets></Package>
                    """.Replace("recursive=\"true\"", scenario == "ignored" ? "recursive=\"true\" ignoreAsset=\"true\"" : "recursive=\"true\"")));
                using var payload = zip.CreateEntry(scenario == "traversal" ? "1/../bad" : "1/app.bin").Open();
                // Raw LZMA2 uncompressed chunk: reset dictionary, 12 bytes, then end marker.
                payload.Write(scenario == "dictionary" ? [40, 0] : compression == "zip-lzma2" ? new byte[] { 16, 1, 0, 11 }.Concat(plain).Append((byte)0).ToArray() : plain);
            }
            var original = await File.ReadAllBytesAsync(path); var hash = Convert.ToHexString(SHA256.HashData(original));
            var metadata = JsonSerializer.Serialize(new { SAPCode = "APP", ProductVersion = "1.0", Platform = "win64", CompressionType = compression,
                Packages = new { Package = new[] { new { PackageName = "test", Path = "test.zip", Type = "core", ProcessorFamily = "64-bit", DownloadSize = original.Length, ValidationURL = "https://ccmdls.adobe.com/validation" } } } });
            var calls = new List<string>();
            using var http = new HttpClient(new DownloadTests.Handler((request, _) => {
                calls.Add(request.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.AbsolutePath == "/core/v3/applications" ? metadata :
                    $"<validationInfo><version>1.0</version><algorithm>TYPE2</algorithm><segmentSize>{original.Length}</segmentSize><lastSegmentSize>{original.Length}</lastSegmentSize><segmentCount>1</segmentCount><segments><segment segmentNumber=\"1\">{hash}</segment></segments></validationInfo>") };
            }));
            var build = PlannerTests.Build("APP");
            var plan = await new DownloadPlanner((b, _) => Task.FromResult(ManifestClient.Parse(metadata, b))).CreateAsync(build, [build], "en_US", "10.0");
            if (scenario == "tampered") { original[^1] ^= 1; await File.WriteAllBytesAsync(path, original); }
            var stager = new InstallStager(new AdobeTransport(http));
            var variables = new Dictionary<string, string> { ["StagingFolder"] = @"C:\Symbolic\Stage", ["INSTALLDIR"] = @"C:\Symbolic\Install" };
            var work = () => stager.StageAsync(plan, "APP", "test", path, variables, destination, scenario == "limit" ? 2 : 1024);
            if (scenario == "unsupported") await Assert.ThrowsAsync<NotSupportedException>(work);
            else if (scenario is not ("zip" or "zip-lzma2" or "zip-deflated" or "ignored")) await Assert.ThrowsAsync<InvalidDataException>(work);
            else
            {
                var result = await work();
                var file = scenario == "ignored"
                    ? new StagedInstallFile(Assert.Single(result.Resources!).RelativePath, "", result.Resources![0].Bytes, result.Resources[0].Sha256)
                    : Assert.Single(result.Files);
                if (scenario == "ignored") Assert.Empty(result.Files);
                Assert.Equal(plain, await File.ReadAllBytesAsync(Path.Combine(destination, file.RelativePath)));
                Assert.Equal(Convert.ToHexString(SHA256.HashData(plain)), file.Sha256); Assert.Equal(12, result.Bytes);
                Assert.False(result.Installed); Assert.False(result.Plan.CanExecute);
            }
            if (scenario is not ("zip" or "zip-lzma2" or "zip-deflated" or "ignored")) Assert.False(Directory.Exists(destination));
            Assert.Empty(Directory.EnumerateDirectories(root, ".flash-install-stage-*"));
            Assert.Equal(new[] { "/core/v3/applications", "/validation" }, calls);
        }
        finally { Directory.Delete(root, true); }
    }
}


