using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Synthetic V16/source-v3 producer contracts only; never recovers or attests a historical/live request.</summary>
internal static class V16InitialPreflightVerification
{
    internal static int RunReport(string output)
    {
        var checks = Run();
        var report = new OfflineCheckReport("83.0.0", checks.All(c => c.Passed),
            checks.Count(c => c.Passed), checks.Count(c => !c.Passed), false, false, false, checks);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, HubProtocol.Json); file.Flush(true);
        return report.Success ? 0 : 1;
    }

    internal static OfflineCheck[] Run()
    {
        const ulong image = 0x100000, owner = 99;
        var source = new SourceIdentity(1234, 5678, @"D:\offline-contract-only\LC.exe", "0x100000",
            ShopNativeHistoricalPackage.SupportedTimestamp, ShopNativeHistoricalPackage.SupportedImageSize,
            ShopNativeHistoricalPackage.SupportedSha, "synthetic-not-a-live-source");
        string stem = @"D:\lineage\LC_PlayerShopListAction_v1\journal\1234-5678";
        var checks = new List<OfflineCheck>();
        JsonObject[] Read(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonNode.Parse(x)!.AsObject()).ToArray();
        string Write(IEnumerable<JsonObject> rows) => string.Join('\n', rows.Select(row => row.ToJsonString()));
        JsonNode Data(JsonObject[] rows, string kind) => rows.Single(row => row["Kind"]!.GetValue<string>() == kind)["Data"]!;
        string Fixture(bool force = true, string rva = "0x287910", string mode = "buy")
        {
            var rows = new List<JsonObject>();
            void Add(string kind, object data) => rows.Add(new JsonObject { ["Kind"] = kind, ["Data"] = JsonSerializer.SerializeToNode(data) });
            string pending = force ? stem + ".force-0123456789abcdef0123456789abcdef.pending.json" : stem + ".pending.json";
            ulong at = image + Convert.ToUInt32(rva[2..], 16);
            var page = new { Base = at & ~4095UL, AllocationBase = image, AllocationProtect = 128u, PartitionId = 0,
                Size = 4096UL, State = 4096u, Protect = 1u, Type = 16777216u };
            Add("START", new { Version = "15.0.0", ManagedProgramVersion = "16.0.0", NativeProtocolVersion = 15,
                NativeGuardContract = 15, NativePayloadVersion = "15.0.0", SubmitPreflightRecheckMaximumMilliseconds = 250,
                Mode = mode, MaximumNativeRequests = 1, MouseMessages = 0, KeyMessages = 0, TouchCalls = 0,
                ForegroundChanges = 0, DirectSelectedOwnerWrites = 0, SharedOwnershipCounterUpdates = true,
                OpenProcessAccess = "0x410", CurrentOwnerBits = 64,
                DeferredCodePolicy = ShopNativePackage.LegacyStrictV16Policy,
                Binding = "Caller-supplied runtime identity, independently verified",
                LogPolicy = "DurableDispatchIntent_Result_End_Error;BufferedVerboseV2", ResearchOnly = true });
            if (force) Add("FORCE_DISPATCH", new { PreviousPending = stem + ".pending.json", PreviousPendingPreserved = true,
                CurrentPending = pending, WaitForPreviousGameState = false });
            Add("SOURCE", new { Pid = source.ProcessId, Creation = source.CreationFileTime, Image = image,
                Hwnd = 0x20000UL, Tid = 456u, Shop = 0x30000UL, Owner = 88UL, TargetOwner = owner,
                Path = source.ImagePath, Sha256 = source.DiskSha256, Timestamp = $"0x{source.PeTimestamp:X}",
                ImageSize = $"0x{source.ImageSize:X}", ReadOnlyProcessHandle = true });
            Add("SUBMIT_CODE_PREFLIGHT_RECHECK", new { FunctionRva = "0x287910", Rva = rva,
                Length = rva == "0x287910" ? 1776 : 1570, Before = page, After = page,
                Attempted = true, Queries = 9, ElapsedMilliseconds = 252.0, Outcome = "BudgetExhausted",
                TotalWaitMilliseconds = 251.9, TotalQueries = 9, MaximumWaitMilliseconds = 250,
                ReadOnly = true, RequestNotPublished = true, ProtectionChanged = false, Deferred = false,
                CurrentBytesMatched = false, FullByteAndSourceProofStillRequired = true });
            Add("NATIVE_CODE_PAGE_UNAVAILABLE", new { Rva = rva, Page = page, Phase = "Preflight", Code = "NativeCodeUnavailable" });
            Add("ERROR", new { Type = "NativePreflightException", Message = "Native code is not currently readable/executable MEM_IMAGE: " + rva,
                Code = "NativeCodeUnavailable", Phase = "Preflight", Rva = rva });
            Add("END", new { ExitCode = 1, PostCodeConfirmed = false, EventResultDurable = false,
                BorrowedSourceContractVerified = false, NativeAlreadyReturned = false, AckObserved = false,
                NativeResult = (object?)null, CleanupConfirmed = true, SessionBlocked = false, PendingJournal = pending,
                SingleTrialPendingJournal = stem + ".v15-singletrial.pending.json", SingleTrialPendingSchema = "V15SingleTrialPendingV1",
                ListReceivedVerified = false, RemoteModuleUnloadProven = false, EntireHookTailExitProven = false });
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i]["At"] = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(i).ToString("O");
                rows[i]["Sequence"] = i + 1; rows[i]["DiskFlush"] = i >= rows.Count - 2;
            }
            var end = rows[^1]["Data"]!;
            end["LogRecordsBeforeEnd"] = rows.Count - 1; end["LogDiskFlushesBeforeEnd"] = 1;
            end["LogStreamFlushesBeforeEnd"] = 0; end["LogBufferedRecordsBeforeEnd"] = rows.Count - 2;
            end["LogIoWriteDurationMsBeforeEnd"] = 1.25; end["LogSerializationDurationMsBeforeEnd"] = 0.75;
            end["LogWriteDurationMsBeforeEnd"] = 2.0;
            return Write(rows);
        }
        void Check(string name, string text, bool expected, SourceIdentity? expectedSource = null, ulong expectedOwner = owner,
            int exit = 1, ShopNativeLogPurpose purpose = ShopNativeLogPurpose.NativeRequest)
        {
            try
            {
                var result = ShopNativeOutcome.Parse(text, expectedSource ?? source, image, expectedOwner, exit, purpose);
                bool passed = expected ? result.SafeToSettle && result.NoEntryConfirmed && result.SourceMatched
                    && !result.Entered && !result.Returned && result.Failure is { Code: "NativeCodeUnavailable", Scope: "Global", Phase: "Preflight" }
                    : !result.SafeToSettle && !result.NoEntryConfirmed;
                checks.Add(new("v16_initial_preflight_" + name, passed,
                    "SYNTHETIC producer contract only; no LC read, helper launch, historical recovery or server claim. " + result.Detail));
            }
            catch (Exception error) { checks.Add(new("v16_initial_preflight_" + name, false, "SYNTHETIC " + error)); }
        }
        string basis = Fixture();
        Check("current79d1_rejects_relabelled_historical_producer", SyntheticHistoricalContractFixture.RelabelCurrentForRejection(basis), false,
            expectedSource: SyntheticHistoricalContractFixture.CurrentBindingForRejection(source));
        {
            var upgraded = Read(basis); Data(upgraded, "START")["ManagedProgramVersion"] = "16.0.1";
            Data(upgraded, "START")["PostCodeRecheckContract"] = 1;
            Check("managed_16_0_1_recheck_contract_1", Write(upgraded), true);
            Data(upgraded, "START").AsObject().Remove("PostCodeRecheckContract");
            Check("reject_managed_16_0_1_missing_recheck_contract", Write(upgraded), false);
            Data(upgraded, "START")["PostCodeRecheckContract"] = 2;
            Check("reject_managed_16_0_1_wrong_recheck_contract", Write(upgraded), false);
        }
        {
            var upgraded = Read(basis); Data(upgraded, "START")["ManagedProgramVersion"] = "16.0.2";
            Data(upgraded, "START")["PostCodeRecheckContract"] = 1;
            Data(upgraded, "START")["ReturnedLifecycleRecoveryContract"] = 1;
            Check("managed_16_0_2_lifecycle_contract_1", Write(upgraded), true);
            Data(upgraded, "START").AsObject().Remove("ReturnedLifecycleRecoveryContract");
            Check("reject_managed_16_0_2_missing_lifecycle_contract", Write(upgraded), false);
            Data(upgraded, "START")["ReturnedLifecycleRecoveryContract"] = 2;
            Check("reject_managed_16_0_2_wrong_lifecycle_contract", Write(upgraded), false);
            Data(upgraded, "START")["ReturnedLifecycleRecoveryContract"] = 1;
            Data(upgraded, "START").AsObject().Remove("PostCodeRecheckContract");
            Check("reject_managed_16_0_2_missing_recheck_contract", Write(upgraded), false);
            Data(upgraded, "START")["PostCodeRecheckContract"] = 2;
            Check("reject_managed_16_0_2_wrong_recheck_contract", Write(upgraded), false);
        }
        void Reject(string name, Action<JsonObject[]> mutate)
        { var rows = Read(basis); mutate(rows); Check("reject_" + name, Write(rows), false); }
        foreach (bool force in new[] { false, true })
            foreach (string rva in new[] { "0x287910", "0x288000" })
                foreach (string mode in new[] { "buy", "sell" })
                    Check($"complete_{(force ? "force" : "normal")}_{mode}_{rva}", Fixture(force, rva, mode), true);
        Reject("wrong_managed_version", rows => Data(rows, "START")["ManagedProgramVersion"] = "15.0.0");
        Reject("missing_managed_version", rows => Data(rows, "START").AsObject().Remove("ManagedProgramVersion"));
        Reject("wrong_native_tuple", rows => Data(rows, "START")["NativeProtocolVersion"] = 14);
        Reject("unknown_logging_contract", rows => Data(rows, "START")["LogPolicy"] = "Unknown");
        Reject("preflight_read_only_mode", rows => { Data(rows, "START")["Mode"] = "--preflight"; Data(rows, "START")["MaximumNativeRequests"] = 0; });
        Reject("multiple_requests", rows => Data(rows, "START")["MaximumNativeRequests"] = 2);
        Reject("input_sent", rows => Data(rows, "START")["KeyMessages"] = 1);
        Reject("write_handle", rows => Data(rows, "START")["OpenProcessAccess"] = "0x1FFFFF");
        Reject("wrong_source", rows => Data(rows, "SOURCE")["Creation"] = 5679);
        Reject("wrong_owner", rows => Data(rows, "SOURCE")["TargetOwner"] = 98);
        Reject("source_not_read_only", rows => Data(rows, "SOURCE")["ReadOnlyProcessHandle"] = false);
        Reject("wrong_pending", rows => Data(rows, "END")["PendingJournal"] = stem + ".force-other.pending.json");
        Reject("wrong_singletrial_pending", rows => Data(rows, "END")["SingleTrialPendingJournal"] = stem + ".wrong.pending.json");
        Reject("missing_singletrial_contract", rows => Data(rows, "END").AsObject().Remove("SingleTrialPendingSchema"));
        Reject("prior_pending_changed", rows => Data(rows, "FORCE_DISPATCH")["PreviousPending"] = "wrong");
        Reject("force_waits_for_prior_state", rows => Data(rows, "FORCE_DISPATCH")["WaitForPreviousGameState"] = true);
        Reject("unknown_rva", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Rva"] = "0x281511");
        Reject("wrong_failure_phase", rows => Data(rows, "ERROR")["Phase"] = "BeforePublish");
        Reject("wrong_failure_type", rows => Data(rows, "ERROR")["Type"] = "IOException");
        Reject("different_error_rva", rows => Data(rows, "ERROR")["Rva"] = "0x288000");
        Reject("page_no_longer_noaccess", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Page"]!["Protect"] = 32);
        Reject("wrong_page_allocation", rows => Data(rows, "SUBMIT_CODE_PREFLIGHT_RECHECK")["Before"]!["AllocationBase"] = image + 4096);
        Reject("guard_page", rows => Data(rows, "SUBMIT_CODE_PREFLIGHT_RECHECK")["After"]!["Protect"] = 257);
        Reject("page_not_image", rows => Data(rows, "SUBMIT_CODE_PREFLIGHT_RECHECK")["After"]!["Type"] = 131072);
        Reject("page_does_not_contain_slice", rows => Data(rows, "SUBMIT_CODE_PREFLIGHT_RECHECK")["Before"]!["Size"] = 1);
        Reject("changed_page_receipt", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Page"]!["AllocationProtect"] = 32);
        Reject("wrong_slice_length", rows => Data(rows, "SUBMIT_CODE_PREFLIGHT_RECHECK")["Length"] = 3346);
        Reject("recheck_not_attempted", rows => Data(rows, "SUBMIT_CODE_PREFLIGHT_RECHECK")["Attempted"] = false);
        Reject("recheck_succeeded", rows => Data(rows, "SUBMIT_CODE_PREFLIGHT_RECHECK")["Outcome"] = "PageChanged");
        Reject("native_publication_possible", rows => Data(rows, "SUBMIT_CODE_PREFLIGHT_RECHECK")["RequestNotPublished"] = false);
        Reject("protection_modified", rows => Data(rows, "SUBMIT_CODE_PREFLIGHT_RECHECK")["ProtectionChanged"] = true);
        Reject("deferred_submit", rows => Data(rows, "SUBMIT_CODE_PREFLIGHT_RECHECK")["Deferred"] = true);
        Reject("unexpected_recheck_queries", rows => Data(rows, "SUBMIT_CODE_PREFLIGHT_RECHECK")["TotalQueries"] = 10);
        Reject("negative_recheck_time", rows => Data(rows, "SUBMIT_CODE_PREFLIGHT_RECHECK")["ElapsedMilliseconds"] = -1);
        Reject("native_result_present", rows => Data(rows, "END")["NativeResult"] = new JsonObject());
        Reject("event_result_durable", rows => Data(rows, "END")["EventResultDurable"] = true);
        Reject("cleanup_not_confirmed", rows => Data(rows, "END")["CleanupConfirmed"] = false);
        Reject("session_blocked", rows => Data(rows, "END")["SessionBlocked"] = true);
        Reject("native_already_returned", rows => Data(rows, "END")["NativeAlreadyReturned"] = true);
        Reject("server_ack_claim", rows => Data(rows, "END")["AckObserved"] = true);
        Reject("source_closure_false", rows => Data(rows, "END")["BorrowedSourceContractVerified"] = true);
        Reject("missing_sequence", rows => rows[3].Remove("Sequence"));
        Reject("sequence_gap", rows => rows[3]["Sequence"] = 20);
        Reject("missing_durable_error", rows => rows[^2]["DiskFlush"] = false);
        Reject("missing_durable_end", rows => rows[^1]["DiskFlush"] = false);
        Reject("unexpected_durable_record", rows => rows[2]["DiskFlush"] = true);
        Reject("wrong_record_count", rows => Data(rows, "END")["LogRecordsBeforeEnd"] = 7);
        Reject("wrong_buffered_count", rows => Data(rows, "END")["LogBufferedRecordsBeforeEnd"] = 99);
        Reject("wrong_flush_count", rows => Data(rows, "END")["LogDiskFlushesBeforeEnd"] = 2);
        Reject("inconsistent_io_time", rows => Data(rows, "END")["LogWriteDurationMsBeforeEnd"] = 10);
        foreach (string extra in new[] { "HOOK_INSTALL_INTENT", "HOOK_INSTALL_RESULT", "EVENT_INTENT_DURABLE", "COMMAND_PUBLISH_INTENT", "WM_NULL_POST_RESULT", "EVENT_RESULT" })
        {
            var rows = Read(basis).ToList(); rows.Insert(3, new JsonObject { ["Kind"] = extra, ["Data"] = new JsonObject() });
            Check("reject_extra_" + extra, Write(rows), false);
        }
        var missing = Read(basis).Where(row => row["Kind"]!.GetValue<string>() != "SUBMIT_CODE_PREFLIGHT_RECHECK").ToArray();
        Check("reject_missing_recheck", Write(missing), false);
        Check("reject_truncated_end", Write(Read(basis).SkipLast(1)), false);
        Check("reject_duplicate_source", basis + "\n" + Read(basis).Single(row => row["Kind"]!.GetValue<string>() == "SOURCE").ToJsonString(), false);
        Check("reject_duplicate_json_key", basis.Replace("\"CleanupConfirmed\":true", "\"CleanupConfirmed\":true,\"CleanupConfirmed\":true", StringComparison.Ordinal), false);
        Check("reject_caller_readonly_purpose", basis, false, purpose: ShopNativeLogPurpose.ReadOnlyPreflight);
        Check("reject_exit_code_mismatch", basis, false, exit: 0);
        Check("reject_other_build", basis, false, expectedSource: source with { DiskSha256 = new string('B', 64) });
        Check("reject_caller_owner_mismatch", basis, false, expectedOwner: 100);
        try
        {
            var parsed = ShopNativeOutcome.Parse(basis, source, image, owner, 1);
            var response = parsed.Failure!.Attach(new() { NativeEntered = parsed.Entered, NativeReturned = parsed.Returned,
                SessionBlocked = !parsed.SafeToSettle || !parsed.NoEntryConfirmed });
            checks.Add(new("v16_initial_preflight_settles_own_unsent_page_but_stops_batch_for_fresh_recovery",
                parsed.NoEntryConfirmed && !response.SessionBlocked && ShopScanPolicy.Decide(response, true, forceDispatch: true) == ShopScanDisposition.StopBatch,
                "SYNTHETIC; Global/Preflight stays a stopped batch for existing patrol cooldown; no native replay or historical settlement."));
        }
        catch (Exception error) { checks.Add(new("v16_initial_preflight_batch_policy", false, error.ToString())); }
        return checks.ToArray();
    }
}
