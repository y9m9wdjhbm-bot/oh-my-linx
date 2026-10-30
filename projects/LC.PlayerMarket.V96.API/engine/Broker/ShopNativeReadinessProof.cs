using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MemoryHub.Service;

/// <summary>Accepts exact V16 strict or V17 certified zero-request preflight receipts.</summary>
internal static class ShopNativeReadinessProof
{
    internal const int MaximumLogBytes = 1024 * 1024;
    internal const string CodeUnavailableDetail = "商店查詢入口目前無法讀取或執行；本次未送出查詢，原進度與已讀商品保留。先前檢查成功也不代表目前可用，請按「重新檢查商店入口」取得新的完整預檢結果。";
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "START", "SOURCE", "PACKET_CODE_PENDING", "SUBMIT_CODE_PREFLIGHT_RECHECK", "CURRENT_NATIVE_CODE_GUARDS",
        "NONINVOKED_CODE_OBSERVATION", "NETWORK_SHARED_COUNTS_SAMPLE", "NETWORK_SHARED_COUNTS_AB",
        "PREFLIGHT_AB_CLOSED", "READ_ONLY_PREFLIGHT_PASSED", "NATIVE_CODE_PAGE_UNAVAILABLE",
        "CURRENT_SHOP_OWNER_CHANGED", "PREFLIGHT_PROOF_AB_ERROR", "PREFLIGHT_PAGE_AB_ERROR",
        "NETWORK_SHARED_COUNTS_ERROR", "ERROR", "END"
    };

    internal static ShopNativeReadiness Unavailable(SourceIdentity? source, string probeId, string code, string detail, string? rva = null) => new()
    {
        BuildCompatible = source is not null && ShopNativeHistoricalPackage.SupportsBuild(source), Source = source,
        ProbeId = probeId, Code = code, Detail = detail.Length <= 1600 ? detail : detail[..1600], FailureRva = rva
    };

    internal static ShopNativeReadiness Parse(string text, SourceIdentity expected, ulong hwnd, ulong shop, ulong owner,
        int actualExitCode, DateTimeOffset launchedAt, DateTimeOffset now, string probeId)
    {
        if (ShopNativePackage.SupportsBuild(expected)) return ShopNativeB47CReadinessProof.Parse(text,expected,hwnd,shop,owner,actualExitCode,launchedAt,now,probeId);
        if (ShopNativeLegacy79D1Package.SupportsBuild(expected)) return ShopNative79D1ReadinessProof.Parse(text,expected,hwnd,shop,owner,actualExitCode,launchedAt,now,probeId);
        try { return ParseStrict(text, expected, hwnd, shop, owner, actualExitCode, launchedAt, now, probeId); }
        catch (Exception error) when (error is JsonException or InvalidDataException or FormatException or OverflowException or ArgumentException)
        { return Unavailable(expected, probeId, "NativeReadinessUnknown", "唯讀檢查收據不完整或不一致；沒有授予搜尋資格。" + error.Message); }
    }

    private static ShopNativeReadiness ParseStrict(string text, SourceIdentity expected, ulong hwnd, ulong shop, ulong owner,
        int actualExitCode, DateTimeOffset launchedAt, DateTimeOffset now, string probeId)
    {
        Need(ShopNativeHistoricalPackage.SupportsBuild(expected), "Unsupported build");
        Need(Encoding.UTF8.GetByteCount(text) is > 0 and <= MaximumLogBytes, "Log byte limit");
        Need(text.EndsWith('\n'), "Truncated final log record");
        var rows = new List<JsonElement>();
        foreach (string line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Need(rows.Count < 1024, "Log row limit");
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            Unique(document.RootElement);
            rows.Add(document.RootElement.Clone());
        }
        Need(rows.Count >= 3, "Missing log envelope");
        DateTimeOffset previous = launchedAt;
        for (int index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            Need(N(row, "Sequence") == (ulong)index + 1 && Allowed.Contains(T(row, "Kind")), "Sequence or event not readonly");
            Need(F(row, "Data").ValueKind == JsonValueKind.Object, "Invalid event data");
            Need(B(row, "DiskFlush") == (T(row, "Kind") == "END" || T(row, "Kind").Contains("ERROR", StringComparison.Ordinal)), "Wrong durability policy");
            var at = Time(row, "At"); Need(at >= previous && at <= now, "Noncurrent timestamp"); previous = at;
        }
        Need(T(rows[0], "Kind") == "START" && T(rows[^1], "Kind") == "END", "Wrong envelope order");
        JsonElement Row(string kind)
        {
            var selected = rows.Where(r => T(r, "Kind") == kind).ToArray();
            Need(selected.Length == 1, "Missing/duplicate " + kind); return selected[0];
        }
        JsonElement Data(string kind) => F(Row(kind), "Data");
        var start = Data("START"); var end = Data("END");
        bool certified = ShopNativeV17Contract.IsProducer(start, expected);
        bool legacy = T(start, "Version") == "15.0.0" && T(start, "NativePayloadVersion") == "15.0.0"
            && T(start, "ManagedProgramVersion") == "16.0.2" && N(start, "NativeProtocolVersion") == 15
            && N(start, "NativeGuardContract") == 15 && T(start, "DeferredCodePolicy") == ShopNativeHistoricalPackage.LegacyStrictV16Policy;
        Need((certified || legacy) && T(start, "LogPolicy") == "DurableDispatchIntent_Result_End_Error;BufferedVerboseV2"
            && T(start, "Mode") == "--preflight" && N(start, "CurrentOwnerBits") == 64
            && T(start, "OpenProcessAccess") == "0x410", "Wrong readonly helper contract");
        Need(!certified || ShopNativeV17Contract.TerminalMatches(start,end), "Terminal producer/cold policy differs");
        Need(certified || !rows.Any(r=>T(r,"Kind")=="PACKET_CODE_PENDING"), "Legacy cannot defer current code");
        foreach (string key in new[] { "MaximumNativeRequests", "MouseMessages", "KeyMessages", "TouchCalls", "ForegroundChanges", "DirectSelectedOwnerWrites" }) Need(N(start, key) == 0, "Nonzero " + key);
        Need(N(end, "ExitCode") == (ulong)actualExitCode && actualExitCode is 0 or 1
            && N(end, "LogRecordsBeforeEnd") == (ulong)rows.Count - 1 && B(end, "CleanupConfirmed")
            && B(rows[^1], "DiskFlush"), "Incomplete terminal receipt");
        ulong flushes = (ulong)rows.Take(rows.Count - 1).Count(r => B(r, "DiskFlush"));
        Need(N(end, "LogDiskFlushesBeforeEnd") == flushes && N(end, "LogStreamFlushesBeforeEnd") == 0
            && N(end, "LogBufferedRecordsBeforeEnd") == (ulong)rows.Count - 1 - flushes, "Terminal durability counters");
        foreach (string key in new[] { "PostCodeConfirmed", "EventResultDurable", "BorrowedSourceContractVerified", "NativeAlreadyReturned", "AckObserved", "ListReceivedVerified" }) Need(!B(end, key), "Unexpected native result " + key);
        Need(F(end, "NativeResult").ValueKind == JsonValueKind.Null, "Unexpected native result");
        if (B(end, "SessionBlocked")) return Unavailable(expected, probeId, "NativeReadinessBusy", "仍有未結清的原生紀錄；唯讀檢查不清除或繞過原紀錄。");
        var source = Data("SOURCE"); ulong image = Image(expected);
        Need(N(source, "Pid") == (ulong)expected.ProcessId && N(source, "Creation") == (ulong)expected.CreationFileTime
            && N(source, "Image") == image && N(source, "Hwnd") == hwnd && N(source, "Tid") > 0
            && N(source, "Shop") == shop && N(source, "Owner") == owner && N(source, "TargetOwner") == 0
            && string.Equals(T(source, "Path"), expected.ImagePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(T(source, "Sha256"), expected.DiskSha256, StringComparison.OrdinalIgnoreCase)
            && Hex(T(source, "Timestamp")) == expected.PeTimestamp && Hex(T(source, "ImageSize")) == expected.ImageSize
            && B(source, "ReadOnlyProcessHandle"), "Source does not match fresh binding");
        Need(N(Row("SOURCE"), "Sequence") > 1 && N(Row("SOURCE"), "Sequence") < N(rows[^1], "Sequence"), "Source order");
        foreach (var row in rows.Where(r => T(r, "Kind") == "NONINVOKED_CODE_OBSERVATION"))
        {
            var data = F(row, "Data");
            Need(Hex(T(data, "Rva")) is 0x73B790 or 0x104870 && !B(data, "DispatchPrerequisite")
                && !B(data, "BodyCallableClaimed") && !B(data, "HistoricalBytesSubstituted"), "Noninvoked observation changed policy");
            // Their pages may be protected; these bodies are not invoked by this helper.
        }
        foreach (var row in rows.Where(r => T(r, "Kind") == "SUBMIT_CODE_PREFLIGHT_RECHECK"))
        {
            var recheck = F(row, "Data");
            Need(B(recheck, "ReadOnly") && B(recheck, "RequestNotPublished") && !B(recheck, "ProtectionChanged")
                && !B(recheck, "Deferred") && !B(recheck, "CurrentBytesMatched") && B(recheck, "FullByteAndSourceProofStillRequired")
                && N(recheck, "MaximumWaitMilliseconds") == 250, "Submit recheck changed readonly contract");
        }
        if (certified) _ = ShopNativeV17Contract.PendingMask(rows, image);
        var errors = rows.Where(r => T(r, "Kind") == "ERROR").ToArray();
        if (errors.Length != 0)
        {
            Need(errors.Length == 1 && actualExitCode == 1 && !rows.Any(r => T(r, "Kind") is "PREFLIGHT_AB_CLOSED" or "READ_ONLY_PREFLIGHT_PASSED"), "Contradictory failure");
            var error = F(errors[0], "Data"); string code = T(error, "Code"), rva = T(error, "Rva");
            Need(T(error, "Type") == "NativePreflightException" && T(error, "Phase") == "Preflight", "Unclassified preflight failure");
            if (code == "NativePreflightPageChanged")
            {
                Need(certified && ShopNativeOutcome.V17ReadOnlyPageAbRefusal(rows,expected,image),"Unclosed page refusal");
                return Unavailable(expected,probeId,code,"目前程式頁的當次範圍證據已變更；完整唯讀收據確認未送出查詢，等待重新檢查。",rva);
            }
            if (code == "NativeCodeUnavailable")
            {
                var unavailable = Data("NATIVE_CODE_PAGE_UNAVAILABLE"); var page = F(unavailable, "Page");
                Need(T(unavailable, "Code") == code && T(unavailable, "Phase") == "Preflight" && Hex(T(unavailable, "Rva")) == Hex(rva), "Failure RVA mismatch");
                Need(N(Row("NATIVE_CODE_PAGE_UNAVAILABLE"), "Sequence") > N(Row("SOURCE"), "Sequence")
                    && N(Row("NATIVE_CODE_PAGE_UNAVAILABLE"), "Sequence") < N(errors[0], "Sequence"), "Failure order");
                ulong address = checked(image + Hex(rva)), pageBase = N(page, "Base"), size = N(page, "Size");
                Need(size > 0 && pageBase <= address && address - pageBase < size, "Failure page range");
                // PAGE_NOACCESS is a current observation, not proof of first-time initialization.
                // A previously readable body may become unavailable again; never reuse an earlier pass.
                bool copyNoAccess = Hex(rva) == 0x76BEB0 && N(page, "AllocationBase") == image
                    && N(page, "State") == 0x1000 && N(page, "Type") == 0x1000000 && N(page, "Protect") == 1;
                return Unavailable(expected, probeId, code,
                    copyNoAccess ? CodeUnavailableDetail : "目前商店程式頁不可讀／不可執行；本次未送出查詢，原進度與已讀商品保留。" + T(error, "Message"), rva);
            }
            Need(code == "NativeCodeMismatch", "Unknown failure classification");
            return Unavailable(expected, probeId, code, "目前程式碼與已核對版本不符；未送出查詢，需重新核對相容性。", rva);
        }
        Need(actualExitCode == 0 && !rows.Any(r => T(r, "Kind") is "NATIVE_CODE_PAGE_UNAVAILABLE" or "CURRENT_SHOP_OWNER_CHANGED" or "PREFLIGHT_PROOF_AB_ERROR" or "PREFLIGHT_PAGE_AB_ERROR" or "NETWORK_SHARED_COUNTS_ERROR"), "Contradictory positive receipt");
        var guardRow = Row("CURRENT_NATIVE_CODE_GUARDS"); var guard = F(guardRow, "Data");
        ulong pendingMask = certified ? ShopNativeV17Contract.Guard(guard, rows, image) : 0;
        Need(certified || (N(guard, "Bodies") == 5 && N(guard, "Bytes") == 4262 && N(guard, "DeferredMask") == 0
            && N(guard, "DeferredCodeBytes") == 0 && !B(guard, "CurrentPendingBytesClaimedMatched")
            && B(guard, "BorrowedScalarSource") && N(guard, "AdditionalDestructorCalls") == 0), "Incomplete synchronous body proof");
        var closedRow = Row("PREFLIGHT_AB_CLOSED"); var closed = F(closedRow, "Data");
        Need(N(closed, "Owner64") == owner && N(closed, "CurrentShop") == shop && N(closed, "TargetOwner") == 0
            && N(closed, "PacketCreate") == checked(image + 0x72D300) && N(closed, "PacketCopy") == checked(image + 0x76BEB0)
            && N(closed, "ProofCount") > 0 && N(closed, "ReadCalls") is > 0 and <= 8192
            && N(closed, "ReadBytes") is > 0 and <= 4 * 1024 * 1024 && N(closed, "SubsystemCount") is > 0 and <= 256
            && N(closed, "DeferredMask") == pendingMask && N(closed, "DeferredCodeBytes") == (ulong)ShopNativeV17Contract.PendingBytes(pendingMask)
            && B(closed, "EverySynchronousGuardBodyCurrentlyMatched") == (pendingMask == 0)
            && B(closed, "ColdCodeBytesPending") == (pendingMask != 0)
            && B(closed, "AsyncAckNotInvokedByHelper"), "Incomplete source/code A/B closure");
        if (certified) ShopNativeV17Contract.Closed(closed, image, shop, owner, 0, pendingMask);
        if (certified && ShopNativeV17Contract.NewRangeProducer(start))
            ShopNativeV17Contract.ClosedRequestedRanges(closed,image,pendingMask,"BeforeTransport");
        Need(rows.Where(r=>T(r,"Kind")=="PACKET_CODE_PENDING").All(r=>N(r,"Sequence")>N(Row("SOURCE"),"Sequence") && N(r,"Sequence")<N(guardRow,"Sequence")), "Pending order");
        var samples = rows.Where(r => T(r, "Kind") == "NETWORK_SHARED_COUNTS_SAMPLE").ToArray();
        Need(samples.Length == 2, "Missing ownership samples");
        var countA = F(samples[0], "Data"); var countB = F(samples[1], "Data");
        ulong control = N(closed, "NetworkControl"); Need(control > 0 && N(closed, "Network") > 0 && N(closed, "NetworkChild") > 0, "Missing network binding");
        bool EqualCounts(JsonElement left, JsonElement right) => N(left, "Strong") == N(right, "Strong") && N(left, "Weak") == N(right, "Weak");
        foreach (var item in new[] { (Data: countA, Phase: "A"), (Data: countB, Phase: "B") })
        {
            var counts = F(item.Data, "Counts");
            Need(T(item.Data, "Phase") == item.Phase && N(item.Data, "NetworkControl") == control
                && Hex(T(item.Data, "AddressHex")) == checked(control + 8) && N(item.Data, "Length") == 8
                && B(item.Data, "Alive") && !B(item.Data, "CountEqualityRequired")
                && N(counts, "Strong") is > 0 and < int.MaxValue && N(counts, "Weak") is > 0 and < int.MaxValue, "Ownership sample mismatch");
        }
        var countAbRow = Row("NETWORK_SHARED_COUNTS_AB"); var countAb = F(countAbRow, "Data");
        Need(N(countAb, "NetworkControl") == control && Hex(T(countAb, "CountsAddress")) == checked(control + 8)
            && N(countAb, "Length") == 8 && B(countAb, "StableControlVtableAbClosed") && !B(countAb, "CountEqualityRequired")
            && !B(closed, "CountEqualityRequired") && EqualCounts(F(countA,"Counts"),F(countAb,"OwnCountsA"))
            && EqualCounts(F(countB,"Counts"),F(countAb,"OwnCountsB")) && EqualCounts(F(countA,"Counts"),F(closed,"OwnCountsA"))
            && EqualCounts(F(countB,"Counts"),F(closed,"OwnCountsB"))
            && B(countAb,"CountsChanged") == !EqualCounts(F(countA,"Counts"),F(countB,"Counts")), "Ownership A/B closure mismatch");
        Need(N(guardRow,"Sequence") < N(samples[0],"Sequence") && N(samples[0],"Sequence") < N(samples[1],"Sequence")
            && N(samples[1],"Sequence") < N(countAbRow,"Sequence") && N(countAbRow,"Sequence") < N(closedRow,"Sequence"), "Ownership proof order");
        var passedRow = Row("READ_ONLY_PREFLIGHT_PASSED"); var passed = F(passedRow, "Data");
        Need(N(passed, "NativeRequests") == 0 && N(passed, "HooksInstalled") == 0 && N(passed, "DeferredMask") == pendingMask
            && !B(passed, "NativeCallableVerified") && !B(passed, "LiveShopQueryVerified"), "Wrong readonly pass flags");
        Need(!certified || ShopNativeV17Contract.Certificate(passed) && B(passed,"CurrentBytesMatched") == (pendingMask == 0)
            && !B(passed,"CurrentPendingBytesClaimedMatched"), "Readonly cold pass differs");
        Need(N(Row("SOURCE"), "Sequence") < N(guardRow, "Sequence") && N(guardRow, "Sequence") < N(closedRow, "Sequence")
            && N(closedRow, "Sequence") < N(passedRow, "Sequence") && N(passedRow, "Sequence") < N(rows[^1], "Sequence"), "Positive proof order");
        DateTimeOffset sampledAt = Time(closedRow, "At");
        var result = new ShopNativeReadiness { BuildCompatible = true, Ready = true, Code = pendingMask == 0 ? "NativeReady" : "NativeColdPolicyReady",
            PendingMask = (uint)pendingMask, CurrentRuntimeCodeVerified = pendingMask == 0,
            EvidenceMode = pendingMask == 0 ? "CurrentRuntimeCode" : ShopNativeColdPolicy.EvidenceMode,
            ColdCodePolicyId = certified ? ShopNativeColdPolicy.Id : "",
            ColdCodeCertificateSha256 = certified ? ShopNativeColdPolicy.CertificateSha256 : "",
            Detail = pendingMask == 0 ? "商店入口的目前來源與程式碼已通過唯讀檢查；開始時仍須核對原工作、來源與每頁預檢。"
                : $"目前來源、metadata 與其餘入口已閉合；Create／Copy 冷頁符合已驗證政策（待核對 mask={pendingMask}），未宣稱冷頁目前位元組相符。每頁返回後仍須完整程式碼與清理核對；不明結果保留原請求，不重送。",
            Source = expected, ProbeId = probeId, SampledAt = sampledAt, ExpiresAt = sampledAt.AddMilliseconds(HubProtocol.FreshMilliseconds) };
        return result.Fresh(now) ? result : Unavailable(expected, probeId, "NativeReadinessUnknown", "唯讀預檢已過期；請重新檢查，未使用舊結果開始。");
    }

    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { Need(names.Add(property.Name), "Duplicate JSON property"); Unique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    internal static ulong Image(SourceIdentity source) => Hex(source.ImageBase);
    private static ulong Hex(string value) => ulong.Parse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private static JsonElement F(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var result) ? result : throw new InvalidDataException("Missing " + key);
    private static string T(JsonElement value, string key) { var item = F(value,key); Need(item.ValueKind == JsonValueKind.String, "Not text " + key); return item.GetString()!; }
    private static ulong N(JsonElement value, string key) { var item = F(value,key); Need(item.ValueKind == JsonValueKind.Number && item.TryGetUInt64(out _), "Not unsigned integer " + key); return item.GetUInt64(); }
    private static bool B(JsonElement value, string key) { var item = F(value,key); Need(item.ValueKind is JsonValueKind.True or JsonValueKind.False, "Not Boolean " + key); return item.GetBoolean(); }
    private static DateTimeOffset Time(JsonElement value, string key) { Need(DateTimeOffset.TryParse(T(value,key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result), "Invalid timestamp"); return result; }
    private static void Need(bool value, string detail) { if (!value) throw new InvalidDataException(detail); }
}
