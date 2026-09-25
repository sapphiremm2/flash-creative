using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SharpCompress.Compressors.LZMA;

namespace AdobeDownloader.Core;

public sealed record InstallOperation(string Kind, string Xml);
public sealed record InstallManifestReport(string Package, string Scheme, string ProcessorFamily, string Condition,
    string ManifestSha256, IReadOnlyList<InstallOperation> Operations, IReadOnlyDictionary<string, int> OperationCounts,
    IReadOnlyList<string> Variables, IReadOnlyList<string> UnknownElements, bool CanInstall = false);
public sealed record InstallInspection(string Product, string ProductVersion, string Archive,
    VerificationResult Verification, InstallManifestReport Manifest, bool DetachedSignatureVerified = false);

/// <summary>Verified, read-only inventory of PIMX instructions. No instruction is authorized for execution.</summary>
public sealed class InstallInspector(AdobeTransport transport)
{
    private const int Limit = 8 * 1024 * 1024;
    public async Task<InstallInspection> InspectAsync(DownloadPlan plan, string product, string packageName,
        string archivePath, CancellationToken ct = default)
    {
        DownloadQueue.Validate(plan);
        var downloads = plan.Downloads.Where(d => d.SapCode == product && d.Package.Name == packageName).ToArray();
        if (downloads.Length != 1) throw new InvalidDataException("Expected one selected package in the plan.");
        var download = downloads[0];
        var builds = plan.Products.Where(p => p.Build.SapCode == product && p.Build.ProductVersion == download.ProductVersion).ToArray();
        if (builds.Length != 1) throw new InvalidDataException("Missing or ambiguous package build.");
        var manifest = await new ManifestClient(transport).FetchAsync(builds[0].Build, ct);
        var package = manifest.Packages.SingleOrDefault(p => p.Name == packageName)
            ?? throw new InvalidDataException("Package is absent from Adobe's current manifest.");
        if (package.Url != download.Package.Url || package.DownloadSize != download.Package.DownloadSize)
            throw new InvalidDataException("Saved plan differs from Adobe's current manifest.");
        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var verified = await new AdobePackageVerifier(transport).VerifyAsync(package, archivePath, ct);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > 100000) throw new InvalidDataException("Archive entry limit exceeded.");
        var entries = archive.Entries.Where(e => e.FullName.Equals(packageName + ".pimx", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length != 1) throw new InvalidDataException("Expected one root package manifest.");
        var entry = entries[0];
        if (entry.Length > Limit || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & 0x400) != 0)
            throw new InvalidDataException("Invalid package manifest entry.");
        await using var input = entry.Open();
        var encoded = await ReadBounded(input, ct);
        var decoded = await DecodeAsync(encoded, ct);
        return new InstallInspection(product, download.ProductVersion, Path.GetFullPath(archivePath), verified,
            Parse(decoded, packageName, package.ProcessorFamily));
    }

    public static InstallManifestReport Parse(byte[] xml, string expectedPackage, string expectedProcessorFamily)
    {
        if (xml.Length > Limit) throw new InvalidDataException("Package manifest exceeds its byte limit.");
        using var input = new MemoryStream(xml);
        var settings = new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = Limit, CloseInput = false };
        using (var scan = XmlReader.Create(input, settings))
        {
            var count = 0;
            while (scan.Read())
                if (scan.Depth > 64 || (scan.NodeType == XmlNodeType.Element && ++count > 100000))
                    throw new InvalidDataException("Package manifest structure limit exceeded.");
        }
        input.Position = 0;
        using var reader = XmlReader.Create(input, settings);
        var document = XDocument.Load(reader);
        var root = document.Root;
        if (root?.Name != "Package") throw new InvalidDataException("Unexpected package manifest root.");
        // The report preserves instruction XML instead of flattening conditions or locale-specific values.
        var elements = root.DescendantsAndSelf().ToArray();
        if (elements.Any(e => e.Name.Namespace != XNamespace.None || e.Attributes().Any(a => a.IsNamespaceDeclaration || a.Name.Namespace != XNamespace.None)))
            throw new InvalidDataException("Namespaced installation instructions are unsupported.");
        string Scalar(string name, bool required = true)
        {
            var found = root.Elements(name).ToArray();
            if (found.Length == 0 && !required) return "";
            if (found.Length != 1 || found[0].HasElements || found[0].HasAttributes)
                throw new InvalidDataException($"Invalid or duplicate package {name}.");
            return found[0].Value;
        }
        var name = Scalar("PackageName"); var family = Scalar("ProcessorFamily");
        if (name != expectedPackage || family != expectedProcessorFamily)
            throw new InvalidDataException("PIMX identity does not match the verified Adobe package manifest.");
        var scheme = Scalar("PackageScheme"); var condition = Scalar("Condition", false);
        _ = Scalar("Type");
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        string[] known = ["Type", "PackageName", "ProcessorFamily", "PackageScheme", "Condition", "Assets", "Commands"];
        foreach (var element in root.Elements().Where(e => !known.Contains(e.Name.LocalName))) unknown.Add("Package/" + element.Name.LocalName);
        foreach (var attribute in root.Attributes()) unknown.Add("Package/@" + attribute.Name.LocalName);
        var operations = new List<InstallOperation>();
        foreach (var section in new[] { "Assets", "Commands" })
        {
            var containers = root.Elements(section).ToArray();
            if (containers.Length > 1) throw new InvalidDataException($"Duplicate {section} section.");
            if (containers.Length == 0) continue;
            var container = containers[0];
            foreach (var attribute in container.Attributes()) unknown.Add(section + "/@" + attribute.Name.LocalName);
            foreach (var operation in container.Elements())
            {
                var kind = operation.Name.LocalName;
                if (section == "Assets" ? kind != "Asset" : kind is not ("Registry" or "RunProgram")) unknown.Add(section + "/" + kind);
                operations.Add(new InstallOperation(section + "/" + kind, operation.ToString(SaveOptions.DisableFormatting)));
            }
        }
        if (scheme != "hd-standard") unknown.Add("PackageScheme=" + scheme);
        var text = root.ToString(SaveOptions.DisableFormatting);
        var variables = Regex.Matches(text, @"\[([A-Za-z][A-Za-z0-9_]*)\]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new InstallManifestReport(name, scheme, family, condition, Convert.ToHexString(SHA256.HashData(xml)).ToLowerInvariant(),
            operations, operations.GroupBy(o => o.Kind).ToDictionary(g => g.Key, g => g.Count()), variables, unknown.Order().ToArray());
    }

    internal static async Task<byte[]> DecodeAsync(byte[] encoded, CancellationToken ct)
    {
        if (encoded.Length == 0 || encoded.Length > Limit) throw new InvalidDataException("Invalid encoded manifest size.");
        // Property bytes are 0..40; XML starts with whitespace, '<', or a Unicode BOM.
        // Confirm the XML prefix first, since TAB and CR overlap valid dictionary properties.
        var prefix = Encoding.UTF8.GetString(encoded.AsSpan(0, Math.Min(encoded.Length, 128))).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (prefix.StartsWith('<') || encoded.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) || encoded.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return encoded;
        if (encoded[0] > 28) throw new InvalidDataException("Unsupported PIMX encoding or LZMA2 dictionary size.");
        using var input = new MemoryStream(encoded, 1, encoded.Length - 1, writable: false);
        try
        {
            using var decoded = LzmaStream.Create([encoded[0]], input, encoded.Length - 1);
            return await ReadBounded(decoded, ct);
        }
        catch (Exception ex) when (ex.GetType().Assembly == typeof(LzmaStream).Assembly)
        { throw new InvalidDataException("Invalid LZMA2 package manifest.", ex); }
    }
    private static async Task<byte[]> ReadBounded(Stream input, CancellationToken ct)
    {
        using var output = new MemoryStream(); var buffer = new byte[32768];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var count = await input.ReadAsync(buffer, ct); if (count == 0) return output.ToArray();
            if (output.Length + count > Limit) throw new InvalidDataException("Decoded manifest exceeds its byte limit.");
            output.Write(buffer, 0, count);
        }
    }
}
