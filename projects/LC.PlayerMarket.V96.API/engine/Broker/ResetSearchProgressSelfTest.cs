using System.Reflection;
using System.Security.Cryptography;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Actual local stores/broker, no runtime start, native helper or LC read.</summary>
internal static class ResetSearchProgressSelfTest
{
    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        foreach (string scenario in new[] { "complete", "prepared", "AfterOwnerClear", "AfterScanReset", "stale_revision", "duplicate_after_new_plan", "corrupt_marker" })
        {
            await Check("reset_local_transaction_" + scenario, () =>
            {
                using var f = new Fixture();
                var tx = f.Resets.Prepare(f.Owners, f.Scans, "reset-" + scenario);
                if (scenario == "stale_revision")
                {
                    f.Owners.Begin(f.Source, f.Targets.Owners[1], ShopPageModes.ViewerBuy, "late-ledger", true);
                    Reject(() => f.Resets.Complete(tx, f.Owners, f.Scans), "過期版本覆蓋新搜尋紀錄。");
                    Need(f.Scans.PlanId == f.OriginalPlanId && f.Owners.Summary(true, "").OwnerCount == 2, "過期reset仍清了其中一個store。");
                }
                else
                {
                    if (scenario != "prepared")
                    {
                        bool interrupted = false;
                        try { f.Resets.Complete(tx, f.Owners, f.Scans, stage => { if (scenario == stage) throw new IOException("OFFLINE_CRASH"); }); }
                        catch (IOException error) when (error.Message == "OFFLINE_CRASH") { interrupted = true; }
                        Need(interrupted == scenario.StartsWith("After", StringComparison.Ordinal), "故障注入未到指定commit階段。");
                    }
                    f.Reopen();
                    int recovered = f.Resets.RecoverPending(f.Owners, f.Scans);
                    Need(recovered == (scenario is "prepared" or "AfterOwnerClear" or "AfterScanReset" ? 1 : 0), "重設復原次數錯誤。");
                    Need(f.Scans.PlanId is null && f.Scans.View(f.Source, f.Targets, false, true, true) is null, "重啟後舊保存計畫復活。");
                    var summary = f.Owners.Summary(true, "");
                    Need(summary.OwnerCount == 0 && summary.SearchedPages == 0 && summary.ConfirmedPages == 0 && summary.UncertainPages == 0, "本期去重未清空。");
                    Need(f.Owners.BlocksSource(f.Source) && f.Scans.BlocksSource(f.Source), "重設誤刪舊unknown來源鎖。");
                    Reject(() => f.Owners.Begin(f.Source, f.Targets.Owners[0], ShopPageModes.ViewerBuy, "old-unknown", true), "同RequestId被重設授予重送。");
                    Reject(() => f.Scans.Continue(f.OriginalPlanId, f.Source, f.Targets, forceDispatch: true), "重設後仍可接續舊plan。");
                    if (scenario == "duplicate_after_new_plan")
                    {
                        Reject(() => f.Scans.StartNew(f.Source, f.Targets, true), "reset後force仍繞過原來源未結清意圖。");
                        Need(f.Scans.PlanId is null, "被拒的新計畫仍改動目前進度。");
                        // A reset clears display/dedup state, not an unknown native
                        // intent. Exercise idempotence after a legitimate new LC
                        // process lifetime while preserving the old source fence.
                        var freshSource = f.Source with { CreationFileTime = f.Source.CreationFileTime + 1,
                            SessionKey = f.Source.SessionKey + "-new-process" };
                        var freshTargets = f.Targets with { SnapshotId = "fresh-reset-targets", SourceSessionKey = freshSource.SessionKey,
                            Owners = f.Targets.Owners.Select(owner => owner with { LocalIdentity = owner.LocalIdentity + "-new-process" }).ToArray() };
                        Need(!f.Scans.BlocksSource(freshSource) && !f.Owners.BlocksSource(freshSource), "原來源未知意圖誤封鎖不同程序生命週期。");
                        var fresh = f.Scans.StartNew(freshSource, freshTargets, true);
                        f.Owners.Begin(freshSource, freshTargets.Owners[0], ShopPageModes.ViewerBuy, "fresh-request", true);
                        f.Owners.Settle("fresh-request", true);
                        string revision = f.Owners.Revision;
                        f.Resets.Complete(tx, f.Owners, f.Scans);
                        Need(f.Resets.RecoverPending(f.Owners, f.Scans) == 0 && f.Owners.Revision == revision && f.Scans.PlanId == fresh.PlanId,
                            "已commit reset重複套用，清了更新的期別或plan。");
                        f.Reopen();
                        Need(f.Scans.PlanId == fresh.PlanId && f.Scans.Source == freshSource && f.Owners.Summary(true, "").SearchedPages == 1,
                            "新plan被舊reset marker遮蔽。");
                        Need(f.Scans.BlocksSource(f.Source) && f.Owners.BlocksSource(f.Source)
                            && !f.Scans.BlocksSource(freshSource) && !f.Owners.BlocksSource(freshSource), "重複reset或重啟誤解除了原unknown或混淆新來源。");
                    }
                    if (scenario == "corrupt_marker")
                    {
                        string marker = Directory.GetFiles(f.Progress, "*.reset.json").Single();
                        File.WriteAllText(marker, "{invalid");
                        f.Scans = new ShopScanProgressStore(f.Progress);
                        Need(f.Scans.Error is not null, "損壞最新reset marker回退成可送舊plan。");
                    }
                }
                f.AssertOriginalEvidence();
                return Task.CompletedTask;
            });
        }

