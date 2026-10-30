using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MemoryHub.Service;

internal sealed record LocalPostCodeRecoveryResult(int Matched, string? RequestId, string Detail);

internal static class ShopPostCodeLocalRecovery
{
    internal static LocalPostCodeRecoveryResult Apply(string root, ShopScanProgressStore scans, ShopSearchedOwnerStore owners,
        ShopPostCodeRecoveredProof recovery, Action<string>? checkpoint = null, bool ownsScanLease = false)
    {
        root = Path.GetFullPath(root); string sessions = Path.Combine(root, "sessions");
        string stem = recovery.Source.ProcessId + "-" + recovery.Source.CreationFileTime;
        var matches = new List<(string Path, string Hash, NativePostCodeRecoveryEvidence Evidence, bool ScanAttempt)>();
        var files = Directory.Exists(sessions) ? Directory.EnumerateFiles(sessions, stem + "*.pending.json").Take(1025).ToArray() : [];
        ShopPostCodeRecoveryProof.Need(files.Length <= 1024, "Local pending search exceeds bounds.");
        foreach (string file in files)
        {
            using var doc = ShopPostCodeRecoveryProof.Document(file, 32768); var p = doc.RootElement;
            var source = p.GetProperty("source").Deserialize<SourceIdentity>(HubProtocol.Json);
            string native = ShopPostCodeRecoveryProof.Text(p, "nativeLog");
            if (source != recovery.Source || !ShopPostCodeRecoveryProof.Same(native, recovery.OriginalNativeLogPath)) continue;
            string requestId = ShopPostCodeRecoveryProof.Text(p, "requestId");
            var owner = p.GetProperty("owner").Deserialize<MarketOwner>(HubProtocol.Json) ?? throw new InvalidDataException("Original owner missing.");
            string direction = ShopPostCodeRecoveryProof.Text(p, "viewerDirection");
            ShopPostCodeRecoveryProof.Need(ShopPostCodeRecoveryProof.Text(p, "state") == "ForceNativeLaunchIntent_NoAutomaticReplay"
                && requestId.Length is > 0 and <= 128 && !requestId.Any(char.IsControl)
                && owner.Id == recovery.TargetOwner && p.GetProperty("targetOwner").GetUInt64() == owner.Id
                && direction == recovery.Direction && !string.IsNullOrWhiteSpace(owner.LocalIdentity)
                && ShopPostCodeRecoveryProof.Same(file, ShopForceDispatchPolicy.PendingPath(root, stem, requestId)), "Original local pending binding differs.");
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId)));
            using var requestDoc = ShopPostCodeRecoveryProof.Document(Path.Combine(root, "requests", key + ".request.json"), 128 * 1024);
            var accepted = requestDoc.RootElement;
            var request = accepted.GetProperty("request").Deserialize<ShopCommandRequest>(HubProtocol.Json) ?? throw new InvalidDataException("Original accepted request missing.");
            string signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, HubProtocol.Json))));
            string state = ShopPostCodeRecoveryProof.Text(accepted, "state");
            ShopPostCodeRecoveryProof.Need(request.RequestId == requestId && request.Command == ShopCommands.OpenShop && request.ForceDispatch
                && request.ExpectedSourceSessionKey == source!.SessionKey && request.OwnerId == owner.Id
                && request.ExpectedOwnerIdentity == owner.LocalIdentity && request.ViewerDirection == direction
                && ShopPostCodeRecoveryProof.Text(accepted, "signature") == signature
                && state is "Accepted_NoAutomaticReplay" or "ScanChild_NoAutomaticReplay", "Original accepted request differs.");
            var evidence = new NativePostCodeRecoveryEvidence(source!, requestId, owner.Id, owner.LocalIdentity, direction,
                recovery.OriginalNativeLogPath, recovery.OriginalNativeLogSha256, recovery.ReceiptPath, recovery.ReceiptSha256, recovery.JournalRoot);
            matches.Add((file, ShopPostCodeRecoveryProof.Hash(file), evidence, state == "ScanChild_NoAutomaticReplay"));
        }
        ShopPostCodeRecoveryProof.Need(matches.Count <= 1, "Original native log maps to multiple local requests.");
        if (matches.Count == 0) return new(0, null, recovery.CompletionDetail + " 本次程式沒有對應原頁，不建立商品成功紀錄。");
        var match = matches[0]; var proof = match.Evidence;
        string detail = recovery.CompletionDetail + " 收據：" + proof.RecoveryReceiptPath;
        string receiptRoot = Path.Combine(root, "post-code-recovery", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proof.RequestId))));
        Directory.CreateDirectory(receiptRoot);
        string proofPath = Path.Combine(receiptRoot, "binding-v1.json");
        if (!File.Exists(proofPath)) WriteNew(proofPath, proof);
        else
        {
            using var saved = ShopPostCodeRecoveryProof.Document(proofPath, 64 * 1024);
            ShopPostCodeRecoveryProof.Need(saved.RootElement.Deserialize<NativePostCodeRecoveryEvidence>(HubProtocol.Json) == proof,
                "Local recovery proof changed.");
        }
        checkpoint?.Invoke("ProofDurable");
        // The caller owns nativeGate and no live scan run lease while entering
        // this recovery path. AcquireRunLease rejects a different live scanner.
        using var lease = ownsScanLease ? null : scans.AcquireRunLease();
        scans.RecoverPostCode(proof, detail, match.ScanAttempt); checkpoint?.Invoke("ScanRecovered");
        owners.RecoverPostCode(proof); checkpoint?.Invoke("OwnerRecovered");
        ShopPostCodeRecoveryProof.Need(ShopPostCodeRecoveryProof.Hash(match.Path) == match.Hash, "Original local pending changed during recovery.");
        string archived = match.Path[..^".pending.json".Length] + ".resolved-postcode-" + Guid.NewGuid().ToString("N") + ".json";
        File.Move(match.Path, archived); checkpoint?.Invoke("PendingArchived");
        WriteNew(Path.Combine(receiptRoot, "completed-" + Guid.NewGuid().ToString("N") + ".json"),
            new { proof.RequestId, proof.Source, State = "SentUnverified", PendingArchive = archived,
                proof.RecoveryReceiptPath, proof.RecoveryReceiptSha256, NativeRequests = 0, GameInput = 0, At = DateTimeOffset.UtcNow });
        return new(1, proof.RequestId, detail);
    }

    private static void WriteNew<T>(string path, T value)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, value, HubProtocol.Json); file.Flush(true);
    }
}
