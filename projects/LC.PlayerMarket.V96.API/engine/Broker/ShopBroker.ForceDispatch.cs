using System.Diagnostics;
using MemoryHub.Reader;

namespace MemoryHub.Service;

internal static class ShopForceDispatchPolicy
{
    internal static void ValidateTargets(ShopTargetSnapshot? snapshot, SourceIdentity? source, ShopCommandRequest request)
    {
        // A fresh list may replace the UI's older snapshot between two clicks.
        // Force binds to the latest saved list of the requested local source;
        // caller owner ID/identity is separately matched against that list.
        ShopTargetPolicy.Validate(snapshot, source, source, request.ExpectedSourceSessionKey,
            snapshot?.SnapshotId, requireDispatch: false);
    }

    internal static MarketOwner RequireSavedShoppingOwner(ShopTargetSnapshot targets, ulong ownerId, string? identity)
    {
        var owner = ShopTargetPolicy.Owner(targets, ownerId, identity);
        if (!ShopEligibilityPolicy.CanScan(owner))
            throw new ShopRequestFailure(new(ShopEligibilityPolicy.RejectionCode(owner), "Target", "BeforePublish", null,
                ShopEligibilityPolicy.RejectionDetail(owner)));
        return owner;
    }

    internal static ShopPreparedDispatch PrepareShoppingDispatch(Func<ShopDispatchSample> read, SourceIdentity expected,
        ReadSelection selected, ShopTargetSnapshot targets, MarketOwner requested, Func<DateTimeOffset> utcNow, FieldReading? map = null,
        ulong? selectedOwnerId = null)
    {
        var savedOwner = RequireSavedShoppingOwner(targets, requested.Id, requested.LocalIdentity);
        // One closed reader call; recovery of a changing collection stays inside that reader.
        var prepared = ShopPreparedDispatch.Capture(read, expected, selected, expected.SessionKey, utcNow, map, selectedOwnerId);
        ShopDispatchPolicy.RequireOwner(prepared.Context, targets, savedOwner, utcNow(), requireDispatch: false);
        return prepared;
    }

    internal static ShopPreparedDispatch PrepareFrozenShoppingDispatch(Func<ShopDispatchSample> read, SourceIdentity expected,
        ReadSelection selected, ShopTargetSnapshot targets, MarketOwner requested, Func<DateTimeOffset> utcNow)
    {
        var owner = RequireSavedShoppingOwner(targets, requested.Id, requested.LocalIdentity);
        return ShopPreparedDispatch.CaptureFrozen(read, expected, selected, targets, owner, utcNow);
    }

    // Called exclusively before this page's NativeIntent/owner intent/pending write.
    // A historical session block must not become this never-published page's native uncertainty.
    internal static ShopCommandResponse RejectUnpublishedReadTimeout(ShopDispatchReadTimeoutException error,
        SourceIdentity expected, bool runtimeRunning, bool sameConfiguration, SourceIdentity? current,
        ShopCommandResponse response)
    {
        if (error.ExpectedSource != expected || error.Attempts is < 1 or > ShopReadAttemptLimit
            || !double.IsFinite(error.ElapsedMilliseconds) || error.ElapsedMilliseconds < 0
            || !runtimeRunning || !sameConfiguration || ShopSourceAvailability.Changed(current, expected)
            || response.NativeEntered || response.NativeReturned || response.ResponseVerified)
            throw new ShopRequestFailure(ShopFailure.Global("SourceUnavailable",
                "唯讀逾時交付時來源、設定或送出前證據不符；沒有授予接續資格。"));
        return ShopFailure.Global("DispatchReadTimeout", error.Message).Attach(response with
        {
            State = "Rejected", Detail = error.Message, NativeEntered = false, NativeReturned = false,
            ResponseVerified = false, SessionBlocked = false
        });
    }

    private const int ShopReadAttemptLimit = 3;

    internal static void CancelPriorWorker(ScanPauseCoordinator? pause, CancellationTokenSource? stop)
    {
        pause?.Cancel();
        stop?.Cancel();
    }