        await Check("reset_actual_broker_contract_and_same_request_id", async () =>
        {
            using var f = new Fixture();
            var fake = new NoReadSource();
            await using var runtime = new HubRuntime(fake, Path.Combine(f.Root, "runtime"), enableTransport: false);
            runtime.SetLogging(false);
            using var reader = new CurrentLcSource(profileDirectory: Path.Combine(f.Root, "absent-profile"));
            await using var broker = new ShopBroker(runtime, reader, f.Shops);
            var request = new ShopCommandRequest { RequestId = "actual-reset", Command = ShopCommands.ResetSearchProgress, ForceDispatch = true };
            var first = await broker.HandleAsync(request);
            Need(first.RequestId == request.RequestId && first.State == "SearchProgressReset" && first.WorkSettled
                && !first.Scan.Running && first.Scan.JobId is null && first.SavedScan is null && first.SearchedOwners.Length == 0
                && first.SearchHistory is { OwnerCount: 0, SearchedPages: 0, ConfirmedPages: 0, UncertainPages: 0 }, "真Broker回覆與UI契約不符。");
            var owners = (ShopSearchedOwnerStore)Get(broker, "searchedOwners");
            owners.Begin(f.Source, f.Targets.Owners[0], ShopPageModes.ViewerBuy, "after-reset", true);
            owners.Settle("after-reset", true);
            string revision = owners.Revision;
            var repeated = await broker.HandleAsync(request);
            Need(repeated == first && owners.Revision == revision && owners.Summary(true, "").SearchedPages == 1, "同RequestId再次清了新搜尋紀錄。");
            Need(fake.Reads == 0 && !runtime.IsRunning, "本機重設意外啟動/讀取來源。");
            f.AssertOriginalEvidence();
        });

