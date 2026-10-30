using MemoryHub;

namespace LcPlayerMarket;

internal sealed partial class MainForm
{
    private readonly Button checkNativeReadinessButton = Button("重新檢查商店入口");
    private bool nativeReadinessCheckBusy, patrolStartupWaiting;
    private int patrolStartupSamplesInFlight;
    private PatrolStartupIdentityGate? patrolStartupContextLatch;
    private bool nativeReadinessBusy => nativeReadinessCheckBusy || patrolStartupWaiting || Volatile.Read(ref patrolStartupSamplesInFlight) != 0;
    private ShopNativeReadiness? lastNativeReadiness;
    private readonly HashSet<string> observedNativeFailures = new(StringComparer.Ordinal);
    private DateTimeOffset lastNativeReadinessAt;
    private CancellationTokenSource? nativeReadinessCancellation;

    // This observation is for presentation only. Each scan and native page
    // retains its own fresh backend guard; no UI result authorizes dispatch.
    private Task<bool> CheckNativeReadinessAsync(CancellationToken token) => CheckNativeReadinessCoreAsync(token, preparePatrol: false);

    private async Task<bool> CheckNativeReadinessCoreAsync(CancellationToken token, bool preparePatrol,
        Action<ShopCommandResponse>? observe = null)
    {
        if (closing || nativeReadinessCheckBusy) return false;
        var expected = snapshot?.Source;
        if (expected is null)
        {
            Report("NATIVE_READINESS_NOT_CHECKED", "目前沒有可核對的 LC 來源，請等待讀取完成後重新檢查。");
            return false;
        }
        nativeReadinessCheckBusy = true;
        using var checkCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        nativeReadinessCancellation = patrolStartupWaiting ? patrolCancellation : checkCancellation;
        var request = new ShopCommandRequest { Command = preparePatrol ? ShopCommands.PrepareNativeReadiness : ShopCommands.CheckNativeReadiness,
            ExpectedSourceSessionKey = expected.SessionKey, WaitForNativeReadinessCompletion = patrolStartupWaiting };
        lastNativeReadiness = null;
        UpdateButtons();
        Report("NATIVE_READINESS_CHECK", preparePatrol
            ? "巡迴啟動準備：先依原有證據核對未完成工作，再檢查商店入口。"
            : patrolStartupWaiting ? "正在唯讀重檢商店入口；原巡迴啟動意圖仍在等待，可停止或 F9 取消。"
            : "正在唯讀檢查商店入口；完成後不會自動開始搜尋。");
        try
        {
            var response = await backend.SendAsync(request, checkCancellation.Token);
            checkCancellation.Token.ThrowIfCancellationRequested();
            if (closing) return false;
            if (response.RequestId != request.RequestId || snapshot?.Source != expected
                || response.NativeReadiness is not { } value || value.Source != expected
                || !value.ReadOnly || value.NativeRequests != 0)
            {
                Report("NATIVE_READINESS_UNCONFIRMED", "商店入口檢查的來源或回覆無法核對，尚未開始搜尋；請重新檢查。");
                return false;
            }
            observe?.Invoke(response);
            lastNativeReadiness = value;
            lastNativeReadinessAt = response.At;
            if (lastConfirmedScan is { FailureCode: "NativeCodeUnavailable" or "NativeCodeMismatch" } previousFailure)
                observedNativeFailures.Add($"{previousFailure.JobId}|{previousFailure.FailureCode}|{previousFailure.FailureRva}");
            string detail = value.Ready && !value.Fresh(DateTimeOffset.UtcNow)
                ? "商店入口檢查結果已過期，請重新檢查。" : value.Detail;
            Report("NATIVE_READINESS_RESULT", detail, value);
            scanStatus.Text = detail;
            return value.Fresh(DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            if (!closing) Report("NATIVE_READINESS_CANCELLED", "已停止商店入口檢查，未開始搜尋。");
            return false;
        }
        catch (Exception error)
        {
            if (!closing) Report("NATIVE_READINESS_FAILED", "商店入口檢查未完成：" + error.Message);
            return false;
        }
        finally
        {
            nativeReadinessCheckBusy = false;
            if (ReferenceEquals(nativeReadinessCancellation, checkCancellation))
                nativeReadinessCancellation = patrolStartupWaiting ? patrolCancellation : null;
            if (!closing) UpdateButtons();
        }
    }

    private PatrolStartupReadinessContext? CurrentPatrolStartupContext()
    {
        return LiveStartupIdentityContext.Read(ReadPatrolSnapshot(), DateTimeOffset.UtcNow).Context;
    }

    private bool NoPatrolStartupUiWork() => !closing && !patrolStopRequested
        && !nativeReadinessCheckBusy && !actionBusy && !cancelBusy && !scanControlBusy
        && !resettingSearch && !clearingListings && patrolTask is null;

    private PatrolStartupReadinessContext? CurrentPatrolStartupWaitContext()
    {
        var observation = LiveStartupIdentityContext.Read(ReadPatrolSnapshot(), DateTimeOffset.UtcNow);
        return patrolStartupContextLatch is { } gate ? gate.Observe(observation) : observation.Context;
    }

    private void ObservePatrolStartupContextTransition()
    {
        if (patrolStartupWaiting && patrolStartupContextLatch is { } gate)
        {
            gate.Observe(LiveStartupIdentityContext.Read(ReadPatrolSnapshot(), DateTimeOffset.UtcNow));
            if (gate.Invalidated) patrolCancellation?.Cancel();
        }
    }

    private Task<bool> WaitForPatrolStartupFreshIdentityAsync(CancellationToken token) =>
        patrolStartupContextLatch is { } gate
            ? gate.WaitFreshAsync(() => LiveStartupIdentityContext.Read(ReadPatrolSnapshot(), DateTimeOffset.UtcNow),
                (wait, cancellation) => Task.Delay(wait, cancellation), token)
            : Task.FromResult(false);

    private async Task<PatrolStartupReadinessObservation> SamplePatrolStartupReadinessAsync(
        bool preparePending, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref patrolStartupSamplesInFlight, 1, 0) != 0)
            throw new InvalidOperationException("上一個巡迴入口檢查尚未結束，不能重複送出。");
        try { return await SamplePatrolStartupReadinessCoreAsync(preparePending, token); }
        finally
        {
            Interlocked.Exchange(ref patrolStartupSamplesInFlight, 0);
            if (!closing) UpdateButtons();
        }
    }

    private async Task<PatrolStartupReadinessObservation> SamplePatrolStartupReadinessCoreAsync(
        bool preparePending, CancellationToken token)
    {
        ShopCommandResponse? observed = null;
        await CheckNativeReadinessCoreAsync(token, preparePending, response => observed = response);
        token.ThrowIfCancellationRequested();
        // Completion of a typed preflight alone does not prove all market work
        // settled. Ask the original broker for a separate, source-bound state.
        if (CurrentPatrolStartupWaitContext() is null)
            await WaitForPatrolStartupFreshIdentityAsync(token);
        var current = CurrentPatrolStartupWaitContext();
        if (observed is null || current is null || observed.NativeReadiness?.Source != current.Source)
            return new(current, observed, null, current is not null, NoPatrolStartupUiWork());
        var request = new ShopCommandRequest { Command = ShopCommands.GetState,
            ExpectedSourceSessionKey = current.Source.SessionKey };
        var state = await backend.SendAsync(request, token);
        token.ThrowIfCancellationRequested();
        if (CurrentPatrolStartupWaitContext() is null)
            await WaitForPatrolStartupFreshIdentityAsync(token);
        current = CurrentPatrolStartupWaitContext();
        return new(current, observed, state.RequestId == request.RequestId ? state : null,
            current is not null, NoPatrolStartupUiWork());
    }

    private string NativeReadinessDetail()
    {
        if (nativeReadinessBusy) return "商店入口檢查中，可按停止搜尋取消。";
        if (lastNativeReadiness is not { } value || value.Source != snapshot?.Source)
            return "商店入口尚未檢查；開始搜尋或巡迴前會先檢查。";
        if (value.Ready && !value.Fresh(DateTimeOffset.UtcNow))
            return "上次商店入口檢查已過期；下次開始會重新檢查。";
        return value.Detail;
    }

    private void ObserveNativeReadiness(ShopCommandResponse response)
    {
        if (response.At < lastNativeReadinessAt) return;
        if (response.NativeReadiness is { } value && value.Source == snapshot?.Source
            && value.ReadOnly && value.NativeRequests == 0
            && (value.SampledAt == default || lastNativeReadiness?.SampledAt is not { } priorSample || value.SampledAt >= priorSample))
        {
            lastNativeReadiness = value;
            lastNativeReadinessAt = response.At;
        }
        bool copiedScanFailure = !string.IsNullOrWhiteSpace(response.Scan.JobId)
            && response.Scan.FailureCode == response.FailureCode && response.Scan.FailureRva == response.FailureRva;
        string failure = response.FailureCode is "NativeCodeUnavailable" or "NativeCodeMismatch"
            ? $"{(copiedScanFailure ? response.Scan.JobId : response.RequestId)}|{response.FailureCode}|{response.FailureRva}"
            : response.Scan.FailureCode is "NativeCodeUnavailable" or "NativeCodeMismatch"
                ? $"{response.Scan.JobId}|{response.Scan.FailureCode}|{response.Scan.FailureRva}"
                : "";
        if (failure.Length > 0 && observedNativeFailures.Add(failure))
        {
            lastNativeReadiness = null;
            // Keep old failed-job identities across successful state replies.
            // This set is diagnostic only and never grants dispatch permission.
            if (observedNativeFailures.Count > 512) observedNativeFailures.Clear();
        }
    }
}
