using System.Security.Cryptography;
using System.Text.Json;

namespace AdobeDownloader.Core;

public enum QueueAuditStatus { Verified, Incomplete, Missing, Invalid, Unavailable }
public sealed record QueueAuditItem(string Product, string Package, QueueAuditStatus Status,
    string? Sha256 = null, VerificationResult? Verification = null, string? Error = null);
public sealed record QueueAuditReport(DateTimeOffset CheckedAt, bool Offline, bool RequiresAdobeValidation,
    IReadOnlyList<QueueAuditItem> Items)
{
    public bool Complete => Items.Count > 0 && Items.All(x => x.Status == QueueAuditStatus.Verified);
    public bool AdobeVerified => Complete && Items.All(x => x.Verification?.Method == "AdobeHttpsSegmentSha256");
}

/// <summary>Checks existing queue artifacts without downloading, repairing, or changing queue state.</summary>
public sealed class QueueAuditor(AdobeTransport transport)
{
    public async Task<QueueAuditReport> AuditAsync(string directory, bool offline = false, CancellationToken ct = default)
    {
        directory = Path.GetFullPath(directory);
        // Prevent the queue checkpoint from being replaced while inspecting this snapshot.
        // Open never creates a lock file or a missing queue directory.
        await using var checkpoint = new FileStream(Path.Combine(directory, "queue.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var snapshot = await DownloadQueue.ReadAsync(directory, ct);
        var results = new List<QueueAuditItem>();
        foreach (var item in snapshot.Items)
        {
            ct.ThrowIfCancellationRequested();
            var download = item.Download;
            var package = download.Package;
            var path = Path.Combine(directory, "packages", download.DirectoryName, package.FileName);
            QueueAuditItem Result(QueueAuditStatus status, string message) => new(download.SapCode, package.Name, status, Error: message);
            if (item.Status != QueueItemStatus.Completed)
            {
                results.Add(Result(QueueAuditStatus.Incomplete, $"Queue item is {item.Status}; audit does not resume it."));
                continue;
            }
            if (!File.Exists(path) || !File.Exists(path + ".receipt.json"))
            {
                results.Add(Result(QueueAuditStatus.Missing, "Completed payload or its receipt is missing."));
                continue;
            }
            try
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var receipt = await JsonFiles.ReadAsync<DownloadReceipt>(path + ".receipt.json", ct);
                if (file.Length != package.DownloadSize || receipt.Size != package.DownloadSize || receipt.Url != package.Url.AbsoluteUri)
                    throw new InvalidDataException("Receipt identity or file size differs from the plan.");
                var sha = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
                if (!sha.Equals(receipt.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    !sha.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("File hash differs from its receipt or completed queue checkpoint.");
                var verification = !offline && !string.IsNullOrWhiteSpace(package.ValidationUrl)
                    ? await new AdobePackageVerifier(transport).VerifyAsync(package, path, ct)
                    : new VerificationResult("LocalSha256ReceiptOnly", "", 0);
                results.Add(new(download.SapCode, package.Name, QueueAuditStatus.Verified, sha, verification));
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or System.Xml.XmlException or OverflowException)
            { results.Add(Result(QueueAuditStatus.Invalid, ex.Message)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException ||
                ex is OperationCanceledException && !ct.IsCancellationRequested)
            { results.Add(Result(QueueAuditStatus.Unavailable, ex.Message)); }
        }
        return new QueueAuditReport(DateTimeOffset.UtcNow, offline, snapshot.Plan.RequiresAdobeValidation, results);
    }
}
