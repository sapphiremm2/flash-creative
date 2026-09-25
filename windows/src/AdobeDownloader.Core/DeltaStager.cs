using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Compressors.LZMA;

namespace AdobeDownloader.Core;

public sealed record DeltaStageResult(string Path, string Product, string Package, string BaseVersion,
    string TargetVersion, int Files, long Bytes, int PatchedFiles, VerificationResult BaselineVerification,
    VerificationResult DeltaVerification, bool Installed = false);

/// <summary>Reconstructs an archive-backed update into a NEW symbolic payload tree. Never modifies an installation.</summary>
public sealed class DeltaStager(AdobeTransport transport)
{
    public async Task<DeltaStageResult> StageAsync(DownloadPlan targetPlan, DownloadPlan baselinePlan, string product,
        string packageName, string baselineArchive, string deltaArchive, string destination, long maxBytes,
        CancellationToken ct = default)
    {
        DownloadQueue.Validate(targetPlan); DownloadQueue.Validate(baselinePlan);
        if (maxBytes <= 0) throw new ArgumentException("A positive staging byte limit is required.");
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Staging destination must not exist.");
        var target = Select(targetPlan); var baseline = Select(baselinePlan);
        if (targetPlan.Platform != baselinePlan.Platform || targetPlan.Locale != baselinePlan.Locale)
            throw new InvalidDataException("Baseline and target must use the same platform and locale.");
        var targetPackage = await Refresh(targetPlan, target);
        var baselinePackage = await Refresh(baselinePlan, baseline);
        if (string.IsNullOrWhiteSpace(baselinePackage.PackageVersion)) throw new InvalidDataException("Baseline package version is required.");
        var deltas = (targetPackage.Deltas ?? []).Where(d => d.BaseVersion == baselinePackage.PackageVersion).ToArray();
        if (deltas.Length != 1) throw new InvalidDataException("No unique delta matches the verified baseline package version.");
        if (baselinePackage.CompressionType is not ("zip" or "zip-lzma2")) throw new NotSupportedException("Unsupported baseline compression type.");
        await using var baselineFile = new FileStream(baselineArchive, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var deltaFile = new FileStream(deltaArchive, FileMode.Open, FileAccess.Read, FileShare.Read);
        var baselineVerification = await new AdobePackageVerifier(transport).VerifyAsync(baselinePackage, baselineArchive, ct);
        var refreshedPlan = targetPlan with { Downloads = targetPlan.Downloads.Select(d => d == target ? d with { Package = targetPackage } : d).ToArray() };
        var inspection = await new DeltaInspector(transport).InspectAsync(refreshedPlan, product, packageName,
            baselinePackage.PackageVersion, ct, deltaArchive);
        if (inspection.PayloadVerification is null || !inspection.EmbeddedMetadataMatched) throw new InvalidDataException("Delta archive verification is required.");
        using var oldZip = new ZipArchive(baselineFile, ZipArchiveMode.Read, leaveOpen: true);
        using var patchZip = new ZipArchive(deltaFile, ZipArchiveMode.Read, leaveOpen: true);
        var oldEntries = Index(oldZip); var patchEntries = Index(patchZip);
        var metadata = await ReadBounded(Entry(patchEntries, packageName + "_diff.json"), 32 * 1024 * 1024, ct);
        if (!Convert.ToHexString(SHA256.HashData(metadata)).Equals(inspection.MetadataSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Delta metadata changed after verification.");
        var instructions = DeltaInspector.Parse(new UTF8Encoding(false, true).GetString(metadata));
        if (instructions.Any(i => i.ExtraFields.Any(f => f != "externalAttributes")))
            throw new NotSupportedException("Staging cannot apply preference, permanence, recursive-delete, or unknown instruction flags.");
        var selected = new List<DeltaInstruction>();
        var baselines = new Dictionary<string, DeltaInstruction>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in instructions.GroupBy(i => i.Destination.TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToArray();
            if (items.Length == 1 && items[0].Action != "PATCH")
            {
                if (items[0].Action != "DELETE") selected.Add(items[0]);
                continue;
            }
            if (items.Length != 2 || items.Select(i => i.Action).Distinct().Count() != 2) throw new InvalidDataException("Ambiguous delta destination.");
            var patch = items.SingleOrDefault(i => i.Action == "PATCH");
            var old = items.SingleOrDefault(i => i.Action == "EXISTS");
            if (patch is not null && old is not null && patch.FileType == "FILE" && old.FileType == "FILE" &&
                patch.Source == old.Source + ".diff" && old.Hash.Length == 64)
            { selected.Add(patch); baselines.Add(patch.Destination, old); continue; }
            var add = items.SingleOrDefault(i => i.Action == "ADD");
            if (add is not null && items.Any(i => i.Action == "DELETE")) { selected.Add(add); continue; }
            throw new InvalidDataException("Unsupported delta action combination.");
        }
        var final = selected.ToArray();
        var files = final.Where(i => i.FileType == "FILE").ToArray();
        if (files.Length == 0 || files.Any(i => i.Hash.Length != 64)) throw new InvalidDataException("Every reconstructed file needs an expected SHA-256.");
        var total = files.Aggregate(0L, (sum, i) => checked(sum + i.Size));
        if (total > maxBytes) throw new InvalidDataException("Reconstructed payload exceeds the staging limit.");
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var largestOld = baselines.Values.Select(i => i.Size).DefaultIfEmpty().Max();
        if (largestOld > maxBytes) throw new InvalidDataException("Baseline file exceeds the staging limit.");
        if (new DriveInfo(Path.GetPathRoot(parent)!).AvailableFreeSpace < checked(total + largestOld))
            throw new IOException("Insufficient disk space for staged files and one baseline temporary file.");
        var scratch = Path.GetFullPath(Path.Combine(parent, ".flash-stage-" + Guid.NewGuid().ToString("N")));
        if (!scratch.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid staging workspace.");
        Directory.CreateDirectory(scratch);
        try
        {
            var payload = Path.Combine(scratch, "payload"); Directory.CreateDirectory(payload);
            foreach (var instruction in final)
            {
                ct.ThrowIfCancellationRequested();
                var path = TargetPath(payload, instruction.Destination);
                if (instruction.FileType == "DIRECTORY") { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (instruction.Action == "PATCH")
                    {
                        if (!instruction.Source.EndsWith(".diff", StringComparison.Ordinal)) throw new InvalidDataException("Unsupported patch source naming.");
                        var oldEntry = Entry(oldEntries, instruction.Source[..^5]);
                        if (oldEntry.Length > maxBytes) throw new InvalidDataException("Baseline file exceeds the staging limit.");
                        var oldPath = Path.Combine(scratch, "baseline.tmp");
                        await using (var oldOutput = new FileStream(oldPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            await CopyBaseline(oldEntry, oldOutput, baselines[instruction.Destination].Size, baselinePackage.CompressionType, ct);
                        try
                        {
                            await using var oldInput = File.OpenRead(oldPath);
                            if (!Convert.ToHexString(await SHA256.HashDataAsync(oldInput, ct)).Equals(baselines[instruction.Destination].Hash, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Patch baseline hash mismatch.");
                            oldInput.Position = 0;
                            var patch = await ReadBounded(Entry(patchEntries, instruction.Source), 256 * 1024 * 1024, ct);
                            BsdiffPatch.Apply(oldInput, patch, output, instruction.Size, ct);
                        }
                        finally { File.Delete(oldPath); }
                    }
                    else if (instruction.Action == "ADD") await CopyExact(Entry(patchEntries, instruction.Source), output, instruction.Size, ct);
                    else await CopyBaseline(Entry(oldEntries, instruction.Source), output, instruction.Size, baselinePackage.CompressionType, ct);
                }
                await using var verify = File.OpenRead(path);
                if (verify.Length != instruction.Size || !Convert.ToHexString(await SHA256.HashDataAsync(verify, ct)).Equals(instruction.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Reconstructed file failed target verification: {instruction.Destination}");
            }
            var receipt = new DeltaStageResult(destination, product, packageName, baselinePackage.PackageVersion,
                targetPackage.PackageVersion, files.Length, total, files.Count(i => i.Action == "PATCH"), baselineVerification, inspection.PayloadVerification);
            await JsonFiles.WriteAsync(Path.Combine(scratch, "stage-receipt.json"), receipt, overwrite: false, ct: ct);
            ct.ThrowIfCancellationRequested();
            Directory.Move(scratch, destination);
            return receipt;
        }
        finally
        {
            // This is the exclusively created sibling workspace, never a supplied installation path.
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }

        PlannedDownload Select(DownloadPlan plan)
        {
            var found = plan.Downloads.Where(d => d.SapCode == product && d.Package.Name == packageName).ToArray();
            return found.Length == 1 ? found[0] : throw new InvalidDataException("Expected one selected package in each plan.");
        }
        async Task<PackageAsset> Refresh(DownloadPlan plan, PlannedDownload download)
        {
            var builds = plan.Products.Where(p => p.Build.SapCode == product && p.Build.ProductVersion == download.ProductVersion).ToArray();
            if (builds.Length != 1) throw new InvalidDataException("Package build identity is missing or ambiguous.");
            var fresh = await new ManifestClient(transport).FetchAsync(builds[0].Build, ct);
            var package = fresh.Packages.SingleOrDefault(p => p.Name == packageName) ?? throw new InvalidDataException("Package is absent from the current Adobe manifest.");
            if (package.Url != download.Package.Url || package.DownloadSize != download.Package.DownloadSize)
                throw new InvalidDataException("Saved plan differs from Adobe's current package manifest.");
            return package;
        }
    }

    private static Dictionary<string, ZipArchiveEntry> Index(ZipArchive archive)
    {
        if (archive.Entries.Count > 100000) throw new InvalidDataException("Archive entry limit exceeded.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/').TrimEnd('/');
            foreach (var component in name.Split('/')) PackageDownloader.ValidateFileName(component);
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & 0x400) != 0 || !entries.TryAdd(name, entry))
                throw new InvalidDataException("Archive has links, reparse points, or ambiguous paths.");
        }
        return entries;
    }
    private static ZipArchiveEntry Entry(Dictionary<string, ZipArchiveEntry> entries, string source) =>
        entries.TryGetValue(source.Replace('\\', '/').TrimEnd('/'), out var entry) ? entry : throw new InvalidDataException($"Required archive entry is absent: {source}");
    private static string TargetPath(string root, string symbolic)
    {
        var end = symbolic.IndexOf(']'); var variable = symbolic[1..end];
        PackageDownloader.ValidateFileName(variable);
        var relative = symbolic[(end + 1)..].TrimStart('\\', '/').Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, variable, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Staged path escapes its root.");
        return path;
    }
    private static async Task<byte[]> ReadBounded(ZipArchiveEntry entry, int limit, CancellationToken ct)
    {
        if (entry.Length > limit) throw new InvalidDataException("Archive entry exceeds the memory limit.");
        using var output = new MemoryStream(); await CopyExact(entry, output, entry.Length, ct); return output.ToArray();
    }
    private static async Task CopyBaseline(ZipArchiveEntry entry, Stream output, long expected, string compression, CancellationToken ct)
    {
        if (compression == "zip") { await CopyExact(entry, output, expected, ct); return; }
        await using var input = entry.Open();
        var property = input.ReadByte();
        // Cap the decoder dictionary at 64 MiB before allocating it.
        if (property < 0 || property > 28) throw new InvalidDataException("Unsupported LZMA2 dictionary size.");
        using var decoded = LzmaStream.Create([(byte)property], input, entry.Length - 1, expected);
        var buffer = new byte[128 * 1024]; long total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = decoded.Read(buffer, 0, buffer.Length); if (read == 0) break;
            total += read; if (total > expected) throw new InvalidDataException("Decoded baseline exceeds its expected size.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (total != expected) throw new InvalidDataException("Decoded baseline length mismatch.");
    }
    private static async Task CopyExact(ZipArchiveEntry entry, Stream output, long expected, CancellationToken ct)
    {
        if (entry.Length != expected) throw new InvalidDataException("Archive entry length differs from expected content.");
        await using var input = entry.Open(); var buffer = new byte[128 * 1024]; long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct); if (read == 0) break;
            total += read; if (total > expected) throw new InvalidDataException("Archive entry expands beyond its declared size.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (total != expected) throw new InvalidDataException("Truncated archive entry.");
    }
}
