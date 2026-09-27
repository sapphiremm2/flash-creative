using System.IO.Compression;
using System.Security.Cryptography;
using SharpCompress.Compressors.LZMA;

namespace AdobeDownloader.Core;

public sealed record StagedInstallFile(string RelativePath, string Target, long Bytes, string Sha256);
public sealed record InstallStageReceipt(int Version, WindowsInstallPlan Plan, VerificationResult Verification,
    IReadOnlyList<StagedInstallFile> Files, long Bytes, bool Installed = false);

/// <summary>Freshly verifies and decodes full-package assets into a NEW private staging directory.
/// The receipt is inventory, not authorization for an elevated helper.</summary>
public sealed class InstallStager(AdobeTransport transport)
{
    public async Task<InstallStageReceipt> StageAsync(DownloadPlan downloadPlan, string product, string package,
        string archivePath, IReadOnlyDictionary<string, string> variables, string destination, long maxBytes, CancellationToken ct = default)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        destination = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        NoLinks(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Stage destination must be new.");
        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidDataException("Stage destination needs a parent.");
        if (!Directory.Exists(parent)) throw new InvalidDataException("Stage parent must already exist.");
        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var inspection = await new InstallInspector(transport).InspectAsync(downloadPlan, product, package, archivePath, ct);
        if (inspection.CompressionType is not ("zip" or "zip-lzma2")) throw new NotSupportedException("Unsupported package compression.");
        var values = new Dictionary<string, string>(variables) {
            ["OSVersion"] = downloadPlan.OsVersion, ["IsEnterpriseDeployment"] = downloadPlan.IsEnterpriseDeployment ? "true" : "false" };
        if (!values.TryGetValue("StagingFolder", out var stagingRoot)) throw new InvalidDataException("StagingFolder is required.");
        var plan = InstallAssetExpander.Expand(WindowsInstallPlanner.Create(inspection, values, downloadPlan.Locale), file, stagingRoot);
        if (!plan.Applicable || plan.Blockers.Any(b => b.Kind.StartsWith("Assets/", StringComparison.Ordinal) || b.Kind == "ArchiveLayout"))
            throw new InvalidDataException("Cannot stage an inapplicable or incomplete asset map.");
        // Unsupported commands remain visible in the receipt and prevent later installation, not read-only decoding.
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var entries = zip.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);
        var scratch = Path.Combine(parent, ".flash-install-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var staged = new List<StagedInstallFile>(); long total = 0;
            foreach (var asset in plan.Files!)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[asset.ArchiveEntry];
                var relative = staged.Count.ToString("D6") + ".payload";
                var outputPath = Path.Combine(scratch, relative);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using var encoded = entry.Open();
                using var decoded = Decoder(encoded, entry.Length, inspection.CompressionType);
                var buffer = new byte[128 * 1024]; long length = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int count;
                    try { count = await decoded.ReadAsync(buffer, ct); }
                    catch (Exception ex) when (ex.GetType().Assembly == typeof(LzmaStream).Assembly)
                    { throw new InvalidDataException("Invalid LZMA2 asset.", ex); }
                    if (count == 0) break;
                    if (count > maxBytes - total) throw new InvalidDataException("Decoded assets exceed the staging byte limit.");
                    total += count; length += count; hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                await output.FlushAsync(ct); output.Flush(true);
                staged.Add(new(relative, asset.Target, length, Convert.ToHexString(hash.GetHashAndReset())));
            }
            var receipt = new InstallStageReceipt(1, plan, inspection.Verification, staged, total);
            await JsonFiles.WriteAsync(Path.Combine(scratch, "stage.json"), receipt, overwrite: false, ct);
            ct.ThrowIfCancellationRequested(); NoLinks(destination);
            Directory.Move(scratch, destination);
            return receipt;
        }
        finally
        {
            // Scratch is an owned GUID sibling, never a path obtained from package metadata.
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }
    private static Stream Decoder(Stream encoded, long length, string compression)
    {
        if (compression == "zip") return encoded;
        var property = encoded.ReadByte();
        if (property < 0 || property > 28) throw new InvalidDataException("Unsupported LZMA2 dictionary size.");
        try { return LzmaStream.Create([(byte)property], encoded, length - 1); }
        catch (Exception ex) when (ex.GetType().Assembly == typeof(LzmaStream).Assembly)
        { throw new InvalidDataException("Invalid LZMA2 asset header.", ex); }
    }
    private static void NoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Staging cannot traverse reparse points."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
}
