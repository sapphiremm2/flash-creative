using System.Globalization;
using System.Text.Json;

namespace AdobeDownloader.Core;

public sealed class ManifestClient(AdobeTransport transport)
{
    public async Task<ApplicationManifest> FetchAsync(ProductBuild build, CancellationToken ct = default)
    {
        CatalogClient.ValidatePlatform(build.Platform);
        if (build.PackageType != "hdPackage" || string.IsNullOrEmpty(build.ProductVersion))
            throw new NotSupportedException($"Package type '{build.PackageType}' is listed for discovery but not supported by the phase-one downloader.");
        var uri = new Uri("https://cdn-ffc.oobesaas.adobe.com/core/v3/applications" +
            $"?name={Uri.EscapeDataString(build.SapCode)}&version={Uri.EscapeDataString(build.ProductVersion)}&platform={build.Platform}");
        return Parse(await transport.GetTextAsync(uri, build.BuildGuid, ct), build);
    }

    public static ApplicationManifest Parse(string json, ProductBuild build)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Text(root, "SAPCode") != build.SapCode || Text(root, "ProductVersion") != build.ProductVersion ||
            Text(root, "Platform") != build.Platform)
            throw new InvalidDataException("Adobe manifest does not match the selected product, version, and platform.");
        var packages = new List<PackageAsset>();
        foreach (var package in Items(root, "Packages", "Package"))
        {
            var name = Required(package, "PackageName");
            var path = Required(package, "Path");
            var url = AdobeTransport.ValidateUrl(new Uri(build.Cdn, path));
            var fileName = Text(package, "fullPackageName");
            if (fileName.Length == 0) fileName = Uri.UnescapeDataString(url.Segments.Last());
            PackageDownloader.ValidateFileName(fileName);
            if (!long.TryParse(Text(package, "DownloadSize"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= 0)
                throw new InvalidDataException($"Package {name} has no valid download size.");
            packages.Add(new PackageAsset(name, fileName, Text(package, "Type"),
                Text(package, "ProcessorFamily"), Text(package, "Condition"), size, url, Text(package, "packageHashKey"),
                Items(package, "Features", "Feature").Where(x => !(x.ValueKind == JsonValueKind.Object && !x.EnumerateObject().Any())).Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : Required(x, "Name")).ToArray(),
                ValidationUrl(package), Text(package, "PackageVersion"), Text(package, "AliasPackageName"), ParseDeltas(package, build.Cdn), Text(package, "PackageValidation"), Text(root, "CompressionType").Trim().ToLowerInvariant()));
        }
        if (packages.Count == 0) throw new InvalidDataException("Adobe manifest contains no downloadable packages.");
        if (packages.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != packages.Count)
            throw new InvalidDataException("Adobe manifest has ambiguous duplicate package names.");
        var dependencies = Items(root, "Dependencies", "Dependency")
            .Select(x => new Dependency(Required(x, "SAPCode"), Text(x, "BaseVersion"), Text(x, "ProductVersion"), Text(x, "BuildGuid"))).ToArray();
        var modules = Items(root, "Modules", "Module").Select(m => new ProductModule(Required(m, "Id"), Text(m, "DisplayName"),
            Text(m, "DeploymentType"), IsTrue(m, "RequiresUserConsent"), IsTrue(m, "Required"),
            Items(m, "ReferencePackages", "ReferencePackage").Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : Required(x, "Name")).ToArray())).ToArray();
        if (modules.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != modules.Length)
            throw new InvalidDataException("Duplicate module IDs.");
        return new ApplicationManifest(build.SapCode, build.ProductVersion, build.Platform, packages, dependencies, json, modules);
    }

    private static string ValidationUrl(JsonElement package)
    {
        var url = package.TryGetProperty("ValidationURLs", out var urls) && urls.TryGetProperty("TYPE2", out var type2)
            ? type2.GetString() ?? "" : Text(package, "ValidationURL");
        if (url.Length == 0) return "";
        var uri = AdobeTransport.ValidateUrl(new Uri(url));
        // Request SHA-256 explicitly when only the default validation endpoint is supplied.
        return uri.Query.Contains("algorithm=", StringComparison.OrdinalIgnoreCase) ? url : url + (uri.Query.Length == 0 ? "?" : "&") + "algorithm=TYPE2";
    }
    private static bool IsTrue(JsonElement value, string key) => Text(value, key).Equals("true", StringComparison.OrdinalIgnoreCase) || Text(value, key) == "1";
    private static IReadOnlyList<DeltaAsset> ParseDeltas(JsonElement package, Uri cdn)
    {
        if (!package.TryGetProperty("DeltaPackages", out var deltas)) return [];
        if (deltas.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Unexpected DeltaPackages shape.");
        return deltas.EnumerateArray().Select(d =>
        {
            var path = Text(d, "MetadataFilePath");
            if (path.Length == 0 && d.TryGetProperty("additionalInfo", out var info)) path = Text(info, "MetadataFilePath");
            if (!long.TryParse(Text(d, "DownloadSize"), out var size) || size <= 0) throw new InvalidDataException("Invalid delta size.");
            return new DeltaAsset(Required(d, "PackageName"), Required(d, "BasePackageVersion"), size,
                AdobeTransport.ValidateUrl(new Uri(cdn, Required(d, "Path"))), path.Length == 0 ? null : AdobeTransport.ValidateUrl(new Uri(cdn, path)), ValidationUrl(d));
        }).ToArray();
    }

    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        ? value.ToString() : "";
    private static string Required(JsonElement element, string name) => Text(element, name) is { Length: > 0 } value
        ? value : throw new InvalidDataException($"Adobe manifest is missing {name}.");
    private static IEnumerable<JsonElement> Items(JsonElement root, string container, string key)
    {
        if (!root.TryGetProperty(container, out var parent) || !parent.TryGetProperty(key, out var value)) yield break;
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) yield return item;
        else if (value.ValueKind == JsonValueKind.Object) yield return value;
        else if (value.ValueKind == JsonValueKind.String) yield return value;
        else throw new InvalidDataException($"Unexpected {container}.{key} format.");
    }
}
