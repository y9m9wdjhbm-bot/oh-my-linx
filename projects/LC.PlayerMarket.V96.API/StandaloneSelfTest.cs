using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Real local-runtime lifecycle tests with fake sources; never opens LC or a service client.</summary>
internal static class StandaloneSelfTest
{
    internal static async Task<IReadOnlyList<OfflineCheck>> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        string root = Path.Combine(Path.GetTempPath(), "LC-PlayerMarket-V33-Standalone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await Check("standalone_runtime_does_not_bind_occupied_endpoints", async () =>
        {
            string pipe = "LC.PlayerMarket.Offline." + Guid.NewGuid().ToString("N");
            string compatibilityPipe = pipe + ".compatibility";
            using var occupied = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var occupiedCompatibility = new NamedPipeServerStream(compatibilityPipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var source = new FakeSource();
            await using var runtime = new HubRuntime(source, Path.Combine(root, "occupied-pipes"), pipe, [compatibilityPipe], enableTransport: false);
            runtime.SetShopSearchOnly(true);
            runtime.SetLogging(false);
            runtime.IntervalMilliseconds = 100;
            runtime.PrepareTransport();
            runtime.Start();
            await Until(() => runtime.Snapshot.Fields.GetValueOrDefault("personalShopListing")?.State == "Observed");
            Require(!runtime.TransportEnabled && runtime.IsRunning, "停用 IPC 後未持續執行本機採樣。");
            Require(runtime.Snapshot.Source == FakeSource.Identity && runtime.Snapshot.ConnectedClients == 0, "未取得 fake 來源或意外連入用戶端。");
            Require(!occupied.IsConnected && !occupiedCompatibility.IsConnected, "本機 runtime 意外連接既有 pipe。");
            await runtime.StopAsync();
        });
        await Check("standalone_shop_selection_and_manual_refresh_boundary", async () =>
        {
            var source = new FakeSource();
            await using var runtime = new HubRuntime(source, Path.Combine(root, "selection"), enableTransport: false);
            runtime.SetLogging(false);
            runtime.SetShopSearchOnly(true);
            runtime.Start();
            await Until(() => runtime.Snapshot.Fields.GetValueOrDefault("personalShopListing")?.State == "Observed"
                && runtime.Snapshot.Fields.GetValueOrDefault("mapName")?.State == "Observed");
            await runtime.StopAsync();
            Require(runtime.ReadSelection.EnabledKeys.SequenceEqual(new ReadSelection(ShopSearchReadMode.RequiredFields).EnabledKeys), "商店模式包含額外欄位或漏掉依賴。");
            string[] sampled = source.Slices.SelectMany(x => x).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            string[] expected = ShopSearchReadMode.RequiredFields.Except(ShopSearchReadMode.ManualFields).Order(StringComparer.Ordinal).ToArray();
            Require(sampled.SequenceEqual(expected), "背景採樣超出自身、座標、地圖及目前商店頁面，或漏採必要欄位：" + string.Join(",", sampled));
            Require(source.FullReads == 0, "商店模式退回完整來源讀取。");
            Require(source.Configurations.Last().SequenceEqual(runtime.ReadSelection.EnabledKeys), "來源未收到與 runtime 相同的欄位選擇。");
        });
        await Check("standalone_runtime_stop_restart_and_dispose", async () =>
        {
            var source = new FakeSource();
            var runtime = new HubRuntime(source, Path.Combine(root, "lifecycle"), enableTransport: false);
            try
            {
                runtime.SetLogging(false);
                runtime.SetShopSearchOnly(true);
                runtime.IntervalMilliseconds = 100;
                runtime.Start();
                await Until(() => source.Slices.Count >= 3);
                await runtime.StopAsync();
                await runtime.StopAsync();
                int stoppedReads = source.Slices.Count;
                await Task.Delay(250);
                Require(!runtime.IsRunning && runtime.Snapshot.State == "Stopped" && source.Slices.Count == stoppedReads, "StopAsync 完成後仍在讀取或未發布停止狀態。");
                Require(runtime.Snapshot.Source is null && runtime.Snapshot.Fields.Values.All(x => x.Value is null), "停止後仍保留可使用的目前來源/讀值。");
                runtime.Start();
                await Until(() => source.Slices.Count > stoppedReads);
                await runtime.DisposeAsync();
                await runtime.DisposeAsync();
                Require(source.DisposeCount == 1 && !runtime.IsRunning, "來源未恰好釋放一次。");
                bool rejected = false;
                try { runtime.Start(); }
                catch (ObjectDisposedException) { rejected = true; }
                Require(rejected, "已釋放的 runtime 仍可重新開始。");
            }
            finally { await runtime.DisposeAsync(); }
        });
        await Check("standalone_backend_watch_never_dispatches_automatically", async () =>
        {
            var source = new FakeSource();
            var commands = new FakeCommands();
            HubRuntime? local = null;
            var backend = new StandaloneMarketBackend(Path.Combine(root, "backend"), _ => source,
                (runtime, _, _) => { local = runtime; return commands; });
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using (var watcher = backend.WatchAsync(deadline.Token).GetAsyncEnumerator(deadline.Token))
                {
                    bool observed = false;
                    while (await watcher.MoveNextAsync())
                    {
                        if (watcher.Current.Source == FakeSource.Identity && watcher.Current.Sequence >= 3) { observed = true; break; }
                    }
                    Require(observed, "backend 未直接發布本機採樣。");
                    Require(local is { TransportEnabled: false, ShopSearchOnlyEnabled: true }, "backend 啟動了 IPC 或未啟用商店專用讀取。");
                    Require(commands.Requests.IsEmpty, "啟動或監看快照自動送出了商店命令。");
                    var request = new ShopCommandRequest { Command = ShopCommands.GetState, RequestId = Guid.NewGuid().ToString("N") };
                    var response = await backend.SendAsync(request, deadline.Token);
                    Require(commands.Requests.Count == 1 && ReferenceEquals(commands.Requests.Single(), request), "明確要求 GetState 並非恰好轉送一次原請求。");
                    Require(response.RequestId == request.RequestId && response.State == "FakeState", "本機 broker 回覆未原樣交回。");
                }
                await backend.DisposeAsync();
                await backend.DisposeAsync();
                int settledReads = source.Slices.Count;
                await Task.Delay(250);
                Require(source.DisposeCount == 1 && commands.DisposeCount == 1 && source.Slices.Count == settledReads, "backend 未完整停止，或來源/broker 重複釋放。");
                Require(commands.Requests.All(x => x.Command == ShopCommands.GetState), "離線測試出現自動查店或掃描命令。");
                bool rejected = false;
                try { await backend.SendAsync(new ShopCommandRequest { Command = ShopCommands.GetState }, CancellationToken.None); }
                catch (ObjectDisposedException) { rejected = true; }
                Require(rejected && commands.Requests.Count == 1, "已釋放 backend 仍接受命令。");
            }
            finally { await backend.DisposeAsync(); }
        });
        await Check("standalone_backend_watch_cancellation_stops_enumeration", async () =>
        {
            var source = new FakeSource();
            var commands = new FakeCommands();
            await using var backend = new StandaloneMarketBackend(Path.Combine(root, "cancel-watch"), _ => source, (_, _, _) => commands);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var watcher = backend.WatchAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
            Require(await watcher.MoveNextAsync(), "監看沒有初始快照。");
            deadline.Cancel();
            bool ended = false;
            try
            {
                while (await watcher.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))) { }
                ended = true;
            }
            catch (OperationCanceledException) { ended = true; }
            Require(ended && commands.Requests.IsEmpty, "取消監看未收束或造成命令送出。");
        });
        await Check("standalone_backend_failed_startup_releases_source", async () =>
        {
            var source = new FakeSource();
            var backend = new StandaloneMarketBackend(Path.Combine(root, "failed-startup"), _ => source,
                (_, _, _) => throw new InvalidOperationException("EXPECTED_OFFLINE_STARTUP_FAILURE"));
            bool expected = false;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using var watcher = backend.WatchAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
                while (await watcher.MoveNextAsync()) { }
            }
            catch (Exception error) when (ContainsError(error, "EXPECTED_OFFLINE_STARTUP_FAILURE")) { expected = true; }
            finally { await backend.DisposeAsync(); }
            await backend.DisposeAsync();
            int settledReads = source.Slices.Count;
            await Task.Delay(100);
            Require(expected && source.DisposeCount == 1 && source.Slices.Count == settledReads && source.FullReads == 0,
                "背景商店初始化失敗未通知監看或未釋放一次來源，或清理後仍在採樣。");
        });
        await Check("standalone_backend_startup_and_cleanup_faults_remain_visible", async () =>
        {
            const string startup = "OFFLINE_STARTUP_FAILURE_WITH_CLEANUP_FAILURE";
            const string cleanup = "OFFLINE_STARTUP_SOURCE_DISPOSE_FAILURE";
            var source = new FakeSource { DisposeError = new InvalidOperationException(cleanup) };
            var backend = new StandaloneMarketBackend(Path.Combine(root, "startup-cleanup-fault"), _ => source,
                (_, _, _) => throw new InvalidOperationException(startup));
            Exception? initializationError = null, disposeError = null, repeatedError = null;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using var watcher = backend.WatchAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
                initializationError = await CaptureError(async () => { while (await watcher.MoveNextAsync()) { } });
            }
            finally
            {
                disposeError = await CaptureError(() => backend.DisposeAsync().AsTask());
                repeatedError = await CaptureError(() => backend.DisposeAsync().AsTask());
            }
            Require(ContainsError(initializationError, startup) && ContainsError(initializationError, cleanup),
                "啟動/清理同時失敗時，監看例外未保存兩個原因。");
            Require(ContainsError(disposeError, cleanup) && ContainsError(repeatedError, cleanup), "Dispose 隱藏啟動清理錯誤。");
            int settledReads = source.Slices.Count;
            await Task.Delay(100);
            Require(source.DisposeCount == 1 && source.Slices.Count == settledReads && source.FullReads == 0,
                "清理失敗導致來源重複釋放，或早期來源觀測在清理完成後繼續採樣。");
        });
        await Check("standalone_backend_command_cleanup_fault_still_disposes_source", async () =>
        {
            foreach (bool sourceAlsoFails in new[] { false, true })
            {
                const string commandFailure = "OFFLINE_COMMAND_DISPOSE_FAILURE";
                const string sourceFailure = "OFFLINE_RUNNING_SOURCE_DISPOSE_FAILURE";
                var source = new FakeSource { DisposeError = sourceAlsoFails ? new InvalidOperationException(sourceFailure) : null };
                var commands = new FakeCommands { DisposeError = new InvalidOperationException(commandFailure) };
                HubRuntime? local = null;
                var backend = new StandaloneMarketBackend(Path.Combine(root, "dispose-fault-" + sourceAlsoFails), _ => source,
                    (runtime, _, _) => { local = runtime; return commands; });
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await using (var watcher = backend.WatchAsync(deadline.Token).GetAsyncEnumerator(deadline.Token))
                        Require(await watcher.MoveNextAsync(), "清理失敗測試未完成初始化。");
                    var first = await CaptureError(() => backend.DisposeAsync().AsTask());
                    var second = await CaptureError(() => backend.DisposeAsync().AsTask());
                    Require(ContainsError(first, commandFailure) && ContainsError(second, commandFailure), "商店協調器清理錯誤被吞掉。");
                    if (sourceAlsoFails)
                        Require(ContainsError(first, sourceFailure) && ContainsError(second, sourceFailure), "雙重清理錯誤只保留最後一項。");
                    Require(source.DisposeCount == 1 && commands.DisposeCount == 1 && local is { IsRunning: false },
                        "協調器清理拋出例外後，讀取來源仍在運作或重複釋放。");
                }
                finally { _ = await CaptureError(() => backend.DisposeAsync().AsTask()); }
            }
        });
        await Check("standalone_broker_faulted_request_still_settles_scan_before_source_dispose", async () =>
        {
            const string priorFailure = "OFFLINE_NAVIGATION_TASK_FAILURE";
            var observedSource = new FakeSource();
            var runtime = new HubRuntime(observedSource, Path.Combine(root, "broker-fault-runtime"), enableTransport: false);
            runtime.SetLogging(false);
            // Construction loads an absent profile only; neither this source
            // nor runtime is started, and no broker command is submitted.
            using var readOnlySource = new CurrentLcSource(profileDirectory: Path.Combine(root, "unused-offline-profile"));
            var broker = new ShopBroker(runtime, readOnlySource, Path.Combine(root, "broker-fault-shops"));
            var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetPrivate(broker, "navigationTask", Task.FromException<ShopCommandResponse>(new InvalidOperationException(priorFailure)));
            SetPrivate(broker, "scanTask", worker.Task);
            Task disposal = DisposeInOrder();
            bool stayedPending = false;
            Exception? completedError = null;
            try
            {
                var stop = (CancellationTokenSource)GetPrivate(broker, "stop");
                await Until(() => stop.IsCancellationRequested);
                stayedPending = await Task.WhenAny(disposal, Task.Delay(200)) != disposal && observedSource.DisposeCount == 0;
            }
            finally
            {
                worker.TrySetResult();
                completedError = await CaptureError(() => disposal);
                _ = await CaptureError(() => broker.DisposeAsync().AsTask());
                _ = await CaptureError(() => runtime.DisposeAsync().AsTask());
            }
            Require(stayedPending, "已失敗的工作使 Dispose 跳過仍未結束的 scan worker，過早釋放來源。");
            Require(ContainsError(completedError, priorFailure), "完成 worker 等候後丟失原先工作錯誤。");
            Require(observedSource.DisposeCount == 1 && observedSource.Slices.IsEmpty && observedSource.FullReads == 0,
                "broker 清理測試意外採樣，或來源未恰好釋放一次。");

            async Task DisposeInOrder()
            {
                try { await broker.DisposeAsync(); }
                finally { await runtime.DisposeAsync(); }
            }
        });
        checks.Add(new("standalone_offline_artifacts", true, root));
        return checks;

        async Task Check(string name, Func<Task> test)
        {
            try { await test(); checks.Add(new(name, true, "通過；僅使用離線來源/工作，未開啟 LC 或提交原生命令。")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
    }

    private static void Require(bool condition, string detail)
    { if (!condition) throw new InvalidOperationException(detail); }

    private static async Task<Exception?> CaptureError(Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); return null; }
        catch (Exception error) { return error; }
    }

    private static bool ContainsError(Exception? error, string expected)
    {
        if (error is null) return false;
        if (error.Message == expected) return true;
        if (error is AggregateException aggregate && aggregate.InnerExceptions.Any(item => ContainsError(item, expected))) return true;
        return ContainsError(error.InnerException, expected);
    }

    private static object GetPrivate(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
        ?? throw new InvalidOperationException("離線測試找不到欄位：" + name);

    private static void SetPrivate(object owner, string name, object value)
    {
        var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("離線測試找不到欄位：" + name);
        field.SetValue(owner, value);
    }

    private static async Task Until(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("本機採樣在 8 秒內未達到測試条件。");
            await Task.Delay(20);
        }
    }

    private sealed class FakeSource : ISlicedMemorySource
    {
        internal static readonly SourceIdentity Identity = new(4242, 987654321, "offline-no-LC.exe", "0x100000", 11, 4096, new string('A', 64), "offline-standalone-scene");
        internal readonly ConcurrentQueue<string[]> Configurations = new();
        internal readonly ConcurrentQueue<string[]> Slices = new();
        private ReadSelection selection = new([]);
        private int fullReads, disposeCount;
        internal int FullReads => Volatile.Read(ref fullReads);
        internal int DisposeCount => Volatile.Read(ref disposeCount);
        internal Exception? DisposeError { get; init; }
        public void Configure(ReadSelection value)
        { Volatile.Write(ref selection, value); Configurations.Enqueue(value.EnabledKeys.ToArray()); }
        public ReadResult Read(CancellationToken cancellationToken)
        { Interlocked.Increment(ref fullReads); throw new InvalidOperationException("Fake source 禁止完整讀取；必須使用分欄讀取。"); }
        public ReadResult ReadSlice(ReadSelection fields, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(DisposeCount != 0, this);
            if (fields.EnabledKeys.Any(key => !Volatile.Read(ref selection).Enabled(key))) throw new InvalidOperationException("讀取未啟用欄位。");
            Slices.Enqueue(fields.EnabledKeys.ToArray());
            var at = DateTimeOffset.UtcNow;
            var readings = fields.EnabledKeys.ToDictionary(key => key, key => key switch
            {
                "selfId" => FieldReading.Observed(11UL, at),
                "position" => FieldReading.Observed(new { x = 100, y = 200 }, at),
                "mapName" => FieldReading.Observed("離線測試地圖", at),
                _ => FieldReading.Observed(new { offline = true }, at)
            });
            return new(Identity, readings, "Reading", "離線來源", fields.EnabledKeys.Count, fields.EnabledKeys.Count * 8);
        }
        public void Dispose()
        {
            Interlocked.Increment(ref disposeCount);
            if (DisposeError is not null) throw DisposeError;
        }
    }

    private sealed class FakeCommands : IStandaloneShopCommands
    {
        internal readonly ConcurrentQueue<ShopCommandRequest> Requests = new();
        private int disposeCount;
        internal int DisposeCount => Volatile.Read(ref disposeCount);
        internal Exception? DisposeError { get; init; }
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(DisposeCount != 0, this);
            Requests.Enqueue(request);
            return Task.FromResult(new ShopCommandResponse { RequestId = request.RequestId, State = "FakeState", NativeEntered = false });
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposeCount);
            if (DisposeError is not null) throw DisposeError;
            return ValueTask.CompletedTask;
        }
    }
}
