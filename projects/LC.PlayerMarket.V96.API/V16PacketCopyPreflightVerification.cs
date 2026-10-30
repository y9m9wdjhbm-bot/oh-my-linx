using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static class V16PacketCopyPreflightVerification
{
    internal static int RunReport(string output)
    {
        var checks = Run();
        var report = new OfflineCheckReport("83.0.0", checks.All(c => c.Passed), checks.Count(c => c.Passed),
            checks.Count(c => !c.Passed), false, false, false, checks);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, HubProtocol.Json); file.Flush(true);
        return report.Success ? 0 : 1;
    }
    internal static OfflineCheck[] Run()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames()
            .Single(x => x.EndsWith("packet-copy-preflight-v1.jsonl", StringComparison.Ordinal)))!;
        using var reader = new StreamReader(stream);
        string fixture = reader.ReadToEnd();
        JsonObject[] Read(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonNode.Parse(x)!.AsObject()).ToArray();
        string Write(IEnumerable<JsonObject> rows) => string.Join('\n', rows.Select(x => x.ToJsonString())) + "\n";
        JsonNode Data(JsonObject[] rows, string kind) => rows.Single(x => x["Kind"]!.GetValue<string>() == kind)["Data"]!;
        var original = Read(fixture); var identity = Data(original, "SOURCE");
        ulong image = identity["Image"]!.GetValue<ulong>(), owner = identity["TargetOwner"]!.GetValue<ulong>();
        var source = new SourceIdentity(identity["Pid"]!.GetValue<int>(), identity["Creation"]!.GetValue<long>(),
            identity["Path"]!.GetValue<string>(), "0x" + image.ToString("X"), ShopNativeHistoricalPackage.SupportedTimestamp,
            ShopNativeHistoricalPackage.SupportedImageSize, ShopNativeHistoricalPackage.SupportedSha, "recorded-parser-replay-only");
        var checks = new List<OfflineCheck>();
        void Check(string name, Func<bool> test)
        {
            try { bool passed = test(); checks.Add(new("packet_copy_preflight_" + name, passed, passed ? "ok" : "contract mismatch")); }
            catch (Exception ex) { checks.Add(new("packet_copy_preflight_" + name, false, ex.ToString())); }
        }
        ShopNativeOutcome Parse(string text, int exit = 1, ShopNativeLogPurpose purpose = ShopNativeLogPurpose.NativeRequest)
            => ShopNativeOutcome.Parse(text, source, image, owner, exit, purpose);
        bool Good(string text)
        {
            var result = Parse(text);
            return result.SafeToSettle && result.NoEntryConfirmed && result.SourceMatched && !result.Entered && !result.Returned
                && result.Failure is { Code: "NativeCodeUnavailable", Scope: "Global", Phase: "Preflight", Rva: "0x76BEB0" };
        }
        void Reject(string name, Action<JsonObject[]> mutate) => Check(name, () =>
        {
            var rows = Read(fixture); mutate(rows); var result = Parse(Write(rows));
            return !result.SafeToSettle && !result.NoEntryConfirmed;
        });
        void RejectText(string name, Func<string, string> mutate) => Check(name, () =>
        {
            var result = Parse(mutate(Write(Read(fixture))));
            return !result.SafeToSettle && !result.NoEntryConfirmed;
        });
        string ReplaceOne(string text, string before, string after)
        {
            int index = text.IndexOf(before, StringComparison.Ordinal);
            if (index < 0 || text.IndexOf(before, index + before.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("Mutation requires one exact anchor.");
            return text[..index] + after + text[(index + before.Length)..];
        }
        Check("current79d1_rejects_relabelled_historical_packet_copy", () =>
        {
            var rejected=ShopNativeOutcome.Parse(SyntheticHistoricalContractFixture.RelabelCurrentForRejection(fixture),
                SyntheticHistoricalContractFixture.CurrentBindingForRejection(source), image, owner, 1, ShopNativeLogPurpose.NativeRequest);
            return !rejected.SafeToSettle && !rejected.NoEntryConfirmed;
        });
        Check("recorded_six_rows_settle_as_unpublished", () => Good(fixture));
        Check("normalized_fixture_with_newline_still_accepted", () => Good(Write(Read(fixture))));
        Check("failed_page_stops_batch_without_pending_block", () =>
        {
            var result = Parse(fixture);
            var response = result.Failure!.Attach(new ShopCommandResponse { SessionBlocked = !result.SafeToSettle });
            return result.NoEntryConfirmed && !response.SessionBlocked && ShopScanPolicy.Decide(response, true, true) == ShopScanDisposition.StopBatch;
        });
        Check("normal_mode_complete_path", () =>
        {
            var rows = Read(fixture).Where(x => x["Kind"]!.GetValue<string>() != "FORCE_DISPATCH").ToArray();
            Data(rows, "END")["PendingJournal"] = Data(original, "FORCE_DISPATCH")["PreviousPending"]!.DeepClone();
            for (int i = 0; i < rows.Length; i++) rows[i]["Sequence"] = i + 1;
            Data(rows, "END")["LogRecordsBeforeEnd"] = rows.Length - 1;
            Data(rows, "END")["LogBufferedRecordsBeforeEnd"] = rows.Length - 2;
            return Good(Write(rows));
        });
        foreach (string field in new[] { "PostCodeConfirmed", "EventResultDurable", "BorrowedSourceContractVerified", "NativeAlreadyReturned", "AckObserved", "ListReceivedVerified", "RemoteModuleUnloadProven", "EntireHookTailExitProven", "SessionBlocked" })
            Reject("reject_end_" + field, rows => Data(rows, "END")[field] = true);
        Reject("reject_cleanup_missing", rows => Data(rows, "END")["CleanupConfirmed"] = false);
        Reject("reject_native_result", rows => Data(rows, "END")["NativeResult"] = new JsonObject());
        Reject("reject_managed_version", rows => Data(rows, "START")["ManagedProgramVersion"] = "16.0.3");
        Reject("reject_native_protocol", rows => Data(rows, "START")["NativeProtocolVersion"] = 14);
        Reject("reject_guard_contract", rows => Data(rows, "START")["NativeGuardContract"] = 14);
        Reject("reject_request_mode", rows => Data(rows, "START")["Mode"] = "--preflight");
        Reject("reject_request_count", rows => Data(rows, "START")["MaximumNativeRequests"] = 2);
        Reject("reject_post_contract", rows => Data(rows, "START")["PostCodeRecheckContract"] = 0);
        Reject("reject_lifecycle_contract", rows => Data(rows, "START")["ReturnedLifecycleRecoveryContract"] = 0);
        Reject("reject_deferred_policy", rows => Data(rows, "START")["DeferredCodePolicy"] = "unknown");
        Reject("reject_pid", rows => Data(rows, "SOURCE")["Pid"] = source.ProcessId + 1);
        Reject("reject_creation", rows => Data(rows, "SOURCE")["Creation"] = source.CreationFileTime + 1);
        Reject("reject_owner", rows => Data(rows, "SOURCE")["TargetOwner"] = owner + 1);
        Reject("reject_build", rows => Data(rows, "SOURCE")["Sha256"] = new string('0', 64));
        Reject("reject_source_handle", rows => Data(rows, "SOURCE")["ReadOnlyProcessHandle"] = false);
        Reject("reject_image", rows => Data(rows, "SOURCE")["Image"] = image + 0x10000);
        Reject("reject_image_path", rows => Data(rows, "SOURCE")["Path"] = @"D:\different\LC.exe");
        Reject("reject_pe_timestamp", rows => Data(rows, "SOURCE")["Timestamp"] = "0x6ACEDB9B");
        Reject("reject_image_size", rows => Data(rows, "SOURCE")["ImageSize"] = "0x3F60000");
        Reject("reject_current_journal", rows => Data(rows, "FORCE_DISPATCH")["CurrentPending"] = "other.pending.json");
        Reject("reject_single_trial_schema", rows => Data(rows, "END")["SingleTrialPendingSchema"] = "unknown");
        Reject("reject_wait_previous", rows => Data(rows, "FORCE_DISPATCH")["WaitForPreviousGameState"] = true);
        Reject("reject_cross_process_force_journal", rows =>
        {
            string path = Data(rows, "FORCE_DISPATCH")["CurrentPending"]!.GetValue<string>()
                .Replace(source.ProcessId + "-", (source.ProcessId + 1) + "-", StringComparison.Ordinal);
            Data(rows, "FORCE_DISPATCH")["CurrentPending"] = path;
            Data(rows, "END")["PendingJournal"] = path;
        });
        Reject("reject_cross_process_single_trial", rows => Data(rows, "END")["SingleTrialPendingJournal"] =
            Data(rows, "END")["SingleTrialPendingJournal"]!.GetValue<string>()
                .Replace(source.ProcessId + "-", (source.ProcessId + 1) + "-", StringComparison.Ordinal));
        Reject("reject_rva", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Rva"] = "0x287910");
        Reject("reject_phase", rows => Data(rows, "ERROR")["Phase"] = "BeforePublish");
        Reject("reject_page_readable", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Page"]!["Protect"] = 0x20);
        Reject("reject_page_allocation", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Page"]!["AllocationBase"] = image + 4096);
        Reject("reject_page_extent", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Page"]!["Size"] = 1);
        Reject("reject_page_exactly_one_byte_short", rows =>
        {
            var page = Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Page"]!;
            page["Base"] = image + 0x76BEB0;
            page["Size"] = 66;
        });
        Reject("reject_page_state", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Page"]!["State"] = 0x2000);
        Reject("reject_page_type", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Page"]!["Type"] = 0x20000);
        Reject("reject_sequence", rows => rows[2]["Sequence"] = 10);
        Reject("reject_error_not_durable", rows => rows[^2]["DiskFlush"] = false);
        Reject("reject_end_not_durable", rows => rows[^1]["DiskFlush"] = false);
        Reject("reject_log_count", rows => Data(rows, "END")["LogRecordsBeforeEnd"] = 0);
        Reject("reject_log_duration", rows => Data(rows, "END")["LogWriteDurationMsBeforeEnd"] = -1);
        Reject("reject_log_sum_mismatch", rows => Data(rows, "END")["LogWriteDurationMsBeforeEnd"] =
            Data(rows, "END")["LogWriteDurationMsBeforeEnd"]!.GetValue<double>() + 1);
        foreach (string kind in new[] { "START", "FORCE_DISPATCH", "SOURCE", "NATIVE_CODE_PAGE_UNAVAILABLE", "ERROR", "END" })
        {
            Reject("reject_missing_at_" + kind, rows => rows.Single(x => x["Kind"]!.GetValue<string>() == kind).Remove("At"));
            Reject("reject_extra_data_field_" + kind, rows => Data(rows, kind)["UnsupportedProducerField"] = true);
        }
        Reject("reject_missing_required_start_field", rows => Data(rows, "START").AsObject().Remove("Binding"));
        Reject("reject_missing_required_page_field", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Page"]!.AsObject().Remove("PartitionId"));
        Reject("reject_extra_row_field", rows => rows[^1]["UnsupportedProducerField"] = true);
        Reject("reject_extra_page_field", rows => Data(rows, "NATIVE_CODE_PAGE_UNAVAILABLE")["Page"]!["UnsupportedProducerField"] = true);
        Reject("reject_invalid_at", rows => rows[^1]["At"] = "invalid-time");
        Reject("reject_default_at", rows => rows[0]["At"] = "0001-01-01T00:00:00+00:00");
        Reject("reject_reversed_at", rows => rows[^1]["At"] = rows[0]["At"]!.DeepClone());
        Check("equal_adjacent_at_allowed", () =>
        {
            var rows = Read(fixture); rows[^1]["At"] = rows[^2]["At"]!.DeepClone();
            return Good(Write(rows));
        });
        RejectText("reject_missing_final_newline", text => text[..^1]);
        RejectText("reject_blank_intermediate_line", text => text.Insert(text.IndexOf('\n') + 1, "\n"));
        RejectText("reject_extra_final_blank_line", text => text + "\n");
        RejectText("reject_duplicate_row_property", text => ReplaceOne(text, "\"Kind\":\"END\"", "\"Kind\":\"ERROR\",\"Kind\":\"END\""));
        RejectText("reject_nested_duplicate_page_property", text => ReplaceOne(text, "\"Page\":{", "\"Page\":{\"Protect\":32,"));
        RejectText("reject_duplicate_end_claim", text => ReplaceOne(text, "\"CleanupConfirmed\":true", "\"CleanupConfirmed\":false,\"CleanupConfirmed\":true"));
        foreach (ulong invalidOwner in new ulong[] { 0, (ulong)int.MaxValue + 1 })
            Check("reject_invalid_caller_owner_" + invalidOwner, () =>
            {
                var rows = Read(fixture); Data(rows, "SOURCE")["TargetOwner"] = invalidOwner;
                var result = ShopNativeOutcome.Parse(Write(rows), source, image, invalidOwner, 1);
                return !result.SafeToSettle && !result.NoEntryConfirmed;
            });
        foreach (bool returned in new[] { false, true })
            Reject("reject_native_entered_" + (returned ? "returned" : "not_returned"), rows =>
            {
                var end = Data(rows, "END");
                end["NativeResult"] = new JsonObject
                {
                    ["Entered"] = 1, ["Returned"] = returned ? 1 : 0, ["Exception"] = 0,
                    ["Completed"] = 1, ["Claimed"] = 1, ["ObservedPid"] = source.ProcessId,
                    ["Status"] = returned ? 1 : 0, ["Stage"] = returned ? 5 : 1,
                    ["HostDeferredMask"] = 0, ["NativeDeferredMask"] = 0, ["PostCodeConfirmed"] = returned ? 1 : 0,
                    ["LeaseAcquired"] = 1, ["LeaseReleaseAttempted"] = 1, ["LeaseReleaseCompleted"] = 1,
                    ["ReleaseExceptionCode"] = 0, ["RequestUnchanged"] = 1, ["RequestMetadataBefore"] = 0,
                    ["RequestMetadataAfter"] = 0, ["DestructorCalls"] = 0
                };
                end["NativeAlreadyReturned"] = returned; end["PostCodeConfirmed"] = returned;
                end["BorrowedSourceContractVerified"] = returned; end["EventResultDurable"] = returned;
            });
        foreach (string kind in new[] { "EVENT_INTENT_DURABLE", "COMMAND_PUBLISH_INTENT", "EVENT_RESULT" })
            Check("reject_additional_" + kind, () =>
            {
                var rows = Read(fixture).ToList();
                rows.Insert(3, new JsonObject { ["At"] = rows[2]["At"]!.DeepClone(), ["Sequence"] = 4,
                    ["Kind"] = kind, ["DiskFlush"] = true, ["Data"] = new JsonObject() });
                for (int i = 0; i < rows.Count; i++) rows[i]["Sequence"] = i + 1;
                Data(rows.ToArray(), "END")["LogRecordsBeforeEnd"] = rows.Count - 1;
                Data(rows.ToArray(), "END")["LogDiskFlushesBeforeEnd"] = 2;
                var result = Parse(Write(rows));
                return !result.SafeToSettle && !result.NoEntryConfirmed;
            });
        Check("reject_truncated_end", () => !Parse(Write(original[..^1])).SafeToSettle);
        Check("reject_duplicate_end", () => !Parse(fixture + original[^1].ToJsonString() + "\n").SafeToSettle);
        Check("reject_hook_intent", () => !Parse(Write(original.Take(3).Concat([new JsonObject { ["Kind"] = "HOOK_INSTALL_INTENT", ["Data"] = new JsonObject() }]).Concat(original.Skip(3)))).SafeToSettle);
        Check("reject_exit_mismatch", () => !Parse(fixture, 2).SafeToSettle);
        Check("reject_wrong_purpose", () => !Parse(fixture, 1, ShopNativeLogPurpose.ReadOnlyPreflight).SafeToSettle);
        return checks.ToArray();
    }
}
