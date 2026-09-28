using System.Runtime.Versioning;
using System.Security.Principal;

namespace AdobeDownloader.Core;

/// <summary>Serializes participating installation coordinators for this Windows user/session.
/// Uses a dedicated owner thread because Windows mutex ownership cannot cross async continuations.
/// This is cooperative concurrency control, not an elevated authorization boundary.</summary>
[SupportedOSPlatform("windows")]
public static class InstallationSessionGate
{
    public static Task RunAsync(Func<Task> operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidDataException("Cannot identify installation session user.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            Exception? failure = null; var acquired = false;
            Mutex? gate = null;
            try
            {
                gate = new Mutex(false, @"Local\FlashCreative.Installation." + sid);
                while (!acquired)
                {
                    ct.ThrowIfCancellationRequested();
                    try { acquired = gate.WaitOne(100); }
                    catch (AbandonedMutexException) { acquired = true; }
                }
                ct.ThrowIfCancellationRequested();
                operation().GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try { if (acquired) gate!.ReleaseMutex(); gate?.Dispose(); }
                catch (Exception ex) { failure ??= ex; }
            }
            if (failure is OperationCanceledException) completion.TrySetCanceled(ct);
            else if (failure is not null) completion.TrySetException(failure);
            else completion.TrySetResult();
        }) { IsBackground = true, Name = "Flash Creative installation session" };
        worker.Start(); return completion.Task;
    }
}
