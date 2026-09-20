using System.Net;
using System.Security.Cryptography;

namespace AdobeDownloader.Core.Tests;

public sealed class DownloadTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "adobe-downloader-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Payload = [1, 2, 3];
    private static PackageAsset Asset => new("test", "test.zip", "core", "64-bit", "", 3,
        new Uri("https://ccmdls.adobe.com/test.zip"), "not-a-file-hash");
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request, ct));
        }
    }
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();
    }

    [Fact] public async Task PublishesCompleteFileAndVerifiedDigest()
    {
        using var http = new HttpClient(new Handler((_, _) => Response(Payload)));
        var hash = Convert.ToHexString(SHA256.HashData(Payload));
        var result = await new PackageDownloader(new AdobeTransport(http)).DownloadAsync(Asset, directory, 10, hash);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.Path));
        Assert.Equal(hash.ToLowerInvariant(), result.Sha256);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact] public async Task RejectsHashMismatchAndDeletesPartial()
    {
        using var http = new HttpClient(new Handler((_, _) => Response(Payload)));
        await Assert.ThrowsAsync<InvalidDataException>(() => new PackageDownloader(new AdobeTransport(http))
            .DownloadAsync(Asset, directory, 10, new string('0', 64)));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory] [InlineData(2)] [InlineData(4)]
    public async Task RejectsIncorrectDeclaredServerLength(int size)
    {
        using var http = new HttpClient(new Handler((_, _) => Response(new byte[size])));
        await Assert.ThrowsAsync<InvalidDataException>(() => new PackageDownloader(new AdobeTransport(http)).DownloadAsync(Asset, directory, 10));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory] [InlineData(2)] [InlineData(4)]
    public async Task ChecksStreamLengthWithoutContentLength(int size)
    {
        using var http = new HttpClient(new Handler((_, _) =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[size]) };
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new PackageDownloader(new AdobeTransport(http)).DownloadAsync(Asset, directory, 10));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact] public async Task DoesNotOverwriteExistingFile()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "test.zip"), "keep");
        using var handler = new Handler((_, _) => Response(Payload));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<IOException>(() => new PackageDownloader(new AdobeTransport(http)).DownloadAsync(Asset, directory, 10));
        Assert.Equal(0, handler.Calls);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(directory, "test.zip")));
    }

    [Fact] public async Task EnforcesLimitBeforeNetworkRequest()
    {
        using var handler = new Handler((_, _) => Response(Payload));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PackageDownloader(new AdobeTransport(http)).DownloadAsync(Asset, directory, 2));
        Assert.Equal(0, handler.Calls);
    }

    [Fact] public async Task CancellationLeavesNoPublishedFile()
    {
        using var cancelled = new CancellationTokenSource();
        using var http = new HttpClient(new Handler((_, _) => { cancelled.Cancel(); return Response(Payload); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PackageDownloader(new AdobeTransport(http))
            .DownloadAsync(Asset, directory, 10, ct: cancelled.Token));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("../bad.zip")] [InlineData("C:\\bad.zip")] [InlineData("file:stream")]
    [InlineData("CON.zip")] [InlineData("name.")] [InlineData("LPT1")]
    public void RejectsUnsafeFilenames(string name) => Assert.Throws<InvalidDataException>(() => PackageDownloader.ValidateFileName(name));

    [Theory]
    [InlineData("http://ccmdls.adobe.com/file")]
    [InlineData("https://adobe.com.evil.example/file")]
    [InlineData("https://eviladobe.com/file")]
    [InlineData("https://user@ccmdls.adobe.com/file")]
    public void RejectsUnofficialUrls(string url) => Assert.Throws<InvalidDataException>(() => AdobeTransport.ValidateUrl(new Uri(url)));

    [Fact] public async Task BlocksForeignRedirectBeforeSecondRequest()
    {
        using var handler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.Redirect)
            { Headers = { Location = new Uri("https://example.com/file") } });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new AdobeTransport(http).GetAsync(Asset.Url, null, default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact] public async Task FollowsAdobeRedirect()
    {
        using var handler = new Handler((request, _) => request.RequestUri!.Host == "ccmdls.adobe.com"
            ? new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://cdn-ffc.oobesaas.adobe.com/test.zip") } }
            : Response(Payload));
        using var http = new HttpClient(handler);
        var result = await new PackageDownloader(new AdobeTransport(http)).DownloadAsync(Asset, directory, 10);
        Assert.Equal(3, result.Bytes);
        Assert.Equal(2, handler.Calls);
    }

    [Fact] public async Task RejectsPartialHttpResponse()
    {
        using var http = new HttpClient(new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.PartialContent)));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new AdobeTransport(http).GetAsync(Asset.Url, null, default));
        Assert.Equal(HttpStatusCode.PartialContent, ex.StatusCode);
    }

    [Fact] public async Task CapsRedirectLoop()
    {
        using var handler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.Redirect)
            { Headers = { Location = Asset.Url } });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => new AdobeTransport(http).GetAsync(Asset.Url, null, default));
        Assert.Equal(6, handler.Calls);
    }

    [Fact] public async Task ManifestRequestIncludesBuildGuidAndWindowsPlatform()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Contains("platform=win64", request.RequestUri!.Query);
            Assert.Contains("version=1.2.3", request.RequestUri.Query);
            Assert.Equal("test-guid", Assert.Single(request.Headers.GetValues("x-adobe-build-guid")));
            Assert.Equal("Creative Cloud_v6_4", Assert.Single(request.Headers.GetValues("x-api-key")));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"SAPCode":"TEST","ProductVersion":"1.2.3","Platform":"win64","Packages":{"Package":{
                "PackageName":"test","Path":"/test.zip","DownloadSize":3}}}
                """) };
        }));
        var manifest = await new ManifestClient(new AdobeTransport(http)).FetchAsync(CatalogTests.Build);
        Assert.Single(manifest.Packages);
    }

    [Fact] public async Task RejectsLegacyManifestBeforeNetworkRequest()
    {
        using var handler = new Handler((_, _) => Response(Payload));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<NotSupportedException>(() => new ManifestClient(new AdobeTransport(http))
            .FetchAsync(CatalogTests.Build with { PackageType = "RIBS" }));
        Assert.Equal(0, handler.Calls);
    }
}
