namespace LcPlayerMarket;

internal static class LiveAutomaticModePlanSelfTest
{
    internal static OfflineCheck[] Run()
    {
        var checks = new List<OfflineCheck>();
        foreach (var item in new[] {
            ("scan", new LiveAutomaticVerification.ModePlan(true, false, false, 0, 60)),
            ("patrol", new LiveAutomaticVerification.ModePlan(false, false, false, 1, 120)),
            ("patrol-round", new LiveAutomaticVerification.ModePlan(false, true, false, 4, 1800)),
            ("patrol-one-round", new LiveAutomaticVerification.ModePlan(false, true, true, 4, 1800)) })
        {
            var actual = LiveAutomaticVerification.PlanMode(item.Item1);
            checks.Add(new("live_mode_plan_" + item.Item1, actual == item.Item2,
                "Pure plan only. No backend, route read/write, LC, or native calls."));
        }
        var speed = LiveAutomaticVerification.PlanMode("scan-speed");
        checks.Add(new("live_mode_bounded_speed_uses_no_navigation", speed.IsScan && !speed.IsFullRound && speed.StopAfterPoints == 0 && speed.LimitSeconds == 180 && speed.SpeedSampleCompletedPageThreshold == 24, "Pure bounded speed mode; saved patrol unchanged."));
        checks.AddRange(LiveSpeedSamplePolicy.RunSelfTest());
        bool rejected = false;
        try { LiveAutomaticVerification.PlanMode("unknown"); } catch (ArgumentException) { rejected = true; }
        checks.Add(new("live_mode_plan_unknown_refused", rejected, "Unknown mode must not become a live plan."));
        return checks.ToArray();
    }
}
