using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static class ContinuousBatchReadinessVerification
{
    private static readonly SourceIdentity Source = new(900001, 123456, @"D:\offline\LC.exe", "0x140000000",
        1793101316, 66609152, "B47CD077B4250FAC772F264A748DB75DC8C0075DDCC2ED475CC5F247CC9945FC", "offline-batch-readiness");

    internal static int RunReport(string output)
    {
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        if (!admin) throw new InvalidOperationException("ADMIN_REQUIRED");
        var checks = RunAsync().GetAwaiter().GetResult();
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, new { Version = 92, Success = checks.All(c => c.Passed), IsAdministrator = admin,
            Passed = checks.Count(c => c.Passed), Failed = checks.Count(c => !c.Passed), Checks = checks,
            LcReads = 0, NativeCalls = 0, GameInput = 0, ProductStarted = false }, HubProtocol.Json);
        file.Flush(true);
        return checks.All(c => c.Passed) ? 0 : 1;
    }

    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>(await PatrolAutoResumeSelfTest.RunContinuousReadinessWaitAsync());
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); checks.Add(new(name, true, "Owned fake helper and source monitor only; no LC/process launch/native/input.")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        await Check("batch_readonly_ninety_second_probe_survives_temporary_unknown_without_overlap", async () =>
        {
            var flight = new ShopNativeHelperFlight(); var process = new FakeProcess(); int starts = 0, missing = 0; long elapsed = 0;
            ShopSourceObservation Observe()
            {
                bool gap = elapsed >= 10000 && elapsed < 80000; if (gap) missing++;
                return new(true, true, gap ? null : Source);
            }
            async Task Delay(TimeSpan interval, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (elapsed < 90000) elapsed += (long)interval.TotalMilliseconds;
                if (elapsed >= 90000)
                {
                    if (!process.HasExited) process.Exit(0);
                    // A signalled fake exit is not the observer's completed handoff.
                    // Join that exact observer before another virtual clock pulse.
                    await flight.WaitUntilExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
                await Task.Yield();
            }
            int exit = await ShopNativeReadinessRunner.RunAsync(flight, () => { starts++; return process; },
                ShopNativeReadinessWaitPolicy.HelperBudget(true), CancellationToken.None, Observe, Source, Delay).WaitAsync(TimeSpan.FromSeconds(2));
            Need(exit == 0 && elapsed >= 90000 && missing > 0 && starts == 1 && process.WaitCalls == 1
                && process.Disposals == 1 && !flight.IsActive, "A missing background sample timed out/replaced the original probe.");
        });
        foreach (string change in new[] { "source", "selection", "lifetime" })
        await Check("batch_readonly_" + change + "_change_ends_wait_but_retains_observer", async () =>
        {
            var flight = new ShopNativeHelperFlight(); var process = new FakeProcess(); int starts = 0, pulses = 0;
            ShopSourceObservation Observe() => new(change != "lifetime" || pulses < 3, change != "selection" || pulses < 3,
                change == "source" && pulses >= 3 ? Source with { SessionKey = "other-source" } : Source);
            Task Delay(TimeSpan _, CancellationToken token) { token.ThrowIfCancellationRequested(); pulses++; return Task.CompletedTask; }
            try
            {
                await ShopNativeReadinessRunner.RunAsync(flight, () => { starts++; return process; }, Timeout.InfiniteTimeSpan,
                    CancellationToken.None, Observe, Source, Delay);
                throw new InvalidOperationException("Expected a source/lifetime/configuration refusal.");
            }
            catch (ShopRequestFailure error)
            {
                Need(error.Failure.Code == (change == "source" ? "SourceMismatch" : "ContextUnavailable"), "Wrong source-change classification.");
            }
            Need(starts == 1 && flight.IsActive && process.WaitCalls == 1 && process.Disposals == 0,
                "Ending the caller wait retired or replaced an unexited helper.");
            try { flight.RequireAvailable(); throw new InvalidOperationException("Expected busy."); } catch (ShopNativeHelperBusyException) { }
            var join = flight.WaitUntilExitAsync(); process.Exit(9); await join.WaitAsync(TimeSpan.FromSeconds(2));
            Need(starts == 1 && !flight.IsActive && process.Disposals == 1, "Real exit did not retire the exact owned handle once.");
        });
        await Check("batch_readonly_stop_after_long_wait_retains_original_helper", async () =>
        {
            var flight = new ShopNativeHelperFlight(); var process = new FakeProcess(); using var stop = new CancellationTokenSource();
            int starts = 0; long elapsed = 0;
            Task Delay(TimeSpan interval, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); elapsed += (long)interval.TotalMilliseconds;
                if (elapsed >= 60500) stop.Cancel(); return Task.CompletedTask;
            }
            try
            {
                await ShopNativeReadinessRunner.RunAsync(flight, () => { starts++; return process; }, Timeout.InfiniteTimeSpan,
                    stop.Token, () => new(true, true, Source), Source, Delay);
                throw new InvalidOperationException("Expected stop.");
            }
            catch (OperationCanceledException) { }
            Need(elapsed >= 60500 && starts == 1 && flight.IsActive && process.Disposals == 0, "Stop changed actual helper ownership.");
            var join = flight.WaitUntilExitAsync(); process.Exit(0); await join.WaitAsync(TimeSpan.FromSeconds(2));
            Need(starts == 1 && !flight.IsActive && process.Disposals == 1, "Stop restarted the original request after exit.");
        });
        await Check("batch_readonly_real_failed_exit_is_not_success", async () =>
        {
            var flight = new ShopNativeHelperFlight(); var process = new FakeProcess(); int starts = 0;
            async Task Delay(TimeSpan _, CancellationToken token) { token.ThrowIfCancellationRequested(); process.Exit(17); await Task.Yield(); }
            int exit = await ShopNativeReadinessRunner.RunAsync(flight, () => { starts++; return process; }, Timeout.InfiniteTimeSpan,
                CancellationToken.None, () => new(true, true, Source), Source, Delay);
            Need(exit == 17 && starts == 1 && !flight.IsActive, "A failed actual exit was rewritten as success.");
        });
        await Check("batch_readonly_initial_changed_source_launches_nothing", async () =>
        {
            var flight = new ShopNativeHelperFlight(); int starts = 0;
            try
            {
                await ShopNativeReadinessRunner.RunAsync(flight, () => { starts++; return new FakeProcess(); }, Timeout.InfiniteTimeSpan,
                    CancellationToken.None, () => new(true, true, Source with { ProcessId = 2 }), Source);
                throw new InvalidOperationException("Expected source mismatch.");
            }
            catch (ShopRequestFailure error) { Need(error.Failure.Code == "SourceMismatch", "Wrong initial refusal."); }
            Need(starts == 0 && !flight.IsActive, "A known different source launched a helper.");
        });
        await Check("batch_readonly_post_exit_unknown_uses_bounded_same_source_recovery", async () =>
        {
            int observations = 0;
            await ShopSourceAvailability.WaitAsync(() => new(true, true, ++observations < 2 ? null : Source), Source, CancellationToken.None);
            Need(observations == 2 && !ShopSourceAvailability.Changed(null, Source), "A short missing sample was labelled a new LC.");
            try
            {
                await ShopSourceAvailability.WaitAsync(() => new(true, true, null), Source, CancellationToken.None, maximumMilliseconds: 0);
                throw new InvalidOperationException("Expected bounded source unavailable.");
            }
            catch (ShopRequestFailure error) { Need(error.Failure.Code == "SourceUnavailable", "Unknown was labelled a changed source."); }
        });
        await Check("batch_wait_flag_is_allowed_only_on_readiness_and_scan_preparation", () =>
        {
            var validate = typeof(ShopBroker).GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (string command in new[] { ShopCommands.CheckNativeReadiness, ShopCommands.PrepareNativeReadiness,
                ShopCommands.ScanVisible, ShopCommands.ContinueSavedScan })
                validate.Invoke(null, [new ShopCommandRequest { Command = command, WaitForNativeReadinessCompletion = true,
                    ExpectedScanPlanId = Guid.NewGuid().ToString("N") }]);
            foreach (string command in new[] { ShopCommands.OpenShop, ShopCommands.RefreshTargets, ShopCommands.GetState, ShopCommands.CancelScan })
            {
                try
                {
                    validate.Invoke(null, [new ShopCommandRequest { Command = command, WaitForNativeReadinessCompletion = true,
                        OwnerId = 24, ViewerDirection = ShopPageModes.ViewerBuy }]);
                    throw new InvalidOperationException("Wait flag granted an unrelated command scope.");
                }
                catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { }
            }
            Need(!new ShopCommandRequest { Command = ShopCommands.ScanVisible }.WaitForNativeReadinessCompletion
                && ShopNativeReadinessWaitPolicy.HelperBudget(false) == TimeSpan.FromSeconds(30), "Ordinary manual scan became unbounded.");
            return Task.CompletedTask;
        });
        return checks.ToArray();
    }

    private static void Need(bool pass, string detail) { if (!pass) throw new InvalidOperationException(detail); }
    private sealed class FakeProcess : IShopNativeHelperProcess
    {
        private readonly TaskCompletionSource exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int code;
        public bool HasExited { get; private set; }
        public int ExitCode => HasExited ? code : throw new InvalidOperationException("Still running.");
        internal int WaitCalls, Disposals;
        public Task WaitForExitAsync() { WaitCalls++; return exit.Task; }
        public void Dispose() { Disposals++; }
        internal void Exit(int value) { code = value; HasExited = true; exit.TrySetResult(); }
    }
}
