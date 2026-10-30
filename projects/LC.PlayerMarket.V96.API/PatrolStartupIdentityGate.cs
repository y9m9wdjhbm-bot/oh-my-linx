using MemoryHub;

namespace LcPlayerMarket;

// This gate never returns the remembered expected context while a field is
// missing/stale. A positive different raw identity permanently retires the
// intent; only a freshly sampled same identity can end a bounded read gap.
internal sealed class PatrolStartupIdentityGate(PatrolStartupReadinessContext expected, Func<long> milliseconds)
{
    internal const int FreshWaitMilliseconds = 4000;
    private readonly object gate = new();
    private bool invalidated, expired;
    private long? unknownSince;
    internal bool Invalidated { get { lock (gate) return invalidated || expired; } }
    internal PatrolStartupReadinessContext? Observe(LiveStartupIdentityObservation observation)
    {
        lock (gate)
        {
            if (observation.DefinitelyDifferent(expected)) invalidated = true;
            if (unknownSince is { } began && milliseconds() - began >= FreshWaitMilliseconds) expired = true;
            if (invalidated || expired) return null;
            if (observation.Context == expected) { unknownSince = null; return observation.Context; }
            unknownSince ??= milliseconds();
            return null;
        }
    }
    internal async Task<bool> WaitFreshAsync(Func<LiveStartupIdentityObservation> read,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(FreshWaitMilliseconds);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (Observe(read()) == expected) return true;
                int remaining;
                lock (gate)
                {
                    if (invalidated || expired) return false;
                    long age = Math.Max(0, milliseconds() - unknownSince!.Value);
                    if (age >= FreshWaitMilliseconds) { expired = true; return false; }
                    remaining = (int)(FreshWaitMilliseconds - age);
                }
                await delay(TimeSpan.FromMilliseconds(Math.Min(100, remaining)), limit.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && limit.IsCancellationRequested)
        { return false; }
    }
}

internal static partial class PatrolStartupReadinessPolicy
{
    // This only allows another READONLY check after the old zero-native flight
    // has actually completed and the broker is settled. It never grants a
    // permit, renews timestamps, or lets an expired native proof be consumed.
    private static bool CanResampleCompletedReadonly(PatrolStartupReadinessContext expected,
        PatrolStartupReadinessObservation observation)
    {
        if (observation.Current != expected || !observation.ContextFresh || !observation.NoOutstandingUiWork
            || observation.ReadinessResponse is not { } response || observation.SettledState is not { } state
            || response.Schema != ShopCommands.Schema || state.Schema != ShopCommands.Schema
            || string.IsNullOrWhiteSpace(response.RequestId) || string.IsNullOrWhiteSpace(state.RequestId)
            || state.State != "State" || !state.WorkSettled || state.SessionBlocked || response.SessionBlocked
            || state.Scan.Running || response.Scan.Running || state.NativeEntered || state.NativeReturned
            || state.ResponseVerified || state.EmptyPageObserved || response.NativeEntered || response.NativeReturned
            || response.ResponseVerified || response.EmptyPageObserved
            || response.CurrentSourceSessionKey != expected.Source.SessionKey || state.CurrentSourceSessionKey != expected.Source.SessionKey
            || !SavedSettled(response.SavedScan, expected) || !SavedSettled(state.SavedScan, expected)
            || response.NativeReadiness is not { BuildCompatible: true, ReadOnly: true, NativeRequests: 0, PendingMask: 0 } proof
            || proof.Source != expected.Source || string.IsNullOrWhiteSpace(proof.ProbeId)) return false;
        if (proof.Ready)
            return response.State == proof.Code && string.IsNullOrEmpty(response.FailureCode)
                && string.IsNullOrEmpty(response.FailureScope) && string.IsNullOrEmpty(response.FailurePhase)
                && string.IsNullOrEmpty(response.FailureRva);
        return response.State == "Rejected" && response.FailureCode == "NativeCodeUnavailable"
            && proof.Code == response.FailureCode && response.FailureScope == "Global"
            && response.FailurePhase == "BeforePublish" && !string.IsNullOrWhiteSpace(response.FailureRva)
            && proof.FailureRva == response.FailureRva;
    }
}
