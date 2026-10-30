using System.Text.Json;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Local contract/store tests only; no runtime, helper, UI or LC process is opened.</summary>
internal static class ForceDispatchSelfTest
{
    internal static OfflineCheck[] Run()
    {
        var checks = new List<OfflineCheck>();
        string root = Path.Combine(Path.GetTempPath(), "LC-Market56-force-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = new SourceIdentity(4242, 987654321, "offline-only", "0x100000", 1, 4096,
            new string('A', 64), "offline-force-session");
        var at = DateTimeOffset.UtcNow.AddYears(-1);
        var unknown = new MarketOwner { Id = 22, LocalIdentity = "unknown-owner", Name = "離線未知狀態",
            Position = new(100, 101), ObservedAt = at, ShopState = ShopEligibilityPolicy.Unknown };
        var other = unknown with { Id = 23, LocalIdentity = "other-owner", ActionType = ".?AVMovingAction@lineage@@",
            ShopState = ShopEligibilityPolicy.KnownOtherActionObserved, ShopStateSampledAt = at,
            ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence };
        var shopping = other with { Id = 24, LocalIdentity = "shopping-owner", ActionType = ".?AVShopAction@lineage@@",
            ShopState = ShopEligibilityPolicy.ShopActionObserved };
        var targets = new ShopTargetSnapshot { SnapshotId = "offline-force-targets", SourceSessionKey = source.SessionKey,
            RefreshedAt = at, SelfId = 11, SelfPosition = new(100, 100), Owners = [unknown, other, shopping], CanDispatch = false };
        void Check(string name, Action action)
        {
            try { action(); checks.Add(new(name, true, "離線資料與實際持久化合約通過")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        static void Need(bool pass, string reason) { if (!pass) throw new InvalidOperationException(reason); }
        static void Reject(Action action, string reason)
        {
            try { action(); }
            catch (ShopRequestFailure) { return; }
            throw new InvalidOperationException(reason);
        }

        Check("force_request_is_explicit_and_roundtrips", () =>
        {
            Need(!new ShopCommandRequest().ForceDispatch, "未明確指定的請求不能自動強制");
            var request = new ShopCommandRequest { Command = ShopCommands.OpenShop, ForceDispatch = true };
            Need(JsonSerializer.Deserialize<ShopCommandRequest>(JsonSerializer.Serialize(request, HubProtocol.Json), HubProtocol.Json)!.ForceDispatch,
                "強制旗標未保留到命令合約");
        });
        Check("force_plan_only_shopping_without_legacy_refresh_permission", () =>
        {
            var store = new ShopScanProgressStore(Path.Combine(root, "plan-state"));
            Reject(() => store.StartNew(source, targets), "預設流程意外放開未刷新名單");
            var plan = store.StartNew(source, targets, true);
            Need(plan.Owners.Length == 1 && plan.Owners[0] == shopping && store.PendingPages(targets, true).Length == 2,
                "直接下達計畫未只保留已觀測擺攤玩家");
            Need(store.PendingPages(targets).Length == 2, "一般及直接模式的頁面規劃都應只包含擺攤玩家");
            Need(store.Continue(plan.PlanId, source, targets, forceDispatch: true).Length == 2,
                "強制接續仍被CanDispatch阻擋");
        });
        Check("force_old_ui_snapshot_uses_latest_list_of_same_local_source", () =>
        {
            var request = new ShopCommandRequest { Command = ShopCommands.OpenShop, ForceDispatch = true,
                ExpectedSourceSessionKey = source.SessionKey, TargetSnapshotId = "older-ui-snapshot",
                OwnerId = unknown.Id, ExpectedOwnerIdentity = unknown.LocalIdentity };
            Reject(() => ShopTargetPolicy.Validate(targets, source, source, source.SessionKey, request.TargetSnapshotId),
                "預設流程意外忽略名單版本");
            ShopForceDispatchPolicy.ValidateTargets(targets, source, request);
            Need(ShopTargetPolicy.Owner(targets, request.OwnerId!.Value, request.ExpectedOwnerIdentity) == unknown,
                "強制舊UI請求未核對最新名單店主映射");
            Reject(() => ShopForceDispatchPolicy.ValidateTargets(targets, source,
                request with { ExpectedSourceSessionKey = "foreign-local-source" }), "不同來源UI請求沿用本機ID");
            Reject(() => ShopTargetPolicy.Owner(targets, unknown.Id, "foreign-owner-identity"),
                "強制流程沿用foreign/reused本機識別");
            Reject(() => ShopTargetPolicy.Owner(targets, targets.SelfId, unknown.LocalIdentity),
                "自身ID被強制當店主");
        });
        Check("force_new_plan_preserves_and_fences_actual_unknown_intent_across_reload", () =>
        {
            string directory = Path.Combine(root, "old-intent");
            var store = new ShopScanProgressStore(directory);
            var first = store.StartNew(source, targets, true);
            var page = store.PendingPages(targets, true)[0];
            store.BeginAttempt(page, "old-unknown-request");
            store.NativeIntent("old-unknown-request");
            Need(store.BlocksSource(source), "未知意圖未保留");
            Reject(() => store.StartNew(source, targets with { CanDispatch = true }), "預設流程意外清除未知鎖");
            Reject(() => store.StartNew(source, targets, true), "強制新計畫繞過真實原生未知意圖");
            var reopened = new ShopScanProgressStore(directory);
            Need(reopened.BlocksSource(source) && reopened.PlanId == first.PlanId && reopened.PendingPages(targets, true).Length == 1,
                "重啟後原未知證據或原計畫頁面遺失");
            Need(!reopened.View(source, targets, false, true, true)!.CanContinue,
                "真實原生未結清被強制回報Ready");
            Reject(() => reopened.Continue(first.PlanId, source, targets, forceDispatch: true), "強制接續繞過原生未結清");
            Reject(() => reopened.Continue(first.PlanId, source with { CreationFileTime = 987654322 }, targets, forceDispatch: true),
                "強制流程把舊本機玩家ID傳入重啟來源");
        });
        Check("force_history_keeps_sent_dedup_and_older_uncertain_evidence", () =>
        {
            var history = new ShopSearchedOwnerStore(Path.Combine(root, "history"), Path.Combine(root, "absent-progress"));
            history.Begin(source, unknown, ShopPageModes.ViewerBuy, "unknown-A", true);
            Need(history.ShouldSkip(unknown.Id, ShopPageModes.ViewerBuy)
                && !history.ShouldSkip(unknown.Id, ShopPageModes.ViewerBuy, true), "未知歷史未按強制旗標區分");
            history.Begin(source, unknown, ShopPageModes.ViewerBuy, "forced-B", true);
            history.Settle("forced-B", true);
            Need(history.ShouldSkip(unknown.Id, ShopPageModes.ViewerBuy, true), "已送出頁面的略過習慣遺失");
            Need(history.BlocksSource(source) && history.Summary(false, "").UncertainPages == 1,
                "新請求已返回被誤當舊未知請求也成功");
            Reject(() => history.Begin(source, unknown, ShopPageModes.ViewerBuy, "forced-C", true), "強制流程重複送出已查頁");
            history.Begin(source, other, ShopPageModes.ViewerSell, "new-other-page", true);
            Reject(() => history.Begin(source, other, ShopPageModes.ViewerSell, "new-other-page", true), "同RequestId被強制重送");
        });
        Check("force_single_response_sample_has_no_retry_on_absence_or_read_failure", () =>
        {
            int reads = 0;
            var failed = ShopForceDispatchPolicy.ObserveOnce(() => { reads++; throw new IOException("offline-read-absence"); }, out string detail);
            Need(failed is null && reads == 1 && detail == "offline-read-absence", "缺少回覆仍輪詢重試");
            reads = 0;
            var sample = new PersonalShopListing();
            Need(ReferenceEquals(ShopForceDispatchPolicy.ObserveOnce(() => { reads++; return sample; }, out detail), sample)
                && reads == 1 && detail.Length == 0, "單次回覆讀取沿用舊值或重採樣");
        });
        Check("force_scan_continues_after_unverified_and_keeps_truthful_flags", () =>
        {
            var unverified = new ShopCommandResponse { State = "Unverified", NativeEntered = true, NativeReturned = true,
                ResponseVerified = false, FailureCode = "ShopResponseUnverified", FailureScope = "Target" };
            Need(ShopScanPolicy.Decide(unverified, true) == ShopScanDisposition.SkipOwner
                && ShopScanPolicy.Decide(unverified, true, true) == ShopScanDisposition.Continue,
                "強制流程仍因未確認回覆略過下一頁");
            Need(!unverified.ResponseVerified && ShopScanPolicy.Decide(unverified with { SessionBlocked = true }, true, true)
                == ShopScanDisposition.StopBatch, "強制流程將真實原生未結清混同已結清但商品未確認");
            Need(ShopScanPolicy.Decide(unverified, false, true) == ShopScanDisposition.StopBatch,
                "真正來源變更仍使用舊ID");
            Need(ShopScanPolicy.Decide(unverified with { FailureCode = "NativeBuildUnsupported", FailureScope = "Global" }, true, true)
                == ShopScanDisposition.StopBatch, "未知原生ABI仍呼叫");
        });
        Check("force_journals_are_per_request_and_do_not_replace_legacy_pending", () =>
        {
            string first = ShopForceDispatchPolicy.PendingPath(root, "4242-987654321", "request/A");
            string second = ShopForceDispatchPolicy.PendingPath(root, "4242-987654321", "request-B");
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            string legacy = Path.Combine(root, "sessions", "4242-987654321.pending.json");
            File.WriteAllText(legacy, "old-unknown");
            File.WriteAllText(first, "new-request-A");
            File.WriteAllText(second, "new-request-B");
            Need(first != second && File.ReadAllText(legacy) == "old-unknown"
                && !Path.GetFileName(first).Contains('/'), "新強制意圖改寫舊pending或路徑未隔離");
        });
        Check("force_new_work_cancels_paused_worker_without_waiting_for_resume", () =>
        {
            var coordinator = new ScanPauseCoordinator(new object(), "paused-old-worker");
            using var stop = new CancellationTokenSource();
            coordinator.RequestPause("paused-old-worker");
            var wait = coordinator.EnterPageAsync("paused-old-worker", () => true, stop.Token);
            Need(!wait.IsCompleted && coordinator.Status.Paused, "測試未進入舊工作暫停邊界");
            ShopForceDispatchPolicy.CancelPriorWorker(coordinator, stop);
            try { wait.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                Need(stop.IsCancellationRequested && !coordinator.Status.Paused,
                    "新工作未取消舊工作或仍保留暫停狀態");
                return;
            }
            throw new InvalidOperationException("舊暫停工作仍等待繼續，或取消後派送了下一頁");
        });
        Check("force_scan_control_without_status_binds_and_controls_real_coordinator", () =>
        {
            var current = new MarketScanState { Running = true, JobId = "actual-force-job",
                SourceSessionKey = source.SessionKey, TargetSnapshotId = targets.SnapshotId };
            var control = new ShopCommandRequest { Command = ShopCommands.PauseScan,
                ForceDispatch = true, ExpectedSourceSessionKey = source.SessionKey };
            var validate = typeof(ShopBroker).GetMethod("Validate", System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static)!;
            validate.Invoke(null, [control]);
            var bound = ShopScanControlPolicy.BindCurrent(control, current);
            Need(bound.ExpectedScanJobId == current.JobId && bound.TargetSnapshotId == current.TargetSnapshotId,
                "無status的強制控制未綁定當前相同來源工作");
            var coordinator = new ScanPauseCoordinator(new object(), current.JobId!);
            coordinator.RequestPause(bound.ExpectedScanJobId!);
            Need(coordinator.Status.Requested, "真實coordinator未收到暫停命令");
            bound = ShopScanControlPolicy.BindCurrent(control with { Command = ShopCommands.ResumeScan }, current);
            coordinator.Resume(bound.ExpectedScanJobId!);
            Need(!coordinator.Status.Requested && !coordinator.Status.Paused, "真實coordinator未收到繼續命令");
            Reject(() => ShopScanControlPolicy.BindCurrent(control with { ExpectedScanJobId = "foreign-job" }, current),
                "明確指定其他工作仍被自動綁成當前工作");
            Reject(() => ShopScanControlPolicy.BindCurrent(control with { ExpectedSourceSessionKey = "foreign-source" }, current),
                "其他LC來源控制了當前工作");
            Need(ShopScanControlPolicy.BindCurrent(control with { ForceDispatch = false }, current).ExpectedScanJobId is null,
                "一般控制模式不應自動借用當前status");
        });
        Check("force_replacement_keeps_already_entered_page_lease_until_cleanup", () =>
        {
            var coordinator = new ScanPauseCoordinator(new object(), "active-old-worker");
            using var stop = new CancellationTokenSource();
            using var page = coordinator.EnterPageAsync("active-old-worker", () => true, stop.Token).GetAwaiter().GetResult();
            ShopForceDispatchPolicy.CancelPriorWorker(coordinator, stop);
            Need(stop.IsCancellationRequested, "新工作未停止旧批次後續頁");
            try { coordinator.EnterPageAsync("active-old-worker", () => true, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("替換工作後舊批次仍可送下一頁");
        });
        return checks.ToArray();
    }
}
