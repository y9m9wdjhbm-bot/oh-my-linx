using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MemoryHub.Service;

public sealed partial class ShopBroker
{
    // Owned from binding through receipt parsing, not only while the helper exists.
    // Access is under gate; completed observations need no durable action journal.
    private readonly HashSet<Task> nativeReadinessTasks = [];
    private Task<ShopCommandResponse> TrackNativeReadiness(Task<ShopCommandResponse> task)
    {
        nativeReadinessTasks.RemoveWhere(owned => owned.IsCompleted);
        nativeReadinessTasks.Add(task);
        return task;
    }

    private async Task<ShopCommandResponse> CheckNativeReadinessAsync(ShopCommandRequest request, CancellationToken token)
    {
        string probeId = Guid.NewGuid().ToString("N");
        ShopNativeReadiness observation;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        if (!request.WaitForNativeReadinessCompletion) deadline.CancelAfter(TimeSpan.FromSeconds(45));
        bool acquired = false;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            acquired = await nativeGate.WaitAsync(0, deadline.Token).ConfigureAwait(false);
            if (!acquired) observation = ShopNativeReadinessProof.Unavailable(runtime.Snapshot.Source, probeId,
                "NativeReadinessBusy", "目前工作尚未釋放；沒有啟動另一個唯讀檢查。");
            else
            {
                SourceIdentity? expected;
                lock (gate)
                {
                    expected = runtime.Snapshot.Source;
                    if (scan.Running || scanTask is { IsCompleted: false })
                        throw new ShopNativeHelperBusyException();
                }
                if (!runtime.IsRunning || expected is null || request.ExpectedSourceSessionKey != expected.SessionKey)
                    observation = ShopNativeReadinessProof.Unavailable(expected, probeId, "SourceMismatch", "目前來源尚未確認或已改變；未進行預檢。");
                else
                {
                    // Explicit patrol-start preparation uses the existing recovery
                    // policy, inside the accepted request/reset/ownership fences.
                    // The separate Check command never enters this mutating path.
                    if (request.Command == ShopCommands.PrepareNativeReadiness)
                    {
                        await RecoverNativePendingBeforePublishAsync(expected, deadline.Token).ConfigureAwait(false);
                        EnsureUnblocked(expected);
                        Audit("SHOP_NATIVE_START_PREPARED", "已依原有證據核對未完成工作；接著唯讀檢查入口，尚未建立搜尋計畫。", new { request.RequestId, Source = expected, NativeRequests = 0 });
                    }
                    observation = await ObserveNativeReadinessAsync(expected, deadline.Token, probeId,
                        request.WaitForNativeReadinessCompletion).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        { observation = ShopNativeReadinessProof.Unavailable(runtime.Snapshot.Source, probeId, token.IsCancellationRequested ? "NativeReadinessCancelled" : "NativeReadinessTimeout", "唯讀檢查已取消或逾時；沒有開始查詢，原 helper 仍追蹤至實際退出。"); }
        catch (ShopRequestFailure error)
        { observation = ShopNativeReadinessProof.Unavailable(runtime.Snapshot.Source, probeId, error.Failure.Code, error.Message, error.Failure.Rva); }
        catch (ShopNativeHelperBusyException error)
        { observation = ShopNativeReadinessProof.Unavailable(runtime.Snapshot.Source, probeId, "NativeReadinessBusy", error.Message); }
        finally { if (acquired) nativeGate.Release(); }
        Audit("SHOP_NATIVE_READINESS", observation.Detail, observation);
        return NativeReadinessResponse(request, observation, ownsPreparationGate: false);
    }

    // Caller owns nativeGate. This does not recover, remove or settle any historical pending record.
    private async Task<ShopNativeReadiness> ObserveNativeReadinessAsync(SourceIdentity expected, CancellationToken token, string? probeId = null,
        bool waitForCompletion = false)
    {
        probeId ??= Guid.NewGuid().ToString("N");
        if (!ShopNativePackage.SupportsBuild(expected))
            return ShopNativeReadinessProof.Unavailable(expected, probeId, "NativeBuildUnsupported", "目前 LC 版本與商店元件不相符；未送出查詢。");
        try
        {
            token.ThrowIfCancellationRequested();
            if (!runtime.IsRunning)
                return ShopNativeReadinessProof.Unavailable(expected, probeId, "ContextUnavailable", "檢查前讀取器已停止；未啟動 helper。");
            if (ShopSourceAvailability.Changed(runtime.Snapshot.Source, expected))
                return ShopNativeReadinessProof.Unavailable(expected, probeId, "SourceMismatch", "檢查前已確認來源不同；未啟動 helper。");
            if (runtime.Snapshot.Source is null)
                return ShopNativeReadinessProof.Unavailable(expected, probeId, "SourceUnavailable", "檢查前暫時沒有目前來源樣本；未宣稱 LC 已變更。");
            EnsureNativeAvailable(expected);
            var selection = runtime.ReadSelection;
            // Shop-only binding is complete even when no player shop page is open; owner -1 is retained as raw ulong.
            var binding = await Task.Run(() => source.ResolveShopActionSource(token), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (runtime.Snapshot.Source is null)
                return ShopNativeReadinessProof.Unavailable(expected, probeId, "SourceUnavailable", "唯讀綁定後來源暫時未知；未啟動 helper，未宣稱 LC 已變更。");
            if (binding.Source != expected || ShopSourceAvailability.Changed(runtime.Snapshot.Source, expected) || !ReferenceEquals(selection, runtime.ReadSelection)
                || binding.SampledAt > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - binding.SampledAt > TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds))
                return ShopNativeReadinessProof.Unavailable(expected, probeId, "SourceMismatch", "唯讀綁定不是本次新鮮完整來源；未啟動 helper。");
            ulong hwnd = ShopRuntimeContext.Window(expected);
            string logPath = NewNativeLogPath();
            var start = ReadinessStart(HelperPath, binding, hwnd, logPath);
            DateTimeOffset launchedAt = DateTimeOffset.UtcNow;
            int exit = await ShopNativeReadinessRunner.RunAsync(nativeHelperFlight,
                () => { token.ThrowIfCancellationRequested(); return StartObservedNativeHelper(start, "Readiness", probeId, logPath); },
                ShopNativeReadinessWaitPolicy.HelperBudget(waitForCompletion), token,
                waitForCompletion ? () => new ShopSourceObservation(runtime.IsRunning,
                    ReferenceEquals(selection, runtime.ReadSelection), runtime.Snapshot.Source) : null,
                waitForCompletion ? expected : null).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // After real helper exit, recover a short missing source sample using
            // the existing bounded same-source policy. This never launches another helper.
            if (waitForCompletion)
                await ShopSourceAvailability.WaitAsync(() => new(runtime.IsRunning,
                    ReferenceEquals(selection, runtime.ReadSelection), runtime.Snapshot.Source), expected, token).ConfigureAwait(false);
            if (!runtime.IsRunning || !ReferenceEquals(selection, runtime.ReadSelection))
                return ShopNativeReadinessProof.Unavailable(expected, probeId, "ContextUnavailable", "檢查交付前讀取器或設定已變更；不沿用檢查結果。");
            if (ShopSourceAvailability.Changed(runtime.Snapshot.Source, expected))
                return ShopNativeReadinessProof.Unavailable(expected, probeId, "SourceMismatch", "檢查交付前已確認來源不同；不沿用檢查結果。");
            if (runtime.Snapshot.Source is null)
                return ShopNativeReadinessProof.Unavailable(expected, probeId, "SourceUnavailable", "檢查交付前來源仍暫時未知；未宣稱 LC 已變更，不沿用檢查結果。");
            var info = new FileInfo(logPath);
            if (!info.Exists || info.Length is <= 0 or > ShopNativeReadinessProof.MaximumLogBytes)
                return ShopNativeReadinessProof.Unavailable(expected, probeId, "NativeReadinessUnknown", "唯讀 helper 收據缺漏或超出大小界線。");
            var result = ShopNativeReadinessProof.Parse(File.ReadAllText(logPath, Encoding.UTF8), expected, hwnd,
                binding.PlayerShopSubsystem, binding.CurrentOwnerIdRaw, exit, launchedAt, DateTimeOffset.UtcNow, probeId);
            Audit("SHOP_NATIVE_READINESS_RECEIPT", result.Detail, new { Result = result, NativeLog = logPath, NativeRequests = 0 });
            if (result.Ready) await WarmResidentWorkerAsync(expected).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (ShopNativeHelperWaitTimeoutException error)
        { return ShopNativeReadinessProof.Unavailable(expected, probeId, "NativeReadinessTimeout", error.Message); }
        catch (ShopNativeHelperBusyException error)
        { return ShopNativeReadinessProof.Unavailable(expected, probeId, "NativeReadinessBusy", error.Message); }
        catch (ShopRequestFailure error)
        { return ShopNativeReadinessProof.Unavailable(expected, probeId, error.Failure.Code, error.Message, error.Failure.Rva); }
        catch (Exception error)
        { return ShopNativeReadinessProof.Unavailable(expected, probeId, "NativeReadinessUnknown", "唯讀檢查尚未取得完整證據；未送出查詢。" + error.Message); }
    }

    internal static ProcessStartInfo ReadinessStart(string helper, MemoryHub.Reader.ShopActionSourceBinding binding, ulong hwnd, string logPath)
    {
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
        static string Hex(ulong number) => "0x" + number.ToString("X", CultureInfo.InvariantCulture);
        foreach (string argument in new[] { "--preflight", binding.Source.ProcessId.ToString(CultureInfo.InvariantCulture),
            binding.Source.CreationFileTime.ToString(CultureInfo.InvariantCulture), Hex(ShopNativeReadinessProof.Image(binding.Source)), Hex(hwnd),
            Hex(binding.PlayerShopSubsystem), binding.CurrentOwnerIdRaw.ToString(CultureInfo.InvariantCulture), logPath, "0" })
            start.ArgumentList.Add(argument);
        return start;
    }

    private ShopCommandResponse NativeReadinessResponse(ShopCommandRequest request, ShopNativeReadiness result, bool ownsPreparationGate)
    {
        var response = State(request.RequestId, result.Ready ? result.Code : "Rejected", result.Detail) with
        {
            NativeReadiness = result, FailureCode = result.Ready ? null : result.Code,
            FailureScope = result.Ready ? null : "Global", FailurePhase = result.Ready ? null : "BeforePublish",
            FailureRva = result.FailureRva
        };
        if (ownsPreparationGate)
        {
            // The readonly check is complete; this caller alone still owns nativeGate until its finally.
            lock (gate) response = response with { WorkSettled = NativeWorkSettled(!scan.Running
                && scanTask is not { IsCompleted: false } && activeNativeSource is null, nativeHelperFlight) };
        }
        return ShopResponseBudget.Fit(response);
    }
}

internal static class ShopNativeReadinessWaitPolicy
{
    internal static TimeSpan HelperBudget(bool waitForCompletion) =>
        waitForCompletion ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(30);
}

internal static class ShopNativeReadinessRunner
{
    internal static async Task<int> RunAsync(ShopNativeHelperFlight flight, Func<IShopNativeHelperProcess> start,
        TimeSpan waitBudget, CancellationToken token, Func<ShopSourceObservation>? observeSource = null,
        SourceIdentity? expectedSource = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (observeSource is not null && expectedSource is null) throw new ArgumentException("Expected source is required for monitored readonly waiting.");
        token.ThrowIfCancellationRequested();
        void RequireSameLifetime()
        {
            // Missing background samples are allowed while the same probe runs.
            // Changed() only identifies a different non-null full source.
            if (observeSource is not null && ShopSourceAvailability.Failure(observeSource(), expectedSource!, timedOut: false) is { } failure)
                throw new ShopRequestFailure(failure);
        }
        RequireSameLifetime();
        Task<int> owned = flight.RunAsync(() => { token.ThrowIfCancellationRequested(); RequireSameLifetime(); return start(); }, waitBudget);
        // Cancellation abandons only this wait; the existing owned process observer remains responsible for exit.
        _ = owned.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (observeSource is null) return await owned.WaitAsync(token).ConfigureAwait(false);
        delay ??= Task.Delay;
        while (!owned.IsCompleted)
        {
            token.ThrowIfCancellationRequested(); RequireSameLifetime();
            using var pulseCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task pulse = delay(TimeSpan.FromMilliseconds(250), pulseCancellation.Token);
            if (await Task.WhenAny(owned, pulse).ConfigureAwait(false) == owned)
            {
                pulseCancellation.Cancel();
                _ = pulse.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else await pulse.ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested(); RequireSameLifetime();
        return await owned.ConfigureAwait(false);
    }
}
