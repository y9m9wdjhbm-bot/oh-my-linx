using MemoryHub.Service;
namespace LcPlayerMarket;

internal static class PagePacingElapsedSelfTest
{
    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        var native = new ShopNativeOutcome(true, true, true, "synthetic completed native lifecycle", null);
        foreach (var (milliseconds, persisted, expected) in new[] { (0, 0, 0), (0, 100, 0), (100, 0, 100), (100, 40, 60), (100, 100, 0), (1000, 2500, 0), (3000, 750, 2250), (5000, 2500, 2500) })
        {
            int delays = 0; double actual = 0;
            bool result = await ShopPagePacing.WaitBeforeCaptureMillisecondsAsync(native, CancellationToken.None,
                (duration, _) => { delays++; actual = duration.TotalMilliseconds; return Task.CompletedTask; },
                milliseconds, TimeSpan.FromMilliseconds(persisted));
            checks.Add(new($"page_elapsed_{milliseconds}ms_{persisted}ms", result && actual == expected && delays == (expected == 0 ? 0 : 1),
                $"Simulated persistence={persisted}ms, remaining delay={actual}ms; no game input."));
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool caught = false;
        try { await ShopPagePacing.WaitBeforeCaptureMillisecondsAsync(native, cancelled.Token, waitMilliseconds: 100, elapsedSinceNativeCompletion: TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { caught = true; }
        checks.Add(new("page_elapsed_cancel_even_if_no_wait_left", caught, "Expired display interval does not bypass user cancellation."));
        foreach (var unsafeResult in new[] { native with { SafeToSettle = false }, new ShopNativeOutcome(false, false, true, "unknown entry", null) })
        {
            caught = false; int calls = 0;
            try { await ShopPagePacing.WaitBeforeCaptureMillisecondsAsync(unsafeResult, CancellationToken.None, (_, _) => { calls++; return Task.CompletedTask; }, 100, TimeSpan.FromSeconds(5)); }
            catch (InvalidOperationException) { caught = true; }
            checks.Add(new("page_elapsed_does_not_relax_native_proof_" + unsafeResult.Entered, caught && calls == 0, "Elapsed time never proves native completion."));
        }
        caught = false;
        try { await ShopPagePacing.WaitBeforeCaptureMillisecondsAsync(native, CancellationToken.None, waitMilliseconds: 100, elapsedSinceNativeCompletion: TimeSpan.FromMilliseconds(-1)); }
        catch (ArgumentOutOfRangeException) { caught = true; }
        checks.Add(new("page_elapsed_reject_negative", caught, "A monotonic completion interval is required."));
        return checks.ToArray();
    }
}