    internal static PersonalShopListing? ObserveOnce(Func<PersonalShopListing> read, out string detail)
    {
        try { detail = ""; return read(); }
        catch (Exception error) { detail = error.Message; return null; }
    }

    internal static string PendingPath(string directory, string sessionStem, string requestId) =>
        Path.Combine(directory, "sessions", sessionStem + ".force-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requestId))) + ".pending.json");
}

public sealed partial class ShopBroker
{
    private bool scanForceDispatch;

    // This queue protects a single native transport and scan worker. It never
    // waits for old results or game ACK observations.
    private async Task AcquireForcePreparationGateAsync(CancellationToken token)
    {
        while (true)
        {
            Task? prior;
            lock (gate)
            {
                prior = scanTask is { IsCompleted: false } ? scanTask : null;
                if (prior is not null)
                    ShopForceDispatchPolicy.CancelPriorWorker(scanPause, scanStop);
            }
            if (prior is not null) await prior.WaitAsync(token).ConfigureAwait(false);
            await nativeGate.WaitAsync(token).ConfigureAwait(false);
            lock (gate)
            {
                if (scanTask is not { IsCompleted: false }) return;
            }
            nativeGate.Release();
        }
    }

    private (ShopTargetSnapshot Targets, SourceIdentity Source) RequireForcedTargets(ShopCommandRequest req)
    {
        lock (gate)
        {
            if (!runtime.IsRunning)
                throw new ShopRequestFailure(ShopFailure.Global("ContextUnavailable", "本機讀取器尚未啟動；無法解析本機 LC 指令來源。"));
            // Saved observations identify the owner. A separate closed read below
            // must still authorize this page; background status alone is no gate.
            ShopForceDispatchPolicy.ValidateTargets(targetSnapshot, targetSource, req);
            if (ShopSourceAvailability.Changed(runtime.Snapshot.Source, targetSource!))
                throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "LC 完整來源已改變，不能將舊本機玩家 ID 傳入新程序。"));
            return (targetSnapshot!, targetSource!);
        }
    }

    private async Task<ShopCommandResponse> OpenForcedAsync(ShopCommandRequest req, CancellationToken token)
    {
        var pageTimer = Stopwatch.StartNew();
        var sourceTimer = new Stopwatch();
        var nativeTimer = new Stopwatch();
        var responseTimer = new Stopwatch();
        var pageDisplayTimer = new Stopwatch();
        var intentPersistenceTimer = new Stopwatch();
        var settlementPersistenceTimer = new Stopwatch();
        var ownerCompletionPersistenceTimer = new Stopwatch();
        bool ownerIntentWritten = false, ownerCompletionCommitted = false, safelyReturnedForCompletion = false;
        bool pageDisplayCompleted = false, pageDisplayCancelled = false;
        SourceIdentity? ownedSource = null;
        int observations = 0;
        ReadOperationMetrics? responseReadMetrics = null;
        ShopDispatchReadMetrics? dispatchReadMetrics = null;
        await nativeGate.WaitAsync(token).ConfigureAwait(false);
        int requestedPageWaitMilliseconds = PageWaitMilliseconds;
        ShopScanPageCycleTiming.CaptureInterval(req.RequestId, requestedPageWaitMilliseconds);
        double queueWaitMs = pageTimer.Elapsed.TotalMilliseconds;
        try
        {
            token.ThrowIfCancellationRequested();
            sourceTimer.Start();
            var (targets, expected) = RequireForcedTargets(req);
            if (req.TargetSnapshotId != targets.SnapshotId)
                throw new ShopRequestFailure(ShopFailure.Global("TargetSnapshotMismatch", "本頁未指定目前固定名單版本。"));
            await RecoverNativePendingBeforePublishAsync(expected, token).ConfigureAwait(false);
            EnsureUnblocked(expected);
            var owner = ShopForceDispatchPolicy.RequireSavedShoppingOwner(targets, req.OwnerId!.Value, req.ExpectedOwnerIdentity);
            if (searchedOwners.ShouldSkip(owner.Id, req.ViewerDirection!, forceDispatch: true))
                return new ShopFailure("AlreadySearched", "Target", "BeforePublish", null,
                    "此玩家 ID 的本頁已有已送出搜尋紀錄；沿用略過已搜尋頁設定。")
                    .Attach(State(req.RequestId, "AlreadySearched", "已搜尋頁仍保留；未重複送出。") with { SessionBlocked = false });
            var selected = runtime.ReadSelection;
            var requestedOwner = owner;
            EnsureNativeAvailable(expected);
            ulong hwnd = ShopRuntimeContext.Window(expected);
            // Use the initially confirmed fixed owner list. This closed pass reads
            // only current self and the actual shop binding/baseline, never owner state.
            ShopPreparedDispatch prepared;
            try
            {
                prepared = ShopForceDispatchPolicy.PrepareFrozenShoppingDispatch(() => source.ResolveFrozenShopDispatchSource(expected, token, owner.Id),
                    expected, selected, targets, requestedOwner, () => DateTimeOffset.UtcNow);
            }
            catch (ShopDispatchReadTimeoutException timeout)
            {
                token.ThrowIfCancellationRequested();
                var rejected = ShopForceDispatchPolicy.RejectUnpublishedReadTimeout(timeout, expected,
                    runtime.IsRunning, ReferenceEquals(selected, runtime.ReadSelection), runtime.Snapshot.Source,
                    State(req.RequestId, "Rejected", timeout.Message));
                Audit("SHOP_DISPATCH_TIMEOUT_UNPUBLISHED", timeout.Message,
                    new { req.RequestId, owner.Id, req.ViewerDirection, ForceDispatch = true,
                        timeout.Attempts, timeout.ElapsedMilliseconds, NativeIntentWritten = false,
                        NativeEntered = false, NativeReturned = false, HistoricalPendingPreserved = true });
                return rejected;
            }
            catch (ShopDispatchCollectionChangedException transient)
            {
                token.ThrowIfCancellationRequested();
                if (transient.Source != expected || !runtime.IsRunning || !ReferenceEquals(selected, runtime.ReadSelection)
                    || ShopSourceAvailability.Changed(runtime.Snapshot.Source, expected))
                    throw new ShopRequestFailure(ShopFailure.Global("SourceUnavailable", "重新採樣後來源或設定已改變；本頁未送出。"));
                Audit("SHOP_DISPATCH_COLLECTION_SKIPPED", transient.Message,
                    new { req.RequestId, owner.Id, req.ViewerDirection, ForceDispatch = true, NativeEntered = false });
                return new ShopFailure("ActorCollectionChanged", "Page", "BeforePublish", null, transient.Message)
                    .Attach(State(req.RequestId, "Rejected", transient.Message) with
                    { NativeEntered = false, NativeReturned = false, ResponseVerified = false });
            }
            var binding = prepared.Binding;
            dispatchReadMetrics = binding.ReadMetrics;
            token.ThrowIfCancellationRequested();
            if (!runtime.IsRunning || !ReferenceEquals(selected, runtime.ReadSelection)
                || ShopSourceAvailability.Changed(runtime.Snapshot.Source, expected))
                throw new ShopRequestFailure(ShopFailure.Global("SourceUnavailable", "同輪採樣交付時來源或讀取設定已改變；本頁未送出。"));
            owner = requestedOwner;
            Audit("SHOP_DISPATCH_SAMPLE", "直接送出前取得同輪必要欄位與商店基準頁，只核對目前自身與真實商店基準頁；名單保持定位點取得時的內容。",
                new { req.RequestId, ForceDispatch = true, Evidence = prepared.Evidence(DateTimeOffset.UtcNow) });
            var baseline = binding.BaselineListing;
            sourceTimer.Stop();
            token.ThrowIfCancellationRequested();
            string nativeLog = NewNativeLogPath();
            string pending = ShopForceDispatchPolicy.PendingPath(directory, SessionStem(expected), req.RequestId);
            intentPersistenceTimer.Start();
            var startedAt = DateTimeOffset.UtcNow;
            object nativeIntentEnvelope;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (!runtime.IsRunning || !ReferenceEquals(selected, runtime.ReadSelection)
                    || ShopSourceAvailability.Changed(runtime.Snapshot.Source, expected))
                    throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "指令送出前 LC 完整來源已變更。"));
                var (latestTargets, latestSource) = RequireForcedTargets(req);
                if (latestTargets.SnapshotId != targets.SnapshotId || req.TargetSnapshotId != targets.SnapshotId)
                    throw new ShopRequestFailure(ShopFailure.Global("TargetSnapshotMismatch", "本頁固定名單已被替換；沒有改用另一份名單。"));
                if (latestSource != expected)
                    throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "指令送出前保存名單的 LC 完整來源已變更。"));
                requestedOwner = ShopForceDispatchPolicy.RequireSavedShoppingOwner(latestTargets, req.OwnerId!.Value, req.ExpectedOwnerIdentity);
                var publishAt = DateTimeOffset.UtcNow;
                prepared.RequireFresh(publishAt);
                owner = requestedOwner;
                targets = latestTargets;
                if (activeNativeSource is not null)
                    throw new ShopRequestFailure(ShopFailure.Global("NativeTransportUnavailable", "同程序原生傳輸資源尚未釋放。"));
                RequireNativeHelperAvailable();
                EnsureUnblocked(expected);
                activeNativeSource = ownedSource = expected;
                nativeIntentEnvelope = new { State = "ForceNativeLaunchIntent_NoAutomaticReplay", req.RequestId,
                    ForceDispatch = true, Source = expected, Owner = owner, binding.CurrentOwnerIdRaw,
                    TargetOwner = owner.Id, req.ViewerDirection, NativeLog = nativeLog, At = startedAt };
            }
            // The original in-gate admission is the cancellation boundary.
            // Once admitted, join every durable writer and invoke native once;
            // cancellation is handled by the original post-settlement boundary.
            await PersistNativeIntentPhaseAsync(searchedOwners, savedScans, expected, owner, req.ViewerDirection!,
                req.RequestId, pending, nativeIntentEnvelope, true, () => ownerIntentWritten = true).ConfigureAwait(false);
            Audit("SHOP_FORCE_NATIVE_INTENT", "依固定擺攤名單與當下自身／商店根直接下達清單指令；不等待舊未確認結果或遊戲回覆狀態。",
                new { req.RequestId, owner.Id, req.ViewerDirection, Pending = pending,
                    RequestedSnapshotId = req.TargetSnapshotId, BoundSnapshotId = targets.SnapshotId,
                    SavedObservationAt = requestedOwner.ObservedAt, ShopStateGate = false, FrozenShoppingTargets = true, ResponsePolling = false,
                    CurrentOwner = owner, Evidence = prepared.Evidence(startedAt) });
            intentPersistenceTimer.Stop();
            ShopNativeOutcome native;
            nativeTimer.Start();
            try { native = await InvokeNativeAsync(binding, hwnd, owner.Id, req.ViewerDirection!, nativeLog, req.RequestId, forceDispatch: true).ConfigureAwait(false); }
            catch (Exception error)
            {
                MarkBlocked(expected, "本次強制指令的原生結果未確認：" + error.Message);
                return NativeInvocationFailure(error).Attach(State(req.RequestId, "Unverified", error.Message)
                    with { SessionBlocked = true });
            }
            finally { nativeTimer.Stop(); }
            var nativeCompletionClock = Stopwatch.StartNew();
            if (!native.SafeToSettle || !native.Returned && !native.NoEntryConfirmed)
            {
                lock (gate) postCodePageWait = new(req.RequestId, requestedPageWaitMilliseconds, nativeCompletionClock);
                MarkBlocked(expected, native.Detail);
                return (native.Failure ?? ShopFailure.Unknown(native.Detail)).Attach(
                    State(req.RequestId, "Unverified", "本次指令結果未確認，保留獨立意圖紀錄；" + native.Detail)
                    with { NativeEntered = native.Entered, NativeReturned = native.Returned, SessionBlocked = true });
            }
            // Settle only this new request. Older unknown journals/attempts remain.
            settlementPersistenceTimer.Start();
            safelyReturnedForCompletion = native.Returned;
            if (!native.Returned) searchedOwners.Settle(req.RequestId, false);
            if (ShopNativeOutcome.IsGuardRejected(native)) savedScans.NativeGuardRejected(req.RequestId, native.Detail);
            else savedScans.NativeSettled(req.RequestId, native.Entered, native.Returned, native.Detail);
            File.Move(pending, pending[..^".pending.json".Length] + ".resolved-" + Guid.NewGuid().ToString("N") + ".json");
            settlementPersistenceTimer.Stop();
            if (!native.Returned)
                return (native.Failure ?? ShopFailure.Unknown(native.Detail)).Attach(
                    State(req.RequestId, "NativeRejected", native.Detail)
                    with { NativeEntered = native.Entered, NativeReturned = false, SessionBlocked = false,
                        PreflightNotEnteredConfirmed = native.NoEntryConfirmed && native.SourceMatched
                            && native.Failure?.Code == "NativePreflightPageChanged",
                        PreflightNoEntrySource = native.NoEntryConfirmed && native.SourceMatched
                            && native.Failure?.Code == "NativePreflightPageChanged" ? binding.Source : null });

            // Native intent and cleanup are settled above. Keep this gate's page
            // visible for the captured setting before one fresh product observation.
            pageDisplayTimer.Start();
            try { pageDisplayCompleted = await ShopPagePacing.WaitBeforeCaptureMillisecondsAsync(native, token, waitMilliseconds: requestedPageWaitMilliseconds, elapsedSinceNativeCompletion: nativeCompletionClock.Elapsed).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                pageDisplayCancelled = true;
                CompleteOwnerPage(false);
                return ShopPagePacing.CancelledAfterSettlement(State(req.RequestId, "Cancelled", ""), native);
            }
            finally { pageDisplayTimer.Stop(); }
            responseTimer.Start();
            observations = 1;
            var after = ShopForceDispatchPolicy.ObserveOnce(() => ReadListing(expected, CancellationToken.None, out responseReadMetrics), out string readDetail);
            responseTimer.Stop();
            bool matched = ShopSingleReadPagePolicy.ProductsMatch(req, expected, after, startedAt, DateTimeOffset.UtcNow);
            var emptyObservation = !matched && runtime.Snapshot.Source == expected
                ? ShopSingleReadPagePolicy.TryObserveEmpty(req, expected, baseline, after, native, startedAt, DateTimeOffset.UtcNow) : null;
            CompleteOwnerPage(matched);
            string detail = matched
                ? "指令已下達並返回；本次一次讀值與請求店主／方向相符。未宣稱伺服器回覆世代或網路 ACK。"
                : "指令已下達並返回；一次採樣未確認新商品回覆，保留未確認標示並繼續後續指令。";
            if (emptyObservation is not null) detail = ShopEmptyPageRules.SingleReadDetail;
            if (readDetail.Length != 0) detail += " " + readDetail;
            MarketListing? responseListing = null;
            if (after is not null)
            {
                bool rowsBound = after.OwnerIdRaw == owner.Id && after.PageMode == req.ViewerDirection && after.Complete;
                var listing = new MarketListing { Revision = Guid.NewGuid().ToString("N"), SourceSessionKey = expected.SessionKey,
                    RequestId = req.RequestId, Owner = owner, ViewerDirection = req.ViewerDirection!, CapturedAt = after.SampledAt,
                    RequestedAt = startedAt, PageDurationMs = pageTimer.Elapsed.TotalMilliseconds,
                    SourceReadDurationMs = sourceTimer.Elapsed.TotalMilliseconds, NativeDurationMs = nativeTimer.Elapsed.TotalMilliseconds,
                    ResponseReadDurationMs = responseTimer.Elapsed.TotalMilliseconds, ResponseReadAttempts = 1,
                    SelfPositionAtRequest = null, DistanceAtRequest = null,
                    State = matched ? "OwnerAndResponseMatched" : emptyObservation is not null ? ShopEmptyPageRules.SingleReadState : "Unverified", Detail = detail,
                    EmptyPageObserved = emptyObservation is not null, EmptyPageObservation = emptyObservation,
                    OwnerAndResponseMatched = matched, PageEmpty = matched && rowsBound && after.PageEmpty,
                    Complete = matched && after.Complete, Rows = emptyObservation is not null ? [] : rowsBound ? after.Rows : [] };
                responseListing = listing;
                RememberListing(listing);
                Audit("SHOP_FORCE_LISTING_OBSERVED", detail,
                    new { Listing = listing, RawObservation = after, NativeReturnedIsNotAck = true, ResponseGenerationVerified = false });
            }
            var response = ShopResponseBudget.Fit(State(req.RequestId, matched ? "OwnerAndResponseMatched" : emptyObservation is not null ? ShopEmptyPageRules.SingleReadState : "Unverified", detail, requiredListing: responseListing)
                with { NativeEntered = native.Entered, NativeReturned = true, ResponseVerified = matched, SessionBlocked = false,
                    EmptyPageObserved = emptyObservation is not null, EmptyPageObservation = emptyObservation }, requiredListing: responseListing);
            return matched || ShopEmptyPageRules.IsNeutralResponse(response) ? response
                : new ShopFailure("ShopResponseUnverified", "Target", "ResponseVerification", null, detail).Attach(response);
        }
        finally
        {
            try { CompleteOwnerPage(false); }
            finally
            {
            lock (gate) if (ownedSource is not null && activeNativeSource == ownedSource) activeNativeSource = null;
            pageTimer.Stop(); sourceTimer.Stop(); nativeTimer.Stop(); responseTimer.Stop();
            intentPersistenceTimer.Stop(); settlementPersistenceTimer.Stop(); ownerCompletionPersistenceTimer.Stop();
            try { Audit("SHOP_FORCE_PAGE_TIMING", "指令依原生生命週期串行；本頁依開始時設定等待後只讀一次商品，不輪詢等狀態。",
                new { req.RequestId, QueueWaitDurationMs = queueWaitMs, PageDurationMs = pageTimer.Elapsed.TotalMilliseconds,
                    SourceReadDurationMs = sourceTimer.Elapsed.TotalMilliseconds, NativeDurationMs = nativeTimer.Elapsed.TotalMilliseconds,
                    ResponseReadDurationMs = responseTimer.Elapsed.TotalMilliseconds, ResponseReadAttempts = observations,
                    ResponseReadGateWaitMs = responseReadMetrics?.AdmissionWaitMs, ResponseReadBodyMs = responseReadMetrics?.BodyMs,
                    DispatchReadGateWaitMs = dispatchReadMetrics?.AdmissionWaitMs, DispatchReadBodyMs = dispatchReadMetrics?.BodyMs,
                    DispatchCacheOwnerProbeMs = dispatchReadMetrics?.OwnerProbeMs, DispatchCacheReused = dispatchReadMetrics?.CacheReused,
                    DispatchCacheDecision = dispatchReadMetrics?.CacheDecision,
                    IntentPersistenceDurationMs = intentPersistenceTimer.Elapsed.TotalMilliseconds,
                    SettlementPersistenceDurationMs = settlementPersistenceTimer.Elapsed.TotalMilliseconds,
                    OwnerCompletionPersistenceDurationMs = ownerCompletionPersistenceTimer.Elapsed.TotalMilliseconds,
                    ResponseReadDeadlineMilliseconds = 0, RequestedPageWaitSeconds = requestedPageWaitMilliseconds / 1000d,
                    RequestedPageWaitMilliseconds = requestedPageWaitMilliseconds,
                    PageDisplayWaitDurationMs = pageDisplayTimer.Elapsed.TotalMilliseconds,
                    PageDisplayWaitCompleted = pageDisplayCompleted, PageDisplayWaitCancelled = pageDisplayCancelled }); }
            finally { nativeGate.Release(); }
            }
        }
        void CompleteOwnerPage(bool confirmed)
        {
            if (!safelyReturnedForCompletion || !ownerIntentWritten || ownerCompletionCommitted) return;
            ownerCompletionPersistenceTimer.Start();
            try { searchedOwners.CompletePage(req.RequestId, confirmed); ownerCompletionCommitted = true; }
            catch (Exception error) { if (ownedSource is not null) MarkBlocked(ownedSource, "已返回頁的持久完成未確認：" + error.Message); throw; }
            finally { ownerCompletionPersistenceTimer.Stop(); }
        }
    }
}
