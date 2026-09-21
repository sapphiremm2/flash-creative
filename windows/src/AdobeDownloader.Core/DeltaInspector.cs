using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdobeDownloader.Core;

public sealed record DeltaInstruction(string Action, string Source, string Destination, string FileType,
    long Size, string Hash, IReadOnlyList<string> ExtraFields);
public sealed record DeltaInspection(string Product, string Package, string TargetVersion, string BaseVersion,
    long FullBytes, long DeltaBytes, Uri MetadataUrl, string MetadataSha256, int ValidationSegments,
    IReadOnlyDictionary<string, int> Actions, IReadOnlyList<string> DestinationVariables,
    IReadOnlyList<string> ExtraFields, VerificationResult? PayloadVerification, bool EmbeddedMetadataMatched,
    bool InstalledBaselineVerified, bool CanApply, string Limitation);

/// <summary>Reads delta metadata only. Does not resolve local paths, select a delta, or modify an installation.</summary>
public sealed class DeltaInspector(AdobeTransport transport)
{
    public async Task<DeltaInspection> InspectAsync(DownloadPlan plan, string product, string packageName,
        string baseVersion, CancellationToken ct = default, string? archivePath = null)
    {
        DownloadQueue.Validate(plan);
        var matches = plan.Downloads.Where(d => d.SapCode.Equals(product, StringComparison.OrdinalIgnoreCase) &&
            d.Package.Name.Equals(packageName, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Expected exactly one selected full package in the saved plan.");
        var download = matches[0];
        var deltas = (download.Package.Deltas ?? []).Where(d => d.BaseVersion == baseVersion).ToArray();
        if (deltas.Length != 1) throw new InvalidDataException("Expected exactly one delta with this exact base package version.");
        var delta = deltas[0];
        if (delta.MetadataUrl is null || string.IsNullOrWhiteSpace(delta.ValidationUrl))
            throw new InvalidDataException("Delta metadata and validation URLs are required.");
        AdobeTransport.ValidateUrl(delta.Url);
        var metadata = await transport.GetTextAsync(AdobeTransport.ValidateUrl(delta.MetadataUrl), null, ct);
        var instructions = Parse(metadata);
        var validationXml = await transport.GetTextAsync(AdobeTransport.ValidateUrl(new Uri(delta.ValidationUrl)), null, ct);
        var validation = AdobePackageVerifier.Parse(validationXml, delta.DownloadSize, "");
        VerificationResult? payloadVerification = null;
        if (archivePath is not null)
        {
            // Hold the archive without write/delete sharing for verification and metadata comparison.
            await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await AdobePackageVerifier.VerifySegmentsAsync(archivePath, validation, ct);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            var expectedName = download.Package.Name + "_diff.json";
            var entries = archive.Entries.Where(e => e.FullName.Equals(expectedName, StringComparison.Ordinal)).ToArray();
            if (entries.Length != 1 || entries[0].Length > 32 * 1024 * 1024)
                throw new InvalidDataException("Expected one bounded embedded delta metadata file.");
            await using var input = entries[0].Open();
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await input.ReadAsync(buffer, ct);
                if (count == 0) break;
                if (content.Length + count > 32 * 1024 * 1024) throw new InvalidDataException("Embedded delta metadata exceeds the size limit.");
                content.Write(buffer, 0, count);
            }
            var embedded = new UTF8Encoding(false, true).GetString(content.ToArray());
            if (!embedded.Equals(metadata, StringComparison.Ordinal))
                throw new InvalidDataException("Embedded delta metadata differs from the inspected Adobe metadata.");
            payloadVerification = new VerificationResult("AdobeHttpsSegmentSha256", delta.ValidationUrl, validation.Hashes.Count);
        }
        return new DeltaInspection(download.SapCode, download.Package.Name, download.ProductVersion, delta.BaseVersion,
            download.Package.DownloadSize, delta.DownloadSize, delta.MetadataUrl,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(metadata))).ToLowerInvariant(), validation.Hashes.Count,
            instructions.GroupBy(x => x.Action).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            instructions.Select(x => x.Destination[..(x.Destination.IndexOf(']') + 1)]).Distinct(StringComparer.Ordinal).Order().ToArray(),
            instructions.SelectMany(x => x.ExtraFields).Distinct(StringComparer.Ordinal).Order().ToArray(), payloadVerification, payloadVerification is not null, false, false,
            payloadVerification is null
                ? "Metadata inspection only: no installed baseline or delta bytes were verified, and no patch operations were applied. File hashes are not assumed to identify pre-patch contents."
                : "Delta bytes and embedded metadata verified; installed baseline is unverified and no patch operations were applied. File hashes are not assumed to identify pre-patch contents.");
    }

    public static IReadOnlyList<DeltaInstruction> Parse(string json)
    {
        if (json.Length > 32 * 1024 * 1024) throw new InvalidDataException("Delta metadata exceeds the size limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is < 1 or > 100000)
            throw new InvalidDataException("Delta metadata must be a bounded nonempty instruction array.");
        var result = new List<DeltaInstruction>();
        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid delta instruction.");
            var properties = entry.EnumerateObject().ToArray();
            if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                throw new InvalidDataException("Duplicate delta instruction property.");
            string Text(string key, bool required = true)
            {
                if (!entry.TryGetProperty(key, out var value))
                    return required ? throw new InvalidDataException($"Missing delta {key}.") : "";
                return value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() :
                    throw new InvalidDataException($"Invalid delta {key}.");
            }
            var action = Text("deltaAction"); var kind = Text("fileType");
            if (action is not ("ADD" or "DELETE" or "PATCH" or "EXISTS") || kind is not ("FILE" or "DIRECTORY"))
                throw new NotSupportedException($"Unsupported delta instruction: {action}/{kind}.");
            if (action == "PATCH" && kind != "FILE") throw new InvalidDataException("Only files can be patched.");
            var source = Text("source"); var destination = Text("destination");
            ValidateRelativePath(source, allowEmpty: false, directory: kind == "DIRECTORY");
            var match = Regex.Match(destination, @"^\[[A-Za-z][A-Za-z0-9_]*\](?:[\\/](.*))?$");
            if (destination.Length > 32767 || !match.Success || match.Length != destination.Length) throw new InvalidDataException("Delta destination must begin with a symbolic directory.");
            ValidateRelativePath(match.Groups[1].Value, allowEmpty: kind == "DIRECTORY", directory: kind == "DIRECTORY");
            if (!long.TryParse(Text("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 0)
                throw new InvalidDataException("Invalid delta file size.");
            var hash = Text("hash", required: false);
            if (hash.Length > 0 && !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Unsupported delta file hash.");
            var extra = properties.Select(p => p.Name).Except(["source", "destination", "deltaAction", "fileType", "size", "hash"], StringComparer.Ordinal).ToArray();
            result.Add(new DeltaInstruction(action, source, destination, kind, size, hash, extra));
        }
        return result;
    }

    private static void ValidateRelativePath(string path, bool allowEmpty, bool directory)
    {
        if (path.Length > 32767 || path.StartsWith('/') || path.StartsWith('\\')) throw new InvalidDataException("Invalid delta relative path.");
        var normalized = path.Replace('\\', '/');
        if (directory && normalized.EndsWith('/')) normalized = normalized[..^1];
        if (allowEmpty && normalized.Length == 0) return;
        foreach (var component in normalized.Split('/')) PackageDownloader.ValidateFileName(component);
    }
}
