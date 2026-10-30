using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;
namespace LcPlayerMarket;
internal static class NativeProtocolV13SelfTest
{
    internal static OfflineCheck[] Run()
    {
        var source = new SourceIdentity(1234, 5678, "offline.exe", "0x100000", 0x6ABB9817, 0x3E95000,
            "9FFC24FE304AACBB98642363F7B19FD4610B12D1FD0CB09A30C0C944F170B7AF", "offline-legacy");
        var current = source with { DiskSha256 = ShopNativeHistoricalPackage.SupportedSha,
            PeTimestamp = ShopNativeHistoricalPackage.SupportedTimestamp, ImageSize = ShopNativeHistoricalPackage.SupportedImageSize };
        const ulong image = 0x100000, owner = 99;
        string Make(string version = "13.0.0", int protocol = 13, int guard = 13, bool cleanup = true, int post = 1,
            ulong target = owner, SourceIdentity? binding = null, string? payload = null, uint deferred = 0)
        {
            var bound = binding ?? source;
            string Line(string kind, object data) => JsonSerializer.Serialize(new { Kind = kind, Data = data });
            return string.Join('\n',
                Line("START", new { Version = version, NativeProtocolVersion = protocol, NativeGuardContract = guard,
                    NativePayloadVersion = payload ?? version, Mode = "buy", MaximumNativeRequests = 1,
                    LogPolicy = "DurableDispatchIntent_Result_End_Error;VerboseStreamFlush" }),
                Line("SOURCE", new { Pid = bound.ProcessId, Creation = bound.CreationFileTime, Image = image, Sha256 = bound.DiskSha256,
                    TargetOwner = target, Path = bound.ImagePath, Timestamp = $"0x{bound.PeTimestamp:X}", ImageSize = $"0x{bound.ImageSize:X}", ReadOnlyProcessHandle = true }),
                Line("END", new { ExitCode = 0, CleanupConfirmed = cleanup, SessionBlocked = false, PostCodeConfirmed = post == 1,
                    BorrowedSourceContractVerified = true, NativeAlreadyReturned = true,
                    NativeResult = new { Entered = 1, Returned = 1, Exception = 0, Completed = 1, Claimed = 1, ObservedPid = 1234, Status = 1, Stage = 5,
                        PostCodeConfirmed = post, HostDeferredMask = deferred, NativeDeferredMask = deferred, LeaseAcquired = 1, LeaseReleaseAttempted = 1,
                        LeaseReleaseCompleted = 1, ReleaseExceptionCode = 0, RequestUnchanged = 1, RequestMetadataBefore = 0, RequestMetadataAfter = 0, DestructorCalls = 0 } }));
        }
        var results = new List<OfflineCheck>();
        void Check(string name, string text, bool expected, SourceIdentity? binding = null)
        {
            var parsed = ShopNativeOutcome.Parse(text, binding ?? source, image, owner, 0);
            bool passed = parsed.SafeToSettle == expected && (!expected || parsed.Entered && parsed.Returned && parsed.Failure is null);
            results.Add(new(name, passed, passed ? "通過" : parsed.Detail));
        }
        Check("native_v13_valid_return_and_cleanup", Make(), true);
        Check("native_v13_rejects_legacy_host", Make(version: "11.0.0"), false);
        Check("native_v13_rejects_legacy_protocol", Make(protocol: 11), false);
        Check("native_v13_rejects_legacy_guard", Make(guard: 11), false);
        Check("native_v13_rejects_unclean_return", Make(cleanup: false), false);
        Check("native_v13_rejects_missing_postcode", Make(post: 0), false);
        Check("native_v13_rejects_wrong_target", Make(target: 100), false);
        Check("native_v13_rejects_duplicate_completion", Make() + "\n" + Make(), false);
        Check("native_v13_historical_deferred_contract_retained", Make(deferred: 3), true);
        string Current(string version = "14.0.0", int protocol = 14, int guard = 14, string? payload = null, uint deferred = 0) =>
            Make(version: version, protocol: protocol, guard: guard, payload: payload, binding: current, deferred: deferred);
        Check("native_v14_current_tuple_valid_return", Current(), true, current);
        Check("native_v14_current_rejects_v13_tuple", Current(version: "13.0.0", protocol: 13, guard: 13), false, current);
        Check("native_v14_current_rejects_protocol_mix", Current(protocol: 13), false, current);
        Check("native_v14_current_rejects_guard_mix", Current(guard: 13), false, current);
        Check("native_v14_current_rejects_payload_mix", Current(payload: "13.0.0"), false, current);
        Check("native_v14_current_rejects_deferred_packet", Current(deferred: 1), false, current);
        Check("native_v14_tuple_rejects_legacy_source", Make(version: "14.0.0", protocol: 14, guard: 14), false);
        Check("native_v14_current_rejects_PE_source_mix", Current(), false, current with { PeTimestamp = 0x6ABB9817 });
        AddV14UnpublishedChecks(results);
        results.AddRange(NativeProtocolV15SelfTest.Run());
        return results.ToArray();
    }

