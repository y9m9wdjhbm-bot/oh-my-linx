namespace MemoryHub.Service;

public sealed partial class ShopBroker
{
    // The production adapter is supplied by the exact native recovery contract.
    // The observer must only return current typed state. Neither hook authorizes
    // native replay or implicit settlement of a different request.
    internal Func<SourceIdentity, ShopNativePendingObservation>? NativePendingObserver { get; set; }
    internal Func<SourceIdentity, CancellationToken, Task>? NativePendingRecovery { get; set; }

    private ShopNativePendingObservation ObserveNativePending(SourceIdentity source) =>
        ShopNativePendingPolicy.Observe(source, SharedJournal, NativePendingObserver);

    private bool HasUnsettledLocalNativeIntent(SourceIdentity source)
    {
        lock (gate) return blockedSessions.Contains(SessionStem(source)) || savedScans.BlocksSource(source)
            || searchedOwners.BlocksSource(source) || File.Exists(SessionPath(source));
    }

    // Call only outside lock(gate), with nativeGate held by this preparation.
    private Task RecoverNativePendingBeforePublishAsync(SourceIdentity source, CancellationToken token) =>
        ShopNativePendingPolicy.RecoverAndRequireReadyAsync(source, () => ObserveNativePending(source),
            () => HasUnsettledLocalNativeIntent(source), NativePendingRecovery, token);
}
