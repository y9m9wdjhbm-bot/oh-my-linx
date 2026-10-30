using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Real capture client and resumable coordinator; all process/game ports are offline.</summary>
internal static class PatrolCaptureAutoRecoveryVerification
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 2, 55, 34, TimeSpan.Zero);
    private static readonly SourceIdentity Source = new(123, 456, "offline-LC.exe", "0x140000000", 1,
        65536, new string('A', 64), "capture-auto-recovery-offline");

    internal static int RunReport(string output)
    {
        var checks = RunAsync().GetAwaiter().GetResult();
        var report = new OfflineCheckReport("83.0.0", checks.Length > 0 && checks.All(c => c.Passed),
            checks.Count(c => c.Passed), checks.Count(c => !c.Passed), false, false, false, checks);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, HubProtocol.Json); file.Flush(true);
        return report.Success ? 0 : 1;
    }

    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); checks.Add(new(name, true, "真 capture client／controller，隔離 checkpoint 與假遊戲 ports；未操作 LC。")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }

        foreach (string phase in new[] { "initial", "arrival", "refreshed" })
            await Check("capture_auto_owned_read_incomplete_recovers_" + phase, async () =>
            {
                await using var f = new Fixture(points: 1);
                if (phase == "initial") f.ArmFailure();
                if (phase == "arrival") f.AfterNavigation = () => f.ArmFailure();
                if (phase == "refreshed") f.AfterRefresh = () => { if (!f.Injected) f.ArmFailure(); };
                var result = await f.Controller.RunAsync(f.Route);
                Need(result.State == "Completed" && result.CleanupConfirmed && result.CompletedPoints == 1, result.Detail);
                Need(f.Cooldowns.SequenceEqual(new[] { 2d }) && f.Failures == 1, "未經一次冷卻，或將暫時讀失敗改成快速重送。");
                Need(f.NavigationStarts.Count == 1 && f.Market.Scans == 1 && f.Market.Continues == 0,
                    "唯讀恢復重送導航／搜尋或遺失本點。");
                f.AssertReadRecoveryOrdering();
            });

        foreach (bool pending in new[] { false, true })
            await Check("capture_auto_third_point_" + (pending ? "running" : "settled") + "_scan_keeps_plan", async () =>
            {
                await using var f = new Fixture();
                f.Market.PartialPoint = 3; f.Market.WorkerPolls = pending ? 2 : 0;
                f.AfterScan = () => { if (f.Market.Scans == 3) f.ArmFailure(); };
                var result = await f.Controller.RunAsync(f.Route);
                Need(result.State == "Completed" && result.CompletedPoints == 4 && result.CompletedRounds == 1 && result.CleanupConfirmed, result.Detail);
                Need(f.Market.Scans == 4 && f.Market.Continues == 1 && f.NavigationStarts.Count == 4,
                    "第三點定位失敗後回首點、重建原計畫或重送原導航。");
                Need(f.NavigationStarts.Select(r => r.Waypoint.PointId).SequenceEqual(f.Route.Points.Select(p => p.PointId)),
                    "恢復後跳點或變更原定位點順序。");
                var continued = f.Market.Commands.Single(c => c.Command == ShopCommands.ContinueSavedScan);
                Need(continued.ExpectedScanPlanId == "plan-3" && continued.TargetSnapshotId != f.Market.OriginalPartialRequest!.TargetSnapshotId,
                    "沒有使用原計畫及重新刷新的名單接續。");
                Need(f.Market.PageSubmissions.Count == f.Market.PageSubmissions.Distinct().Count()
                    && f.Market.PageSubmissions.Count(p => p.StartsWith("plan-3/", StringComparison.Ordinal)) == 3,
                    "已確認或已送未確認頁被再次提交。");
                var cool = f.Recovering.Single();
                Need(cool.PointIndex == 2 && cool.Phase == PatrolCheckpointPhase.Search && cool.PlanId == "plan-3"
                    && cool.ScanRequest?.RequestId == f.Market.OriginalPartialRequest.RequestId && cool.ScanSettled == !pending,
                    "冷卻 checkpoint 未保留第 3 點 Search／原 request／plan／settlement。");
                if (pending)
                {
                    int failure = f.Events.FindIndex(e => e.StartsWith("capture-fail:", StringComparison.Ordinal));
                    int state = f.Events.FindIndex(failure + 1, e => e == "status:" + f.Market.OriginalPartialRequest.RequestId);
                    int settled = f.Events.FindIndex(failure + 1, e => e == "market-settled:" + f.Market.OriginalPartialRequest.RequestId);
                    int nextMutation = f.Events.FindIndex(failure + 1, e => e is ShopCommands.RefreshTargets or ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan);
                    Need(state > failure && settled >= state && nextMutation > settled,
                        "原工作仍未釋放時刷新／發出另一個搜尋，或沒有追蹤原 request。");
                }
                f.AssertReadRecoveryOrdering();
            });

        await Check("capture_auto_completed_original_plan_does_not_search_again", async () =>
        {
            await using var f = new Fixture();
            f.AfterScan = () => { if (f.Market.Scans == 3) f.ArmFailure(); };
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Completed" && f.Market.Scans == 4 && f.Market.Continues == 0 && f.NavigationStarts.Count == 4,
                "原 plan 已無未送頁，定位恢復後仍重查已完成頁。");
            Need(f.Recovering.Single().PointIndex == 2 && f.Recovering.Single().PlanId == "plan-3", "遺失失敗點的已完成 plan。");
        });

        await Check("capture_auto_status_terminal_tracks_old_capture_before_fresh_id", async () =>
        {
            await using var f = new Fixture(points: 1); f.TimeoutFirstFailure = true;
            f.AfterScan = f.ArmFailure;
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Completed" && f.StatusCaptureIds.Count == 1 && f.Cooldowns.Count == 1, result.Detail);
            Need(f.StatusCaptureIds[0] == f.FailedCaptureIds[0] && f.PostCaptureIds.Distinct().Count() == f.PostCaptureIds.Count,
                "逾時後沒有用原取點 ID 查 terminal，或重用了已結束 ID。");
            Need(f.Market.Scans == 1 && f.NavigationStarts.Count == 1, "status 恢復重送了動作。");
            f.AssertReadRecoveryOrdering();
        });

        await Check("capture_auto_repeated_read_gaps_use_capped_cancellable_backoff", async () =>
        {
            await using var f = new Fixture(points: 1);
            f.AfterScan = () => f.ArmFailure(6);
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Completed" && f.Cooldowns.SequenceEqual(new[] { 2d, 4d, 8d, 16d, 30d, 30d }), result.Detail);
            Need(f.Failures == 6 && f.Market.Scans == 1 && f.Market.Continues == 0 && f.NavigationStarts.Count == 1,
                "連續讀空窗增加動作提交或以無界快速輪詢代替冷卻。");
            Need(f.Recovering.All(c => c.PointIndex == 0 && c.PlanId == "plan-1" && c.Phase == PatrolCheckpointPhase.Search),
                "連續冷卻重設了同點階段。");
            f.AssertReadRecoveryOrdering();
        });

        foreach (string stop in new[] { "token", "UserStop", "Cancelled" })
            await Check("capture_auto_" + stop + "_during_cooldown_prevents_resume", async () =>
            {
                await using var f = new Fixture(points: 1); using var cancellation = new CancellationTokenSource();
                f.Market.PartialPoint = 1; f.Market.WorkerPolls = 2; f.AfterScan = f.ArmFailure;
                f.OnCooldown = _ => { if (stop == "token") cancellation.Cancel(); else f.Market.StopReason = stop; };
                var result = await f.Controller.RunAsync(f.Route, cancellation.Token);
                Need(result.State == "Stopped" && result.CleanupConfirmed && f.Market.Scans == 1
                    && f.Market.Continues == 0 && f.NavigationStarts.Count == 1, result.Detail);
                var checkpoint = f.Store.LoadLatest()!;
                Need(checkpoint.PointIndex == 0 && checkpoint.PlanId == "plan-1" && checkpoint.Status == "Stopped",
                    "人工停止後覆蓋原點或原計畫。");
                Need(f.Market.Commands.Where(c => c.Command == ShopCommands.CancelScan)
                    .All(c => c.ExpectedScanJobId == f.Market.OriginalPartialRequest!.RequestId), "清理取消了非本次 job。");
            });

        await Check("capture_auto_cancel_before_failed_receipt_consumed_wins_over_recovery", async () =>
        {
            await using var f = new Fixture(points: 1); using var stop = new CancellationTokenSource();
            f.AfterScan = f.ArmFailure; f.OnFailureReceipt = stop.Cancel;
            var result = await f.Controller.RunAsync(f.Route, stop.Token);
            Need(result.State == "Stopped" && f.Cooldowns.Count == 0 && f.Failures == 1 && f.Market.Scans == 1,
                "收到 terminal 前的使用者取消被 typed recovery 覆蓋。");
        });

        foreach (string changed in new[] { "source", "self", "map" })
            await Check("capture_auto_" + changed + "_change_never_rebinds_origin", async () =>
            {
                await using var f = new Fixture(points: 1); f.Market.PartialPoint = 1; f.AfterScan = f.ArmFailure;
                f.OnCooldown = _ =>
                {
                    if (changed == "source") f.CurrentSource = Source with { CreationFileTime = 789, SessionKey = "changed-source" };
                    if (changed == "self") f.SelfId = 12;
                    if (changed == "map") f.MapId = 5;
                };
                var result = await f.Controller.RunAsync(f.Route);
                Need(result.State is "Blocked" or "Failed" && f.Market.Continues == 0 && f.Market.Scans == 1
                    && f.NavigationStarts.Count == 1, "自動恢復重綁新來源／角色／地圖並繼續送出。");
                Need(f.Store.LoadLatest()!.Origin!.Source == Source && f.Store.LoadLatest()!.Origin!.SelfId == 11,
                    "自動恢復覆寫固定 origin；只有新手動 Start 才可重綁。");
            });

        await Check("capture_auto_unknown_native_result_remains_blocked_and_unsent", async () =>
        {
            await using var f = new Fixture(points: 1); f.Market.PartialPoint = 1; f.Market.WorkerPolls = 1; f.AfterScan = f.ArmFailure;
            f.OnCooldown = _ => f.Market.UnknownResult = true;
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Blocked" && f.Market.Scans == 1 && f.Market.Continues == 0 && f.Market.PageSubmissions.Count == 2,
                "唯讀取點恢復授權了未知原生結果重送。");
            Need(f.Store.LoadLatest()!.PlanId == "plan-1", "未知結果阻擋時丟失原 plan。");
        });

        await Check("capture_auto_foreign_status_cannot_replace_owned_scan", async () =>
        {
            await using var f = new Fixture(points: 1); f.Market.PartialPoint = 1; f.Market.WorkerPolls = 2; f.AfterScan = f.ArmFailure;
            f.OnCooldown = _ => f.Market.ForeignStatus = true;
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Blocked" && !result.CleanupConfirmed && f.Market.Scans == 1 && f.Market.Continues == 0,
                "來源工作不符仍接管或宣稱已清理。");
        });

        foreach (string invalid in new[] { "generic", "sourceError", "sourcePhase", "cleanup", "input", "source", "captureId" })
            await Check("capture_auto_invalid_" + invalid + "_receipt_still_stops", async () =>
            {
                await using var f = new Fixture(points: 1); f.InvalidProof = invalid; f.AfterScan = f.ArmFailure;
                var result = await f.Controller.RunAsync(f.Route);
                Need(result.State == "Failed" && f.Cooldowns.Count == 0 && f.Failures == 1 && f.Market.Scans == 1
                    && f.Market.Continues == 0 && f.NavigationStarts.Count == 1,
                    "generic／不完整終態證據被擴成自動重試。");
            });

        await Check("capture_auto_disabled_mode_does_not_enable_recovery", async () =>
        {
            await using var f = new Fixture(points: 1, autoResume: false); f.AfterScan = f.ArmFailure;
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Failed" && f.Cooldowns.Count == 0 && f.Market.Scans == 1 && f.Market.Continues == 0,
                "未啟用 autoResume 仍自行重启。");
        });
        return checks.ToArray();
    }

    private static void Need(bool condition, string detail)
    { if (!condition) throw new InvalidOperationException(detail); }

    private sealed class Fixture : IPatrolNavigation, IAsyncDisposable
    {
        internal readonly PatrolCheckpointStore Store;
        internal readonly PatrolRouteSnapshot Route;
        internal readonly PatrolController Controller;
        internal readonly MarketPort Market;
        internal readonly List<PatrolNavigationRequest> NavigationStarts = [];
        internal readonly List<double> Cooldowns = [];
        internal readonly List<PatrolCheckpoint> Recovering = [];
        internal readonly List<string> Events = [], PostCaptureIds = [], FailedCaptureIds = [], StatusCaptureIds = [];
        internal SourceIdentity CurrentSource = Source;
        internal ulong SelfId = 11;
        internal int MapId = 4, X = 10, Y = 20, Failures;
        internal bool Injected, TimeoutFirstFailure;
        internal string? InvalidProof;
        internal Action? AfterNavigation, AfterRefresh, AfterScan, OnFailureReceipt;
        internal Action<double>? OnCooldown;
        private readonly PatrolNavigationClient capture;
        private PatrolNavigationState currentNavigation = new();
        private int failureUntilCooldown;
        private JsonElement pendingTimeoutReceipt;

        internal Fixture(int points = 4, bool autoResume = true)
        {
            string root = Path.Combine(Path.GetTempPath(), "LcCaptureAutoRecoveryOffline", Guid.NewGuid().ToString("N"));
            Store = new(root);
            Route = new() { Revision = Guid.NewGuid().ToString("N"), SavedAt = Now, RoundCount = 1, RetryLimit = 1, ArrivalRadius = 1,
                Points = Enumerable.Range(0, points).Select(i => new PatrolWaypoint(Guid.NewGuid().ToString("N"), "離線點" + (i + 1),
                    "4", "離線圖", 10 + i * 10, 20)).ToArray() };
            Market = new(this);
            capture = new(() => new HubSnapshot { Source = CurrentSource, State = "Reading", PublishedAt = Now,
                Fields = new() { ["selfId"] = FieldReading.Observed(SelfId, Now),
                    ["position"] = FieldReading.Observed(new { x = X, y = Y }, Now) } },
                Transport, () => Now, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, forceDispatch: true);
            Controller = new(this, Market, () => Now, (amount, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (amount >= TimeSpan.FromSeconds(2))
                {
                    Need(Cooldowns.Count < 8, "離線 bounded scenario 超出 8 次冷卻，拒絕無界測試。");
                    Cooldowns.Add(amount.TotalSeconds); Events.Add("cooldown:" + amount.TotalSeconds.ToString(CultureInfo.InvariantCulture));
                    var checkpoint = Store.LoadLatest(); if (checkpoint is not null) Recovering.Add(checkpoint);
                    OnCooldown?.Invoke(amount.TotalSeconds);
                }
                token.ThrowIfCancellationRequested(); return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(500), forceDispatch: true, checkpointStore: Store, autoResume: autoResume);
        }

        internal void ArmFailure() => ArmFailure(1);
        internal void ArmFailure(int cycles)
        { Injected = true; failureUntilCooldown = Cooldowns.Count + cycles; }

        private Task<PatrolNavigationReply> Transport(HttpMethod method, string path, object? payload, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (method == HttpMethod.Get)
            {
                string id = pendingTimeoutReceipt.GetProperty("captureId").GetString()!;
                Need(path == "/api/capture-status?captureId=" + id, "status 沒有追蹤原取點 ID。");
                StatusCaptureIds.Add(id); Events.Add("capture-status:" + id);
                var terminal = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(pendingTimeoutReceipt.GetRawText())!;
                terminal["ok"] = JsonSerializer.SerializeToElement(true);
                return Task.FromResult(new PatrolNavigationReply(HttpStatusCode.OK, JsonSerializer.SerializeToElement(terminal)));
            }
            Need(method == HttpMethod.Post && path == "/api/capture", "離線 capture transport 禁止送出導航或遊戲輸入。");
            Need(PostCaptureIds.Count < 100, "capture 未有界停止。");
            var request = JsonSerializer.SerializeToElement(payload);
            Need(request.GetProperty("forceDispatch").GetBoolean(), "真實 force 模式遺失。");
            string idNew = request.GetProperty("captureId").GetString()!;
            PostCaptureIds.Add(idNew);
            bool failed = Injected && Cooldowns.Count < failureUntilCooldown;
            var body = new Dictionary<string, object?>
            {
                ["ok"] = !failed, ["captureId"] = idNew, ["expectedSource"] = request.GetProperty("expectedSource"),
                ["source"] = new { pid = CurrentSource.ProcessId, creationFileTime = CurrentSource.CreationFileTime.ToString(CultureInfo.InvariantCulture),
                    imagePath = CurrentSource.ImagePath, sha256 = CurrentSource.DiskSha256, imageBase = CurrentSource.ImageBase,
                    imageSize = CurrentSource.ImageSize, selfId = SelfId.ToString(CultureInfo.InvariantCulture) },
                ["readOnly"] = true, ["inputSent"] = false, ["accepted"] = true, ["terminal"] = true,
                ["cleanupConfirmed"] = true, ["state"] = failed ? "Failed" : "Completed", ["error"] = failed ? "READ_CAPTURE_FAILED" : "",
                ["mapId"] = MapId, ["x"] = X, ["y"] = Y, ["standing"] = false, ["sampledAtUtc"] = Now.ToString("O")
            };
            if (failed)
            {
                Failures++; FailedCaptureIds.Add(idNew); Events.Add("capture-fail:" + idNew);
                body["sourceError"] = "READ_INCOMPLETE"; body["sourcePhase"] = "factory.context";
                switch (InvalidProof)
                {
                    case "generic": body.Remove("sourceError"); body.Remove("sourcePhase"); break;
                    case "sourceError": body["sourceError"] = "UNKNOWN_READ_FAILURE"; break;
                    case "sourcePhase": body["sourcePhase"] = "factory.start"; break;
                    case "cleanup": body["cleanupConfirmed"] = false; break;
                    case "input": body["inputSent"] = true; break;
                    case "source": body["expectedSource"] = new { pid = 999 }; break;
                    case "captureId": body["captureId"] = new string('f', 32); break;
                }
                if (TimeoutFirstFailure)
                {
                    TimeoutFirstFailure = false; pendingTimeoutReceipt = JsonSerializer.SerializeToElement(body);
                    throw new OperationCanceledException("offline POST timeout; original read completed after lost reply");
                }
                OnFailureReceipt?.Invoke();
            }
            else Events.Add("capture-ok:" + idNew);
            return Task.FromResult(new PatrolNavigationReply(failed ? HttpStatusCode.Conflict : HttpStatusCode.OK,
                JsonSerializer.SerializeToElement(body)));
        }

        internal void AssertReadRecoveryOrdering()
        {
            Need(PostCaptureIds.Count == PostCaptureIds.Distinct().Count(), "復用取點 ID。");
            foreach (string failedId in FailedCaptureIds)
            {
                int failed = Events.IndexOf("capture-fail:" + failedId);
                int cooldown = Events.FindIndex(failed + 1, e => e.StartsWith("cooldown:", StringComparison.Ordinal));
                int nextRead = Events.FindIndex(failed + 1, e => e.StartsWith("capture-ok:", StringComparison.Ordinal)
                    || e.StartsWith("capture-fail:", StringComparison.Ordinal));
                Need(cooldown > failed && nextRead > cooldown, "typed READ_INCOMPLETE 沒有先冷卻就送下一筆取點。");
                Need(!Events.Skip(failed + 1).Take(cooldown - failed - 1).Any(e => e.StartsWith("navigate:", StringComparison.Ordinal)
                    || e is ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan), "冷卻前重送了行走或搜尋。");
            }
        }

        public Task<PatrolObservation> CaptureAsync(CancellationToken token, PatrolObservation? expected = null) => capture.CaptureAsync(token, expected);
        public Task<PatrolNavigationState> StartAsync(PatrolNavigationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); NavigationStarts.Add(request); Events.Add("navigate:" + request.Waypoint.PointId);
            X = request.Waypoint.X; Y = request.Waypoint.Y;
            currentNavigation = new() { JobId = request.JobId, WaypointId = request.Waypoint.PointId, TargetX = X, TargetY = Y,
                ArrivalRadius = request.ArrivalRadius, Source = Source, SelfId = 11, MapId = 4, X = X, Y = Y, SampledAt = Now,
                State = "Completed", Completed = true, Terminal = true, CleanupConfirmed = true, WorkSettled = true, PositionVerified = true };
            AfterNavigation?.Invoke(); return Task.FromResult(currentNavigation);
        }
        public Task<PatrolNavigationState> GetStateAsync(string jobId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Need(jobId == currentNavigation.JobId, "wrong navigation ownership"); return Task.FromResult(currentNavigation); }
        public Task<PatrolNavigationState> StopAsync(string expectedJobId, CancellationToken token) => GetStateAsync(expectedJobId, token);
        public ValueTask DisposeAsync() => capture.DisposeAsync();
    }

    private sealed class MarketPort(Fixture f) : IPatrolMarket
    {
        internal readonly List<ShopCommandRequest> Commands = [];
        internal readonly List<string> PageSubmissions = [];
        internal int Scans, Continues, Refreshes, PartialPoint, WorkerPolls;
        internal bool UnknownResult, ForeignStatus;
        internal string? StopReason;
        internal ShopCommandRequest? OriginalPartialRequest;
        private int pollsRemaining;
        private ShopCommandResponse current = new();

        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Commands.Add(request); f.Events.Add(request.Command);
            Need(request.Command == ShopCommands.GetState || request.ForceDispatch, "mutating request lost force mode");
            if (request.Command == ShopCommands.RefreshTargets)
            {
                Refreshes++;
                var refreshed = current with { RequestId = request.RequestId, State = "TargetsRefreshed", CurrentSourceSessionKey = Source.SessionKey,
                    TargetSnapshot = new() { SnapshotId = "snapshot-" + Refreshes, SourceSessionKey = Source.SessionKey,
                        SelfId = 11, RefreshedAt = Now, CanDispatch = !UnknownResult } };
                if (UnknownResult && refreshed.SavedScan is { } saved)
                    refreshed = refreshed with { SessionBlocked = true, SavedScan = saved with { CanContinue = false,
                        SearchedPages = 1, ConfirmedPages = 1, UncertainPages = 1, ContinueBlockCode = "SessionBlocked" } };
                f.AfterRefresh?.Invoke(); return Task.FromResult(refreshed);
            }
            if (request.Command is ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan)
            {
                if (UnknownResult)
                    return Task.FromResult(current with { RequestId = request.RequestId, State = "SessionBlocked", SessionBlocked = true,
                        Detail = "offline broker refuses unknown native result", WorkSettled = true, CurrentSourceSessionKey = Source.SessionKey });
                string plan; int remaining;
                if (request.Command == ShopCommands.ScanVisible)
                {
                    Scans++; plan = "plan-" + Scans; remaining = Scans == PartialPoint ? 1 : 0;
                    // Two distinct pages: confirmed and sent-without-confirmed-response.
                    PageSubmissions.Add(plan + "/confirmed"); PageSubmissions.Add(plan + "/sent");
                    if (remaining == 0) PageSubmissions.Add(plan + "/last");
                    else OriginalPartialRequest = request;
                    pollsRemaining = remaining > 0 ? WorkerPolls : 0;
                }
                else
                {
                    Need(request.ExpectedScanPlanId == current.SavedScan!.PlanId, "continue changed plan");
                    Need(current.WorkSettled && !current.Scan.Running, "continue while original worker active");
                    Continues++; plan = current.SavedScan.PlanId; remaining = 0; PageSubmissions.Add(plan + "/last");
                    pollsRemaining = 0;
                }
                current = new() { RequestId = request.RequestId, State = "ScanStarted", WorkSettled = pollsRemaining == 0,
                    CurrentSourceSessionKey = Source.SessionKey,
                    Scan = new() { JobId = request.RequestId, SourceSessionKey = Source.SessionKey, TargetSnapshotId = request.TargetSnapshotId,
                        Running = pollsRemaining > 0 },
                    SavedScan = new() { PlanId = plan, SourceSessionKey = Source.SessionKey, TotalPages = 3, SearchedPages = 3 - remaining,
                        ConfirmedPages = 1, RemainingPages = remaining, AvailablePages = remaining, CanContinue = remaining > 0 },
                    TargetSnapshot = new() { SnapshotId = request.TargetSnapshotId!, SourceSessionKey = Source.SessionKey,
                        SelfId = 11, RefreshedAt = Now, CanDispatch = true } };
                if (request.Command == ShopCommands.ScanVisible) f.AfterScan?.Invoke();
                return Task.FromResult(current);
            }
            if (request.Command == ShopCommands.CancelScan)
            {
                Need(request.ExpectedScanJobId == current.Scan.JobId, "cancel changed scan owner");
                pollsRemaining = 0; current = current with { WorkSettled = true, Scan = current.Scan with { Running = false, StopReason = "Cancelled" } };
            }
            else
            {
                Need(request.Command == ShopCommands.GetState, "unexpected market command");
                f.Events.Add("status:" + current.Scan.JobId);
                if (pollsRemaining > 0)
                {
                    pollsRemaining--; current = current with { WorkSettled = pollsRemaining == 0,
                        Scan = current.Scan with { Running = pollsRemaining != 0, StopReason = StopReason } };
                    if (pollsRemaining == 0) f.Events.Add("market-settled:" + current.Scan.JobId);
                }
            }
            return Task.FromResult(current with { RequestId = request.RequestId, State = "State",
                Scan = ForeignStatus ? current.Scan with { JobId = "foreign-job" } : current.Scan });
        }
    }
}
