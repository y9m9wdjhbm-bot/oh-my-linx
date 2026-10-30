using MemoryHub;

namespace LcPlayerMarket;

internal static class SearchOverlayPresentation
{
    internal static SearchOverlayView Build(ShopCommandResponse? response, bool confirmed,
        string unconfirmedReason, bool requestPending, string? unavailableReason = null, bool forceDispatch = false)
    {
        if (forceDispatch && !string.IsNullOrWhiteSpace(unavailableReason))
            return new("商店指令資料待就緒", "", "觀測待更新", unavailableReason, false);
        if (!confirmed || response is null)
            return forceDispatch
                ? new(requestPending ? "指令已送出" : "狀態觀測待更新", "", "進度觀測待更新",
                    "可直接下達下一指令。" + (string.IsNullOrWhiteSpace(unconfirmedReason) ? "" : unconfirmedReason), requestPending)
                : new("連線待確認", "", "進度待確認", string.IsNullOrWhiteSpace(unconfirmedReason)
                    ? "等待本機讀取器確認目前狀態。" : unconfirmedReason, false);

        var scan = response.Scan;
        bool current = !string.IsNullOrWhiteSpace(scan.SourceSessionKey)
            && scan.SourceSessionKey == response.CurrentSourceSessionKey;
        string progress = current ? Progress(scan) : "尚無目前批次進度";
        if (response.SessionBlocked && (response.FailureScope is "Session" or "Unknown"
            || current && scan.FailureScope is "Session" or "Unknown"))
            return new("原生工作待恢復", "", progress,
                "保留原請求並核對恢復狀態；尚未啟動下一個開頁元件。" + response.Detail, false);
        if (response.SessionBlocked && !forceDispatch)
            return new("已停止（操作結果待確認）", "", progress, "請查看主視窗的操作結果；不會自動重送。", false);
        if (requestPending)
            return forceDispatch
                ? new("指令已送出", "", progress, "回覆仍在更新；可直接下達下一指令。", true)
                : new("等待確認", "", progress, "請求已送出，等待本機讀取器確認。", true);
        if (response.SessionBlocked && forceDispatch)
            return new("上次操作結果未確認", "", progress, "保留原結果紀錄；可直接下達下一指令。", false);
        if (!scan.Running && !string.IsNullOrWhiteSpace(unavailableReason))
            return new("商店元件待更新", "", "搜尋尚未可用", "目前 LC 版本尚未相容；請更新商店查詢元件。", false);
        if (ShopNotEnteredPageRules.IsNotEnteredResponse(response))
            return new("本頁確定未執行", "", progress,
                "原生檢查在執行前拒絕；原頁未重送，尚未查得本頁商品。" + response.Detail, scan.Running);
        // A refusal keeps the preceding scan in the response for history. Its
        // completion must never become the result of this rejected request.
        bool currentRequestFailed = response.State == "Rejected"
            || !string.IsNullOrWhiteSpace(response.FailureCode) && response.State is not ("State" or "Stopping");
        if (currentRequestFailed)
        {
            bool rejectedBeforePublish = response.State == "Rejected" && response.FailurePhase == "BeforePublish"
                && response.WorkSettled && !scan.Running && !response.SessionBlocked
                && !response.NativeEntered && !response.NativeReturned && !response.ResponseVerified;
            string priorProgress = current && !string.IsNullOrWhiteSpace(scan.JobId)
                ? (scan.JobId == response.RequestId ? "本次批次進度：" : scan.Running ? "既有批次進度（非本次請求）：" : "上一批搜尋紀錄：") + Progress(scan)
                : "本次未取得新批次進度";
            string reason = string.IsNullOrWhiteSpace(response.Detail) ? "請查看主視窗的本次操作結果。" : response.Detail;
            if (!string.IsNullOrWhiteSpace(response.FailureCode))
                reason = "原因 " + response.FailureCode + (string.IsNullOrWhiteSpace(response.FailureRva) ? "" : "／" + response.FailureRva) + "：" + reason;
            return new(rejectedBeforePublish ? "本次請求未受理" : "本次請求未完成", "", priorProgress,
                (rejectedBeforePublish ? "本次請求已在發布前拒絕，沒有送出新的查詢；原進度保留。" : "本次請求沒有取得成功結果。") + reason,
                scan.Running || !response.WorkSettled || response.NativeEntered && !response.NativeReturned);
        }

        if (!current)
            return new("已停止", "", progress, "舊批次不代表目前搜尋狀態。", false);

        string player = "";
        if (scan.Running && scan.CurrentOwnerId is > 0)
        {
            ulong id = scan.CurrentOwnerId.Value;
            player = $"玩家 ID {id}";
            var target = response.TargetSnapshot;
            if (target is not null && !string.IsNullOrWhiteSpace(scan.TargetSnapshotId)
                && target.SourceSessionKey == scan.SourceSessionKey && target.SnapshotId == scan.TargetSnapshotId)
            {
                // Never choose an arbitrary name when the saved ID is ambiguous.
                var matches = target.Owners.Where(owner => owner.Id == id).Take(2).ToArray();
                if (matches.Length == 1 && !string.IsNullOrWhiteSpace(matches[0].Name))
                    player = matches[0].Name + $"（ID {id}）";
            }
        }
        string detail = $"此次查詢 {Math.Max(0L, (long)scan.Completed - scan.SkippedHistoryPages)} 頁（確認 {Math.Max(0, scan.Succeeded)}／未讀到商品 {Math.Max(0, scan.EmptyObservedPages)}（方向未確認）／失敗或未確認 {Math.Max(0, scan.Failed)}）"
            + (scan.NotEnteredPages>0 ? $"；確定未執行 {scan.NotEnteredPages}（原頁未重送）" : "")
            + $"；已記錄略過 {Math.Max(0, scan.SkippedHistoryPages)} 頁";
        if (scan.Running)
        {
            if (scan.Paused) return new("已暫停", player, progress, detail, false);
            if (scan.PauseRequested) return forceDispatch
                ? new("已要求暫停", player, progress, "可直接下達下一指令；" + detail, true)
                : new("正在暫停", player, progress, "等待目前頁面完成；" + detail, true);
            return new("正在搜尋", player, progress, detail, true);
        }
        bool finished = scan.Total > 0 && scan.Completed >= scan.Total && string.IsNullOrWhiteSpace(scan.StopReason);
        return new(finished ? (scan.NotEnteredPages>0 ? "搜尋處理完成（仍有未查頁）" : "搜尋完成") : "已停止", "", progress,
            detail + (string.IsNullOrWhiteSpace(scan.StopReason) ? "" : "；本批已停止，請查看主視窗說明。"), false);
    }

    private static string Progress(MarketScanState scan)
    {
        string percent = scan.Total > 0
            ? $"（{Math.Clamp(100d * Math.Max(0, scan.Completed) / scan.Total, 0, 100):0}%）" : "（尚無總頁數）";
        return $"處理頁面 {Math.Max(0, scan.Completed)}/{Math.Max(0, scan.Total)} {percent}｜商店 {Math.Max(0, scan.CompletedShops)}/{Math.Max(0, scan.TotalShops)}";
    }
}