        await Check("reset_actual_paused_worker_join_and_live_local_wait_configuration", async () =>
        {
            using var f = new Fixture();
            var fake = new NoReadSource();
            await using var runtime = new HubRuntime(fake, Path.Combine(f.Root, "runtime"), enableTransport: false);
            runtime.SetLogging(false);
            using var reader = new CurrentLcSource(profileDirectory: Path.Combine(f.Root, "absent-profile"));
            await using var broker = new ShopBroker(runtime, reader, f.Shops);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workerStop = new CancellationTokenSource(); // Broker owns its final disposal.
            object sync = Get(broker, "gate");
            Set(broker, "scanStop", workerStop);
            Set(broker, "scan", new MarketScanState { JobId = "offline-paused", Running = true, Paused = true });
            var worker = Task.Run(async () => { await release.Task; lock (sync) Set(broker, "scan", new MarketScanState()); });
            Set(broker, "scanTask", worker);
            var reset = broker.HandleAsync(new() { RequestId = "paused-reset", Command = ShopCommands.ResetSearchProgress });
            try
            {
                Need(workerStop.IsCancellationRequested, "reset未取消目前worker。");
                Need(!reset.IsCompleted && ((ShopScanProgressStore)Get(broker, "savedScans")).PlanId is not null, "清理尚未join就已清plan。");
                broker.ConfigurePageWaitMilliseconds(5000);
                Need(broker.PageWaitMilliseconds == 5000 && !reset.IsCompleted, "本機等待設定被reset/worker barrier阻擋。");
                Reject(() => broker.ConfigurePageWaitMilliseconds(99), "非法等待秒數未拒絕。");
                Need(broker.PageWaitMilliseconds == 5000, "拒絕非法設定仍更改了值。");
            }
            finally { release.TrySetResult(); }
            var result = await reset.WaitAsync(TimeSpan.FromSeconds(10));
            Need(result.State == "SearchProgressReset" && result.WorkSettled && fake.Reads == 0, "paused worker join後reset未完成。");
            f.AssertOriginalEvidence();
        });

