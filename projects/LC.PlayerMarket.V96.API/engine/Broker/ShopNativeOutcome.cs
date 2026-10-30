using System.Globalization;
using System.Text;
using System.Text.Json;
using static MemoryHub.Service.ShopNativeV17Contract;

namespace MemoryHub.Service;

internal sealed record ShopFailure(string Code, string Scope, string Phase, string? Rva, string Detail)
{
    internal static ShopFailure Global(string code, string detail, string phase = "BeforePublish", string? rva = null) => new(code, "Global", phase, rva, detail);
    internal static ShopFailure Target(string detail) => new("TargetChanged", "Target", "BeforePublish", null, detail);
    internal static ShopFailure Unknown(string detail) => new("NativeOutcomeUnknown", "Session", "Native", null, detail);
    internal ShopCommandResponse Attach(ShopCommandResponse response) => response with
    { FailureCode = Code, FailureScope = Scope, FailurePhase = Phase, FailureRva = Rva };
}

internal sealed class ShopRequestFailure(ShopFailure failure) : InvalidOperationException(failure.Detail)
{
    internal ShopFailure Failure { get; } = failure;
}

internal enum ShopScanDisposition { Continue, SkipOwner, StopBatch }
internal static class ShopScanPolicy
{
    internal static ShopScanDisposition Decide(ShopCommandResponse response, bool sameSource, bool forceDispatch = false)
    {
        if (ShopNativePendingPolicy.BlocksResponse(response)) return ShopScanDisposition.StopBatch;
        if (forceDispatch)
        {
            if (!sameSource || response.FailureScope == "Global") return ShopScanDisposition.StopBatch;
            // Settled native calls with unverified product observations may
            // continue. Unsettled native/session results were fenced above.
            return ShopScanDisposition.Continue;
        }
        if (response.SessionBlocked || !sameSource || response.FailureScope is "Global" or "Session" or "Unknown")
            return ShopScanDisposition.StopBatch;
        // Only this verified pre-dispatch transient may skip a page. An unknown
        // Page failure or any native-entry evidence still stops the batch.
        if (response.FailureScope == "Page")
            return ((response.FailureCode == "ActorCollectionChanged" && response.FailurePhase == "BeforePublish")
                || (response.FailureCode == "NativePreflightReadUnavailable" && response.FailurePhase == "Preflight")) &&
                !response.NativeEntered && !response.NativeReturned && !response.ResponseVerified
                ? ShopScanDisposition.Continue : ShopScanDisposition.StopBatch;
        return response.FailureScope == "Target" ? ShopScanDisposition.SkipOwner : ShopScanDisposition.Continue;
    }
}

/// <summary>Retained observations require explicit current-broker refresh authorization before dispatch.</summary>
internal static class ShopTargetPolicy
{
    internal static void ValidateStored(ShopTargetSnapshot? snapshot, SourceIdentity? source)
    {
        if (snapshot is null || source is null || source.ProcessId <= 0 || source.CreationFileTime <= 0
            || string.IsNullOrWhiteSpace(source.SessionKey) || source.SessionKey.Length > 2048
            || source.DiskSha256 is not { Length: 64 } || !source.DiskSha256.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(snapshot.SnapshotId) || snapshot.SnapshotId.Length > 128
            || snapshot.SourceSessionKey != source.SessionKey || snapshot.RefreshedAt == default
            || snapshot.SelfId is 0 or > int.MaxValue || !ValidPoint(snapshot.SelfPosition)
            || snapshot.Owners is null || snapshot.Owners.Length > ActorCollectionContract.MaximumActors || snapshot.Detail is null || snapshot.Detail.Length > 4096)
            throw new InvalidDataException("保存的周遭名單來源／大小無效。");
        var ids = new HashSet<ulong>();
        foreach (var owner in snapshot.Owners)
            if (owner is null || owner.Id is 0 or > int.MaxValue || owner.Id == snapshot.SelfId || !ids.Add(owner.Id)
                || owner.LocalIdentity is not { Length: > 0 and <= 256 } || owner.Name is null || owner.Name.Length > 1024
                || !ValidPoint(owner.Position) || owner.ObservedAt == default || owner.MapName?.Length > 1024
                || owner.VisibilityEvidence is null || owner.VisibilityEvidence.Length > 256)
                throw new InvalidDataException("保存的周遭名單含重複 ID 或無效店主欄位。");
    }
    private static bool ValidPoint(ShopPoint? point) => point is { X: > 0 and <= 1_000_000, Y: > 0 and <= 1_000_000 };

