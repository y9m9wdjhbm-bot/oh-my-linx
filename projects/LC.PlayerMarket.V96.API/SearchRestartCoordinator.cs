using MemoryHub;

namespace LcPlayerMarket;

/// <summary>A single user transaction; no request is automatically replayed.</summary>
internal interface ISearchRestartPort
{
    SourceIdentity? CurrentSource { get; }
    bool CanStart(SourceIdentity source, out string reason);
    Task StopPatrolAndJoinAsync(CancellationToken token);
    Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token);
    void ApplyReset(ShopCommandRequest request, ShopCommandResponse response);
    Task ResetPatrolAsync(CancellationToken token);
    PatrolRouteLoadResult LoadRoute();
    Task StartPatrolAsync(PatrolRouteSnapshot route, SourceIdentity expected, CancellationToken token);
    void AdoptRefresh(ShopCommandRequest request, ShopCommandResponse response, SourceIdentity expected);
    Task StopAcceptedScanAndJoinAsync(ShopCommandRequest scanRequest, ShopCommandResponse acceptance);
}

internal sealed record SearchRestartOutcome(bool ResetConfirmed, bool Restarted, string Mode, string Detail);

internal sealed class SearchRestartCoordinator(ISearchRestartPort port)
{
    private int running;

    internal async Task<SearchRestartOutcome> RunAsync(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("正在清除並從頭開始，請等待這次操作完成。");
        try
        {
            var source = port.CurrentSource;
            token.ThrowIfCancellationRequested();
            await port.StopPatrolAndJoinAsync(token);
            token.ThrowIfCancellationRequested();
            var reset = new ShopCommandRequest
            {
                Command = ShopCommands.ResetSearchProgress, ForceDispatch = true,
                ExpectedSourceSessionKey = source?.SessionKey ?? ""
            };
            var cleared = await port.SendAsync(reset, token);
            ValidateReset(reset, cleared);
            // An acknowledged local reset is truthful even if Stop was pressed
            // while its reply arrived. Cancellation prevents the subsequent start.
            port.ApplyReset(reset, cleared);
            // Complete the local checkpoint reset after an exact durable broker
            // acknowledgement. Stop cancels automatic start, not this final
            // local commit; otherwise a later Start could revive the old point.
            await port.ResetPatrolAsync(CancellationToken.None);
            token.ThrowIfCancellationRequested();
            if (source is null)
                return new(true, false, "Cleared", "已清除搜尋紀錄與進度；尚未開啟遊戲，開啟後可重新搜尋或開始巡迴。");
            if (port.CurrentSource != source)
                return new(true, false, "Cleared", "已清除搜尋紀錄與進度；LC 來源已變更，請使用目前角色重新開始。");
            if (!port.CanStart(source, out string reason))
                return new(true, false, "Cleared", "已清除搜尋紀錄與進度；尚未開始：" + reason);
            var loaded = port.LoadRoute();
            if (!loaded.Success)
                return new(true, false, "Cleared", "已清除搜尋紀錄與進度；巡迴設定無法讀取：" + loaded.Error);
            if (loaded.Snapshot!.Points.Length > 0)
            {
                PatrolRouteRules.Validate(loaded.Snapshot);
                RequireSource(source, token);
                await port.StartPatrolAsync(loaded.Snapshot, source, token);
                return new(true, true, "Patrol", "已清除搜尋紀錄與進度；已送出從第 1 輪第 1 點開始巡迴指令，正在準備。");
            }
            RequireSource(source, token);
            var refresh = new ShopCommandRequest
            { Command = ShopCommands.RefreshTargets, ForceDispatch = true, ExpectedSourceSessionKey = source.SessionKey };
            var refreshed = await port.SendAsync(refresh, token);
            RequireSource(source, token);
            if (!ManualNearbyContext.TryAdoptRefresh(null, source, port.CurrentSource,
                refresh.RequestId, refreshed, out var fresh, out string refreshReason))
                throw new InvalidDataException(refreshReason + " " + refreshed.Detail);
            port.AdoptRefresh(refresh, refreshed, source);
            RequireSource(source, token);
            var scan = new ShopCommandRequest
            {
                Command = ShopCommands.ScanVisible, ForceDispatch = true,
                ExpectedSourceSessionKey = source.SessionKey, TargetSnapshotId = fresh!.SnapshotId
            };
            // Preserve the original acceptance task through a Stop race. The
            // scan worker has its own lifetime after acceptance; caller token
            // cancellation alone cannot stop it.
            var acceptance = port.SendAsync(scan, CancellationToken.None);
            ShopCommandResponse searched;
            try { searched = await acceptance.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                searched = await acceptance;
                await StopAcceptedScanAsync(scan, searched);
                throw;
            }
            if (token.IsCancellationRequested || port.CurrentSource != source)
            {
                await StopAcceptedScanAsync(scan, searched);
                RequireSource(source, token);
            }
            if (searched.Schema != ShopCommands.Schema || searched.RequestId != scan.RequestId
                || searched.State != "ScanStarted" || !string.IsNullOrWhiteSpace(searched.FailureCode))
                throw new InvalidDataException("進度已清除，但重新搜尋回覆未確認；沒有自動重送。" + searched.Detail);
            return new(true, true, "Search", "已清除搜尋紀錄與進度，已重新刷新周遭並建立新的搜尋。");
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private Task StopAcceptedScanAsync(ShopCommandRequest request, ShopCommandResponse response)
    {
        if (response.Schema != ShopCommands.Schema || response.RequestId != request.RequestId)
            throw new InvalidDataException("取消後無法核對原搜尋回覆；保留原請求，沒有盲目停止另一工作。");
        if (response.State != "ScanStarted")
        {
            if (response.Scan.JobId == request.RequestId && response.Scan.Running)
                throw new InvalidDataException("原搜尋回覆未確認但工作仍在執行；保留原工作識別供停止。");
            return Task.CompletedTask;
        }
        if (response.Scan.JobId != request.RequestId)
            throw new InvalidDataException("取消後搜尋工作識別不符；沒有停止另一工作。");
        return port.StopAcceptedScanAndJoinAsync(request, response);
    }

    private void RequireSource(SourceIdentity source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (port.CurrentSource != source)
            throw new InvalidOperationException("LC 來源已變更，未繼續送出下一個重新開始指令。");
    }

    internal static void ValidateReset(ShopCommandRequest request, ShopCommandResponse response)
    {
        var history = response.SearchHistory;
        if (response.Schema != ShopCommands.Schema || response.RequestId != request.RequestId
            || response.State != "SearchProgressReset" || !string.IsNullOrWhiteSpace(response.FailureCode)
            || !response.WorkSettled || response.Scan.Running || response.Scan.JobId is not null
            || response.SavedScan is not null || response.SearchedOwners.Length != 0
            || history is null || string.IsNullOrWhiteSpace(history.Revision) || string.IsNullOrWhiteSpace(history.Epoch)
            || !string.IsNullOrWhiteSpace(history.ReadError) || history.OwnerCount != 0 || history.SearchedPages != 0
            || history.ConfirmedPages != 0 || history.UncertainPages != 0)
            throw new InvalidDataException("搜尋紀錄清除回覆未確認；未重送，也未開始新的搜尋或巡迴。" + response.Detail);
    }
}
