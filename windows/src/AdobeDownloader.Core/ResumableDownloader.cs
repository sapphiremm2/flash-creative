using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace AdobeDownloader.Core;

public sealed record ResumeState(string Url, long Size, string? EntityTag);
public sealed record DownloadReceipt(string Url, long Size, string Sha256);

/// <summary>Preserves interrupted transfers; reuses bytes only with a matching strong ETag.</summary>
public sealed class ResumableDownloader(AdobeTransport transport)
{
    public async Task<DownloadResult> DownloadAsync(PackageAsset package, string directory, long maxBytes,
        int maxAttempts = 3, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        PackageDownloader.ValidateFileName(package.FileName);
        AdobeTransport.ValidateUrl(package.Url);
        if (package.DownloadSize <= 0 || package.DownloadSize > maxBytes) throw new InvalidDataException("Package exceeds the download limit.");
        if (maxAttempts is < 1 or > 5) throw new ArgumentException("Retry attempts must be between one and five.");
        Directory.CreateDirectory(directory);
        var destination = Path.GetFullPath(Path.Combine(directory, package.FileName));
        await using var guard = new FileStream(destination + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var partial = destination + ".partial";
        var statePath = destination + ".resume.json";
        var receiptPath = destination + ".receipt.json";
        if (File.Exists(destination))
        {
            if (!File.Exists(receiptPath)) throw new IOException("Existing output has no download receipt; refusing to overwrite it.");
            var receipt = await JsonFiles.ReadAsync<DownloadReceipt>(receiptPath, ct);
            var hash = await HashAsync(destination, ct);
            if (receipt.Url != package.Url.AbsoluteUri || receipt.Size != package.DownloadSize ||
                new FileInfo(destination).Length != package.DownloadSize || receipt.Sha256 != hash)
                throw new InvalidDataException("Existing completed download failed receipt verification.");
            return new DownloadResult(destination, package.DownloadSize, hash);
        }

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ResumeState? state = File.Exists(statePath) ? await JsonFiles.ReadAsync<ResumeState>(statePath, ct) : null;
                long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                var validState = state is not null && state.Url == package.Url.AbsoluteUri && state.Size == package.DownloadSize &&
                    EntityTagHeaderValue.TryParse(state.EntityTag, out var tag) && !tag.IsWeak && tag.Tag != "*";
                if (!validState || offset > package.DownloadSize)
                {
                    Reset(); offset = 0; state = null;
                }
                if (offset != package.DownloadSize)
                {
                    using var response = await transport.GetAsync(package.Url, null, ct, offset > 0 ? offset : null, state?.EntityTag);
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (range?.Unit != "bytes" || range.From != offset || range.To != package.DownloadSize - 1 ||
                            range.Length != package.DownloadSize || response.Headers.ETag?.ToString() != state?.EntityTag)
                            throw new InvalidDataException("Resume response does not match the saved ETag, offset, or total size.");
                    }
                    else offset = 0; // If-Range mismatch or a server that ignores Range: restart, never append.
                    if (response.Content.Headers.ContentLength is long length && length != package.DownloadSize - offset)
                        throw new InvalidDataException("Server length differs from the planned package size.");
                    if (response.Content.Headers.ContentEncoding.Count != 0)
                        throw new InvalidDataException("Encoded package transfers cannot be resumed safely.");
                    var etag = response.Headers.ETag is { IsWeak: false } strong && strong.Tag != "*" ? strong.ToString() : null;
                    // Truncate old bytes BEFORE saving a changed validator; a crash must never label old bytes with a new ETag.
                    await using var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Open,
                        FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
                    output.Position = offset;
                    await JsonFiles.WriteAsync(statePath, new ResumeState(package.Url.AbsoluteUri, package.DownloadSize, etag), ct: ct);
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    var buffer = new byte[128 * 1024];
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    while (true)
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(60));
                        var count = await input.ReadAsync(buffer, idle.Token);
                        if (count == 0) break;
                        offset += count;
                        if (offset > package.DownloadSize) throw new InvalidDataException("Package exceeds its declared size.");
                        await output.WriteAsync(buffer.AsMemory(0, count), idle.Token);
                        progress?.Report(offset);
                    }
                    await output.FlushAsync(ct);
                    output.Flush(flushToDisk: true);
                    if (offset != package.DownloadSize) throw new EndOfStreamException("Package transfer ended early; partial file retained.");
                }
                var sha = await HashAsync(partial, ct);
                // Receipt first permits recovery if publication succeeds but the queue checkpoint is interrupted.
                await JsonFiles.WriteAsync(receiptPath, new DownloadReceipt(package.Url.AbsoluteUri, package.DownloadSize, sha), ct: ct);
                ct.ThrowIfCancellationRequested();
                File.Move(partial, destination, overwrite: false);
                File.Delete(statePath);
                return new DownloadResult(destination, package.DownloadSize, sha);
            }
            catch (InvalidDataException) { Reset(); throw; }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && attempt + 1 < maxAttempts)
            { Reset(); }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt + 1 < maxAttempts && IsTransient(ex))
            { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct); }
        }
        throw new IOException("Download retry limit exceeded.");

        void Reset()
        {
            if (File.Exists(partial)) File.Delete(partial);
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    private static bool IsTransient(Exception ex) => ex is EndOfStreamException or OperationCanceledException ||
        ex is HttpRequestException http && (http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500) ||
        ex is IOException and not FileNotFoundException and not DirectoryNotFoundException;

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }
}
