using MemoryHub;

namespace LcPlayerMarket;

internal static class SearchRestartCoordinatorSelfTest
{
    internal static readonly SourceIdentity Source = new(7100, 9001, "offline-only", "0x100000",
        MarketNativeCompatibility.SupportedTimestamp, MarketNativeCompatibility.SupportedImageSize,
        MarketNativeCompatibility.SupportedSha256, "reset-offline-source");

    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> action)
        {
            try { await action().WaitAsync(TimeSpan.FromSeconds(5)); checks.Add(new(name, true, "純離線 fake port；未讀取 LC、未發送輸入或開店。")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        await Check("search_restart_saved_route_order_and_force_local_reset", async () =>
        {
            var port = new FakePort { Route = Route() };
            var outcome = await new SearchRestartCoordinator(port).RunAsync(default);
            Need(outcome.ResetConfirmed && outcome.Restarted && outcome.Mode == "Patrol", "保存路線未從頭啟動。");
            Need(port.Calls.SequenceEqual(["join", "ResetSearchProgress", "apply", "checkpoint", "route", "patrol"]), "清理、清除、重置與啟動順序錯誤。");
            Need(port.Requests.Single().ForceDispatch && port.Requests.Single().TargetSnapshotId is null
                && port.Requests.Single().ExpectedScanPlanId is null && port.StartedRoute == port.Route,
                "本機清除沿用了舊計畫，或修改了座標設定。");
        });
        await Check("search_restart_empty_route_refresh_then_fresh_plan", async () =>
        {
            var port = new FakePort();
            var outcome = await new SearchRestartCoordinator(port).RunAsync(default);
            Need(outcome.Restarted && outcome.Mode == "Search", "未保存路線時没有重新搜尋。");
            Need(port.Calls.SequenceEqual(["join", "ResetSearchProgress", "apply", "checkpoint", "route", "RefreshTargets", "adopt", "ScanVisible"]), "未先清理再刷新與搜尋。");
            Need(port.Requests.All(r => r.ForceDispatch) && port.Requests.Select(r => r.RequestId).Distinct().Count() == 3,
                "新操作 ID 重複或缺少 force。");
            Need(port.Requests.Last().TargetSnapshotId == "new-targets" && port.Requests.Last().ExpectedScanPlanId is null,
                "重新搜尋未使用本次新快照。");
        });
        await Check("search_restart_no_game_still_clears", async () =>
        {
            var port = new FakePort { CurrentSource = null };
            var result = await new SearchRestartCoordinator(port).RunAsync(default);
            Need(result.ResetConfirmed && !result.Restarted && port.Requests.Single().ExpectedSourceSessionKey == ""
                && port.Calls.SequenceEqual(["join", "ResetSearchProgress", "apply", "checkpoint"]), "無遊戲時本機清除仍受來源門檻阻擋。");
        });
        await Check("search_restart_unsupported_abi_can_clear_without_start", async () =>
        {
            var port = new FakePort { Compatible = false };
            var result = await new SearchRestartCoordinator(port).RunAsync(default);
            Need(result.ResetConfirmed && !result.Restarted && port.Requests.Count == 1 && port.CheckpointReset, "ABI阻擋了本機清除或仍送出搜尋。");
        });
        await Check("search_restart_source_changes_after_clear_do_not_rebind", async () =>
        {
            var port = new FakePort(); port.OnApply = () => port.CurrentSource = Source with { CreationFileTime = Source.CreationFileTime + 1 };
            var result = await new SearchRestartCoordinator(port).RunAsync(default);
            Need(result.ResetConfirmed && !result.Restarted && port.Requests.Count == 1, "重置後來源變更仍自動綁新LC。");
        });
        foreach (string invalid in new[] { "request", "progress", "history", "worker" })
        {
            await Check("search_restart_reject_invalid_reset_" + invalid, async () =>
            {
                var port = new FakePort();
                port.TransformReset = response => invalid switch
                {
                    "request" => response with { RequestId = "other" },
                    "progress" => response with { SavedScan = new() { PlanId = "old-plan" } },
                    "history" => response with { SearchHistory = response.SearchHistory! with { UncertainPages = 1 } },
                    _ => response with { WorkSettled = false }
                };
                await Reject(() => new SearchRestartCoordinator(port).RunAsync(default));
                Need(!port.Applied && !port.CheckpointReset && port.Requests.Count == 1, "不完整清除回覆仍導致後續重置／啟動。");
            });
        }
        await Check("search_restart_unclean_patrol_prevents_broker_clear", async () =>
        {
            var port = new FakePort { JoinFailure = new InvalidOperationException("worker still owned") };
            await Reject(() => new SearchRestartCoordinator(port).RunAsync(default));
            Need(port.Requests.Count == 0 && !port.CheckpointReset, "未清理巡迴仍清除broker進度。");
        });
        await Check("search_restart_checkpoint_failure_prevents_new_start", async () =>
        {
            var port = new FakePort { CheckpointFailure = new IOException("preserved checkpoint") };
            await Reject(() => new SearchRestartCoordinator(port).RunAsync(default));
            Need(port.Applied && port.Requests.Count == 1 && port.StartedRoute is null, "巡迴重置失敗仍啟動新計畫。");
        });
        await Check("search_restart_cancel_after_ack_keeps_clear_without_restart", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var port = new FakePort { OnApply = cancellation.Cancel };
            await Reject(() => new SearchRestartCoordinator(port).RunAsync(cancellation.Token));
            Need(port.Applied && port.CheckpointReset && port.Requests.Count == 1, "確認清除後取消留下舊恢復點，或仍啟動／重送。");
        });
        await Check("search_restart_concurrent_click_keeps_single_transaction", async () =>
        {
            var join = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var port = new FakePort { CurrentSource = null, JoinWait = join.Task };
            var coordinator = new SearchRestartCoordinator(port);
            var first = coordinator.RunAsync(default);
            await Reject(() => coordinator.RunAsync(default));
            join.SetResult(true); await first;
            Need(port.Calls.Count(c => c == "join") == 1 && port.Requests.Count == 1, "同時操作建立第二清除指令。");
        });
        await Check("search_restart_refresh_identity_mismatch_never_scans", async () =>
        {
            var port = new FakePort { InvalidRefresh = true };
            await Reject(() => new SearchRestartCoordinator(port).RunAsync(default));
            Need(port.Applied && port.Requests.Count == 2 && !port.Calls.Contains("adopt"), "來源不符的新名單仍啟動搜尋。");
        });
        await Check("search_restart_corrupt_route_retained_no_start", async () =>
        {
            var port = new FakePort { RouteError = "route read failed" };
            var result = await new SearchRestartCoordinator(port).RunAsync(default);
            Need(result.ResetConfirmed && !result.Restarted && port.Requests.Count == 1, "損毀路線被忽略並送出搜尋。");
        });
        await Check("search_restart_unknown_session_is_not_cleared_by_local_reset", async () =>
        {
            var port = new FakePort { CurrentSource = null, TransformReset = response => response with { SessionBlocked = true } };
            var result = await new SearchRestartCoordinator(port).RunAsync(default);
            Need(result.ResetConfirmed && port.LastReset?.SessionBlocked == true, "清除擅自解除了未知原生操作。");
        });
        await Check("search_restart_stop_after_scan_acceptance_joins_exact_original_job", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var port = new FakePort { OnScan = cancellation.Cancel };
            await Reject(() => new SearchRestartCoordinator(port).RunAsync(cancellation.Token));
            Need(port.JoinedAcceptedScan && port.Requests.Count(r => r.Command == ShopCommands.ScanVisible) == 1
                && port.Calls.Last() == "cancel-join:" + port.Requests.Last().RequestId,
                "接受新搜尋後停止沒有釋放原工作，或重送搜尋。");
        });
        await Check("search_restart_stop_before_delayed_scan_reply_observes_original_acceptance", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var reply = new TaskCompletionSource<ShopCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var port = new FakePort { DelayedScan = reply };
            var operation = new SearchRestartCoordinator(port).RunAsync(cancellation.Token);
            var scan = port.Requests.Last();
            Need(scan.Command == ShopCommands.ScanVisible, "未建立待回覆的新搜尋。");
            cancellation.Cancel();
            Need(!operation.IsCompleted, "取消放棄原acceptance，沒有等原工作清理。");
            reply.SetResult(new() { RequestId = scan.RequestId, State = "ScanStarted", Scan = new() { JobId = scan.RequestId, Running = true } });
            await Reject(() => operation);
            Need(port.JoinedAcceptedScan && port.Requests.Count == 3, "晚到接受回覆未以原job清理或重送。");
        });
        await Check("search_restart_source_changes_after_scan_acceptance_cleanup_only", async () =>
        {
            var port = new FakePort(); port.OnScan = () => port.CurrentSource = Source with { SessionKey = "another-session" };
            await Reject(() => new SearchRestartCoordinator(port).RunAsync(default));
            Need(port.JoinedAcceptedScan && port.Requests.Count == 3, "LC變更後沒有清理原工作，或自動綁定新來源。");
        });
        await Check("search_restart_cancelled_reply_other_job_does_not_blind_stop", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var reply = new TaskCompletionSource<ShopCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var port = new FakePort { DelayedScan = reply };
            var operation = new SearchRestartCoordinator(port).RunAsync(cancellation.Token);
            var scan = port.Requests.Last(); cancellation.Cancel();
            reply.SetResult(new() { RequestId = scan.RequestId, State = "ScanStarted", Scan = new() { JobId = "another-job", Running = true } });
            await Reject(() => operation);
            Need(!port.JoinedAcceptedScan && port.Requests.Count == 3, "錯誤job回覆仍停止另一工作。");
        });
        return checks.ToArray();
    }

    internal static PatrolRouteSnapshot Route() => new()
    {
        Revision = Guid.NewGuid().ToString("N"), SavedAt = DateTimeOffset.UtcNow, RoundCount = 2,
        Points = [new(Guid.NewGuid().ToString("N"), "原第一點", "4", "離線地圖", 100, 100)]
    };
    internal static ShopCommandResponse ResetReply(ShopCommandRequest request) => new()
    {
        RequestId = request.RequestId, State = "SearchProgressReset", WorkSettled = true,
        SearchHistory = new() { Revision = "new-history", Epoch = "new-epoch" }
    };
    internal static async Task Reject(Func<Task> action)
    {
        bool rejected = false;
        try { await action(); }
        catch { rejected = true; }
        Need(rejected, "無效操作未拒絕。");
    }
    internal static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }

    internal sealed class FakePort : ISearchRestartPort
    {
        public SourceIdentity? CurrentSource { get; set; } = Source;
        internal bool Compatible = true, Applied, CheckpointReset, InvalidRefresh;
        internal bool JoinedAcceptedScan;
        internal Action? OnScan;
        internal TaskCompletionSource<ShopCommandResponse>? DelayedScan;
        internal Exception? JoinFailure, CheckpointFailure;
        internal Task? JoinWait;
        internal Action? OnApply;
        internal Func<ShopCommandResponse, ShopCommandResponse>? TransformReset;
        internal PatrolRouteSnapshot Route = new();
        internal PatrolRouteSnapshot? StartedRoute;
        internal string? RouteError;
        internal ShopCommandResponse? LastReset;
        internal readonly List<string> Calls = [];
        internal readonly List<ShopCommandRequest> Requests = [];
        public bool CanStart(SourceIdentity source, out string reason) { reason = "fake unsupported ABI"; return Compatible; }
        public async Task StopPatrolAndJoinAsync(CancellationToken token)
        {
            Calls.Add("join"); if (JoinFailure is not null) throw JoinFailure;
            if (JoinWait is not null) await JoinWait.WaitAsync(token);
        }
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request); Calls.Add(request.Command);
            if (request.Command == ShopCommands.ResetSearchProgress)
            {
                var reset = ResetReply(request);
                return Task.FromResult(TransformReset?.Invoke(reset) ?? reset);
            }
            if (request.Command == ShopCommands.RefreshTargets)
                return Task.FromResult(new ShopCommandResponse
                {
                    RequestId = request.RequestId, State = "TargetsRefreshed", CurrentSourceSessionKey = Source.SessionKey,
                    TargetSnapshot = new() { SnapshotId = "new-targets", SourceSessionKey = InvalidRefresh ? "other" : Source.SessionKey,
                        SelfId = 11, RefreshedAt = DateTimeOffset.UtcNow, CanDispatch = true }
                });
            OnScan?.Invoke();
            return DelayedScan?.Task ?? Task.FromResult(new ShopCommandResponse { RequestId = request.RequestId, State = "ScanStarted", Scan = new() { JobId = request.RequestId } });
        }
        public void ApplyReset(ShopCommandRequest request, ShopCommandResponse response)
        { Calls.Add("apply"); Applied = true; LastReset = response; OnApply?.Invoke(); }
        public Task ResetPatrolAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add("checkpoint");
            if (CheckpointFailure is not null) return Task.FromException(CheckpointFailure);
            CheckpointReset = true; return Task.CompletedTask;
        }
        public PatrolRouteLoadResult LoadRoute() { Calls.Add("route"); return new(RouteError is null ? Route : null, RouteError); }
        public Task StartPatrolAsync(PatrolRouteSnapshot route, SourceIdentity expected, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls.Add("patrol"); StartedRoute = route; return Task.CompletedTask; }
        public void AdoptRefresh(ShopCommandRequest request, ShopCommandResponse response, SourceIdentity expected) => Calls.Add("adopt");
        public Task StopAcceptedScanAndJoinAsync(ShopCommandRequest request, ShopCommandResponse acceptance)
        {
            Need(acceptance.Scan.JobId == request.RequestId, "停止了另一搜尋工作。");
            Calls.Add("cancel-join:" + request.RequestId); JoinedAcceptedScan = true;
            return Task.CompletedTask;
        }
    }
}
