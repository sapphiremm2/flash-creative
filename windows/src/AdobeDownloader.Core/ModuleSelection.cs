namespace AdobeDownloader.Core;

public sealed record SelectionOptions(IReadOnlyList<string>? Modules = null, IReadOnlyList<string>? Features = null);

public static class ModuleSelection
{
    // Tokens may be root-local IDs or SAP:ID for a dependency product. Listing a
    // consent-requiring module explicitly is the user's selection, never an inferred default.
    public static IReadOnlySet<string> Select(ApplicationManifest manifest, string rootCode, SelectionOptions options, ISet<string> used)
    {
        var modules = manifest.Modules ?? [];
        var names = manifest.Packages.SelectMany(p => new[] { p.Name, p.FileName, p.Alias }).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
        foreach (var module in modules)
            if (module.Packages.Count == 0 || module.Packages.Any(p => !names.Contains(p)))
                throw new InvalidDataException($"Module {module.Id} has missing package references.");
        var requested = Resolve(options.Modules, manifest.SapCode, rootCode, used, modules.Select(m => m.Id));
        foreach (var module in modules)
        {
            var defaultSelected = module.Required || module.DeploymentType is "Immediate" or "Default";
            var ownsCore = manifest.Packages.Any(p => p.Type.Equals("core", StringComparison.OrdinalIgnoreCase) &&
                module.Packages.Any(n => n == p.Name || n == p.FileName || n == p.Alias));
            if ((defaultSelected || ownsCore) && module.RequiresConsent && !requested.Contains(module.Id))
                throw new InvalidDataException($"Required module {manifest.SapCode}:{module.Id} requires explicit selection.");
            if (defaultSelected) requested.Add(module.Id);
            if (module.DeploymentType is not ("Immediate" or "Default" or "Deferred" or "OnDemand"))
                throw new NotSupportedException($"Unknown module deployment type: {module.DeploymentType}");
        }
        var features = Resolve(options.Features, manifest.SapCode, rootCode, used, manifest.Packages.SelectMany(p => p.Features ?? []));
        var included = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in manifest.Packages)
        {
            var owners = modules.Where(m => m.Packages.Any(n => n == package.Name || n == package.FileName || n == package.Alias)).ToArray();
            var core = package.Type.Equals("core", StringComparison.OrdinalIgnoreCase);
            var moduleMatch = owners.Length == 0 || owners.Any(m => requested.Contains(m.Id));
            var featureMatch = package.Features is not { Count: > 0 } || package.Features.Any(features.Contains);
            if (core || (moduleMatch && featureMatch)) included.Add(package.Name);
        }
        return included;
    }

    public static void ValidateIncluded(ApplicationManifest manifest, string rootCode, SelectionOptions options, IReadOnlySet<string> included)
    {
        var modules = manifest.Modules ?? [];
        var requested = Resolve(options.Modules, manifest.SapCode, rootCode, new HashSet<string>(), modules.Select(m => m.Id));
        foreach (var module in modules.Where(m => requested.Contains(m.Id) || m.Required || m.DeploymentType is "Immediate" or "Default"))
            if (!manifest.Packages.Any(p => included.Contains(p.Name) && module.Packages.Any(n => n == p.Name || n == p.FileName || n == p.Alias)))
                throw new InvalidDataException($"Selected or required module {manifest.SapCode}:{module.Id} has no compatible packages.");
        var features = Resolve(options.Features, manifest.SapCode, rootCode, new HashSet<string>(), manifest.Packages.SelectMany(p => p.Features ?? []));
        foreach (var feature in features)
            if (!manifest.Packages.Any(p => included.Contains(p.Name) && (p.Features ?? []).Contains(feature)))
                throw new InvalidDataException($"Selected feature {manifest.SapCode}:{feature} has no compatible packages.");
    }

    private static HashSet<string> Resolve(IReadOnlyList<string>? tokens, string code, string rootCode, ISet<string> used, IEnumerable<string> known)
    {
        var available = known.ToHashSet(StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens ?? [])
        {
            var prefix = code + ":";
            var id = token.StartsWith(prefix, StringComparison.Ordinal) ? token[prefix.Length..] : code == rootCode && !token.Contains(':') ? token : null;
            if (id is null) continue;
            if (!available.Contains(id)) throw new InvalidDataException($"Unknown module/feature selection {token} for {code}.");
            result.Add(id); used.Add(token);
        }
        return result;
    }
}
