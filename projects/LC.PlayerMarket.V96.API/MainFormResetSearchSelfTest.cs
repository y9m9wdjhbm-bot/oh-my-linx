using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;

namespace LcPlayerMarket;

/// <summary>Actual MainForm handlers with fake ports only; the fixture has no LC source.</summary>
internal static class MainFormResetSearchSelfTest
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static int Run(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Evidence directory must be new.");
        Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
        var checks = new List<OfflineCheck>();
        void Check(string name, Action<Fixture> action)
        {
            try
            {
                using var fixture = new Fixture(Path.Combine(output, name));
                action(fixture); checks.Add(new(name, true, "真主窗＋fake來源／指令／重置port；無LC程序、記憶體讀取、輸入或原生操作。"));
            }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        Check("reset_ui_one_click_no_game_clears_cached_plan", f =>
        {
            var port = new SearchRestartCoordinatorSelfTest.FakePort { CurrentSource = null };
            f.Use(port);
            f.SeedProgress();
            Field<Button>(f.Form, "resetSearchButton").PerformClick();
            PumpUntil(() => !Field<bool>(f.Form, "resettingSearch"), "單擊本機清除沒有完成。");
            Need(port.Requests.Count == 1 && port.Requests[0].Command == ShopCommands.ResetSearchProgress && port.CheckpointReset,
                "單擊未送一次清除或未完成巡迴進度重置。");
            Need(Field<ShopSavedScanProgress?>(f.Form, "lastKnownSavedScan") is null && Field<ShopCommandResponse>(f.Form, "broker").SavedScan is null,
                "清除後舊plan仍留在主窗快取。");
            Need(Field<ShopSearchHistorySummary>(f.Form, "lastKnownSearchHistory").OwnerCount == 0 && !port.Requests.Any(r => r.Command == ShopCommands.GetState),
                "清除仍先等 GetState 或歷史摘要未清空。");
        });
        Check("reset_ui_transaction_blocks_regular_mutations_and_double_click", f =>
        {
            var join = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var port = new SearchRestartCoordinatorSelfTest.FakePort { CurrentSource = null, JoinWait = join.Task };
            f.Use(port); f.SeedSource(); f.SeedProgress();
            Field<Button>(f.Form, "resetSearchButton").PerformClick();
            Need(Field<bool>(f.Form, "resettingSearch") && !Field<Button>(f.Form, "resetSearchButton").Enabled, "等待清理時未鎖住重置按鈕。");
            foreach (string name in new[] { "refreshNearby", "scan", "continueSaved", "pause", "resume", "patrolButton", "startSavedPatrolButton" })
                Need(!Field<Button>(f.Form, name).Enabled, "重置交易中仍可開始另一工作：" + name);
            Need(Field<Button>(f.Form, "stop").Enabled, "重置交易不能直接停止。");
            Field<Button>(f.Form, "resetSearchButton").PerformClick();
            var attempted = new[] { (Task)Call(f.Form, "RefreshNearbyAsync")!, (Task)Call(f.Form, "SendAction", ShopCommands.ScanVisible, null)!,
                (Task)Call(f.Form, "SendScanControl", ShopCommands.ResumeScan)!, (Task)Call(f.Form, "StartOrStopSavedPatrolAsync")! };
            // The homepage stop path also cancels the active reset transaction.
            PumpUntil(() => attempted.All(t => t.IsCompleted), "本機guard未立即完成。");
            join.TrySetResult(true);
            PumpUntil(() => !Field<bool>(f.Form, "resettingSearch"), "停止後重置交易未釋放。");
            Need(port.Calls.Count(c => c == "join") == 1 && f.Commands.Requests.Count == 0
                && port.Requests.Count == 0, "取消或雙擊發出額外商店指令。");
        });
        Check("reset_ui_stop_after_ack_finishes_checkpoint_without_start", f =>
        {
            var port = new SearchRestartCoordinatorSelfTest.FakePort { Route = SearchRestartCoordinatorSelfTest.Route() };
            port.OnApply = () => Call(f.Form, "CancelSearchRestart");
            f.Use(port); f.SeedProgress();
            Field<Button>(f.Form, "resetSearchButton").PerformClick();
            PumpUntil(() => !Field<bool>(f.Form, "resettingSearch"), "取消後重置交易仍占用主窗。");
            Need(port.Applied && port.CheckpointReset && port.Requests.Count == 1 && port.StartedRoute is null,
                "已確認清除時按停止仍保留舊恢復點或自啟動。");
        });
        Check("reset_ui_old_response_preserves_goods_without_repopulating_plan", f =>
        {
            f.SeedSource(); f.SeedProgress();
            long oldGeneration = Field<long>(f.Form, "searchResetGeneration");
            f.ApplyReset();
            var old = new ShopCommandResponse { At = DateTimeOffset.UtcNow.AddSeconds(1), State = "State",
                SavedScan = new() { PlanId = "retired-plan", SourceSessionKey = SearchRestartCoordinatorSelfTest.Source.SessionKey },
                SearchHistory = new() { Revision = "retired-history", Epoch = "retired-epoch", OwnerCount = 3 },
                Scan = new() { Running = true, JobId = "retired-job" }, Listings = [f.Listing] };
            Call(f.Form, "MergeResponseCore", old, true, oldGeneration);
            Need(Field<ShopSavedScanProgress?>(f.Form, "lastKnownSavedScan") is null && Field<ShopCommandResponse>(f.Form, "broker").State == "SearchProgressReset"
                && Field<ShopSearchHistorySummary>(f.Form, "lastKnownSearchHistory").OwnerCount == 0,
                "舊回覆把已清除搜尋進度補回主窗。");
            Need(Field<MarketListingIndex>(f.Form, "history").Count == 1 && Directory.EnumerateFiles(f.Store.HistoryDirectory, "*.json").Count() == 1,
                "世代過濾丟失真實商品收錄副作用。");
        });
        Check("reset_ui_queued_old_patrol_reply_cannot_restore_progress", f =>
        {
            f.SeedSource(); f.SeedProgress();
            var marketType = typeof(MainForm).GetNestedType("MainPatrolMarket", BindingFlags.NonPublic)!;
            var market = (IPatrolMarket)Activator.CreateInstance(marketType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [f.Form], null)!;
            var request = new ShopCommandRequest { Command = ShopCommands.GetState };
            var pending = market.SendAsync(request, default);
            PumpUntil(() => f.Commands.Requests.Count == 1, "原巡迴狀態讀取未進fakebackend。");
            f.Commands.Complete(request, new() { RequestId = request.RequestId, State = "State", At = DateTimeOffset.UtcNow.AddSeconds(2),
                SavedScan = new() { PlanId = "old-posted-plan", SourceSessionKey = SearchRestartCoordinatorSelfTest.Source.SessionKey },
                SearchHistory = new() { Revision = "old-posted-history", Epoch = "old-posted-epoch", OwnerCount = 4 } });
            Need(pending.Wait(TimeSpan.FromSeconds(5)), "fake巡迴回覆未排入UI佇列。");
            // Leave BeginInvoke queued until the real reset applies its fence.
            f.ApplyReset(); Application.DoEvents();
            Need(Field<ShopSavedScanProgress?>(f.Form, "lastKnownSavedScan") is null
                && Field<ShopSearchHistorySummary>(f.Form, "lastKnownSearchHistory").OwnerCount == 0,
                "停止後排隊的原巡迴callback恢復了舊progress。");
        });
        Check("reset_ui_patrol_stop_joins_ui_finally", f =>
        {
            var ui = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Set(f.Form, "patrolTask", Task.FromResult(new PatrolRunResult("offline-run", "Stopped", 2, 3, 0, "clean", true)));
            Set(f.Form, "patrolUiCompletion", ui);
            var stopping = (Task)Call(f.Form, "StopPatrolAsync")!;
            Need(!stopping.IsCompleted, "停止只等controller，未等待原UIfinally。");
            ui.SetResult(true); PumpUntil(() => stopping.IsCompleted, "UIfinally已完成但停止未釋放。");
            Need(stopping.IsCompletedSuccessfully, "UIjoin失敗。");
        });
        Check("reset_ui_cancel_accepted_scan_once_waits_for_real_worker_settlement", f =>
        {
            var portType = typeof(MainForm).GetNestedType("MainSearchRestartPort", BindingFlags.NonPublic)!;
            var port = (ISearchRestartPort)Activator.CreateInstance(portType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [f.Form], null)!;
            var scan = new ShopCommandRequest { Command = ShopCommands.ScanVisible, ExpectedSourceSessionKey = SearchRestartCoordinatorSelfTest.Source.SessionKey };
            var stopping = port.StopAcceptedScanAndJoinAsync(scan, new() { RequestId = scan.RequestId, State = "ScanStarted", Scan = new() { JobId = scan.RequestId, Running = true } });
            PumpUntil(() => f.Commands.Requests.Count == 1, "已接受新搜尋未送出停止。");
            var cancel = f.Commands.Requests.Single();
            Need(cancel.Command == ShopCommands.CancelScan && cancel.ForceDispatch && cancel.ExpectedScanJobId == scan.RequestId,
                "未使用原新搜尋job停止，可能盲停另一工作。");
            f.Commands.Complete(cancel, new() { RequestId = cancel.RequestId, State = "Stopping", WorkSettled = false, Scan = new() { JobId = scan.RequestId } });
            PumpUntil(() => f.Commands.Requests.Count == 2, "停止只看Running=false，未核對worker釋放。");
            Need(!stopping.IsCompleted, "worker未清理就退出取消交易。");
            var read = f.Commands.Requests.Last();
            Need(read.Command == ShopCommands.GetState, "停止核對竟重送商店操作。");
            f.Commands.Complete(read, new() { RequestId = read.RequestId, State = "State", WorkSettled = true, Scan = new() { JobId = scan.RequestId } });
            PumpUntil(() => stopping.IsCompleted, "原job已清理但停止交易未結束。");
            Need(stopping.IsCompletedSuccessfully && f.Commands.Requests.Count == 2, "取消重送或沒有使用真實清理回覆。");
        });
        Check("reset_ui_page_wait_visible_persistent_and_no_minimap_host", f =>
        {
            var wait = Field<NumericUpDown>(f.Form, "pageWaitMilliseconds");
            Need(wait.Visible && wait.Minimum == 100 && wait.Maximum == 5000 && wait.Value == 3000 && wait.Increment == 100, "首頁頁面等待缺少合法範圍／預設值。");
            var noWait = Field<CheckBox>(f.Form, "noAdditionalPageWait");
            Need(noWait.Checked && f.Store.LoadPreferences().NoAdditionalPageWait, "V96預設沒有停用額外等待。");
            wait.Value = 5000; Need(f.Store.LoadPreferences().PageOpenIntervalMilliseconds == 5000, "主窗秒數未保存。");
            wait.Value = 100; Need(f.Store.LoadPreferences().PageOpenIntervalMilliseconds == 100, "主窗最小秒數未保存。");
            noWait.Checked = false;
            Need(!f.Store.LoadPreferences().NoAdditionalPageWait && f.Store.LoadPreferences().PageOpenIntervalMilliseconds == 100, "恢復毫秒設定時遺失數值。");
            noWait.Checked = true;
            Need(f.Store.LoadPreferences().EffectivePageOpenIntervalMilliseconds == 0 && f.Store.LoadPreferences().PageOpenIntervalMilliseconds == 100, "零等待覆蓋了保留的毫秒數值。");
            Need(typeof(MainForm).GetField("miniMapHost", Hidden) is null && typeof(MainForm).GetField("miniMapEnabled", Hidden) is null
                && typeof(MainForm).GetMethod("UpdateMiniMap", Hidden) is null, "小地圖只隱藏，仍保留執行元件。");
            Need(f.Commands.Requests.Count == 0 && Field<object?>(Field<PatrolNavigationClient>(f.Form, "patrolNavigation"), "process") is null,
                "修改頁面節奏啟動了遊戲／導航請求。");
        });
        Check("reset_ui_terminal_patrol_failure_is_not_successful_restart", f =>
        {
            Set(f.Form, "patrolRunning", true);
            Set(f.Form, "patrolTask", Task.FromResult(new PatrolRunResult("failed-first-capture", "Failed", 0, 1, 0, "fresh capture failed", true)));
            var rejected = (Task)Call(f.Form, "ConfirmPatrolRestartLaunchAsync", Task.CompletedTask)!;
            Need(rejected.IsFaulted, "fresh初始位置失敗被報為巡迴已重新開始。");
            Set(f.Form, "patrolTask", null); Set(f.Form, "patrolStartFailure", new IOException("start threw before capture"));
            rejected = (Task)Call(f.Form, "ConfirmPatrolRestartLaunchAsync", Task.CompletedTask)!;
            Need(rejected.IsFaulted, "主窗吞下的啟動錯誤被報為成功。");
            Set(f.Form, "patrolStartFailure", null); Set(f.Form, "patrolRunning", false);
        });
        Check("reset_ui_waits_for_readiness_before_reporting_patrol_started", f =>
        {
            var preparation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Set(f.Form, "patrolStartCompletion", preparation); Set(f.Form, "patrolRunning", true);
            var pending = (Task)Call(f.Form, "ConfirmPatrolRestartLaunchAsync", Task.CompletedTask)!;
            Need(!pending.IsCompleted, "入口檢查未完成即宣稱已重新開始巡迴。");
            Set(f.Form, "patrolStartFailure", new InvalidOperationException("entry unavailable"));
            preparation.SetResult(false); PumpUntil(() => pending.IsCompleted, "失敗的入口檢查沒有完成啟動結果。");
            Need(pending.IsFaulted && f.Commands.Requests.Count == 0, "入口失敗仍宣稱啟動或送出指令。");
            Set(f.Form, "patrolStartCompletion", null); Set(f.Form, "patrolStartFailure", null); Set(f.Form, "patrolRunning", false);
        });
        Check("readiness_cancel_after_backend_ready_before_ui_continuation", f =>
        {
            f.SeedSource();
            var original = SynchronizationContext.Current;
            var queued = new QueuedReadinessContext();
            try
            {
                SynchronizationContext.SetSynchronizationContext(queued);
                var pending = (Task<bool>)Call(f.Form, "CheckNativeReadinessAsync", CancellationToken.None)!;
                Need(SpinWait.SpinUntil(() => f.Commands.Requests.Count == 1, TimeSpan.FromSeconds(5)), "唯讀檢查未進入fakebackend。");
                var request = f.Commands.Requests.Single();
                Need(request.Command == ShopCommands.CheckNativeReadiness, "檢查送出非唯讀命令。");
                var now = DateTimeOffset.UtcNow;
                f.Commands.Complete(request, new ShopCommandResponse { RequestId = request.RequestId,
                    NativeReadiness = new ShopNativeReadiness { Source = SearchRestartCoordinatorSelfTest.Source,
                        BuildCompatible = true, Ready = true, Code = "NativeReady", SampledAt = now,
                        ExpiresAt = now.AddMilliseconds(HubProtocol.FreshMilliseconds), Detail = "fake ready" } });
                Need(SpinWait.SpinUntil(() => queued.HasPost, TimeSpan.FromSeconds(5)), "backend完成但沒有捕捉到UI續行。");
                ((Task)Call(f.Form, "SendCancel")!).GetAwaiter().GetResult();
                queued.Drain();
                Need(pending.IsCompletedSuccessfully && !pending.Result, "停止後仍接納排隊的Ready回覆。");
                Need(Field<ShopNativeReadiness?>(f.Form, "lastNativeReadiness") is null && f.Commands.Requests.Count == 1,
                    "取消的檢查更新Ready或另外送出查詢。");
            }
            finally { SynchronizationContext.SetSynchronizationContext(original); }
        });
        var report = new { Version = "83.0.0", Success = checks.All(c => c.Passed), Passed = checks.Count(c => c.Passed), Failed = checks.Count(c => !c.Passed),
            ActualMainForm = true, FakePortsOnly = true, OpenedGameProcess = false, ProcessMemoryReads = 0, NativeCalls = 0, GameInputs = 0, Checks = checks };
        using (var stream = new FileStream(Path.Combine(output, "result.json"), FileMode.CreateNew, FileAccess.Write))
        { JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
        return report.Success ? 0 : 1;
    }

    private static void Need(bool value, string detail) => SearchRestartCoordinatorSelfTest.Need(value, detail);
    private static T Field<T>(object owner, string name) => (T)(owner.GetType().GetField(name, Hidden) ?? throw new MissingFieldException(name)).GetValue(owner)!;
    private static void Set(object owner, string name, object? value) => (owner.GetType().GetField(name, Hidden) ?? throw new MissingFieldException(name)).SetValue(owner, value);
    private static object? Call(object owner, string name, params object?[] values) => (owner.GetType().GetMethod(name, Hidden) ?? throw new MissingMethodException(name)).Invoke(owner, values);
    private static void PumpUntil(Func<bool> condition, string detail)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(8)) { Application.DoEvents(); Thread.Sleep(5); }
        Need(condition(), detail);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly LocalStore Store;
        internal readonly MainForm Form;
        internal readonly PendingCommands Commands = new();
        internal readonly MarketListing Listing;
        private readonly OffscreenHost host = new();
        internal Fixture(string root)
        {
            Store = new(Path.Combine(root, "state"));
            Store.SavePreferences(new() { LogEnabled = false, ShowNavigation = false, ShowSearchOverlay = false });
            var backend = new StandaloneMarketBackend(Path.Combine(root, "backend"), _ => new FakeSource(), (_, _, _) => Commands);
            Form = new MainForm(Store, backend) { TopLevel = false, Location = Point.Empty, Size = new(1320, 980) };
            host.Controls.Add(Form); Form.Show(); host.Show(); _ = Form.Handle; Application.DoEvents();
            Listing = new() { SourceSessionKey = SearchRestartCoordinatorSelfTest.Source.SessionKey, Revision = "offline-goods",
                RequestId = "old-read", CapturedAt = DateTimeOffset.UtcNow, ViewerDirection = ShopPageModes.ViewerBuy, Complete = true,
                Owner = new() { Id = 22, LocalIdentity = "offline-owner", Name = "離線店主", Position = new(100, 101), ObservedAt = DateTimeOffset.UtcNow },
                Rows = [new(0, "Observed", true, "離線商品", "123", 123, "每個", null, "Unverified", "fake")] };
        }
        internal void Use(SearchRestartCoordinatorSelfTest.FakePort port)
            => Set(Form, "searchRestartPortFactory", (Func<ISearchRestartPort>)(() => new FormPort(Form, port)));
        internal void SeedSource()
        {
            Set(Form, "snapshot", new HubSnapshot { Source = SearchRestartCoordinatorSelfTest.Source });
            Set(Form, "manualNearby", new ManualNearbyContext("old-targets", SearchRestartCoordinatorSelfTest.Source,
                DateTimeOffset.UtcNow, new(100, 100), [Listing.Owner], true));
        }
        internal void SeedProgress()
        {
            Set(Form, "lastKnownSavedScan", new ShopSavedScanProgress { PlanId = "retired-plan", SourceSessionKey = SearchRestartCoordinatorSelfTest.Source.SessionKey, RemainingPages = 2 });
            Set(Form, "lastKnownSearchHistory", new ShopSearchHistorySummary { Revision = "old-history", Epoch = "old-epoch", OwnerCount = 3 });
            Call(Form, "UpdateButtons");
        }
        internal void ApplyReset()
        {
            var request = new ShopCommandRequest { Command = ShopCommands.ResetSearchProgress, ForceDispatch = true };
            Call(Form, "ApplySearchReset", request, SearchRestartCoordinatorSelfTest.ResetReply(request));
        }
        public void Dispose()
        {
            Commands.CompleteAll(); Call(Form, "CancelSearchRestart");
            PumpUntil(() => { Commands.CompleteAll(); return !Field<bool>(Form, "resettingSearch"); }, "fake重置未在釋放前結束。");
            Form.Dispose(); host.Dispose();
        }
    }

    private sealed class FormPort(MainForm form, SearchRestartCoordinatorSelfTest.FakePort fake) : ISearchRestartPort
    {
        public SourceIdentity? CurrentSource => fake.CurrentSource;
        public bool CanStart(SourceIdentity source, out string reason) => fake.CanStart(source, out reason);
        public Task StopPatrolAndJoinAsync(CancellationToken token) => fake.StopPatrolAndJoinAsync(token);
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token) => fake.SendAsync(request, token);
        public void ApplyReset(ShopCommandRequest request, ShopCommandResponse response)
        { Call(form, "ApplySearchReset", request, response); fake.ApplyReset(request, response); }
        public Task ResetPatrolAsync(CancellationToken token) => fake.ResetPatrolAsync(token);
        public PatrolRouteLoadResult LoadRoute() => fake.LoadRoute();
        public Task StartPatrolAsync(PatrolRouteSnapshot route, SourceIdentity expected, CancellationToken token) => fake.StartPatrolAsync(route, expected, token);
        public void AdoptRefresh(ShopCommandRequest request, ShopCommandResponse response, SourceIdentity expected) => fake.AdoptRefresh(request, response, expected);
        public Task StopAcceptedScanAndJoinAsync(ShopCommandRequest request, ShopCommandResponse acceptance) => fake.StopAcceptedScanAndJoinAsync(request, acceptance);
    }
    private sealed class OffscreenHost : Form
    {
        internal OffscreenHost() { ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; Bounds = new(-32000, -32000, 1320, 980); }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080; return p; } }
    }
    private sealed class PendingCommands : IStandaloneShopCommands
    {
        internal readonly ConcurrentQueue<ShopCommandRequest> Requests = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ShopCommandResponse>> pending = new();
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            var completion = new TaskCompletionSource<ShopCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.AddOrUpdate(request.RequestId, completion, (_, _) => throw new InvalidOperationException("duplicate request"));
            Requests.Enqueue(request); return completion.Task.WaitAsync(token);
        }
        internal void Complete(ShopCommandRequest request, ShopCommandResponse response)
        { if (pending.TryRemove(request.RequestId, out var completion)) completion.TrySetResult(response); }
        internal void CompleteAll()
        { foreach (var request in Requests) Complete(request, new() { RequestId = request.RequestId, State = "Rejected" }); }
        public ValueTask DisposeAsync() { CompleteAll(); return ValueTask.CompletedTask; }
    }
    private sealed class QueuedReadinessContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> callbacks = new();
        internal bool HasPost => !callbacks.IsEmpty;
        public override void Post(SendOrPostCallback callback, object? state) => callbacks.Enqueue((callback, state));
        internal void Drain() { while (callbacks.TryDequeue(out var queued)) queued.Callback(queued.State); }
    }
    private sealed class FakeSource : ISlicedMemorySource
    {
        public void Configure(ReadSelection selection) { }
        public ReadResult Read(CancellationToken token) { token.ThrowIfCancellationRequested(); return new(null, new(), "ResetUiOffline", "Fake source only."); }
        public ReadResult ReadSlice(ReadSelection fields, CancellationToken token) => Read(token);
        public void Dispose() { }
    }
}
