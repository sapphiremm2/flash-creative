using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdobeDownloader.Core;

public sealed record RuntimeExecutionRecord(int Version, Guid ExecutionId, string Sha256, string State,
    int? ProcessId = null, DateTimeOffset? ProcessStartedUtc = null, int? ExitCode = null);

public sealed record RuntimeRecoveryStatus(string Outcome, bool MayBeginLaunch, RuntimeExitResult? Exit);

/// <summary>Durable launch bookkeeping for trusted, unprivileged fixtures. Does not launch processes,
/// authenticate journals, authorize elevation, or determine whether a recorded PID is still alive.</summary>
public static class RuntimeExecutionJournal
{
    private static readonly JsonSerializerOptions Options = new(JsonFiles.Options)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4 };

    public static async Task CreateAsync(string directory, Guid executionId, string sha256, CancellationToken ct = default)
    {
        var record = new RuntimeExecutionRecord(1, executionId, sha256, "Prepared");
        Validate(record, executionId, sha256);
        ct.ThrowIfCancellationRequested();
        PrivateStorage.CreateNewDirectory(directory);
        using var gate = FileTransaction.Lock(directory);
        await JsonFiles.WriteAsync(Path.Combine(directory, "runtime.json"), record, overwrite: false, ct);
    }

    public static Task RecordLaunchIntentAsync(string directory, Guid executionId, string sha256, CancellationToken ct = default) =>
        Transition(directory, executionId, sha256, "Prepared", r => r with { State = "LaunchIntent" }, ct);

    public static Task RecordProcessAsync(string directory, Guid executionId, string sha256, int processId,
        DateTimeOffset processStartedUtc, CancellationToken ct = default) =>
        Transition(directory, executionId, sha256, "LaunchIntent", r => r with
        { State = "Started", ProcessId = processId, ProcessStartedUtc = processStartedUtc }, ct);

    public static Task RecordCompletionAsync(string directory, Guid executionId, string sha256, int exitCode,
        CancellationToken ct = default) =>
        Transition(directory, executionId, sha256, "Started", r => r with { State = "Completed", ExitCode = exitCode }, ct);

    public static async Task<RuntimeRecoveryStatus> InspectAsync(string directory, Guid executionId, string sha256,
        CancellationToken ct = default)
    {
        directory = FileTransaction.ValidateRoot(directory);
        using var gate = FileTransaction.Lock(directory);
        var record = await Read(directory, executionId, sha256, ct);
        if (record.State == "Prepared") return new("NotLaunched", true, null);
        // Even a recorded PID may have exited or been reused. No automatic retry, kill, or rollback.
        if (record.State != "Completed") return new("RequiresReconciliation", false, null);
        var result = RuntimeInstallerPolicy.ClassifyExit(record.ExitCode!.Value);
        return new(result.Succeeded ? result.RebootRequired ? "RebootRequired" : "Succeeded" : "Failed", false, result);
    }

    private static async Task Transition(string directory, Guid executionId, string sha256, string expectedState,
        Func<RuntimeExecutionRecord, RuntimeExecutionRecord> change, CancellationToken ct)
    {
        directory = FileTransaction.ValidateRoot(directory);
        using var gate = FileTransaction.Lock(directory);
        var record = await Read(directory, executionId, sha256, ct);
        if (record.State != expectedState) throw new InvalidDataException("Runtime journal transition is not permitted; reconcile the existing execution.");
        var next = change(record);
        Validate(next, executionId, sha256);
        // Flush and atomic replacement must complete before the caller proceeds to its next side effect.
        await JsonFiles.WriteAsync(Path.Combine(directory, "runtime.json"), next, ct: ct);
    }

    private static async Task<RuntimeExecutionRecord> Read(string directory, Guid executionId, string sha256, CancellationToken ct)
    {
        var path = Path.Combine(directory, "runtime.json");
        FileTransaction.NoLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 2 or > 4096) throw new InvalidDataException("Runtime journal size is invalid.");
        using var document = await JsonDocument.ParseAsync(file, new JsonDocumentOptions { MaxDepth = 4 }, ct);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != document.RootElement.EnumerateObject().Count())
            throw new InvalidDataException("Runtime journal must be an object without duplicate fields.");
        var record = document.Deserialize<RuntimeExecutionRecord>(Options) ?? throw new InvalidDataException("Runtime journal is empty.");
        Validate(record, executionId, sha256);
        return record;
    }

    private static void Validate(RuntimeExecutionRecord record, Guid executionId, string sha256)
    {
        if (executionId == Guid.Empty || string.IsNullOrEmpty(sha256) || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit) ||
            record.Version != 1 || record.ExecutionId != executionId || !string.Equals(record.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runtime journal identity differs from the expected execution.");
        var hasProcess = record.ProcessId is > 0 && record.ProcessStartedUtc is { } started &&
            started.Offset == TimeSpan.Zero && started > DateTimeOffset.UnixEpoch;
        var valid = record.State switch
        {
            "Prepared" or "LaunchIntent" => record.ProcessId is null && record.ProcessStartedUtc is null && record.ExitCode is null,
            "Started" => hasProcess && record.ExitCode is null,
            "Completed" => hasProcess && record.ExitCode is not null,
            _ => false
        };
        if (!valid) throw new InvalidDataException("Runtime journal state contradicts its process or exit fields.");
    }
}
