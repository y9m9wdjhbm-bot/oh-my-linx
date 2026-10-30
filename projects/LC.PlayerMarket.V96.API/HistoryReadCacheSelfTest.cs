using System.Diagnostics;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal static class HistoryReadCacheSelfTest
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "LC-HistoryCache-SelfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DateTimeOffset old = DateTimeOffset.Parse("2026-09-29T01:02:03+08:00"), clear = old.AddHours(1), resume = clear.AddMinutes(1);
            var source = Listing("old-a", old); Write(root, "a.json", source); Write(root, "b.json", Listing("old-b", old));
            var first = Load(root); Check(first.FileCount == 2 && first.ParsedFileCount == 2 && first.ReusedFileCount == 0, "first parses all");
            var second = Load(root); Check(second.ParsedFileCount == 0 && second.ReusedFileCount == 2, "second source JSON parse zero");
            Check(JsonSerializer.Serialize(source, HubProtocol.Json) == JsonSerializer.Serialize(second.Listings.Single(v => v.Revision == "old-a"), HubProtocol.Json), "binary preserves every field");
            // A caller adopts cached results only through its current persisted visibility watermark.
            DateTimeOffset? after = null;
            bool Visible(MarketListing v) => after is { } water && v.CapturedAt > water && v.RequestedAt is { } requested && requested > water;
            Check(Load(root).Listings.Where(Visible).Count() == 0, "clear then reopen excludes cached old history");
            after = resume;
            Write(root, "c.json", Listing("new-c", resume.AddMinutes(1))); Write(root, "d.json", Listing("new-d", resume.AddMinutes(2)));
            var added = Load(root); Check(added.ParsedFileCount == 2 && added.ReusedFileCount == 2 && added.Listings.Where(Visible).Count() == 2, "resume plus two new parses exactly two");
            after = null; Check(Load(root).Listings.Where(Visible).Count() == 0, "second clear excludes warm cache");
            after = resume;
            Write(root, "e.json", Listing("late-capture-old-request", resume.AddMinutes(3)) with { RequestedAt = old });
            Check(Load(root).Listings.Where(Visible).Count() == 2, "captured time alone never crosses clear watermark");
            Write(root, "a.json", Listing("a-modified", old.AddMinutes(3)));
            var modified = Load(root); Check(modified.ParsedFileCount == 1 && modified.Listings.All(v => v.Revision != "old-a"), "modified replaces old cached value");
            File.Delete(Path.Combine(root, "b.json")); File.Move(Path.Combine(root, "c.json"), Path.Combine(root, "renamed.json"));
            var renamed = Load(root); Check(renamed.FileCount == 4 && renamed.ParsedFileCount == 1 && renamed.Listings.All(v => v.Revision != "old-b"), "delete and rename are reconciled");
            File.WriteAllText(Path.Combine(root, "d.json"), "{bad JSON");
            var errors = new List<string>(); var corrupt = HistoryReadCache.Load(root, error: errors.Add);
            Check(corrupt.Listings.All(v => v.Revision != "new-d") && errors.Any(v => v.Contains("d.json")), "corrupt source cannot revive cached value");
            byte[] cache = File.ReadAllBytes(HistoryReadCache.CachePath(root)); cache[cache.Length / 2] ^= 0x40;
            File.WriteAllBytes(HistoryReadCache.CachePath(root), cache); errors.Clear();
            var fallback = HistoryReadCache.Load(root, error: errors.Add);
            Check(fallback.ParsedFileCount == 4 && fallback.ReusedFileCount == 0 && fallback.Listings.Length == 3 && errors.Any(v => v.Contains("checksum")), "cache corruption falls back to complete source reads");
            Check(Load(root).ReusedFileCount == 3, "fallback repairs snapshot");
            Write(root, "oversized.json", Listing("oversized", old) with { Detail = new string('x', 1024 * 1024) });
            Write(root, "too-many-rows.json", Listing("bad-rows", old) with { Rows = Enumerable.Repeat(source.Rows[0], 257).ToArray() });
            Write(root, "invalid.json", Listing("bad-owner", old) with { Owner = source.Owner with { Id = 0 } });
            errors.Clear(); var limited = HistoryReadCache.Load(root, error: errors.Add);
            Check(limited.Listings.Length == 3 && errors.Count == 4, "source 1MiB/256rows/Valid checks preserved");
            var beforeCancel = File.ReadAllBytes(HistoryReadCache.CachePath(root));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            CheckCanceled(() => HistoryReadCache.Load(root, canceled.Token), "pre-canceled load");
            using var mid = new CancellationTokenSource();
            CheckCanceled(() => HistoryReadCache.Load(root, mid.Token, _ => mid.Cancel()), "cancel during source scan");
            Check(beforeCancel.SequenceEqual(File.ReadAllBytes(HistoryReadCache.CachePath(root))) && Directory.GetFiles(root, "*.tmp").Length == 0, "cancellation preserves snapshot without temp debris");
            Console.WriteLine("HistoryReadCacheSelfTest: PASS (13 scenario assertions plus 2 cancellation assertions)");
        }
        finally
        {
            // Only this freshly-created isolated test directory is removed.
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("LC-HistoryCache-SelfTest-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture cleanup path");
            Directory.Delete(resolved, true);
        }
    }

    internal static void Benchmark(int count = 7022)
    {
        string root = Path.Combine(Path.GetTempPath(), "LC-HistoryCache-Benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var value = Listing("sample", DateTimeOffset.UtcNow);
            for (int i = 0; i < count; i++) Write(root, i.ToString("D5") + ".json", value with { Revision = i.ToString() });
            var watch = Stopwatch.StartNew(); var cold = Load(root); long coldMs = watch.ElapsedMilliseconds;
            watch.Restart(); var warm = Load(root); long warmMs = watch.ElapsedMilliseconds;
            Write(root, "new-a.json", value with { Revision = "new-a" }); Write(root, "new-b.json", value with { Revision = "new-b" });
            watch.Restart(); var incremental = Load(root); long addMs = watch.ElapsedMilliseconds;
            Check(cold.Listings.Length == count && warm.ParsedFileCount == 0 && warm.ReusedFileCount == count
                && incremental.ParsedFileCount == 2 && incremental.ReusedFileCount == count, "7022 synthetic persistence counts");
            Console.WriteLine($"SYNTHETIC {count} files: cold={coldMs}ms warm={warmMs}ms add2={addMs}ms; warm parsed=0/reused={warm.ReusedFileCount}; add parsed=2/reused={incremental.ReusedFileCount}; cacheBytes={new FileInfo(HistoryReadCache.CachePath(root)).Length}");
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("LC-HistoryCache-Benchmark-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected benchmark cleanup path");
            Directory.Delete(resolved, true);
        }
    }
    private static HistoryCacheReadResult Load(string root) => HistoryReadCache.Load(root);
    private static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    private static void CheckCanceled(Action action, string name)
    { try { action(); } catch (OperationCanceledException) { Console.WriteLine("PASS: " + name); return; } throw new InvalidOperationException("FAIL: " + name); }
    private static void Write(string root, string name, MarketListing value)
    {
        string path = Path.Combine(root, name); File.WriteAllText(path, JsonSerializer.Serialize(value, HubProtocol.Json));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 10000)));
    }
    private static MarketListing Listing(string revision, DateTimeOffset at) => new()
    {
        Revision = revision, SourceSessionKey = "fixture-session", RequestId = "request-" + revision,
        Owner = new() { Id = 42, LocalIdentity = "local-42", Name = "隔離攤位", Position = new(120, 130), MapName = "測試地圖",
            ObservedAt = at.AddSeconds(-1), InViewport = true, VisibilityEvidence = "fixture", ActionType = "Shop", ShopState = "Open",
            ShopStateSampledAt = at.AddSeconds(-2), ShopStateEvidence = "fixture-evidence", ShopStateDetail = "fixture-detail" },
        ViewerDirection = ShopPageModes.ViewerBuy, CapturedAt = at, RequestedAt = at.AddSeconds(-1), PageDurationMs = 123.45,
        SourceReadDurationMs = 12.3, NativeDurationMs = 4.5, ResponseReadDurationMs = 6.7, ResponseReadAttempts = 3,
        SelfPositionAtRequest = new(117, 128), DistanceAtRequest = 3, State = "Observed", Detail = "display-only",
        OwnerAndResponseMatched = true, PageEmpty = false, Complete = true,
        Rows = [new(1, "Observed", true, "測試商品", "12,345", 12345, "個", 9, "Verified", "detail")
            { ItemKindRaw = 5, ItemKindDetailFlag = 1, ItemKind = "Weapon", ItemKindState = "Observed", ItemIdentificationState = "Unidentified", ItemIdentificationEvidence = "Verified" },
            new(2, "Unknown", false, null, null, null, null, null, "Unknown", "nullable-fields")]
    };
}
