using System.Globalization;
using MemoryHub;
namespace LcPlayerMarket;

internal sealed class PatrolControllerStrict
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
    private int running;
    private bool blocked;
    private string? blockedReason;
    public event Action<PatrolProgress>? Progress;

    internal PatrolControllerStrict(IPatrolNavigation navigation, IPatrolMarket market,
        Func<DateTimeOffset>? utcNow = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? cleanupTimeout = null)
    {
        this.navigation = navigation;
        this.market = market;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.delay = delay ?? Task.Delay;
        this.cleanupTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(15);
        if (this.cleanupTimeout <= TimeSpan.Zero || this.cleanupTimeout > TimeSpan.FromSeconds(15))
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
    }

    internal async Task<PatrolRunResult> RunAsync(PatrolRouteSnapshot route, CancellationToken token = default)
    {
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
            var idle = await SourceStateAsync(run, token).ConfigureAwait(false);
            ValidateMarketSource(idle, run.Origin);
            if (!idle.WorkSettled || idle.Scan.Running || SavedProgressBlocks(idle, run.Origin, beforeNewPlan: true))
                throw Block("商店仍有其他工作、清理或未定頁，未啟動巡迴。");
            if (idle.SavedScan?.UncertainPages > 0)
                Publish(run, "Preparing", "已核對新的 LC 程序；舊程序未定搜尋紀錄保留，本輪將建立新搜尋計畫。");
            for (int round = 1; round <= run.Route.RoundCount; round++)
            {
                // Keep this run's immutable route/source fences and owned pending work across rounds.
                token.ThrowIfCancellationRequested();
                run.CurrentRound = round; run.Completed = 0; run.Point = null; run.Retry = 0;
                if (round > 1)
                {
                    var between = await SourceStateAsync(run, token).ConfigureAwait(false);
                    ValidateMarketSource(between, run.Origin);
                    if (!between.WorkSettled || between.Scan.Running || SavedProgressBlocks(between, run.Origin, beforeNewPlan: true))
                        throw Block("前輪市場工作尚未清理或結果未明，沒有開始下一輪。");
                }
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
            if (uncertain)
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
        var request = new PatrolNavigationRequest(Guid.NewGuid().ToString("N"), before, run.Point!, run.Route!.ArrivalRadius);
        run.NavigationRequest = request;
        run.NavigationSettled = false;
        Publish(run, "Moving", "正在前往定位點。");
        PatrolNavigationState state;
        try { state = await navigation.StartAsync(request, token).ConfigureAwait(false); }
        catch (Exception error) when (TransportFailure(error) && !token.IsCancellationRequested)
        {
            // A lost acceptance reply is never permission to repeat Start.
            state = await navigation.GetStateAsync(request.JobId, token).ConfigureAwait(false);
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
            if (state.Terminal)
            {
                if (!state.Completed || state.State != "Completed") throw Fail(state.Error ?? "導航未完成，沒有查詢商店。");
                if (!state.WorkSettled || !state.CleanupConfirmed || !state.Standing || state.StandingSamples < 2)
                    throw Block("導航完成回覆缺少站定或清理證據，未查詢商店。");
                Fresh(state.SampledAt);
                if (Distance(state.X, state.Y, request.Waypoint) > request.ArrivalRadius)
                    throw Fail("導航回覆位置超出定位點容差，未查詢商店。");
                Publish(run, "Arriving", "已確認導航工作完成、站定與清理。");
                return;
            }
            bool actualProgress = state.State == "Moving" && !state.Standing
                && state.SampledAt > before.SampledAt && (state.X != before.X || state.Y != before.Y)
                && utcNow() - state.SampledAt <= TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds) && state.SampledAt <= utcNow();
            Publish(run, actualProgress ? "MovingConfirmed" : "Navigating", actualProgress
                ? $"已觀察同來源座標進展 {state.X},{state.Y}；取樣 {state.SampledAt.ToLocalTime():HH:mm:ss}。"
                : "導航命令中；等待同來源的實際行走進展。");
            await delay(PollInterval, token).ConfigureAwait(false);
            state = await navigation.GetStateAsync(request.JobId, token).ConfigureAwait(false);
        }
        throw Block("導航工作未在有界等候內結束；未重送導航。");
    }

    private async Task SearchPointAsync(RunState run, CancellationToken token)
    {
        string? plan = null;
        bool classificationOnly = false;
        string stoppedScanFailure = "";
        bool recoveringRefresh = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await CaptureAtPointAsync(run, token).ConfigureAwait(false);
            Publish(run, run.Retry == 0 ? "Refreshing" : "Retrying", classificationOnly
                ? "本點接續額度已用完；只刷新確認剩餘頁是否都已離場或收攤，不再送出搜尋。" : run.Retry == 0
                ? "站定後刷新此點周遭商店。" : plan is null
                    ? $"本點已使用 {run.Retry} 次重試額度；重新確認此點商店。"
                    : $"第 {run.Retry} 次接續：重新確認此點商店，只查尚未送出頁。");
            var beforeRefresh = await SourceStateAsync(run, token).ConfigureAwait(false);
            ValidateMarketSource(beforeRefresh, run.Origin!);
            if (!beforeRefresh.WorkSettled || beforeRefresh.Scan.Running || SavedProgressBlocks(beforeRefresh, run.Origin!, beforeNewPlan: plan is null))
                throw Block("刷新前商店工作尚未清理或含未定頁，未接管其他工作。");
            string? refreshScanJob = beforeRefresh.Scan.JobId, refreshPlan = beforeRefresh.SavedScan?.PlanId;
            var refreshRequest = Command(run, ShopCommands.RefreshTargets);
            var refresh = await market.SendAsync(refreshRequest, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (refresh.RequestId != refreshRequest.RequestId)
                throw Block("刷新回覆識別不符，沒有重送或開始搜尋。");
            if (refresh.FailureCode is "SourceUnavailable" or "SourceMismatch")
                throw Block("刷新期間來源無法確認或已改變，沒有重送或開始搜尋。");
            if (refresh.Scan.JobId != refreshScanJob || refresh.SavedScan?.PlanId != refreshPlan)
                throw Block("刷新期間搜尋工作或計畫被替換，未接管其他工作。");
            bool readOnlyGap = refresh.FailureCode is "RefreshReadTimeout" or "RefreshSourceSampleGap";
            if (readOnlyGap)
            {
                ValidateMarketReceipt(refresh, beforeRefresh);
                if (refresh.SessionBlocked || !refresh.WorkSettled || refresh.Scan.Running
                    || refresh.NativeEntered || refresh.NativeReturned || refresh.ResponseVerified
                    || SavedProgressBlocks(refresh, run.Origin!, beforeNewPlan: plan is null))
                    throw Block("唯讀刷新回覆含尚未清理、原生操作或未定搜尋證據；不能當作可恢復讀值空窗。");
            }
            var refreshReceipt = refresh;
            if (refreshReceipt.State == "TargetsRefreshed")
                refresh = await WaitForMarketSourceAsync(run, refresh, token,
                    state => ValidateMarketReceipt(state, refreshReceipt)).ConfigureAwait(false);
            else if (!readOnlyGap || refresh.CurrentSourceSessionKey is not null)
                ValidateMarketSource(refresh, run.Origin!);
            // Status re-reads cannot create a successful refresh receipt or replace its snapshot.
            if (refreshReceipt.State != "TargetsRefreshed")
            {
                bool retryable = refresh.State == "Rejected" && (refresh.FailureCode == "ContextUnavailable" || readOnlyGap)
                    && refresh.FailureScope == "Global" && refresh.FailurePhase == "BeforePublish" && !refresh.NativeEntered && !refresh.NativeReturned
                    && !refresh.ResponseVerified && refresh.WorkSettled && !refresh.SessionBlocked && !refresh.Scan.Running
                    && (!readOnlyGap || refresh.TargetSnapshot?.CanDispatch != true)
                    && !SavedProgressBlocks(refresh, run.Origin!, beforeNewPlan: plan is null);
                if (!retryable || classificationOnly) throw Fail("刷新未成功且沒有完整可重試證據或剩餘額度，沒有重送或開始搜尋。");
                if (run.Retry >= run.Route!.RetryLimit)
                    throw Fail("刷新失敗，重試次數已達設定上限；巡迴停止。");
                run.Retry++; run.RetriesUsed++;
                recoveringRefresh |= readOnlyGap;
                // Reserve from the shared budget before a capture which may itself
                // consume recovery attempts. A null response source is not proof:
                // this read must match every field of this run's original identity.
                await CaptureAtPointAsync(run, token).ConfigureAwait(false);
                Publish(run, "Retrying", $"刷新失敗，第 {run.Retry} 次重試；尚未開始新的商店搜尋。");
                continue;
            }
            var targets = refreshReceipt.TargetSnapshot;
            if (targets is null || !targets.CanDispatch || string.IsNullOrWhiteSpace(targets.SnapshotId)
                || targets.SourceSessionKey != run.Origin!.Source.SessionKey || targets.SelfId != run.Origin.SelfId)
                throw Fail("刷新名單的來源、角色或送出資格不完整。");
            Fresh(targets.RefreshedAt);
            for (int poll = 0; !refresh.WorkSettled || refresh.Scan.Running; poll++)
            {
                if (poll >= MaximumPolls) throw Block("刷新工作未在有界等候內結束。");
                await delay(PollInterval, token).ConfigureAwait(false);
                refresh = await StateAsync(run, token).ConfigureAwait(false);
                refresh = await WaitForMarketSourceAsync(run, refresh, token,
                    state => ValidateMarketReceipt(state, refreshReceipt)).ConfigureAwait(false);
            }
            if (SavedProgressBlocks(refresh, run.Origin!, beforeNewPlan: plan is null))
                throw Block("刷新後仍有本程序未定頁或無法確認的歷史來源，未啟動搜尋。");
            await CaptureAtPointAsync(run, token).ConfigureAwait(false);
            if (recoveringRefresh)
            {
                Publish(run, "Recovered", "已重新核對完整原來源、站定位置及新刷新名單；可恢復的唯讀刷新錯誤已解除，接續本點未送出頁。");
                recoveringRefresh = false;
            }
            if (plan is not null)
            {
                if (refresh.SavedScan?.PlanId != plan) throw Block("保存搜尋計畫已被替換，沒有接續其他計畫。");
                ValidateSaved(refresh.SavedScan, run.Origin!);
                if (!refresh.SavedScan.CanContinue)
                {
                    if (CanFinishDeferredPoint(refresh, run.Origin!))
                    {
                        Fresh(targets.RefreshedAt);
                        Publish(run, "Searching", $"本點可搜尋頁已處理；{refresh.SavedScan.DeferredPages} 個目前不在可搜尋名單的未送出頁保留，繼續下一定位點。");
                        return;
                    }
                    throw Fail(stoppedScanFailure + "刷新後仍無法接續尚未送出頁：" + refresh.SavedScan.ContinueBlockReason);
                }
                if (classificationOnly)
                    throw Fail(stoppedScanFailure + $"尚有 {refresh.SavedScan.RemainingPages} 個未送出頁；接續次數已達上限，巡迴停止。");
            }
            string? previousPlan = refresh.SavedScan?.PlanId;
            var scanRequest = Command(run, plan is null ? ShopCommands.ScanVisible : ShopCommands.ContinueSavedScan) with
                { TargetSnapshotId = targets.SnapshotId, ExpectedScanPlanId = plan };
            run.ScanRequest = scanRequest;
            run.ScanSettled = false;
            run.ScanOwned = false;
            run.PlanId = plan;
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
            for (int poll = 0; ; poll++)
            {
                token.ThrowIfCancellationRequested();
                ValidateScan(response, run, scanRequest, plan);
                string actualPlan = response.SavedScan!.PlanId;
                if (plan is null && previousPlan == actualPlan) throw Block("新搜尋沒有取得新的計畫識別，沒有採用舊工作結果。");
                plan = run.PlanId = actualPlan;
                run.ScanOwned = true;
                response = await WaitForMarketSourceAsync(run, response, token, state =>
                {
                    ValidateScan(state, run, scanRequest, plan);
                    ValidateSaved(state.SavedScan!, run.Origin!, requireSettled: state.WorkSettled && !state.Scan.Running);
                }, includeFailure: true).ConfigureAwait(false);
                var savedProgress = response.SavedScan!; // ValidateScan above also guards every replacement response.
                bool workerSettled = response.WorkSettled && !response.Scan.Running;
                run.ScanSettled = workerSettled && !response.SessionBlocked && savedProgress.UncertainPages == 0;
                ValidateMarketSource(response, run.Origin!, includeFailure: true);
                ValidateSaved(savedProgress, run.Origin!, requireSettled: workerSettled);
                if (response.Scan.StopReason is "Cancelled" or "UserStop")
                {
                    run.UserStopped = true;
                    throw new OperationCanceledException("商店搜尋已被手動停止。", token);
                }
                await CaptureAtPointAsync(run, token).ConfigureAwait(false);
                if (run.ScanSettled)
                {
                    // Retain only this run's validated, settled scan evidence across the classification refresh.
                    // This is presentation only: it changes no stop, retry, pending or native-entry rule.
                    stoppedScanFailure = DescribeStoppedScan(response.Scan);
                    break;
                }
                if (poll >= MaximumPolls) throw Block("商店工作未在有界等候內完成，沒有開始下一點。");
                await delay(PollInterval, token).ConfigureAwait(false);
                response = await StateAsync(run, token).ConfigureAwait(false);
            }
            // Sent without verified rows remains historical Sent. It is never retried,
            // but confirmed native settlement permits the remaining distinct pages.
            if (response.SavedScan!.RemainingPages == 0) return;
            if (run.Retry >= run.Route!.RetryLimit)
                classificationOnly = true;
            else
            {
                run.Retry++;
                run.RetriesUsed++;
            }
        }
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
                        || run.Retry >= run.Route!.RetryLimit) throw;
                    retries++; run.Retry++; run.RetriesUsed++;
                    Publish(run, "WaitingSource", $"本點第 {run.Retry} 次重試：短暫讀值空窗後只重新取得同來源的新座標，不重送行走或商店搜尋。");
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
        if (!observed.Standing || Distance(observed.X, observed.Y, run.Point!) > run.Route!.ArrivalRadius)
            throw Fail("角色未站定在本次定位點容差內，巡迴停止。");
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
        if (response.CurrentSourceSessionKey != expected.Source.SessionKey
            || includeFailure && (response.FailureCode is "SourceMismatch" or "SourceUnavailable" || response.Scan.FailureCode is "SourceMismatch" or "SourceUnavailable"))
            throw Block("商店來源已改變或無法確認，禁止接續與前往下一點。");
        if (response.SessionBlocked) throw Block("商店原生結果或清理未確認，巡迴已封鎖。");
    }

    private static void ValidateSaved(ShopSavedScanProgress saved, PatrolObservation expected, bool requireSettled = true)
    {
        if (string.IsNullOrWhiteSpace(saved.PlanId) || saved.SourceSessionKey != expected.Source.SessionKey
            || saved.UncertainPages < 0 || requireSettled && saved.UncertainPages != 0 || saved.TotalPages < 0 || saved.RemainingPages < 0
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
        response.WorkSettled && !response.Scan.Running && !response.SessionBlocked
        && response.CurrentSourceSessionKey == expected.Source.SessionKey
        && response.TargetSnapshot is { CanDispatch: true } targets
        && targets.SourceSessionKey == expected.Source.SessionKey && targets.SelfId == expected.SelfId
        && response.SavedScan is { CanContinue: false, ContinueBlockCode: "NoAvailableOwners", UncertainPages: 0,
            AvailablePages: 0, RemainingPages: > 0 } saved
        && saved.SourceSessionKey == expected.Source.SessionKey && saved.DeferredPages == saved.RemainingPages;

    private static bool SavedProgressBlocks(ShopCommandResponse response, PatrolObservation expected, bool beforeNewPlan)
    {
        var saved = response.SavedScan;
        if (saved is null || saved.UncertainPages == 0) return false;
        // A prior process's record remains historical. It can never authorize Continue,
        // remove a native pending lock, or replace a plan already owned by this run.
        if (!beforeNewPlan || saved.UncertainPages < 0 || !response.WorkSettled || response.Scan.Running || response.SessionBlocked
            || response.CurrentSourceSessionKey != expected.Source.SessionKey || string.IsNullOrWhiteSpace(saved.PlanId)
            || saved.TotalPages < 0 || saved.RemainingPages < 0 || !SavedCountsValid(saved))
            return true;
        var source = expected.Source;
        if (!TrySessionIdentity(source.SessionKey, out var current)
            || current.ProcessId != source.ProcessId || current.Created != source.CreationFileTime || current.SelfId != expected.SelfId
            || string.IsNullOrWhiteSpace(source.ImagePath) || source.PeTimestamp == 0 || source.ImageSize == 0
            || source.DiskSha256 is not { Length: 64 } || !source.DiskSha256.All(Uri.IsHexDigit)
            || source.ImageBase is null || !TryHex(source.ImageBase.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? source.ImageBase[2..] : source.ImageBase, out ulong image) || current.Image != image
            || !TrySessionIdentity(saved.SourceSessionKey, out var previous))
            return true;
        // Scene, character and image changes within the SAME process do not prove
        // that its unconfirmed native work has ended. PID reuse requires a new creation time.
        return previous.ProcessId == current.ProcessId && previous.Created == current.Created;
    }

    private static bool TrySessionIdentity(string? value, out (int ProcessId, long Created, ulong Image, ulong SelfId) identity)
    {
        identity = default;
        if (value is null || value.Length > 160) return false;
        string[] parts = value.Split(':');
        if (parts.Length != 6
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long created) || created <= 0
            || !TryHex(parts[2], out ulong image) || !TryHex(parts[3], out _) || !TryHex(parts[4], out _)
            || !ulong.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out ulong selfId) || selfId == 0)
            return false;
        identity = (pid, created, image, selfId);
        return true;
    }

    private static bool TryHex(string value, out ulong address)
    {
        address = 0;
        return value.Length is > 0 and <= 16 && value.All(Uri.IsHexDigit)
            && ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address) && address != 0;
    }

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

    private async Task<ShopCommandResponse> SourceStateAsync(RunState run, CancellationToken token)
    {
        var receipt = await StateAsync(run, token).ConfigureAwait(false);
        return await WaitForMarketSourceAsync(run, receipt, token,
            state => ValidateMarketReceipt(state, receipt)).ConfigureAwait(false);
    }

    private async Task<ShopCommandResponse> WaitForMarketSourceAsync(RunState run, ShopCommandResponse response,
        CancellationToken token, Action<ShopCommandResponse> validateReceipt, bool includeFailure = false)
    {
        // A missing current sample does not erase the accepted job. Re-read only its status;
        // each response must pass its request and immutable receipt fences before it can wait.
        token.ThrowIfCancellationRequested();
        validateReceipt(response);
        if (response.CurrentSourceSessionKey is not null)
        {
            ValidateMarketSource(response, run.Origin!, includeFailure);
            return response;
        }
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        waiting.CancelAfter(SourceWaitTimeout);
        Publish(run, "WaitingSource", "商店來源採樣暫缺，正在等候原工作狀態恢復；沒有重送搜尋。");
        try
        {
            for (int poll = 0; ; poll++)
            {
                waiting.Token.ThrowIfCancellationRequested();
                validateReceipt(response);
                if (response.CurrentSourceSessionKey is not null)
                {
                    ValidateMarketSource(response, run.Origin!, includeFailure: true);
                    return response;
                }
                // Explicit source failures and the native pending lock are never transient gaps.
                if (response.SessionBlocked || response.FailureCode is "SourceMismatch" or "SourceUnavailable"
                    || response.Scan.FailureCode is "SourceMismatch" or "SourceUnavailable")
                    throw Block("商店來源已有明確錯誤或原生清理未確認，未繼續等候或重送搜尋。");
                if (poll >= MaximumSourcePolls)
                    throw Block("商店來源採樣在有界等候內未恢復，未重送搜尋或前往下一點。");
                await delay(SourcePollInterval, waiting.Token).WaitAsync(waiting.Token).ConfigureAwait(false);
                response = await StateAsync(run, waiting.Token).WaitAsync(waiting.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw Block("商店來源採樣在有界等候內未恢復，未重送搜尋或前往下一點。");
        }
    }

    private static void ValidateMarketReceipt(ShopCommandResponse response, ShopCommandResponse expected)
    {
        if (response.Scan.JobId != expected.Scan.JobId || response.Scan.SourceSessionKey != expected.Scan.SourceSessionKey
            || response.Scan.TargetSnapshotId != expected.Scan.TargetSnapshotId
            || response.SavedScan?.PlanId != expected.SavedScan?.PlanId
            || response.SavedScan?.SourceSessionKey != expected.SavedScan?.SourceSessionKey
            || response.TargetSnapshot?.SnapshotId != expected.TargetSnapshot?.SnapshotId
            || response.TargetSnapshot?.SourceSessionKey != expected.TargetSnapshot?.SourceSessionKey
            || response.TargetSnapshot?.SelfId != expected.TargetSnapshot?.SelfId)
            throw Block("等候來源時搜尋工作、計畫或刷新名單已被替換，未接管其他工作。");
    }

    private async Task<bool> CleanupAsync(RunState run)
    {
        using var cleanup = new CancellationTokenSource(cleanupTimeout);
        var token = cleanup.Token;
        bool navigationClean = run.NavigationRequest is null || run.NavigationSettled;
        bool marketClean = run.ScanRequest is null || run.ScanSettled;
        // Ports are bounded by the cleanup token even if a transport ignores cancellation.
        try
        {
            if (!navigationClean)
            {
                var request = run.NavigationRequest!;
                // Stop is idempotent and fenced by this registered job ID. Its historical
                // cleanup receipt does not require the freshness used for arrival handoff.
                var state = await navigation.StopAsync(request.JobId, token).WaitAsync(token).ConfigureAwait(false);
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
                var request = run.ScanRequest!;
                var state = await StateAsync(run, token).WaitAsync(token).ConfigureAwait(false);
                ValidateScan(state, run, request, run.PlanId);
                run.ScanOwned = true;
                run.PlanId ??= state.SavedScan!.PlanId;
                if (!state.WorkSettled || state.Scan.Running)
                {
                    var cancel = Command(run, ShopCommands.CancelScan) with
                    {
                        ExpectedScanJobId = request.RequestId, TargetSnapshotId = request.TargetSnapshotId,
                        ExpectedScanPlanId = run.PlanId
                    };
                    state = await market.SendAsync(cancel, token).WaitAsync(token).ConfigureAwait(false);
                    ValidateScan(state, run, request, run.PlanId);
                }
                while (!state.WorkSettled || state.Scan.Running)
                {
                    await delay(PollInterval, token).WaitAsync(token).ConfigureAwait(false);
                    state = await StateAsync(run, token).WaitAsync(token).ConfigureAwait(false);
                    ValidateScan(state, run, request, run.PlanId);
                }
                marketClean = run.ScanSettled = !state.SessionBlocked && state.SavedScan!.UncertainPages == 0;
            }
        }
        catch { marketClean = false; }
        return navigationClean && marketClean;
    }

    private void Fresh(DateTimeOffset at)
    {
        var now = utcNow();
        if (at == default || at > now || now - at > TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds))
            throw Fail("角色觀測已過期或時間不符，未沿用舊座標。");
    }
    private static int Map(PatrolWaypoint point) => int.TryParse(point.MapId, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id >= 0
        ? id : throw Fail("定位點地圖 ID 必須是非負整數。");
    private static long Distance(int x, int y, PatrolWaypoint point) => Math.Max(Math.Abs((long)x - point.X), Math.Abs((long)y - point.Y));
    private static bool TransportFailure(Exception error) =>
        error is TimeoutException or IOException or System.Net.Http.HttpRequestException or OperationCanceledException;
    private static ShopCommandRequest Command(RunState run, string command) => new()
        { Command = command, RequestId = "patrol-" + Guid.NewGuid().ToString("N"), ExpectedSourceSessionKey = run.Origin?.Source.SessionKey ?? "" };
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
    { internal bool Uncertain { get; } = uncertain; }
    private sealed class RunState(string id)
    {
        internal string Id = id;
        internal PatrolRouteSnapshot? Route;
        internal PatrolObservation? Origin;
        internal PatrolWaypoint? Point;
        internal int Completed, Retry, RetriesUsed, CompletedRounds;
        internal int CurrentRound = 1;
        internal PatrolNavigationRequest? NavigationRequest;
        internal bool NavigationOwned, NavigationSettled;
        internal ShopCommandRequest? ScanRequest;
        internal string? PlanId;
        internal bool ScanOwned, ScanSettled, UserStopped;
    }
}
