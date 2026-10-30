using System.Text.Json;

namespace MemoryHub.Service;

public sealed partial class ShopBroker
{
    private static string? LegacyHistoryDirectory(string shops)
    {
        var folder = new DirectoryInfo(shops);
        return folder.Name == "shops" && folder.Parent?.Name == "standalone" && folder.Parent.Parent is { } application
            ? Path.Combine(application.FullName, "history") : null;
    }

    private bool HistoryCanClear(bool ownsNativeGate = false, bool forceDispatch = false)
    {
        var current = runtime.Snapshot.Source;
        return !scan.Running && scanTask is not { IsCompleted: false } && activeNativeSource is null && !nativeHelperFlight.IsActive
            && (ownsNativeGate || nativeGate.CurrentCount == 1) && (forceDispatch || current is null || !IsBlocked(current));
    }

    private ShopCommandResponse ReadSearchedOwners(ShopCommandRequest req)
    {
        try
        {
            if (req.ExpectedHistoryRevision is { } revision && revision != searchedOwners.Revision)
                return State(req.RequestId, "HistoryChanged", "搜尋紀錄已更新；請重新載入第一頁。", false) with { TargetSnapshot = null, SavedScan = null };
            var page = searchedOwners.ReadOwners(req.HistoryAfterOwnerId, req.HistoryLimit);
            var rows = page.Owners; bool more = page.Truncated;
            // Keep this independent history page comfortably below the wire
            // limit, including long Unicode names/map names and source strings.
            while (rows.Length > 1 && JsonSerializer.SerializeToUtf8Bytes(rows, HubProtocol.Json).Length > 600_000)
            { rows = rows[..Math.Max(1, rows.Length / 2)]; more = true; }
            return State(req.RequestId, "SearchedOwners", "本機持久擺攤搜尋紀錄；同 ID 的買／賣頁分別去重，座標保留原觀測時間。", false)
                with { TargetSnapshot = null, SavedScan = null, SearchedOwners = rows,
                    HistoryTruncated = more, NextHistoryOwnerId = more && rows.Length > 0 ? rows[^1].OwnerId : null };
        }
        catch (Exception error) { return Rejected(req.RequestId, error) with { Listings = [], TargetSnapshot = null, SavedScan = null }; }
    }

    private async Task<ShopCommandResponse> ClearSearchedOwnersAsync(ShopCommandRequest req, CancellationToken token)
    {
        if (req.ForceDispatch) await AcquireForcePreparationGateAsync(token).ConfigureAwait(false);
        else if (!await nativeGate.WaitAsync(0, token).ConfigureAwait(false)) return State(req.RequestId, "Busy", "商店工作尚未結束；未清除搜尋紀錄。");
        try
        {
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (!HistoryCanClear(ownsNativeGate: true, forceDispatch: req.ForceDispatch)) return State(req.RequestId, "Busy", "目前來源有工作或未定安全鎖；未清除搜尋紀錄。");
                if (req.ExpectedHistoryRevision != searchedOwners.Revision)
                    return State(req.RequestId, "HistoryChanged", "搜尋紀錄已更新；未清除其他版本。");
                searchedOwners.Clear(req.ExpectedHistoryRevision!);
                Audit("SHOP_SEARCH_HISTORY_CLEARED", "已建立新的搜尋紀錄期別；商品、巡迴點、原生意圖與來源安全鎖全部保留。",
                    new { req.RequestId, Revision = searchedOwners.Revision });
                return State(req.RequestId, "SearchedOwnersCleared", "已清除本期擺攤 ID／座標去重紀錄；未解除任何來源未定請求。", false);
            }
        }
        finally { nativeGate.Release(); }
    }
}
