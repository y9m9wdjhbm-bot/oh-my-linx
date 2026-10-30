using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MemoryHub.Service;

public sealed partial class ShopBroker
{
    private async Task RecoverPostCodePendingAsync(SourceIdentity expected, CancellationToken token)
        => _ = await TryRecoverPostCodePendingAsync(expected, token, false).ConfigureAwait(false);

    private async Task<ShopPostCodeRecoveryAttempt> TryRecoverPostCodePendingAsync(SourceIdentity expected, CancellationToken token, bool ownsScanLease)
    {
        token.ThrowIfCancellationRequested();
        // This method is called with nativeGate held, outside lock(gate).
        // Reuse the existing helper flight; timeout/cancellation never means exit.
        string single = Path.Combine(SharedJournal, SessionStem(expected) + ".v15-singletrial.pending.json");
        string transaction = Path.Combine(SharedJournal, SessionStem(expected) + ".delayed-postcode-recovery-v1.transaction.json");
        bool hasTransaction = ShopPostCodeRecoveryProof.Exists(transaction);
        bool committedTransaction = hasTransaction && ShopPostCodeRecoveryProof.CommittedTransaction(transaction, expected, SharedJournal);
        if (ShopPostCodeRecoveryProof.Exists(single)
            || hasTransaction && !committedTransaction)
        {
            EnsureNativeAvailable(expected);
            string receiptPath = Path.Combine(nativeLogDirectory, "recovery-" + Guid.NewGuid().ToString("N") + ".jsonl");
            string expectedReceipt = ShopPostCodeRecoveryOutput.ExpectedReceipt(transaction, expected, receiptPath, hasTransaction, committedTransaction);
            var start = new ProcessStartInfo(HelperPath) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(HelperPath)! };
            foreach (string arg in new[] { "--recover-returned-lifecycle", expected.ProcessId.ToString(CultureInfo.InvariantCulture),
                expected.CreationFileTime.ToString(CultureInfo.InvariantCulture),
                "0x" + ShopRuntimeContext.ImageBase(expected).ToString("X", CultureInfo.InvariantCulture), receiptPath })
                start.ArgumentList.Add(arg);
            ShopPostCodeRecoveryProcess? process = null;
            int exit = await nativeHelperFlight.RunAsync(() => process = ShopPostCodeRecoveryProcess.Start(start),
                TimeSpan.FromSeconds(30)).WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (exit != 0)
            {
                bool retryable = ShopPostCodeRecoveryOutput.RetryableFailure(await process!.Output.ConfigureAwait(false), exit);
                Audit("SHOP_NATIVE_RECOVERY_PENDING", "原生唯讀恢復尚未取得完整證據；保留原請求，不啟動買賣開頁元件。",
                    new { expected.ProcessId, ExitCode = exit, ReceiptPath = receiptPath, RetryableReadOnlyRecovery = retryable, NativeRequests = 0, GameInput = 0 });
                return new(false, retryable, null);
            }
            var result = ShopPostCodeRecoveryOutput.Read(await process!.Output.ConfigureAwait(false), expectedReceipt, expected, SharedJournal);
            var local = ApplyPostCodeProof(result);
            return new(true, false, local.RequestId);
        }

        LocalPostCodeRecoveryResult? applied = null;
        if (hasTransaction)
            applied = ApplyPostCodeProof(ShopPostCodeRecoveryProof.Read(ShopPostCodeRecoveryProof.TransactionReceiptPath(transaction, expected), expected, SharedJournal));

        // A crash can occur after the helper committed both archives but before
        // this broker appended its local recovery event. Resume only immutable
        // receipts from this runtime's own recovery directory, never a filename.
        var receipts = Directory.EnumerateFiles(nativeLogDirectory, "recovery-*.jsonl").Take(257).ToArray();
        if (receipts.Length > 256) throw new InvalidDataException("原生恢復收據搜尋超出界線；原紀錄保留。");
        foreach (string path in receipts)
        {
            token.ThrowIfCancellationRequested();
            using var document = ShopPostCodeRecoveryProof.Document(path, 1024 * 1024);
            var identity = document.RootElement.GetProperty("Source");
            if (identity.GetProperty("Pid").GetInt32() != expected.ProcessId
                || identity.GetProperty("Creation").GetInt64() != expected.CreationFileTime) continue;
            var local = ApplyPostCodeProof(ShopPostCodeRecoveryProof.Read(path, expected, SharedJournal));
            if (local.Matched > 0) applied = local;
        }
        return new(applied is not null, false, applied?.RequestId);

