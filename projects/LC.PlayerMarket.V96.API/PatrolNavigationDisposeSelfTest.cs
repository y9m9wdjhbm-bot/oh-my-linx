using System.Net;
using MemoryHub;

namespace LcPlayerMarket;

internal static class PatrolNavigationDisposeSelfTest
{
    internal static async Task<IReadOnlyList<OfflineCheck>> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        int requests = 0;
        PatrolNavigationClient NewClient() => new(() => null,
            (_, _, _, _) => { requests++; throw new InvalidOperationException("NO_TRANSPORT_DURING_OFFLINE_DISPOSE"); },
            () => DateTimeOffset.UtcNow, Task.Delay);
        var cancelled = NewClient();
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            bool observedCancellation = false;
            try { await cancelled.DisposeAsync(stop.Token); }
            catch (OperationCanceledException error) { observedCancellation = error.CancellationToken == stop.Token; }
            checks.Add(new("navigation_dispose_cancelled_token_not_reported_complete", observedCancellation && cancelled.ChildExitCode is null,
                "預先取消會拒絕開始清理，未捏造子程序退出或成功收尾。"));
        }
        await ((IAsyncDisposable)cancelled).DisposeAsync();
        checks.Add(new("navigation_dispose_original_wrapper_after_cancel", cancelled.ChildExitCode is null && requests == 0,
            "取消後仍可走原 IAsyncDisposable 收尾；不產生 transport 或偽造退出碼。"));
        var bounded = NewClient();
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
            await bounded.DisposeAsync(stop.Token);
        using (var alreadyCancelled = new CancellationTokenSource())
        {
            alreadyCancelled.Cancel();
            await bounded.DisposeAsync(alreadyCancelled.Token);
            await ((IAsyncDisposable)bounded).DisposeAsync();
        }
        checks.Add(new("navigation_dispose_bounded_and_original_overloads_idempotent", bounded.ChildExitCode is null && requests == 0,
            "已完成清理的 bounded／原重載均可重複呼叫；未送任何 transport。"));
        return checks;
    }
}
