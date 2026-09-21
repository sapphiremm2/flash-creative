using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AdobeDownloader.Core;

public sealed class PackageDownloader(AdobeTransport transport)
{
    public static void ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name is "." or ".." ||
            name.EndsWith('.') || name.EndsWith(' ') || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) ||
            Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase))
            throw new InvalidDataException("Package filename is not a safe Windows filename.");
    }

    public async Task<DownloadResult> DownloadAsync(PackageAsset package, string directory,
        long maxBytes, string? expectedSha256 = null, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        ValidateFileName(package.FileName);
        AdobeTransport.ValidateUrl(package.Url);
        if (package.DownloadSize <= 0 || package.DownloadSize > maxBytes)
            throw new InvalidDataException($"Package size {package.DownloadSize} exceeds the download limit {maxBytes}, or is invalid.");
        if (expectedSha256 is not null && !Regex.IsMatch(expectedSha256, "^[a-fA-F0-9]{64}$"))
            throw new ArgumentException("Expected SHA-256 must contain exactly 64 hexadecimal characters.");
        Directory.CreateDirectory(directory);
        var destination = Path.GetFullPath(Path.Combine(directory, package.FileName));
        if (File.Exists(destination)) throw new IOException($"Destination already exists: {destination}");
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var response = await transport.GetAsync(package.Url, null, ct);
            if (response.Content.Headers.ContentLength is long length && length != package.DownloadSize)
                throw new InvalidDataException("Server Content-Length does not match the package manifest.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytes = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                while (true)
                {
                    idleTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                    var count = await input.ReadAsync(buffer, idleTimeout.Token);
                    if (count == 0) break;
                    bytes += count;
                    if (bytes > package.DownloadSize) throw new InvalidDataException("Package is larger than its declared size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), idleTimeout.Token);
                    progress?.Report(bytes);
                }
                await output.FlushAsync(ct);
            }
            if (bytes != package.DownloadSize) throw new InvalidDataException("Package download is incomplete.");
            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (expectedSha256 is not null && !sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Downloaded package failed SHA-256 verification.");
            var verification = package.ValidationUrl.Length > 0
                ? await new AdobePackageVerifier(transport).VerifyAsync(package, partial, ct)
                : new VerificationResult(expectedSha256 is null ? "LocalSha256ReceiptOnly" : "SuppliedSha256", "", 0);
            ct.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: false);
            return new DownloadResult(destination, bytes, sha256, verification);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }
}
