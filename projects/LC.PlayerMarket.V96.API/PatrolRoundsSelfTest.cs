using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LcPlayerMarket;

internal static class PatrolRoundsSelfTest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static async Task<int> RunAsync(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Test output already exists.");
        Directory.CreateDirectory(output);
        var checks = new List<OfflineCheck>();
        void Check(string name, Action<string> action)
        {
            string state = Path.Combine(output, "fixtures", name);
            try { action(state); checks.Add(new(name, true, "Isolated fixture; no GUI, LC or native dispatch.")); }
            catch (Exception e) { checks.Add(new(name, false, e.ToString())); }
        }
        PatrolWaypoint Point() => new(Guid.NewGuid().ToString("N"), "offline point", "4", "offline", 100, 100);
        PatrolRouteSnapshot Load(PatrolRouteStore store)
        {
            var value = store.Load();
            Need(value.Success, value.Error ?? "Load failed.");
            return value.Snapshot!;
        }
        string RoutePath(PatrolRouteStore store, PatrolRouteSnapshot route) =>
            Path.Combine(store.DirectoryPath, route.SavedAt.UtcTicks.ToString("D19") + "-" + route.Revision + ".route.json");

        Check("rounds_unsaved_defaults_to_one_without_writing", state =>
        {
            var loaded = Load(new(state));
            Need(loaded.RoundCount == 1 && !Directory.Exists(state), "Default read wrote state or changed round count.");
        });
        foreach (int rounds in new[] { 1, 100 })
            Check("rounds_save_reopen_" + rounds, state =>
            {
                var store = new PatrolRouteStore(state);
                Need(store.TrySave([Point()], 0, 1, out var saved, out string error, roundCount: rounds), error);
                byte[] original = File.ReadAllBytes(RoutePath(store, saved!));
                var reopened = Load(new(state));
                Need(reopened.RoundCount == rounds && reopened.Revision == saved!.Revision, "Reopen lost rounds/revision.");
                Need(original.SequenceEqual(File.ReadAllBytes(RoutePath(store, reopened))), "Loading rewrote route.");
                Need(store.TrySave(reopened.Points, 2, 2, out var edited, out error, expectedRevision: reopened.Revision), error);
                Need(edited!.RoundCount == rounds, "Unrelated edit reset saved round count.");
                var document = JsonNode.Parse(original)!.AsObject();
                Need(!document.ContainsKey("roundCount"), "V50 inserted unknown field into legacy route.");
                var legacy = JsonSerializer.Deserialize<LegacyRoute>(original, Json);
                Need(legacy is not null && legacy.Revision == saved!.Revision, "Strict V49-format reader rejected route.");
            });
        Check("rounds_legacy_and_later_v49_revision_default_to_one", state =>
        {
            var store = new PatrolRouteStore(state);
            Need(store.TrySave([Point()], 0, 1, out var saved, out string error, roundCount: 100), error);
            var next = saved! with { Revision = Guid.NewGuid().ToString("N"), SavedAt = saved.SavedAt.AddTicks(1) };
            // V49 writes only the legacy route. It neither reads nor adopts another revision's sidecar.
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            File.WriteAllBytes(RoutePath(store, next), bytes);
            Need(Load(new(state)).RoundCount == 1, "New legacy revision inherited unrelated rounds.");
            Need(bytes.SequenceEqual(File.ReadAllBytes(RoutePath(store, next))), "Legacy route bytes changed.");
        });
        foreach (int invalid in new[] { 0, 101, -1, int.MaxValue })
            Check("rounds_save_rejects_" + invalid, state =>
            {
                var store = new PatrolRouteStore(state);
                Need(!store.TrySave([Point()], 0, 1, out _, out _, roundCount: invalid), "Invalid count accepted.");
                Need(!Directory.Exists(state), "Invalid count created state before validation.");
            });
        foreach (string mode in new[] { "json", "count", "null", "fraction", "string", "revision", "sha", "unknown" })
            Check("rounds_corrupt_sidecar_rejected_" + mode, state =>
            {
                var store = new PatrolRouteStore(state);
                Need(store.TrySave([Point()], 0, 1, out var route, out string error, roundCount: 3), error);
                string sidecar = PatrolRoundSettings.SidecarPath(RoutePath(store, route!), route!.Revision);
                var value = JsonNode.Parse(File.ReadAllText(sidecar))!.AsObject();
                if (mode == "json") File.WriteAllText(sidecar, "{broken");
                else
                {
                    switch (mode)
                    {
                        case "count": value["roundCount"] = 101; break;
                        case "null": value["roundCount"] = null; break;
                        case "fraction": value["roundCount"] = 1.5; break;
                        case "string": value["roundCount"] = "3"; break;
                        case "revision": value["revision"] = Guid.NewGuid().ToString("N"); break;
                        case "sha": value["routeSha256"] = new string('0', 64); break;
                        case "unknown": value["other"] = 1; break;
                    }
                    File.WriteAllText(sidecar, value.ToJsonString());
                }
                byte[] prior = File.ReadAllBytes(RoutePath(store, route));
                Need(!new PatrolRouteStore(state).Load().Success, "Bad sidecar silently defaulted to one.");
                Need(!store.TrySave([Point()], 0, 1, out _, out _, roundCount: 1), "Save hid corrupt current sidecar.");
                Need(prior.SequenceEqual(File.ReadAllBytes(RoutePath(store, route))), "Rejected save changed original route.");
            });
        Check("rounds_sidecar_write_failure_never_commits_route", state =>
        {
            Directory.CreateDirectory(state);
            File.WriteAllText(Path.Combine(state, "patrol-rounds"), "block directory creation");
            var store = new PatrolRouteStore(state);
            Need(!store.TrySave([Point()], 0, 1, out _, out _, roundCount: 2), "Sidecar failure reported success.");
            Need(!Directory.EnumerateFiles(store.DirectoryPath, "*.route.json").Any(), "Route committed without sidecar.");
            Need(Load(new(state)).Points.Length == 0, "Failed save became visible.");
        });
        Check("rounds_route_commit_failure_orphan_never_becomes_current", state =>
        {
            var store = new PatrolRouteStore(state);
            Need(store.TrySave([Point()], 0, 1, out var prior, out string error, roundCount: 2), error);
            var attempted = prior! with { Revision = Guid.NewGuid().ToString("N"), SavedAt = prior.SavedAt.AddTicks(1), RoundCount = 100 };
            string destination = RoutePath(store, attempted);
            Directory.CreateDirectory(destination); // Deterministic file publication failure, using the real commit implementation.
            bool rejected = false;
            try { PatrolRoundSettings.Commit(destination, attempted, JsonSerializer.SerializeToUtf8Bytes(attempted, Json)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { rejected = true; }
            Need(rejected && File.Exists(PatrolRoundSettings.SidecarPath(destination, attempted.Revision)), "Two-file failure not exercised.");
            var reopened = Load(new(state));
            Need(reopened.Revision == prior.Revision && reopened.RoundCount == 2, "Orphan sidecar changed current route.");
        });
        Check("rounds_import_preserves_bound_count_and_legacy_bytes", state =>
        {
            var old = new PatrolRouteStore(Path.Combine(state, "LC.PlayerMarket", "V49"));
            Need(old.TrySave([Point()], 0, 1, out var saved, out string error, roundCount: 100), error);
            byte[] oldBytes = File.ReadAllBytes(RoutePath(old, saved!));
            var next = new PatrolRouteStore(Path.Combine(state, "LC.PlayerMarket", "V50"));
            var imported = Load(next);
            Need(imported.RoundCount == 100 && imported.Revision == saved!.Revision, "Import lost revision-bound count.");
            Need(oldBytes.SequenceEqual(File.ReadAllBytes(RoutePath(next, imported))), "Import changed legacy route bytes.");
        });
        foreach (bool conflict in new[] { false, true })
            Check("rounds_interrupted_import_sidecar_retry_conflict_" + conflict, state =>
            {
                var old = new PatrolRouteStore(Path.Combine(state, "LC.PlayerMarket", "V49"));
                Need(old.TrySave([Point()], 0, 1, out var saved, out string error, roundCount: 100), error);
                string original = RoutePath(old, saved!);
                byte[] prior = File.ReadAllBytes(original);
                var next = new PatrolRouteStore(Path.Combine(state, "LC.PlayerMarket", "V50"));
                string destination = RoutePath(next, saved!);
                string sidecar = PatrolRoundSettings.SidecarPath(destination, saved!.Revision);
                Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
                byte[] prepared = File.ReadAllBytes(PatrolRoundSettings.SidecarPath(original, saved.Revision));
                if (conflict)
                {
                    var value = JsonNode.Parse(prepared)!.AsObject(); value["roundCount"] = 2;
                    prepared = System.Text.Encoding.UTF8.GetBytes(value.ToJsonString());
                }
                File.WriteAllBytes(sidecar, prepared); // State after sidecar publication, before the route commit.
                var loaded = next.Load();
                Need(loaded.Success != conflict, "Import failed to resume identical sidecar or accepted a conflicting one.");
                Need(File.Exists(destination) != conflict, "Conflicting sidecar published a route.");
                Need(prepared.SequenceEqual(File.ReadAllBytes(sidecar)) && prior.SequenceEqual(File.ReadAllBytes(original)),
                    "Retry overwrote immutable sidecar or old route.");
                if (!conflict) Need(loaded.Snapshot!.RoundCount == 100 && prior.SequenceEqual(File.ReadAllBytes(destination)),
                    "Resumed import lost rounds or changed route bytes.");
            });
        Check("rounds_hud_keeps_round_during_scan_and_terminal", _ =>
        {
            var progress = new PatrolProgress("run", "Searching", 0, 2, "p", "point", 0, "detail")
                { CurrentRound = 2, TotalRounds = 3, CompletedRounds = 1 };
            var patrol = PatrolOverlayPresentation.Build("Searching", "detail", DateTimeOffset.UtcNow, progress);
            Need(patrol.Progress.Contains("2/3") && patrol.Progress.Contains("1/2"), "HUD round/point are ambiguous.");
            var scan = new SearchOverlayView("Scanning", "owner", "1/8", "scan detail", true);
            var combined = PatrolOverlayPresentation.Combine(scan, patrol, true, false, true, false);
            Need(combined.Progress.Contains("2/3") && combined.Progress.Contains("1/8"), "Scan hides round progress.");
            var done = PatrolOverlayPresentation.Build("Completed", "done", DateTimeOffset.UtcNow,
                progress with { CurrentRound = 3, CompletedRounds = 3 });
            Need(done.Progress.Contains("3/3") && !done.Busy, "Terminal HUD lost total rounds.");
        });

        checks.AddRange(await PatrolControllerSelfTest.RunRoundsAsync().ConfigureAwait(false));
        // Directly relevant existing receipt/source/cleanup and immutable store regressions only.
        checks.AddRange(await PatrolControllerSelfTest.RunAsync().ConfigureAwait(false));
        checks.AddRange(PatrolRouteStoreSelfTest.Run());
        var report = new { Version = "50.0.0", Success = checks.Count > 0 && checks.All(c => c.Passed),
            Passed = checks.Count(c => c.Passed), Failed = checks.Count(c => !c.Passed),
            Scope = "Actual coordinator/store/presentation with isolated files and in-memory ports.",
            RealGamePatrolVerified = false, RealNativeHistoryDedupVerified = false,
            GuiStarted = false, LcAccess = false, NavigationChildStarted = false, Checks = checks };
        using var file = new FileStream(Path.Combine(output, "result.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, Json); file.Flush(true);
        return report.Success ? 0 : 1;
    }
    internal static async Task<int> RunTerminalAsync(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Test output already exists.");
        Directory.CreateDirectory(output);
        var checks = await PatrolControllerSelfTest.RunTerminalFailureAsync().ConfigureAwait(false);
        var report = new { Version = "50.0.0", Success = checks.Length > 0 && checks.All(c => c.Passed),
            Passed = checks.Count(c => c.Passed), Failed = checks.Count(c => !c.Passed),
            Scope = "Targeted terminal failure presentation; actual coordinator with in-memory ports.",
            RealGamePatrolVerified = false, GuiStarted = false, LcAccess = false, NavigationChildStarted = false, Checks = checks };
        using var file = new FileStream(Path.Combine(output, "result.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, Json); file.Flush(true);
        return report.Success ? 0 : 1;
    }

    private static void Need(bool okay, string detail) { if (!okay) throw new InvalidDataException(detail); }

    private sealed record LegacyRoute
    {
        [JsonRequired] public int Schema { get; init; }
        [JsonRequired] public string Revision { get; init; } = "";
        [JsonRequired] public DateTimeOffset SavedAt { get; init; }
        [JsonRequired] public int RetryLimit { get; init; }
        public int ArrivalRadius { get; init; }
        [JsonRequired] public PatrolWaypoint[] Points { get; init; } = [];
    }
}
