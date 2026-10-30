using System.Globalization;
using MemoryHub;

namespace LcPlayerMarket;

internal sealed record SearchHistoryRecordRow(ulong OwnerId, string OwnerName, string Position, string MapName,
    string BuyStatus, string SellStatus, string ObservedAt, string LastSearchedAt);

/// <summary>Local history is not a source or dispatch lease. Backend CanClear owns native-work safety.</summary>
internal static class ShopSearchHistoryPresentation
{
    internal const int PageSize = 100;
    internal static bool CanClear(ShopSearchHistorySummary? history, bool uiIdle, bool recentState) =>
        uiIdle && recentState && history is { CanClear: true, ReadError: null }
        && !string.IsNullOrWhiteSpace(history.Revision) && !string.IsNullOrWhiteSpace(history.Epoch);

    internal static string Summary(ShopSearchHistorySummary? history) => history is null ? "持久搜尋紀錄尚未載入"
        : !string.IsNullOrWhiteSpace(history.ReadError) ? "持久搜尋紀錄讀取失敗：" + history.ReadError
        : $"已記錄 {history.OwnerCount} 位擺攤玩家；已搜尋 {history.SearchedPages} 頁（已確認 {history.ConfirmedPages}）；結果待確認 {history.UncertainPages} 頁。跨巡迴點、跨輪與重開程式保留。";

    internal static string ClearPrompt(ShopSearchHistorySummary history, bool forceDispatch = false) =>
        $"要清除 {history.OwnerCount} 位擺攤玩家的搜尋去重紀錄嗎？\n\n"
        + "清除後，按「重新搜尋」或開始新一輪巡迴，才會重新查詢可安全送出的頁面。\n"
        + "買／賣商品、巡迴點、本次保存進度與歷史檔案都會保留。\n"
        + (forceDispatch ? "原操作結果紀錄保留，仍可直接下達商店指令；舊計畫已略過的頁面不會自動重送。"
            : "操作結果未知的安全鎖不會解除；舊計畫已略過的頁面不會自動重送。");

    internal static ShopCommandRequest ReadRequest(ulong? after, string? revision) => new()
    {
        Command = ShopCommands.GetSearchedOwners, RequestId = Guid.NewGuid().ToString("N"),
        HistoryAfterOwnerId = after, HistoryLimit = PageSize, ExpectedHistoryRevision = revision
    };

    internal static ShopCommandRequest ClearRequest(ShopSearchHistorySummary history, bool forceDispatch = false) => new()
    {
        Command = ShopCommands.ClearSearchedOwners, RequestId = Guid.NewGuid().ToString("N"), ExpectedHistoryRevision = history.Revision,
        ForceDispatch = forceDispatch
    };

    internal static async Task<ShopCommandResponse?> ClearAsync(ShopSearchHistorySummary history, bool confirmed,
        Func<ShopCommandRequest, CancellationToken, Task<ShopCommandResponse>> send, CancellationToken token, bool forceDispatch = false)
    {
        token.ThrowIfCancellationRequested();
        if (!confirmed) return null;
        if (forceDispatch ? string.IsNullOrWhiteSpace(history.Revision) || string.IsNullOrWhiteSpace(history.Epoch)
            : !CanClear(history, true, true)) throw new InvalidOperationException("目前缺少清除搜尋紀錄所需的資料。");
        var request = ClearRequest(history, forceDispatch);
        var response = await send(request, token);
        ValidateReply(request, response);
        if (response.SearchHistory is not { ReadError: null } current || string.IsNullOrWhiteSpace(current.Revision)
            || string.IsNullOrWhiteSpace(current.Epoch) || current.Revision == history.Revision || current.Epoch == history.Epoch)
            throw new InvalidOperationException("清除回覆的新版紀錄無法核對；沒有自動重送，請重新讀取狀態。");
        return response;
    }

    internal static void ValidatePage(ShopCommandRequest request, ShopCommandResponse response)
    {
        ValidateReply(request, response);
        if (response.SearchHistory is not { } history || !string.IsNullOrWhiteSpace(history.ReadError)
            || string.IsNullOrWhiteSpace(history.Revision) || string.IsNullOrWhiteSpace(history.Epoch))
            throw new InvalidOperationException("未取得完整搜尋紀錄摘要；原顯示資料保留。");
        if (request.ExpectedHistoryRevision is { } expected && history.Revision != expected)
            throw new InvalidOperationException("搜尋紀錄已有更新，請按「重新載入」；未混用不同版本的分頁。");
        var rows = response.SearchedOwners;
        if (rows.Length > PageSize || rows.Any(r => r.OwnerId == 0)
            || rows.Select(r => r.OwnerId).Distinct().Count() != rows.Length
            || !rows.Select(r => r.OwnerId).SequenceEqual(rows.Select(r => r.OwnerId).Order()))
            throw new InvalidOperationException("搜尋紀錄分頁範圍或順序不符，未更新表格。");
        if (request.HistoryAfterOwnerId is { } cursor && rows.Any(r => r.OwnerId <= cursor))
            throw new InvalidOperationException("搜尋紀錄分頁游標未前進，未更新表格。");
        if (response.HistoryTruncated != response.NextHistoryOwnerId.HasValue
            || response.NextHistoryOwnerId is { } next && (rows.Length == 0 || rows[^1].OwnerId != next))
            throw new InvalidOperationException("搜尋紀錄下一頁游標無法核對，未更新表格。");
    }

    internal static void ValidateReply(ShopCommandRequest request, ShopCommandResponse response)
    {
        if (response.Schema != ShopCommands.Schema || response.RequestId != request.RequestId)
            throw new InvalidOperationException("搜尋紀錄回覆不屬於本次請求；沒有自動重送。");
        if (!string.IsNullOrWhiteSpace(response.FailureCode))
            throw new InvalidOperationException(response.Detail + "（" + response.FailureCode + "）");
        string expected = request.Command == ShopCommands.GetSearchedOwners ? "SearchedOwners" : "SearchedOwnersCleared";
        if (response.State != expected)
            throw new InvalidOperationException(response.State == "HistoryChanged" ? "搜尋紀錄已有更新，請重新載入後再操作；沒有自動重送。"
                : "搜尋紀錄操作未完成：" + response.Detail + "（" + response.State + "）");
    }

    internal static SearchHistoryRecordRow[] Rows(IEnumerable<ShopSearchedOwnerRecord> records) => records.Select(r => new SearchHistoryRecordRow(
        r.OwnerId, string.IsNullOrWhiteSpace(r.OwnerName) ? "（未取得名稱）" : r.OwnerName,
        r.Position is { } point ? $"{point.X}, {point.Y}" : "（未取得座標）", r.MapName ?? "（未取得地圖）",
        SavedSearchPresentation.PageState(r.BuyState), SavedSearchPresentation.PageState(r.SellState),
        Format(r.ObservedAt), Format(r.LastSearchedAt))).ToArray();

    private static string Format(DateTimeOffset at) => at == default ? "尚無時間" : at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
