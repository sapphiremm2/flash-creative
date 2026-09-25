using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AdobeDownloader.Core;

public sealed record PlannedAsset(string Source, string Target, bool Recursive, bool Ignored);
public sealed record PlannedRegistryValue(string Hive, string Key, string View, string Name, string Type, string Data);
public sealed record InstallPlanBlocker(int? Operation, string Kind, string Reason);
public sealed record WindowsInstallPlan(string Product, string Version, string Package, string ManifestSha256, string Locale,
    bool Applicable, IReadOnlyList<PlannedAsset> Assets, IReadOnlyList<PlannedRegistryValue> Registry,
    IReadOnlyList<InstallPlanBlocker> Blockers, bool CanExecute = false,
    string DetachedSignatureStatus = "DeferredUnverified", bool RequireExecutablePublisherVerification = true);

/// <summary>Compiles a reviewable subset. Never executes operations or treats a saved report as authorization.</summary>
public static class WindowsInstallPlanner
{
    public static WindowsInstallPlan Create(InstallInspection inspection, IReadOnlyDictionary<string, string> variables, string locale)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows path planning requires Windows.");
        if (!Regex.IsMatch(locale, "^[a-z]{2}_[A-Z]{2}$")) throw new ArgumentException("Expected an exact Adobe locale, such as en_US.");
        var manifest = inspection.Manifest;
        if (variables.Count > 256) throw new InvalidDataException("Too many installation variables.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in variables)
        {
            if (!Regex.IsMatch(key, "^[A-Za-z][A-Za-z0-9_]*$") || value is null || value.Length > 32767 || value.Contains('\0') || value.Contains('[') || value.Contains(']'))
                throw new InvalidDataException("Invalid or recursively defined installation variable.");
            values.Add(key, value);
        }
        if (values.TryGetValue("installLanguage", out var suppliedLocale) && suppliedLocale != locale)
            throw new InvalidDataException("installLanguage differs from the requested locale.");
        values["installLanguage"] = locale;
        values["InstallLanguage"] = locale;
        // The catalog supplies the processor family; callers cannot override this condition.
        values["OSProcessorFamily"] = manifest.ProcessorFamily;
        var blockers = manifest.UnknownElements.Where(x => x.StartsWith("Package", StringComparison.Ordinal) || x.Contains("/@"))
            .Select(x => new InstallPlanBlocker(null, x, "Unsupported manifest section or attribute.")).ToList();
        var assets = new List<PlannedAsset>(); var registry = new List<PlannedRegistryValue>();
        var applicable = true;
        try { applicable = PackageConditions.Evaluate(manifest.Condition, values); }
        catch (InvalidDataException ex) { applicable = false; blockers.Add(new(null, "Condition", ex.Message)); }
        if (manifest.Scheme != "hd-standard") blockers.Add(new(null, "PackageScheme", "Unsupported package scheme."));
        var view = manifest.ProcessorFamily switch { "64-bit" => "Registry64", "32-bit" => "Registry32", _ => "" };
        if (view.Length == 0) blockers.Add(new(null, "ProcessorFamily", "Unsupported registry architecture."));
        if (applicable)
        for (var index = 0; index < manifest.Operations.Count; index++)
        {
            var operation = manifest.Operations[index];
            try
            {
                if (operation.Xml.Length > 8 * 1024 * 1024) throw new InvalidDataException("Instruction exceeds size limit.");
                using var reader = XmlReader.Create(new StringReader(operation.Xml), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
                var element = XElement.Load(reader);
                if (element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                    throw new InvalidDataException("Unexpected instruction text.");
                if (operation.Kind == "Assets/Asset")
                {
                    if (element.Name != "Asset" || element.HasElements || element.Attributes().Any(a => a.Name.LocalName is not ("source" or "target" or "recursive" or "ignoreAsset") || a.Name.Namespace != XNamespace.None))
                        throw new InvalidDataException("Unsupported asset fields.");
                    bool Flag(string name) => element.Attribute(name)?.Value switch { null or "false" => false, "true" => true, _ => throw new InvalidDataException("Unsupported asset flag.") };
                    var source = element.Attribute("source")?.Value ?? throw new InvalidDataException("Asset source is missing.");
                    var target = element.Attribute("target")?.Value ?? throw new InvalidDataException("Asset target is missing.");
                    if (!source.StartsWith("[StagingFolder]", StringComparison.Ordinal)) throw new InvalidDataException("Asset source must use StagingFolder.");
                    var asset = new PlannedAsset(ResolvePath(source), ResolvePath(target), Flag("recursive"), Flag("ignoreAsset"));
                    if (!asset.Ignored && assets.Any(a => !a.Ignored && PathsOverlap(a.Target, asset.Target)))
                        throw new InvalidDataException("Overlapping asset destinations require a merge policy.");
                    assets.Add(asset);
                }
                else if (operation.Kind == "Commands/Registry")
                {
                    if (element.Name != "Registry" || element.HasAttributes || element.Elements().Any(e => e.Name.Namespace != XNamespace.None || e.Name.LocalName is not ("Path" or "Name" or "Type" or "Value" or "LocalizedValue")))
                        throw new InvalidDataException("Unsupported registry fields.");
                    string Scalar(string name)
                    {
                        var found = element.Elements(name).ToArray();
                        if (found.Length != 1 || found[0].HasElements || found[0].HasAttributes) throw new InvalidDataException($"Invalid registry {name}.");
                        return found[0].Value;
                    }
                    var path = Resolve(Scalar("Path")); var separator = path.IndexOf('\\');
                    if (separator < 1) throw new InvalidDataException("Invalid registry path.");
                    var hive = path[..separator]; var key = path[(separator + 1)..];
                    // A machine install uses the explicit machine Classes store, never merged HKCR.
                    if (hive == "HKEY_CLASSES_ROOT") { hive = "HKEY_LOCAL_MACHINE"; key = "Software\\Classes\\" + key; }
                    if (hive != "HKEY_LOCAL_MACHINE") throw new InvalidDataException("Only machine registry targets are currently planned.");
                    if (key.Split('\\').Any(c => c.Length == 0 || c is "." or "..") || key.Contains('\0')) throw new InvalidDataException("Invalid registry subkey.");
                    var name = Resolve(Scalar("Name")); if (name == "Default") name = "";
                    var type = Scalar("Type");
                    if (element.Elements("Value").Count() + element.Elements("LocalizedValue").Count() != 1) throw new InvalidDataException("Expected one registry value source.");
                    string data;
                    if (element.Element("LocalizedValue") is { } localized)
                    {
                        if (localized.HasAttributes || localized.Elements().Any(e => e.Name != "Language" || e.HasElements || e.Attributes().Count() != 1 || e.Attribute("locale") is null))
                            throw new InvalidDataException("Unsupported localized registry fields.");
                        var languages = localized.Elements().ToArray();
                        if (languages.Select(e => e.Attribute("locale")!.Value).Distinct().Count() != languages.Length) throw new InvalidDataException("Duplicate registry locale.");
                        data = languages.SingleOrDefault(e => e.Attribute("locale")!.Value == locale)?.Value ?? throw new InvalidDataException("Registry value has no exact locale match.");
                    }
                    else data = Scalar("Value");
                    data = Resolve(data);
                    switch (type)
                    {
                        case "REG_SZ": break;
                        case "REG_BINARY":
                            if (data.Length % 2 != 0 || !Regex.IsMatch(data, "^[a-fA-F0-9]*$")) throw new InvalidDataException("Invalid binary registry value.");
                            data = Convert.ToHexString(Convert.FromHexString(data)); break;
                        default: throw new InvalidDataException("Unsupported registry value type.");
                    }
                    if (view.Length == 0) throw new InvalidDataException("Registry view is unresolved.");
                    var existing = registry.SingleOrDefault(r => r.Hive == hive && r.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && r.View == view);
                    if (existing is not null)
                    {
                        if (existing.Type == type && existing.Data == data) continue;
                        throw new InvalidDataException("Conflicting registry value target.");
                    }
                    registry.Add(new(hive, key, view, name, type, data));
                }
                else throw new InvalidDataException(operation.Kind == "Commands/RunProgram"
                    ? "Program execution requires a publisher allowlist, exit/reboot handling, and execution-time signature verification."
                    : "Operation has no implemented installation semantics.");
            }
            catch (Exception ex) when (ex is InvalidDataException or XmlException or ArgumentException or NotSupportedException)
            { blockers.Add(new(index, operation.Kind, ex.Message)); }
        }
        return new(inspection.Product, inspection.ProductVersion, manifest.Package, manifest.ManifestSha256, locale, applicable, assets, registry, blockers);

        string Resolve(string text)
        {
            const string pattern = @"\[([A-Za-z][A-Za-z0-9_]*)\]";
            long length = text.Length;
            foreach (Match match in Regex.Matches(text, pattern))
            {
                if (!values.TryGetValue(match.Groups[1].Value, out var value)) throw new InvalidDataException($"Unresolved variable: {match.Groups[1].Value}");
                length += value.Length - match.Length;
                if (length > 1024 * 1024) throw new InvalidDataException("Expanded value exceeds its size limit.");
            }
            if (length > 1024 * 1024) throw new InvalidDataException("Value exceeds its size limit.");
            var resolved = Regex.Replace(text, pattern, match => values[match.Groups[1].Value]);
            if (resolved.Contains('[') || resolved.Contains(']') || resolved.Contains('\0')) throw new InvalidDataException("Unsupported variable syntax.");
            return resolved;
        }
        string ResolvePath(string symbolic)
        {
            var match = Regex.Match(symbolic, @"^\[([A-Za-z][A-Za-z0-9_]*)\](.*)$");
            if (!match.Success || !values.TryGetValue(match.Groups[1].Value, out var root)) throw new InvalidDataException("Path needs an explicit root variable.");
            if (!Regex.IsMatch(root, @"^[A-Za-z]:[\\/]") || root.Length <= 3) throw new InvalidDataException("Path root must be a local absolute directory below a drive root.");
            foreach (var component in root[3..].Replace('/', '\\').TrimEnd('\\').Split('\\')) PackageDownloader.ValidateFileName(component);
            root = Path.GetFullPath(root).TrimEnd('\\');
            var tail = match.Groups[2].Value;
            if (tail.Length > 0 && tail[0] is not ('\\' or '/')) throw new InvalidDataException("Missing path separator after variable.");
            var relative = tail.Length == 0 ? "" : tail[1..].Replace('/', '\\');
            if (relative.Length > 0) foreach (var component in relative.Split('\\')) PackageDownloader.ValidateFileName(component);
            var result = Path.GetFullPath(Path.Combine(root, relative));
            if (result != root && !result.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes configured root.");
            return result;
        }
    }
    private static bool PathsOverlap(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(right + "\\", StringComparison.OrdinalIgnoreCase) || right.StartsWith(left + "\\", StringComparison.OrdinalIgnoreCase);
}