        await Check("reset_native_gate_pending_evidence_and_old_new_request_barrier", async () =>
        {
            using var f = new Fixture();
            var fake = new NoReadSource();
            await using var runtime = new HubRuntime(fake, Path.Combine(f.Root, "runtime"), enableTransport: false);
            runtime.SetLogging(false);
            using var reader = new CurrentLcSource(profileDirectory: Path.Combine(f.Root, "absent-profile"));
            await using var broker = new ShopBroker(runtime, reader, f.Shops);
            Set(runtime, "latest", new HubSnapshot { Source = f.Source });
            string nativePending = Path.Combine(f.Shops, "sessions", "4242-987654321.pending.json");
            File.WriteAllText(nativePending, "UNKNOWN_PRESERVE_ORIGINAL");
            string pendingHash = Hash(nativePending);
            var nativeGate = (SemaphoreSlim)Get(broker, "nativeGate");
            await nativeGate.WaitAsync();
            Set(broker, "activeNativeSource", f.Source);
            string priorRevision = ((ShopSearchedOwnerStore)Get(broker, "searchedOwners")).Revision;
            var oldOpen = broker.HandleAsync(new() { RequestId = "old-queued-open", Command = ShopCommands.OpenShop,
                ForceDispatch = true, OwnerId = 21, ViewerDirection = ShopPageModes.ViewerBuy });
            var reset = broker.HandleAsync(new() { RequestId = "native-gate-reset", Command = ShopCommands.ResetSearchProgress });
            var later = broker.HandleAsync(new() { RequestId = "later-clear", Command = ShopCommands.ClearSearchedOwners,
                ForceDispatch = true, ExpectedHistoryRevision = priorRevision });
            try
            {
                var oldResponse = await oldOpen.WaitAsync(TimeSpan.FromSeconds(10));
                Need(oldResponse.State == "Cancelled" && !reset.IsCompleted && !later.IsCompleted,
                    "舊排隊請求未取消，或新工作越過尚未清理的reset barrier。");
                Need(Hash(nativePending) == pendingHash, "等待原生清理時已改動pending。");
            }
            finally
            {
                broker.GetType().GetField("activeNativeSource", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(broker, null);
                nativeGate.Release();
            }
            var response = await reset.WaitAsync(TimeSpan.FromSeconds(10));
            Need(response.State == "SearchProgressReset" && response.WorkSettled && response.SessionBlocked,
                "清除把原生unknown偽稱為已解除，或未等transport清理。");
            Need((await later).State == "HistoryChanged" && Hash(nativePending) == pendingHash && fake.Reads == 0,
                "新工作未在reset後核對期別，或原pending/來源被改動。");
            f.AssertOriginalEvidence();
        });

        return checks.ToArray();

        async Task Check(string name, Func<Task> test)
        {
            try { await test(); checks.Add(new(name, true, "實際持久store／Broker離線合約通過；未啟動讀取器或呼叫原生helper。")); }
            catch (Exception failure) { checks.Add(new(name, false, failure.ToString())); }
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "LC-Market54-reset-" + Guid.NewGuid().ToString("N"));
        internal string Shops => Path.Combine(Root, "shops");
        internal string Progress => Path.Combine(Shops, "scan-progress");
        internal readonly SourceIdentity Source = new(4242, 987654321, "offline-only", "0x100000", 1, 4096, new string('A', 64), "offline-reset-source");
        internal readonly ShopTargetSnapshot Targets;
        internal ShopScanProgressStore Scans;
        internal ShopSearchedOwnerStore Owners;
        internal readonly ShopSearchProgressResetStore Resets;
        internal readonly string OriginalPlanId;
        private readonly Dictionary<string, string> original;
        internal Fixture()
        {
            var at = DateTimeOffset.UtcNow.AddDays(-1);
            MarketOwner Owner(ulong id) => new() { Id = id, LocalIdentity = "owner-" + id, Name = "offline", Position = new(100, 101), ObservedAt = at, ShopState = ShopEligibilityPolicy.ShopActionObserved, ActionType = ".?AVShopAction@lineage@@", ShopStateSampledAt = at, ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence };
            Targets = new() { SnapshotId = "offline-reset-targets", SourceSessionKey = Source.SessionKey, RefreshedAt = at,
                SelfId = 11, SelfPosition = new(100, 100), Owners = [Owner(21), Owner(22)] };
            Scans = new(Progress);
            Owners = new(Path.Combine(Shops, "searched-owners"), Path.Combine(Root, "absent-import"));
            Resets = new(Path.Combine(Shops, "search-progress-resets"));
            OriginalPlanId = Scans.StartNew(Source, Targets, true).PlanId;
            var pages = Scans.PendingPages(Targets, true);
            Scans.BeginAttempt(pages[0], "old-unknown"); Scans.NativeIntent("old-unknown");
            Owners.Begin(Source, Targets.Owners[0], ShopPageModes.ViewerBuy, "old-unknown", true);
            Scans.BeginAttempt(pages[1], "old-sent"); Scans.NativeIntent("old-sent");
            Scans.NativeSettled("old-sent", true, true, "offline returned");
            Scans.Result("old-sent", new() { NativeEntered = true, NativeReturned = true, State = "Unverified" });
            Owners.Begin(Source, Targets.Owners[0], ShopPageModes.ViewerSell, "old-sent", true); Owners.Settle("old-sent", true);
            File.WriteAllText(Path.Combine(Shops, "old-native.pending.json"), "UNKNOWN_DO_NOT_DELETE");
            original = Directory.GetFiles(Shops, "*.json", SearchOption.AllDirectories).ToDictionary(path => path, Hash);
        }
        internal void Reopen()
        {
            Scans = new(Progress);
            Owners = new(Path.Combine(Shops, "searched-owners"), Progress);
            Need(Scans.Error is null && Owners.Error is null, "重啟完整性失敗：" + Scans.Error + Owners.Error);
        }
        internal void AssertOriginalEvidence() => Need(original.All(item => File.Exists(item.Key) && Hash(item.Key) == item.Value), "原始plan/events/intent/ledger/pending被覆蓋或刪除。");
        public void Dispose()
        {
            string path = Path.GetFullPath(Root);
            if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).StartsWith("LC-Market54-reset-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup path");
            Directory.Delete(path, true);
        }
    }
    private sealed class NoReadSource : IMemorySource
    {
        internal int Reads;
        public ReadResult Read(CancellationToken token) { Reads++; throw new InvalidOperationException("Reset must never read LC."); }
        public void Dispose() { }
    }
    private static object Get(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
        ?? throw new InvalidOperationException("Missing offline field: " + name);
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static void Need(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Reject(Action action, string reason)
    {
        try { action(); }
        catch (Exception error) when (error is ShopRequestFailure or InvalidDataException or ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException(reason);
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
}
