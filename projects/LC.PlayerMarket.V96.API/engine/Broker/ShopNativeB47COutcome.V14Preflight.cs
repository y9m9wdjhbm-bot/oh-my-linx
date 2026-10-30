using System.Text.Json;
namespace MemoryHub.Service;


internal sealed partial record ShopNativeB47COutcome
{
    private const string LegacyLcSha = "9FFC24FE304AACBB98642363F7B19FD4610B12D1FD0CB09A30C0C944F170B7AF";
    internal bool SourceMatched { get; init; }
    internal ShopNativeLogPurpose LogPurpose { get; init; }
    private const string V14LegacyLogPolicy = "DurableDispatchIntent_Result_End_Error;VerboseStreamFlush";
    private const string V14BufferedLogPolicy = "DurableDispatchIntent_Result_End_Error;BufferedVerboseV2";
    private static bool V14LogPolicyKnown(JsonElement start) => Text(start, "LogPolicy") is V14LegacyLogPolicy or V14BufferedLogPolicy;

    private static bool V14FlushCountersMatch(JsonElement start, JsonElement end, int records)
    {
        if (end.GetProperty("LogRecordsBeforeEnd").GetInt32() != records - 1
            || end.GetProperty("LogDiskFlushesBeforeEnd").GetInt32() != 1) return false;
        if (Text(start, "LogPolicy") == V14LegacyLogPolicy)
            return end.GetProperty("LogStreamFlushesBeforeEnd").GetInt32() == records - 2
                && !end.TryGetProperty("LogBufferedRecordsBeforeEnd", out _)
                && !end.TryGetProperty("LogIoWriteDurationMsBeforeEnd", out _)
                && !end.TryGetProperty("LogSerializationDurationMsBeforeEnd", out _);
        if (Text(start, "LogPolicy") != V14BufferedLogPolicy) return false;
        double io = end.GetProperty("LogIoWriteDurationMsBeforeEnd").GetDouble();
        double serialization = end.GetProperty("LogSerializationDurationMsBeforeEnd").GetDouble();
        double total = end.GetProperty("LogWriteDurationMsBeforeEnd").GetDouble();
        return end.GetProperty("LogStreamFlushesBeforeEnd").GetInt32() == 0
            && end.GetProperty("LogBufferedRecordsBeforeEnd").GetInt32() == records - 2
            && double.IsFinite(io) && io >= 0 && double.IsFinite(serialization) && serialization >= 0
            && double.IsFinite(total) && total >= 0 && total == io + serialization;
    }
    private static bool LegacyBuild(SourceIdentity source) => false;

    private static int NativeTupleVersion(JsonElement start, SourceIdentity source) => ShopNativeB47CContract.IsProducer(start, source) ? 17 : 0;
    private static bool NativeTupleMatches(JsonElement start, SourceIdentity source) => NativeTupleVersion(start, source) != 0;
    private static bool NativeLogPolicyMatches(JsonElement start, SourceIdentity source) => NativeTupleVersion(start, source) switch
    {
        13 => true, // Preserve the historical V13 return contract.
        14 => V14LogPolicyKnown(start),
        15 or 17 => Text(start, "LogPolicy") == V14BufferedLogPolicy,
        _ => false
    };
    private static uint MaximumDeferredMask(int nativeVersion) => 0u;

    private static bool PurposeMatches(JsonElement start, ShopNativeLogPurpose purpose) =>
        purpose == ShopNativeLogPurpose.ReadOnlyPreflight
            ? Text(start, "Mode") == "--preflight" && start.GetProperty("MaximumNativeRequests").GetInt32() == 0
            : (Text(start, "Mode") is "buy" or "sell") && start.GetProperty("MaximumNativeRequests").GetInt32() == 1;

    private static bool NativeSourceMatches(JsonElement identity, SourceIdentity source, ulong image, ulong owner)
    {
        if (identity.GetProperty("Pid").GetInt32() != source.ProcessId
            || identity.GetProperty("Creation").GetInt64() != source.CreationFileTime
            || identity.GetProperty("Image").GetUInt64() != image
            || !string.Equals(Text(identity, "Sha256"), source.DiskSha256, StringComparison.OrdinalIgnoreCase)
            || identity.GetProperty("TargetOwner").GetUInt64() != owner) return false;
        // Historical V13 return fixtures retain their original compact SOURCE.
        if (!ShopNativePackage.SupportsBuild(source)) return true;
        return string.Equals(Text(identity, "Path"), source.ImagePath, StringComparison.OrdinalIgnoreCase)
            && Rva(Text(identity, "Timestamp")) == $"0x{source.PeTimestamp:X}"
            && Rva(Text(identity, "ImageSize")) == $"0x{source.ImageSize:X}"
            && identity.GetProperty("ReadOnlyProcessHandle").GetBoolean();
    }

