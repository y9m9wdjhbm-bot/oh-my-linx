using MemoryHub;

namespace LcPlayerMarket;

internal static class SavedSearchPresentationSelfTest
{
    internal static OfflineCheck[] Run()
    {
        var checks = new List<OfflineCheck>();
        void Check(string name, Action action)
        {
            try { action(); checks.Add(new(name, true, "通過")); }
            catch (Exception error) { checks.Add(new(name, false, error.Message)); }
        }
        void Need(bool ok, string detail) { if (!ok) throw new InvalidOperationException(detail); }
        var at = DateTimeOffset.Parse("2026-09-26T12:00:00Z");
        var saved = new ShopSavedScanProgress
        {
            PlanId = "saved-plan", SourceSessionKey = "current-source", CreatedAt = at, UpdatedAt = at,
            TotalPages = 4, SearchedPages = 2, ConfirmedPages = 1, RemainingPages = 2, CanContinue = true,
            Owners = [new() { OwnerId = 17, OwnerName = "測試店主", BuyState = "Confirmed", SellState = "Sent",
                BuyDetail = "清單已核對", SellDetail = "已返回，清單尚未核對", UpdatedAt = at },
                new() { OwnerId = 18, OwnerName = "", BuyState = "NotSent", SellState = "NotSent", UpdatedAt = at }]
        };
        SavedSearchControls Controls(ShopSavedScanProgress? value, string? source = "current-source",
            bool ready = true, bool dispatch = true, bool running = false, string reason = "") =>
            SavedSearchPresentation.Controls(value, source, ready, dispatch, running, reason);
        Check("saved_search_no_plan_allows_restart_only", () =>
        {
            var result = Controls(null);
            Need(result.CanRestart && !result.CanContinue && !result.CanViewRecords, "無進度時不應顯示接續資格。");
        });
        Check("saved_search_authorized_partial_plan_can_continue", () =>
        {
            var result = Controls(saved);
            Need(result.CanRestart && result.CanContinue && result.CanViewRecords && result.ContinueReason.Length == 0,
                "目前來源與刷新資格完整時，剩餘頁面應可接續。");
        });
        Check("saved_search_restored_plan_cannot_restore_refresh_authorization", () =>
        {
            var result = Controls(saved, dispatch: false, reason: "請先手動刷新周遭");
            Need(!result.CanRestart && !result.CanContinue && result.CanViewRecords && result.ContinueReason == "請先手動刷新周遭",
                "保存進度自行恢復了本視窗的搜尋資格。");
        });
        Check("saved_search_active_or_paused_job_blocks_new_requests", () =>
        {
            var result = Controls(saved, running: true);
            Need(!result.CanRestart && !result.CanContinue && result.CanViewRecords
                && result.ContinueReason.Contains("繼續暫停工作"), "暫停的原工作被當成可另開批次。");
        });
        Check("saved_search_busy_controls_do_not_send_another_request", () =>
        {
            var result = Controls(saved, ready: false);
            Need(!result.CanRestart && !result.CanContinue && result.CanViewRecords, "請求等待時仍可接續／重開。");
        });
        Check("saved_search_source_change_keeps_records_without_reusing_old_ids", () =>
        {
            var result = Controls(saved, source: "new-source");
            Need(result.CanRestart && !result.CanContinue && result.CanViewRecords
                && result.ContinueReason.Contains("LC 來源"), "不同來源沿用了舊進度資格。");
            Need(!Controls(saved, source: null).CanContinue, "尚未確認來源卻可接續。");
        });
        Check("saved_search_uncertain_current_page_cannot_be_reset_or_continued", () =>
        {
            var value = saved with { UncertainPages = 1, CanContinue = true };
            var result = Controls(value);
            Need(!result.CanRestart && !result.CanContinue && result.CanViewRecords, "不確定操作可藉重開／接續送出。");
            Need(Controls(value, source: "new-source").CanRestart, "舊 LC 不確定記錄誤鎖已確認的新來源。");
        });
        Check("saved_search_all_sent_pages_explain_clear_before_repeat", () =>
        {
            var result = Controls(saved with { SearchedPages = 4, RemainingPages = 0 });
            Need(result.CanRestart && !result.CanContinue && result.ContinueReason.Contains("清除擺攤搜尋紀錄") && result.ContinueReason.Contains("仍會略過"),
                "已送出的全部頁面被接續重送。");
        });
        Check("saved_search_broker_block_reason_is_preserved", () =>
        {
            var result = Controls(saved with { CanContinue = false, ContinueBlockReason = "剩餘店主目前不在周遭" });
            Need(!result.CanContinue && result.ContinueReason == "剩餘店主目前不在周遭", "後端停用原因被掩蓋。");
        });
        Check("saved_search_summary_separates_sent_confirmed_and_uncertain", () =>
        {
            string text = SavedSearchPresentation.Summary(saved with { UncertainPages = 1 }, "current-source");
            Need(text.Contains("已搜尋 2/4") && text.Contains("清單已確認 1") && text.Contains("已送未確認 1")
                && text.Contains("待查 2") && text.Contains("操作結果待確認 1"), "摘要混淆已送出／已確認／未送出。");
            Need(SavedSearchPresentation.Summary(saved, "other").Contains("舊 LC 來源"), "舊記錄被標成目前進度。");
            Need(SavedSearchPresentation.Summary(null, null).Contains("尚無"), "沒有進度卻宣稱完成。");
        });
        Check("saved_search_record_rows_preserve_id_name_and_distinct_page_states", () =>
        {
            var rows = SavedSearchPresentation.Rows(saved);
            Need(rows.Length == 2 && rows[0].OwnerId == 17 && rows[0].OwnerName == "測試店主"
                && rows[0].BuyStatus == "已搜尋（清單已確認）" && rows[0].SellStatus == "已搜尋（清單未確認）"
                && rows[0].BuyDetail == "清單已核對" && rows[1].BuyStatus == "待查（尚未送出）"
                && rows[1].OwnerName == "（未取得名稱）", "玩家或買賣頁狀態顯示失真。");
            Need(SavedSearchPresentation.Rows(null).Length == 0, "空進度產生虛構玩家。");
        });
        Check("saved_search_uncertain_and_unknown_states_are_not_completed", () =>
        {
            Need(SavedSearchPresentation.PageState("Uncertain") == "操作結果待確認"
                && SavedSearchPresentation.PageState("unexpected") == "記錄狀態不明", "未知狀態誤標成已搜尋。");
            Need(saved.RemainingPages == 2 && saved.Owners[1].BuyState == "NotSent", "顯示進度時修改了保存資料。");
        });
        Check("saved_search_history_skipped_and_deferred_are_not_native_completion", () =>
        {
            var value = saved with { TotalPages = 8, AvailablePages = 0, DeferredPages = 2, HistorySkippedPages = 4 };
            string text = SavedSearchPresentation.Summary(value, "current-source");
            Need(text.Contains("已搜尋 2/8") && text.Contains("已記錄略過 4") && text.Contains("本輪未送出")
                && text.Contains("暫不在場 2"), "略過或暫不在場頁面被標成已送出。");
            Need(SavedSearchPresentation.PageState("HistorySkipped") == "已記錄略過（本輪未送出）", "略過頁面顯示錯誤。");
        });
        return checks.ToArray();
    }
}
