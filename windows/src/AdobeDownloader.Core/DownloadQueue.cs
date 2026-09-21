using System.Text.RegularExpressions;

namespace AdobeDownloader.Core;

public enum QueueItemStatus { Pending, Downloading, Paused, Failed, Completed }
public sealed record QueueItem(PlannedDownload Download, QueueItemStatus Status = QueueItemStatus.Pending,
    string? Sha256 = null, string? Error = null, VerificationResult? Verification = null);
public sealed record QueueSnapshot(int SchemaVersion, DownloadPlan Plan, List<QueueItem> Items);

public sealed class DownloadQueue(ResumableDownloader downloader)
{
    public static async Task CreateAsync(DownloadPlan plan, string directory, CancellationToken ct = default)
    {
        Validate(plan);
        Directory.CreateDirectory(directory);
        await JsonFiles.WriteAsync(Path.Combine(directory, "queue.json"),
            new QueueSnapshot(1, plan, plan.Downloads.Select(x => new QueueItem(x)).ToList()), overwrite: false, ct: ct);
    }

    public static async Task<QueueSnapshot> ReadAsync(string directory, CancellationToken ct = default)
    {
        var snapshot = await JsonFiles.ReadAsync<QueueSnapshot>(Path.Combine(directory, "queue.json"), ct);
        if (snapshot.SchemaVersion != 1 || snapshot.Items is null) throw new InvalidDataException("Unsupported queue schema.");
        Validate(snapshot.Plan);
        if (snapshot.Items.Count != snapshot.Plan.Downloads.Count || snapshot.Items.Any(x => x is null || x.Download is null || x.Download.Package is null))
            throw new InvalidDataException("Queue items do not match the saved plan.");
        for (var i = 0; i < snapshot.Items.Count; i++)
        {
            var item = snapshot.Items[i]; var expected = snapshot.Plan.Downloads[i];
            if (item.Download.DirectoryName != expected.DirectoryName || item.Download.Package.Url != expected.Package.Url ||
                item.Download.Package.DownloadSize != expected.Package.DownloadSize || item.Download.Package.FileName != expected.Package.FileName ||
                item.Download.Package.ValidationUrl != expected.Package.ValidationUrl ||
                item.Download.Package.OpaqueHashKey != expected.Package.OpaqueHashKey || !Enum.IsDefined(item.Status)) throw new InvalidDataException("Queue item identity differs from the saved plan.");
        }
        return snapshot;
    }

    public async Task<QueueSnapshot> RunAsync(string directory, long maxBytes, CancellationToken ct = default)
    {
        directory = Path.GetFullPath(directory);
        await using var guard = new FileStream(Path.Combine(directory, ".queue.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var snapshot = await ReadAsync(directory, ct);
        if (snapshot.Plan.TotalBytes > maxBytes) throw new InvalidDataException($"Queue requires {snapshot.Plan.TotalBytes} bytes; limit is {maxBytes}.");
        // Conservative preflight: reserve the full remaining size rather than trusting partial state before validation.
        var needed = snapshot.Items.Where(x => x.Status != QueueItemStatus.Completed).Sum(x => x.Download.Package.DownloadSize);
        var drive = new DriveInfo(Path.GetPathRoot(directory)!);
        if (drive.AvailableFreeSpace < needed) throw new IOException($"Not enough disk space: need {needed} bytes.");
        for (var i = 0; i < snapshot.Items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = snapshot.Items[i];
            snapshot.Items[i] = item with { Status = QueueItemStatus.Downloading, Error = null, Verification = null };
            await Save();
            try
            {
                var result = await downloader.DownloadAsync(item.Download.Package,
                    Path.Combine(directory, "packages", item.Download.DirectoryName), maxBytes, ct: ct);
                snapshot.Items[i] = item with { Status = QueueItemStatus.Completed, Sha256 = result.Sha256, Error = null, Verification = result.Verification };
                await Save();
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or HttpRequestException or System.Xml.XmlException or OverflowException)
            {
                snapshot.Items[i] = item with { Status = ct.IsCancellationRequested ? QueueItemStatus.Paused : QueueItemStatus.Failed, Error = ex.Message, Verification = null };
                await Save();
                throw;
            }
        }
        return snapshot;
        Task Save() => JsonFiles.WriteAsync(Path.Combine(directory, "queue.json"), snapshot);
    }

    public static void Validate(DownloadPlan plan)
    {
        if (plan is null || plan.SchemaVersion != 1 || plan.Downloads is null || plan.Products is null ||
            plan.Downloads.Count is < 1 or > 10000) throw new InvalidDataException("Invalid download plan.");
        CatalogClient.ValidatePlatform(plan.Platform);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan.Downloads)
        {
            if (item is null || item.Package is null || item.DirectoryName is null || !Regex.IsMatch(item.DirectoryName, "^[0-9]{4,6}$") ||
                !paths.Add(item.DirectoryName)) throw new InvalidDataException("Plan package directories must be unique numeric identifiers.");
            PackageDownloader.ValidateFileName(item.Package.FileName);
            AdobeTransport.ValidateUrl(item.Package.Url);
            if (plan.RequiresAdobeValidation && string.IsNullOrWhiteSpace(item.Package.ValidationUrl))
                throw new InvalidDataException("This plan requires Adobe validation metadata for every package.");
            if (!string.IsNullOrEmpty(item.Package.ValidationUrl)) AdobeTransport.ValidateUrl(new Uri(item.Package.ValidationUrl));
            if (item.Package.DownloadSize <= 0) throw new InvalidDataException("Invalid package size in plan.");
        }
        _ = plan.TotalBytes;
    }
}
