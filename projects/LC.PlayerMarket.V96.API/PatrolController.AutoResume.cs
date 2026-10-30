namespace LcPlayerMarket;



internal sealed partial class PatrolController

{

    private readonly bool autoResume;

    private readonly PatrolCheckpointStore? checkpointStore;

    private PatrolCheckpoint? resumeCheckpoint;

    private RunState? blockedRun;

    private long memoryResetGeneration;



    private async Task<PatrolRunResult> RunResumableAsync(PatrolRouteSnapshot route, CancellationToken token)

    {

        string invocation = Guid.NewGuid().ToString("N");

        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)

            return new(invocation, "Blocked", 0, route.Points.Length, 0, "原巡迴仍在執行；沒有同時啟動另一個巡迴。", false) { TotalRounds = route.RoundCount };

        var run = new RunState(invocation);

        try

        {

            PatrolRouteRules.Validate(route); run.Route = PatrolRouteRules.Copy(route);

            if (route.Points.Length == 0) throw Fail("請先保存至少一個定位點。");

            int map = Map(route.Points[0]);

            if (route.Points.Any(point => Map(point) != map)) throw Fail("巡迴只接受同一張地圖的定位點。");

            var saved = checkpointStore is null ? resumeCheckpoint : checkpointStore.LoadLatest();

            run.ResetGeneration = saved?.ResetGeneration ?? checkpointStore?.CurrentResetGeneration ?? memoryResetGeneration;

            if (saved is { Origin: { } savedOrigin } && !PatrolCheckpointStore.Matches(saved, route)

                && (saved.NavigationRequest is not null && !saved.NavigationSettled || saved.ScanRequest is not null && !saved.ScanSettled))

            {

                run.PriorCheckpoint = saved;

                run.PriorOwnership = new(saved.RunId) { Origin = savedOrigin, PlanId = saved.PlanId,

                    ScanRequest = saved.ScanRequest, ScanSettled = saved.ScanSettled,

                    NavigationRequest = saved.NavigationRequest, NavigationSettled = saved.NavigationSettled };

            }

            if (saved is not null && saved.RouteRevision == route.Revision && !PatrolCheckpointStore.Matches(saved, route))

                throw Block("相同路線修訂的內容已變更；原恢復紀錄保留，沒有接續不同路線。");

            bool resuming = saved is not null && PatrolCheckpointStore.Matches(saved, route)

                && saved.CompletedRounds < route.RoundCount && saved.Phase != PatrolCheckpointPhase.Completed;

            if (resuming)

            {

                run.Id = saved!.RunId; run.CurrentRound = saved.CurrentRound; run.Completed = saved.PointIndex;

                run.CompletedRounds = saved.CompletedRounds; run.Phase = saved.Phase; run.Origin = saved.Origin;

                run.PlanId = saved.PlanId; run.ScanRequest = saved.ScanRequest; run.ScanSettled = saved.ScanSettled;

                run.NavigationRequest = saved.NavigationRequest; run.NavigationSettled = saved.NavigationSettled;

                run.RetriesUsed = saved.RetriesUsed; run.RecoveryCycles = saved.RecoveryCycles;

                run.ImportedLogSha256 = new(saved.ImportedLogSha256);

                run.ImportedSource = saved.ImportedSource; run.ImportedSelfId = saved.ImportedSelfId;

                Publish(run, "Preparing", $"從保存進度接續第 {run.CurrentRound} 輪第 {Math.Min(run.Completed + 1, route.Points.Length)} 點；先重新取得目前來源與位置。");

            }

            else run.Phase = PatrolCheckpointPhase.Navigate;



            // This call belongs to the user's new Start. A restarted LC may bind a

            // fresh origin here; automatic recovery below never changes that origin.

            while (true)

            {

                token.ThrowIfCancellationRequested();

                try

                {

                    var current = await navigation.CaptureAsync(token).ConfigureAwait(false);

                    ValidateObservation(current, current, map);

                    if (!resuming && saved is { Origin: { } priorOrigin }

                        && (saved.NavigationRequest is not null && !saved.NavigationSettled

                            || saved.ScanRequest is not null && !saved.ScanSettled))

                    {

                        // Choosing a different route does not retire the previous

                        // route's native owner. Keep its own identity and checkpoint.

                        run.PriorCheckpoint = saved;

                        run.PriorOwnership ??= new RunState(saved.RunId)

                        {

                            Origin = priorOrigin, PlanId = saved.PlanId, ScanRequest = saved.ScanRequest,

                            ScanSettled = saved.ScanSettled, NavigationRequest = saved.NavigationRequest,

                            NavigationSettled = saved.NavigationSettled

                        };

                        bool endedProcess = priorOrigin.Source.ProcessId != current.Source.ProcessId

                            || priorOrigin.Source.CreationFileTime != current.Source.CreationFileTime;

                        if (!endedProcess && !await CleanupAsync(run.PriorOwnership).ConfigureAwait(false))

                            throw Block("原路線的工作仍未完整釋放；保存原恢復紀錄，不以新路線取代未知工作。");

                        if (!endedProcess) SaveRetiredOwnership(saved, run.PriorOwnership, "Retired");

                        run.PriorOwnership = null; run.PriorCheckpoint = null;

                    }

                    if (blocked)

                    {

                        bool restarted = blockedRun?.Origin is { } prior && (prior.Source.ProcessId != current.Source.ProcessId

                            || prior.Source.CreationFileTime != current.Source.CreationFileTime);

                        if (!restarted && (blockedRun is null || !await CleanupAsync(blockedRun).ConfigureAwait(false)))

                            throw Block(blockedReason ?? "前次原生工作仍未釋放；恢復點保留。");

                        blocked = false; blockedReason = null; blockedRun = null;

                    }

                    var previousSource = run.Origin?.Source ?? run.ImportedSource;

                    ulong previousSelf = run.Origin?.SelfId ?? run.ImportedSelfId;

                    bool newSource = previousSource is not null && (previousSource != current.Source || previousSelf != current.SelfId);

                    bool newProcess = previousSource is not null && (previousSource.ProcessId != current.Source.ProcessId

                        || previousSource.CreationFileTime != current.Source.CreationFileTime);

                    if (!newProcess && (run.NavigationRequest is not null && !run.NavigationSettled

                        || run.ScanRequest is not null && !run.ScanSettled))

                    {

                        // The user can start again, but a surviving native owner may

                        // only be retired by its original fenced stop/cleanup receipt.

                        if (!await CleanupAsync(run).ConfigureAwait(false))

                            throw Block("原工作尚未取得完整釋放證據；保存同點請求，不開始新的工作。");

                    }

                    run.Origin = current;

                    // Retired jobs are not replayed across Start. A new process proves

                    // the old process has ended; old pending records remain untouched.

                    run.NavigationRequest = null; run.NavigationSettled = false;

                    if (newSource)

                    {

                        run.PlanId = null; run.ScanRequest = null; run.ScanSettled = false;

                        if (run.Completed < route.Points.Length && run.Phase != PatrolCheckpointPhase.PointCompleted)

                            run.Phase = PatrolCheckpointPhase.Navigate;

                        Publish(run, "Preparing", "本次手動開始已重新核對新的 LC 來源；保留定位點索引，舊玩家 ID、計畫及請求不沿用。");

                    }

                    else if (run.Completed < route.Points.Length && run.Phase is not (PatrolCheckpointPhase.Navigate or PatrolCheckpointPhase.PointCompleted)

                        && Distance(current.X, current.Y, route.Points[run.Completed]) > route.ArrivalRadius)

                        run.Phase = PatrolCheckpointPhase.Navigate;

                    SaveCheckpoint(run, "Running", "重新核對目前來源後，接續保存定位點與階段。");

                    break;

                }

                catch (PatrolSourceReadException error) when (RecoverableCapture(error) && !token.IsCancellationRequested)

                { await RecoveryCooldownAsync(run, error.Message, token).ConfigureAwait(false); }

            }



            while (true)

            {

                token.ThrowIfCancellationRequested();

                if (run.Completed == route.Points.Length)

                {

                    if (run.CompletedRounds < run.CurrentRound)

                    {

                        run.CompletedRounds = run.CurrentRound;

                        SaveCheckpoint(run, "Running", "已完成本輪所有定位點。");

                        Publish(run, "RoundCompleted", $"已完整完成 {run.CompletedRounds}/{route.RoundCount} 輪。");

                    }

                    token.ThrowIfCancellationRequested();

                    if (run.CompletedRounds == route.RoundCount)

                    {

                        run.Phase = PatrolCheckpointPhase.Completed;

                        SaveCheckpoint(run, "Completed", "已完成設定輪數。");

                        Publish(run, "Completed", $"已完整完成設定的 {run.CompletedRounds}/{route.RoundCount} 輪，巡迴結束。");

                        return Result(run, "Completed", $"已完整完成設定的 {run.CompletedRounds}/{route.RoundCount} 輪，巡迴結束。", true);

                    }

                    run.CurrentRound++; run.Completed = 0; run.Point = null;

                    ResetPointWork(run); run.Phase = PatrolCheckpointPhase.Navigate;

                    SaveCheckpoint(run, "Running", "保存下一輪起點。");

                }

                run.Point = run.Route.Points[run.Completed];

                await ExecuteSavedPointAsync(run, token).ConfigureAwait(false);

            }

        }

