using System.Globalization;
using System.Text.Json;

namespace MemoryHub.Service;

internal sealed partial record ShopNativeB47COutcome
{
    // V16.0.2 Program: first VerifyFull -> Install -> second VerifyFull fails.
    // Program has not written either intent or called RunOnceAsync. A complete
    // zero native state and the complete cleanup tail corroborate that path.
    // This classifies only this receipt; it never reads or clears old journals.
    private static bool ConfirmedUnpublishedV16InstalledSubmitPreflight(string text, SourceIdentity source,
        ulong image, ulong owner, int exitCode)
    {
        if (!ShopNativePackage.SupportsBuild(source) || exitCode != 1 || !text.EndsWith('\n')) return false;
        string[] lines = text.Split('\n');
        string[] kinds = ["START", "FORCE_DISPATCH", "SOURCE", "CURRENT_NATIVE_CODE_GUARDS",
            "NONINVOKED_CODE_OBSERVATION", "NONINVOKED_CODE_OBSERVATION", "NETWORK_SHARED_COUNTS_SAMPLE",
            "NETWORK_SHARED_COUNTS_SAMPLE", "NETWORK_SHARED_COUNTS_AB", "PREFLIGHT_AB_CLOSED", "HOOK_INSTALL_INTENT",
            "NATIVE_PAYLOAD", "HOOK_INSTALL_RESULT", "SUBMIT_CODE_PREFLIGHT_RECHECK", "NATIVE_CODE_PAGE_UNAVAILABLE",
            "ERROR", "NATIVE_FINAL_STATE", "UNHOOK", "LOCAL_VIEW_RELEASE", "LOCAL_MAPPING_CLOSE",
            "LOCAL_LIBRARY_FREE", "CLEANUP", "END"];
        if (lines.Length != kinds.Length + 1) return false;
        var data = new JsonElement[kinds.Length];
        DateTimeOffset prior = DateTimeOffset.MinValue;
        for (int i = 0; i < kinds.Length; i++)
        {
            using var document = JsonDocument.Parse(lines[i]); var row = document.RootElement;
            if (!UniqueProperties(row) || row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 5
                || Text(row, "Kind") != kinds[i] || row.GetProperty("Sequence").GetInt32() != i + 1
                || row.GetProperty("DiskFlush").GetBoolean() != (i is 15 or 22)
                || !DateTimeOffset.TryParse(Text(row, "At"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var at) || at < prior) return false;
            prior = at;
            data[i] = row.GetProperty("Data").Clone();
            if (data[i].ValueKind != JsonValueKind.Object) return false;
        }

        var start = data[0]; var identity = data[2]; var guard = data[3]; var preflight = data[9];
        var hook = data[10]; var payload = data[11]; var installed = data[12]; var recheck = data[13];
        var unavailable = data[14]; var error = data[15]; var final = data[16]; var cleanup = data[21]; var end = data[22];
        if (NativeTupleVersion(start, source) != 15 || Text(start, "ManagedProgramVersion") != "16.0.2"
            || !PurposeMatches(start, ShopNativeLogPurpose.NativeRequest) || Text(start, "LogPolicy") != V14BufferedLogPolicy
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
            || Text(start, "DeferredCodePolicy") != ShopNativePackage.LegacyStrictV16Policy
            || !NativeSourceMatches(identity, source, image, owner)) return false;
        ulong hwnd = identity.GetProperty("Hwnd").GetUInt64(), shop = identity.GetProperty("Shop").GetUInt64();
        ulong currentOwner = identity.GetProperty("Owner").GetUInt64();
        uint tid = identity.GetProperty("Tid").GetUInt32();
        if (owner is 0 or > int.MaxValue || tid == 0 || hwnd < 0x10000 || hwnd > 0x7FFFFFFFFFFFUL
            || shop < 0x10000 || (shop & 7) != 0 || shop > 0x7FFFFFFFFFFFUL - 0x2C8
            || !ValidForcePreflightJournal(data[1], end, source) || !V15JournalContractMatches(end, source)) return false;

        if (guard.GetProperty("Bodies").GetInt32() != 5 || guard.GetProperty("Bytes").GetInt32() != 4262
            || guard.GetProperty("DeferredMask").GetUInt32() != 0 || guard.GetProperty("DeferredCodeBytes").GetInt32() != 0
            || guard.GetProperty("CurrentPendingBytesClaimedMatched").GetBoolean()
            || !guard.GetProperty("BorrowedScalarSource").GetBoolean() || guard.GetProperty("AdditionalDestructorCalls").GetInt32() != 0
            || !V16InstalledNonInvokedObservation(data[4], "0x748700") || !V16InstalledNonInvokedObservation(data[5], "0x105270")
            || preflight.GetProperty("ProofCount").GetInt32() <= 0 || preflight.GetProperty("ReadCalls").GetInt32() <= 0
            || preflight.GetProperty("ReadBytes").GetInt64() <= 0 || preflight.GetProperty("SubsystemCount").GetInt32() <= 0
            || preflight.GetProperty("Owner64").GetUInt64() != currentOwner || preflight.GetProperty("CurrentShop").GetUInt64() != shop
            || preflight.GetProperty("TargetOwner").GetUInt64() != owner
            || preflight.GetProperty("PacketCreate").GetUInt64() != checked(image + 0x73A0C0UL)
            || preflight.GetProperty("PacketCopy").GetUInt64() != checked(image + 0x779760UL)
            || preflight.GetProperty("DeferredMask").GetUInt32() != 0 || preflight.GetProperty("DeferredCodeBytes").GetInt32() != 0
            || !preflight.GetProperty("EverySynchronousGuardBodyCurrentlyMatched").GetBoolean()
            || preflight.GetProperty("ColdCodeBytesPending").GetBoolean() || !preflight.GetProperty("AsyncAckNotInvokedByHelper").GetBoolean()
            || preflight.GetProperty("CountEqualityRequired").GetBoolean()) return false;
        ulong control = preflight.GetProperty("NetworkControl").GetUInt64();
        if (!V16InstalledPointer(control) || !V16InstalledPointer(preflight.GetProperty("Network").GetUInt64())
            || !V16InstalledPointer(preflight.GetProperty("NetworkChild").GetUInt64())
            || !V16InstalledCounts(data[6], "A", control, preflight.GetProperty("OwnCountsA"))
            || !V16InstalledCounts(data[7], "B", control, preflight.GetProperty("OwnCountsB"))) return false;
        var counts = data[8];
        if (counts.GetProperty("NetworkControl").GetUInt64() != control
            || !string.Equals(Text(counts, "CountsAddress"), "0x" + (control + 8).ToString("X", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            || counts.GetProperty("Length").GetInt32() != 8 || !counts.GetProperty("StableControlVtableAbClosed").GetBoolean()
            || counts.GetProperty("CountEqualityRequired").GetBoolean()
            || !JsonElement.DeepEquals(counts.GetProperty("OwnCountsA"), preflight.GetProperty("OwnCountsA"))
            || !JsonElement.DeepEquals(counts.GetProperty("OwnCountsB"), preflight.GetProperty("OwnCountsB"))) return false;
        _ = counts.GetProperty("CountsChanged").GetBoolean(); // Live reference counts need not remain equal.

        int mode = Text(start, "Mode") == "buy" ? 1 : 2;
        string? payloadPath = Text(payload, "Path");
        if (hook.GetProperty("Pid").GetInt32() != source.ProcessId || hook.GetProperty("Creation").GetInt64() != source.CreationFileTime
            || hook.GetProperty("Tid").GetUInt32() != tid || hook.GetProperty("Hwnd").GetUInt64() != hwnd
            || hook.GetProperty("Mode").GetInt32() != mode || hook.GetProperty("TargetOwner").GetUInt64() != owner
            || Rva(Text(hook, "EntryRva")) != "0x28C630" || Text(hook, "NativePacketType") != "CS_PERSONAL_SHOP_LIST_REQ"
            || payloadPath is null || !Path.IsPathFullyQualified(payloadPath) || Path.GetFileName(payloadPath) != "LC_PlayerShopListActionHookV15.dll"
            || Text(payload, "Sha256") != "124E5B0AD3D3D109C52BBC297670C9879A4655D4C67C2B9DFCEAD3BB9A7E836E"
            || Text(payload, "Export") != "PlayerShopListHook" || !payload.GetProperty("NewProtocol").GetBoolean()
            || !installed.GetProperty("Installed").GetBoolean() || installed.GetProperty("Win32Error").GetInt32() != 0
            || Text(installed, "HookType") != "WH_GETMESSAGE" || installed.GetProperty("Thread").GetUInt32() != tid
            || installed.GetProperty("GlobalHook").GetBoolean()) return false;

        string? rva = Rva(Text(unavailable, "Rva"));
        int length = rva switch { "0x28C630" => 2512, "0x28D000" => 834, _ => 0 };
        if (length == 0 || Text(unavailable, "Code") != "NativeCodeUnavailable" || Text(unavailable, "Phase") != "Preflight"
            || Text(error, "Type") != "NativePreflightException" || Text(error, "Code") != "NativeCodeUnavailable"
            || Text(error, "Phase") != "BeforePublish" || Rva(Text(error, "Rva")) != rva
            || Text(error, "Message") != "Native code is not currently readable/executable MEM_IMAGE: " + rva) return false;
        uint slice = Convert.ToUInt32(rva![2..], 16);
        var before = recheck.GetProperty("Before"); var after = recheck.GetProperty("After");
        if (Rva(Text(recheck, "FunctionRva")) != "0x28C630" || Rva(Text(recheck, "Rva")) != rva
            || recheck.GetProperty("Length").GetInt32() != length || !recheck.GetProperty("Attempted").GetBoolean()
            || Text(recheck, "Outcome") != "BudgetExhausted" || recheck.GetProperty("MaximumWaitMilliseconds").GetInt32() != 250
            || !recheck.GetProperty("ReadOnly").GetBoolean() || !recheck.GetProperty("RequestNotPublished").GetBoolean()
            || recheck.GetProperty("ProtectionChanged").GetBoolean() || recheck.GetProperty("Deferred").GetBoolean()
            || recheck.GetProperty("CurrentBytesMatched").GetBoolean() || !recheck.GetProperty("FullByteAndSourceProofStillRequired").GetBoolean()
            || !V16InitialUnpublishedPage(before, image, slice, length) || !V16InitialUnpublishedPage(after, image, slice, length)
            || !JsonElement.DeepEquals(before, after) || !JsonElement.DeepEquals(after, unavailable.GetProperty("Page"))) return false;
        int queries = recheck.GetProperty("Queries").GetInt32();
        double elapsed = recheck.GetProperty("ElapsedMilliseconds").GetDouble(), waited = recheck.GetProperty("TotalWaitMilliseconds").GetDouble();
        // Scheduling may overshoot the 250-ms budget. Never reinterpret it as
        // publication, and never admit nonfinite or negative clock evidence.
        if (queries is < 0 or > 10 || recheck.GetProperty("TotalQueries").GetInt32() != queries
            || !double.IsFinite(elapsed) || elapsed < 0 || !double.IsFinite(waited) || waited < 0) return false;

        string[] idleFields = ["Sequence", "Completed", "Active", "Claimed", "Entered", "Returned", "Status", "Exception",
            "ObservedPid", "ObservedTid", "Nonce", "Tick", "Callback", "Module", "Stage", "RequestMetadataBefore",
            "RequestMetadataAfter", "RequestUnchanged", "DestructorCalls", "HostDeferredMask", "NativeDeferredMask",
            "PostCodeConfirmed", "ExceptionStage", "ExceptionAddress", "ExceptionAccessAddress", "ExceptionAccessKind",
            "LeaseReleaseAttempted", "LeaseReleaseCompleted", "LeaseAcquired", "ExceptionCount", "ReleaseExceptionCode", "ReleaseExceptionAddress"];
        var state = final.GetProperty("State");
        if (state.ValueKind != JsonValueKind.Object || state.EnumerateObject().Count() != idleFields.Length
            || idleFields.Any(name => state.GetProperty(name).GetUInt64() != 0)
            || !final.GetProperty("QuiescentObserved").GetBoolean() || final.GetProperty("DisarmedBeforeClaim").GetBoolean()
            || final.GetProperty("EntireCallNextHookTailExitProven").GetBoolean()) return false;
        for (int i = 17; i <= 20; i++)
            if (!data[i].GetProperty("Success").GetBoolean() || i != 20 && data[i].GetProperty("Win32Error").GetInt32() != 0) return false;
        if (data[17].GetProperty("InterruptsActiveCallback").GetBoolean()
            || !cleanup.GetProperty("CleanupConfirmed").GetBoolean() || !cleanup.GetProperty("QuiescentObserved").GetBoolean()
            || !cleanup.GetProperty("Installed").GetBoolean() || !cleanup.GetProperty("Unhooked").GetBoolean()
            || cleanup.GetProperty("LocalViewRetained").GetBoolean() || cleanup.GetProperty("MappingRetained").GetBoolean()
            || cleanup.GetProperty("LibraryRetained").GetBoolean() || cleanup.GetProperty("HookRetained").GetBoolean()
            || cleanup.GetProperty("RemoteModuleUnloadProven").GetBoolean() || cleanup.GetProperty("EntireCallNextHookTailExitProven").GetBoolean()
            || cleanup.GetProperty("AdditionalMessagesSent").GetInt32() != 0) return false;
        return end.GetProperty("ExitCode").GetInt32() == 1 && end.GetProperty("NativeResult").ValueKind == JsonValueKind.Null
            && end.GetProperty("CleanupConfirmed").GetBoolean() && !end.GetProperty("SessionBlocked").GetBoolean()
            && !end.GetProperty("PostCodeConfirmed").GetBoolean() && !end.GetProperty("EventResultDurable").GetBoolean()
            && !end.GetProperty("BorrowedSourceContractVerified").GetBoolean() && !end.GetProperty("NativeAlreadyReturned").GetBoolean()
            && !end.GetProperty("AckObserved").GetBoolean() && !end.GetProperty("ListReceivedVerified").GetBoolean()
            && !end.GetProperty("RemoteModuleUnloadProven").GetBoolean() && !end.GetProperty("EntireHookTailExitProven").GetBoolean()
            && V14FlushCountersMatch(start, end, kinds.Length);
    }

    private static bool V16InstalledNonInvokedObservation(JsonElement data, string rva)
        => Rva(Text(data, "Rva")) == rva
            && Text(data, "Role") == "Not invoked by borrowed scalar source path; LC owns clone lifecycle and replies"
            && !data.GetProperty("DispatchPrerequisite").GetBoolean() && !data.GetProperty("BodyCallableClaimed").GetBoolean()
            && !data.GetProperty("HistoricalBytesSubstituted").GetBoolean() && data.GetProperty("Pages").ValueKind == JsonValueKind.Array;

    private static bool V16InstalledPointer(ulong value) => value >= 0x10000 && (value & 7) == 0 && value <= 0x7FFFFFFFFFFFUL - 16;

    private static bool V16InstalledCounts(JsonElement data, string phase, ulong control, JsonElement expected)
    {
        var counts = data.GetProperty("Counts");
        return Text(data, "Phase") == phase && data.GetProperty("NetworkControl").GetUInt64() == control
            && string.Equals(Text(data, "AddressHex"), "0x" + (control + 8).ToString("X", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            && data.GetProperty("Length").GetInt32() == 8 && data.GetProperty("Alive").GetBoolean()
            && !data.GetProperty("CountEqualityRequired").GetBoolean() && counts.GetProperty("Strong").GetInt64() > 0
            && counts.GetProperty("Weak").GetInt64() > 0 && JsonElement.DeepEquals(counts, expected);
    }
}
