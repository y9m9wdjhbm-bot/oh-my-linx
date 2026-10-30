using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Saved receipt and isolated store regressions. Never opens LC or launches the native helper.</summary>
internal static class OwnerChangedPreflightVerification
{
    private const string FixtureName = "ff589da3d3ee480bbe5bc6c604d2f690.jsonl";
    private const string FixtureSha256 = "565A6E7E65894A048695CABB11303CE31FB3678725EE732C30435AB00B8CA4D5";

    internal static int RunReport(string output)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("ADMIN_REQUIRED");
        var checks = Run();
        var report = new OfflineCheckReport("83.0.0", checks.All(c => c.Passed), checks.Count(c => c.Passed),
            checks.Count(c => !c.Passed), false, false, false, checks);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, HubProtocol.Json); file.Flush(true);
        return report.Success ? 0 : 1;
    }

    internal static OfflineCheck[] Run()
    {
        var checks = RunParser().ToList();
        // Independent existing producer contracts are controls for classification scope.
        checks.AddRange(NativeProtocolV15SelfTest.Run().Where(c => c.Name == "native_v15_returned_mask_0"));
        checks.AddRange(V16InitialPreflightVerification.Run().Where(c => c.Name == "v16_initial_preflight_managed_16_0_2_lifecycle_contract_1"));
        checks.AddRange(RunStoresAndRecovery());
        return checks.ToArray();
    }

    internal static OfflineCheck[] RunParser()
    {
        string basis = ReadFixture();
        var rows = Read(basis);
        var source = Source(rows); ulong image = Data(rows, "SOURCE")["Image"]!.GetValue<ulong>();
        ulong owner = Data(rows, "SOURCE")["TargetOwner"]!.GetValue<ulong>();
        var checks = new List<OfflineCheck>
        {
            new("owner_changed_fixture_original_sha", Hash(ReadHistoricalFixture()) == FixtureSha256,
                "Original ff589 saved LOG copied byte-for-byte. No historical intent is settled by parser replay.")
        };
        void Check(string name, string input, bool expected, SourceIdentity? caller = null, ulong? target = null,
            ulong? imageBase = null, int exit = 1, ShopNativeLogPurpose purpose = ShopNativeLogPurpose.NativeRequest)
        {
            try
            {
                var result = ShopNativeOutcome.Parse(input, caller ?? source, imageBase ?? image, target ?? owner, exit, purpose);
                bool passed = expected ? result.SafeToSettle && result.NoEntryConfirmed && result.SourceMatched
                    && !result.Entered && !result.Returned
                    && result.Failure is { Code: "NativePreflightOwnerChanged", Scope: "Global", Phase: "Preflight", Rva: null }
                    : !result.SafeToSettle && !result.NoEntryConfirmed;
                checks.Add(new("owner_changed_" + name, passed, "Production parser; SYNTHETIC historical38A7 grammar/dataflow; no source or recovery attestation; original saved bytes retained. " + result.Detail));
            }
            catch (Exception error) { checks.Add(new("owner_changed_" + name, false, error.ToString())); }
        }
        void Change(string name, bool expected, Action<JsonObject[]> change)
        { var copy = Read(basis); change(copy); Check(name, Write(copy), expected); }
        void Reject(string name, Action<JsonObject[]> change) => Change("reject_" + name, false, change);

        Check("synthetic_historical38A7_force_sell_ten_records", basis, true);
        Check("current79d1_rejects_relabelled_historical_owner_refusal", SyntheticHistoricalContractFixture.RelabelCurrentForRejection(basis), false,
            caller: SyntheticHistoricalContractFixture.CurrentBindingForRejection(source));
        Change("force_buy_ten_records", true, r => Data(r, "START")["Mode"] = "buy");
        Change("previous_pending_preserved", true, r => Data(r, "FORCE_DISPATCH")["PreviousPendingPreserved"] = true);
        var normal = Read(basis).Where(r => Kind(r) != "FORCE_DISPATCH").ToArray();
        Data(normal, "END")["PendingJournal"] = Data(rows, "FORCE_DISPATCH")["PreviousPending"]!.DeepClone();
        Renumber(normal); Check("normal_sell_nine_records", Write(normal), true);
        Data(normal, "START")["Mode"] = "buy"; Check("normal_buy_nine_records", Write(normal), true);

        foreach (int index in new[] { 4, 5 })
        {
            int row = index;
            Change("optional_diagnostic_mismatch_" + row, true, r => r[row]["Data"]!["Pages"]![0]!["CurrentBytesMatched"] = false);
            Change("optional_diagnostic_noaccess_" + row, true, r =>
            {
                var page = r[row]["Data"]!["Pages"]![0]!;
                page["Page"]!["Protect"] = 1; page["CurrentlyReadableExecutable"] = false; page["CurrentBytesMatched"] = null;
            });
            Change("optional_diagnostic_error_" + row, true, r =>
            {
                var page = r[row]["Data"]!["Pages"]![0]!;
                r[row]["Data"]!["Pages"] = new JsonArray(new JsonObject
                { ["Rva"] = page["Rva"]!.DeepClone(), ["Length"] = page["Length"]!.DeepClone(), ["Error"] = "Synthetic optional observation unavailable" });
            });
        }

        Check("reject_caller_pid", basis, false, caller: source with { ProcessId = source.ProcessId + 1 });
        Check("reject_caller_creation", basis, false, caller: source with { CreationFileTime = source.CreationFileTime + 1 });
        Check("reject_caller_path", basis, false, caller: source with { ImagePath = @"D:\offline\other.exe" });
        Check("reject_caller_timestamp", basis, false, caller: source with { PeTimestamp = source.PeTimestamp + 1 });
        Check("reject_caller_image_size", basis, false, caller: source with { ImageSize = source.ImageSize + 4096 });
        Check("reject_caller_sha", basis, false, caller: source with { DiskSha256 = new string('A', 64) });
        Check("reject_caller_owner", basis, false, target: owner + 1);
        Check("reject_caller_image", basis, false, imageBase: image + 4096);
        Check("reject_readonly_purpose", basis, false, purpose: ShopNativeLogPurpose.ReadOnlyPreflight);
        Check("reject_exit_code", basis, false, exit: 0);
        Check("reject_missing_final_newline", basis.TrimEnd('\r', '\n'), false);
        Check("reject_truncated_json", basis[..^5], false);
        Check("reject_duplicate_nested_key", basis.Replace("\"CleanupConfirmed\":true",
            "\"CleanupConfirmed\":true,\"CleanupConfirmed\":true", StringComparison.Ordinal), false);

        // The observed producer path is complete: missing and additional fields at
        // every object depth must not be silently reinterpreted as this proof.
        for (int index = 0; index < rows.Length; index++)
        {
            int row = index;
            Check("reject_missing_record_" + (index + 1), Write(Read(basis).Where((_, n) => n != row)), false);
            foreach (string[] path in ObjectPaths(rows[index]))
            {
                string[] objectPath = path;
                string tag = row + "_" + string.Join("_", objectPath);
                Reject("unknown_field_" + tag, r => ObjectAt(r[row], objectPath)["UnexpectedProducerField"] = true);
                foreach (string field in ObjectAt(rows[index], path).Select(p => p.Key).ToArray())
                {
                    string key = field;
                    Reject("missing_field_" + tag + "_" + key, r => ObjectAt(r[row], objectPath).Remove(key));
                }
            }
            Reject("sequence_gap_" + row, r => r[row]["Sequence"] = row + 2);
            Reject("wrong_flush_" + row, r => r[row]["DiskFlush"] = !r[row]["DiskFlush"]!.GetValue<bool>());
        }
        Reject("time_reversed", r => r[7]["At"] = r[0]["At"]!.DeepClone());
        Reject("time_invalid", r => r[2]["At"] = "not-a-time");
        Reject("wrong_record_order", r => (r[4], r[5]) = (r[5], r[4]));
        foreach (string kind in new[] { "HOOK_INSTALL_INTENT", "HOOK_INSTALL_RESULT", "EVENT_INTENT_DURABLE",
            "COMMAND_PUBLISH_INTENT", "WM_NULL_POST_INTENT", "WM_NULL_POST_RESULT", "EVENT_RESULT", "UNKNOWN_RECORD" })
        {
            var extra = Read(basis).ToList();
            var inserted = (JsonObject)extra[7].DeepClone(); inserted["Kind"] = kind; inserted["Data"] = new JsonObject();
            extra.Insert(7, inserted); Renumber(extra); Check("reject_extra_" + kind, Write(extra), false);
        }

        foreach (var (field, value) in new (string, JsonNode?)[]
        {
            ("Version", JsonValue.Create("14.0.0")), ("ManagedProgramVersion", JsonValue.Create("16.0.1")),
            ("NativeProtocolVersion", JsonValue.Create(16)), ("NativeGuardContract", JsonValue.Create(16)),
            ("NativePayloadVersion", JsonValue.Create("16.0.0")), ("PostCodeRecheckContract", JsonValue.Create(2)),
            ("PostCodeRecheckMaximumMilliseconds", JsonValue.Create(1001)), ("ReturnedLifecycleRecoveryContract", JsonValue.Create(2)),
            ("SubmitPreflightRecheckMaximumMilliseconds", JsonValue.Create(251)), ("Mode", JsonValue.Create("preflight")),
            ("MaximumNativeRequests", JsonValue.Create(2)), ("MouseMessages", JsonValue.Create(1)),
            ("KeyMessages", JsonValue.Create(1)), ("TouchCalls", JsonValue.Create(1)),
            ("ForegroundChanges", JsonValue.Create(1)), ("DirectSelectedOwnerWrites", JsonValue.Create(1)),
            ("SharedOwnershipCounterUpdates", JsonValue.Create(false)), ("OpenProcessAccess", JsonValue.Create("0x1F0FFF")),
            ("CurrentOwnerBits", JsonValue.Create(32)), ("ResearchOnly", JsonValue.Create(false)),
            ("DeferredCodePolicy", JsonValue.Create("unknown")), ("Binding", JsonValue.Create("unknown")),
            ("LogPolicy", JsonValue.Create("unknown"))
        })
            Reject("start_" + field, r => Data(r, "START")[field] = value?.DeepClone());
        foreach (string field in new[] { "Pid", "Creation", "Image", "Hwnd", "Tid", "Shop", "TargetOwner" })
            Reject("source_zero_" + field, r => Data(r, "SOURCE")[field] = 0);
        Reject("source_changed_owner", r => Data(r, "SOURCE")["Owner"] = owner + 1);
        Reject("source_writable_handle", r => Data(r, "SOURCE")["ReadOnlyProcessHandle"] = false);
        Reject("source_wrong_path", r => Data(r, "SOURCE")["Path"] = "other.exe");
        Reject("source_wrong_sha", r => Data(r, "SOURCE")["Sha256"] = new string('A', 64));
        Reject("source_wrong_timestamp", r => Data(r, "SOURCE")["Timestamp"] = "0x1");
        Reject("source_wrong_size", r => Data(r, "SOURCE")["ImageSize"] = "0x1");
        Reject("source_unaligned_shop", r => Data(r, "SOURCE")["Shop"] = 0x10001);
        foreach (string field in new[] { "Bodies", "Bytes", "DeferredMask", "DeferredCodeBytes", "AdditionalDestructorCalls" })
            Reject("guard_" + field, r => Data(r, "CURRENT_NATIVE_CODE_GUARDS")[field] = Data(r, "CURRENT_NATIVE_CODE_GUARDS")[field]!.GetValue<int>() + 1);
        Reject("guard_pending_bytes_claim", r => Data(r, "CURRENT_NATIVE_CODE_GUARDS")["CurrentPendingBytesClaimedMatched"] = true);
        Reject("guard_borrowed_false", r => Data(r, "CURRENT_NATIVE_CODE_GUARDS")["BorrowedScalarSource"] = false);
        foreach (int index in new[] { 4, 5 })
        {
            int row = index;
            foreach (string flag in new[] { "DispatchPrerequisite", "BodyCallableClaimed", "HistoricalBytesSubstituted" })
                Reject("diagnostic_" + row + "_" + flag, r => r[row]["Data"]![flag] = true);
            Reject("diagnostic_" + row + "_wrong_rva", r => r[row]["Data"]!["Rva"] = "0x1");
            Reject("diagnostic_" + row + "_wrong_role", r => r[row]["Data"]!["Role"] = "invoked");
            Reject("diagnostic_" + row + "_wrong_slice", r => r[row]["Data"]!["Pages"]![0]!["Length"] = 1);
            Reject("diagnostic_" + row + "_false_readiness", r => r[row]["Data"]!["Pages"]![0]!["CurrentlyReadableExecutable"] = false);
            Reject("diagnostic_" + row + "_null_readable_match", r => r[row]["Data"]!["Pages"]![0]!["CurrentBytesMatched"] = null);
            Reject("diagnostic_" + row + "_foreign_allocation", r => r[row]["Data"]!["Pages"]![0]!["Page"]!["AllocationBase"] = image + 4096);
            Reject("diagnostic_" + row + "_empty_pages", r => r[row]["Data"]!["Pages"] = new JsonArray());
        }
        Reject("network_wrong_phase", r => Data(r, "NETWORK_SHARED_COUNTS_SAMPLE")["Phase"] = "B");
        Reject("network_wrong_address", r => Data(r, "NETWORK_SHARED_COUNTS_SAMPLE")["AddressHex"] = "0x1");
        Reject("network_wrong_length", r => Data(r, "NETWORK_SHARED_COUNTS_SAMPLE")["Length"] = 4);
        Reject("network_bad_control", r => Data(r, "NETWORK_SHARED_COUNTS_SAMPLE")["NetworkControl"] = 0);
        Reject("network_not_alive", r => Data(r, "NETWORK_SHARED_COUNTS_SAMPLE")["Alive"] = false);
        Reject("network_requires_equality", r => Data(r, "NETWORK_SHARED_COUNTS_SAMPLE")["CountEqualityRequired"] = true);
        foreach (string field in new[] { "Strong", "Weak" })
            foreach (int value in new[] { 0, -1, int.MaxValue })
                Reject("network_" + field + "_" + value, r => Data(r, "NETWORK_SHARED_COUNTS_SAMPLE")["Counts"]![field] = value);
        Reject("owner_wrong_expected", r => Data(r, "CURRENT_SHOP_OWNER_CHANGED")["ExpectedOwner"] = owner + 1);
        Reject("owner_wrong_target", r => Data(r, "CURRENT_SHOP_OWNER_CHANGED")["TargetOwner"] = owner + 1);
        Reject("owner_no_change", r => Data(r, "CURRENT_SHOP_OWNER_CHANGED")["ObservedOwner"] = owner);
        Reject("owner_published", r => Data(r, "CURRENT_SHOP_OWNER_CHANGED")["RequestNotPublished"] = false);
        Reject("owner_truncated_to_32bit", r => Data(r, "CURRENT_SHOP_OWNER_CHANGED")["ObservedOwner"] = uint.MaxValue);
        Reject("error_wrong_type", r => Data(r, "ERROR")["Type"] = "InvalidOperationException");
        Reject("error_wrong_code", r => Data(r, "ERROR")["Code"] = "NativeCodeUnavailable");
        Reject("error_wrong_phase", r => Data(r, "ERROR")["Phase"] = "BeforePublish");
        Reject("error_native_phase", r => Data(r, "ERROR")["Phase"] = "Native");
        Reject("error_nonnull_rva", r => Data(r, "ERROR")["Rva"] = "0x287910");
        Reject("error_message_disagrees", r => Data(r, "ERROR")["Message"] = "Current full 64-bit shop owner changed before dispatch: expected=1, observed=2.");
        foreach (string flag in new[] { "PostCodeConfirmed", "EventResultDurable", "BorrowedSourceContractVerified",
            "NativeAlreadyReturned", "AckObserved", "SessionBlocked", "ListReceivedVerified", "RemoteModuleUnloadProven", "EntireHookTailExitProven" })
            Reject("end_" + flag, r => Data(r, "END")[flag] = true);
        Reject("end_cleanup_false", r => Data(r, "END")["CleanupConfirmed"] = false);
        Reject("end_nonnull_native", r => Data(r, "END")["NativeResult"] = new JsonObject());
        Reject("end_wrong_exit", r => Data(r, "END")["ExitCode"] = 0);
        foreach (string field in new[] { "LogRecordsBeforeEnd", "LogDiskFlushesBeforeEnd", "LogStreamFlushesBeforeEnd", "LogBufferedRecordsBeforeEnd" })
            Reject("end_" + field, r => Data(r, "END")[field] = Data(r, "END")[field]!.GetValue<int>() + 1);
        foreach (string field in new[] { "LogWriteDurationMsBeforeEnd", "LogIoWriteDurationMsBeforeEnd", "LogSerializationDurationMsBeforeEnd" })
            Reject("end_negative_" + field, r => Data(r, "END")[field] = -1);
        Reject("end_duration_sum", r => Data(r, "END")["LogWriteDurationMsBeforeEnd"] = 1);
        Reject("end_wrong_single_schema", r => Data(r, "END")["SingleTrialPendingSchema"] = "unknown");
        Reject("end_wrong_single_path", r => Data(r, "END")["SingleTrialPendingJournal"] = "foreign.pending.json");
        Reject("force_wait", r => Data(r, "FORCE_DISPATCH")["WaitForPreviousGameState"] = true);
        Reject("force_previous_wrong_identity", r => Data(r, "FORCE_DISPATCH")["PreviousPending"] = @"D:\lineage\LC_PlayerShopListAction_v1\journal\1-1.pending.json");
        Reject("force_current_disagrees", r => Data(r, "FORCE_DISPATCH")["CurrentPending"] = Data(r, "FORCE_DISPATCH")["PreviousPending"]!.DeepClone());
        Reject("force_end_disagrees", r => Data(r, "END")["PendingJournal"] = Data(r, "FORCE_DISPATCH")["PreviousPending"]!.DeepClone());
        Reject("force_unsafe_guid", r =>
        {
            string path = Data(r, "END")["PendingJournal"]!.GetValue<string>().Replace("56a1cb35931949a38ceef5db3558fb7d", "NOT-A-GUID", StringComparison.Ordinal);
            Data(r, "END")["PendingJournal"] = path; Data(r, "FORCE_DISPATCH")["CurrentPending"] = path;
        });
        return checks.ToArray();
    }

    private static OfflineCheck[] RunStoresAndRecovery()
    {
        var checks = new List<OfflineCheck>();
        void Check(string name, Action work)
        {
            try { work(); checks.Add(new("owner_changed_integration_" + name, true,
                "Saved receipt rebound to a synthetic process identity; production policy/stores/recovery; zero native helper/game calls.")); }
            catch (Exception error) { checks.Add(new("owner_changed_integration_" + name, false, error.ToString())); }
        }
        foreach (bool force in new[] { false, true }) Check((force ? "force" : "normal") + "_settles_only_unsent_page_and_stops_batch", () =>
        {
            // Immediate completion is NativeIntent -> NativeSettled -> Result.
            // Historical blocked Result fixtures instead use startup recovery,
            // which validates/backups the old terminal before replacing it.
            var f = new RecoveryFixture(force, includeBlockedTerminal: false);
            var native = f.Parse();
            Need(native.SafeToSettle && native.NoEntryConfirmed && !native.Entered && !native.Returned, native.Detail);
            var response = native.Failure!.Attach(new() { RequestId = f.RequestId, State = "NativeRejected",
                NativeEntered = native.Entered, NativeReturned = native.Returned, SessionBlocked = !native.SafeToSettle });
            Need(!ShopNativePendingPolicy.BlocksResponse(response)
                && ShopScanPolicy.Decide(response, true, force) == ShopScanDisposition.StopBatch,
                "The rejected page must stop the batch without becoming a native-unknown lock.");
            f.Owners.Settle(f.RequestId, native.Returned);
            f.Scans.NativeSettled(f.RequestId, native.Entered, native.Returned, native.Detail);
            f.Scans.Result(f.RequestId, response); f.Reload();
            var view = f.Scans.View(f.Source, f.Targets, false, false, force)!;
            Need(!f.Scans.BlocksSource(f.Source) && !f.Owners.BlocksSource(f.Source)
                && view.UncertainPages == 0 && view.SearchedPages == 0 && view.ConfirmedPages == 0 && view.RemainingPages == 2,
                "No-entry must remain NotSent and cannot claim a completed search or product response.");
            Need(f.Scans.Continue(f.Plan.PlanId, f.Source, f.Targets, forceDispatch: force).Length == 2,
                "A later explicit continuation could not select the still-unsent pages.");
            f.Scans.BeginAttempt(f.Page, f.RequestId + "-explicit-next");
            Need(f.Owners.ShouldSkip(f.Owner.Id, f.Direction, force) == false, "The unsent owner page was incorrectly marked searched.");
        });
        Check("uncertain_receipt_keeps_original_locks", () =>
        {
            var f = new RecoveryFixture(true);
            f.ChangeNative(r => Data(r, "END")["CleanupConfirmed"] = false);
            var native = f.Parse();
            var response = (native.Failure ?? ShopFailure.Unknown(native.Detail)).Attach(new()
            { NativeEntered = native.Entered, NativeReturned = native.Returned, SessionBlocked = !native.SafeToSettle });
            Need(!native.SafeToSettle && !native.NoEntryConfirmed && ShopNativePendingPolicy.BlocksResponse(response)
                && ShopScanPolicy.Decide(response, true, true) == ShopScanDisposition.StopBatch,
                "Force dispatch accepted an incomplete cleanup proof.");
            Need(f.Scans.BlocksSource(f.Source) && f.Owners.BlocksSource(f.Source) && File.Exists(f.Pending), "Unknown original latches were lost.");
        });
        foreach (bool force in new[] { false, true }) Check((force ? "force" : "normal") + "_startup_recovery_stays_stopped_and_preserves_evidence", () =>
        {
            var f = new RecoveryFixture(force);
            string nativeHash = FileHash(f.Native), responseHash = FileHash(f.Response), intentHash = FileHash(f.Intent);
            string terminalHash = FileHash(f.Terminal), pendingHash = FileHash(f.Pending);
            var result = f.Recover();
            Need(result is [{ State: "RecoveredStopped" }], JsonSerializer.Serialize(result, HubProtocol.Json));
            f.Reload(); var view = f.Scans.View(f.Source, f.Targets, false, false, force)!;
            Need(!File.Exists(f.Pending) && !f.Scans.BlocksSource(f.Source) && !f.Owners.BlocksSource(f.Source)
                && view.UncertainPages == 0 && view.SearchedPages == 0 && view.ConfirmedPages == 0 && view.RemainingPages == 2,
                "Startup recovery did not settle all original latches as NotSent.");
            string backup = result[0].BackupDirectory!;
            Need(FileHash(f.Native) == nativeHash && FileHash(f.Response) == responseHash && FileHash(f.Intent) == intentHash
                && FileHash(Path.Combine(backup, "original-terminal-result.json")) == terminalHash
                && FileHash(Path.Combine(backup, "original-session.pending.json")) == pendingHash,
                "An original native receipt, response, intent, failed terminal or pending backup changed.");
            Need(Directory.GetFiles(Path.Combine(f.Root, "sessions"), "*.resolved-preflight-*.json").Length == 1
                && f.Recover().Length == 0, "Repeated startup created duplicate recovery or failed to preserve the resolved pending.");
            if (force) Need(File.Exists(Path.Combine(backup, "force-binding.json"))
                && Directory.GetFiles(Path.Combine(backup, "original-owner-ledger"), "*", SearchOption.AllDirectories).Length > 0,
                "Force recovery lacks its complete bound evidence and original owner ledger backup.");
        });
        foreach (string phase in new[] { "ForceProofSaved", "TerminalSettled", "ResultRestored", "OwnerSettled" })
            Check("force_reentry_after_" + phase, () =>
            {
                var f = new RecoveryFixture(true); string hash = FileHash(f.Native); bool reached = false;
                var interrupted = f.Recover(checkpoint: point => { if (point == phase) { reached = true; throw new IOException("Synthetic crash at " + phase); } });
                Need(reached && interrupted is [{ State: "Deferred" }] && File.Exists(f.Pending),
                    "The expected interruption boundary was not exercised or pending was retired early.");
                f.Reload(); var retried = f.Recover();
                Need(retried is [{ State: "RecoveredStopped" }], JsonSerializer.Serialize(retried, HubProtocol.Json));
                f.Reload(); Need(!f.Scans.BlocksSource(f.Source) && !f.Owners.BlocksSource(f.Source)
                    && FileHash(f.Native) == hash && f.Recover().Length == 0, "Interrupted recovery did not converge without rewriting native evidence.");
            });
        foreach (string mutation in new[] { "native_incomplete", "mode_mismatch", "pending_force_false", "pending_owner_identity",
            "pending_sampled_owner", "pending_wrong_hash", "request_signature", "request_owner", "request_force_false",
            "response_other_request", "response_entered", "response_returned", "response_verified", "response_settled",
            "response_unknown_code", "response_wrong_phase", "response_wrong_scope", "terminal_entered", "wrong_native_root", "missing_request", "missing_response" })
            Check("force_reject_" + mutation, () =>
            {
                var f = new RecoveryFixture(true);
                switch (mutation)
                {
                    case "native_incomplete": f.ChangeNative(r => Data(r, "END")["CleanupConfirmed"] = false); break;
                    case "mode_mismatch": f.ChangeNative(r => Data(r, "START")["Mode"] = "buy"); break;
                    case "pending_force_false": ChangeFile(f.Pending, n => n["forceDispatch"] = false); break;
                    case "pending_owner_identity": ChangeFile(f.Pending, n => n["owner"]!["localIdentity"] = "foreign-owner"); break;
                    case "pending_sampled_owner": ChangeFile(f.Pending, n => n["currentOwnerIdRaw"] = 1); break;
                    case "pending_wrong_hash": f.RenamePendingWrongHash(); break;
                    case "request_signature": ChangeFile(f.Request, n => n["signature"] = new string('A', 64)); break;
                    case "request_owner": ChangeFile(f.Request, n => n["request"]!["ownerId"] = 1); break;
                    case "request_force_false": ChangeFile(f.Request, n => n["request"]!["forceDispatch"] = false); break;
                    case "response_other_request": ChangeFile(f.Response, n => n["requestId"] = "other"); break;
                    case "response_entered": ChangeFile(f.Response, n => n["nativeEntered"] = true); break;
                    case "response_returned": ChangeFile(f.Response, n => n["nativeReturned"] = true); break;
                    case "response_verified": ChangeFile(f.Response, n => n["responseVerified"] = true); break;
                    case "response_settled": ChangeFile(f.Response, n => n["workSettled"] = true); break;
                    case "response_unknown_code": ChangeFile(f.Response, n => n["failureCode"] = "NativeOutcomeUnknown"); break;
                    case "response_wrong_phase": ChangeFile(f.Response, n => n["failurePhase"] = "Native"); break;
                    case "response_wrong_scope": ChangeFile(f.Response, n => n["failureScope"] = "Session"); break;
                    case "terminal_entered": ChangeFile(f.Terminal, n => n["nativeEntered"] = true); f.Reload(); break;
                    case "missing_request": File.Move(f.Request, f.Request + ".preserved-missing-fixture"); break;
                    case "missing_response": File.Move(f.Response, f.Response + ".preserved-missing-fixture"); break;
                }
                string nativeHash = FileHash(f.Native), pendingHash = FileHash(f.Pending);
                string responsePath = File.Exists(f.Response) ? f.Response : f.Response + ".preserved-missing-fixture";
                string responseHash = FileHash(responsePath);
                var result = f.Recover(nativeRoot: mutation == "wrong_native_root" ? Path.Combine(f.Root, "foreign-native") : null);
                Need(result is [{ State: "Deferred" }] && File.Exists(f.Pending)
                    && f.Scans.BlocksSource(f.Source) && f.Owners.BlocksSource(f.Source), JsonSerializer.Serialize(result, HubProtocol.Json));
                Need(FileHash(f.Native) == nativeHash && FileHash(f.Pending) == pendingHash && FileHash(responsePath) == responseHash,
                    "A rejected recovery modified its original evidence.");
            });
        Check("force_busy_keeps_all_locks", () =>
        {
            var f = new RecoveryFixture(true); var result = f.Recover(busy: () => true);
            Need(result is [{ State: "Deferred" }] && File.Exists(f.Pending)
                && f.Scans.BlocksSource(f.Source) && f.Owners.BlocksSource(f.Source), "Busy recovery changed the pending work.");
        });
        Check("force_busy_after_proof_keeps_pending_and_can_resume", () =>
        {
            var f = new RecoveryFixture(true); bool busy = false;
            var result = f.Recover(busy: () => busy, checkpoint: phase => { if (phase == "ForceProofSaved") busy = true; });
            Need(busy && result is [{ State: "Deferred" }] && File.Exists(f.Pending), "Work arriving after durable proof was ignored.");
            f.Reload(); Need(f.Recover() is [{ State: "RecoveredStopped" }], "Busy-deferred recovery could not resume.");
        });
        Check("presentation_verified_no_entry_allows_explicit_restart", () =>
        {
            var response = new ShopCommandResponse { State = "NativeRejected", FailureCode = "NativePreflightOwnerChanged",
                FailureScope = "Global", FailurePhase = "Preflight", Detail = "saved dedicated proof" };
            string text = MarketFailurePresentation.Format(response, true);
            Need(text.Contains("已核對本頁未派送且清理完成", StringComparison.Ordinal)
                && text.Contains("可重新開始", StringComparison.Ordinal)
                && text.Contains("不會阻擋下一個使用者指令", StringComparison.Ordinal), text);
        });
        Check("presentation_old_generic_error_never_promises_restart", () =>
        {
            var response = new ShopCommandResponse { State = "Unverified", FailureCode = "ContextUnavailable",
                FailureScope = "Global", FailurePhase = "Preflight", SessionBlocked = true,
                Detail = "Current full 64-bit shop owner changed before dispatch: expected=1, observed=2." };
            string text = MarketFailurePresentation.Format(response, true);
            Need(text.Contains("尚未取得完整未派送證據", StringComparison.Ordinal)
                && !text.Contains("不會阻擋下一個使用者指令", StringComparison.Ordinal)
                && !text.Contains("可直接下達下一指令", StringComparison.Ordinal), text);
        });
        foreach (string kind in new[] { "blocked", "entered_not_returned", "session_scope", "unknown_scope", "blocked_poll" })
            Check("presentation_" + kind + "_never_promises_restart", () =>
            {
                var response = new ShopCommandResponse { State = kind == "blocked_poll" ? "State" : "Unverified",
                    FailureCode = "NativeOutcomeUnknown", FailureScope = kind == "unknown_scope" ? "Unknown" : kind == "session_scope" ? "Session" : "Global",
                    SessionBlocked = kind is "blocked" or "blocked_poll", NativeEntered = kind == "entered_not_returned",
                    Detail = "synthetic unresolved native result" };
                string text = kind == "blocked_poll" ? MarketFailurePresentation.Status(response, true) : MarketFailurePresentation.Format(response, true);
                Need(!text.Contains("不會阻擋下一個使用者指令", StringComparison.Ordinal)
                    && !text.Contains("可直接下達下一指令", StringComparison.Ordinal), text);
            });
        return checks.ToArray();
    }

    private sealed class RecoveryFixture
    {
        internal readonly string Root, NativeRoot, Native, RequestId, Direction, Request, Response, Intent;
        internal string Pending;
        internal readonly SourceIdentity Source;
        internal readonly MarketOwner Owner;
        internal readonly ShopTargetSnapshot Targets;
        internal readonly SavedScanPlan Plan;
        internal readonly SavedScanPage Page;
        internal ShopScanProgressStore Scans;
        internal ShopSearchedOwnerStore Owners;
        internal string Terminal => Directory.GetFiles(Path.Combine(Root, "scan-progress", Plan.PlanId + ".events"), "*.json").Order(StringComparer.Ordinal).Last();

        internal RecoveryFixture(bool force, bool includeBlockedTerminal = true)
        {
            Root = Path.Combine(Path.GetTempPath(), "LC-Market70-owner-change-" + Guid.NewGuid().ToString("N"));
            NativeRoot = Path.Combine(Root, "saved-diagnostics", "native");
            Directory.CreateDirectory(NativeRoot); Directory.CreateDirectory(Path.Combine(Root, "sessions"));
            Directory.CreateDirectory(Path.Combine(Root, "requests"));
            RequestId = "owner-change-fixture-" + Guid.NewGuid().ToString("N"); Direction = ShopPageModes.ViewerSell;
            var rows = Read(ReadFixture()); var identity = Data(rows, "SOURCE");
            string oldStem = identity["Pid"]!.GetValue<int>() + "-" + identity["Creation"]!.GetValue<long>();
            // Deliberately nonexistent process identity: never bind these isolated
            // recovery tests to an active LC or its actual shared journal prefix.
            identity["Pid"] = 2147000000; identity["Creation"] = 130000000000000000L;
            Source = OwnerChangedPreflightVerification.Source(rows) with { SessionKey = "synthetic-owner-change-" + Guid.NewGuid().ToString("N") };
            string stem = Source.ProcessId + "-" + Source.CreationFileTime;
            foreach (string journalField in new[] { "PendingJournal", "SingleTrialPendingJournal" })
                Data(rows, "END")[journalField] = Data(rows, "END")[journalField]!.GetValue<string>().Replace(oldStem, stem, StringComparison.Ordinal);
            foreach (string journalField in new[] { "PreviousPending", "CurrentPending" })
                Data(rows, "FORCE_DISPATCH")[journalField] = Data(rows, "FORCE_DISPATCH")[journalField]!.GetValue<string>().Replace(oldStem, stem, StringComparison.Ordinal);
            if (!force)
            {
                Data(rows, "END")["PendingJournal"] = Data(rows, "FORCE_DISPATCH")["PreviousPending"]!.DeepClone();
                rows = rows.Where(row => Kind(row) != "FORCE_DISPATCH").ToArray(); Renumber(rows);
            }
            Native = Path.Combine(NativeRoot, Guid.NewGuid().ToString("N") + ".jsonl"); File.WriteAllText(Native, Write(rows));
            var at = DateTimeOffset.UtcNow;
            Owner = new() { Id = identity["TargetOwner"]!.GetValue<ulong>(), LocalIdentity = "synthetic-owner-identity", Name = "離線測試店主",
                Position = new(101, 100), ObservedAt = at, ShopState = ShopEligibilityPolicy.ShopActionObserved,
                ActionType = ".?AVShopAction@lineage@@", ShopStateSampledAt = at, ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence };
            Targets = new() { SnapshotId = "synthetic-snapshot-" + Guid.NewGuid().ToString("N"), SourceSessionKey = Source.SessionKey,
                RefreshedAt = at, SelfId = 11, SelfPosition = new(100, 100), Owners = [Owner], CanDispatch = true };
            Scans = new(Path.Combine(Root, "scan-progress"));
            Owners = new(Path.Combine(Root, "searched-owners"), Path.Combine(Root, "scan-progress"));
            Need(Scans.Error is null && Owners.Error is null, Scans.Error + Owners.Error);
            Plan = Scans.StartNew(Source, Targets, forceDispatch: force);
            Page = Scans.PendingPages(Targets, force).Single(page => page.Direction == Direction);
            Scans.BeginAttempt(Page, RequestId); Scans.NativeIntent(RequestId);
            Owners.Begin(Source, Owner, Direction, RequestId, forceDispatch: force);
            var response = new ShopCommandResponse { RequestId = RequestId, CurrentSourceSessionKey = Source.SessionKey,
                State = force ? "Unverified" : "NativeRejected", FailureCode = "ContextUnavailable", FailureScope = "Global",
                FailurePhase = "Preflight", FailureRva = null, SessionBlocked = true, WorkSettled = false,
                NativeEntered = false, NativeReturned = false, ResponseVerified = false, Detail = "Original saved owner-change failure; synthetic local stores." };
            if (includeBlockedTerminal) Scans.Result(RequestId, response);
            string key = Hash(RequestId); Intent = Path.Combine(Root, "scan-progress", "source-intents", key + ".intent.json");
            Pending = force ? ShopForceDispatchPolicy.PendingPath(Root, stem, RequestId) : Path.Combine(Root, "sessions", stem + ".pending.json");
            WriteNew(Pending, new { State = force ? "ForceNativeLaunchIntent_NoAutomaticReplay" : "NativeLaunchIntent_NoAutomaticRetry",
                RequestId, ForceDispatch = force, Source, Owner, CurrentOwnerIdRaw = identity["Owner"]!.GetValue<ulong>(),
                TargetOwner = Owner.Id, ViewerDirection = Direction, NativeLog = Native, At = at });
            var request = new ShopCommandRequest { RequestId = RequestId, Command = ShopCommands.OpenShop, ForceDispatch = force,
                ExpectedSourceSessionKey = Source.SessionKey, OwnerId = Owner.Id, ExpectedOwnerIdentity = Owner.LocalIdentity,
                ViewerDirection = Direction, TargetSnapshotId = Targets.SnapshotId };
            Request = Path.Combine(Root, "requests", key + ".request.json"); Response = Path.Combine(Root, "requests", key + ".response.json");
            WriteNew(Request, new { State = "ScanChild_NoAutomaticReplay", Request = request,
                Signature = Hash(JsonSerializer.Serialize(request, HubProtocol.Json)), At = at }); WriteNew(Response, response);
        }
        internal void Reload()
        {
            Scans = new(Path.Combine(Root, "scan-progress"));
            Owners = new(Path.Combine(Root, "searched-owners"), Path.Combine(Root, "scan-progress"));
        }
        internal ShopNativeOutcome Parse() => ShopNativeOutcome.ParseFile(Native, Source,
            ulong.Parse(Source.ImageBase[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture), Owner.Id, 1);
        internal void ChangeNative(Action<JsonObject[]> change)
        { var rows = Read(File.ReadAllText(Native)); change(rows); File.WriteAllText(Native, Write(rows)); }
        internal StartupRecoveryItem[] Recover(Func<bool>? busy = null, Action<string>? checkpoint = null, string? nativeRoot = null) =>
            ShopStartupRecovery.Run(Root, Scans, Owners, busyCheck: busy ?? (() => false), checkpoint: checkpoint, nativeLogDirectory: nativeRoot ?? NativeRoot);
        internal void RenamePendingWrongHash()
        {
            string renamed = ShopForceDispatchPolicy.PendingPath(Root, Source.ProcessId + "-" + Source.CreationFileTime, RequestId + "-wrong");
            File.Move(Pending, renamed); Pending = renamed;
        }
    }
    private static void ChangeFile(string path, Action<JsonNode> change)
    { var node = JsonNode.Parse(File.ReadAllText(path))!; change(node); File.WriteAllText(path, node.ToJsonString()); }
    private static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void WriteNew<T>(string path, T value)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var file = new FileStream(path, FileMode.CreateNew); JsonSerializer.Serialize(file, value, HubProtocol.Json); file.Flush(true); }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static string ReadFixture()=>SyntheticHistoricalContractFixture.FromSavedShape(ReadHistoricalFixture());
    private static string ReadHistoricalFixture()
    {
        var assembly = typeof(OwnerChangedPreflightVerification).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("." + FixtureName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException("Missing saved owner-change receipt.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }
    private static SourceIdentity Source(JsonObject[] rows)
    {
        var identity = Data(rows, "SOURCE");
        uint Hex(string name) => uint.Parse(identity[name]!.GetValue<string>()[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new(identity["Pid"]!.GetValue<int>(), identity["Creation"]!.GetValue<long>(), identity["Path"]!.GetValue<string>(),
            "0x" + identity["Image"]!.GetValue<ulong>().ToString("X", CultureInfo.InvariantCulture), Hex("Timestamp"), Hex("ImageSize"),
            identity["Sha256"]!.GetValue<string>(), "saved-owner-change-regression-only");
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static JsonObject[] Read(string value) => value.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
    private static string Write(IEnumerable<JsonObject> rows) => string.Join('\n', rows.Select(row => row.ToJsonString())) + "\n";
    private static string Kind(JsonObject row) => row["Kind"]!.GetValue<string>();
    private static JsonObject Data(JsonObject[] rows, string kind) => rows.Single(row => Kind(row) == kind)["Data"]!.AsObject();
    private static void Renumber(IList<JsonObject> rows)
    {
        for (int i = 0; i < rows.Count; i++) rows[i]["Sequence"] = i + 1;
        var end = rows[^1]["Data"]!;
        end["LogRecordsBeforeEnd"] = rows.Count - 1;
        end["LogDiskFlushesBeforeEnd"] = rows.Take(rows.Count - 1).Count(row => row["DiskFlush"]!.GetValue<bool>());
        end["LogBufferedRecordsBeforeEnd"] = rows.Take(rows.Count - 1).Count(row => !row["DiskFlush"]!.GetValue<bool>());
    }
    private static IEnumerable<string[]> ObjectPaths(JsonNode node, string[]? path = null)
    {
        path ??= [];
        if (node is JsonObject obj)
        {
            yield return path;
            foreach (var pair in obj)
                if (pair.Value is JsonObject or JsonArray)
                    foreach (var child in ObjectPaths(pair.Value, [.. path, pair.Key])) yield return child;
        }
        else if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
                if (array[i] is JsonObject or JsonArray)
                    foreach (var child in ObjectPaths(array[i]!, [.. path, i.ToString(CultureInfo.InvariantCulture)])) yield return child;
    }
    private static JsonObject ObjectAt(JsonNode root, string[] path)
    {
        foreach (string part in path) root = root is JsonArray ? root[int.Parse(part, CultureInfo.InvariantCulture)]! : root[part]!;
        return root.AsObject();
    }
}
