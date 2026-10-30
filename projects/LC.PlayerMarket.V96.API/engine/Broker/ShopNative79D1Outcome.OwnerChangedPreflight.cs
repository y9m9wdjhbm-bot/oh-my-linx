using System.Globalization;
using System.Text.Json;

namespace MemoryHub.Service;

internal sealed partial record ShopNative79D1Outcome
{
    // Verified V16.0.2 producer: Program.VerifyFull at line 49 reads the full
    // owner at lines 237-241, before Transport is constructed at line 54.
    // The later checks run under BeforePublish/Native and cannot use this path.
    // This recognizes one closed receipt; it never reads or retires old journals
    // and does not authorize an automatic retry or interpret an owner sentinel.
    private static bool ConfirmedUnpublishedV16OwnerChangedPreflight(string text, SourceIdentity source,
        ulong image, ulong owner, int exitCode)
    {
        if (!ShopNativeLegacy79D1Package.SupportsBuild(source) || exitCode != 1 || owner is 0 or > int.MaxValue
            || !text.EndsWith('\n')) return false;
        string[] lines = text.Split('\n');
        bool force = lines.Length == 11;
        if (!force && lines.Length != 10) return false;
        string[] kinds = force
            ? ["START", "FORCE_DISPATCH", "SOURCE", "CURRENT_NATIVE_CODE_GUARDS", "NONINVOKED_CODE_OBSERVATION",
                "NONINVOKED_CODE_OBSERVATION", "NETWORK_SHARED_COUNTS_SAMPLE", "CURRENT_SHOP_OWNER_CHANGED", "ERROR", "END"]
            : ["START", "SOURCE", "CURRENT_NATIVE_CODE_GUARDS", "NONINVOKED_CODE_OBSERVATION",
                "NONINVOKED_CODE_OBSERVATION", "NETWORK_SHARED_COUNTS_SAMPLE", "CURRENT_SHOP_OWNER_CHANGED", "ERROR", "END"];
        var data = new JsonElement[9];
        JsonElement? forceJournal = null;
        DateTimeOffset prior = DateTimeOffset.MinValue;
        for (int i = 0; i < kinds.Length; i++)
        {
            using var document = JsonDocument.Parse(lines[i]);
            var row = document.RootElement;
            if (!UniqueProperties(row) || !OwnerPreflightFields(row, "At", "Sequence", "Kind", "DiskFlush", "Data")
                || Text(row, "Kind") != kinds[i] || row.GetProperty("Sequence").GetInt32() != i + 1
                || row.GetProperty("DiskFlush").GetBoolean() != (i >= kinds.Length - 2)
                || !DateTimeOffset.TryParse(Text(row, "At"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var at) || at == default || at < prior) return false;
            prior = at;
            var value = row.GetProperty("Data");
            if (value.ValueKind != JsonValueKind.Object) return false;
            if (force && i == 1) forceJournal = value.Clone();
            else data[i - (force && i > 1 ? 1 : 0)] = value.Clone();
        }

        var start = data[0]; var identity = data[1]; var guard = data[2];
        var counts = data[5]; var changed = data[6]; var error = data[7]; var end = data[8];
        if (!OwnerPreflightFields(start, "Version", "ManagedProgramVersion", "NativeProtocolVersion", "NativeGuardContract",
                "NativePayloadVersion", "PostCodeRecheckContract", "PostCodeRecheckMaximumMilliseconds", "ReturnedLifecycleRecoveryContract",
                "SubmitPreflightRecheckMaximumMilliseconds", "Mode", "MaximumNativeRequests", "MouseMessages", "KeyMessages",
                "TouchCalls", "ForegroundChanges", "DirectSelectedOwnerWrites", "SharedOwnershipCounterUpdates", "OpenProcessAccess",
                "DeferredCodePolicy", "Binding", "CurrentOwnerBits", "LogPolicy", "ResearchOnly")
            || NativeTupleVersion(start, source) != 15 || Text(start, "ManagedProgramVersion") != "16.0.2"
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
            || Text(start, "DeferredCodePolicy") != ShopNativeLegacy79D1Package.LegacyStrictV16Policy) return false;
        if (!OwnerPreflightFields(identity, "Pid", "Creation", "Image", "Hwnd", "Tid", "Shop", "Owner", "TargetOwner",
                "Path", "Sha256", "Timestamp", "ImageSize", "ReadOnlyProcessHandle")
            || !NativeSourceMatches(identity, source, image, owner)) return false;
        ulong hwnd = identity.GetProperty("Hwnd").GetUInt64(), shop = identity.GetProperty("Shop").GetUInt64();
        ulong expectedOwner = identity.GetProperty("Owner").GetUInt64();
        if (identity.GetProperty("Tid").GetUInt32() == 0 || hwnd < 0x10000 || hwnd > 0x7FFFFFFFFFFFUL
            || shop < 0x10000 || (shop & 7) != 0 || shop > 0x7FFFFFFFFFFFUL - 0x2C8) return false;

        if (!OwnerPreflightFields(guard, "Bodies", "Bytes", "DeferredMask", "DeferredCodeBytes",
                "CurrentPendingBytesClaimedMatched", "BorrowedScalarSource", "AdditionalDestructorCalls")
            || guard.GetProperty("Bodies").GetInt32() != 5 || guard.GetProperty("Bytes").GetInt32() != 4262
            || guard.GetProperty("DeferredMask").GetUInt32() != 0 || guard.GetProperty("DeferredCodeBytes").GetInt32() != 0
            || guard.GetProperty("CurrentPendingBytesClaimedMatched").GetBoolean()
            || !guard.GetProperty("BorrowedScalarSource").GetBoolean() || guard.GetProperty("AdditionalDestructorCalls").GetInt32() != 0
            || !OwnerPreflightNonInvoked(data[3], image, "0x740090", 73)
            || !OwnerPreflightNonInvoked(data[4], image, "0x104CA0", 80)) return false;
        if (!OwnerPreflightFields(counts, "Phase", "NetworkControl", "AddressHex", "Length", "Counts", "Alive", "CountEqualityRequired")) return false;
        ulong control = counts.GetProperty("NetworkControl").GetUInt64();
        var shared = counts.GetProperty("Counts");
        if (!V16InstalledPointer(control) || Text(counts, "Phase") != "A" || counts.GetProperty("Length").GetInt32() != 8
            || !string.Equals(Text(counts, "AddressHex"), "0x" + (control + 8).ToString("X", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            || !counts.GetProperty("Alive").GetBoolean() || counts.GetProperty("CountEqualityRequired").GetBoolean()
            || !OwnerPreflightFields(shared, "Strong", "Weak")
            || shared.GetProperty("Strong").GetUInt32() is 0 or >= int.MaxValue
            || shared.GetProperty("Weak").GetUInt32() is 0 or >= int.MaxValue) return false;

        if (!OwnerPreflightFields(changed, "ExpectedOwner", "ObservedOwner", "TargetOwner", "RequestNotPublished")
            || changed.GetProperty("ExpectedOwner").GetUInt64() != expectedOwner
            || changed.GetProperty("TargetOwner").GetUInt64() != owner || !changed.GetProperty("RequestNotPublished").GetBoolean()) return false;
        ulong observedOwner = changed.GetProperty("ObservedOwner").GetUInt64();
        if (observedOwner == expectedOwner || !OwnerPreflightFields(error, "Type", "Message", "Code", "Phase", "Rva")
            || Text(error, "Type") != "NativePreflightException" || Text(error, "Code") != "ContextUnavailable"
            || Text(error, "Phase") != "Preflight" || error.GetProperty("Rva").ValueKind != JsonValueKind.Null
            || Text(error, "Message") != "Current full 64-bit shop owner changed before dispatch: expected="
                + expectedOwner.ToString(CultureInfo.InvariantCulture) + ", observed="
                + observedOwner.ToString(CultureInfo.InvariantCulture) + ".") return false;

        if (!OwnerPreflightFields(end, "ExitCode", "PostCodeConfirmed", "EventResultDurable", "BorrowedSourceContractVerified",
                "NativeAlreadyReturned", "AckObserved", "NativeResult", "CleanupConfirmed", "SessionBlocked", "PendingJournal",
                "SingleTrialPendingJournal", "SingleTrialPendingSchema", "ListReceivedVerified", "RemoteModuleUnloadProven",
                "EntireHookTailExitProven", "LogRecordsBeforeEnd", "LogDiskFlushesBeforeEnd", "LogStreamFlushesBeforeEnd",
                "LogBufferedRecordsBeforeEnd", "LogWriteDurationMsBeforeEnd", "LogIoWriteDurationMsBeforeEnd", "LogSerializationDurationMsBeforeEnd")
            || !V15JournalContractMatches(end, source)) return false;
        if (force)
        {
            if (forceJournal is not { } journal || !OwnerPreflightFields(journal, "PreviousPending", "PreviousPendingPreserved", "CurrentPending", "WaitForPreviousGameState")
                || !ValidForcePreflightJournal(journal, end, source)) return false;
        }
        else if (!string.Equals(Text(end, "PendingJournal"), Path.Combine(@"D:\lineage\LC_PlayerShopListAction_v1\journal",
            $"{source.ProcessId}-{source.CreationFileTime}.pending.json"), StringComparison.OrdinalIgnoreCase)) return false;
        return end.GetProperty("ExitCode").GetInt32() == 1 && end.GetProperty("NativeResult").ValueKind == JsonValueKind.Null
            && end.GetProperty("CleanupConfirmed").GetBoolean() && !end.GetProperty("SessionBlocked").GetBoolean()
            && !end.GetProperty("PostCodeConfirmed").GetBoolean() && !end.GetProperty("EventResultDurable").GetBoolean()
            && !end.GetProperty("BorrowedSourceContractVerified").GetBoolean() && !end.GetProperty("NativeAlreadyReturned").GetBoolean()
            && !end.GetProperty("AckObserved").GetBoolean() && !end.GetProperty("ListReceivedVerified").GetBoolean()
            && !end.GetProperty("RemoteModuleUnloadProven").GetBoolean() && !end.GetProperty("EntireHookTailExitProven").GetBoolean()
            && V14FlushCountersMatch(start, end, kinds.Length);
    }

    private static bool OwnerPreflightFields(JsonElement value, params string[] names) =>
        value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == names.Length
        && names.All(name => value.TryGetProperty(name, out _));

    private static bool OwnerPreflightNonInvoked(JsonElement observation, ulong image, string rva, int length)
    {
        if (!OwnerPreflightFields(observation, "Rva", "Role", "DispatchPrerequisite", "BodyCallableClaimed", "HistoricalBytesSubstituted", "Pages")
            || Rva(Text(observation, "Rva")) != rva
            || Text(observation, "Role") != "Not invoked by borrowed scalar source path; LC owns clone lifecycle and replies"
            || observation.GetProperty("DispatchPrerequisite").GetBoolean() || observation.GetProperty("BodyCallableClaimed").GetBoolean()
            || observation.GetProperty("HistoricalBytesSubstituted").GetBoolean()) return false;
        var pages = observation.GetProperty("Pages");
        if (pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength() != 1) return false;
        var item = pages[0];
        if (Rva(Text(item, "Rva")) != rva || item.GetProperty("Length").GetInt32() != length) return false;
        // The producer records these pages only for diagnostics. An observation
        // error or inaccessible/mismatched unused body cannot imply publication.
        if (item.TryGetProperty("Error", out _))
            return OwnerPreflightFields(item, "Rva", "Length", "Error") && Text(item, "Error") is not null;
        if (!OwnerPreflightFields(item, "Rva", "Length", "Page", "CurrentlyReadableExecutable", "CurrentBytesMatched")) return false;
        var page = item.GetProperty("Page");
        if (!OwnerPreflightFields(page, "Base", "AllocationBase", "AllocationProtect", "PartitionId", "Size", "State", "Protect", "Type")) return false;
        ulong address = checked(image + Convert.ToUInt32(rva[2..], 16));
        ulong start = page.GetProperty("Base").GetUInt64(), size = page.GetProperty("Size").GetUInt64();
        ulong allocationBase = page.GetProperty("AllocationBase").GetUInt64();
        uint state = page.GetProperty("State").GetUInt32(), type = page.GetProperty("Type").GetUInt32(), protect = page.GetProperty("Protect").GetUInt32();
        _ = page.GetProperty("AllocationProtect").GetUInt32();
        if (page.GetProperty("PartitionId").GetUInt32() > ushort.MaxValue || start > address || size < (uint)length
            || address - start > size - (uint)length) return false;
        bool ready = state == 0x1000 && type == 0x1000000 && allocationBase == image && protect is 0x20 or 0x40 or 0x80;
        var matched = item.GetProperty("CurrentBytesMatched");
        return item.GetProperty("CurrentlyReadableExecutable").GetBoolean() == ready
            && (ready ? matched.ValueKind is JsonValueKind.True or JsonValueKind.False : matched.ValueKind == JsonValueKind.Null);
    }
}