        catch (Exception failure)

        {

            bool manualStop = token.IsCancellationRequested || run.UserStopped || failure is OperationCanceledException;

            Publish(run, "Stopping", manualStop ? "已停止自動接續，正在結束目前工作；恢復點保留。" : "目前錯誤需要結束工作，恢復點保留。");

            bool clean = await CleanupAsync(run).ConfigureAwait(false);

            Exception? priorCheckpointError = null;

            if (run.PriorOwnership is { } prior)

            {

                bool priorClean = await CleanupAsync(prior).ConfigureAwait(false);

                if (run.PriorCheckpoint is { } previous)

                {

                    try { SaveRetiredOwnership(previous, prior, priorClean ? "Retired" : "Blocked"); }

                    catch (Exception error) { priorCheckpointError = error; }

                }

                clean &= priorClean;

            }

            if (!clean) { blocked = true; blockedRun = run.PriorOwnership ?? run; blockedReason = "前次原生工作未釋放；保存定位點與計畫，沒有自動啟動另一個工作。"; }

            string state = !clean || failure is PatrolFailure { Uncertain: true } ? "Blocked" : manualStop ? "Stopped" : "Failed";

            string detail = manualStop ? "巡迴已手動停止；下次按開始會接續保存定位點，不會自行重啟。" : failure.Message;

            if (!clean) detail += " 原生工作釋放尚未確認。";

            if (priorCheckpointError is not null) { detail += " 原工作恢復紀錄保存失敗：" + priorCheckpointError.Message; state = "Failed"; }

            try { SaveCheckpoint(run, state, detail); }

            catch (Exception checkpointError) { detail += " 恢復紀錄保存失敗：" + checkpointError.Message; state = "Failed"; }

            Publish(run, state, detail); return Result(run, state, detail, clean) with { FailureCode = (failure as PatrolFailure)?.Code };

        }

