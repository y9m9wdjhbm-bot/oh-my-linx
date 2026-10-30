using System.Text.Json;
using System.Globalization;
using System.Net;
using System.Net.Http;
using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Production resume coordinator and immutable store, using fake ports only.</summary>
internal static partial class PatrolAutoResumeSelfTest
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly SourceIdentity Source = new(123, 456, "offline-LC.exe", "0x140000000", 1, 65536, new string('A', 64), "offline-session");

    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); checks.Add(new(name, true, "純離線；production自動接續與持久恢復紀錄，沒有LC存取或輸入。")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        foreach (string code in new[] { "READ_CAPTURE_MODULE_PATH_FAILED", "READ_CAPTURE_VECTOR_CHANGED" })
            await Check("patrol_resume_force_client_third_safe_failure_is_recoverable_" + code, async () =>
            {
                await using var f = new RealCaptureFixture { FailureCode = code, FailuresRemaining = 3 };
                bool typed = false;
                try { await f.Client.CaptureAsync(default); }
                catch (PatrolSourceReadException error) when (error.Code == "SourceUnavailableTimeout") { typed = true; }
                Need(typed && f.Posts == 3 && f.Delays == 2, "第三次完整typed讀取失敗沒有轉成可恢復SRE或超出本次三次上限。");
                var observation = await f.Client.CaptureAsync(default);
                Need(observation.Source == Source && f.Posts == 4 && f.StatusReads == 0,
                    "確認已清理的第三筆仍卡pending，或下次沒有freshCapture。");
            });
        await Check("patrol_resume_real_client_third_safe_failure_cools_original_phase", async () =>
        {
            var ports = new Fixture(); await using var capture = new RealCaptureFixture(ports.Nav);
            bool injected = false;
            ports.Nav.AfterStart = () =>
            { if (ports.Nav.Starts.Count == 3 && !injected) { injected = true; capture.FailuresRemaining = 9; } };
            var controller = new PatrolController(new CaptureNavigation(capture.Client, ports.Nav), ports.Market, () => Now,
                (amount, token) => { token.ThrowIfCancellationRequested(); if (amount >= TimeSpan.FromSeconds(2)) ports.Cooldowns.Add(amount.TotalSeconds); return Task.CompletedTask; },
                TimeSpan.FromMilliseconds(500), forceDispatch: true, checkpointStore: ports.Store, autoResume: true);
            var result = await controller.RunAsync(ports.Route);
            Need(result.State == "Completed" && ports.Cooldowns.SequenceEqual(new[] { 2d }) && ports.Market.Scans == 4, result.Detail);
            Need(ports.Nav.Starts.Select(r => r.Waypoint.PointId).SequenceEqual(ports.Route.Points.Select(p => p.PointId)),
                "真Client第三次安全失敗之後重送導航或回首點，未保留原ConfirmArrival階段。");
            Need(capture.StatusReads == 0, "已清理的安全失敗被重新當成pending取點。");
        });
        foreach (string proof in new[] { "cleanup", "source", "input" })
            await Check("patrol_resume_force_client_invalid_" + proof + "_proof_never_becomes_retry", async () =>
            {
                await using var f = new RealCaptureFixture { FailuresRemaining = 3, InvalidProof = proof };
                bool rejected = false;
                try { await f.Client.CaptureAsync(default); }
                catch (InvalidOperationException error) when (error is not PatrolSourceReadException) { rejected = true; }
                Need(rejected && f.Posts == 1 && f.Delays == 0, "不完整唯讀／來源／清理證據被force分支當成可恢復安全失敗。");
            });
        await Check("patrol_resume_capture_cap_keeps_third_point_and_navigation_id", async () =>
        {
            var f = new Fixture(); bool injected = false;
            f.Nav.AfterStart = () => { if (f.Nav.Starts.Count == 3 && !injected) { injected = true; f.Nav.FailCaptures = 3; } };
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Completed" && result.CompletedPoints == 4, result.Detail);
            Need(f.Nav.Starts.Select(r => r.Waypoint.PointId).SequenceEqual(f.Route.Points.Select(p => p.PointId)), "定位重讀上限後重回首點或重送了第三點導航。");
            Need(f.Cooldowns.Count == 1 && f.Market.Scans == 4, "沒有同點冷卻接續，或新增了重複搜尋。");
        });
        await Check("patrol_resume_capture_cap_retains_partial_plan_without_scanvisible_replay", async () =>
        {
            var f = new Fixture(); f.Market.RemainingOnScan = n => n == 3 ? 1 : 0;
            f.Market.AfterScan = () => { if (f.Market.Scans == 3) f.Nav.FailCaptures = 3; };
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Completed" && f.Cooldowns.Count == 1, result.Detail);
            Need(f.Market.Scans == 4 && f.Market.Continues == 1, "定位失敗後重建了第三點計畫或沒有接續。");
            Need(f.Market.Commands.Single(r => r.Command == ShopCommands.ContinueSavedScan).ExpectedScanPlanId == "plan-3", "接續了其他點計畫。");
            Need(f.Market.Commands.Where(r => r.Command != ShopCommands.GetState).All(r => r.ForceDispatch), "接續分支丟失強制送出。");
        });
        await Check("patrol_resume_range_exit_stops_original_scan_before_renavigation", async () =>
        {
            var f = new Fixture(points: 1); f.Market.RemainingOnScan = _ => 1; f.Market.WorkerPollsOnScan = 2;
            f.Market.AfterScan = () => f.Nav.SetPosition(13, 20);
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Completed" && f.Nav.Starts.Count == 2 && f.Market.Scans == 1 && f.Market.Continues == 1, result.Detail);
            int cancel = f.Events.IndexOf(ShopCommands.CancelScan), settled = f.Events.IndexOf("worker-settled");
            int firstNavigation = f.Events.FindIndex(e => e.StartsWith("navigate-", StringComparison.Ordinal));
            int nextNavigation = f.Events.FindIndex(firstNavigation + 1, e => e.StartsWith("navigate-", StringComparison.Ordinal));
            Need(cancel >= 0 && settled > cancel && nextNavigation > settled, "原商店worker仍未釋放就重新走路。");
            var original = f.Market.Commands.Single(c => c.Command == ShopCommands.ScanVisible);
            Need(f.Market.Commands.Single(c => c.Command == ShopCommands.CancelScan).ExpectedScanJobId == original.RequestId
                && f.Market.Commands.Single(c => c.Command == ShopCommands.ContinueSavedScan).ExpectedScanPlanId == "plan-1",
                "離開範圍取消了其他job或丟失原未送出頁計畫。");
        });
        await Check("patrol_resume_preflight_unsent_exhaustion_cools_same_plan", async () =>
        {
            var f = new Fixture(points: 1, retries: 0); f.Market.RemainingOnScan = _ => 1;
            f.Market.PreflightFailures = 3; f.Market.FailureCode = "NativePreflightReadUnavailable"; f.Market.FailureScope = "Page";
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Completed" && f.Market.Scans == 1 && f.Market.Continues == 3, result.Detail);
            Need(f.Cooldowns.Count == 3 && f.Cooldowns.SequenceEqual(new[] { 2d, 4d, 8d }), "未按同點有界冷卻接續。");
            Need(f.Market.Commands.Where(c => c.Command is ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan)
                .Select(c => c.RequestId).Distinct().Count() == 4, "重送了原已登記請求 ID。");
        });
        await Check("patrol_resume_nonrecoverable_scan_does_not_auto_restart", async () =>
        {
            var f = new Fixture(points: 1, retries: 0); f.Market.RemainingOnScan = _ => 1;
            f.Market.PreflightFailures = 1; f.Market.FailureCode = "NativeCodeMismatch";
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Failed" && f.Cooldowns.Count == 0 && f.Market.Continues == 0, "非暫時原生 ABI 失敗仍自動重啟。");
        });
        await Check("patrol_resume_dispatch_timeout_cools_same_plan_without_replaying_scan", async () =>
        {
            var f = new Fixture(points: 1, retries: 0); f.Market.RemainingOnScan = _ => 1;
            f.Market.PreflightFailures = 3; f.Market.FailureCode = "DispatchReadTimeout";
            f.Market.FailurePhase = "BeforePublish";
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Completed" && f.Market.Scans == 1 && f.Market.Continues == 3, result.Detail);
            Need(f.Cooldowns.SequenceEqual(new[] { 2d, 4d, 8d }) && f.Nav.Starts.Count == 1,
                "送出前唯讀逾時未使用原同點冷卻，或重送導航／搜尋。");
            var requests = f.Market.Commands.Where(c => c.Command is ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan).ToArray();
            Need(requests.Select(c => c.RequestId).Distinct().Count() == 4
                && requests.Skip(1).All(c => c.ExpectedScanPlanId == "plan-1"), "送出前逾時重用了 RequestId 或換計畫。");
        });
        foreach (string phase in new[] { "Native", "Preflight" })
            await Check("patrol_resume_dispatch_timeout_wrong_phase_stops_" + phase, async () =>
            {
                var f = new Fixture(points: 1, retries: 0); f.Market.RemainingOnScan = _ => 1;
                f.Market.PreflightFailures = 1; f.Market.FailureCode = "DispatchReadTimeout"; f.Market.FailurePhase = phase;
                var result = await f.Controller.RunAsync(f.Route);
                Need(result.State == "Failed" && f.Cooldowns.Count == 0 && f.Market.Continues == 0,
                    "沒有 BeforePublish 證據的逾時仍接續。");
            });
        await Check("patrol_resume_dispatch_timeout_stop_during_cooldown_preserves_plan", async () =>
        {
            using var stop = new CancellationTokenSource();
            var f = new Fixture(points: 1, retries: 0); f.Market.RemainingOnScan = _ => 1;
            f.Market.PreflightFailures = 1; f.Market.FailureCode = "DispatchReadTimeout"; f.Market.FailurePhase = "BeforePublish";
            f.OnCooldown = _ => stop.Cancel();
            var result = await f.Controller.RunAsync(f.Route, stop.Token);
            Need(result.State == "Stopped" && f.Market.Scans == 1 && f.Market.Continues == 0
                && f.Store.LoadLatest()!.PlanId == "plan-1", "人工停止後仍接續或丟失原計畫。");
        });
        await Check("patrol_resume_dispatch_timeout_changed_source_stops_before_continue", async () =>
        {
            var f = new Fixture(points: 1, retries: 0); f.Market.RemainingOnScan = _ => 1;
            f.Market.PreflightFailures = 1; f.Market.FailureCode = "DispatchReadTimeout"; f.Market.FailurePhase = "BeforePublish";
            f.OnCooldown = _ => f.Nav.CurrentSource = Source with { CreationFileTime = 789, SessionKey = "different-source" };
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Blocked" && f.Market.Scans == 1 && f.Market.Continues == 0,
                "逾時冷卻後不同來源仍得到接續資格。");
        });
        await Check("patrol_resume_deferred_fresh_receipt_survives_slow_fresh_position_capture", async () =>
        {
            var f = new Fixture(retries: 0); f.Market.RemainingOnScan = n => n == 3 ? 1 : 0;
            bool slowCapture = false, injected = false; string? deferredPlan = null;
            f.Market.TransformRefresh = response =>
            {
                if (f.Nav.Starts.Count != 3 || response.SavedScan?.PlanId != "plan-3" || response.SavedScan.RemainingPages == 0) return response;
                deferredPlan = response.SavedScan.PlanId; slowCapture = true;
                return DeferAllPages(response);
            };
            f.Nav.BeforeCapture = () =>
            {
                if (!slowCapture) return;
                slowCapture = false; injected = true; f.Clock = f.Clock.AddSeconds(3);
            };
            var result = await f.Controller.RunAsync(f.Route);
            Need(injected && deferredPlan == "plan-3", "未注入同計畫新鮮 receipt 後的慢取點。");
            Need(result.State == "Completed" && result.CompletedPoints == 4 && result.CompletedRounds == 1 && result.CleanupConfirmed, result.Detail);
            Need(f.Market.Scans == 4 && f.Market.Continues == 0 && f.Market.Refreshes == 5 && f.Cooldowns.Count == 0,
                "慢唯讀取點後重送搜尋／接續或錯用恢復額度。");
            Need(f.Nav.Starts.Select(r => r.Waypoint.PointId).SequenceEqual(f.Route.Points.Select(p => p.PointId)),
                "慢取點使原定位點重走、跳過或未走完第四點。");
            Need(f.Store.LoadLatest()!.Phase == PatrolCheckpointPhase.Completed, "完成四點卻未保存完整輪數。");
        });
        await Check("patrol_resume_deferred_initially_stale_receipt_still_stops", async () =>
        {
            var f = new Fixture(retries: 0); f.Market.RemainingOnScan = n => n == 3 ? 1 : 0;
            f.Market.TransformRefresh = response => f.Nav.Starts.Count == 3 && response.SavedScan?.PlanId == "plan-3"
                ? DeferAllPages(response) with { TargetSnapshot = response.TargetSnapshot! with { RefreshedAt = f.Clock.AddSeconds(-3) } }
                : response;
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Failed" && result.CompletedPoints == 2 && result.CleanupConfirmed, result.Detail);
            Need(f.Nav.Starts.Count == 3 && f.Market.Scans == 3 && f.Market.Continues == 0 && f.Cooldowns.Count == 0,
                "原先已過期的刷新回覆授予了第三點完成或下一點資格。");
        });
        foreach (string change in new[] { "source", "map", "outside" })
            await Check("patrol_resume_deferred_slow_capture_rejects_" + change, async () =>
            {
                using var stop = new CancellationTokenSource();
                var f = new Fixture(retries: 0); f.Market.RemainingOnScan = n => n == 3 ? 1 : 0;
                bool nextCapture = false, invalidPose = false;
                f.Market.TransformRefresh = response =>
                {
                    if (f.Nav.Starts.Count != 3 || response.SavedScan?.PlanId != "plan-3") return response;
                    nextCapture = true; return DeferAllPages(response);
                };
                f.Nav.BeforeCapture = () =>
                {
                    if (!nextCapture) return;
                    nextCapture = false; invalidPose = true; f.Clock = f.Clock.AddSeconds(3);
                };
                f.Nav.TransformCapture = observation => !invalidPose ? observation : change switch
                {
                    "source" => observation with { Source = observation.Source with { DiskSha256 = new string('B', 64) } },
                    "map" => observation with { MapId = observation.MapId + 1 },
                    _ => observation with { X = observation.X + f.Route.ArrivalRadius + 1 }
                };
                // Outside-range recovery may revisit the same point. A user
                // stop in its first cooldown must prevent that new navigation.
                if (change == "outside") f.OnCooldown = _ => stop.Cancel();
                var result = await f.Controller.RunAsync(f.Route, stop.Token);
                Need(invalidPose && result.State == (change == "outside" ? "Stopped" : "Blocked")
                    && result.CompletedPoints == 2 && result.CleanupConfirmed, result.Detail);
                Need(f.Nav.Starts.Count == 3 && f.Market.Scans == 3 && f.Market.Continues == 0
                    && f.Store.LoadLatest()!.PointIndex == 2, "不符的 fresh pose 被當作第三點完成或第四點送出資格。");
            });
        foreach (string code in new[] { "RefreshReadTimeout", "ContextUnavailable" })
            await Check("patrol_resume_classification_refresh_" + code + "_cools_and_preserves_original_plan", async () =>
            {
                var f = new Fixture(retries: 0); f.Market.RemainingOnScan = n => n == 3 ? 1 : 0;
                bool rejected = false; string? retainedPlan = null;
                f.Market.TransformRefresh = response =>
                {
                    if (response.SavedScan?.PlanId != "plan-3" || rejected) return response;
                    rejected = true; retainedPlan = response.SavedScan.PlanId;
                    return RejectReadOnlyRefresh(response, f.Market.Current, code);
                };
                var result = await f.Controller.RunAsync(f.Route);
                Need(rejected && result.State == "Completed" && result.CompletedPoints == 4 && result.CleanupConfirmed, result.Detail);
                Need(f.Cooldowns.Count > 0 && f.Market.Scans == 4 && f.Market.Continues == 1
                    && f.Market.Refreshes == 7, $"classification-only 冷卻後沒有重新刷新，或重建已登記搜尋：refreshes={f.Market.Refreshes}。");
                var continued = f.Market.Commands.Single(r => r.Command == ShopCommands.ContinueSavedScan);
                Need(continued.TargetSnapshotId == "snapshot-6", "接續沿用冷卻前名單，沒有使用冷卻後的全新刷新。");
                Need(f.Market.Commands.Single(r => r.Command == ShopCommands.ContinueSavedScan).ExpectedScanPlanId == retainedPlan
                    && retainedPlan == "plan-3", "恢復刷新丟失原第三點未送出頁計畫。");
                Need(f.Nav.Starts.Select(r => r.Waypoint.PointId).SequenceEqual(f.Route.Points.Select(p => p.PointId)),
                    "唯讀刷新失敗後重送導航、回首點或沒有走完四點。");
                var mutations = f.Market.Commands.Where(r => r.Command != ShopCommands.GetState).ToArray();
                Need(mutations.All(r => r.ForceDispatch) && mutations.Select(r => r.RequestId).Distinct().Count() == mutations.Length,
                    "同計畫恢復漏了 ForceDispatch 或重用了已登記 RequestId。");
            });
        await Check("patrol_resume_classification_real_source_unavailable_still_stops", async () =>
        {
            var f = new Fixture(retries: 0); f.Market.RemainingOnScan = n => n == 3 ? 1 : 0;
            f.Market.TransformRefresh = response => response.SavedScan?.PlanId == "plan-3"
                ? RejectReadOnlyRefresh(response, f.Market.Current, "SourceUnavailable") : response;
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Blocked" && result.CompletedPoints == 2 && result.CleanupConfirmed, result.Detail);
            Need(f.Cooldowns.Count == 0 && f.Nav.Starts.Count == 3 && f.Market.Scans == 3 && f.Market.Continues == 0,
                "真正來源無法確認被視為安全刷新逾時，仍接續或前往第四點。");
        });
        await Check("patrol_resume_classification_refresh_stop_during_cooldown_keeps_third_point", async () =>
        {
            using var stop = new CancellationTokenSource();
            var f = new Fixture(retries: 0); f.Market.RemainingOnScan = n => n == 3 ? 1 : 0;
            f.Market.TransformRefresh = response => response.SavedScan?.PlanId == "plan-3"
                ? RejectReadOnlyRefresh(response, f.Market.Current, "RefreshReadTimeout") : response;
            f.OnCooldown = _ => stop.Cancel();
            var result = await f.Controller.RunAsync(f.Route, stop.Token);
            Need(result.State == "Stopped" && result.CompletedPoints == 2 && result.CleanupConfirmed, result.Detail);
            Need(f.Cooldowns.Count == 1 && f.Nav.Starts.Count == 3 && f.Market.Scans == 3 && f.Market.Continues == 0
                && f.Market.Refreshes == 4, "冷卻人工停止後仍重刷、接續或前往第四點。");
            var checkpoint = f.Store.LoadLatest()!;
            Need(checkpoint.PointIndex == 2 && checkpoint.PlanId == "plan-3" && checkpoint.Status == "Stopped",
                "classification 冷卻停止遺失原第三點計畫。");
        });
        await Check("patrol_resume_manual_stop_preserves_index_two_and_saved_plan", async () =>
        {
            using var stop = new CancellationTokenSource(); var f = new Fixture();
            f.Market.RemainingOnScan = n => n == 3 ? 1 : 0;
            f.Market.AfterScan = () => { if (f.Market.Scans == 3) stop.Cancel(); };
            var stopped = await f.Controller.RunAsync(f.Route, stop.Token);
            var checkpoint = f.Store.LoadLatest()!;
            Need(stopped.State == "Stopped" && stopped.CompletedPoints == 2 && checkpoint.PointIndex == 2, stopped.Detail);
            Need(checkpoint.PlanId == "plan-3" && checkpoint.Status == "Stopped", "人工停止丟失第三點部分計畫。");
            int commands = f.Market.Commands.Count;
            await Task.Yield(); Need(commands == f.Market.Commands.Count, "人工停止仍自行送指令。");
            f.Market.AfterScan = null;
            var restarted = await f.NewController().RunAsync(f.Route);
            Need(restarted.State == "Completed" && restarted.RunId == stopped.RunId, restarted.Detail);
            Need(f.Nav.Starts.Count == 4 && f.Market.Scans == 4 && f.Market.Continues == 1, "重新按開始回到首點或重送已查頁。");
        });
        await Check("patrol_resume_stop_during_cooldown_requires_new_manual_start", async () =>
        {
            using var stop = new CancellationTokenSource(); var f = new Fixture(); bool injected = false;
            f.Nav.AfterStart = () => { if (f.Nav.Starts.Count == 3 && !injected) { injected = true; f.Nav.FailCaptures = 3; } };
            f.OnCooldown = _ => stop.Cancel();
            var result = await f.Controller.RunAsync(f.Route, stop.Token);
            Need(result.State == "Stopped" && result.CompletedPoints == 2 && f.Market.Scans == 2, result.Detail);
            Need(f.Store.LoadLatest()!.Phase == PatrolCheckpointPhase.ConfirmArrival, "冷卻人工停止遺失階段。");
            f.OnCooldown = null;
            var resumed = await f.NewController().RunAsync(f.Route);
            Need(resumed.State == "Completed" && f.Market.Scans == 4, resumed.Detail);
            Need(f.Nav.Starts.Select(r => r.Waypoint.PointId).Take(3).SequenceEqual(f.Route.Points.Take(3).Select(p => p.PointId)), "首兩點進度丟失。");
        });
        await Check("patrol_resume_new_lc_manual_start_retains_index_but_drops_old_plan", async () =>
        {
            using var stop = new CancellationTokenSource(); var f = new Fixture();
            f.Market.AfterScan = () => { if (f.Market.Scans == 3) stop.Cancel(); };
            var stopped = await f.Controller.RunAsync(f.Route, stop.Token);
            Need(stopped.CompletedPoints == 2 && f.Store.LoadLatest()!.PlanId == "plan-3", stopped.Detail);
            f.Market.AfterScan = null; f.Nav.CurrentSource = Source with { CreationFileTime = 789, SessionKey = "offline-new-session" };
            var resumed = await f.NewController().RunAsync(f.Route);
            Need(resumed.State == "Completed" && f.Nav.Starts[3].Waypoint.PointId == f.Route.Points[2].PointId, resumed.Detail);
            Need(f.Market.Continues == 0 && f.Market.Scans == 5, "新 LC 沿用了舊來源計畫或回到首點。");
            Need(f.Store.LoadLatest()!.Origin!.Source == f.Nav.CurrentSource, "新手動開始沒有保存 fresh origin。");
        });
        await Check("patrol_resume_round_boundary_is_counted_once_after_stop", async () =>
        {
            using var stop = new CancellationTokenSource(); var f = new Fixture(points: 2, rounds: 2);
            f.Controller.Progress += p => { if (p.State == "RoundCompleted" && p.CompletedRounds == 1) stop.Cancel(); };
            var first = await f.Controller.RunAsync(f.Route, stop.Token);
            Need(first.State == "Stopped" && first.CompletedRounds == 1 && first.CompletedPoints == 2, first.Detail);
            var next = await f.NewController().RunAsync(f.Route);
            Need(next.State == "Completed" && next.CompletedRounds == 2 && f.Nav.Starts.Count == 4, next.Detail);
        });
        await Check("patrol_resume_completed_route_starts_new_full_run", async () =>
        {
            var f = new Fixture(points: 2); var first = await f.Controller.RunAsync(f.Route);
            var next = await f.NewController().RunAsync(f.Route);
            Need(first.State == "Completed" && next.State == "Completed" && first.RunId != next.RunId && f.Nav.Starts.Count == 4,
                "已完成輪數仍接續旧run或未從新輪首點開始。");
        });
        await Check("patrol_resume_unknown_accepted_scan_resolves_original_before_refresh", async () =>
        {
            var f = new Fixture(points: 1); var registered = f.Market.PrepareUnknownAcceptedScan();
            f.Store.Save(f.Checkpoint() with { Phase = PatrolCheckpointPhase.Search, PlanId = null, ScanRequest = registered, ScanSettled = false });
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Completed" && f.Market.Scans == 0 && f.Market.Continues == 1, result.Detail);
            Need(f.Market.Commands[0].Command == ShopCommands.CancelScan
                && f.Market.Commands[0].ExpectedScanJobId == registered.RequestId, "新Start未先核對／釋放原未知接受請求。");
        });
        await Check("patrol_resume_unknown_accepted_scan_never_replays_on_foreign_status", async () =>
        {
            var f = new Fixture(points: 1); var registered = f.Market.PrepareUnknownAcceptedScan();
            f.Store.Save(f.Checkpoint() with { Phase = PatrolCheckpointPhase.Search, PlanId = null, ScanRequest = registered, ScanSettled = false });
            f.Market.ForeignStatus = true;
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Blocked" && f.Market.Scans == 0 && f.Market.Continues == 0 && f.Market.Refreshes == 0,
                "無法核對原 scan identity 卻重發搜尋。");
        });
        await Check("patrol_resume_unsettled_original_worker_is_polled_before_new_refresh", async () =>
        {
            var f = new Fixture(points: 1); var original = f.Market.PrepareUnknownAcceptedScan();
            f.Market.StatusPollsRemaining = 3;
            f.Store.Save(f.Checkpoint() with { Phase = PatrolCheckpointPhase.Search, PlanId = "original-plan",
                ScanRequest = original, ScanSettled = false });
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Completed" && f.Market.StateReads == 3 && f.Market.Continues == 1, result.Detail);
            Need(f.Market.Commands[0].Command == ShopCommands.CancelScan
                && f.Market.Commands.Skip(1).Take(3).All(r => r.Command == ShopCommands.GetState), "原worker仍執行即刷新/取代原請求。");
        });
        await Check("patrol_resume_clean_stop_new_navigation_client_does_not_replay_old_job", async () =>
        {
            using var stop = new CancellationTokenSource(); var f = new Fixture();
            f.Market.RemainingOnScan = n => n == 3 ? 1 : 0;
            f.Market.AfterScan = () => { if (f.Market.Scans == 3) stop.Cancel(); };
            var first = await f.Controller.RunAsync(f.Route, stop.Token);
            var checkpoint = f.Store.LoadLatest()!;
            Need(first.State == "Stopped" && first.CleanupConfirmed && checkpoint.NavigationSettled && checkpoint.ScanSettled, first.Detail);
            var freshNavigation = new FakeNavigation(); freshNavigation.SetPosition(30, 20); f.Market.AfterScan = null;
            var next = await f.NewController(freshNavigation).RunAsync(f.Route);
            Need(next.State == "Completed" && freshNavigation.Starts.Count == 1
                && freshNavigation.Starts[0].Waypoint.PointId == f.Route.Points[3].PointId, "cleanStopped的新client因舊job阻擋或重查已完成點。");
        });
        await Check("patrol_resume_unsettled_unknown_navigation_is_retained_and_blocked", async () =>
        {
            var f = new Fixture(); var origin = f.Checkpoint().Origin!;
            var request = new PatrolNavigationRequest(Guid.NewGuid().ToString("N"), origin, f.Route.Points[2], 1);
            f.Store.Save(f.Checkpoint() with { PointIndex = 2, NavigationRequest = request, NavigationSettled = false });
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Blocked" && f.Nav.Starts.Count == 0 && f.Market.Scans == 0, "舊未釋放導航被當成已完成。");
            Need(f.Store.LoadLatest() is { NavigationSettled: false, NavigationRequest: { } kept } && kept.JobId == request.JobId,
                "未能核對的舊job識別被清除。");
        });
        await Check("patrol_resume_changed_route_cannot_hide_previous_native_owner", async () =>
        {
            var f = new Fixture(); var request = new PatrolNavigationRequest(Guid.NewGuid().ToString("N"), f.Checkpoint().Origin!, f.Route.Points[2], 1);
            f.Store.Save(f.Checkpoint() with { PointIndex = 2, NavigationRequest = request, NavigationSettled = false });
            var nextRoute = f.Route with { Revision = Guid.NewGuid().ToString("N") };
            var result = await f.Controller.RunAsync(nextRoute);
            Need(result.State == "Blocked" && !result.CleanupConfirmed && f.Nav.Starts.Count == 0, "改路線跳過原native owner。");
            Need(f.Store.LoadLatest() is { NavigationSettled: false, PointIndex: 2 } kept
                && kept.RouteRevision == f.Route.Revision && kept.NavigationRequest?.JobId == request.JobId,
                "新route空checkpoint覆蓋了舊未知工作及進度。");
        });
        await Check("patrol_resume_committed_point_phase_advances_without_requery_after_rebind", async () =>
        {
            var f = new Fixture();
            f.Store.Save(f.Checkpoint() with { PointIndex = 1, Phase = PatrolCheckpointPhase.PointCompleted });
            f.Nav.CurrentSource = Source with { CreationFileTime = 789, SessionKey = "offline-new-session" };
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Completed" && f.Nav.Starts.Count == 2 && f.Nav.Starts[0].Waypoint.PointId == f.Route.Points[2].PointId,
                "已提交完成階段因重啟或移位重查同點。");
        });
        await Check("patrol_resume_store_appends_and_refuses_damaged_latest", () =>
        {
            var f = new Fixture(); var first = f.Store.Save(f.Checkpoint());
            var next = f.Store.Save(first with { PointIndex = 2 });
            string[] paths = Directory.GetFiles(f.Store.DirectoryPath, "*.checkpoint.json").Order(StringComparer.Ordinal).ToArray();
            Need(paths.Length == 2 && next.Sequence == first.Sequence + 1 && next.At > first.At, "恢復紀錄覆蓋了舊版本。");
            File.WriteAllText(paths[^1], "{ broken");
            Throws(() => f.Store.LoadLatest(), "損壞最新版被舊檔 fallback 掩蓋。");
            return Task.CompletedTask;
        });
        await Check("patrol_resume_same_revision_mutation_cannot_restore_different_route", async () =>
        {
            var f = new Fixture(); f.Store.Save(f.Checkpoint() with { PointIndex = 2 });
            var changed = f.Route with { ArrivalRadius = f.Route.ArrivalRadius + 1 };
            var result = await f.Controller.RunAsync(changed);
            Need(result.State == "Blocked" && f.Nav.Starts.Count == 0 && f.Market.Scans == 0, "相同revision不同內容仍接續。");
        });
        await Check("patrol_resume_import_latest_stopped_source_only_starts_third_point", async () =>
        {
            var f = new Fixture(); string run = Guid.NewGuid().ToString("N"); string logs = f.WriteLogs(run, "Stopped", 2);
            var imported = f.Store.ImportLatestStoppedRun(f.Route, Source, 11, logs, run);
            Need(imported.Origin is null && imported.ImportedSource == Source && imported.PointIndex == 2
                && imported.ImportedLogSha256.Count == 1 && imported.Status == "Stopped", "遷移捏造位置或丟失停止索引。");
            Need(f.Nav.Starts.Count == 0 && f.Market.Commands.Count == 0, "匯入紀錄自行開始遊戲操作。");
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Completed" && result.RunId == run && f.Nav.Starts.Count == 2
                && f.Nav.Starts[0].Waypoint.PointId == f.Route.Points[2].PointId, result.Detail);
        });
        foreach (string latest in new[] { "Failed", "Completed", "NewStart" })
            await Check("patrol_resume_import_never_uses_older_stop_after_" + latest, () =>
            {
                var f = new Fixture(); string logs = f.WriteLogs(Guid.NewGuid().ToString("N"), "Stopped", 2);
                f.AppendLogs(logs, Guid.NewGuid().ToString("N"), latest, latest == "Completed" ? 4 : 0);
                Throws(() => f.Store.ImportLatestStoppedRun(f.Route, Source, 11, logs), "套用了較舊停止紀錄。");
                Need(f.Store.LoadLatest() is null, "匯入拒絕後仍產生恢復紀錄。"); return Task.CompletedTask;
            });
        await Check("patrol_resume_import_cannot_replace_existing_checkpoint", () =>
        {
            var f = new Fixture(); var original = f.Store.Save(f.Checkpoint() with { PointIndex = 3 });
            string logs = f.WriteLogs(Guid.NewGuid().ToString("N"), "Stopped", 2);
            Throws(() => f.Store.ImportLatestStoppedRun(f.Route, Source, 11, logs), "舊 LOG 覆蓋了新版進度。");
            Need(f.Store.LoadLatest() is { } current && current.Sequence == original.Sequence && current.PointIndex == 3,
                "拒絕匯入後更改了進度。"); return Task.CompletedTask;
        });
        return checks.ToArray();
    }

    private static void Need(bool ok, string detail) { if (!ok) throw new InvalidOperationException(detail); }
    private static void Throws(Action action, string detail)
    { try { action(); } catch { return; } throw new InvalidOperationException(detail); }

    private sealed class CaptureNavigation(PatrolNavigationClient capture, FakeNavigation navigation) : IPatrolNavigation
    {
        public Task<PatrolObservation> CaptureAsync(CancellationToken token, PatrolObservation? expected = null) => capture.CaptureAsync(token, expected);
        public Task<PatrolNavigationState> StartAsync(PatrolNavigationRequest request, CancellationToken token) => navigation.StartAsync(request, token);
        public Task<PatrolNavigationState> GetStateAsync(string jobId, CancellationToken token) => navigation.GetStateAsync(jobId, token);
        public Task<PatrolNavigationState> StopAsync(string expectedJobId, CancellationToken token) => navigation.StopAsync(expectedJobId, token);
    }

    private sealed class RealCaptureFixture : IAsyncDisposable
    {
        internal readonly PatrolNavigationClient Client;
        internal int Posts, StatusReads, Delays, FailuresRemaining;
        internal string FailureCode = "READ_CAPTURE_MODULE_PATH_FAILED";
        internal string? InvalidProof;
        internal RealCaptureFixture(FakeNavigation? navigation = null)
        {
            navigation ??= new FakeNavigation();
            Client = new(() => new HubSnapshot { Source = Source, State = "Reading", PublishedAt = Now,
                Fields = new() { ["selfId"] = FieldReading.Observed(11UL, Now),
                    ["position"] = FieldReading.Observed(new { x = navigation.Position.X, y = navigation.Position.Y }, Now) } },
                (method, path, payload, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (method == HttpMethod.Get) { StatusReads++; throw new InvalidOperationException("已清理取點不應再輪詢status。"); }
                    Need(method == HttpMethod.Post && path == "/api/capture", "真capture測試不允許行走／其他transport。");
                    Posts++; var request = JsonSerializer.SerializeToElement(payload);
                    bool failed = FailuresRemaining > 0; if (failed) FailuresRemaining--;
                    var wire = new { pid = Source.ProcessId, creationFileTime = Source.CreationFileTime.ToString(CultureInfo.InvariantCulture),
                        imagePath = Source.ImagePath, sha256 = Source.DiskSha256, imageBase = Source.ImageBase,
                        imageSize = Source.ImageSize, selfId = "11" };
                    var body = new Dictionary<string, object?>
                    {
                        ["ok"] = !failed, ["captureId"] = request.GetProperty("captureId").GetString(),
                        ["expectedSource"] = request.GetProperty("expectedSource"), ["source"] = wire,
                        ["readOnly"] = true, ["inputSent"] = false, ["accepted"] = true,
                        ["terminal"] = true, ["cleanupConfirmed"] = true, ["state"] = failed ? "Failed" : "Completed",
                        ["error"] = failed ? FailureCode : "", ["mapId"] = 4, ["x"] = navigation.Position.X,
                        ["y"] = navigation.Position.Y, ["standing"] = false, ["sampledAtUtc"] = Now.ToString("O")
                    };
                    if (failed && InvalidProof == "cleanup") body["cleanupConfirmed"] = false;
                    if (failed && InvalidProof == "input") body["inputSent"] = true;
                    if (failed && InvalidProof == "source") body["expectedSource"] = new { pid = 999 };
                    return Task.FromResult(new PatrolNavigationReply(failed ? HttpStatusCode.Conflict : HttpStatusCode.OK,
                        JsonSerializer.SerializeToElement(body)));
                }, () => Now, (_, token) => { token.ThrowIfCancellationRequested(); Delays++; return Task.CompletedTask; }, forceDispatch: true);
        }
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    private static ShopCommandResponse DeferAllPages(ShopCommandResponse response) => response with
    {
        SavedScan = response.SavedScan! with { AvailablePages = 0, DeferredPages = response.SavedScan!.RemainingPages,
            CanContinue = false, ContinueBlockCode = "NoAvailableOwners", ContinueBlockReason = "offline owner no longer shopping" }
    };

    private static ShopCommandResponse RejectReadOnlyRefresh(ShopCommandResponse response, ShopCommandResponse retained, string code) => response with
    {
        State = "Rejected", FailureCode = code, FailureScope = "Global", FailurePhase = "BeforePublish",
        NativeEntered = false, NativeReturned = false, ResponseVerified = false, WorkSettled = true,
        TargetSnapshot = retained.TargetSnapshot is { } previous ? previous with { CanDispatch = false } : null,
        SavedScan = retained.SavedScan, Scan = retained.Scan
    };

    private sealed class Fixture
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "LcPatrolResumeOffline", Guid.NewGuid().ToString("N"));
        internal readonly PatrolCheckpointStore Store;
        internal readonly PatrolRouteSnapshot Route;
        internal readonly FakeNavigation Nav = new();
        internal readonly FakeMarket Market;
        internal readonly PatrolController Controller;
        internal readonly List<double> Cooldowns = [];
        internal readonly List<string> Events = [];
        internal DateTimeOffset Clock = Now;
        internal Action<double>? OnCooldown;
        internal Fixture(int points = 4, int rounds = 1, int retries = 1)
        {
            Store = new(Root); Route = new() { Revision = Guid.NewGuid().ToString("N"), SavedAt = Now,
                RoundCount = rounds, RetryLimit = retries, ArrivalRadius = 1,
                Points = Enumerable.Range(0, points).Select(i => new PatrolWaypoint(Guid.NewGuid().ToString("N"), "点" + i, "4", "離線圖", 10 + i * 10, 20)).ToArray() };
            Nav.UtcNow = () => Clock; Market = new(Nav); Nav.Observer = Market.Observer = Events.Add; Controller = NewController();
        }
        internal PatrolController NewController(IPatrolNavigation? alternateNavigation = null) => new(alternateNavigation ?? Nav, Market, () => Clock, (interval, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (interval >= TimeSpan.FromSeconds(2)) { Cooldowns.Add(interval.TotalSeconds); OnCooldown?.Invoke(interval.TotalSeconds); }
            token.ThrowIfCancellationRequested(); return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(500), forceDispatch: true, checkpointStore: Store, autoResume: true);
        internal PatrolCheckpoint Checkpoint() => new()
        {
            RunId = Guid.NewGuid().ToString("N"), RouteRevision = Route.Revision, RouteFingerprint = PatrolCheckpointStore.Fingerprint(Route),
            TotalRounds = Route.RoundCount, TotalPoints = Route.Points.Length, CurrentRound = 1,
            Phase = PatrolCheckpointPhase.Navigate, Origin = new(Source, 11, 4, 10, 20, Now, false), Status = "Stopped"
        };
        internal string WriteLogs(string run, string state, int index)
        {
            string path = Path.Combine(Root, "old-logs"); Directory.CreateDirectory(path); AppendLogs(path, run, state, index); return path;
        }
        internal void AppendLogs(string path, string run, string state, int index)
        {
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            string start = JsonSerializer.Serialize(new { kind = "PATROL_START", data = new { revision = Route.Revision,
                roundCount = Route.RoundCount, retryLimit = Route.RetryLimit, arrivalRadius = Route.ArrivalRadius, points = Route.Points } }, json);
            string finish = JsonSerializer.Serialize(new { kind = "PATROL_FINISHED", data = new { runId = run, state,
                completedPoints = index, totalPoints = Route.Points.Length, retriesUsed = 0, cleanupConfirmed = true,
                currentRound = 1, totalRounds = Route.RoundCount, completedRounds = state == "Completed" ? Route.RoundCount : 0 } }, json);
            File.AppendAllText(Path.Combine(path, "0001.jsonl"), start + Environment.NewLine + (state == "NewStart" ? "" : finish + Environment.NewLine));
        }
    }

    private sealed class FakeNavigation : IPatrolNavigation
    {
        internal SourceIdentity CurrentSource = Source;
        internal readonly List<PatrolNavigationRequest> Starts = [];
        internal int FailCaptures;
        internal Action? AfterStart;
        internal Action<string>? Observer;
        internal Func<DateTimeOffset> UtcNow = () => Now;
        internal Action? BeforeCapture;
        internal Func<PatrolObservation, PatrolObservation>? TransformCapture;
        private int x = 10, y = 20;
        private PatrolNavigationState current = new();
        internal (int X, int Y) Position => (x, y);
        internal void SetPosition(int nextX, int nextY) { x = nextX; y = nextY; }
        public Task<PatrolObservation> CaptureAsync(CancellationToken token, PatrolObservation? expected = null)
        {
            token.ThrowIfCancellationRequested();
            if (FailCaptures > 0) { FailCaptures--; throw new PatrolSourceReadException("SourceUnavailableTimeout", "offline capture bounded cap"); }
            BeforeCapture?.Invoke();
            var observation = new PatrolObservation(CurrentSource, 11, 4, x, y, UtcNow(), false);
            return Task.FromResult(TransformCapture?.Invoke(observation) ?? observation);
        }
        public Task<PatrolNavigationState> StartAsync(PatrolNavigationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Starts.Add(request); Observer?.Invoke("navigate-" + request.Waypoint.PointId);
            x = request.Waypoint.X; y = request.Waypoint.Y;
            current = new() { JobId = request.JobId, WaypointId = request.Waypoint.PointId, TargetX = x, TargetY = y,
                ArrivalRadius = request.ArrivalRadius, Source = CurrentSource, SelfId = 11, MapId = 4, X = x, Y = y, SampledAt = UtcNow(),
                State = "Completed", Completed = true, Terminal = true, CleanupConfirmed = true, WorkSettled = true, PositionVerified = true };
            AfterStart?.Invoke(); return Task.FromResult(current);
        }
        public Task<PatrolNavigationState> GetStateAsync(string jobId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Need(jobId == current.JobId, "wrong navigation job"); return Task.FromResult(current); }
        public Task<PatrolNavigationState> StopAsync(string expectedJobId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Need(expectedJobId == current.JobId, "wrong stop job"); return Task.FromResult(current); }
    }

    private sealed class FakeMarket(FakeNavigation navigation) : IPatrolMarket
    {
        internal readonly List<ShopCommandRequest> Commands = [];
        internal int Scans, Continues, Refreshes, PreflightFailures, StatusPollsRemaining, StateReads, WorkerPollsOnScan;
        internal string FailureCode = "NativeCodeUnavailable", FailurePhase = "Preflight", FailureScope = "Global";
        internal Func<int, int> RemainingOnScan = _ => 0;
        internal Action? AfterScan;
        internal bool ForeignStatus;
        internal Action<string>? Observer;
        internal Func<ShopCommandResponse, ShopCommandResponse>? TransformRefresh;
        private ShopCommandResponse current = new();
        internal ShopCommandResponse Current => current;
        internal void SetRecordedNativeCodeFailure() => current = current with { Scan = current.Scan with
            { FailureCode = "NativeCodeUnavailable", FailureScope = "Global", FailurePhase = "Preflight", FailureRva = "0x76BEB0", FailureDetail = "PAGE_NOACCESS offline" } };
        internal ShopCommandRequest PrepareUnknownAcceptedScan()
        {
            var request = new ShopCommandRequest { RequestId = "original-unknown-scan", Command = ShopCommands.ScanVisible,
                ExpectedSourceSessionKey = Source.SessionKey, TargetSnapshotId = "original-snapshot", ForceDispatch = true };
            current = Response(request, "original-plan", 1, null); return request;
        }
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Commands.Add(request); Observer?.Invoke(request.Command);
            if (request.Command == ShopCommands.RefreshTargets)
            {
                Refreshes++;
                var response = current with { RequestId = request.RequestId, State = "TargetsRefreshed",
                    CurrentSourceSessionKey = navigation.CurrentSource.SessionKey,
                    TargetSnapshot = new() { SnapshotId = "snapshot-" + Refreshes, SourceSessionKey = navigation.CurrentSource.SessionKey,
                        SelfId = 11, RefreshedAt = navigation.UtcNow(), CanDispatch = true } };
                return Task.FromResult(TransformRefresh?.Invoke(response) ?? response);
            }
            if (request.Command is ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan)
            {
                string plan; int remaining; string? failure = null;
                if (request.Command == ShopCommands.ScanVisible) { Scans++; plan = "plan-" + Scans; remaining = RemainingOnScan(Scans); }
                else { Continues++; Need(request.ExpectedScanPlanId == current.SavedScan?.PlanId, "wrong saved plan"); plan = current.SavedScan!.PlanId; remaining = 0; }
                if (PreflightFailures > 0) { PreflightFailures--; remaining = 1; failure = FailureCode; }
                current = Response(request, plan, remaining, failure);
                if (request.Command == ShopCommands.ScanVisible && WorkerPollsOnScan > 0)
                {
                    StatusPollsRemaining = WorkerPollsOnScan;
                    current = current with { WorkSettled = false, Scan = current.Scan with { Running = true } };
                }
                if (request.Command == ShopCommands.ScanVisible) AfterScan?.Invoke();
                return Task.FromResult(current);
            }
            if (request.Command == ShopCommands.CancelScan)
            {
                Need(request.ExpectedScanJobId == current.Scan.JobId, "wrong cancel job");
                current = current with { WorkSettled = StatusPollsRemaining == 0,
                    Scan = current.Scan with { Running = StatusPollsRemaining != 0, StopReason = "Cancelled" } };
                return Task.FromResult(current with { RequestId = request.RequestId,
                    Scan = ForeignStatus ? current.Scan with { JobId = "foreign-job" } : current.Scan });
            }
            Need(request.Command == ShopCommands.GetState, "unknown merchant command");
            StateReads++;
            if (StatusPollsRemaining > 0)
            {
                StatusPollsRemaining--;
                current = current with { WorkSettled = StatusPollsRemaining == 0,
                    Scan = current.Scan with { Running = StatusPollsRemaining != 0 } };
                if (StatusPollsRemaining == 0) Observer?.Invoke("worker-settled");
            }
            return Task.FromResult(current with { RequestId = request.RequestId,
                Scan = ForeignStatus ? current.Scan with { JobId = "foreign-job" } : current.Scan });
        }
        private ShopCommandResponse Response(ShopCommandRequest request, string plan, int remaining, string? failure) => new()
        {
            RequestId = request.RequestId, State = "ScanStarted", WorkSettled = true, CurrentSourceSessionKey = navigation.CurrentSource.SessionKey,
            Scan = new() { JobId = request.RequestId, SourceSessionKey = navigation.CurrentSource.SessionKey,
                TargetSnapshotId = request.TargetSnapshotId, Running = false, FailureCode = failure,
                FailurePhase = failure is null ? null : FailurePhase, FailureScope = failure is null ? null : FailureScope,
                FailureRva = failure is null ? null : "0x76BEB0", FailureDetail = failure is null ? null : "PAGE_NOACCESS offline" },
            SavedScan = new() { PlanId = plan, SourceSessionKey = navigation.CurrentSource.SessionKey,
                TotalPages = 1, SearchedPages = 1 - remaining, ConfirmedPages = 0, RemainingPages = remaining,
                AvailablePages = remaining, CanContinue = remaining > 0 },
            TargetSnapshot = new() { SnapshotId = request.TargetSnapshotId!, SourceSessionKey = navigation.CurrentSource.SessionKey,
                SelfId = 11, RefreshedAt = navigation.UtcNow(), CanDispatch = true }
        };
    }
}
