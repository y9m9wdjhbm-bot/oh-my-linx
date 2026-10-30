using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

// The real HubRuntime publishes fake source readings, then the real MainForm
// performs startup through its normal backend and real controller. No LC ports.
internal static class PatrolStartupMainFormVerification
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    internal static int RunReport(string output)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("ADMIN_REQUIRED");
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("STA_REQUIRED");
        string fullOutput = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        string fixtures = fullOutput + ".fixtures";
        if (File.Exists(fullOutput) || Directory.Exists(fixtures)) throw new IOException("New fixture output required.");
        Directory.CreateDirectory(fixtures);
        nint foreground = GetForegroundWindow();
        var checks = new List<OfflineCheck>();
        var evidence = new List<object>();
        void Check(string name, bool cold, int actualMap, bool stopDuringWait)
        {
            try
            {
                using var f = new Fixture(Path.Combine(fixtures, checks.Count.ToString("00")), cold, actualMap);
                PumpUntil(() => f.LastSnapshot is { } s && new LiveContext(s, DateTimeOffset.UtcNow).Valid,
                    "Production HubRuntime did not publish the fake source readings.");
                var snapshot = f.LastSnapshot!;
                Need(!snapshot.Fields.ContainsKey("mapId"), "Fixture must not invent the missing numeric map field.");
                Need(new[] { "actorViewport", "mapName", "nearbyPlayers", "personalShopListing", "playerShopStates", "position", "selfId" }
                    .All(snapshot.Fields.ContainsKey), "Producer-shaped fixture lost one of the seven selected fields.");
                var context = Call(f.Form, "CurrentPatrolStartupContext");
                Need(context is PatrolStartupReadinessContext { SelfId: 11 },
                    "Actual MainForm extraction refused a valid producer-shaped snapshot without mapId.");
                Task run = (Task)Call(f.Form, "RunPatrolAsync", f.Route, true)!;
                Task? stop = null;
                if (stopDuringWait)
                {
                    PumpUntil(() => f.Commands.ReadinessChecks == 1 && f.Commands.Requests.Any(r => r.Command == ShopCommands.GetState),
                        "First readonly refusal did not finish before stop.");
                    stop = (Task)Call(f.Form, "StopPatrolAsync")!;
                }
                PumpUntil(() => run.IsCompleted && (stop is null || stop.IsCompleted), "Actual startup did not settle within the fixture bound.");
                run.GetAwaiter().GetResult(); stop?.GetAwaiter().GetResult();
                var requests = f.Commands.Requests.ToArray();
                var readiness = requests.Where(r => r.Command is ShopCommands.PrepareNativeReadiness or ShopCommands.CheckNativeReadiness).ToArray();
                Need(readiness.Length >= 1 && readiness[0].Command == ShopCommands.PrepareNativeReadiness,
                    "Real MainForm did not enter the original prepare command.");
                Need(readiness.Skip(1).All(r => r.Command == ShopCommands.CheckNativeReadiness), "Retry unexpectedly prepared pending work again.");
                Need(readiness.All(r => r.WaitForNativeReadinessCompletion),
                    "Real continuous MainForm startup did not request ownership waiting for its original probe.");
                Need(requests.All(r => r.Command is ShopCommands.PrepareNativeReadiness or ShopCommands.CheckNativeReadiness or ShopCommands.GetState),
                    "Fixture unexpectedly submitted a market action.");
                Task<PatrolRunResult>? controllerTask = Get<Task<PatrolRunResult>?>(f.Form, "patrolTask");
                if (stopDuringWait)
                {
                    Need(readiness.Length == 1 && f.Navigation.Captures == 0 && f.Navigation.Starts == 0 && controllerTask is null,
                        "Stopping the real cold wait must prevent controller startup.");
                }
                else
                {
                    Need(controllerTask is { IsCompletedSuccessfully: true } && f.Navigation.Captures > 0,
                        "The real MainForm never invoked the real controller.");
                    var result = controllerTask!.Result;
                    if (actualMap != 4)
                    {
                        Need(f.Navigation.Starts == 0 && (result.State is "Failed" or "Blocked") && result.CleanupConfirmed,
                            "Existing controller numeric-map guard did not reject before navigation.");
                    }
                    else
                    {
                        Need(f.Navigation.Starts == 1 && result.State == "Stopped" && result.CleanupConfirmed,
                            "Correct-map startup did not reach the fake navigation port and stop cleanly.");
                        Need(readiness.Length == (cold ? 2 : 1), "Readiness sequence did not match immediate versus cold recovery.");
                    }
                    Need(Get<TaskCompletionSource<bool>>(f.Form, "patrolStartCompletion").Task.Result,
                        "Production start completion did not acknowledge controller invocation.");
                }
                Need(!Get<bool>(f.Form, "patrolRunning") && !Get<bool>(f.Form, "patrolBlocked"), "Startup did not release its UI ownership.");
                Need(!f.Form.Visible && !f.Form.IsHandleCreated && GetForegroundWindow() == foreground,
                    "Offline startup fixture unexpectedly displayed/focused a window.");
                Need(f.Commands.ForbiddenCalls == 0, "A forbidden command reached the fake port.");
                evidence.Add(new { Name = name, Producer = "Actual HubRuntime over ISlicedMemorySource fixture", Fields = snapshot.Fields.Keys.Order().ToArray(),
                    NumericMapFieldPresent = false, RealMainFormContextAccepted = true, Commands = requests.Select(r => r.Command).ToArray(),
                    ActualNumericMap = actualMap, RouteNumericMap = 4, ControllerCaptures = f.Navigation.Captures,
                    FakeNavigationStarts = f.Navigation.Starts, ControllerResult = controllerTask?.Result.State,
                    RealNavigationOrNativeCalls = 0, IsLiveSnapshot = false });
                checks.Add(new(name, true, "Real MainForm/HubRuntime/controller; all source, commands and navigation ports are offline fakes."));
            }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        Check("Producer snapshot without mapId starts immediately", false, 4, false);
        Check("Real MainForm cold prepare then check starts once", true, 4, false);
        Check("Real controller still rejects different actual numeric map", false, 5, false);
        Check("Real MainForm stop during cold wait prevents startup", true, 4, true);
        bool sameForeground = GetForegroundWindow() == foreground;
        var report = new { Version = 88, Success = checks.All(c => c.Passed) && sameForeground,
            IsAdministrator = true, Passed = checks.Count(c => c.Passed), Failed = checks.Count(c => !c.Passed), Checks = checks,
            Evidence = evidence, ForegroundUnchanged = sameForeground, LcReads = 0, NativeCalls = 0, GameInputs = 0,
            FixtureDataOnly = true, LiveGameOrServerAcceptanceVerified = false };
        using var file = new FileStream(fullOutput, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, new JsonSerializerOptions { WriteIndented = true });
        return report.Success ? 0 : 1;
    }

    private static void PumpUntil(Func<bool> condition, string detail)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(8)) { Application.DoEvents(); Thread.Sleep(5); }
        Need(condition(), detail);
    }
    private static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    private static FieldInfo Field(string name) => typeof(MainForm).GetField(name, Hidden) ?? throw new MissingFieldException(name);
    private static T Get<T>(MainForm form, string name) => (T)Field(name).GetValue(form)!;
    private static object? Call(MainForm form, string name, params object?[] args)
        => (typeof(MainForm).GetMethod(name, Hidden) ?? throw new MissingMethodException(name)).Invoke(form, args);

    private sealed class Fixture : IDisposable
    {
        internal static readonly SourceIdentity Source = new(900001, 123456, @"C:\offline\LC.exe", "0x140000000",
            1793101316, 66609152, "B47CD077B4250FAC772F264A748DB75DC8C0075DDCC2ED475CC5F247CC9945FC", "offline-mainform-startup");
        internal readonly MainForm Form;
        internal readonly Commands Commands;
        internal readonly Navigation Navigation;
        internal readonly PatrolRouteSnapshot Route;
        private readonly StandaloneMarketBackend backend;
        private readonly CancellationTokenSource watchCancellation = new();
        private readonly Task watch;
        private HubSnapshot? lastSnapshot;
        internal HubSnapshot? LastSnapshot => Volatile.Read(ref lastSnapshot);
        internal Fixture(string directory, bool cold, int actualMap)
        {
            Directory.CreateDirectory(directory);
            Commands = new(cold);
            backend = new(Path.Combine(directory, "backend"), _ => new ReaderSource(), (_, _, _) => Commands);
            var store = new LocalStore(Path.Combine(directory, "state"));
            store.SavePreferences(new() { LogEnabled = true, ShowNavigation = false, ShowSearchOverlay = false });
            var routes = new PatrolRouteStore(store.Root);
            Need(routes.TrySave([new("11111111-2222-3333-4444-555555555555", "offline point", "4", "offline map", 32780, 32768)], 1, 2, out var saved, out var error), error ?? "Route save failed.");
            Route = saved!;
            Form = new(store, backend);
            Navigation = new(actualMap, () => Get<CancellationTokenSource>(Form, "patrolCancellation").Cancel());
            var controller = new PatrolController(Navigation, Commands, forceDispatch: true,
                checkpointStore: new PatrolCheckpointStore(store.Root), autoResume: true, continuousRecovery: true);
            Field("patrolController").SetValue(Form, controller);
            watch = Task.Run(async () =>
            {
                try
                {
                    await foreach (var snapshot in backend.WatchAsync(watchCancellation.Token))
                    { Field("snapshot").SetValue(Form, snapshot); Volatile.Write(ref lastSnapshot, snapshot); }
                }
                catch (OperationCanceledException) when (watchCancellation.IsCancellationRequested) { }
            });
        }
        public void Dispose()
        {
            if (Field("patrolCancellation").GetValue(Form) is CancellationTokenSource cancellation) cancellation.Cancel();
            watchCancellation.Cancel(); watch.GetAwaiter().GetResult();
            if (Field("patrolTask").GetValue(Form) is Task task)
                PumpUntil(() => task.IsCompleted, "Fake controller did not settle on disposal.");
            Form.Dispose(); watchCancellation.Dispose();
        }
        private sealed class ReaderSource : ISlicedMemorySource
        {
            public void Configure(ReadSelection selection) { }
            public ReadResult Read(CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); var now = DateTimeOffset.UtcNow;
                return new(Fixture.Source, new()
                {
                    ["actorViewport"] = FieldReading.Unknown("offline fixture"),
                    ["mapName"] = FieldReading.Observed("offline map", now),
                    ["nearbyPlayers"] = FieldReading.Unknown("offline fixture"),
                    ["personalShopListing"] = FieldReading.Unknown("offline fixture"),
                    ["playerShopStates"] = FieldReading.Unknown("offline fixture"),
                    ["position"] = FieldReading.Observed(new { x = 32768, y = 32768 }, now),
                    ["selfId"] = FieldReading.Observed(11UL, now)
                }, "Reading", "Offline producer fixture; no process enumeration or memory access.");
            }
            public ReadResult ReadSlice(ReadSelection fields, CancellationToken token) => Read(token);
            public void Dispose() { }
        }
    }

    private sealed class Commands(bool coldFirst) : IStandaloneShopCommands, IPatrolMarket
    {
        internal readonly ConcurrentQueue<ShopCommandRequest> Requests = new();
        internal int ReadinessChecks, ForbiddenCalls;
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Enqueue(request); var now = DateTimeOffset.UtcNow;
            if (request.Command == ShopCommands.GetState)
                return Task.FromResult(new ShopCommandResponse { RequestId = request.RequestId, State = "State", At = now,
                    CurrentSourceSessionKey = Fixture.Source.SessionKey, WorkSettled = true });
            if (request.Command is not (ShopCommands.PrepareNativeReadiness or ShopCommands.CheckNativeReadiness))
            { Interlocked.Increment(ref ForbiddenCalls); throw new InvalidOperationException("Offline fixture forbids market action: " + request.Command); }
            bool ready = !coldFirst || Interlocked.Increment(ref ReadinessChecks) > 1;
            if (!coldFirst) Interlocked.Increment(ref ReadinessChecks);
            var proof = new ShopNativeReadiness
            {
                BuildCompatible = true, Ready = ready, Code = ready ? "NativeReady" : "NativeCodeUnavailable",
                FailureRva = ready ? null : "0x7710F0", Source = Fixture.Source, ReadOnly = true, NativeRequests = 0, PendingMask = 0,
                ProbeId = "offline-mainform-" + request.RequestId, SampledAt = now, ExpiresAt = now.AddMilliseconds(HubProtocol.FreshMilliseconds),
                CurrentRuntimeCodeVerified = ready, EvidenceMode = "CurrentRuntimeCode",
                ColdCodePolicyId = ShopNativeCurrentB47CPolicy.Id, ColdCodeCertificateSha256 = ShopNativeCurrentB47CPolicy.CertificateSha256
            };
            return Task.FromResult(new ShopCommandResponse { RequestId = request.RequestId, State = ready ? "NativeReady" : "Rejected", At = now,
                CurrentSourceSessionKey = Fixture.Source.SessionKey, NativeReadiness = proof, WorkSettled = false,
                FailureCode = ready ? null : "NativeCodeUnavailable", FailureScope = ready ? null : "Global",
                FailurePhase = ready ? null : "BeforePublish", FailureRva = ready ? null : "0x7710F0" });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Navigation(int map, Action stop) : IPatrolNavigation
    {
        internal int Captures, Starts;
        private PatrolNavigationRequest? owned;
        public Task<PatrolObservation> CaptureAsync(CancellationToken token, PatrolObservation? expected = null)
        { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Captures); return Task.FromResult(new PatrolObservation(Fixture.Source, 11, map, 32768, 32768, DateTimeOffset.UtcNow, true)); }
        public Task<PatrolNavigationState> StartAsync(PatrolNavigationRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); owned = request; Interlocked.Increment(ref Starts); stop(); return Task.FromCanceled<PatrolNavigationState>(token); }
        public Task<PatrolNavigationState> GetStateAsync(string jobId, CancellationToken token) => Task.FromResult(Settled(jobId));
        public Task<PatrolNavigationState> StopAsync(string expectedJobId, CancellationToken token) => Task.FromResult(Settled(expectedJobId));
        private PatrolNavigationState Settled(string jobId) => new()
        {
            JobId = jobId, WaypointId = owned?.Waypoint.PointId ?? "11111111-2222-3333-4444-555555555555", TargetX = owned?.Waypoint.X ?? 32780,
            TargetY = owned?.Waypoint.Y ?? 32768, ArrivalRadius = owned?.ArrivalRadius ?? 1,
            Source = Fixture.Source, SelfId = 11, MapId = map, X = 32768, Y = 32768, SampledAt = DateTimeOffset.UtcNow,
            State = "Stopped", Terminal = true, CleanupConfirmed = true, WorkSettled = true, Standing = true, PositionVerified = true
        };
    }
}
