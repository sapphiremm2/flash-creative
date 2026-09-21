namespace AdobeDownloader.Core;

public sealed record PackageDecision(string Package, bool Included, string Reason);
public sealed record PlannedProduct(ProductBuild Build, ApplicationManifest Manifest, IReadOnlyList<PackageDecision> Decisions);
public sealed record PlannedDownload(string DirectoryName, string SapCode, string ProductVersion, PackageAsset Package);
public sealed record DownloadPlan(int SchemaVersion, DateTimeOffset CreatedAt, string Platform, string Locale,
    string OsVersion, IReadOnlyList<PlannedProduct> Products, IReadOnlyList<PlannedDownload> Downloads)
{
    public long TotalBytes => Downloads.Aggregate(0L, (total, item) => checked(total + item.Package.DownloadSize));
}

public sealed class DownloadPlanner(Func<ProductBuild, CancellationToken, Task<ApplicationManifest>> fetchManifest)
{
    public async Task<DownloadPlan> CreateAsync(ProductBuild root, IReadOnlyList<ProductBuild> catalog,
        string locale, string osVersion, CancellationToken ct = default)
    {
        CatalogClient.ValidatePlatform(root.Platform);
        AdobeVersion.Compare(osVersion, osVersion);
        if (!SupportsLocale(root, locale)) throw new InvalidDataException($"{root.SapCode} does not support {locale}.");
        var selected = new Dictionary<string, ProductBuild>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<PlannedProduct>();
        var downloads = new List<PlannedDownload>();
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OSProcessorFamily"] = root.Platform switch { "win64" => "64-bit", "winarm64" => "arm64", _ => "32-bit" },
            ["OSVersion"] = osVersion, ["InstallLanguage"] = locale
        };

        async Task Visit(ProductBuild build)
        {
            ct.ThrowIfCancellationRequested();
            if (!visiting.Add(build.SapCode)) throw new InvalidDataException($"Dependency cycle at {build.SapCode}.");
            if (visiting.Count > 64 || selected.Count >= 256) throw new InvalidDataException("Dependency graph exceeds planning limits.");
            if (selected.TryGetValue(build.SapCode, out var previous))
            {
                visiting.Remove(build.SapCode);
                if (previous.ProductVersion != build.ProductVersion || previous.BuildGuid != build.BuildGuid || previous.Platform != build.Platform)
                    throw new InvalidDataException($"Conflicting dependency builds for {build.SapCode}.");
                return;
            }
            selected.Add(build.SapCode, build);
            var manifest = await fetchManifest(build, ct);
            if (manifest.SapCode != build.SapCode || manifest.ProductVersion != build.ProductVersion || manifest.Platform != build.Platform)
                throw new InvalidDataException("Resolved manifest identity mismatch.");
            var dependencies = build.Dependencies.Concat(manifest.Dependencies)
                .GroupBy(d => d.SapCode, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var group in dependencies)
            {
                var requirements = group.Distinct().ToArray();
                var candidates = catalog.Where(b => b.SapCode.Equals(group.Key, StringComparison.OrdinalIgnoreCase) &&
                    b.PackageType == "hdPackage" && (b.Platform == root.Platform || b.Platform == "win32") &&
                    SupportsLocale(b, locale) && requirements.All(r => Matches(b, r))).ToArray();
                if (selected.TryGetValue(group.Key, out var existing))
                {
                    if (!requirements.All(r => Matches(existing, r)))
                        throw new InvalidDataException($"Conflicting dependency constraints for {group.Key}.");
                    await Visit(existing);
                    continue;
                }
                if (candidates.Length == 0)
                    throw new InvalidDataException($"Missing compatible dependency {group.Key}: {string.Join(", ", requirements.Select(r => "base=" + r.BaseVersion + " version=" + r.ProductVersion))}.");
                var native = candidates.Where(b => b.Platform == root.Platform).ToArray();
                if (native.Length > 0) candidates = native;
                var newest = candidates.OrderByDescending(b => b.ProductVersion, Comparer<string>.Create(AdobeVersion.Compare)).First().ProductVersion;
                candidates = candidates.Where(b => AdobeVersion.Compare(b.ProductVersion, newest) == 0)
                    .DistinctBy(b => (b.ProductVersion, b.BuildGuid, b.Platform, b.LanguageSet)).ToArray();
                if (candidates.Length != 1) throw new InvalidDataException($"Ambiguous dependency build for {group.Key} {newest}.");
                await Visit(candidates[0]);
            }
            var decisions = new List<PackageDecision>();
            foreach (var package in manifest.Packages)
            {
                if (package.Features is { Count: > 0 })
                    throw new NotSupportedException($"{build.SapCode}/{package.Name} has feature/module selection metadata not yet supported.");
                var compatible = IsProcessorCompatible(package.ProcessorFamily, root.Platform);
                var condition = PackageConditions.Evaluate(package.Condition, variables);
                var include = compatible && condition;
                decisions.Add(new PackageDecision(package.Name, include,
                    !compatible ? "Processor family differs from target" : !condition ? "Package condition is false" : "Compatible full package"));
                if (include)
                {
                    var directory = $"{downloads.Count:D4}";
                    downloads.Add(new PlannedDownload(directory, build.SapCode, build.ProductVersion, package));
                }
            }
            if (!decisions.Any(d => d.Included)) throw new InvalidDataException($"No compatible packages for required product {build.SapCode}.");
            ordered.Add(new PlannedProduct(build, manifest, decisions));
            visiting.Remove(build.SapCode);
        }
        await Visit(root);
        var plan = new DownloadPlan(1, DateTimeOffset.UtcNow, root.Platform, locale, osVersion, ordered, downloads);
        _ = plan.TotalBytes;
        return plan;
    }

    public static bool SupportsLocale(ProductBuild build, string locale) => build.Locales.Count == 0 ||
        build.Locales.Any(x => x.Equals(locale, StringComparison.OrdinalIgnoreCase) || x is "mul" or "ALL");
    private static bool Matches(ProductBuild b, Dependency r) =>
        (r.BaseVersion.Length == 0 || (b.BaseVersion.Length > 0 && AdobeVersion.Compare(b.BaseVersion, r.BaseVersion) == 0) || AdobeVersion.Compare(b.Version, r.BaseVersion) == 0) &&
        (r.ProductVersion.Length == 0 || AdobeVersion.Compare(b.ProductVersion, r.ProductVersion) == 0) &&
        (r.BuildGuid.Length == 0 || b.BuildGuid.Equals(r.BuildGuid, StringComparison.OrdinalIgnoreCase));

    private static bool IsProcessorCompatible(string family, string platform) => family.ToLowerInvariant() switch
    {
        "" or "all" or "32-bit" or "x86" => true,
        "64-bit" or "x64" or "x86_64" => platform == "win64",
        "arm64" or "arm" => platform == "winarm64",
        _ => throw new NotSupportedException($"Unknown processor family '{family}'.")
    };
}
