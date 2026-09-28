using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace AdobeDownloader.Core.Tests;
public class RuntimeInstallerPreparationTests
{
    [Theory] [InlineData("unsigned")] [InlineData("extra-payload")] [InlineData("changed-command")]
    public async Task PreparationRejectsUntrustedRuntimeOrUnexpectedPackageContents(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "FlashCreativeTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "runtime.zip"); var destination = Path.Combine(root, "stage");
            using (var zip = new ZipArchive(File.Create(archive), ZipArchiveMode.Create))
            {
                using (var manifest = zip.CreateEntry("VCRedist14-64.pimx").Open()) manifest.Write(Encoding.UTF8.GetBytes("""
                    <Package><Type>core</Type><PackageName>VCRedist14-64</PackageName><ProcessorFamily>64-bit</ProcessorFamily><PackageScheme>hd-standard</PackageScheme>
                    <Assets><Asset source="[StagingFolder]" target="[InstallDir]" recursive="true" ignoreAsset="true"/></Assets>
                    <Commands><RunProgram><InstallCommand isThirdParty="true"><Path>[StagingFolder]/VC_redist.x64.exe</Path><Arguments><Argument>/q</Argument><Argument>/norestart</Argument></Arguments></InstallCommand></RunProgram></Commands></Package>
                    """.Replace("/q", scenario == "changed-command" ? "/uninstall" : "/q")));
                using (var file = zip.CreateEntry("1/VC_redist.x64.exe").Open()) file.Write(Encoding.UTF8.GetBytes("unsigned fixture"));
                if (scenario == "extra-payload") { using var extra = zip.CreateEntry("1/extra.dll").Open(); extra.WriteByte(1); }
            }
            var bytes = await File.ReadAllBytesAsync(archive); var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var metadata = JsonSerializer.Serialize(new { SAPCode = "VC14win64", ProductVersion = "2.0.0.2", Platform = "win64", CompressionType = "zip-deflated",
                Packages = new { Package = new[] { new { PackageName = "VCRedist14-64", Path = "runtime.zip", Type = "core", ProcessorFamily = "64-bit", DownloadSize = bytes.Length, ValidationURL = "https://ccmdls.adobe.com/validation" } } } });
            var calls = new List<string>();
            using var http = new HttpClient(new DownloadTests.Handler((request, _) => {
                calls.Add(request.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.AbsolutePath switch {
                    "/core/v3/applications" => metadata,
                    "/validation" => $"<validationInfo><version>1.0</version><algorithm>TYPE2</algorithm><segmentSize>{bytes.Length}</segmentSize><lastSegmentSize>{bytes.Length}</lastSegmentSize><segmentCount>1</segmentCount><segments><segment segmentNumber=\"1\">{hash}</segment></segments></validationInfo>",
                    _ => throw new InvalidOperationException("Unexpected request") }) };
            }));
            var build = PlannerTests.Build("VC14win64", "2.0.0.2");
            var plan = await new DownloadPlanner((b, _) => Task.FromResult(ManifestClient.Parse(metadata, b))).CreateAsync(build, [build], "en_US", "10.0");
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => new RuntimeInstallerPreparation(new AdobeTransport(http)).PrepareAsync(plan, archive, destination));
            Assert.Contains(scenario switch { "unsigned" => "signature verification failed", "extra-payload" => "package layout", _ => "unsupported installation instructions" }, failure.Message);
            Assert.Equal(new[] { "/core/v3/applications", "/validation" }, calls);
        }
        finally { Directory.Delete(root, true); }
    }
}