    internal static void Validate(ShopTargetSnapshot? snapshot, SourceIdentity? observedSource, SourceIdentity? currentSource,
        string expectedSession, string? snapshotId, bool requireDispatch = true)
    {
        if (snapshot is null || observedSource is null || snapshot.SnapshotId != snapshotId)
            throw new ShopRequestFailure(ShopFailure.Global("TargetSnapshotMismatch", "請手動刷新周遭名單；請求未指定目前保存的名單版本。"));
        if (requireDispatch && !snapshot.CanDispatch)
            throw new ShopRequestFailure(ShopFailure.Global("TargetRefreshRequired",
                string.IsNullOrWhiteSpace(snapshot.DispatchBlockReason) ? "周遭名單只供查看；請成功刷新後再查詢。" : snapshot.DispatchBlockReason));
        if (expectedSession != observedSource.SessionKey || snapshot.SourceSessionKey != expectedSession || ShopSourceAvailability.Changed(currentSource, observedSource))
            throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "LC 來源已改變；舊名單保留供查看，操作前請手動刷新。"));
        if (currentSource is null)
            throw new ShopRequestFailure(ShopFailure.Global("SourceUnavailable", "目前 LC 來源採樣暫不可用；不能據此判定 LC 已更換，舊名單保留。"));
    }

    internal static MarketOwner Owner(ShopTargetSnapshot snapshot, ulong id, string? identity)
    {
        var owners = snapshot.Owners.Where(p => p.Id == id).Take(2).ToArray();
        if (id == snapshot.SelfId || owners.Length != 1 || string.IsNullOrEmpty(identity) || owners[0].LocalIdentity != identity)
            throw new ShopRequestFailure(ShopFailure.Target("店主 ID／本機識別不符合指定的手動名單；未沿用其他版本。"));
        return owners[0];
    }
}

internal sealed partial record ShopNativeOutcome(bool Entered, bool Returned, bool SafeToSettle, string Detail, ShopFailure? Failure)
{
    // Safe cleanup alone does not establish which source was observed or prove
    // non-entry. Persistent scan retries need this stronger positive evidence.
    internal bool NoEntryConfirmed { get; init; }
    private const int MaximumBytes = 4 * 1024 * 1024;

