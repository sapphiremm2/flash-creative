using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace AdobeDownloader.Core;

/// <summary>Prepare value transactions in an explicitly owned scope. No writes or elevation.
/// Preference policy: initialize missing values only; preserve existing settings and retain preferences on uninstall.
/// Recursive deletion requests are retained in the plan for auditing but are never enacted.</summary>
[SupportedOSPlatform("windows")]
public static class RegistryPlanCompiler
{
    public static IReadOnlyList<RegistryReplacement> Prepare(IReadOnlyList<PlannedRegistryValue> values, RegistryScope scope)
    {
        if (values.Count > 10000) throw new InvalidDataException("Registry value limit exceeded.");
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidDataException("Initiating identity is unavailable.");
        var hive = scope.Hive switch { RegistryHive.CurrentUser => "HKEY_CURRENT_USER", RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE", _ => throw new InvalidDataException("Unsupported hive.") };
        var entries = new List<RegistryReplacement>(); var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (value.Hive != hive || value.View != scope.View.ToString()) throw new InvalidDataException("Registry scope differs from the plan.");
            if (scope.Hive == RegistryHive.CurrentUser && value.OwnerSid != sid) throw new InvalidDataException("Registry plan belongs to a different initiating user.");
            if (scope.Hive == RegistryHive.LocalMachine && (value.OwnerSid is not null || value.PreserveExisting || value.PreserveOnUninstall || value.RecursiveDeleteRequested))
                throw new InvalidDataException("User preference semantics cannot target the machine hive.");
            if (value.RecursiveDeleteRequested && !(value.PreserveExisting && value.PreserveOnUninstall))
                throw new InvalidDataException("Recursive deletion is not implemented.");
            var relative = value.Key.Equals(scope.Root, StringComparison.OrdinalIgnoreCase) ? "" :
                value.Key.StartsWith(scope.Root + "\\", StringComparison.OrdinalIgnoreCase) ? value.Key[(scope.Root.Length + 1)..] :
                throw new InvalidDataException("Registry value lies outside the owned scope.");
            if (!targets.Add(relative + "\0" + value.Name)) throw new InvalidDataException("Duplicate registry target.");
            if (value.Data.Length > 1024 * 1024) throw new InvalidDataException("Registry data exceeds limit.");
            RegistryValueSnapshot replacement;
            try
            {
                replacement = value.Type switch {
                    "REG_SZ" when !value.Data.Contains('\0') => new(RegistryValueKind.String, Text: value.Data),
                    "REG_BINARY" => new(RegistryValueKind.Binary, Bytes: Convert.FromHexString(value.Data)),
                    _ => throw new InvalidDataException("Unsupported registry value type.") };
            }
            catch (FormatException ex) { throw new InvalidDataException("Invalid registry binary data.", ex); }
            var existing = RegistryTransaction.Read(scope, relative, value.Name);
            if (value.PreserveExisting && existing is not null) continue;
            entries.Add(new(relative, value.Name, existing, replacement, value.PreserveOnUninstall));
        }
        return entries;
    }
}
