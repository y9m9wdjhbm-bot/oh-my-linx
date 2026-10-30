using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using MemoryHub;
using MemoryHub.Service;
namespace LcPlayerMarket;

/// <summary>Synthetic receipt regressions; these do not attest a live request or server effect.</summary>
internal static class NativeProtocolV15SelfTest
{
    internal static OfflineCheck[] Run()
    {
        var source = new SourceIdentity(1234, 5678, "offline.exe", "0x100000", ShopNativeHistoricalPackage.SupportedTimestamp,
            ShopNativeHistoricalPackage.SupportedImageSize, ShopNativeHistoricalPackage.SupportedSha, "offline-current-v15");
        const ulong image = 0x100000, owner = 99;
        string Line(string kind, object data) => JsonSerializer.Serialize(new { Kind = kind, Data = data });
        string fixture = string.Join('\n',
            Line("START", new { Version = "15.0.0", NativeProtocolVersion = 15, NativeGuardContract = 15,
                NativePayloadVersion = "15.0.0", Mode = "buy", MaximumNativeRequests = 1,
                LogPolicy = "DurableDispatchIntent_Result_End_Error;BufferedVerboseV2" }),
            Line("SOURCE", new { Pid = source.ProcessId, Creation = source.CreationFileTime, Image = image,
                Sha256 = source.DiskSha256, TargetOwner = owner, Path = source.ImagePath,
                Timestamp = $"0x{source.PeTimestamp:X}", ImageSize = $"0x{source.ImageSize:X}", ReadOnlyProcessHandle = true }),
            Line("END", new { ExitCode = 0, CleanupConfirmed = true, SessionBlocked = false, PostCodeConfirmed = true,
                BorrowedSourceContractVerified = true, NativeAlreadyReturned = true,
                NativeResult = new { Entered = 1, Returned = 1, Exception = 0, Completed = 1, Claimed = 1,
                    ObservedPid = source.ProcessId, Status = 1, Stage = 5, PostCodeConfirmed = 1,
                    HostDeferredMask = 0u, NativeDeferredMask = 0u, LeaseAcquired = 1, LeaseReleaseAttempted = 1,
                    LeaseReleaseCompleted = 1, ReleaseExceptionCode = 0u, RequestUnchanged = 1,
                    RequestMetadataBefore = 0UL, RequestMetadataAfter = 0UL, DestructorCalls = 0 } }));
        JsonObject[] Read(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonNode.Parse(x)!.AsObject()).ToArray();
        string Write(JsonObject[] rows) => string.Join('\n', rows.Select(x => x.ToJsonString()));
        string Mutate(string basis, Action<JsonObject[]> change) { var rows = Read(basis); change(rows); return Write(rows); }
        JsonNode Proof(string rva, string hex, bool metadata)
        {
            ulong address = image + Convert.ToUInt32(rva[2..], 16); byte[] bytes = Convert.FromHexString(hex);
            var page = new { Base = address & ~4095UL, AllocationBase = image, AllocationProtect = 32u, PartitionId = 0,
                Size = 4096UL, State = 4096u, Protect = metadata ? 2u : 32u, Type = 16777216u };
            return JsonSerializer.SerializeToNode(new { Rva = rva, Length = bytes.Length, Before = page, After = page,
                Matched = true, BodyAbClosed = true, PageAbClosed = true, Metadata = metadata,
                CurrentSha256 = Convert.ToHexString(SHA256.HashData(bytes)) })!;
        }
        string CompleteRows(JsonObject[] rows)
        {
            for (int i = 0; i < rows.Length; i++)
            { rows[i]["Sequence"] = i + 1; rows[i]["DiskFlush"] = rows[i]["Kind"]!.GetValue<string>() is "EVENT_RESULT" or "ERROR" or "END"; }
            var end = rows[^1]["Data"]!;
            end["LogRecordsBeforeEnd"] = rows.Length - 1;
            end["LogDiskFlushesBeforeEnd"] = rows[..^1].Count(x => x["DiskFlush"]!.GetValue<bool>());
            end["LogStreamFlushesBeforeEnd"] = 0;
            end["LogBufferedRecordsBeforeEnd"] = rows[..^1].Count(x => !x["DiskFlush"]!.GetValue<bool>());
            end["LogIoWriteDurationMsBeforeEnd"] = 1.25; end["LogSerializationDurationMsBeforeEnd"] = 0.75; end["LogWriteDurationMsBeforeEnd"] = 2.0;
            return Write(rows);
        }
        var initial = Read(fixture); var final = initial[^1]["Data"]!;
        final["PendingJournal"] = @"D:\lineage\LC_PlayerShopListAction_v1\journal\1234-5678.pending.json";
        final["SingleTrialPendingJournal"] = @"D:\lineage\LC_PlayerShopListAction_v1\journal\1234-5678.v15-singletrial.pending.json";
        final["SingleTrialPendingSchema"] = "V15SingleTrialPendingV1";
        final["EventResultDurable"] = true;
        final["AckObserved"] = false; final["ListReceivedVerified"] = false;
        final["RemoteModuleUnloadProven"] = false; final["EntireHookTailExitProven"] = false;
        var result = new JsonObject {
            ["State"] = final["NativeResult"]!.DeepClone(), ["NativeReturned"] = true, ["PostCodeConfirmed"] = true,
            ["PostCodeErrorCode"] = null, ["PostCodeErrorRva"] = null, ["PostReadBeforeDurableResult"] = true,
            ["PostStartedAt"] = "2026-10-08T01:00:00+08:00", ["PostFinishedAt"] = "2026-10-08T01:00:00.001+08:00",
            ["ListReceivedVerified"] = false, ["OwnerUpdatedByAckVerified"] = false, ["TargetOwner"] = owner,
            ["PostProof"] = new JsonArray(
                Proof("0x3EC5C1C", "00D372005BD37200DC715301", true),
                Proof("0x15371DC", "010A04000A3406000A320670", true),
                Proof("0x72D300", "48895C2408574883EC20488BDA33FF4885D27515B920000000E852B98C00488944243848897808EB11BA20000000488BCBE88A65700048895808488D0D370CB20048890833C94889481089481888481C488B5C24304883C4205FC3", false),
                Proof("0x76BEB0", "448B421041F6C003741D41F6C00174068B421889411841F6C00274070FB6421C88411C44094110F64208017415488B52084883C1084883E2FE4883C208E9DEB86D00C3", false))
        };
        fixture = CompleteRows([initial[0], initial[1], new JsonObject { ["Kind"] = "EVENT_RESULT", ["Data"] = result }, initial[^1]]);
        var checks = new List<OfflineCheck>();
        void Check(string name, string text, bool expected, SourceIdentity? binding = null,
            ShopNativeLogPurpose purpose = ShopNativeLogPurpose.NativeRequest, int exit = 0, bool noEntry = false)
        {
            try
            {
                var parsed = ShopNativeOutcome.Parse(text, binding ?? source, image, owner, exit, purpose);
                bool passed = parsed.SafeToSettle == expected && (!expected || parsed.SourceMatched
                    && (noEntry ? parsed.NoEntryConfirmed && !parsed.Entered && !parsed.Returned
                        : parsed.Entered && parsed.Returned && parsed.Failure is null));
                checks.Add(new("native_v15_" + name, passed, passed ? "通過；synthetic receipt" : parsed.Detail));
            }
            catch (Exception ex) { checks.Add(new("native_v15_" + name, false, ex.ToString())); }
        }
        void Reject(string name, Action<JsonObject[]> change) => Check("reject_" + name, Mutate(fixture, change), false);
        for (uint mask = 0; mask <= 3; mask++)
        {
            uint value = mask;
            Check(mask == 0 ? "returned_mask_0" : "reject_current_deferred_mask_" + mask, Mutate(fixture, r => {
                r[^1]["Data"]!["NativeResult"]!["HostDeferredMask"] = value;
                r[^1]["Data"]!["NativeResult"]!["NativeDeferredMask"] = value;
                r[^2]["Data"]!["State"] = r[^1]["Data"]!["NativeResult"]!.DeepClone();
            }), mask == 0);
        }
        // A page may become readable between host and native verification.
        Check("reject_current_host_deferred_even_native_readable", Mutate(fixture, r => {
            r[^1]["Data"]!["NativeResult"]!["HostDeferredMask"] = 3u;
            r[^2]["Data"]!["State"] = r[^1]["Data"]!["NativeResult"]!.DeepClone();
        }), false);
        Reject("host_version14_mix", r => r[0]["Data"]!["Version"] = "14.0.0");
        Reject("protocol14_mix", r => r[0]["Data"]!["NativeProtocolVersion"] = 14);
        Reject("guard14_mix", r => r[0]["Data"]!["NativeGuardContract"] = 14);
        Reject("payload14_mix", r => r[0]["Data"]!["NativePayloadVersion"] = "14.0.0");
        Reject("host_version16", r => r[0]["Data"]!["Version"] = "16.0.0");
        Reject("old_log_policy", r => r[0]["Data"]!["LogPolicy"] = "DurableDispatchIntent_Result_End_Error;VerboseStreamFlush");
        Reject("unknown_log_policy", r => r[0]["Data"]!["LogPolicy"] = "BufferedVerboseV99");
        Reject("host_mask4", r => r[^1]["Data"]!["NativeResult"]!["HostDeferredMask"] = 4u);
        Reject("native_mask4", r => r[^1]["Data"]!["NativeResult"]!["NativeDeferredMask"] = 4u);
        Reject("host_postcode_missing", r => r[^1]["Data"]!["PostCodeConfirmed"] = false);
        Reject("native_postcode_missing", r => r[^1]["Data"]!["NativeResult"]!["PostCodeConfirmed"] = 0);
        Reject("borrowed_source_unconfirmed", r => r[^1]["Data"]!["BorrowedSourceContractVerified"] = false);
        Reject("already_returned_unconfirmed", r => r[^1]["Data"]!["NativeAlreadyReturned"] = false);
        Reject("lease_not_acquired", r => r[^1]["Data"]!["NativeResult"]!["LeaseAcquired"] = 0);
        Reject("release_not_attempted", r => r[^1]["Data"]!["NativeResult"]!["LeaseReleaseAttempted"] = 0);
        Reject("release_not_completed", r => r[^1]["Data"]!["NativeResult"]!["LeaseReleaseCompleted"] = 0);
        Reject("release_exception", r => r[^1]["Data"]!["NativeResult"]!["ReleaseExceptionCode"] = 0xC0000005u);
        Reject("request_changed", r => r[^1]["Data"]!["NativeResult"]!["RequestUnchanged"] = 0);
        Reject("metadata_before_nonzero", r => r[^1]["Data"]!["NativeResult"]!["RequestMetadataBefore"] = 1UL);
        Reject("metadata_after_nonzero", r => r[^1]["Data"]!["NativeResult"]!["RequestMetadataAfter"] = 1UL);
        Reject("destructor_called", r => r[^1]["Data"]!["NativeResult"]!["DestructorCalls"] = 1);
        Reject("cleanup_unknown", r => r[^1]["Data"]!["CleanupConfirmed"] = false);
        Reject("pending_blocked", r => r[^1]["Data"]!["SessionBlocked"] = true);
        Reject("wrong_source_PE", r => r[1]["Data"]!["Timestamp"] = "0x6ABB9817");
        Reject("wrong_owner", r => r[1]["Data"]!["TargetOwner"] = owner + 1);
        Reject("native_exception", r => r[^1]["Data"]!["NativeResult"]!["Exception"] = 0xC0000005u);
        Reject("return_stage", r => r[^1]["Data"]!["NativeResult"]!["Stage"] = 4);
        Reject("return_without_entry", r => r[^1]["Data"]!["NativeResult"]!["Entered"] = 0);
        Reject("returned_request_max0", r => r[0]["Data"]!["MaximumNativeRequests"] = 0);
        Reject("missing_event_result", r => r[^2]["Kind"] = "OTHER");
        Reject("event_not_durable", r => r[^2]["DiskFlush"] = false);
        Reject("END_not_durable", r => r[^1]["DiskFlush"] = false);
        Reject("event_durable_checkpoint_failed", r => r[^1]["Data"]!["EventResultDurable"] = false);
        Reject("event_durable_checkpoint_missing", r => r[^1]["Data"]!.AsObject().Remove("EventResultDurable"));
        Reject("event_state_disagrees", r => r[^2]["Data"]!["State"]!["NativeDeferredMask"] = 3u);
        Reject("event_postcode_false", r => r[^2]["Data"]!["PostCodeConfirmed"] = false);
        Reject("postread_after_result", r => r[^2]["Data"]!["PostReadBeforeDurableResult"] = false);
        Reject("post_error_code", r => r[^2]["Data"]!["PostCodeErrorCode"] = "PostCodeUnconfirmed");
        Reject("post_error_rva", r => r[^2]["Data"]!["PostCodeErrorRva"] = "0x72D300");
        Reject("event_owner_changed", r => r[^2]["Data"]!["TargetOwner"] = owner + 1);
        Reject("server_ACK_claim", r => r[^2]["Data"]!["OwnerUpdatedByAckVerified"] = true);
        Reject("postproof_missing", r => r[^2]["Data"]!.AsObject().Remove("PostProof"));
        Reject("postproof_incomplete", r => r[^2]["Data"]!["PostProof"]!.AsArray().RemoveAt(3));
        Reject("postproof_unknown_rva", r => r[^2]["Data"]!["PostProof"]![2]!["Rva"] = "0x71E1C1");
        Reject("postproof_short_body", r => r[^2]["Data"]!["PostProof"]![2]!["Length"] = 90);
        Reject("postproof_wrong_sha", r => r[^2]["Data"]!["PostProof"]![3]!["CurrentSha256"] = new string('0', 64));
        Reject("postproof_metadata_wrong_sha", r => r[^2]["Data"]!["PostProof"]![0]!["CurrentSha256"] = new string('0', 64));
        Reject("postproof_not_matched", r => r[^2]["Data"]!["PostProof"]![2]!["Matched"] = false);
        Reject("postproof_body_AB_open", r => r[^2]["Data"]!["PostProof"]![2]!["BodyAbClosed"] = false);
        Reject("postproof_page_AB_open", r => r[^2]["Data"]!["PostProof"]![2]!["PageAbClosed"] = false);
        Reject("postproof_metadata_label", r => r[^2]["Data"]!["PostProof"]![2]!["Metadata"] = true);
        Reject("postproof_page_changed", r => r[^2]["Data"]!["PostProof"]![2]!["After"]!["Protect"] = 1u);
        Reject("postproof_wrong_allocation", r => {
            r[^2]["Data"]!["PostProof"]![2]!["Before"]!["AllocationBase"] = image + 4096;
            r[^2]["Data"]!["PostProof"]![2]!["After"]!["AllocationBase"] = image + 4096;
        });
        Reject("postproof_noaccess", r => {
            r[^2]["Data"]!["PostProof"]![2]!["Before"]!["Protect"] = 1u;
            r[^2]["Data"]!["PostProof"]![2]!["After"]!["Protect"] = 1u;
        });
        Reject("post_timing_reversed", r => r[^2]["Data"]!["PostFinishedAt"] = "2026-10-08T00:59:59+08:00");
        Reject("wrong_sidecar_session", r => r[^1]["Data"]!["SingleTrialPendingJournal"] = @"D:\lineage\LC_PlayerShopListAction_v1\journal\1234-1.v15-singletrial.pending.json");
        Reject("unknown_sidecar_schema", r => r[^1]["Data"]!["SingleTrialPendingSchema"] = "V15SingleTrialPendingV2");
        Reject("wrong_primary_pending", r => r[^1]["Data"]!["PendingJournal"] = @"D:\lineage\LC_PlayerShopListAction_v1\journal\1234-1.pending.json");
        Reject("wrong_durable_count", r => r[^1]["Data"]!["LogDiskFlushesBeforeEnd"] = 0);
        Reject("wrong_buffer_count", r => r[^1]["Data"]!["LogBufferedRecordsBeforeEnd"] = 1);
        Reject("invalid_log_timing_sum", r => r[^1]["Data"]!["LogWriteDurationMsBeforeEnd"] = 3.0);
        string readonlyNative = Mutate(fixture, r => { r[0]["Data"]!["Mode"] = "--preflight"; r[0]["Data"]!["MaximumNativeRequests"] = 0; });
        Check("reject_readonly0_with_native_return", readonlyNative, false, purpose: ShopNativeLogPurpose.ReadOnlyPreflight);
        Check("reject_readonly0_used_for_buy", readonlyNative, false);
        var legacy = source with { DiskSha256 = "9FFC24FE304AACBB98642363F7B19FD4610B12D1FD0CB09A30C0C944F170B7AF",
            PeTimestamp = 0x6ABB9817, ImageSize = 0x3E95000 };
        Check("reject_v15_on_legacy_source", Mutate(fixture, r => { r[1]["Data"]!["Sha256"] = legacy.DiskSha256;
            r[1]["Data"]!["Timestamp"] = "0x6ABB9817"; r[1]["Data"]!["ImageSize"] = "0x3E95000"; }), false, legacy);
        string v14 = Mutate(fixture, r => { r[0]["Data"]!["Version"] = "14.0.0"; r[0]["Data"]!["NativePayloadVersion"] = "14.0.0";
            r[0]["Data"]!["NativeProtocolVersion"] = 14; r[0]["Data"]!["NativeGuardContract"] = 14; });
        Check("synthetic_v14_current_schema_retained", v14, true);
        Check("v14_still_rejects_deferred1", Mutate(v14, r => r[^1]["Data"]!["NativeResult"]!["HostDeferredMask"] = 1u), false);
        Check("v14_still_rejects_deferred3", Mutate(v14, r => r[^1]["Data"]!["NativeResult"]!["NativeDeferredMask"] = 3u), false);
        string v14ReadonlyNative = Mutate(v14, r => { r[0]["Data"]!["Mode"] = "--preflight"; r[0]["Data"]!["MaximumNativeRequests"] = 0; });
        Check("v14_rejects_readonly0_with_native_return", v14ReadonlyNative, false, purpose: ShopNativeLogPurpose.ReadOnlyPreflight);
        string preEntry = Mutate(fixture, r => {
            var end = r[^1]["Data"]!; var native = end["NativeResult"]!;
            end["ExitCode"] = 1; end["PostCodeConfirmed"] = false; end["BorrowedSourceContractVerified"] = false; end["NativeAlreadyReturned"] = false;
            native["Entered"] = 0; native["Returned"] = 0; native["Status"] = -3; native["Stage"] = 0; native["PostCodeConfirmed"] = 0;
            native["LeaseAcquired"] = 0; native["LeaseReleaseAttempted"] = 0; native["LeaseReleaseCompleted"] = 0; native["RequestUnchanged"] = 0;
            var eventResult = r[^2]["Data"]!; eventResult["State"] = native.DeepClone();
            eventResult["NativeReturned"] = false; eventResult["PostCodeConfirmed"] = false;
            eventResult["PostCodeErrorCode"] = "NativeResultUnconfirmed"; eventResult["PostProof"] = new JsonArray();
        });
        var rejectedRows = Read(preEntry);
        preEntry = CompleteRows([.. rejectedRows[..^1], JsonNode.Parse(Line("ERROR", new { Type = "NativePreflightException", Code = "SourceMismatch", Phase = "Native", Message = "synthetic native source rejection" }))!.AsObject(), rejectedRows[^1]]);
        Check("completed_source_rejection_no_entry", preEntry, true, exit: 1, noEntry: true);
        Check("completed_noentry_does_not_require_return_checkpoint", Mutate(preEntry,
            r => r[^1]["Data"]!["EventResultDurable"] = false), true, exit: 1, noEntry: true);
        void RejectNoEntry(string name, Action<JsonObject[]> change) => Check("noentry_reject_" + name, Mutate(preEntry, change), false, exit: 1);
        RejectNoEntry("strict_host_mask1", r => r[^1]["Data"]!["NativeResult"]!["HostDeferredMask"] = 1u);
        RejectNoEntry("strict_native_mask1", r => r[^1]["Data"]!["NativeResult"]!["NativeDeferredMask"] = 1u);
        RejectNoEntry("host_mask4", r => r[^1]["Data"]!["NativeResult"]!["HostDeferredMask"] = 4u);
        RejectNoEntry("native_mask4", r => r[^1]["Data"]!["NativeResult"]!["NativeDeferredMask"] = 4u);
        RejectNoEntry("lease_claim", r => r[^1]["Data"]!["NativeResult"]!["LeaseAcquired"] = 1);
        RejectNoEntry("release_claim", r => r[^1]["Data"]!["NativeResult"]!["LeaseReleaseCompleted"] = 1);
        RejectNoEntry("request_stage", r => r[^1]["Data"]!["NativeResult"]!["Stage"] = 2);
        RejectNoEntry("success_status", r => r[^1]["Data"]!["NativeResult"]!["Status"] = 1);
        RejectNoEntry("metadata", r => r[^1]["Data"]!["NativeResult"]!["RequestMetadataAfter"] = 1UL);
        Check("readonly0_rejects_native_source_rejection", Mutate(preEntry, r => {
            r[0]["Data"]!["Mode"] = "--preflight"; r[0]["Data"]!["MaximumNativeRequests"] = 0;
        }), false, purpose: ShopNativeLogPurpose.ReadOnlyPreflight, exit: 1);
        return checks.ToArray();
    }
}
