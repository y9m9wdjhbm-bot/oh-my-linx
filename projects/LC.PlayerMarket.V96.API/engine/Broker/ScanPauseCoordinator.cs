namespace MemoryHub.Service;

/// <summary>
/// Linearizes pause requests and page dispatch on the broker's own gate. A page
/// is committed only by EnterPageAsync while pause is false. Already committed
/// work must finish its cleanup; no page is restarted or automatically replayed.
/// </summary>
internal sealed class ScanPauseCoordinator(object sync, string jobId, Action? pausedAtBoundary = null)
{
    private TaskCompletionSource changed = NewSignal();
    private bool active = true, cancelled, pauseRequested, paused, pageInFlight;
    internal (bool Requested, bool Paused) Status { get { lock (sync) return (active && pauseRequested, active && paused); } }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse() { var previous = changed; changed = NewSignal(); previous.TrySetResult(); }
    private void AcknowledgePause()
    {
        if (paused) return;
        paused = true; pausedAtBoundary?.Invoke();
    }
    private void RequireJob(string expected)
    {
        if (!active || expected != jobId) throw new InvalidOperationException("SCAN_JOB_MISMATCH：搜尋工作已不同或已結束。");
    }
    internal void RequestPause(string expected)
    {
        lock (sync)
        {
            RequireJob(expected);
            if (cancelled) throw new InvalidOperationException("SCAN_STOPPING：工作已要求停止，不能改為暫停。");
            pauseRequested = true;
            // Only the worker reaching a safe point may acknowledge Paused.
            Pulse();
        }
    }
    internal void Resume(string expected)
    {
        lock (sync)
        {
            RequireJob(expected);
            if (cancelled) throw new InvalidOperationException("SCAN_STOPPING：工作已要求停止，不能繼續。");
            pauseRequested = paused = false; Pulse();
        }
    }
    internal void Cancel()
    {
        lock (sync) { cancelled = true; pauseRequested = paused = false; Pulse(); }
    }
    internal void Complete()
    {
        lock (sync) { active = false; pauseRequested = paused = false; Pulse(); }
    }
    internal async Task<IDisposable> EnterPageAsync(string expected, Func<bool> runtimeRunning, CancellationToken token)
    {
        while (true)
        {
            Task wake;
            lock (sync)
            {
                RequireJob(expected); token.ThrowIfCancellationRequested();
                if (cancelled) throw new OperationCanceledException("搜尋已停止。", token);
                if (!runtimeRunning()) throw new InvalidOperationException("SCAN_RUNTIME_STOPPED：本機讀取器已停止；不再繼續剩餘頁面。");
                if (pageInFlight) throw new InvalidOperationException("SCAN_PAGE_CONCURRENT：上一頁尚未完成。");
                if (!pauseRequested)
                {
                    paused = false; pageInFlight = true;
                    return new PageLease(this);
                }
                AcknowledgePause(); wake = changed.Task;
            }
            // This low-cost wake also notices a stopped runtime without adding
            // a game read, timer callback, or dependency on future IPC traffic.
            // The signal is captured under the same lock as its state: a Resume
            // between this point and await is not lost.
            try { await wake.WaitAsync(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
    }
    private void EndPage()
    {
        lock (sync)
        {
            pageInFlight = false;
            if (active && !cancelled && pauseRequested) AcknowledgePause();
            Pulse();
        }
    }
    private sealed class PageLease(ScanPauseCoordinator owner) : IDisposable
    {
        private ScanPauseCoordinator? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.EndPage();
    }
}

