using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>
/// Owns one in-process reader and guarded shop coordinator for this application.
/// Watching or reconnecting only changes local subscribers; it never creates
/// another reader, opens a transport endpoint, or submits a shop request.
/// </summary>
internal sealed class StandaloneMarketBackend : IAsyncDisposable
{
    private readonly object lifecycle = new();
    private readonly SemaphoreSlim commandEntry = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource<HubRuntime> readerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task<Components> initialization;
    private int pageWaitMilliseconds;
    private IStandaloneShopCommands? configuredCommands;
    private bool disposed;
    private Task? disposal;
    internal string? DiagnosticsRoot { get; }

    public StandaloneMarketBackend(string stateRoot, string? diagnosticsRoot = null)
        : this(stateRoot, CreateDefaultSource, null, useDefaultCommands: true, diagnosticsRoot)
    {
    }

    // Factories let offline checks exercise the actual lifecycle and runtime
    // without opening LC or replacing any broker safety checks in production.
    internal StandaloneMarketBackend(string stateRoot,
        Func<Action<HubLog>, IMemorySource> sourceFactory,
        Func<HubRuntime, IMemorySource, string, IStandaloneShopCommands> commandsFactory, string? diagnosticsRoot = null)
        : this(stateRoot, sourceFactory, commandsFactory ?? throw new ArgumentNullException(nameof(commandsFactory)), false, diagnosticsRoot) { }

    private StandaloneMarketBackend(string stateRoot, Func<Action<HubLog>, IMemorySource> sourceFactory,
        Func<HubRuntime, IMemorySource, string, IStandaloneShopCommands>? commandsFactory, bool useDefaultCommands, string? diagnosticsRoot)
    {
        string? diagnostics = MarketLogPaths.BackendDiagnostics(stateRoot, useDefaultCommands, diagnosticsRoot);
        DiagnosticsRoot = diagnostics;
        if (useDefaultCommands) commandsFactory = (runtime, source, shops) => new StandaloneShopCommands(new ShopBroker(runtime, (CurrentLcSource)source, shops, lifetime.Token,
            nativeLogDirectory: diagnostics is null ? null : Path.Combine(diagnostics, "native")));
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        ArgumentNullException.ThrowIfNull(sourceFactory);
        ArgumentNullException.ThrowIfNull(commandsFactory);
        string directory = Path.GetFullPath(stateRoot);
        initialization = Task.Run(() => InitializeAsync(directory, sourceFactory, commandsFactory, diagnostics));
    }

    internal static IMemorySource CreateDefaultSource(Action<HubLog> log) =>
        new CurrentLcSource(log, profileDirectory: Path.Combine(AppContext.BaseDirectory, "profiles"));

