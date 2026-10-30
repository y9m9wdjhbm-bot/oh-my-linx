using MemoryHub;

namespace LcPlayerMarket;

internal sealed partial class MainForm
{
    private readonly Button clearSearchHistory = Button("清除擺攤搜尋紀錄");
    private bool clearingSearchHistory;

    private bool CanClearSearchHistory()
    {
        return SearchMutationAllowed && !clearingSearchHistory && CurrentSearchHistory is { } historyToClear
            && !string.IsNullOrWhiteSpace(historyToClear.Revision)
            && !string.IsNullOrWhiteSpace(historyToClear.Epoch);
    }

    private async Task ClearSearchHistoryAsync()
    {
        if (!CanClearSearchHistory() || CurrentSearchHistory is not { } historyToClear) return;
        clearingSearchHistory = true; actionBusy = ++activeActionRequests > 0; UpdateButtons();
        long resetGeneration = Volatile.Read(ref searchResetGeneration);
        try
        {
            bool confirmed = MessageBox.Show(this, ShopSearchHistoryPresentation.ClearPrompt(historyToClear, forceDispatch: true),
                "清除擺攤搜尋紀錄", MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) == DialogResult.OK;
            if (!confirmed || closing) return;
            Report("SEARCH_HISTORY_CLEAR_SEND_ONCE", "清除本機擺攤搜尋去重紀錄一次；保留商品、本次進度與未定操作。",
                new { historyToClear.Revision, historyToClear.Epoch, historyToClear.OwnerCount });
            var response = await ShopSearchHistoryPresentation.ClearAsync(historyToClear, confirmed, backend.SendAsync, lifetime.Token, forceDispatch: true);
            if (closing || response is null) return;
            MergeResponseCore(response, false, resetGeneration);
            Report("SEARCH_HISTORY_CLEARED", "已清除擺攤搜尋去重紀錄；可直接重新搜尋或開始新一輪巡迴。商品、本次進度與原操作結果紀錄保留。", response.SearchHistory);
        }
        catch (Exception error)
        {
            if (!closing) Report("SEARCH_HISTORY_CLEAR_UNCONFIRMED", "清除未完成或回覆無法核對；沒有自動重送。請重新讀取搜尋紀錄：" + error.Message);
        }
        finally { clearingSearchHistory = false; actionBusy = --activeActionRequests > 0; if (!closing) UpdateButtons(); }
    }
}
