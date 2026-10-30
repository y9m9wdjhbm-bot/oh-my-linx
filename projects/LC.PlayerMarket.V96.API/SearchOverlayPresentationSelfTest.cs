using MemoryHub;

namespace LcPlayerMarket;

internal static class SearchOverlayPresentationSelfTest
{
    internal static IReadOnlyList<OfflineCheck> Run()
    {
        var results = new List<OfflineCheck>();
        var scan = new MarketScanState { SourceSessionKey = "current", TargetSnapshotId = "saved",
            Running = true, CurrentOwnerId = 22, Total = 20, Completed = 5, TotalShops = 10, CompletedShops = 2, Succeeded = 4, Failed = 1 };
        var response = new ShopCommandResponse { CurrentSourceSessionKey = "current", Scan = scan,
            TargetSnapshot = new() { SourceSessionKey = "current", SnapshotId = "saved", Owners = [new() { Id = 22, Name = "測試店主" }] } };
        SearchOverlayView Build(ShopCommandResponse? r, bool confirmed = true, bool pending = false) => SearchOverlayPresentation.Build(r, confirmed, "等待連線確認", pending);
        void Check(string name, Action action)
        { try { action(); results.Add(new(name, true, "通過")); } catch (Exception error) { results.Add(new(name, false, error.Message)); } }
        static void Need(bool value) { if (!value) throw new InvalidOperationException("HUD 狀態或來源對應不符"); }

        Check("overlay_unconfirmed_does_not_replay_old_running_player", () =>
        { var v = Build(response, false); Need(v.State == "連線待確認" && v.Player == "" && !v.Busy && !v.Progress.Contains("25%")); Need(Build(null).State == "連線待確認"); });
        Check("overlay_old_source_is_not_current_work", () =>
        { var v = Build(response with { CurrentSourceSessionKey = "new-source" }); Need(v.State == "已停止" && v.Player == "" && !v.Busy && !v.Progress.Contains("5/20")); });
        Check("overlay_paused_keeps_confirmed_target_and_page_shop_progress", () =>
        { var v = Build(response with { Scan = scan with { Paused = true } }); Need(v.State == "已暫停" && v.Player.Contains("測試店主") && !v.Busy && v.Progress.Contains("5/20") && v.Progress.Contains("2/10") && v.Progress.Contains("25%")); });
        Check("overlay_mismatched_target_snapshot_never_supplies_name", () =>
        { var v = Build(response with { TargetSnapshot = response.TargetSnapshot! with { SnapshotId = "old" } }); Need(v.Player == "玩家 ID 22"); var other = Build(response with { TargetSnapshot = response.TargetSnapshot! with { SourceSessionKey = "old" } }); Need(other.Player == "玩家 ID 22"); });
        Check("overlay_missing_current_owner_does_not_reuse_previous_name", () =>
        { Need(Build(response with { Scan = scan with { CurrentOwnerId = null } }).Player == ""); Need(Build(response with { TargetSnapshot = null }).Player == "玩家 ID 22"); });
        Check("overlay_blocked_overrides_running_and_pending", () =>
        { var v = Build(response with { SessionBlocked = true }, pending: true); Need(v.State == "已停止（操作結果待確認）" && v.Player == "" && !v.Busy); });
        Check("overlay_zero_total_has_no_fake_completion_and_percent_is_bounded", () =>
        { var v = Build(response with { Scan = scan with { Running = false, Total = 0, Completed = 0 } }); Need(v.State == "已停止" && !v.Progress.Contains("100%")); Need(Build(response with { Scan = scan with { Completed = int.MaxValue } }).Progress.Contains("100%")); });
        Check("overlay_pending_pause_running_completed_and_stopped_are_distinct", () =>
        {
            Need(Build(response, pending: true).State == "等待確認");
            Need(Build(response).State == "正在搜尋");
            Need(Build(response with { Scan = scan with { PauseRequested = true } }).State == "正在暫停");
            var done = response with { Scan = scan with { Running = false, Completed = 20 } };
            Need(Build(done).State == "搜尋完成" && Build(done).Player == "");
            Need(Build(done with { Scan = done.Scan with { StopReason = "InternalFailureCode" } }).State == "已停止"
                && !Build(done with { Scan = done.Scan with { StopReason = "InternalFailureCode" } }).Detail.Contains("InternalFailureCode"));
        });
        Check("overlay_incompatible_build_shows_update_without_false_progress", () =>
        {
            var stopped = response with { Scan = scan with { Running = false, StopReason = "ContextUnavailable" } };
            var view = SearchOverlayPresentation.Build(stopped, true, "", false, "目前 LC 尚未相容");
            Need(view.State == "商店元件待更新" && view.Progress == "搜尋尚未可用" && view.Player == "" && !view.Busy);
        });
        Check("overlay_compatibility_does_not_hide_active_or_unresolved_operation", () =>
        {
            Need(SearchOverlayPresentation.Build(response, true, "", false, "尚未相容").State == "正在搜尋");
            Need(SearchOverlayPresentation.Build(response with { SessionBlocked = true }, true, "", false, "尚未相容").State
                == "已停止（操作結果待確認）");
            Need(SearchOverlayPresentation.Build(response, false, "等待連線", false, "尚未相容").State == "連線待確認");
        });
        Check("overlay_all_history_is_processed_with_zero_actual_queries", () =>
        {
            var value = Build(response with { Scan = scan with { Running = false, Completed = 20, SkippedHistoryPages = 20, Succeeded = 0, Failed = 0 } });
            Need(value.Progress.Contains("處理頁面 20/20") && value.Detail.Contains("此次查詢 0 頁（確認 0／未讀到商品 0（方向未確認）／失敗或未確認 0）")
                && value.Detail.Contains("已記錄略過 20 頁"));
        });
        Check("overlay_mixed_history_does_not_increase_native_confirmations", () =>
        {
            var value = Build(response with { Scan = scan with { Completed = 12, SkippedHistoryPages = 8, Succeeded = 3, Failed = 1 } });
            Need(value.Detail.Contains("此次查詢 4 頁（確認 3／未讀到商品 0（方向未確認）／失敗或未確認 1）") && value.Detail.Contains("已記錄略過 8 頁"));
        });
        Check("overlay_neutral_empty_observation_is_not_confirmation_or_failure", () =>
        {
            var value = Build(response with { Scan = scan with
                { Total = 4, Completed = 4, Succeeded = 3, EmptyObservedPages = 1, Failed = 0 } });
            Need(value.Detail.Contains("此次查詢 4 頁（確認 3／未讀到商品 1（方向未確認）／失敗或未確認 0）"));
        });
        results.AddRange(SearchOverlayCurrentRequestVerification.Run());
        return results;
    }
}

