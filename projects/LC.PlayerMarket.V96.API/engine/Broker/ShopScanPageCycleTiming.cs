using System.Diagnostics;
namespace MemoryHub.Service;

internal sealed class ShopScanPageCycleTiming(Action<string, string, object?> audit, string requestId, ulong ownerId, string direction) : IDisposable
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private static readonly AsyncLocal<ShopScanPageCycleTiming?> current = new();
    private readonly ShopScanPageCycleTiming? previous = SetCurrent();
    private int? interval;
    private static ShopScanPageCycleTiming? SetCurrent() => current.Value;
    internal void Enter() => current.Value = this;
    internal static void CaptureInterval(string id, int milliseconds)
    { if (current.Value is { } active && active.RequestId == id) active.interval = milliseconds; }
    private string RequestId => requestId;
    public void Dispose()
    {
        elapsed.Stop();
        audit("SHOP_SCAN_PAGE_CYCLE_TIMING", "完整單頁週期：含原請求／嘗試、原生、商品單讀及頁尾回覆／恢復進度持久化；非僅展示等待。",
            new { RequestId = requestId, OwnerId = ownerId, Direction = direction, StartedAt = startedAt,
                FinishedAt = DateTimeOffset.UtcNow, PageCycleMs = elapsed.Elapsed.TotalMilliseconds, PageCycleDurationMs = elapsed.Elapsed.TotalMilliseconds,
                ConfiguredPageOpenIntervalMilliseconds = interval == 0 ? (int?)null : interval,
                EffectiveIntervalMilliseconds = interval, NoAdditionalPageWait = interval is null ? (bool?)null : interval == 0,
                IncludesBeginAttemptAndRequestDurability = true, IncludesResponseAndResultDurability = true, PauseWaitExcluded = true });
        current.Value = previous;
    }
}
