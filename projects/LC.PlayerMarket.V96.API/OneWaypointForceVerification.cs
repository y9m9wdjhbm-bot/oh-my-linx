using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>One owned navigation job; no shop commands, native retries or input simulation.</summary>
internal static class OneWaypointForceVerification
{
    private const int ArrivalRadius = 1;

    internal static async Task<int> RunAsync(int dx, int dy, string outputPath)
    {
        // A point already within the arrival radius cannot prove this movement.
        if (Math.Abs((long)dx) > 3 || Math.Abs((long)dy) > 3
            || Math.Max(Math.Abs((long)dx), Math.Abs((long)dy)) <= ArrivalRadius) return 2;
        bool administrator = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        if (!administrator) return 2;
        string output = Path.GetFullPath(outputPath);
        if (Directory.Exists(output) || File.Exists(output)) return 2;
        Directory.CreateDirectory(output);
        using var userStop = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource();
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(userStop.Token, lifetime.Token);
        preparation.CancelAfter(TimeSpan.FromSeconds(180));
        HubSnapshot? latest = null;
        HubRuntime? runtime = null;
        var errors = new List<string>();
        var states = new List<PatrolNavigationState>();
        var after = new List<PatrolObservation>();
        var notices = new List<PatrolSourceWaitNotice>();
        PatrolObservation? initialBeforeForeground = null, before = null;
        PatrolNavigationRequest? request = null;
        PatrolNavigationState? terminal = null, cleanupState = null;
        DateTimeOffset? movementStartedAt = null, movementFinishedAt = null;
        bool navigationDisposed = false, readerStopped = false, cleanupConfirmed = false;
        bool userCancelled = false, foregroundAttempted = false, foregroundVerified = false;
        int startAttempts = 0;
        var backend = new StandaloneMarketBackend(Path.Combine(output, "standalone-state"),
            StandaloneMarketBackend.CreateDefaultSource,
            (rt, src, shops) => { runtime = rt; return new StandaloneShopCommands(new ShopBroker(rt, (CurrentLcSource)src, shops)); });
        var navigation = new PatrolNavigationClient(Path.Combine(output, "navigation-runtime"),
            () => Volatile.Read(ref latest), readOnly: false,
            report: notice => { lock (notices) notices.Add(notice); }, forceDispatch: true);
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; userCancelled = true; userStop.Cancel(); };
        Console.CancelKeyPress += cancel;
        Task watcher = WatchAsync();
        try
        {
            while (!new LiveContext(Volatile.Read(ref latest), DateTimeOffset.UtcNow).Valid)
            {
                if (watcher.IsFaulted) await watcher;
                await Task.Delay(100, preparation.Token);
            }
            before = await navigation.CaptureAsync(preparation.Token);
            ValidateObservation(before, before);
            if (runtime is null || runtime.TransportEnabled || runtime.Snapshot.Source != before.Source)
                throw new InvalidDataException("FRESH_LOCAL_SOURCE_REQUIRED");
            initialBeforeForeground = before;
            foregroundAttempted = true;
            foregroundVerified = PatrolForegroundGate.TryOnce(initialBeforeForeground.Source,
                () => Volatile.Read(ref latest)?.Source, new PatrolForegroundWindow(), preparation.Token);
            if (!foregroundVerified) throw new InvalidDataException("FOREGROUND_NOT_CONFIRMED_BEFORE_NAVIGATION");
            // The focus attempt may take time. Re-read the exact same source before
            // constructing this sole immutable destination and owned job.
            before = await navigation.CaptureAsync(preparation.Token, initialBeforeForeground);
            ValidateObservation(before, initialBeforeForeground);
            if (runtime is null || runtime.TransportEnabled || runtime.Snapshot.Source != before.Source)
                throw new InvalidDataException("FRESH_LOCAL_SOURCE_REQUIRED_AFTER_FOREGROUND");
            long targetX = (long)before.X + dx, targetY = (long)before.Y + dy;
            if (targetX is < 0 or > 65535 || targetY is < 0 or > 65535)
                throw new InvalidDataException("RELATIVE_TARGET_OUTSIDE_COORDINATE_RANGE");
            request = new(Guid.NewGuid().ToString("N"), before,
                new(Guid.NewGuid().ToString("N"), "單次直接導航短程驗證", before.MapId.ToString(CultureInfo.InvariantCulture), "", (int)targetX, (int)targetY), ArrivalRadius);
            using var movement = CancellationTokenSource.CreateLinkedTokenSource(userStop.Token, lifetime.Token);
            movement.CancelAfter(TimeSpan.FromSeconds(30));
            movementStartedAt = DateTimeOffset.UtcNow;
            startAttempts++;
            // Register the immutable job before awaiting acceptance. An exception
            // is reconciled only through Stop/GetState for this job, never Start.
            var state = await navigation.StartAsync(request, movement.Token);
            for (int poll = 0; ; poll++)
            {
                movement.Token.ThrowIfCancellationRequested();
                ValidateState(state, request);
                states.Add(state);
                if (state.UserStop || state.StopRequested)
                    throw new OperationCanceledException("導航已被 F9 或使用者停止；未重送。", movement.Token);
                if (state.Terminal)
                {
                    terminal = state;
                    if (!state.Completed || state.State != "Completed" || !state.CleanupConfirmed || !state.WorkSettled)
                        throw new InvalidDataException(state.Error ?? "NAVIGATION_TERMINAL_NOT_COMPLETED_AND_SETTLED");
                    break;
                }
                if (poll >= 150) throw new TimeoutException("NAVIGATION_POLL_LIMIT; original job retained");
                await Task.Delay(200, movement.Token);
                state = await navigation.GetStateAsync(request.JobId, movement.Token);
            }
            for (int index = 0; index < 2;)
            {
                DateTimeOffset captureStartedAt = DateTimeOffset.UtcNow;
                var observed = await navigation.CaptureAsync(movement.Token, before);
                ValidateObservation(observed, before);
                // The production force port may complete while the released final
                // step is settling. Only re-read; never submit another movement job.
                if (!observed.Standing)
                {
                    await Task.Delay(100, movement.Token);
                    continue;
                }
                if (observed.SampledAt < captureStartedAt
                    || observed.SampledAt <= terminal.SampledAt
                    || (after.Count > 0 && observed.SampledAt <= after[^1].SampledAt)
                    || Distance(observed.X, observed.Y, request.Waypoint.X, request.Waypoint.Y) > ArrivalRadius
                    || (observed.X == before.X && observed.Y == before.Y))
                    throw new InvalidDataException("NEW_STANDING_ARRIVAL_OBSERVATION_REQUIRED");
                after.Add(observed);
                index++;
                if (index == 1) await Task.Delay(150, movement.Token);
            }
            if (after[0].X != after[1].X || after[0].Y != after[1].Y)
                throw new InvalidDataException("AFTER_OBSERVATIONS_NOT_STATIONARY");
            movement.Token.ThrowIfCancellationRequested();
            movementFinishedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception error) { errors.Add(error.ToString()); }
        finally
        {
            // Cleanup has its own total budget, even after preparation, movement,
            // user cancellation, lost acceptance, or unknown native outcome.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (request is not null && startAttempts == 1)
            {
                try
                {
                    cleanupState = await navigation.StopAsync(request.JobId, cleanup.Token);
                    while (true)
                    {
                        ValidateState(cleanupState, request);
                        if (cleanupState.Terminal && cleanupState.CleanupConfirmed && cleanupState.WorkSettled)
                        { cleanupConfirmed = true; break; }
                        await Task.Delay(200, cleanup.Token);
                        cleanupState = await navigation.GetStateAsync(request.JobId, cleanup.Token);
                    }
                }
                catch (Exception error) { errors.Add("原導航工作收尾：" + error); }
            }
            try { await navigation.DisposeAsync(cleanup.Token); navigationDisposed = true; }
            catch (Exception error) { errors.Add("導航關閉／退出尚未確認：" + error); }
            lifetime.Cancel();
            try { await watcher.WaitAsync(cleanup.Token); }
            catch (Exception error) { errors.Add("讀取工作收尾：" + error); }
            try
            {
                await backend.DisposeAsync().AsTask().WaitAsync(cleanup.Token);
                readerStopped = runtime is null || !runtime.IsRunning;
            }
            catch (Exception error) { errors.Add("本機讀取後端關閉尚未確認：" + error); }
            Console.CancelKeyPress -= cancel;
        }
        bool success = administrator && foregroundVerified && startAttempts == 1 && errors.Count == 0 && !userCancelled
            && terminal is { Completed: true, Terminal: true, CleanupConfirmed: true, WorkSettled: true }
            && after.Count == 2 && cleanupConfirmed && navigationDisposed && readerStopped
            && navigation.ChildExitCode == 0 && movementFinishedAt is not null;
        PatrolSourceWaitNotice[] recordedNotices;
        lock (notices) recordedNotices = notices.ToArray();
        string executedAssembly = typeof(Program).Assembly.Location;
        string executedAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executedAssembly)));
        var report = new
        {
            At = DateTimeOffset.UtcNow, Version = typeof(Program).Assembly.GetName().Version?.ToString(), Success = success,
            IsAdministrator = administrator, ForceDispatch = true, ForegroundAttempted = foregroundAttempted, ForegroundVerified = foregroundVerified, ForegroundAttemptLimit = 1, ExecutedAssembly = executedAssembly, ExecutedAssemblySha256 = executedAssemblySha256, RelativeX = dx, RelativeY = dy, MaximumRelativeGridDistance = 3, ArrivalRadius,
            PreparationLimitSeconds = 180, MovementLimitSeconds = 30, CleanupLimitSeconds = 15,
            NavigationStartAttempts = startAttempts, MaximumNavigationJobs = 1, AutomaticRetries = 0,
            ShopCommands = 0, TradeCommands = 0, KeyboardMouseInputs = 0, UserCancelled = userCancelled,
            InitialBeforeForeground = initialBeforeForeground, Before = before, Request = request, States = states, Terminal = terminal, After = after,
            MovementStartedAt = movementStartedAt, MovementFinishedAt = movementFinishedAt,
            CleanupState = cleanupState, CleanupConfirmed = cleanupConfirmed,
            WorkSettled = cleanupConfirmed && navigationDisposed && readerStopped,
            NativePending = startAttempts == 1 ? (bool?)!cleanupConfirmed : null,
            NavigationDisposed = navigationDisposed, NavigationChildExitCode = navigation.ChildExitCode, ReaderStopped = readerStopped,
            CoordinateMovementObserved = after.Count == 2, NativeDispatchVerified = false, ServerAcceptanceVerified = false,
            Notices = recordedNotices, Errors = errors
        };
        using var file = new FileStream(Path.Combine(output, "result.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, new JsonSerializerOptions(HubProtocol.Json) { WriteIndented = true });
        file.Flush(true);
        return success ? 0 : 1;

        async Task WatchAsync()
        {
            try { await foreach (var item in backend.WatchAsync(lifetime.Token)) Volatile.Write(ref latest, item); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private static long Distance(int x, int y, int targetX, int targetY)
        => Math.Max(Math.Abs((long)x - targetX), Math.Abs((long)y - targetY));

    private static void ValidateObservation(PatrolObservation value, PatrolObservation expected)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (value.Source != expected.Source || value.SelfId == 0 || value.SelfId != expected.SelfId || value.MapId < 0 || value.MapId != expected.MapId
            || value.X is < 0 or > 65535 || value.Y is < 0 or > 65535 || value.SampledAt == default || value.SampledAt > now
            || now - value.SampledAt > TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds))
            throw new InvalidDataException("FRESH_SAME_SOURCE_ROLE_MAP_OBSERVATION_REQUIRED");
    }

    private static void ValidateState(PatrolNavigationState state, PatrolNavigationRequest request)
    {
        if (state.JobId != request.JobId || state.WaypointId != request.Waypoint.PointId
            || state.TargetX != request.Waypoint.X || state.TargetY != request.Waypoint.Y || state.ArrivalRadius != request.ArrivalRadius
            || state.Source != request.Expected.Source || state.SelfId != request.Expected.SelfId || state.MapId != request.Expected.MapId)
            throw new InvalidDataException("OWNED_NAVIGATION_JOB_SOURCE_ROLE_MAP_MISMATCH");
    }
}
