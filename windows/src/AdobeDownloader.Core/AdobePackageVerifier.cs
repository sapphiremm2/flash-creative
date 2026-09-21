using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AdobeDownloader.Core;

public sealed record SegmentValidation(long SegmentSize, long LastSegmentSize, string PackageHashKey, IReadOnlyList<string> Hashes);
public sealed record VerificationResult(string Method, string Source, int Segments);

public sealed class AdobePackageVerifier(AdobeTransport transport)
{
    public async Task<VerificationResult> VerifyAsync(PackageAsset package, string file, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(package.ValidationUrl)) throw new InvalidDataException("Adobe SHA-256 validation metadata is required.");
        var url = AdobeTransport.ValidateUrl(new Uri(package.ValidationUrl));
        var xml = await transport.GetTextAsync(url, null, ct);
        var info = Parse(xml, package.DownloadSize, package.OpaqueHashKey);
        await VerifySegmentsAsync(file, info, ct);
        return new VerificationResult("AdobeHttpsSegmentSha256", url.AbsoluteUri, info.Hashes.Count);
    }

    public static SegmentValidation Parse(string xml, long expectedSize, string expectedKey)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        var root = XDocument.Load(reader).Root;
        if (root?.Name != "validationInfo") throw new InvalidDataException("Unexpected Adobe validation response.");
        string Text(string name)
        {
            var elements = root.Elements(name).ToArray();
            if (elements.Length != 1) throw new InvalidDataException($"Missing or duplicate validation {name}.");
            return elements[0].Value.Trim();
        }
        if (Text("version") != "1.0") throw new InvalidDataException("Unsupported Adobe validation version.");
        long Number(string name) => long.TryParse(Text(name), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n : throw new InvalidDataException($"Invalid validation {name}.");
        if (Text("algorithm") is not ("TYPE2" or "SHA256" or "sha256" or "SHA-256"))
            throw new InvalidDataException("Only explicit SHA-256 Adobe validation is supported.");
        var size = Number("segmentSize"); var last = Number("lastSegmentSize"); var count = Number("segmentCount");
        if (count > 100000 || last > size || checked((count - 1) * size + last) != expectedSize)
            throw new InvalidDataException("Validation metadata does not cover the exact package size.");
        var keyElements = root.Elements("packageHashKey").ToArray();
        if (keyElements.Length > 1) throw new InvalidDataException("Duplicate validation packageHashKey.");
        var key = keyElements.SingleOrDefault()?.Value.Trim() ?? "";
        // Delta validation responses omit this field. A supplied manifest key remains mandatory.
        if (expectedKey.Length > 0 && !key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Validation packageHashKey differs from the selected manifest.");
        if (root.Elements("segments").Count() != 1) throw new InvalidDataException("Missing or duplicate validation segments.");
        var nodes = root.Element("segments")?.Elements("segment").ToArray() ?? [];
        if (nodes.Length != count) throw new InvalidDataException("Validation segment count mismatch.");
        var hashes = new string[(int)count];
        foreach (var node in nodes)
        {
            if (!int.TryParse((string?)node.Attribute("segmentNumber"), out var number) || number < 1 || number > count || hashes[number - 1] is not null ||
                !Regex.IsMatch(node.Value.Trim(), "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Invalid or duplicate validation segment.");
            hashes[number - 1] = node.Value.Trim();
        }
        return new SegmentValidation(size, last, key, hashes);
    }

    public static async Task VerifySegmentsAsync(string path, SegmentValidation info, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(path);
        if (file.Length != checked((info.Hashes.Count - 1) * info.SegmentSize + info.LastSegmentSize))
            throw new InvalidDataException("File size differs from Adobe validation metadata.");
        var buffer = new byte[128 * 1024];
        for (var i = 0; i < info.Hashes.Count; i++)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var remaining = i == info.Hashes.Count - 1 ? info.LastSegmentSize : info.SegmentSize;
            while (remaining > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (read == 0) throw new InvalidDataException("Truncated package during verification.");
                hash.AppendData(buffer, 0, read); remaining -= read;
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(info.Hashes[i], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Adobe SHA-256 validation failed for segment {i + 1}.");
        }
    }
}