    private static void AddV14UnpublishedChecks(List<OfflineCheck> checks)
    {
        try
        {
            const string suffix = ".v14-readonly-copy-unavailable.jsonl";
            var assembly = typeof(NativeProtocolV13SelfTest).Assembly;
            string resource = assembly.GetManifestResourceNames().Single(x => x.EndsWith(suffix, StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException("Missing V14 readonly fixture.");
            using var reader = new StreamReader(stream); string fixture = SyntheticHistoricalContractFixture.FromSavedShape(reader.ReadToEnd());
            JsonObject[] Read(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => JsonNode.Parse(x)!.AsObject()).ToArray();
            string Write(JsonObject[] rows) => string.Join('\n', rows.Select(x => x.ToJsonString()));
            var original = Read(fixture); var id = original[1]["Data"]!;
            ulong image = id["Image"]!.GetValue<ulong>(), owner = id["TargetOwner"]!.GetValue<ulong>();
            var source = new SourceIdentity(id["Pid"]!.GetValue<int>(), id["Creation"]!.GetValue<long>(), id["Path"]!.GetValue<string>(), $"0x{image:X}",
                Convert.ToUInt32(id["Timestamp"]!.GetValue<string>()[2..], 16), Convert.ToUInt32(id["ImageSize"]!.GetValue<string>()[2..], 16),
                id["Sha256"]!.GetValue<string>(), "saved-v14-readonly-source");
            ShopNativeOutcome Parse(string text, ShopNativeLogPurpose purpose = ShopNativeLogPurpose.ReadOnlyPreflight, int exit = 1) =>
                ShopNativeOutcome.Parse(text, source, image, owner, exit, purpose);
            bool Verified(ShopNativeOutcome x) => x.SafeToSettle && x.NoEntryConfirmed && x.SourceMatched && !x.Entered && !x.Returned
                && x.Failure is { Code: "NativeCodeUnavailable", Scope: "Global", Phase: "Preflight", Rva: "0x76BEB0" };
            void Check(string name, Func<bool> test)
            { try { bool passed = test(); checks.Add(new("native_v14_" + name, passed, passed ? "通過；SYNTHETIC historical38A7 grammar/dataflow; no source or recovery attestation; original embedded fixture retained" : "receipt check failed")); }
              catch (Exception ex) { checks.Add(new("native_v14_" + name, false, ex.ToString())); } }
            string Mutate(string basis, Action<JsonObject[]> action) { var rows = Read(basis); action(rows); return Write(rows); }
            Check("synthetic_historical38A7_readonly_five_record_rejection", () => Verified(Parse(fixture)));
            Check("current79d1_rejects_relabelled_historical_v14", () =>
            {
                var rejected = ShopNativeOutcome.Parse(SyntheticHistoricalContractFixture.RelabelCurrentForRejection(fixture),
                    SyntheticHistoricalContractFixture.CurrentBindingForRejection(source), image, owner, 1, ShopNativeLogPurpose.ReadOnlyPreflight);
                return !rejected.SafeToSettle && !rejected.NoEntryConfirmed;
            });
            string buffered = Mutate(fixture, r => {
                r[0]["Data"]!["LogPolicy"] = "DurableDispatchIntent_Result_End_Error;BufferedVerboseV2";
                r[^1]["Data"]!["LogStreamFlushesBeforeEnd"] = 0;
                r[^1]["Data"]!["LogBufferedRecordsBeforeEnd"] = 3;
                r[^1]["Data"]!["LogIoWriteDurationMsBeforeEnd"] = 12.5;
                r[^1]["Data"]!["LogSerializationDurationMsBeforeEnd"] = 1.0;
                r[^1]["Data"]!["LogWriteDurationMsBeforeEnd"] = 13.5;
            });
            Check("synthetic_buffered_policy_five_record_rejection", () => Verified(Parse(buffered)));
            void RejectBuffered(string name, Action<JsonObject[]> action) => Check("buffered_reject_" + name, () => {
                var x = Parse(Mutate(buffered, action)); return !x.SafeToSettle && !x.NoEntryConfirmed;
            });
            RejectBuffered("unknown_policy", r => r[0]["Data"]!["LogPolicy"] = "BufferedVerboseV99");
            RejectBuffered("wrong_buffer_count", r => r[^1]["Data"]!["LogBufferedRecordsBeforeEnd"] = 2);
            RejectBuffered("stream_flush_not_zero", r => r[^1]["Data"]!["LogStreamFlushesBeforeEnd"] = 3);
            RejectBuffered("wrong_disk_count", r => r[^1]["Data"]!["LogDiskFlushesBeforeEnd"] = 0);
            RejectBuffered("missing_buffer_count", r => r[^1]["Data"]!.AsObject().Remove("LogBufferedRecordsBeforeEnd"));
            RejectBuffered("negative_io_duration", r => r[^1]["Data"]!["LogIoWriteDurationMsBeforeEnd"] = -1.0);
            RejectBuffered("negative_serialization_duration", r => r[^1]["Data"]!["LogSerializationDurationMsBeforeEnd"] = -1.0);
            RejectBuffered("missing_io_duration", r => r[^1]["Data"]!.AsObject().Remove("LogIoWriteDurationMsBeforeEnd"));
            RejectBuffered("timing_type", r => r[^1]["Data"]!["LogSerializationDurationMsBeforeEnd"] = "unknown");
            RejectBuffered("inconsistent_duration_sum", r => r[^1]["Data"]!["LogWriteDurationMsBeforeEnd"] = 14.0);
            Check("readonly_receipt_cannot_settle_native_request", () => !Parse(fixture, ShopNativeLogPurpose.NativeRequest).SafeToSettle);
            Check("unknown_receipt_purpose_rejected", () => !Parse(fixture, (ShopNativeLogPurpose)99).SafeToSettle);
            string request = Mutate(fixture, r => { r[0]["Data"]!["Mode"] = "buy"; r[0]["Data"]!["MaximumNativeRequests"] = 1; });
            Check("synthetic_buy_unpublished_uses_request_purpose", () => Verified(Parse(request, ShopNativeLogPurpose.NativeRequest)));
            Check("buy_receipt_not_readonly_receipt", () => !Parse(request).SafeToSettle);
            void Reject(string name, Action<JsonObject[]> action) => Check("reject_" + name, () => {
                var x = Parse(Mutate(fixture, action)); return !x.SafeToSettle && !x.NoEntryConfirmed;
            });
            Reject("readonly_maximum_one", r => r[0]["Data"]!["MaximumNativeRequests"] = 1);
            Reject("unknown_legacy_policy", r => r[0]["Data"]!["LogPolicy"] = "DurableDispatchIntent_Result_End_Error;Unknown");
            Reject("legacy_marker_with_buffered_counter", r => r[^1]["Data"]!["LogBufferedRecordsBeforeEnd"] = 3);
            Reject("buffered_marker_with_legacy_footer", r => r[0]["Data"]!["LogPolicy"] = "DurableDispatchIntent_Result_End_Error;BufferedVerboseV2");
            Reject("protocol13_on_current_source", r => r[0]["Data"]!["NativeProtocolVersion"] = 13);
            Reject("guard13_on_current_source", r => r[0]["Data"]!["NativeGuardContract"] = 13);
            Reject("payload13_on_current_source", r => r[0]["Data"]!["NativePayloadVersion"] = "13.0.0");
            Reject("wrong_source", r => r[1]["Data"]!["Creation"] = 1);
            Reject("wrong_path", r => r[1]["Data"]!["Path"] = "other.exe");
            Reject("wrong_PE", r => r[1]["Data"]!["Timestamp"] = "0x6ABB9817");
            Reject("wrong_owner", r => r[1]["Data"]!["TargetOwner"] = owner + 1);
            Reject("unknown_function", r => { r[2]["Data"]!["Rva"] = "0x75CC41"; r[3]["Data"]!["Rva"] = "0x75CC41"; });
            Reject("page_private", r => r[2]["Data"]!["Page"]!["Type"] = 0x20000);
            Reject("page_wrong_image", r => r[2]["Data"]!["Page"]!["AllocationBase"] = image + 4096);
            Reject("page_range_incomplete", r => r[2]["Data"]!["Page"]!["Size"] = 1);
            Reject("later_phase", r => r[3]["Data"]!["Phase"] = "BeforePublish");
            Reject("missing_code_page_record", r => r[2]["Kind"] = "OTHER");
            Reject("hook_record", r => r[2]["Kind"] = "HOOK_INSTALL_INTENT");
            Reject("event_record", r => r[2]["Kind"] = "EVENT_RESULT");
            Reject("intent_record", r => r[2]["Kind"] = "EVENT_INTENT_DURABLE");
            Reject("sequence_gap", r => r[2]["Sequence"] = 4);
            Reject("not_durable", r => r[^1]["DiskFlush"] = false);
            Reject("record_count", r => r[^1]["Data"]!["LogRecordsBeforeEnd"] = 3);
            Reject("flush_count", r => r[^1]["Data"]!["LogStreamFlushesBeforeEnd"] = 2);
            Reject("cleanup_unknown", r => r[^1]["Data"]!["CleanupConfirmed"] = false);
            Reject("pending_blocked", r => r[^1]["Data"]!["SessionBlocked"] = true);
            Reject("already_returned", r => r[^1]["Data"]!["NativeAlreadyReturned"] = true);
            Reject("ACK_claim", r => r[^1]["Data"]!["AckObserved"] = true);
            Reject("journal_other_session", r => r[^1]["Data"]!["PendingJournal"] = @"D:\lineage\LC_PlayerShopListAction_v1\journal\1-1.pending.json");
            Check("reject_truncated", () => !Parse(Write(original[..^1])).SafeToSettle);
            Check("reject_exit_mismatch", () => !Parse(fixture, exit: 0).SafeToSettle);
            Check("reject_duplicate_property", () => !Parse(fixture.Replace("\"Phase\":\"Preflight\"", "\"Phase\":\"Preflight\",\"Phase\":\"Preflight\"", StringComparison.Ordinal)).SafeToSettle);
            Check("reject_generic_null_global_error", () => !Parse(Write(original.Where(x => x["Kind"]!.GetValue<string>() != "NATIVE_CODE_PAGE_UNAVAILABLE").ToArray())).SafeToSettle);
            Check("code_unavailable_stops_batch", () => ShopScanPolicy.Decide(Parse(fixture).Failure!.Attach(new() { RequestId = "offline" }), true) == ShopScanDisposition.StopBatch);
        }
        catch (Exception ex) { checks.Add(new("native_v14_fixture_load", false, ex.ToString())); }
    }
}
