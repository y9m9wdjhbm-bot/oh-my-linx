using System.Diagnostics;

namespace MemoryHub.Service;

internal readonly record struct ShopSourceObservation(bool Running, bool SameConfiguration, SourceIdentity? Source);

/// <summary>A missing sample is not evidence of a different LC. No dispatch is allowed while waiting.</summary>
internal static class ShopSourceAvailability
{
    internal const int WaitMilliseconds = 1000;
    internal static bool Changed(SourceIdentity? current, SourceIdentity expected) => current is not null && current != expected;

    internal static ShopFailure? Failure(ShopSourceObservation observed, SourceIdentity expected, bool timedOut)
    {
        if (!observed.Running || !observed.SameConfiguration)
            return ShopFailure.Global("ContextUnavailable", "本機讀取器或欄位設定已改變；尚未送出下一頁。");
        if (Changed(observed.Source, expected))
            return ShopFailure.Global("SourceMismatch", "已確認 LC 來源與手動名單不同；請手動刷新後再操作。");
        return observed.Source is null && timedOut
            ? ShopFailure.Global("SourceUnavailable", "暫時無法確認 LC 來源，等候重新採樣後仍未恢復；尚未送出下一頁，已讀清單保留。")
            : null;
    }

    internal static async Task<double> WaitAsync(Func<ShopSourceObservation> observe, SourceIdentity expected,
        CancellationToken token, int maximumMilliseconds = WaitMilliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var current = observe();
            if (Failure(current, expected, timer.ElapsedMilliseconds >= maximumMilliseconds) is { } error)
                throw new ShopRequestFailure(error);
            if (current.Source == expected) return timer.Elapsed.TotalMilliseconds;
            await Task.Delay(20, token).ConfigureAwait(false);
        }
    }
}

