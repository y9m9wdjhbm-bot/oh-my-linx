using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Exercises the same formatter as MainForm against recorded failure combinations, without a Form or IPC.</summary>
public static class FailurePresentationSelfTest
{
    public static OfflineCheck[] Run()
    {
        var checks = new List<OfflineCheck>();
        void Check(string name, Action assertion)
        {
            try { assertion(); checks.Add(new(name, true, "通過")); }
            catch (Exception error) { checks.Add(new(name, false, error.Message)); }
        }
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var oldScan = new MarketScanState
        {
            JobId = "preceding-scan", TotalShops = 60, Total = 120, Completed = 1, Failed = 1,
            StopReason = "NativeCodeUnavailable", FailureCode = "NativeCodeUnavailable", FailureRva = "0x70B5B0",
            FailureDetail = "PreviousNativeGuard: Native code is not currently readable/executable MEM_IMAGE: 0x70B5B0",
            Detail = "前一批已停止：PreviousNativeGuard 0x70B5B0"
        };
        // Formal V3 LOG 20260920-060804... line 611/612: current selfId disabled,
        // but the former formatter selected the previous scan's address/detail.
        var current = new ShopCommandResponse
        {
            RequestId = "manual-refresh-now", State = "Rejected", FailureCode = "ContextUnavailable", FailureScope = "Global",
            Detail = "selfId 欄位已停用；請自行啟用需要的讀取欄位後再操作。", Scan = oldScan
        };
        Check("current_failure_keeps_own_code_detail_and_missing_rva", () =>
        {
            var selected = MarketFailurePresentation.Select(current);
            Require(selected is { FromCurrentResponse: true, Code: "ContextUnavailable", Rva: null } && selected.Detail == current.Detail,
                "當前錯誤不得借用舊批次 RVA 或錯誤全文");
        });
        Check("real_selfid_rejection_does_not_display_previous_native_guard", () =>
        {
            string message = MarketFailurePresentation.Status(current);
            Require(message.Contains("selfId 欄位已停用") && message.Contains("ContextUnavailable")
                && message.Contains("上一批搜尋統計") && !message.Contains("0x70B5B0")
                && !message.Contains("NativeCodeUnavailable") && !message.Contains("PreviousNativeGuard"),
                "實際介面訊息仍混入上一批錯誤，或未標明舊統計");
        });
        Check("current_native_failure_uses_current_address_even_with_old_scan", () =>
        {
            var response = current with { State = "NativeRejected", FailureCode = "NativeCodeMismatch", FailureRva = "0x123450", Detail = "CurrentNativeGuard mismatch" };
            string message = MarketFailurePresentation.Status(response);
            Require(message.Contains("NativeCodeMismatch／0x123450") && message.Contains("CurrentNativeGuard mismatch")
                && !message.Contains("0x70B5B0") && !message.Contains("PreviousNativeGuard"), "新版原生錯誤被舊批次覆蓋");
        });
        Check("without_current_failure_selects_entire_scan_observation", () =>
        {
            var response = new ShopCommandResponse { State = "State", Detail = "一般查詢回覆", FailureRva = "orphan-current-rva", Scan = oldScan };
            var selected = MarketFailurePresentation.Select(response);
            string message = MarketFailurePresentation.Status(response);
            Require(selected is { FromCurrentResponse: false, Code: "NativeCodeUnavailable", Rva: "0x70B5B0" }
                && selected.Detail == oldScan.FailureDetail && message.Contains("PreviousNativeGuard")
                && !message.Contains("orphan-current-rva") && !message.Contains("一般查詢回覆"), "無當前錯誤時未整組採用批次錯誤");
        });
        Check("scan_without_failure_detail_uses_scan_detail_not_response_detail", () =>
        {
            var response = new ShopCommandResponse { State = "State", Detail = "不相干的查詢文字", Scan = oldScan with { FailureDetail = null } };
            Require(MarketFailurePresentation.Select(response)?.Detail == oldScan.Detail, "批次錯誤欠缺全文時用了不相關回覆");
        });
        Check("blank_current_error_is_absent_and_no_error_does_not_invent_one", () =>
        {
            Require(MarketFailurePresentation.Select(current with { FailureCode = "  " })?.FromCurrentResponse == false, "空白錯誤碼阻止有效批次原因");
            Require(MarketFailurePresentation.Format(new()) == "", "成功／無錯誤回覆被虛構成失敗");
        });
        Check("shop_state_rejection_does_not_claim_unknown_player_is_not_shopping", () =>
        {
            var response = current with { FailureCode = "TargetShopStateUnknown", Detail = "尚未確認擺攤狀態" };
            string message = MarketFailurePresentation.Status(response);
            Require(message.Contains("這不代表玩家未擺攤") && message.Contains("沒有送出開店")
                && !message.Contains("PreviousNativeGuard"), "未知狀態被誤報為沒有商店，或借用舊批次失敗原因");
        });
        Check("continued_single_page_does_not_claim_two_pages_per_shop", () =>
        {
            var singlePage = new MarketScanState { JobId = "continue-one-page", TotalShops = 1, CompletedShops = 1,
                Total = 1, Completed = 1, Succeeded = 1, Detail = "已接續剩餘賣出頁" };
            var continued = new ShopCommandResponse { State = "State", Scan = singlePage };
            string text = MarketFailurePresentation.Status(continued);
            Require(text.Contains("本輪商店 1/1") && text.Contains("本輪處理 1/1 頁（買／賣頁分別計數）") && text.Contains("此次查詢 1 頁")
                && !text.Contains("兩頁"), "單店只接續一頁，文案仍宣稱每店查詢兩頁。");
            string refreshed = MarketFailurePresentation.Status(continued with { State = "TargetsRefreshed", Detail = "已刷新周遭" });
            Require(refreshed.Contains("上一批搜尋統計（非本次刷新結果）") && !refreshed.Contains("本輪商店"),
                "刷新回覆把舊搜尋計數當成目前新一輪進度。");
        });
        Check("all_history_pages_are_processed_without_claiming_native_queries", () =>
        {
            var response = new ShopCommandResponse { State = "State", Scan = new()
                { JobId = "all-history", Total = 40, Completed = 40, SkippedHistoryPages = 40 } };
            string text = MarketFailurePresentation.Status(response);
            Require(text.Contains("本輪處理 40/40") && text.Contains("此次查詢 0 頁（確認 0、未讀到商品 0（方向未確認）、失敗或未確認 0）")
                && text.Contains("已記錄略過 40 頁（本輪未送出）"), "全歷史頁面被標成這次已送出或已確認。");
            string old = MarketFailurePresentation.Status(response with { State = "TargetsRefreshed", Detail = "刷新成功" });
            Require(old.Contains("非本次刷新結果") && old.Contains("此次查詢 0 頁") && old.Contains("已記錄略過 40"), "刷新混淆歷史略過計數。");
        });
        Check("mixed_history_and_actual_queries_keep_confirmation_counts_separate", () =>
        {
            string text = MarketFailurePresentation.Status(new() { State = "State", Scan = new()
                { Total = 20, Completed = 12, SkippedHistoryPages = 8, Succeeded = 3, Failed = 1 } });
            Require(text.Contains("本輪處理 12/20") && text.Contains("此次查詢 4 頁（確認 3、未讀到商品 0（方向未確認）、失敗或未確認 1）")
                && text.Contains("已記錄略過 8 頁（本輪未送出）"), "混合頁面計數將歷史略過併入原生查詢。");
        });
        Check("neutral_empty_observation_is_separate_from_confirmation_and_failure", () =>
        {
            string text = MarketFailurePresentation.Status(new() { State = "State", Scan = new()
                { Total = 4, Completed = 4, Succeeded = 3, EmptyObservedPages = 1, Failed = 0 } });
            Require(text.Contains("此次查詢 4 頁（確認 3、未讀到商品 1（方向未確認）、失敗或未確認 0）"),
                "未讀到商品被計成匹配回覆或失敗。");
        });
        return checks.ToArray();
    }
}

