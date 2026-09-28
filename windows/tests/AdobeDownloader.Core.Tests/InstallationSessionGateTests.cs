using System.Runtime.Versioning;
namespace AdobeDownloader.Core.Tests;

[SupportedOSPlatform("windows")]
public class InstallationSessionGateTests
{
    [Fact] public async Task ConcurrentSessionsCannotInterleaveAndWaitingCancellationDoesNotRunWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = InstallationSessionGate.RunAsync(async () => { entered.SetResult(); await release.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var cancellation = new CancellationTokenSource(); var ran = false;
            var second = InstallationSessionGate.RunAsync(() => { ran = true; return Task.CompletedTask; }, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(ran);
        }
        finally { release.SetResult(); await first; }
        await InstallationSessionGate.RunAsync(() => Task.CompletedTask);
    }
    [Fact] public async Task FailingOperationReleasesGateAfterAsyncContinuation()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallationSessionGate.RunAsync(async () => { await Task.Yield(); throw new InvalidDataException("fixture"); }));
        await InstallationSessionGate.RunAsync(async () => await Task.Yield()).WaitAsync(TimeSpan.FromSeconds(10));
    }
}
