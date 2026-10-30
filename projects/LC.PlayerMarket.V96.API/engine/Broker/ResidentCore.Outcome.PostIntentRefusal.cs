using System.Text.Json;
using static MemoryHub.Service.ShopResidentB47CContract;

namespace MemoryHub.Service;

internal sealed partial record ShopResidentNativeOutcome
{
    // Pinned 79D1 managed producer 17.0.1 writes the three durable intents before
    // VerifyFull("BeforeCommandPublication"). Its command/nonce writes and post
    // occur only after that check returns. ProtectedCallee's typed exception
    // omits the stage fields, so ERROR.Phase is Native even though no command was
    // published. No single flag, missing post record, or cleanup alone proves it:
    // require the entire source-bound producer path and both settled journals.
    private static bool ConfirmedV17PostIntentCodeRefusal(IReadOnlyList<JsonElement> rows,
        SourceIdentity source, ulong image, ulong owner)
    {
        var start = V17Data(rows, "START"); var end = V17Data(rows, "END");
        var identity = V17Data(rows, "SOURCE"); var error = V17Data(rows, "ERROR");
        V17Start(start, source);
        Need(NewRangeProducer(start) && TerminalMatches(start, end)
            && NativeSourceMatches(identity, source, image, owner) && V15JournalContractMatches(end, source)
            && N(identity, "Tid") > 0 && N(identity, "Hwnd") > 0 && Pointer(N(identity, "Shop")),
            "Post-intent refusal producer/source/journal differs");
        int cursor = 1;
        void Next(string kind)
        {
            Need(cursor < rows.Count && T(rows[cursor], "Kind") == kind, "Post-intent refusal order differs: " + kind);
            cursor++;
        }
        if (cursor < rows.Count && T(rows[cursor], "Kind") == "FORCE_DISPATCH")
        {
            Need(ValidForcePreflightJournal(F(rows[cursor], "Data"), end, source), "Post-intent force journal differs");
            cursor++;
        }
        else Need(T(end, "PendingJournal") == Path.Combine(@"D:\lineage\LC_PlayerShopListAction_v1\journal",
            $"{source.ProcessId}-{source.CreationFileTime}.pending.json"), "Post-intent normal journal differs");
        Next("SOURCE");

        ulong Closure(string stage)
        {
            int last = cursor;
            while (last < rows.Count && T(rows[last], "Kind") != "PREFLIGHT_AB_CLOSED") last++;
            Need(last < rows.Count, "Missing complete post-intent prerequisite closure");
            var group = rows.Skip(cursor).Take(last - cursor + 1).ToArray();
            V17PreflightKinds(group, true, image);
            ulong mask = Group(group, image, N(identity, "Shop"), N(identity, "Owner"), owner);
            Need(mask == 0, "Post-intent refusal cannot use deferred code");
            ClosedRequestedRanges(F(rows[last], "Data"), image, mask, stage);
            cursor = last + 1; return mask;
        }
        _ = Closure("BeforeTransport");
        Next("HOOK_INSTALL_INTENT"); Next("NATIVE_PAYLOAD"); Next("HOOK_INSTALL_RESULT");
        V17Hook(rows, source, image, owner, start, identity);
        ulong installedMask = Closure("InstalledBeforeIntent");
        Next("EVENT_INTENT_DURABLE"); Next("COMMAND_PUBLISH_INTENT"); Next("WM_NULL_POST_INTENT");

        var intent = V17Data(rows, "EVENT_INTENT_DURABLE");
        var publish = V17Data(rows, "COMMAND_PUBLISH_INTENT"); var post = V17Data(rows, "WM_NULL_POST_INTENT");
        ulong mode = T(start, "Mode") == "buy" ? 1UL : 2UL, nonce = N(publish, "Nonce");
        Need(T(intent, "Path") == T(end, "PendingJournal")
            && T(intent, "SingleTrialPending") == T(end, "SingleTrialPendingJournal")
            && N(intent, "Mode") == mode && N(intent, "Calls") == 1 && B(intent, "NoAutomaticRetransmission")
            && N(publish, "Sequence") == 1 && nonce is > 0 and <= long.MaxValue
            && N(publish, "Mode") == mode && N(publish, "TargetOwner") == owner && N(publish, "MaximumRequests") == 1
            && N(post, "Sequence") == 1 && N(post, "Nonce") == nonce && N(post, "TargetThread") == N(identity, "Tid")
            && N(post, "Posts") == 1 && B(post, "TransportOnly"), "Post-intent refusal command binding differs");

        var unavailableRow = V17One(rows, "NATIVE_CODE_PAGE_UNAVAILABLE"); var unavailable = F(unavailableRow, "Data");
        int unavailableIndex = checked((int)N(unavailableRow, "Sequence") - 1);
        Need(unavailableIndex >= cursor, "Post-intent refusal failure order differs");
        var partial = rows.Skip(cursor).Take(unavailableIndex - cursor).ToArray();
        V17PreflightKinds(partial, false, image); Need(PendingMask(partial, image) == 0, "Unexpected deferred partial preflight");
        cursor = unavailableIndex; Next("NATIVE_CODE_PAGE_UNAVAILABLE"); Next("ERROR");
        // This recognizes only the observed legacy field omission of the exact
        // pinned producer. A new/contradictory typed contract remains unknown.
        string rva = T(unavailable, "Rva"); uint relative = checked((uint)Hex(rva));
        int length = relative switch { 0x3FDC0 => 209, 0x28C630 => 2512, 0x28D000 => 834,
            0x27CA10 => 549, 0x73A0C0 => 91, 0x779760 => 67, _ => 0 };
        var page = F(unavailable, "Page"); PageShape(page, image);
        Need(length > 0 && T(unavailable, "Code") == "NativeCodeUnavailable" && T(unavailable, "Phase") == "Preflight"
            && T(error, "Type") == "NativePreflightException" && T(error, "Code") == "NativeCodeUnavailable"
            && T(error, "Phase") == "Native" && Hex(T(error, "Rva")) == relative
            && T(error, "Message") == "Native code is not currently readable/executable MEM_IMAGE: " + rva
            && F(error, "PageRangeProofContract").ValueKind == JsonValueKind.Null
            && F(error, "PreflightStage").ValueKind == JsonValueKind.Null && !B(error, "RequestNotPublished")
            && V16InitialUnpublishedPage(page, image, relative, length), "Post-intent unavailable page/typed error differs");

        foreach (string kind in new[] { "NATIVE_FINAL_STATE", "UNHOOK", "LOCAL_VIEW_RELEASE", "LOCAL_MAPPING_CLOSE", "LOCAL_LIBRARY_FREE", "CLEANUP" }) Next(kind);
        V17Cleanup(rows, true, installedMask);
        Next("JOURNAL_SETTLED"); Next("SINGLE_TRIAL_JOURNAL_SETTLED"); Next("END");
        Need(cursor == rows.Count, "Extra post-intent refusal event");
        Need(V17ResolvedJournalMatches(T(V17Data(rows, "JOURNAL_SETTLED"), "Path"), T(end, "PendingJournal"))
            && V17ResolvedJournalMatches(T(V17Data(rows, "SINGLE_TRIAL_JOURNAL_SETTLED"), "Path"), T(end, "SingleTrialPendingJournal")),
            "Post-intent settled journals differ from durable intent");
        foreach (string key in new[] { "PostCodeConfirmed", "EventResultDurable", "BorrowedSourceContractVerified", "NativeAlreadyReturned",
            "AckObserved", "ListReceivedVerified", "RemoteModuleUnloadProven", "EntireHookTailExitProven", "SessionBlocked" })
            Need(!B(end, key), "Contradictory post-intent no-entry receipt");
        Need(N(end, "ExitCode") == 1 && F(end, "NativeResult").ValueKind == JsonValueKind.Null && B(end, "CleanupConfirmed"),
            "Post-intent refusal terminal state differs");
        int disk = rows.Take(rows.Count - 1).Count(r => B(r, "DiskFlush"));
        double io = F(end, "LogIoWriteDurationMsBeforeEnd").GetDouble(), serialization = F(end, "LogSerializationDurationMsBeforeEnd").GetDouble();
        Need(N(end, "LogRecordsBeforeEnd") == (ulong)rows.Count - 1 && N(end, "LogDiskFlushesBeforeEnd") == (ulong)disk
            && N(end, "LogStreamFlushesBeforeEnd") == 0 && N(end, "LogBufferedRecordsBeforeEnd") == (ulong)(rows.Count - 1 - disk)
            && double.IsFinite(io) && io >= 0 && double.IsFinite(serialization) && serialization >= 0
            && F(end, "LogWriteDurationMsBeforeEnd").GetDouble() == io + serialization, "Post-intent refusal durability counters differ");
        foreach (var row in rows)
            Need(B(row, "DiskFlush") == (T(row, "Kind") is "EVENT_INTENT_DURABLE" or "COMMAND_PUBLISH_INTENT" or "WM_NULL_POST_INTENT" or "ERROR" or "END"),
                "Post-intent refusal durable event differs");
        return true;
    }

    private static bool V17ResolvedJournalMatches(string resolved, string pending)
    {
        const string pendingSuffix = ".pending.json", resolvedSuffix = ".jsonl";
        if (!Path.IsPathFullyQualified(resolved) || !pending.EndsWith(pendingSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        string prefix = pending[..^pendingSuffix.Length] + ".resolved-";
        return resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && resolved.EndsWith(resolvedSuffix, StringComparison.OrdinalIgnoreCase)
            && resolved.Length == prefix.Length + 32 + resolvedSuffix.Length
            && Guid.TryParseExact(resolved.AsSpan(prefix.Length, 32), "N", out _);
    }
}
