using System.Text.Json;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static class PatrolV62Verification
{
    internal static async Task<int> RunAsync(string output)
    {
        var checks = (await PatrolAutoResumeSelfTest.RunAsync()).Concat(SourceChecks()).ToArray();
        var report = new OfflineCheckReport("83.0.0", checks.All(c => c.Passed),
            checks.Count(c => c.Passed), checks.Count(c => !c.Passed), false, false, false, checks);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(file, report, HubProtocol.Json);
        file.Flush(true);
        return report.Success ? 0 : 1;
    }

    internal static OfflineCheck[] SourceChecks()
    {
        var source = new SourceIdentity(1, 2, "offline-LC", "0x10000", 3, 4, "sha", "session");
        ReadResult Timeout() => new(null, new()
        {
            ["selfId"] = FieldReading.Unknown("TIME_LIMIT"),
            ["position"] = FieldReading.Unknown("TIME_LIMIT"),
            ["nearbyPlayers"] = FieldReading.Unknown("TIME_LIMIT"),
            ["playerShopStates"] = FieldReading.Unknown("TIME_LIMIT")
        }, "Unknown", "") { ReadFailureCode = "TIME_LIMIT", ReadFailureNativeError = 0 };
        var checks = new List<OfflineCheck>();
        void Check(string name, ReadResult result, SourceIdentity? current, string expectedCode, bool force = true)
        {
            string actual = "Accepted";
            try { ShopRefreshFailurePolicy.RequireReadSource(result, source, current, force); }
            catch (ShopRequestFailure error) { actual = error.Failure.Code; }
            checks.Add(new(name, actual == expectedCode, $"expected={expectedCode}; actual={actual}; fake read only"));
        }
        Check("patrol_v57_force_timeout_remains_typed_read_failure", Timeout(), source, "RefreshReadTimeout");
        Check("patrol_v57_force_timeout_without_background_source_remains_typed", Timeout(), null, "RefreshReadTimeout");
        Check("patrol_v57_force_background_gap_is_typed", new(source, new(), "Reading", ""), null, "RefreshSourceSampleGap");
        Check("patrol_v57_strict_timeout_classification_preserved", Timeout(), source, "RefreshReadTimeout", false);
        Check("patrol_v57_known_different_source_wins", Timeout(), source with { SessionKey = "other" }, "SourceMismatch");
        var invalid = Timeout() with { NoSourceRead = true };
        Check("patrol_v57_no_source_read_does_not_become_retry", invalid, source, "SourceUnavailable");
        invalid = Timeout() with { ReadFailureNativeError = 5 };
        Check("patrol_v57_native_error_does_not_become_retry", invalid, source, "SourceUnavailable");
        invalid = Timeout(); invalid.Fields.Remove("playerShopStates");
        Check("patrol_v57_missing_shop_evidence_does_not_become_retry", invalid, source, "SourceUnavailable");
        invalid = Timeout(); invalid.Fields["position"] = FieldReading.Observed(new { x = 1, y = 2 }, DateTimeOffset.UtcNow);
        Check("patrol_v57_partial_timeout_does_not_become_retry", invalid, source, "SourceUnavailable");
        invalid = Timeout() with { ReadFailureCode = "RPM_INCOMPLETE" };
        Check("patrol_v57_unclassified_read_failure_does_not_become_retry", invalid, source, "SourceUnavailable");
        return checks.ToArray();
    }
}
