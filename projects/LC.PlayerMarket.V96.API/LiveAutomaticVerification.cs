using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Explicit administrator diagnostic. Real production ports, isolated evidence, no UI automation.</summary>
internal static class LiveAutomaticVerification
{
    private const string UserRoot = @"D:\lineage\LC_PlayerMarket_UserData\V38";
    private static readonly JsonSerializerOptions Json = new(HubProtocol.Json) { WriteIndented = true };
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    internal sealed record ModePlan(bool IsScan, bool IsFullRound, bool ExplicitOneRoundDiagnostic, int StopAfterPoints, int LimitSeconds)
    { internal int SpeedSampleCompletedPageThreshold { get; init; } }
    // Pure plan is inspectable offline; it does not construct a backend or read LC.
    internal static ModePlan PlanMode(string mode) => mode switch
    {
        "scan" => new(true, false, false, 0, 60),
        "scan-speed" => new(true, false, false, 0, 180) { SpeedSampleCompletedPageThreshold = LiveSpeedSamplePolicy.CompletedPageThreshold },
        "patrol" => new(false, false, false, 1, 120),
        "patrol-round" => new(false, true, false, 4, 1800),
        "patrol-one-round" => new(false, true, true, 4, 1800),
        _ => throw new ArgumentException("Unknown live verification mode.", nameof(mode))
    };

