using MemoryHub;

namespace LcPlayerMarket;

internal static class ShopSearchHistoryPresentationSelfTest
{
    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        void Check(string name, Action action)
        { try { action(); checks.Add(new(name, true, "通過")); } catch (Exception e) { checks.Add(new(name, false, e.Message)); } }
        async Task CheckAsync(string name, Func<Task> action)
        { try { await action(); checks.Add(new(name, true, "通過")); } catch (Exception e) { checks.Add(new(name, false, e.Message)); } }
        static void Need(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
        static void Reject(Action action)
        { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("應拒絕此回覆。"); }
        var history = new ShopSearchHistorySummary { Revision = "revision-a", Epoch = "epoch-a", OwnerCount = 120,
            SearchedPages = 30, ConfirmedPages = 25, UncertainPages = 4, CanClear = true };
        var first = ShopSearchHistoryPresentation.ReadRequest(null, null);
        ShopCommandResponse Page(ShopCommandRequest request) => new() { RequestId = request.RequestId, State = "SearchedOwners", SearchHistory = history,
            SearchedOwners = [new() { OwnerId = 12, OwnerName = "測試擺攤", Position = new(32001, 32002), MapName = "市場",
                BuyState = "Confirmed", SellState = "Uncertain" }, new() { OwnerId = 18 }], HistoryTruncated = true, NextHistoryOwnerId = 18 };
        Check("history_clear_old_uncertain_does_not_override_backend_eligibility", () =>
            Need(ShopSearchHistoryPresentation.CanClear(history, true, true), "舊來源未定頁不應覆蓋後端清除資格。"));
        Check("history_clear_requires_ui_idle_recent_state_backend_permission", () =>
        {
            Need(!ShopSearchHistoryPresentation.CanClear(history, false, true) && !ShopSearchHistoryPresentation.CanClear(history, true, false)
                && !ShopSearchHistoryPresentation.CanClear(history with { CanClear = false }, true, true), "忙碌或未確認仍可清除。");
            Need(!ShopSearchHistoryPresentation.CanClear(null, true, true)
                && !ShopSearchHistoryPresentation.CanClear(history with { ReadError = "unreadable" }, true, true)
                && !ShopSearchHistoryPresentation.CanClear(history with { Revision = "" }, true, true), "損壞／未知紀錄可清除。");
        });
        Check("history_clear_prompt_preserves_products_plan_and_unknown_locks", () =>
        {
            string text = ShopSearchHistoryPresentation.ClearPrompt(history);
            Need(text.Contains("120") && text.Contains("重新搜尋") && text.Contains("巡迴點") && text.Contains("本次保存進度")
                && text.Contains("安全鎖不會解除") && text.Contains("不會自動重送"), "清除確認說明不完整。");
        });
        Check("history_read_requests_are_bounded_and_revision_fenced", () =>
        {
            var next = ShopSearchHistoryPresentation.ReadRequest(18, history.Revision);
            Need(first.Command == ShopCommands.GetSearchedOwners && first.HistoryLimit == 100 && first.HistoryAfterOwnerId is null
                && next.HistoryAfterOwnerId == 18 && next.ExpectedHistoryRevision == history.Revision
                && first.RequestId != next.RequestId && Guid.TryParseExact(next.RequestId, "N", out _), "分頁沒有邊界或重用了請求 ID。");
            ShopSearchHistoryPresentation.ValidatePage(first, Page(first));
        });
        Check("history_page_rejects_stale_revision_or_wrong_request", () =>
        {
            var next = ShopSearchHistoryPresentation.ReadRequest(18, "old-revision");
            Reject(() => ShopSearchHistoryPresentation.ValidatePage(next, Page(next)));
            Reject(() => ShopSearchHistoryPresentation.ValidatePage(first, Page(first) with { RequestId = "foreign" }));
            Reject(() => ShopSearchHistoryPresentation.ValidatePage(first, Page(first) with { State = "HistoryChanged" }));
        });
        Check("history_page_rejects_unbounded_duplicate_or_backwards_rows", () =>
        {
            Reject(() => ShopSearchHistoryPresentation.ValidatePage(first, Page(first) with { SearchedOwners = Enumerable.Range(1, 101).Select(i => new ShopSearchedOwnerRecord { OwnerId = (ulong)i }).ToArray() }));
            Reject(() => ShopSearchHistoryPresentation.ValidatePage(first, Page(first) with { SearchedOwners = [new() { OwnerId = 18 }, new() { OwnerId = 18 }] }));
            Reject(() => ShopSearchHistoryPresentation.ValidatePage(first, Page(first) with { SearchedOwners = [new() { OwnerId = 18 }, new() { OwnerId = 12 }] }));
            var next = ShopSearchHistoryPresentation.ReadRequest(18, history.Revision);
            Reject(() => ShopSearchHistoryPresentation.ValidatePage(next, Page(next)));
        });
        Check("history_page_rejects_inconsistent_cursor_or_read_error", () =>
        {
            Reject(() => ShopSearchHistoryPresentation.ValidatePage(first, Page(first) with { NextHistoryOwnerId = 99 }));
            Reject(() => ShopSearchHistoryPresentation.ValidatePage(first, Page(first) with { HistoryTruncated = false }));
            Reject(() => ShopSearchHistoryPresentation.ValidatePage(first, Page(first) with { SearchHistory = history with { ReadError = "failed" } }));
        });
        Check("history_rows_preserve_recorded_coordinates_and_distinct_page_states", () =>
        {
            var rows = ShopSearchHistoryPresentation.Rows(Page(first).SearchedOwners);
            Need(rows[0].OwnerId == 12 && rows[0].Position == "32001, 32002" && rows[0].MapName == "市場"
                && rows[0].BuyStatus.Contains("已確認") && rows[0].SellStatus == "操作結果待確認"
                && rows[1].Position == "（未取得座標）", "座標或頁狀態被遺失／虛構。");
        });
        Check("history_summary_distinguishes_confirmed_and_unknown", () =>
        {
            string text = ShopSearchHistoryPresentation.Summary(history);
            Need(text.Contains("120 位") && text.Contains("已搜尋 30") && text.Contains("已確認 25") && text.Contains("待確認 4")
                && ShopSearchHistoryPresentation.Summary(null).Contains("尚未載入"), "摘要將未定頁算成已確認。");
        });
        await CheckAsync("history_clear_cancel_sends_zero_requests", async () =>
        {
            int sends = 0;
            var result = await ShopSearchHistoryPresentation.ClearAsync(history, false, (request, token) => { sends++; throw new Exception("不得送出"); }, default);
            Need(result is null && sends == 0, "取消確認仍送出清除。");
        });
        await CheckAsync("history_clear_precancel_sends_zero_requests", async () =>
        {
            int sends = 0;
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await ShopSearchHistoryPresentation.ClearAsync(history, true, (request, token) => { sends++; throw new Exception("不得送出"); }, cancellation.Token); }
            catch (OperationCanceledException) { Need(sends == 0, "取消後仍送出。"); return; }
            throw new Exception("未尊重取消。");
        });
        await CheckAsync("history_clear_confirmed_sends_once_and_requires_new_epoch", async () =>
        {
            int sends = 0;
            var result = await ShopSearchHistoryPresentation.ClearAsync(history, true, (request, token) =>
            {
                sends++; Need(request.Command == ShopCommands.ClearSearchedOwners && request.ExpectedHistoryRevision == history.Revision
                    && Guid.TryParseExact(request.RequestId, "N", out _) && request.ExpectedSourceSessionKey == "", "清除要求錯誤或無故綁 LC 來源。");
                return Task.FromResult(new ShopCommandResponse { RequestId = request.RequestId, State = "SearchedOwnersCleared",
                    SearchHistory = history with { Revision = "revision-b", Epoch = "epoch-b", OwnerCount = 0 } });
            }, default);
            Need(sends == 1 && result?.SearchHistory?.Epoch == "epoch-b" && history.OwnerCount == 120, "清除不只送一次或先改舊摘要。");
        });
        await CheckAsync("history_clear_rejected_reply_does_not_retry_or_clear_local_summary", async () =>
        {
            foreach (string state in new[] { "HistoryChanged", "Busy", "Rejected", "SearchedOwners" })
            {
                int sends = 0;
                try { await ShopSearchHistoryPresentation.ClearAsync(history, true, (request, token) => { sends++; return Task.FromResult(new ShopCommandResponse { RequestId = request.RequestId, State = state, SearchHistory = history }); }, default); }
                catch (InvalidOperationException) { Need(sends == 1 && history.OwnerCount == 120, "拒絕後重送或修改摘要。"); continue; }
                throw new Exception("拒絕回覆誤判成功：" + state);
            }
        });
        await CheckAsync("history_clear_unknown_reply_and_unchanged_epoch_are_not_success", async () =>
        {
            foreach (bool wrongId in new[] { true, false })
            {
                int sends = 0;
                try { await ShopSearchHistoryPresentation.ClearAsync(history, true, (request, token) => { sends++; return Task.FromResult(new ShopCommandResponse { RequestId = wrongId ? "wrong" : request.RequestId, State = "SearchedOwnersCleared", SearchHistory = history }); }, default); }
                catch (InvalidOperationException) { Need(sends == 1, "不明回覆重送。"); continue; }
                throw new Exception("不明清除回覆誤判成功。");
            }
        });
        return checks.ToArray();
    }
}
