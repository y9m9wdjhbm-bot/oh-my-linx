using MemoryHub.Service;

namespace LcPlayerMarket;

internal static class ShopStartupTraceSelfTest
{
    internal static OfflineCheck[] Run()
    {
        var checks = new List<OfflineCheck>();
        void Check(string name, Action work)
        {
            try { work(); checks.Add(new(name, true, "通過")); }
            catch (Exception failure) { checks.Add(new(name, false, failure.ToString())); }
        }
        static void Need(bool value) { if (!value) throw new InvalidOperationException("Startup trace changed operation semantics."); }
        Check("startup_trace_keeps_nested_result_and_order", () =>
        {
            var events = new List<ShopStartupStageEvent>();
            int executions = 0;
            int result = ShopStartupTrace.Run("outer", events.Add,
                () => ShopStartupTrace.Run("inner", events.Add, () => { executions++; return 7; }, value => value));
            Need(result == 7 && executions == 1 && events.Select(value => value.Stage + ":" + value.State)
                .SequenceEqual(["outer:Started", "inner:Started", "inner:Completed", "outer:Completed"]));
            Need(events[2].Items == 7 && events.All(value => double.IsFinite(value.ElapsedMs) && value.ElapsedMs >= 0));
        });
        Check("startup_trace_preserves_original_replay_exception", () =>
        {
            var events = new List<ShopStartupStageEvent>();
            var original = new InvalidDataException("incomplete committed tail");
            Exception? observed = null;
            try { ShopStartupTrace.Run("tail", events.Add, (Action)(() => throw original)); }
            catch (Exception failure) { observed = failure; }
            Need(ReferenceEquals(original, observed) && events.Count == 2 && events[^1].State == "Failed"
                && events[^1].Error!.Contains(original.Message, StringComparison.Ordinal));
        });
        Check("startup_trace_preserves_original_cancellation", () =>
        {
            var events = new List<ShopStartupStageEvent>();
            var original = new OperationCanceledException("startup canceled");
            Exception? observed = null;
            try { ShopStartupTrace.Run("tail", events.Add, (Action)(() => throw original)); }
            catch (Exception failure) { observed = failure; }
            Need(ReferenceEquals(original, observed) && events[^1].State == "Failed");
        });
        Check("startup_trace_diagnostic_failure_cannot_skip_replay", () =>
        {
            int executions = 0;
            int result = ShopStartupTrace.Run("replay", _ => throw new IOException("diagnostic sink"), () => { executions++; return 9; });
            Need(result == 9 && executions == 1);
        });
        Check("startup_trace_disabled_still_executes_once", () =>
        {
            int executions = 0;
            ShopStartupTrace.Run("replay", null, () => { executions++; });
            Need(executions == 1);
        });
        return checks.ToArray();
    }
}
