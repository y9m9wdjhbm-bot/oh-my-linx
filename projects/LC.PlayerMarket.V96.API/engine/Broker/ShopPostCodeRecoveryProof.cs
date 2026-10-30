using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace MemoryHub.Service;

internal sealed record NativePostCodeRecoveryEvidence(SourceIdentity Source, string RequestId,
    ulong OwnerId, string OwnerIdentity, string Direction, string OriginalNativeLogPath,
    string OriginalNativeLogSha256, string RecoveryReceiptPath, string RecoveryReceiptSha256, string JournalRoot);

internal sealed record ShopPostCodeRecoveredProof(SourceIdentity Source, ulong TargetOwner, string Direction,
    string OriginalNativeLogPath, string OriginalNativeLogSha256, string ReceiptPath, string ReceiptSha256,
    string JournalRoot, string OriginalForcePendingPath, string OriginalSingleTrialPendingPath)
{
    internal bool LifecycleOnly { get; init; }
    internal string CompletionDetail => LifecycleOnly
        ? "原指令已返回且清理完成，事後程式頁仍未確認；商品回應未確認，原頁不重送，只接下一個未送頁。"
        : "原指令已返回且清理完成，目前程式頁已另行核對；商品回應仍未確認，原頁不重送，只接下一個未送頁。";
}

internal static class ShopPostCodeRecoveryProof
{
    internal static ShopPostCodeRecoveredProof Read(string path, SourceIdentity expected, string journalRoot,
        string? expectedHash = null, bool requireCurrentClear = true)
    {
        path = Path.GetFullPath(path); journalRoot = Path.GetFullPath(journalRoot);
        using var doc = Document(path, 1024 * 1024); var r = doc.RootElement;
        string hash = Hash(path);
        Need(expectedHash is null || hash == expectedHash, "Recovery receipt SHA changed.");
        string schema = Text(r, "Schema");
        bool lifecycleOnly = schema == "ReturnedLifecycleRecoveryV1";
        Need((schema == "DelayedPostCodeRecoveryV1" || lifecycleOnly) && Text(r, "Phase") == "Committed"
            && Sha(Text(r, "TransactionId")), "Recovery receipt is not committed.");
        if (lifecycleOnly)
            Need(r.GetProperty("LifecycleSettled").GetBoolean()
                && Text(r, "Disposition") == "ReturnedAndCleaned_PostCodeUnavailable"
                && !r.GetProperty("CurrentCreateCopyBytesVerified").GetBoolean(), "Returned lifecycle disposition differs.");
        else foreach (string name in new[] { "LifecycleSettled", "Disposition", "CurrentCreateCopyBytesVerified" })
            Need(!r.TryGetProperty(name, out _), "Strict recovery contains lifecycle-only fields.");
        CheckIdentity(r.GetProperty("Source"), expected);
        foreach (string name in new[] { "OriginalNativeReturned", "OriginalCleanupConfirmed",
            "OnlyFutureDistinctRequestsMayProceed", "NoAutomaticRetransmission" }) Need(r.GetProperty(name).GetBoolean(), name);
        foreach (string name in new[] { "OriginalPostCodeConfirmed", "OriginalRequestCompletionVerified", "ServerAcknowledgmentVerified",
            "RemoteModuleUnloadProven", "EntireHookTailExitProven" })
            Need(!r.GetProperty(name).GetBoolean(), name);
        Need(r.GetProperty("NativeRequests").GetInt32() == 0 && r.GetProperty("GameInput").GetInt32() == 0,
            "Recovery receipt contains dispatch/input.");
        ulong owner = r.GetProperty("TargetOwner").GetUInt64(); int mode = r.GetProperty("Mode").GetInt32();
        Need(owner != 0 && mode is 1 or 2 && r.GetProperty("NativeNonce").GetUInt64() != 0, "Original target/mode/nonce missing.");
        var fresh = r.GetProperty("FreshProof"); CheckIdentity(fresh.GetProperty("Identity"), expected);
        var started = fresh.GetProperty("StartedAt").GetDateTimeOffset(); var finished = fresh.GetProperty("FinishedAt").GetDateTimeOffset();
        Need(started != default && started >= r.GetProperty("OriginalEndAt").GetDateTimeOffset()
            && finished >= started
            && fresh.GetProperty("ReadCalls").GetInt64() > 0 && fresh.GetProperty("ReadBytes").GetInt64() > 0, "Fresh readonly proof incomplete.");
        if (lifecycleOnly)
            Need(fresh.GetProperty("ReturnedLifecycleContract").GetInt32() == 1
                && fresh.GetProperty("UnavailableProtectedMask").GetInt32() is >= 1 and <= 3
                && !fresh.GetProperty("CurrentCreateCopyBytesVerified").GetBoolean()
                && !fresh.GetProperty("AllCurrentBytesMatched").GetBoolean() && !fresh.GetProperty("NoDeferredBodies").GetBoolean()
                && fresh.GetProperty("MetadataAndDispatchProofs").GetArrayLength() == 7,
                "Returned lifecycle readonly proof contract differs.");
        else
        {
            foreach (string name in new[] { "ReturnedLifecycleContract", "UnavailableProtectedMask", "CurrentCreateCopyBytesVerified" })
                Need(!fresh.TryGetProperty(name, out _), "Strict current proof contains lifecycle-only fields.");
            Need(fresh.GetProperty("AllCurrentBytesMatched").GetBoolean() && fresh.GetProperty("NoDeferredBodies").GetBoolean()
                && fresh.GetProperty("MetadataAndDispatchProofs").GetArrayLength() >= 7, "Strict current code proof incomplete.");
        }
        string native = Path.GetFullPath(Text(r, "OriginalNativeLogPath")); string nativeHash = Text(r, "OriginalNativeLogSha256");
        Need(Sha(nativeHash) && Hash(native) == nativeHash, "Original native log SHA changed.");
        Need(new FileInfo(native).Length <= 4 * 1024 * 1024, "Original native log exceeds bounds.");
        string originalText = File.ReadAllText(native);
        Need(Hash(native) == nativeHash, "Original native log changed while reading.");
        ShopNativeOutcome.ValidateV17RecoveryOriginal(originalText, expected, owner, mode, r.GetProperty("NativeNonce").GetUInt64(), r);
        string stem = expected.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + expected.CreationFileTime.ToString(CultureInfo.InvariantCulture);
        string force = Path.GetFullPath(Text(r, "OriginalForcePendingPath"));
        string single = Path.GetFullPath(Text(r, "OriginalSingleTrialPendingPath"));
        Need(Same(Path.GetDirectoryName(force)!, journalRoot) && Path.GetFileName(force).StartsWith(stem + ".force-", StringComparison.Ordinal)
            && force.EndsWith(".pending.json", StringComparison.Ordinal), "Original force pending path differs.");
        Need(Same(single, Path.Combine(journalRoot, stem + ".v15-singletrial.pending.json")), "Original V15 pending path differs.");
        CheckArchive(r, "ForceResolvedPath", "OriginalForcePendingSha256", force, journalRoot);
        CheckArchive(r, "SingleTrialResolvedPath", "OriginalSingleTrialPendingSha256", single, journalRoot);
        if (requireCurrentClear) Need(!Exists(force) && !Exists(single), "Shared native pending remains after recovery.");
        return new(expected, owner, mode == 1 ? ShopPageModes.ViewerBuy : ShopPageModes.ViewerSell,
            native, nativeHash, path, hash, journalRoot, force, single) { LifecycleOnly = lifecycleOnly };
    }

