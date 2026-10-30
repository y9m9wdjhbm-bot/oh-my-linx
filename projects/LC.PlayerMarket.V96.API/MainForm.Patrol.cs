using System.Globalization;
using System.Runtime.InteropServices;
using MemoryHub;

namespace LcPlayerMarket;

internal sealed partial class MainForm
{
    private readonly Button patrolButton = Button("巡迴點設定");
    private PatrolRouteStore patrolRoutes = null!;
    private PatrolNavigationClient patrolNavigation = null!;
    private PatrolController patrolController = null!;
    private PatrolRouteForm? patrolWindow;
    private CancellationTokenSource? patrolCancellation;
    private Task<PatrolRunResult>? patrolTask;
    private TaskCompletionSource<bool>? patrolUiCompletion;
    private Exception? patrolStartFailure;
    private TaskCompletionSource<bool>? patrolStartCompletion;
    private bool patrolRunning, patrolBlocked, patrolStopRequested;
    private long patrolUiGeneration;
    private string patrolStatus = "定位點會自動保存；每點取得一次擺攤名單，再依名單與設定間隔強制逐頁查詢，無道具即記為空頁，完成設定輪數後停止。F9 可停止整個巡迴。";
    private SearchOverlayView? patrolOverlayView;
    private bool PatrolActive => patrolRunning || patrolBlocked;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private void InitializePatrol()
    {
        patrolRoutes = new(store.Root);
        // Keep failure details after session cleanup; the client creates a fresh ticks/GUID directory for every child.
        patrolNavigation = new(MarketLogPaths.Navigation(store.DiagnosticsRoot),
            () => Volatile.Read(ref snapshot), report: notice =>
            {
                long generation = Volatile.Read(ref patrolUiGeneration);
                PostPatrolUi(() =>
                {
                    Report("PATROL_NAV_" + notice.Code, notice.Detail, notice);
                    if (!patrolRunning || patrolStopRequested || generation != Volatile.Read(ref patrolUiGeneration)) return;
                    var view = PatrolOverlayPresentation.FromNavigationNotice(notice, patrolOverlayView, DateTimeOffset.UtcNow);
                    if (view is null) return;
                    patrolOverlayView = view;
                    patrolStatus = notice.Detail;
                    patrolWindow?.SetRunState(true, patrolStatus);
                });
            }, forceDispatch: true);
        patrolController = new(patrolNavigation, new MainPatrolMarket(this), forceDispatch: true,
            checkpointStore: new PatrolCheckpointStore(SessionRoot), autoResume: true, continuousRecovery: true, freezeShoppingTargets: true);
        patrolController.Progress += progress =>
        {
            long generation = Volatile.Read(ref patrolUiGeneration);
            PostPatrolUi(() =>
            {
            if (!patrolRunning || generation != Volatile.Read(ref patrolUiGeneration)) return;
            patrolStatus = $"第 {progress.CurrentRound}/{progress.TotalRounds} 輪（完成 {progress.CompletedRounds} 輪）｜第 {Math.Min(progress.PointIndex + 1, progress.TotalPoints)}/{progress.TotalPoints} 點「{progress.PointName}」｜{progress.Detail}";
            patrolWindow?.SetRunState(true, patrolStatus);
            patrolOverlayView = PatrolOverlayPresentation.Build(progress.State, progress.Detail, DateTimeOffset.UtcNow, progress);
            Report("PATROL_PROGRESS", patrolStatus, progress);
            });
        };
    }

    private void ShowPatrolRoute()
    {
        if (closing) return;
        if (patrolWindow is null || patrolWindow.IsDisposed)
        {
            var saved = patrolRoutes.Load();
            Report("PATROL_ROUTE_OPEN", saved.Success
                ? $"已載入 {saved.Snapshot!.Points.Length} 個巡迴點。"
                : saved.Error ?? "巡迴清單無法載入。", new
                {
                    Path = patrolRoutes.DirectoryPath, saved.Error,
                    saved.Snapshot?.Revision, saved.Snapshot?.RoundCount, PointCount = saved.Snapshot?.Points.Length,
                    saved.Snapshot?.RetryLimit, saved.Snapshot?.ArrivalRadius
                });
            patrolWindow = new(patrolRoutes, CapturePatrolPointAsync, route => RunPatrolAsync(route), StopPatrolAsync);
            patrolWindow.SetRunState(PatrolActive, patrolStatus);
            patrolWindow.Show(this);
        }
        else { patrolWindow.SetRunState(PatrolActive, patrolStatus); patrolWindow.Activate(); }
    }

