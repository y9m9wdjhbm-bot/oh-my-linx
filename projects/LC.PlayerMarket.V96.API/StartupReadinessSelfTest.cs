using System.Collections.Concurrent;
using MemoryHub;
using MemoryHub.Reader;

namespace LcPlayerMarket;

/// <summary>Real reader/broker lifecycle, delayed fake broker; no game process or native action.</summary>
internal static class StartupReadinessSelfTest
{
    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        string root = Path.Combine(Path.GetTempPath(), "LC-PlayerMarket-V52-Startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        async Task Check(string name, Func<string, Task> action)
        {
            try { await action(Path.Combine(root, name)); checks.Add(new(name, true, "Fake reader/broker; no LC process, game input or native request.")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }

        await Check("startup_reader_source_arrives_while_broker_is_pending", async directory =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new FakeSource(); var commands = new FakeCommands();
            var backend = new StandaloneMarketBackend(directory, _ => source, (runtime, _, _) =>
            {
                Need(runtime.IsRunning, "Reader runtime did not start before historical broker construction.");
                entered.TrySetResult(); release.Wait(deadline.Token); return commands;
            });
            try
            {
                await entered.Task.WaitAsync(deadline.Token);
                await using var watcher = backend.WatchAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
                HubSnapshot observed = await ObserveSourceAsync(watcher);
                Need(observed.Source == FakeSource.Identity && !release.IsSet && source.SliceCount > 0,
                    "Watcher remained dependent on full historical broker initialization.");
                var request = new ShopCommandRequest { Command = ShopCommands.GetState };
                Task<ShopCommandResponse> pending = backend.SendAsync(request, deadline.Token);
                Need(!pending.IsCompleted && commands.Requests.IsEmpty, "A shop command entered an incompletely initialized broker.");
                release.Set();
                ShopCommandResponse response = await pending.WaitAsync(deadline.Token);
                Need(response.RequestId == request.RequestId && commands.Requests.Count == 1,
                    "Queued command was dropped or sent more than once after complete initialization.");
            }
            finally { release.Set(); await backend.DisposeAsync(); }
            Need(source.DisposeCount == 1 && commands.DisposeCount == 1, "Split startup leaked or released owned resources twice.");
        });

        await Check("startup_cancelled_watch_does_not_cancel_reader_or_submit_commands", async directory =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            using var release = new ManualResetEventSlim();
            var source = new FakeSource(); var commands = new FakeCommands();
            var backend = new StandaloneMarketBackend(directory, _ => source, (_, _, _) => { release.Wait(deadline.Token); return commands; });
            try
            {
                await using var watcher = backend.WatchAsync(watchCancellation.Token).GetAsyncEnumerator(watchCancellation.Token);
                await ObserveSourceAsync(watcher);
                watchCancellation.Cancel();
                Exception? cancelled = await Error(async () => { while (await watcher.MoveNextAsync()) { } });
                Need(cancelled is OperationCanceledException && commands.Requests.IsEmpty && source.DisposeCount == 0,
                    "Cancelling the early subscription disposed the owned reader or submitted a command.");
            }
            finally { release.Set(); await backend.DisposeAsync(); }
            Need(source.DisposeCount == 1 && commands.DisposeCount == 1, "Cancelled early subscription prevented complete disposal.");
        });

        await Check("startup_late_broker_failure_is_visible_to_early_watcher", async directory =>
        {
            const string failure = "EXPECTED_LATE_BROKER_STARTUP_FAILURE";
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var release = new ManualResetEventSlim();
            var source = new FakeSource();
            var backend = new StandaloneMarketBackend(directory, _ => source, (_, _, _) =>
            { release.Wait(deadline.Token); throw new InvalidOperationException(failure); });
            try
            {
                await using var watcher = backend.WatchAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
                await ObserveSourceAsync(watcher);
                release.Set();
                Exception? observed = await Error(async () => { while (await watcher.MoveNextAsync()) { } });
                Need(observed?.ToString().Contains(failure) == true && source.DisposeCount == 1,
                    "Reader observation hid a later failed historical broker initialization.");
            }
            finally { release.Set(); await backend.DisposeAsync(); }
            int after = source.SliceCount;
            await Task.Delay(100);
            Need(source.SliceCount == after && source.DisposeCount == 1, "Failed startup left a sampler running or repeated disposal.");
        });

        await Check("startup_source_constructor_failure_reaches_watch_and_send", async directory =>
        {
            const string failure = "EXPECTED_SOURCE_STARTUP_FAILURE";
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var commands = new FakeCommands();
            var backend = new StandaloneMarketBackend(directory, _ => throw new InvalidOperationException(failure), (_, _, _) => commands);
            try
            {
                await using var watcher = backend.WatchAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
                Exception? watch = await Error(async () => { await watcher.MoveNextAsync(); });
                Exception? send = await Error(async () => { await backend.SendAsync(new ShopCommandRequest { Command = ShopCommands.GetState }, deadline.Token); });
                Need(watch?.ToString().Contains(failure) == true && send?.ToString().Contains(failure) == true
                    && commands.Requests.IsEmpty && commands.DisposeCount == 0,
                    "Source-construction failure left one readiness task pending or created command resources.");
            }
            finally { await backend.DisposeAsync(); }
        });

        await Check("startup_dispose_cancels_pending_command_and_joins_initialization", async directory =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var release = new ManualResetEventSlim();
            var source = new FakeSource(); var commands = new FakeCommands();
            var backend = new StandaloneMarketBackend(directory, _ => source, (_, _, _) => { release.Wait(deadline.Token); return commands; });
            try
            {
                await using var watcher = backend.WatchAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
                await ObserveSourceAsync(watcher);
                Task<ShopCommandResponse> pending = backend.SendAsync(new ShopCommandRequest { Command = ShopCommands.GetState }, deadline.Token);
                Task disposing = backend.DisposeAsync().AsTask();
                Exception? send = await Error(async () => { await pending.WaitAsync(deadline.Token); });
                Need(send is OperationCanceledException && commands.Requests.IsEmpty && !disposing.IsCompleted,
                    "Disposal dispatched a pending command or did not join the owned initializing broker.");
                release.Set();
                await disposing.WaitAsync(deadline.Token);
                Need(source.DisposeCount == 1 && commands.DisposeCount == 1, "Partial early startup was not released exactly once.");
                await backend.DisposeAsync();
                Need(source.DisposeCount == 1 && commands.DisposeCount == 1, "Repeated disposal repeated resource release.");
            }
            finally { release.Set(); await backend.DisposeAsync(); }
        });