    private static bool ConfirmedUnpublishedV14Preflight(string text, SourceIdentity source, ulong image, ulong owner,
        int exitCode, ShopNativeLogPurpose purpose)
    {
        if (!ShopNativePackage.SupportsBuild(source) || exitCode != 1) return false;
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        bool force = lines.Length == 6;
        if (lines.Length != 5 && !force) return false;
        string[] kinds = force
            ? ["START", "FORCE_DISPATCH", "SOURCE", "NATIVE_CODE_PAGE_UNAVAILABLE", "ERROR", "END"]
            : ["START", "SOURCE", "NATIVE_CODE_PAGE_UNAVAILABLE", "ERROR", "END"];
        var data = new JsonElement[5]; JsonElement? journal = null;
        for (int i = 0; i < lines.Length; i++)
        {
            using var doc = JsonDocument.Parse(lines[i]); var row = doc.RootElement;
            if (!UniqueProperties(row) || Text(row, "Kind") != kinds[i]
                || row.GetProperty("Sequence").GetInt32() != i + 1
                || row.GetProperty("DiskFlush").GetBoolean() != (i >= lines.Length - 2)) return false;
            if (force && i == 1) journal = row.GetProperty("Data").Clone();
            else data[i - (force && i > 1 ? 1 : 0)] = row.GetProperty("Data").Clone();
        }
        var start = data[0]; var identity = data[1]; var unavailable = data[2]; var error = data[3]; var end = data[4];
        if (NativeTupleVersion(start, source) != 14 || !PurposeMatches(start, purpose)
            || start.GetProperty("SubmitPreflightRecheckMaximumMilliseconds").GetInt32() != 250
            || start.GetProperty("MouseMessages").GetInt32() != 0 || start.GetProperty("KeyMessages").GetInt32() != 0
            || start.GetProperty("TouchCalls").GetInt32() != 0 || start.GetProperty("ForegroundChanges").GetInt32() != 0
            || start.GetProperty("DirectSelectedOwnerWrites").GetInt32() != 0 || Text(start, "OpenProcessAccess") != "0x410"
            || !NativeSourceMatches(identity, source, image, owner)) return false;
        if (force)
        { if (journal is not { } value || !ValidForcePreflightJournal(value, end, source)) return false; }
        else if (!string.Equals(Text(end, "PendingJournal"), Path.Combine(@"D:\lineage\LC_PlayerShopListAction_v1\journal",
            $"{source.ProcessId}-{source.CreationFileTime}.pending.json"), StringComparison.OrdinalIgnoreCase)) return false;
        string? rva = Rva(Text(unavailable, "Rva"));
        int length = rva switch { "0x3FDC0" => 209, "0x28C630" => 2512, "0x28D000" => 834,
            "0x27CA10" => 549, "0x73A0C0" => 91, "0x779760" => 67, _ => 0 };
        if (length == 0 || Text(unavailable, "Code") != "NativeCodeUnavailable" || Text(unavailable, "Phase") != "Preflight"
            || !UnpublishedSubmitPage(unavailable.GetProperty("Page"), image, Convert.ToUInt32(rva![2..], 16), length)
            || Text(error, "Type") != "NativePreflightException" || Text(error, "Code") != "NativeCodeUnavailable"
            || Text(error, "Phase") != "Preflight" || Rva(Text(error, "Rva")) != rva
            || Text(error, "Message") != "Native code is not currently readable/executable MEM_IMAGE: " + rva) return false;
        return end.GetProperty("ExitCode").GetInt32() == 1 && end.GetProperty("NativeResult").ValueKind == JsonValueKind.Null
            && end.GetProperty("CleanupConfirmed").GetBoolean() && !end.GetProperty("SessionBlocked").GetBoolean()
            && !end.GetProperty("PostCodeConfirmed").GetBoolean() && !end.GetProperty("BorrowedSourceContractVerified").GetBoolean()
            && !end.GetProperty("NativeAlreadyReturned").GetBoolean() && !end.GetProperty("AckObserved").GetBoolean()
            && !end.GetProperty("ListReceivedVerified").GetBoolean() && !end.GetProperty("RemoteModuleUnloadProven").GetBoolean()
            && !end.GetProperty("EntireHookTailExitProven").GetBoolean()
            && V14FlushCountersMatch(start, end, lines.Length);
    }
}