        finally { Volatile.Write(ref running, 0); }

    }



    private async Task ExecuteSavedPointAsync(RunState run, CancellationToken token)

    {

        while (true)

        {

            token.ThrowIfCancellationRequested();

            try

            {

                switch (run.Phase)

                {

                    case PatrolCheckpointPhase.Navigate:

                        var before = await CaptureAsync(run, token).ConfigureAwait(false);

                        await NavigateAsync(run, before, token).ConfigureAwait(false);

                        run.Phase = PatrolCheckpointPhase.ConfirmArrival; break;

                    case PatrolCheckpointPhase.ConfirmArrival:

                        await CaptureAtPointAsync(run, token).ConfigureAwait(false);

                        run.Phase = PatrolCheckpointPhase.Search; break;

                    case PatrolCheckpointPhase.Search:

                        await SearchPointAsync(run, token).ConfigureAwait(false);

                        run.Phase = PatrolCheckpointPhase.VerifyPosition; break;

                    case PatrolCheckpointPhase.VerifyPosition:

                        await CaptureAtPointAsync(run, token).ConfigureAwait(false);

                        run.Phase = PatrolCheckpointPhase.SettleNavigation; break;

                    case PatrolCheckpointPhase.SettleNavigation:

                        await SettleNavigationAsync(run, token).ConfigureAwait(false);

                        run.Phase = PatrolCheckpointPhase.PointCompleted; break;

                    case PatrolCheckpointPhase.PointCompleted:

                        run.Completed++; run.Point = null; ResetPointWork(run);

                        run.Phase = PatrolCheckpointPhase.Navigate;

                        SaveCheckpoint(run, "Running", "本點已完成，保存下一定位點索引。");

                        return;

                    default: throw Block("保存的巡迴階段無法接續；沒有重設為首點。");

                }

                SaveCheckpoint(run, "Running", "保存同點執行階段。");

            }

            catch (PatrolSourceReadException error) when (RecoverableCapture(error) && !token.IsCancellationRequested)

            {

                // CaptureAsync retains and settles its own pending capture ID. This

                // layer only starts another bounded read after a cancellable cooldown.

                await RecoveryCooldownAsync(run, error.Message, token).ConfigureAwait(false);

            }

            catch (PatrolFailure error) when (continuousRecovery && error.Code == "RetrySettledNavigation" && !token.IsCancellationRequested)
            {
                await RetrySettledNavigationAsync(run, error.Message, token).ConfigureAwait(false);
            }
            catch (PatrolFailure error) when (continuousRecovery && error.Code == "RetryReadinessRefusal" && !token.IsCancellationRequested)
            {
                await RecoveryCooldownAsync(run, error.Message, token).ConfigureAwait(false);
            }
            catch (PatrolFailure error) when (error.Code == "PositionOutsideRange" && !token.IsCancellationRequested)

            {

                if (!await CleanupAsync(run).ConfigureAwait(false))

                    throw Block("離開定位點後原工作尚未完整釋放；保存同點計畫，不開始新的行走。");

                run.NavigationRequest = null; run.NavigationSettled = false;

                run.Phase = PatrolCheckpointPhase.Navigate;

                await RecoveryCooldownAsync(run, "實際位置離開指定範圍；保留同點計畫，重新前往本點後接續。", token).ConfigureAwait(false);

            }

        }

    }



    private void ResetPointWork(RunState run)

    {

        run.PlanId = null; run.ScanRequest = null; run.NavigationRequest = null;

        run.FrozenTargets = null;

        run.ScanOwned = run.ScanSettled = run.NavigationOwned = run.NavigationSettled = false;

        run.Retry = 0; run.CaptureRecoveryAttempts = 0;

        // This budget belongs to the current point. Total retries remain in RetriesUsed.
        if (continuousRecovery) run.RecoveryCycles = 0;

        run.LastScanFailureCode = run.LastScanFailurePhase = run.LastScanFailureScope = null;

    }



    private static bool RecoverableCapture(PatrolSourceReadException error) =>

        error.Code is "SourceUnavailableTimeout" or "NavigationSampleStale" or "NavigationCaptureReadIncomplete";



    private bool CanRecoverScan(RunState run) =>

        (run.LastScanFailureCode is null or "ContextUnavailable" or "RefreshReadTimeout" or "RefreshSourceSampleGap")

        || run.LastScanFailureCode == "DispatchReadTimeout" && run.LastScanFailurePhase == "BeforePublish"

        || run.LastScanFailureCode == "NativePreflightReadUnavailable"

            && run.LastScanFailurePhase == "Preflight"
        || continuousRecovery && (run.LastScanFailureCode == "NativeCodeUnavailable" || run.LastScanFailureCode == "NativePreflightPageChanged" && run.PageRangeFailureEligible)
            && run.LastScanFailureScope == "Global" && run.LastScanFailurePhase == "Preflight";



    private async Task RecoveryCooldownAsync(RunState run, string detail, CancellationToken token)

    {

        token.ThrowIfCancellationRequested();
        // Shared by readiness, capture, refresh, status and settled navigation recovery.
        // Successful intermediate reads never replenish this point's budget; resume
        // reloads the count from its immutable checkpoint. Native guards are unchanged.
        if (continuousRecovery && run.RecoveryCycles >= run.Route!.RetryLimit)
            throw new PatrolFailure($"RecoveryBudgetExhausted：第 {run.CurrentRound} 輪第 {run.Completed + 1} 點已達 {run.Route.RetryLimit} 次恢復上限；保存同點與原計畫，停止自動重試。最後原因：{detail}", false)
                { Code = "RecoveryBudgetExhausted" };
        run.RecoveryCycles = IncrementRecoveryCount(run.RecoveryCycles); run.Retry = 0;
        if (continuousRecovery) run.RetriesUsed = IncrementRecoveryCount(run.RetriesUsed);

        int seconds = Math.Min(30, 2 * (1 << Math.Min(run.RecoveryCycles - 1, 4)));

        SaveCheckpoint(run, "Recovering", detail);

        Publish(run, "Retrying", $"保留第 {run.CurrentRound} 輪第 {run.Completed + 1} 點與原階段；{seconds} 秒後自動接續。"
            + (continuousRecovery ? $"第 {run.RecoveryCycles}/{run.Route!.RetryLimit} 次恢復。" : "") + $"原因：{detail}");

        await delay(TimeSpan.FromSeconds(seconds), token).WaitAsync(token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();

    }



    private void SaveCheckpoint(RunState run, string status, string detail)

    {

        if (!autoResume || run.Route is null || run.Origin is null) return;

        var checkpoint = new PatrolCheckpoint

        {

            RunId = run.Id, RouteRevision = run.Route.Revision, RouteFingerprint = PatrolCheckpointStore.Fingerprint(run.Route),

            CurrentRound = run.CurrentRound, PointIndex = run.Completed, CompletedRounds = run.CompletedRounds,

            TotalRounds = run.Route.RoundCount, TotalPoints = run.Route.Points.Length, Phase = run.Phase,

            Origin = run.Origin, PlanId = run.PendingScanRequest is { } pending ? pending.ExpectedScanPlanId : run.PlanId,

            NavigationRequest = run.NavigationRequest,

            NavigationSettled = run.NavigationSettled, ScanRequest = run.PendingScanRequest ?? run.ScanRequest,

            ScanSettled = run.PendingScanRequest is null && run.ScanSettled,

            RetriesUsed = run.RetriesUsed, RecoveryCycles = run.RecoveryCycles, Status = status,

            Detail = detail.Length <= 16000 ? detail : detail[..16000], ImportedLogSha256 = new(run.ImportedLogSha256),

            ImportedSource = run.ImportedSource, ImportedSelfId = run.ImportedSelfId,

            ResetGeneration = run.ResetGeneration

        };

        resumeCheckpoint = checkpointStore is null ? checkpoint with

            { At = DateTimeOffset.UtcNow, Sequence = (resumeCheckpoint?.Sequence ?? 0) + 1 } : checkpointStore.Save(checkpoint);

    }



    private void SaveRetiredOwnership(PatrolCheckpoint previous, RunState owner, string status)

    {

        var updated = previous with { NavigationSettled = owner.NavigationSettled, ScanSettled = owner.ScanSettled,

            PlanId = owner.PlanId, Status = status, Detail = "使用原工作識別核對生命週期；定位點進度與舊請求保留。" };

        resumeCheckpoint = checkpointStore is null ? updated : checkpointStore.Save(updated);

    }

}

