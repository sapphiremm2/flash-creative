using System.Security.Cryptography;

namespace AdobeDownloader.Core;

/// <summary>A freshly staged runtime whose executable remains locked and publisher-verified.
/// Preparation alone does not authorize elevation or launch.</summary>
public sealed class PreparedRuntime : IDisposable
{
    internal VerifiedExecutableLease Lease { get; }
    public PlannedRuntimeInstaller Policy { get; }
    public AuthenticodeResult Signature => Lease.Verification;
    public string Sha256 { get; }
    internal PreparedRuntime(VerifiedExecutableLease lease, PlannedRuntimeInstaller policy, string sha256)
    { Lease = lease; Policy = policy; Sha256 = sha256; }
    public void Dispose() => Lease.Dispose();
}

public sealed class RuntimeInstallerPreparation(AdobeTransport transport)
{
    public async Task<PreparedRuntime> PrepareAsync(DownloadPlan plan, string archive, string destination, CancellationToken ct = default)
    {
        const string product = "VC14win64", package = "VCRedist14-64";
        var selected = plan.Downloads.Where(d => d.SapCode == product && d.Package.Name == package).ToArray();
        if (selected.Length != 1 || !RuntimeInstallerPolicy.IsReviewedPackage(product, selected[0].ProductVersion, package) || plan.Platform != "win64")
            throw new InvalidDataException("Only the reviewed win64 VC2022 runtime package is supported.");
        destination = Path.GetFullPath(destination);
        var variables = new Dictionary<string, string> { ["StagingFolder"] = Path.Combine(destination, "symbolic-stage"), ["InstallDir"] = Path.Combine(destination, "unused-install-target") };
        var receipt = await new InstallStager(transport).StageAsync(plan, product, package, archive, variables, destination, 128 * 1024 * 1024, ct);
        if (receipt.Plan.Runtimes is not { Count: 1 } || receipt.Plan.Blockers.Any(b => b.Kind != "Commands/RunProgram/Execution") || receipt.Files.Count != 0)
            throw new InvalidDataException("Runtime package has unsupported installation instructions.");
        var policy = receipt.Plan.Runtimes[0];
        if (receipt.Resources is not { Count: 1 } || receipt.Resources[0].ArchiveEntry != policy.ArchiveEntry)
            throw new InvalidDataException("Runtime payload differs from the reviewed package layout.");
        var resource = receipt.Resources[0];
        using var source = LockedWindowsFile.Open(Path.Combine(destination, resource.RelativePath));
        if (source.Stream.Length != resource.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(source.Stream, ct)).Equals(resource.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Staged runtime changed after fresh Adobe verification.");
        source.Stream.Position = 0;
        var executable = Path.Combine(destination, "VC_redist.x64.exe");
        await using (var output = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { await source.Stream.CopyToAsync(output, ct); await output.FlushAsync(ct); output.Flush(true); }
        ct.ThrowIfCancellationRequested();
        var verified = WindowsSignatureVerifier.VerifyAndHold(executable, policy.Publisher);
        try
        {
            if (!Convert.ToHexString(await SHA256.HashDataAsync(verified.File.Stream, ct)).Equals(resource.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("Runtime executable differs from its freshly verified payload.");
            verified.File.Stream.Position = 0;
            return new(verified, policy, resource.Sha256);
        }
        catch { verified.Dispose(); throw; }
    }
}
