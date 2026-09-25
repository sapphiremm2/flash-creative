using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AdobeDownloader.Core.Tests;

public class DeltaStageTests
{
    [Theory]
    [InlineData("valid")] [InlineData("lzma")] [InlineData("baseline-hash")]
    [InlineData("target-hash")] [InlineData("limit")] [InlineData("existing")]
    [InlineData("traversal")] [InlineData("duplicate")] [InlineData("ambiguous")]
    [InlineData("tampered")]
    public async Task StagesOnlyVerifiedPayloadIntoNewDirectory(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "flash-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var old = "abc"u8.ToArray(); var expected = "abd!"u8.ToArray();
            var records = new List<object>
            {
                new { source = "1/app/file.diff", destination = "[INSTALLDIR]\\file", deltaAction = "PATCH", fileType = "FILE", size = 4,
                    hash = scenario == "target-hash" ? new string('a', 64) : Hash(expected) },
                new { source = "1/app/file", destination = "[INSTALLDIR]\\file", deltaAction = "EXISTS", fileType = "FILE", size = 3,
                    hash = scenario == "baseline-hash" ? new string('a', 64) : Hash(old) },
                new { source = "1/app/keep", destination = "[INSTALLDIR]\\keep", deltaAction = "EXISTS", fileType = "FILE", size = 3, hash = Hash(old) },
                new { source = "1/app/new", destination = "[INSTALLDIR]\\new", deltaAction = "ADD", fileType = "FILE", size = 4, hash = Hash(expected) },
                new { source = "1/app/removed", destination = "[INSTALLDIR]\\removed", deltaAction = "DELETE", fileType = "FILE", size = 0, hash = "" }
            };
            if (scenario == "ambiguous") records.Add(records[0]);
            var metadata = JsonSerializer.Serialize(records);
            var baselinePath = Path.Combine(root, "old.zip"); var deltaPath = Path.Combine(root, "delta.zip");
            Zip(baselinePath, [("1/app/file", scenario == "lzma" ? PatchFixtures.LzmaOld : old),
                ("1/app/keep", scenario == "lzma" ? PatchFixtures.LzmaOld : old)]);
            var entries = new List<(string, byte[])> { ("test_diff.json", Encoding.UTF8.GetBytes(metadata)),
                ("1/app/file.diff", PatchFixtures.Padded), ("1/app/new", expected) };
            if (scenario == "traversal") entries.Add(("../escape", old));
            if (scenario == "duplicate") entries.Add(("1/APP/NEW", old));
            Zip(deltaPath, entries);
            var baselineBytes = await File.ReadAllBytesAsync(baselinePath); var deltaBytes = await File.ReadAllBytesAsync(deltaPath);
            if (scenario == "tampered") { var changed = deltaBytes.ToArray(); changed[^1] ^= 1; await File.WriteAllBytesAsync(deltaPath, changed); }
            var oldBuild = PlannerTests.Build("APP", "1.0"); var newBuild = PlannerTests.Build("APP", "2.0");
            string Manifest(bool baseline) => JsonSerializer.Serialize(new
            {
                SAPCode = "APP", ProductVersion = baseline ? "1.0" : "2.0", Platform = "win64", CompressionType = scenario == "lzma" ? "Zip-Lzma2" : "Zip",
                Packages = new { Package = new[] { new
                {
                    PackageName = "test", Path = baseline ? "old.zip" : "new.zip", Type = "core", ProcessorFamily = "64-bit",
                    DownloadSize = baseline ? baselineBytes.Length : 3, PackageVersion = baseline ? "1.0" : "2.0",
                    ValidationURL = "https://ccmdls.adobe.com/old-validation",
                    DeltaPackages = baseline ? Array.Empty<object>() : new object[] { new {
                        PackageName = "delta", BasePackageVersion = "1.0", DownloadSize = deltaBytes.Length, Path = "delta.zip",
                        MetadataFilePath = "diff.json", ValidationURL = "https://ccmdls.adobe.com/delta-validation" } }
                } } }
            });
            using var http = new HttpClient(new DownloadTests.Handler((request, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath switch
                {
                    "/core/v3/applications" => Manifest(request.RequestUri.Query.Contains("version=1.0")),
                    "/old-validation" => Validation(baselineBytes), "/delta-validation" => Validation(deltaBytes),
                    "/diff.json" => metadata, _ => throw new Exception("Unexpected request")
                })
            }));
            var planner = new DownloadPlanner((b, _) => Task.FromResult(ManifestClient.Parse(Manifest(b.ProductVersion == "1.0"), b)));
            var baselinePlan = await planner.CreateAsync(oldBuild, [oldBuild], "en_US", "10.0");
            var targetPlan = await planner.CreateAsync(newBuild, [newBuild], "en_US", "10.0");
            var destination = Path.Combine(root, "staged");
            if (scenario == "existing") { Directory.CreateDirectory(destination); await File.WriteAllTextAsync(Path.Combine(destination, "sentinel"), "keep"); }
            var stager = new DeltaStager(new AdobeTransport(http));
            var run = () => stager.StageAsync(targetPlan, baselinePlan, "APP", "test", baselinePath, deltaPath, destination, scenario == "limit" ? 1 : 10000);
            if (scenario is "valid" or "lzma")
            {
                var result = await run(); Assert.False(result.Installed); Assert.Equal(1, result.PatchedFiles); Assert.Equal(3, result.Files);
                Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(destination, "payload", "INSTALLDIR", "file")));
                Assert.Equal(old, await File.ReadAllBytesAsync(Path.Combine(destination, "payload", "INSTALLDIR", "keep")));
                Assert.False(File.Exists(Path.Combine(destination, "payload", "INSTALLDIR", "removed")));
                Assert.True(File.Exists(Path.Combine(destination, "stage-receipt.json")));
            }
            else
            {
                if (scenario == "existing") await Assert.ThrowsAsync<IOException>(run);
                else await Assert.ThrowsAsync<InvalidDataException>(run);
                if (scenario == "existing") Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(destination, "sentinel")));
                else Assert.False(Directory.Exists(destination));
            }
            Assert.Empty(Directory.GetDirectories(root, ".flash-stage-*"));
            Assert.Equal(baselineBytes, await File.ReadAllBytesAsync(baselinePath));
        }
        finally { Directory.Delete(root, true); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Validation(byte[] bytes) => $"<validationInfo><version>1.0</version><algorithm>TYPE2</algorithm><segmentSize>{bytes.Length}</segmentSize><lastSegmentSize>{bytes.Length}</lastSegmentSize><segmentCount>1</segmentCount><segments><segment segmentNumber=\"1\">{Hash(bytes)}</segment></segments></validationInfo>";
    private static void Zip(string path, IEnumerable<(string Name, byte[] Bytes)> entries)
    {
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries) { using var stream = zip.CreateEntry(name).Open(); stream.Write(bytes); }
    }
}
