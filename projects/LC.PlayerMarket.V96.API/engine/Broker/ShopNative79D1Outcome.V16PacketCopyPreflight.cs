using System.Globalization;
using System.Text.Json;

namespace MemoryHub.Service;

internal sealed partial record ShopNative79D1Outcome
{
    // The shipped 16.0.2 producer can fail its initial packet-copy page check
    // before constructing Transport. Accept only that complete, durable path.
    // This settles a failed request; it does not permit calling an unreadable page.
    private static bool ConfirmedUnpublishedV16PacketCopyPreflight(string text, SourceIdentity source,
        ulong image, ulong owner, int exitCode)
    {
        if (!ShopNativeLegacy79D1Package.SupportsBuild(source) || exitCode != 1 || owner is 0 or > int.MaxValue
            || !text.EndsWith('\n')) return false;
        string[] lines = text.Split('\n');
        bool force = lines.Length == 7;
        if (lines.Length != 6 && !force) return false;
        var rows = new List<JsonElement>();
        foreach (string line in lines[..^1])
        {
            using var document = JsonDocument.Parse(line);
            if (!UniqueProperties(document.RootElement)
                || !OwnerPreflightFields(document.RootElement, "At", "Sequence", "Kind", "DiskFlush", "Data")) return false;
            rows.Add(document.RootElement.Clone());
        }
        string[] kinds = force
            ? ["START", "FORCE_DISPATCH", "SOURCE", "NATIVE_CODE_PAGE_UNAVAILABLE", "ERROR", "END"]
            : ["START", "SOURCE", "NATIVE_CODE_PAGE_UNAVAILABLE", "ERROR", "END"];
        DateTimeOffset previousAt = DateTimeOffset.MinValue;
        for (int i = 0; i < rows.Count; i++)
        {
            if (Text(rows[i], "Kind") != kinds[i] || rows[i].GetProperty("Sequence").GetInt32() != i + 1
                || rows[i].GetProperty("DiskFlush").GetBoolean() != (i >= rows.Count - 2)
                || !DateTimeOffset.TryParse(Text(rows[i], "At"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var at) || at == default || at < previousAt) return false;
            previousAt = at;
        }
        var start = rows[0].GetProperty("Data");
        var identity = rows[force ? 2 : 1].GetProperty("Data");
        var unavailable = rows[^3].GetProperty("Data");
        var error = rows[^2].GetProperty("Data");
        var end = rows[^1].GetProperty("Data");
        if (!OwnerPreflightFields(start, "Version", "ManagedProgramVersion", "NativeProtocolVersion", "NativeGuardContract",
                "NativePayloadVersion", "PostCodeRecheckContract", "PostCodeRecheckMaximumMilliseconds", "ReturnedLifecycleRecoveryContract",
                "SubmitPreflightRecheckMaximumMilliseconds", "Mode", "MaximumNativeRequests", "MouseMessages", "KeyMessages",
                "TouchCalls", "ForegroundChanges", "DirectSelectedOwnerWrites", "SharedOwnershipCounterUpdates", "OpenProcessAccess",
                "DeferredCodePolicy", "Binding", "CurrentOwnerBits", "LogPolicy", "ResearchOnly")
            || !OwnerPreflightFields(identity, "Pid", "Creation", "Image", "Hwnd", "Tid", "Shop", "Owner", "TargetOwner",
                "Path", "Sha256", "Timestamp", "ImageSize", "ReadOnlyProcessHandle")
            || !OwnerPreflightFields(unavailable, "Rva", "Page", "Phase", "Code")
            || !OwnerPreflightFields(error, "Type", "Message", "Code", "Phase", "Rva")
            || !OwnerPreflightFields(end, "ExitCode", "PostCodeConfirmed", "EventResultDurable", "BorrowedSourceContractVerified",
                "NativeAlreadyReturned", "AckObserved", "NativeResult", "CleanupConfirmed", "SessionBlocked", "PendingJournal",
                "SingleTrialPendingJournal", "SingleTrialPendingSchema", "ListReceivedVerified", "RemoteModuleUnloadProven",
                "EntireHookTailExitProven", "LogRecordsBeforeEnd", "LogDiskFlushesBeforeEnd", "LogStreamFlushesBeforeEnd",
                "LogBufferedRecordsBeforeEnd", "LogWriteDurationMsBeforeEnd", "LogIoWriteDurationMsBeforeEnd", "LogSerializationDurationMsBeforeEnd")
            || NativeTupleVersion(start, source) != 15 || Text(start, "ManagedProgramVersion") != "16.0.2"
            || !PurposeMatches(start, ShopNativeLogPurpose.NativeRequest)
            || Text(start, "LogPolicy") != V14BufferedLogPolicy
            || start.GetProperty("PostCodeRecheckContract").GetInt32() != 1
            || start.GetProperty("PostCodeRecheckMaximumMilliseconds").GetInt32() != 1000
            || start.GetProperty("ReturnedLifecycleRecoveryContract").GetInt32() != 1
            || start.GetProperty("SubmitPreflightRecheckMaximumMilliseconds").GetInt32() != 250
            || start.GetProperty("MouseMessages").GetInt32() != 0 || start.GetProperty("KeyMessages").GetInt32() != 0
            || start.GetProperty("TouchCalls").GetInt32() != 0 || start.GetProperty("ForegroundChanges").GetInt32() != 0
            || start.GetProperty("DirectSelectedOwnerWrites").GetInt32() != 0
            || !start.GetProperty("SharedOwnershipCounterUpdates").GetBoolean()
            || Text(start, "OpenProcessAccess") != "0x410" || start.GetProperty("CurrentOwnerBits").GetInt32() != 64
            || !start.GetProperty("ResearchOnly").GetBoolean()
            || Text(start, "Binding") != "Caller-supplied runtime identity, independently verified"
            || Text(start, "DeferredCodePolicy") != ShopNativeLegacy79D1Package.LegacyStrictV16Policy
            || !NativeSourceMatches(identity, source, image, owner)) return false;
        ulong window = identity.GetProperty("Hwnd").GetUInt64(), shop = identity.GetProperty("Shop").GetUInt64();
        _ = identity.GetProperty("Owner").GetUInt64();
        if (identity.GetProperty("Tid").GetUInt32() == 0 || window < 0x10000 || window > 0x7FFFFFFFFFFFUL
            || shop < 0x10000 || (shop & 7) != 0 || shop > 0x7FFFFFFFFFFFUL - 0x2C8) return false;
        if (!V15JournalContractMatches(end, source)) return false;
        if (force)
        {
            var journal = rows[1].GetProperty("Data");
            if (!OwnerPreflightFields(journal, "PreviousPending", "PreviousPendingPreserved", "CurrentPending", "WaitForPreviousGameState")
                || !ValidForcePreflightJournal(journal, end, source)) return false;
        }
        else if (!string.Equals(Text(end, "PendingJournal"), Path.Combine(@"D:\lineage\LC_PlayerShopListAction_v1\journal",
            $"{source.ProcessId}-{source.CreationFileTime}.pending.json"), StringComparison.OrdinalIgnoreCase)) return false;
        var page = unavailable.GetProperty("Page");
        if (!OwnerPreflightFields(page, "Base", "AllocationBase", "AllocationProtect", "PartitionId", "Size", "State", "Protect", "Type")
            || Rva(Text(unavailable, "Rva")) != "0x7710F0" || Text(unavailable, "Code") != "NativeCodeUnavailable"
            || Text(unavailable, "Phase") != "Preflight" || Text(error, "Type") != "NativePreflightException"
            || Text(error, "Code") != "NativeCodeUnavailable" || Text(error, "Phase") != "Preflight"
            || Rva(Text(error, "Rva")) != "0x7710F0"
            || Text(error, "Message") != "Native code is not currently readable/executable MEM_IMAGE: 0x7710F0"
            || !UnpublishedSubmitPage(page, image, 0x7710F0, 67)
            || page.GetProperty("PartitionId").GetUInt32() > ushort.MaxValue) return false;
        _ = page.GetProperty("AllocationProtect").GetUInt32();
        return end.GetProperty("ExitCode").GetInt32() == 1 && end.GetProperty("NativeResult").ValueKind == JsonValueKind.Null
            && end.GetProperty("CleanupConfirmed").GetBoolean() && !end.GetProperty("SessionBlocked").GetBoolean()
            && !end.GetProperty("PostCodeConfirmed").GetBoolean() && !end.GetProperty("EventResultDurable").GetBoolean()
            && !end.GetProperty("BorrowedSourceContractVerified").GetBoolean() && !end.GetProperty("NativeAlreadyReturned").GetBoolean()
            && !end.GetProperty("AckObserved").GetBoolean() && !end.GetProperty("ListReceivedVerified").GetBoolean()
            && !end.GetProperty("RemoteModuleUnloadProven").GetBoolean() && !end.GetProperty("EntireHookTailExitProven").GetBoolean()
            && V14FlushCountersMatch(start, end, rows.Count);
    }
}
