using System.Text.Json;
using MemoryHub;
namespace LcPlayerMarket;
internal static class PageWaitPreferencesSelfTest
{
    internal static Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        string root = Path.Combine(Path.GetTempPath(), "market95-ms-prefs-" + Guid.NewGuid().ToString("N"));
        void Check(string name, Action body)
        { try { body(); checks.Add(new(name, true, "Isolated preference files only; no LC/native/input.")); } catch (Exception e) { checks.Add(new(name, false, e.ToString())); } }
        Preferences Load(string label, string json, out string path)
        {
            string dir = Path.Combine(root, label); Directory.CreateDirectory(dir); path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, json); return new LocalStore(dir).LoadPreferences();
        }
        Check("page_interval_legacy_missing_keeps_default_and_other_preferences", () =>
        {
            var value = Load("missing", "{\"showHistory\":false,\"logEnabled\":false}", out var path);
            Need(value.PageOpenIntervalMilliseconds == 3000 && !value.ShowHistory && !value.LogEnabled, "Missing interval changed old options or default.");
            Need(File.ReadAllText(path) == "{\"showHistory\":false,\"logEnabled\":false}", "Reading migrated settings overwrote the original file.");
        });
        foreach (var (seconds, expected) in new[] { (1, 1000), (3, 3000), (5, 5000), (10, 5000), (int.MaxValue, 5000), (0, 3000), (-1, 3000) })
            Check("page_interval_legacy_seconds_migration_" + seconds, () =>
            {
                string json = "{\"pageWaitSeconds\":" + seconds + ",\"showHistory\":false}";
                var value = Load("seconds-" + seconds, json, out var path);
                Need(value.PageOpenIntervalMilliseconds == expected && !value.ShowHistory && File.ReadAllText(path) == json,
                    "Legacy seconds migration was inaccurate, overflowed, or wrote the old file.");
            });
        foreach (int ms in new[] { 100, 150, 3000, 5000 })
            Check("page_interval_ms_roundtrip_" + ms, () =>
            {
                var store = new LocalStore(Path.Combine(root, "valid-" + ms));
                store.SavePreferences(new() { PageOpenIntervalMilliseconds = ms, SynchronizeItemFilters = true });
                var value = store.LoadPreferences();
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(store.Root, "settings.json")));
                Need(value.PageOpenIntervalMilliseconds == ms && value.SynchronizeItemFilters
                    && doc.RootElement.GetProperty("pageOpenIntervalMilliseconds").GetInt32() == ms, "Milliseconds were rounded, not persisted, or lost other options.");
            });
        Check("page_interval_explicit_ms_takes_precedence_over_legacy_seconds", () =>
        {
            var value = Load("precedence", "{\"pageWaitSeconds\":5,\"pageOpenIntervalMilliseconds\":100}", out _);
            Need(value.PageOpenIntervalMilliseconds == 100, "Legacy seconds replaced the exact new interval.");
        });
        foreach (var (raw, expected) in new[] { ("99", 100), ("5001", 5000), ("null", 3000), ("\"bad\"", 3000) })
            Check("page_interval_invalid_settings_normalize_" + raw, () =>
            {
                var value = Load("invalid-" + checks.Count, "{\"pageOpenIntervalMilliseconds\":" + raw + ",\"showSearchOverlay\":false}", out _);
                Need(value.PageOpenIntervalMilliseconds == expected && !value.ShowSearchOverlay, "Malformed interval lost unrelated preferences or escaped range.");
            });
        return Task.FromResult(checks.ToArray());
    }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
