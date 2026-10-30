using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemoryHub.Reader;

namespace MemoryHub.Service;

/// <summary>
/// Service-owned, once-only list-request broker. Reader supplies every game
/// observation; the separate guarded helper alone submits the native request.
/// No trade, click, field write, timeout retry or implicit field enable exists.
/// </summary>
public sealed partial class ShopBroker : IAsyncDisposable
{
    private const string SharedJournal = @"D:\lineage\LC_PlayerShopListAction_v1\journal";
    private const int MaximumOwners = ActorCollectionContract.MaximumActors, MaximumListings = 96;
    private const int ResponseReadDeadlineMilliseconds = 3000;
    private readonly HubRuntime runtime;
    private readonly CurrentLcSource source;
    private readonly string directory;
    private readonly string nativeLogDirectory;
    private readonly ShopScanProgressStore savedScans;
    private readonly ShopSearchedOwnerStore searchedOwners;
    private readonly IDisposable maintenanceLease;
    private readonly object gate = new();
    private readonly SemaphoreSlim nativeGate = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, RequestEntry> requests = new(StringComparer.Ordinal);
    private readonly HashSet<string> blockedSessions = new(StringComparer.Ordinal);
    private readonly List<MarketListing> listings = [];
    private MarketScanState scan = new();
    private ShopTargetSnapshot? targetSnapshot;
    private SourceIdentity? targetSource;
    private CancellationTokenSource? scanStop;
    private Task? scanTask, disposal;
    // Only nativeGate's current owner may set this. It describes work still
    // being settled by this broker, never permission to bypass a pending file.
    private SourceIdentity? activeNativeSource;
    private bool disposed;

