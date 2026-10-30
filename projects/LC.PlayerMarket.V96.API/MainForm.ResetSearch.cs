using MemoryHub;

namespace LcPlayerMarket;

internal sealed partial class MainForm
{
    private readonly Button resetSearchButton = Button("清除並從頭開始");
    private readonly CheckBox noAdditionalPageWait = Check("不額外等待", true);
    private readonly NumericUpDown pageWaitMilliseconds = new()
    { Name = "PageOpenIntervalMilliseconds", Minimum = 100, Maximum = 5000, Increment = 100, Value = 3000, Width = 72, Margin = new Padding(3, 6, 3, 3) };
    private bool resettingSearch, searchRestartLaunching;
    private long searchResetGeneration;
    private CancellationTokenSource? searchResetCancellation;
    private Task? searchResetTask;
    // Pure test ports replace orchestration only; production UI state fencing
    // remains exercised by the actual form fixture.
    private Func<ISearchRestartPort>? searchRestartPortFactory;

    private bool SearchMutationAllowed => !closing && !nativeReadinessBusy && (!resettingSearch || searchRestartLaunching);

    private async Task ResetSearchAndRestartAsync()
    {
        if (closing || resettingSearch || nativeReadinessBusy) return;
        resettingSearch = true;
        InvalidateSearchResponses();
        searchResetCancellation?.Dispose();
        searchResetCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = searchResetCancellation.Token;
        ShowMarketListings("ResetSearch"); UpdateButtons();
        Report("SEARCH_RESET_START", "正在停止目前工作並清除搜尋紀錄與進度；完成後從頭開始。商品、座標設定與舊 LOG 保留。");
        var operation = ExecuteSearchRestartAsync(token);
        searchResetTask = operation;
        try { await operation; }
        finally
        {
            resettingSearch = false;
            if (!closing) { UpdateButtons(); RefreshRows(); }
        }
    }

