using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace AdobeDownloader.Core;

public sealed class CatalogClient(AdobeTransport transport)
{
    public static Uri CatalogUrl(string platform, IEnumerable<string>? channels = null)
    {
        ValidatePlatform(platform);
        var names = (channels ?? ["ccm", "sti", "nocc"]).Distinct().Order().ToArray();
        if (names.Length == 0 || names.Length > 32 || names.Any(x => !Regex.IsMatch(x, "^[A-Za-z0-9_-]{1,64}$")))
            throw new InvalidDataException("Invalid Adobe catalog channels.");
        return new Uri("https://prod-rel-ffc-ccm.oobesaas.adobe.com/adobe-ffc-external/core/v6/products/all" +
            "?" + string.Join("&", names.Select(x => "channel=" + Uri.EscapeDataString(x))) +
            $"&platform={platform}&payload=true&productType=Desktop&_type=xml");
    }

    public async Task<IReadOnlyList<ProductBuild>> FetchAsync(string platform = "win64", CancellationToken ct = default)
        => Parse(await transport.GetTextAsync(CatalogUrl(platform), null, ct), platform);

    public async Task<IReadOnlyList<ProductBuild>> FetchDependencyCatalogAsync(string target, CancellationToken ct = default)
    {
        ValidatePlatform(target);
        // Adobe publishes architecture-neutral and mixed-architecture packages (including ACR)
        // under win32. Payload ProcessorFamily and Condition still control selection.
        var platforms = target == "win32" ? new[] { target } : new[] { target, "win32" };
        var channels = new HashSet<string>(["ccm", "sti", "nocc"], StringComparer.Ordinal);
        for (var pass = 0; pass < 5; pass++)
        {
            var discovered = new HashSet<string>(channels, StringComparer.Ordinal);
            var builds = new List<ProductBuild>();
            foreach (var platform in platforms)
            {
                var xml = await transport.GetTextAsync(CatalogUrl(platform, channels), null, ct);
                builds.AddRange(Parse(xml, platform));
                using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
                    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                foreach (var entry in XDocument.Load(reader).Descendants("custom-entry")
                    .Where(x => (string?)x.Attribute("key") == "dependencyFFCChannel"))
                    foreach (var value in entry.Elements("value"))
                        if (!string.IsNullOrWhiteSpace(value.Value)) discovered.Add(value.Value.Trim());
            }
            if (discovered.SetEquals(channels)) return builds;
            channels = discovered;
        }
        throw new InvalidDataException("Adobe dependency channel discovery did not converge.");
    }

    public static void ValidatePlatform(string platform)
    {
        if (platform is not ("win64" or "winarm64" or "win32"))
            throw new ArgumentException("Platform must be win64, winarm64, or win32.");
    }

    public static IReadOnlyList<ProductBuild> Parse(string xml, string platform)
    {
        ValidatePlatform(platform);
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024
        });
        var document = XDocument.Load(reader);
        var channels = document.Root?.Element("channels")?.Elements("channel").ToList();
        if (document.Root?.Name != "response" || channels is null || channels.Count == 0)
            throw new InvalidDataException("Unrecognized Adobe catalog response.");
        var builds = new List<ProductBuild>();
        foreach (var channel in channels)
        {
            var cdn = AdobeTransport.ValidateUrl(new Uri(Required(channel.Element("cdn")?.Element("secure")?.Value, "CDN")));
            foreach (var product in channel.Element("products")?.Elements("product") ?? [])
            foreach (var target in product.Element("platforms")?.Elements("platform") ?? [])
            {
                if ((string?)target.Attribute("id") != platform) continue;
                foreach (var language in target.Elements("languageSet"))
                {
                    var packageType = (string?)language.Attribute("packageType") ?? "";
                    var productVersion = (string?)language.Attribute("productVersion") ?? "";
                    if (packageType == "hdPackage") Required(productVersion, "productVersion");
                    builds.Add(new ProductBuild(
                        Required((string?)product.Attribute("id"), "product id"),
                        product.Element("displayName")?.Value ?? "",
                        Required((string?)product.Attribute("version"), "version"),
                        productVersion,
                        (string?)language.Attribute("baseVersion") ?? "", platform,
                        Required((string?)channel.Attribute("name"), "channel"),
                        (string?)language.Attribute("name") ?? "",
                        (string?)language.Attribute("buildGuid") ?? "", cdn,
                        (language.Element("locales")?.Elements("locale") ?? [])
                            .Select(x => (string?)x.Attribute("name") ?? "").Where(x => x.Length > 0).Distinct().ToArray(),
                        (language.Element("dependencies")?.Elements("dependency") ?? [])
                            .Select(x => new Dependency(Required(x.Element("sapCode")?.Value, "dependency SAP code"),
                                x.Element("baseVersion")?.Value ?? "", x.Element("productVersion")?.Value ?? "",
                                x.Element("buildGuid")?.Value ?? "")).ToArray(), packageType));
                }
            }
        }
        return builds.DistinctBy(x => (x.SapCode, x.ProductVersion, x.Platform, x.Channel, x.LanguageSet, x.BuildGuid)).ToArray();
    }

    private static string Required(string? value, string name) => !string.IsNullOrWhiteSpace(value)
        ? value : throw new InvalidDataException($"Adobe catalog is missing {name}.");
}
