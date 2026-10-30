using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal static class MarketLogPathsSelfTest
{
    internal static OfflineCheck[] Run()
    {
        var checks = new List<OfflineCheck>();
        string fixture = Path.Combine(MarketLogPaths.DefaultRoot, "verification-fixtures", "log-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        void Check(string name, Action body)
        {
            try { body(); checks.Add(new("log_paths_" + name, true, "Isolated D-drive files; no LC/native/UI. Fixture=" + fixture)); }
            catch (Exception error) { checks.Add(new("log_paths_" + name, false, error.ToString())); }
        }
        static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        Check("default_store_and_application_use_fixed_root", () =>
        {
            var first = new LocalStore(); var next = new LocalStore();
            Need(first.DiagnosticsRoot == @"D:\lineage\LC_PlayerMarket_LOG" && next.DiagnosticsRoot == first.DiagnosticsRoot,
                "Production diagnostics changed with the product instance/version.");
            Need(first.Root == @"D:\lineage\LC_PlayerMarket_UserData\V38", "LOG change moved product data.");
            using var log = new AppLog();
            Need(log.DirectoryPath == Path.Combine(first.DiagnosticsRoot, "application"), "Default AppLog does not use fixed diagnostics root.");
            Need(!log.Enabled && log.CurrentPath is null, "Inspecting default paths started a log.");
        });
        Check("explicit_fixture_root_stays_isolated", () =>
        {
            string state = Path.Combine(fixture, "isolated-state"); var store = new LocalStore(state);
            using var log = new AppLog(state);
            Need(store.DiagnosticsRoot == Path.Combine(state, "logs")
                && log.DirectoryPath == Path.Combine(state, "logs", "application"), "Explicit fixture leaked to production diagnostics.");
        });
        Check("different_version_data_roots_share_explicit_diagnostics", () =>
        {
            string shared = Path.Combine(fixture, "shared-logs");
            var previous = new LocalStore(Path.Combine(fixture, "V89-data"), diagnosticsRoot: shared);
            var current = new LocalStore(Path.Combine(fixture, "V90-data"), diagnosticsRoot: shared);
            Need(previous.Root != current.Root && previous.DiagnosticsRoot == current.DiagnosticsRoot
                && current.DiagnosticsRoot == shared, "Changing version data root changed shared diagnostic root.");
            Need(MarketLogPaths.Navigation(previous.DiagnosticsRoot) == MarketLogPaths.Navigation(current.DiagnosticsRoot),
                "Navigation diagnostics are version dependent.");
        });
        Check("actual_application_logs_preserve_old_files_and_use_unique_sessions", () =>
        {
            string shared = Path.Combine(fixture, "application-shared"); Directory.CreateDirectory(shared);
            string old = Path.Combine(shared, "old-v89.jsonl"); const string sentinel = "old diagnostic remains byte-for-byte";
            File.WriteAllText(old, sentinel);
            string? firstPath, secondPath;
            using (var first = new AppLog(Path.Combine(fixture, "V89-state"), shared))
            { first.SetEnabled(true); Need(first.Enabled, "First logger failed."); first.Write("FIRST_SENTINEL", "first version"); firstPath = first.CurrentPath; }
            using (var second = new AppLog(Path.Combine(fixture, "V90-state"), shared))
            { second.SetEnabled(true); Need(second.Enabled, "Second logger failed."); second.Write("SECOND_SENTINEL", "second version"); secondPath = second.CurrentPath; }
            Need(firstPath is not null && secondPath is not null && firstPath != secondPath
                && Path.GetDirectoryName(firstPath) == Path.Combine(shared, "application")
                && Path.GetDirectoryName(secondPath) == Path.Combine(shared, "application"), "Log files were reused or escaped shared root.");
            Need(File.ReadAllText(old) == sentinel && File.ReadAllText(firstPath!).Contains("FIRST_SENTINEL")
                && File.ReadAllText(secondPath!).Contains("SECOND_SENTINEL"), "A prior diagnostic was lost/overwritten.");
        });
        Check("standalone_default_uses_fixed_root_and_stable_state_identity", () =>
        {
            string a = MarketLogPaths.BackendDiagnostics(Path.Combine(fixture, "old-state"), true, null)!;
            string b = MarketLogPaths.BackendDiagnostics(Path.Combine(fixture, "new-state"), true, null)!;
            string prefix = Path.Combine(MarketLogPaths.DefaultRoot, "market-runtime") + Path.DirectorySeparatorChar;
            Need(a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && b.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && a != b && a == MarketLogPaths.BackendDiagnostics(Path.Combine(fixture, "old-state"), true, null), "Standalone diagnostics must reuse the same state and isolate different states.");
            Need(MarketLogPaths.BackendDiagnostics(Path.Combine(fixture, "fake-state"), false, null) is null,
                "Fake ports acquired default production diagnostics.");
            string explicitRoot = Path.Combine(fixture, "explicit-backend");
            Need(MarketLogPaths.BackendDiagnostics("unused", true, explicitRoot) == explicitRoot
                && MarketLogPaths.BackendDiagnostics("unused", false, explicitRoot) == explicitRoot, "Explicit backend isolation ignored.");
        });
        Check("legacy_user_data_logs_are_not_moved_or_deleted", () =>
        {
            string state = Path.Combine(fixture, "legacy-state"), old = Path.Combine(state, "logs", "old.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(old)!); File.WriteAllText(old, "preserve-old-log");
            string shared = Path.Combine(fixture, "new-diagnostics");
            var store = new LocalStore(state, diagnosticsRoot: shared);
            using (var log = new AppLog(store.Root, store.DiagnosticsRoot))
            { log.SetEnabled(true); Need(log.Enabled, "New logger failed."); log.Write("NEW_ROOT", "new log only"); }
            Need(File.ReadAllText(old) == "preserve-old-log" && Directory.GetFiles(Path.Combine(state, "logs")).Length == 1,
                "Opening new logging modified/moved old history.");
        });
        return checks.ToArray();
    }

    internal static int RunReport(string output)
    {
        var checks = Run();
        var report = new OfflineCheckReport("90.0.0", checks.All(value => value.Passed), checks.Count(value => value.Passed),
            checks.Count(value => !value.Passed), false, false, false, checks);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { JsonSerializer.Serialize(file, report, HubProtocol.Json); file.Flush(true); }
        return report.Success ? 0 : 1;
    }
}