    private async Task<Components> InitializeAsync(string stateRoot,
        Func<Action<HubLog>, IMemorySource> sourceFactory,
        Func<HubRuntime, IMemorySource, string, IStandaloneShopCommands> commandsFactory, string? diagnosticsRoot)
    {
        IMemorySource? source = null;
        HubRuntime? runtime = null;
        IStandaloneShopCommands? commands = null;
        var pendingLogs = new ConcurrentQueue<HubLog>();
        void Record(HubLog entry)
        {
            var current = Volatile.Read(ref runtime);
            if (current is null) pendingLogs.Enqueue(entry);
            else current.RecordLog(entry);
        }
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            StartupTiming.MarkOnce("source-constructor-start");
            source = sourceFactory(Record) ?? throw new InvalidOperationException("未建立程式內讀取來源。");
            StartupTiming.MarkOnce("source-constructor-end");
            runtime = new HubRuntime(source, Path.Combine(stateRoot, "reader"), enableTransport: false,
                logDirectory: diagnosticsRoot is null ? null : Path.Combine(diagnosticsRoot, "reader"));
            while (pendingLogs.TryDequeue(out var entry)) runtime.RecordLog(entry);
            runtime.SetShopSearchOnly(true);
            lifetime.Token.ThrowIfCancellationRequested();
            // Source observation is independent of historical shop replay. A
            // command still awaits the complete broker below before admission.
            runtime.Start();
            StartupTiming.MarkOnce("reader-runtime-ready");
            runtime.RecordLog(new(DateTimeOffset.UtcNow, "STANDALONE_READER_READY",
                "LC 來源觀測已啟動；商店歷史在背景核對，尚未送出商店指令。",
                new { runtime.TransportEnabled, runtime.ShopSearchOnlyEnabled, EnabledFields = runtime.ReadSelection.EnabledKeys }));
            readerReady.TrySetResult(runtime);
            StartupTiming.MarkOnce("broker-constructor-start");
            commands = commandsFactory(runtime, source, Path.Combine(stateRoot, "shops"))
                ?? throw new InvalidOperationException("未建立程式內商店協調器。");
            lifetime.Token.ThrowIfCancellationRequested();
            lock (lifecycle)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                commands.ConfigurePageWaitMilliseconds(pageWaitMilliseconds);
                configuredCommands = commands;
            }
            StartupTiming.MarkOnce("broker-constructor-end");
            runtime.RecordLog(new(DateTimeOffset.UtcNow, "STANDALONE_BACKEND_READY",
                "玩家商店由同程序自行讀取；資料傳輸介面已停用。",
                new { runtime.TransportEnabled, runtime.ShopSearchOnlyEnabled, EnabledFields = runtime.ReadSelection.EnabledKeys }));
            return new(runtime, commands);
        }
        catch (Exception initializationError)
        {
            lock (lifecycle)
                if (ReferenceEquals(configuredCommands, commands)) configuredCommands = null;
            // The coordinator must release its outstanding work before the
            // runtime disposes the source, including partially failed startup.
            var cleanupErrors = await ReleaseOwnedAsync(runtime, source, commands).ConfigureAwait(false);
            if (cleanupErrors.Count != 0)
            {
                var combined = new StandaloneInitializationCleanupException(initializationError, cleanupErrors);
                readerReady.TrySetException(combined);
                throw combined;
            }
            readerReady.TrySetException(initializationError);
            throw;
        }
    }

    public async IAsyncEnumerable<HubSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken token = default)
    {
        using var linked = CreateOperationToken(token);
        var runtime = await readerReady.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        var snapshots = Channel.CreateBounded<HubSnapshot>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        var publishGate = new object();
        long newestSequence = -1;
        void Publish(HubSnapshot snapshot)
        {
            lock (publishGate)
            {
                if (snapshot.Sequence < newestSequence) return;
                newestSequence = snapshot.Sequence;
                snapshots.Writer.TryWrite(snapshot);
            }
        }
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            runtime.Changed += Publish;
        }
        Task startupObservation = ObserveStartupAsync();
        try
        {
            // Subscribe first so the transition from initial snapshot to local
            // updates cannot miss a completed read. Keep only the newest value.
            Publish(await Task.Run(() => runtime.Snapshot, linked.Token).ConfigureAwait(false));
            await foreach (var snapshot in snapshots.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
                yield return snapshot;
        }
        finally
        {
            runtime.Changed -= Publish;
            linked.Cancel();
            snapshots.Writer.TryComplete();
            await startupObservation.ConfigureAwait(false);
        }

        async Task ObserveStartupAsync()
        {
            try { await initialization.WaitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            catch (Exception error) { snapshots.Writer.TryComplete(error); }
        }
    }

    public async Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var linked = CreateOperationToken(token);
        var components = await initialization.WaitAsync(linked.Token).ConfigureAwait(false);
        // Broker validation, saved-state access, and request preparation can
        // perform synchronous work before returning a task. Keep it off the UI.
        return await Task.Run(async () =>
        {
            await commandEntry.WaitAsync(linked.Token).ConfigureAwait(false);
            Task<ShopCommandResponse> response;
            try
            {
                lock (lifecycle) ObjectDisposedException.ThrowIf(disposed, this);
                linked.Token.ThrowIfCancellationRequested();
                response = components.Commands.SendAsync(request, linked.Token);
            }
            finally { commandEntry.Release(); }
            return await response.ConfigureAwait(false);
        }, CancellationToken.None).ConfigureAwait(false);
    }

    // This only changes local configuration. It must remain available while
    // background initialization or an already-open page is still running.
    internal void ConfigurePageWaitSeconds(int seconds) => ConfigurePageWaitMilliseconds(checked(ShopPagePacing.ValidateSeconds(seconds) * 1000));
    internal void ConfigurePageWaitMilliseconds(int milliseconds)
    {
        ShopPagePacing.ValidateMilliseconds(milliseconds);
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            configuredCommands?.ConfigurePageWaitMilliseconds(milliseconds);
            pageWaitMilliseconds = milliseconds;
        }
    }

    private CancellationTokenSource CreateOperationToken(CancellationToken token)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            disposed = true;
            configuredCommands = null;
            disposal ??= Task.Run(DisposeCoreAsync);
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            lifetime.Cancel();
            Components components;
            try { components = await initialization.ConfigureAwait(false); }
            catch (StandaloneInitializationCleanupException) { throw; }
            catch { return; } // Original startup failure; partial-resource cleanup succeeded.
            // Wait only on this worker for any synchronous command admission
            // already in progress. The UI's lifetime lock is always brief.
            await commandEntry.WaitAsync().ConfigureAwait(false);
            var cleanupErrors = await ReleaseOwnedAsync(components.Runtime, null, components.Commands).ConfigureAwait(false);
            if (cleanupErrors.Count != 0)
                throw new AggregateException("程式內商店後端已完成關閉處理，但清理發生錯誤。", cleanupErrors);
        }
        finally { commandEntry.Dispose(); lifetime.Dispose(); }
    }

    private static async Task<List<Exception>> ReleaseOwnedAsync(HubRuntime? runtime,
        IMemorySource? source, IStandaloneShopCommands? commands)
    {
        var errors = new List<Exception>();
        try { if (commands is not null) await commands.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        try
        {
            if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
            else source?.Dispose();
        }
        catch (Exception error) { errors.Add(error); }
        return errors;
    }

    private sealed record Components(HubRuntime Runtime, IStandaloneShopCommands Commands);
}

internal sealed class StandaloneInitializationCleanupException(Exception initializationError, IEnumerable<Exception> cleanupErrors)
    : AggregateException("程式內商店後端啟動失敗，釋放初始化資源時也發生錯誤。",
        new[] { initializationError }.Concat(cleanupErrors))
{
}

internal interface IStandaloneShopCommands : IAsyncDisposable
{
    Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token);
    void ConfigurePageWaitSeconds(int seconds) { }
    void ConfigurePageWaitMilliseconds(int milliseconds) { }
}

internal sealed class StandaloneShopCommands(ShopBroker broker) : IStandaloneShopCommands
{
    public void ConfigurePageWaitSeconds(int seconds) => broker.ConfigurePageWaitSeconds(seconds);
    public void ConfigurePageWaitMilliseconds(int milliseconds) => broker.ConfigurePageWaitMilliseconds(milliseconds);
    public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token) => broker.HandleAsync(request, token);
    public ValueTask DisposeAsync() => broker.DisposeAsync();
}
