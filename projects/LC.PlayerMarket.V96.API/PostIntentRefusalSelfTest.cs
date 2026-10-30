using System.Globalization;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Parses preserved receipts and in-memory negative mutations only. No runtime, IPC, journal or game access.</summary>
public static class PostIntentRefusalSelfTest
{
    public static OfflineCheck[] Run(string fixtureDirectory)
    {
        var checks = new List<OfflineCheck>();
        void Check(string name, Action assertion)
        {
            try { assertion(); checks.Add(new(name, true, "Passed (offline receipt parsing)")); }
            catch (Exception error) { checks.Add(new(name, false, error.GetType().Name + ": " + error.Message)); }
        }
        string refused = File.ReadAllText(Path.Combine(fixtureDirectory, "775ee7cafd1e451e9fb6fbb04d7904ed.jsonl"));
        string returned = File.ReadAllText(Directory.GetFiles(fixtureDirectory, "8b1d*.jsonl", SearchOption.TopDirectoryOnly).Single());
        var original = Rows(refused);
        var identity = Identity(original);
        ulong image = U(Data(original, "SOURCE"), "Image"), owner = U(Data(original, "SOURCE"), "TargetOwner");

        void Both(string name, string receipt, bool accepted, SourceIdentity source, ulong baseAddress, ulong target, int exit)
        {
            Check(name + "_facade", () =>
            {
                var p = ShopNativeOutcome.Parse(receipt, source, baseAddress, target, exit);
                if (accepted) Require(p.SafeToSettle && p.NoEntryConfirmed && !p.Entered && !p.Returned && p.SourceMatched
                    && p.Failure is { Code: "NativeCodeUnavailable", Scope: "Global", Phase: "Preflight", Rva: "0x7710F0" }, p.Detail);
                else Require(!p.SafeToSettle && !p.NoEntryConfirmed, "Unsafe evidence was accepted: " + p.Detail);
            });
            Check(name + "_current79D1", () =>
            {
                var p = ShopNative79D1Outcome.Parse(receipt, source, baseAddress, target, exit);
                if (accepted) Require(p.SafeToSettle && p.NoEntryConfirmed && !p.Entered && !p.Returned && p.SourceMatched
                    && p.Failure is { Code: "NativeCodeUnavailable", Scope: "Global", Phase: "Preflight", Rva: "0x7710F0" }, p.Detail);
                else Require(!p.SafeToSettle && !p.NoEntryConfirmed, "Unsafe evidence was accepted: " + p.Detail);
            });
        }
        Check("preserved_refusal_fixture_has_34_original_rows", () =>
        {
            Require(original.Count == 34 && original[20]["Kind"]!.GetValue<string>() == "EVENT_INTENT_DURABLE"
                && original[23]["Kind"]!.GetValue<string>() == "NATIVE_CODE_PAGE_UNAVAILABLE"
                && original[32]["Kind"]!.GetValue<string>() == "SINGLE_TRIAL_JOURNAL_SETTLED", "Wrong preserved receipt");
        });
        // These two assertions intentionally fail before the parser correction. The original text is not normalized.
        Both("preserved_postintent_noentry_refusal", refused, true, identity, image, owner, 1);
        // Negative mutations below repair the envelope to isolate content guards.
        // This unchanged-content positive control proves normalization itself
        // does not make every mutation fail for an unrelated envelope reason.
        var normalizedControl = Rows(refused); Normalize(normalizedControl);
        Both("normalized_unchanged_postintent_noentry_refusal", Text(normalizedControl), true, identity, image, owner, 1);
        foreach (bool force in new[] { false, true })
        {
            bool forced = force;
            Check("confirmed_postintent_global_refusal_stops_" + (forced ? "force" : "normal") + "_scan", () =>
            {
                var p = ShopNativeOutcome.Parse(refused, identity, image, owner, 1);
                Require(p.SafeToSettle && p.NoEntryConfirmed && p.Failure is not null, p.Detail);
                var response = p.Failure!.Attach(new ShopCommandResponse { State = "NativeRejected",
                    NativeEntered = p.Entered, NativeReturned = p.Returned, SessionBlocked = false });
                Require(ShopScanPolicy.Decide(response, sameSource: true, forceDispatch: forced) == ShopScanDisposition.StopBatch,
                    "Settling a positively proved no-entry failure must not continue or retransmit this scan");
            });
        }
        Check("preserved_returned_fixture_facade", () =>
        {
            var rows = Rows(returned); var d = Data(rows, "SOURCE");
            var p = ShopNativeOutcome.Parse(returned, Identity(rows), U(d, "Image"), U(d, "TargetOwner"), 0);
            Require(p.SafeToSettle && p.Entered && p.Returned && !p.NoEntryConfirmed && p.SourceMatched && p.Failure is null, p.Detail);
        });
        Check("preserved_returned_fixture_current79D1", () =>
        {
            var rows = Rows(returned); var d = Data(rows, "SOURCE");
            var p = ShopNative79D1Outcome.Parse(returned, Identity(rows), U(d, "Image"), U(d, "TargetOwner"), 0);
            Require(p.SafeToSettle && p.Entered && p.Returned && !p.NoEntryConfirmed && p.SourceMatched && p.Failure is null, p.Detail);
        });
        void Reject(string name, Action<List<JsonObject>> mutation, bool repairEnvelope = true)
        {
            var rows = Rows(refused); mutation(rows); if (repairEnvelope) Normalize(rows);
            Both("reject_" + name, Text(rows), false, identity, image, owner, 1);
        }
        foreach (string kind in new[] { "FORCE_DISPATCH", "HOOK_INSTALL_INTENT", "NATIVE_PAYLOAD", "HOOK_INSTALL_RESULT",
            "EVENT_INTENT_DURABLE", "COMMAND_PUBLISH_INTENT", "WM_NULL_POST_INTENT", "NATIVE_CODE_PAGE_UNAVAILABLE", "ERROR",
            "NATIVE_FINAL_STATE", "UNHOOK", "LOCAL_VIEW_RELEASE", "LOCAL_MAPPING_CLOSE", "LOCAL_LIBRARY_FREE", "CLEANUP",
            "JOURNAL_SETTLED", "SINGLE_TRIAL_JOURNAL_SETTLED" })
        {
            string key = kind;
            Reject("missing_" + key, rows => rows.Remove(Row(rows, key)));
            Reject("duplicate_" + key, rows => { int at = rows.IndexOf(Row(rows, key)); rows.Insert(at, (JsonObject)rows[at].DeepClone()); });
        }
        Reject("missing_first_AB_closure", rows => rows.Remove(Row(rows, "PREFLIGHT_AB_CLOSED", 0)));
        Reject("missing_second_AB_closure", rows => rows.Remove(Row(rows, "PREFLIGHT_AB_CLOSED", 1)));
        Reject("duplicate_AB_closure", rows => rows.Insert(10, (JsonObject)Row(rows, "PREFLIGHT_AB_CLOSED").DeepClone()));
        Reject("wrong_first_stage", rows => Data(rows, "PREFLIGHT_AB_CLOSED")["PreflightStage"] = "BeforeCommandPublication");
        Reject("wrong_second_stage", rows => Data(rows, "PREFLIGHT_AB_CLOSED", 1)["PreflightStage"] = "BeforeTransport");
        Reject("missing_requested_code_range", rows => Data(rows, "PREFLIGHT_AB_CLOSED", 1)["RequestedCodeRanges"]!.AsArray().RemoveAt(0));
        Reject("source_pid", rows => Data(rows, "SOURCE")["Pid"] = identity.ProcessId + 1);
        Reject("source_creation", rows => Data(rows, "SOURCE")["Creation"] = identity.CreationFileTime + 1);
        Reject("source_sha", rows => Data(rows, "SOURCE")["Sha256"] = new string('0', 64));
        Reject("source_target_owner", rows => Data(rows, "SOURCE")["TargetOwner"] = owner + 1);
        Reject("source_thread", rows => Data(rows, "SOURCE")["Tid"] = U(Data(rows, "SOURCE"), "Tid") + 1);
        Reject("payload_sha", rows => Data(rows, "NATIVE_PAYLOAD")["Sha256"] = new string('0', 64));
        Reject("hook_owner", rows => Data(rows, "HOOK_INSTALL_INTENT")["TargetOwner"] = owner + 1);
        Reject("hook_mode", rows => Data(rows, "HOOK_INSTALL_INTENT")["Mode"] = 1);
        Reject("command_owner", rows => Data(rows, "COMMAND_PUBLISH_INTENT")["TargetOwner"] = owner + 1);
        Reject("command_mode", rows => Data(rows, "COMMAND_PUBLISH_INTENT")["Mode"] = 1);
        Reject("intent_mode", rows => Data(rows, "EVENT_INTENT_DURABLE")["Mode"] = 1);
        Reject("intent_calls", rows => Data(rows, "EVENT_INTENT_DURABLE")["Calls"] = 2);
        Reject("automatic_retransmission", rows => Data(rows, "EVENT_INTENT_DURABLE")["NoAutomaticRetransmission"] = false);
        Reject("zero_nonce", rows => { Data(rows, "COMMAND_PUBLISH_INTENT")["Nonce"] = 0; Data(rows, "WM_NULL_POST_INTENT")["Nonce"] = 0; });
        Reject("nonce_above_producer_range", rows => { Data(rows, "COMMAND_PUBLISH_INTENT")["Nonce"] = ulong.MaxValue; Data(rows, "WM_NULL_POST_INTENT")["Nonce"] = ulong.MaxValue; });
        Reject("mismatched_nonce", rows => Data(rows, "WM_NULL_POST_INTENT")["Nonce"] = U(Data(rows, "COMMAND_PUBLISH_INTENT"), "Nonce") + 1);
        Reject("command_sequence", rows => Data(rows, "COMMAND_PUBLISH_INTENT")["Sequence"] = 2);
        Reject("post_sequence", rows => Data(rows, "WM_NULL_POST_INTENT")["Sequence"] = 2);
        Reject("maximum_requests", rows => Data(rows, "COMMAND_PUBLISH_INTENT")["MaximumRequests"] = 2);
        Reject("target_thread", rows => Data(rows, "WM_NULL_POST_INTENT")["TargetThread"] = 0);
        Reject("multiple_posts", rows => Data(rows, "WM_NULL_POST_INTENT")["Posts"] = 2);
        Reject("nontransport_post", rows => Data(rows, "WM_NULL_POST_INTENT")["TransportOnly"] = false);
        Reject("post_result_present", rows => rows.Insert(23, NewRow("WM_NULL_POST_RESULT", new() { ["Posted"] = true, ["Win32Error"] = 0, ["NoAutomaticRetransmission"] = true }, true)));
        Reject("event_result_present", rows => rows.Insert(25, NewRow("EVENT_RESULT", new(), true)));
        Reject("intent_order", rows => { (rows[21], rows[22]) = (rows[22], rows[21]); });
        Reject("cleanup_before_error", rows => { (rows[24], rows[25]) = (rows[25], rows[24]); });
        Reject("settlement_order", rows => { (rows[31], rows[32]) = (rows[32], rows[31]); });
        foreach (string field in new[] { "Sequence", "Active", "Claimed", "Entered", "Returned", "Nonce", "LeaseAcquired", "ExceptionCount", "HostDeferredMask" })
        {
            string key = field; Reject("nonidle_" + key, rows => Data(rows, "NATIVE_FINAL_STATE")["State"]![key] = 1);
        }
        Reject("final_state_extra_field", rows => Data(rows, "NATIVE_FINAL_STATE")["State"]!["UnknownState"] = 0);
        Reject("disarmed_after_claim", rows => Data(rows, "NATIVE_FINAL_STATE")["DisarmedBeforeClaim"] = true);
        Reject("not_quiescent", rows => Data(rows, "NATIVE_FINAL_STATE")["QuiescentObserved"] = false);
        Reject("cleanup_failed", rows => Data(rows, "CLEANUP")["CleanupConfirmed"] = false);
        Reject("unhook_failed", rows => Data(rows, "UNHOOK")["Success"] = false);
        Reject("cleanup_additional_messages", rows => Data(rows, "CLEANUP")["AdditionalMessagesSent"] = 1);
        Reject("retained_mapping", rows => Data(rows, "CLEANUP")["MappingRetained"] = true);
        Reject("event_journal_path", rows => Data(rows, "EVENT_INTENT_DURABLE")["Path"] = "D:\\unrelated.pending.json");
        Reject("event_singletrial_path", rows => Data(rows, "EVENT_INTENT_DURABLE")["SingleTrialPending"] = "D:\\unrelated.pending.json");
        Reject("resolved_other_directory", rows => Data(rows, "JOURNAL_SETTLED")["Path"] = "D:\\unrelated.resolved-11111111111111111111111111111111.jsonl");
        Reject("resolved_other_request", rows => Data(rows, "JOURNAL_SETTLED")["Path"] = Data(rows, "JOURNAL_SETTLED")["Path"]!.GetValue<string>().Replace("cd8d6d52453147929b04451b131cd937", "11111111111111111111111111111111"));
        Reject("resolved_invalid_guid", rows => Data(rows, "JOURNAL_SETTLED")["Path"] = Data(rows, "JOURNAL_SETTLED")["Path"]!.GetValue<string>().Replace("7e0340019cbd4f058e53095651568c3a", new string('z', 32)));
        Reject("singletrial_resolved_path", rows => Data(rows, "SINGLE_TRIAL_JOURNAL_SETTLED")["Path"] = Data(rows, "JOURNAL_SETTLED")["Path"]!.GetValue<string>());
        Reject("journal_error_present", rows => rows.Insert(32, NewRow("JOURNAL_ERROR", new() { ["Message"] = "injected failure" }, true)));
        Reject("singletrial_journal_error_present", rows => rows.Insert(33, NewRow("SINGLE_TRIAL_JOURNAL_ERROR", new() { ["Message"] = "injected failure" }, true)));
        Reject("pending_schema", rows => Data(rows, "END")["SingleTrialPendingSchema"] = "Unknown");
        foreach (string field in new[] { "SessionBlocked", "PostCodeConfirmed", "EventResultDurable", "NativeAlreadyReturned", "BorrowedSourceContractVerified", "AckObserved", "ListReceivedVerified" })
        {
            string key = field; Reject("contradictory_end_" + key, rows => Data(rows, "END")[key] = true);
        }
        Reject("end_cleanup_failed", rows => Data(rows, "END")["CleanupConfirmed"] = false);
        Reject("library_release_failed", rows => Data(rows, "LOCAL_LIBRARY_FREE")["Success"] = false);
        Reject("end_native_result", rows => Data(rows, "END")["NativeResult"] = Data(rows, "NATIVE_FINAL_STATE")["State"]!.DeepClone());
        Reject("end_exit_code", rows => Data(rows, "END")["ExitCode"] = 0);
        Reject("unavailable_other_rva", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Rva"] = "0x1234");
        Reject("readable_page", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Page"]!["Protect"] = 32);
        Reject("page_allocation_base", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Page"]!["AllocationBase"] = image + 4096);
        Reject("page_private", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Page"]!["Type"] = 131072);
        Reject("page_not_committed", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Page"]!["State"] = 8192);
        Reject("page_too_short", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Page"]!["Size"] = 1);
        Reject("wrong_exception_type", rows => Data(rows, "ERROR")["Type"] = "InvalidOperationException");
        Reject("wrong_exception_message", rows => Data(rows, "ERROR")["Message"] = "Unknown failure");
        Reject("wrong_exception_rva", rows => Data(rows, "ERROR")["Rva"] = "0x731AA0");
        Reject("wrong_exception_phase", rows => Data(rows, "ERROR")["Phase"] = "BeforePublish");
        Reject("different_typed_stage", rows => Data(rows, "ERROR")["PreflightStage"] = "BeforeTransport");
        Reject("different_typed_contract", rows => Data(rows, "ERROR")["PageRangeProofContract"] = 2);
        Reject("different_typed_unpublished_flag", rows => Data(rows, "ERROR")["RequestNotPublished"] = true);
        Reject("unknown_producer_version", rows => { Data(rows, "START")["ManagedProgramVersion"] = "17.0.2"; Data(rows, "END")["ManagedProgramVersion"] = "17.0.2"; });
        Reject("missing_range_contract", rows => { Data(rows, "START").Remove("PageRangeProofContract"); Data(rows, "END").Remove("PageRangeProofContract"); });
        Reject("changed_certificate", rows => { Data(rows, "START")["ColdCodeCertificateSha256"] = new string('0', 64); Data(rows, "END")["ColdCodeCertificateSha256"] = new string('0', 64); });
        foreach (string kind in new[] { "EVENT_INTENT_DURABLE", "COMMAND_PUBLISH_INTENT", "WM_NULL_POST_INTENT", "ERROR", "END" })
        {
            string key = kind; Reject("not_durable_" + key, rows => Row(rows, key)["DiskFlush"] = false);
        }
        Reject("unexpected_durable_record", rows => Row(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["DiskFlush"] = true);
        foreach (string field in new[] { "LogRecordsBeforeEnd", "LogDiskFlushesBeforeEnd", "LogStreamFlushesBeforeEnd", "LogBufferedRecordsBeforeEnd" })
        {
            string key = field; Reject("wrong_" + key, rows => Data(rows, "END")[key] = U(Data(rows, "END"), key) + 1, false);
        }
        Reject("negative_io_duration", rows => Data(rows, "END")["LogIoWriteDurationMsBeforeEnd"] = -1, false);
        Reject("duration_sum", rows => Data(rows, "END")["LogWriteDurationMsBeforeEnd"] = 0, false);
        Reject("row_sequence", rows => rows[21]["Sequence"] = 999, false);
        Reject("timestamp_regression", rows => rows[21]["At"] = "2020-01-01T00:00:00+00:00", false);
        Both("reject_truncated_final_newline", refused.TrimEnd('\r', '\n'), false, identity, image, owner, 1);
        Both("reject_duplicate_json_key", refused.Replace("\"RequestNotPublished\":false", "\"RequestNotPublished\":false,\"RequestNotPublished\":false"), false, identity, image, owner, 1);
        Both("reject_wrong_exit_argument", refused, false, identity, image, owner, 0);
        return checks.ToArray();
    }

    private static List<JsonObject> Rows(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
    private static JsonObject Row(List<JsonObject> rows, string kind, int occurrence = 0) => rows.Where(r => r["Kind"]!.GetValue<string>() == kind).ElementAt(occurrence);
    private static JsonObject Data(List<JsonObject> rows, string kind, int occurrence = 0) => Row(rows, kind, occurrence)["Data"]!.AsObject();
    private static ulong U(JsonObject data, string key) => data[key]!.GetValue<ulong>();
    private static ulong Hex(string value) => ulong.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private static SourceIdentity Identity(List<JsonObject> rows)
    {
        var d = Data(rows, "SOURCE"); return new(checked((int)U(d, "Pid")), checked((long)U(d, "Creation")), d["Path"]!.GetValue<string>(),
            "0x" + U(d, "Image").ToString("X"), checked((uint)Hex(d["Timestamp"]!.GetValue<string>())),
            checked((uint)Hex(d["ImageSize"]!.GetValue<string>())), d["Sha256"]!.GetValue<string>(), "offline-preserved-receipt-not-live");
    }
    private static JsonObject NewRow(string kind, JsonObject data, bool disk) => new() { ["At"] = "2026-10-25T12:59:47+00:00", ["Sequence"] = 0, ["Kind"] = kind, ["DiskFlush"] = disk, ["Data"] = data };
    private static void Normalize(List<JsonObject> rows)
    {
        var epoch = DateTimeOffset.Parse("2026-10-25T12:59:46+00:00", CultureInfo.InvariantCulture);
        for (int i = 0; i < rows.Count; i++) { rows[i]["Sequence"] = i + 1; rows[i]["At"] = epoch.AddMilliseconds(i).ToString("O"); }
        var end = Data(rows, "END"); int disk = rows.Take(rows.Count - 1).Count(r => r["DiskFlush"]!.GetValue<bool>());
        end["LogRecordsBeforeEnd"] = rows.Count - 1; end["LogDiskFlushesBeforeEnd"] = disk;
        end["LogStreamFlushesBeforeEnd"] = 0; end["LogBufferedRecordsBeforeEnd"] = rows.Count - 1 - disk;
    }
    private static string Text(List<JsonObject> rows) => string.Join("\n", rows.Select(r => r.ToJsonString())) + "\n";
    private static void Require(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
}