    internal static async Task<int> RunAsync(string mode, string output, int? pageIntervalMillisecondsOverride = null)
    {
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        if (!admin) return 5;
        ModePlan plan;
        try { plan = PlanMode(mode); } catch (ArgumentException) { return 2; }
        bool isScan = plan.IsScan, isFullRound = plan.IsFullRound;
        int stopAfterPoints = plan.StopAfterPoints;
        output = Path.GetFullPath(output);
        if (File.Exists(output) || Directory.Exists(output)) return 2;
        Directory.CreateDirectory(output);
        using var trace = new EvidenceLog(Path.Combine(output, "commands-and-observations.jsonl"));
        using var operation = new CancellationTokenSource();
        using var watcherStop = new CancellationTokenSource();
        using var keyStop = new CancellationTokenSource();
        int limitSeconds = plan.LimitSeconds;
        operation.CancelAfter(TimeSpan.FromSeconds(limitSeconds));
        var elapsed = Stopwatch.StartNew();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        string? stopReason = null;
        void Stop(string reason) { Interlocked.CompareExchange(ref stopReason, reason, null); operation.Cancel(); }
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; Stop("ConsoleCancel"); };
        Console.CancelKeyPress += cancel;
        FileStream? productLease = null;
        StandaloneMarketBackend? backend = null;
        MarketPort? market = null;
        HubRuntime? runtime = null;
        HubSnapshot? latest = null;
        SourceIdentity? bound = null;
        ulong? boundSelfId = null;
        int sourceMismatchObserved = 0;
        var freshSourceNotices = new System.Collections.Concurrent.ConcurrentQueue<object>();
        NavigationPort? navigation = null;
        PatrolController? controller = null;
        PatrolCheckpointStore? checkpoints = null;
        PatrolRouteSnapshot? route = null;
        int? savedRoundCount = null;
        PatrolRunResult? patrolResult = null;
        Task<PatrolRunResult>? patrolTask = null;
        ShopCommandRequest? scanRequest = null;
        ShopCommandResponse? readiness = null, refreshed = null, finalState = null;
        Task watcher = Task.CompletedTask, keys = Task.CompletedTask;
        var firstSource = new TaskCompletionSource<HubSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool actionStarted = false, scanAccepted = false, backendDisposed = false, readerStopped = false;
        bool navigationDisposed = isScan, ownedScanSettled = false, foregroundAttempted = false;
        bool? foregroundVerified = null;
        long snapshotCount = 0;
        var sourceChanges = new System.Collections.Concurrent.ConcurrentQueue<SourceIdentity>();
        SourceIdentity? previousObservedSource = null;
        int maximumObservedCompletedShops = 0;
        bool speedSamplePauseRequested = false, speedSamplePauseObserved = false;
        string? settingsSha = null;
        int pageWaitMilliseconds = 3000;
        int savedPageOpenIntervalMilliseconds = 3000;
        bool savedNoAdditionalPageWait = true;
        string operatorStopPath = Path.Combine(output, "operator-stop.requested");
        PatrolObservation? prewarmObservation = null;
        PatrolStartupIdentityGate? startupContextLatch = null;
        int startupWaiting = 0;
        try
        {
            // The real UI owns this exact lease. Never run a second coordinator beside it.
            if (!Directory.Exists(UserRoot)) throw new IOException("PRODUCT_USER_ROOT_REQUIRED");
            try { productLease = new(Path.Combine(UserRoot, "market-session-v55.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) { throw new IOException("PRODUCT_SESSION_BUSY: close the product normally before this isolated diagnostic.", error); }
            string settings = Path.Combine(UserRoot, "settings.json");
            if (File.Exists(settings))
            {
                byte[] bytes = File.ReadAllBytes(settings); settingsSha = Convert.ToHexString(SHA256.HashData(bytes));
                using var document = JsonDocument.Parse(bytes);
                pageWaitMilliseconds = Preferences.ReadPageWaitMilliseconds(document.RootElement);
                savedNoAdditionalPageWait = Preferences.ReadNoAdditionalPageWait(document.RootElement);
            }
            savedPageOpenIntervalMilliseconds = pageWaitMilliseconds;
            pageWaitMilliseconds = ShopPagePacing.ValidateMilliseconds(pageIntervalMillisecondsOverride ?? (savedNoAdditionalPageWait ? 0 : savedPageOpenIntervalMilliseconds));
            trace.Write("DIAGNOSTIC_START", new { mode, IsAdministrator = admin, LimitSeconds = limitSeconds,
                LimitIncludesPreparation = true, SoftShopStopThreshold = plan.SpeedSampleCompletedPageThreshold > 0 ? (int?)null : 2, StrictShopCountLimit = false,
                BoundedSpeedSample = plan.SpeedSampleCompletedPageThreshold > 0, plan.SpeedSampleCompletedPageThreshold, UserWaivedFullPatrol = plan.SpeedSampleCompletedPageThreshold > 0,
                StopAfterCompletedPatrolPoints = stopAfterPoints, StopOnPointCompletion = !isScan && !isFullRound, UserRoot, SettingsSha256 = settingsSha, PageOpenIntervalMilliseconds = pageWaitMilliseconds,
                SavedPageOpenIntervalMilliseconds = savedPageOpenIntervalMilliseconds,
                SavedConfiguredMs = savedPageOpenIntervalMilliseconds,
                SavedNoAdditionalPageWait = savedNoAdditionalPageWait,
                NoAdditionalPageWait = pageWaitMilliseconds == 0,
                EffectiveIntervalMilliseconds = pageWaitMilliseconds,
                EffectivePageOpenIntervalMilliseconds = pageWaitMilliseconds,
                PageOpenIntervalOverrideApplied = pageIntervalMillisecondsOverride.HasValue,
                plan.ExplicitOneRoundDiagnostic,
                RealProductionPorts = true, MockTransport = false, UiAutomation = false, GameProtectionChanged = false });
            backend = new StandaloneMarketBackend(Path.Combine(output, "state"), StandaloneMarketBackend.CreateDefaultSource,
                (rt, source, shops) =>
                {
                    Volatile.Write(ref runtime, rt); rt.SetLogging(true);
                    return new StandaloneShopCommands(new ShopBroker(rt, (CurrentLcSource)source, shops,
                        watcherStop.Token, nativeLogDirectory: Path.Combine(output, "diagnostics", "native")));
                }, diagnosticsRoot: Path.Combine(output, "diagnostics"));
            backend.ConfigurePageWaitMilliseconds(pageWaitMilliseconds);
            market = new(backend, trace);
            watcher = WatchAsync();
            keys = WatchStopKeyAsync();
            var initial = await firstSource.Task.WaitAsync(operation.Token).ConfigureAwait(false);
            bound = initial.Source ?? throw new InvalidDataException("FRESH_SOURCE_REQUIRED");
            boundSelfId = LiveStartupIdentityContext.Read(initial, DateTimeOffset.UtcNow).FreshSelfId;
            if (!isFullRound) await RequireCurrentSourceAsync().ConfigureAwait(false);
            if (!isFullRound)
            {
                readiness = await market.SendAsync(new ShopCommandRequest { Command = ShopCommands.PrepareNativeReadiness,
                    ExpectedSourceSessionKey = bound.SessionKey, WaitForNativeReadinessCompletion = false }, operation.Token).WaitAsync(operation.Token).ConfigureAwait(false);
                await RequireCurrentSourceAsync().ConfigureAwait(false);
                if (readiness.NativeReadiness is not { } ready || ready.Source != bound || !ready.ReadOnly || ready.NativeRequests != 0
                    || !ready.Fresh(DateTimeOffset.UtcNow))
                    throw new InvalidOperationException("NATIVE_PREFLIGHT_REJECTED: " + (readiness.NativeReadiness?.Code ?? readiness.FailureCode ?? readiness.State)
                        + ": " + (readiness.NativeReadiness?.Detail ?? readiness.Detail));
            }
            if (isScan)
            {
                refreshed = await market.SendAsync(new ShopCommandRequest { Command = ShopCommands.RefreshTargets,
                    ExpectedSourceSessionKey = bound.SessionKey, ForceDispatch = true }, operation.Token).WaitAsync(operation.Token).ConfigureAwait(false);
                await RequireCurrentSourceAsync().ConfigureAwait(false);
                if (refreshed.State != "TargetsRefreshed" || refreshed.TargetSnapshot is not { CanDispatch: true } targets
                    || targets.SourceSessionKey != bound.SessionKey || refreshed.CurrentSourceSessionKey != bound.SessionKey)
                    throw new InvalidDataException("FRESH_TARGET_REFRESH_REQUIRED: " + refreshed.Detail);
                scanRequest = new() { Command = ShopCommands.ScanVisible, ExpectedSourceSessionKey = bound.SessionKey,
                    TargetSnapshotId = targets.SnapshotId, ForceDispatch = true };
                // Register before awaiting acceptance. This exact request is never submitted twice.
                actionStarted = true;
                var response = await market.SendAsync(scanRequest, operation.Token).WaitAsync(operation.Token).ConfigureAwait(false);
                while (true)
                {
                    operation.Token.ThrowIfCancellationRequested();
                    if (response.Scan.JobId != scanRequest.RequestId)
                        throw new InvalidDataException("SCAN_ACCEPTANCE_NOT_OWNED: " + response.Detail);
                    scanAccepted = true;
                    maximumObservedCompletedShops = Math.Max(maximumObservedCompletedShops, response.Scan.CompletedShops);
                    finalState = response;
                    if (response.WorkSettled && !response.Scan.Running) { Stop("ScanTerminal"); break; }
                    if (plan.SpeedSampleCompletedPageThreshold > 0)
                    {
                        var decision = LiveSpeedSamplePolicy.Decide(plan.SpeedSampleCompletedPageThreshold, scanRequest, response, speedSamplePauseRequested);
                        if (decision.StopAtPausedBoundary) { speedSamplePauseObserved = true; Stop("SpeedSamplePaused"); break; }
                        if (decision.PauseRequest is { } pauseRequest)
                        {
                            speedSamplePauseRequested = true;
                            response = await market.SendAsync(pauseRequest, operation.Token).WaitAsync(operation.Token).ConfigureAwait(false);
                            continue;
                        }
                    }
                    else if (response.Scan.CompletedShops >= 2) { Stop("SoftTwoShopThreshold"); break; }
                    await RequireCurrentSourceAsync().ConfigureAwait(false);
                    await Task.Delay(250, operation.Token).ConfigureAwait(false);
                    response = await market.StateAsync(bound, operation.Token).WaitAsync(operation.Token).ConfigureAwait(false);
                }
            }
            else
            {
                string routeDirectory = Path.Combine(UserRoot, "patrol-routes");
                string routePath = Directory.EnumerateFiles(routeDirectory, "*.route.json")
                    .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal).FirstOrDefault()
                    ?? throw new InvalidDataException("SAVED_ROUTE_REQUIRED");
                var loaded = new PatrolRouteStore(UserRoot).Load();
                route = loaded.Snapshot ?? throw new InvalidDataException(loaded.Error ?? "SAVED_ROUTE_INVALID");
                PatrolRouteRules.Validate(route);
                savedRoundCount = route.RoundCount;
                if (route.Points.Length != 4 || !plan.ExplicitOneRoundDiagnostic && route.RoundCount != 1)
                    throw new InvalidDataException("EXPECTED_SAVED_FOUR_POINT_ONE_ROUND_ROUTE_REQUIRED; no route was modified.");
                // Only this explicit diagnostic gets a local round override. The
                // immutable route revision, its sidecar and user settings stay intact.
                if (plan.ExplicitOneRoundDiagnostic) route = route with { RoundCount = 1 };
                PatrolRouteRules.Validate(route);
                WriteNew(Path.Combine(output, "route-used.json"), new { Route = route, route.RoundCount,
                    SavedRoundCount = savedRoundCount, EffectiveRoundCount = route.RoundCount, plan.ExplicitOneRoundDiagnostic,
                    SourcePath = routePath, SourceSha256 = HashFile(routePath),
                    RoundSettingsPath = PatrolRoundSettings.SidecarPath(routePath, route.Revision) });
                checkpoints = new PatrolCheckpointStore(Path.Combine(output, "state"));
                void PointLimit()
                {
                    if (isFullRound) return; // Full-round mode waits for the controller's normal Completed result.
                    var saved = checkpoints.LoadLatest();
                    if (saved is not null && (saved.PointIndex >= 1 || saved.CompletedRounds >= 1)) Stop("FirstPatrolPointCompleted");
                }
                var client = new PatrolNavigationClient(Path.Combine(output, "navigation-runtime"), () => Volatile.Read(ref latest),
                    readOnly: false, report: notice => trace.Write("NAVIGATION_NOTICE", notice), forceDispatch: true);
                navigation = new(client, trace, PointLimit);
                controller = new PatrolController(navigation, market, forceDispatch: true, checkpointStore: checkpoints, autoResume: true, continuousRecovery: true, freezeShoppingTargets: true);
                controller.Progress += progress =>
                {
                    trace.Write("PATROL_PROGRESS", progress);
                    if (!isFullRound && (progress.PointIndex >= 1 || progress.CompletedRounds >= 1)) Stop("FirstPatrolPointCompleted");
                };
                if (!isFullRound) await RequireCurrentSourceAsync().ConfigureAwait(false);
                foregroundAttempted = true;
                foregroundVerified = PatrolForegroundGate.TryOnce(bound, () => Volatile.Read(ref latest)?.Source,
                    new PatrolForegroundWindow(), operation.Token);
                trace.Write("PATROL_FOREGROUND_ATTEMPT", new { AttemptLimit = 1, ForegroundVerified = foregroundVerified });
                operation.Token.ThrowIfCancellationRequested();
                if (isFullRound)
                {
                    if (boundSelfId is not { } startupSelf || startupSelf == 0)
                        throw new InvalidOperationException("DIAGNOSTIC_STARTUP_CONTEXT_REQUIRED");
                    var expected = new PatrolStartupReadinessContext(bound, startupSelf);
                    try
                    {
                        Volatile.Write(ref startupContextLatch, new(expected, () => Environment.TickCount64));
                        Volatile.Write(ref startupWaiting, 1);
                        prewarmObservation = await PatrolNavigationPrewarm.RunAsync(expected,
                            token => navigation.CaptureAsync(token), CurrentStartupContext,
                            () => DateTimeOffset.UtcNow, operation.Token, WaitForStartupFreshIdentityAsync).ConfigureAwait(false);
                        trace.Write("PATROL_NAVIGATION_PREWARM_READY",new { prewarmObservation.SampledAt,
                            HistoricalPoseOnly=true, NativeReadinessRequests=0, NavigationStartRequests=0 });
                        var admitted = await LivePatrolStartupBridge.WaitAndStartAsync(expected,
                            market.SendAsync, CurrentStartupContext,
                            () => !operation.IsCancellationRequested && patrolTask is null && market.AllRequestsCompleted,
                            () => DateTimeOffset.UtcNow, () => Environment.TickCount64,
                            (wait, token) => Task.Delay(wait, token), operation.Token,
                            () =>
                            {
                                // The policy consumes its single-use permit and invokes this
                                // real controller start in the same synchronous callback.
                                Volatile.Write(ref startupWaiting, 0);
                                actionStarted = true;
                                return patrolTask = controller.RunAsync(route, operation.Token);
                            }, response => readiness = response,
                            progress => trace.Write("DIAGNOSTIC_STARTUP_READINESS_WAIT", progress),
                            recoverContext: WaitForStartupFreshIdentityAsync).ConfigureAwait(false);
                        trace.Write("DIAGNOSTIC_STARTUP_READINESS_RESULT", new { admitted.Readiness.Ready,
                            admitted.Readiness.Code, admitted.Readiness.Attempts, admitted.Readiness.ElapsedMilliseconds,
                            admitted.CommitCode, ControllerStartedInsidePermit = admitted.Run is not null });
                        if (admitted.Run is null)
                            throw new InvalidOperationException("DIAGNOSTIC_STARTUP_NOT_ADMITTED: " + admitted.Readiness.Code + "/" + admitted.CommitCode);
                        patrolTask = admitted.Run;
                    }
                    finally { Volatile.Write(ref startupWaiting, 0); }
                }
                else
                {
                    actionStarted = true;
                    patrolTask = controller.RunAsync(route, operation.Token);
                }
                patrolResult = await patrolTask.ConfigureAwait(false);
                trace.Write("PATROL_FINISHED", patrolResult);
                if (stopReason is null) Stop("PatrolTerminal");
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { Interlocked.CompareExchange(ref stopReason, "TotalTimeLimit", null); }
        catch (Exception error) { errors.Add(error.ToString()); Stop("Failure"); }
        finally
        {
            operation.Cancel(); keyStop.Cancel();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            // Join the original accepted preparation before looking for a scan.
            // A late acceptance is reconciled with the same job, never a new ScanVisible.
            if (market is not null)
            {
                try
                {
                    await market.DrainAcceptedAsync(cleanup.Token).ConfigureAwait(false);
                    if (bound is not null)
                    {
                        finalState = await market.StateAsync(bound, cleanup.Token).WaitAsync(cleanup.Token).ConfigureAwait(false);
                        var owned = market.FindScan(finalState.Scan.JobId);
                        if (owned is not null)
                        {
                            if (isScan) scanAccepted = true;
                            maximumObservedCompletedShops = Math.Max(maximumObservedCompletedShops, finalState.Scan.CompletedShops);
                            if (finalState.Scan.Running || !finalState.WorkSettled)
                            {
                                await market.SendAsync(new ShopCommandRequest { Command = ShopCommands.CancelScan,
                                    ExpectedSourceSessionKey = owned.ExpectedSourceSessionKey, ExpectedScanJobId = owned.RequestId,
                                    TargetSnapshotId = owned.TargetSnapshotId, ExpectedScanPlanId = finalState.SavedScan?.PlanId,
                                    ForceDispatch = true }, cleanup.Token).WaitAsync(cleanup.Token).ConfigureAwait(false);
                            }
                        }
                        else if (finalState.Scan.Running) throw new InvalidDataException("UNOWNED_SCAN_NOT_CANCELLED");
                        while (!finalState.WorkSettled || finalState.Scan.Running)
                        {
                            await Task.Delay(200, cleanup.Token).ConfigureAwait(false);
                            finalState = await market.StateAsync(bound, cleanup.Token).WaitAsync(cleanup.Token).ConfigureAwait(false);
                            if (finalState.Scan.Running && market.FindScan(finalState.Scan.JobId) is null)
                                throw new InvalidDataException("SCAN_OWNERSHIP_CHANGED_DURING_CLEANUP");
                        }
                        ownedScanSettled = !finalState.SessionBlocked;
                    }
                }
                catch (Exception error) { errors.Add("Owned market settlement: " + error); }
            }
            if (navigation is not null)
            {
                try { await navigation.SettleOwnedAsync(cleanup.Token).ConfigureAwait(false); }
                catch (Exception error) { errors.Add("Owned navigation settlement: " + error); }
                try { await navigation.Inner.DisposeAsync(cleanup.Token).ConfigureAwait(false); navigationDisposed = true; }
                catch (Exception error) { errors.Add("Navigation disposal not confirmed: " + error); }
            }
            else navigationDisposed = true; // No navigation port was constructed, so no navigation child/job was ever owned.
            watcherStop.Cancel();
            try { await watcher.WaitAsync(cleanup.Token).ConfigureAwait(false); }
            catch (Exception error) { errors.Add("Snapshot watcher settlement: " + error); }
            if (backend is not null)
            {
                try { await backend.DisposeAsync().AsTask().WaitAsync(cleanup.Token).ConfigureAwait(false);
                    backendDisposed = true; readerStopped = runtime is null || !runtime.IsRunning; }
                catch (Exception error) { errors.Add("Backend disposal not confirmed: " + error); }
            }
            else { backendDisposed = true; readerStopped = true; ownedScanSettled = true; }
            try { await keys.ConfigureAwait(false); } catch (OperationCanceledException) { }
            Console.CancelKeyPress -= cancel;
            productLease?.Dispose();
        }
        trace.Dispose();
        var pageResults = ReadPageResults(Path.Combine(output, "state", "shops", "requests"), Path.Combine(output, "diagnostics", "native"), bound, errors);
        var audit = ReadAudit(Path.Combine(output, "diagnostics", "reader"), errors);
        var captures = navigation?.Captures.ToArray() ?? [];
        bool coordinateMovement = captures.Any(a => captures.Any(b => a.Source == b.Source && a.SelfId == b.SelfId
            && a.MapId == b.MapId && b.SampledAt > a.SampledAt && (a.X != b.X || a.Y != b.Y)));
        bool clean = ownedScanSettled && navigationDisposed && backendDisposed && readerStopped
            && (patrolResult is null || patrolResult.CleanupConfirmed) && (navigation is null || navigation.AllOwnedSettled);
        bool pointComplete = patrolResult?.CompletedPoints >= 1 || patrolResult?.CompletedRounds >= 1;
        bool fullPatrolVerified = patrolResult is { State: "Completed", CleanupConfirmed: true } completed
            && completed.CompletedRounds == completed.TotalRounds;
        int verifiedPages = pageResults.Count(r => r.ResponseVerified);
        int emptyObservedPages = pageResults.Count(r => r.EmptyPageObserved);
        int notEnteredPages = pageResults.Count(r => r.NotEnteredConfirmed);
        int failedPages = pageResults.Count(r => !r.ResponseVerified && !r.EmptyPageObserved && !r.NotEnteredConfirmed);
        bool userCancelled = stopReason is "UserF9" or "ConsoleCancel";
        bool pageOutcomesAccepted = LiveAutomaticPageOutcomePolicy.Accept(pageResults.Length, verifiedPages, emptyObservedPages, failedPages,
            isFullRound && fullPatrolVerified);
        bool succeeded = !userCancelled && Volatile.Read(ref sourceMismatchObserved) == 0 && errors.Count == 0 && actionStarted && clean && pageOutcomesAccepted
            && (isScan ? scanAccepted : isFullRound ? fullPatrolVerified : pointComplete && coordinateMovement);
        WriteNew(Path.Combine(output, "latest-snapshot.json"), Volatile.Read(ref latest));
        var evidence = Inventory(output, errors);
        var report = new
        {
            Version = typeof(Program).Assembly.GetName().Version?.ToString(), Mode = mode,
            Success = succeeded && errors.Count == 0, IsAdministrator = admin, StartedAt = startedAt, FinishedAt = DateTimeOffset.UtcNow,
            ElapsedSeconds = elapsed.Elapsed.TotalSeconds, ActionLimitSeconds = limitSeconds, LimitIncludesPreparation = true,
            CleanupLimitSeconds = 45, OuterCleanupLimitSeconds = 45, ControllerCleanupLimitSeconds = isScan ? 0 : 15,
            CleanupLimitScope = "The controller may first perform its own cleanup; 45 seconds bounds the following outer settlement, not total elapsed time.",
            StopReason = stopReason, UserCancelled = userCancelled, SoftShopStopThreshold = plan.SpeedSampleCompletedPageThreshold > 0 ? (int?)null : 2, StrictShopCountLimit = false,
            BoundedSpeedSample = plan.SpeedSampleCompletedPageThreshold > 0, UserWaivedFullPatrol = plan.SpeedSampleCompletedPageThreshold > 0,
            plan.SpeedSampleCompletedPageThreshold, SpeedSamplePauseRequested = speedSamplePauseRequested, SpeedSamplePauseObserved = speedSamplePauseObserved,
            SpeedSampleMinimumWarmPages = 20, SpeedSampleActualWarmPages = Math.Max(0, pageResults.Length - 1),
            SpeedSampleCoveragePassed = plan.SpeedSampleCompletedPageThreshold > 0 ? (bool?)(pageResults.Length >= 21) : null,
            MaximumObservedCompletedShops = maximumObservedCompletedShops,
            ActualDistinctOwnersWithPageResults = pageResults.Select(r => r.OwnerId).Where(id => id is not null).Distinct().Count(),
            ActualPageResults = pageResults.Length, SucceededPages = verifiedPages, EmptyObservedPages = emptyObservedPages, NotEnteredPages = notEnteredPages, FailedPages = failedPages,
            ResponseVerified = verifiedPages > 0, AllRecordedPageResponsesVerified = verifiedPages > 0 && verifiedPages == pageResults.Length,
            AllRecordedPageOutcomesAccountedFor = failedPages == 0,
            PageOutcomesAccepted = pageOutcomesAccepted,
            NoEligibleShops = isFullRound && fullPatrolVerified && pageResults.Length == 0,
            FunctionalCompletionVerified = succeeded && errors.Count == 0, EmptyObservationsAreNotMatchedResponses = true, NotEnteredPagesAreNotResponses = true,
            ActionStarted = actionStarted, ScanAccepted = scanAccepted, ScanStartAttempts = market?.ScanStarts ?? 0,
            PrepareRequest = readiness, RefreshResponse = refreshed, ScanRequest = scanRequest, FinalState = finalState,
            PatrolResult = patrolResult, RouteRevision = route?.Revision, PlannedPoints = route?.Points.Length,
            PlannedRounds = route?.RoundCount, StopAfterCompletedPatrolPoints = stopAfterPoints, StopOnPointCompletion = !isScan && !isFullRound, CompletedPatrolPointObserved = pointComplete,
            SavedRoundCount = savedRoundCount, EffectiveRoundCount = route?.RoundCount, plan.ExplicitOneRoundDiagnostic,
            BoundSource = bound, BoundSelfId = boundSelfId, SourceChanges = sourceChanges.ToArray(), SnapshotCount = snapshotCount,
            SourceMismatchObserved = Volatile.Read(ref sourceMismatchObserved) != 0, FreshSourceWaitNotices = freshSourceNotices.ToArray(),
            FreshSourceWaitSeconds = 4, FreshSourcePollMilliseconds = 100, FreshSourceGate = nameof(PatrolFreshSourceGate),
            LatestSource = Volatile.Read(ref latest)?.Source, NavigationCaptures = captures,
            NavigationStartAttempts = navigation?.StartAttempts ?? 0, CoordinateMovementObserved = coordinateMovement,
            MovementCausationVerified = false, ForegroundAttempted = foregroundAttempted, ForegroundVerified = foregroundVerified,
            CleanupConfirmed = clean, OwnedMarketWorkSettled = ownedScanSettled, NavigationCreated = navigation is not null, NavigationDisposed = navigationDisposed,
            NavigationChildExitCode = navigation?.Inner.ChildExitCode, BackendDisposed = backendDisposed, ReaderStopped = readerStopped,
            NativeAudit = audit, PageResults = pageResults, EvidenceFiles = evidence,
            SavedPageOpenIntervalMilliseconds = savedPageOpenIntervalMilliseconds,
                SavedConfiguredMs = savedPageOpenIntervalMilliseconds,
                SavedNoAdditionalPageWait = savedNoAdditionalPageWait,
                NoAdditionalPageWait = pageWaitMilliseconds == 0,
                EffectiveIntervalMilliseconds = pageWaitMilliseconds,
            EffectivePageOpenIntervalMilliseconds = pageWaitMilliseconds,
            PageOpenIntervalOverrideApplied = pageIntervalMillisecondsOverride.HasValue,
            ExecutedAssembly = typeof(LiveAutomaticVerification).Assembly.Location,
            ExecutedAssemblySha256 = HashFile(typeof(LiveAutomaticVerification).Assembly.Location),
            OperatorStopPath=operatorStopPath,
            ReadOnlyPrewarmObservation=prewarmObservation,
            NewScanAfterLostAcceptance = false, DirectKeyboardMouseInputsByHarness = 0, TradeCommands = 0,
            ProductionProofsChanged = false, MockPorts = false, UiAutomation = false,
            FullPatrolVerified = fullPatrolVerified, FullRoundRequested = isFullRound,
            ServerAcceptanceVerified = false, Errors = errors,
            Limitation = "Production APIs were exercised directly, not the UI. The action time limit stops new work and is followed by bounded cleanup; an admitted native call is not killed. The two-shop stop is a polling threshold, not a strict dispatch limit. Native return, matched rows, complete empty observations with unknown direction, coordinate observations and cleanup are separate evidence. Empty observations are not matched responses or confirmed empty stores; no server acceptance is inferred."
        };
        WriteNew(Path.Combine(output, "result.json"), report);
        return report.Success ? 0 : 1;

        PatrolStartupReadinessContext? CurrentStartupContext()
        {
            var observation = ObserveStartupIdentity();
            return Volatile.Read(ref startupContextLatch) is { } gate ? gate.Observe(observation) : observation.Context;
        }
        Task<bool> WaitForStartupFreshIdentityAsync(CancellationToken token) =>
            Volatile.Read(ref startupContextLatch) is { } gate
                ? gate.WaitFreshAsync(ObserveStartupIdentity, (wait, cancellation) => Task.Delay(wait, cancellation), token)
                : Task.FromResult(false);
        LiveStartupIdentityObservation ObserveStartupIdentity()=>
            LiveStartupIdentityContext.Read(Volatile.Read(ref latest), DateTimeOffset.UtcNow);
        async Task RequireCurrentSourceAsync()
        {
            if (bound is null || boundSelfId is null) throw new InvalidDataException("FRESH_FULL_SOURCE_BINDING_REQUIRED");
            var fresh = new PatrolFreshSourceGate(() => Volatile.Read(ref latest), report: notice =>
            {
                var evidence = new { Stage = actionStarted ? "ObserveOwnedWork" : "BeforeNewWork", Notice = notice,
                    OriginalScanRequestId = scanRequest?.RequestId, NewScanSubmitted = false };
                freshSourceNotices.Enqueue(evidence); trace.Write("DIAGNOSTIC_FRESH_SOURCE", evidence);
            });
            // The established gate permits only a bounded fresh same-source recovery;
            // a non-null changed full identity/role is rejected immediately. No old sample
            // is restored and this method never submits or repeats a shop command.
            await fresh.WaitAsync(bound, boundSelfId, operation.Token).ConfigureAwait(false);
        }
        async Task WatchAsync()
        {
            try
            {
                await foreach (var value in backend!.WatchAsync(watcherStop.Token).ConfigureAwait(false))
                {
                    Volatile.Write(ref latest, value); Interlocked.Increment(ref snapshotCount);
                    var expectedSource = bound;
                    var identity = LiveStartupIdentityContext.Read(value, DateTimeOffset.UtcNow);
                    bool changedSelf = boundSelfId is { } expectedSelf && identity.FreshSelfId is { } observedSelf
                        && observedSelf != expectedSelf;
                    if (expectedSource is not null && (value.Source is { } observedSource && observedSource != expectedSource || changedSelf))
                    {
                        Interlocked.Exchange(ref sourceMismatchObserved, 1);
                        trace.Write("DIAGNOSTIC_SOURCE_CHANGED", new { ExpectedSource = expectedSource, ObservedSource = value.Source,
                            ExpectedSelfId = boundSelfId, RoleChanged = changedSelf, NoAutomaticResend = true });
                        Stop("SourceChanged");
                    }
                    var live = new LiveContext(value, DateTimeOffset.UtcNow);
                    if (Volatile.Read(ref startupWaiting) != 0 && Volatile.Read(ref startupContextLatch) is { } startupLatch)
                    {
                        var current = startupLatch.Observe(identity);
                        if (startupLatch.Invalidated)
                        {
                            trace.Write("DIAGNOSTIC_STARTUP_CONTEXT_INVALIDATED", new { Observed = current,
                                OriginalSource = bound, OriginalSelfId = boundSelfId, NoAutomaticResend = true });
                            Stop("StartupContextChangedOrUnknown");
                        }
                    }
                    if (value.Source is not null && previousObservedSource != value.Source)
                    { sourceChanges.Enqueue(value.Source); previousObservedSource = value.Source; }
                    trace.Write("SOURCE_OBSERVATION", new { value.Sequence, value.PublishedAt, value.Source, value.State,
                        live.Valid, live.SelfId, live.SelfPosition, live.MapId, value.LogError,
                        StartupIdentityFresh=identity.Context is not null, StartupIdentitySelfId=identity.FreshSelfId });
                    if (isFullRound ? identity.Context is not null : live.Valid) firstSource.TrySetResult(value);
                }
            }
            catch (OperationCanceledException) when (watcherStop.IsCancellationRequested) { }
            catch (Exception error) { firstSource.TrySetException(error); Stop("SourceWatcherFailed"); throw; }
        }
        async Task WatchStopKeyAsync()
        {
            while (!keyStop.IsCancellationRequested)
            {
                if (File.Exists(operatorStopPath))
                {
                    trace.Write("DIAGNOSTIC_OPERATOR_STOP_FILE", new { Path=operatorStopPath,
                        NormalCancellation=true, ProcessKill=false, NativeFlightCleared=false });
                    Stop("OperatorStopFile"); return;
                }
                if ((GetAsyncKeyState(0x78) & 0x8000) != 0) { Stop("UserF9"); return; }
                await Task.Delay(100, keyStop.Token).ConfigureAwait(false);
            }
        }
    }

    private sealed class MarketPort(StandaloneMarketBackend backend, EvidenceLog trace) : IPatrolMarket
    {
        private readonly object gate = new();
        private readonly List<(ShopCommandRequest Request, Task<ShopCommandResponse> Task)> flights = [];
        internal int ScanStarts { get { lock (gate) return flights.Count(f => f.Request.Command == ShopCommands.ScanVisible); } }
        internal bool AllRequestsCompleted { get { lock (gate) return flights.All(f => f.Task.IsCompleted); } }
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            Task<ShopCommandResponse> task = Invoke();
            lock (gate) flights.Add((request, task));
            return task;
            async Task<ShopCommandResponse> Invoke()
            {
                trace.Write("COMMAND", request);
                try
                {
                    var response = await backend.SendAsync(request, token).ConfigureAwait(false);
                    object observed = request.Command == ShopCommands.GetState
                        ? new { StateSummaryOnly = true, response.RequestId, response.At, response.State, response.Detail,
                            response.CurrentSourceSessionKey, response.NativeEntered, response.NativeReturned,
                            response.ResponseVerified, response.EmptyPageObserved, response.SessionBlocked, response.WorkSettled,
                            response.FailureCode, response.FailureScope, response.FailurePhase, response.FailureRva, response.Scan,
                            ListingCount = response.Listings.Length, response.ListingsTruncated }
                        : response;
                    trace.Write("RESPONSE", observed);
                    if (response.RequestId != request.RequestId) throw new InvalidDataException("COMMAND_RESPONSE_ID_MISMATCH");
                    return response;
                }
                catch (Exception error) { trace.Write("COMMAND_ERROR", new { request.RequestId, request.Command, Error = error.ToString(), AutomaticResend = false }); throw; }
            }
        }
        internal Task<ShopCommandResponse> StateAsync(SourceIdentity source, CancellationToken token) =>
            SendAsync(new() { Command = ShopCommands.GetState, ExpectedSourceSessionKey = source.SessionKey }, token);
        internal ShopCommandRequest? FindScan(string job)
        { lock (gate) return flights.Select(f => f.Request).FirstOrDefault(r => r.RequestId == job && (r.Command is ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan)); }
        internal async Task DrainAcceptedAsync(CancellationToken token)
        {
            Task[] tasks;
            lock (gate) tasks = flights.Where(f => f.Request.Command is ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan or ShopCommands.PrepareNativeReadiness)
                .Select(f => (Task)f.Task).ToArray();
            foreach (var task in tasks)
            {
                try { await task.WaitAsync(token).ConfigureAwait(false); }
                catch (Exception) when (task.IsCompleted) { /* Failure is retained in COMMAND_ERROR; state still needs reconciliation. */ }
            }
        }
    }

    private sealed class NavigationPort(PatrolNavigationClient inner, EvidenceLog trace, Action pointLimit) : IPatrolNavigation
    {
        internal PatrolNavigationClient Inner => inner;
        internal List<PatrolObservation> Captures { get; } = [];
        private readonly Dictionary<string, (PatrolNavigationRequest Request, bool Settled)> jobs = new(StringComparer.Ordinal);
        internal int StartAttempts => jobs.Count;
        internal bool AllOwnedSettled => jobs.Values.All(value => value.Settled);
        public async Task<PatrolObservation> CaptureAsync(CancellationToken token, PatrolObservation? expected = null)
        {
            pointLimit(); token.ThrowIfCancellationRequested();
            trace.Write("NAV_CAPTURE", new { Expected = expected });
            var observed = await inner.CaptureAsync(token, expected).ConfigureAwait(false);
            Captures.Add(observed); trace.Write("NAV_OBSERVATION", observed); return observed;
        }
        public async Task<PatrolNavigationState> StartAsync(PatrolNavigationRequest request, CancellationToken token)
        {
            if (!jobs.TryAdd(request.JobId, (request, false))) throw new InvalidOperationException("NAV_START_WOULD_REPEAT");
            trace.Write("NAV_START", request);
            return Observe(await inner.StartAsync(request, token).ConfigureAwait(false), request.JobId);
        }
        public async Task<PatrolNavigationState> GetStateAsync(string jobId, CancellationToken token) =>
            Observe(await inner.GetStateAsync(jobId, token).ConfigureAwait(false), jobId);
        public async Task<PatrolNavigationState> StopAsync(string expectedJobId, CancellationToken token)
        {
            trace.Write("NAV_STOP", new { JobId = expectedJobId });
            return Observe(await inner.StopAsync(expectedJobId, token).ConfigureAwait(false), expectedJobId);
        }
        private PatrolNavigationState Observe(PatrolNavigationState state, string job)
        {
            trace.Write("NAV_STATE", state);
            if (!jobs.TryGetValue(job, out var owned) || state.JobId != job || state.WaypointId != owned.Request.Waypoint.PointId
                || state.Source != owned.Request.Expected.Source || state.SelfId != owned.Request.Expected.SelfId
                || state.MapId != owned.Request.Expected.MapId || state.TargetX != owned.Request.Waypoint.X
                || state.TargetY != owned.Request.Waypoint.Y || state.ArrivalRadius != owned.Request.ArrivalRadius)
                throw new InvalidDataException("OWNED_NAVIGATION_REPLY_MISMATCH");
            jobs[job] = (owned.Request, state.CleanupConfirmed && state.WorkSettled);
            return state;
        }
        internal async Task SettleOwnedAsync(CancellationToken token)
        {
            foreach (string job in jobs.Where(pair => !pair.Value.Settled).Select(pair => pair.Key).ToArray())
            {
                var state = await StopAsync(job, token).WaitAsync(token).ConfigureAwait(false);
                while (!state.CleanupConfirmed || !state.WorkSettled)
                { await Task.Delay(200, token).ConfigureAwait(false); state = await GetStateAsync(job, token).WaitAsync(token).ConfigureAwait(false); }
            }
        }
    }

    private sealed class EvidenceLog : IDisposable
    {
        private readonly object gate = new();
        private readonly StreamWriter writer;
        private long sequence;
        private bool disposed;
        internal EvidenceLog(string path) => writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        internal void Write(string kind, object? data)
        { lock (gate) writer.WriteLine(JsonSerializer.Serialize(new { Sequence = ++sequence, At = DateTimeOffset.UtcNow, Kind = kind, Data = data }, HubProtocol.Json)); }
        internal void Flush() { lock (gate) writer.Flush(); }
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; writer.Dispose(); } }
    }
    private sealed record PageResult(string RequestId, ulong? OwnerId, string? Direction, bool NativeEntered, bool NativeReturned,
        bool ResponseVerified, bool EmptyPageObserved, bool SessionBlocked, string? FailureCode, string State, string File, string RequestFile, bool MatchedListingIncluded, bool NotEnteredConfirmed, object? NotEnteredEvidence);
    private static PageResult[] ReadPageResults(string directory, string nativeDirectory, SourceIdentity? expectedSource, List<string> errors)
    {
        var rows = new List<PageResult>();
        if (!Directory.Exists(directory)) return [];
        foreach (string path in Directory.EnumerateFiles(directory, "*.response.json"))
        {
            try
            {
                var response = JsonSerializer.Deserialize<ShopCommandResponse>(File.ReadAllBytes(path), HubProtocol.Json);
                if (response is null || !response.RequestId.StartsWith("scan-", StringComparison.Ordinal)) continue;
                string requestPath = path[..^".response.json".Length] + ".request.json";
                using var requestDocument = JsonDocument.Parse(File.ReadAllBytes(requestPath));
                var request = Property(requestDocument.RootElement, "request").Deserialize<ShopCommandRequest>(HubProtocol.Json);
                if (request is null || request.RequestId != response.RequestId || request.Command != ShopCommands.OpenShop
                    || request.OwnerId is not > 0 || request.ViewerDirection is not (ShopPageModes.ViewerBuy or ShopPageModes.ViewerSell))
                    throw new InvalidDataException("PAGE_REQUEST_RESPONSE_IDENTITY_MISMATCH");
                string stem = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.RequestId)));
                if (!string.Equals(Path.GetFileName(path), stem + ".response.json", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("PAGE_REQUEST_FILENAME_MISMATCH");
                var matching = response.Listings.Where(listing => listing.RequestId == request.RequestId).ToArray();
                if (matching.Length > 1 || matching.Any(listing => listing.Owner.Id != request.OwnerId
                    || listing.Owner.LocalIdentity != request.ExpectedOwnerIdentity || listing.ViewerDirection != request.ViewerDirection
                    || listing.SourceSessionKey != request.ExpectedSourceSessionKey
                    || response.ResponseVerified && (!listing.OwnerAndResponseMatched || !listing.Complete)))
                    throw new InvalidDataException("PAGE_LISTING_IDENTITY_MISMATCH");
                if (response.ResponseVerified && matching.Length != 1)
                    throw new InvalidDataException("MATCHED_RESPONSE_REQUIRES_ITS_LISTING");
                bool emptyObserved = ShopEmptyPageRules.IsNeutralResponse(response)
                    && matching.Length == 1 && ShopEmptyPageRules.IsNeutralListing(matching[0])
                    && response.EmptyPageObservation is { } empty
                    && empty.RequestId == request.RequestId && empty.OwnerId == request.OwnerId
                    && empty.SourceSessionKey == request.ExpectedSourceSessionKey
                    && empty.RequestedDirection == request.ViewerDirection;
                if ((response.EmptyPageObserved || response.EmptyPageObservation is not null
                    || response.State == "CurrentOwnerEmptyPageObserved") && !emptyObserved)
                    throw new InvalidDataException("NEUTRAL_EMPTY_OBSERVATION_CONTRACT_MISMATCH");
                bool notEntered = ShopNotEnteredPageRules.IsNotEnteredResponse(response);
                object? notEnteredEvidence = null;
                if (notEntered)
                {
                    if (expectedSource is null || matching.Length != 0)
                        throw new InvalidDataException("GUARD_REJECTED_SOURCE_OR_LISTING_MISMATCH");
                    // A consumer DTO is insufficient: bind this exact request to its
                    // settled session and revalidate the full original native receipt.
                    notEnteredEvidence = ShopNativeGuardRejectedReconciliation.VerifyResolvedRequest(
                        Path.GetDirectoryName(directory)!, nativeDirectory, request.RequestId, expectedSource);
                }
                else if (response.FailureCode == "NativeGuardRejected" || response.FailurePhase == "NativeGuard")
                    throw new InvalidDataException("GUARD_REJECTED_RESPONSE_CONTRACT_MISMATCH");
                rows.Add(new(response.RequestId, request.OwnerId, request.ViewerDirection,
                    response.NativeEntered, response.NativeReturned, response.ResponseVerified, emptyObserved, response.SessionBlocked,
                    response.FailureCode, response.State, path, requestPath, matching.Length == 1, notEntered, notEnteredEvidence));
            }
            catch (Exception error) { errors.Add("Page evidence read: " + path + ": " + error.Message); }
        }
        return rows.DistinctBy(row => row.RequestId).ToArray();
    }
    private static object ReadAudit(string directory, List<string> errors)
    {
        int intents = 0, guardedIntents = 0, forceIntents = 0, ends = 0, entered = 0, returned = 0, matched = 0;
        if (Directory.Exists(directory)) foreach (string path in Directory.EnumerateFiles(directory, "hub-*.jsonl"))
        {
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                    var kindField = Property(root, "kind");
                    string? kind = kindField.ValueKind == JsonValueKind.String ? kindField.GetString() : null; var data = Property(root, "data");
                    if (kind is "SHOP_NATIVE_INTENT" or "SHOP_FORCE_NATIVE_INTENT")
                    { intents++; if (kind == "SHOP_FORCE_NATIVE_INTENT") forceIntents++; else guardedIntents++; }
                    if (kind == "SHOP_NATIVE_END")
                    {
                        ends++; var outcome = Property(data, "outcome");
                        if (Property(outcome, "entered").ValueKind == JsonValueKind.True) entered++;
                        if (Property(outcome, "returned").ValueKind == JsonValueKind.True) returned++;
                    }
                    if (kind is "SHOP_LISTING_OBSERVED" or "SHOP_FORCE_LISTING_OBSERVED")
                    { var listing = Property(data, "listing"); if (Property(listing, "ownerAndResponseMatched").ValueKind == JsonValueKind.True) matched++; }
                }
            }
            catch (Exception error) { errors.Add("Audit evidence read: " + path + ": " + error.Message); }
        }
        return new { NativeIntents = intents, GuardedNativeIntents = guardedIntents, ForceNativeIntents = forceIntents, NativeEndReceipts = ends, NativeEntered = entered, NativeReturned = returned,
            OwnerAndResponseMatchedListings = matched, ServerAcceptanceVerified = false };
    }
    private static JsonElement Property(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object
        ? item.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)).Value : default;
    private static object[] Inventory(string output, List<string> errors)
    {
        var rows = new List<object>();
        foreach (string path in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            try
            {
                var before = new FileInfo(path); long length = before.Length; var stamp = before.LastWriteTimeUtc;
                string hash = HashFile(path); var after = new FileInfo(path);
                bool stable = length == after.Length && stamp == after.LastWriteTimeUtc;
                rows.Add(new { Path = path, Bytes = length, Sha256 = hash, Stable = stable });
                if (!stable) errors.Add("EVIDENCE_CHANGED_DURING_HASH: " + path);
            }
            catch (Exception error) { errors.Add("Evidence inventory: " + path + ": " + error.Message); }
        }
        return rows.ToArray();
    }
    private static string HashFile(string path)
    { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void WriteNew(string path, object? value)
    { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); JsonSerializer.Serialize(file, value, Json); file.Flush(true); }
}
