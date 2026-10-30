using MemoryHub;
using System.Text.Json;
using System.Security.Principal;

namespace LcPlayerMarket;

internal static partial class PatrolAutoResumeSelfTest
{
    internal static async Task<OfflineCheck[]> RunContinuousRecoveryAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); checks.Add(new(name, true, "正式協調器、假port；未存取LC或送出輸入。")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        PatrolController Controller(Fixture f, IPatrolNavigation? nav = null, IPatrolMarket? market = null) =>
            new(nav ?? f.Nav, market ?? f.Market, () => f.Clock, (interval, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (interval >= TimeSpan.FromSeconds(2))
                { f.Cooldowns.Add(interval.TotalSeconds); f.OnCooldown?.Invoke(interval.TotalSeconds); }
                token.ThrowIfCancellationRequested(); return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(250), forceDispatch: true, checkpointStore: f.Store,
                autoResume: true, continuousRecovery: true);

        foreach (string code in new[] { "POSITION_CHANGING", "GAME_NOT_FOREGROUND", "DYNAMIC_PATH_BLOCKED" })
        await Check("continuous_nav_three_failures_then_same_point_" + code, async () =>
        {
            var f = new Fixture(points: 1, retries: 3);
            var nav = new RecoveryNavigation(f.Nav, 3, code);
            var result = await Controller(f, nav).RunAsync(f.Route);
            Need(result.State == "Completed" && result.CompletedPoints == 1 && nav.Requests.Count == 4
                && f.Market.Scans == 1 && f.Cooldowns.Count == 3, result.Detail);
            Need(nav.Requests.Select(r => r.JobId).Distinct().Count() == 4
                && nav.Requests.All(r => r.Waypoint.PointId == f.Route.Points[0].PointId), "重送舊ID或換到別的定位點。");
            Need(f.Cooldowns.SequenceEqual(new[] { 2d, 4d, 8d })
                && f.Cooldowns.All(seconds => seconds <= 30), "冷卻沒有有界退避。");
        });
        await Check("continuous_native_preflight_three_failures_same_plan", async () =>
        {
            var f = new Fixture(points: 1, retries: 3);
            f.Market.PreflightFailures = 3;
            var result = await Controller(f).RunAsync(f.Route);
            Need(result.State == "Completed" && f.Market.Scans == 1 && f.Market.Continues == 3
                && f.Cooldowns.Count == 3, result.Detail);
            Need(f.Market.Commands.Where(r => r.Command == ShopCommands.ContinueSavedScan)
                .All(r => r.ExpectedScanPlanId == "plan-1"), "原計畫被重建。");
        });
        await Check("continuous_readiness_three_definitive_refusals", async () =>
        {
            var f = new Fixture(points: 1, retries: 3); var market = new RefusingMarket(f.Market, 3);
            var result = await Controller(f, market: market).RunAsync(f.Route);
            Need(result.State == "Completed" && market.Refusals == 3 && f.Market.Scans == 1
                && f.Cooldowns.Count == 3, result.Detail);
        });
        await Check("continuous_same_plan_continue_refusals", async () =>
        {
            var f = new Fixture(points: 1, retries: 4); f.Market.PreflightFailures = 1;
            var market = new RefusingMarket(f.Market, 3) { ContinueOnly = true };
            var result = await Controller(f, market: market).RunAsync(f.Route);
            Need(result.State == "Completed" && f.Market.Scans == 1 && f.Market.Continues == 1
                && market.Refusals == 3 && market.RefusedPlans.All(plan => plan == "plan-1"), result.Detail);
        });
        await Check("continuous_original_scan_manual_stop_during_refusal", async () =>
        {
            var f = new Fixture(points: 1, retries: 1); f.Market.PreflightFailures = 1;
            var market = new RefusingMarket(f.Market, int.MaxValue) { ContinueOnly = true, StopOriginal = true };
            var result = await Controller(f, market: market).RunAsync(f.Route);
            Need(result.State == "Stopped" && result.CleanupConfirmed && market.Refusals == 1
                && f.Market.Continues == 0, "原工作取消卻自動接續。");
        });
        await Check("continuous_status_transport_retries_only_original_id", async () =>
        {
            var f = new Fixture(points: 1, retries: 7); var nav = new RecoveryNavigation(f.Nav, 1, "POSITION_CHANGING") { StatusFailures = 6 };
            var result = await Controller(f, nav).RunAsync(f.Route);
            Need(result.State == "Completed" && nav.Requests.Count == 2 && nav.StatusIds.Count == 7
                && nav.StatusIds.All(id => id == nav.Requests[0].JobId), "狀態讀取短暫失敗後重送了Start或換job。");
        });
        await Check("continuous_manual_stop_on_second_navigation_receipt", async () =>
        {
            var f = new Fixture(points: 1); var nav = new RecoveryNavigation(f.Nav, 1, "POSITION_CHANGING") { StopOnStatus = true };
            var result = await Controller(f, nav).RunAsync(f.Route);
            Need(result.State == "Stopped" && nav.Requests.Count == 1 && f.Market.Scans == 0, "重核終態的停止被忽略。");
        });
        await Check("continuous_cancel_after_two_cycles_does_not_restart", async () =>
        {
            using var stop = new CancellationTokenSource(); var f = new Fixture(points: 1, retries: 10);
            var nav = new RecoveryNavigation(f.Nav, int.MaxValue, "POSITION_CHANGING");
            f.OnCooldown = _ => { if (f.Cooldowns.Count == 2) stop.Cancel(); };
            var result = await Controller(f, nav).RunAsync(f.Route, stop.Token);
            Need(result.State == "Stopped" && result.CleanupConfirmed && nav.Requests.Count == 2
                && f.Market.Scans == 0, result.Detail);
        });
        foreach (string code in new[] { "SOURCE_CHANGED", "CONTROL_LATCHED", "CONTROL_UNCONFIRMED", "RELEASE_UNCONFIRMED", "UNKNOWN_FRAME" })
        await Check("continuous_unknown_or_unsafe_not_retried_" + code, async () =>
        {
            var f = new Fixture(points: 1); var nav = new RecoveryNavigation(f.Nav, int.MaxValue, code);
            var result = await Controller(f, nav).RunAsync(f.Route);
            Need(result.State is "Failed" or "Blocked" && nav.Requests.Count == 1 && f.Cooldowns.Count == 0,
                "未知／危險結果取得重試資格。");
        });
        foreach (string mutation in new[] { "cleanup", "userStop", "source" })
        await Check("continuous_navigation_proof_" + mutation + "_not_retried", async () =>
        {
            var f = new Fixture(points: 1); var nav = new RecoveryNavigation(f.Nav, int.MaxValue, "POSITION_CHANGING") { Mutation = mutation };
            var result = await Controller(f, nav).RunAsync(f.Route);
            Need(result.State != "Completed" && nav.Requests.Count == 1 && f.Cooldowns.Count == 0,
                "清理／停止／來源不符卻重啟。");
        });
        foreach (string mutation in new[] { "session", "entered", "readOnly", "source", "code" })
        await Check("continuous_readiness_invalid_" + mutation + "_not_retried", async () =>
        {
            var f = new Fixture(points: 1); var market = new RefusingMarket(f.Market, int.MaxValue) { Mutation = mutation };
            var result = await Controller(f, market: market).RunAsync(f.Route);
            Need(result.State != "Completed" && market.Refusals == 1 && f.Cooldowns.Count == 0,
                "不完整未受理證據被重試。");
        });
        await Check("continuous_post_cleanup_transient_only", () =>
        {
            var state = new PatrolNavigationState { State = "Failed", Terminal = true, CleanupConfirmed = true,
                WorkSettled = true, Error = "POST_CLEANUP_POSITION_CHANGING" };
            Need(PatrolController.RecoverableNavigationCode(state) == "POSITION_CHANGING", "暫態未辨識。");
            Need(PatrolController.RecoverableNavigationCode(state with { Error = "POST_CLEANUP_SOURCE_CHANGED" }) is null
                && PatrolController.RecoverableNavigationCode(state with { WorkSettled = false }) is null,
                "錯來源或未清理卻被接受。");
            return Task.CompletedTask;
        });
        return checks.ToArray();
    }

    private sealed class RecoveryNavigation(FakeNavigation inner, int failures, string code) : IPatrolNavigation
    {
        internal readonly List<PatrolNavigationRequest> Requests = [];
        internal string? Mutation;
        internal int StatusFailures;
        internal bool StopOnStatus;
        internal readonly List<string> StatusIds = [];
        private readonly Dictionary<string, PatrolNavigationState> states = new();
        public Task<PatrolObservation> CaptureAsync(CancellationToken token, PatrolObservation? expected = null) => inner.CaptureAsync(token, expected);
        public async Task<PatrolNavigationState> StartAsync(PatrolNavigationRequest request, CancellationToken token)
        {
            Requests.Add(request);
            var normal = await inner.StartAsync(request, token);
            var state = Requests.Count <= failures ? normal with { State = "Failed", Completed = false, Error = code } : normal;
            if (Mutation == "cleanup") state = state with { CleanupConfirmed = false, WorkSettled = false };
            if (Mutation == "userStop") state = state with { UserStop = true, StopRequested = true };
            if (Mutation == "source") state = state with { Source = Source with { SessionKey = "other" } };
            states.Add(request.JobId, state); return state;
        }
        public Task<PatrolNavigationState> GetStateAsync(string jobId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); StatusIds.Add(jobId);
            if (StatusFailures > 0) { StatusFailures--; throw new IOException("offline status transport gap"); }
            return Task.FromResult(StopOnStatus ? states[jobId] with { UserStop = true, StopRequested = true } : states[jobId]);
        }
        public Task<PatrolNavigationState> StopAsync(string jobId, CancellationToken token) => GetStateAsync(jobId, token);
    }

    private sealed class RefusingMarket(FakeMarket inner, int failures) : IPatrolMarket
    {
        internal int Refusals;
        internal string? Mutation;
        internal bool ContinueOnly, StopOriginal;
        internal readonly List<string?> RefusedPlans = [];
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.Command == (ContinueOnly ? ShopCommands.ContinueSavedScan : ShopCommands.ScanVisible) && Refusals < failures)
            {
                Refusals++; RefusedPlans.Add(request.ExpectedScanPlanId);
                var readiness = new ShopNativeReadiness { BuildCompatible = true, Source = Source, ProbeId = "probe-" + Refusals,
                    Code = "NativeCodeUnavailable", Ready = false, ReadOnly = true, NativeRequests = 0 };
                var value = (ContinueOnly ? inner.Current : new ShopCommandResponse()) with { RequestId = request.RequestId, State = "Rejected", WorkSettled = true,
                    CurrentSourceSessionKey = Source.SessionKey, FailureCode = readiness.Code,
                    FailureScope = "Global", FailurePhase = "BeforePublish", NativeReadiness = readiness };
                if (StopOriginal) value = value with { Scan = value.Scan with { StopReason = "Cancelled" } };
                if (Mutation == "session") value = value with { SessionBlocked = true };
                if (Mutation == "entered") value = value with { NativeEntered = true };
                if (Mutation == "readOnly") value = value with { NativeReadiness = readiness with { ReadOnly = false } };
                if (Mutation == "source") value = value with { NativeReadiness = readiness with { Source = Source with { SessionKey = "other" } } };
                if (Mutation == "code") value = value with { FailureCode = "NativeCodeMismatch", NativeReadiness = readiness with { Code = "NativeCodeMismatch" } };
                return Task.FromResult(value);
            }
            return inner.SendAsync(request, token);
        }
    }
}

internal static class ContinuousRecoveryVerification
{
    internal static OfflineCheck[] Run() => PatrolAutoResumeSelfTest.RunContinuousRecoveryAsync().GetAwaiter().GetResult()
        .Concat(PatrolAutoResumeSelfTest.RunBoundedRecoveryAsync().GetAwaiter().GetResult()).ToArray();
    internal static int RunReport(string output)
    {
        var checks = Run();
        var report = new { Success = checks.All(c => c.Passed),
            IsAdministrator = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            Passed = checks.Count(c => c.Passed), Failed = checks.Count(c => !c.Passed), Checks = checks,
            NativeCalls = 0, GameInput = 0, LcRead = 0 };
        using var stream = new FileStream(output, FileMode.CreateNew);
        JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true });
        return report.Success ? 0 : 1;
    }
}
