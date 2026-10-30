using MemoryHub;

namespace LcPlayerMarket;

internal sealed partial class PatrolController
{
    private void AdoptScanAcceptance(RunState run, ShopCommandRequest request, ShopCommandResponse response,
        string? expectedPlan, string? previousPlan, ShopCommandResponse? beforeSend = null)
    {
        if (response.Schema != ShopCommands.Schema)
            throw Block("搜尋接受回覆協定不同；保留新請求識別核對清理，沒有重送。");
        // A definitive refusal still carries the previous scan's public state.
        // It must not replace the ID whose lifetime the patrol already owns.
        if (response.RequestId == request.RequestId && response.State is "Rejected" or "SessionBlocked" or "Busy"
            && !response.NativeEntered && !response.NativeReturned && !response.ResponseVerified
            && response.CurrentSourceSessionKey == run.Origin!.Source.SessionKey
            && response.Scan.JobId != request.RequestId)
        {
            bool priorMatched = false;
            if (run.ScanRequest is { } prior && run.PlanId is not null)
            {
                ValidateScan(response, run, prior, run.PlanId);
                ValidateSaved(response.SavedScan!, run.Origin!, requireSettled: response.WorkSettled && !response.Scan.Running);
                priorMatched = true;
            }
            else if (run.ScanRequest is null && response.Scan.JobId is null && !response.Scan.Running
                && response.WorkSettled && response.SavedScan is null)
                priorMatched = true;
            if (priorMatched)
            {
                run.PendingScanRequest = null;
                if (run.ScanRequest is not null)
                {
                    run.ScanSettled = response.WorkSettled && !response.Scan.Running;
                    if (response.Scan.StopReason is "Cancelled" or "UserStop")
                    { run.UserStopped = true; throw new OperationCanceledException("原商店搜尋已被手動停止。"); }
                }
                SaveCheckpoint(run, "Running", "接續請求明確未受理；保留原已受理工作與清理狀態。" + response.Detail);
                if (RecoverableReadinessRefusal(response, run))
                    throw new PatrolFailure("唯讀入口暫時不可用；本次明確未受理，冷卻後重新取得完整來源再試。" + $" 原因：{response.FailureCode}（{response.FailureScope}／{response.FailurePhase}／{response.FailureRva}）：{response.Detail}", false) { Code = "RetryReadinessRefusal" };
                throw Block("搜尋請求未受理；保留原工作與未送出頁：" + response.Detail);
            }
        }

        // A new point has no active local plan, while the broker deliberately retains
        // its previous completed scan. An exact readonly refusal is not a lost reply.
        // The pre-send snapshot is evidence only for rejection/cleanup, never permission
        // to repeat this request or to adopt the previous point's plan as current work.
        if (IsSettledReadinessRefusalOfNewPoint(run, request, response, beforeSend))
        {
            run.PendingScanRequest = null;
            SaveCheckpoint(run, "Running", "本次搜尋已明確在發布前拒絕；保留前批紀錄，沒有建立新工作。" + response.Detail);
            if (RecoverableReadinessRefusal(response, run))
                throw new PatrolFailure("唯讀入口暫時不可用；本次未建立新工作，保留原進度後重試。" + $" 原因：{response.FailureCode}（{response.FailureScope}／{response.FailurePhase}／{response.FailureRva}）：{response.Detail}", false) { Code = "RetryReadinessRefusal" };
            throw Fail("搜尋請求未受理；本次未建立新工作，前批紀錄保持不變。" + response.Detail);
        }

        // A State response can establish acceptance after a lost command reply;
        // its exact job/plan/source/refresh binding is still mandatory.
        if (response.State is not ("ScanStarted" or "State" or "Stopping"))
            throw Block("搜尋接受狀態未確認；保留新請求識別核對清理，沒有重送。" + response.Detail);
        ValidateScan(response, run, request, expectedPlan);
        ValidateMarketSource(response, run.Origin!);
        ValidateSaved(response.SavedScan!, run.Origin!, requireSettled: response.WorkSettled && !response.Scan.Running);
        if (expectedPlan is null && response.SavedScan!.PlanId == previousPlan)
            throw Block("新搜尋沒有取得新的計畫識別，沒有採用舊工作結果。");
        run.ScanRequest = request; run.PendingScanRequest = null;
        run.ScanOwned = true; run.ScanSettled = response.WorkSettled && !response.Scan.Running;
        run.PlanId = response.SavedScan!.PlanId;
        SaveCheckpoint(run, "Running", "完整核對搜尋接受回覆後保存原工作識別與進度。");
    }
    private static bool IsSettledReadinessRefusalOfNewPoint(RunState run, ShopCommandRequest request,
        ShopCommandResponse response, ShopCommandResponse? beforeSend)
    {
        if (run.Origin is not { } origin || run.ScanRequest is not null || run.PlanId is not null || run.ScanOwned
            || run.PendingScanRequest != request || request.Command != ShopCommands.ScanVisible
            || request.ExpectedScanPlanId is not null || request.ExpectedSourceSessionKey != origin.Source.SessionKey
            || beforeSend is null || beforeSend.Schema != ShopCommands.Schema
            || beforeSend.State != "TargetsRefreshed" && !(run.FrozenTargets is not null && beforeSend.State == "State")
            || response.Schema != ShopCommands.Schema || response.RequestId != request.RequestId || response.State != "Rejected"
            || response.FailureScope != "Global" || response.FailurePhase != "BeforePublish"
            || response.NativeEntered || response.NativeReturned || response.ResponseVerified
            || !response.WorkSettled || beforeSend.SessionBlocked || response.SessionBlocked
            || beforeSend.Scan.Running || response.Scan.Running
            || beforeSend.CurrentSourceSessionKey != origin.Source.SessionKey || response.CurrentSourceSessionKey != origin.Source.SessionKey
            || response.NativeReadiness is not { Ready: false, ReadOnly: true, NativeRequests: 0 } readiness
            || readiness.Source != origin.Source || string.IsNullOrWhiteSpace(readiness.ProbeId)
            || string.IsNullOrWhiteSpace(readiness.Code) || readiness.Code == "NativeReady" || readiness.Code != response.FailureCode
            || readiness.FailureRva != response.FailureRva)
            return false;
        // Historical outcome counts do not establish native lifetime. The final refusal must
        // independently prove WorkSettled; every prior count must remain valid and unchanged.
        var prior = beforeSend.Scan;
        var oldSaved = beforeSend.SavedScan;
        var saved = response.SavedScan;
        var targets = beforeSend.TargetSnapshot;
        var returnedTargets = response.TargetSnapshot;
        if (string.IsNullOrWhiteSpace(prior.JobId) || prior.JobId == request.RequestId
            || string.IsNullOrWhiteSpace(prior.TargetSnapshotId) || prior.SourceSessionKey != origin.Source.SessionKey
            || response.Scan.JobId != prior.JobId || response.Scan.SourceSessionKey != prior.SourceSessionKey
            || response.Scan.TargetSnapshotId != prior.TargetSnapshotId
            || oldSaved is null || saved is null || string.IsNullOrWhiteSpace(oldSaved.PlanId)
            || oldSaved.SourceSessionKey != origin.Source.SessionKey || saved.SourceSessionKey != oldSaved.SourceSessionKey
            || saved.PlanId != oldSaved.PlanId || oldSaved.UncertainPages < 0 || saved.UncertainPages < 0
            || !SavedCountsValid(oldSaved) || !SavedCountsValid(saved)
            || !SameSavedCounts(oldSaved, saved)
            || targets is null || returnedTargets is null || string.IsNullOrWhiteSpace(request.TargetSnapshotId)
            || targets.SnapshotId != request.TargetSnapshotId || returnedTargets.SnapshotId != targets.SnapshotId
            || targets.SourceSessionKey != origin.Source.SessionKey || returnedTargets.SourceSessionKey != targets.SourceSessionKey
            || targets.SelfId != origin.SelfId || returnedTargets.SelfId != targets.SelfId
            || returnedTargets.RefreshedAt != targets.RefreshedAt || !targets.CanDispatch || !returnedTargets.CanDispatch)
            return false;
        return true;
    }

    private static bool SameSavedCounts(ShopSavedScanProgress a, ShopSavedScanProgress b) =>
        a.TotalPages == b.TotalPages && a.SearchedPages == b.SearchedPages && a.ConfirmedPages == b.ConfirmedPages
        && a.RemainingPages == b.RemainingPages && a.AvailablePages == b.AvailablePages && a.DeferredPages == b.DeferredPages
        && a.HistorySkippedPages == b.HistorySkippedPages && a.UncertainPages == b.UncertainPages
        && a.CanContinue == b.CanContinue && a.ContinueBlockCode == b.ContinueBlockCode;

}
