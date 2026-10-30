using System.Security.Cryptography;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Fake owned-process lifecycles plus isolated durable stores; no process is started.</summary>
internal static class NativeHelperFlightVerification
{
    internal static int RunReport(string output)
    {
        var checks = Run();
        var report = new OfflineCheckReport("83.0.0", checks.All(c => c.Passed),
            checks.Count(c => c.Passed), checks.Count(c => !c.Passed), false, false, false, checks);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, HubProtocol.Json); file.Flush(true);
        return report.Success ? 0 : 1;
    }

    internal static OfflineCheck[] Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new("native_helper_flight_" + name, true,
                "SYNTHETIC process lifecycle only; no helper launch, LC read, native dispatch or historical recovery.")); }
            catch (Exception error) { checks.Add(new("native_helper_flight_" + name, false, error.ToString())); }
        }
        static void Need(bool pass, string detail) { if (!pass) throw new InvalidOperationException(detail); }
        static async Task<T> Reject<T>(Func<Task> action) where T : Exception
        {
            try { await action().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (T error) { return error; }
            throw new InvalidOperationException("Expected " + typeof(T).Name);
        }
        static Task Within(Task task) => task.WaitAsync(TimeSpan.FromSeconds(2));

        await Check("timeout_keeps_flight_and_handle_until_real_exit", async () =>
        {
            var tracker = new ShopNativeHelperFlight(); var process = new FakeProcess(); int starts = 0;
            var original = tracker.RunAsync(() => { starts++; return process; }, TimeSpan.Zero);
            await Reject<ShopNativeHelperWaitTimeoutException>(() => original);
            Need(tracker.IsActive && starts == 1 && process.WaitCalls == 1 && process.DisposeCalls == 0,
                "逾時不得釋放flight或dispose仍活著的helper");
            await Reject<ShopNativeHelperBusyException>(() => { tracker.RequireAvailable(); return Task.CompletedTask; });
            await Reject<ShopNativeHelperBusyException>(() => tracker.RunAsync(() => { starts++; return new FakeProcess(); }, TimeSpan.Zero));
            Need(starts == 1, "拒絕第二頁前已啟動另一個helper");
            Task shutdownJoin = tracker.WaitUntilExitAsync();
            Need(!shutdownJoin.IsCompleted, "關閉流程把timeout当exit");
            process.Exit(7); await Within(shutdownJoin);
            Need(!tracker.IsActive && process.DisposeCalls == 1 && starts == 1, "原程序exit後flight未正確釋放");
            tracker.RequireAvailable();
            await Reject<ShopNativeHelperWaitTimeoutException>(() => original);
        });

        await Check("late_exit_only_opens_transport_and_never_replays_original", async () =>
        {
            var tracker = new ShopNativeHelperFlight(); var first = new FakeProcess(); int firstStarts = 0, nextStarts = 0;
            var original = tracker.RunAsync(() => { firstStarts++; return first; }, TimeSpan.Zero);
            await Reject<ShopNativeHelperWaitTimeoutException>(() => original);
            Task join = tracker.WaitUntilExitAsync(); first.Exit(1); await Within(join);
            var next = new FakeProcess(); next.Exit(0);
            int exit = await tracker.RunAsync(() => { nextStarts++; return next; }, TimeSpan.FromSeconds(1));
            Need(exit == 0 && firstStarts == 1 && nextStarts == 1 && first.DisposeCalls == 1 && next.DisposeCalls == 1,
                "已退出後的新transport呼叫錯誤或原請求被重播");
            Need(original.IsFaulted && original.Exception!.InnerException is ShopNativeHelperWaitTimeoutException,
                "late exit不應將原先不明結果改成成功");
        });

        await Check("multiple_waiters_cannot_release_or_cancel_observer", async () =>
        {
            var tracker = new ShopNativeHelperFlight(); var process = new FakeProcess();
            await Reject<ShopNativeHelperWaitTimeoutException>(() => tracker.RunAsync(() => process, TimeSpan.Zero));
            Task first = tracker.WaitUntilExitAsync(), second = tracker.WaitUntilExitAsync();
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Reject<OperationCanceledException>(() => first.WaitAsync(cancel.Token));
            Need(tracker.IsActive && !second.IsCompleted && process.WaitCalls == 1 && process.DisposeCalls == 0,
                "取消觀察者不應影響原helper的持續監看");
            process.Exit(0); await Within(Task.WhenAll(first, second));
            Need(!tracker.IsActive && process.DisposeCalls == 1, "多觀察者重複處置原程序");
        });

        await Check("normal_completion_returns_actual_exit_code_once", async () =>
        {
            var tracker = new ShopNativeHelperFlight(); var process = new FakeProcess();
            Task<int> pending = tracker.RunAsync(() => process, TimeSpan.FromSeconds(2));
            Need(tracker.IsActive && !pending.IsCompleted, "真正exit之前回傳完成");
            process.Exit(19);
            Need(await pending == 19 && !tracker.IsActive && process.DisposeCalls == 1,
                "正常完成遺失實際ExitCode或持續佔用");
        });

        await Check("continuous_readonly_runner_joins_real_exit_once_and_preserves_cancellation_ownership", async () =>
        {
            var tracker = new ShopNativeHelperFlight(); var process = new FakeProcess(); int starts = 0;
            using var cancel = new CancellationTokenSource();
            Task<int> pending = ShopNativeReadinessRunner.RunAsync(tracker, () => { starts++; return process; },
                ShopNativeReadinessWaitPolicy.HelperBudget(true), cancel.Token);
            await Task.Yield();
            Need(!pending.IsCompleted && tracker.IsActive && starts == 1 && process.WaitCalls == 1,
                "Continuous readonly runner must keep the original real-exit observer.");
            cancel.Cancel(); await Reject<OperationCanceledException>(() => pending);
            Need(tracker.IsActive && process.DisposeCalls == 0 && starts == 1,
                "Stop abandons only the readiness wait; it cannot retire the live helper.");
            Task exit = tracker.WaitUntilExitAsync(); process.Exit(7); await Within(exit);
            Need(!tracker.IsActive && process.DisposeCalls == 1 && starts == 1 && pending.IsCanceled,
                "Late real exit may release the transport but cannot revive the cancelled readiness result.");
        });

        foreach (string failure in new[] { "wait_fault", "false_exit_signal", "exit_code_fault", "dispose_fault" })
            await Check(failure + "_fails_closed", async () =>
            {
                var tracker = new ShopNativeHelperFlight();
                var process = new FakeProcess { ExitCodeFault = failure == "exit_code_fault", DisposeFault = failure == "dispose_fault" };
                Task<int> running = tracker.RunAsync(() => process, TimeSpan.FromSeconds(2));
                if (failure == "wait_fault") process.FailWait();
                else if (failure == "false_exit_signal") process.FalseExitSignal();
                else process.Exit(3);
                await Reject<ShopNativeHelperExitUnconfirmedException>(() => running);
                Need(tracker.IsActive, "監看／處置錯誤不得當作已完成");
                int unexpectedStarts = 0;
                await Reject<ShopNativeHelperBusyException>(() => tracker.RunAsync(() => { unexpectedStarts++; return new FakeProcess(); }, TimeSpan.Zero));
                await Reject<ShopNativeHelperExitUnconfirmedException>(() => tracker.WaitUntilExitAsync());
                Need(unexpectedStarts == 0 && process.WaitCalls == 1,
                    "監看失敗後啟動另一helper或重複監看");
                Need(process.DisposeCalls == (failure == "dispose_fault" ? 1 : 0),
                    "未確認exit時不應dispose原程序handle");
            });

        await Check("start_failure_does_not_invent_a_live_process", async () =>
        {
            var tracker = new ShopNativeHelperFlight(); int starts = 0;
            await Reject<IOException>(() => tracker.RunAsync(() => { starts++; throw new IOException("synthetic start failed"); }, TimeSpan.Zero));
            Need(!tracker.IsActive && starts == 1 && tracker.WaitUntilExitAsync().IsCompleted,
                "Process.Start失敗不應產生不存在的flight");
            tracker.RequireAvailable();
        });

        await Check("invalid_budget_never_starts_process", async () =>
        {
            var tracker = new ShopNativeHelperFlight(); int starts = 0;
            await Reject<ArgumentOutOfRangeException>(() => tracker.RunAsync(() => { starts++; return new FakeProcess(); }, TimeSpan.FromMilliseconds(-2)));
            Need(starts == 0 && !tracker.IsActive, "不合法預算不應啟動helper");
        });

        await Check("concurrent_callers_start_exactly_one_helper", async () =>
        {
            var tracker = new ShopNativeHelperFlight(); var process = new FakeProcess(); int starts = 0;
            Task TryStart() => Task.Run(async () =>
            {
                try { await tracker.RunAsync(() => { Interlocked.Increment(ref starts); return process; }, TimeSpan.Zero); }
                catch (Exception error) when (error is ShopNativeHelperBusyException or ShopNativeHelperWaitTimeoutException) { }
            });
            await Within(Task.WhenAll(TryStart(), TryStart(), TryStart()));
            Need(starts == 1 && tracker.IsActive && process.WaitCalls == 1, "同時競爭啟動了多個helper");
            Task join = tracker.WaitUntilExitAsync(); process.Exit(0); await Within(join);
        });

        await Check("timeout_stops_force_batch_and_remains_unknown", async () =>
        {
            var tracker = new ShopNativeHelperFlight(); var process = new FakeProcess();
            var error = await Reject<ShopNativeHelperWaitTimeoutException>(() => tracker.RunAsync(() => process, TimeSpan.Zero));
            var failure = ShopBroker.NativeInvocationFailure(error);
            var response = failure.Attach(new ShopCommandResponse { SessionBlocked = true, WorkSettled = false });
            Need(failure is { Code: "NativeOutcomeUnknown", Scope: "Global", Phase: "Native" }
                && ShopScanPolicy.Decide(response, true, true) == ShopScanDisposition.StopBatch,
                "helper timeout被force批次略過，導致下一頁重疊");
            Need(ShopBroker.NativeInvocationFailure(new IOException("other failure")).Scope == "Session",
                "不相關既有失敗被擴大分類");
            Task join = tracker.WaitUntilExitAsync(); process.Exit(1); await Within(join);
            Need(response.SessionBlocked && !response.WorkSettled && failure.Code == "NativeOutcomeUnknown",
                "late exit竄改先前回傳的unknown結果");
        });

        await Check("cleared_page_owner_and_released_gate_do_not_report_work_settled", async () =>
        {
            var tracker = new ShopNativeHelperFlight(); var process = new FakeProcess();
            await Reject<ShopNativeHelperWaitTimeoutException>(() => tracker.RunAsync(() => process, TimeSpan.Zero));
            // The timed-out Open finally has cleared activeNativeSource and
            // released nativeGate; the scan/request tasks have already ended.
            bool otherWorkSettled = true;
            Need(!ShopBroker.NativeWorkSettled(otherWorkSettled, tracker),
                "頁面owner/gate已釋放，但helper仍活著時不可WorkSettled=true");
            Task join = tracker.WaitUntilExitAsync(); process.Exit(0); await Within(join);
            Need(ShopBroker.NativeWorkSettled(otherWorkSettled, tracker)
                && !ShopBroker.NativeWorkSettled(false, tracker),
                "helper真正exit後仍須同時核對其他工作已結束");
        });

        await Check("late_exit_preserves_actual_durable_unknown_intent", async () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "LC-NativeFlight-SYNTHETIC-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var source = new SourceIdentity(4242, 987654321, "offline-only", "0x100000", 1, 4096,
                    new string('A', 64), "synthetic-native-flight-session");
                var at = DateTimeOffset.UtcNow.AddDays(-1);
                var owner = new MarketOwner { Id = 24, LocalIdentity = "synthetic-owner", Name = "離線假店主",
                    Position = new(100, 101), ObservedAt = at, ActionType = ".?AVShopAction@lineage@@",
                    ShopState = ShopEligibilityPolicy.ShopActionObserved, ShopStateSampledAt = at,
                    ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence };
                var targets = new ShopTargetSnapshot { SnapshotId = "synthetic-targets", SourceSessionKey = source.SessionKey,
                    RefreshedAt = at, SelfId = 11, SelfPosition = new(100, 100), Owners = [owner] };
                string state = Path.Combine(root, "scan"); var store = new ShopScanProgressStore(state);
                store.StartNew(source, targets, true);
                store.BeginAttempt(store.PendingPages(targets, true)[0], "unknown-before-timeout");
                store.NativeIntent("unknown-before-timeout");
                File.WriteAllText(Path.Combine(root, "original.pending.json"), "{\"State\":\"NativeLaunchIntent_NoAutomaticRetry\"}");
                string[] Snapshot() => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path)
                        + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
                string[] before = Snapshot();
                var tracker = new ShopNativeHelperFlight(); var process = new FakeProcess();
                await Reject<ShopNativeHelperWaitTimeoutException>(() => tracker.RunAsync(() => process, TimeSpan.Zero));
                Need(store.BlocksSource(source), "timeout後原意圖不應解除");
                Task join = tracker.WaitUntilExitAsync(); process.Exit(0); await Within(join);
                tracker.RequireAvailable();
                Need(store.BlocksSource(source) && new ShopScanProgressStore(state).BlocksSource(source)
                    && before.SequenceEqual(Snapshot()), "helper exit改写/清除原意圖或持久進度");
            }
            finally
            {
                string full = Path.GetFullPath(root), temp = Path.GetFullPath(Path.GetTempPath());
                if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(full).StartsWith("LC-NativeFlight-SYNTHETIC-", StringComparison.Ordinal))
                    throw new IOException("Synthetic cleanup path rejected");
                Directory.Delete(full, true);
            }
        });
        return checks.ToArray();
    }

    private sealed class FakeProcess : IShopNativeHelperProcess
    {
        private readonly TaskCompletionSource exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int code;
        public bool HasExited { get; private set; }
        internal int WaitCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal bool ExitCodeFault { get; init; }
        internal bool DisposeFault { get; init; }
        public int ExitCode => ExitCodeFault ? throw new IOException("synthetic exit-code failure")
            : HasExited ? code : throw new InvalidOperationException("still running");
        public Task WaitForExitAsync() { WaitCalls++; return exit.Task; }
        public void Dispose() { DisposeCalls++; if (DisposeFault) throw new IOException("synthetic dispose failure"); }
        internal void Exit(int value) { code = value; HasExited = true; exit.TrySetResult(); }
        internal void FailWait() => exit.TrySetException(new IOException("synthetic observer failure"));
        internal void FalseExitSignal() => exit.TrySetResult();
    }
}
