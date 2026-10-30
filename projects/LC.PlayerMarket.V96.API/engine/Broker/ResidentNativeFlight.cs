namespace MemoryHub.Service;

internal static class ShopResidentShutdownObservation
{
    // Caller owns one overall deadline. Neither an ignored stop token nor a
    // missing actual process exit can make shutdown wait forever.
    internal static async Task StopAndObserveAsync(Func<CancellationToken, Task> stop,
        Func<Task> actualExit, CancellationToken token)
    {
        await stop(token).WaitAsync(token).ConfigureAwait(false);
        await actualExit().WaitAsync(token).ConfigureAwait(false);
    }
}

internal sealed partial class ShopNativeHelperFlight
{
    private ResidentReservation? residentOperation;

    // A request reservation shares the same lock as the legacy process flight.
    // No fake IShopNativeHelperProcess or synthetic process exit is introduced.
    internal Task<T> RunResidentOperationAsync<T>(Func<Task<T>> start, Func<T, bool> positiveClosure,
        Func<Task> actualWorkerExit, TimeSpan waitBudget, Action? onTimeout = null)
    {
        if (waitBudget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(waitBudget));
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ResidentReservation owned;
        lock (gate)
        {
            if (current is not null || residentOperation is not null) throw new ShopNativeHelperBusyException();
            residentOperation = owned = new(actualWorkerExit);
        }
        _ = Observe();
        return Wait();
        async Task<T> Wait()
        {
            try
            {
                T result = await completion.Task.WaitAsync(waitBudget).ConfigureAwait(false);
                lock (gate)
                {
                    if (ReferenceEquals(residentOperation, owned) && !owned.Quarantined && owned.PositiveClosure)
                        residentOperation = null;
                }
                return result;
            }
            catch (TimeoutException error)
            {
                lock (gate) owned.Quarantined = true;
                try { onTimeout?.Invoke(); } catch { }
                throw new ShopNativeHelperWaitTimeoutException(error);
            }
        }
        async Task Observe()
        {
            try
            {
                T result = await start().ConfigureAwait(false);
                bool closed = positiveClosure(result);
                lock (gate)
                {
                    if (!ReferenceEquals(residentOperation, owned)) throw new InvalidOperationException("RESIDENT_RESERVATION_IDENTITY_CHANGED");
                    owned.PositiveClosure = closed;
                }
                // A returned negative outcome remains a global active fence.
                completion.TrySetResult(result);
            }
            catch (Exception error)
            {
                // Exit or a broken connection alone cannot settle the original
                // native intent. Retain the reservation and report uncertainty.
                completion.TrySetException(error);
                _ = completion.Task.Exception;
            }
            finally { owned.Observed.TrySetResult(); }
        }
    }
    internal Task WaitUntilResidentObservationAsync()
    { lock (gate) return residentOperation?.Observed.Task ?? Task.CompletedTask; }
    private sealed class ResidentReservation(Func<Task> actualWorkerExit)
    {
        internal Func<Task> ActualWorkerExit { get; } = actualWorkerExit;
        internal bool Quarantined, PositiveClosure;
        internal TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed partial class ShopBroker
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<ShopBroker> quarantinedResidentOwners = [];
    private ShopResidentNativeClient? residentNativeClient;
    private ShopResidentNativeClient ResidentNativeClient => residentNativeClient ??= new(
        Path.Combine(Path.GetDirectoryName(HelperPath)!, "WorkerV18"), nativeLogDirectory,
        (kind, data) => Audit(kind, "Resident worker V18 原始生命週期與請求證據。", data));

    private async Task WarmResidentWorkerAsync(SourceIdentity expected)
    {
        var client = ResidentNativeClient;
        var receipt = await nativeHelperFlight.RunResidentOperationAsync(() => client.WarmAsync(expected),
            r => r.HelloVerified, client.WaitForActualWorkerExitAsync, TimeSpan.FromSeconds(30),
            onTimeout: client.RetireAfterHostTimeout).ConfigureAwait(false);
        Audit("RESIDENT_BATCH_STARTUP", "Resident hello-only 預熱完成；不送遊戲指令，source pin仍待首次execute。", receipt);
    }
    private void PreserveUnconfirmedResidentOwnership() => quarantinedResidentOwners.Add(this);
}