    internal static ShopNativeOutcome ParseFile(string path, SourceIdentity source, ulong image, ulong owner, int exitCode,
        ShopNativeLogPurpose purpose = ShopNativeLogPurpose.NativeRequest)
    {
        try
        {
            // Read only the exact per-request log, never search another helper's journal.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > MaximumBytes) return Uncertain("原生 LOG 缺少內容或超過 4 MiB。");
            using var copy = new MemoryStream(); var buffer = new byte[8192]; int read;
            while ((read = file.Read(buffer)) > 0)
            {
                if (copy.Length + read > MaximumBytes) return Uncertain("原生 LOG 讀取途中超過 4 MiB。");
                copy.Write(buffer, 0, read);
            }
            return Parse(Encoding.UTF8.GetString(copy.ToArray()), source, image, owner, exitCode, purpose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Uncertain("原生 LOG 無法讀取：" + ex.Message); }
    }

    internal static ShopNativeOutcome Parse(string text, SourceIdentity source, ulong image, ulong owner, int exitCode,
        ShopNativeLogPurpose purpose = ShopNativeLogPurpose.NativeRequest)
    {
        if (ShopNativePackage.SupportsBuild(source))
        {
            var current = ShopNativeB47COutcome.Parse(text,source,image,owner,exitCode,purpose);
            return new(current.Entered,current.Returned,current.SafeToSettle,current.Detail,current.Failure)
            { NoEntryConfirmed=current.NoEntryConfirmed, SourceMatched=current.SourceMatched, LogPurpose=current.LogPurpose };
        }
        if (ShopNativeLegacy79D1Package.SupportsBuild(source))
        {
            var current = ShopNative79D1Outcome.Parse(text,source,image,owner,exitCode,purpose);
            return new(current.Entered,current.Returned,current.SafeToSettle,current.Detail,current.Failure)
            { NoEntryConfirmed=current.NoEntryConfirmed, SourceMatched=current.SourceMatched, LogPurpose=current.LogPurpose };
        }
        bool entered = false, returned = false;
        ShopFailure? failure = null;
        var errors = new List<string>();
        try
        {
            if (purpose is not (ShopNativeLogPurpose.NativeRequest or ShopNativeLogPurpose.ReadOnlyPreflight))
                return Uncertain("原生 LOG 用途未指定或不支援。");
            if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) return Uncertain("原生 LOG 超過 4 MiB。");
            JsonElement? end = null, identity = null, start = null;
            int endings = 0, sources = 0, records = 0, starts = 0;
            bool recordsAfterEnd = false;
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (++records > 8192) return Uncertain("原生 LOG 超過記錄數界線。");
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!UniqueProperties(root)) return Uncertain("原生 LOG 含重複欄位，不能確認收據。", entered, returned, failure);
                string? kind = root.GetProperty("Kind").GetString();
                var data = root.GetProperty("Data");
                if (endings > 0) recordsAfterEnd = true;
                if (kind == "START") { start = data.Clone(); starts++; }
                if (kind == "SOURCE") { identity = data.Clone(); sources++; }
                if (kind == "ERROR")
                {
                    var next = Error(data);
                    // Retain the earliest concrete cause. Later cleanup errors remain in the detail.
                    if (failure is null || failure.Code == "NativeOutcomeUnknown") failure = next;
                    if (errors.Count < 8) errors.Add(Limit(data.GetRawText(), 2048));
                }
                if (kind is "PREFLIGHT_PROOF_AB_ERROR" or "PREFLIGHT_PAGE_AB_ERROR" or "NATIVE_CODE_PAGE_UNAVAILABLE")
                    if (errors.Count < 8) errors.Add(kind + ": " + Limit(data.GetRawText(), 2048));
                if (kind == "END") { end = data.Clone(); endings++; }
            }
            string context = errors.Count > 0 ? " Helper 錯誤：" + string.Join("；", errors) : "";
            if (end is not { } final || endings != 1 || recordsAfterEnd || sources > 1)
                return Uncertain("原生 SOURCE／END 不唯一、缺少或順序矛盾。" + context, failure: failure);
            if (final.GetProperty("ExitCode").GetInt32() != exitCode)
                return Uncertain("原生 END 與程序退出碼不一致。" + context, failure: failure);
            bool cleanup = final.GetProperty("CleanupConfirmed").GetBoolean(), blocked = final.GetProperty("SessionBlocked").GetBoolean();
            bool sourceMatches = identity is { } current && NativeSourceMatches(current, source, image, owner);
            bool tupleMatches = starts == 1 && start is { } tupleStart && NativeTupleMatches(tupleStart, source);
            int nativeVersion = tupleMatches && start is { } versionStart ? NativeTupleVersion(versionStart, source) : 0;
            bool purposeMatches = start is { } purposeStart && (ShopNativeHistoricalPackage.SupportsBuild(source)
                ? PurposeMatches(purposeStart, purpose) : purpose == ShopNativeLogPurpose.NativeRequest);
            bool policyMatches = start is { } policyStart && NativeLogPolicyMatches(policyStart, source);
            if (nativeVersion == 17 && !ShopNativeV17Contract.TerminalMatches(start!.Value, final))
                return Uncertain("V17 終態政策／證書不符；保留原請求。", failure: failure);
            uint exception = 0;
            if (!final.TryGetProperty("NativeResult", out var native))
                return Uncertain("原生 END 缺少 NativeResult，進入狀態不明。" + context, failure: failure);
            if (native.ValueKind != JsonValueKind.Null)
            {
                int entryCount = native.GetProperty("Entered").GetInt32(), returnCount = native.GetProperty("Returned").GetInt32();
                entered = entryCount != 0; returned = returnCount != 0;
                exception = native.GetProperty("Exception").GetUInt32();
                if (entryCount is < 0 or > 1 || returnCount is < 0 or > 1 || returnCount > entryCount
                    || native.GetProperty("Completed").GetInt32() != 1 || native.GetProperty("Claimed").GetInt32() != 1
                    || native.GetProperty("ObservedPid").GetUInt32() != source.ProcessId
                    || returned && (!entered || native.GetProperty("Status").GetInt32() != 1 || native.GetProperty("Stage").GetInt32() != 5))
                    return Uncertain("原生 END 的 PID／返回狀態矛盾。" + context, entered, returned, failure);
                if (purpose == ShopNativeLogPurpose.ReadOnlyPreflight)
                    return Uncertain("唯讀預檢卻包含 NativeResult，不能當成無原生進入的收據。" + context, entered, returned, failure);
                uint maximumDeferredMask = MaximumDeferredMask(nativeVersion);
                if (nativeVersion is 14 or 15 or 17 && (native.GetProperty("HostDeferredMask").GetUInt32() > maximumDeferredMask
                    || native.GetProperty("NativeDeferredMask").GetUInt32() > maximumDeferredMask))
                    return Uncertain("原生 deferred mask 超出該版協定的完整契約。" + context, entered, returned, failure);
                if (returned && (!tupleMatches || !purposeMatches || !policyMatches
                    || !final.GetProperty("PostCodeConfirmed").GetBoolean()
                    || !final.GetProperty("BorrowedSourceContractVerified").GetBoolean()
                    || !final.GetProperty("NativeAlreadyReturned").GetBoolean()
                    || native.GetProperty("PostCodeConfirmed").GetInt32() != 1
                    || native.GetProperty("HostDeferredMask").GetUInt32() > maximumDeferredMask
                    || native.GetProperty("NativeDeferredMask").GetUInt32() > maximumDeferredMask
                    || native.GetProperty("LeaseAcquired").GetInt32() != 1
                    || native.GetProperty("LeaseReleaseAttempted").GetInt32() != 1
                    || native.GetProperty("LeaseReleaseCompleted").GetInt32() != 1
                    || native.GetProperty("ReleaseExceptionCode").GetUInt32() != 0
                    || native.GetProperty("RequestUnchanged").GetInt32() != 1
                    || native.GetProperty("RequestMetadataBefore").GetUInt64() != 0
                    || native.GetProperty("RequestMetadataAfter").GetUInt64() != 0
                    || native.GetProperty("DestructorCalls").GetInt32() != 0))
                    return Uncertain("Host／Native／LC 版本配對、返回、程式頁面或參照清理記錄未閉合。" + context, entered, returned, failure);
                if (returned && (nativeVersion is 15 or 17) && !ConfirmedV15ResultReceipt(text, source, image, owner, native, final, true))
                    return Uncertain("V15 當次完整返回程式碼／metadata、來源及單次意圖收據未閉合；保留未知結果。" + context, entered, returned, failure);
            }
            if (nativeVersion == 17 && native.ValueKind == JsonValueKind.Object
                && !ConfirmedV17CurrentEvidence(text, source, image, owner, native, returned))
                return Uncertain("V17 當次政策、來源閉合或清理證據不符；保留原請求。", entered, returned, failure);
            if (identity is not null && !sourceMatches)
                failure = ShopFailure.Global("SourceMismatch", "原生 SOURCE 與服務綁定不符。", "Preflight");
            if (entered && !sourceMatches) return Uncertain("原生 SOURCE 與已進入的呼叫綁定不符。" + context, entered, returned, failure);
            ShopFailure? readFailure = null;
            bool readUnpublished = !entered && !returned && native.ValueKind == JsonValueKind.Null && cleanup && !blocked
                && sourceMatches && sources == 1 && tupleMatches && purpose == ShopNativeLogPurpose.NativeRequest && LegacyBuild(source)
                && TryUnpublishedReadFailure(text, source, image, owner, exitCode, out readFailure);
            if (readUnpublished) failure = readFailure;
            bool submitUnpublished = !entered && !returned && native.ValueKind == JsonValueKind.Null && cleanup && !blocked
                && sourceMatches && sources == 1 && tupleMatches && purpose == ShopNativeLogPurpose.NativeRequest && LegacyBuild(source)
                && ConfirmedUnpublishedSubmitPreflight(text, source, image, owner, exitCode);
            bool v14Unpublished = !entered && !returned && native.ValueKind == JsonValueKind.Null && cleanup && !blocked
                && sourceMatches && sources == 1 && tupleMatches
                && ConfirmedUnpublishedV14Preflight(text, source, image, owner, exitCode, purpose);
            bool v16InitialUnpublished = !entered && !returned && native.ValueKind == JsonValueKind.Null && cleanup && !blocked
                && sourceMatches && sources == 1 && tupleMatches && purpose == ShopNativeLogPurpose.NativeRequest
                && ConfirmedUnpublishedV16InitialSubmitPreflight(text, source, image, owner, exitCode);
            bool v16PacketCopyUnpublished = !entered && !returned && native.ValueKind == JsonValueKind.Null && cleanup && !blocked
                && sourceMatches && sources == 1 && tupleMatches && purpose == ShopNativeLogPurpose.NativeRequest
                && ConfirmedUnpublishedV16PacketCopyPreflight(text, source, image, owner, exitCode);
            bool v16InstalledUnpublished = !entered && !returned && native.ValueKind == JsonValueKind.Null && cleanup && !blocked
                && sourceMatches && sources == 1 && tupleMatches && purpose == ShopNativeLogPurpose.NativeRequest
                && ConfirmedUnpublishedV16InstalledSubmitPreflight(text, source, image, owner, exitCode);
            if (v16InstalledUnpublished)
                failure = new("NativeCodeUnavailable", "Global", "Preflight", failure?.Rva,
                    "已核對已安裝但尚未發布請求的完整清理收據（原 helper Phase=BeforePublish）；本頁確定未進入。" + failure?.Detail);
            bool v16OwnerChangedUnpublished = !entered && !returned && native.ValueKind == JsonValueKind.Null && cleanup && !blocked
                && sourceMatches && sources == 1 && tupleMatches && purpose == ShopNativeLogPurpose.NativeRequest
                && ConfirmedUnpublishedV16OwnerChangedPreflight(text, source, image, owner, exitCode);
            if (v16OwnerChangedUnpublished)
                failure = new("NativePreflightOwnerChanged", "Global", "Preflight", null,
                    "已核對完整商店 owner 變更收據：初次預檢在建立 Transport 前停止，本頁確定未發布；本次搜尋停止，可由使用者重新開始。" + failure?.Detail);
            bool v17Unpublished = !entered && !returned && native.ValueKind == JsonValueKind.Null && cleanup && !blocked
                && sourceMatches && sources == 1 && nativeVersion == 17 && purpose == ShopNativeLogPurpose.NativeRequest
                && ConfirmedUnpublishedV17Preflight(text, source, image, owner, exitCode);
            if (v17Unpublished && Rows(text).Any(r => T(r,"Kind") == "PREFLIGHT_PAGE_AB_ERROR"))
            {
                var page = V17Data(Rows(text),"PREFLIGHT_PAGE_AB_ERROR");
                failure = new("NativePreflightPageChanged", "Global", "Preflight", $"0x{N(page,"Address")-image:X}",
                    "完整原 helper 預檢、來源、未發布及清理收據已閉合；舊版 Size-only 的 bytes 相等僅為 producer 控制流推論，未聲稱獨立 hash 證據。" + failure?.Detail);
            }
            if (v17Unpublished && failure?.Phase == "BeforePublish")
                failure = new(failure.Code, "Global", "Preflight", failure.Rva, "已核對 V17 尚未發布與完整清理；原頁不重送。" + failure.Detail);
            if (v17Unpublished && failure?.Code == "ContextUnavailable")
                failure = new("NativePreflightOwnerChanged", "Global", "Preflight", null, "已核對 V17 初次預檢店主變更與未發布；原頁不重送。" + failure.Detail);
            if (!returned && failure is null) failure = ShopFailure.Unknown("原生呼叫未正常返回；helper 未提供可分類的原因。");
            bool nativeNoEntry = !entered && !returned && native.ValueKind == JsonValueKind.Object
                && sourceMatches && sources == 1 && tupleMatches && purposeMatches && policyMatches
                && native.GetProperty("Completed").GetInt32() == 1 && native.GetProperty("Claimed").GetInt32() == 1
                && native.GetProperty("ObservedPid").GetUInt32() == source.ProcessId
                && (nativeVersion is not (15 or 17) || V15NoEntryContractMatches(native)
                    && ConfirmedV15ResultReceipt(text, source, image, owner, native, final, false));
            bool guardRejected = nativeVersion == 17 && nativeNoEntry && cleanup && !blocked && exception == 0
                && ConfirmedV17GuardRejected(text, source, image, owner, native, final, exitCode);
            if (guardRejected)
                failure = new("NativeGuardRejected", "Page", "NativeGuard", null,
                    "完整 V17 收據確認 callback 在查詢前被 source_guard 拒絕，未進入查詢且已清理；守門子原因未知，本頁不重送、不代表商品回應。");
            bool unpublished = readUnpublished || submitUnpublished || v14Unpublished || v16InitialUnpublished || v16InstalledUnpublished
                || v16OwnerChangedUnpublished || v16PacketCopyUnpublished || v17Unpublished;
            bool safe = cleanup && !blocked && exception == 0 && (!entered || returned)
                && (returned && exitCode == 0 && failure is null
                    || nativeNoEntry && (failure?.Scope is "Global" or "Target") || unpublished || guardRejected);
            string detail = $"NativeEntered={entered}；NativeReturned={returned}；Exception=0x{exception:X}；CleanupConfirmed={cleanup}；SessionBlocked={blocked}；ExitCode={exitCode}。原生返回不等於清單回應。" + context;
            if (failure is not null) detail = $"{failure.Code} ({failure.Phase}{(failure.Rva is null ? "" : ", " + failure.Rva)})：{failure.Detail}；" + detail;
            // A null result requires one complete recognized producer path.
            // Unknown cleanup-only receipts retain pending, including Global errors.
            bool noEntryConfirmed = safe && (nativeNoEntry || unpublished);
            return new(entered, returned, safe, Limit(detail, 20000), failure)
                { NoEntryConfirmed = noEntryConfirmed, SourceMatched = sourceMatches, LogPurpose = purpose };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or InvalidDataException or ArgumentException)
        { return Uncertain("原生 LOG 格式無法確認：" + ex.Message + "；" + string.Join("；", errors), entered, returned, failure); }
    }

    private static bool ConfirmedUnpublishedSubmitPreflight(string text, SourceIdentity source, ulong image, ulong owner, int exitCode)
    {
        // This classifier deliberately recognizes only the observed V13 path.
        // A second VerifyFull, installed transport, another phase, missing or
        // additional record stays uncertain under the existing parser rules.
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        bool force = lines.Length == 7;
        if (exitCode != 1 || lines.Length != 6 && !force) return false;
        string[] kinds = force
            ? ["START", "FORCE_DISPATCH", "SOURCE", "SUBMIT_CODE_PREFLIGHT_RECHECK", "NATIVE_CODE_PAGE_UNAVAILABLE", "ERROR", "END"]
            : ["START", "SOURCE", "SUBMIT_CODE_PREFLIGHT_RECHECK", "NATIVE_CODE_PAGE_UNAVAILABLE", "ERROR", "END"];
        var data = new JsonElement[6];
        JsonElement? forceJournal = null;
        for (int i = 0; i < lines.Length; i++)
        {
            using var document = JsonDocument.Parse(lines[i]);
            var record = document.RootElement;
            if (!UniqueProperties(record) || Text(record, "Kind") != kinds[i]
                || record.GetProperty("Sequence").GetInt32() != i + 1
                || record.GetProperty("DiskFlush").GetBoolean() != (i >= lines.Length - 2)) return false;
            if (force && i == 1) forceJournal = record.GetProperty("Data").Clone();
            else data[i - (force && i > 1 ? 1 : 0)] = record.GetProperty("Data").Clone();
        }
        var begun = data[0]; var identity = data[1]; var proof = data[2];
        var unavailable = data[3]; var error = data[4]; var end = data[5];
        if (force && (forceJournal is not { } journal || !ValidForcePreflightJournal(journal, end, source))) return false;
        if (Text(begun, "Version") != "13.0.0" || Text(begun, "NativePayloadVersion") != "13.0.0"
            || begun.GetProperty("NativeProtocolVersion").GetInt32() != 13 || begun.GetProperty("NativeGuardContract").GetInt32() != 13
            || begun.GetProperty("SubmitPreflightRecheckMaximumMilliseconds").GetInt32() != 250
            || begun.GetProperty("MaximumNativeRequests").GetInt32() != 1 || Text(begun, "Mode") is not ("buy" or "sell")
            || begun.GetProperty("MouseMessages").GetInt32() != 0 || begun.GetProperty("KeyMessages").GetInt32() != 0
            || begun.GetProperty("TouchCalls").GetInt32() != 0 || begun.GetProperty("ForegroundChanges").GetInt32() != 0
            || begun.GetProperty("DirectSelectedOwnerWrites").GetInt32() != 0 || Text(begun, "OpenProcessAccess") != "0x410") return false;
        if (identity.GetProperty("Pid").GetInt32() != source.ProcessId || identity.GetProperty("Creation").GetInt64() != source.CreationFileTime
            || identity.GetProperty("Image").GetUInt64() != image || identity.GetProperty("TargetOwner").GetUInt64() != owner
            || Text(identity, "Sha256") != source.DiskSha256 || !string.Equals(Text(identity, "Path"), source.ImagePath, StringComparison.OrdinalIgnoreCase)
            || Rva(Text(identity, "Timestamp")) != $"0x{source.PeTimestamp:X}" || Rva(Text(identity, "ImageSize")) != $"0x{source.ImageSize:X}"
            || !identity.GetProperty("ReadOnlyProcessHandle").GetBoolean()) return false;
        // V13 checks the registered 3346-byte Submit body in logical 4 KiB slices.
        // The first slice is 2528 bytes, the tail is 818. Either can be the
        // first unavailable slice; neither is evidence that a request was sent.
        string? sliceText = Rva(Text(proof, "Rva"));
        if (sliceText is null) return false;
        uint sliceRva = uint.Parse(sliceText.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        int sliceLength = proof.GetProperty("Length").GetInt32();
        const uint submitStart = 0x27D620, submitEnd = submitStart + 3346;
        bool registeredSlice = sliceRva == submitStart && sliceLength == 0x1000 - (int)(submitStart & 0xFFF)
            || sliceRva == 0x27E000 && sliceLength == (int)(submitEnd - 0x27E000);
        if (!registeredSlice) return false;
        bool modern = Text(begun, "ManagedVersion") == "13.0.1"
            && begun.TryGetProperty("PreflightDiagnosticContract", out var diagnostic) && diagnostic.GetInt32() == 1;
        if (modern)
        {
            if (!end.TryGetProperty("PreflightNoDispatchProof", out var noDispatch)
                || noDispatch.ValueKind != JsonValueKind.Object || noDispatch.GetProperty("Contract").GetInt32() != 1
                || Text(noDispatch, "Stage") != "InitialVerify" || !noDispatch.GetProperty("SourceEstablished").GetBoolean()
                || noDispatch.GetProperty("TransportConstructionAttempted").GetBoolean()
                || noDispatch.GetProperty("DurableIntentWritten").GetBoolean()
                || noDispatch.GetProperty("RequestPublicationPossible").GetBoolean()) return false;
        }
        else if (sliceRva != submitStart || sliceLength != 2528) return false; // preserve only the known legacy path
        if (Rva(Text(proof, "FunctionRva")) != "0x27D620" || !proof.GetProperty("Attempted").GetBoolean()
            || Text(proof, "Outcome") != "BudgetExhausted" || proof.GetProperty("MaximumWaitMilliseconds").GetInt32() != 250
            || !proof.GetProperty("ReadOnly").GetBoolean() || !proof.GetProperty("RequestNotPublished").GetBoolean()
            || proof.GetProperty("ProtectionChanged").GetBoolean() || proof.GetProperty("Deferred").GetBoolean()
            || proof.GetProperty("CurrentBytesMatched").GetBoolean() || !proof.GetProperty("FullByteAndSourceProofStillRequired").GetBoolean()
            || !UnpublishedSubmitPage(proof.GetProperty("Before"), image, sliceRva, sliceLength) || !UnpublishedSubmitPage(proof.GetProperty("After"), image, sliceRva, sliceLength)) return false;
        int queries = proof.GetProperty("Queries").GetInt32();
        double elapsed = proof.GetProperty("ElapsedMilliseconds").GetDouble(), waited = proof.GetProperty("TotalWaitMilliseconds").GetDouble();
        // Scheduler overshoot is expected (the actual receipt is >250 ms).
        // The configured admission budget is fixed; elapsed time is not a proof of dispatch.
        if (queries is < 0 or > 10 || proof.GetProperty("TotalQueries").GetInt32() != queries
            || !double.IsFinite(elapsed) || elapsed < 0 || !double.IsFinite(waited) || waited < 0) return false;
        if (Rva(Text(unavailable, "Rva")) != sliceText || Text(unavailable, "Code") != "NativeCodeUnavailable"
            || Text(unavailable, "Phase") != "Preflight" || !UnpublishedSubmitPage(unavailable.GetProperty("Page"), image, sliceRva, sliceLength)
            || !JsonElement.DeepEquals(proof.GetProperty("After"), unavailable.GetProperty("Page"))) return false;
        if (Text(error, "Type") != "NativePreflightException" || Text(error, "Code") != "NativeCodeUnavailable"
            || Text(error, "Phase") != "Preflight" || Rva(Text(error, "Rva")) != sliceText) return false;
        return end.GetProperty("ExitCode").GetInt32() == 1 && end.GetProperty("NativeResult").ValueKind == JsonValueKind.Null
            && end.GetProperty("CleanupConfirmed").GetBoolean() && !end.GetProperty("SessionBlocked").GetBoolean()
            && !end.GetProperty("PostCodeConfirmed").GetBoolean() && !end.GetProperty("BorrowedSourceContractVerified").GetBoolean()
            && !end.GetProperty("NativeAlreadyReturned").GetBoolean() && !end.GetProperty("AckObserved").GetBoolean()
            && !end.GetProperty("ListReceivedVerified").GetBoolean()
            && end.GetProperty("LogRecordsBeforeEnd").GetInt32() == lines.Length - 1 && end.GetProperty("LogDiskFlushesBeforeEnd").GetInt32() == 1
            && end.GetProperty("LogStreamFlushesBeforeEnd").GetInt32() == lines.Length - 2;
    }

    private static bool ValidForcePreflightJournal(JsonElement journal, JsonElement end, SourceIdentity source)
    {
        const string root = @"D:\lineage\LC_PlayerShopListAction_v1\journal";
        string stem = $"{source.ProcessId}-{source.CreationFileTime}";
        string? previous = Text(journal, "PreviousPending"), current = Text(journal, "CurrentPending");
        if (!UniqueProperties(journal) || current is null || !string.Equals(previous,
                Path.Combine(root, stem + ".pending.json"), StringComparison.OrdinalIgnoreCase)
            || !journal.TryGetProperty("PreviousPendingPreserved", out var preserved)
            || preserved.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || journal.GetProperty("WaitForPreviousGameState").GetBoolean()) return false;
        string name = Path.GetFileName(current), prefix = stem + ".force-", suffix = ".pending.json";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)
            || name.Length != prefix.Length + 32 + suffix.Length
            || !Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out _)
            || !string.Equals(current, Path.Combine(root, name), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Text(end, "PendingJournal"), current, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool UnpublishedSubmitPage(JsonElement page, ulong image, uint sliceRva, int sliceLength)
    {
        ulong start = page.GetProperty("Base").GetUInt64(), size = page.GetProperty("Size").GetUInt64();
        ulong address = checked(image + sliceRva);
        return page.GetProperty("AllocationBase").GetUInt64() == image && page.GetProperty("State").GetUInt32() == 0x1000
            && page.GetProperty("Protect").GetUInt32() == 1 && page.GetProperty("Type").GetUInt32() == 0x1000000
            && start <= address && size >= (uint)sliceLength && address - start <= size - (uint)sliceLength;
    }

    private static bool UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !UniqueProperties(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) if (!UniqueProperties(value)) return false;
        return true;
    }

    private static ShopFailure Error(JsonElement data)
    {
        string message = Text(data, "Message") ?? "未提供錯誤訊息";
        string? code = Text(data, "Code"), rva = Rva(Text(data, "Rva"));
        string phase = Text(data, "Phase") ?? "Preflight";
        const string unavailable = "Native code is not currently readable/executable MEM_IMAGE: 0x";
        const string differs = "Current native code differs: 0x";
        if (code is null && message.StartsWith(unavailable, StringComparison.Ordinal))
        { code = "NativeCodeUnavailable"; rva = Rva("0x" + message[unavailable.Length..]); }
        if (code is null && message.StartsWith(differs, StringComparison.Ordinal))
        { code = "NativeCodeMismatch"; rva = Rva("0x" + message[differs.Length..]); }
        string scope = code switch
        {
            "NativeCodeUnavailable" or "NativeCodeMismatch" or "NativePreflightPageChanged" or "SourceMismatch" or "ContextUnavailable" => "Global",
            "TargetChanged" => "Target",
            _ => "Session"
        };
        return new(scope == "Session" ? "NativeOutcomeUnknown" : code!, scope, Limit(phase, 64), rva,
            Limit((Text(data, "Type") is { } type ? type + ": " : "") + message, 4096));
    }

    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static string? Rva(string? value) => value is not null && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && uint.TryParse(value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint n) ? $"0x{n:X}" : null;
    private static string Limit(string value, int n) => value.Length <= n ? value : value[..n] + "…";
    private static ShopNativeOutcome Uncertain(string detail, bool entered = false, bool returned = false, ShopFailure? failure = null) =>
        new(entered, returned, false, Limit(detail, 20000), failure ?? ShopFailure.Unknown(detail));
}


