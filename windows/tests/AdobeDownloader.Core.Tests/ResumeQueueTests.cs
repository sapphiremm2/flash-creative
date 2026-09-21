using System.Net;
using System.Net.Http.Headers;

namespace AdobeDownloader.Core.Tests;

public sealed class ResumeQueueTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flash-creative-resume-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Bytes = [1, 2, 3];
    private static PackageAsset Asset => PlannerTests.Package();
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private sealed class InterruptedStream(CancellationTokenSource? cancel = null) : Stream
    {
        private bool started;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!started) { started = true; Bytes.AsMemory(0, 2).CopyTo(buffer); return ValueTask.FromResult(2); }
            if (cancel is not null) { cancel.Cancel(); ct.ThrowIfCancellationRequested(); }
            throw new IOException("Simulated connection lost after two bytes.");
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Broken(CancellationTokenSource? cancel = null, string? etag = "\"v1\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(cancel)) };
        response.Content.Headers.ContentLength = 3;
        if (etag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }
    private static HttpResponseMessage Complete(byte[]? bytes = null, string etag = "\"v1\"") =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? Bytes), Headers = { ETag = EntityTagHeaderValue.Parse(etag) } };
    private static HttpResponseMessage Remaining(HttpRequestMessage request)
    {
        Assert.Equal(2, Assert.Single(request.Headers.Range!.Ranges).From);
        Assert.Equal("\"v1\"", request.Headers.IfRange!.EntityTag!.Tag);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent([3]), Headers = { ETag = EntityTagHeaderValue.Parse("\"v1\"") } };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(2, 2, 3);
        return response;
    }
    private async Task Interrupt(string? etag = "\"v1\"")
    {
        using var http = new HttpClient(new DownloadTests.Handler((_, _) => Broken(etag: etag)));
        await Assert.ThrowsAsync<IOException>(() => new ResumableDownloader(new AdobeTransport(http))
            .DownloadAsync(Asset, directory, 10, maxAttempts: 1));
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Path.Combine(directory, "test.zip.partial")));
    }

    [Fact] public async Task ResumesAcrossDownloaderInstances()
    {
        await Interrupt();
        using var handler = new DownloadTests.Handler((r, _) => Remaining(r));
        using var http = new HttpClient(handler);
        var result = await new ResumableDownloader(new AdobeTransport(http)).DownloadAsync(Asset, directory, 10);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(result.Path));
        Assert.Equal(1, handler.Calls);
        Assert.False(File.Exists(result.Path + ".partial"));
    }

    [Fact] public async Task RangeIgnoredOrChangedEtagRestartsWithoutAppending()
    {
        await Interrupt();
        using var http = new HttpClient(new DownloadTests.Handler((r, _) =>
        { Assert.NotNull(r.Headers.Range); return Complete([4, 5, 6], "\"v2\""); }));
        var result = await new ResumableDownloader(new AdobeTransport(http)).DownloadAsync(Asset, directory, 10);
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(result.Path));
    }

    [Theory] [InlineData(null)] [InlineData("W/\"v1\"")]
    public async Task MissingOrWeakEtagRestarts(string? etag)
    {
        await Interrupt(etag);
        using var http = new HttpClient(new DownloadTests.Handler((r, _) =>
        { Assert.Null(r.Headers.Range); return Complete(); }));
        var result = await new ResumableDownloader(new AdobeTransport(http)).DownloadAsync(Asset, directory, 10);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(result.Path));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task WrongContentRangeOrEtagRejectsAndDiscardsPartial(bool changeTag)
    {
        await Interrupt();
        using var http = new HttpClient(new DownloadTests.Handler((r, _) =>
        {
            var response = Remaining(r);
            if (changeTag) response.Headers.ETag = EntityTagHeaderValue.Parse("\"v2\"");
            else response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1, 1, 3);
            return response;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ResumableDownloader(new AdobeTransport(http)).DownloadAsync(Asset, directory, 10));
        Assert.False(File.Exists(Path.Combine(directory, "test.zip")));
        Assert.False(File.Exists(Path.Combine(directory, "test.zip.partial")));
    }

    [Fact] public async Task RangeNotSatisfiableRestarts()
    {
        await Interrupt(); var count = 0;
        using var http = new HttpClient(new DownloadTests.Handler((r, _) =>
        {
            if (++count == 1) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            Assert.Null(r.Headers.Range); return Complete();
        }));
        var result = await new ResumableDownloader(new AdobeTransport(http)).DownloadAsync(Asset, directory, 10, maxAttempts: 2);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(result.Path));
    }

    [Fact] public async Task RetryResumesInterruptedConnection()
    {
        var count = 0;
        using var http = new HttpClient(new DownloadTests.Handler((r, _) => ++count == 1 ? Broken() : Remaining(r)));
        var result = await new ResumableDownloader(new AdobeTransport(http)).DownloadAsync(Asset, directory, 10, maxAttempts: 2);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(result.Path));
        Assert.Equal(2, count);
    }

    [Fact] public async Task TamperedCompletedFileIsNotSilentlySkipped()
    {
        using var handler = new DownloadTests.Handler((_, _) => Complete());
        using var http = new HttpClient(handler);
        var downloader = new ResumableDownloader(new AdobeTransport(http));
        var result = await downloader.DownloadAsync(Asset, directory, 10);
        await File.WriteAllBytesAsync(result.Path, [9, 9, 9]);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Asset, directory, 10));
        Assert.Equal(1, handler.Calls);
    }

    private static DownloadPlan Plan => new(1, DateTimeOffset.UtcNow, "win64", "en_US", "10.0", [],
        [new PlannedDownload("0000", "TEST", "1.0", Asset)]);

    [Fact] public async Task QueuePersistsPauseResumesAndRechecksCompletedReceipt()
    {
        await DownloadQueue.CreateAsync(Plan, directory);
        using var cancellation = new CancellationTokenSource();
        using (var http = new HttpClient(new DownloadTests.Handler((_, _) => Broken(cancellation))))
        {
            var queue = new DownloadQueue(new ResumableDownloader(new AdobeTransport(http)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.RunAsync(directory, 10, cancellation.Token));
        }
        var paused = await DownloadQueue.ReadAsync(directory);
        Assert.Equal(QueueItemStatus.Paused, Assert.Single(paused.Items).Status);
        using var handler = new DownloadTests.Handler((r, _) => Remaining(r));
        using var resumedHttp = new HttpClient(handler);
        var resumed = new DownloadQueue(new ResumableDownloader(new AdobeTransport(resumedHttp)));
        var done = await resumed.RunAsync(directory, 10);
        Assert.Equal(QueueItemStatus.Completed, Assert.Single(done.Items).Status);
        await resumed.RunAsync(directory, 10);
        Assert.Equal(1, handler.Calls);
    }

    [Fact] public async Task QueueCreationNeverOverwritesExistingState()
    {
        await DownloadQueue.CreateAsync(Plan, directory);
        await Assert.ThrowsAsync<IOException>(() => DownloadQueue.CreateAsync(Plan, directory));
        Assert.Single((await DownloadQueue.ReadAsync(directory)).Items);
    }

    [Fact] public async Task QueueBudgetCheckedBeforeDownload()
    {
        await DownloadQueue.CreateAsync(Plan, directory);
        using var handler = new DownloadTests.Handler((_, _) => Complete());
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new DownloadQueue(new ResumableDownloader(new AdobeTransport(http))).RunAsync(directory, 2));
        Assert.Equal(0, handler.Calls);
    }

    [Fact] public async Task QueueRejectsTraversalInSavedPlan()
    {
        var plan = Plan with { Downloads = [new("../escape", "TEST", "1.0", Asset)] };
        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadQueue.CreateAsync(plan, directory));
    }

    [Fact] public async Task QueueLockPreventsConcurrentRun()
    {
        await DownloadQueue.CreateAsync(Plan, directory);
        using var locked = new FileStream(Path.Combine(directory, ".queue.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        using var handler = new DownloadTests.Handler((_, _) => Complete());
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<IOException>(() => new DownloadQueue(new ResumableDownloader(new AdobeTransport(http))).RunAsync(directory, 10));
        Assert.Equal(0, handler.Calls);
    }
}
