using MemoryHub.Service;
namespace LcPlayerMarket;
internal static class ShopPageWaitConfigurationSelfTest
{
    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        var native = new ShopNativeOutcome(true, true, true, "offline complete return/cleanup", null);
        async Task Check(string name, Func<Task> body)
        { try { await body().WaitAsync(TimeSpan.FromSeconds(5)); checks.Add(new(name, true, "Fake delay/config only; no LC/native/input.")); } catch (Exception e) { checks.Add(new(name, false, e.ToString())); } }
        foreach (int ms in new[] { 0, 100, 150, 1000, 3000, 5000 })
            await Check("page_wait_config_exact_milliseconds_" + ms, async () =>
            {
                var observed = new List<TimeSpan>();
                bool opened = await ShopPagePacing.WaitBeforeCaptureMillisecondsAsync(native, default,
                    (amount, token) => { token.ThrowIfCancellationRequested(); observed.Add(amount); return Task.CompletedTask; }, ms);
                Need(opened && observed.SequenceEqual(ms == 0 ? Array.Empty<TimeSpan>() : [TimeSpan.FromMilliseconds(ms)]), "Exact millisecond duration was lost.");
            });
        foreach (int ms in new[] { int.MinValue, -1, 1, 99, 5001, 10000, int.MaxValue })
            await Check("page_wait_config_rejects_milliseconds_" + ms, async () =>
            {
                bool rejected = false; int waits = 0;
                try { await ShopPagePacing.WaitBeforeCaptureMillisecondsAsync(native, default, (_, _) => { waits++; return Task.CompletedTask; }, ms); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Need(rejected && waits == 0, "An out-of-range interval reached the delay.");
            });
        foreach (int seconds in new[] { 0, 6, 10 })
            await Check("page_wait_config_legacy_seconds_cannot_bypass_limit_" + seconds, async () =>
            {
                bool rejected = false;
                try { await ShopPagePacing.WaitBeforeCaptureAsync(native, default, (_, _) => Task.CompletedTask, seconds); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Need(rejected, "Legacy API expanded the product's 5000ms maximum.");
            });
        await Check("page_wait_config_default_is_no_additional_wait", async () =>
        {
            TimeSpan actual = default;
            await ShopPagePacing.WaitBeforeCaptureMillisecondsAsync(native, default, (amount, _) => { actual = amount; return Task.CompletedTask; });
            Need(actual == TimeSpan.Zero, "Default changed during unit migration.");
        });
        await Check("page_wait_config_cancel_retains_native_return", async () =>
        {
            using var stop = new CancellationTokenSource(); bool cancelled = false; int waits = 0;
            try { await ShopPagePacing.WaitBeforeCaptureMillisecondsAsync(native, stop.Token, (amount, token) =>
            { Need(amount == TimeSpan.FromMilliseconds(5000), "Captured interval changed."); waits++; stop.Cancel(); return Task.Delay(Timeout.Infinite, token); }, 5000); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { cancelled = true; }
            var response = ShopPagePacing.CancelledAfterSettlement(new() { RequestId = "offline-config-cancel" }, native);
            Need(cancelled && waits == 1 && response.NativeReturned && response.NativeEntered && !response.SessionBlocked && !response.ResponseVerified,
                "Cancel lost the original settled native receipt.");
        });
        await Check("page_wait_config_next_page_uses_new_millisecond_value", async () =>
        {
            int configured = 100, dispatched = 0, captures = 0; var observed = new List<TimeSpan>();
            async Task Page()
            {
                int captured = ShopPagePacing.ValidateMilliseconds(Volatile.Read(ref configured)); dispatched++;
                await ShopPagePacing.WaitBeforeCaptureMillisecondsAsync(native, default, (amount, _) =>
                { observed.Add(amount); Volatile.Write(ref configured, 5000); return Task.CompletedTask; }, captured); captures++;
            }
            await Page(); await Page();
            Need(observed.SequenceEqual([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(5000)]) && dispatched == 2 && captures == 2,
                "Changing configuration changed or replayed the current page.");
        });
        return checks.ToArray();
    }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
