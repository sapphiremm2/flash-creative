namespace AdobeDownloader.Core;

public sealed record DeltaDecision(string BaseVersion, long Bytes, bool Selected, string Reason);
public sealed record PackageDecision(string Package, bool Included, string Reason, IReadOnlyList<DeltaDecision>? Deltas = null);
public sealed record PlannedProduct(ProductBuild Build, ApplicationManifest Manifest, IReadOnlyList<PackageDecision> Decisions);
public sealed record PlannedDownload(string DirectoryName, string SapCode, string ProductVersion, PackageAsset Package);
public sealed record DownloadPlan(int SchemaVersion, DateTimeOffset CreatedAt, string Platform, string Locale,
    string OsVersion, IReadOnlyList<PlannedProduct> Products, IReadOnlyList<PlannedDownload> Downloads, bool RequiresAdobeValidation = false)
{
    public long TotalBytes => Downloads.Aggregate(0L, (total, item) => checked(total + item.Package.DownloadSize));
}

public sealed class DownloadPlanner(Func<ProductBuild, CancellationToken, Task<ApplicationManifest>> fetchManifest)
{
    public async Task<DownloadPlan> CreateAsync(ProductBuild root, IReadOnlyList<ProductBuild> catalog,
        string locale, string osVersion, CancellationToken ct = default, SelectionOptions? options = null)
    {
        CatalogClient.ValidatePlatform(root.Platform);
        AdobeVersion.Compare(osVersion, osVersion);
        if (!SupportsLocale(root, locale)) throw new InvalidDataException($"{root.SapCode} does not support {locale}.");
        options ??= new SelectionOptions();
        var usedSelections = new HashSet<string>(StringComparer.Ordinal);
        var manifests = new Dictionary<ProductBuild, ApplicationManifest>();
        var searchSteps = 0;
        async Task<ApplicationManifest> Fetch(ProductBuild build)
        {
            if (manifests.TryGetValue(build, out var cached)) return cached;
            var manifest = await fetchManifest(build, ct);
            if (manifest.SapCode != build.SapCode || manifest.ProductVersion != build.ProductVersion || manifest.Platform != build.Platform)
                throw new InvalidDataException("Resolved manifest identity mismatch.");
            manifests.Add(build, manifest);
            return manifest;
        }
        async Task<Dictionary<string, ProductBuild>?> Solve(Dictionary<string, ProductBuild> chosen)
        {
            ct.ThrowIfCancellationRequested();
            if (++searchSteps > 1024 || chosen.Count > 256) throw new InvalidDataException("Dependency search exceeds planning limits.");
            var requirements = new List<Dependency>();
            foreach (var build in chosen.Values)
                requirements.AddRange(build.Dependencies.Concat((await Fetch(build)).Dependencies));
            var groups = requirements.GroupBy(d => d.SapCode, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
            if (groups.Any(g => chosen.TryGetValue(g.Key, out var b) && !g.All(r => Matches(b, r)))) return null;
            var pending = groups.FirstOrDefault(g => !chosen.ContainsKey(g.Key));
            if (pending is null) return chosen;
            var candidates = catalog.Where(b => b.SapCode.Equals(pending.Key, StringComparison.OrdinalIgnoreCase) &&
                b.PackageType == "hdPackage" && (b.Platform == root.Platform || b.Platform == "win32") &&
                SupportsLocale(b, locale) && pending.All(r => Matches(b, r)))
                .DistinctBy(b => (b.ProductVersion, b.BuildGuid, b.Platform, b.LanguageSet))
                .OrderByDescending(b => b.Platform == root.Platform)
                .ThenByDescending(b => b.ProductVersion, Comparer<string>.Create(AdobeVersion.Compare)).ToArray();
            foreach (var candidate in candidates)
            {
                if (candidates.Count(b => b.Platform == candidate.Platform && AdobeVersion.Compare(b.ProductVersion, candidate.ProductVersion) == 0) > 1)
                    throw new InvalidDataException($"Ambiguous dependency build for {pending.Key} {candidate.ProductVersion}.");
                var branch = new Dictionary<string, ProductBuild>(chosen, StringComparer.OrdinalIgnoreCase) { [pending.Key] = candidate };
                var solved = await Solve(branch);
                if (solved is not null) return solved;
            }
            return null;
        }
        var selected = await Solve(new Dictionary<string, ProductBuild>(StringComparer.OrdinalIgnoreCase) { [root.SapCode] = root })
            ?? throw new InvalidDataException("Conflicting or missing compatible dependency constraints; no complete solution.");
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<PlannedProduct>();
        var downloads = new List<PlannedDownload>();
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OSProcessorFamily"] = root.Platform switch { "win64" or "winarm64" => "64-bit", _ => "32-bit" },
            ["OSVersion"] = osVersion, ["InstallLanguage"] = locale
        };

        async Task Visit(ProductBuild build)
        {
            ct.ThrowIfCancellationRequested();
            if (!visiting.Add(build.SapCode)) throw new InvalidDataException($"Dependency cycle at {build.SapCode}.");
            if (visiting.Count > 64) throw new InvalidDataException("Dependency graph exceeds planning limits.");
            if (completed.Contains(build.SapCode)) { visiting.Remove(build.SapCode); return; }
            var manifest = await Fetch(build);
            foreach (var code in build.Dependencies.Concat(manifest.Dependencies).Select(d => d.SapCode)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal))
                await Visit(selected[code]);
            var includedPackages = ModuleSelection.Select(manifest, root.SapCode, options, usedSelections);
            var decisions = new List<PackageDecision>();
            foreach (var package in manifest.Packages)
            {
                var compatible = IsProcessorCompatible(package.ProcessorFamily, root.Platform, manifest.Platform);
                var condition = PackageConditions.Evaluate(package.Condition, variables);
                var selectedPackage = includedPackages.Contains(package.Name);
                var include = selectedPackage && compatible && condition;
                decisions.Add(new PackageDecision(package.Name, include,
                    !selectedPackage ? "Optional module or feature not selected" : !compatible ? "Processor family differs from target" : !condition ? "Package condition is false" : "Compatible full package",
                    (package.Deltas ?? []).Select(d => new DeltaDecision(d.BaseVersion, d.DownloadSize, false,
                        d.MetadataUrl is null ? "Full package: delta metadata URL is missing" :
                        "Full package: no verified installed baseline; delta application requires phase-three installation support")).ToArray()));
                if (include)
                {
                    var directory = $"{downloads.Count:D4}";
                    downloads.Add(new PlannedDownload(directory, build.SapCode, build.ProductVersion, package));
                }
            }
            ModuleSelection.ValidateIncluded(manifest, root.SapCode, options,
                decisions.Where(d => d.Included).Select(d => d.Package).ToHashSet(StringComparer.Ordinal));
            if (!decisions.Any(d => d.Included)) throw new InvalidDataException($"No compatible packages for required product {build.SapCode}.");
            ordered.Add(new PlannedProduct(build, manifest, decisions));
            completed.Add(build.SapCode);
            visiting.Remove(build.SapCode);
        }
        await Visit(root);
        var unused = (options.Modules ?? []).Concat(options.Features ?? []).Where(x => !usedSelections.Contains(x)).ToArray();
        if (unused.Length > 0) throw new InvalidDataException("Selections reference products outside this plan: " + string.Join(", ", unused));
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

    private static bool IsProcessorCompatible(string family, string platform, string manifestPlatform) => family.ToLowerInvariant() switch
    {
        "" or "all" or "32-bit" or "x86" => true,
        // Adobe's native winarm64 manifests use 64-bit as bitness, including some legacy _x64 names.
        // A shared win32 manifest does not establish ARM compatibility for its 64-bit payloads.
        "64-bit" => platform == "win64" || (platform == "winarm64" && manifestPlatform == "winarm64"),
        "x64" or "x86_64" => platform == "win64",
        "arm64" or "arm" => platform == "winarm64",
        _ => throw new NotSupportedException($"Unknown processor family '{family}'.")
    };
}
