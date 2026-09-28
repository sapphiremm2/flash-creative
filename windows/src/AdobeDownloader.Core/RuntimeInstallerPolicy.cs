using System.Xml.Linq;
namespace AdobeDownloader.Core;

public sealed record PlannedRuntimeInstaller(string Policy, string ArchiveEntry, string Publisher, IReadOnlyList<string> Arguments);

/// <summary>Code-owned runtime allowlist. Downloaded metadata cannot choose publishers or arbitrary commands.</summary>
public static class RuntimeInstallerPolicy
{
    public static bool IsReviewedPackage(string product, string version, string package) =>
        product == "VC14win64" && version == "2.0.0.2" && package == "VCRedist14-64";
    public static PlannedRuntimeInstaller Compile(string product, string version, string package, XElement command)
    {
        if (!IsReviewedPackage(product, version, package)) throw new InvalidDataException("Runtime package has no reviewed execution policy.");
        if (command.Name != "RunProgram" || command.HasAttributes || command.Elements().Count() != 1 || command.Element("InstallCommand") is not { } install ||
            install.Attributes().Count() != 1 || install.Attribute("isThirdParty")?.Value != "true" ||
            install.Elements().Count() != 2 || install.Element("Path") is not { } path || install.Element("Arguments") is not { } arguments ||
            path.HasAttributes || path.HasElements || arguments.HasAttributes ||
            path.Value.Replace('\\', '/') != "[StagingFolder]/VC_redist.x64.exe" ||
            arguments.Elements().Any(e => e.Name != "Argument" || e.HasAttributes || e.HasElements) ||
            command.DescendantsAndSelf().Where(e => e.HasElements).Any(e => e.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))) ||
            !arguments.Elements().Select(e => e.Value).SequenceEqual(new[] { "/q", "/norestart" }))
            throw new InvalidDataException("Runtime command differs from the reviewed VC2022 x64 policy.");
        return new("VC2022-x64-2.0.0.2", "1/VC_redist.x64.exe", "Microsoft Corporation", ["/q", "/norestart"]);
    }
    public static RuntimeExitResult ClassifyExit(int exitCode) => exitCode switch {
        0 => new(exitCode, true, false, false),
        3010 => new(exitCode, true, true, false),
        1641 => new(exitCode, true, true, true),
        _ => new(exitCode, false, false, false) };
}
public sealed record RuntimeExitResult(int ExitCode, bool Succeeded, bool RebootRequired, bool RebootInitiated);