    private async Task ExecuteSearchRestartAsync(CancellationToken token)
    {
        try
        {
            var coordinator = new SearchRestartCoordinator(searchRestartPortFactory?.Invoke() ?? new MainSearchRestartPort(this));
            var outcome = await coordinator.RunAsync(token);
            if (!closing) Report("SEARCH_RESET_RESULT", outcome.Detail, outcome);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!closing) Report("SEARCH_RESET_CANCELLED", "已停止這次從頭開始流程；已確認的清除仍保留，沒有再啟動搜尋或巡迴。");
        }
        catch (Exception error)
        {
            if (!closing) Report("SEARCH_RESET_FAILED", "清除並從頭開始尚未完成，沒有自動重送：" + error.Message,
                new { error = error.ToString() });
        }
    }

    private void CancelSearchRestart()
    {
        if (!resettingSearch) return;
        searchResetCancellation?.Cancel(); patrolCancellation?.Cancel();
    }

    private void InvalidateSearchResponses()
    {
        Interlocked.Increment(ref searchResetGeneration);
        refreshRequestTicket++;
    }

    private void ApplySearchReset(ShopCommandRequest request, ShopCommandResponse response)
    {
        SearchRestartCoordinator.ValidateReset(request, response);
        InvalidateSearchResponses();
        lastKnownSavedScan = null; lastKnownSearchHistory = null; lastConfirmedScan = null;
        lastConfirmedScanAt = default; lastSuccessfulAction = null; lastSuccessfulActionJobId = null;
        manualNearby = null; broker = null; liveKeys.Clear();
        selectedNavigation = null; navigationReadTicket++; overlay.Clear();
        patrolOverlayView = null;
        // A completed homepage marker must not display the prior route progress
        // while the restarted run is being prepared.
        homePatrolRequested = false;
        MergeResponse(response, false);
        recordsWindow?.UpdateProgress(null, snapshot?.Source?.SessionKey);
        recordsWindow?.UpdateHistorySummary(response.SearchHistory);
        Report("SEARCH_RESET_CONFIRMED", "已清除搜尋去重與本次進度；商品紀錄、巡迴設定、原操作與 LOG 保留。", response.SearchHistory);
    }

    private async Task ConfirmPatrolRestartLaunchAsync(Task launch)
    {
        if (patrolStartCompletion is { } preparation && !await preparation.Task)
            throw new InvalidOperationException("巡迴準備失敗：" + (patrolStartFailure?.Message ?? "入口檢查未完成，未開始巡迴。"));
        if (launch.IsCompleted) await launch;
        if (patrolStartFailure is { } failure) throw new InvalidOperationException("巡迴準備失敗：" + failure.Message, failure);
        if (patrolTask is { IsCompleted: true } completed)
        {
            var result = await completed;
            if (result.State != "Completed" || !result.CleanupConfirmed) throw new InvalidOperationException("巡迴尚未開始：" + result.Detail);
            return;
        }
        if (!patrolRunning) throw new InvalidOperationException("新巡迴未建立執行工作。");
    }

    private sealed class MainSearchRestartPort(MainForm owner) : ISearchRestartPort
    {
        public SourceIdentity? CurrentSource => owner.snapshot?.Source;
        public bool CanStart(SourceIdentity source, out string reason)
        {
            var compatibility = MarketNativeCompatibility.Assess(source);
            reason = compatibility.Message;
            return compatibility.CanSend && !owner.closing;
        }
        public async Task StopPatrolAndJoinAsync(CancellationToken token)
        {
            await owner.StopPatrolAsync();
            token.ThrowIfCancellationRequested();
            if (owner.patrolBlocked || owner.patrolTask is { IsCompletedSuccessfully: true } completed && !completed.Result.CleanupConfirmed)
                throw new InvalidOperationException("原巡迴工作尚未完成實際清理，不能清除後建立新巡迴。");
        }
        public async Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            long ticket = Volatile.Read(ref owner.searchResetGeneration);
            var response = await owner.backend.SendAsync(request, token);
            if (request.Command != ShopCommands.ResetSearchProgress && !owner.closing)
                owner.MergeResponseCore(response, false, ticket);
            return response;
        }
        public void ApplyReset(ShopCommandRequest request, ShopCommandResponse response) => owner.ApplySearchReset(request, response);
        public Task ResetPatrolAsync(CancellationToken token) => owner.patrolController.ResetProgressAsync(token);
        public PatrolRouteLoadResult LoadRoute() => owner.patrolRoutes.Load();
        public async Task StartPatrolAsync(PatrolRouteSnapshot route, SourceIdentity expected, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (owner.snapshot?.Source != expected) throw new InvalidOperationException("LC 來源已變更，未開始巡迴。");
            if (!owner.store.ResumeListings(DateTimeOffset.UtcNow)) throw new IOException("商品收錄狀態無法保存，未開始巡迴。");
            owner.searchRestartLaunching = true;
            Task launch;
            try { launch = owner.RunPatrolAsync(route); }
            finally { owner.searchRestartLaunching = false; }
            // RunPatrolAsync owns the full route. Reset completes once this new
            // run has started; it must not hold the toolbar for all rounds.
            using var cancelPreparation = token.Register(() => owner.patrolCancellation?.Cancel());
            await owner.ConfirmPatrolRestartLaunchAsync(launch);
            token.ThrowIfCancellationRequested();
        }
        public void AdoptRefresh(ShopCommandRequest request, ShopCommandResponse response, SourceIdentity expected)
        {
            if (!ManualNearbyContext.TryAdoptRefresh(null, expected, owner.snapshot?.Source,
                request.RequestId, response, out var fresh, out string reason)) throw new InvalidDataException(reason);
            if (!owner.store.ResumeListings(DateTimeOffset.UtcNow)) throw new IOException("商品收錄狀態無法保存，未開始搜尋。");
            owner.manualNearby = fresh;
            owner.selectedNavigation = null; owner.navigationReadTicket++; owner.overlay.Clear();
            owner.RefreshRows();
        }
        public async Task StopAcceptedScanAndJoinAsync(ShopCommandRequest scanRequest, ShopCommandResponse acceptance)
        {
            if (acceptance.RequestId != scanRequest.RequestId || acceptance.Scan.JobId != scanRequest.RequestId)
                throw new InvalidDataException("原重新搜尋工作無法核對，沒有停止另一工作。");
            long generation = Volatile.Read(ref owner.searchResetGeneration);
            var cancel = new ShopCommandRequest
            {
                Command = ShopCommands.CancelScan, ForceDispatch = true,
                ExpectedSourceSessionKey = scanRequest.ExpectedSourceSessionKey,
                ExpectedScanJobId = scanRequest.RequestId
            };
            var current = await owner.backend.SendAsync(cancel, owner.lifetime.Token);
            if (current.RequestId != cancel.RequestId) throw new InvalidDataException("原工作停止回覆識別不符。");
            while (true)
            {
                if (!owner.closing) owner.MergeResponseCore(current, true, generation);
                if (current.Schema != ShopCommands.Schema || current.Scan.JobId != scanRequest.RequestId)
                    throw new InvalidDataException("停止核對時工作已不同；原識別保留，沒有停止另一工作。");
                if (current.WorkSettled && !current.Scan.Running) return;
                await Task.Delay(100, owner.lifetime.Token);
                var read = new ShopCommandRequest { Command = ShopCommands.GetState };
                current = await owner.backend.SendAsync(read, owner.lifetime.Token);
                if (current.RequestId != read.RequestId) throw new InvalidDataException("原工作清理回覆識別不符。");
            }
        }
    }
}
