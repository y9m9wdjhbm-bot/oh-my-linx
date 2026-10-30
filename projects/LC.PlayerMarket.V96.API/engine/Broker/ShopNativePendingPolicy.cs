using System.Globalization;

namespace MemoryHub.Service;

internal enum ShopNativePendingState { None, ReturnedPostCodePending, Unknown }

// A typed status observation grants no authority to settle an original intent.
// Only the recovery adapter may validate and reconcile its exact durable proof.
internal sealed record ShopNativePendingObservation(SourceIdentity Source, ShopNativePendingState State,
    string? OriginalRequestId, string Detail);

internal static class ShopNativePendingPolicy
{
    internal static bool BlocksResponse(ShopCommandResponse response) => response.SessionBlocked
        || response.FailureScope is "Session" or "Unknown";

    internal static ShopNativePendingObservation Observe(SourceIdentity expected, string sharedJournal,
        Func<SourceIdentity, ShopNativePendingObservation>? observer = null, Func<string, bool>? exists = null)
    {
        try
        {
            var status = observer?.Invoke(expected) ?? new(expected, ShopNativePendingState.None, null, "");
            if (status is null || status.Source != expected || !Enum.IsDefined(status.State)
                || status.State == ShopNativePendingState.ReturnedPostCodePending && string.IsNullOrWhiteSpace(status.OriginalRequestId))
                return Unknown(expected, "原生工作狀態缺少相同來源或原請求識別；未解除原始紀錄。");
            if (status.State != ShopNativePendingState.None) return status;
            exists ??= ExistsStrict;
            string stem = expected.ProcessId.ToString(CultureInfo.InvariantCulture) + "-"
                + expected.CreationFileTime.ToString(CultureInfo.InvariantCulture);
            foreach (string suffix in new[] { ".pending.json", ".v15-singletrial.pending.json" })
                if (exists(Path.Combine(sharedJournal, stem + suffix)))
                    return Unknown(expected, "此 LC 仍有原生未結清紀錄（" + suffix
                        + "）；先核對原請求恢復狀態，未啟動下一個開頁元件。");
            string transaction = Path.Combine(sharedJournal, stem + ".delayed-postcode-recovery-v1.transaction.json");
            if (exists(transaction) && !ShopPostCodeRecoveryProof.CommittedTransaction(transaction, expected, sharedJournal))
                return Unknown(expected, "此 LC 的唯讀恢復交易尚未完整提交；先恢復同一交易，不啟動買賣開頁元件。");
            return status;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Unknown(expected, "原生未結清狀態無法確認：" + error.Message);
        }
    }

    internal static void RequireReady(ShopNativePendingObservation observation, bool localIntentUnsettled)
    {
        if (observation.State == ShopNativePendingState.None && !localIntentUnsettled) return;
        string detail = string.IsNullOrWhiteSpace(observation.Detail)
            ? "此 LC 的原生請求尚未完整結清；只可核對原工作狀態，未啟動下一個開頁元件。" : observation.Detail;
        throw new ShopRequestFailure(new("NativePendingUnresolved", "Session", "PendingRecovery", null, detail));
    }

    // The callback is exclusively a read/recovery adapter, never a buy/sell
    // launcher. Its return is not proof: always recheck durable and typed state.
    internal static async Task RecoverAndRequireReadyAsync(SourceIdentity expected,
        Func<ShopNativePendingObservation> observe, Func<bool> localIntentUnsettled,
        Func<SourceIdentity, CancellationToken, Task>? recovery, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var status = observe();
        bool local = localIntentUnsettled();
        if (status.Source != expected) throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "原生恢復狀態來源不同。"));
        if (status.State == ShopNativePendingState.None && !local) return;
        if (recovery is not null)
        {
            await recovery(expected, token).WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            status = observe(); local = localIntentUnsettled();
            if (status.Source != expected) throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "原生恢復後來源不同。"));
        }
        RequireReady(status, local);
    }

    private static ShopNativePendingObservation Unknown(SourceIdentity source, string detail) =>
        new(source, ShopNativePendingState.Unknown, null, detail);

    private static bool ExistsStrict(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
