namespace LcPlayerMarket;

internal sealed partial class PatrolController
{
    /// <summary>The UI joins the old patrol and clears broker progress before calling this.</summary>
    internal async Task ResetProgressAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (strictController is not null)
            throw new InvalidOperationException("目前協調器未啟用新版巡迴恢復；不能重置另一個執行中的協調器。");
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("原巡迴仍在執行／停止寫回；先完成停止及清理，不能同時重置。");
        try
        {
            if (blockedRun is { } prior)
            {
                if (!await CleanupAsync(prior).ConfigureAwait(false))
                    throw new InvalidOperationException("原巡迴工作尚未釋放；沒有清除原請求或恢復點。");
                token.ThrowIfCancellationRequested();
                var saved = checkpointStore is null ? resumeCheckpoint : checkpointStore.LoadLatest();
                if (prior.Route is null && saved?.RunId == prior.Id)
                    SaveRetiredOwnership(saved, prior, "Stopped");
                else SaveCheckpoint(prior, "Stopped", "重置前已用原工作識別再次核對清理。");
            }
            else if (blocked) throw new InvalidOperationException("原巡迴清理尚未確認，不能重置。");
            token.ThrowIfCancellationRequested();
            var reset = checkpointStore?.Reset();
            memoryResetGeneration = reset?.Generation ?? checked(memoryResetGeneration + 1);
            resumeCheckpoint = null; blockedRun = null; blocked = false; blockedReason = null;
        }
        finally { Volatile.Write(ref running, 0); }
    }
}
