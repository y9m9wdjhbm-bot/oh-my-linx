using MemoryHub;

namespace LcPlayerMarket;

internal static partial class PatrolAutoResumeSelfTest
{
    internal static async Task<OfflineCheck[]> RunContinuousReadinessWaitAsync()
    {
        var checks = new List<OfflineCheck>();
        foreach (bool continuous in new[] { false, true })
        {
            try
            {
                var f = new Fixture(points: 2, retries: continuous ? 1 : 0);
                if (continuous) f.Market.PreflightFailures = 1;
                var controller = new PatrolController(f.Nav, f.Market, () => f.Clock,
                    (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                    TimeSpan.FromMilliseconds(500), forceDispatch: true, checkpointStore: f.Store,
                    autoResume: true, continuousRecovery: continuous);
                var result = await controller.RunAsync(f.Route);
                var scans = f.Market.Commands.Where(r => r.Command is ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan).ToArray();
                Need(result.State == "Completed" && result.CompletedPoints == 2 && scans.Length > 0, result.Detail);
                Need(scans.All(r => r.WaitForNativeReadinessCompletion == continuous),
                    "Actual controller did not propagate its continuous policy to batch preparation.");
                Need(f.Market.Commands.Where(r => r.Command is not (ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan))
                    .All(r => !r.WaitForNativeReadinessCompletion), "Wait policy leaked to state, refresh or cancellation commands.");
                if (continuous)
                    Need(f.Market.Scans == 2 && f.Market.Continues == 1 && scans.Select(r => r.RequestId).Distinct().Count() == scans.Length
                        && scans.Single(r => r.Command == ShopCommands.ContinueSavedScan).ExpectedScanPlanId == "plan-1",
                        "Continuous wait changed plan ownership or repeated an existing request.");
                checks.Add(new("actual_patrol_batch_wait_policy_" + (continuous ? "continuous_scan_and_continue" : "bounded_default"), true,
                    "Production controller and immutable checkpoint store with fake ports; no LC/native/input."));
            }
            catch (Exception error) { checks.Add(new("actual_patrol_batch_wait_policy_" + continuous, false, error.ToString())); }
        }
        return checks.ToArray();
    }
}
