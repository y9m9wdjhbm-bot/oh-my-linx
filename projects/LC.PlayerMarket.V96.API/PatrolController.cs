using System.Globalization;
using MemoryHub;

namespace LcPlayerMarket;

internal sealed record PatrolObservation(SourceIdentity Source, ulong SelfId, int MapId, int X, int Y,
    DateTimeOffset SampledAt, bool Standing);

internal sealed record PatrolNavigationRequest(string JobId, PatrolObservation Expected,
    PatrolWaypoint Waypoint, int ArrivalRadius = PatrolRouteRules.DefaultArrivalRadius);

internal sealed record PatrolNavigationState
{
    public string JobId { get; init; } = "";
    public string WaypointId { get; init; } = "";
    public int TargetX { get; init; }
    public int TargetY { get; init; }
    public int ArrivalRadius { get; init; }
    public SourceIdentity? Source { get; init; }
    public ulong SelfId { get; init; }
    public int MapId { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public DateTimeOffset SampledAt { get; init; }
    public string State { get; init; } = "Unknown";
    public bool Terminal { get; init; }
    public bool Completed { get; init; }
    public bool CleanupConfirmed { get; init; }
    public bool WorkSettled { get; init; }
    public bool Standing { get; init; }
    public int StandingSamples { get; init; }
    public bool PositionVerified { get; init; } = true;
    public bool UserStop { get; init; }
    public bool StopRequested { get; init; }
    public string? Error { get; init; }
}

internal interface IPatrolNavigation
{
    Task<PatrolObservation> CaptureAsync(CancellationToken token, PatrolObservation? expected = null);
    Task<PatrolNavigationState> StartAsync(PatrolNavigationRequest request, CancellationToken token);
    Task<PatrolNavigationState> GetStateAsync(string jobId, CancellationToken token);
    Task<PatrolNavigationState> StopAsync(string expectedJobId, CancellationToken token);
}

internal interface IPatrolMarket
{
    Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token);
}

internal sealed record PatrolProgress(string RunId, string State, int PointIndex, int TotalPoints,
    string? PointId, string? PointName, int RetryAttempt, string Detail)
{
    public int CurrentRound { get; init; } = 1;
    public int TotalRounds { get; init; } = 1;
    public int CompletedRounds { get; init; }
}
internal sealed record PatrolRunResult(string RunId, string State, int CompletedPoints, int TotalPoints,
    int RetriesUsed, string Detail, bool CleanupConfirmed)
{
    public string? FailureCode { get; init; }
    // CompletedPoints/TotalPoints are local to CurrentRound, not cumulative across rounds.
    public int CurrentRound { get; init; } = 1;
    public int TotalRounds { get; init; } = 1;
    public int CompletedRounds { get; init; }
}

/// <summary>A finite number of complete route rounds. Ports own all process and game interaction; this class only coordinates receipts.</summary>
internal sealed partial class PatrolController
{
    private const int MaximumPolls = 9000;
    private const int MaximumSourcePolls = 40;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SourcePollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SourceWaitTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CaptureRecoveryTimeout = TimeSpan.FromSeconds(12);
    private const int MaximumCaptureRetries = 2;
    private readonly IPatrolNavigation navigation;
    private readonly IPatrolMarket market;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TimeSpan cleanupTimeout;
    private readonly bool freezeShoppingTargets;
    private int running;
    private bool blocked;
    private string? blockedReason;
    private readonly PatrolControllerStrict? strictController;
    public event Action<PatrolProgress>? Progress;

    internal PatrolController(IPatrolNavigation navigation, IPatrolMarket market,
        Func<DateTimeOffset>? utcNow = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? cleanupTimeout = null, bool forceDispatch = false,
        PatrolCheckpointStore? checkpointStore = null, bool autoResume = false, bool continuousRecovery = false,
        bool freezeShoppingTargets = false)
    {
        this.navigation = navigation;
        this.market = market;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.delay = delay ?? Task.Delay;
        this.cleanupTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(15);
        this.checkpointStore = checkpointStore;
        this.autoResume = autoResume && forceDispatch;
        this.continuousRecovery = continuousRecovery && this.autoResume;
        this.freezeShoppingTargets = freezeShoppingTargets && forceDispatch;
        if (this.cleanupTimeout <= TimeSpan.Zero || this.cleanupTimeout > TimeSpan.FromSeconds(15))
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
        if (!forceDispatch)
        {
            strictController = new(navigation, market, this.utcNow, this.delay, this.cleanupTimeout);
            strictController.Progress += progress => Progress?.Invoke(progress);
        }
    }