    public ShopBroker(HubRuntime runtime, CurrentLcSource source, string? stateRoot = null, CancellationToken startupCancellation = default, string? nativeLogDirectory = null)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        directory = Path.GetFullPath(stateRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LCMemoryHub", "v32", "shops"));
        this.nativeLogDirectory = Path.GetFullPath(nativeLogDirectory ?? Path.Combine(directory, "native"));
        NativePendingRecovery = RecoverPostCodePendingAsync;
        var startupClock = Stopwatch.StartNew();
        Action<ShopStartupStageEvent> startupStage = item => Audit("SHOP_STARTUP_STAGE", "商店初始化分段耗時；保留完整歷史核對。", item);
        Audit("SHOP_STARTUP_BEGIN", "開始載入本機商店紀錄；尚未送出遊戲指令。", new { Directory = directory, GameRequests = 0 });
        Directory.CreateDirectory(directory);
        maintenanceLease=new FileStream(Path.Combine(directory,"lock-tool-operation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        try
        {
        Directory.CreateDirectory(Path.Combine(directory, "requests"));
        Directory.CreateDirectory(Path.Combine(directory, "sessions"));
        Directory.CreateDirectory(this.nativeLogDirectory);
        savedScans = ShopStartupTrace.Run("broker.scan-progress", startupStage,
            () => new ShopScanProgressStore(Path.Combine(directory, "scan-progress"), startupStage));
        searchedOwners = ShopStartupTrace.Run("broker.searched-owners", startupStage,
            () => new ShopSearchedOwnerStore(Path.Combine(directory, "searched-owners"),
                Path.Combine(directory, "scan-progress"), LegacyHistoryDirectory(directory), startupCancellation, startupStage));
        searchResets = new ShopSearchProgressResetStore(Path.Combine(directory, "search-progress-resets"));
        ShopStartupTrace.Run("broker.search-progress-reset-recovery", startupStage,
            () => searchResets.RecoverPending(searchedOwners, savedScans), count => count);
        try
        {
            var recovered=ShopStartupTrace.Run("broker.startup-recovery", startupStage,
                () => ShopStartupRecovery.Run(directory,savedScans,searchedOwners,startupStage:startupStage,nativeLogDirectory:this.nativeLogDirectory), items => items.Length);
            foreach(var item in recovered)Audit("SHOP_STARTUP_RECOVERY",item.Detail,new{item.RequestId,item.State,item.BackupDirectory,GameRequests=0});
        }
        catch(Exception recoveryError){Audit("SHOP_STARTUP_RECOVERY_DEFERRED",recoveryError.Message);}
        ShopStartupTrace.Run("broker.load-targets", startupStage, LoadTargets);
        Audit("SHOP_BROKER_READY", "本機商店查詢已就緒；只請求清單，每個 RequestId 最多執行一次。",
            new { StartupElapsedMs = startupClock.Elapsed.TotalMilliseconds, GameRequests = 0 });
        }
        catch (Exception initializationError)
        {
            try { maintenanceLease.Dispose(); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("商店初始化失敗，本機工作鎖釋放也失敗。", initializationError, cleanupError);
            }
            throw;
        }
    }

    public Task<ShopCommandResponse> HandleAsync(ShopCommandRequest req, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        Validate(req);
        if (req.Command == ShopCommands.GetState) { lock (gate) ObjectDisposedException.ThrowIf(disposed, this); return Task.FromResult(State(req.RequestId, "State", "已保留原始採樣時間；歷史清單不因查詢而重新變新。")); }
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (req.Command == ShopCommands.GetSearchedOwners) return Task.FromResult(ReadSearchedOwners(req));
            if (req.Command == ShopCommands.CancelScan)
            {
                if (req.ExpectedScanJobId is not null && req.ExpectedScanJobId != scan.JobId)
                    return Task.FromResult(State(req.RequestId, "Rejected", "搜尋工作已不同；未停止另一個工作。"));
                scanPause?.Cancel();
                scanStop?.Cancel();
                if (scan.Running) scan = scan with { Detail = "已要求停止；目前已啟動的單次請求會完成清理與回覆核對，之後不再開下一頁。" };
                Audit("SHOP_SCAN_CANCEL", "收到停止掃描要求；不終止已進入的原生呼叫。");
                return Task.FromResult(State(req.RequestId, "Stopping", scan.Detail));
            }
            if (req.Command is ShopCommands.PauseScan or ShopCommands.ResumeScan)
                return Task.FromResult(ControlScan(req));
            if (req.Command == ShopCommands.ReadNavigation) return StartNavigationRead(req, token);
            if (req.Command == ShopCommands.CheckNativeReadiness) return TrackNativeReadiness(CheckNativeReadinessAsync(req, token));
            token.ThrowIfCancellationRequested();
            string signature = Hash(JsonSerializer.Serialize(req, HubProtocol.Json));
            if (requests.TryGetValue(req.RequestId, out var existing))
            {
                if (existing.Signature != signature) return Task.FromResult(State(req.RequestId, "Rejected", "同一 RequestId 不可改成不同內容；未送出動作。"));
                return existing.Task;
            }
            string requestPath = RequestPath(req.RequestId);
            if (File.Exists(requestPath)) return Task.FromResult(ReadPrior(req, signature, requestPath));
            // Persist acceptance before scheduling. Restart never replays an accepted action.
            Durable(requestPath, new { Request = req, Signature = signature, State = "Accepted_NoAutomaticReplay", At = DateTimeOffset.UtcNow });
            Task<ShopCommandResponse>? resetBarrier = searchResetBarrier;
            Task[] priorRequests = [];
            CancellationToken generationToken = searchGenerationStop.Token;
            if (req.Command == ShopCommands.ResetSearchProgress)
            {
                priorRequests = requests.Values.Where(item => !item.Task.IsCompleted).Select(item => (Task)item.Task).ToArray();
                searchGenerationStop.Cancel();
                retiredSearchGenerations.Add(searchGenerationStop);
                searchGenerationStop = new();
                generationToken = CancellationToken.None;
                scanPause?.Cancel(); scanStop?.Cancel();
            }
            var task = Task.Run(() => RunAcceptedAsync(req, token, generationToken, resetBarrier, priorRequests), CancellationToken.None);
            if (req.Command == ShopCommands.ResetSearchProgress) searchResetBarrier = task;
            requests.Add(req.RequestId, new(signature, task));
            if (requests.Count > 512)
                foreach (var key in requests.Where(p => p.Value.Task.IsCompleted).Take(requests.Count - 512).Select(p => p.Key).ToArray()) requests.Remove(key);
            return task;
        }
    }

    private async Task<ShopCommandResponse> RunAcceptedAsync(ShopCommandRequest req, CancellationToken token,
        CancellationToken generationToken, Task<ShopCommandResponse>? resetBarrier, Task[] priorRequests)
    {
        var requestTimer = Stopwatch.StartNew();
        ShopCommandResponse response;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token, generationToken);
        try
        {
            if (resetBarrier is not null) await resetBarrier.WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (req.Command != ShopCommands.ResetSearchProgress) RequireSearchResetCompleted();
            response = req.Command == ShopCommands.ResetSearchProgress
                ? await ResetSearchProgressAsync(req, priorRequests, linked.Token).ConfigureAwait(false)
                : req.Command == ShopCommands.RefreshTargets
                ? await RefreshTargetsAsync(req, linked.Token).ConfigureAwait(false)
                : req.Command == ShopCommands.ClearSearchedOwners
                ? await ClearSearchedOwnersAsync(req, linked.Token).ConfigureAwait(false)
                : req.Command == ShopCommands.PrepareNativeReadiness
                ? await CheckNativeReadinessAsync(req, linked.Token).ConfigureAwait(false)
                : req.Command is ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan
                ? await StartScanAsync(req, linked.Token).ConfigureAwait(false)
                : await OpenAsync(req, false, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { response = State(req.RequestId, "Cancelled", "尚未開始下一個原生請求；已啟動的請求不會因用戶端取消而重送。" ); }
        catch (Exception ex) { Audit("SHOP_REQUEST_REJECTED", ex.Message, new { req.RequestId }); response = Rejected(req.RequestId, ex); }
        response = response with { RequestDurationMs = requestTimer.Elapsed.TotalMilliseconds };
        try { Durable(ResponsePath(req.RequestId), response); }
        catch (Exception ex) { Audit("SHOP_RESPONSE_SAVE_FAILED", ex.Message, new { req.RequestId }); response = response with { Detail = response.Detail + "；回覆保存失敗，但原請求已登記且不會重送。" }; }
        return response;
    }

    private async Task<ShopCommandResponse> RefreshTargetsAsync(ShopCommandRequest req, CancellationToken token)
    {
        // Once accepted, even a cancelled refresh revokes the idle retained list.
        // A busy gate still belongs to another operation and must not be revoked.
        if (req.ForceDispatch) await AcquireForcePreparationGateAsync(token).ConfigureAwait(false);
        else if (!await nativeGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false)) return State(req.RequestId, "Busy", "目前正在處理商店請求；周遭名單尚未刷新。");
        try
        {
            lock (gate) if (!req.ForceDispatch && scan.Running) return State(req.RequestId, "Busy", "請先停止掃描再手動刷新；目前名單保持不變。");
            BlockTargetsForRefresh();
            token.ThrowIfCancellationRequested();
            EnsureFields(req.ForceDispatch);
            if (!runtime.IsRunning || !runtime.ReadSelection.Enabled("nearbyPlayers"))
                throw new ShopRequestFailure(ShopFailure.Global("ContextUnavailable", "手動刷新需要本機讀取器及 nearbyPlayers 欄位；未暗中啟用。"));
            var selected = runtime.ReadSelection;
            var expected = runtime.Snapshot.Source ?? (req.ForceDispatch ? targetSource : null);
            if (expected is null)
                throw new ShopRequestFailure(ShopFailure.Global("SourceUnavailable", "目前 LC 來源採樣暫不可用；舊周遭名單保留。"));
            if (expected.SessionKey != req.ExpectedSourceSessionKey)
                throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "LC 來源已變更；舊周遭名單保留。"));
            // A complete nearby-player capture is independent of optional viewport geometry.
            var result = req.ForceDispatch
                ? source.ReadSlice(new ReadSelection(["selfId", "position", "nearbyPlayers", "playerShopStates"]), token)
                : await ShopRefreshReadRetry.RunAsync(t => source.ReadSlice(new ReadSelection(["selfId", "position", "nearbyPlayers", "playerShopStates"]), t), expected, () => runtime.Snapshot.Source, attempt => Audit("SHOP_REFRESH_READ_RETRY", "集合變動，重新完整唯讀採樣；不沿用舊清單", new { Attempt=attempt, ActionsExecuted=false }), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var currentSource = runtime.Snapshot.Source;
            if (!runtime.IsRunning || !ReferenceEquals(selected, runtime.ReadSelection))
                throw new ShopRequestFailure(ShopFailure.Global("ContextUnavailable", "手動刷新期間本機讀取器或選項改變；未替換既有名單。"));
            // These codes only permit a new read-only refresh after the caller
            // reacquires its full original source. No old list is reauthorized.
            ShopRefreshFailurePolicy.RequireReadSource(result, expected, currentSource, req.ForceDispatch);
            var context = ShopRuntimeContext.FromFreshResult(result, expected, selected, req.ExpectedSourceSessionKey, DateTimeOffset.UtcNow,
                runtime.Snapshot.Fields.GetValueOrDefault("mapName"), requireShopStates: true);
            var owners = context.Players
                .OrderBy(p => p.Position!.GridDistance(context.SelfPosition)).ThenBy(p => p.Id).ToArray();
            if (owners.Length > MaximumOwners) throw new ShopRequestFailure(ShopFailure.Global("TargetCollectionTooLarge", "完整玩家名單超過共同界線；不截斷交付。"));
            var shopCounts = ShopEligibilityPolicy.Count(owners);
            var snapshot = new ShopTargetSnapshot { SnapshotId = Guid.NewGuid().ToString("N"), SourceSessionKey = expected.SessionKey,
                RefreshedAt = DateTimeOffset.UtcNow, SelfId = context.SelfId, SelfPosition = context.SelfPosition,
                Owners = owners, ScreenCoverageVerified = false,
                CanDispatch = true, DispatchBlockReason = "",
                ShopActionObservedCount = shopCounts.ShopActionObservedCount,
                KnownOtherActionObservedCount = shopCounts.KnownOtherActionObservedCount,
                UnknownShopStateCount = shopCounts.UnknownShopStateCount,
                Detail = $"已保存 {owners.Length} 位周遭玩家：確認擺攤 {shopCounts.ShopActionObservedCount}、非擺攤動作 {shopCounts.KnownOtherActionObservedCount}、待確認 {shopCounts.UnknownShopStateCount}。買賣頁只查已確認擺攤玩家；非擺攤及狀態未知一律略過。每頁送出前重新核對店主目前在場及擺攤狀態；可見性與畫面完整覆蓋未確認。" };
            token.ThrowIfCancellationRequested();
            ShopTargetSnapshotBudget.RequireFits(expected, snapshot,
                State(req.RequestId, "TargetsRefreshed", snapshot.Detail, proposedTargetSnapshot: snapshot));
            string prepared = PrepareTargets(expected, snapshot);
            try
            {
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (!runtime.IsRunning || !ReferenceEquals(selected, runtime.ReadSelection)
                        || (req.ForceDispatch ? ShopSourceAvailability.Changed(runtime.Snapshot.Source, expected) : runtime.Snapshot.Source != expected))
                        throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "提交刷新結果前來源或選項已變更；舊名單仍僅供查看。"));
                    File.Move(prepared, Path.Combine(directory, "targets.json"), overwrite: true);
                    targetSource = expected; targetSnapshot = snapshot;
                }
            }
            finally { if (File.Exists(prepared)) File.Delete(prepared); }
            Audit("SHOP_TARGETS_REFRESHED", snapshot.Detail, new { Snapshot = snapshot, result.Reads, result.ReadBytes });
            return State(req.RequestId, "TargetsRefreshed", snapshot.Detail);
        }
        finally { nativeGate.Release(); }
    }

    private async Task<ShopCommandResponse> StartScanAsync(ShopCommandRequest req, CancellationToken token)
    {
        if (req.ForceDispatch) await AcquireForcePreparationGateAsync(token).ConfigureAwait(false);
        else if (!await nativeGate.WaitAsync(0, token).ConfigureAwait(false)) return State(req.RequestId, "Busy", "目前正在核對另一筆商店請求。");
        IDisposable? progressLease = null;
        try
        {
            var (targets, expected) = req.ForceDispatch ? RequireForcedTargets(req) : await RequireTargetsAsync(req, token).ConfigureAwait(false);
            EnsureFields(req.ForceDispatch);
            await RecoverNativePendingBeforePublishAsync(expected, token).ConfigureAwait(false);
            EnsureUnblocked(expected);
            var readiness = await ObserveNativeReadinessAsync(expected, token,
                waitForCompletion: req.WaitForNativeReadinessCompletion).ConfigureAwait(false);
            if (!readiness.Fresh(DateTimeOffset.UtcNow)) return NativeReadinessResponse(req, readiness, ownsPreparationGate: true);
            EnsureUnblocked(expected); EnsureNativeAvailable(expected);
            var owners = targets.Owners;
            var shopCounts = ShopEligibilityPolicy.Count(owners);
            lock (gate)
            {
                if (scan.Running || scanTask is { IsCompleted: false })
                    return State(req.RequestId, "Busy", "前一批商店搜尋或最後記錄仍在處理中。");
                try { progressLease = savedScans.AcquireRunLease(); }
                catch (IOException) { return State(req.RequestId, "Busy", "另一個程式視窗正在使用保存的搜尋計畫；未重複啟動。"); }
                savedScans.Reload();
                bool continuing = req.Command == ShopCommands.ContinueSavedScan;
                SavedScanPage[] pendingPages;
                if (continuing)
                {
                    // Validate the exact plan/source before any history skip event.
                    savedScans.Continue(req.ExpectedScanPlanId, expected, targets, allowEmpty: true, forceDispatch: req.ForceDispatch);
                    savedScans.SkipKnownHistory((ownerId, direction) => searchedOwners.ShouldSkip(ownerId, direction, req.ForceDispatch));
                    pendingPages = savedScans.Continue(req.ExpectedScanPlanId, expected, targets, allowEmpty: true, forceDispatch: req.ForceDispatch);
                }
                else
                {
                    savedScans.StartNew(expected, targets, req.ForceDispatch, State(req.RequestId, "ScanStarting", "準備保存完整搜尋計畫。"));
                    savedScans.SkipKnownHistory((ownerId, direction) => searchedOwners.ShouldSkip(ownerId, direction, req.ForceDispatch));
                    pendingPages = savedScans.PendingPages(targets, req.ForceDispatch);
                }
                int historySkipped = savedScans.View(expected, targets, false, false, req.ForceDispatch)!.HistorySkippedPages;
                scanStop?.Dispose(); scanStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var selected = owners.Where(owner => pendingPages.Any(page => page.OwnerId == owner.Id && page.OwnerIdentity == owner.LocalIdentity)).ToArray();
                scanSource = expected;
                scanForceDispatch = req.ForceDispatch;
                scanPause = selected.Length > 0 ? new ScanPauseCoordinator(gate, req.RequestId,
                    () => Audit("SHOP_SCAN_PAUSED", "已在頁面之間暫停，未開始下一頁。", new { JobId = req.RequestId })) : null;
                scan = new() { JobId = req.RequestId, SourceSessionKey = expected.SessionKey, TargetSnapshotId = targets.SnapshotId,
                    Running = selected.Length > 0, Total = pendingPages.Length + historySkipped, Completed = historySkipped,
                    SkippedHistoryPages = historySkipped, TotalShops = selected.Length,
                    ShopActionObservedCount = shopCounts.ShopActionObservedCount,
                    KnownOtherActionObservedCount = shopCounts.KnownOtherActionObservedCount,
                    UnknownShopStateCount = shopCounts.UnknownShopStateCount,
                    ScreenCoverageVerified = false, Detail = $"{(continuing ? "接續保存計畫" : "搜尋未查頁")}；使用保存名單 {targets.RefreshedAt:O}。本輪 {selected.Length} 位確認擺攤玩家、{pendingPages.Length} 個尚未送出頁面；持久紀錄略過 {historySkipped} 頁。"
                        + (req.ForceDispatch ? "只對已確認擺攤玩家直接下達商店指令；後續依固定名單開頁，不重新讀取店主狀態，回覆未確認也繼續後續符合資格頁。" : "清除擺攤搜尋紀錄前不重搜同 ID 的已查頁；後續使用本次固定擺攤名單。") };
                if (selected.Length > 0)
                {
                    var coordinator = scanPause!;
                    var scanToken = scanStop.Token;
                    var ownedLease = progressLease!;
                    scanTask = Task.Run(() => ScanAsync(req, expected, selected, pendingPages, coordinator, ownedLease, scanToken), CancellationToken.None);
                    progressLease = null;
                }
            }
            Audit("SHOP_SCAN_STARTED", scan.Detail, new { req.RequestId, Candidates = owners.Length, Eligible = scan.TotalShops, Counts = shopCounts, ScreenCoverageVerified = false });
            return State(req.RequestId, "ScanStarted", scan.Detail);
        }
        finally { progressLease?.Dispose(); nativeGate.Release(); }
    }

    private async Task ScanAsync(ShopCommandRequest original, SourceIdentity expectedSource, MarketOwner[] owners,
        SavedScanPage[] queuedPages, ScanPauseCoordinator coordinator, IDisposable progressLease, CancellationToken token)
    {
        IDisposable? backgroundPersonalShopDeferral = null;
        try
        {
            backgroundPersonalShopDeferral = runtime.DeferBackgroundPersonalShop();
            int ordinal = 0;
            double totalPageMs = 0, completedShopMs = 0;
            foreach (var owner in owners)
            {
            double ownerElapsedMs = 0;
            int ownerPages = 0;
            int ownerTotal = queuedPages.Count(page => page.OwnerId == owner.Id && page.OwnerIdentity == owner.LocalIdentity);
            bool ownerSkipped = false;
            foreach (string direction in new[] { ShopPageModes.ViewerBuy, ShopPageModes.ViewerSell })
            {
                var savedPage = new SavedScanPage(owner.Id, owner.LocalIdentity, direction);
                if (!queuedPages.Contains(savedPage)) continue;
                token.ThrowIfCancellationRequested();
                // This is the page's linearization point. Pause before this
                // gate prevents dispatch; pause afterwards waits for this page
                // to finish all cleanup, observation and durable bookkeeping.
                using var page = await coordinator.EnterPageAsync(original.RequestId, () => runtime.IsRunning, token).ConfigureAwait(false);
                var pageTimer = Stopwatch.StartNew();
                lock (gate) scan = scan with { CurrentOwnerId = owner.Id };
                string requestId = "scan-" + Hash(original.RequestId)[..32] + "-" + (++ordinal).ToString(CultureInfo.InvariantCulture);
                var req = new ShopCommandRequest { RequestId = requestId, Command = ShopCommands.OpenShop, ExpectedSourceSessionKey = expectedSource.SessionKey,
                    OwnerId = owner.Id, ExpectedOwnerIdentity = owner.LocalIdentity, ViewerDirection = direction, TargetSnapshotId = original.TargetSnapshotId,
                    ForceDispatch = original.ForceDispatch };
                // Persist each child too, so a crash cannot turn scan progress into replay permission.
                using var cycleTiming = new ShopScanPageCycleTiming(Audit, requestId, owner.Id, direction); cycleTiming.Enter();
                await PersistScanAttemptPhaseAsync(savedScans, savedPage, requestId, RequestPath(requestId),
                    new { Request = req, Signature = Hash(JsonSerializer.Serialize(req, HubProtocol.Json)), State = "ScanChild_NoAutomaticReplay", At = DateTimeOffset.UtcNow }).ConfigureAwait(false);
                ShopCommandResponse response;
                try { response = await OpenAsync(req, true, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { response = Rejected(requestId, ex); Audit("SHOP_SCAN_ITEM_REJECTED", ex.Message, new { owner.Id, direction, response.FailureCode }); }
                response = response with { RequestDurationMs = pageTimer.Elapsed.TotalMilliseconds };
                double elapsedMs = pageTimer.Elapsed.TotalMilliseconds;
                totalPageMs += elapsedMs;
                ownerElapsedMs += elapsedMs;
                ownerPages++;
                lock (gate) scan = ShopEmptyPagePolicy.CountPage(scan, response) with { LastPageDurationMs = elapsedMs,
                    AveragePageDurationMs = totalPageMs / (scan.Completed - scan.SkippedHistoryPages + 1),
                    Detail = $"{scan.Completed + 1}/{scan.Total} 頁：ID {owner.Id} {direction}；本頁 {elapsedMs:0} 毫秒；{response.Detail} 畫面覆蓋未驗證。" };
                // A null background sample proves no change. The next OpenAsync
                // must reacquire a confirmed source before it can dispatch.
                bool sameSource = !ShopSourceAvailability.Changed(runtime.Snapshot.Source, expectedSource);
                var disposition = ShopScanPolicy.Decide(response, sameSource, original.ForceDispatch);
                bool originalResponseSaved = false;
                if (disposition == ShopScanDisposition.StopBatch && sameSource && original.ForceDispatch && response.SessionBlocked)
                {
                    // Preserve the original terminal Result before appending a
                    // separate recovery event. Recovery never rewrites it.
                    await SaveScanPageResponseAsync(requestId, response).ConfigureAwait(false); originalResponseSaved = true;
                    if (await RecoverCurrentScanPageAsync(req, expectedSource, token).ConfigureAwait(false))
                    {
                        response = new ShopFailure("ShopResponseUnverified", "Target", "PostCodeRecovered", null,
                            "原指令已返回且清理完成；原頁商品回應仍未確認，原頁不重送。").Attach(response with
                            { NativeEntered = true, NativeReturned = true, SessionBlocked = false, ResponseVerified = false,
                              Detail = response.Detail + " 原指令已返回且清理完成；原頁商品回應仍未確認，原頁不重送。" });
                        disposition = ShopScanDisposition.Continue;
                        lock (gate) scan = scan with { Detail = response.Detail };
                    }
                }
                if (disposition == ShopScanDisposition.StopBatch)
                {
                    if (!sameSource && !response.SessionBlocked)
                        response = ShopFailure.Global("SourceMismatch", "LC 來源已改變；未送下一頁，舊周遭名單保留供查看。").Attach(response with { Detail = response.Detail + " LC 來源已改變。" });
                    StopScan(response);
                    if (!originalResponseSaved) await SaveScanPageResponseAsync(requestId, response).ConfigureAwait(false);
                    return;
                }
                if (disposition == ShopScanDisposition.SkipOwner)
                {
                    ownerSkipped = true;
                    lock (gate) scan = scan with { SkippedPages = scan.SkippedPages + (ownerTotal - ownerPages) };
                    Audit("SHOP_SCAN_OWNER_SKIPPED", response.Detail, new { owner.Id, RemainingPages = ownerTotal - ownerPages, response.FailureCode });
                    await SaveScanPageResponseAsync(requestId, response).ConfigureAwait(false);
                    break;
                }
                if (ownerPages == ownerTotal)
                {
                    completedShopMs += ownerElapsedMs;
                    lock (gate) scan = scan with { CompletedShops = scan.CompletedShops + 1,
                        AverageShopDurationMs = completedShopMs / (scan.CompletedShops + 1) };
                }
                if (!originalResponseSaved) await SaveScanPageResponseAsync(requestId, response).ConfigureAwait(false);
                // OpenAsync keeps the current page visible for its captured wait
                // after native settlement and before capture, while owning nativeGate.
            }
            if (ownerSkipped) continue;
            Audit("SHOP_SCAN_SHOP_TIMING", "已處理此店本輪待查頁；保存進度與核對成功數另計。", new { owner.Id,
                ShopDurationMs = ownerElapsedMs, PauseWaitExcluded = true, Scan = scan });
            token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { lock (gate) scan = scan with { StopReason = "Cancelled", Detail = "掃描已停止；已進行項目保留，未送出下一筆。畫面覆蓋未驗證。" }; }
        catch (Exception ex) { Audit("SHOP_SCAN_STOPPED_ERROR", ex.Message); StopScan(Rejected(original.RequestId, ex)); }
        finally
        {
            try
            {
                lock (gate)
                {
                    coordinator.Complete();
                    if (scan.JobId == original.RequestId)
                        scan = scan with { Running = false, PauseRequested = false, Paused = false, CurrentOwnerId = null };
                }
                try { Audit("SHOP_SCAN_FINISHED", "搜尋已結束；以下為最終頁數與停止原因。", new { Scan = CurrentScan() }); }
                finally { progressLease.Dispose(); }
            }
            finally { backgroundPersonalShopDeferral?.Dispose(); }
        }
    }

    private async Task SaveScanPageResponseAsync(string requestId, ShopCommandResponse response)
    {
        // Persist after counters and disposition. The failed last page must not
        // claim the previous page count or report a stopped batch as running.
        lock (gate) response = response with { Scan = CurrentScan() };
        await PersistScanResultPhaseAsync(savedScans, requestId, ResponsePath(requestId), response).ConfigureAwait(false);
    }

    private void StopScan(ShopCommandResponse response)
    {
        string reason = response.SessionBlocked ? "SessionBlocked" : response.FailureCode ?? "ScanFailure";
        lock (gate) scan = scan with { Running = false, PauseRequested = false, Paused = false, CurrentOwnerId = null,
            StopReason = reason, FailureCode = response.FailureCode ?? reason,
            FailureScope = response.FailureScope ?? "Session", FailurePhase = response.FailurePhase,
            FailureRva = response.FailureRva, FailureDetail = response.Detail,
            PreflightNotEnteredConfirmed = response.PreflightNotEnteredConfirmed,
            PreflightNoEntrySource = response.PreflightNoEntrySource,
            Detail = $"整批已停止（{reason}）；不再向後續店主重複送出。{response.Detail}" };
        Audit("SHOP_SCAN_BATCH_STOPPED", scan.Detail, new { Scan = scan });
    }

    private Task<ShopCommandResponse> OpenAsync(ShopCommandRequest req, bool fromScan, CancellationToken token) =>
        OpenForcedAsync(req with { ForceDispatch = true }, token);

    // Retained strict implementation for historical diagnostics; public OpenAsync uses the fixed-list pipeline above.
    private async Task<ShopCommandResponse> OpenLegacyStrictAsync(ShopCommandRequest req, bool fromScan, CancellationToken token)
    {
        if (req.ForceDispatch) return await OpenForcedAsync(req, token).ConfigureAwait(false);
        var pageTimer = Stopwatch.StartNew();
        Stopwatch? sourceTimer = null, nativeTimer = null, responseTimer = null;
        var pageDisplayTimer = new Stopwatch();
        bool pageDisplayCompleted = false, pageDisplayCancelled = false;
        SourceIdentity? ownedActiveSource = null;
        int responseReadAttempts = 0;
        await nativeGate.WaitAsync(token).ConfigureAwait(false);
        int requestedPageWaitMilliseconds = PageWaitMilliseconds;
        double queueWaitMs = pageTimer.Elapsed.TotalMilliseconds;
        try
        {
            lock (gate) if (scan.Running && !fromScan) return State(req.RequestId, "Busy", "正在掃描商店；請先停止掃描再開指定店主。");
            token.ThrowIfCancellationRequested(); EnsureFields();
            sourceTimer = Stopwatch.StartNew();
            var (targets, expectedSource) = await RequireTargetsAsync(req, token).ConfigureAwait(false);
            var owner = ShopTargetPolicy.Owner(targets, req.OwnerId!.Value, req.ExpectedOwnerIdentity);
            if (searchedOwners.ShouldSkip(owner.Id, req.ViewerDirection!))
                return new ShopFailure("AlreadySearched", "Target", "BeforePublish", null,
                    "此玩家 ID 的本頁已在本機搜尋紀錄中；清除紀錄前不再送出。")
                    .Attach(State(req.RequestId, "AlreadySearched", "此玩家 ID 的本頁已有搜尋／未明紀錄；未重送。"));
            if (!ShopEligibilityPolicy.CanScan(owner))
            {
                string code = ShopEligibilityPolicy.RejectionCode(owner);
                string skipDetail = ShopEligibilityPolicy.RejectionDetail(owner);
                Audit("SHOP_TARGET_STATE_SKIPPED", skipDetail, new { req.RequestId, targets.SnapshotId, owner.Id,
                    owner.LocalIdentity, owner.ActionType, owner.ShopState, owner.ShopStateSampledAt,
                    owner.ShopStateEvidence, owner.ShopStateDetail, FailureCode = code });
                return new ShopFailure(code, "Target", "BeforePublish", null, skipDetail).Attach(
                    State(req.RequestId, code, skipDetail) with { NativeEntered = false, NativeReturned = false });
            }
            var selected = runtime.ReadSelection;
            var requestedOwner = owner;
            EnsureUnblocked(expectedSource); EnsureNativeAvailable(expectedSource);
            ulong hwnd = ShopRuntimeContext.Window(expectedSource);
            // Take every prerequisite in one closed reader pass after preflight.
            // Do not carry a nearly expired background actor snapshot through a separate shop-root read.
            ShopPreparedDispatch prepared;
            try
            {
                prepared = ShopPreparedDispatch.Capture(() => source.ResolveShopDispatchSource(expectedSource, token, owner.Id), expectedSource,
                    selected, expectedSource.SessionKey, () => DateTimeOffset.UtcNow,
                    runtime.Snapshot.Fields.GetValueOrDefault("mapName"), selectedOwnerId: owner.Id);
            }
            catch (ShopDispatchCollectionChangedException transient)
            {
                // This catch surrounds only preparation, before journal intent or helper launch.
                token.ThrowIfCancellationRequested();
                EnsureFields(); EnsureUnblocked(expectedSource); RequireTargets(req);
                if (transient.Source != expectedSource || !runtime.IsRunning ||
                    !ReferenceEquals(selected, runtime.ReadSelection) || runtime.Snapshot.Source != expectedSource)
                    throw new ShopRequestFailure(ShopFailure.Global("SourceUnavailable", "重新採樣後來源或設定已改變；搜尋停止。"));
                Audit("SHOP_DISPATCH_COLLECTION_SKIPPED", transient.Message,
                    new { req.RequestId, owner.Id, req.ViewerDirection, NativeEntered = false });
                return new ShopFailure("ActorCollectionChanged", "Page", "BeforePublish", null, transient.Message)
                    .Attach(State(req.RequestId, "Rejected", transient.Message) with
                    { NativeEntered = false, NativeReturned = false, ResponseVerified = false });
            }
            ShopActionSourceBinding binding = prepared.Binding;
            var currentContext = prepared.Context;
            token.ThrowIfCancellationRequested();
            if (!runtime.IsRunning || !ReferenceEquals(selected, runtime.ReadSelection) || runtime.Snapshot.Source != expectedSource)
                throw new ShopRequestFailure(ShopFailure.Global("SourceUnavailable", "同輪採樣交付時來源或讀取設定已改變；本頁未送出。"));
            owner = ShopDispatchPolicy.RequireOwner(currentContext, targets, requestedOwner, DateTimeOffset.UtcNow);
            Audit("SHOP_DISPATCH_SAMPLE", "前置檢查後取得同輪必要欄位與商店基準頁；保留各欄原始採樣時間。",
                new { req.RequestId, Evidence = prepared.Evidence(DateTimeOffset.UtcNow) });
            PersonalShopListing baseline = binding.BaselineListing;
            if (binding.Source != expectedSource)
                throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "本次商店根來源與手動名單來源不符。"));
            if (binding.CurrentOwnerIdRaw != baseline.OwnerIdRaw || baseline.SourceSessionKey != binding.Source.SessionKey
                || baseline.SampledAt != binding.SampledAt)
                throw new ShopRequestFailure(ShopFailure.Global("ContextUnavailable", "本次商店根與基準頁面店主／採樣時間未閉合。"));
            var self = currentContext.SelfPosition;
            RequireTargets(req);
            if (!ShopRuntimeContext.Fresh(binding.SampledAt, DateTimeOffset.UtcNow))
                throw new ShopRequestFailure(ShopFailure.Global("ContextUnavailable", "原生請求前商店根綁定已過期；未送出。"));
            EnsureFields(); EnsureUnblocked(binding.Source);
            sourceTimer.Stop();
            EnsureNativeAvailable(binding.Source);
            token.ThrowIfCancellationRequested();
            string nativeLog = NewNativeLogPath();
            string pending = SessionPath(binding.Source);
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (!runtime.IsRunning || !ReferenceEquals(selected, runtime.ReadSelection) || runtime.Snapshot.Source != expectedSource)
                    throw new ShopRequestFailure(ShopFailure.Global("SourceUnavailable", "送出前來源或讀取選項無法確認；本頁未送出。"));
                RequireTargets(req);
                prepared.RequireFresh(DateTimeOffset.UtcNow);
                owner = ShopDispatchPolicy.RequireOwner(currentContext, targets, requestedOwner, DateTimeOffset.UtcNow);
                if (!ShopRuntimeContext.Fresh(binding.SampledAt, DateTimeOffset.UtcNow))
                    throw new ShopRequestFailure(ShopFailure.Global("ContextUnavailable", "送出前商店根綁定已過期；本頁未送出。"));
                // Recheck before taking ownership; an existing/foreign pending
                // must never be disguised as this operation's normal progress.
                EnsureUnblocked(binding.Source);
                ShopRuntimeContext.Need(activeNativeSource is null, "另一筆原生請求仍在處理中。");
                RequireNativeHelperAvailable();
                activeNativeSource = ownedActiveSource = binding.Source;
                searchedOwners.Begin(binding.Source, owner, req.ViewerDirection!, req.RequestId);
                // Monotonic per-request intent survives a crash, even after the
                // session pending file has later been resolved. Never dispatch
                // if progress persistence fails.
                savedScans.NativeIntent(req.RequestId);
                Durable(pending, new { State = "NativeLaunchIntent_NoAutomaticRetry", req.RequestId, Source = binding.Source,
                    Owner = owner, binding.CurrentOwnerIdRaw, TargetOwner = owner.Id, req.ViewerDirection, NativeLog = nativeLog, At = startedAt });
            }
            Audit("SHOP_TARGET_CURRENT_VERIFIED", "本頁店主仍在完整周遭集合且擺攤狀態新鮮；未宣稱遊戲已接受。",
                new { req.RequestId, targets.SnapshotId, SavedAt = targets.RefreshedAt, Owner = owner,
                    SelfPosition = self, Distance = owner.Position?.GridDistance(self), NativeEntered = false,
                    Evidence = prepared.Evidence(startedAt) });
            Audit("SHOP_NATIVE_INTENT", "持久化單次清單請求；不含買賣交易。", new { req.RequestId, owner.Id, req.ViewerDirection, BaselineAt = baseline.SampledAt, BaselineHash = PageHash(baseline) });
            ShopNativeOutcome native;
            nativeTimer = Stopwatch.StartNew();
            try { native = await InvokeNativeAsync(binding, hwnd, owner.Id, req.ViewerDirection!, nativeLog, req.RequestId).ConfigureAwait(false); }
            catch (Exception ex)
            {
                MarkBlocked(binding.Source, "啟動或等待原生 helper 失敗，是否進入不明：" + ex.Message);
                return NativeInvocationFailure(ex).Attach(State(req.RequestId, "SessionBlocked", "此 LC session 已封鎖後續請求；" + ex.Message) with { SessionBlocked = true });
            }
            finally { nativeTimer.Stop(); }
            var nativeCompletionClock = Stopwatch.StartNew();
            bool sharedBlocked = File.Exists(SharedPending(binding.Source));
            if (!native.SafeToSettle || sharedBlocked)
            {
                MarkBlocked(binding.Source, native.Detail);
                return (native.Failure ?? ShopFailure.Unknown(native.Detail)).Attach(State(req.RequestId, "SessionBlocked", native.Detail + "；未自動重送。") with
                    { NativeEntered = native.Entered, NativeReturned = native.Returned, SessionBlocked = true });
            }
            // Commit known native return/no-entry before resolving the session
            // journal. A crash after resolution must not restore this page as new.
            if (native.Returned || native.NoEntryConfirmed)
            {
                lock (gate)
                {
                    searchedOwners.Settle(req.RequestId, native.Returned);
                    if (ShopNativeOutcome.IsGuardRejected(native)) savedScans.NativeGuardRejected(req.RequestId, native.Detail);
                    else savedScans.NativeSettled(req.RequestId, native.Entered, native.Returned, native.Detail);
                }
            }
            // A safe helper cleanup alone does not settle its durable native intent.
            // Retain the exact binding for recovery when no positive entry outcome exists.
            if (!native.Returned && !native.NoEntryConfirmed)
            {
                MarkBlocked(binding.Source, "原生前置結果缺少未派送正證據；保留完整請求綁定供恢復。" + native.Detail);
                return (native.Failure ?? ShopFailure.Unknown(native.Detail)).Attach(
                    State(req.RequestId, "SessionBlocked", native.Detail + "；未結清意圖，保留原始 pending。")
                    with { NativeEntered = native.Entered, NativeReturned = native.Returned, SessionBlocked = true });
            }
            try { File.Move(pending, pending[..^".pending.json".Length] + ".resolved-" + Guid.NewGuid().ToString("N") + ".json"); }
            catch (Exception ex)
            {
                MarkBlocked(binding.Source, "清理記錄無法保存：" + ex.Message);
                return ShopFailure.Unknown(ex.Message).Attach(State(req.RequestId, "SessionBlocked", "原生結果已返回，但清理記錄未完成；後續請求停止。") with
                    { NativeEntered = native.Entered, NativeReturned = native.Returned, SessionBlocked = true });
            }
            if (!native.Returned) return (native.Failure ?? ShopFailure.Unknown(native.Detail)).Attach(
                State(req.RequestId, "NativeRejected", native.Detail) with { NativeEntered = native.Entered,
                    SessionBlocked = IsBlocked(binding.Source),
                    PreflightNotEnteredConfirmed = native.NoEntryConfirmed && native.SourceMatched
                        && native.Failure?.Code == "NativePreflightPageChanged",
                    PreflightNoEntrySource = native.NoEntryConfirmed && native.SourceMatched
                        && native.Failure?.Code == "NativePreflightPageChanged" ? binding.Source : null });

            // Native settlement/journal resolution happen before this cancellable
            // wait. Cancellation preserves Sent and cannot restore replay permission.
            pageDisplayTimer.Start();
            try { pageDisplayCompleted = await ShopPagePacing.WaitBeforeCaptureMillisecondsAsync(native, token, waitMilliseconds: requestedPageWaitMilliseconds, elapsedSinceNativeCompletion: nativeCompletionClock.Elapsed).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                pageDisplayCancelled = true;
                return ShopPagePacing.CancelledAfterSettlement(State(req.RequestId, "Cancelled", ""), native);
            }
            finally { pageDisplayTimer.Stop(); }
            PersonalShopListing? after = null; string readDetail = "";
            responseTimer = Stopwatch.StartNew();
            responseReadAttempts = 1;
            after = ShopForceDispatchPolicy.ObserveOnce(() => ReadListing(binding.Source, CancellationToken.None), out readDetail);
            responseTimer.Stop();
            if (after is null)
                return ShopDispatchPolicy.Unverified(State(req.RequestId, "Unverified", "原生清單請求已正常返回；尚未取得同來源的新採樣，不能宣稱收到清單。" + readDetail)
                    with { NativeEntered = true, NativeReturned = true });

            bool matched = ResponseMatched(baseline, after, owner.Id, req.ViewerDirection!);
            var emptyObservation = !matched && runtime.Snapshot.Source == binding.Source
                ? ShopEmptyPagePolicy.TryObserve(req, binding.Source, baseline, after, native, startedAt, DateTimeOffset.UtcNow) : null;
            if (matched) lock (gate) searchedOwners.Confirm(req.RequestId);
            bool rowsBound = after.OwnerIdRaw == owner.Id && after.PageMode == req.ViewerDirection && after.Complete;
            string detail = matched
                ? "同來源店主與獨立頁面方向均符合請求，且請求後的頁面／bank 指紋或已確認模式改變；已建立本次回應關聯。這是讀值關聯，未直接觀察網路 ACK 或證實整店覆蓋。"
                : "原生請求已返回，但缺少獨立頁面方向或新頁變化證據；此採樣可能為快取，未宣稱新回覆。已驗證的舊版本清單仍保留。";
            if (req.ViewerDirection == ShopPageModes.ViewerSell && after.PageEmpty)
                detail += " 目前 Sell UI 為空不代表整個店沒有收購。";
            if (after.PageMode != req.ViewerDirection) detail += " Reader 尚未獨立確認本頁方向；方向欄是本次請求方向。";
            if (!rowsBound) detail += $" 原始採樣店主 {after.OwnerIdRaw}／方向 {after.PageMode ?? "Unknown"}／完整 {after.Complete} 未符合本次店主與頁面綁定；商品列只保留在稽核記錄，不加入查詢表格。";
            if (!string.IsNullOrEmpty(readDetail)) detail += " " + readDetail;
            if (emptyObservation is not null) detail = ShopEmptyPageRules.Detail;
            var listing = new MarketListing { Revision = Guid.NewGuid().ToString("N"), SourceSessionKey = after.SourceSessionKey,
                RequestId = req.RequestId, Owner = owner, ViewerDirection = req.ViewerDirection!, CapturedAt = after.SampledAt,
                RequestedAt = startedAt, PageDurationMs = pageTimer.Elapsed.TotalMilliseconds,
                SourceReadDurationMs = sourceTimer.Elapsed.TotalMilliseconds, NativeDurationMs = nativeTimer.Elapsed.TotalMilliseconds,
                ResponseReadDurationMs = responseTimer.Elapsed.TotalMilliseconds, ResponseReadAttempts = responseReadAttempts,
                SelfPositionAtRequest = self, DistanceAtRequest = owner.Position?.GridDistance(self),
                State = matched ? "OwnerAndResponseMatched" : emptyObservation is not null ? ShopEmptyPageRules.State : "Unverified", Detail = detail, OwnerAndResponseMatched = matched,
                EmptyPageObserved = emptyObservation is not null, EmptyPageObservation = emptyObservation,
                PageEmpty = rowsBound && after.PageEmpty, Complete = matched && after.Complete, Rows = rowsBound ? after.Rows : [] };
            RememberListing(listing);
            Audit("SHOP_LISTING_OBSERVED", detail, new { Listing = listing, BeforeHash = PageHash(baseline), AfterHash = PageHash(after),
                RawOwnerId = after.OwnerIdRaw, RawPageMode = after.PageMode, RowsBound = rowsBound, RawObservation = after, NativeReturnedIsNotAck = true });
            var pageResponse = ShopResponseBudget.Fit(State(req.RequestId, listing.State, detail, requiredListing: listing) with { NativeEntered = true, NativeReturned = true, ResponseVerified = matched,
                EmptyPageObserved = emptyObservation is not null, EmptyPageObservation = emptyObservation }, requiredListing: listing);
            return matched || ShopEmptyPageRules.IsNeutralResponse(pageResponse) ? pageResponse : ShopDispatchPolicy.Unverified(pageResponse);
        }
        finally
        {
            lock (gate)
                if (ownedActiveSource is not null && activeNativeSource == ownedActiveSource) activeNativeSource = null;
            sourceTimer?.Stop(); nativeTimer?.Stop(); responseTimer?.Stop(); pageTimer.Stop();
            Audit("SHOP_PAGE_TIMING", "單頁實際耗時；只讀重採樣不會重送原生請求。", new { req.RequestId, req.OwnerId, req.ViewerDirection,
                PageDurationMs = pageTimer.Elapsed.TotalMilliseconds, QueueWaitDurationMs = queueWaitMs,
                SourceReadDurationMs = sourceTimer?.Elapsed.TotalMilliseconds, NativeDurationMs = nativeTimer?.Elapsed.TotalMilliseconds,
                ResponseReadDurationMs = responseTimer?.Elapsed.TotalMilliseconds, ResponseReadAttempts = responseReadAttempts,
                ResponseReadDeadlineMilliseconds, RequestedPageWaitSeconds = requestedPageWaitMilliseconds / 1000d,
                RequestedPageWaitMilliseconds = requestedPageWaitMilliseconds,
                PageDisplayWaitDurationMs = pageDisplayTimer.Elapsed.TotalMilliseconds,
                PageDisplayWaitCompleted = pageDisplayCompleted, PageDisplayWaitCancelled = pageDisplayCancelled });
            nativeGate.Release();
        }
    }

    private PersonalShopListing ReadListing(SourceIdentity expected, CancellationToken token) => ReadListing(expected, token, out _);

    private PersonalShopListing ReadListing(SourceIdentity expected, CancellationToken token, out ReadOperationMetrics? readMetrics)
    {
        readMetrics = null;
        EnsureFields();
        ReadResult result = source.ReadSlice(new ReadSelection(["personalShopListing"]), token);
        readMetrics = result.ReadMetrics;
        ShopRuntimeContext.Need(result.Source == expected, "商店採樣來源已改變或無法確認。");
        var field = result.Fields.GetValueOrDefault("personalShopListing");
        ShopRuntimeContext.Need(field is not null && field.Fresh(DateTimeOffset.UtcNow) && field.Value is not null,
            "商店頁面尚未取得新鮮閉合採樣：" + (field?.Detail ?? result.Detail));
        var listing = field!.Value!.Value.Deserialize<PersonalShopListing>(HubProtocol.Json) ?? throw new InvalidDataException("商店頁格式無效。");
        ShopRuntimeContext.Need(listing.SourceSessionKey == expected.SessionKey && listing.SampledAt == field.SampledAt
            && listing.OwnerIdRaw is not null, "商店值與來源／採樣時間不一致。");
        return listing;
    }

    private static bool ResponseMatched(PersonalShopListing before, PersonalShopListing after, ulong target, string direction) =>
        before.SourceSessionKey == after.SourceSessionKey && after.OwnerIdRaw == target
        && after.SampledAt > before.SampledAt && after.LayoutState == "Present" && after.Complete
        && after.PageMode == direction && after.PageModeState == "MatchedExclusiveBankAndRows"
        && (PageHash(before) != PageHash(after)
            || BankChanged(before, after)
            || before.PageModeState == "MatchedExclusiveBankAndRows" && before.PageMode is not null && before.PageMode != after.PageMode);

    private static bool BankChanged(PersonalShopListing before, PersonalShopListing after) =>
        !string.IsNullOrEmpty(before.BankFingerprint) && !string.IsNullOrEmpty(after.BankFingerprint) && before.BankFingerprint != after.BankFingerprint;

    private static string PageHash(PersonalShopListing page) => Hash(JsonSerializer.Serialize(new
    { page.LayoutState, page.ContainerRowCount, page.InspectedRows, page.ExcludedEmptyOrTemplateRows,
        Rows = page.Rows.Select(row => new { row.RowIndex, row.IncludedInListing, row.Name, row.PriceRaw, row.UnitRaw }) }, HubProtocol.Json));

    internal string NewNativeLogPath() => Path.Combine(nativeLogDirectory, Guid.NewGuid().ToString("N") + ".jsonl");

    private async Task<ShopNativeOutcome> InvokeNativeAsync(ShopActionSourceBinding binding, ulong hwnd, ulong owner,
        string direction, string logPath, string requestId, bool forceDispatch = false)
    {
        var client = ResidentNativeClient;
        var outcome = await nativeHelperFlight.RunResidentOperationAsync(
            () => client.ExecuteAsync(binding, hwnd, owner, direction, logPath, requestId, forceDispatch),
            result => result.SafeToSettle, client.WaitForActualWorkerExitAsync, TimeSpan.FromSeconds(30),
            onTimeout: client.RetireAfterHostTimeout).ConfigureAwait(false);
        Audit("SHOP_NATIVE_END", outcome.Detail, new { binding.Source.ProcessId, TargetOwner = owner,
            Outcome = outcome, NativeLog = logPath, EvidenceMode = "ResidentRequestEvidence18", ActualProcessExitClaimed = false });
        return outcome;
    }

    private void EnsureNativeAvailable(SourceIdentity expected)
    {
        RequireNativeHelperAvailable();
        var ready = ShopNativePackage.Check(expected, Path.GetDirectoryName(HelperPath)!);
        if (ready.Ready) return;
        Audit("SHOP_NATIVE_PACKAGE_REJECTED", ready.Detail, new { ready.Code, NativeEntered = false, NativeRequests = 0 });
        throw new ShopRequestFailure(ShopFailure.Global(ready.Code, ready.Detail));
    }

    private void EnsureFields(bool forceDispatch = false)
    {
        if (!runtime.ReadSelection.Enabled("playerShopStates"))
            throw new ShopRequestFailure(ShopFailure.Global("ShopStateReadingDisabled",
                "玩家擺攤狀態（playerShopStates）讀取未開啟；請重新啟動程式後手動刷新周遭名單。未送出商店請求。"));
        foreach (string key in new[] { "selfId", "position", "nearbyPlayers", "personalShopListing" })
            if (!runtime.ReadSelection.Enabled(key))
                throw new ShopRequestFailure(ShopFailure.Global("ContextUnavailable", $"{key} 欄位已停用；請自行啟用需要的讀取欄位後再操作。"));
    }

    private (ShopTargetSnapshot Targets, SourceIdentity Source) RequireTargets(ShopCommandRequest req, bool requireDispatch = true)
    {
        lock (gate)
        {
            if (!runtime.IsRunning)
                throw new ShopRequestFailure(ShopFailure.Global("ContextUnavailable", "本機讀取器尚未啟動；已保存周遭名單不會因此改寫。"));
            ShopTargetPolicy.Validate(targetSnapshot, targetSource, runtime.Snapshot.Source, req.ExpectedSourceSessionKey, req.TargetSnapshotId, requireDispatch);
            return (targetSnapshot!, targetSource!);
        }
    }

    private async Task<(ShopTargetSnapshot Targets, SourceIdentity Source)> RequireTargetsAsync(ShopCommandRequest req, CancellationToken token)
    {
        SourceIdentity expected;
        lock (gate)
        {
            // Validate request and retained snapshot first, independently of a
            // temporarily missing observation. This alone permits no operation.
            ShopTargetPolicy.Validate(targetSnapshot, targetSource, targetSource, req.ExpectedSourceSessionKey, req.TargetSnapshotId);
            expected = targetSource!;
        }
        var selected = runtime.ReadSelection;
        bool waiting = runtime.Snapshot.Source is null;
        if (waiting) Audit("SHOP_SOURCE_WAIT", "來源採樣暫缺，等候重新確認；尚未送出指令。", new { req.RequestId, MaximumWaitMs = ShopSourceAvailability.WaitMilliseconds });
        double waited = await ShopSourceAvailability.WaitAsync(
            () => new(runtime.IsRunning, ReferenceEquals(selected, runtime.ReadSelection), runtime.Snapshot.Source), expected, token).ConfigureAwait(false);
        if (waiting) Audit("SHOP_SOURCE_RECOVERED", "已取得相同 LC 來源的新採樣。", new { req.RequestId, WaitedMs = waited });
        return RequireTargets(req);
    }

    private ShopPoint ReadSelf(SourceIdentity expected, ulong expectedSelfId, CancellationToken token)
    {
        var selected = runtime.ReadSelection;
        var snapshot = runtime.Snapshot;
        if (ShopSourceAvailability.Changed(snapshot.Source, expected))
            throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "自身採樣的 LC 來源已變更。"));
        var now = DateTimeOffset.UtcNow;
        if (snapshot.Source is null || new[] { "selfId", "position" }.Any(key => !snapshot.Fields.TryGetValue(key, out var f) || !f.Fresh(now) || f.Value is null))
        {
            // A local self-only refresh never visits nearbyPlayers/actorViewport.
            var result = source.ReadSlice(new ReadSelection(["selfId", "position"]), token);
            if (ShopSourceAvailability.Changed(result.Source, expected))
                throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "自身即時採樣未能確認原 LC 來源。"));
            if (result.Source is null || result.NoSourceRead)
                throw new ShopRequestFailure(ShopFailure.Global("SourceUnavailable", "自身即時採樣暫無有效来源；尚未送出本頁，已讀清單保留。"));
            snapshot = new HubSnapshot { Source = result.Source, Fields = result.Fields };
        }
        token.ThrowIfCancellationRequested();
        if (ShopSourceAvailability.Failure(new(runtime.IsRunning, ReferenceEquals(selected, runtime.ReadSelection), runtime.Snapshot.Source), expected, false) is { } error)
            throw new ShopRequestFailure(error);
        now = DateTimeOffset.UtcNow;
        var own = ShopRuntimeContext.Field(snapshot, "selfId", now);
        if (own.ValueKind != JsonValueKind.Number || !own.TryGetUInt64(out ulong id) || id != expectedSelfId)
            throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "自身角色已不同於手動名單；請明確刷新後操作。"));
        return ShopRuntimeContext.Point(ShopRuntimeContext.Field(snapshot, "position", now))
            ?? throw new ShopRequestFailure(ShopFailure.Global("ContextUnavailable", "尚未取得有效自身座標。"));
    }

    private ShopCommandResponse Rejected(string requestId, Exception error)
    {
        var failure = error is ShopRequestFailure typed ? typed.Failure : ShopFailure.Global("ContextUnavailable", error.Message);
        return failure.Attach(State(requestId, failure.Scope == "Session" ? "SessionBlocked" : "Rejected", error.Message))
            with { SessionBlocked = failure.Scope == "Session" || IsBlocked(runtime.Snapshot.Source) };
    }

    private void SaveTargets(SourceIdentity identity, ShopTargetSnapshot snapshot)
    {
        string temp = PrepareTargets(identity, snapshot);
        try
        {
            File.Move(temp, Path.Combine(directory, "targets.json"), overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private string PrepareTargets(SourceIdentity identity, ShopTargetSnapshot snapshot)
    {
        ShopTargetPolicy.ValidateStored(snapshot, identity);
        ShopTargetSnapshotBudget.RequireSavedFits(identity, snapshot);
        string temp = Path.Combine(directory, ".targets-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { Durable(temp, new SavedTargets(identity, snapshot)); return temp; }
        catch { if (File.Exists(temp)) File.Delete(temp); throw; }
    }

    private void BlockTargetsForRefresh()
    {
        ShopTargetSnapshot? retained; SourceIdentity? retainedSource;
        lock (gate)
        {
            if (targetSnapshot is not null)
                targetSnapshot = targetSnapshot with { CanDispatch = false,
                    DispatchBlockReason = ShopTargetSnapshotBudget.RefreshBlockedReason };
            retained = targetSnapshot; retainedSource = targetSource;
        }
        // Revoke in memory first. Save failure still leaves this lifetime blocked;
        // every subsequent broker lifetime loads retained data without authorization.
        if (retained is not null && retainedSource is not null) SaveTargets(retainedSource, retained);
        Audit("SHOP_TARGET_DISPATCH_REVOKED", "刷新開始；已撤銷舊名單的發送資格，保留原始名單與時間。",
            new { retained?.SnapshotId, retained?.RefreshedAt, CanDispatch = false });
    }

    private void LoadTargets()
    {
        string path = Path.Combine(directory, "targets.json");
        if (!File.Exists(path)) return;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > 1024 * 1024) throw new InvalidDataException("保存名單大小超出界線。");
            var saved = JsonSerializer.Deserialize<SavedTargets>(input, HubProtocol.Json) ?? throw new InvalidDataException("保存名單為空。");
            ShopTargetPolicy.ValidateStored(saved.Snapshot, saved.Source);
            targetSource = saved.Source;
            targetSnapshot = saved.Snapshot with { CanDispatch = false,
                DispatchBlockReason = ShopTargetSnapshotBudget.ReloadBlockedReason };
            Audit("SHOP_TARGETS_LOADED", "已載入原手動名單及原時間；本次查詢程式必須明確刷新後才可發送。", new { saved.Snapshot.SnapshotId, saved.Snapshot.RefreshedAt, CanDispatch = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Audit("SHOP_TARGETS_LOAD_REJECTED", ex.Message); }
    }
    private void EnsureUnblocked(SourceIdentity identity) => ShopNativePendingPolicy.RequireReady(
        ObserveNativePending(identity), HasUnsettledLocalNativeIntent(identity));
    private bool IsBlocked(SourceIdentity? identity)
    {
        if (identity is null) return false;
        return ObserveNativePending(identity).State != ShopNativePendingState.None || HasUnsettledLocalNativeIntent(identity);
    }
    internal static bool ReportedSessionBlocked(SourceIdentity? identity, SourceIdentity? active, bool explicitlyBlocked,
        bool pending, bool sharedPending) => identity is not null
        && (explicitlyBlocked || (pending || sharedPending) && identity != active);
    private void MarkBlocked(SourceIdentity identity, string reason)
    { lock (gate) blockedSessions.Add(SessionStem(identity)); Audit("SHOP_SESSION_BLOCKED", reason, new { identity.ProcessId, identity.CreationFileTime }); }

    private void RememberListing(MarketListing listing)
    {
        lock (gate)
        {
            listings.Add(listing);
            while (listings.Count > MaximumListings)
            {
                // Preserve the existing bounded history policy. The current
                // request receipt carries its own immutable listing separately.
                int remove = listings.FindIndex(value => !value.OwnerAndResponseMatched);
                listings.RemoveAt(remove < 0 ? 0 : remove);
            }
        }
    }

    private ShopCommandResponse State(string requestId, string state, string detail, bool includeListings = true, ShopTargetSnapshot? proposedTargetSnapshot = null, MarketListing? requiredListing = null)
    {
        ShopCommandResponse captured;
        MarketListing[] retainedListings;
        lock (gate)
        {
            var current = runtime.Snapshot.Source;
            var responseTargets = proposedTargetSnapshot ?? targetSnapshot;
            retainedListings = listings.ToArray();
            bool exposeScanFailure = state is "State" or "Stopping";
            if (state == "State" && scan.StopReason is not null) detail = scan.Detail;
            captured = new() { RequestId = requestId, State = state, Detail = detail, CurrentSourceSessionKey = current?.SessionKey,
                WorkSettled = NativeWorkSettled(!scan.Running && scanTask is not { IsCompleted: false }
                    && nativeGate.CurrentCount == 1 && activeNativeSource is null, nativeHelperFlight),
                SessionBlocked = current is not null && ReportedSessionBlocked(current, activeNativeSource,
                    blockedSessions.Contains(SessionStem(current)) || current != activeNativeSource && (savedScans.BlocksSource(current) || searchedOwners.BlocksSource(current)),
                    File.Exists(SessionPath(current)), ObserveNativePending(current).State != ShopNativePendingState.None),
                PreflightNotEnteredConfirmed = exposeScanFailure && scan.PreflightNotEnteredConfirmed,
                PreflightNoEntrySource = exposeScanFailure ? scan.PreflightNoEntrySource : null,
                FailureCode = exposeScanFailure ? scan.FailureCode : null, FailureScope = exposeScanFailure ? scan.FailureScope : null,
                FailurePhase = exposeScanFailure ? scan.FailurePhase : null, FailureRva = exposeScanFailure ? scan.FailureRva : null, TargetSnapshot = responseTargets,
                Scan = CurrentScan(), SavedScan = savedScans.View(current, responseTargets, scan.Running,
                    savedScans.Source is { } savedSource && IsBlocked(savedSource), scanForceDispatch),
                SearchHistory = searchedOwners.Summary(HistoryCanClear(), "商店工作或目前 LC 的未明請求尚未安全結束；不能清除。"),
                Listings = [], ListingsTruncated = false };
        }
            MarketListing[] recent = retainedListings.Where(_ => includeListings).GroupBy(p => (p.SourceSessionKey, p.Owner.Id, p.Owner.LocalIdentity, p.ViewerDirection))
                .SelectMany(group =>
                {
                    var latest = group.Last(); var verified = group.LastOrDefault(p => p.OwnerAndResponseMatched);
                    return verified is not null && verified.Revision != latest.Revision ? new[] { latest, verified } : new[] { latest };
                }).OrderByDescending(p => p.CapturedAt).Take(32).ToArray();
            bool truncated = retainedListings.Length > recent.Length;
            if (requiredListing is not null)
            {
                if (!includeListings || requiredListing.RequestId != requestId)
                    throw new InvalidDataException("PAGE_RECEIPT_LISTING_IDENTITY_MISMATCH");
                var optional = recent.Where(value => value.RequestId != requestId).ToArray();
                truncated |= optional.Length >= 32;
                recent = new[] { requiredListing }.Concat(optional).Take(32).ToArray();
            }
            int listingBudget = Math.Max(100_000, 800_000 - JsonSerializer.SerializeToUtf8Bytes(captured.TargetSnapshot, HubProtocol.Json).Length);
            while (recent.Length > (requiredListing is null ? 0 : 1) && JsonSerializer.SerializeToUtf8Bytes(recent, HubProtocol.Json).Length > listingBudget)
            { recent = recent[..^1]; truncated = true; }
        return ShopResponseBudget.Fit(captured with { Listings = recent, ListingsTruncated = truncated }, requiredListing: requiredListing);
    }
    private ShopCommandResponse ReadPrior(ShopCommandRequest req, string signature, string path)
    {
        try
        {
            using var stored = JsonDocument.Parse(File.ReadAllText(path));
            if (stored.RootElement.GetProperty("signature").GetString() != signature) return State(req.RequestId, "Rejected", "RequestId 已用於不同內容。");
            string responsePath = ResponsePath(req.RequestId);
            if (File.Exists(responsePath))
            {
                var response = JsonSerializer.Deserialize<ShopCommandResponse>(File.ReadAllText(responsePath), HubProtocol.Json);
                if (response is not null && response.RequestId == req.RequestId) return response with { Detail = "重複 RequestId：回傳既有結果，未重送。" + response.Detail };
            }
        }
        catch (Exception ex) { Audit("SHOP_DEDUP_READ_ERROR", ex.Message, new { req.RequestId }); }
        return State(req.RequestId, "DuplicateUnresolved", "此 RequestId 已被持久化；未取得完整舊回覆，禁止重送。請查詢狀態與 LOG。");
    }
    private string RequestPath(string id) => Path.Combine(directory, "requests", Hash(id) + ".request.json");
    private string ResponsePath(string id) => Path.Combine(directory, "requests", Hash(id) + ".response.json");
    private static string SessionStem(SourceIdentity s) => s.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + s.CreationFileTime.ToString(CultureInfo.InvariantCulture);
    private string SessionPath(SourceIdentity s) => Path.Combine(directory, "sessions", SessionStem(s) + ".pending.json");
    private static string SharedPending(SourceIdentity s) => Path.Combine(SharedJournal, SessionStem(s) + ".pending.json");
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "ShopControl", ShopNativePackage.HelperFile);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void Durable(string path, object value)
    { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); file.Write(JsonSerializer.SerializeToUtf8Bytes(value, HubProtocol.Json)); file.Flush(true); }
    private void Audit(string kind, string detail, object? data = null) => runtime.RecordLog(new(DateTimeOffset.UtcNow, kind, detail, data));
    private static void Validate(ShopCommandRequest req)
    {
        ShopRuntimeContext.Need(req.Schema == ShopCommands.Schema && !string.IsNullOrWhiteSpace(req.RequestId) && req.RequestId.Length <= 128
            && !req.RequestId.Any(char.IsControl), "商店請求版本／識別無效。");
        ShopRuntimeContext.Need(req.Command is ShopCommands.GetState or ShopCommands.GetSearchedOwners or ShopCommands.ClearSearchedOwners or ShopCommands.ResetSearchProgress or ShopCommands.OpenShop or ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan or ShopCommands.CancelScan or ShopCommands.RefreshTargets or ShopCommands.ReadNavigation or ShopCommands.CheckNativeReadiness or ShopCommands.PrepareNativeReadiness or ShopCommands.PauseScan or ShopCommands.ResumeScan, "未知商店命令。");
        ShopRuntimeContext.Need(!req.WaitForNativeReadinessCompletion
            || req.Command is ShopCommands.CheckNativeReadiness or ShopCommands.PrepareNativeReadiness
                or ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan,
            "等待原檢查完成僅適用入口檢查或搜尋批次的唯讀準備，不授予原生命令送出資格。");
        ShopRuntimeContext.Need(req.ExpectedSourceSessionKey is not null && req.ExpectedSourceSessionKey.Length <= 2048
            && req.ExpectedOwnerIdentity?.Length is not > 256 && req.ListingRevision?.Length is not > 1024 && req.ViewerDirection?.Length is not > 64 && req.TargetSnapshotId?.Length is not > 128
            && req.ExpectedScanJobId?.Length is not > 128 && req.ExpectedScanJobId?.Any(char.IsControl) != true
            && req.ExpectedScanPlanId?.Length is not > 128 && req.ExpectedScanPlanId?.Any(char.IsControl) != true
            && req.ExpectedHistoryRevision?.Length is not > 128 && req.ExpectedHistoryRevision?.Any(char.IsControl) != true
            && req.HistoryLimit is >= 1 and <= 200 && req.HistoryAfterOwnerId is not > int.MaxValue,
            "商店請求欄位超出界線。");
        if (req.Command == ShopCommands.ContinueSavedScan)
            ShopRuntimeContext.Need(Guid.TryParseExact(req.ExpectedScanPlanId, "N", out _), "接續搜尋需要有效的保存計畫識別。");
        if (req.Command == ShopCommands.ClearSearchedOwners)
            ShopRuntimeContext.Need(!string.IsNullOrWhiteSpace(req.ExpectedHistoryRevision), "清除搜尋紀錄需要目前紀錄版本。");
        if (req.Command == ShopCommands.OpenShop)
            ShopRuntimeContext.Need(req.OwnerId is > 0 and <= int.MaxValue && req.ViewerDirection is ShopPageModes.ViewerBuy or ShopPageModes.ViewerSell, "開店需有效店主 ID 與 ViewerBuy／ViewerSell。");
        if (req.Command == ShopCommands.ReadNavigation)
            ShopRuntimeContext.Need(req.OwnerId is > 0 and <= int.MaxValue && !string.IsNullOrWhiteSpace(req.TargetSnapshotId)
                && !string.IsNullOrWhiteSpace(req.ExpectedSourceSessionKey) && !string.IsNullOrWhiteSpace(req.ExpectedOwnerIdentity),
                "導航需指定同來源的保存名單版本、店主 ID 與本機識別。");
        if (req.Command is ShopCommands.PauseScan or ShopCommands.ResumeScan)
            ShopRuntimeContext.Need(!string.IsNullOrWhiteSpace(req.ExpectedSourceSessionKey)
                && (req.ForceDispatch || !string.IsNullOrWhiteSpace(req.ExpectedScanJobId) && !string.IsNullOrWhiteSpace(req.TargetSnapshotId)),
                "暫停／繼續需要相同 LC 來源；一般模式另需原搜尋工作與保存名單版本。");
    }
    public ValueTask DisposeAsync()
    {
        lock (gate) { disposed = true; disposal ??= Task.Run(DisposeCoreAsync); return new(disposal); }
    }
    private async Task DisposeCoreAsync()
    {
        var errors = new List<Exception>();
        using var disposalBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task? worker = null;
        void CompleteCleanup(Action action)
        {
            try { action(); }
            catch (Exception error) { errors.Add(error); }
        }
        CompleteCleanup(stop.Cancel);
        Task[] active;
        lock (gate)
        {
            CompleteCleanup(() => scanPause?.Cancel());
            CompleteCleanup(() => scanStop?.Cancel());
            active = requests.Values.Select(p => (Task)p.Task).Concat(nativeReadinessTasks).Concat(navigationTask is null ? [] : new Task[] { navigationTask }).ToArray();
        }
        try
        {
            var accepted = Task.WhenAll(active);
            try { await accepted.WaitAsync(disposalBudget.Token).ConfigureAwait(false); }
            catch (Exception error) { errors.Add(accepted.Exception ?? error); }
            // A previously accepted ScanVisible may have created its worker while
            // shutdown was awaiting that request. Even a failed accepted task
            // cannot skip joining that worker before the reader is released.
            // worker is retained across the bounded observation and quarantine decision.
            lock (gate)
            {
                CompleteCleanup(() => scanPause?.Cancel());
                CompleteCleanup(() => scanStop?.Cancel());
                worker = scanTask;
            }
            if (worker is not null)
            {
                try { await worker.WaitAsync(disposalBudget.Token).ConfigureAwait(false); }
                catch (Exception error) { errors.Add(worker.Exception ?? error); }
            }
            // A caller timeout did not stop the owned helper. Join its actual
            // exit before releasing broker resources or confirming shutdown.
            if (residentNativeClient is not null)
            {
                try { await ShopResidentShutdownObservation.StopAndObserveAsync(
                    residentNativeClient.StopAndWaitForActualExitAsync, residentNativeClient.WaitForActualWorkerExitAsync,
                    disposalBudget.Token).ConfigureAwait(false); }
                catch (Exception error) { errors.Add(error); }
            }
            try { await nativeHelperFlight.WaitUntilExitAsync().WaitAsync(disposalBudget.Token).ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        }
        finally
        {
            bool ownershipUnconfirmed = nativeHelperFlight.IsActive || active.Any(t => !t.IsCompleted)
                || worker is { IsCompleted: false } || residentNativeClient is { ActualWorkerExitConfirmed: false };
            if (ownershipUnconfirmed)
            {
                // Keep the broker and its maintenance lease/native gate/package
                // pins strongly alive. A bounded observation is never retirement.
                PreserveUnconfirmedResidentOwnership();
                errors.Add(new IOException("RESIDENT_SHUTDOWN_OWNERSHIP_UNCONFIRMED_QUARANTINED"));
                CompleteCleanup(() => Audit("SHOP_BROKER_QUARANTINED", "關閉觀察已達界線；保留未知原生工作、套件pin與維修鎖，未宣稱已釋放。",
                    new { FailureCount = errors.Count, OwnershipRetained = true, ProcessKilled = false }));
            }
            else
            {
            CompleteCleanup(() => scanStop?.Dispose());
            CompleteCleanup(searchGenerationStop.Dispose);
            foreach (var retired in retiredSearchGenerations) CompleteCleanup(retired.Dispose);
            CompleteCleanup(stop.Dispose);
            CompleteCleanup(nativeGate.Dispose);
            CompleteCleanup(maintenanceLease.Dispose);
            CompleteCleanup(() => Audit("SHOP_BROKER_STOPPED",
                errors.Count == 0 ? "已完成現有請求清理；不再送出後續清單請求。" : "現有請求與掃描工作已結束；清理發生錯誤，保留例外供檢查。",
                new { FailureCount = errors.Count }));
            }
        }
        if (errors.Count != 0) throw new AggregateException("商店協調器已等待現有工作結束，但關閉期間發生錯誤。", errors);
    }
    private sealed record RequestEntry(string Signature, Task<ShopCommandResponse> Task);
    private sealed record SavedTargets(SourceIdentity Source, ShopTargetSnapshot Snapshot);
}


