namespace MemoryHub.Service;

/// <summary>A captured millisecond interval after confirmed native completion, before one page read.</summary>
internal static class ShopPagePacing
{
    internal const int FixedInterPageDelayMilliseconds = 3000;
    internal const int MinimumMilliseconds = 100, MaximumMilliseconds = 5000;
    internal static int ValidateMilliseconds(int milliseconds) => milliseconds == 0 || milliseconds is >= MinimumMilliseconds and <= MaximumMilliseconds ? milliseconds
        : throw new ArgumentOutOfRangeException(nameof(milliseconds), milliseconds, "開頁間隔須為 0（不額外等待）或 100～5000 毫秒。");
    // Compatibility for callers that still express whole seconds. The product range is never expanded.
    internal static int ValidateSeconds(int seconds) => seconds is >= 1 and <= 5 ? seconds
        : throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "整秒開頁間隔須為 1～5 秒。");
    internal static Task<bool> WaitBeforeCaptureAsync(ShopNativeOutcome native, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delay = null, int waitSeconds = 3,
        TimeSpan elapsedSinceNativeCompletion = default) =>
        WaitBeforeCaptureMillisecondsAsync(native, token, delay, checked(ValidateSeconds(waitSeconds) * 1000), elapsedSinceNativeCompletion);

    internal static async Task<bool> WaitBeforeCaptureMillisecondsAsync(ShopNativeOutcome native, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delay = null, int waitMilliseconds = 0,
        TimeSpan elapsedSinceNativeCompletion = default)
    {
        ValidateMilliseconds(waitMilliseconds);
        ArgumentNullException.ThrowIfNull(native);
        if (elapsedSinceNativeCompletion < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsedSinceNativeCompletion));
        if (!native.SafeToSettle || !native.Returned && !native.NoEntryConfirmed)
            throw new InvalidOperationException("原生結果尚未完整釋放，不能開始開頁間隔或讀取商品。");
        if (!native.Returned) return false;
        token.ThrowIfCancellationRequested();
        var remaining = TimeSpan.FromMilliseconds(waitMilliseconds) - elapsedSinceNativeCompletion;
        if (remaining > TimeSpan.Zero)
            await (delay ?? Task.Delay)(remaining, token).WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return true;
    }

    internal static ShopCommandResponse CancelledAfterSettlement(ShopCommandResponse response, ShopNativeOutcome native)
    {
        if (!native.SafeToSettle || !native.Returned)
            throw new InvalidOperationException("不能將未確認原生結果標為已清理的等待取消。");
        return response with { State = "Cancelled", NativeEntered = native.Entered, NativeReturned = true,
            ResponseVerified = false, SessionBlocked = false,
            Detail = "已取消本頁等待；原請求已正常返回並完成清理，保留本頁已送紀錄，沒有重送或開下一頁。" };
    }
}