        LocalPostCodeRecoveryResult ApplyPostCodeProof(ShopPostCodeRecoveredProof proof)
        {
            var local = ShopPostCodeLocalRecovery.Apply(directory, savedScans, searchedOwners, proof, ownsScanLease: ownsScanLease);
            lock (gate)
            {
                if (!savedScans.BlocksSource(expected) && !searchedOwners.BlocksSource(expected)
                    && !File.Exists(SessionPath(expected)) && ObserveNativePending(expected).State == ShopNativePendingState.None)
                    blockedSessions.Remove(SessionStem(expected));
            }
            Audit("SHOP_NATIVE_POSTCODE_RECOVERED", local.Detail,
                new { expected.ProcessId, local.RequestId, local.Matched, proof.ReceiptPath, proof.ReceiptSha256,
                    proof.LifecycleOnly, CurrentCreateCopyBytesVerified = !proof.LifecycleOnly,
                    OriginalPostCodeConfirmed = false, OriginalRequestRetransmitted = false,
                    OriginalProductResponseVerified = false, NativeRequests = 0, GameInput = 0 });
            return local;
        }
    }
}

internal static class ShopPostCodeRecoveryOutput
{
    internal static string ExpectedReceipt(string transaction, SourceIdentity expected, string requested, bool hasTransaction, bool committed)
        => hasTransaction && !committed ? ShopPostCodeRecoveryProof.TransactionReceiptPath(transaction, expected) : requested;

    internal static bool RetryableFailure(string output, int exit)
    {
        try
        {
            if (exit != 1 || output.Length is 0 or > 1024 * 1024) return false;
            using var document = JsonDocument.Parse(output); var r = document.RootElement;
            return ShopPostCodeRecoveryProof.Unique(r) && !r.GetProperty("VerifiedRecovered").GetBoolean()
                && r.GetProperty("RetryableReadOnlyRecovery").GetBoolean() && r.GetProperty("Code").GetString() == "PostCodeUnconfirmed"
                && r.GetProperty("NativeRequests").GetInt32() == 0 && r.GetProperty("GameInput").GetInt32() == 0
                && !r.GetProperty("OriginalRequestCompleted").GetBoolean() && !r.GetProperty("ServerAcknowledgmentVerified").GetBoolean();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException) { return false; }
    }
    internal static ShopPostCodeRecoveredProof Read(string output, string requestedReceipt, SourceIdentity expected, string journal)
    {
        if (output.Length is 0 or > 1024 * 1024) throw new InvalidDataException("Recovery stdout missing or exceeds bounds.");
        using var result = JsonDocument.Parse(output); var value = result.RootElement;
        if (!ShopPostCodeRecoveryProof.Unique(value)) throw new InvalidDataException("Duplicate recovery stdout fields.");
        if (!value.GetProperty("VerifiedRecovered").GetBoolean()
            || !ShopPostCodeRecoveryProof.Same(ShopPostCodeRecoveryProof.Text(value, "RecoveryReceiptPath"), requestedReceipt))
            throw new InvalidDataException("Helper did not confirm this requested recovery receipt.");
        string hash = ShopPostCodeRecoveryProof.Text(value, "RecoveryReceiptSha256");
        var proof = ShopPostCodeRecoveryProof.Read(requestedReceipt, expected, journal, hash);
        using var actual = ShopPostCodeRecoveryProof.Document(requestedReceipt, 1024 * 1024);
        if (!JsonElement.DeepEquals(value.GetProperty("Receipt"), actual.RootElement)) throw new InvalidDataException("Recovery stdout and durable receipt differ.");
        return proof;
    }
}

internal sealed class ShopPostCodeRecoveryProcess : IShopNativeHelperProcess
{
    private readonly Process process;
    internal Task<string> Output { get; }
    private ShopPostCodeRecoveryProcess(Process process) { this.process = process; Output = ReadBoundedAsync(process.StandardOutput); }
    internal static ShopPostCodeRecoveryProcess Start(ProcessStartInfo start) =>
        new(Process.Start(start) ?? throw new IOException("原生唯讀恢復元件未能啟動。"));
    public async Task WaitForExitAsync() { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); await Output.ConfigureAwait(false); }
    public bool HasExited => process.HasExited;
    public int ExitCode => process.ExitCode;
    public void Dispose() => process.Dispose();
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text = new StringBuilder(); var buffer = new char[4096];
        while (true)
        {
            int count = await reader.ReadAsync(buffer).ConfigureAwait(false); if (count == 0) return text.ToString();
            if (text.Length + count > 1024 * 1024) throw new IOException("Recovery stdout exceeds bounds.");
            text.Append(buffer, 0, count);
        }
    }
}