    private async Task<PatrolWaypoint> CapturePatrolPointAsync(CancellationToken token)
    {
        if (!SearchMutationAllowed || PatrolActive) throw new InvalidOperationException("請先等待目前清除或巡迴工作完成，再變更定位點。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Report("PATROL_CAPTURE_START", "正在讀取目前位置；首次讀取需要初始化導航元件，可能較久。");
        PatrolObservation value;
        try { value = await patrolNavigation.CaptureAsync(linked.Token); }
        catch (Exception error)
        {
            Report("PATROL_CAPTURE_FAILED", "目前位置尚未加入：" + error.Message,
                new { ElapsedMilliseconds = elapsed.ElapsedMilliseconds });
            throw;
        }
        Report("PATROL_CAPTURE_READY", "已讀取目前位置。",
            new { value.MapId, value.X, value.Y, ElapsedMilliseconds = elapsed.ElapsedMilliseconds });
        var live = new LiveContext(snapshot, DateTimeOffset.UtcNow);
        string name = string.IsNullOrWhiteSpace(live.MapName) ? "地圖 " + value.MapId : live.MapName;
        return new(Guid.NewGuid().ToString("N"), $"{name} {value.X},{value.Y}",
            value.MapId.ToString(CultureInfo.InvariantCulture), name, value.X, value.Y);
    }

    private async Task RunPatrolAsync(PatrolRouteSnapshot route, bool foregroundAttempted = false)
    {
        patrolStartFailure = null; patrolStartCompletion = null;
        PreparePatrolStart(() =>
        {
            PatrolRouteRules.Validate(route);
            if (!SearchMutationAllowed || PatrolActive)
                throw new InvalidOperationException("原巡迴工作仍在執行或尚未釋放，不能同時建立另一個巡迴。");
            var compatibility = MarketNativeCompatibility.Assess(snapshot?.Source);
            if (!compatibility.CanSend) throw new InvalidOperationException(compatibility.Message);

        });
        patrolCancellation?.Dispose(); patrolCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var uiCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        patrolUiCompletion = uiCompletion;
        var startCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        patrolStartCompletion = startCompletion;
        Interlocked.Increment(ref patrolUiGeneration);
        patrolRunning = true; patrolStopRequested = false; patrolTask = null;
        bool dispatched = false;
        try
        {
            patrolOverlayView = PatrolOverlayPresentation.Build("Preparing", "正在核對巡迴路線與 LC 身分。", DateTimeOffset.UtcNow,
                new PatrolProgress("", "Preparing", 0, route.Points.Length, null, null, 0, "啟動核對")
                { CurrentRound = 1, TotalRounds = route.RoundCount, CompletedRounds = 0 });
            patrolStatus = $"第 1/{route.RoundCount} 輪（完成 0 輪）｜正在核對同地圖定位點與目前角色；每點取得一次擺攤名單，再依名單與設定間隔強制逐頁查詢，無道具即記為空頁，完成設定輪數後停止。";
            patrolWindow?.SetRunState(true, patrolStatus); UpdateButtons();
            var expected = CurrentPatrolStartupContext()
                ?? throw new InvalidOperationException("目前角色或完整來源尚未確認，尚未開始巡迴。");
            PatrolStartupReadinessResult readiness;
            patrolStartupContextLatch = new(expected, () => Environment.TickCount64);
            patrolStartupWaiting = true;
            nativeReadinessCancellation = patrolCancellation;
            try
            {
                var prewarm = await PatrolNavigationPrewarm.RunAsync(expected,
                    token => patrolNavigation.CaptureAsync(token), CurrentPatrolStartupWaitContext,
                    () => DateTimeOffset.UtcNow, patrolCancellation.Token, WaitForPatrolStartupFreshIdentityAsync);
                Report("PATROL_NAVIGATION_PREWARM_READY", "導航唯讀初始化完成，現在重新核對商店入口。",
                    new { prewarm.SampledAt, prewarm.Source, prewarm.SelfId, HistoricalPoseOnly = true });
                readiness = await PatrolStartupReadinessPolicy.WaitAsync(expected,
                    SamplePatrolStartupReadinessAsync, CurrentPatrolStartupWaitContext,
                    () => DateTimeOffset.UtcNow, () => Environment.TickCount64,
                    (wait, token) => Task.Delay(wait, token), patrolCancellation.Token,
                    progress =>
                    {
                        patrolStatus = progress.ProbeStillRunning
                            ? $"原入口檢查仍在執行，巡迴尚未開始；已等待 {progress.ElapsedMilliseconds / 1000} 秒，可停止/F9。未啟動另一筆檢查。"
                            : $"入口未就緒，巡迴尚未開始，等待重檢，可停止/F9。已檢查 {progress.Attempt}/{progress.MaximumAttempts} 次（總上限 30 秒）；{progress.NextCheckMilliseconds / 1000} 秒後再次檢查。";
                        patrolWindow?.SetRunState(true, patrolStatus);
                        patrolOverlayView = PatrolOverlayPresentation.Build("WaitingNativeReady", patrolStatus, DateTimeOffset.UtcNow) with
                        { Player = patrolOverlayView?.Player ?? "", Progress = patrolOverlayView?.Progress ?? "" };
                        Report("PATROL_STARTUP_READINESS_WAIT", patrolStatus, progress);
                    }, continuous: true, recoverContext: WaitForPatrolStartupFreshIdentityAsync);
            }
            finally
            {
                patrolStartupWaiting = false;
                if (ReferenceEquals(nativeReadinessCancellation, patrolCancellation)) nativeReadinessCancellation = null;
                if (!closing) UpdateButtons();
            }
            Report("PATROL_STARTUP_READINESS_RESULT", readiness.Code,
                new { readiness.Ready, readiness.Code, readiness.Attempts, readiness.ElapsedMilliseconds });
            if (!readiness.Ready || readiness.Permit is null || readiness.FinalObservation is null)
            {
                if (patrolStartupContextLatch?.Invalidated == true)
                    throw new InvalidOperationException("LC 來源或角色已改變，或等待全新讀值已逾上限，已結束這次啟動意圖；巡迴尚未開始。");
                patrolCancellation.Token.ThrowIfCancellationRequested();
                throw new InvalidOperationException($"商店入口啟動核對未通過（{readiness.Code}）；{NativeReadinessDetail()}");
            }
            // The permit is consumed once and rechecks the exact response,
            // context, freshness, cancellation and time since the final Ready observation.
            var final = readiness.FinalObservation with { Current = CurrentPatrolStartupWaitContext(),
                ContextFresh = CurrentPatrolStartupWaitContext() == expected,
                NoOutstandingUiWork = NoPatrolStartupUiWork() };
            if (!readiness.Permit.TryCommit(final, DateTimeOffset.UtcNow, patrolCancellation.Token, () =>
            {
                if (!foregroundAttempted) AttemptPatrolForeground();
                if (!store.ResumeListings(DateTimeOffset.UtcNow)) throw new InvalidOperationException("商品收錄狀態無法保存，未開始巡迴。");
                ShowMarketListings("StartPatrol");
                Report("PATROL_START", patrolStatus, new { route.Revision, route.RoundCount, route.RetryLimit, recoverableRetryPolicy = "BoundedPerPoint", route.ArrivalRadius, route.Points });
                dispatched = true;
                patrolTask = patrolController.RunAsync(route, patrolCancellation.Token);
                startCompletion.TrySetResult(true);
            }, out string commitCode))
                throw new InvalidOperationException($"商店入口啟動資格已失效（{commitCode}），尚未開始巡迴。");
            var result = await patrolTask!;
            Interlocked.Increment(ref patrolUiGeneration);
            patrolBlocked = !result.CleanupConfirmed;
            patrolStatus = $"{result.Detail} 完整輪數 {result.CompletedRounds}/{result.TotalRounds}；第 {result.CurrentRound} 輪完成 {result.CompletedPoints}/{result.TotalPoints} 點；接續 {result.RetriesUsed} 次。";
            patrolOverlayView = PatrolOverlayPresentation.BuildResult(result, DateTimeOffset.UtcNow);
            Report("PATROL_FINISHED", patrolStatus, result);
        }
        catch (Exception error)
        {
            patrolStartFailure = error;
            patrolBlocked = dispatched; patrolStatus = dispatched ? "巡查結果尚未完成確認：" + error.Message : "巡查尚未啟動：" + error.Message;
            patrolOverlayView = PatrolOverlayPresentation.Build(error is OperationCanceledException && !dispatched ? "UserStopped" : "Failed", error.Message, DateTimeOffset.UtcNow);
            Report("PATROL_ERROR", patrolStatus, new { error = error.ToString(), dispatched });
        }
        finally
        {
            Interlocked.Increment(ref patrolUiGeneration);
            patrolRunning = false;
            try { if (!closing) { patrolWindow?.SetRunState(PatrolActive, patrolStatus); RefreshRows(); UpdateButtons(); } }
            finally { startCompletion.TrySetResult(false); uiCompletion.TrySetResult(true); }
        }
    }

    private void PreparePatrolStart(Action preparation) => PatrolStartPreparation.Execute(preparation, PresentPatrolStartFailure);

    private void PresentPatrolStartFailure(Exception error)
    {
        patrolStatus = "巡迴尚未啟動：" + error.Message;
        patrolOverlayView = PatrolOverlayPresentation.Build("Failed", error.Message, DateTimeOffset.UtcNow);
        if (!closing) { patrolWindow?.SetRunState(PatrolActive, patrolStatus); Report("PATROL_START_PREPARATION_FAILED", patrolStatus); }
    }

    private void AttemptPatrolForeground()
    {
        if (ReadPatrolSnapshot()?.Source is not { } source)
            throw new InvalidOperationException("目前沒有可核對的 LC 身分；巡迴尚未啟動。");
        bool focused = PatrolForegroundGate.TryOnce(source, () => ReadPatrolSnapshot()?.Source,
            new PatrolForegroundWindow(), lifetime.Token);
        Report("PATROL_FOREGROUND_ATTEMPT", focused ? "已單次切換 LC 前景，繼續巡迴準備。" : "單次切換 LC 前景未成功，繼續巡迴準備。",
            new { source.ProcessId, source.CreationFileTime, ForegroundVerified = focused, AttemptLimit = 1, WaitMilliseconds = 0 });
    }

    private async Task StopPatrolAsync()
    {
        var ownedTask = patrolTask;
        var ownedUi = patrolUiCompletion?.Task;
        if (patrolRunning)
        {
            patrolStopRequested = true; patrolCancellation?.Cancel();
            patrolStatus = "已直接送出停止巡迴指令；正在結束目前工作，不會前往下一點。";
            if (!closing) patrolWindow?.SetRunState(true, patrolStatus);
        }
        if (ownedTask is not null) await ownedTask;
        if (ownedUi is not null) await ownedUi;
    }

    private void CheckPatrolStopHotkey()
    {
        if (nativeReadinessBusy && (GetAsyncKeyState(0x78) & 0x8000) != 0) nativeReadinessCancellation?.Cancel();
        if (resettingSearch && (GetAsyncKeyState(0x78) & 0x8000) != 0)
            CancelSearchRestart();
        if (patrolRunning && !patrolStopRequested && (GetAsyncKeyState(0x78) & 0x8000) != 0)
        {
            patrolStopRequested = true;
            patrolCancellation?.Cancel();
            Report("PATROL_F9_STOP", "F9 已直接送出停止巡迴指令，不會再走下一點。");
        }
    }

    private void PostPatrolUi(Action action)
    {
        if (closing || IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => { if (!closing && !IsDisposed && !Disposing) action(); })); }
        catch (InvalidOperationException) { }
    }

    private HubSnapshot? ReadPatrolSnapshot() => Volatile.Read(ref snapshot);

    private sealed class MainPatrolMarket(MainForm owner) : IPatrolMarket
    {
        public async Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            var requestedSource = owner.ReadPatrolSnapshot()?.Source;
            long resetGeneration = Volatile.Read(ref owner.searchResetGeneration);
            if (request.Command == ShopCommands.RefreshTargets)
                owner.PostPatrolUi(() => { if (resetGeneration == Volatile.Read(ref owner.searchResetGeneration)) owner.manualNearby?.BlockDispatch("巡迴到點重新刷新周遭；本次成功前不使用原名單。"); });
            var response = await owner.backend.SendAsync(request, token).ConfigureAwait(false);
            owner.PostPatrolUi(() =>
            {
                owner.MergeResponseCore(response, request.Command == ShopCommands.GetState, resetGeneration);
                if (resetGeneration == Volatile.Read(ref owner.searchResetGeneration)
                    && request.Command == ShopCommands.RefreshTargets && requestedSource is not null
                    && ManualNearbyContext.TryAdoptRefresh(owner.manualNearby, requestedSource, owner.snapshot?.Source,
                        request.RequestId, response, out var adopted, out _))
                {
                    owner.manualNearby = adopted;
                    owner.selectedNavigation = null; owner.navigationReadTicket++; owner.overlay.Clear();
                    owner.RefreshRows(); owner.UpdateButtons();
                }
            });
            return response;
        }
    }
}
