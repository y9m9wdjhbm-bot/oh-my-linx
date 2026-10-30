using MemoryHub;

namespace LcPlayerMarket;

internal static partial class PatrolAutoResumeSelfTest
{
    internal static async Task<OfflineCheck[]> RunBoundedRecoveryAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> body)
        {
            try { await body(); checks.Add(new("bounded_patrol_" + name, true, "Production coordinator and immutable checkpoints, fake ports only; no LC/native/input.")); }
            catch (Exception error) { checks.Add(new("bounded_patrol_" + name, false, error.ToString())); }
        }
        PatrolController Controller(Fixture f, IPatrolMarket market) => new(f.Nav, market, () => f.Clock,
            (interval, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (interval >= TimeSpan.FromSeconds(2))
                { f.Cooldowns.Add(interval.TotalSeconds); f.OnCooldown?.Invoke(interval.TotalSeconds); }
                token.ThrowIfCancellationRequested(); return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(250), forceDispatch: true, checkpointStore: f.Store,
            autoResume: true, continuousRecovery: true);
        foreach (int limit in new[] { 0, 1, 3, 10 })
        await Check("persistent_readiness_stops_at_exact_route_limit_" + limit, async () =>
        {
            var f = new Fixture(points: 1, retries: limit);
            var market = new RefusingMarket(f.Market, int.MaxValue);
            var result = await Controller(f, market).RunAsync(f.Route);
            Need(result.State == "Failed" && result.CleanupConfirmed && result.CompletedPoints == 0
                && market.Refusals == limit + 1 && f.Cooldowns.Count == limit && f.Market.Scans == 0,
                result.Detail);
            Need(result.Detail.Contains("RecoveryBudgetExhausted") && result.Detail.Contains("NativeCodeUnavailable")
                && result.RetriesUsed == limit && f.Store.LoadLatest()!.RecoveryCycles == limit,
                "Lost exact limit, original cause, accounting or durable resume budget.");
            if (limit == 10) Need(f.Cooldowns.Sum() == 210 && f.Cooldowns.All(s => s <= 30),
                "Ten attempts exceeded the bounded 210 second cooldown schedule.");
        });
        await Check("same_plan_exhaustion_does_not_rebuild_original_scan", async () =>
        {
            var f = new Fixture(points: 1, retries: 3); f.Market.PreflightFailures = int.MaxValue;
            var result = await Controller(f, f.Market).RunAsync(f.Route);
            var saved = f.Store.LoadLatest()!;
            Need(result.State == "Failed" && result.CleanupConfirmed && f.Market.Scans == 1
                && f.Market.Continues == 3 && f.Cooldowns.Count == 3 && saved.PlanId == "plan-1"
                && saved.ScanSettled && saved.RecoveryCycles == 3, result.Detail);
            Need(f.Market.Commands.Where(c => c.Command == ShopCommands.ContinueSavedScan)
                .All(c => c.ExpectedScanPlanId == "plan-1"), "Budget exhaustion replaced the original owned plan.");
        });
        await Check("capture_and_readiness_share_one_point_budget", async () =>
        {
            var f = new Fixture(points: 1, retries: 2); f.Nav.FailCaptures = 1;
            var market = new RefusingMarket(f.Market, int.MaxValue);
            var result = await Controller(f, market).RunAsync(f.Route);
            Need(result.State == "Failed" && result.CleanupConfirmed && f.Cooldowns.Count == 2
                && market.Refusals == 2 && f.Nav.Starts.Count == 1 && f.Market.Scans == 0,
                "Changing error category replenished recovery budget: " + result.Detail);
        });
        await Check("completed_points_each_receive_distinct_budget", async () =>
        {
            var f = new Fixture(points: 4, retries: 1); var market = new RefuseOncePerNavigation(f);
            var result = await Controller(f, market).RunAsync(f.Route);
            Need(result.State == "Completed" && result.CompletedPoints == 4 && result.CompletedRounds == 1
                && f.Market.Scans == 4 && f.Nav.Starts.Count == 4 && f.Cooldowns.Count == 4
                && result.RetriesUsed == 4 && f.Store.LoadLatest()!.RecoveryCycles == 0,
                "Completed point did not reset only its own budget: " + result.Detail);
        });
        await Check("next_round_resets_point_budget", async () =>
        {
            var f = new Fixture(points: 1, rounds: 2, retries: 1); var market = new RefuseOncePerNavigation(f);
            var result = await Controller(f, market).RunAsync(f.Route);
            Need(result.State == "Completed" && result.CompletedRounds == 2 && f.Market.Scans == 2
                && f.Nav.Starts.Count == 2 && f.Cooldowns.SequenceEqual(new[] { 2d, 2d }), result.Detail);
        });
        await Check("cancel_resume_preserves_consumed_budget", async () =>
        {
            using var stop = new CancellationTokenSource(); var f = new Fixture(points: 1, retries: 3);
            var market = new RefusingMarket(f.Market, int.MaxValue);
            f.OnCooldown = _ => { if (f.Cooldowns.Count == 2) stop.Cancel(); };
            var first = await Controller(f, market).RunAsync(f.Route, stop.Token);
            Need(first.State == "Stopped" && first.CleanupConfirmed && f.Store.LoadLatest()!.RecoveryCycles == 2,
                "Cancellation discarded consumed point budget.");
            f.OnCooldown = null;
            var second = await Controller(f, market).RunAsync(f.Route);
            Need(second.State == "Failed" && second.CleanupConfirmed && market.Refusals == 4
                && f.Cooldowns.Count == 3 && f.Store.LoadLatest()!.RecoveryCycles == 3
                && second.Detail.Contains("RecoveryBudgetExhausted"),
                "Resume replenished budget or retried beyond saved count: " + second.Detail);
        });
        return checks.ToArray();
    }

    private sealed class RefuseOncePerNavigation(Fixture fixture) : IPatrolMarket
    {
        private readonly HashSet<int> refusedNavigation = [];
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            if (request.Command == ShopCommands.ScanVisible && refusedNavigation.Add(fixture.Nav.Starts.Count))
                return new RefusingMarket(fixture.Market, 1).SendAsync(request, token);
            return fixture.Market.SendAsync(request, token);
        }
    }
}
