using System.Diagnostics;

namespace MemoryHub.Service;

internal sealed record ShopStartupStageEvent(string Stage, string State, double ElapsedMs,
    int? Items = null, string? Error = null);

/// <summary>Observes initialization without changing replay, settlement, or exception behavior.</summary>
internal static class ShopStartupTrace
{
    internal static T Run<T>(string stage, Action<ShopStartupStageEvent>? observer, Func<T> action,
        Func<T, int?>? count = null)
    {
        if (observer is null) return action();
        Notify(observer, new(stage, "Started", 0));
        var clock = Stopwatch.StartNew();
        try
        {
            T result = action();
            Notify(observer, new(stage, "Completed", clock.Elapsed.TotalMilliseconds, count?.Invoke(result)));
            return result;
        }
        catch (Exception failure)
        {
            string detail = failure.GetType().Name + ": " + failure.Message;
            Notify(observer, new(stage, "Failed", clock.Elapsed.TotalMilliseconds, Error: detail[..Math.Min(detail.Length, 1024)]));
            throw;
        }
    }

    internal static void Run(string stage, Action<ShopStartupStageEvent>? observer, Action action) =>
        Run(stage, observer, () => { action(); return 0; });

    private static void Notify(Action<ShopStartupStageEvent> observer, ShopStartupStageEvent item)
    {
        // A diagnostic sink must never skip validation or change the original failure.
        try { observer(item); } catch (Exception) { }
    }
}
