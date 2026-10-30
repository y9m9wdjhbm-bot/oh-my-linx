using MemoryHub;

namespace LcPlayerMarket;

internal static class PatrolNavigationPrewarm
{
    // Initialize the navigation component before sampling expiring readiness.
    // This historical pose is never reused as arrival/dispatch authority.
    internal static async Task<PatrolObservation> RunAsync(PatrolStartupReadinessContext expected,
        Func<CancellationToken, Task<PatrolObservation>> capture,
        Func<PatrolStartupReadinessContext?> context, Func<DateTimeOffset> utcNow, CancellationToken token,
        Func<CancellationToken, Task<bool>>? recoverContext = null)
    {
        if (context() != expected && (recoverContext is null || !await recoverContext(token).ConfigureAwait(false)))
            throw new InvalidOperationException("PREWARM_CONTEXT_CHANGED_OR_UNKNOWN");
        if (context() != expected) throw new InvalidOperationException("PREWARM_CONTEXT_NOT_FRESH");
        var pose = await capture(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); var now = utcNow();
        if (pose.Source != expected.Source || pose.SelfId != expected.SelfId
            || pose.MapId < 0 || pose.X is < 0 or > 65535 || pose.Y is < 0 or > 65535
            || pose.SampledAt == default || pose.SampledAt > now
            || now - pose.SampledAt > TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds))
            throw new InvalidOperationException("PREWARM_POSE_NOT_CURRENT_OR_BOUND");
        // Validate freshness when Capture completes. This pose is historical
        // initialization evidence, never the controller's arrival/dispatch proof.
        if (context() != expected && recoverContext is not null)
            await recoverContext(token).ConfigureAwait(false);
        if (context() != expected) throw new InvalidOperationException("PREWARM_IDENTITY_NOT_FRESH");
        return pose;
    }
}
