using MemoryHub;

namespace LcPlayerMarket;

internal sealed partial class PatrolController
{
    private readonly bool continuousRecovery;

    private static readonly HashSet<string> RecoverableNavigationCodes = new(StringComparer.Ordinal)
    {
        "POSITION_CHANGING", "GAME_NOT_FOREGROUND", "NO_PATH", "DYNAMIC_PATH_BLOCKED",
        "SHORT_ROUTE_NOT_ARRIVED", "WAYPOINT_TIMEOUT", "TIME_LIMIT", "SMOOTH_OSCILLATION",
        "STALL_REPLAN_LIMIT", "ROUTE_STALE", "FRESH_OBSERVATION_REPLAN", "NO_ROUTE", "TARGET_BLOCKED",
        "F8_RECOVERY_REQUIRED", "NO_MOVEMENT", "LOCAL_ATTEMPTS_EXHAUSTED", "DETOUR_TOO_LONG",
        "POSITION_CHANGING_TIMEOUT", "VECTOR_CHANGED", "ACTOR_MEMBERSHIP_CHANGED"
    };

    internal static string? RecoverableNavigationCode(PatrolNavigationState state)
    {
        if (!state.Terminal || state.Completed || !state.CleanupConfirmed || !state.WorkSettled
            || state.State != "Failed" || state.UserStop || state.StopRequested) return null;
        string code = (state.Error ?? "").Trim();
        int separator = code.IndexOf(':');
        if (separator >= 0) code = code[..separator].Trim();
        if (code.StartsWith("POST_CLEANUP_", StringComparison.Ordinal)) code = code[13..];
        return RecoverableNavigationCodes.Contains(code) ? code : null;
    }

    private bool RecoverableReadinessRefusal(ShopCommandResponse response, RunState run) =>
        continuousRecovery && run.Origin is { } origin && response.WorkSettled && !response.SessionBlocked
        && !response.Scan.Running && !response.NativeEntered && !response.NativeReturned && !response.ResponseVerified
        && response.FailureCode is "NativeCodeUnavailable" or "NativePreflightReadUnavailable" or "ContextUnavailable" or "NativePreflightPageChanged"
        && response.FailureScope == "Global" && response.FailurePhase == "BeforePublish"
        && response.CurrentSourceSessionKey == origin.Source.SessionKey
        && (response.SavedScan is null || response.SavedScan.UncertainPages == 0)
        && response.NativeReadiness is { BuildCompatible: true, Ready: false, ReadOnly: true, NativeRequests: 0 } readiness
        && readiness.Source == origin.Source && readiness.Code == response.FailureCode
        && readiness.FailureRva == response.FailureRva
        && !string.IsNullOrWhiteSpace(readiness.ProbeId);

    private static int IncrementRecoveryCount(int count) => count == int.MaxValue ? count : count + 1;

    internal static bool IsSettledPageRangeFailure(ShopCommandResponse response, SourceIdentity origin) =>
        response.WorkSettled && !response.Scan.Running && !response.SessionBlocked
        && !response.NativeEntered && !response.NativeReturned && !response.ResponseVerified
        && response.Scan.FailureCode == "NativePreflightPageChanged" && response.Scan.FailureScope == "Global"
        && response.Scan.FailurePhase == "Preflight" && response.Scan.PreflightNotEnteredConfirmed
        && response.Scan.PreflightNoEntrySource == origin && response.CurrentSourceSessionKey == origin.SessionKey
        && response.Scan.SourceSessionKey == origin.SessionKey
        && response.SavedScan is { UncertainPages: 0 } saved && saved.SourceSessionKey == origin.SessionKey;

    private async Task<PatrolNavigationState> ReadNavigationStateAsync(RunState run,
        PatrolNavigationRequest request, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return await navigation.GetStateAsync(request.JobId, token).ConfigureAwait(false); }
            catch (Exception error) when (continuousRecovery && TransportFailure(error) && !token.IsCancellationRequested)
            {
                // A missing status reply never creates a second navigation Start.
                await RecoveryCooldownAsync(run, "導航狀態暫時讀取失敗；只重新查詢原工作 " + request.JobId, token).ConfigureAwait(false);
            }
        }
    }

    private async Task RetrySettledNavigationAsync(RunState run, string detail, CancellationToken token)
    {
        if (run.NavigationRequest is not { } request || !run.NavigationSettled)
            throw Block("導航尚未完整釋放；只保留原工作，不開始另一個導航。");
        var state = await ReadNavigationStateAsync(run, request, token).ConfigureAwait(false);
        ValidateNavigation(state, request);
        if (state.UserStop || state.StopRequested)
        { run.UserStopped = true; throw new OperationCanceledException("原導航已被手動停止。", token); }
        if (RecoverableNavigationCode(state) is null)
            throw Block("導航重試前原終態、取消或清理證據已改變；沒有重送。");
        // A new job is allowed only after the original job has a complete terminal
        // release receipt. A transport timeout never reaches this path.
        run.NavigationRequest = null; run.NavigationSettled = true; run.NavigationOwned = false;
        run.Phase = PatrolCheckpointPhase.Navigate;
        await RecoveryCooldownAsync(run, detail + "；已結清原導航，重新取得同來源位置後重規劃。", token).ConfigureAwait(false);
    }
}
