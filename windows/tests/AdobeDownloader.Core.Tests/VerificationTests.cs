using System.Net;
using System.Security.Cryptography;

namespace AdobeDownloader.Core.Tests;

public sealed class VerificationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flash-verification-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Bytes = [1, 2, 3];
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Xml => $"<validationInfo><version>1.0</version><algorithm>TYPE2</algorithm><segmentSize>2</segmentSize><lastSegmentSize>1</lastSegmentSize><segmentCount>2</segmentCount><packageHashKey>key</packageHashKey><segments><segment segmentNumber=\"1\">{Digest([1, 2])}</segment><segment segmentNumber=\"2\">{Digest([3])}</segment></segments></validationInfo>";
    private static PackageAsset Asset => PlannerTests.Package() with { OpaqueHashKey = "key", ValidationUrl = "https://cdn-ffc.oobesaas.adobe.com/validation" };
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Theory]
    [InlineData("TYPE2", "TYPE1")]
    [InlineData("<segmentCount>2", "<segmentCount>3")]
    [InlineData("segmentNumber=\"2\"", "segmentNumber=\"1\"")]
    [InlineData("<lastSegmentSize>1", "<lastSegmentSize>2")]
    [InlineData("<packageHashKey>key", "<packageHashKey>other")]
    public void RejectsInvalidValidation(string from, string to) =>
        Assert.Throws<InvalidDataException>(() => AdobePackageVerifier.Parse(Xml.Replace(from, to), 3, "key"));

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task BothDownloadersVerifyBeforePublication(bool resume)
    {
        using var http = new HttpClient(new DownloadTests.Handler((r, _) => new(HttpStatusCode.OK)
        { Content = r.RequestUri!.AbsolutePath == "/validation" ? new StringContent(Xml) : new ByteArrayContent(Bytes) }));
        var transport = new AdobeTransport(http);
        var result = resume ? await new ResumableDownloader(transport).DownloadAsync(Asset, directory, 100)
            : await new PackageDownloader(transport).DownloadAsync(Asset, directory, 100);
        Assert.Equal("AdobeHttpsSegmentSha256", result.Verification!.Method);
        Assert.Equal(2, result.Verification.Segments);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(result.Path));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task TamperedSegmentNeverPublished(bool resume)
    {
        using var http = new HttpClient(new DownloadTests.Handler((r, _) => new(HttpStatusCode.OK)
        { Content = r.RequestUri!.AbsolutePath == "/validation" ? new StringContent(Xml) : new ByteArrayContent([1, 2, 4]) }));
        var transport = new AdobeTransport(http);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            if (resume) await new ResumableDownloader(transport).DownloadAsync(Asset, directory, 100);
            else await new PackageDownloader(transport).DownloadAsync(Asset, directory, 100);
        });
        Assert.False(File.Exists(Path.Combine(directory, Asset.FileName)));
        Assert.Empty(Directory.GetFiles(directory, "*.partial"));
    }

    [Fact] public async Task ReopenedReceiptDoesNotBypassFreshAdobeChecks()
    {
        var metadata = Xml;
        using var http = new HttpClient(new DownloadTests.Handler((r, _) => new(HttpStatusCode.OK)
        { Content = r.RequestUri!.AbsolutePath == "/validation" ? new StringContent(metadata) : new ByteArrayContent(Bytes) }));
        var downloader = new ResumableDownloader(new AdobeTransport(http));
        await downloader.DownloadAsync(Asset, directory, 100);
        metadata = Xml.Replace(Digest([3]), Digest([4]));
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Asset, directory, 100));
    }
    [Fact] public async Task QueueCannotDropRequiredValidationMetadata()
    {
        var root = PlannerTests.Build("APP");
        var plan = await new DownloadPlanner((b, _) => Task.FromResult(PlannerTests.Manifest(b)))
            .CreateAsync(root, [root], "en_US", "10.0");
        Assert.Throws<InvalidDataException>(() => DownloadQueue.Validate(plan with { RequiresAdobeValidation = true }));
    }

    [Fact] public async Task QueueCannotOverridePlanValidationIdentity()
    {
        var root = PlannerTests.Build("APP");
        var plan = await new DownloadPlanner((b, _) => Task.FromResult(PlannerTests.Manifest(b, [Asset])))
            .CreateAsync(root, [root], "en_US", "10.0");
        await DownloadQueue.CreateAsync(plan with { RequiresAdobeValidation = true }, directory);
        var queue = await DownloadQueue.ReadAsync(directory);
        var item = queue.Items[0];
        queue.Items[0] = item with { Download = item.Download with { Package = item.Download.Package with { ValidationUrl = "" } } };
        await JsonFiles.WriteAsync(Path.Combine(directory, "queue.json"), queue);
        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadQueue.ReadAsync(directory));
    }
}
