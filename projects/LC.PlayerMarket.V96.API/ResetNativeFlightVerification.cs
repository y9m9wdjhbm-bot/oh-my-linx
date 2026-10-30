using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Actual broker/store path with a fake owned helper; no process or reader is started.</summary>
internal static class ResetNativeFlightVerification
{
    internal static int RunReport(string output)
    {
        var checks = RunAsync().GetAwaiter().GetResult();
        var report = new OfflineCheckReport("83.0.0", checks.All(c => c.Passed),
            checks.Count(c => c.Passed), checks.Count(c => !c.Passed), false, false, false, checks);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, HubProtocol.Json); file.Flush(true);
        return report.Success ? 0 : 1;
    }

    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        foreach (bool preparedReset in new[] { false, true })
        {
            string name = "reset_native_flight_timeout_rejected_" + (preparedReset ? "before_recover_pending" : "before_prepare");
            try
            {
                await VerifyAsync(preparedReset);
                checks.Add(new(name, true, "SYNTHETIC helper lifecycle + actual broker/stores; reset rejected, business file hashes unchanged, WorkSettled=false; no LC read or native call."));
            }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        return checks.ToArray();
    }

    private static async Task VerifyAsync(bool preparedReset)
    {
        using var f = new Fixture();
        var fakeSource = new NoReadSource();
        await using var runtime = new HubRuntime(fakeSource, Path.Combine(f.Root, "runtime"), enableTransport: false);
        runtime.SetLogging(false);
        using var reader = new CurrentLcSource(profileDirectory: Path.Combine(f.Root, "absent-profile"));
        await using var broker = new ShopBroker(runtime, reader, f.Shops);
        Set(runtime, "latest", new HubSnapshot { Source = f.Source });
        var scans = (ShopScanProgressStore)Get(broker, "savedScans");
        var owners = (ShopSearchedOwnerStore)Get(broker, "searchedOwners");
        var resets = (ShopSearchProgressResetStore)Get(broker, "searchResets");
        string plan = scans.StartNew(f.Source, f.Targets, true).PlanId;
        scans.BeginAttempt(scans.PendingPages(f.Targets, true)[0], "unknown-before-timeout");
        scans.NativeIntent("unknown-before-timeout");
        owners.Begin(f.Source, f.Targets.Owners[0], ShopPageModes.ViewerBuy, "unknown-before-timeout", true);
        string nativePending = Path.Combine(f.Shops, "sessions", "4242-987654321.pending.json");
        File.WriteAllText(nativePending, "UNKNOWN_PRESERVE_ORIGINAL");
        string pendingHash = Hash(nativePending);
        if (preparedReset) resets.Prepare(owners, scans, "prepared-before-helper-timeout");
        string revision = owners.Revision;
        string[] before = f.BusinessSnapshot();

        // Use the real broker's tracker without replacing its readonly field.
        // Completion is guaranteed in finally so other sequential suite cases
        // cannot inherit an artificial outstanding helper.
        var tracker = (ShopNativeHelperFlight)(typeof(ShopBroker).GetField("nativeHelperFlight",
            BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("Missing actual broker helper tracker"));
        tracker.RequireAvailable();
        var helper = new FakeProcess(); int starts = 0;
        var original = tracker.RunAsync(() => { starts++; return helper; }, TimeSpan.Zero);
        try
        {
            try { await original; throw new InvalidOperationException("Expected synthetic wait timeout"); }
            catch (ShopNativeHelperWaitTimeoutException) { }
            Need(tracker.IsActive && GetNullable(broker, "activeNativeSource") is null
                && ((SemaphoreSlim)Get(broker, "nativeGate")).CurrentCount == 1,
                "fixture未建立page已清理但helper仍活著的條件");

            var result = await broker.HandleAsync(new()
            {
                RequestId = "blocked-reset-" + (preparedReset ? "recover" : "prepare"),
                Command = ShopCommands.ResetSearchProgress, ForceDispatch = true
            }).WaitAsync(TimeSpan.FromSeconds(10));
            Need(result.State == "Rejected" && result.FailureCode == "NativeTransportUnavailable"
                && result.FailureScope == "Global" && result.FailurePhase == "BeforePublish"
                && !result.WorkSettled && result.SessionBlocked && result.SearchHistory is { CanClear: false },
                "存活helper期間Reset沒有拒絕，或謊報WorkSettled/CanClear/來源安全狀態");
            Need(scans.PlanId == plan && owners.Revision == revision && scans.BlocksSource(f.Source)
                && owners.BlocksSource(f.Source) && before.SequenceEqual(f.BusinessSnapshot())
                && Hash(nativePending) == pendingHash,
                "Reset拒絕前已執行RecoverPending/Prepare/Complete或改動business持久檔案");
            var current = await broker.HandleAsync(new() { RequestId = "active-helper-state", Command = ShopCommands.GetState });
            Need(!current.WorkSettled && current.SessionBlocked && current.SearchHistory is { CanClear: false }
                && tracker.IsActive && helper.DisposeCalls == 0 && starts == 1,
                "Reset回覆後仍活著的helper被丟失或重新啟動");
            Need(fakeSource.Reads == 0 && !runtime.IsRunning, "reset regression意外讀取LC");
        }
        finally
        {
            Task joined = tracker.WaitUntilExitAsync(); helper.Exit();
            await joined.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Need(before.SequenceEqual(f.BusinessSnapshot()) && original.IsFaulted && starts == 1,
            "helper晚到exit竄改原unknown進度／pending或重播請求");
        var afterExit = await broker.HandleAsync(new() { RequestId = "after-helper-exit", Command = ShopCommands.GetState });
        Need(afterExit.WorkSettled && afterExit.SessionBlocked && scans.BlocksSource(f.Source)
            && owners.BlocksSource(f.Source) && helper.DisposeCalls == 1,
            "helper真正退出只能settle工作生命週期，不能清掉原unknown鎖");
        // A separate explicit reset is permitted only after transport is idle.
        // Existing completed-reset UI behavior must remain true after gate release.
        var successful = await broker.HandleAsync(new()
        {
            RequestId = "explicit-reset-after-exit", Command = ShopCommands.ResetSearchProgress, ForceDispatch = true
        }).WaitAsync(TimeSpan.FromSeconds(10));
        Need(successful.State == "SearchProgressReset" && successful.WorkSettled && successful.SessionBlocked
            && successful.SavedScan is null && successful.SearchHistory is { OwnerCount: 0, CanClear: false }
            && scans.BlocksSource(f.Source) && owners.BlocksSource(f.Source)
            && Hash(nativePending) == pendingHash && fakeSource.Reads == 0,
            "正常reset沒有以實際gate/flight狀態回覆，或清除了原unknown證據");
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "LC-Market65-flight-reset-SYNTHETIC-" + Guid.NewGuid().ToString("N"));
        internal string Shops => Path.Combine(Root, "shops");
        internal SourceIdentity Source { get; } = new(4242, 987654321, "offline-only", "0x100000", 1, 4096,
            new string('A', 64), "offline-reset-helper-flight");
        internal ShopTargetSnapshot Targets { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            var at = DateTimeOffset.UtcNow.AddDays(-1);
            Targets = new() { SnapshotId = "offline-reset-helper-targets", SourceSessionKey = Source.SessionKey,
                RefreshedAt = at, SelfId = 11, SelfPosition = new(100, 100), Owners = [new()
                {
                    Id = 21, LocalIdentity = "offline-owner-21", Name = "synthetic", Position = new(100, 101), ObservedAt = at,
                    ShopState = ShopEligibilityPolicy.ShopActionObserved, ActionType = ".?AVShopAction@lineage@@",
                    ShopStateSampledAt = at, ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence
                }] };
        }
        // Request response files are expected bookkeeping; only the business
        // stores and native pending are subject to the no-mutation assertion.
        internal string[] BusinessSnapshot() => new[] { "scan-progress", "searched-owners", "search-progress-resets", "sessions", "native" }
            .Select(name => Path.Combine(Shops, name)).Where(Directory.Exists)
            .SelectMany(path => Directory.GetFiles(path, "*.json", SearchOption.AllDirectories))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Shops, path) + ":" + Hash(path)).ToArray();
        public void Dispose()
        {
            string full = Path.GetFullPath(Root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("LC-Market65-flight-reset-SYNTHETIC-", StringComparison.Ordinal))
                throw new IOException("Synthetic cleanup path rejected");
            Directory.Delete(full, true);
        }
    }
    private sealed class FakeProcess : IShopNativeHelperProcess
    {
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasExited { get; private set; }
        public int ExitCode => HasExited ? 0 : throw new InvalidOperationException("still running");
        internal int DisposeCalls { get; private set; }
        public Task WaitForExitAsync() => exited.Task;
        public void Dispose() { DisposeCalls++; }
        internal void Exit() { HasExited = true; exited.TrySetResult(); }
    }
    private sealed class NoReadSource : IMemorySource
    {
        internal int Reads;
        public ReadResult Read(CancellationToken token) { Reads++; throw new InvalidOperationException("Reset must never read LC."); }
        public void Dispose() { }
    }
    private static object Get(object owner, string field) => GetNullable(owner, field)
        ?? throw new InvalidOperationException("Missing offline field: " + field);
    private static object? GetNullable(object owner, string field) => owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner);
    private static void Set(object owner, string field, object value) => owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void Need(bool pass, string detail) { if (!pass) throw new InvalidOperationException(detail); }
}
