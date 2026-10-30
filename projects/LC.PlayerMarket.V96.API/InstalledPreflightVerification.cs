using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Saved-file parser plus production patrol with fake ports; never launches helper or opens LC.</summary>
internal static class InstalledPreflightVerification
{
    public static int RunReport(string output)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("ADMIN_REQUIRED");
        var checks = RunAsync().GetAwaiter().GetResult();
        var report = new OfflineCheckReport("83.0.0", checks.All(c => c.Passed), checks.Count(c => c.Passed),
            checks.Count(c => !c.Passed), false, false, false, checks);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, HubProtocol.Json); file.Flush(true);
        return report.Success ? 0 : 1;
    }

    public static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = RunParser().ToList();
        checks.AddRange(await PatrolScanAcceptanceVerification.RunAsync().ConfigureAwait(false));
        return checks.ToArray();
    }

    internal static OfflineCheck[] RunParser()
    {
        string original = Encoding.UTF8.GetString(Convert.FromBase64String(FixtureBase64));
        string basis = SyntheticHistoricalContractFixture.FromSavedShape(original);
        const string expectedHash = "3D09875198B528DC05304A4D2D42B1CDBA277B46664E421AA2E5E3AB02F33204";
        var rows = Read(basis); var identity = Data(rows, "SOURCE");
        ulong image = identity["Image"]!.GetValue<ulong>(), owner = identity["TargetOwner"]!.GetValue<ulong>();
        uint Hex(string name) => uint.Parse(identity[name]!.GetValue<string>()[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var source = new SourceIdentity(identity["Pid"]!.GetValue<int>(), identity["Creation"]!.GetValue<long>(),
            identity["Path"]!.GetValue<string>(), "0x" + image.ToString("X"), Hex("Timestamp"), Hex("ImageSize"),
            identity["Sha256"]!.GetValue<string>(), "saved-installed-preflight-regression-only");
        var checks = new List<OfflineCheck>();
        checks.Add(new("installed_preflight_fixture_original_sha", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original))) == expectedHash,
            "Original saved LOG 8626e4cf03b64a3189a6c97b598add99.jsonl. Parsing this copy does not settle the historical request."));
        void Check(string name, string input, bool expected, SourceIdentity? caller = null, ulong? target = null,
            int exit = 1, ShopNativeLogPurpose purpose = ShopNativeLogPurpose.NativeRequest)
        {
            try
            {
                var result = ShopNativeOutcome.Parse(input, caller ?? source, image, target ?? owner, exit, purpose);
                bool passed = expected ? result.SafeToSettle && result.NoEntryConfirmed && result.SourceMatched && !result.Entered && !result.Returned
                    && result.Failure is { Code: "NativeCodeUnavailable", Scope: "Global", Phase: "Preflight", Rva: "0x287910" }
                    && result.Detail.Contains("BeforePublish", StringComparison.Ordinal)
                    : !result.SafeToSettle && !result.NoEntryConfirmed;
                checks.Add(new("installed_preflight_" + name, passed, "Production Parse; SYNTHETIC historical38A7 grammar/dataflow; no source or recovery attestation; original saved bytes retained. " + result.Detail));
            }
            catch (Exception error) { checks.Add(new("installed_preflight_" + name, false, error.ToString())); }
        }
        void Reject(string name, Action<JsonObject[]> mutate)
        { var changed = Read(basis); mutate(changed); Check("reject_" + name, Write(changed), false); }
        Check("synthetic_historical38A7_23_record_buy", basis, true);
        Check("current79d1_rejects_relabelled_historical_v16", SyntheticHistoricalContractFixture.RelabelCurrentForRejection(basis), false,
            caller: SyntheticHistoricalContractFixture.CurrentBindingForRejection(source));
        var sell = Read(basis); Data(sell, "START")["Mode"] = "sell"; Data(sell, "HOOK_INSTALL_INTENT")["Mode"] = 2;
        Check("matching_sell_mode", Write(sell), true);
        Check("caller_source_creation_changed", basis, false, source with { CreationFileTime = source.CreationFileTime + 1 });
        Check("caller_owner_changed", basis, false, target: owner + 1);
        Check("caller_build_changed", basis, false, source with { DiskSha256 = new string('B', 64) });
        Check("readonly_purpose", basis, false, purpose: ShopNativeLogPurpose.ReadOnlyPreflight);
        Check("exit_code_changed", basis, false, exit: 0);
        Check("missing_final_newline", basis.TrimEnd('\r', '\n'), false);
        Check("duplicate_json_key", basis.Replace("\"CleanupConfirmed\":true", "\"CleanupConfirmed\":true,\"CleanupConfirmed\":true", StringComparison.Ordinal), false);
        foreach (string field in Data(rows, "NATIVE_FINAL_STATE")["State"]!.AsObject().Select(p => p.Key).ToArray())
            Reject("nonzero_native_" + field, r => Data(r, "NATIVE_FINAL_STATE")["State"]![field] = 1);
        for (int i = 0; i < rows.Length; i++)
        {
            int index = i;
            Check("missing_row_" + (i + 1), Write(Read(basis).Where((_, n) => n != index)), false);
        }
        Reject("old_managed_version", r => Data(r, "START")["ManagedProgramVersion"] = "16.0.1");
        Reject("unknown_protocol", r => Data(r, "START")["NativeProtocolVersion"] = 16);
        Reject("missing_lifecycle_contract", r => Data(r, "START").AsObject().Remove("ReturnedLifecycleRecoveryContract"));
        Reject("wrong_lifecycle_contract", r => Data(r, "START")["ReturnedLifecycleRecoveryContract"] = 2);
        Reject("wrong_recheck_contract", r => Data(r, "START")["PostCodeRecheckContract"] = 2);
        Reject("wrong_helper_sha", r => Data(r, "NATIVE_PAYLOAD")["Sha256"] = new string('A', 64));
        Reject("wrong_hook_target", r => Data(r, "HOOK_INSTALL_INTENT")["TargetOwner"] = owner + 1);
        Reject("wrong_hook_mode", r => Data(r, "HOOK_INSTALL_INTENT")["Mode"] = 2);
        Reject("hook_install_failed", r => Data(r, "HOOK_INSTALL_RESULT")["Installed"] = false);
        Reject("deferred_initial_guard", r => Data(r, "CURRENT_NATIVE_CODE_GUARDS")["DeferredMask"] = 1);
        Reject("deferred_initial_proof", r => Data(r, "PREFLIGHT_AB_CLOSED")["DeferredMask"] = 1);
        Reject("initial_fullbody_false", r => Data(r, "PREFLIGHT_AB_CLOSED")["EverySynchronousGuardBodyCurrentlyMatched"] = false);
        Reject("initial_old_owner_mismatch", r => Data(r, "PREFLIGHT_AB_CLOSED")["Owner64"] = owner);
        Reject("preflight_error_phase", r => Data(r, "ERROR")["Phase"] = "Preflight");
        Reject("different_error", r => Data(r, "ERROR")["Code"] = "NativeOutcomeUnknown");
        Reject("request_published_flag", r => Data(r, "SUBMIT_CODE_PREFLIGHT_RECHECK")["RequestNotPublished"] = false);
        Reject("protection_changed", r => Data(r, "SUBMIT_CODE_PREFLIGHT_RECHECK")["ProtectionChanged"] = true);
        Reject("page_ab_changed", r => Data(r, "SUBMIT_CODE_PREFLIGHT_RECHECK")["After"]!["Protect"] = 32);
        Reject("recheck_unknown", r => Data(r, "SUBMIT_CODE_PREFLIGHT_RECHECK")["Outcome"] = "Unknown");
        Reject("no_quiescence", r => Data(r, "NATIVE_FINAL_STATE")["QuiescentObserved"] = false);
        foreach (string kind in new[] { "UNHOOK", "LOCAL_VIEW_RELEASE", "LOCAL_MAPPING_CLOSE", "LOCAL_LIBRARY_FREE" })
            Reject("cleanup_" + kind, r => Data(r, kind)["Success"] = false);
        foreach (string field in new[] { "CleanupConfirmed", "QuiescentObserved", "Installed", "Unhooked" })
            Reject("cleanup_missing_" + field, r => Data(r, "CLEANUP")[field] = false);
        foreach (string field in new[] { "LocalViewRetained", "MappingRetained", "LibraryRetained", "HookRetained" })
            Reject("cleanup_retained_" + field, r => Data(r, "CLEANUP")[field] = true);
        Reject("end_cleanup_false", r => Data(r, "END")["CleanupConfirmed"] = false);
        Reject("end_session_blocked", r => Data(r, "END")["SessionBlocked"] = true);
        Reject("end_native_result", r => Data(r, "END")["NativeResult"] = new JsonObject());
        Reject("end_event_result", r => Data(r, "END")["EventResultDurable"] = true);
        Reject("end_record_count", r => Data(r, "END")["LogRecordsBeforeEnd"] = 21);
        Reject("end_flush_count", r => Data(r, "END")["LogDiskFlushesBeforeEnd"] = 2);
        Reject("sequence_gap", r => r[8]["Sequence"] = 10);
        Reject("row_time_reversed", r => r[10]["At"] = r[0]["At"]!.DeepClone());
        Reject("missing_error_flush", r => r[15]["DiskFlush"] = false);
        foreach (string kind in new[] { "EVENT_INTENT_DURABLE", "COMMAND_PUBLISH_INTENT", "WM_NULL_POST_INTENT", "EVENT_RESULT" })
        {
            var extra = Read(basis).ToList(); extra.Insert(13, new JsonObject { ["Kind"] = kind, ["Data"] = new JsonObject() });
            Check("reject_extra_" + kind, Write(extra), false);
        }
        var parsed = ShopNativeOutcome.Parse(basis, source, image, owner, 1);
        var response = parsed.Failure!.Attach(new() { NativeEntered = parsed.Entered, NativeReturned = parsed.Returned,
            SessionBlocked = !parsed.SafeToSettle || !parsed.NoEntryConfirmed });
        checks.Add(new("installed_preflight_stop_batch_without_unknown_native_lock", !response.SessionBlocked
            && ShopScanPolicy.Decide(response, true, true) == ShopScanDisposition.StopBatch,
            "Current unsent page is settled only on strict proof. Existing Global/Preflight recovery semantics remain; no call permission granted."));
        return checks.ToArray();
    }

    private static JsonObject[] Read(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
    private static JsonNode Data(JsonObject[] rows, string kind) => rows.Single(r => r["Kind"]!.GetValue<string>() == kind)["Data"]!;
    private static string Write(IEnumerable<JsonObject> rows) => string.Join('\n', rows.Select(r => r.ToJsonString())) + "\n";
    private const string FixtureBase64 = "eyJBdCI6IjIwMjYtMTAtMTBUMTY6MDE6MTUuOTg4NzYrMDA6MDAiLCJTZXF1ZW5jZSI6MSwiS2luZCI6IlNUQVJUIiwiRGlza0ZsdXNoIjpmYWxzZSwiRGF0YSI6eyJWZXJzaW9uIjoiMTUuMC4wIiwiTWFuYWdlZFByb2dyYW1WZXJzaW9uIjoiMTYuMC4yIiwiTmF0aXZlUHJvdG9jb2xWZXJzaW9uIjoxNSwiTmF0aXZlR3VhcmRDb250cmFjdCI6MTUsIk5hdGl2ZVBheWxvYWRWZXJzaW9uIjoiMTUuMC4wIiwiUG9zdENvZGVSZWNoZWNrQ29udHJhY3QiOjEsIlBvc3RDb2RlUmVjaGVja01heGltdW1NaWxsaXNlY29uZHMiOjEwMDAsIlJldHVybmVkTGlmZWN5Y2xlUmVjb3ZlcnlDb250cmFjdCI6MSwiU3VibWl0UHJlZmxpZ2h0UmVjaGVja01heGltdW1NaWxsaXNlY29uZHMiOjI1MCwiTW9kZSI6ImJ1eSIsIk1heGltdW1OYXRpdmVSZXF1ZXN0cyI6MSwiTW91c2VNZXNzYWdlcyI6MCwiS2V5TWVzc2FnZXMiOjAsIlRvdWNoQ2FsbHMiOjAsIkZvcmVncm91bmRDaGFuZ2VzIjowLCJEaXJlY3RTZWxlY3RlZE93bmVyV3JpdGVzIjowLCJTaGFyZWRPd25lcnNoaXBDb3VudGVyVXBkYXRlcyI6dHJ1ZSwiT3BlblByb2Nlc3NBY2Nlc3MiOiIweDQxMCIsIkRlZmVycmVkQ29kZVBvbGljeSI6Ik9ubHkgQ3JlYXRlL0NvcHkgY29tbWl0dGVkIGltYWdlIE5PQUNDRVNTLCBwaW5uZWQgaGlzdG9yaWNhbCBleGFjdC1pbWFnZSBzZXZlbiBmdWxsIEEvQiBib2RpZXM7IGluZGVwZW5kZW50bHkgdmVyaWZpZWQgY3VycmVudCBzb3VyY2U7IHNpbmdsZSB0cmlhbDsgaW1tZWRpYXRlIHN0cmljdCBmdWxsIHBvc3Rib2R5OyBtYXhtYXNrMyIsIkJpbmRpbmciOiJDYWxsZXItc3VwcGxpZWQgcnVudGltZSBpZGVudGl0eSwgaW5kZXBlbmRlbnRseSB2ZXJpZmllZCIsIkN1cnJlbnRPd25lckJpdHMiOjY0LCJMb2dQb2xpY3kiOiJEdXJhYmxlRGlzcGF0Y2hJbnRlbnRfUmVzdWx0X0VuZF9FcnJvcjtCdWZmZXJlZFZlcmJvc2VWMiIsIlJlc2VhcmNoT25seSI6dHJ1ZX19CnsiQXQiOiIyMDI2LTEwLTEwVDE2OjAxOjE2LjI5MDA0ODMrMDA6MDAiLCJTZXF1ZW5jZSI6MiwiS2luZCI6IkZPUkNFX0RJU1BBVENIIiwiRGlza0ZsdXNoIjpmYWxzZSwiRGF0YSI6eyJQcmV2aW91c1BlbmRpbmciOiJEOlxcbGluZWFnZVxcTENfUGxheWVyU2hvcExpc3RBY3Rpb25fdjFcXGpvdXJuYWxcXDgxNDQtMTM0MzYwNzc2MTE5MDIzMTczLnBlbmRpbmcuanNvbiIsIlByZXZpb3VzUGVuZGluZ1ByZXNlcnZlZCI6ZmFsc2UsIkN1cnJlbnRQZW5kaW5nIjoiRDpcXGxpbmVhZ2VcXExDX1BsYXllclNob3BMaXN0QWN0aW9uX3YxXFxqb3VybmFsXFw4MTQ0LTEzNDM2MDc3NjExOTAyMzE3My5mb3JjZS02OGNhMWY2MTI4NjU0ZDQyYjViNjMyZGVmYTRhYzJiYi5wZW5kaW5nLmpzb24iLCJXYWl0Rm9yUHJldmlvdXNHYW1lU3RhdGUiOmZhbHNlfX0KeyJBdCI6IjIwMjYtMTAtMTBUMTY6MDE6MTYuNjU1NzkxNSswMDowMCIsIlNlcXVlbmNlIjozLCJLaW5kIjoiU09VUkNFIiwiRGlza0ZsdXNoIjpmYWxzZSwiRGF0YSI6eyJQaWQiOjgxNDQsIkNyZWF0aW9uIjoxMzQzNjA3NzYxMTkwMjMxNzMsIkltYWdlIjoxNDA2OTc3MTU0MDg4OTYsIkh3bmQiOjY3Mjk0LCJUaWQiOjE0MzcyLCJTaG9wIjoyNDEwMTU4NzY3Mzc2LCJPd25lciI6MTE1MjMxMDY3LCJUYXJnZXRPd25lciI6MTA0MDczMjI1LCJQYXRoIjoiQzpcXFByb2dyYW0gRmlsZXMgKHg4NilcXE5DU09GVFxcTGluZWFnZSBDbGFzc2ljXFxMQy5leGUiLCJTaGEyNTYiOiJGRURGQzg5RDc3NkUwMTFDMEYxRDU3RkQyMjM1NDEzQkEyQTg1N0FEMUU1MUNBREEwOEJCOTIwOTI1M0E3QjZBIiwiVGltZXN0YW1wIjoiMHg2QUM1MTMwRiIsIkltYWdlU2l6ZSI6IjB4M0YxRDAwMCIsIlJlYWRPbmx5UHJvY2Vzc0hhbmRsZSI6dHJ1ZX19CnsiQXQiOiIyMDI2LTEwLTEwVDE2OjAxOjE2LjY3MzA2NjMrMDA6MDAiLCJTZXF1ZW5jZSI6NCwiS2luZCI6IkNVUlJFTlRfTkFUSVZFX0NPREVfR1VBUkRTIiwiRGlza0ZsdXNoIjpmYWxzZSwiRGF0YSI6eyJCb2RpZXMiOjUsIkJ5dGVzIjo0MjYyLCJEZWZlcnJlZE1hc2siOjAsIkRlZmVycmVkQ29kZUJ5dGVzIjowLCJDdXJyZW50UGVuZGluZ0J5dGVzQ2xhaW1lZE1hdGNoZWQiOmZhbHNlLCJCb3Jyb3dlZFNjYWxhclNvdXJjZSI6dHJ1ZSwiQWRkaXRpb25hbERlc3RydWN0b3JDYWxscyI6MH19CnsiQXQiOiIyMDI2LTEwLTEwVDE2OjAxOjE2LjY3NzE3ODcrMDA6MDAiLCJTZXF1ZW5jZSI6NSwiS2luZCI6Ik5PTklOVk9LRURfQ09ERV9PQlNFUlZBVElPTiIsIkRpc2tGbHVzaCI6ZmFsc2UsIkRhdGEiOnsiUnZhIjoiMHg3MkM1ODAiLCJSb2xlIjoiTm90IGludm9rZWQgYnkgYm9ycm93ZWQgc2NhbGFyIHNvdXJjZSBwYXRoOyBMQyBvd25zIGNsb25lIGxpZmVjeWNsZSBhbmQgcmVwbGllcyIsIkRpc3BhdGNoUHJlcmVxdWlzaXRlIjpmYWxzZSwiQm9keUNhbGxhYmxlQ2xhaW1lZCI6ZmFsc2UsIkhpc3RvcmljYWxCeXRlc1N1YnN0aXR1dGVkIjpmYWxzZSwiUGFnZXMiOlt7IlJ2YSI6IjB4NzJDNTgwIiwiTGVuZ3RoIjo3MywiUGFnZSI6eyJCYXNlIjoxNDA2OTc3MjI5MjkxNTIsIkFsbG9jYXRpb25CYXNlIjoxNDA2OTc3MTU0MDg4OTYsIkFsbG9jYXRpb25Qcm90ZWN0IjoxMjgsIlBhcnRpdGlvbklkIjowLCJTaXplIjo0MDk2LCJTdGF0ZSI6NDA5NiwiUHJvdGVjdCI6MzIsIlR5cGUiOjE2Nzc3MjE2fSwiQ3VycmVudGx5UmVhZGFibGVFeGVjdXRhYmxlIjp0cnVlLCJDdXJyZW50Qnl0ZXNNYXRjaGVkIjp0cnVlfV19fQp7IkF0IjoiMjAyNi0xMC0xMFQxNjowMToxNi42OTcyNTYzKzAwOjAwIiwiU2VxdWVuY2UiOjYsIktpbmQiOiJOT05JTlZPS0VEX0NPREVfT0JTRVJWQVRJT04iLCJEaXNrRmx1c2giOmZhbHNlLCJEYXRhIjp7IlJ2YSI6IjB4MTAxOUEwIiwiUm9sZSI6Ik5vdCBpbnZva2VkIGJ5IGJvcnJvd2VkIHNjYWxhciBzb3VyY2UgcGF0aDsgTEMgb3ducyBjbG9uZSBsaWZlY3ljbGUgYW5kIHJlcGxpZXMiLCJEaXNwYXRjaFByZXJlcXVpc2l0ZSI6ZmFsc2UsIkJvZHlDYWxsYWJsZUNsYWltZWQiOmZhbHNlLCJIaXN0b3JpY2FsQnl0ZXNTdWJzdGl0dXRlZCI6ZmFsc2UsIlBhZ2VzIjpbeyJSdmEiOiIweDEwMTlBMCIsIkxlbmd0aCI6ODAsIlBhZ2UiOnsiQmFzZSI6MTQwNjk3NzE2NDYxNTY4LCJBbGxvY2F0aW9uQmFzZSI6MTQwNjk3NzE1NDA4ODk2LCJBbGxvY2F0aW9uUHJvdGVjdCI6MTI4LCJQYXJ0aXRpb25JZCI6MCwiU2l6ZSI6NDA5NiwiU3RhdGUiOjQwOTYsIlByb3RlY3QiOjEsIlR5cGUiOjE2Nzc3MjE2fSwiQ3VycmVudGx5UmVhZGFibGVFeGVjdXRhYmxlIjpmYWxzZSwiQ3VycmVudEJ5dGVzTWF0Y2hlZCI6bnVsbH1dfX0KeyJBdCI6IjIwMjYtMTAtMTBUMTY6MDE6MTYuNjk4MjczMiswMDowMCIsIlNlcXVlbmNlIjo3LCJLaW5kIjoiTkVUV09SS19TSEFSRURfQ09VTlRTX1NBTVBMRSIsIkRpc2tGbHVzaCI6ZmFsc2UsIkRhdGEiOnsiUGhhc2UiOiJBIiwiTmV0d29ya0NvbnRyb2wiOjI0MTAxNTA3MjE5MjAsIkFkZHJlc3NIZXgiOiIweDIzMTI4MkQ3OTg4IiwiTGVuZ3RoIjo4LCJDb3VudHMiOnsiU3Ryb25nIjoxLCJXZWFrIjoxfSwiQWxpdmUiOnRydWUsIkNvdW50RXF1YWxpdHlSZXF1aXJlZCI6ZmFsc2V9fQp7IkF0IjoiMjAyNi0xMC0xMFQxNjowMToxNi43MDU1MDI4KzAwOjAwIiwiU2VxdWVuY2UiOjgsIktpbmQiOiJORVRXT1JLX1NIQVJFRF9DT1VOVFNfU0FNUExFIiwiRGlza0ZsdXNoIjpmYWxzZSwiRGF0YSI6eyJQaGFzZSI6IkIiLCJOZXR3b3JrQ29udHJvbCI6MjQxMDE1MDcyMTkyMCwiQWRkcmVzc0hleCI6IjB4MjMxMjgyRDc5ODgiLCJMZW5ndGgiOjgsIkNvdW50cyI6eyJTdHJvbmciOjEsIldlYWsiOjF9LCJBbGl2ZSI6dHJ1ZSwiQ291bnRFcXVhbGl0eVJlcXVpcmVkIjpmYWxzZX19CnsiQXQiOiIyMDI2LTEwLTEwVDE2OjAxOjE2LjcwNTg0NDIrMDA6MDAiLCJTZXF1ZW5jZSI6OSwiS2luZCI6Ik5FVFdPUktfU0hBUkVEX0NPVU5UU19BQiIsIkRpc2tGbHVzaCI6ZmFsc2UsIkRhdGEiOnsiTmV0d29ya0NvbnRyb2wiOjI0MTAxNTA3MjE5MjAsIkNvdW50c0FkZHJlc3MiOiIweDIzMTI4MkQ3OTg4IiwiTGVuZ3RoIjo4LCJPd25Db3VudHNBIjp7IlN0cm9uZyI6MSwiV2VhayI6MX0sIk93bkNvdW50c0IiOnsiU3Ryb25nIjoxLCJXZWFrIjoxfSwiQ291bnRzQ2hhbmdlZCI6ZmFsc2UsIlN0YWJsZUNvbnRyb2xWdGFibGVBYkNsb3NlZCI6dHJ1ZSwiQ291bnRFcXVhbGl0eVJlcXVpcmVkIjpmYWxzZX19CnsiQXQiOiIyMDI2LTEwLTEwVDE2OjAxOjE2LjcxMDUxMjcrMDA6MDAiLCJTZXF1ZW5jZSI6MTAsIktpbmQiOiJQUkVGTElHSFRfQUJfQ0xPU0VEIiwiRGlza0ZsdXNoIjpmYWxzZSwiRGF0YSI6eyJQcm9vZkNvdW50Ijo4NiwiUmVhZENhbGxzIjoyMDUsIlJlYWRCeXRlcyI6MTM1NTIsIk93bmVyNjQiOjExNTIzMTA2NywiQ3VycmVudFNob3AiOjI0MTAxNTg3NjczNzYsIlRhcmdldE93bmVyIjoxMDQwNzMyMjUsIk5ldHdvcmsiOjI0MTAxNTA3MjE5MzYsIk5ldHdvcmtDb250cm9sIjoyNDEwMTUwNzIxOTIwLCJOZXR3b3JrQ2hpbGQiOjI0MTA1MzQ1OTg0NjQsIlBhY2tldENyZWF0ZSI6MTQwNjk3NzIyODcyMjU2LCJQYWNrZXRDb3B5IjoxNDA2OTc3MjMxMjg4OTYsIlN1YnN5c3RlbUNvdW50Ijo2MywiRGVmZXJyZWRNYXNrIjowLCJEZWZlcnJlZENvZGVCeXRlcyI6MCwiRXZlcnlTeW5jaHJvbm91c0d1YXJkQm9keUN1cnJlbnRseU1hdGNoZWQiOnRydWUsIkNvbGRDb2RlQnl0ZXNQZW5kaW5nIjpmYWxzZSwiQXN5bmNBY2tOb3RJbnZva2VkQnlIZWxwZXIiOnRydWUsIk93bkNvdW50c0EiOnsiU3Ryb25nIjoxLCJXZWFrIjoxfSwiT3duQ291bnRzQiI6eyJTdHJvbmciOjEsIldlYWsiOjF9LCJDb3VudEVxdWFsaXR5UmVxdWlyZWQiOmZhbHNlfX0KeyJBdCI6IjIwMjYtMTAtMTBUMTY6MDE6MTYuNzE2NjU5OSswMDowMCIsIlNlcXVlbmNlIjoxMSwiS2luZCI6IkhPT0tfSU5TVEFMTF9JTlRFTlQiLCJEaXNrRmx1c2giOmZhbHNlLCJEYXRhIjp7IlBpZCI6ODE0NCwiQ3JlYXRpb24iOjEzNDM2MDc3NjExOTAyMzE3MywiVGlkIjoxNDM3MiwiSHduZCI6NjcyOTQsIk1vZGUiOjEsIlRhcmdldE93bmVyIjoxMDQwNzMyMjUsIkVudHJ5UnZhIjoiMHgyODE1MTAiLCJOYXRpdmVQYWNrZXRUeXBlIjoiQ1NfUEVSU09OQUxfU0hPUF9MSVNUX1JFUSJ9fQp7IkF0IjoiMjAyNi0xMC0xMFQxNjowMToxNi43MzEwNzIrMDA6MDAiLCJTZXF1ZW5jZSI6MTIsIktpbmQiOiJOQVRJVkVfUEFZTE9BRCIsIkRpc2tGbHVzaCI6ZmFsc2UsIkRhdGEiOnsiUGF0aCI6IkQ6XFxsaW5lYWdlXFxMQ19QbGF5ZXJNYXJrZXRfdjY3XFxyZWxlYXNlLXYyXFxTaG9wQ29udHJvbFxcTENfUGxheWVyU2hvcExpc3RBY3Rpb25Ib29rVjE1LmRsbCIsIlNoYTI1NiI6IjlFREMzMzQxMThBNzMwRTM2Nzk2NkRCMUJGMjM4RkY3MUVCRkQ3RTY0NUM1RUNDQTFDRjREOUNFQkYzRjVEODYiLCJFeHBvcnQiOiJQbGF5ZXJTaG9wTGlzdEhvb2siLCJOZXdQcm90b2NvbCI6dHJ1ZX19CnsiQXQiOiIyMDI2LTEwLTEwVDE2OjAxOjE2LjczOTA5NDgrMDA6MDAiLCJTZXF1ZW5jZSI6MTMsIktpbmQiOiJIT09LX0lOU1RBTExfUkVTVUxUIiwiRGlza0ZsdXNoIjpmYWxzZSwiRGF0YSI6eyJJbnN0YWxsZWQiOnRydWUsIldpbjMyRXJyb3IiOjAsIkhvb2tUeXBlIjoiV0hfR0VUTUVTU0FHRSIsIlRocmVhZCI6MTQzNzIsIkdsb2JhbEhvb2siOmZhbHNlfX0KeyJBdCI6IjIwMjYtMTAtMTBUMTY6MDE6MTcuMDAyNDMyKzAwOjAwIiwiU2VxdWVuY2UiOjE0LCJLaW5kIjoiU1VCTUlUX0NPREVfUFJFRkxJR0hUX1JFQ0hFQ0siLCJEaXNrRmx1c2giOmZhbHNlLCJEYXRhIjp7IkZ1bmN0aW9uUnZhIjoiMHgyODE1MTAiLCJSdmEiOiIweDI4MTUxMCIsIkxlbmd0aCI6MjgwMCwiQmVmb3JlIjp7IkJhc2UiOjE0MDY5NzcxODAzNDQzMiwiQWxsb2NhdGlvbkJhc2UiOjE0MDY5NzcxNTQwODg5NiwiQWxsb2NhdGlvblByb3RlY3QiOjEyOCwiUGFydGl0aW9uSWQiOjAsIlNpemUiOjQwOTYsIlN0YXRlIjo0MDk2LCJQcm90ZWN0IjoxLCJUeXBlIjoxNjc3NzIxNn0sIkFmdGVyIjp7IkJhc2UiOjE0MDY5NzcxODAzNDQzMiwiQWxsb2NhdGlvbkJhc2UiOjE0MDY5NzcxNTQwODg5NiwiQWxsb2NhdGlvblByb3RlY3QiOjEyOCwiUGFydGl0aW9uSWQiOjAsIlNpemUiOjQwOTYsIlN0YXRlIjo0MDk2LCJQcm90ZWN0IjoxLCJUeXBlIjoxNjc3NzIxNn0sIkF0dGVtcHRlZCI6dHJ1ZSwiUXVlcmllcyI6OCwiRWxhcHNlZE1pbGxpc2Vjb25kcyI6MjU5LjkzNDM5OTk5OTY3ODEzLCJPdXRjb21lIjoiQnVkZ2V0RXhoYXVzdGVkIiwiVG90YWxXYWl0TWlsbGlzZWNvbmRzIjoyNTkuNzA0MDk5OTk3ODc4MSwiVG90YWxRdWVyaWVzIjo4LCJNYXhpbXVtV2FpdE1pbGxpc2Vjb25kcyI6MjUwLCJSZWFkT25seSI6dHJ1ZSwiUmVxdWVzdE5vdFB1Ymxpc2hlZCI6dHJ1ZSwiUHJvdGVjdGlvbkNoYW5nZWQiOmZhbHNlLCJEZWZlcnJlZCI6ZmFsc2UsIkN1cnJlbnRCeXRlc01hdGNoZWQiOmZhbHNlLCJGdWxsQnl0ZUFuZFNvdXJjZVByb29mU3RpbGxSZXF1aXJlZCI6dHJ1ZX19CnsiQXQiOiIyMDI2LTEwLTEwVDE2OjAxOjE3LjAxMTUwMjUrMDA6MDAiLCJTZXF1ZW5jZSI6MTUsIktpbmQiOiJOQVRJVkVfQ09ERV9QQUdFX1VOQVZBSUxBQkxFIiwiRGlza0ZsdXNoIjpmYWxzZSwiRGF0YSI6eyJSdmEiOiIweDI4MTUxMCIsIlBhZ2UiOnsiQmFzZSI6MTQwNjk3NzE4MDM0NDMyLCJBbGxvY2F0aW9uQmFzZSI6MTQwNjk3NzE1NDA4ODk2LCJBbGxvY2F0aW9uUHJvdGVjdCI6MTI4LCJQYXJ0aXRpb25JZCI6MCwiU2l6ZSI6NDA5NiwiU3RhdGUiOjQwOTYsIlByb3RlY3QiOjEsIlR5cGUiOjE2Nzc3MjE2fSwiUGhhc2UiOiJQcmVmbGlnaHQiLCJDb2RlIjoiTmF0aXZlQ29kZVVuYXZhaWxhYmxlIn19CnsiQXQiOiIyMDI2LTEwLTEwVDE2OjAxOjE3LjAxNDE1MzYrMDA6MDAiLCJTZXF1ZW5jZSI6MTYsIktpbmQiOiJFUlJPUiIsIkRpc2tGbHVzaCI6dHJ1ZSwiRGF0YSI6eyJUeXBlIjoiTmF0aXZlUHJlZmxpZ2h0RXhjZXB0aW9uIiwiTWVzc2FnZSI6Ik5hdGl2ZSBjb2RlIGlzIG5vdCBjdXJyZW50bHkgcmVhZGFibGUvZXhlY3V0YWJsZSBNRU1fSU1BR0U6IDB4MjgxNTEwIiwiQ29kZSI6Ik5hdGl2ZUNvZGVVbmF2YWlsYWJsZSIsIlBoYXNlIjoiQmVmb3JlUHVibGlzaCIsIlJ2YSI6IjB4MjgxNTEwIn19CnsiQXQiOiIyMDI2LTEwLTEwVDE2OjAxOjE3LjIzMzY4NDUrMDA6MDAiLCJTZXF1ZW5jZSI6MTcsIktpbmQiOiJOQVRJVkVfRklOQUxfU1RBVEUiLCJEaXNrRmx1c2giOmZhbHNlLCJEYXRhIjp7IlN0YXRlIjp7IlNlcXVlbmNlIjowLCJDb21wbGV0ZWQiOjAsIkFjdGl2ZSI6MCwiQ2xhaW1lZCI6MCwiRW50ZXJlZCI6MCwiUmV0dXJuZWQiOjAsIlN0YXR1cyI6MCwiRXhjZXB0aW9uIjowLCJPYnNlcnZlZFBpZCI6MCwiT2JzZXJ2ZWRUaWQiOjAsIk5vbmNlIjowLCJUaWNrIjowLCJDYWxsYmFjayI6MCwiTW9kdWxlIjowLCJTdGFnZSI6MCwiUmVxdWVzdE1ldGFkYXRhQmVmb3JlIjowLCJSZXF1ZXN0TWV0YWRhdGFBZnRlciI6MCwiUmVxdWVzdFVuY2hhbmdlZCI6MCwiRGVzdHJ1Y3RvckNhbGxzIjowLCJIb3N0RGVmZXJyZWRNYXNrIjowLCJOYXRpdmVEZWZlcnJlZE1hc2siOjAsIlBvc3RDb2RlQ29uZmlybWVkIjowLCJFeGNlcHRpb25TdGFnZSI6MCwiRXhjZXB0aW9uQWRkcmVzcyI6MCwiRXhjZXB0aW9uQWNjZXNzQWRkcmVzcyI6MCwiRXhjZXB0aW9uQWNjZXNzS2luZCI6MCwiTGVhc2VSZWxlYXNlQXR0ZW1wdGVkIjowLCJMZWFzZVJlbGVhc2VDb21wbGV0ZWQiOjAsIkxlYXNlQWNxdWlyZWQiOjAsIkV4Y2VwdGlvbkNvdW50IjowLCJSZWxlYXNlRXhjZXB0aW9uQ29kZSI6MCwiUmVsZWFzZUV4Y2VwdGlvbkFkZHJlc3MiOjB9LCJRdWllc2NlbnRPYnNlcnZlZCI6dHJ1ZSwiRGlzYXJtZWRCZWZvcmVDbGFpbSI6ZmFsc2UsIkVudGlyZUNhbGxOZXh0SG9va1RhaWxFeGl0UHJvdmVuIjpmYWxzZX19CnsiQXQiOiIyMDI2LTEwLTEwVDE2OjAxOjE3LjI0ODA3NjUrMDA6MDAiLCJTZXF1ZW5jZSI6MTgsIktpbmQiOiJVTkhPT0siLCJEaXNrRmx1c2giOmZhbHNlLCJEYXRhIjp7IlN1Y2Nlc3MiOnRydWUsIldpbjMyRXJyb3IiOjAsIkludGVycnVwdHNBY3RpdmVDYWxsYmFjayI6ZmFsc2V9fQp7IkF0IjoiMjAyNi0xMC0xMFQxNjowMToxNy4yNTA4NzA5KzAwOjAwIiwiU2VxdWVuY2UiOjE5LCJLaW5kIjoiTE9DQUxfVklFV19SRUxFQVNFIiwiRGlza0ZsdXNoIjpmYWxzZSwiRGF0YSI6eyJTdWNjZXNzIjp0cnVlLCJXaW4zMkVycm9yIjowfX0KeyJBdCI6IjIwMjYtMTAtMTBUMTY6MDE6MTcuMjUzNzUzNiswMDowMCIsIlNlcXVlbmNlIjoyMCwiS2luZCI6IkxPQ0FMX01BUFBJTkdfQ0xPU0UiLCJEaXNrRmx1c2giOmZhbHNlLCJEYXRhIjp7IlN1Y2Nlc3MiOnRydWUsIldpbjMyRXJyb3IiOjB9fQp7IkF0IjoiMjAyNi0xMC0xMFQxNjowMToxNy4yNTQwMDU0KzAwOjAwIiwiU2VxdWVuY2UiOjIxLCJLaW5kIjoiTE9DQUxfTElCUkFSWV9GUkVFIiwiRGlza0ZsdXNoIjpmYWxzZSwiRGF0YSI6eyJTdWNjZXNzIjp0cnVlfX0KeyJBdCI6IjIwMjYtMTAtMTBUMTY6MDE6MTcuMjU2NzQ5KzAwOjAwIiwiU2VxdWVuY2UiOjIyLCJLaW5kIjoiQ0xFQU5VUCIsIkRpc2tGbHVzaCI6ZmFsc2UsIkRhdGEiOnsiQ2xlYW51cENvbmZpcm1lZCI6dHJ1ZSwiUXVpZXNjZW50T2JzZXJ2ZWQiOnRydWUsIkluc3RhbGxlZCI6dHJ1ZSwiVW5ob29rZWQiOnRydWUsIkxvY2FsVmlld1JldGFpbmVkIjpmYWxzZSwiTWFwcGluZ1JldGFpbmVkIjpmYWxzZSwiTGlicmFyeVJldGFpbmVkIjpmYWxzZSwiSG9va1JldGFpbmVkIjpmYWxzZSwiUmVtb3RlTW9kdWxlVW5sb2FkUHJvdmVuIjpmYWxzZSwiRW50aXJlQ2FsbE5leHRIb29rVGFpbEV4aXRQcm92ZW4iOmZhbHNlLCJBZGRpdGlvbmFsTWVzc2FnZXNTZW50IjowfX0KeyJBdCI6IjIwMjYtMTAtMTBUMTY6MDE6MTcuMjYwNjA5MyswMDowMCIsIlNlcXVlbmNlIjoyMywiS2luZCI6IkVORCIsIkRpc2tGbHVzaCI6dHJ1ZSwiRGF0YSI6eyJFeGl0Q29kZSI6MSwiUG9zdENvZGVDb25maXJtZWQiOmZhbHNlLCJFdmVudFJlc3VsdER1cmFibGUiOmZhbHNlLCJCb3Jyb3dlZFNvdXJjZUNvbnRyYWN0VmVyaWZpZWQiOmZhbHNlLCJOYXRpdmVBbHJlYWR5UmV0dXJuZWQiOmZhbHNlLCJBY2tPYnNlcnZlZCI6ZmFsc2UsIk5hdGl2ZVJlc3VsdCI6bnVsbCwiQ2xlYW51cENvbmZpcm1lZCI6dHJ1ZSwiU2Vzc2lvbkJsb2NrZWQiOmZhbHNlLCJQZW5kaW5nSm91cm5hbCI6IkQ6XFxsaW5lYWdlXFxMQ19QbGF5ZXJTaG9wTGlzdEFjdGlvbl92MVxcam91cm5hbFxcODE0NC0xMzQzNjA3NzYxMTkwMjMxNzMuZm9yY2UtNjhjYTFmNjEyODY1NGQ0MmI1YjYzMmRlZmE0YWMyYmIucGVuZGluZy5qc29uIiwiU2luZ2xlVHJpYWxQZW5kaW5nSm91cm5hbCI6IkQ6XFxsaW5lYWdlXFxMQ19QbGF5ZXJTaG9wTGlzdEFjdGlvbl92MVxcam91cm5hbFxcODE0NC0xMzQzNjA3NzYxMTkwMjMxNzMudjE1LXNpbmdsZXRyaWFsLnBlbmRpbmcuanNvbiIsIlNpbmdsZVRyaWFsUGVuZGluZ1NjaGVtYSI6IlYxNVNpbmdsZVRyaWFsUGVuZGluZ1YxIiwiTGlzdFJlY2VpdmVkVmVyaWZpZWQiOmZhbHNlLCJSZW1vdGVNb2R1bGVVbmxvYWRQcm92ZW4iOmZhbHNlLCJFbnRpcmVIb29rVGFpbEV4aXRQcm92ZW4iOmZhbHNlLCJMb2dSZWNvcmRzQmVmb3JlRW5kIjoyMiwiTG9nRGlza0ZsdXNoZXNCZWZvcmVFbmQiOjEsIkxvZ1N0cmVhbUZsdXNoZXNCZWZvcmVFbmQiOjAsIkxvZ0J1ZmZlcmVkUmVjb3Jkc0JlZm9yZUVuZCI6MjEsIkxvZ1dyaXRlRHVyYXRpb25Nc0JlZm9yZUVuZCI6MzA3LjM1ODEwMDAwMDAwMDA0LCJMb2dJb1dyaXRlRHVyYXRpb25Nc0JlZm9yZUVuZCI6OTAuMTA3ODAwMDAwMDAwMDEsIkxvZ1NlcmlhbGl6YXRpb25EdXJhdGlvbk1zQmVmb3JlRW5kIjoyMTcuMjUwM319Cg==";
}
