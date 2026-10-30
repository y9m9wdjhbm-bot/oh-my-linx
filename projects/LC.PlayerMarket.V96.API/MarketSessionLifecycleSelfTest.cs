using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static class MarketSessionLifecycleSelfTest
{
    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        string fixtureOwner = Path.Combine(MarketLogPaths.DefaultRoot, "verification-fixtures", "session-lifecycle" );
        string fixture = Path.Combine(fixtureOwner, "LC-MarketSession-V55-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        async Task Check(string name, Func<Task> work)
        {
            try { await work(); checks.Add(new(name, true, "通過；隔離檔案 fixture，沒有開啟 LC")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        try
        {
            await Check("session_fresh_start_never_parses_old_products_and_preserves_preferences", async () =>
            {
                string source = Path.Combine(fixture, "fresh", "private"), mirror = Path.Combine(fixture, "fresh", "public");
                Directory.CreateDirectory(Path.Combine(source, "history")); Directory.CreateDirectory(Path.Combine(mirror, "history"));
                File.WriteAllText(Path.Combine(source, "history", "old-corrupt.json"), "this is not JSON");
                File.WriteAllText(Path.Combine(mirror, "history", "old-corrupt.json"), "this is not JSON");
                var preserved = new[] { "settings.json", "patrol-route.json", "stall-draft.json", Path.Combine("logs", "old.jsonl"), Path.Combine("standalone", "reader", "diagnostic.jsonl") };
                foreach (string name in preserved) { string path = Path.Combine(source, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "preserved-" + name); }
                Directory.CreateDirectory(Path.Combine(source, "standalone", "shops"));
                File.WriteAllText(Path.Combine(source, "standalone", "shops", "uncertain-old.json"), "never parse old intent");
                Directory.CreateDirectory(Path.Combine(source, "patrol-progress-v52"));
                File.WriteAllText(Path.Combine(source, "patrol-progress-v52", "old.json"), "never parse old checkpoint");
                var store = new LocalStore(source, mirror); var errors = new List<string>(); store.Error += errors.Add;
                using var session = MarketSessionLifecycle.BeginFresh(store);
                Need(store.LoadHistory().Length == 0 && store.LastHistoryParsedFiles == 0 && errors.Count == 0, "啟動讀取了損壞舊商品");
                Need(!Directory.EnumerateFileSystemEntries(store.HistoryDirectory).Any() && !Directory.EnumerateFileSystemEntries(Path.Combine(mirror, "history")).Any(), "產品目錄沒有清空");
                Need(session.RuntimeRoot.StartsWith(Path.Combine(source, "sessions-v55") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "runtime 並未隔離");
                Need(!Directory.Exists(Path.Combine(source, "standalone", "shops")) && !Directory.Exists(Path.Combine(source, "patrol-progress-v52")), "legacy 商店狀態仍在原路徑");
                foreach (string name in preserved) Need(File.ReadAllText(Path.Combine(source, name)) == "preserved-" + name, "保留檔被改寫：" + name);
                using var localState = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "list-state.json")));
                using var publicState = JsonDocument.Parse(File.ReadAllText(Path.Combine(mirror, "list-state.json")));
                Need(localState.RootElement.GetProperty("resumeAfter").GetDateTimeOffset() == session.StartedAt && publicState.RootElement.GetProperty("resumeAfter").GetDateTimeOffset() == session.StartedAt, "新 session 未以同一時間恢復收錄");
                Need((await session.CleanupAfterStopAsync(true)).Success, "清理失敗");
            });
            await Check("session_close_preserves_products_until_next_fresh_start", async () =>
            {
                string source = Path.Combine(fixture, "restart", "private"), mirror = Path.Combine(fixture, "restart", "public");
                var store = new LocalStore(source, mirror);
                string firstRoot;
                using (var first = MarketSessionLifecycle.BeginFresh(store))
                {
                    firstRoot = first.RuntimeRoot;
                    Directory.CreateDirectory(Path.Combine(firstRoot, "standalone", "shops"));
                    File.WriteAllText(Path.Combine(firstRoot, "standalone", "shops", "shop-search.json"), "runtime state");
                    Need(store.SaveListing(Page(first.StartedAt.AddSeconds(1))), "fixture 商品沒有保存");
                    var result = await first.CleanupAfterStopAsync(true);
                    Need(result.Success && !Directory.Exists(firstRoot), "正常關閉沒有刪除商店搜尋紀錄");
                    Need(Directory.GetFiles(store.HistoryDirectory, "*.json").Length == 1 && Directory.GetFiles(Path.Combine(mirror, "history"), "*.json").Length == 1, "關閉過早刪除網頁商品");
                }
                using var second = MarketSessionLifecycle.BeginFresh(new LocalStore(source, mirror));
                Need(second.RuntimeRoot != firstRoot && Directory.GetFiles(Path.Combine(source, "history"), "*.json").Length == 0 && Directory.GetFiles(Path.Combine(mirror, "history"), "*.json").Length == 0, "重開未清除雙邊商品或重用 runtime");
                Need((await second.CleanupAfterStopAsync(true)).Success, "第二次 session 清理失敗");
            });
            await Check("session_navigation_diagnostics_survive_close_and_next_fresh_start", async () =>
            {
                string source = Path.Combine(fixture, "navigation-diagnostics");
                var store = new LocalStore(source);
                string diagnosticsRoot = MarketLogPaths.Navigation(store.DiagnosticsRoot);
                string diagnosticRun = Path.Combine(diagnosticsRoot, DateTimeOffset.UtcNow.UtcTicks.ToString("D19", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
                string diagnosticFile = Path.Combine(diagnosticRun, "log", "adapter.jsonl");
                const string diagnostic = "{\"code\":\"READ_CAPTURE_FAILURE_DETAIL\",\"detail\":\"preserve exact failure evidence\"}\n";
                string firstRoot;
                using (var first = MarketSessionLifecycle.BeginFresh(store))
                {
                    firstRoot = first.RuntimeRoot;
                    Directory.CreateDirectory(Path.GetDirectoryName(diagnosticFile)!);
                    File.WriteAllText(diagnosticFile, diagnostic);
                    File.WriteAllText(Path.Combine(firstRoot, "current-shop-intent.json"), "ephemeral runtime state");
                    var result = await first.CleanupAfterStopAsync(true);
                    Need(result.Success && !Directory.Exists(firstRoot), "正常關閉沒有清除本次 runtime");
                    Need(File.Exists(diagnosticFile) && File.ReadAllText(diagnosticFile) == diagnostic, "正常關閉遺失或改寫導航診斷明細");
                }
                using var second = MarketSessionLifecycle.BeginFresh(new LocalStore(source));
                var background = await second.BackgroundCleanup;
                Need(background.Success && second.RuntimeRoot != firstRoot && !Directory.EnumerateFileSystemEntries(second.RuntimeRoot).Any(), "新 session 重用了舊 runtime 或背景清理失敗");
                Need(File.Exists(diagnosticFile) && File.ReadAllText(diagnosticFile) == diagnostic, "下次啟動遺失或改寫導航診斷明細");
                Need((await second.CleanupAfterStopAsync(true)).Success && File.Exists(diagnosticFile) && File.ReadAllText(diagnosticFile) == diagnostic, "第二次關閉遺失或改寫導航診斷明細");
            });
            await Check("session_actual_backend_reader_and_native_diagnostics_survive_clean_stop_and_fresh_start", async () =>
            {
                string source = Path.Combine(fixture, "backend-diagnostics");
                string sharedDiagnostics = Path.Combine(fixture, "shared-version-independent-logs");
                var store = new LocalStore(source, diagnosticsRoot: sharedDiagnostics);
                string firstRuntime, firstDiagnostics;
                Dictionary<string, string> preserved;
                using (var first = MarketSessionLifecycle.BeginFresh(store))
                {
                    firstRuntime = first.RuntimeRoot; firstDiagnostics = first.DiagnosticsRoot;
                    Need(firstDiagnostics == Path.Combine(sharedDiagnostics, "market-runtime", Path.GetFileName(firstRuntime)), "診斷根目錄沒有使用跨版本固定位置與本次唯一 session 名稱");
                    await RunBackend(first, "FIRST_SESSION_DIAGNOSTIC");
                    // Runtime state remains disposable and must never be copied into diagnostics.
                    string oldPending = Path.Combine(firstRuntime, "standalone", "shops", "sessions", "fixture-only.pending.json");
                    File.WriteAllText(oldPending, "invalid fixture-only intent; never replay");
                    preserved = Directory.GetFiles(firstDiagnostics, "*", SearchOption.AllDirectories)
                        .ToDictionary(path => path, path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
                    Need(preserved.Count >= 2 && preserved.Keys.All(path => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)), "永久診斷夾缺LOG或混入設定／pending狀態");
                    Need((await first.CleanupAfterStopAsync(true)).Success && !Directory.Exists(firstRuntime), "後端停止後沒有清除runtime");
                    Need(preserved.All(pair => File.Exists(pair.Key) && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value), "關閉刪除或改寫Reader/native診斷LOG");
                }
                using var second = MarketSessionLifecycle.BeginFresh(new LocalStore(source, diagnosticsRoot: sharedDiagnostics));
                Need(second.RuntimeRoot != firstRuntime && second.DiagnosticsRoot != firstDiagnostics
                    && !Directory.EnumerateFileSystemEntries(second.RuntimeRoot).Any(), "新session重用舊狀態或舊診斷輸出");
                Need((await second.BackgroundCleanup).Success, "新session背景清理失敗");
                await RunBackend(second, "SECOND_SESSION_DIAGNOSTIC");
                Need((await second.CleanupAfterStopAsync(true)).Success && !Directory.Exists(second.RuntimeRoot), "第二次正常停止未清除runtime");
                Need(preserved.All(pair => File.Exists(pair.Key) && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value), "下次啟動／停止刪除、重用或改寫前次診斷LOG");

                async Task RunBackend(MarketSessionLifecycle session, string sentinel)
                {
                    // CurrentLcSource is never used for a read: only GetState is allowed by the wrapper.
                    using var unusedNativeSource = new CurrentLcSource(profileDirectory: Path.Combine(fixture, "unused-diagnostic-profile"), itemStateContradictionDirectory: Path.Combine(fixture, "unused-negative-ledger"));
                    ShopBroker? actualBroker = null;
                    string? nativePath = null;
                    string stateRoot = Path.Combine(session.RuntimeRoot, "standalone");
                    await using (var backend = new StandaloneMarketBackend(stateRoot, _ => new DiagnosticReadSource(), (runtime, _, shops) =>
                    {
                        actualBroker = new ShopBroker(runtime, unusedNativeSource, shops,
                            nativeLogDirectory: Path.Combine(session.DiagnosticsRoot, "native"));
                        runtime.RecordLog(new(DateTimeOffset.UtcNow, sentinel, "isolated diagnostic lifecycle", new { NativeRequests = 0 }));
                        return new DiagnosticStateOnlyCommands(actualBroker);
                    }, diagnosticsRoot: session.DiagnosticsRoot))
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        var response = await backend.SendAsync(new() { Command = ShopCommands.GetState, RequestId = "diagnostic-" + Guid.NewGuid().ToString("N") }, deadline.Token);
                        Need(response.State == "State" && response.SavedScan is null && response.TargetSnapshot is null
                            && !response.NativeEntered && !response.NativeReturned, "新session採用了舊LOG或意外送出命令");
                        Need(actualBroker is not null, "未建立真實broker fixture");
                        nativePath = actualBroker!.NewNativeLogPath();
                        Need(Path.GetDirectoryName(nativePath) == Path.Combine(session.DiagnosticsRoot, "native"), "真實broker未使用永久native診斷路徑");
                        Need(nativePath != actualBroker.NewNativeLogPath(), "不同native工作重用同一LOG路徑");
                        using var native = new FileStream(nativePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        byte[] content = System.Text.Encoding.UTF8.GetBytes(sentinel + " native fixture only; no helper invoked\n");
                        native.Write(content); native.Flush(true);
                    }
                    string readerRoot = Path.Combine(session.DiagnosticsRoot, "reader");
                    string[] readerLogs = Directory.GetFiles(readerRoot, "hub-*.jsonl");
                    Need(readerLogs.Length > 0 && readerLogs.Any(path => File.ReadAllText(path).Contains(sentinel, StringComparison.Ordinal)), "實際HubRuntime未將完整診斷寫到永久目錄");
                    Need(File.Exists(nativePath), "後端停止時刪除native LOG");
                    Need(File.Exists(Path.Combine(stateRoot, "reader", "settings.json")) && !File.Exists(Path.Combine(readerRoot, "settings.json")), "Reader設定被移入永久診斷目錄");
                    string oldNativeRoot = Path.Combine(stateRoot, "shops", "native");
                    Need(Directory.GetFiles(Path.Combine(stateRoot, "reader"), "hub-*.jsonl").Length == 0
                        && (!Directory.Exists(oldNativeRoot) || Directory.GetFiles(oldNativeRoot, "*.jsonl").Length == 0), "Reader或native診斷仍寫在將被清除的runtime");
                }
            });
            await Check("session_uncertain_shutdown_preserves_runtime_and_crash_reopen_ignores_it", async () =>
            {
                string source = Path.Combine(fixture, "uncertain"); string oldRoot;
                using (var old = MarketSessionLifecycle.BeginFresh(new LocalStore(source)))
                {
                    oldRoot = old.RuntimeRoot; File.WriteAllText(Path.Combine(oldRoot, "old-intent.json"), "invalid old intent");
                    Need(!(await old.CleanupAfterStopAsync(false)).Success && File.Exists(Path.Combine(oldRoot, "old-intent.json")), "未確認停止卻清掉 runtime");
                }
                using var fresh = MarketSessionLifecycle.BeginFresh(new LocalStore(source));
                Need(fresh.RuntimeRoot != oldRoot && !Directory.EnumerateFileSystemEntries(fresh.RuntimeRoot).Any(), "crash 重開重播舊狀態");
                Need((await fresh.CleanupAfterStopAsync(true)).Success && !Directory.Exists(oldRoot), "孤兒狀態沒有清理");
            });
            await Check("session_locked_old_retired_does_not_block_current_runtime_cleanup", async () =>
            {
                const string prefix = "LC-MarketDeferred-V55-";
                string source = Path.Combine(fixture, prefix + Guid.NewGuid().ToString("N"));
                string retired = Path.Combine(source, ".retired-history-v55-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(retired); string lockedPath = Path.Combine(retired, "old-product.json");
                File.WriteAllText(lockedPath, "unparsed retired product");
                FileStream? held = null; MarketSessionLifecycle? session = null;
                try
                {
                    // FileShare.Read permits readers but excludes deletion, as
                    // happens while another viewer retains a retired page file.
                    held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    session = MarketSessionLifecycle.BeginFresh(new LocalStore(source));
                    string currentRoot = session.RuntimeRoot;
                    File.WriteAllText(Path.Combine(currentRoot, "current-shop-intent.json"), "current runtime");
                    var background = await session.BackgroundCleanup;
                    Need(!background.Success && background.Errors.Any(error => error.Contains(retired, StringComparison.OrdinalIgnoreCase))
                        && File.Exists(lockedPath), "舊退役檔鎖定未產生明確背景清理錯誤，或假報背景成功");
                    var result = await session.CleanupAfterStopAsync(true);
                    Need(result.Success && result.Errors.Length == 0 && result.DeferredErrors.Length == background.Errors.Length
                        && result.DeferredErrors.Any(error => error.Contains(retired, StringComparison.OrdinalIgnoreCase))
                        && !Directory.Exists(currentRoot) && Directory.Exists(retired) && File.Exists(lockedPath),
                        "背景延後錯誤阻擋本次runtime清理，或遺失錯誤／刪掉仍鎖定的舊檔／假報當次成功");
                }
                finally
                {
                    held?.Dispose(); session?.Dispose();
                    MarketSessionLifecycle.DeleteFixtureDirectory(source, fixture, prefix);
                }
            });
            await Check("session_overlapping_export_is_rejected_before_mutation", () =>
            {
                string source = Path.Combine(fixture, "overlap"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "sentinel"), "preserve");
                foreach (string destination in new[] { source, Path.Combine(source, "nested"), fixture })
                {
                    try { _ = new LocalStore(source, destination); throw new InvalidOperationException("相交路徑未拒絕"); }
                    catch (InvalidDataException) { }
                }
                Need(File.ReadAllText(Path.Combine(source, "sentinel")) == "preserve", "相交目錄已被修改"); return Task.CompletedTask;
            });
            await Check("session_parallel_open_and_outside_retirement_are_rejected", async () =>
            {
                string source = Path.Combine(fixture, "lease"), outside = Path.Combine(fixture, "outside-retirement");
                Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "sentinel"), "preserve");
                using var session = MarketSessionLifecycle.BeginFresh(new LocalStore(source));
                try { using var duplicate = MarketSessionLifecycle.BeginFresh(new LocalStore(source)); throw new InvalidOperationException("第二個 active session 未拒絕"); }
                catch (IOException) { }
                try { MarketSessionLifecycle.RetireIfPresent(outside, source, "history"); throw new InvalidOperationException("外部路徑未拒絕"); }
                catch (InvalidDataException) { }
                Need(Directory.Exists(session.RuntimeRoot) && File.ReadAllText(Path.Combine(outside, "sentinel")) == "preserve", "重複開啟或外部退役已改寫資料");
                Need((await session.CleanupAfterStopAsync(true)).Success, "session 清理失敗");
            });
            await Check("session_history_link_is_rejected_without_touching_target", () =>
            {
                string source = Path.Combine(fixture, "linked-source"), outside = Path.Combine(fixture, "outside-products");
                Directory.CreateDirectory(source); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "sentinel.json"), "preserve");
                string link = Path.Combine(source, "history"); Directory.CreateSymbolicLink(link, outside);
                try
                {
                    try { using var rejected = MarketSessionLifecycle.BeginFresh(new LocalStore(source)); throw new InvalidOperationException("商品連結未拒絕"); }
                    catch (InvalidDataException) { }
                    Need(File.ReadAllText(Path.Combine(outside, "sentinel.json")) == "preserve", "刪除了連結外的商品");
                }
                finally { Directory.Delete(link); }
                return Task.CompletedTask;
            });
            await Check("session_cleanup_rejects_runtime_link_and_does_not_claim_success", async () =>
            {
                string source = Path.Combine(fixture, "linked-runtime"), outside = Path.Combine(fixture, "outside-runtime");
                Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "sentinel"), "preserve");
                using var session = MarketSessionLifecycle.BeginFresh(new LocalStore(source)); string link = Path.Combine(session.RuntimeRoot, "escape");
                Directory.CreateSymbolicLink(link, outside);
                try { var result = await session.CleanupAfterStopAsync(true); Need(!result.Success && result.Errors.Length > 0 && File.ReadAllText(Path.Combine(outside, "sentinel")) == "preserve", "清理連結被忽略或假報成功"); }
                finally { Directory.Delete(link); }
            });
        }
        finally
        {
            // The fixture has a new GUID and no remaining symbolic links.
            MarketSessionLifecycle.DeleteFixtureDirectory(fixture, fixtureOwner, "LC-MarketSession-V55-");
        }
        return checks.ToArray();
    }

    internal static async Task<int> RunReportAsync(string output)
    {
        var checks = await RunAsync();
        var report = new OfflineCheckReport("90.0.0", checks.All(value => value.Passed), checks.Count(value => value.Passed), checks.Count(value => !value.Passed), false, false, false, checks);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { JsonSerializer.Serialize(file, report, HubProtocol.Json); file.Flush(true); }
        return report.Success ? 0 : 1;
    }

    private sealed class DiagnosticReadSource : ISlicedMemorySource
    {
        public void Configure(ReadSelection selection) { }
        public ReadResult Read(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return new(null, new(), "Unknown", "isolated fake source; no process access"); }
        public ReadResult ReadSlice(ReadSelection selected, CancellationToken token) => Read(token);
        public void Dispose() { }
    }

    private sealed class DiagnosticStateOnlyCommands(ShopBroker broker) : IStandaloneShopCommands
    {
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            if (request.Command != ShopCommands.GetState) throw new InvalidOperationException("diagnostic fixture forbids game commands");
            return broker.HandleAsync(request, token);
        }
        public ValueTask DisposeAsync() => broker.DisposeAsync();
    }

    private static MarketListing Page(DateTimeOffset at) => new()
    {
        Revision = "session-product", RequestId = "session-query", SourceSessionKey = "offline-fixture",
        Owner = new() { Id = 1, Name = "隔離測試商店", LocalIdentity = "fixture-owner", ObservedAt = at },
        ViewerDirection = ShopPageModes.ViewerBuy, RequestedAt = at, CapturedAt = at,
        State = "OwnerAndResponseMatched", OwnerAndResponseMatched = true, Complete = true
    };
}
