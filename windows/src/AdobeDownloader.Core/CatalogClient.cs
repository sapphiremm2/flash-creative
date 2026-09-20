using System.Xml;
using System.Xml.Linq;

namespace AdobeDownloader.Core;

public sealed class CatalogClient(AdobeTransport transport)
{
    public static Uri CatalogUrl(string platform)
    {
        ValidatePlatform(platform);
        return new Uri("https://prod-rel-ffc-ccm.oobesaas.adobe.com/adobe-ffc-external/core/v6/products/all" +
            $"?channel=ccm&channel=sti&channel=nocc&platform={platform}&payload=true&productType=Desktop&_type=xml");
    }

    public async Task<IReadOnlyList<ProductBuild>> FetchAsync(string platform = "win64", CancellationToken ct = default)
        => Parse(await transport.GetTextAsync(CatalogUrl(platform), null, ct), platform);

    public static void ValidatePlatform(string platform)
    {
        if (platform is not ("win64" or "winarm64"))
            throw new ArgumentException("Platform must be win64 or winarm64.");
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
                                x.Element("baseVersion")?.Value ?? "")).ToArray(), packageType));
                }
            }
        }
        return builds.DistinctBy(x => (x.SapCode, x.ProductVersion, x.Platform, x.Channel, x.LanguageSet, x.BuildGuid)).ToArray();
    }

    private static string Required(string? value, string name) => !string.IsNullOrWhiteSpace(value)
        ? value : throw new InvalidDataException($"Adobe catalog is missing {name}.");
}
