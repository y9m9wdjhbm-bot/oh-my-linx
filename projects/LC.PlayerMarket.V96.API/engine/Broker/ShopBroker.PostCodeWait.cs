using System.Text.Json;

namespace MemoryHub.Service;

internal sealed record ShopPostCodeRecoveryAttempt(bool Recovered, bool RetryableReadOnlyRecovery, string? RequestId);

internal sealed record ShopPostCodePageWait(string RequestId, int Milliseconds, System.Diagnostics.Stopwatch NativeCompletionClock)
{
    internal Task WaitAfterRecoveryAsync(CancellationToken token) => WaitRemainingMillisecondsAsync(Milliseconds, NativeCompletionClock.Elapsed, token);
    internal static Task WaitRemainingAsync(int capturedSeconds, TimeSpan elapsedSinceCompletion,
        CancellationToken token, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        WaitRemainingMillisecondsAsync(checked(ShopPagePacing.ValidateSeconds(capturedSeconds) * 1000), elapsedSinceCompletion, token, delay);
    internal static async Task WaitRemainingMillisecondsAsync(int capturedMilliseconds, TimeSpan elapsedSinceCompletion,
        CancellationToken token, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ShopPagePacing.ValidateMilliseconds(capturedMilliseconds);
        if (elapsedSinceCompletion < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsedSinceCompletion));
        token.ThrowIfCancellationRequested();
        var remaining = TimeSpan.FromMilliseconds(capturedMilliseconds) - elapsedSinceCompletion;
        if (remaining > TimeSpan.Zero) await (delay ?? Task.Delay)(remaining, token).WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }
}

internal static class ShopPostCodeRecoveryWait
{
    internal static async Task<bool> RunAsync(string requestId, Func<bool> originalUnchanged,
        Func<CancellationToken, Task<ShopPostCodeRecoveryAttempt>> attempt,
        Func<CancellationToken, Task> delay, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!originalUnchanged()) return false;
            var result = await attempt(token).WaitAsync(token).ConfigureAwait(false);
            if (result.Recovered) return result.RequestId == requestId;
            // This flag is emitted only after the packaged helper validated the
            // exact original native receipt and both pending journals, and then
            // observed a temporary NOACCESS page while reading current code.
            if (!result.RetryableReadOnlyRecovery) return false;
            await delay(token).WaitAsync(token).ConfigureAwait(false);
        }
    }
}

public sealed partial class ShopBroker
{
    private ShopPostCodePageWait? postCodePageWait;

    private async Task<bool> RecoverCurrentScanPageAsync(ShopCommandRequest request, SourceIdentity expected, CancellationToken token)
    {
        ShopPostCodePageWait? pageWait;
        lock (gate) pageWait = postCodePageWait;
        if (pageWait is null || pageWait.RequestId != request.RequestId) return false;
        try
        {
        string localPath = ShopForceDispatchPolicy.PendingPath(directory, SessionStem(expected), request.RequestId);
        string singlePath = Path.Combine(SharedJournal, SessionStem(expected) + ".v15-singletrial.pending.json");
        if (!ShopPostCodeRecoveryProof.Exists(localPath) || !ShopPostCodeRecoveryProof.Exists(singlePath)) return false;
        using var localDoc = ShopPostCodeRecoveryProof.Document(localPath, 32768);
        using var sharedDoc = ShopPostCodeRecoveryProof.Document(singlePath, 32768);
        var local = localDoc.RootElement; var shared = sharedDoc.RootElement;
        string native = ShopPostCodeRecoveryProof.Text(local, "nativeLog");
        var owner = local.GetProperty("owner").Deserialize<MarketOwner>(HubProtocol.Json);
        if (local.GetProperty("source").Deserialize<SourceIdentity>(HubProtocol.Json) != expected
            || local.GetProperty("requestId").GetString() != request.RequestId
            || local.GetProperty("state").GetString() != "ForceNativeLaunchIntent_NoAutomaticReplay"
            || owner?.Id != request.OwnerId || owner?.LocalIdentity != request.ExpectedOwnerIdentity
            || local.GetProperty("viewerDirection").GetString() != request.ViewerDirection
            || shared.GetProperty("Schema").GetString() != "V15SingleTrialPendingV1"
            || shared.GetProperty("Pid").GetInt32() != expected.ProcessId
            || shared.GetProperty("Creation").GetInt64() != expected.CreationFileTime
            || shared.GetProperty("Image").GetUInt64() != ShopRuntimeContext.ImageBase(expected)
            || shared.GetProperty("TargetOwner").GetUInt64() != request.OwnerId
            || shared.GetProperty("Mode").GetInt32() != (request.ViewerDirection == ShopPageModes.ViewerBuy ? 1 : 2)
            || !ShopPostCodeRecoveryProof.Same(ShopPostCodeRecoveryProof.Text(shared, "Log"), native)) return false;
        string localHash = ShopPostCodeRecoveryProof.Hash(localPath), singleHash = ShopPostCodeRecoveryProof.Hash(singlePath), nativeHash = ShopPostCodeRecoveryProof.Hash(native);
        bool recovered = await ShopPostCodeRecoveryWait.RunAsync(request.RequestId, () =>
        {
            token.ThrowIfCancellationRequested();
            return runtime.IsRunning && !ShopSourceAvailability.Changed(runtime.Snapshot.Source, expected)
                && ShopPostCodeRecoveryProof.Exists(localPath) && ShopPostCodeRecoveryProof.Hash(localPath) == localHash
                && ShopPostCodeRecoveryProof.Exists(singlePath) && ShopPostCodeRecoveryProof.Hash(singlePath) == singleHash
                && ShopPostCodeRecoveryProof.Hash(native) == nativeHash;
        }, async cancellation =>
        {
            await nativeGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                RequireNativeHelperAvailable();
                return await TryRecoverPostCodePendingAsync(expected, cancellation, ownsScanLease: true).ConfigureAwait(false);
            }
            finally { nativeGate.Release(); }
        }, async cancellation =>
        {
            lock (gate) scan = scan with { Detail = "原生指令已返回，正在唯讀核對同一原請求的程式頁；保留商品未確認，不重送原頁、不換店主。" };
            // A polling interval for this readonly condition, not the patrol's
            // retry budget or configured cooldown; F9 cancels the wait.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellation).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        if (recovered)
        {
            lock (gate) scan = scan with { Detail = "原指令已返回且清理完成；保留原頁商品未確認，補足本頁已捕捉的展示等待後接下一個未送頁。" };
            await pageWait.WaitAfterRecoveryAsync(token).ConfigureAwait(false);
        }
        return recovered;
        }
        finally { lock (gate) if (ReferenceEquals(postCodePageWait, pageWait)) postCodePageWait = null; }
    }
}
