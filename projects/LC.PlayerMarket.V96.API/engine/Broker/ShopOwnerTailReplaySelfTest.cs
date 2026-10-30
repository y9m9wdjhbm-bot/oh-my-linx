using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static class ShopOwnerTailReplaySelfTest
{
    internal static OfflineCheck[] Run()
    {
        var checks = new List<OfflineCheck>();
        var source = new SourceIdentity(123, 456, "offline.exe", "0x10000", 1, 65536, new string('A', 64), "offline-source");
        foreach (string scenario in new[] { "plain", "archive", "gap", "missing_tail", "corrupt_json", "oversize", "wrong_head" })
        {
            string fixture = Path.Combine(Path.GetTempPath(), "LC-OwnerTail-" + Guid.NewGuid().ToString("N"));
            string root = Path.Combine(fixture, "ledger");
            Directory.CreateDirectory(root);
            try
            {
                var at = DateTimeOffset.UtcNow.AddDays(-1);
                string oldEpoch = Guid.NewGuid().ToString("N"), currentEpoch = Guid.NewGuid().ToString("N");
                var items = new List<SearchedOwnerEvent>();
                void Add(SearchedOwnerEvent value) => items.Add(value with { Sequence = items.Count + 1, At = at.AddTicks(items.Count + 1) });
                MarketOwner Owner(ulong id) => new() { Id = id, LocalIdentity = "owner-" + id, Name = "offline", Position = new(100, 100), ObservedAt = at };
                SearchedOwnerEvent Intent(string request, string epoch, ulong owner) => new()
                {
                    Kind = "Intent", Epoch = epoch, Key = Hash(Encoding.UTF8.GetBytes("request:" + request)), RequestId = request,
                    Source = source, SourceSessionKey = source.SessionKey, Owner = Owner(owner), Direction = ShopPageModes.ViewerBuy,
                    State = "Uncertain", SearchedAt = at
                };
                Add(new() { Kind = "Initialize", Epoch = oldEpoch });
                Add(new() { Kind = "ImportComplete", Epoch = oldEpoch });
                Add(Intent("old-uncertain", oldEpoch, 7));
                Add(new() { Kind = "Clear", Epoch = currentEpoch });
                // 144 events cross the 128-file batch boundary; the archive
                // case also validates the complete prefix before its tail.
                for (int index = 0; index < 70; index++)
                {
                    var intent = Intent("sent-" + index, currentEpoch, (ulong)(100 + index));
                    Add(intent); Add(intent with { Kind = "Settled", State = "Sent" });
                }
                File.WriteAllBytes(Path.Combine(root, "writer.lock"), []);
                void Write(SearchedOwnerEvent item) => File.WriteAllBytes(Path.Combine(root, Name(item.Sequence)), JsonSerializer.SerializeToUtf8Bytes(item, HubProtocol.Json));
                if (scenario == "archive")
                {
                    string prefix = Path.Combine(fixture, "prefix"); Directory.CreateDirectory(prefix);
                    File.WriteAllBytes(Path.Combine(prefix, "writer.lock"), []);
                    foreach (var item in items.Take(4)) File.WriteAllBytes(Path.Combine(prefix, Name(item.Sequence)), JsonSerializer.SerializeToUtf8Bytes(item, HubProtocol.Json));
                    WriteHead(prefix, 4); File.Delete(Path.Combine(root, "writer.lock"));
                    SearchedOwnerArchive.CreateFromLegacy(prefix, root);
                    foreach (var item in items.Skip(4)) Write(item);
                }
                else foreach (var item in items) Write(item);
                WriteHead(root, items.Count);
                if (scenario == "gap") File.Delete(Path.Combine(root, Name(130)));
                if (scenario == "missing_tail") File.Delete(Path.Combine(root, Name(items.Count)));
                if (scenario == "corrupt_json") File.WriteAllText(Path.Combine(root, Name(130)), "{invalid");
                if (scenario == "oversize") File.WriteAllBytes(Path.Combine(root, Name(130)), new byte[128 * 1024 + 1]);
                if (scenario == "wrong_head") File.WriteAllBytes(Path.Combine(root, "head.checkpoint"),
                    JsonSerializer.SerializeToUtf8Bytes(new SearchedOwnerHead(items.Count, new string('0', 64)), HubProtocol.Json));
                var original = Directory.GetFiles(root).ToDictionary(Path.GetFileName, path => Hash(File.ReadAllBytes(path)));
                var trace = new List<ShopStartupStageEvent>();
                var store = new ShopSearchedOwnerStore(root, Path.Combine(fixture, "progress"), startupStage: trace.Add);
                bool healthy = scenario is "plain" or "archive";
                Need((store.Error is null) == healthy, "Tail integrity was relaxed: " + store.Error);
                Need(original.All(pair => Hash(File.ReadAllBytes(Path.Combine(root, pair.Key!))) == pair.Value), "Replay changed ledger bytes.");
                if (healthy)
                {
                    Need(store.Summary(true, "").SearchedPages == 70 && store.ShouldSkip(169, ShopPageModes.ViewerBuy), "Sent-page dedup lost across a batch.");
                    Need(store.BlocksSource(source) && !store.ShouldSkip(7, ShopPageModes.ViewerBuy, true), "Cross-epoch uncertainty disappeared.");
                    bool rejected = false;
                    try { store.Begin(source, Owner(7), ShopPageModes.ViewerBuy, "old-uncertain", forceDispatch: true); }
                    catch (ShopRequestFailure) { rejected = true; }
                    Need(rejected, "A durable request ID was replayed.");
                    Need(trace.Any(value => value.Stage == "owner.json-tail-replay" && value.State == "Completed"
                        && value.Items == (scenario == "archive" ? 140 : 144)), "Tail metric missing.");
                }
                else Need(trace.Any(value => value.State == "Failed"), "Corrupt replay was logged as successful.");
                checks.Add(new("owner_tail_read8_apply_ordered_" + scenario, true, "通過；保留檔案與去重／未確認來源證據"));
            }
            catch (Exception failure) { checks.Add(new("owner_tail_read8_apply_ordered_" + scenario, false, failure.ToString())); }
            finally
            {
                string resolved = Path.GetFullPath(fixture);
                if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(resolved).StartsWith("LC-OwnerTail-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup path");
                Directory.Delete(resolved, true);
            }
        }
        return checks.ToArray();
    }

    private static string Name(int sequence) => sequence.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + ".json";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void WriteHead(string root, int sequence) => File.WriteAllBytes(Path.Combine(root, "head.checkpoint"),
        JsonSerializer.SerializeToUtf8Bytes(new SearchedOwnerHead(sequence, Hash(File.ReadAllBytes(Path.Combine(root, Name(sequence))))), HubProtocol.Json));
    private static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
}
