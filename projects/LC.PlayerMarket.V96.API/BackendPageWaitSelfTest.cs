using System.Collections.Concurrent;
using MemoryHub;
using MemoryHub.Reader;

namespace LcPlayerMarket;

internal static class BackendPageWaitSelfTest
{
    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new(name, true, "離線設定橋接通過；沒有遊戲操作。")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        await Check("page_settings_bridge_before_and_after_initialization", async () =>
        {
            using var factoryEntered = new ManualResetEventSlim();
            using var allowFactory = new ManualResetEventSlim();
            var commands = new FakeCommands();
            await using var backend = new StandaloneMarketBackend(NewRoot(), _ => new FakeSource(), (_, _, _) =>
            {
                factoryEntered.Set();
                if (!allowFactory.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("離線 factory 未釋放。");
                return commands;
            });
            try
            {
                if (!await Task.Run(() => factoryEntered.Wait(TimeSpan.FromSeconds(5)))) throw new TimeoutException("未進入離線 factory。");
                backend.ConfigurePageWaitMilliseconds(100);
                backend.ConfigurePageWaitMilliseconds(5000);
                Need(commands.Values.IsEmpty, "後端初始化尚未完成即呼叫了 commands。");
                allowFactory.Set();
                await backend.SendAsync(new() { Command = ShopCommands.GetState }).WaitAsync(TimeSpan.FromSeconds(5));
                Need(commands.Values.SequenceEqual([5000]), "初始化沒有採用最後的設定。");
                backend.ConfigurePageWaitMilliseconds(100);
                Need(commands.Values.SequenceEqual([5000, 100]), "設定變更沒有立即傳至現有協調器。");
            }
            finally { allowFactory.Set(); }
        });
        await Check("page_settings_bridge_invalid_values_keep_current_configuration", async () =>
        {
            var commands = new FakeCommands();
            await using var backend = new StandaloneMarketBackend(NewRoot(), _ => new FakeSource(), (_, _, _) => commands);
            await backend.SendAsync(new() { Command = ShopCommands.GetState }).WaitAsync(TimeSpan.FromSeconds(5));
            Need(commands.Values.SequenceEqual([0]), "預設值不是不額外等待。");
            foreach (int invalid in new[] { -1, 1, 99, 5001, 10000 })
            {
                bool rejected = false;
                try { backend.ConfigurePageWaitMilliseconds(invalid); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Need(rejected, "非法秒數未拒絕。");
            }
            Need(commands.Values.SequenceEqual([0]), "非法值覆蓋了現有設定。");
        });
        await Check("page_settings_bridge_disposed_backend_rejects_changes", async () =>
        {
            var commands = new FakeCommands();
            var backend = new StandaloneMarketBackend(NewRoot(), _ => new FakeSource(), (_, _, _) => commands);
            await backend.SendAsync(new() { Command = ShopCommands.GetState }).WaitAsync(TimeSpan.FromSeconds(5));
            await backend.DisposeAsync();
            bool rejected = false;
            try { backend.ConfigurePageWaitMilliseconds(500); }
            catch (ObjectDisposedException) { rejected = true; }
            Need(rejected && commands.Values.SequenceEqual([0]) && commands.DisposeCount == 1, "已關閉後端仍接受設定或重複釋放。");
        });
        checks.Add(new("minimap_runtime_types_removed", typeof(Program).Assembly.GetTypes().All(type =>
            type.Namespace != "LocalMiniMap" && type.Name is not "MarketMiniMapHost" and not "LocalTerrainProvider"
            and not "MarketMiniMapDeliverySelfTest" and not "MarketMiniMapIntegrationSelfTest"),
            "小地圖渲染、地形供應器、host 與專用測試類型應全部不在發布組件中。"));
        return checks.ToArray();
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "LC-V54-page-settings-" + Guid.NewGuid().ToString("N"));
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class FakeCommands : IStandaloneShopCommands
    {
        internal ConcurrentQueue<int> Values { get; } = new();
        internal int DisposeCount;
        public void ConfigurePageWaitMilliseconds(int milliseconds) => Values.Enqueue(milliseconds);
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new ShopCommandResponse { RequestId = request.RequestId, State = "FakeState" });
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref DisposeCount); return ValueTask.CompletedTask; }
    }

    private sealed class FakeSource : ISlicedMemorySource
    {
        public void Configure(ReadSelection selection) { }
        public ReadResult Read(CancellationToken token) => throw new InvalidOperationException("禁止完整讀取。");
        public ReadResult ReadSlice(ReadSelection fields, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var at = DateTimeOffset.UtcNow;
            return new(new(54, 54, "offline-page-config", "0x100000", 54, 4096, new string('A', 64), "offline"),
                fields.EnabledKeys.ToDictionary(key => key, _ => FieldReading.Observed(new { offline = true }, at)),
                "Reading", "離線", fields.EnabledKeys.Count, 0);
        }
        public void Dispose() { }
    }
}
