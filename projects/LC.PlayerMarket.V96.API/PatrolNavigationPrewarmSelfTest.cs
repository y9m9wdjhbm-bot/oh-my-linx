using MemoryHub;

namespace LcPlayerMarket;

// Pure orchestration oracle: the continuation counter stands in for the next
// readiness stage. No backend, source, helper, process, native call or input.
internal static class PatrolNavigationPrewarmSelfTest
{
    internal static async Task<IReadOnlyList<OfflineCheck>> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> body)
        {
            try { await body(); checks.Add(new("prewarm_" + name, true, "Production prewarm with fake pose/context/continuation only; zero LC/native/input.")); }
            catch (Exception error) { checks.Add(new("prewarm_" + name, false, error.ToString())); }
        }
        static void Need(bool condition, string detail)
        { if (!condition) throw new InvalidOperationException(detail); }

        await Check("fresh_same_binding_reaches_readiness_once_without_retiming", async () =>
        {
            var f = new Fixture(); var original = f.Pose;
            var result = await f.Run();
            Need(ReferenceEquals(result, original) && result.SampledAt == original.SampledAt
                && f.Captures == 1 && f.ReadinessContinuations == 1,
                "Fresh pose was copied/retimed or the next stage ran more than once.");
        });
        foreach (string mutation in new[] { "source", "self", "stale", "future", "missing_time", "map", "coordinate" })
        await Check(mutation + "_pose_prevents_readiness", async () =>
        {
            var f = new Fixture();
            f.Pose = mutation switch
            {
                "source" => f.Pose with { Source = f.Source with { CreationFileTime = 123457 } },
                "self" => f.Pose with { SelfId = f.Pose.SelfId + 1 },
                "stale" => f.Pose with { SampledAt = f.Now.AddMilliseconds(-HubProtocol.FreshMilliseconds - 1) },
                "future" => f.Pose with { SampledAt = f.Now.AddTicks(1) },
                "missing_time" => f.Pose with { SampledAt = default },
                "map" => f.Pose with { MapId = -1 },
                _ => f.Pose with { X = 65536 }
            };
            await f.RequireRefusal();
            Need(f.Captures == 1 && f.ReadinessContinuations == 0,
                "Invalid pose reached the next readiness stage.");
        });
        await Check("unknown_context_before_capture_prevents_capture_and_readiness", async () =>
        {
            var f = new Fixture { Current = null };
            await f.RequireRefusal();
            Need(f.Captures == 0 && f.ReadinessContinuations == 0, "Unknown identity allowed preparation.");
        });
        await Check("changed_context_during_capture_prevents_readiness", async () =>
        {
            var f = new Fixture();
            f.AfterCapture = () => f.Current = f.Expected with { SelfId = 54321 };
            await f.RequireRefusal();
            Need(f.Captures == 1 && f.ReadinessContinuations == 0, "Capture completion hid a changed role.");
        });
        await Check("cancellation_during_capture_prevents_readiness", async () =>
        {
            var f = new Fixture(); using var cancel = new CancellationTokenSource();
            f.AfterCapture = cancel.Cancel;
            await f.RequireRefusal(cancel.Token);
            Need(f.Captures == 1 && f.ReadinessContinuations == 0, "Cancelled capture continued to readiness.");
        });
        await Check("cancelled_token_cannot_reach_readiness", async () =>
        {
            var f = new Fixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await f.RequireRefusal(cancel.Token);
            Need(f.Captures == 0 && f.ReadinessContinuations == 0, "Pre-cancelled work continued to readiness.");
        });
        return checks;
    }

    private sealed class Fixture
    {
        internal readonly DateTimeOffset Now = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        internal readonly SourceIdentity Source = new(900001, 123456, @"C:\offline\LC.exe", "0x140000000", 1793101316, 66609152,
            "B47CD077B4250FAC772F264A748DB75DC8C0075DDCC2ED475CC5F247CC9945FC", "offline-prewarm");
        internal readonly PatrolStartupReadinessContext Expected;
        internal PatrolStartupReadinessContext? Current;
        internal PatrolObservation Pose;
        internal Action? AfterCapture;
        internal int Captures, ReadinessContinuations;
        internal Fixture()
        {
            Expected = new(Source, 12345); Current = Expected;
            Pose = new(Source, 12345, 4, 32610, 32778, Now.AddMilliseconds(-100), true);
        }
        internal async Task<PatrolObservation> Run(CancellationToken token = default)
        {
            var result = await PatrolNavigationPrewarm.RunAsync(Expected, captureToken =>
            {
                captureToken.ThrowIfCancellationRequested(); Captures++; AfterCapture?.Invoke();
                return Task.FromResult(Pose);
            }, () => Current, () => Now, token);
            ReadinessContinuations++;
            return result;
        }
        internal async Task RequireRefusal(CancellationToken token = default)
        {
            bool refused = false;
            try { await Run(token); }
            catch (InvalidOperationException) { refused = true; }
            catch (OperationCanceledException) { refused = true; }
            if (!refused) throw new InvalidOperationException("Invalid prewarm was admitted.");
        }
    }
}
