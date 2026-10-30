using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal static partial class PatrolAutoResumeSelfTest
{
    internal static async Task<OfflineCheck[]> RunNativeCodeStopAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); checks.Add(new(name, true, "真實PatrolController與既有fake ports；無LC或原生呼叫。")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        foreach (bool auto in new[] { true, false })
        foreach (int retry in new[] { 0, 10 })
            await Check($"native_code_global_first_settled_failure_stops_auto_{auto}_retry_{retry}", async () =>
            {
                var f = new Fixture(points: 1, retries: retry);
                f.Market.PreflightFailures = 1; f.Market.RemainingOnScan = _ => 1;
                var controller = auto ? f.Controller : new PatrolController(f.Nav, f.Market, () => f.Clock,
                    (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, forceDispatch: true);
                var result = await controller.RunAsync(f.Route);
                Need(result.State == "Failed" && result.CleanupConfirmed && result.CompletedPoints == 0 && result.RetriesUsed == 0, result.Detail);
                Need(f.Market.Scans == 1 && f.Market.Continues == 0 && f.Market.Refreshes == 1 && f.Cooldowns.Count == 0,
                    "全域原生頁失敗後仍有自動refresh/Continue/cooldown。");
                foreach (string expected in new[] { "NativeCodeUnavailable", "Global", "Preflight", "0x76BEB0", "PAGE_NOACCESS", "手動" })
                    Need(result.Detail.Contains(expected), "原始錯誤或手動操作說明遺失：" + expected);
            });
        await Check("native_code_stopped_same_controller_manual_start_uses_new_continue_same_plan", async () =>
        {
            var f = new Fixture(points: 1, retries: 10); f.Market.PreflightFailures = 1; f.Market.RemainingOnScan = _ => 1;
            var first = await f.Controller.RunAsync(f.Route);
            var saved = f.Store.LoadLatest()!;
            Need(first.State == "Failed" && saved.ScanSettled && saved.PlanId == "plan-1" && saved.ScanRequest is not null, first.Detail);
            string originalId = saved.ScanRequest!.RequestId;
            int commandsAtStop = f.Market.Commands.Count;
            var second = await f.Controller.RunAsync(f.Route);
            var next = f.Market.Commands.Skip(commandsAtStop).Where(r => r.Command is ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan).ToArray();
            Need(second.State == "Completed" && f.Market.Scans == 1 && f.Market.Continues == 1 && next.Length == 1,
                "手動新Start被舊失敗永久封鎖，或重建/重送了舊計畫。");
            Need(next[0].Command == ShopCommands.ContinueSavedScan && next[0].ExpectedScanPlanId == "plan-1" && next[0].RequestId != originalId,
                "手動Start沒有使用同plan的新請求ID。");
        });
        await Check("native_code_original_registered_unsettled_branch_checks_before_refresh", async () =>
        {
            var f = new Fixture(points: 1, retries: 10);
            var request = f.Market.PrepareUnknownAcceptedScan();
            f.Market.SetRecordedNativeCodeFailure();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type runType = typeof(PatrolController).GetNestedType("RunState", BindingFlags.NonPublic)!;
            object run = Activator.CreateInstance(runType, flags, null, [Guid.NewGuid().ToString("N")], null)!;
            void Set(string name, object? value) => runType.GetField(name, flags)!.SetValue(run,value);
            Set("Origin",f.Checkpoint().Origin); Set("Route",f.Route); Set("Point",f.Route.Points[0]);
            Set("ScanRequest",request); Set("ScanSettled",false); Set("PlanId",null);
            bool stopped = false;
            try { await (Task)typeof(PatrolController).GetMethod("SearchPointAsync",flags)!.Invoke(f.Controller,[run,CancellationToken.None])!; }
            catch (InvalidOperationException error) when (error.Message.Contains("NativeCodeUnavailable")) { stopped = true; }
            Need(stopped && f.Market.StateReads == 1 && f.Market.Refreshes == 0 && f.Market.Scans == 0 && f.Market.Continues == 0,
                "追蹤原請求分支在第一筆已結清全域失敗後仍重新刷新/送出。");
        });
        await Check("native_code_waits_for_original_worker_settlement_before_terminal_failure", async () =>
        {
            var f = new Fixture(points: 1); f.Market.PreflightFailures = 1; f.Market.RemainingOnScan = _ => 1;
            f.Market.WorkerPollsOnScan = 2;
            var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Failed" && result.CleanupConfirmed && f.Market.StateReads == 2 && f.Market.Continues == 0,
                "尚未結清就宣稱停止，或結清後仍繼續。");
        });
        await Check("native_code_predicate_does_not_upgrade_wrong_or_incomplete_metadata", () =>
        {
            var good = new ShopCommandResponse { WorkSettled = true, Scan = new() { Running = false,
                FailureCode = "NativeCodeUnavailable", FailureScope = "Global", FailurePhase = "Preflight" } };
            Need(PatrolController.IsSettledGlobalNativeCodeFailure(good), "完整全域失敗未辨識。");
            foreach (string? scope in new string?[] { null, "Page", "Session", "Unknown" })
                Need(!PatrolController.IsSettledGlobalNativeCodeFailure(good with { Scan = good.Scan with { FailureScope = scope } }), "錯scope被升格。");
            foreach (string? phase in new string?[] { null, "BeforePublish", "Native", "Unknown" })
                Need(!PatrolController.IsSettledGlobalNativeCodeFailure(good with { Scan = good.Scan with { FailurePhase = phase } }), "錯phase被升格。");
            Need(!PatrolController.IsSettledGlobalNativeCodeFailure(good with { WorkSettled = false })
                && !PatrolController.IsSettledGlobalNativeCodeFailure(good with { Scan = good.Scan with { Running = true } })
                && !PatrolController.IsSettledGlobalNativeCodeFailure(good with { Scan = good.Scan with { FailureCode = "NativePreflightReadUnavailable" } }),
                "尚未結清或其他暫時讀取錯誤被新規則冒稱完整全域失敗。");
            return Task.CompletedTask;
        });
        await Check("native_code_invalid_retry_twenty_still_rejected_before_work", async () =>
        {
            var f = new Fixture(points: 1, retries: 20); var result = await f.Controller.RunAsync(f.Route);
            Need(result.State == "Failed" && f.Market.Scans == 0 && f.Market.Continues == 0 && f.Nav.Starts.Count == 0,
                "非法retry20被本次修正放寬。");
        });
        return checks.ToArray();
    }

    internal static int RunNativeCodeStopReport(string output)
    {
        var checks = RunNativeCodeStopAsync().GetAwaiter().GetResult()
            .Concat(RunAsync().GetAwaiter().GetResult())
            .Concat(PatrolControllerSelfTest.RunTerminalFailureAsync().GetAwaiter().GetResult()).ToArray();
        string assembly = typeof(PatrolController).Assembly.Location;
        var report = new { Success=checks.All(c=>c.Passed), IsAdministrator=new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),
            Passed=checks.Count(c=>c.Passed), Failed=checks.Count(c=>!c.Passed), Checks=checks, TestedAssembly=assembly,
            TestedAssemblySHA256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))), LCRead=0,GameInput=0,NativeCalls=0 };
        using(var stream=new FileStream(output,FileMode.CreateNew)) JsonSerializer.Serialize(stream,report,new JsonSerializerOptions { WriteIndented=true });
        return report.Success ? 0 : 1;
    }
}
