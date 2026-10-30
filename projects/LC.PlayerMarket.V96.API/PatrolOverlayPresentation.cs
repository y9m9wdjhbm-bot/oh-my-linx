namespace LcPlayerMarket;
internal static class PatrolOverlayPresentation
{
    internal static SearchOverlayView Combine(SearchOverlayView scan, SearchOverlayView? patrol,
        bool running, bool blocked, bool scanRunning, bool requestPending)
    {
        if (patrol is null) return scan;
        if (patrol.PatrolState is "Failed" or "Error" or "Blocked") return patrol;
        if (running && scanRunning) return scan with { Progress = patrol.Progress + "；" + scan.Progress, Detail = patrol.Detail + "；" + scan.Detail };
        if (running || blocked) return patrol;
        return scanRunning || requestPending ? scan : patrol;
    }
    internal static SearchOverlayView Build(string state, string detail, DateTimeOffset at, PatrolProgress? progress = null)
    {
        string title = state switch {
            "ForegroundWait" => "等待 LC 前景", "Moving" or "Navigating" => "導航命令中", "MovingConfirmed" => "角色正在行走",
            "Retrying" => "巡查重試中", "Replanning" => "受阻，重新規劃", "WaitingSource" or "SourceWait" => "等待目前角色讀值",
            "Preparing" => "正在準備巡迴", "WaitingNativeReady" => "入口未就緒，巡迴尚未開始", "Capturing" => "正在取得目前位置",
            "BundleVerifying" => "正在核對導航檔案", "NavigationStarting" => "正在啟動導航元件",
            "Scanning" or "Searching" => "巡查商店搜尋中", "Refreshing" => "正在更新周圍商店", "Arriving" => "已到達，正在確認停止", "Recovered" => "巡查資料已恢復",
            "Blocked" => "巡查已阻擋，結果待確認", "Paused" => "已暫停", "Stopping" => "正在停止巡查",
            "RoundCompleted" => "本輪完成",
            "UserStopped" => "使用者已停止", "Stopped" => "已停止", "Completed" => "巡查完成", "Failed" or "Error" => "巡查失敗",
            _ => "巡查：" + state
        };
        return new(title, progress?.PointName ?? "", progress is null ? "" : $"第 {progress.CurrentRound}/{progress.TotalRounds} 輪（完成 {progress.CompletedRounds} 輪）；定點 {Math.Clamp(progress.PointIndex + 1, 0, progress.TotalPoints)}/{progress.TotalPoints}；重試 {progress.RetryAttempt}",
            $"{at.ToLocalTime():HH:mm:ss} {detail}", state is not ("Completed" or "Stopped" or "UserStopped" or "Failed" or "Error" or "Blocked" or "Paused")) { PatrolState = state, TransitionAt = at };
    }
    internal static SearchOverlayView? FromNavigationNotice(PatrolSourceWaitNotice notice, SearchOverlayView? prior, DateTimeOffset at)
    {
        if (prior?.PatrolState is "Stopping" or "Stopped" or "UserStopped" or "Completed" or "Failed" or "Error" or "Blocked") return null;
        string? state = notice.Code switch
        {
            "BUNDLE_VERIFY_START" or "BUNDLE_VERIFY_PROGRESS" => "BundleVerifying",
            "BUNDLE_VERIFY_READY" or "BACKEND_START" => "NavigationStarting",
            "BACKEND_READY" or "SOURCE_RECOVERED" => "Capturing",
            "SOURCE_WAIT" => "WaitingSource",
            _ => null
        };
        return state is null ? null : Build(state, notice.Detail, at) with
        { Player = prior?.Player ?? "", Progress = prior?.Progress ?? "" };
    }
    internal static SearchOverlayView BuildResult(PatrolRunResult result, DateTimeOffset at) =>
        Build(result.State, result.Detail, at, new PatrolProgress(result.RunId, result.State, result.CompletedPoints,
            result.TotalPoints, null, null, 0, result.Detail)
            { CurrentRound = result.CurrentRound, TotalRounds = result.TotalRounds, CompletedRounds = result.CompletedRounds });

