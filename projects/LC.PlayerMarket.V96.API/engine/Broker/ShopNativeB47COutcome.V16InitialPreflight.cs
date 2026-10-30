using System.Text.Json;

namespace MemoryHub.Service;

internal sealed partial record ShopNativeB47COutcome
{
    /// <summary>
    /// Exact V16/source-v3 initial VerifyFull failure before Transport construction.
    /// This is a no-publication classification, never a permission to call a protected page.
    /// No historical journal is read, cleared or reinterpreted by this method.
    /// </summary>
    private static bool ConfirmedUnpublishedV16InitialSubmitPreflight(string text, SourceIdentity source,
        ulong image, ulong owner, int exitCode)
    {
        if (!ShopNativePackage.SupportsBuild(source) || exitCode != 1) return false;
        var rows = new List<JsonElement>();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            if (!UniqueProperties(document.RootElement)) return false;
            rows.Add(document.RootElement.Clone());
        }
        bool force = rows.Count == 7;
        if (rows.Count != 6 && !force) return false;
        string[] kinds = force
            ? ["START", "FORCE_DISPATCH", "SOURCE", "SUBMIT_CODE_PREFLIGHT_RECHECK", "NATIVE_CODE_PAGE_UNAVAILABLE", "ERROR", "END"]
            : ["START", "SOURCE", "SUBMIT_CODE_PREFLIGHT_RECHECK", "NATIVE_CODE_PAGE_UNAVAILABLE", "ERROR", "END"];
        for (int i = 0; i < rows.Count; i++)
            if (Text(rows[i], "Kind") != kinds[i] || rows[i].GetProperty("Sequence").GetInt32() != i + 1
                || rows[i].GetProperty("DiskFlush").GetBoolean() != (i >= rows.Count - 2)) return false;

        var start = rows[0].GetProperty("Data");
        var identity = rows[force ? 2 : 1].GetProperty("Data");
        var recheck = rows[force ? 3 : 2].GetProperty("Data");
        var unavailable = rows[^3].GetProperty("Data");
        var error = rows[^2].GetProperty("Data");
        var end = rows[^1].GetProperty("Data");

        string? managedVersion = Text(start, "ManagedProgramVersion");
        bool recheckManagedContract = (managedVersion is "16.0.1" or "16.0.2")
            && start.TryGetProperty("PostCodeRecheckContract", out var recheckContract)
            && recheckContract.ValueKind == JsonValueKind.Number && recheckContract.GetInt32() == 1;
        bool managedContract = managedVersion == "16.0.0" || managedVersion == "16.0.1" && recheckManagedContract
            || managedVersion == "16.0.2" && recheckManagedContract
                && start.TryGetProperty("ReturnedLifecycleRecoveryContract", out var lifecycleContract)
                && lifecycleContract.ValueKind == JsonValueKind.Number && lifecycleContract.GetInt32() == 1;
        if (NativeTupleVersion(start, source) != 15 || !managedContract
            || !PurposeMatches(start, ShopNativeLogPurpose.NativeRequest)
            || Text(start, "LogPolicy") != V14BufferedLogPolicy
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
        ulong window = identity.GetProperty("Hwnd").GetUInt64(), shop = identity.GetProperty("Shop").GetUInt64();
        _ = identity.GetProperty("Owner").GetUInt64();
        if (identity.GetProperty("Tid").GetUInt32() == 0 || window < 0x10000 || window > 0x7FFFFFFFFFFFUL
            || shop < 0x10000 || (shop & 7) != 0 || shop > 0x7FFFFFFFFFFFUL - 0x2C8) return false;

        if (!V15JournalContractMatches(end, source)) return false;
        if (force)
        {
            if (!ValidForcePreflightJournal(rows[1].GetProperty("Data"), end, source)) return false;
        }
        else if (!string.Equals(Text(end, "PendingJournal"), Path.Combine(@"D:\lineage\LC_PlayerShopListAction_v1\journal",
            $"{source.ProcessId}-{source.CreationFileTime}.pending.json"), StringComparison.OrdinalIgnoreCase)) return false;

        string? rva = Rva(Text(unavailable, "Rva"));
        int length = rva switch { "0x28C630" => 2512, "0x28D000" => 834, _ => 0 };
        if (length == 0 || Text(unavailable, "Code") != "NativeCodeUnavailable" || Text(unavailable, "Phase") != "Preflight"
            || Text(error, "Type") != "NativePreflightException" || Text(error, "Code") != "NativeCodeUnavailable"
            || Text(error, "Phase") != "Preflight" || Rva(Text(error, "Rva")) != rva
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
            || !JsonElement.DeepEquals(after, unavailable.GetProperty("Page"))) return false;
        int queries = recheck.GetProperty("Queries").GetInt32();
        double elapsed = recheck.GetProperty("ElapsedMilliseconds").GetDouble(), waited = recheck.GetProperty("TotalWaitMilliseconds").GetDouble();
        // One recheck in the complete initial path. Timer overshoot is allowed;
        // it is not evidence of a native publication or a second request.
        if (queries is < 0 or > 10 || recheck.GetProperty("TotalQueries").GetInt32() != queries
            || !double.IsFinite(elapsed) || elapsed < 0 || !double.IsFinite(waited) || waited < 0) return false;

        // Program.cs catches this failure before line 49 constructs Transport.
        // The exact ordered record set excludes HOOK_INSTALL_INTENT, durable
        // EVENT_INTENT and all publication/result/cleanup transport records.
        // Cleanup alone and a null result are insufficient without that producer path.
        return end.GetProperty("ExitCode").GetInt32() == 1 && end.GetProperty("NativeResult").ValueKind == JsonValueKind.Null
            && end.GetProperty("CleanupConfirmed").GetBoolean() && !end.GetProperty("SessionBlocked").GetBoolean()
            && !end.GetProperty("PostCodeConfirmed").GetBoolean() && !end.GetProperty("EventResultDurable").GetBoolean()
            && !end.GetProperty("BorrowedSourceContractVerified").GetBoolean() && !end.GetProperty("NativeAlreadyReturned").GetBoolean()
            && !end.GetProperty("AckObserved").GetBoolean() && !end.GetProperty("ListReceivedVerified").GetBoolean()
            && !end.GetProperty("RemoteModuleUnloadProven").GetBoolean() && !end.GetProperty("EntireHookTailExitProven").GetBoolean()
            && V14FlushCountersMatch(start, end, rows.Count);
    }

    private static bool V16InitialUnpublishedPage(JsonElement page, ulong image, uint slice, int length)
    {
        _ = page.GetProperty("AllocationProtect").GetUInt32();
        return page.GetProperty("PartitionId").GetUInt32() <= ushort.MaxValue
            && UnpublishedSubmitPage(page, image, slice, length);
    }
}
