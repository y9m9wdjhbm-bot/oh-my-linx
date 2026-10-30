using System.Globalization;
using MemoryHub;

namespace LcPlayerMarket;

internal sealed record SavedSearchControls(bool CanRestart, bool CanContinue, bool CanViewRecords, string ContinueReason);
internal sealed record SavedSearchRecordRow(ulong OwnerId, string OwnerName, string BuyStatus, string SellStatus,
    string BuyDetail, string SellDetail, string UpdatedAt);

/// <summary>Pure presentation; saved progress never grants target dispatch authorization.</summary>
internal static class SavedSearchPresentation
{
    internal static SavedSearchControls Controls(ShopSavedScanProgress? saved, string? currentSource,
        bool controlReady, bool dispatchReady, bool running, string dispatchBlockReason, bool forceDispatch = false)
    {
        if (forceDispatch)
        {
            bool ready = controlReady && dispatchReady;
            string forceReason = saved is null || string.IsNullOrWhiteSpace(saved.PlanId)
                ? "尚無可接續的搜尋記錄；請先重新搜尋。"
                : string.IsNullOrWhiteSpace(currentSource) || saved.SourceSessionKey != currentSource
                    ? "保存進度屬於其他 LC 來源，不能接續舊玩家 ID。"
                    : !dispatchReady ? dispatchBlockReason
                    : !controlReady ? "視窗正在關閉，無法送出指令。"
                    : saved.RemainingPages <= 0 ? "本次沒有待查頁；重新搜尋仍會略過已記錄玩家，重查前請清除擺攤搜尋紀錄。"
                    : "";
            return new(ready, ready && forceReason.Length == 0, saved is not null, forceReason);
        }
        bool restart = controlReady && dispatchReady && !running
            && !(saved is not null && saved.SourceSessionKey == currentSource && saved.UncertainPages > 0);
        string reason;
        if (saved is null || string.IsNullOrWhiteSpace(saved.PlanId)) reason = "尚無可接續的搜尋記錄；請先重新搜尋。";
        else if (running) reason = "搜尋工作仍在進行；暫停後請使用「繼續暫停工作」。";
        else if (string.IsNullOrWhiteSpace(currentSource) || saved.SourceSessionKey != currentSource)
            reason = "保存進度屬於其他或尚未確認的 LC 來源，不能接續舊玩家 ID。";
        else if (saved.UncertainPages > 0) reason = "有操作結果待確認的頁面，停止接續；請檢查搜尋記錄與 LOG。";
        else if (!dispatchReady) reason = string.IsNullOrWhiteSpace(dispatchBlockReason)
            ? "請先在本視窗成功手動刷新周遭，再接續搜尋。" : dispatchBlockReason;
        else if (!controlReady) reason = "請等目前請求完成，確認搜尋狀態後再接續。";
        else if (saved.RemainingPages <= 0) reason = "本次沒有待查頁；重新搜尋仍會略過已記錄玩家，重查前請按「清除擺攤搜尋紀錄」。";
        else if (!saved.CanContinue) reason = string.IsNullOrWhiteSpace(saved.ContinueBlockReason)
            ? "目前尚未確認剩餘頁面可以接續；請手動刷新周遭。" : saved.ContinueBlockReason;
        else reason = "";
        return new(restart, restart && reason.Length == 0, saved is not null, reason);
    }

    internal static string Summary(ShopSavedScanProgress? saved, string? currentSource)
    {
        if (saved is null) return "尚無保存的搜尋進度";
        string source = string.IsNullOrWhiteSpace(currentSource) ? "（目前來源尚未確認）"
            : saved.SourceSessionKey == currentSource ? "" : "（舊 LC 來源）";
        int searched = Math.Max(0, saved.SearchedPages), confirmed = Math.Max(0, saved.ConfirmedPages);
        return $"保存進度{source}：已搜尋 {searched}/{Math.Max(0, saved.TotalPages)} 頁"
            + $"（清單已確認 {confirmed}、已送未確認 {Math.Max(0, searched - confirmed)}）"
            + $"；待查 {Math.Max(0, saved.RemainingPages)} 頁（目前可查 {Math.Max(0, saved.AvailablePages)}、暫不在場 {Math.Max(0, saved.DeferredPages)}）"
            + $"；已記錄略過 {Math.Max(0, saved.HistorySkippedPages)} 頁（本輪未送出）；操作結果待確認 {Math.Max(0, saved.UncertainPages)} 頁";
    }

    internal static SavedSearchRecordRow[] Rows(ShopSavedScanProgress? saved) => saved?.Owners?.Select(owner => new SavedSearchRecordRow(owner.OwnerId,
            string.IsNullOrWhiteSpace(owner.OwnerName) ? "（未取得名稱）" : owner.OwnerName,
            PageState(owner.BuyState), PageState(owner.SellState), owner.BuyDetail, owner.SellDetail,
            owner.UpdatedAt == default ? "尚無更新時間" : owner.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))
        .ToArray() ?? [];

    internal static string PageState(string? state) => state switch
    {
        "NotSent" => "待查（尚未送出）",
        "Sent" => "已搜尋（清單未確認）",
        "Confirmed" => "已搜尋（清單已確認）",
        "Uncertain" => "操作結果待確認",
        "ManualReleased" => "已手動解鎖（原結果仍未知）",
        "HistorySkipped" => "已記錄略過（本輪未送出）",
        _ => "記錄狀態不明"
    };
}