        checks.Add(new("startup_readiness_offline_artifacts", true, root));
        return checks.ToArray();
    }

    private static async Task<HubSnapshot> ObserveSourceAsync(IAsyncEnumerator<HubSnapshot> watcher)
    {
        while (await watcher.MoveNextAsync()) if (watcher.Current.Source is not null) return watcher.Current;
        throw new InvalidOperationException("Early reader subscription ended without a source observation.");
    }
    private static async Task<Exception?> Error(Func<Task> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }
    private static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }

    private sealed class FakeSource : ISlicedMemorySource
    {
        internal static readonly SourceIdentity Identity = new(7101, 9002, "offline-only", "0x100000", 1, 4096,
            new string('A', 64), "early-reader-offline-session");
        private int slices, disposed;
        internal int SliceCount => Volatile.Read(ref slices);
        internal int DisposeCount => Volatile.Read(ref disposed);
        public void Configure(ReadSelection selection) { }
        public ReadResult Read(CancellationToken token) => throw new InvalidOperationException("Fixture permits sliced source observations only.");
        public ReadResult ReadSlice(ReadSelection fields, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(DisposeCount != 0, this);
            Interlocked.Increment(ref slices);
            DateTimeOffset at = DateTimeOffset.UtcNow;
            var values = fields.Empty("Offline reader observation.");
            foreach (string key in fields.EnabledKeys) values[key] = FieldReading.Observed(key == "selfId" ? (object)11UL : "offline", at);
            return new(Identity, values, "Reading", "Fake reader source is ready before shop historical replay.");
        }
        public void Dispose() => Interlocked.Increment(ref disposed);
    }
    private sealed class FakeCommands : IStandaloneShopCommands
    {
        internal readonly ConcurrentQueue<ShopCommandRequest> Requests = new();
        private int disposed;
        internal int DisposeCount => Volatile.Read(ref disposed);
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            return Task.FromResult(new ShopCommandResponse { RequestId = request.RequestId, State = "FakeReady", CurrentSourceSessionKey = FakeSource.Identity.SessionKey });
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref disposed); return ValueTask.CompletedTask; }
    }
}
