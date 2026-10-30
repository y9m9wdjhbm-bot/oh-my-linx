using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static class DispatchTimeoutVerification
{
    internal static async Task<int> RunAsync(string output)
    {
        var checks = ShopDispatchTimeoutSelfTest.Run().Select(c => new OfflineCheck(c.Name, c.Passed, c.Detail))
            .Concat(BrokerChecks()).Concat(await PatrolAutoResumeSelfTest.RunAsync()).ToArray();
        var report = new OfflineCheckReport("83.0.0", checks.All(c => c.Passed),
            checks.Count(c => c.Passed), checks.Count(c => !c.Passed), false, false, false, checks);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(file, report, HubProtocol.Json);
        file.Flush(true);
        return report.Success ? 0 : 1;
    }

    internal static OfflineCheck[] BrokerChecks()
    {
        var checks = new List<OfflineCheck>();
        var source = new SourceIdentity(4242, 987654321, "offline-LC", "0x100000", 1, 4096,
            new string('A', 64), "offline-dispatch-timeout");
        var now = DateTimeOffset.UtcNow;
        ShopDispatchReadTimeoutException Timeout(int attempts = 3, double elapsed = 3900, SourceIdentity? original = null)
            => (ShopDispatchReadTimeoutException)Activator.CreateInstance(typeof(ShopDispatchReadTimeoutException),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [original ?? source, attempts, elapsed, "offline complete typed timeout"], null)!;
        void Check(string name, Action run)
        {
            try { run(); checks.Add(new(name, true, "isolated fake progress; native calls=0")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        static void Need(bool pass, string detail) { if (!pass) throw new InvalidOperationException(detail); }

        Check("dispatch_timeout_current_page_is_not_sent_while_older_pending_is_preserved", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "LC-Market64-dispatch-timeout-" + Guid.NewGuid().ToString("N"));
            var store = new ShopScanProgressStore(root);
            var owner = new MarketOwner { Id = 22, LocalIdentity = "offline-owner", Name = "offline",
                Position = new(100, 101), ObservedAt = now, ActionType = ".?AVShopAction@lineage@@",
                ShopState = ShopEligibilityPolicy.ShopActionObserved, ShopStateSampledAt = now,
                ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence };
            var targets = new ShopTargetSnapshot { SnapshotId = "offline-targets", SourceSessionKey = source.SessionKey,
                RefreshedAt = now, SelfId = 11, SelfPosition = new(100, 100), Owners = [owner], CanDispatch = true };
            store.StartNew(source, targets, forceDispatch: true);
            var pages = store.PendingPages(targets, forceDispatch: true);
            store.BeginAttempt(pages[0], "older-pending"); store.NativeIntent("older-pending");
            string[] intents = Directory.GetFiles(Path.Combine(root, "source-intents"));
            var before = intents.ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            store.BeginAttempt(pages[1], "deadline-before-publish");
            var response = ShopForceDispatchPolicy.RejectUnpublishedReadTimeout(Timeout(), source, true, true, source,
                new() { RequestId = "deadline-before-publish", State = "Rejected", SessionBlocked = true, WorkSettled = true });
            Need(response.FailureCode == "DispatchReadTimeout" && response.FailureScope == "Global"
                && response.FailurePhase == "BeforePublish" && !response.SessionBlocked && !response.NativeEntered
                && !response.NativeReturned && !response.ResponseVerified, "typed送出前拒絕錯誤地繼承舊未明結果。");
            store.Result(response.RequestId, response);
            var saved = store.View(source, targets, false, true, forceDispatch: true)!;
            Need(saved.UncertainPages == 1 && saved.RemainingPages == 1 && saved.SearchedPages == 0
                && store.BlocksSource(source), $"舊pending被清除或新頁污染：uncertain={saved.UncertainPages}; remaining={saved.RemainingPages}");
            Need(Directory.GetFiles(Path.Combine(root, "source-intents")).Length == before.Count
                && before.All(pair => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value),
                "唯讀失敗修改了舊原生意圖或新增新頁原生意圖。");
            Need(ShopScanPolicy.Decide(response, true, forceDispatch: true) == ShopScanDisposition.StopBatch,
                "恢復資格應交給已結束掃描的巡迴流程，不可直接跳過此頁當成功。");
            // Keep the isolated evidence for the administrator-run report; never touch live data.
        });
        foreach (string invalid in new[] { "unexhausted", "too-many-attempts", "timer", "expected-source", "current-source",
            "runtime", "configuration", "entered", "returned", "response" })
            Check("dispatch_timeout_rejects_incomplete_before_publish_proof_" + invalid, () =>
            {
                var timeout = invalid switch
                {
                    "unexhausted" => Timeout(0), "too-many-attempts" => Timeout(4), "timer" => Timeout(elapsed: double.NaN),
                    "expected-source" => Timeout(original: source with { SessionKey = "other" }), _ => Timeout()
                };
                var response = new ShopCommandResponse { NativeEntered = invalid == "entered", NativeReturned = invalid == "returned",
                    ResponseVerified = invalid == "response" };
                bool rejected = false;
                try { ShopForceDispatchPolicy.RejectUnpublishedReadTimeout(timeout, source, invalid != "runtime", invalid != "configuration",
                    invalid == "current-source" ? source with { CreationFileTime = 99 } : source, response); }
                catch (ShopRequestFailure error) { rejected = error.Failure.Code == "SourceUnavailable"; }
                Need(rejected, "不完整送出前證據獲得 DispatchReadTimeout 恢復資格。");
            });
        Check("dispatch_timeout_absent_background_source_is_not_a_source_claim", () =>
        {
            var response = ShopForceDispatchPolicy.RejectUnpublishedReadTimeout(Timeout(), source, true, true, null,
                new() { SessionBlocked = true });
            Need(response.FailureCode == "DispatchReadTimeout" && response.CurrentSourceSessionKey is null
                && !response.NativeEntered && !response.NativeReturned, "缺失背景來源被填入歷史來源或送出旗標。");
        });
        return checks.ToArray();
    }
}
