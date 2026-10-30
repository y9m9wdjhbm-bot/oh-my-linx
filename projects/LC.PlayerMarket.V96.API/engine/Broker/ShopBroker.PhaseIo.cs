namespace MemoryHub.Service;

public sealed partial class ShopBroker
{
    // These groups contain independent destinations. Each existing writer
    // retains its own event/head order and Flush(true). The owning page/native
    // gate must remain held until every writer and every fault is observed.
    internal static Task<ShopDurablePhaseIoReceipt> PersistScanAttemptPhaseAsync(
        ShopScanProgressStore progress, SavedScanPage page, string requestId, string requestPath, object requestEnvelope) =>
        ShopDurablePhaseIo.RunAsync("scan-attempt-and-child-request",
            [() => progress.BeginAttempt(page, requestId), () => Durable(requestPath, requestEnvelope)], CancellationToken.None);

    internal static Task<ShopDurablePhaseIoReceipt> PersistNativeIntentPhaseAsync(
        ShopSearchedOwnerStore owners, ShopScanProgressStore progress, SourceIdentity source, MarketOwner owner,
        string direction, string requestId, string pendingPath, object pendingEnvelope, bool forceDispatch,
        Action ownerIntentCommitted) =>
        ShopDurablePhaseIo.RunAsync("owner-scan-and-pending-native-intent",
            [() => { owners.Begin(source, owner, direction, requestId, forceDispatch); ownerIntentCommitted(); },
             () => progress.NativeIntent(requestId), () => Durable(pendingPath, pendingEnvelope)], CancellationToken.None);

    internal static Task<ShopDurablePhaseIoReceipt> PersistScanResultPhaseAsync(
        ShopScanProgressStore progress, string requestId, string responsePath, ShopCommandResponse response) =>
        ShopDurablePhaseIo.RunAsync("scan-response-and-result",
            [() => Durable(responsePath, response), () => progress.Result(requestId, response)], CancellationToken.None);
}
