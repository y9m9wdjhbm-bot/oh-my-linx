using MemoryHub;

namespace LcPlayerMarket;

internal static partial class PatrolAutoResumeSelfTest
{
    internal static async Task<OfflineCheck[]> RunFrozenPointTargetsAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> body)
        {
            try { await body(); checks.Add(new("frozen_point_" + name, true, "Production controller with fixed-list fake broker/navigation; no LC/native/input.")); }
            catch (Exception error) { checks.Add(new("frozen_point_" + name, false, error.ToString())); }
        }
        PatrolController Controller(Fixture f, IPatrolMarket market) => new(f.Nav, market, () => f.Clock,
            (interval, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (interval >= TimeSpan.FromSeconds(2)) { f.Cooldowns.Add(interval.TotalSeconds); f.OnCooldown?.Invoke(interval.TotalSeconds); }
                token.ThrowIfCancellationRequested(); return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(250), forceDispatch: true, checkpointStore: f.Store,
            autoResume: true, continuousRecovery: true, freezeShoppingTargets: true);
        await Check("four_points_four_refreshes_despite_multiple_owners_and_continues", async () =>
        {
            var f = new Fixture(points: 4, retries: 2); f.Market.RemainingOnScan = _ => 1;
            var market = new FrozenPointMarket(f);
            var result = await Controller(f, market).RunAsync(f.Route);
            Need(result.State == "Completed" && result.CompletedPoints == 4 && result.CompletedRounds == 1
                && result.CleanupConfirmed && f.Market.Refreshes == 4 && f.Market.Scans == 4 && f.Market.Continues == 4,
                "Each point must have exactly one initial list, then its original remaining pages: " + result.Detail);
            Need(market.Targets.Count == 4 && market.Targets.All(t => t.Owners.Length == 2)
                && f.Market.Commands.Where(r => r.Command is ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan)
                    .GroupBy(r => r.TargetSnapshotId).All(g => g.Count() == 2)
                && f.Market.Commands.Count(r => r.Command == ShopCommands.RefreshTargets) == 4,
                "A continuation acquired another list or lost the frozen owner IDs.");
        });
        await Check("same_point_preflight_recovery_keeps_one_list_and_plan", async () =>
        {
            var f = new Fixture(points: 1, retries: 3); f.Market.PreflightFailures = 3;
            var market = new FrozenPointMarket(f);
            var result = await Controller(f, market).RunAsync(f.Route);
            Need(result.State == "Completed" && f.Market.Refreshes == 1 && f.Market.Scans == 1
                && f.Market.Continues == 3 && f.Cooldowns.Count == 3 && result.CleanupConfirmed, result.Detail);
            Need(f.Market.Commands.Where(r => r.Command == ShopCommands.ContinueSavedScan)
                .All(r => r.TargetSnapshotId == "snapshot-1" && r.ExpectedScanPlanId == "plan-1"),
                "Recovery replaced the fixed snapshot or accepted plan.");
        });
        await Check("historical_list_age_is_preserved_while_actual_pose_is_recaptured", async () =>
        {
            var f = new Fixture(points: 1, retries: 1); f.Market.RemainingOnScan = _ => 1;
            f.OnCooldown = _ => f.Clock = f.Clock.AddSeconds(30);
            var market = new FrozenPointMarket(f);
            var result = await Controller(f, market).RunAsync(f.Route);
            Need(result.State == "Completed" && f.Market.Refreshes == 1 && f.Market.Scans == 1 && f.Market.Continues == 1
                && market.Targets.Single().RefreshedAt == Now && f.Clock - market.Targets[0].RefreshedAt > TimeSpan.FromSeconds(2),
                "Historical list was retimed or unnecessarily refreshed: " + result.Detail);
        });
        await Check("changed_fixed_snapshot_stops_without_refresh_or_replacement_scan", async () =>
        {
            var f = new Fixture(points: 1, retries: 2); f.Market.PreflightFailures = 1;
            var market = new FrozenPointMarket(f) { ReplaceSnapshotOnState = true };
            var result = await Controller(f, market).RunAsync(f.Route);
            Need(result.State == "Blocked" && result.CleanupConfirmed && f.Market.Refreshes == 1
                && f.Market.Scans == 1 && f.Market.Continues == 0,
                "Changed broker snapshot caused a new refresh/query: " + result.Detail);
        });
        await Check("persistent_failure_exhausts_budget_without_refreshing_list", async () =>
        {
            var f = new Fixture(points: 1, retries: 2); f.Market.PreflightFailures = int.MaxValue;
            var market = new FrozenPointMarket(f);
            var result = await Controller(f, market).RunAsync(f.Route);
            Need(result.State == "Failed" && result.FailureCode == "RecoveryBudgetExhausted" && result.CleanupConfirmed
                && f.Market.Refreshes == 1 && f.Market.Scans == 1 && f.Market.Continues == 2 && f.Cooldowns.Count == 2,
                "Frozen list recovery became unbounded or repeated initial collection: " + result.Detail);
        });
        await Check("known_entered_batch_is_never_replaced_by_new_scanvisible", async () =>
        {
            var f = new Fixture(points: 1, retries: 1); f.Market.RemainingOnScan = _ => 1;
            var market = new FrozenPointMarket(f) { FirstBatchHasSettledEnteredPage = true };
            var result = await Controller(f, market).RunAsync(f.Route);
            var scans = f.Market.Commands.Where(c => c.Command == ShopCommands.ScanVisible).ToArray();
            var continues = f.Market.Commands.Where(c => c.Command == ShopCommands.ContinueSavedScan).ToArray();
            Need(result.State == "Completed" && result.CleanupConfirmed && scans.Length == 1 && continues.Length == 1
                && continues[0].ExpectedScanPlanId == "plan-1" && continues[0].TargetSnapshotId == scans[0].TargetSnapshotId
                && f.Market.Refreshes == 1, "A sent page caused replacement/replay of the initial batch: " + result.Detail);
        });
        await Check("four_points_empty_and_multiple_owner_lists_refresh_once_each", async () =>
        {
            var f = new Fixture(points: 4, retries: 2); f.Market.RemainingOnScan = _ => 1;
            var market = new FrozenPointMarket(f); market.EmptyRefreshes.UnionWith(new[] { 1, 3 });
            var result = await Controller(f, market).RunAsync(f.Route);
            Need(result.State == "Completed" && result.CompletedPoints == 4 && result.CleanupConfirmed
                && f.Market.Refreshes == 4 && f.Market.Scans == 2 && f.Market.Continues == 2,
                "Empty points did not complete without a scan/readiness retry: " + result.Detail);
            Need(market.Targets.Count(t => t.Owners.Length == 0) == 2
                && market.Targets.Count(t => t.Owners.Count(ShopEligibilityPolicy.CanScan) == 2) == 2
                && market.CommandsByPoint.Where(x => x.Point is 1 or 3)
                    .All(x => x.Command is not (ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan
                        or ShopCommands.PrepareNativeReadiness or ShopCommands.CheckNativeReadiness)),
                "An empty point sent a page or native readiness command.");
        });
        await Check("empty_list_does_not_cancel_or_abandon_unsettled_prior_worker", async () =>
        {
            var f = new Fixture(points: 1, retries: 2);
            var market = new FrozenPointMarket(f) { UnsettledOnEmpty = true }; market.EmptyRefreshes.Add(1);
            var result = await Controller(f, market).RunAsync(f.Route);
            Need(result.State == "Blocked" && result.CompletedPoints == 0 && f.Market.Refreshes == 1
                && f.Market.Scans == 0 && f.Market.Continues == 0
                && !f.Market.Commands.Any(c => c.Command == ShopCommands.CancelScan),
                "Empty-list completion bypassed or cancelled unowned prior work: " + result.Detail);
        });
        return checks.ToArray();
    }

    // Models the real broker's retained manual target list and ordinary State
    // receipt. No TargetsRefreshed receipt is synthesized on state reads.
    private sealed class FrozenPointMarket(Fixture fixture) : IPatrolMarket
    {
        internal readonly List<ShopTargetSnapshot> Targets = [];
        internal bool ReplaceSnapshotOnState, FirstBatchHasSettledEnteredPage, UnsettledOnEmpty;
        internal readonly HashSet<int> EmptyRefreshes = [];
        internal readonly List<(int Point, string Command)> CommandsByPoint = [];
        private ShopTargetSnapshot? retained;
        public async Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            CommandsByPoint.Add((fixture.Nav.Starts.Count, request.Command));
            var response = await fixture.Market.SendAsync(request, token);
            if (request.Command == ShopCommands.RefreshTargets && response.State == "TargetsRefreshed")
            {
                retained = response.TargetSnapshot! with
                {
                    SelfPosition = new(fixture.Nav.Position.X, fixture.Nav.Position.Y),
                    Owners = (EmptyRefreshes.Contains(fixture.Market.Refreshes) ? Array.Empty<ulong>() : new[] { 21UL, 22UL }).Select(id => new MarketOwner
                    {
                        Id = id, LocalIdentity = "fixed-owner-" + id, Position = new(fixture.Nav.Position.X, fixture.Nav.Position.Y),
                        ObservedAt = fixture.Clock, ShopState = ShopEligibilityPolicy.ShopActionObserved,
                        ShopStateSampledAt = fixture.Clock, ActionType = ".?AVShopAction@lineage@@",
                        ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence
                    }).ToArray()
                };
                Targets.Add(retained);
                bool empty = retained.Owners.Length == 0;
                return response with { TargetSnapshot = retained,
                    WorkSettled = empty && UnsettledOnEmpty ? false : response.WorkSettled || response.Scan.JobId is null && !response.Scan.Running,
                    Scan = empty && UnsettledOnEmpty ? response.Scan with { Running = true } : response.Scan };
            }
            if (request.Command == ShopCommands.GetState)
                response = response with { State = "State", CurrentSourceSessionKey = fixture.Nav.CurrentSource.SessionKey,
                    TargetSnapshot = retained is not null && ReplaceSnapshotOnState ? retained with { SnapshotId = "foreign-list" } : retained };
            else if (request.Command is ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan)
                response = response with { TargetSnapshot = retained };
            if (FirstBatchHasSettledEnteredPage && response.SavedScan is { } saved)
            {
                bool remaining = saved.RemainingPages > 0;
                response = response with { NativeEntered = true, NativeReturned = true, ResponseVerified = true,
                    SavedScan = saved with { TotalPages = 2, SearchedPages = remaining ? 1 : 2,
                        ConfirmedPages = remaining ? 1 : 2, RemainingPages = remaining ? 1 : 0,
                        AvailablePages = remaining ? 1 : 0, CanContinue = remaining } };
            }
            return response;
        }
    }
}