    internal async Task<PatrolRunResult> RunAsync(PatrolRouteSnapshot route, CancellationToken token = default)
    {
        if (strictController is not null) return await strictController.RunAsync(route, token).ConfigureAwait(false);
        if (autoResume) return await RunResumableAsync(route, token).ConfigureAwait(false);
        string runId = Guid.NewGuid().ToString("N");
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return new(runId, "Blocked", 0, route.Points?.Length ?? 0, 0, "原巡迴工作仍在執行或清理，未啟動第二個工作。", false) { TotalRounds = route.RoundCount };
        var run = new RunState(runId);
        try
        {
            if (blocked) throw Block(blockedReason ?? "前次巡迴仍有未確認的來源、工作識別或清理結果；本協調器禁止再啟動。");
            PatrolRouteRules.Validate(route);
            run.Route = PatrolRouteRules.Copy(route);
            if (run.Route.Points.Length == 0) throw Fail("請先保存至少一個定位點。");
            int map = Map(run.Route.Points[0]);
            if (run.Route.Points.Any(point => Map(point) != map)) throw Fail("巡迴只接受同一張地圖的定位點。");
            token.ThrowIfCancellationRequested();
            run.Origin = await navigation.CaptureAsync(token).ConfigureAwait(false);
            ValidateObservation(run.Origin, run.Origin, map);
            for (int round = 1; round <= run.Route.RoundCount; round++)
            {
                // Keep this run's immutable route/source fences and owned pending work across rounds.
                token.ThrowIfCancellationRequested();
                run.CurrentRound = round; run.Completed = 0; run.Point = null; run.Retry = 0;
                Publish(run, "Preparing", $"準備第 {round}/{run.Route.RoundCount} 輪；已查頁面仍依既有紀錄跳過。");
                foreach (PatrolWaypoint point in run.Route.Points)
                {
                    token.ThrowIfCancellationRequested();
                    run.Point = point;
                    run.Retry = 0;
                    var before = await CaptureAsync(run, token).ConfigureAwait(false);
                    await NavigateAsync(run, before, token).ConfigureAwait(false);
                    await CaptureAtPointAsync(run, token).ConfigureAwait(false);
                    await SearchPointAsync(run, token).ConfigureAwait(false);
                    await CaptureAtPointAsync(run, token).ConfigureAwait(false);
                    await SettleNavigationAsync(run, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    run.Completed++;
                }
                token.ThrowIfCancellationRequested();
                run.CompletedRounds++;
                Publish(run, "RoundCompleted", $"已完整完成 {run.CompletedRounds}/{run.Route.RoundCount} 輪。");
            }
            token.ThrowIfCancellationRequested();
            Publish(run, "Completed", $"已完整完成設定的 {run.CompletedRounds}/{run.Route.RoundCount} 輪，巡迴結束。");
            return Result(run, "Completed", $"已完整完成設定的 {run.CompletedRounds}/{run.Route.RoundCount} 輪，巡迴結束。", true);
        }
        catch (Exception failure)
        {
            Publish(run, "Stopping", "正在核對本巡迴已啟動工作的停止與清理。");
            bool clean = await CleanupAsync(run).ConfigureAwait(false);
            bool uncertain = !clean || failure is PatrolFailure { Uncertain: true };
            if (!clean)
            {
                blocked = true;
                blockedReason ??= !clean ? "前次工作未取得完整清理證據；本協調器禁止再啟動。"
                    : "前次巡迴因來源或工作識別無法確認而封鎖；清理已確認，但本協調器禁止再啟動。";
            }
            string state = uncertain ? "Blocked" : failure is OperationCanceledException || run.UserStopped ? "Stopped" : "Failed";
            string detail = failure is OperationCanceledException ? "巡迴已取消，未啟動下一個定位點。" : failure.Message;
            if (!clean) detail += " 停止或清理仍未確認；未宣稱已安全停止。";
            Publish(run, state, detail);
            return Result(run, state, detail, clean);
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private async Task NavigateAsync(RunState run, PatrolObservation before, CancellationToken token)
    {
        bool trackingExisting = autoResume && run.NavigationRequest is { } active && !run.NavigationSettled
            && active.Waypoint.PointId == run.Point!.PointId;
        var request = trackingExisting ? run.NavigationRequest!
            : new PatrolNavigationRequest(Guid.NewGuid().ToString("N"), before, run.Point!, run.Route!.ArrivalRadius);
        run.NavigationRequest = request;
        run.NavigationSettled = false;
        SaveCheckpoint(run, "Running", trackingExisting ? "繼續追蹤原導航工作。" : "保存本點導航工作識別，直接前往定位點。");
        Publish(run, "Moving", "正在前往定位點。");
        PatrolNavigationState state;
        try { state = trackingExisting ? await ReadNavigationStateAsync(run, request, token).ConfigureAwait(false)
            : await navigation.StartAsync(request, token).ConfigureAwait(false); }
        catch (Exception error) when (TransportFailure(error) && !token.IsCancellationRequested)
        {
            // A lost acceptance reply is never permission to repeat Start.
            state = await ReadNavigationStateAsync(run, request, token).ConfigureAwait(false);
        }
        for (int poll = 0; poll < MaximumPolls; poll++)
        {
            token.ThrowIfCancellationRequested();
            ValidateNavigation(state, request);
            run.NavigationOwned = true;
            run.NavigationSettled = state.WorkSettled && state.CleanupConfirmed;
            if (state.UserStop || state.StopRequested)
            {
                run.UserStopped = true;
                throw new OperationCanceledException("導航已被手動停止。", token);
            }
            if (state.Terminal && (!state.Completed || state.State != "Completed"))
            {
                if (continuousRecovery && RecoverableNavigationCode(state) is { } code)
                    throw new PatrolFailure("導航暫時未完成：" + code, false) { Code = "RetrySettledNavigation" };
                throw Fail(state.Error ?? "導航未完成，沒有查詢商店。");
            }
            bool freshPosition = state.PositionVerified && state.X is >= 0 and <= 65535 && state.Y is >= 0 and <= 65535
                && state.SampledAt != default && state.SampledAt <= utcNow()
                && utcNow() - state.SampledAt <= TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds);
            if (autoResume && state.Terminal && state.Completed && !freshPosition)
            {
                // A completed receipt can be historical. Arrival requires a new
                // same-source read; it never comes from its old numerical position.
                var current = await CaptureAsync(run, token).ConfigureAwait(false);
                if (Distance(current.X, current.Y, request.Waypoint) > request.ArrivalRadius)
                    throw new PatrolFailure("導航已結束，但目前位置仍在指定範圍外。", false) { Code = "PositionOutsideRange" };
                state = state with { X = current.X, Y = current.Y, SampledAt = current.SampledAt, PositionVerified = true };
                freshPosition = true;
            }
            if (freshPosition && Distance(state.X, state.Y, request.Waypoint) <= request.ArrivalRadius)
            {
                if (!run.NavigationSettled)
                {
                    // Stop is sent immediately on the actual range crossing. Its worker
                    // cleanup continues independently while this point's shop is searched.
                    var stopping = await navigation.StopAsync(request.JobId, token).ConfigureAwait(false);
                    ValidateNavigation(stopping, request);
                    run.NavigationSettled = stopping.WorkSettled && stopping.CleanupConfirmed;
                }
                Publish(run, "Arriving", "已確認同來源、同地圖的實際位置進入指定範圍，直接進行此點商店搜尋。");
                return;
            }
            if (state.Terminal) throw Fail("導航回覆尚無新鮮且位於定位點範圍內的實際座標，未查詢商店。");
            bool actualProgress = state.State == "Moving" && !state.Standing
                && state.SampledAt > before.SampledAt && (state.X != before.X || state.Y != before.Y)
                && utcNow() - state.SampledAt <= TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds) && state.SampledAt <= utcNow();
            Publish(run, actualProgress ? "MovingConfirmed" : "Navigating", actualProgress
                ? $"已觀察同來源座標進展 {state.X},{state.Y}；取樣 {state.SampledAt.ToLocalTime():HH:mm:ss}。"
                : "導航命令中；等待同來源的實際行走進展。");
            await delay(PollInterval, token).ConfigureAwait(false);
            state = await ReadNavigationStateAsync(run, request, token).ConfigureAwait(false);
        }
        throw Block("導航工作未在有界等候內結束；未重送導航。");
    }

    private async Task SettleNavigationAsync(RunState run, CancellationToken token)
    {
        if (run.NavigationRequest is not { } request || run.NavigationSettled) return;
        for (int poll = 0; poll < MaximumPolls; poll++)
        {
            token.ThrowIfCancellationRequested();
            var state = await ReadNavigationStateAsync(run, request, token).ConfigureAwait(false);
            ValidateNavigation(state, request);
            if (state.WorkSettled && state.CleanupConfirmed) { run.NavigationSettled = true; return; }
            await delay(PollInterval, token).ConfigureAwait(false);
        }
        throw Block("本點商店已搜尋，但導航原生工作仍未釋放；沒有覆蓋尚在執行的工作。");
    }

    private async Task SearchPointAsync(RunState run, CancellationToken token)
    {
        if (autoResume && run.ScanRequest is { } registered && (!run.ScanSettled || run.PlanId is null))
        {
            // A request recorded before a lost acceptance reply must first be
            // identified. Refresh/new ScanVisible cannot replace that unknown job.
            for (int poll = 0; ; poll++)
            {
                var original = await StateAsync(run, token).ConfigureAwait(false);
                ValidateScan(original, run, registered, run.PlanId);
                ValidateMarketSource(original, run.Origin!, includeFailure: true);
                ValidateSaved(original.SavedScan!, run.Origin!);
                bool planChanged = run.PlanId != original.SavedScan!.PlanId;
                bool nowSettled = original.WorkSettled && !original.Scan.Running;
                bool settlementChanged = run.ScanSettled != nowSettled;
                run.PlanId = original.SavedScan!.PlanId; run.ScanOwned = true;
                run.ScanSettled = nowSettled;
                if (planChanged || settlementChanged) SaveCheckpoint(run, "Running", "追蹤原搜尋工作及計畫，沒有重送或取代原請求。");
                if (original.Scan.StopReason is "Cancelled" or "UserStop")
                { run.UserStopped = true; throw new OperationCanceledException("原商店搜尋已被手動停止。", token); }
                if (run.ScanSettled) StopForSettledNativeCodeFailure(original);
                await CaptureAtPointAsync(run, token).ConfigureAwait(false);
                if (run.ScanSettled)
                {
                    run.LastScanFailureCode = original.Scan.FailureCode; run.LastScanFailurePhase = original.Scan.FailurePhase; run.LastScanFailureScope = original.Scan.FailureScope; run.PageRangeFailureEligible = IsSettledPageRangeFailure(original, run.Origin!.Source);
                    if (original.SavedScan.RemainingPages == 0) return;
                    break;
                }
                if (poll >= MaximumPolls) throw Block("原搜尋工作尚未完成，保存原請求及計畫，沒有開始另一個搜尋。");
                await delay(PollInterval, token).ConfigureAwait(false);
            }
        }
        string? plan = autoResume ? run.PlanId : null;
        bool classificationOnly = false;
        string stoppedScanFailure = "";
        bool recoveringRefresh = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await CaptureAtPointAsync(run, token).ConfigureAwait(false);
            Publish(run, run.Retry == 0 ? "Refreshing" : "Retrying", freezeShoppingTargets && run.FrozenTargets is not null
                ? "使用本定位點固定名單直接接續未送出頁；不重抓名單或重驗擺攤狀態。" : classificationOnly
                ? "本點接續額度已用完；只刷新確認剩餘頁是否都已離場或收攤，不再送出搜尋。" : run.Retry == 0
                ? "確認位置進入範圍後，直接刷新此點周遭商店。" : plan is null
                    ? $"本點已使用 {run.Retry} 次重試額度；重新確認此點商店。"
                    : $"第 {run.Retry} 次接續：重新確認此點商店，只查尚未送出頁。");
            // The point's first successful refresh is its fixed shopping list.
            // Subsequent automatic recovery only observes the same broker/plan;
            // it never refreshes nearby owners or creates a synthetic refresh receipt.
            bool reuseFrozenTargets = freezeShoppingTargets && run.FrozenTargets is not null;
            var refreshRequest = Command(run, ShopCommands.RefreshTargets);
            var refresh = reuseFrozenTargets
                ? await ReadFrozenPointStateAsync(run, token).ConfigureAwait(false)
                : await market.SendAsync(refreshRequest, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!reuseFrozenTargets && refresh.RequestId != refreshRequest.RequestId)
                throw Block("刷新回覆識別不符，沒有重送或開始搜尋。");
            if (refresh.FailureCode is "SourceUnavailable" or "SourceMismatch")
                throw Block("刷新期間來源無法確認或已改變，沒有重送或開始搜尋。");
            bool readOnlyGap = refresh.FailureCode is "RefreshReadTimeout" or "RefreshSourceSampleGap";
            var refreshReceipt = refresh;
            if (!readOnlyGap || refresh.CurrentSourceSessionKey is not null)
                ValidateMarketSource(refresh, run.Origin!);
            // Status re-reads cannot create a successful refresh receipt or replace its snapshot.
            if (!reuseFrozenTargets && refreshReceipt.State != "TargetsRefreshed")
            {
                bool retryable = refresh.State == "Rejected" && (refresh.FailureCode == "ContextUnavailable" || readOnlyGap)
                    && refresh.FailureScope == "Global" && refresh.FailurePhase == "BeforePublish" && !refresh.NativeEntered && !refresh.NativeReturned
                    && !refresh.ResponseVerified && (!readOnlyGap || refresh.TargetSnapshot?.CanDispatch != true);
                if (!retryable || classificationOnly && (!autoResume || !CanRecoverScan(run)))
                    throw Fail("刷新未成功且沒有完整可重試證據或剩餘額度，沒有重送或開始搜尋。");
                if (continuousRecovery)
                {
                    recoveringRefresh |= readOnlyGap;
                    await RecoveryCooldownAsync(run, "唯讀刷新暫時失敗；保留同點與計畫，持續重試。", token).ConfigureAwait(false);
                    continue;
                }
                if (classificationOnly)
                {
                    recoveringRefresh |= readOnlyGap;
                    await RecoveryCooldownAsync(run, "分類刷新暫時失敗；保留原定位點與計畫，冷卻後重新取得分類回覆。", token).ConfigureAwait(false);
                    continue;
                }
                if (run.Retry >= run.Route!.RetryLimit)
                {
                    if (!autoResume) throw Fail("刷新失敗，重試次數已達設定上限；巡迴停止。");
                    await RecoveryCooldownAsync(run, "唯讀刷新暫時失敗，短次數額度已用完；冷卻後接續同一定位點。", token).ConfigureAwait(false);
                }
                run.Retry++; run.RetriesUsed++;
                recoveringRefresh |= readOnlyGap;
                // Reserve from the shared budget before a capture which may itself
                // consume recovery attempts. A null response source is not proof:
                // this read must match every field of this run's original identity.
                await CaptureAtPointAsync(run, token).ConfigureAwait(false);
                Publish(run, "Retrying", $"刷新失敗，第 {run.Retry} 次重試；尚未開始新的商店搜尋。");
                continue;
            }
            var targets = reuseFrozenTargets ? run.FrozenTargets : refreshReceipt.TargetSnapshot;
            if (targets is null || string.IsNullOrWhiteSpace(targets.SnapshotId)
                || targets.SourceSessionKey != run.Origin!.Source.SessionKey || targets.SelfId != run.Origin.SelfId)
                throw Fail("刷新名單的來源、角色或送出資格不完整。");
            if (!reuseFrozenTargets) Fresh(targets.RefreshedAt, "商店名單刷新回覆");
            if (freezeShoppingTargets && run.FrozenTargets is null)
            {
                run.FrozenTargets = targets with { Owners = targets.Owners.Select(owner => owner with { }).ToArray() };
                targets = run.FrozenTargets;
                Publish(run, "TargetsFrozen", $"本定位點名單已固定，共 {targets.Owners.Length} 個保存玩家；後續買賣頁與自動接續不再刷新或重驗擺攤狀態。");
            }
            await CaptureAtPointAsync(run, token).ConfigureAwait(false);
            if (freezeShoppingTargets && plan is null && run.PlanId is null && run.ScanRequest is null
                && run.PendingScanRequest is null && !targets.Owners.Any(ShopEligibilityPolicy.CanScan))
            {
                // An empty initial list is a successful point observation, not
                // a native-readiness failure. Never abandon or cancel old work.
                if (!refreshReceipt.WorkSettled || refreshReceipt.Scan.Running || refreshReceipt.SessionBlocked)
                    throw Block("本定位點沒有已確認擺攤玩家，但原商店工作尚未釋放；沒有送出新指令或取消未知工作。");
                Publish(run, "EmptyPoint", "本定位點沒有已確認擺攤玩家；未送出商店或原生入口指令，接續位置及清理確認後前往下一點。");
                return;
            }
            if (recoveringRefresh)
            {
                Publish(run, "Recovered", "已重新取得同來源範圍內的位置及新商店名單，直接接續本點未送出頁。");
                recoveringRefresh = false;
            }
            if (plan is not null)
            {
                if (refresh.SavedScan?.PlanId != plan) throw Block("保存搜尋計畫已被替換，沒有接續其他計畫。");
                ValidateSaved(refresh.SavedScan, run.Origin!);
                if (autoResume && refresh.SavedScan.RemainingPages == 0)
                {
                    run.ScanSettled = refresh.WorkSettled && !refresh.Scan.Running;
                    SaveCheckpoint(run, "Running", "本點保存計畫已無未送出頁，保留已送紀錄並接續位置確認。");
                    return;
                }
                if (refresh.SavedScan.RemainingPages > 0 && refresh.SavedScan.AvailablePages == 0)
                {
                    if (CanFinishDeferredPoint(refresh, run.Origin!))
                    {
                        // The receipt was fresh when accepted above. Position capture
                        // may take longer than its TTL; this decision retains deferred
                        // pages and is followed by position and worker settlement checks.
                        Publish(run, "Searching", $"本點可搜尋頁已處理；{refresh.SavedScan.DeferredPages} 個目前不在可搜尋名單的未送出頁保留，繼續下一定位點。");
                        return;
                    }
                    throw Fail(stoppedScanFailure + "刷新後仍無法接續尚未送出頁：" + refresh.SavedScan.ContinueBlockReason);
                }
                if (classificationOnly)
                {
                    if (!autoResume || !CanRecoverScan(run))
                        throw Fail(stoppedScanFailure + $"尚有 {refresh.SavedScan.RemainingPages} 個未送出頁；接續次數已達上限，巡迴停止。");
                    await RecoveryCooldownAsync(run, stoppedScanFailure + "接續同點保存計畫的未送出頁。", token).ConfigureAwait(false);
                    classificationOnly = false;
                    // Cooldown invalidates the earlier source/position/target observations.
                    // Keep the same plan, but reacquire them before issuing ContinueSavedScan.
                    continue;
                }
            }
            string? previousPlan = refresh.SavedScan?.PlanId;
            var scanRequest = Command(run, plan is null ? ShopCommands.ScanVisible : ShopCommands.ContinueSavedScan) with
                { TargetSnapshotId = targets.SnapshotId, ExpectedScanPlanId = plan,
                  WaitForNativeReadinessCompletion = continuousRecovery };
            run.PendingScanRequest = scanRequest;
            SaveCheckpoint(run, "Running", "保存本點搜尋請求；已送頁不重送。");
            Publish(run, "Searching", plan is null ? "正在搜尋此定位點的商店。" : "正在接續尚未送出頁面。");
            ShopCommandResponse response;
            try
            {
                response = await market.SendAsync(scanRequest, token).ConfigureAwait(false);
                if (response.RequestId != scanRequest.RequestId) throw Block("搜尋接受回覆識別不符，未採用其他請求結果。");
            }
            catch (Exception error) when (TransportFailure(error) && !token.IsCancellationRequested)
            {
                // Status must identify the original accepted scan. Never issue another ScanVisible on timeout.
                response = await StateAsync(run, token).ConfigureAwait(false);
            }
            AdoptScanAcceptance(run, scanRequest, response, plan, previousPlan, refreshReceipt);
            for (int poll = 0; ; poll++)
            {
                token.ThrowIfCancellationRequested();
                ValidateScan(response, run, scanRequest, plan);
                string actualPlan = response.SavedScan!.PlanId;
                if (plan is null && previousPlan == actualPlan) throw Block("新搜尋沒有取得新的計畫識別，沒有採用舊工作結果。");
                bool planChanged = run.PlanId != actualPlan;
                plan = run.PlanId = actualPlan;
                run.ScanOwned = true;
                var savedProgress = response.SavedScan!; // ValidateScan above also guards every replacement response.
                bool workerSettled = response.WorkSettled && !response.Scan.Running;
                bool settlementChanged = run.ScanSettled != workerSettled;
                run.ScanSettled = workerSettled;
                ValidateMarketSource(response, run.Origin!, includeFailure: true);
                ValidateSaved(savedProgress, run.Origin!, requireSettled: workerSettled);
                if (planChanged || settlementChanged) SaveCheckpoint(run, "Running", "保存原搜尋計畫與本點進度。");
                if (response.Scan.StopReason is "Cancelled" or "UserStop")
                {
                    run.UserStopped = true;
                    throw new OperationCanceledException("商店搜尋已被手動停止。", token);
                }
                if (run.ScanSettled) StopForSettledNativeCodeFailure(response);
                await CaptureAtPointAsync(run, token).ConfigureAwait(false);
                if (run.ScanSettled)
                {
                    // Retain only this run's validated, settled scan evidence across the classification refresh.
                    // This is presentation only: it changes no stop, retry, pending or native-entry rule.
                    stoppedScanFailure = DescribeStoppedScan(response.Scan);
                    run.LastScanFailureCode = response.Scan.FailureCode;
                    run.LastScanFailurePhase = response.Scan.FailurePhase;
                    run.LastScanFailureScope = response.Scan.FailureScope;
                    run.PageRangeFailureEligible = IsSettledPageRangeFailure(response, run.Origin!.Source);
                    break;
                }
                if (poll >= MaximumPolls) throw Block("商店工作未在有界等候內完成，沒有開始下一點。");
                await delay(PollInterval, token).ConfigureAwait(false);
                response = await StateAsync(run, token).ConfigureAwait(false);
            }
            // Sent without verified rows remains historical Sent. It is never retried,
            // but confirmed native settlement permits the remaining distinct pages.
            if (response.SavedScan!.RemainingPages == 0) return;
            if (continuousRecovery)
            {
                if (!CanRecoverScan(run)) throw Fail(stoppedScanFailure + "此結果不屬於可恢復錯誤，保留原工作並停止。");
                await RecoveryCooldownAsync(run, stoppedScanFailure + "保留同點未送出頁，持續接續；不重送已送頁。", token).ConfigureAwait(false);
                classificationOnly = false;
                continue;
            }
            if (run.Retry >= run.Route!.RetryLimit)
                classificationOnly = true;
            else
            {
                run.Retry++;
                run.RetriesUsed++;
            }
        }
    }

