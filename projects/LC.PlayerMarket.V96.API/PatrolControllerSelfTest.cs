using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Runs the actual coordinator with in-memory ports. Never starts a process or reads LC.</summary>
internal static class PatrolControllerSelfTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly SourceIdentity Source = new(123, 456, "offline-LC.exe", "0x140000000", 1, 65536, new string('A', 64), "offline-session");

    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> test)
        {
            try { await test().ConfigureAwait(false); checks.Add(new(name, true, "通過；純離線 fake ports")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        await Check("patrol_two_points_one_round_in_exact_order", async () =>
        {
            var f = new Fixture(); var result = await f.Run(2);
            Need(result.State == "Completed" && result.CompletedPoints == 2 && result.CleanupConfirmed, result.Detail);
            Need(f.Events.Where(e => e is "navigate" or "refresh" or "scan").SequenceEqual(new[] { "navigate", "refresh", "scan", "navigate", "refresh", "scan" }), "未按到點、刷新、搜尋順序完成單輪");
            Need(f.Nav.Starts == 2 && f.Market.Scans == 2 && f.Market.Continues == 0, "完成後自行重跑一輪");
            Need(f.Nav.JobIds.All(id => Guid.TryParseExact(id, "N", out _) && id == id.ToLowerInvariant()), "導航jobId不符合adapter32小寫hex契約");
        });
        await Check("patrol_each_run_captures_new_origin_then_fences_every_subsequent_capture", async () =>
        {
            var f = new Fixture(); var first = await f.Run(1);
            Need(first.State == "Completed" && f.Nav.CaptureExpectations[0] is null, "首次取點不應綁歷史來源");
            var firstOrigin = new PatrolObservation(Source, 11, 4, 100, 100, Now, true);
            Need(f.Nav.CaptureExpectations.Count > 1 && f.Nav.CaptureExpectations.Skip(1).All(e => e == firstOrigin), "同輪取點沒有完整綁定首筆來源");
            int boundary = f.Nav.CaptureExpectations.Count;
            f.Nav.TransformCapture = (_, observed) => observed with { X = observed.X + 2 };
            var second = await f.Run(1);
            Need(second.State == "Completed" && f.Nav.CaptureExpectations[boundary] is null, "下一輪仍綁定上一輪來源");
            var secondOrigin = firstOrigin with { X = 102 };
            Need(f.Nav.CaptureExpectations.Skip(boundary + 1).Any()
                && f.Nav.CaptureExpectations.Skip(boundary + 1).All(e => e == secondOrigin), "下一輪未改用本輪取得的新origin");
        });
        await Check("patrol_retry_only_saved_unsent_pages", async () =>
        {
            var f = new Fixture(); f.Market.Outcomes.Enqueue(new(Remaining: 1));
            var result = await f.Run(1);
            Need(result.State == "Completed" && result.RetriesUsed == 1, result.Detail);
            Need(f.Market.Scans == 1 && f.Market.Continues == 1 && f.Market.Refreshes == 2, "失敗後重建計畫或未刷新接續");
            var first = f.Market.Commands.Single(c => c.Command == ShopCommands.ScanVisible);
            var retry = f.Market.Commands.Single(c => c.Command == ShopCommands.ContinueSavedScan);
            Need(first.RequestId != retry.RequestId && retry.ExpectedScanPlanId == f.Market.Current.SavedScan!.PlanId
                && first.TargetSnapshotId != retry.TargetSnapshotId, "接續未綁定原計畫與新刷新");
        });
        await Check("patrol_retry_zero_stops_without_next_point", async () =>
        {
            var f = new Fixture(); f.Market.Outcomes.Enqueue(new(Remaining: 1));
            var result = await f.Run(2, retries: 0);
            Need(result.State == "Failed" && f.Nav.Starts == 1 && f.Market.Continues == 0, "零次接續仍重試或前往下一點");
        });
        await Check("patrol_retry_limit_is_initial_plus_configured_retries", async () =>
        {
            var f = new Fixture(); for (int i = 0; i < 4; i++) f.Market.Outcomes.Enqueue(new(Remaining: 1));
            var result = await f.Run(2, retries: 3);
            Need(result.State == "Failed" && result.RetriesUsed == 3 && f.Market.Scans == 1 && f.Market.Continues == 3 && f.Nav.Starts == 1, "接續上限計數錯誤");
        });
        await Check("patrol_sent_unverified_rows_are_not_resent", async () =>
        {
            var f = new Fixture(); f.Market.Outcomes.Enqueue(new(Confirmed: 0));
            var result = await f.Run(2);
            Need(result.State == "Completed" && f.Nav.Starts == 2 && f.Market.Continues == 0, "已確認原生返回但列未驗證被重送或誤當未定原生");
        });
        await Check("patrol_uncertain_native_blocks_retry_and_next", async () =>
        {
            var f = new Fixture(); f.Market.Outcomes.Enqueue(new(Remaining: 1, Uncertain: 1));
            var result = await f.Run(2);
            Need(result.State == "Blocked" && f.Nav.Starts == 1 && f.Market.Continues == 0 && !result.CleanupConfirmed, "未定原生結果仍接續");
        });
        await Check("patrol_session_block_is_not_cleared_by_restart", async () =>
        {
            var f = new Fixture(); f.Market.Outcomes.Enqueue(new(SessionBlocked: true));
            var first = await f.Run(2); int starts = f.Nav.Starts;
            var second = await f.Run(1);
            Need(first.State == "Blocked" && second.State == "Blocked" && f.Nav.Starts == starts, "同協調器重啟清除未完成鎖");
        });
        await Check("patrol_map_change_prevents_refresh", async () =>
        {
            var f = new Fixture(); f.Nav.TransformCapture = (n, value) => n >= 3 ? value with { MapId = 5 } : value;
            var result = await f.Run(2);
            Need(result.State == "Blocked" && f.Market.Refreshes == 0, "換圖後仍查店");
        });
        await Check("patrol_full_source_change_with_same_session_is_rejected", async () =>
        {
            var f = new Fixture(); f.Nav.TransformCapture = (n, value) => n >= 3 ? value with { Source = Source with { CreationFileTime = 999 } } : value;
            var result = await f.Run(1);
            Need(result.State == "Blocked" && f.Market.Refreshes == 0, "只比SessionKey而忽略完整來源");
        });
        await Check("patrol_foreign_navigation_job_is_never_stopped", async () =>
        {
            var f = new Fixture(); f.Nav.TransformState = state => state with { JobId = "foreign-job" };
            var result = await f.Run(1);
            Need(result.State == "Blocked" && f.Nav.Stops.Count == 0 && f.Market.Refreshes == 0, "接管或停止別人的導航");
        });
        await Check("patrol_navigation_target_is_bound_to_waypoint", async () =>
        {
            var f = new Fixture(); f.Nav.TransformState = state => state with { TargetX = state.TargetX + 1 };
            var result = await f.Run(1);
            Need(result.State == "Blocked" && f.Market.Refreshes == 0, "錯誤目的地完成回覆被採用");
        });
        await Check("patrol_arrival_requires_cleanup_and_two_standing_samples", async () =>
        {
            foreach (bool cleanup in new[] { false, true })
            {
                var f = new Fixture(); f.Nav.TransformState = state => state with { CleanupConfirmed = cleanup, StandingSamples = cleanup ? 1 : 2 };
                var result = await f.Run(1);
                Need(result.State == "Blocked" && f.Market.Refreshes == 0, "缺少清理或兩次站定仍查店");
            }
        });
        await Check("patrol_default_two_rejects_three_cells_before_search", async () =>
        {
            var f = new Fixture(); f.Nav.TransformState = state => state with { X = state.TargetX + 3 };
            var result = await f.Run(1);
            Need(result.State == "Failed" && f.Market.Refreshes == 0 && f.Nav.Radii.SequenceEqual([2]), "預設容差兩格卻接受三格偏移");
        });
        foreach (int radius in new[] { 1, 2, 10 })
        {
            await Check("patrol_radius_" + radius + "_diagonal_boundary_and_whole_route_snapshot", async () =>
            {
                var f = new Fixture(); f.Nav.TransformState = state => state with { X = state.TargetX + radius, Y = state.TargetY + radius };
                var result = await f.Run(2, radius: radius);
                Need(result.State == "Completed" && f.Market.Scans == 2 && f.Nav.Radii.SequenceEqual([radius, radius]), "對角線格距或整輪容差不一致");
            });
            await Check("patrol_radius_" + radius + "_plus_one_never_refreshes", async () =>
            {
                var f = new Fixture(); f.Nav.TransformState = state => state with { X = state.TargetX + radius + 1 };
                var result = await f.Run(1, radius: radius);
                Need(result.State == "Failed" && f.Market.Refreshes == 0 && f.Market.Scans == 0, "設定容差外仍刷新或查詢");
            });
            await Check("patrol_radius_" + radius + "_point_recapture_uses_same_radius", async () =>
            {
                var f = new Fixture(); f.Nav.TransformCapture = (n, value) => n >= 3 ? value with { X = value.X + radius + 1 } : value;
                var result = await f.Run(1, radius: radius);
                Need(result.State == "Failed" && f.Nav.Starts == 1 && f.Market.Refreshes == 0, "到點後捕捉位置沿用不同容差");
            });
        }
        await Check("patrol_receipt_radius_mismatch_blocks_handoff_and_cleanup_claim", async () =>
        {
            var f = new Fixture(); f.Nav.TransformState = state => state with { ArrivalRadius = 5 };
            var result = await f.Run(1);
            Need(result.State == "Blocked" && !result.CleanupConfirmed && f.Nav.Starts == 1 && f.Market.Refreshes == 0, "不符的導航容差被當作本輪完成證據");
        });
        foreach (int invalid in new[] { 0, 11 })
            await Check("patrol_invalid_radius_" + invalid + "_starts_nothing", async () =>
            {
                var f = new Fixture(); var result = await f.Run(1, radius: invalid);
                Need(result.State == "Failed" && f.Nav.Captures == 0 && f.Nav.Starts == 0 && f.Market.Commands.Count == 0, "無效容差仍啟動流程");
            });
        await Check("patrol_navigation_user_stop_cannot_become_success", async () =>
        {
            var f = new Fixture(); f.Nav.TransformState = state => state with { UserStop = true };
            var result = await f.Run(1);
            Need(result.State == "Stopped" && f.Market.Refreshes == 0, "手動停止被完成回覆蓋過");
        });
        await Check("patrol_foreign_market_job_is_never_cancelled", async () =>
        {
            var f = new Fixture(); f.Market.TransformScan = state => state with { Scan = state.Scan with { JobId = "foreign-market" } };
            var result = await f.Run(2);
            Need(result.State == "Blocked" && f.Market.Cancels.Count == 0 && f.Nav.Starts == 1, "停止其他商店工作或前往下一點");
        });
        await Check("patrol_changed_plan_is_never_adopted", async () =>
        {
            var f = new Fixture(); f.Market.Outcomes.Enqueue(new(SettlementPolls: 1));
            f.Market.TransformPoll = state => state with { SavedScan = state.SavedScan! with { PlanId = "another-plan" } };
            var result = await f.Run(1);
            Need(result.State == "Blocked" && f.Market.Cancels.Count == 0, "不同計畫的狀態被採用或停止");
        });
        await Check("patrol_lost_scan_acceptance_queries_original_job_once", async () =>
        {
            var f = new Fixture(); f.Market.ThrowScanAcceptance = true;
            var result = await f.Run(1);
            Need(result.State == "Completed" && f.Market.Scans == 1 && f.Market.Continues == 0, "timeout後重送ScanVisible");
        });
        await Check("patrol_unknown_timeout_never_guesses_not_sent", async () =>
        {
            var f = new Fixture(); f.Market.ThrowScanAcceptance = true;
            f.Market.TransformPoll = state => state with { Scan = state.Scan with { JobId = "unrelated" } };
            var result = await f.Run(1);
            Need(result.State == "Blocked" && f.Market.Scans == 1 && f.Market.Cancels.Count == 0, "無法確認timeout工作卻重送或取消其他工作");
        });
        await Check("patrol_cancel_market_poll_stops_only_owned_job_and_waits", async () =>
        {
            var f = new Fixture(); using var cancel = new CancellationTokenSource();
            f.Market.Outcomes.Enqueue(new(SettlementPolls: 2));
            f.Market.AfterScan = cancel.Cancel;
            var result = await f.Run(2, token: cancel.Token);
            Need(result.State == "Stopped" && result.CleanupConfirmed && f.Nav.Starts == 1, result.Detail);
            Need(f.Market.Cancels.Count == 1 && f.Market.Cancels[0].ExpectedScanJobId == f.Market.LastJobId, "取消未指定本巡迴商店工作");
        });
        await Check("patrol_cleanup_timeout_is_blocked_not_safe_stop", async () =>
        {
            var f = new Fixture(cleanupTimeout: TimeSpan.FromMilliseconds(30)); using var cancel = new CancellationTokenSource();
            f.Market.Outcomes.Enqueue(new(SettlementPolls: int.MaxValue)); f.Market.AfterScan = cancel.Cancel; f.Market.NeverSettle = true;
            var result = await f.Run(1, token: cancel.Token);
            Need(result.State == "Blocked" && !result.CleanupConfirmed, "未完成清理逾時被宣稱安全停止");
        });
        await Check("patrol_explicit_30ms_deadline_refuses_60ms_fake_hold", async () =>
        {
            var f = new Fixture(cleanupTimeout: TimeSpan.FromMilliseconds(30));
            using var caller = new CancellationTokenSource();
            CancellationToken observedCleanup = default; Task? held = null;
            f.Market.BeforeSend = (_, token) =>
            {
                if (caller.IsCancellationRequested && token != caller.Token) observedCleanup = token;
            };
            f.DelayOverride = (_, token) => caller.IsCancellationRequested && token != caller.Token
                ? held = Task.Delay(60, CancellationToken.None) : Task.Delay(1, token);
            f.Market.Outcomes.Enqueue(new(SettlementPolls: 2)); f.Market.AfterScan = caller.Cancel;
            var result = await f.Run(2, token: caller.Token);
            Need(held is not null, "明確deadline反例未進入自有cleanup delay。");
            await held!.WaitAsync(TimeSpan.FromSeconds(2));
            Need(result.State == "Blocked" && !result.CleanupConfirmed && observedCleanup.IsCancellationRequested
                && f.Nav.Starts == 1 && f.Market.Scans == 1 && f.Market.Continues == 0
                && f.Market.Cancels.Count == 1 && f.Market.Cancels[0].ExpectedScanJobId == f.Market.LastJobId,
                "30ms cleanup截止後仍宣稱安全或發送下一工作。");
        });
        await Check("patrol_cleanup_foreign_job_is_not_canceled", async () =>
        {
            var f = new Fixture(); using var caller = new CancellationTokenSource();
            f.Market.Outcomes.Enqueue(new(SettlementPolls: 2)); f.Market.AfterScan = caller.Cancel;
            f.Market.TransformAnyPoll = response => caller.IsCancellationRequested
                ? response with { Scan = response.Scan with { JobId = "foreign-cleanup-job" } } : response;
            var result = await f.Run(2, token: caller.Token);
            Need(result.State == "Blocked" && !result.CleanupConfirmed && f.Market.Cancels.Count == 0
                && f.Nav.Starts == 1 && f.Market.Scans == 1 && f.Market.Continues == 0,
                "cleanup接管或取消不屬於本巡迴的工作。");
        });
        await Check("patrol_cleanup_changed_plan_never_becomes_settled", async () =>
        {
            var f = new Fixture(); using var caller = new CancellationTokenSource();
            f.Market.Outcomes.Enqueue(new(SettlementPolls: 2)); f.Market.AfterScan = caller.Cancel;
            f.Market.TransformAnyPoll = response => f.Market.Cancels.Count > 0
                ? response with { SavedScan = response.SavedScan! with { PlanId = "foreign-cleanup-plan" } } : response;
            var result = await f.Run(2, token: caller.Token);
            Need(result.State == "Blocked" && !result.CleanupConfirmed && f.Market.Cancels.Count == 1
                && f.Market.Cancels[0].ExpectedScanJobId == f.Market.LastJobId
                && f.Market.Cancels[0].ExpectedScanPlanId == f.Market.Current.SavedScan!.PlanId
                && f.Nav.Starts == 1 && f.Market.Scans == 1 && f.Market.Continues == 0,
                "cleanup採用已被替換的計畫或發出下一工作。");
        });
        await Check("patrol_verified_settlement_before_cancel_needs_no_cancel_dispatch", async () =>
        {
            var f = new Fixture(); using var caller = new CancellationTokenSource();
            f.Market.Outcomes.Enqueue(new(SettlementPolls: 0)); f.Market.AfterScan = caller.Cancel;
            var result = await f.Run(2, token: caller.Token);
            Need(result.State == "Stopped" && result.CleanupConfirmed && f.Market.Current.WorkSettled
                && f.Market.Current.SavedScan!.UncertainPages == 0 && f.Market.Cancels.Count == 0
                && f.Nav.Starts == 1 && f.Market.Scans == 1 && f.Market.Continues == 0,
                "已獨立驗證結束的原工作仍重送取消或前往下一點。");
        });
        await Check("patrol_lost_navigation_acceptance_is_not_repeated", async () =>
        {
            var f = new Fixture(); f.Nav.ThrowStartReply = true;
            var result = await f.Run(1);
            Need(result.State == "Completed" && f.Nav.Starts == 1, "導航timeout後重送Start");
        });
        await Check("patrol_http_navigation_reply_loss_queries_original_job", async () =>
        {
            var f = new Fixture(); f.Nav.ThrowHttpStartReply = true;
            var result = await f.Run(1);
            Need(result.State == "Completed" && f.Nav.Starts == 1 && f.Nav.StateReads == 1 && f.Nav.Stops.Count == 0,
                "HTTP回覆中斷後未查原工作，或提前停止／重送導航");
        });
        await Check("patrol_future_capture_is_not_fresh", async () =>
        {
            var f = new Fixture(); f.Nav.TransformCapture = (_, value) => value with { SampledAt = Now.AddSeconds(1) };
            var result = await f.Run(1);
            Need(result.State == "Failed" && f.Nav.Starts == 0, "未來座標被當新鮮");
        });
        await Check("patrol_idle_other_market_work_prevents_navigation", async () =>
        {
            var f = new Fixture(); f.Market.Current = f.Market.Current with { WorkSettled = false, Scan = new() { JobId = "foreign", Running = true } };
            var result = await f.Run(1);
            Need(result.State == "Blocked" && f.Nav.Starts == 0 && f.Market.Cancels.Count == 0, "已有外部商店工作仍導航或取消外部工作");
        });
        await Check("patrol_false_running_is_not_work_settled", async () =>
        {
            var f = new Fixture(); f.Market.Outcomes.Enqueue(new(SettlementPolls: 2));
            var result = await f.Run(2);
            Need(result.State == "Completed", result.Detail);
            int firstScan = f.Events.IndexOf("scan"), secondMove = f.Events.FindIndex(f.Events.IndexOf("navigate") + 1, e => e == "navigate");
            Need(f.Events.Skip(firstScan + 1).Take(secondMove - firstScan - 1).Contains("settled"), "Scan.Running=false即前往下一點，未等WorkSettled");
        });
        await Check("patrol_refresh_waits_until_native_gate_release", async () =>
        {
            var f = new Fixture(); f.Market.RefreshSettlementPolls = 2;
            var result = await f.Run(1);
            Need(result.State == "Completed", result.Detail);
            int refresh = f.Events.IndexOf("refresh"), scan = f.Events.IndexOf("scan");
            Need(f.Events.Skip(refresh + 1).Take(scan - refresh - 1).Contains("settled"), "刷新回覆WorkSettled=false仍直接搜尋");
        });
        await Check("patrol_refresh_rejection_retries_before_first_scan", async () =>
        {
            var f = new Fixture(); f.Market.RefreshFailures.Enqueue(new());
            var result = await f.Run(1);
            Need(result.State == "Completed" && result.RetriesUsed == 1 && f.Market.Refreshes == 2
                && f.Market.Scans == 1 && f.Market.Continues == 0, "首次刷新重試被誤當接續舊搜尋");
            Need(f.Market.Commands.Where(c => c.Command == ShopCommands.RefreshTargets).Select(c => c.RequestId).Distinct().Count() == 2,
                "刷新重試沒有使用新的唯讀請求識別");
        });
        await Check("patrol_refresh_rejection_obeys_zero_and_exhausted_limits", async () =>
        {
            foreach (int limit in new[] { 0, 2 })
            {
                var f = new Fixture(); for (int i = 0; i <= limit; i++) f.Market.RefreshFailures.Enqueue(new());
                var result = await f.Run(2, retries: limit);
                Need(result.State == "Failed" && result.RetriesUsed == limit && f.Market.Refreshes == limit + 1
                    && f.Market.Scans == 0 && f.Nav.Starts == 1, "刷新失敗未遵守同一重試上限");
            }
        });
        await Check("patrol_refresh_unknown_source_or_unsettled_is_not_retried", async () =>
        {
            foreach (var failure in new RefreshFailure[]
            {
                new(Code: "Unknown"), new(Code: "SourceUnavailable"), new(Code: "SourceMismatch"),
                new(Settled: false), new(State: "Busy"), new(Entered: true), new(WrongRequest: true), new(Timeout: true)
            })
            {
                var f = new Fixture(); f.Market.RefreshFailures.Enqueue(failure);
                var result = await f.Run(2);
                Need(result.State != "Completed" && f.Market.Refreshes == 1 && f.Market.Scans == 0
                    && f.Market.Continues == 0 && f.Nav.Starts == 1, "來源／未知／未清理刷新失敗被重試");
            }
        });
        await Check("patrol_refresh_and_saved_scan_share_retry_budget", async () =>
        {
            var f = new Fixture(); f.Market.RefreshFailures.Enqueue(new());
            f.Market.Outcomes.Enqueue(new(Remaining: 1));
            var result = await f.Run(2, retries: 1);
            // The final third refresh only classifies remaining pages; it grants no native continuation.
            Need(result.State == "Failed" && result.RetriesUsed == 1 && f.Market.Refreshes == 3
                && f.Market.Scans == 1 && f.Market.Continues == 0 && f.Nav.Starts == 1, "刷新重試與接續搜尋未共用設定次數，或終局分類新增了搜尋");
        });
        await Check("patrol_refresh_retry_preserves_existing_saved_plan", async () =>
        {
            var f = new Fixture(); f.Market.FailRefreshAt = 2; f.Market.RefreshFailures.Enqueue(new());
            f.Market.Outcomes.Enqueue(new(Remaining: 1));
            var result = await f.Run(1, retries: 2);
            var continuation = f.Market.Commands.Single(c => c.Command == ShopCommands.ContinueSavedScan);
            Need(result.State == "Completed" && result.RetriesUsed == 2 && f.Market.Refreshes == 3
                && f.Market.Scans == 1 && f.Market.Continues == 1
                && continuation.ExpectedScanPlanId == f.Market.Current.SavedScan!.PlanId, "接續前刷新重試丟失原計畫或重建ScanVisible");
        });
        await Check("patrol_inflight_intent_may_settle_before_retry_decision", async () =>
        {
            var f = new Fixture(); f.Market.Outcomes.Enqueue(new(Uncertain: 1, SettlementPolls: 2, ResolveUncertain: true));
            var result = await f.Run(2);
            Need(result.State == "Completed" && f.Market.Cancels.Count == 0 && f.Market.Continues == 0, "正常原生執行中的intent被誤判永久未定");
        });
        await Check("patrol_cleanup_stops_registered_job_without_fresh_status", async () =>
        {
            var f = new Fixture(); using var cancel = new CancellationTokenSource();
            f.Nav.AfterStart = cancel.Cancel; f.Nav.ThrowGetState = true;
            f.Nav.TransformState = state => state with { SampledAt = Now.AddMinutes(-1), WorkSettled = false, CleanupConfirmed = false };
            var result = await f.Run(1, token: cancel.Token);
            Need(result.State == "Stopped" && result.CleanupConfirmed && f.Nav.Stops.SequenceEqual(f.Nav.JobIds)
                && f.Nav.StateReads == 0, "清理先要求新鮮GetState而無法取得歷史terminal停止證據");
        });
        await Check("patrol_idle_uncertain_plan_blocks_navigation", async () =>
        {
            var f = new Fixture(); f.Market.Current = f.Market.Current with { SavedScan = new() { UncertainPages = 1 } };
            var result = await f.Run(1);
            Need(result.State == "Blocked" && f.Nav.Starts == 0, "舊未定頁尚存仍啟動導航");
        });
        await Check("patrol_source_failure_does_not_retry", async () =>
        {
            var f = new Fixture(); f.Market.Outcomes.Enqueue(new(Remaining: 1, Failure: "SourceMismatch"));
            var result = await f.Run(2);
            Need(result.State == "Blocked" && f.Market.Continues == 0 && f.Nav.Starts == 1, "來源錯誤仍接續");
        });
        await Check("patrol_zero_shop_scan_finishes_without_retry", async () =>
        {
            var f = new Fixture(); f.Market.Outcomes.Enqueue(new(Total: 0, Confirmed: 0));
            var result = await f.Run(1);
            Need(result.State == "Completed" && f.Market.Scans == 1 && f.Market.Continues == 0, "零店主無法完成此點");
        });
        await Check("patrol_running_scan_source_gap_resumes_original_job_without_retry", async () =>
        {
            var f = new Fixture(); int status = 0;
            f.Market.Outcomes.Enqueue(new(SettlementPolls: 4));
            f.Market.TransformScan = state => state with { Scan = state.Scan with { Running = true } };
            f.Market.TransformPoll = state => ++status <= 2 ? state with { CurrentSourceSessionKey = null } : state;
            var result = await f.Run(1);
            Need(result.State == "Completed" && result.CompletedPoints == 1 && result.RetriesUsed == 0 && result.CleanupConfirmed, result.Detail);
            Need(f.Market.Scans == 1 && f.Market.Refreshes == 1 && f.Market.Continues == 0 && f.Market.Cancels.Count == 0
                && f.Nav.Starts == 1 && f.SourceDelays == 2, "原搜尋空窗被取消、重送或消耗接續次數");
        });
        await Check("patrol_scan_acceptance_source_gap_waits_without_resending", async () =>
        {
            foreach (bool lostReply in new[] { false, true })
            {
                var f = new Fixture(); int status = 0;
                f.Market.ThrowScanAcceptance = lostReply;
                f.Market.Outcomes.Enqueue(new(SettlementPolls: 3));
                f.Market.TransformScan = state => state with { CurrentSourceSessionKey = null, Scan = state.Scan with { Running = true } };
                f.Market.TransformPoll = state => ++status < 3 ? state : state with { CurrentSourceSessionKey = Source.SessionKey };
                var result = await f.Run(1);
                Need(result.State == "Completed" && result.RetriesUsed == 0 && result.CleanupConfirmed, result.Detail);
                Need(f.Market.Scans == 1 && f.Market.Continues == 0 && f.Market.Cancels.Count == 0
                    && f.SourceDelays == (lostReply ? 2 : 3), "第一份接受回覆缺來源時重送或跳過有界狀態恢復");
            }
        });
        await Check("patrol_source_gap_timeout_is_bounded_with_frozen_clock_and_cleans_only_owned_job", async () =>
        {
            var f = new Fixture();
            f.Market.Outcomes.Enqueue(new(SettlementPolls: int.MaxValue));
            f.Market.TransformScan = state => state with { CurrentSourceSessionKey = null, Scan = state.Scan with { Running = true } };
            var result = await f.Run(2);
            Need(result.State == "Blocked" && result.CleanupConfirmed && result.RetriesUsed == 0 && f.SourceDelays == 40, result.Detail);
            Need(f.Nav.Starts == 1 && f.Market.Scans == 1 && f.Market.Continues == 0 && f.Market.Cancels.Count == 1
                && f.Market.Cancels[0].ExpectedScanJobId == f.Market.LastJobId, "空窗耗盡後重送、前往下一點或取消非本工作");
        });
        await Check("patrol_source_gap_cancellation_cleans_with_independent_token", async () =>
        {
            var f = new Fixture(); using var cancel = new CancellationTokenSource();
            f.Market.Outcomes.Enqueue(new(SettlementPolls: int.MaxValue));
            f.Market.TransformScan = state => state with { CurrentSourceSessionKey = null };
            f.Market.TransformPoll = state => { cancel.Cancel(); return state; };
            var result = await f.Run(2, token: cancel.Token);
            Need(result.State == "Stopped" && result.CleanupConfirmed && f.SourceDelays == 1 && f.Nav.Starts == 1, result.Detail);
            Need(f.Market.Cancels.Count == 1 && f.Market.Cancels[0].ExpectedScanJobId == f.Market.LastJobId
                && f.Market.Scans == 1 && f.Market.Continues == 0, "取消空窗等候沒有清理原工作，或使用已取消token清理");
        });
        await Check("patrol_source_gap_real_session_change_stops_immediately", async () =>
        {
            var f = new Fixture();
            f.Market.Outcomes.Enqueue(new(SettlementPolls: int.MaxValue));
            f.Market.TransformScan = state => state with { CurrentSourceSessionKey = null };
            f.Market.TransformPoll = state => state with { CurrentSourceSessionKey = "another-session" };
            var result = await f.Run(2);
            Need(result.State == "Blocked" && result.CleanupConfirmed && f.SourceDelays == 1 && f.Nav.Starts == 1, result.Detail);
            Need(f.Market.Scans == 1 && f.Market.Continues == 0 && f.Market.Cancels.Count == 1
                && f.Market.Cancels[0].ExpectedScanJobId == f.Market.LastJobId, "明確不同來源仍等候恢復或重送");
        });
        await Check("patrol_source_gap_explicit_source_failures_are_not_waitable", async () =>
        {
            foreach (string code in new[] { "SourceMismatch", "SourceUnavailable" })
            foreach (bool scanFailure in new[] { false, true })
            {
                var f = new Fixture(); f.Market.Outcomes.Enqueue(new(SettlementPolls: int.MaxValue));
                f.Market.TransformScan = state => state with { CurrentSourceSessionKey = null,
                    FailureCode = scanFailure ? null : code, Scan = state.Scan with { FailureCode = scanFailure ? code : null } };
                var result = await f.Run(2);
                Need(result.State == "Blocked" && result.CleanupConfirmed && f.SourceDelays == 0 && f.Nav.Starts == 1
                    && f.Market.Scans == 1 && f.Market.Continues == 0 && f.Market.Cancels.Count == 1,
                    "明確來源失敗被當成可恢復空窗：" + code);
            }
        });
        await Check("patrol_source_gap_checks_each_request_job_plan_and_target_before_waiting", async () =>
        {
            foreach (string fence in new[] { "request", "job", "plan", "target", "source" })
            {
                var f = new Fixture(); f.Market.Outcomes.Enqueue(new(SettlementPolls: int.MaxValue));
                f.Market.TransformScan = state => state with { CurrentSourceSessionKey = null };
                f.Market.TransformPoll = state => fence switch
                {
                    "request" => state with { RequestId = "foreign-request" },
                    "job" => state with { Scan = state.Scan with { JobId = "foreign-job" } },
                    "plan" => state with { SavedScan = state.SavedScan! with { PlanId = "foreign-plan" } },
                    "target" => state with { Scan = state.Scan with { TargetSnapshotId = "foreign-target" } },
                    _ => state with { Scan = state.Scan with { SourceSessionKey = "foreign-source" } }
                };
                var result = await f.Run(2);
                Need(result.State == "Blocked" && !result.CleanupConfirmed && f.SourceDelays == 1 && f.Nav.Starts == 1
                    && f.Market.Scans == 1 && f.Market.Continues == 0 && f.Market.Cancels.Count == 0,
                    "空窗期間採用、等候或取消了異動的識別：" + fence);
            }
        });
        await Check("patrol_foreign_job_seen_during_gap_cannot_disappear_then_resume", async () =>
        {
            var f = new Fixture(); int status = 0;
            f.Market.Outcomes.Enqueue(new(SettlementPolls: 1));
            f.Market.TransformScan = state => state with { CurrentSourceSessionKey = null };
            f.Market.TransformPoll = state => ++status == 1
                ? state with { Scan = state.Scan with { JobId = "temporary-foreign-job" } }
                : state with { CurrentSourceSessionKey = Source.SessionKey };
            var result = await f.Run(2);
            Need(result.State == "Blocked" && result.CompletedPoints == 0 && result.CleanupConfirmed
                && f.SourceDelays == 1 && f.Nav.Starts == 1 && f.Market.Cancels.Count == 0,
                "曾觀測其他工作後仍等待原識別變回並恢復巡迴");
        });
        await Check("patrol_cleanup_can_settle_historical_receipt_without_current_source", async () =>
        {
            var f = new Fixture(); using var cancel = new CancellationTokenSource();
            f.Market.Outcomes.Enqueue(new(SettlementPolls: 2));
            f.Market.TransformScan = state => state with { CurrentSourceSessionKey = null };
            f.Market.AfterScan = cancel.Cancel;
            var result = await f.Run(2, token: cancel.Token);
            Need(result.State == "Stopped" && result.CleanupConfirmed && f.SourceDelays == 0 && f.Nav.Starts == 1
                && f.Market.Cancels.Count == 1 && f.Market.Cancels[0].ExpectedScanJobId == f.Market.LastJobId,
                "清理流程要求fresh來源而無法使用原工作的歷史清理證據");
        });
        await Check("patrol_idle_state_source_gap_uses_only_readonly_requeries", async () =>
        {
            var f = new Fixture(); int status = 0;
            f.Market.TransformAnyPoll = state => ++status <= 2 ? state with { CurrentSourceSessionKey = null } : state;
            var result = await f.Run(1);
            Need(result.State == "Completed" && f.SourceDelays == 2 && result.RetriesUsed == 0
                && f.Market.Scans == 1 && f.Market.Refreshes == 1 && f.Market.Cancels.Count == 0, result.Detail);
            Need(f.Market.Commands.Take(3).All(c => c.Command == ShopCommands.GetState), "普通狀態空窗等候送出其他命令");
        });
        await Check("patrol_idle_state_gap_rejects_replaced_work_before_navigation", async () =>
        {
            var f = new Fixture(); int status = 0;
            f.Market.TransformAnyPoll = state => ++status == 1 ? state with { CurrentSourceSessionKey = null }
                : state with { CurrentSourceSessionKey = null, Scan = state.Scan with { JobId = "foreign-work" } };
            var result = await f.Run(1);
            Need(result.State == "Blocked" && f.SourceDelays == 1 && f.Nav.Starts == 0
                && f.Market.Refreshes == 0 && f.Market.Cancels.Count == 0, "普通狀態空窗接管了異動工作");
        });
        await Check("patrol_refresh_cleanup_source_gap_keeps_original_snapshot", async () =>
        {
            var f = new Fixture(); int status = 0; f.Market.RefreshSettlementPolls = 2;
            f.Market.TransformAnyPoll = state => f.Market.Refreshes == 1 && f.Market.Scans == 0 && ++status == 1
                ? state with { CurrentSourceSessionKey = null } : state;
            var result = await f.Run(1);
            Need(result.State == "Completed" && f.SourceDelays == 1 && f.Market.Refreshes == 1 && f.Market.Scans == 1
                && f.Market.Cancels.Count == 0 && result.RetriesUsed == 0, result.Detail);
        });
        await Check("patrol_refresh_cleanup_gap_rejects_replaced_snapshot_before_wait", async () =>
        {
            var f = new Fixture(); f.Market.RefreshSettlementPolls = 2;
            f.Market.TransformAnyPoll = state => f.Market.Refreshes == 1 && f.Market.Scans == 0
                ? state with { CurrentSourceSessionKey = null, TargetSnapshot = state.TargetSnapshot! with { SnapshotId = "foreign-snapshot" } } : state;
            var result = await f.Run(1);
            Need(result.State == "Blocked" && f.SourceDelays == 0 && f.Market.Scans == 0
                && f.Market.Refreshes == 1 && f.Market.Cancels.Count == 0, "刷新名單異動仍等候來源或採用新名單");
        });
        await Check("patrol_successful_refresh_reply_source_gap_preserves_acceptance_and_snapshot", async () =>
        {
            var f = new Fixture(); f.Market.RefreshSettlementPolls = 2;
            f.Market.TransformRefresh = state => state with { CurrentSourceSessionKey = null };
            f.Market.TransformAnyPoll = state => state with { CurrentSourceSessionKey = Source.SessionKey };
            var result = await f.Run(1);
            var scan = f.Market.Commands.Single(c => c.Command == ShopCommands.ScanVisible);
            Need(result.State == "Completed" && result.RetriesUsed == 0 && f.SourceDelays == 1
                && f.Market.Refreshes == 1 && f.Market.Scans == 1 && f.Market.Cancels.Count == 0
                && scan.TargetSnapshotId == "snapshot-1", "成功刷新空來源被拒絕、重送或使用非原始成功名單");
        });
        await Check("patrol_refresh_reply_source_gap_does_not_hide_explicit_failure", async () =>
        {
            var f = new Fixture();
            f.Market.TransformRefresh = state => state with { CurrentSourceSessionKey = null, FailureCode = "SourceUnavailable" };
            var result = await f.Run(1);
            Need(result.State == "Blocked" && f.SourceDelays == 0 && f.Market.Refreshes == 1 && f.Market.Scans == 0
                && f.Market.Cancels.Count == 0, "明確來源錯誤的刷新回覆被視為可恢復空窗");
        });
        await Check("patrol_typed_readonly_refresh_timeout_retries_without_repeating_actions", async () =>
        {
            var f = new Fixture(); f.Market.RefreshFailures.Enqueue(new(Code: "RefreshReadTimeout"));
            var states = new List<string>(); f.Controller.Progress += value => states.Add(value.State);
            var result = await f.Run(4);
            Need(result.State == "Completed" && result.CompletedPoints == 4 && result.RetriesUsed == 1 && f.Market.Refreshes == 5
                && f.Market.Scans == 4 && f.Market.Continues == 0 && f.Nav.Starts == 4, result.Detail);
            Need(states.Contains("Recovered") && states.IndexOf("Recovered") < states.IndexOf("Searching"), "自動恢復未回報恢復與接續搜尋");
            Need(f.Market.Commands.Where(c => c.Command == ShopCommands.RefreshTargets).Select(c => c.RequestId).Distinct().Count() == 5,
                "唯讀刷新重取沒有使用新識別");
        });
        await Check("patrol_typed_refresh_timeout_preserves_unsent_page_plan", async () =>
        {
            var f = new Fixture(); f.Market.FailRefreshAt = 3;
            f.Market.RefreshFailures.Enqueue(new(Code: "RefreshReadTimeout"));
            f.Market.Outcomes.Enqueue(new());
            f.Market.Outcomes.Enqueue(new(Remaining: 1, Confirmed: 0));
            string? retainedPlan = null;
            f.Market.TransformRejectedRefresh = response => { retainedPlan = response.SavedScan!.PlanId; return response; };
            var result = await f.Run(4, retries: 3);
            Need(result.State == "Completed" && result.CompletedPoints == 4 && result.RetriesUsed == 2 && f.Market.Scans == 4
                && f.Market.Continues == 1 && f.Market.Refreshes == 6 && f.Nav.Starts == 4, result.Detail);
            Need(f.Market.Commands.Single(c => c.Command == ShopCommands.ContinueSavedScan).ExpectedScanPlanId == retainedPlan,
                "第二點刷新重試重建計畫或重送已返回頁；未能自動走完四點");
        });
        await Check("patrol_typed_refresh_timeout_exhaustion_is_clean_and_manually_restartable", async () =>
        {
            var f = new Fixture(); for (int i = 0; i < 3; i++) f.Market.RefreshFailures.Enqueue(new(Code: "RefreshReadTimeout"));
            var failed = await f.Run(2, retries: 2);
            Need(failed.State == "Failed" && failed.CleanupConfirmed && failed.RetriesUsed == 2
                && f.Market.Refreshes == 3 && f.Market.Scans == 0 && f.Nav.Starts == 1, failed.Detail);
            var restarted = await f.Run(1);
            Need(restarted.State == "Completed" && f.Nav.Starts == 2 && f.Market.Scans == 1,
                "已清理的唯讀刷新失敗造成協調器永久封鎖");
        });
        await Check("patrol_typed_refresh_gap_requires_new_full_origin_capture", async () =>
        {
            foreach (string code in new[] { "RefreshReadTimeout", "RefreshSourceSampleGap" })
            {
                var f = new Fixture(); f.Market.RefreshFailures.Enqueue(new(Code: code));
                f.Market.TransformRejectedRefresh = response => response with { CurrentSourceSessionKey = null };
                var result = await f.Run(1);
                Need(result.State == "Completed" && f.Market.Refreshes == 2 && f.Market.Scans == 1, result.Detail);
                Need(f.Nav.CaptureExpectations.Skip(1).All(e => e?.Source == Source), "空窗恢復未固定完整原來源");
            }
        });
        await Check("patrol_typed_refresh_retry_checks_same_session_full_source_and_character", async () =>
        {
            foreach (bool changedCharacter in new[] { false, true })
            {
                var f = new Fixture(); f.Market.RefreshFailures.Enqueue(new(Code: "RefreshReadTimeout"));
                f.Nav.TransformCapture = (_, value) => f.Market.Refreshes == 0 ? value : changedCharacter
                    ? value with { SelfId = value.SelfId + 1 } : value with { Source = value.Source with { DiskSha256 = new string('B', 64) } };
                var result = await f.Run(2);
                Need(result.State == "Blocked" && f.Market.Refreshes == 1 && f.Market.Scans == 0 && f.Nav.Starts == 1,
                    "只比session放過完整來源或角色改變");
            }
        });
        await Check("patrol_typed_refresh_retry_rejects_foreign_receipt_and_native_uncertainty", async () =>
        {
            foreach (Func<ShopCommandResponse, ShopCommandResponse> change in new Func<ShopCommandResponse, ShopCommandResponse>[]
            {
                r => r with { Scan = r.Scan with { JobId = "foreign" } },
                r => r with { TargetSnapshot = r.TargetSnapshot! with { SnapshotId = "foreign" } },
                r => r with { SavedScan = r.SavedScan! with { PlanId = "foreign" } },
                r => r with { CurrentSourceSessionKey = "different-source" },
                r => r with { SessionBlocked = true }, r => r with { NativeEntered = true },
                r => r with { NativeReturned = true }, r => r with { ResponseVerified = true },
                r => r with { WorkSettled = false }, r => r with { FailureScope = "Session" },
                r => r with { FailurePhase = "Native" },
                r => r with { TargetSnapshot = r.TargetSnapshot! with { CanDispatch = true } }
            })
            {
                var f = new Fixture(); f.Market.FailRefreshAt = 2; f.Market.Outcomes.Enqueue(new(Remaining: 1));
                f.Market.RefreshFailures.Enqueue(new(Code: "RefreshReadTimeout")); f.Market.TransformRejectedRefresh = change;
                var result = await f.Run(2);
                Need(result.State != "Completed" && f.Market.Refreshes == 2 && f.Market.Continues == 0
                    && f.Market.Scans == 1 && f.Nav.Starts == 1, "矛盾的唯讀刷新拒絕授予了接續資格");
            }
        });
        await Check("patrol_typed_refresh_retry_cancellation_never_starts_next_refresh", async () =>
        {
            var f = new Fixture(); using var cancel = new CancellationTokenSource();
            f.Market.RefreshFailures.Enqueue(new(Code: "RefreshReadTimeout"));
            f.Market.TransformRejectedRefresh = response => { cancel.Cancel(); return response; };
            var result = await f.Run(2, token: cancel.Token);
            Need(result.State == "Stopped" && result.CleanupConfirmed && f.Market.Refreshes == 1 && f.Market.Scans == 0
                && f.Nav.Starts == 1, "取消後仍重試刷新或送出動作");
        });
        await Check("patrol_vector_churn_actual_capture_client_three_rounds_four_points_no_manual_restart", async () =>
        {
            var f = new Fixture();
            await using var replay = new CaptureReplay(f, "transient");
            for (int round = 0; round < 3; round++)
            {
                var result = await f.Run(4);
                Need(result.State == "Completed" && result.CompletedPoints == 4 && result.CleanupConfirmed, result.Detail);
            }
            Need(f.Nav.Starts == 12 && f.Market.Scans == 12 && f.Market.Refreshes == 12 && f.Market.Continues == 0,
                "唯讀恢復重送了導航或商品請求");
            Need(replay.Posts == 2 * f.Nav.Captures && replay.Retries == f.Nav.Captures,
                "未在每次集合瞬變後自動重新取得成功新資料");
        });
        foreach (string mode in new[] { "bounds", "unknown", "cleanup_false", "source_changed", "cancel" })
            await Check("patrol_capture_" + mode + "_does_not_hide_failure_or_start_navigation", async () =>
            {
                var f = new Fixture(); using var cancel = new CancellationTokenSource();
                await using var replay = new CaptureReplay(f, mode, cancel);
                var result = await f.Run(4, token: cancel.Token);
                Need(result.State != "Completed" && f.Nav.Starts == 0 && f.Market.Scans == 0 && replay.Posts == 1,
                    "不明結果或取消被誤當恢復並送出動作");
            });
        return checks.ToArray();
    }

    internal static async Task<OfflineCheck[]> RunRoundsAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); checks.Add(new(name, true, "Actual coordinator, in-memory ports only.")); }
            catch (Exception e) { checks.Add(new(name, false, e.ToString())); }
        }
        await Check("rounds_default_one_and_maximum_100_are_finite", async () =>
        {
            foreach (int count in new[] { 1, 100 })
            {
                var f = new Fixture(); var result = await f.Run(1, rounds: count);
                Need(result.State == "Completed" && result.CompletedRounds == count && result.TotalRounds == count
                    && result.CurrentRound == count && result.CompletedPoints == 1 && f.Nav.Starts == count
                    && f.Market.Scans == count && f.Market.Continues == 0, result.Detail);
            }
        });
        await Check("rounds_one_run_keeps_route_order_and_unique_dispatches", async () =>
        {
            var f = new Fixture(); var progress = new List<PatrolProgress>();
            f.Controller.Progress += progress.Add;
            var result = await f.Run(2, rounds: 3);
            Need(result.State == "Completed" && result.CompletedRounds == 3 && result.CompletedPoints == 2, result.Detail);
            Need(f.Nav.TargetXs.SequenceEqual(new[] { 100,110,100,110,100,110 }), "Cross-round point order changed.");
            Need(f.Nav.JobIds.Distinct().Count() == 6 && f.Market.Commands.Where(c => c.Command == ShopCommands.ScanVisible)
                .Select(c => c.RequestId).Distinct().Count() == 6, "Cross-round request reused.");
            Need(progress.All(p => p.RunId == result.RunId && p.TotalRounds == 3 && p.PointIndex <= 2)
                && progress.Where(p => p.State == "Moving").Select(p => p.PointIndex).SequenceEqual(new[] { 0,1,0,1,0,1 }),
                "Progress uses cumulative point index or a new run ID per round.");
            Need(f.Nav.CaptureExpectations.Count(e => e is null) == 1
                && f.Nav.CaptureExpectations.Skip(1).All(e => e == f.Nav.CaptureExpectations[1]), "Origin was rebound between rounds.");
        });
        foreach (int invalid in new[] { 0, 101 })
            await Check("rounds_invalid_" + invalid + "_rejected_before_ports", async () =>
            {
                var f = new Fixture(); var result = await f.Run(1, rounds: invalid);
                Need(result.State == "Failed" && result.CompletedRounds == 0 && f.Nav.Captures == 0
                    && f.Nav.Starts == 0 && f.Market.Commands.Count == 0, "Invalid count reached a port.");
            });
        await Check("rounds_cancel_mid_first_round_counts_zero", async () =>
        {
            var f = new Fixture(); using var stop = new CancellationTokenSource();
            f.Market.AfterScan = () => stop.Cancel();
            var result = await f.Run(2, token: stop.Token, rounds: 3);
            Need(result.State == "Stopped" && result.CompletedRounds == 0 && result.CleanupConfirmed
                && f.Nav.Starts == 1 && f.Market.Scans == 1, "Partial first round counted or next work dispatched.");
        });
        await Check("rounds_boundary_cancel_never_queues_next_round", async () =>
        {
            var f = new Fixture(); using var stop = new CancellationTokenSource();
            f.Controller.Progress += p => { if (p.State == "RoundCompleted") stop.Cancel(); };
            var result = await f.Run(2, token: stop.Token, rounds: 100);
            Need(result.State == "Stopped" && result.CompletedRounds == 1 && result.CurrentRound == 1
                && f.Nav.Starts == 2 && f.Market.Scans == 2, "Cancellation scheduled another round.");
        });
        foreach (int total in new[] { 1, 2 })
            await Check("rounds_final_boundary_stop_" + total, async () =>
            {
                var f = new Fixture(); using var stop = new CancellationTokenSource();
                f.Controller.Progress += p => { if (p.State == "RoundCompleted" && p.CompletedRounds == total) stop.Cancel(); };
                var result = await f.Run(1, token: stop.Token, rounds: total);
                Need(result.State == "Stopped" && result.CompletedRounds == total && result.CurrentRound == total
                    && result.CleanupConfirmed && f.Nav.Starts == total && f.Market.Scans == total,
                    "Final-round stop was reported Completed or scheduled extra work.");
            });
        await Check("rounds_failure_in_second_round_preserves_only_first", async () =>
        {
            var f = new Fixture();
            f.Nav.TransformState = s => f.Nav.Starts == 3 ? s with { State = "Failed", Completed = false } : s;
            var result = await f.Run(2, rounds: 3);
            Need(result.State == "Failed" && result.CompletedRounds == 1 && result.CurrentRound == 2
                && result.CompletedPoints == 0 && f.Nav.Starts == 3 && f.Market.Scans == 2, "Failed round counted or advanced.");
        });
        await Check("rounds_final_post_search_capture_failure_does_not_count_round", async () =>
        {
            var f = new Fixture(); f.Nav.TransformCapture = (_, p) => f.Market.Scans == 2 ? p with { Standing = false } : p;
            var result = await f.Run(2, rounds: 3);
            Need(result.State == "Failed" && result.CompletedRounds == 0 && result.CompletedPoints == 1
                && f.Nav.Starts == 2 && f.Market.Scans == 2, "Round counted before final standing verification.");
        });
        await Check("rounds_second_round_uncertain_stays_blocked_on_restart", async () =>
        {
            var f = new Fixture(); f.Market.Outcomes.Enqueue(new()); f.Market.Outcomes.Enqueue(new(Remaining: 1, Uncertain: 1));
            var result = await f.Run(1, rounds: 3); int captures = f.Nav.Captures;
            var restart = await f.Run(1, rounds: 3);
            Need(result.State == "Blocked" && result.CompletedRounds == 1 && !result.CleanupConfirmed
                && f.Nav.Starts == 2 && f.Market.Scans == 2 && f.Market.Continues == 0
                && restart.State == "Blocked" && f.Nav.Captures == captures, "Uncertainty was reset or resent by another round/run.");
        });
        await Check("rounds_source_change_at_boundary_never_rebinds", async () =>
        {
            var f = new Fixture();
            f.Controller.Progress += p => { if (p.State == "RoundCompleted") f.Nav.TransformCapture = (_, o) => o with { SelfId = 12 }; };
            var result = await f.Run(1, rounds: 3);
            Need(result.State == "Blocked" && result.CompletedRounds == 1 && f.Nav.Starts == 1 && f.Market.Scans == 1,
                "Second round accepted a different character.");
        });
        await Check("rounds_history_skipped_receipt_can_finish_without_clear_or_continue", async () =>
        {
            var f = new Fixture();
            f.Market.TransformScan = response => f.Market.Scans < 2 ? response : response with
            { SavedScan = response.SavedScan! with { SearchedPages = 0, ConfirmedPages = 0, HistorySkippedPages = 2 } };
            var result = await f.Run(1, rounds: 3);
            Need(result.State == "Completed" && result.CompletedRounds == 3 && f.Market.Scans == 3 && f.Market.Continues == 0,
                "History-only subsequent rounds were forced to resubmit.");
            Need(f.Market.Commands.All(c => c.Command is ShopCommands.GetState or ShopCommands.RefreshTargets or ShopCommands.ScanVisible),
                "Multi-round scheduling sent an unauthorized history-clear or extra command.");
        });
        await Check("rounds_retry_limit_stays_per_point_per_round_and_total_is_cumulative", async () =>
        {
            var f = new Fixture();
            f.Market.Outcomes.Enqueue(new(Remaining: 1)); f.Market.Outcomes.Enqueue(new());
            f.Market.Outcomes.Enqueue(new(Remaining: 1)); f.Market.Outcomes.Enqueue(new());
            var result = await f.Run(1, retries: 1, rounds: 2);
            Need(result.State == "Completed" && result.CompletedRounds == 2 && result.RetriesUsed == 2
                && f.Market.Scans == 2 && f.Market.Continues == 2,
                "Per-point retry contract changed or cumulative count reset at round boundary.");
        });
        await Check("rounds_run_uses_snapshot_despite_external_route_edit", async () =>
        {
            var f = new Fixture();
            var points = new[] { new PatrolWaypoint(Guid.NewGuid().ToString("N"), "first", "4", "offline", 100, 100) };
            var route = new PatrolRouteSnapshot { Revision = Guid.NewGuid().ToString("N"), SavedAt = Now,
                RoundCount = 3, RetryLimit = 0, ArrivalRadius = 1, Points = points };
            f.Controller.Progress += p => { if (p.State == "RoundCompleted") points[0] = points[0] with { X = 900 }; };
            var result = await f.Controller.RunAsync(route);
            Need(result.State == "Completed" && result.CompletedRounds == 3 && f.Nav.TargetXs.All(x => x == 100),
                "Caller edit changed the active route snapshot.");
        });
        return checks.ToArray();
    }

    internal static async Task<OfflineCheck[]> RunTerminalFailureAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); checks.Add(new(name, true, "Actual coordinator with in-memory ports; no native dispatch.")); }
            catch (Exception e) { checks.Add(new(name, false, e.ToString())); }
        }
        await Check("terminal_native_preflight_root_reason_survives_classification_refresh", async () =>
        {
            var f = new Fixture(); f.Market.Outcomes.Enqueue(new(Total: 30, Remaining: 21, Confirmed: 7));
            f.Market.TransformScan = r => r with { Scan = r.Scan with { Completed = 10, FailureCode = "NativeCodeUnavailable",
                FailureScope = "Global", FailurePhase = "Preflight", FailureRva = "0x27D620", FailureDetail = "PAGE_NOACCESS\r\nNativeEntered=false" } };
            f.Market.TransformRefresh = r => r with { Scan = r.Scan with { FailureCode = null, FailureDetail = null,
                FailureScope = null, FailurePhase = null, FailureRva = null } };
            var result = await f.Run(2, retries: 0, rounds: 3);
            Need(result.State == "Failed" && result.CleanupConfirmed && result.CompletedRounds == 0
                && f.Nav.Starts == 1 && f.Market.Scans == 1 && f.Market.Continues == 0, "Failure changed scheduling/cleanup guards.");
            foreach (string expected in new[] { "NativeCodeUnavailable", "Global", "Preflight", "0x27D620", "PAGE_NOACCESS", "21", "接續次數已達上限" })
                Need(result.Detail.Contains(expected), "Terminal omitted " + expected);
            Need(!result.Detail.Contains('\r') && !result.Detail.Contains('\n'), "Failure detail is not single-line.");
            var view = PatrolOverlayPresentation.BuildResult(result, Now);
            Need(view.PatrolState == "Failed" && view.Detail.Contains("NativeCodeUnavailable") && view.Progress.Contains("1/3") && !view.Busy,
                "Terminal HUD hid root reason or round progress.");
        });
        await Check("terminal_without_failure_metadata_keeps_existing_reason", async () =>
        {
            var f = new Fixture(); f.Market.Outcomes.Enqueue(new(Remaining: 1));
            var result = await f.Run(2, retries: 0, rounds: 2);
            Need(result.State == "Failed" && result.Detail == "尚有 1 個未送出頁；接續次數已達上限，巡迴停止。"
                && f.Market.Continues == 0 && f.Nav.Starts == 1, "No-metadata behavior changed.");
        });
        await Check("terminal_latest_validated_scan_reason_replaces_earlier_failure", async () =>
        {
            var f = new Fixture(); f.Market.Outcomes.Enqueue(new(Remaining: 1)); f.Market.Outcomes.Enqueue(new(Remaining: 1));
            f.Market.TransformScan = r => r with { Scan = r.Scan with { FailureCode = f.Market.Continues == 0 ? "FirstPageFailure" : "LatestPageFailure",
                FailureScope = "Page", FailurePhase = "BeforePublish", FailureDetail = "No native input in this fixture." } };
            var result = await f.Run(2, retries: 1);
            Need(result.State == "Failed" && result.Detail.Contains("LatestPageFailure") && !result.Detail.Contains("FirstPageFailure")
                && f.Market.Scans == 1 && f.Market.Continues == 1 && f.Nav.Starts == 1, "Terminal used stale reason or changed retry budget.");
        });
        await Check("terminal_successful_recovery_does_not_report_old_failure", async () =>
        {
            var f = new Fixture(); f.Market.Outcomes.Enqueue(new(Remaining: 1)); f.Market.Outcomes.Enqueue(new());
            f.Market.TransformScan = r => f.Market.Continues == 0
                ? r with { Scan = r.Scan with { FailureCode = "OldPageFailure", FailureScope = "Page" } } : r;
            var result = await f.Run(1, retries: 1, rounds: 2);
            Need(result.State == "Completed" && result.CompletedRounds == 2 && !result.Detail.Contains("OldPageFailure")
                && f.Nav.Starts == 2 && f.Market.Scans == 2 && f.Market.Continues == 1, "Recovered prior failure leaked into success or next round.");
        });
        await Check("terminal_foreign_scan_failure_is_never_trusted", async () =>
        {
            var f = new Fixture(); f.Market.Outcomes.Enqueue(new(Remaining: 1));
            f.Market.TransformScan = r => r with { Scan = r.Scan with { JobId = "foreign-job", FailureCode = "ForeignFailure",
                FailureDetail = "untrusted diagnostic" } };
            var result = await f.Run(2, retries: 0, rounds: 2);
            Need(result.State == "Blocked" && !result.Detail.Contains("ForeignFailure") && !result.Detail.Contains("untrusted diagnostic")
                && f.Market.Continues == 0 && f.Nav.Starts == 1, "Unvalidated receipt leaked into trusted failure evidence.");
        });
        return checks.ToArray();
    }

    private static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    private sealed record Outcome(int Total = 2, int Remaining = 0, int? Confirmed = null, int Uncertain = 0,
        bool SessionBlocked = false, int SettlementPolls = 0, string? Failure = null, bool ResolveUncertain = false);
    private sealed record RefreshFailure(string Code = "ContextUnavailable", bool Settled = true,
        string State = "Rejected", bool Entered = false, bool WrongRequest = false, bool Timeout = false);
    private sealed class Fixture
    {
        internal readonly List<string> Events = [];
        internal readonly FakeNavigation Nav;
        internal readonly FakeMarket Market;
        internal readonly PatrolController Controller;
        internal int SourceDelays;
        internal Func<TimeSpan, CancellationToken, Task>? DelayOverride;
        internal Fixture(TimeSpan? cleanupTimeout = null)
        {
            Nav = new(Events); Market = new(Events);
            Controller = new(Nav, Market, () => Now, (interval, token) =>
            {
                if (interval == TimeSpan.FromMilliseconds(100)) SourceDelays++;
                return DelayOverride is null ? Task.Delay(1, token) : DelayOverride(interval, token);
            }, cleanupTimeout);
        }
        internal Task<PatrolRunResult> Run(int points, int retries = 3, CancellationToken token = default, int radius = 2, int rounds = 1) => Controller.RunAsync(new()
        {
            Revision = Guid.NewGuid().ToString("N"), SavedAt = Now, RetryLimit = retries, ArrivalRadius = radius, RoundCount = rounds,
            Points = Enumerable.Range(0, points).Select(i => new PatrolWaypoint(Guid.NewGuid().ToString("N"), "點 " + i, "4", "離線地圖", 100 + i * 10, 100)).ToArray()
        }, token);
    }
    private sealed class FakeNavigation(List<string> events) : IPatrolNavigation
    {
        internal int Starts, Captures, StateReads;
        internal readonly List<string> Stops = [], JobIds = [];
        internal readonly List<int> Radii = [], TargetXs = [];
        internal readonly List<PatrolObservation?> CaptureExpectations = [];
        internal bool ThrowStartReply, ThrowHttpStartReply, ThrowGetState;
        internal Action? AfterStart;
        internal Func<int, PatrolObservation, PatrolObservation>? TransformCapture;
        internal Func<PatrolNavigationState, PatrolNavigationState>? TransformState;
        private int x = 100, y = 100;
        private PatrolNavigationState current = new();
        internal Func<PatrolObservation, PatrolObservation?, CancellationToken, Task<PatrolObservation>>? CaptureHook;
        public Task<PatrolObservation> CaptureAsync(CancellationToken token, PatrolObservation? expected = null)
        {
            token.ThrowIfCancellationRequested();
            CaptureExpectations.Add(expected);
            var value = new PatrolObservation(Source, 11, 4, x, y, Now, true);
            Captures++;
            value = TransformCapture?.Invoke(Captures, value) ?? value;
            return CaptureHook is null ? Task.FromResult(value) : CaptureHook(value, expected, token);
        }
        public Task<PatrolNavigationState> StartAsync(PatrolNavigationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Starts++; JobIds.Add(request.JobId); Radii.Add(request.ArrivalRadius); TargetXs.Add(request.Waypoint.X); events.Add("navigate"); x = request.Waypoint.X; y = request.Waypoint.Y;
            current = new() { JobId = request.JobId, WaypointId = request.Waypoint.PointId, TargetX = x, TargetY = y,
                ArrivalRadius = request.ArrivalRadius,
                Source = Source, SelfId = 11, MapId = 4, X = x, Y = y, SampledAt = Now, State = "Completed",
                Terminal = true, Completed = true, CleanupConfirmed = true, WorkSettled = true, Standing = true, StandingSamples = 2 };
            if (TransformState is not null) current = TransformState(current);
            AfterStart?.Invoke();
            if (ThrowHttpStartReply) throw new System.Net.Http.HttpRequestException("offline lost HTTP navigation reply");
            if (ThrowStartReply) throw new TimeoutException("offline lost navigation reply");
            return Task.FromResult(current);
        }
        public Task<PatrolNavigationState> GetStateAsync(string jobId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); StateReads++; if (ThrowGetState) throw new IOException("offline stale terminal status"); return Task.FromResult(current); }
        public Task<PatrolNavigationState> StopAsync(string expectedJobId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Need(expectedJobId == current.JobId, "fixture refused stopping a foreign navigation job");
            Stops.Add(expectedJobId);
            current = current with { State = "Stopped", Terminal = true, Completed = false, WorkSettled = true, CleanupConfirmed = true };
            return Task.FromResult(current);
        }
    }
    // Actual capture client + actual patrol coordinator, with fake read transport and game ports.
    private sealed class CaptureReplay : IAsyncDisposable
    {
        internal int Posts, Retries;
        private PatrolObservation current = new(Source, 11, 4, 100, 100, Now, true);
        private readonly PatrolNavigationClient client;
        internal CaptureReplay(Fixture f, string mode, CancellationTokenSource? cancel = null)
        {
            object Wire() => new { pid = Source.ProcessId, creationFileTime = Source.CreationFileTime.ToString(),
                imagePath = Source.ImagePath, sha256 = Source.DiskSha256, imageBase = Source.ImageBase,
                imageSize = Source.ImageSize, selfId = "11" };
            client = new(() => new HubSnapshot { Source = Source, State = "Reading", PublishedAt = Now,
                Fields = new() { ["selfId"] = FieldReading.Observed(11UL, Now),
                    ["position"] = FieldReading.Observed(new { x = current.X, y = current.Y }, Now) } },
                (method, path, body, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    Need(method == System.Net.Http.HttpMethod.Post && path == "/api/capture", "發出非唯讀取點請求");
                    Posts++;
                    var request = System.Text.Json.JsonSerializer.SerializeToElement(body);
                    bool failed = mode != "transient" || Posts % 2 == 1;
                    string error = mode == "bounds" ? "READ_CAPTURE_VECTOR_BOUNDS" : mode == "unknown" ? "READ_CAPTURE_FAILED" : "READ_CAPTURE_VECTOR_CHANGED";
                    var data = new Dictionary<string, object?> { ["ok"] = !failed, ["captureId"] = request.GetProperty("captureId").GetString(),
                        ["expectedSource"] = mode == "source_changed" ? new { pid = 999 } : Wire(),
                        ["readOnly"] = true, ["inputSent"] = false, ["accepted"] = true,
                        ["terminal"] = true, ["cleanupConfirmed"] = mode != "cleanup_false", ["state"] = failed ? "Failed" : "Completed",
                        ["error"] = failed ? error : "", ["source"] = Wire(), ["mapId"] = 4,
                        ["x"] = current.X, ["y"] = current.Y, ["standing"] = true, ["sampledAtUtc"] = Now.ToString("O") };
                    if (mode == "cancel") cancel!.Cancel();
                    return Task.FromResult(new PatrolNavigationReply(failed ? System.Net.HttpStatusCode.Conflict : System.Net.HttpStatusCode.OK,
                        System.Text.Json.JsonSerializer.SerializeToElement(data)));
                }, () => Now, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                notice => { if (notice.Code == "CAPTURE_READ_RETRY") Retries++; });
            f.Nav.CaptureHook = (value, expected, token) => { current = value; return client.CaptureAsync(token, expected); };
        }
        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
    private sealed class FakeMarket(List<string> events) : IPatrolMarket
    {
        internal int Scans, Continues, Refreshes, RefreshSettlementPolls;
        internal int FailRefreshAt = 1;
        internal bool ThrowScanAcceptance, NeverSettle;
        internal string? LastJobId;
        internal Action? AfterScan;
        internal Action<ShopCommandRequest, CancellationToken>? BeforeSend;
        internal Func<ShopCommandResponse, ShopCommandResponse>? TransformScan, TransformPoll, TransformAnyPoll, TransformRefresh, TransformRejectedRefresh;
        internal readonly Queue<Outcome> Outcomes = [];
        internal readonly Queue<RefreshFailure> RefreshFailures = [];
        internal readonly List<ShopCommandRequest> Commands = [], Cancels = [];
        internal ShopCommandResponse Current = new() { CurrentSourceSessionKey = Source.SessionKey, WorkSettled = true, State = "State" };
        private int polls;
        private bool resolveUncertain;
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            BeforeSend?.Invoke(request, token);
            token.ThrowIfCancellationRequested(); Commands.Add(request);
            switch (request.Command)
            {
                case ShopCommands.GetState:
                    if (polls > 0 && !NeverSettle && --polls == 0)
                    {
                        var settledProgress = Current.SavedScan;
                        if (resolveUncertain && settledProgress is not null) settledProgress = settledProgress with
                            { SearchedPages = settledProgress.SearchedPages + settledProgress.UncertainPages, UncertainPages = 0 };
                        Current = Current with { WorkSettled = true, Scan = Current.Scan with { Running = false }, SavedScan = settledProgress };
                        resolveUncertain = false; events.Add("settled");
                    }
                    var polled = Current with { RequestId = request.RequestId, State = "State" };
                    if (TransformAnyPoll is not null) polled = TransformAnyPoll(polled);
                    if (LastJobId is not null && TransformPoll is not null) polled = TransformPoll(polled);
                    return Task.FromResult(polled);
                case ShopCommands.RefreshTargets:
                    Refreshes++; events.Add("refresh"); polls = RefreshSettlementPolls;
                    if (Refreshes >= FailRefreshAt && RefreshFailures.TryDequeue(out var failedRefresh))
                    {
                        if (failedRefresh.Timeout) throw new TimeoutException("offline lost refresh reply");
                        Current = Current with { TargetSnapshot = Current.TargetSnapshot is { } retained
                            ? retained with { CanDispatch = false } : null };
                        var rejected = Current with
                        {
                            RequestId = failedRefresh.WrongRequest ? "foreign-refresh" : request.RequestId,
                            State = failedRefresh.State, FailureCode = failedRefresh.Code, FailureScope = "Global", FailurePhase = "BeforePublish",
                            NativeEntered = failedRefresh.Entered, WorkSettled = failedRefresh.Settled
                        };
                        return Task.FromResult(TransformRejectedRefresh?.Invoke(rejected) ?? rejected);
                    }
                    Current = Current with { RequestId = request.RequestId, State = "TargetsRefreshed", WorkSettled = polls == 0,
                        TargetSnapshot = new() { SnapshotId = "snapshot-" + Refreshes, SourceSessionKey = Source.SessionKey,
                            SelfId = 11, SelfPosition = new(100, 100), CanDispatch = true, RefreshedAt = Now },
                        SavedScan = Current.SavedScan is { } saved ? saved with { CanContinue = saved.RemainingPages > 0,
                            AvailablePages = saved.RemainingPages, DeferredPages = 0,
                            ContinueBlockCode = saved.RemainingPages > 0 ? "Ready" : "Complete" } : null };
                    if (TransformRefresh is not null) Current = TransformRefresh(Current);
                    return Task.FromResult(Current);
                case ShopCommands.ScanVisible:
                case ShopCommands.ContinueSavedScan:
                    bool continuation = request.Command == ShopCommands.ContinueSavedScan;
                    if (continuation) { Continues++; events.Add("continue"); } else { Scans++; events.Add("scan"); }
                    var outcome = Outcomes.Count == 0 ? new Outcome() : Outcomes.Dequeue();
                    int searched = outcome.Total - outcome.Remaining - outcome.Uncertain;
                    string plan = continuation ? request.ExpectedScanPlanId! : Guid.NewGuid().ToString("N");
                    LastJobId = request.RequestId; polls = outcome.SettlementPolls; resolveUncertain = outcome.ResolveUncertain;
                    Current = new() { RequestId = request.RequestId, State = "ScanStarted", CurrentSourceSessionKey = Source.SessionKey,
                        WorkSettled = polls == 0, SessionBlocked = outcome.SessionBlocked, FailureCode = outcome.Failure,
                        TargetSnapshot = Current.TargetSnapshot,
                        Scan = new() { JobId = request.RequestId, SourceSessionKey = Source.SessionKey, TargetSnapshotId = request.TargetSnapshotId,
                            Total = outcome.Total, Completed = outcome.Total - outcome.Remaining, Running = false, FailureCode = outcome.Failure },
                        SavedScan = new() { PlanId = plan, SourceSessionKey = Source.SessionKey, TotalPages = outcome.Total,
                            RemainingPages = outcome.Remaining, SearchedPages = searched, ConfirmedPages = outcome.Confirmed ?? searched,
                            UncertainPages = outcome.Uncertain, CanContinue = outcome.Remaining > 0, AvailablePages = outcome.Remaining,
                            ContinueBlockCode = outcome.Remaining > 0 ? "Ready" : "Complete" } };
                    if (TransformScan is not null) Current = TransformScan(Current);
                    AfterScan?.Invoke();
                    if (ThrowScanAcceptance) throw new TimeoutException("offline lost scan reply");
                    return Task.FromResult(Current);
                case ShopCommands.CancelScan:
                    Need(request.ExpectedScanJobId == Current.Scan.JobId, "fixture caught foreign cancellation");
                    Cancels.Add(request); events.Add("cancel");
                    Current = Current with { RequestId = request.RequestId, State = "Stopping", WorkSettled = false,
                        Scan = Current.Scan with { Running = false, StopReason = "Cancelled" } };
                    if (!NeverSettle) polls = 1;
                    return Task.FromResult(Current);
                default: throw new InvalidOperationException("unexpected offline command " + request.Command);
            }
        }
    }
}
