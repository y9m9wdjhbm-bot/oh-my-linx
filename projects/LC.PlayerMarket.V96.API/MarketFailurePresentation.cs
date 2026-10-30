using MemoryHub;

namespace LcPlayerMarket;

internal sealed record MarketFailureDescription(string Code, string? Rva, string Detail, bool FromCurrentResponse);

/// <summary>Selects code, address and detail as one observation, never mixing two requests.</summary>
internal static class MarketFailurePresentation
{
    internal static MarketFailureDescription? Select(ShopCommandResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.FailureCode))
            return new(response.FailureCode, response.FailureRva, response.Detail, true);
        if (!string.IsNullOrWhiteSpace(response.Scan.FailureCode))
            return new(response.Scan.FailureCode, response.Scan.FailureRva,
                !string.IsNullOrWhiteSpace(response.Scan.FailureDetail) ? response.Scan.FailureDetail : response.Scan.Detail, false);
        return null;
    }

    internal static string Format(ShopCommandResponse response, bool forceDispatch = false)
    {
        var failure = Select(response);
        if (failure is null) return "";
        bool canContinue = forceDispatch && MaySuggestNextCommand(response);
        string title = failure.Code switch
        {
            ShopNotEnteredPageRules.FailureCode when failure.FromCurrentResponse && ShopNotEnteredPageRules.IsNotEnteredResponse(response)
                => "已核對原生檢查在執行前拒絕，本頁確定未執行（原頁未重送）；子檢查未知，這不代表商店沒有商品。",
            ShopNotEnteredPageRules.FailureCode
                => "本頁的未執行收據欄位不一致，結果仍未確認；保留原原因，不把本頁當作已查商品。",
            "NativeHelperMissing" => "本機讀取器的商店查詢元件缺少，尚未送出查詢。請使用包含相容商店元件的完整本機讀取器發布資料夾。",
            "NativeHelperMismatch" => "本機讀取器的商店元件檔案不完整或版本不符，尚未送出查詢。請重新使用完整發布資料夾。",
            "NativeBuildUnsupported" => "目前 LC 不符合商店元件支援版本，尚未送出查詢；需要重新更新相容元件。",
            "ContextUnavailable" when failure.Detail.Contains("尚未發布 ShopControl/LC_PlayerShopListActionV10.exe", StringComparison.Ordinal)
                => "本機讀取器的商店查詢元件缺少，本頁尚未送出。需安裝相容目前 LC 版本的完整商店查詢元件；手動刷新周遭無法補齊此元件。",
            "ContextUnavailable" when failure.Detail.Contains("Current full 64-bit shop owner changed before dispatch", StringComparison.Ordinal)
                => "商店頁面店主在預檢期間改變；尚未取得完整未派送證據，原請求保留，需先核對原始紀錄。",
            "NativePreflightOwnerChanged" => "送出前商店頁面店主已改變；已核對本頁未派送且清理完成，本次搜尋停止，可重新開始。",
            "NativeInitializationRequired" => "商店查詢入口目前尚未就緒。請在遊戲中正常開啟一家玩家商店，再按「重新檢查商店入口」；檢查通過後可重新開始。",
            "NativeCodeUnavailable" when response.SessionBlocked || !response.WorkSettled
                => "LC 開店入口目前無法讀取，原請求結果仍未確認；保留原請求與先前讀到的商品，需先核對原始紀錄。這不代表商店沒有商品。",
            "NativeCodeUnavailable" when canContinue => "本次 LC 開店入口無法讀取，本次查詢未完成；先前商品保留，請先按「重新檢查商店入口」確認原因。這不代表商店沒有商品。",
            "NativeCodeUnavailable" => "LC 開店入口目前無法讀取，本次查詢未完成，搜尋已停止；先前讀到的商品仍保留。這不代表商店沒有商品。",
            "SourceUnavailable" when canContinue => "本次 LC 資料尚未取得；先前資料保留，可直接下達下一指令。",
            "SourceUnavailable" => "LC 資料暫時無法確認，搜尋已停止；先前讀到的商品仍保留。",
            "ActorCollectionChanged" => "周遭角色持續變動，重新採樣仍無法核對；本頁未送出並記為未確認，整批搜尋可繼續後續頁面。這不代表商店沒有商品。",
            "NativeCodeMismatch" => "自動查詢尚未送出：目前程式內容與已核對版本不符，需重新確認相容性；不能判定商店沒有商品。",
            "ShopStateReadingDisabled" => "玩家擺攤狀態讀取已停用；請重新啟動程式後手動刷新周遭。",
            "TargetRefreshRequired" when canContinue => "本次請求的名單資料未能對應，尚未送出；保留商品與座標，可直接下達刷新或下一商店指令。",
            "TargetRefreshRequired" => "目前名單不能用於搜尋或開店，尚未送出；請重新成功手動刷新周遭。保存商品與座標仍保留。",
            "TargetNoLongerNearby" => "送出前無法在目前周遭玩家中找到此店主，已略過，沒有送出開店請求；保存商品與座標仍保留。",
            "TargetChanged" => "送出前店主的身分已變更，已略過，沒有送出開店請求；請手動刷新周遭後重新比對。",
            "TargetObservationExpired" => "送出前店主的最新觀測已過期或不完整，已略過，沒有送出開店請求；請等資料恢復後手動刷新。",
            "TargetNotShoppingObserved" => "送出前讀到此玩家目前為非擺攤動作，已略過，沒有送出開店請求；歷史商品及座標保留。",
            "TargetShopStateUnknown" => "送出前此玩家目前的擺攤狀態尚未確認，已略過，沒有送出開店請求；這不代表玩家未擺攤。請等資料恢復後手動刷新。",
            "ShopResponseUnverified" => "商店呼叫已完成，但未取得能核對目標店主的商品頁；已略過此店剩餘頁面，不能判定沒有商品，也無法由此確認遊戲拒絕的原因。",
            _ => "本次查詢未完成，不能判定沒有商品。"
        };
        return $"{title} 原因 {failure.Code}{(string.IsNullOrWhiteSpace(failure.Rva) ? "" : "／" + failure.Rva)}：{failure.Detail}"
            + (canContinue
                ? " 本次結果紀錄不會阻擋下一個使用者指令。" : "");
    }

    internal static string Status(ShopCommandResponse response, bool forceDispatch = false)
    {
        if (response.State == "TargetsRefreshed" && string.IsNullOrWhiteSpace(response.FailureCode))
            return response.Detail + (string.IsNullOrWhiteSpace(response.Scan.JobId) ? "" :
                $" 上一批搜尋統計（非本次刷新結果）：商店 {response.Scan.CompletedShops}/{response.Scan.TotalShops}；{PageCounts(response.Scan)}。"
                + (string.IsNullOrWhiteSpace(response.Scan.StopReason) ? "" : "上批停止原因：" + response.Scan.StopReason));
        string failure = Format(response, forceDispatch);
        bool currentRequestFailed = !string.IsNullOrWhiteSpace(response.FailureCode) && response.State is not ("State" or "Stopping");
        if (currentRequestFailed)
        {
            // A rejected refresh/open may carry the preceding scan as context.
            // Keep its counts explicitly historical, without appending its cause.
            return failure + (string.IsNullOrEmpty(response.Scan.JobId) ? "" :
                $" 上一批搜尋統計：商店 {response.Scan.CompletedShops}/{response.Scan.TotalShops}；{PageCounts(response.Scan)}。");
        }
        string timing = response.Scan.AverageShopDurationMs is > 0 ? $"；平均每店 {response.Scan.AverageShopDurationMs / 1000:0.00} 秒" : "";
        string message = $"本輪商店 {response.Scan.CompletedShops}/{response.Scan.TotalShops}；本輪{PageCounts(response.Scan)}{timing}。{response.Scan.Detail}";
        if (!string.IsNullOrWhiteSpace(response.Scan.JobId))
            message += $" 本批使用上次手動刷新狀態：{PlayerShopStatePresentation.Counts(new(response.Scan.ShopActionObservedCount, response.Scan.KnownOtherActionObservedCount, response.Scan.UnknownShopStateCount))}。";
        if (!response.Scan.ScreenCoverageVerified) message += "（畫面商店完整覆蓋仍待驗證）";
        if (response.ListingsTruncated) message += "（本次查詢回覆有省略，歷史仍保留）";
        if (response.Scan.SkippedPages > 0) message += $" 尚未送出 {response.Scan.SkippedPages} 頁。";
        if (!string.IsNullOrWhiteSpace(response.Scan.StopReason)) message += (forceDispatch ? " 上次批次停止原因：" : " 停止原因：") + response.Scan.StopReason;
        if (forceDispatch && MaySuggestNextCommand(response)) message += " 可直接下達下一指令。";
        return failure.Length > 0 ? failure + " " + message : message;
    }

    private static bool MaySuggestNextCommand(ShopCommandResponse response)
    {
        var failure = Select(response);
        string? scope = failure?.FromCurrentResponse == false ? response.Scan.FailureScope : response.FailureScope;
        return !response.SessionBlocked && !(response.NativeEntered && !response.NativeReturned)
            && (failure?.Code != "NativeCodeUnavailable" || response.WorkSettled)
            && scope is not ("Session" or "Unknown")
            && failure?.Code is not ("ContextUnavailable" or "NativeBuildUnsupported" or "NativeCodeMismatch" or "NativeHelperMissing" or "NativeHelperMismatch");
    }
    private static string PageCounts(MarketScanState scan) =>
        $"處理 {Math.Max(0, scan.Completed)}/{Math.Max(0, scan.Total)} 頁（買／賣頁分別計數）；此次查詢 {Math.Max(0L, (long)scan.Completed - scan.SkippedHistoryPages)} 頁"
        + $"（確認 {Math.Max(0, scan.Succeeded)}、未讀到商品 {Math.Max(0, scan.EmptyObservedPages)}（方向未確認）、失敗或未確認 {Math.Max(0, scan.Failed)}）"
        + (scan.NotEnteredPages>0 ? $"；確定未執行 {scan.NotEnteredPages}（原頁未重送）" : "")
        + $"；已記錄略過 {Math.Max(0, scan.SkippedHistoryPages)} 頁（本輪未送出）";
}