    internal static void ValidateEvidence(NativePostCodeRecoveryEvidence value, SourceIdentity source,
        string requestId, ulong ownerId, string ownerIdentity, string direction)
    {
        Need(value.Source == source && value.RequestId == requestId && value.OwnerId == ownerId
            && value.OwnerIdentity == ownerIdentity && value.Direction == direction && !string.IsNullOrWhiteSpace(ownerIdentity),
            "Recovery evidence is not bound to the exact original request.");
        var proof = Read(value.RecoveryReceiptPath, source, value.JournalRoot, value.RecoveryReceiptSha256, requireCurrentClear: false);
        Need(proof.TargetOwner == ownerId && proof.Direction == direction && Same(proof.OriginalNativeLogPath, value.OriginalNativeLogPath)
            && proof.OriginalNativeLogSha256 == value.OriginalNativeLogSha256, "Recovery evidence original native identity differs.");
    }

    internal static string TransactionReceiptPath(string path, SourceIdentity expected)
    {
        using var document = Document(path, 1024 * 1024); var value = document.RootElement;
        Need(Text(value, "Schema") == "DelayedPostCodeRecoveryTransactionV1" && Text(value, "Phase") == "Prepared"
            && value.GetProperty("PendingFencesMayBeRemovedWithoutNativeReplay").GetBoolean()
            && value.GetProperty("NativeRequests").GetInt32() == 0 && value.GetProperty("GameInput").GetInt32() == 0,
            "Recovery transaction contract differs.");
        var intended = value.GetProperty("IntendedReceipt"); CheckIdentity(intended.GetProperty("Source"), expected);
        Need(Text(intended, "Schema") is "DelayedPostCodeRecoveryV1" or "ReturnedLifecycleRecoveryV1", "Unknown intended recovery schema.");
        Need(Sha(Text(value, "TransactionId")) && Text(value, "TransactionId") == Text(intended, "TransactionId"), "Recovery transaction identity differs.");
        return Path.GetFullPath(Text(value, "RecoveryReceiptPath"));
    }

