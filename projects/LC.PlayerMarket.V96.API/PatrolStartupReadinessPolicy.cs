using MemoryHub;

namespace LcPlayerMarket;

internal sealed record PatrolStartupReadinessContext(SourceIdentity Source, ulong SelfId);
internal sealed record PatrolStartupReadinessObservation(
    PatrolStartupReadinessContext? Current, ShopCommandResponse? ReadinessResponse,
    ShopCommandResponse? SettledState, bool ContextFresh, bool NoOutstandingUiWork);
internal sealed record PatrolStartupReadinessResult(bool Ready, string Code, int Attempts,
    long ElapsedMilliseconds, PatrolStartupReadinessObservation? FinalObservation,
    PatrolStartupReadinessPermit? Permit);
internal sealed record PatrolStartupReadinessProgress(int Attempt, int MaximumAttempts,
    long ElapsedMilliseconds, string Code)
{
    public bool ContinuousWait { get; init; }
    public bool ProbeStillRunning { get; init; }
    public int NextCheckMilliseconds { get; init; }
}

// A bounded retry of completed readonly preflights, never of a native request.
// This admission binds source/self only. Numeric map authority remains the
// controller's fresh Capture/ValidateObservation before navigation or queries.
internal static partial class PatrolStartupReadinessPolicy
{
    internal const int TotalBudgetMilliseconds = 30000;
    internal const int MaximumAttempts = 6;
    internal const int MinimumCooldownMilliseconds = 2000;

