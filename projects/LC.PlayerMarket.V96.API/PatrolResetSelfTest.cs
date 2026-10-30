using System.Security.Cryptography;
using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Immutable reset records and real coordinator, with offline ports only.</summary>
internal static class PatrolResetSelfTest
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly SourceIdentity Source = new(5432, 987654321, "offline-LC.exe", "0x140000000", 1, 65536,
        new string('A', 64), "offline-patrol-reset");

    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> action)
        {
            try { await action().WaitAsync(TimeSpan.FromSeconds(10)); checks.Add(new(name, true, "純離線；實際checkpoint與巡迴reset，沒有LC／helper／輸入。")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        await Check("patrol_reset_appends_tombstone_preserves_old_progress_and_pending", () =>
        {
            var f = new Fixture(); var old = f.Store.Save(f.Checkpoint() with { PointIndex = 2, Phase = PatrolCheckpointPhase.Search, PlanId = "old-plan" });
            string path = Directory.GetFiles(f.Store.DirectoryPath, "*.checkpoint.json").Single(); string before = Hash(path);
            string pending = Path.Combine(f.Root, "unrelated-native.pending.json"); File.WriteAllText(pending, "preserved offline pending evidence"); string pendingBefore = Hash(pending);
            var reset = f.Store.Reset(); var reloaded = new PatrolCheckpointStore(f.Root);
            Need(reset.PreviousRunId == old.RunId && reset.PreviousSequence == old.Sequence && reset.CleanupConfirmed
                && reset.Generation == 1 && reset.Sequence > old.Sequence && reloaded.LoadLatest() is null
                && reloaded.CurrentResetGeneration == 1 && Hash(path) == before && Hash(pending) == pendingBefore
                && Directory.GetFiles(f.Store.DirectoryPath, "*.reset.json").Length == 1,
                "重置沒有留存原檔／pending，或重開仍接續舊索引。");
            return Task.CompletedTask;
        });
        foreach (bool navigation in new[] { true, false })
            await Check("patrol_reset_refuses_unsettled_" + (navigation ? "navigation" : "scan"), () =>
            {
                var f = new Fixture(); var old = f.Checkpoint();
                old = navigation ? old with { NavigationRequest = new("old-nav", old.Origin!, f.Route.Points[2], 1), NavigationSettled = false }
                    : old with { ScanRequest = new() { RequestId = "old-scan", Command = ShopCommands.ScanVisible,
                        ExpectedSourceSessionKey = Source.SessionKey, ForceDispatch = true }, ScanSettled = false };
                var saved = f.Store.Save(old); bool rejected = false;
                try { f.Store.Reset(); } catch (InvalidOperationException) { rejected = true; }
                Need(rejected && SameCheckpoint(f.Store.LoadLatest(), saved) && f.Store.CurrentResetGeneration == 0
                    && Directory.GetFiles(f.Store.DirectoryPath, "*.reset.json").Length == 0,
                    "未釋放工作被tombstone遮蔽／原checkpoint變更。");
                return Task.CompletedTask;
            });
        await Check("patrol_reset_old_controller_late_save_rejected_across_new_store_and_run", () =>
        {
            var f = new Fixture(); var stale = f.Store.Save(f.Checkpoint() with { PointIndex = 2 }); f.Store.Reset();
            var freshStore = new PatrolCheckpointStore(f.Root); bool beforeRejected = false, afterRejected = false;
            try { f.Store.Save(stale); } catch (InvalidOperationException) { beforeRejected = true; }
            var fresh = freshStore.Save(f.Checkpoint() with { ResetGeneration = freshStore.CurrentResetGeneration });
            try { new PatrolCheckpointStore(f.Root).Save(stale); } catch (InvalidOperationException) { afterRejected = true; }
            Need(beforeRejected && afterRejected && SameCheckpoint(freshStore.LoadLatest(), fresh) && fresh.PointIndex == 0
                && fresh.CurrentRound == 1 && fresh.ResetGeneration == 1 && fresh.RunId != stale.RunId,
                "舊run晚寫回覆蓋了重置或新run。");
            return Task.CompletedTask;
        });
        await Check("patrol_reset_second_generation_rejects_previous_reset_run", () =>
        {
            var f = new Fixture(); f.Store.Save(f.Checkpoint()); f.Store.Reset();
            var firstGeneration = f.Store.Save(f.Checkpoint() with { ResetGeneration = 1 });
            var secondReset = f.Store.Reset(); bool rejected = false;
            try { f.Store.Save(firstGeneration); } catch (InvalidOperationException) { rejected = true; }
            Need(secondReset.Generation == 2 && rejected && f.Store.LoadLatest() is null
                && Directory.GetFiles(f.Store.DirectoryPath, "*.checkpoint.json").Length == 2
                && Directory.GetFiles(f.Store.DirectoryPath, "*.reset.json").Length == 2, "第二次重置回退世代或刪除歷史。");
            return Task.CompletedTask;
        });
        await Check("patrol_reset_corrupt_latest_marker_never_falls_back_to_old_checkpoint", () =>
        {
            var f = new Fixture(); f.Store.Save(f.Checkpoint() with { PointIndex = 2 }); f.Store.Reset();
            File.WriteAllText(Directory.GetFiles(f.Store.DirectoryPath, "*.reset.json").Single(), "{}"); bool rejected = false;
            try { new PatrolCheckpointStore(f.Root).LoadLatest(); } catch (InvalidDataException) { rejected = true; }
            Need(rejected && Directory.GetFiles(f.Store.DirectoryPath, "*.checkpoint.json").Length == 1, "毀損重置標記退回舊進度。");
            return Task.CompletedTask;
        });
        await Check("patrol_reset_explicit_old_log_import_cannot_revive_tombstoned_progress", () =>
        {
            var f = new Fixture(); f.Store.Save(f.Checkpoint()); f.Store.Reset(); bool rejected = false;
            try { f.Store.ImportLatestStoppedRun(f.Route, f.Checkpoint().Origin!, Path.Combine(f.Root, "never-read-old-logs")); }
            catch (InvalidOperationException) { rejected = true; }
            Need(rejected && f.Store.LoadLatest() is null && f.Store.CurrentResetGeneration == 1, "重置後匯入舊LOG恢復了首輪後段。");
            return Task.CompletedTask;
        });
        await Check("patrol_reset_manual_stop_third_point_then_new_run_starts_round_one_point_one", async () =>
        {
            var f = new Fixture(); using var stop = new CancellationTokenSource();
            f.Market.AfterScan = () => { if (f.Market.Scans == 3) stop.Cancel(); };
            var stopped = await f.Controller.RunAsync(f.Route, stop.Token); var old = f.Store.LoadLatest()!;
            Need(stopped.State == "Stopped" && stopped.CleanupConfirmed && old.PointIndex == 2 && old.PlanId == "plan-3"
                && old.NavigationSettled && old.ScanSettled, stopped.Detail);
            f.Market.AfterScan = null; f.Market.ClearActivePlan(); await f.Controller.ResetProgressAsync();
            Need(f.Store.LoadLatest() is null, "清理完成後controller沒有清除active巡迴checkpoint。");
            var starts = f.Nav.Starts.Count; var fresh = f.NewController(); var progress = new List<PatrolProgress>(); fresh.Progress += progress.Add;
            var result = await fresh.RunAsync(f.Route);
            Need(result.State == "Completed" && result.RunId != stopped.RunId && result.RetriesUsed == 0
                && f.Nav.Starts.Skip(starts).Select(r => r.Waypoint.PointId).SequenceEqual(f.Route.Points.Select(p => p.PointId))
                && progress.Any(p => p.CurrentRound == 1 && p.PointIndex == 0) && f.Market.Scans == 7 && f.Market.Continues == 0
                && f.Store.LoadLatest()!.ResetGeneration == 1 && f.Store.LoadLatest()!.RouteRevision == f.Route.Revision,
                "重置仍接續第三點／舊plan，或改變路線revision。");
        });
        await Check("patrol_reset_round_two_checkpoint_restarts_all_rounds_from_first_point", async () =>
        {
            var f = new Fixture(points: 2, rounds: 2);
            f.Store.Save(f.Checkpoint() with { CurrentRound = 2, CompletedRounds = 1, PointIndex = 1, RetriesUsed = 9, RecoveryCycles = 4 });
            await f.Controller.ResetProgressAsync(); var progress = new List<PatrolProgress>(); f.Controller.Progress += progress.Add;
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Completed" && result.CompletedRounds == 2 && result.RetriesUsed == 0
                && f.Nav.Starts.Select(r => r.Waypoint.PointId).SequenceEqual(f.Route.Points.Concat(f.Route.Points).Select(p => p.PointId))
                && progress.First(p => p.State == "Moving").CurrentRound == 1 && f.Store.LoadLatest()!.RecoveryCycles == 0,
                "第二輪checkpoint仍被接續或新run帶入舊retry。");
        });
        await Check("patrol_reset_active_run_rejected_then_joined_stop_allows_reset", async () =>
        {
            var f = new Fixture(points: 1); using var stop = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Nav.BeforeCapture = token => { entered.TrySetResult(); return Task.Delay(Timeout.Infinite, token); };
            var run = f.Controller.RunAsync(f.Route, stop.Token); await entered.Task; bool rejected = false;
            try { await f.Controller.ResetProgressAsync(); } catch (InvalidOperationException) { rejected = true; }
            Need(rejected && f.Store.CurrentResetGeneration == 0, "舊run仍執行就重置。");
            stop.Cancel(); var finished = await run; f.Nav.BeforeCapture = null;
            Need(finished.State == "Stopped" && finished.CleanupConfirmed, finished.Detail);
            await f.Controller.ResetProgressAsync();
            Need(f.Store.CurrentResetGeneration == 1 && f.Store.LoadLatest() is null && f.Nav.Starts.Count == 0 && f.Market.Commands.Count == 0,
                "停止join後reset送入新遊戲工作或仍保留舊進度。");
        });
        await Check("patrol_reset_blocked_cleanup_rechecks_original_id_before_tombstone", async () =>
        {
            var f = new Fixture(points: 1); var cp = f.Checkpoint(); var original = new PatrolNavigationRequest("original-unsettled", cp.Origin!, f.Route.Points[0], 1);
            f.Nav.Prepare(original); f.Nav.RejectStop = true;
            f.Store.Save(cp with { NavigationRequest = original, NavigationSettled = false });
            var blocked = await f.Controller.RunAsync(f.Route); Need(blocked.State == "Blocked" && !blocked.CleanupConfirmed, blocked.Detail);
            int attempts = f.Nav.Stops.Count; bool rejected = false;
            try { await f.Controller.ResetProgressAsync(); } catch (InvalidOperationException) { rejected = true; }
            Need(rejected && f.Store.CurrentResetGeneration == 0 && f.Store.LoadLatest()!.NavigationRequest == original
                && f.Nav.Stops.Count > attempts, "blocked reset未核原ID就丟棄未知工作。");
            f.Nav.RejectStop = false; await f.Controller.ResetProgressAsync();
            Need(f.Store.LoadLatest() is null && f.Store.CurrentResetGeneration == 1 && f.Nav.Stops.All(id => id == original.JobId)
                && f.Nav.Starts.Count == 0 && f.Market.Commands.Count == 0, "清理成功前已重置，或recheck重送原導航。");
        });
        await Check("patrol_reset_precancelled_request_preserves_existing_checkpoint", async () =>
        {
            var f = new Fixture(); var saved = f.Store.Save(f.Checkpoint() with { PointIndex = 2 });
            using var stop = new CancellationTokenSource(); stop.Cancel(); bool cancelled = false;
            try { await f.Controller.ResetProgressAsync(stop.Token); } catch (OperationCanceledException) { cancelled = true; }
            Need(cancelled && SameCheckpoint(f.Store.LoadLatest(), saved) && f.Store.CurrentResetGeneration == 0, "已取消仍寫入reset。");
        });
        return checks.ToArray();
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static bool SameCheckpoint(PatrolCheckpoint? actual, PatrolCheckpoint expected) => actual is not null
        && actual.Sequence == expected.Sequence && actual.RunId == expected.RunId && actual.At == expected.At
        && actual.PointIndex == expected.PointIndex && actual.ResetGeneration == expected.ResetGeneration;
    private static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }

    private sealed class Fixture
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "LcPatrolResetOffline", Guid.NewGuid().ToString("N"));
        internal readonly PatrolCheckpointStore Store;
        internal readonly PatrolRouteSnapshot Route;
        internal readonly FakeNavigation Nav = new();
        internal readonly FakeMarket Market = new();
        internal readonly PatrolController Controller;
        internal Fixture(int points = 4, int rounds = 1)
        {
            Store = new(Root); Route = new() { Revision = Guid.NewGuid().ToString("N"), SavedAt = Now, RoundCount = rounds,
                RetryLimit = 1, ArrivalRadius = 1, Points = Enumerable.Range(0, points).Select(i => new PatrolWaypoint(
                    Guid.NewGuid().ToString("N"), "離線點" + i, "4", "離線圖", 10 + i * 10, 20)).ToArray() };
            Controller = NewController();
        }
        internal PatrolController NewController() => new(Nav, Market, () => Now,
            (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, TimeSpan.FromMilliseconds(100),
            forceDispatch: true, checkpointStore: Store, autoResume: true);
        internal PatrolCheckpoint Checkpoint() => new() { RunId = Guid.NewGuid().ToString("N"), RouteRevision = Route.Revision,
            RouteFingerprint = PatrolCheckpointStore.Fingerprint(Route), TotalRounds = Route.RoundCount, TotalPoints = Route.Points.Length,
            CurrentRound = 1, Phase = PatrolCheckpointPhase.Navigate, Origin = new(Source, 11, 4, 10, 20, Now, false), Status = "Stopped" };
    }

    private sealed class FakeNavigation : IPatrolNavigation
    {
        internal readonly List<PatrolNavigationRequest> Starts = [];
        internal readonly List<string> Stops = [];
        internal Func<CancellationToken, Task>? BeforeCapture;
        internal bool RejectStop;
        private int x = 10, y = 20;
        private PatrolNavigationState current = new();
        public async Task<PatrolObservation> CaptureAsync(CancellationToken token, PatrolObservation? expected = null)
        { token.ThrowIfCancellationRequested(); if (BeforeCapture is { } before) await before(token); return new(Source, 11, 4, x, y, Now, false); }
        internal void Prepare(PatrolNavigationRequest request)
        {
            x = request.Waypoint.X; y = request.Waypoint.Y;
            current = new() { JobId = request.JobId, WaypointId = request.Waypoint.PointId, Source = Source, SelfId = 11,
                MapId = 4, X = x, Y = y, SampledAt = Now, TargetX = x, TargetY = y, ArrivalRadius = request.ArrivalRadius,
                State = "Completed", Completed = true, Terminal = true, CleanupConfirmed = true, WorkSettled = true, PositionVerified = true };
        }
        public Task<PatrolNavigationState> StartAsync(PatrolNavigationRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Starts.Add(request); Prepare(request); return Task.FromResult(current); }
        public Task<PatrolNavigationState> GetStateAsync(string jobId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Need(jobId == current.JobId, "wrong navigation ID"); return Task.FromResult(current); }
        public Task<PatrolNavigationState> StopAsync(string expectedJobId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Stops.Add(expectedJobId); Need(expectedJobId == current.JobId, "wrong stop ID");
            if (RejectStop) throw new IOException("offline original cleanup not proven"); return Task.FromResult(current);
        }
    }

    private sealed class FakeMarket : IPatrolMarket
    {
        internal readonly List<ShopCommandRequest> Commands = [];
        internal int Scans, Continues, Refreshes;
        internal Action? AfterScan;
        private ShopCommandResponse current = new();
        internal void ClearActivePlan() { current = new(); }
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Commands.Add(request);
            if (request.Command == ShopCommands.RefreshTargets)
            {
                Refreshes++; return Task.FromResult(current with { RequestId = request.RequestId, State = "TargetsRefreshed",
                    CurrentSourceSessionKey = Source.SessionKey, TargetSnapshot = new() { SnapshotId = "snapshot-" + Refreshes,
                        SourceSessionKey = Source.SessionKey, SelfId = 11, RefreshedAt = Now, CanDispatch = true } });
            }
            if (request.Command is ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan)
            {
                if (request.Command == ShopCommands.ScanVisible) Scans++; else Continues++;
                string plan = request.Command == ShopCommands.ScanVisible ? "plan-" + Scans : request.ExpectedScanPlanId!;
                current = new() { RequestId = request.RequestId, State = "ScanStarted", WorkSettled = true, CurrentSourceSessionKey = Source.SessionKey,
                    Scan = new() { JobId = request.RequestId, SourceSessionKey = Source.SessionKey, TargetSnapshotId = request.TargetSnapshotId, Running = false },
                    SavedScan = new() { PlanId = plan, SourceSessionKey = Source.SessionKey, TotalPages = 1, SearchedPages = 1,
                        ConfirmedPages = 0, RemainingPages = 0, AvailablePages = 0, CanContinue = false },
                    TargetSnapshot = new() { SnapshotId = request.TargetSnapshotId!, SourceSessionKey = Source.SessionKey,
                        SelfId = 11, RefreshedAt = Now, CanDispatch = true } };
                AfterScan?.Invoke(); return Task.FromResult(current);
            }
            Need(request.Command is ShopCommands.CancelScan or ShopCommands.GetState, "unexpected merchant command");
            if (request.Command == ShopCommands.CancelScan) Need(request.ExpectedScanJobId == current.Scan.JobId, "wrong cancel ID");
            return Task.FromResult(current with { RequestId = request.RequestId });
        }
    }
}