    internal static bool CommittedTransaction(string path, SourceIdentity expected, string journal)
    {
        try
        {
            string receipt = TransactionReceiptPath(path, expected);
            var proof = Read(receipt, expected, journal, requireCurrentClear: false);
            using var tx = Document(path, 1024 * 1024); using var complete = Document(receipt, 1024 * 1024);
            var intended = tx.RootElement.GetProperty("IntendedReceipt");
            return Text(tx.RootElement, "TransactionId") == Text(complete.RootElement, "TransactionId")
                && CompatibleSchema(Text(intended, "Schema"), Text(complete.RootElement, "Schema"))
                && new[] { "ManagedProgramVersion", "PageRangeProofContract", "ColdCodePolicyId", "ColdCodeCertificateSha256", "MaximumDeferredMask" }
                    .All(name => intended.TryGetProperty(name, out var a)
                        ? complete.RootElement.TryGetProperty(name, out var b) && JsonElement.DeepEquals(a, b)
                        : !complete.RootElement.TryGetProperty(name, out _))
                && new[] { "Source", "TargetOwner", "Mode", "NativeNonce", "OriginalNativeLogPath", "OriginalNativeLogSha256",
                    "OriginalForcePendingPath", "OriginalForcePendingSha256", "ForceResolvedPath",
                    "OriginalSingleTrialPendingPath", "OriginalSingleTrialPendingSha256", "SingleTrialResolvedPath", "OriginalEndAt" }
                    .All(name => JsonElement.DeepEquals(intended.GetProperty(name), complete.RootElement.GetProperty(name)));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException or OverflowException) { return false; }
    }

    private static bool CompatibleSchema(string prepared, string committed) => prepared == committed
        || prepared == "ReturnedLifecycleRecoveryV1" && committed == "DelayedPostCodeRecoveryV1";

    internal static JsonDocument Document(string path, int maximum)
    {
        var info = new FileInfo(path);
        Need(info.Exists && info.Length > 0 && info.Length <= maximum && (info.Attributes & FileAttributes.ReparsePoint) == 0,
            "Recovery evidence missing, linked or exceeds bounds.");
        var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!Unique(doc.RootElement)) { doc.Dispose(); throw new InvalidDataException("Duplicate recovery JSON fields."); }
        return doc;
    }

    internal static string Hash(string path)
    {
        var info = new FileInfo(path); Need(info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0, "Evidence missing or linked.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(file));
    }
    internal static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    internal static bool Exists(string path)
    { try { _ = File.GetAttributes(path); return true; } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; } }
    internal static void Need(bool pass, string detail) { if (!pass) throw new InvalidDataException(detail); }
    internal static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new InvalidDataException(name + " missing.");
    private static bool Sha(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static void CheckIdentity(JsonElement value, SourceIdentity expected)
    {
        Need(value.GetProperty("Pid").GetInt32() == expected.ProcessId && value.GetProperty("Creation").GetInt64() == expected.CreationFileTime
            && value.GetProperty("Image").GetUInt64() == ShopRuntimeContext.ImageBase(expected)
            && Same(Text(value, "Path"), expected.ImagePath) && Text(value, "Sha256") == expected.DiskSha256
            && value.GetProperty("PeTimestamp").GetUInt32() == expected.PeTimestamp && value.GetProperty("ImageSize").GetUInt32() == expected.ImageSize,
            "Recovery physical source identity differs.");
    }
    private static void CheckArchive(JsonElement root, string archivedName, string hashName, string original, string journal)
    {
        string archived = Path.GetFullPath(Text(root, archivedName)); string hash = Text(root, hashName);
        Need(Sha(hash) && !Same(archived, original) && Same(Path.GetDirectoryName(archived)!, journal)
            && archived.StartsWith(original[..^".pending.json".Length] + ".", StringComparison.OrdinalIgnoreCase)
            && Hash(archived) == hash, "Recovery archive binding/SHA differs.");
    }
    internal static bool Unique(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object)
        { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var p in item.EnumerateObject()) if (!names.Add(p.Name) || !Unique(p.Value)) return false; }
        if (item.ValueKind == JsonValueKind.Array) foreach (var value in item.EnumerateArray()) if (!Unique(value)) return false;
        return true;
    }
}
