using System.Net;
using System.Security.Cryptography;

namespace AdobeDownloader.Core.Tests;

public sealed class QueueAuditTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flash-audit-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Bytes = [1, 2, 3];
    private static string Hash => Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
    private static string Validation => $"<validationInfo><version>1.0</version><algorithm>TYPE2</algorithm><segmentSize>3</segmentSize><lastSegmentSize>3</lastSegmentSize><segmentCount>1</segmentCount><packageHashKey>key</packageHashKey><segments><segment segmentNumber=\"1\">{Hash}</segment></segments></validationInfo>";
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private async Task Prepare(int count = 1, bool validation = true)
    {
        var asset = PlannerTests.Package() with { OpaqueHashKey = "key", ValidationUrl = validation ? "https://cdn-ffc.oobesaas.adobe.com/validation" : "" };
        var downloads = Enumerable.Range(0, count).Select(i => new PlannedDownload($"{i:D4}", "TEST", "1.0", asset)).ToArray();
        var plan = new DownloadPlan(1, DateTimeOffset.UtcNow, "win64", "en_US", "10.0", [], downloads, validation);
        await DownloadQueue.CreateAsync(plan, directory);
        var queue = await DownloadQueue.ReadAsync(directory);
        for (var i = 0; i < count; i++)
        {
            var path = Payload(i);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, Bytes);
            await JsonFiles.WriteAsync(path + ".receipt.json", new DownloadReceipt(asset.Url.AbsoluteUri, 3, Hash));
            queue.Items[i] = queue.Items[i] with { Status = QueueItemStatus.Completed, Sha256 = Hash };
        }
        await JsonFiles.WriteAsync(Path.Combine(directory, "queue.json"), queue);
    }
    private string Payload(int i = 0) => Path.Combine(directory, "packages", $"{i:D4}", "test.zip");
    private string[] Fingerprint() => Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order()
        .Select(p => Path.GetRelativePath(directory, p) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SuccessfulAuditIsReadOnlyAndOfflineDoesNotRequestNetwork(bool offline)
    {
        await Prepare(); var before = Fingerprint();
        using var handler = new DownloadTests.Handler((request, _) =>
        {
            Assert.False(offline);
            Assert.Equal("/validation", request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new StringContent(Validation) };
        });
        using var http = new HttpClient(handler);
        var report = await new QueueAuditor(new AdobeTransport(http)).AuditAsync(directory, offline);
        Assert.True(report.Complete);
        Assert.Equal(!offline, report.AdobeVerified);
        Assert.Equal(offline ? 0 : 1, handler.Calls);
        Assert.Equal(before, Fingerprint());
    }

    [Fact] public async Task ReportsEveryProblemWithoutRepairOrPayloadRequests()
    {
        await Prepare(4);
        File.Delete(Payload(0));
        await File.WriteAllBytesAsync(Payload(1), [9, 9, 9]);
        var queue = await DownloadQueue.ReadAsync(directory);
        queue.Items[2] = queue.Items[2] with { Status = QueueItemStatus.Paused };
        await JsonFiles.WriteAsync(Path.Combine(directory, "queue.json"), queue);
        var before = Fingerprint();
        using var http = new HttpClient(new DownloadTests.Handler((_, _) => throw new Exception("Offline audit made a request.")));
        var report = await new QueueAuditor(new AdobeTransport(http)).AuditAsync(directory, offline: true);
        Assert.False(report.Complete);
        Assert.Equal(new[] { QueueAuditStatus.Missing, QueueAuditStatus.Invalid, QueueAuditStatus.Incomplete, QueueAuditStatus.Verified }, report.Items.Select(x => x.Status));
        Assert.Equal(before, Fingerprint());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FreshAdobeFailureCannotReuseSavedSuccess(bool unavailable)
    {
        await Prepare();
        using var http = new HttpClient(new DownloadTests.Handler((_, _) => unavailable
            ? new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("offline") }
            : new(HttpStatusCode.OK) { Content = new StringContent(Validation.Replace(Hash, new string('0', 64))) }));
        var report = await new QueueAuditor(new AdobeTransport(http)).AuditAsync(directory);
        Assert.False(report.Complete);
        Assert.False(report.AdobeVerified);
        Assert.Equal(unavailable ? QueueAuditStatus.Unavailable : QueueAuditStatus.Invalid, Assert.Single(report.Items).Status);
    }

    [Fact] public async Task LegacyReceiptNeverBecomesAdobeVerification()
    {
        await Prepare(validation: false);
        using var http = new HttpClient(new DownloadTests.Handler((_, _) => throw new Exception("Legacy audit made a request.")));
        var report = await new QueueAuditor(new AdobeTransport(http)).AuditAsync(directory);
        Assert.True(report.Complete);
        Assert.False(report.AdobeVerified);
        Assert.Equal("LocalSha256ReceiptOnly", Assert.Single(report.Items).Verification!.Method);
    }

    [Fact] public async Task MissingQueueIsNotCreated()
    {
        using var http = new HttpClient(new DownloadTests.Handler((_, _) => throw new Exception("Unexpected network.")));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new QueueAuditor(new AdobeTransport(http)).AuditAsync(directory));
        Assert.False(Directory.Exists(directory));
    }

    [Fact] public async Task CallerCancellationPropagatesWithoutChangingQueue()
    {
        await Prepare(); var before = Fingerprint();
        using var ct = new CancellationTokenSource(); ct.Cancel();
        using var http = new HttpClient(new DownloadTests.Handler((_, _) => throw new Exception("Unexpected network.")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new QueueAuditor(new AdobeTransport(http)).AuditAsync(directory, ct: ct.Token));
        Assert.Equal(before, Fingerprint());
    }
    [Theory] [InlineData("product")] [InlineData("version")] [InlineData("package")]
    public async Task QueueCannotRelabelVerifiedPayloadAsAnotherProduct(string field)
    {
        await Prepare();
        var queue = await DownloadQueue.ReadAsync(directory);
        var item = queue.Items[0];
        var download = field switch
        {
            "product" => item.Download with { SapCode = "OTHER" },
            "version" => item.Download with { ProductVersion = "999.0" },
            _ => item.Download with { Package = item.Download.Package with { Name = "other" } }
        };
        queue.Items[0] = item with { Download = download };
        await JsonFiles.WriteAsync(Path.Combine(directory, "queue.json"), queue);
        using var http = new HttpClient(new DownloadTests.Handler((_, _) => throw new Exception("Unexpected network.")));
        await Assert.ThrowsAsync<InvalidDataException>(() => new QueueAuditor(new AdobeTransport(http)).AuditAsync(directory));
    }
}
