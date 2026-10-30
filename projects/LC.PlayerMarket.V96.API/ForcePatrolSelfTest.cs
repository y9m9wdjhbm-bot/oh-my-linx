using System.Net;
using System.Net.Http;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Runs the production force coordinator and parser with fake ports only.</summary>
internal static class ForcePatrolSelfTest
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly SourceIdentity Source = new(123, 456, "offline-LC.exe", "0x140000000", 1, 65536, new string('A', 64), "offline-session");

    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); checks.Add(new(name, true, "純離線；實際 force 協調器／解析器，沒有 LC 存取或輸入。")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        await Check("force_patrol_direct_mutations_without_preflight_status", async () =>
        {
            var f = new Fixture(); var result = await f.Run(points: 2, rounds: 2);
            Need(result.State == "Completed" && result.CompletedRounds == 2, result.Detail);
            Need(f.Market.Commands.All(c => c.Command != ShopCommands.GetState), "同步完成的商店操作仍先查狀態。");
            Need(f.Market.Commands.All(c => c.ForceDispatch), "巡迴商店指令漏了 ForceDispatch。");
            Need(f.Events.First(e => e is "refresh" or "scan") == "refresh", "到點後没有直接刷新。");
            Need(f.Events.IndexOf("scan") < f.Events.IndexOf("nav-state"), "開始查店仍等導航 Terminal／清理狀態。");
        });
        await Check("force_patrol_accepts_in_range_moving_position_without_standing", async () =>
        {
            var f = new Fixture(); var result = await f.Run();
            Need(result.State == "Completed" && f.Market.Scans == 1, result.Detail);
            Need(f.Nav.Stops == 1 && f.Nav.StateReads == 1, "入範圍沒有直接停止導航或丟失清理追蹤。");
        });
        foreach (string mode in new[] { "outside", "map", "source", "stale", "fallback" })
            await Check("force_patrol_actual_position_rejects_" + mode, async () =>
            {
                var f = new Fixture();
                f.Nav.Transform = state => mode switch
                {
                    "outside" => state with { X = state.TargetX + 3, Terminal = true, Completed = true, State = "Completed" },
                    "map" => state with { MapId = 5 },
                    "source" => state with { Source = Source with { CreationFileTime = 999 } },
                    "stale" => state with { SampledAt = Now.AddSeconds(-10), Terminal = true, Completed = true, State = "Completed" },
                    _ => state with { PositionVerified = false, Terminal = true, Completed = true, State = "Completed" }
                };
                var result = await f.Run();
                Need(result.State != "Completed" && f.Market.Scans == 0 && f.Market.Refreshes == 0, "無有效實際位置仍查店。");
            });
        await Check("force_patrol_range_is_rechecked_after_arrival", async () =>
        {
            var f = new Fixture(); f.Nav.TransformCapture = (n, value) => n >= 3 ? value with { X = value.X + 3 } : value;
            var result = await f.Run();
            Need(result.State == "Failed" && f.Market.Refreshes == 0, "到點後離開指定範圍仍查店。");
        });
        await Check("force_patrol_saved_unverified_history_does_not_gate_next_point", async () =>
        {
            var f = new Fixture(); f.Market.Uncertain = 1;
            var result = await f.Run(points: 2);
            Need(result.State == "Completed" && result.CleanupConfirmed && f.Market.Scans == 2, result.Detail);
        });
        await Check("force_patrol_continue_uses_plan_without_cancontinue_gate", async () =>
        {
            var f = new Fixture(); f.Market.RemainingFirst = 1;
            var result = await f.Run();
            Need(result.State == "Completed" && result.RetriesUsed == 1 && f.Market.Continues == 1, result.Detail);
            var continuation = f.Market.Commands.Single(c => c.Command == ShopCommands.ContinueSavedScan);
            Need(continuation.ForceDispatch && continuation.ExpectedScanPlanId == "force-plan-1", "接續未直接送原計畫。");
        });
        await Check("force_patrol_worker_completion_still_precedes_next_point", async () =>
        {
            var f = new Fixture(); f.Market.WorkerPolls = 2;
            var result = await f.Run(points: 2);
            Need(result.State == "Completed" && f.Market.StatusReads >= 4, result.Detail);
            int nextNavigation = f.Events.FindIndex(f.Events.IndexOf("scan") + 1, e => e == "navigate");
            Need(f.Events.IndexOf("market-worker-completed") < nextNavigation, "商店原生 worker 仍執行就走下一點。");
        });
        await Check("force_patrol_stop_dispatches_cancel_before_cleanup_status", async () =>
        {
            using var stop = new CancellationTokenSource();
            var f = new Fixture(); f.Market.WorkerPolls = 10; f.Market.AfterScan = stop.Cancel;
            var result = await f.Run(points: 2, token: stop.Token);
            Need(result.State == "Stopped" && result.CleanupConfirmed && f.Market.Scans == 1, result.Detail);
            int cancel = f.Events.IndexOf("cancel");
            Need(cancel >= 0 && !f.Events.Take(cancel).Contains("market-state"), "停止前仍先查商店狀態。");
            Need(f.Market.Commands.Single(c => c.Command == ShopCommands.CancelScan).ForceDispatch, "停止漏了強制參數。");
        });
        await Check("force_navigation_parser_submits_without_snapshot_confirmation", async () =>
        {
            int sends = 0, waits = 0;
            var expected = new PatrolObservation(Source, 11, 4, 10, 20, Now, false);
            var request = new PatrolNavigationRequest(new string('a', 32), expected,
                new(new string('b', 32), "離線點", "4", "離線圖", 10, 20), 2);
            object Wire() => new { pid = Source.ProcessId, creationFileTime = Source.CreationFileTime.ToString(),
                imagePath = Source.ImagePath, sha256 = Source.DiskSha256, imageBase = Source.ImageBase,
                imageSize = Source.ImageSize, selfId = "11" };
            await using var client = new PatrolNavigationClient(() => null, (method, path, payload, token) =>
            {
                Need(method == HttpMethod.Post && path == "/api/navigate", "命令前出現額外狀態輪詢。");
                var body = JsonSerializer.SerializeToElement(payload);
                Need(body.GetProperty("forceDispatch").GetBoolean(), "導航命令沒有forceDispatch。"); sends++;
                return Task.FromResult(new PatrolNavigationReply(HttpStatusCode.OK, JsonSerializer.SerializeToElement(new
                {
                    ok = true, jobId = request.JobId, source = Wire(), arrivalRadius = 2, waypoint = new { mapId = 4, x = 10, y = 20 },
                    state = "Completed", completed = true, terminal = true, cleanupConfirmed = true,
                    standing = false, standingSamples = 1, sameMap = true, mapId = 4, x = 10, y = 20, distance = 0,
                    sampledAtUtc = Now.ToString("O"), stopRequested = false, userStop = false
                })));
            }, () => Now, (_, _) => { waits++; return Task.CompletedTask; }, readOnly: false, forceDispatch: true);
            var state = await client.StartAsync(request, default);
            Need(state.PositionVerified && state.Completed && !state.Standing && sends == 1 && waits == 0,
                "force解析仍要求LiveContext狀態、站定或雙樣本確認。");
        });
        return checks.ToArray();
    }

    private static void Need(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }

    private sealed class Fixture
    {
        internal readonly List<string> Events = [];
        internal readonly FakeNavigation Nav;
        internal readonly FakeMarket Market;
        internal readonly PatrolController Controller;
        internal Fixture()
        {
            Nav = new(Events); Market = new(Events);
            Controller = new(Nav, Market, () => Now, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                TimeSpan.FromMilliseconds(500), forceDispatch: true);
        }
        internal Task<PatrolRunResult> Run(int points = 1, int rounds = 1, CancellationToken token = default) => Controller.RunAsync(new()
        {
            Revision = Guid.NewGuid().ToString("N"), SavedAt = Now, RetryLimit = 2, ArrivalRadius = 2, RoundCount = rounds,
            Points = Enumerable.Range(0, points).Select(i => new PatrolWaypoint(Guid.NewGuid().ToString("N"), "點" + i, "4", "離線圖", 10 + i * 10, 20)).ToArray()
        }, token);
    }

    private sealed class FakeNavigation(List<string> events) : IPatrolNavigation
    {
        internal int Starts, Stops, StateReads, Captures;
        internal Func<PatrolNavigationState, PatrolNavigationState>? Transform;
        internal Func<int, PatrolObservation, PatrolObservation>? TransformCapture;
        private int x = 10, y = 20;
        private PatrolNavigationState current = new();
        public Task<PatrolObservation> CaptureAsync(CancellationToken token, PatrolObservation? expected = null)
        {
            token.ThrowIfCancellationRequested(); Captures++;
            var sample = new PatrolObservation(Source, 11, 4, x, y, Now, false);
            return Task.FromResult(TransformCapture?.Invoke(Captures, sample) ?? sample);
        }
        public Task<PatrolNavigationState> StartAsync(PatrolNavigationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Starts++; events.Add("navigate"); x = request.Waypoint.X; y = request.Waypoint.Y;
            current = new() { JobId = request.JobId, WaypointId = request.Waypoint.PointId, TargetX = x, TargetY = y,
                ArrivalRadius = request.ArrivalRadius, Source = Source, SelfId = 11, MapId = 4, X = x, Y = y,
                SampledAt = Now, State = "Moving", PositionVerified = true, Standing = false, StandingSamples = 0 };
            current = Transform?.Invoke(current) ?? current;
            return Task.FromResult(current);
        }
        public Task<PatrolNavigationState> StopAsync(string expectedJobId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Need(expectedJobId == current.JobId, "錯誤導航ID");
            Stops++; events.Add("nav-stop");
            return Task.FromResult(current with { State = "CleanupPending", StopRequested = true, UserStop = true });
        }
        public Task<PatrolNavigationState> GetStateAsync(string jobId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Need(jobId == current.JobId, "錯誤導航ID");
            StateReads++; events.Add("nav-state");
            return Task.FromResult(current with { State = "Stopped", Terminal = true, CleanupConfirmed = true, WorkSettled = true });
        }
    }

    private sealed class FakeMarket(List<string> events) : IPatrolMarket
    {
        internal readonly List<ShopCommandRequest> Commands = [];
        internal int Scans, Refreshes, Continues, StatusReads, WorkerPolls, Uncertain, RemainingFirst;
        internal Action? AfterScan;
        private int pendingPolls;
        private string? plan;
        private ShopCommandResponse current = new();
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Commands.Add(request);
            if (request.Command != ShopCommands.GetState) Need(request.ForceDispatch, "未強制送出mutation。");
            if (request.Command == ShopCommands.RefreshTargets)
            {
                Refreshes++; events.Add("refresh");
                return Task.FromResult(current with { RequestId = request.RequestId, State = "TargetsRefreshed",
                    SessionBlocked = true, WorkSettled = false,
                    TargetSnapshot = new() { SnapshotId = "snapshot-" + Refreshes, SourceSessionKey = Source.SessionKey, SelfId = 11,
                        RefreshedAt = Now, CanDispatch = false } });
            }
            if (request.Command is ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan)
            {
                if (request.Command == ShopCommands.ScanVisible) { Scans++; plan = "force-plan-" + Scans; events.Add("scan"); }
                else { Continues++; events.Add("continue"); Need(request.ExpectedScanPlanId == plan, "接续別的plan。"); }
                int remaining = Continues == 0 ? RemainingFirst : 0;
                pendingPolls = WorkerPolls;
                current = new() { RequestId = request.RequestId, State = "ScanStarted", SessionBlocked = true, WorkSettled = pendingPolls == 0,
                    TargetSnapshot = new() { SnapshotId = request.TargetSnapshotId!, SourceSessionKey = Source.SessionKey, SelfId = 11, RefreshedAt = Now },
                    Scan = new() { JobId = request.RequestId, SourceSessionKey = Source.SessionKey, TargetSnapshotId = request.TargetSnapshotId,
                        Running = pendingPolls != 0 },
                    SavedScan = new() { PlanId = plan!, SourceSessionKey = Source.SessionKey, TotalPages = 1 + remaining + Uncertain,
                        SearchedPages = 1, ConfirmedPages = 0, RemainingPages = remaining, AvailablePages = remaining,
                        UncertainPages = Uncertain, CanContinue = false, ContinueBlockCode = "Unverified" } };
                AfterScan?.Invoke(); return Task.FromResult(current);
            }
            if (request.Command == ShopCommands.CancelScan)
            {
                events.Add("cancel"); Need(request.ExpectedScanJobId == current.Scan.JobId, "停止別的scan。");
                pendingPolls = 0;
                current = current with { WorkSettled = true, Scan = current.Scan with { Running = false, StopReason = "Cancelled" } };
                return Task.FromResult(current with { RequestId = request.RequestId });
            }
            Need(request.Command == ShopCommands.GetState, "未知商店指令。");
            StatusReads++; events.Add("market-state");
            if (--pendingPolls <= 0)
            {
                current = current with { WorkSettled = true, Scan = current.Scan with { Running = false } };
                events.Add("market-worker-completed");
            }
            return Task.FromResult(current with { RequestId = request.RequestId });
        }
    }
}
