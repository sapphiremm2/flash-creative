namespace AdobeDownloader.Core.Tests;

public sealed class RuntimeExecutionJournalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FlashCreativeTests-" + Guid.NewGuid().ToString("N"));
    private readonly Guid execution = Guid.NewGuid();
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private string Journal => Path.Combine(root, "runtime");
    public RuntimeExecutionJournalTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task CrashAfterIntentOrProcessRecordNeverAuthorizesAutomaticRetry()
    {
        await RuntimeExecutionJournal.CreateAsync(Journal, execution, Hash);
        Assert.True((await RuntimeExecutionJournal.InspectAsync(Journal, execution, Hash)).MayBeginLaunch);
        await RuntimeExecutionJournal.RecordLaunchIntentAsync(Journal, execution, Hash);
        var intent = await RuntimeExecutionJournal.InspectAsync(Journal, execution, Hash);
        Assert.Equal("RequiresReconciliation", intent.Outcome);
        Assert.False(intent.MayBeginLaunch);
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeExecutionJournal.RecordLaunchIntentAsync(Journal, execution, Hash));
        await RuntimeExecutionJournal.RecordProcessAsync(Journal, execution, Hash, 123, DateTimeOffset.UtcNow);
        Assert.Equal(intent, await RuntimeExecutionJournal.InspectAsync(Journal, execution, Hash));
    }

    [Theory]
    [InlineData(0, "Succeeded", false, false)]
    [InlineData(3010, "RebootRequired", true, false)]
    [InlineData(1641, "RebootRequired", true, true)]
    [InlineData(1638, "Failed", false, false)]
    [InlineData(1603, "Failed", false, false)]
    public async Task CompletionPreservesFailureAndDistinctRebootOutcomes(int code, string outcome, bool reboot, bool initiated)
    {
        await RuntimeExecutionJournal.CreateAsync(Journal, execution, Hash);
        await RuntimeExecutionJournal.RecordLaunchIntentAsync(Journal, execution, Hash);
        await RuntimeExecutionJournal.RecordProcessAsync(Journal, execution, Hash, 123, DateTimeOffset.UtcNow);
        await RuntimeExecutionJournal.RecordCompletionAsync(Journal, execution, Hash, code);
        var result = await RuntimeExecutionJournal.InspectAsync(Journal, execution, Hash);
        Assert.Equal(outcome, result.Outcome); Assert.False(result.MayBeginLaunch);
        Assert.Equal(code, result.Exit!.ExitCode); Assert.Equal(reboot, result.Exit.RebootRequired); Assert.Equal(initiated, result.Exit.RebootInitiated);
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeExecutionJournal.RecordCompletionAsync(Journal, execution, Hash, 0));
    }

    [Fact]
    public async Task CancelledIntentLeavesPreparedButCancelledCompletionRemainsUncertain()
    {
        await RuntimeExecutionJournal.CreateAsync(Journal, execution, Hash);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeExecutionJournal.RecordLaunchIntentAsync(Journal, execution, Hash, new(true)));
        Assert.True((await RuntimeExecutionJournal.InspectAsync(Journal, execution, Hash)).MayBeginLaunch);
        await RuntimeExecutionJournal.RecordLaunchIntentAsync(Journal, execution, Hash);
        await RuntimeExecutionJournal.RecordProcessAsync(Journal, execution, Hash, 123, DateTimeOffset.UtcNow);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeExecutionJournal.RecordCompletionAsync(Journal, execution, Hash, 0, new(true)));
        Assert.Equal("RequiresReconciliation", (await RuntimeExecutionJournal.InspectAsync(Journal, execution, Hash)).Outcome);
    }

    [Fact]
    public async Task WrongExecutionOrDigestCannotAdvanceExistingJournal()
    {
        await RuntimeExecutionJournal.CreateAsync(Journal, execution, Hash);
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeExecutionJournal.RecordLaunchIntentAsync(Journal, Guid.NewGuid(), Hash));
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeExecutionJournal.RecordLaunchIntentAsync(Journal, execution, new string('B', 64)));
        Assert.True((await RuntimeExecutionJournal.InspectAsync(Journal, execution, Hash)).MayBeginLaunch);
    }

    [Theory]
    [InlineData("Completed", null, null)]
    [InlineData("Prepared", 123, 0)]
    [InlineData("Started", 123, 0)]
    [InlineData("Started", -1, null)]
    public async Task ContradictoryRecoveryRecordIsRejected(string state, int? pid, int? exit)
    {
        await RuntimeExecutionJournal.CreateAsync(Journal, execution, Hash);
        await JsonFiles.WriteAsync(Path.Combine(Journal, "runtime.json"), new RuntimeExecutionRecord(1, execution, Hash, state, pid, DateTimeOffset.UtcNow, exit));
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeExecutionJournal.InspectAsync(Journal, execution, Hash));
    }

    [Fact]
    public async Task SkippingLaunchIntentOrRecordingInvalidProcessDoesNotAdvanceState()
    {
        await RuntimeExecutionJournal.CreateAsync(Journal, execution, Hash);
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeExecutionJournal.RecordProcessAsync(Journal, execution, Hash, 123, DateTimeOffset.UtcNow));
        await RuntimeExecutionJournal.RecordLaunchIntentAsync(Journal, execution, Hash);
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeExecutionJournal.RecordProcessAsync(Journal, execution, Hash, 0, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeExecutionJournal.RecordCompletionAsync(Journal, execution, Hash, 0));
        Assert.Equal("RequiresReconciliation", (await RuntimeExecutionJournal.InspectAsync(Journal, execution, Hash)).Outcome);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    [InlineData("unknown")]
    public async Task MalformedJournalCannotAuthorizeLaunch(string corruption)
    {
        await RuntimeExecutionJournal.CreateAsync(Journal, execution, Hash);
        var path = Path.Combine(Journal, "runtime.json");
        var json = await File.ReadAllTextAsync(path);
        json = corruption switch
        {
            "duplicate" => json.Insert(json.LastIndexOf('}'), ",\"State\":\"Prepared\""),
            "unknown" => json.Insert(json.LastIndexOf('}'), ",\"Publisher\":\"Untrusted\""),
            _ => json + new string(' ', 4096)
        };
        await File.WriteAllTextAsync(path, json);
        if (corruption == "unknown")
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => RuntimeExecutionJournal.InspectAsync(Journal, execution, Hash));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeExecutionJournal.InspectAsync(Journal, execution, Hash));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
