using System.Text.Json;
using System.Text.Json.Nodes;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Replays preserved helper logs; it never launches a helper or opens LC.</summary>
internal static class ForcePreflightReplaySelfTest
{
    internal static OfflineCheck[] Run(string? fixtureDirectory = null)
    {
        var checks = new List<OfflineCheck>();
        string[] names = ["6958ecc5ca3f4c709cf173b6b13e96ab", "2618a39cc25a457bb8f102c52ecf04fb",
            "1dac76a9d01c4e3bb8e3a35fed57f045", "7dcedfcba1254330aae5a2cea44f8709"];
        foreach (string name in names)
        {
            try
            {
                string fixture = ReadFixture(name, fixtureDirectory);
                var rows = Read(fixture);
                var identity = rows[2]["Data"]!;
                ulong image = identity["Image"]!.GetValue<ulong>(), owner = identity["TargetOwner"]!.GetValue<ulong>();
                var source = new SourceIdentity(identity["Pid"]!.GetValue<int>(), identity["Creation"]!.GetValue<long>(),
                    identity["Path"]!.GetValue<string>(), $"0x{image:X}",
                    Convert.ToUInt32(identity["Timestamp"]!.GetValue<string>()[2..], 16),
                    Convert.ToUInt32(identity["ImageSize"]!.GetValue<string>()[2..], 16), identity["Sha256"]!.GetValue<string>(), "recorded-source");
                var parsed = ShopNativeOutcome.Parse(fixture, source, image, owner, 1);
                Need(parsed.SafeToSettle && parsed.NoEntryConfirmed && !parsed.Entered && !parsed.Returned
                    && parsed.Failure is { Code: "NativeCodeUnavailable", Scope: "Global", Phase: "Preflight", Rva: "0x27D620" }, parsed.Detail);
                checks.Add(new("force_preflight_actual_" + name, true, "完整同來源未發送證據；不宣稱成功或 ACK"));
                if (name != names[0]) continue;
                void Check(string suffix, Func<bool> work)
                {
                    try { Need(work(), suffix); checks.Add(new("force_preflight_" + suffix, true, "通過")); }
                    catch (Exception failure) { checks.Add(new("force_preflight_" + suffix, false, failure.ToString())); }
                }
                string Mutate(Action<JsonObject[]> change)
                {
                    var copy = Read(fixture); change(copy); return Write(copy);
                }
                void Reject(string suffix, Action<JsonObject[]> change) =>
                    Check(suffix, () => !ShopNativeOutcome.Parse(Mutate(change), source, image, owner, 1).NoEntryConfirmed);
                void CurrentJournal(JsonObject[] values, string path)
                {
                    values[1]["Data"]!["CurrentPending"] = path;
                    values[^1]["Data"]!["PendingJournal"] = path;
                }
                Reject("reject_force_wait", values => values[1]["Data"]!["WaitForPreviousGameState"] = true);
                Reject("reject_force_previous_foreign_process", values => values[1]["Data"]!["PreviousPending"] = @"D:\lineage\LC_PlayerShopListAction_v1\journal\1-1.pending.json");
                Reject("reject_force_wrong_directory", values => CurrentJournal(values, @"D:\other\16632-134356008593162424.force-72300be8d4614c4fabeaa99cb1640497.pending.json"));
                Reject("reject_force_path_traversal", values => CurrentJournal(values, @"D:\lineage\LC_PlayerShopListAction_v1\journal\..\journal\16632-134356008593162424.force-72300be8d4614c4fabeaa99cb1640497.pending.json"));
                Reject("reject_force_non_guid", values => CurrentJournal(values, @"D:\lineage\LC_PlayerShopListAction_v1\journal\16632-134356008593162424.force-NOT-A-GUID.pending.json"));
                Reject("reject_force_shared_pending", values => CurrentJournal(values, values[1]["Data"]!["PreviousPending"]!.GetValue<string>()));
                Reject("reject_force_current_foreign_process", values => CurrentJournal(values, @"D:\lineage\LC_PlayerShopListAction_v1\journal\1-1.force-72300be8d4614c4fabeaa99cb1640497.pending.json"));
                Reject("reject_force_missing_preservation_flag", values => values[1]["Data"]!.AsObject().Remove("PreviousPendingPreserved"));
                Reject("reject_force_wrong_record_order", values => values[1]["Kind"] = "SOURCE");
                Reject("reject_force_non_boolean_preservation_flag", values => values[1]["Data"]!["PreviousPendingPreserved"] = "false");
                Reject("reject_force_end_other_journal", values => values[^1]["Data"]!["PendingJournal"] = values[1]["Data"]!["PreviousPending"]!.GetValue<string>());
                Reject("reject_changed_source", values => values[2]["Data"]!["Creation"] = 1);
                Reject("reject_changed_owner", values => values[2]["Data"]!["TargetOwner"] = 1);
                Reject("reject_changed_source_path", values => values[2]["Data"]!["Path"] = "other.exe");
                Reject("reject_changed_source_SHA", values => values[2]["Data"]!["Sha256"] = new string('0', 64));
                Reject("reject_changed_PE", values => values[2]["Data"]!["Timestamp"] = "0x1");
                Reject("reject_input_evidence", values => values[0]["Data"]!["KeyMessages"] = 1);
                Reject("reject_duplicate_sequence", values => values[3]["Sequence"] = 3);
                Reject("reject_published_request", values => values[3]["Data"]!["RequestNotPublished"] = false);
                Reject("reject_changed_protection", values => values[3]["Data"]!["ProtectionChanged"] = true);
                Reject("reject_wrong_slice", values => values[3]["Data"]!["Length"] = 1);
                Reject("reject_wrong_preflight_page", values => values[3]["Data"]!["After"]!["Protect"] = 32);
                Reject("reject_later_failure_phase", values => values[5]["Data"]!["Phase"] = "BeforePublish");
                Reject("reject_non_durable_end", values => values[^1]["DiskFlush"] = false);
                Reject("reject_non_durable_error", values => values[^2]["DiskFlush"] = false);
                Reject("reject_wrong_record_count", values => values[^1]["Data"]!["LogRecordsBeforeEnd"] = 5);
                Reject("reject_wrong_stream_flush_count", values => values[^1]["Data"]!["LogStreamFlushesBeforeEnd"] = 4);
                Reject("reject_cleanup_unconfirmed", values => values[^1]["Data"]!["CleanupConfirmed"] = false);
                Reject("reject_session_blocked", values => values[^1]["Data"]!["SessionBlocked"] = true);
                Reject("reject_already_returned", values => values[^1]["Data"]!["NativeAlreadyReturned"] = true);
                Reject("reject_ack", values => values[^1]["Data"]!["AckObserved"] = true);
                Reject("reject_claimed_list", values => values[^1]["Data"]!["ListReceivedVerified"] = true);
                Check("reject_wrong_exit_code", () => !ShopNativeOutcome.Parse(fixture, source, image, owner, 0).NoEntryConfirmed);
                Check("reject_truncated_log", () => !ShopNativeOutcome.Parse(Write(rows[..^1]), source, image, owner, 1).NoEntryConfirmed);
                Check("reject_force_duplicate_property", () => !ShopNativeOutcome.Parse(fixture.Replace(
                    "\"WaitForPreviousGameState\":false", "\"WaitForPreviousGameState\":false,\"WaitForPreviousGameState\":false", StringComparison.Ordinal), source, image, owner, 1).NoEntryConfirmed);
                Check("reject_extra_transport_record", () =>
                {
                    var extra = Read(fixture).ToList();
                    extra.Insert(2, JsonNode.Parse("{\"Kind\":\"HOOK_INSTALL_INTENT\",\"Data\":{}}")!.AsObject());
                    for (int index = 0; index < extra.Count; index++) extra[index]["Sequence"] = index + 1;
                    return !ShopNativeOutcome.Parse(Write(extra.ToArray()), source, image, owner, 1).NoEntryConfirmed;
                });
                Check("legacy_six_record_path_retained", () =>
                {
                    var legacy = Read(fixture).Where(value => value["Kind"]!.GetValue<string>() != "FORCE_DISPATCH").ToArray();
                    for (int index = 0; index < legacy.Length; index++) legacy[index]["Sequence"] = index + 1;
                    legacy[^1]["Data"]!["LogRecordsBeforeEnd"] = 5;
                    legacy[^1]["Data"]!["LogStreamFlushesBeforeEnd"] = 4;
                    var result = ShopNativeOutcome.Parse(Write(legacy), source, image, owner, 1);
                    return result.SafeToSettle && result.NoEntryConfirmed && !result.Entered && !result.Returned;
                });
                Check("force_previous_pending_preserved_is_allowed", () =>
                    ShopNativeOutcome.Parse(Mutate(values => values[1]["Data"]!["PreviousPendingPreserved"] = true), source, image, owner, 1).NoEntryConfirmed);
            }
            catch (Exception failure) { checks.Add(new("force_preflight_actual_" + name, false, failure.ToString())); }
        }
        return checks.ToArray();
    }

    private static JsonObject[] Read(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
    private static string Write(JsonObject[] rows) => string.Join('\n', rows.Select(value => value.ToJsonString()));
    private static string ReadFixture(string name, string? directory)
    {
        if (directory is not null) return File.ReadAllText(Path.Combine(directory, name + ".jsonl"));
        var assembly = typeof(ForcePreflightReplaySelfTest).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(value => value.EndsWith("." + name + ".jsonl", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException("Missing preserved force preflight fixture.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
}
