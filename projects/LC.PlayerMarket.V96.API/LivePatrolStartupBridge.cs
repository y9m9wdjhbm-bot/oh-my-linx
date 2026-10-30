using MemoryHub;

namespace LcPlayerMarket;

internal sealed record LivePatrolStartupAdmission(PatrolStartupReadinessResult Readiness,
    Task<PatrolRunResult>? Run, string CommitCode);

/// <summary>Production CLI adapter to the shared bounded UI startup policy; no direct native or LC port.</summary>
internal static class LivePatrolStartupBridge
{
    internal static async Task<LivePatrolStartupAdmission> WaitAndStartAsync(
        PatrolStartupReadinessContext expected,
        Func<ShopCommandRequest, CancellationToken, Task<ShopCommandResponse>> send,
        Func<PatrolStartupReadinessContext?> currentContext, Func<bool> noOutstandingWork,
        Func<DateTimeOffset> utcNow, Func<long> milliseconds,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken token,
        Func<Task<PatrolRunResult>> startController,
        Action<ShopCommandResponse>? observeReadiness = null,
        Action<PatrolStartupReadinessProgress>? progress = null,
        Func<CancellationToken, Task<bool>>? recoverContext = null)
    {
        var result = await PatrolStartupReadinessPolicy.WaitAsync(expected, Sample,
            currentContext, utcNow, milliseconds, delay, token, progress, continuous: true, recoverContext: recoverContext).ConfigureAwait(false);
        if (!result.Ready || result.Permit is null || result.FinalObservation is null)
            return new(result, null, "NotReady");
        var final = result.FinalObservation with { Current = currentContext(),
            ContextFresh = currentContext() == expected, NoOutstandingUiWork = noOutstandingWork() };
        Task<PatrolRunResult>? run = null;
        // There is no await or deferred continuation between commit and start.
        // TryCommit calls this exact callback synchronously and only once.
        bool committed = result.Permit.TryCommit(final, utcNow(), token,
            () => run = startController(), out string code);
        return new(result, committed ? run : null, code);

        async Task<PatrolStartupReadinessObservation> Sample(bool preparePending, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var request = new ShopCommandRequest
            {
                Command = preparePending ? ShopCommands.PrepareNativeReadiness : ShopCommands.CheckNativeReadiness,
                ExpectedSourceSessionKey = expected.Source.SessionKey,
                WaitForNativeReadinessCompletion = true
            };
            var observed = await send(request, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (observed.RequestId != request.RequestId)
                throw new InvalidDataException("STARTUP_READINESS_REQUEST_ID_MISMATCH");
            observeReadiness?.Invoke(observed);
            if (currentContext() is null && recoverContext is not null)
                await recoverContext(cancellation).ConfigureAwait(false);
            var current = currentContext();
            if (current is null || observed.NativeReadiness?.Source != current.Source)
                return new(current, observed, null, current is not null, noOutstandingWork());
            // Like the UI, a finished typed preflight requires a separate state
            // receipt before another readonly probe or controller admission.
            var stateRequest = new ShopCommandRequest { Command = ShopCommands.GetState,
                ExpectedSourceSessionKey = current.Source.SessionKey };
            var state = await send(stateRequest, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (currentContext() is null && recoverContext is not null)
                await recoverContext(cancellation).ConfigureAwait(false);
            current = currentContext();
            return new(current, observed, state.RequestId == stateRequest.RequestId ? state : null,
                current is not null, noOutstandingWork());
        }
    }
}