    internal static SearchOverlayView? RetireForAcceptedAction(SearchOverlayView? prior, string command,
        string requestId, DateTimeOffset requestedAt, string source, MemoryHub.ShopCommandResponse response)
    {
        bool accepted = response.RequestId == requestId && response.At >= requestedAt
            && response.CurrentSourceSessionKey == source && string.IsNullOrEmpty(response.FailureCode)
            && (command == MemoryHub.ShopCommands.RefreshTargets && response.State == "TargetsRefreshed"
                || command is MemoryHub.ShopCommands.ScanVisible or MemoryHub.ShopCommands.ContinueSavedScan
                    && response.Scan.Running && !string.IsNullOrWhiteSpace(response.Scan.JobId)
                    && response.Scan.SourceSessionKey == source);
        return accepted ? null : prior;
    }
    internal static IEnumerable<OfflineCheck> SelfTest()
    {
        var at = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        foreach(var state in new[]{"ForegroundWait","Preparing","Moving","Retrying","WaitingSource","Refreshing","Searching","Arriving","Recovered","Blocked","Paused","Stopping","UserStopped","Completed","Failed"})
        {
            var view=Build(state,"exact reason",at);
            yield return new("patrol_overlay_"+state,view.State.Length>0&&view.Detail.Contains("exact reason")&&view.Detail.Contains(at.ToLocalTime().ToString("HH:mm:ss")),"Existing state + reason + transition timestamp");
        }
        var scan = new SearchOverlayView("已暫停", "owner", "1/3", "scan reason", false);
        var patrol = Build("Moving", "patrol reason", at);
        yield return new("overlay_patrol_scan_pause_truthful", Combine(scan,patrol,true,false,true,false).State == "已暫停", "Scan pause remains visible during patrol");
        yield return new("overlay_new_manual_scan_overrides_old_patrol", Combine(scan,patrol,false,false,true,false)==scan,"Old terminal patrol never masks new scan");
        yield return new("overlay_new_request_overrides_old_patrol", Combine(scan,patrol,false,false,false,true)==scan,"New pending manual request remains visible");
        yield return new("overlay_active_movement_visible", Combine(scan,patrol,true,false,false,false)==patrol,"Walking state independent of stopped scan");
        var fault = Build("Failed", "READ_CAPTURE_FAILED", at);
        var preparing = Build("Preparing", "start", at) with { Player = "保存定位點", Progress = "第 1/2 輪；定點 1/3" };
        var verifying = FromNavigationNotice(new("BUNDLE_VERIFY_PROGRESS", "64/3022", 64, TimeSpan.Zero), preparing, at);
        yield return new("patrol_bundle_progress_distinct_from_source_wait", verifying?.PatrolState == "BundleVerifying"
            && verifying.State == "正在核對導航檔案" && verifying.Detail.Contains("64/3022")
            && verifying.Player == preparing.Player && verifying.Progress == preparing.Progress, "Initialization preserves route progress and names the actual file check");
        yield return new("patrol_backend_ready_starts_capture_presentation", FromNavigationNotice(new("BACKEND_READY", "ready", 3022, TimeSpan.Zero), preparing, at)?.State == "正在取得目前位置", "Only validated child readiness changes initialization into capture");
        yield return new("patrol_source_wait_remains_separate", FromNavigationNotice(new("SOURCE_WAIT", "source gap", 1, TimeSpan.Zero), preparing, at)?.State == "等待目前角色讀值", "Actual source gap is distinct from bundle initialization");
        foreach (string terminal in new[] { "Stopping", "Stopped", "UserStopped", "Completed", "Failed", "Blocked" })
            yield return new("patrol_late_bundle_progress_preserves_" + terminal,
                FromNavigationNotice(new("BUNDLE_VERIFY_READY", "ready", 3022, TimeSpan.Zero), Build(terminal, "terminal", at), at) is null,
                "Late initialization notice cannot replace stopped or terminal presentation");
        foreach (var pending in new[] { false, true })
            yield return new("terminal_error_survives_cleanup_"+pending, Combine(scan,fault,false,false,false,pending)==fault, "Cleanup/cancel busy is not an accepted new request");
        yield return new("terminal_error_survives_scan_stop", Combine(scan,fault,false,false,false,false)==fault, "Stopped scan cannot erase terminal reason/time");
        yield return new("unconfirmed_navigation_is_not_walking", Build("Moving","command pending",at).State=="導航命令中", "Controller publishes Moving before dispatch; wait for position evidence");
        yield return new("confirmed_position_progress_is_walking", Build("MovingConfirmed","fresh same-source position progress",at).State=="角色正在行走", "Only explicit confirmed progress maps to walking");
        yield return new("new_patrol_retry_replaces_fault", Combine(scan,Build("Retrying","new accepted run",at.AddSeconds(1)),true,false,false,false).PatrolState=="Retrying", "New run owns new transition");
        yield return new("completed_terminal_retained", Combine(scan,Build("Completed","done",at),false,false,false,false).PatrolState=="Completed", "Cleanup never rewrites completion");
        yield return new("cancel_terminal_retained", Combine(scan,Build("UserStopped","F9",at),false,false,false,true).PatrolState=="UserStopped" || Combine(scan,Build("UserStopped","F9",at),false,false,false,true)==scan, "Cancel remains truthful; error precedence is independent");
        var accepted=new MemoryHub.ShopCommandResponse { RequestId="new-request", At=at.AddSeconds(1), CurrentSourceSessionKey="current", State="TargetsRefreshed" };
        yield return new("accepted_manual_refresh_retires_fault", RetireForAcceptedAction(fault,MemoryHub.ShopCommands.RefreshTargets,"new-request",at,"current",accepted) is null, "Exact request/time/source success retires old fault");
        yield return new("wrong_request_does_not_retire_fault", RetireForAcceptedAction(fault,MemoryHub.ShopCommands.RefreshTargets,"other",at,"current",accepted)==fault, "Wrong receipt is not new work");
        yield return new("old_timestamp_does_not_retire_fault", RetireForAcceptedAction(fault,MemoryHub.ShopCommands.RefreshTargets,"new-request",at.AddSeconds(2),"current",accepted)==fault, "Historical receipt cannot erase current error");
        yield return new("cancel_receipt_does_not_retire_fault", RetireForAcceptedAction(fault,MemoryHub.ShopCommands.CancelScan,"new-request",at,"current",accepted)==fault, "Cleanup/cancel is never a fresh action");
        using var overlay = new SearchStatusOverlay();
        yield return new("search_overlay_half_opacity", Math.Abs(overlay.Opacity - 0.5) < 0.0001, "50% opacity; existing fake-window hit-test/noactivation suite retained");
    }
}