    internal static async Task<PatrolStartupReadinessResult> WaitAsync(
        PatrolStartupReadinessContext expected,
        Func<bool, CancellationToken, Task<PatrolStartupReadinessObservation>> sample,
        Func<PatrolStartupReadinessContext?> currentContext, Func<DateTimeOffset> utcNow,
        Func<long> milliseconds, Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken token, Action<PatrolStartupReadinessProgress>? progress = null, bool continuous = false,
        Func<CancellationToken, Task<bool>>? recoverContext = null)
    {
        if (continuous) return await WaitContinuousAsync(expected, sample, currentContext, utcNow, milliseconds, delay, token, progress, recoverContext);
        long started = milliseconds();
        int attempts = 0;
        PatrolStartupReadinessObservation? last = null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TotalBudgetMilliseconds);
        long Elapsed() => Math.Max(0, milliseconds() - started);
        PatrolStartupReadinessResult End(string code) => new(false, code, attempts, Elapsed(), last, null);
        try
        {
            while (attempts < MaximumAttempts)
            {
                if (token.IsCancellationRequested) return End("Cancelled");
                if (Elapsed() >= TotalBudgetMilliseconds || budget.IsCancellationRequested) return End("TimedOut");
                if (!ValidContext(expected) || currentContext() != expected) return End("ContextChangedOrUnknown");
                attempts++;
                last = await sample(attempts == 1, budget.Token);
                if (token.IsCancellationRequested) return End("Cancelled");
                if (Elapsed() >= TotalBudgetMilliseconds || budget.IsCancellationRequested) return End("TimedOut");
                if (currentContext() != expected) return End("ContextChangedOrUnknown");
                var now = utcNow();
                if (FinalValid(expected, last, now))
                    return new(true, "Ready", attempts, Elapsed(), last,
                        new PatrolStartupReadinessPermit(expected, last, currentContext, milliseconds, milliseconds()));
                if (!Retryable(expected, last, now)) return End("ReadinessNotRetryable");
                if (attempts >= MaximumAttempts) return End("AttemptLimit");
                if (TotalBudgetMilliseconds - Elapsed() <= MinimumCooldownMilliseconds) return End("TimedOut");
                progress?.Invoke(new(attempts, MaximumAttempts, Elapsed(), "NativeCodeUnavailable"));
                await delay(TimeSpan.FromMilliseconds(MinimumCooldownMilliseconds), budget.Token);
            }
            return End("AttemptLimit");
        }
        catch (OperationCanceledException) { return End(token.IsCancellationRequested ? "Cancelled" : "TimedOut"); }
        catch (Exception) { return End("ReadinessObservationFailed"); }
    }

    internal static bool FinalValid(PatrolStartupReadinessContext expected,
        PatrolStartupReadinessObservation observation, DateTimeOffset now)
    {
        if (!CommonValid(expected, observation, now)) return false;
        var response = observation.ReadinessResponse!;
        return response.State == response.NativeReadiness!.Code
            && string.IsNullOrEmpty(response.FailureCode) && string.IsNullOrEmpty(response.FailureScope)
            && string.IsNullOrEmpty(response.FailurePhase) && string.IsNullOrEmpty(response.FailureRva)
            && response.NativeReadiness!.Fresh(now);
    }

    private static bool Retryable(PatrolStartupReadinessContext expected,
        PatrolStartupReadinessObservation observation, DateTimeOffset now)
    {
        if (!CommonValid(expected, observation, now)) return false;
        var response = observation.ReadinessResponse!;
        var proof = response.NativeReadiness!;
        // The broker's typed receipt is produced only after the strict, zero-native
        // preflight parser has closed its END and exact source. Ordinary false
        // NativeEntered/Returned defaults alone are never a no-entry proof.
        return !proof.Ready && response.State == "Rejected"
            && response.FailureCode == "NativeCodeUnavailable" && proof.Code == response.FailureCode
            && response.FailureScope == "Global" && response.FailurePhase == "BeforePublish"
            && !string.IsNullOrWhiteSpace(response.FailureRva) && proof.FailureRva == response.FailureRva;
    }

    private static bool CommonValid(PatrolStartupReadinessContext expected,
        PatrolStartupReadinessObservation observation, DateTimeOffset now)
    {
        if (!ValidContext(expected) || !observation.ContextFresh || !observation.NoOutstandingUiWork
            || observation.Current != expected || observation.ReadinessResponse is not { } response
            || observation.SettledState is not { } settled || string.IsNullOrWhiteSpace(response.RequestId)
            || string.IsNullOrWhiteSpace(settled.RequestId) || response.Schema != ShopCommands.Schema
            || settled.Schema != ShopCommands.Schema || settled.State != "State"
            || response.CurrentSourceSessionKey != expected.Source.SessionKey
            || settled.CurrentSourceSessionKey != expected.Source.SessionKey
            || !Fresh(response.At, now) || !Fresh(settled.At, now)
            || response.SessionBlocked || settled.SessionBlocked || !settled.WorkSettled
            || response.Scan.Running || settled.Scan.Running
            || response.NativeEntered || response.NativeReturned || response.ResponseVerified || response.EmptyPageObserved
            || settled.NativeEntered || settled.NativeReturned || settled.ResponseVerified || settled.EmptyPageObserved
            || !SavedSettled(response.SavedScan, expected) || !SavedSettled(settled.SavedScan, expected)
            || response.NativeReadiness is not { BuildCompatible: true, ReadOnly: true, NativeRequests: 0, PendingMask: 0 } proof
            || proof.Source != expected.Source || string.IsNullOrWhiteSpace(proof.ProbeId)) return false;
        return true;
    }

    private static bool SavedSettled(ShopSavedScanProgress? saved, PatrolStartupReadinessContext expected)
        => saved is null || saved.UncertainPages == 0 && saved.SourceSessionKey == expected.Source.SessionKey;
    private static bool ValidContext(PatrolStartupReadinessContext expected)
        => expected.Source is not null && expected.SelfId != 0
            && !string.IsNullOrWhiteSpace(expected.Source.SessionKey);
    private static bool Fresh(DateTimeOffset at, DateTimeOffset now)
        => at != default && at <= now && now - at <= TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds);
}

internal sealed class PatrolStartupReadinessPermit(
    PatrolStartupReadinessContext expected, PatrolStartupReadinessObservation owned,
    Func<PatrolStartupReadinessContext?> currentContext, Func<long> milliseconds, long issuedAtMilliseconds)
{
    private int consumed;
    internal bool TryCommit(PatrolStartupReadinessObservation observation, DateTimeOffset now,
        CancellationToken token, Action start, out string code)
    {
        if (Interlocked.CompareExchange(ref consumed, 1, 0) != 0) { code = "AlreadyConsumed"; return false; }
        if (token.IsCancellationRequested) { code = "Cancelled"; return false; }
        if (milliseconds() - issuedAtMilliseconds is < 0 or > HubProtocol.FreshMilliseconds)
        { code = "TimedOut"; return false; }
        if (!ReferenceEquals(observation.ReadinessResponse, owned.ReadinessResponse)
            || !ReferenceEquals(observation.SettledState, owned.SettledState)
            || currentContext() != expected || !PatrolStartupReadinessPolicy.FinalValid(expected, observation, now))
        { code = "ContextChangedOrReadinessExpired"; return false; }
        token.ThrowIfCancellationRequested();
        code = "Committed";
        start();
        return true;
    }
}
