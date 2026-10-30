using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Actual resumable PatrolController, immutable checkpoints, and fake navigation/market ports.</summary>
internal static class PatrolScanAcceptanceVerification
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
    private static readonly SourceIdentity Source = new(1234, 5678, "offline-LC.exe", "0x140000000", 1, 65536, new string('A', 64), "offline-acceptance-only");

    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> test)
        {
            try { await test().WaitAsync(TimeSpan.FromSeconds(10)); checks.Add(new("scan_acceptance_" + name, true, "Actual production coordinator with fake ports only; no helper, LC reads or input.")); }
            catch (Exception error) { checks.Add(new("scan_acceptance_" + name, false, error.ToString())); }
        }
        foreach (string state in new[] { "SessionBlocked", "Rejected", "Busy" })
            await Check("definitive_" + state + "_retains_old_settled_job_and_target", async () =>
            {
                var f = new Fixture("reject") { RefusalState = state };
                var result = await f.Run(); var saved = f.Store.LoadLatest()!;
                Need(result.State == "Blocked" && result.CleanupConfirmed && result.CompletedPoints == 0, result.Detail);
                Need(f.First is not null && f.Second is not null && f.First.TargetSnapshotId != f.Second.TargetSnapshotId,
                    "Fixture did not reproduce refreshed targets differing from the previous scan's target.");
                Need(saved.ScanRequest?.RequestId == f.First!.RequestId && saved.ScanSettled && saved.PlanId == "plan-one"
                    && f.Cancels.Count == 0 && f.Market.Current.SessionBlocked && f.Market.Current.SavedScan!.UncertainPages == 1,
                    "Definitively refused new request replaced old ownership, cancelled an unaccepted job, or cleared native pending evidence.");
                Need(saved.RetriesUsed == 1 && f.Cooldowns.Count == 0 && f.Market.Continues == 1, "Changed retry scheduling or silently restarted.");
            });
        await Check("rejected_prior_unsettled_stops_only_original_job", async () =>
        {
            var f = new Fixture("reject_unsettled"); var result = await f.Run();
            Need(result.CleanupConfirmed && f.Cancels.Count == 1 && f.Cancels[0].ExpectedScanJobId == f.First!.RequestId
                && f.Store.LoadLatest()!.ScanRequest?.RequestId == f.First.RequestId, result.Detail);
        });
        foreach (string mismatch in new[] { "source", "job", "plan", "target", "request", "native_flags", "schema" })
            await Check("refusal_with_" + mismatch + "_mismatch_keeps_unknown_new_request", async () =>
            {
                var f = new Fixture("reject_" + mismatch); var result = await f.Run(); var saved = f.Store.LoadLatest()!;
                Need(result.State == "Blocked" && !result.CleanupConfirmed && saved.ScanRequest?.RequestId == f.Second!.RequestId
                    && !saved.ScanSettled && f.Cancels.All(c => c.ExpectedScanJobId == f.Second.RequestId), result.Detail);
            });
        await Check("matching_acceptance_transfers_to_new_job", async () =>
        {
            var f = new Fixture("accept"); var result = await f.Run();
            Need(result.State == "Completed" && result.CleanupConfirmed && result.RetriesUsed == 1 && f.Market.Continues == 1
                && f.Cancels.Count == 0 && f.Market.Current.Scan.JobId == f.Second!.RequestId, result.Detail);
        });
        foreach (string mismatch in new[] { "job", "plan", "target", "source", "request", "schema" })
            await Check("acceptance_" + mismatch + "_mismatch_never_assumes_settled", async () =>
            {
                var f = new Fixture("accept_" + mismatch) { RefuseCleanup = true }; var result = await f.Run(); var saved = f.Store.LoadLatest()!;
                Need(result.State == "Blocked" && !result.CleanupConfirmed && saved.ScanRequest?.RequestId == f.Second!.RequestId
                    && !saved.ScanSettled && f.Cancels.Count == 1 && f.Cancels[0].ExpectedScanJobId == f.Second.RequestId, result.Detail);
            });
        await Check("lost_acceptance_uses_matching_state_without_resending", async () =>
        {
            var f = new Fixture("lost_acceptance"); var result = await f.Run();
            Need(result.State == "Completed" && result.CleanupConfirmed && f.Market.Continues == 1 && f.Cancels.Count == 0, result.Detail);
        });
        await Check("lost_unaccepted_request_remains_unknown_after_old_state", async () =>
        {
            var f = new Fixture("lost_unaccepted"); var result = await f.Run(); var saved = f.Store.LoadLatest()!;
            Need(result.State == "Blocked" && !result.CleanupConfirmed && saved.ScanRequest?.RequestId == f.Second!.RequestId
                && !saved.ScanSettled && f.Cancels.Single().ExpectedScanJobId == f.Second.RequestId, result.Detail);
        });
        await Check("restart_keeps_unconfirmed_pending_id_without_new_scan", async () =>
        {
            var f = new Fixture("lost_unaccepted"); var first = await f.Run();
            Need(!first.CleanupConfirmed, first.Detail);
            var restarted = await f.NewController().RunAsync(f.Route); var saved = f.Store.LoadLatest()!;
            Need(!restarted.CleanupConfirmed && f.Market.Continues == 1 && saved.ScanRequest?.RequestId == f.Second!.RequestId
                && !saved.ScanSettled && f.Cancels.All(c => c.ExpectedScanJobId == f.Second.RequestId), restarted.Detail);
        });
        await Check("new_scan_lost_reply_uses_its_null_plan_not_prior_plan", async () =>
        {
            var f = new Fixture("newplan_lost", autoResume: false, points: 2); var result = await f.Run();
            Need(result.State != "Completed" && result.CleanupConfirmed && f.Market.Scans == 2 && f.Market.Continues == 0
                && f.Cancels.Single().ExpectedScanJobId == f.Second!.RequestId && f.Cancels.Single().ExpectedScanPlanId is null
                && f.Market.Current.SavedScan!.PlanId == "plan-two", result.Detail);
        });
        await Check("cancel_after_acceptance_stops_exact_new_job", async () =>
        {
            var f = new Fixture("cancel_accepted"); var result = await f.Run();
            Need(result.State == "Stopped" && result.CleanupConfirmed && f.Cancels.Single().ExpectedScanJobId == f.Second!.RequestId,
                result.Detail);
        });
        await Check("cancel_after_definitive_refusal_does_not_cancel_unaccepted_id", async () =>
        {
            var f = new Fixture("cancel_rejected"); var result = await f.Run();
            Need(result.State == "Blocked" && result.CleanupConfirmed && f.Cancels.Count == 0
                && f.Store.LoadLatest()!.ScanRequest?.RequestId == f.First!.RequestId, result.Detail);
        });
        await Check("pending_checkpoint_exists_before_command_side_effect", async () =>
        {
            var f = new Fixture("reject"); bool checkedPending = false;
            f.BeforeContinue = request =>
            {
                var saved = f.Store.LoadLatest()!;
                Need(saved.ScanRequest?.RequestId == request.RequestId && !saved.ScanSettled && saved.PlanId == "plan-one",
                    "Submitted but unknown request was not durably fenced before sending.");
                checkedPending = true;
            };
            var result = await f.Run();
            Need(checkedPending && result.CleanupConfirmed && f.Store.LoadLatest()!.ScanRequest?.RequestId == f.First!.RequestId,
                "Definitive refusal did not restore the existing checkpoint shape to the original worker.");
        });
        checks.AddRange(await PatrolReadinessRefusalVerification.RunAsync().ConfigureAwait(false));
        return checks.ToArray();
    }

    private static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }

    private sealed class Fixture
    {
        internal readonly string Mode;
        internal readonly PatrolCheckpointStore Store;
        internal readonly PatrolRouteSnapshot Route;
        internal readonly FakeNavigation Nav = new();
        internal readonly FakeMarket Market;
        internal readonly PatrolController Controller;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly List<ShopCommandRequest> Cancels = [];
        internal readonly List<double> Cooldowns = [];
        private readonly bool autoResume;
        internal ShopCommandRequest? First, Second;
        internal string RefusalState = "SessionBlocked";
        internal bool RefuseCleanup;
        internal Action<ShopCommandRequest>? BeforeContinue;
        internal Fixture(string mode, bool autoResume = true, int points = 1)
        {
            Mode = mode; this.autoResume = autoResume;
            Store = new(Path.Combine(Path.GetTempPath(), "LcInstalledPreflightOffline", Guid.NewGuid().ToString("N")));
            Route = new() { Revision = Guid.NewGuid().ToString("N"), SavedAt = Now, RoundCount = 1, RetryLimit = 10,
                ArrivalRadius = 1, Points = Enumerable.Range(0, points).Select(i => new PatrolWaypoint(Guid.NewGuid().ToString("N"),
                    "saved-log-point-" + i, "4", "offline", 10 + i, 20)).ToArray() };
            Market = new(this);
            Controller = NewController();
        }
        internal PatrolController NewController() => new(Nav, Market, () => Now, (span, token) =>
            { if (span >= TimeSpan.FromSeconds(2)) Cooldowns.Add(span.TotalSeconds); return Task.Delay(1, token); },
                TimeSpan.FromMilliseconds(200), forceDispatch: true, checkpointStore: Store, autoResume: autoResume);
        internal Task<PatrolRunResult> Run() => Controller.RunAsync(Route, Stop.Token);
    }

    private sealed class FakeNavigation : IPatrolNavigation
    {
        private PatrolNavigationState current = new();
        private int x = 10, y = 20;
        public Task<PatrolObservation> CaptureAsync(CancellationToken token, PatrolObservation? expected = null)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new PatrolObservation(Source, 11, 4, x, y, Now, false)); }
        public Task<PatrolNavigationState> StartAsync(PatrolNavigationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); x = request.Waypoint.X; y = request.Waypoint.Y;
            current = new() { JobId = request.JobId, Source = Source, SelfId = 11, MapId = 4,
                WaypointId = request.Waypoint.PointId, TargetX = x, TargetY = y, X = x, Y = y, ArrivalRadius = 1,
                SampledAt = Now, State = "Completed", Completed = true, Terminal = true, WorkSettled = true,
                CleanupConfirmed = true, PositionVerified = true };
            return Task.FromResult(current);
        }
        public Task<PatrolNavigationState> StopAsync(string expectedJobId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Need(current.JobId == expectedJobId, "Wrong navigation stop."); return Task.FromResult(current); }
        public Task<PatrolNavigationState> GetStateAsync(string jobId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Need(current.JobId == jobId, "Wrong navigation state."); return Task.FromResult(current); }
    }

    private sealed class FakeMarket(Fixture f) : IPatrolMarket
    {
        internal int Scans, Continues;
        private int refreshes;
        internal ShopCommandResponse Current = new() { State = "State", WorkSettled = true, CurrentSourceSessionKey = Source.SessionKey };
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.Command == ShopCommands.RefreshTargets)
            {
                var targets = new ShopTargetSnapshot { SnapshotId = "snapshot-" + (++refreshes), SourceSessionKey = Source.SessionKey,
                    SelfId = 11, RefreshedAt = Now, CanDispatch = true };
                Current = Current with { TargetSnapshot = targets };
                return Task.FromResult(Current with { RequestId = request.RequestId, State = "TargetsRefreshed" });
            }
            if (request.Command == ShopCommands.ScanVisible)
            {
                Scans++;
                if (f.Mode == "newplan_lost")
                {
                    if (Scans == 1) f.First = request; else f.Second = request;
                    Current = Accepted(request, true);
                    if (Scans == 2)
                    { Current = Current with { SavedScan = Current.SavedScan! with { PlanId = "plan-two" } }; throw new TimeoutException("offline new plan lost acceptance"); }
                    return Task.FromResult(Current);
                }
                f.First = request; Current = Accepted(request, false);
                return Task.FromResult(Current);
            }
            if (request.Command == ShopCommands.ContinueSavedScan)
            {
                Continues++; f.Second = request; f.BeforeContinue?.Invoke(request);
                if (f.Mode == "lost_unaccepted") throw new IOException("offline lost request acknowledgement");
                if (f.Mode.StartsWith("reject", StringComparison.Ordinal) || f.Mode == "cancel_rejected")
                {
                    if (f.Mode == "reject_unsettled") Current = Current with { WorkSettled = false };
                    var rejected = Current with { RequestId = request.RequestId, State = f.RefusalState,
                        FailureCode = "NativePendingUnresolved", FailureScope = "Session", FailurePhase = "PendingRecovery",
                        Detail = "Saved LOG refusal: original native remains unclassified; no new scan accepted." };
                    rejected = Mismatch(rejected, f.Mode["reject".Length..].TrimStart('_'));
                    if (f.Mode == "cancel_rejected") f.Stop.Cancel();
                    return Task.FromResult(rejected);
                }
                Current = Accepted(request, true);
                if (f.Mode == "lost_acceptance") throw new TimeoutException("offline lost acceptance reply");
                if (f.Mode == "cancel_accepted")
                { Current = Current with { WorkSettled = false, Scan = Current.Scan with { Running = true } }; f.Stop.Cancel(); }
                return Task.FromResult(f.Mode.StartsWith("accept_", StringComparison.Ordinal) ? Mismatch(Current, f.Mode[7..]) : Current);
            }
            if (request.Command == ShopCommands.CancelScan)
            {
                f.Cancels.Add(request);
                if (f.RefuseCleanup || request.ExpectedScanJobId != Current.Scan.JobId
                    || request.ExpectedScanPlanId is not null && request.ExpectedScanPlanId != Current.SavedScan?.PlanId)
                    return Task.FromResult(Current with { RequestId = request.RequestId, State = "Rejected", Detail = "搜尋工作已不同；未停止另一個工作。",
                        Scan = f.RefuseCleanup ? Current.Scan with { JobId = "unconfirmed-cleanup" } : Current.Scan });
                Current = Current with { State = "Stopping", WorkSettled = true, Scan = Current.Scan with { Running = false, StopReason = "Cancelled" } };
                return Task.FromResult(Current with { RequestId = request.RequestId });
            }
            Need(request.Command == ShopCommands.GetState, "Unexpected command " + request.Command);
            if (f.Mode == "newplan_lost" && Scans == 2) throw new IOException("offline state lookup also lost");
            return Task.FromResult(Current with { RequestId = request.RequestId, State = "State" });
        }
        private ShopCommandResponse Accepted(ShopCommandRequest request, bool completed) => new()
        {
            RequestId = request.RequestId, State = "ScanStarted", CurrentSourceSessionKey = Source.SessionKey,
            WorkSettled = true, SessionBlocked = !completed,
            Scan = new() { JobId = request.RequestId, SourceSessionKey = Source.SessionKey, TargetSnapshotId = request.TargetSnapshotId,
                Running = false, StopReason = completed ? null : "SessionBlocked", FailureCode = completed ? null : "NativeCodeUnavailable",
                FailureScope = completed ? null : "Global", FailurePhase = completed ? null : "BeforePublish" },
            SavedScan = new() { PlanId = "plan-one", SourceSessionKey = Source.SessionKey, TotalPages = 98,
                SearchedPages = completed ? 98 : 2, ConfirmedPages = 1, RemainingPages = completed ? 0 : 95,
                UncertainPages = completed ? 0 : 1, AvailablePages = completed ? 0 : 95,
                CanContinue = false, ContinueBlockCode = completed ? "Complete" : "SessionBlocked" },
            TargetSnapshot = Current.TargetSnapshot
        };
        private static ShopCommandResponse Mismatch(ShopCommandResponse value, string field) => field switch
        {
            "source" => value with { CurrentSourceSessionKey = "different-source" },
            "job" => value with { Scan = value.Scan with { JobId = "different-job" } },
            "plan" => value with { SavedScan = value.SavedScan! with { PlanId = "different-plan" } },
            "target" => value with { Scan = value.Scan with { TargetSnapshotId = "different-target" } },
            "request" => value with { RequestId = "different-request" },
            "native_flags" => value with { NativeEntered = true },
            "schema" => value with { Schema = 999 },
            _ => value
        };
    }
}
