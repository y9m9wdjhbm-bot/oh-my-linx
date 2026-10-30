using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LcPlayerMarket;

/// <summary>Isolated file fixtures only; no MainForm, backend, LC, or real user state.</summary>
internal static class PatrolRouteStoreSelfTest
{
    internal static IReadOnlyList<OfflineCheck> Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "LC-Patrol-Route-Carry-" + Guid.NewGuid().ToString("N"));
        var checks = new List<OfflineCheck>();
        void Check(string name, Action<Fixture> test)
        {
            try
            {
                test(new Fixture(Path.Combine(root, name)));
                checks.Add(new("patrol_route_" + name, true, "隔離檔案案例通過；未開啟 UI、導航或 LC。"));
            }
            catch (Exception error) { checks.Add(new("patrol_route_" + name, false, error.ToString())); }
        }

        Check("carry_exact_latest_bytes_and_all_settings", f =>
        {
            f.Write(27, [f.Point("較舊點", 1, 2)], 3, 2, -10);
            var points = new[] { f.Point("古魯丁村一", 32610, 32778), f.Point("古魯丁村二", 32605, 32759),
                f.Point("古魯丁村三", 32609, 32730), f.Point("古魯丁村四", 32618, 32732) };
            string newest = f.Write(27, points, 10, 1);
            var before = Snapshot(f.Version(27)); var store = f.Store(29);
            var loaded = Load(store);
            Need(loaded.Points.SequenceEqual(points) && loaded.RetryLimit == 10 && loaded.ArrivalRadius == 1,
                "Coordinates, map identity, order, retry setting or radius changed.");
            var files = Files(store);
            Need(files.Length == 1 && Path.GetFileName(files[0]) == Path.GetFileName(newest)
                && File.ReadAllBytes(files[0]).SequenceEqual(File.ReadAllBytes(newest)), "Import rewrote or copied the wrong revision.");
            Need(Directory.GetFiles(store.DirectoryPath, "*.importing-*").Length == 0, "Completed import left an unpublished temporary file.");
            Unchanged(before);
        });
        Check("reopen_does_not_import_again", f =>
        {
            f.Write(28, [f.Point("原來點", 10, 20)]);
            var first = Load(f.Store(29)); var currentBefore = Snapshot(f.Version(29));
            f.Write(28, [f.Point("舊版之後新增", 30, 40)], atOffset: 20);
            var oldBefore = Snapshot(f.Version(28));
            var reopened = Load(f.Store(29));
            Need(reopened.Revision == first.Revision && Files(f.Store(29)).Length == 1, "Reopen replaced an existing current revision.");
            Unchanged(currentBefore); Unchanged(oldBefore);
        });
        Check("explicit_current_empty_route_stays_empty", f =>
        {
            f.Write(28, [f.Point("不要復活", 10, 20)]); string empty = f.Write(29, [], 0, 7);
            var before = Snapshot(f.Root); var value = Load(f.Store(29));
            Need(value.Points.Length == 0 && value.RetryLimit == 0 && value.ArrivalRadius == 7
                && Path.GetFileName(Files(f.Store(29)).Single()) == Path.GetFileName(empty), "Explicit empty route revived prior points.");
            Unchanged(before); Need(Snapshot(f.Root).Count == before.Count, "Reading a current empty route created a file.");
        });
        Check("nearest_prior_empty_route_is_authoritative", f =>
        {
            f.Write(27, [f.Point("更舊點", 10, 20)]); string empty = f.Write(28, [], 5, 3);
            var before = Snapshot(f.Root); var value = Load(f.Store(29));
            Need(value.Points.Length == 0 && value.RetryLimit == 5 && value.ArrivalRadius == 3
                && File.ReadAllBytes(Files(f.Store(29)).Single()).SequenceEqual(File.ReadAllBytes(empty)), "Empty nearest prior was skipped.");
            Unchanged(before);
        });
        Check("corrupt_current_cannot_be_replaced", f =>
        {
            f.Write(28, [f.Point("完整舊點", 10, 20)]); string broken = f.Write(29, [f.Point("目前點", 30, 40)]);
            File.WriteAllText(broken, "{broken", new UTF8Encoding(false)); var before = Snapshot(f.Root);
            var store = f.Store(29);
            Need(!store.Load().Success && !store.TrySave([], 3, 2, out _, out _, expectedRevision: ""), "Broken current route was silently replaced.");
            Unchanged(before); Need(Files(store).Length == 1, "A replacement revision hid the damaged file.");
        });
        Check("corrupt_latest_prior_does_not_fall_back", f =>
        {
            f.Write(27, [f.Point("更舊有效點", 10, 20)]);
            f.Write(28, [f.Point("同版有效舊點", 20, 30)], atOffset: -10);
            string broken = f.Write(28, [f.Point("最新損壞點", 30, 40)]); File.WriteAllText(broken, "{bad");
            var before = Snapshot(f.Root); var store = f.Store(29);
            Need(!store.Load().Success && Files(store).Length == 0, "Broken latest prior was skipped or imported.");
            Unchanged(before);
        });
        Check("prior_filename_identity_is_strict", f =>
        {
            string original = f.Write(28, [f.Point("名稱核對", 10, 20)]);
            string wrong = Path.Combine(Path.GetDirectoryName(original)!, "9999999999999999999-wrong.route.json");
            File.Copy(original, wrong); var before = Snapshot(f.Root);
            Need(!f.Store(29).Load().Success && Files(f.Store(29)).Length == 0, "Mismatched prior filename was accepted."); Unchanged(before);
        });
        Check("versions_are_numeric_and_only_earlier", f =>
        {
            f.Write(9, [f.Point("九", 9, 9)], atOffset: 50);
            var selected = f.Point("十", 10, 10); f.Write(10, [selected]);
            f.Write(12, [f.Point("未來版", 12, 12)], atOffset: 100);
            Directory.CreateDirectory(f.Version(8)); var before = Snapshot(f.Root);
            var loaded = Load(f.Store(11));
            Need(loaded.Points.Single() == selected, "Lexical version order, timestamp order or future version was used."); Unchanged(before);
        });
        Check("future_only_does_not_import_or_create_state", f =>
        {
            f.Write(30, [f.Point("未來版", 10, 20)]); var before = Snapshot(f.Root);
            var loaded = Load(f.Store(29));
            Need(loaded.Revision == "" && loaded.Points.Length == 0 && !Directory.Exists(f.Version(29)), "Future route was imported or empty loading wrote state.");
            Unchanged(before);
        });
        Check("nonstandard_roots_do_not_guess_siblings", f =>
        {
            f.Write(28, [f.Point("真實形狀fixture", 10, 20)]);
            string otherParent = Path.Combine(f.Root, "OtherApplication");
            f.WriteAt(Path.Combine(otherParent, "V28"), [f.Point("其他程式", 30, 40)], 3, 2);
            foreach (string current in new[] { Path.Combine(otherParent, "V39"), Path.Combine(f.Parent, "V029"), Path.Combine(f.Parent, "custom") })
            {
                var loaded = Load(new PatrolRouteStore(current));
                Need(loaded.Revision == "" && loaded.Points.Length == 0 && !Directory.Exists(current), "An arbitrary fixture root guessed an old version.");
            }
        });
        Check("legacy_radius_default_preserves_original_bytes", f =>
        {
            string path = f.Write(24, [f.Point("舊格式", 10, 20)], 7, 9);
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); json.Remove("arrivalRadius");
            File.WriteAllText(path, json.ToJsonString(), new UTF8Encoding(false)); var before = Snapshot(f.Root);
            var loaded = Load(f.Store(29));
            Need(loaded.ArrivalRadius == 2 && loaded.RetryLimit == 7
                && File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(Files(f.Store(29)).Single())), "Legacy default rewrote source bytes or changed settings."); Unchanged(before);
        });
        Check("import_obeys_writer_lock_and_remains_atomic", f =>
        {
            f.Write(28, [f.Point("鎖定測試", 10, 20)]); var before = Snapshot(f.Version(28));
            var store = f.Store(29); Directory.CreateDirectory(store.DirectoryPath);
            using (var held = new FileStream(Path.Combine(store.DirectoryPath, "writer.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                Need(!store.Load().Success && Files(store).Length == 0, "Import bypassed the current writer lock.");
            Need(Load(store).Points.Length == 1 && Files(store).Length == 1, "Import did not recover after the writer released the lock."); Unchanged(before);
        });
        Check("stale_empty_editor_cannot_replace_external_points", f =>
        {
            var store = new PatrolRouteStore(Path.Combine(f.Root, "custom")); var stale = Load(store);
            var points = new[] { f.Point("一", 1, 2), f.Point("二", 3, 4), f.Point("三", 5, 6), f.Point("四", 7, 8) };
            Need(store.TrySave(points, 10, 1, out var external, out string error), error);
            var before = Snapshot(f.Root);
            Need(!store.TrySave(stale.Points, 3, 2, out var saved, out error, expectedRevision: stale.Revision)
                && saved is null && error.Length > 0, "An empty stale snapshot replaced externally saved points.");
            Need(Load(store).Revision == external!.Revision && Files(store).Length == 1, "Rejected save created a revision."); Unchanged(before);
        });
        Check("stale_loaded_revision_cannot_overwrite_newer_settings", f =>
        {
            var store = new PatrolRouteStore(Path.Combine(f.Root, "custom")); var point = f.Point("既有點", 10, 20);
            Need(store.TrySave([point], 3, 2, out var first, out string error), error);
            Need(store.TrySave([point with { X = 99 }], 8, 1, out var newer, out error, expectedRevision: first!.Revision), error);
            var before = Snapshot(f.Root);
            Need(!store.TrySave([], 0, 10, out _, out _, expectedRevision: first.Revision), "Stale nonempty editor overwrote newer settings.");
            Need(Load(store).Revision == newer!.Revision && Files(store).Length == 2, "Conflict changed the latest route."); Unchanged(before);
        });
        Check("matching_revision_saves_new_file_and_preserves_old", f =>
        {
            var store = new PatrolRouteStore(Path.Combine(f.Root, "custom")); var point = f.Point("可修改點", 10, 20);
            Need(store.TrySave([point], 3, 2, out var first, out string error, expectedRevision: ""), error);
            var before = Snapshot(f.Root);
            Need(store.TrySave([point with { Y = 44 }], 6, 4, out var next, out error, expectedRevision: first!.Revision), error);
            Need(next!.Revision != first.Revision && next.Points[0].Y == 44 && next.RetryLimit == 6 && next.ArrivalRadius == 4
                && Files(store).Length == 2, "Matching revision did not save the requested definition."); Unchanged(before);
        });
        Check("save_imports_before_empty_revision_fence", f =>
        {
            string old = f.Write(28, [f.Point("需先帶回", 10, 20)], 10, 1); var before = Snapshot(f.Root); var store = f.Store(29);
            Need(!store.TrySave([], 3, 2, out _, out _, expectedRevision: ""), "Empty first save bypassed the prior route import.");
            var loaded = Load(store);
            Need(loaded.Points.Length == 1 && loaded.RetryLimit == 10 && Files(store).Length == 1
                && File.ReadAllBytes(Files(store).Single()).SequenceEqual(File.ReadAllBytes(old)), "First-save conflict lost the imported definition."); Unchanged(before);
        });
        Check("no_saved_prior_creates_no_route_on_load", f =>
        {
            Directory.CreateDirectory(Path.Combine(f.Version(28), "patrol-routes"));
            var loaded = Load(f.Store(29));
            Need(loaded.Revision == "" && loaded.Points.Length == 0 && loaded.RetryLimit == 3 && loaded.ArrivalRadius == 2
                && !Directory.Exists(f.Version(29)), "No prior route should return an unsaved default without writing.");
        });
        return checks;
    }

    private static PatrolRouteSnapshot Load(PatrolRouteStore store)
    { var loaded = store.Load(); Need(loaded.Success, loaded.Error ?? "Missing snapshot."); return loaded.Snapshot!; }
    private static string[] Files(PatrolRouteStore store) => Directory.Exists(store.DirectoryPath)
        ? Directory.GetFiles(store.DirectoryPath, "*.route.json") : [];
    private static void Need(bool condition, string detail) { if (!condition) throw new InvalidDataException(detail); }
    private static Dictionary<string, string> Snapshot(string directory) => !Directory.Exists(directory) ? []
        : Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => path,
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase);
    private static void Unchanged(Dictionary<string, string> before)
    {
        foreach (var entry in before)
            Need(File.Exists(entry.Key) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry.Key))) == entry.Value,
                "An existing fixture input changed: " + entry.Key);
    }

    private sealed class Fixture(string root)
    {
        internal string Root => root;
        internal string Parent => Path.Combine(root, "LC.PlayerMarket");
        internal string Version(int number) => Path.Combine(Parent, "V" + number.ToString(CultureInfo.InvariantCulture));
        internal PatrolRouteStore Store(int number) => new(Version(number));
        internal PatrolWaypoint Point(string name, int x, int y) => new(Guid.NewGuid().ToString("N"), name, "4", "古魯丁村", x, y);
        internal string Write(int version, PatrolWaypoint[] points, int retries = 3, int radius = 2, int atOffset = 0)
            => WriteAt(Version(version), points, retries, radius, atOffset);
        internal string WriteAt(string state, PatrolWaypoint[] points, int retries, int radius, int atOffset = 0)
        {
            var route = new PatrolRouteSnapshot { Revision = Guid.NewGuid().ToString("N"), SavedAt = DateTimeOffset.UtcNow.AddSeconds(atOffset),
                RetryLimit = retries, ArrivalRadius = radius, Points = points };
            PatrolRouteRules.Validate(route);
            string directory = Path.Combine(state, "patrol-routes"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, route.SavedAt.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" + route.Revision + ".route.json");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, route, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }); stream.Flush(true);
            return path;
        }
    }
}
