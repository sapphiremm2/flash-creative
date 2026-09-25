namespace AdobeDownloader.Core;

public sealed record Dependency(string SapCode, string BaseVersion, string ProductVersion = "", string BuildGuid = "");

public sealed record ProductBuild(
    string SapCode, string DisplayName, string Version, string ProductVersion,
    string BaseVersion, string Platform, string Channel, string LanguageSet,
    string BuildGuid, Uri Cdn, IReadOnlyList<string> Locales,
    IReadOnlyList<Dependency> Dependencies, string PackageType);

public sealed record PackageAsset(
    string Name, string FileName, string Type, string ProcessorFamily,
    string Condition, long DownloadSize, Uri Url, string OpaqueHashKey,
    IReadOnlyList<string>? Features = null, string ValidationUrl = "", string PackageVersion = "",
    string Alias = "", IReadOnlyList<DeltaAsset>? Deltas = null, string DetachedSignature = "", string CompressionType = "");

public sealed record DeltaAsset(string Name, string BaseVersion, long DownloadSize, Uri Url, Uri? MetadataUrl, string ValidationUrl);
public sealed record ProductModule(string Id, string DisplayName, string DeploymentType, bool RequiresConsent,
    bool Required, IReadOnlyList<string> Packages);

public sealed record ApplicationManifest(
    string SapCode, string ProductVersion, string Platform,
    IReadOnlyList<PackageAsset> Packages, IReadOnlyList<Dependency> Dependencies,
    string RawJson, IReadOnlyList<ProductModule>? Modules = null);

public sealed record DownloadResult(string Path, long Bytes, string Sha256, VerificationResult? Verification = null);
