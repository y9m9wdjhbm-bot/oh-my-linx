namespace LcPlayerMarket;

// A changed or unknown source/self invalidates this start intent permanently.
internal sealed class PatrolStartupContextLatch(PatrolStartupReadinessContext expected)
{
    private int invalidated;
    internal bool Invalidated => Volatile.Read(ref invalidated) != 0;
    internal bool Observe(PatrolStartupReadinessContext? current)
    {
        if (current != expected) Interlocked.Exchange(ref invalidated, 1);
        return !Invalidated;
    }
}

internal static partial class PatrolStartupReadinessPolicy
{
    internal const int ContinuousCooldownMilliseconds = 10000;
    private const int ContextPollMilliseconds = 250;

    // The historical continuous entry now has the same finite attempt/time
    // budget. A slow owned readonly probe is cancelled, never replaced. The
    // broker retains its flight until actual completion/cleanup is known.
    private static async Task<PatrolStartupReadinessResult> WaitContinuousAsync(
        PatrolStartupReadinessContext expected,
        Func<bool, CancellationToken, Task<PatrolStartupReadinessObservation>> sample,
        Func<PatrolStartupReadinessContext?> currentContext, Func<DateTimeOffset> utcNow,
        Func<long> milliseconds, Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken token, Action<PatrolStartupReadinessProgress>? progress,
        Func<CancellationToken, Task<bool>>? recoverContext)
    {
        long started = milliseconds(); int attempts = 0;
        PatrolStartupReadinessObservation? last = null;
        long Elapsed() => Math.Max(0, milliseconds() - started);
        PatrolStartupReadinessResult End(string code) => new(false, code, attempts, Elapsed(), last, null);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TotalBudgetMilliseconds);
        async Task<bool> ContextMatchesAsync()
        {
            if (!ValidContext(expected)) return false;
            var current = currentContext();
            if (current == expected) return true;
            if (current is not null || recoverContext is null) return false;
            return await recoverContext(budget.Token) && currentContext() == expected;
        }

        try
        {
            while (attempts < MaximumAttempts)
            {
                if (token.IsCancellationRequested) return End("Cancelled");
                if (budget.IsCancellationRequested || Elapsed() >= TotalBudgetMilliseconds) return End("TimedOut");
                if (!await ContextMatchesAsync()) return End(recoverContext is null ? "ContextChangedOrUnknown" : "FreshIdentityUnavailableOrChanged");
                if (budget.IsCancellationRequested || Elapsed() >= TotalBudgetMilliseconds) return End("TimedOut");
                attempts++;
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                Task<PatrolStartupReadinessObservation> pending = sample(attempts == 1, probe.Token);
                try
                {
                    while (!pending.IsCompleted)
                    {
                        if (token.IsCancellationRequested) return End("Cancelled");
                        if (budget.IsCancellationRequested || Elapsed() >= TotalBudgetMilliseconds) return End("TimedOut");
                        if (!await ContextMatchesAsync()) return End(recoverContext is null ? "ContextChangedOrUnknown" : "FreshIdentityUnavailableOrChanged");
                if (budget.IsCancellationRequested || Elapsed() >= TotalBudgetMilliseconds) return End("TimedOut");
                        if (pending.IsCompleted) break;
                        using var pulseCancellation = CancellationTokenSource.CreateLinkedTokenSource(probe.Token);
                        var pulse = delay(TimeSpan.FromMilliseconds(Math.Min(ContextPollMilliseconds,
                            TotalBudgetMilliseconds - Elapsed())), pulseCancellation.Token);
                        if (await Task.WhenAny(pending, pulse) == pending)
                        { pulseCancellation.Cancel(); ObserveLateFailure(pulse); }
                        else await pulse;
                    }
                    last = await pending;
                    if (token.IsCancellationRequested) return End("Cancelled");
                    if (budget.IsCancellationRequested || Elapsed() >= TotalBudgetMilliseconds) return End("TimedOut");
                    if (!await ContextMatchesAsync()) return End(recoverContext is null ? "ContextChangedOrUnknown" : "FreshIdentityUnavailableOrChanged");
                if (budget.IsCancellationRequested || Elapsed() >= TotalBudgetMilliseconds) return End("TimedOut");
                }
                finally
                {
                    if (!pending.IsCompleted) { probe.Cancel(); ObserveLateFailure(pending); }
                }
                if (recoverContext is not null)
                {
                    // Only the context comes from the new fresh identity sample.
                    // Readiness and broker receipt timestamps remain untouched.
                    var current = currentContext();
                    last = last with { Current = current, ContextFresh = current == expected };
                }
                var now = utcNow();
                if (FinalValid(expected, last, now))
                    return new(true, "Ready", attempts, Elapsed(), last,
                        new PatrolStartupReadinessPermit(expected, last, currentContext, milliseconds, milliseconds()));
                if (!Retryable(expected, last, now)
                    && !(recoverContext is not null && CanResampleCompletedReadonly(expected, last)))
                    return End("ReadinessNotRetryable");
                if (attempts >= MaximumAttempts) return End("AttemptLimit");
                if (TotalBudgetMilliseconds - Elapsed() <= MinimumCooldownMilliseconds) return End("TimedOut");
                progress?.Invoke(new(attempts, MaximumAttempts, Elapsed(), "NativeCodeUnavailable")
                    { ContinuousWait = false, NextCheckMilliseconds = MinimumCooldownMilliseconds });
                for (int remaining = MinimumCooldownMilliseconds; remaining > 0;)
                {
                    if (token.IsCancellationRequested) return End("Cancelled");
                    if (budget.IsCancellationRequested || Elapsed() >= TotalBudgetMilliseconds) return End("TimedOut");
                    if (!await ContextMatchesAsync()) return End(recoverContext is null ? "ContextChangedOrUnknown" : "FreshIdentityUnavailableOrChanged");
                if (budget.IsCancellationRequested || Elapsed() >= TotalBudgetMilliseconds) return End("TimedOut");
                    int step = Math.Min(ContextPollMilliseconds, remaining);
                    await delay(TimeSpan.FromMilliseconds(step), budget.Token); remaining -= step;
                }
            }
            return End("AttemptLimit");
        }
        catch (OperationCanceledException)
        { return End(token.IsCancellationRequested ? "Cancelled" : budget.IsCancellationRequested || Elapsed() >= TotalBudgetMilliseconds ? "TimedOut" : "ReadinessObservationFailed"); }
        catch (Exception) { return End("ReadinessObservationFailed"); }
    }
    private static void ObserveLateFailure(Task task) => _ = task.ContinueWith(
        completed => { _ = completed.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