    private async Task<ShopCommandResponse> ReadFrozenPointStateAsync(RunState run, CancellationToken token)
    {
        var state = await StateAsync(run, token).ConfigureAwait(false);
        ValidateMarketSource(state, run.Origin!, includeFailure: true);
        var frozen = run.FrozenTargets ?? throw Block("本定位點尚未保存固定名單。");
        if (state.State != "State" || !state.WorkSettled || state.Scan.Running || state.SessionBlocked
            || state.CurrentSourceSessionKey != run.Origin!.Source.SessionKey
            || state.TargetSnapshot is not { } current || current.SnapshotId != frozen.SnapshotId
            || current.SourceSessionKey != frozen.SourceSessionKey || current.SelfId != frozen.SelfId)
            throw Block("固定定位點名單的原來源、工作清理或名單識別已改變；沒有重抓名單或接管其他工作。");
        return state;
    }

    private static string DescribeStoppedScan(MarketScanState scan)
    {
        if (string.IsNullOrWhiteSpace(scan.FailureCode)) return "";
        static string OneLine(string? text, int maximum)
        {
            string value = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= maximum ? value : value[..maximum] + "…";
        }
        string code = OneLine(scan.FailureCode, 80);
        string context = string.Join("／", new[] { scan.FailureScope, scan.FailurePhase, scan.FailureRva }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => OneLine(value, 80)));
        string detail = OneLine(scan.FailureDetail, 240);
        return "搜尋停止原因：" + code + (context.Length == 0 ? "" : "（" + context + "）")
            + (detail.Length == 0 ? "" : "：" + detail) + "；";
    }

    private async Task<PatrolObservation> CaptureAsync(RunState run, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        window.CancelAfter(CaptureRecoveryTimeout);
        var deadline = utcNow() + CaptureRecoveryTimeout;
        int retries = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                window.Token.ThrowIfCancellationRequested();
                if (utcNow() >= deadline) throw CaptureTimeout();
                try
                {
                    // Only the read-only capture is repeated. The run's complete origin
                    // stays fixed; Start, ScanVisible and Continue are never repeated here.
                    var observed = await navigation.CaptureAsync(window.Token, run.Origin!)
                        .WaitAsync(window.Token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    window.Token.ThrowIfCancellationRequested();
                    if (utcNow() >= deadline) throw CaptureTimeout();
                    ValidateObservation(observed, run.Origin!, run.Origin!.MapId);
                    return observed;
                }
                catch (PatrolSourceReadException error) when (error.Code == "SourceUnavailableTimeout")
                {
                    token.ThrowIfCancellationRequested();
                    if (window.IsCancellationRequested || utcNow() >= deadline || retries >= MaximumCaptureRetries
                        || !autoResume && run.Retry >= run.Route!.RetryLimit) throw;
                    retries++;
                    if (!autoResume) run.Retry++;
                    else run.CaptureRecoveryAttempts++;
                    run.RetriesUsed++;
                    Publish(run, "WaitingSource", $"本點第 {(autoResume ? run.CaptureRecoveryAttempts : run.Retry)} 次定位重讀：短暫讀值空窗後只重新取得同來源的新座標，不重送行走或商店搜尋。");
                    await delay(SourcePollInterval, window.Token).WaitAsync(window.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && window.IsCancellationRequested)
        { throw CaptureTimeout(); }
    }

    private static PatrolSourceReadException CaptureTimeout() => new("SourceUnavailableTimeout",
        "本次定位點讀取與恢復已達十二秒上限，沒有採用舊座標或重送動作。");

    private async Task CaptureAtPointAsync(RunState run, CancellationToken token)
    {
        var observed = await CaptureAsync(run, token).ConfigureAwait(false);
        if (Distance(observed.X, observed.Y, run.Point!) > run.Route!.ArrivalRadius)
            throw new PatrolFailure("角色實際位置已離開本次定位點指定範圍，巡迴停止。", false) { Code = "PositionOutsideRange" };
    }

    private void ValidateObservation(PatrolObservation observed, PatrolObservation expected, int map)
    {
        if (observed.Source is null || observed.Source != expected.Source || observed.SelfId == 0
            || observed.SelfId != expected.SelfId || observed.MapId != map)
            throw Block("LC 完整來源、角色或地圖已改變，禁止接續與前往下一點。");
        if (observed.Source.ProcessId <= 0 || observed.Source.CreationFileTime <= 0
            || string.IsNullOrWhiteSpace(observed.Source.SessionKey) || observed.X is < 0 or > 65535 || observed.Y is < 0 or > 65535)
            throw Block("即時角色來源或座標不完整。");
        Fresh(observed.SampledAt);
    }

    private static void ValidateNavigation(PatrolNavigationState state, PatrolNavigationRequest request)
    {
        if (state.JobId != request.JobId || state.Source != request.Expected.Source || state.SelfId != request.Expected.SelfId
            || state.MapId != request.Expected.MapId || state.WaypointId != request.Waypoint.PointId
            || state.TargetX != request.Waypoint.X || state.TargetY != request.Waypoint.Y || state.ArrivalRadius != request.ArrivalRadius)
            throw Block("導航回覆不屬於本工作、來源或定位點；未接管其他導航。");
    }

    private static void ValidateMarketSource(ShopCommandResponse response, PatrolObservation expected, bool includeFailure = false)
    {
        if (response.CurrentSourceSessionKey is not null && response.CurrentSourceSessionKey != expected.Source.SessionKey
            || includeFailure && (response.FailureCode is "SourceMismatch" or "SourceUnavailable" || response.Scan.FailureCode is "SourceMismatch" or "SourceUnavailable"))
            throw Block("商店來源已改變或無法確認，禁止接續與前往下一點。");
    }

    private static void ValidateSaved(ShopSavedScanProgress saved, PatrolObservation expected, bool requireSettled = true)
    {
        if (string.IsNullOrWhiteSpace(saved.PlanId) || saved.SourceSessionKey != expected.Source.SessionKey
            || saved.UncertainPages < 0 || saved.TotalPages < 0 || saved.RemainingPages < 0
            || !SavedCountsValid(saved))
            throw Block("保存搜尋進度含未定頁或來源／計數矛盾，沒有重送或前往下一點。");
    }

    private static bool SavedCountsValid(ShopSavedScanProgress saved) =>
        saved.SearchedPages >= 0 && saved.ConfirmedPages >= 0 && saved.ConfirmedPages <= saved.SearchedPages
        && saved.HistorySkippedPages >= 0 && saved.AvailablePages >= 0 && saved.DeferredPages >= 0
        && (long)saved.AvailablePages + saved.DeferredPages == saved.RemainingPages
        && (long)saved.SearchedPages + saved.RemainingPages + saved.UncertainPages + saved.HistorySkippedPages == saved.TotalPages
        && (saved.ContinueBlockCode != "NoAvailableOwners" || !saved.CanContinue && saved.RemainingPages > 0
            && saved.AvailablePages == 0 && saved.DeferredPages == saved.RemainingPages);

    private static bool CanFinishDeferredPoint(ShopCommandResponse response, PatrolObservation expected) =>
        (response.CurrentSourceSessionKey is null || response.CurrentSourceSessionKey == expected.Source.SessionKey)
        && response.TargetSnapshot is { } targets
        && targets.SourceSessionKey == expected.Source.SessionKey && targets.SelfId == expected.SelfId
        && response.SavedScan is { ContinueBlockCode: "NoAvailableOwners",
            AvailablePages: 0, RemainingPages: > 0 } saved
        && saved.SourceSessionKey == expected.Source.SessionKey && saved.DeferredPages == saved.RemainingPages;

    private static void ValidateScan(ShopCommandResponse response, RunState run, ShopCommandRequest request, string? expectedPlan)
    {
        if (response.Scan.JobId != request.RequestId || response.Scan.SourceSessionKey != run.Origin!.Source.SessionKey
            || response.Scan.TargetSnapshotId != request.TargetSnapshotId || response.SavedScan is null
            || expectedPlan is not null && response.SavedScan.PlanId != expectedPlan)
            throw Block("搜尋回覆不屬於本工作、計畫或刷新名單；未接續其他工作。");
    }

    private async Task<ShopCommandResponse> StateAsync(RunState run, CancellationToken token)
    {
        var request = Command(run, ShopCommands.GetState);
        var result = await market.SendAsync(request, token).ConfigureAwait(false);
        if (result.RequestId != request.RequestId) throw Block("商店狀態回覆識別不符。");
        return result;
    }

    private async Task<bool> CleanupAsync(RunState run)
    {
        using var cleanup = new CancellationTokenSource(cleanupTimeout);
        var token = cleanup.Token;
        bool navigationClean = run.NavigationRequest is null || run.NavigationSettled;
        bool marketClean = run.PendingScanRequest is null && (run.ScanRequest is null || run.ScanSettled);
        string? cleanupPlan = run.PendingScanRequest is { } pendingScan ? pendingScan.ExpectedScanPlanId : run.PlanId;
        Task<PatrolNavigationState>? navigationStop = null;
        Task<ShopCommandResponse>? marketStop = null;
        // Dispatch both stops before waiting for either worker's release receipt.
        if (!navigationClean)
        {
            try { navigationStop = navigation.StopAsync(run.NavigationRequest!.JobId, token); }
            catch { navigationClean = false; }
        }
        if (!marketClean)
        {
            var request = run.PendingScanRequest ?? run.ScanRequest!;
            var cancel = Command(run, ShopCommands.CancelScan) with
            {
                ExpectedScanJobId = request.RequestId, TargetSnapshotId = request.TargetSnapshotId,
                ExpectedScanPlanId = cleanupPlan
            };
            try { marketStop = market.SendAsync(cancel, token); }
            catch { marketClean = false; }
        }
        // Ports are bounded by the cleanup token even if a transport ignores cancellation.
        try
        {
            if (!navigationClean)
            {
                var request = run.NavigationRequest!;
                // Stop is idempotent and fenced by this registered job ID. Its historical
                // cleanup receipt does not require the freshness used for arrival handoff.
                var state = await (navigationStop ?? throw new IOException("導航停止指令未受理。")).WaitAsync(token).ConfigureAwait(false);
                ValidateNavigation(state, request);
                run.NavigationOwned = true;
                while (!state.WorkSettled || !state.CleanupConfirmed)
                {
                    await delay(PollInterval, token).WaitAsync(token).ConfigureAwait(false);
                    state = await navigation.GetStateAsync(request.JobId, token).WaitAsync(token).ConfigureAwait(false);
                    ValidateNavigation(state, request);
                }
                navigationClean = run.NavigationSettled = true;
            }
        }
        catch { navigationClean = false; }
        try
        {
            if (!marketClean)
            {
                var request = run.PendingScanRequest ?? run.ScanRequest!;
                var state = await (marketStop ?? throw new IOException("商店停止指令未受理。")).WaitAsync(token).ConfigureAwait(false);
                ValidateScan(state, run, request, cleanupPlan);
                run.ScanRequest = request; run.PendingScanRequest = null;
                run.ScanOwned = true;
                run.PlanId = state.SavedScan!.PlanId;
                while (!state.WorkSettled || state.Scan.Running)
                {
                    await delay(PollInterval, token).WaitAsync(token).ConfigureAwait(false);
                    state = await StateAsync(run, token).WaitAsync(token).ConfigureAwait(false);
                    ValidateScan(state, run, request, run.PlanId);
                }
                marketClean = run.ScanSettled = true;
            }
        }
        catch { marketClean = false; }
        return navigationClean && marketClean;
    }

    private void Fresh(DateTimeOffset at, string context = "角色觀測")
    {
        var now = utcNow();
        if (at == default || at > now || now - at > TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds))
            throw Fail($"{context}已過期或時間不符，未沿用舊值。取樣={at:O}；核對={now:O}；間隔={(now - at).TotalMilliseconds:F0} 毫秒。");
    }
    private static int Map(PatrolWaypoint point) => int.TryParse(point.MapId, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id >= 0
        ? id : throw Fail("定位點地圖 ID 必須是非負整數。");
    private static long Distance(int x, int y, PatrolWaypoint point) => Math.Max(Math.Abs((long)x - point.X), Math.Abs((long)y - point.Y));
    private static bool TransportFailure(Exception error) =>
        error is TimeoutException or IOException or System.Net.Http.HttpRequestException or OperationCanceledException;
    private static ShopCommandRequest Command(RunState run, string command) => new()
        { Command = command, RequestId = "patrol-" + Guid.NewGuid().ToString("N"), ExpectedSourceSessionKey = run.Origin?.Source.SessionKey ?? "",
            ForceDispatch = command != ShopCommands.GetState };
    private static PatrolFailure Fail(string message) => new(message, false);
    private static PatrolFailure Block(string message) => new(message, true);
    private static PatrolRunResult Result(RunState run, string state, string detail, bool clean) =>
        new(run.Id, state, run.Completed, run.Route?.Points.Length ?? 0, run.RetriesUsed, detail, clean)
        { CurrentRound = run.CurrentRound, TotalRounds = run.Route?.RoundCount ?? 1, CompletedRounds = run.CompletedRounds };
    private void Publish(RunState run, string state, string detail)
    {
        try { Progress?.Invoke(new(run.Id, state, run.Completed, run.Route?.Points.Length ?? 0,
            run.Point?.PointId, run.Point?.Name, run.Retry, detail)
            { CurrentRound = run.CurrentRound, TotalRounds = run.Route?.RoundCount ?? 1, CompletedRounds = run.CompletedRounds }); }
        catch { /* Presentation callbacks cannot change dispatch or cleanup decisions. */ }
    }
    private sealed class PatrolFailure(string message, bool uncertain) : InvalidOperationException(message)
    { internal bool Uncertain { get; } = uncertain; internal string? Code { get; init; } }
    private sealed class RunState(string id)
    {
        internal string Id = id;
        internal PatrolRouteSnapshot? Route;
        internal PatrolObservation? Origin;
        internal PatrolWaypoint? Point;
        internal ShopTargetSnapshot? FrozenTargets;
        internal int Completed, Retry, RetriesUsed, CompletedRounds;
        internal int CurrentRound = 1;
        internal PatrolNavigationRequest? NavigationRequest;
        internal bool NavigationOwned, NavigationSettled;
        internal ShopCommandRequest? ScanRequest;
        // A submitted request has no ownership until a matching acceptance is
        // observed. Unknown acceptance remains separately fenced for cleanup.
        internal ShopCommandRequest? PendingScanRequest;
        internal string? PlanId;
        internal bool ScanOwned, ScanSettled, UserStopped;
        internal PatrolCheckpointPhase Phase;
        internal int CaptureRecoveryAttempts, RecoveryCycles;
        internal string? LastScanFailureCode, LastScanFailurePhase, LastScanFailureScope;
        internal bool PageRangeFailureEligible;
        internal Dictionary<string, string> ImportedLogSha256 = [];
        internal SourceIdentity? ImportedSource;
        internal ulong ImportedSelfId;
        internal RunState? PriorOwnership;
        internal PatrolCheckpoint? PriorCheckpoint;
        internal long ResetGeneration;
    }
}
