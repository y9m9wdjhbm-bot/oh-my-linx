using System.Security.Cryptography;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static class BrokerInitializationFailureSelfTest
{
    internal static async Task<OfflineCheck[]> RunAsync()
    {
        const string name = "broker_corrupt_reset_startup_releases_maintenance_lease";
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "LC-V54-init-failure-" + Guid.NewGuid().ToString("N"));
            string shops = Path.Combine(root, "shops"), evidence = Path.Combine(shops, "search-progress-resets");
            Directory.CreateDirectory(evidence);
            string prepared = Path.Combine(evidence, "broken.prepared.json");
            File.WriteAllText(prepared, "{ broken reset evidence preserved }");
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(prepared)));
            var fake = new NoReadSource();
            await using var runtime = new HubRuntime(fake, Path.Combine(root, "reader"), enableTransport: false);
            using var nativeSource = new CurrentLcSource(profileDirectory: Path.Combine(root, "profiles"));
            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool rejected = false;
                try { await using var broker = new ShopBroker(runtime, nativeSource, shops); }
                catch (JsonException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("損壞重置證據未拒絕初始化。");
                using var lease = new FileStream(Path.Combine(shops, "lock-tool-operation.lock"), FileMode.Open,
                    FileAccess.ReadWrite, FileShare.None);
            }
            if (fake.Reads != 0 || runtime.IsRunning || hash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(prepared))))
                throw new InvalidOperationException("初始化失敗時啟動讀取或改動原重置證據。");
            return [new(name, true, "損壞的本機重置證據保留；連續兩次初始化失敗均釋放工作鎖，未啟動LC讀取。")];
        }
        catch (Exception error) { return [new(name, false, error.ToString())]; }
    }
    private sealed class NoReadSource : IMemorySource
    {
        internal int Reads;
        public ReadResult Read(CancellationToken token) { Reads++; throw new InvalidOperationException("禁止LC讀取。"); }
        public void Dispose() { }
    }
}
