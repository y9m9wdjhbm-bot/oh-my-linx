using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Synthetic consumer/count/display checks. No native receipt is certified here.</summary>
internal static class NotEnteredPageVerification
{
    internal static int RunReport(string output)
    {
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        if (!admin) throw new InvalidOperationException("ADMIN_REQUIRED");
        var checks = Run();
        const string source = @"D:\lineage\LC_UpdateExecution_20261021_v1\market-v1\LC_PlayerMarket_v85\source-v1";
        string[] files = ["engine/Core/ShopCommandContracts.cs", "engine/Core/ShopNotEnteredPageRules.cs",
            "engine/Broker/ShopEmptyPagePolicy.cs", "MarketFailurePresentation.cs", "SearchOverlayPresentation.cs",
            "NotEnteredPageVerification.cs"];
        string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
        var report = new { Success = checks.All(c => c.Passed), IsAdministrator = admin,
            Passed = checks.Count(c => c.Passed), Failed = checks.Count(c => !c.Passed), Checks = checks,
            Source = source, SourceHashes = files.ToDictionary(f => Path.Combine(source, f), f => Hash(Path.Combine(source, f))),
            TestedAssembly = typeof(NotEnteredPageVerification).Assembly.Location,
            TestedAssemblySha256 = Hash(typeof(NotEnteredPageVerification).Assembly.Location),
            LCReads = 0, NativeCalls = 0, GameInput = 0, CreatedWindow = false,
            Scope = "Consumer classification/count/display only; full native receipt verification is a separate required gate." };
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true);
        return report.Success ? 0 : 1;
    }

    internal static OfflineCheck[] Run()
    {
        var checks = new List<OfflineCheck>();
        void Check(string name, Action action)
        {
            try { action(); checks.Add(new("not_entered_" + name, true, "Synthetic consumer contract; no LC/native/input.")); }
            catch (Exception error) { checks.Add(new("not_entered_" + name, false, error.ToString())); }
        }
        static void Need(bool ok, string detail) { if (!ok) throw new InvalidOperationException(detail); }
        var at = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        var baseline = new MarketScanState { JobId = "scan", SourceSessionKey = "same-source", Total = 10,
            Completed = 5, SkippedHistoryPages = 1, Succeeded = 2, EmptyObservedPages = 1, Failed = 1, Running = true };
        var noEntry = new ShopCommandResponse { RequestId = "page-request", State = "NativeRejected", At = at,
            CurrentSourceSessionKey = "same-source", FailureCode = "NativeGuardRejected", FailureScope = "Page",
            FailurePhase = "NativeGuard", Detail = "原生來源檢查拒絕；確定未進入，具體子檢查未知。",
            Scan = baseline, WorkSettled = false };
        Check("active_scan_and_settled_outer_work_have_same_page_class", () =>
        {
            Need(ShopNotEnteredPageRules.IsNotEnteredResponse(noEntry), "Active scan no-entry rejected.");
            Need(ShopNotEnteredPageRules.IsNotEnteredResponse(noEntry with { WorkSettled = true }), "Outer settlement changed page classification.");
        });
        Check("background_source_gap_does_not_revoke_bound_no_entry_receipt", () =>
        {
            var missing = noEntry with { CurrentSourceSessionKey = null };
            Need(ShopNotEnteredPageRules.IsNotEnteredResponse(missing), "Background gap revoked verified per-request classification.");
            var count = ShopEmptyPagePolicy.CountPage(baseline, missing);
            Need(count.NotEnteredPages == 1 && count.Failed == 1 && count.Succeeded == 2 && count.EmptyObservedPages == 1,
                "Source gap changed exclusive page accounting.");
            var view = SearchOverlayPresentation.Build(missing, true, "", false, forceDispatch: true);
            Need(view.State == "本頁確定未執行" && view.Progress == "尚無目前批次進度" && !view.Progress.Contains("5/10"),
                "No-entry classification fabricated current source/progress.");
        });
        (string Name, ShopCommandResponse Value)[] invalid = [
            ("missing_code", noEntry with { FailureCode = null }), ("missing_scope", noEntry with { FailureScope = null }),
            ("missing_phase", noEntry with { FailurePhase = null }), ("wrong_state", noEntry with { State = "Unverified" }),
            ("unknown_code", noEntry with { FailureCode = "NativeOutcomeUnknown" }), ("global_scope", noEntry with { FailureScope = "Global" }),
            ("wrong_phase", noEntry with { FailurePhase = "BeforePublish" }), ("wrong_schema", noEntry with { Schema = -1 }),
            ("missing_request", noEntry with { RequestId = "" }),
            ("missing_time", noEntry with { At = default }), ("missing_reason", noEntry with { Detail = "" }),
            ("invented_rva", noEntry with { FailureRva = "0x72D300" }), ("entered", noEntry with { NativeEntered = true }),
            ("returned", noEntry with { NativeReturned = true }), ("response_verified", noEntry with { ResponseVerified = true }),
            ("empty_flag", noEntry with { EmptyPageObserved = true }), ("empty_evidence", noEntry with { EmptyPageObservation = new() }),
            ("blocked", noEntry with { SessionBlocked = true }) ];
        foreach (var item in invalid)
            Check("reject_" + item.Name, () =>
            {
                Need(!ShopNotEnteredPageRules.IsNotEnteredResponse(item.Value), "Contradiction accepted: " + item.Name);
                var result = ShopEmptyPagePolicy.CountPage(baseline, item.Value);
                Need(result.Completed == 6 && result.NotEnteredPages == 0 && result.Failed == 2
                    && result.Succeeded == 2 && result.EmptyObservedPages == 1, "Contradictory claim escaped failure accounting.");
            });
        Check("exclusive_counter_and_history_preserved", () =>
        {
            var result = ShopEmptyPagePolicy.CountPage(baseline, noEntry);
            Need(result.Completed == 6 && result.NotEnteredPages == 1 && result.Failed == 1 && result.Succeeded == 2
                && result.EmptyObservedPages == 1 && result.SkippedHistoryPages == 1 && result.Running,
                "No-entry counted as success/empty/failure or changed history/work.");
            Need(result.Completed == result.SkippedHistoryPages + result.Succeeded + result.EmptyObservedPages + result.NotEnteredPages + result.Failed,
                "Four-way accounting lost a page.");
        });
        Check("existing_success_unknown_and_neutral_accounting", () =>
        {
            var success = new ShopCommandResponse { ResponseVerified = true };
            Need(ShopEmptyPagePolicy.CountPage(baseline, success).Succeeded == 3, "Matched page changed.");
            var unknown = new ShopCommandResponse { FailureCode = "NativeOutcomeUnknown", FailureScope = "Session", SessionBlocked = true };
            Need(ShopEmptyPagePolicy.CountPage(baseline, unknown).Failed == 2, "Unknown no longer fails.");
            var e = new ShopEmptyPageObservation { RequestId = "neutral", SourceSessionKey = "same-source", OwnerId = 22,
                RequestedDirection = ShopPageModes.ViewerBuy, RequestedAt = at.AddMilliseconds(-200), BaselineSampledAt = at.AddMilliseconds(-300),
                SampledAt = at.AddMilliseconds(-100), ValidatedAt = at, LayoutState = "Present", BankState = "Closed", Complete = true,
                BankFingerprint = new string('A', 64), NativeLifecycleSettled = true,
                BankSummaries = [new(0, ShopPageModes.ViewerBuy, 2, 0, 0, new string('B', 64)), new(1, ShopPageModes.ViewerSell, 1, 0, 0, new string('C', 64))],
                Slots = Enumerable.Range(0, 2).SelectMany(b => Enumerable.Range(0, 8).Select(r => new ShopEmptySlotObservation(b, r, "EmptyPair", false))).ToArray(),
                ExcludedRowIndices = Enumerable.Range(0, 8).ToArray() };
            var neutral = new ShopCommandResponse { RequestId = "neutral", CurrentSourceSessionKey = "same-source", At = at,
                State = ShopEmptyPageRules.State, NativeEntered = true, NativeReturned = true, EmptyPageObserved = true, EmptyPageObservation = e };
            Need(ShopEmptyPageRules.IsNeutralResponse(neutral), "Neutral fixture invalid.");
            var count = ShopEmptyPagePolicy.CountPage(baseline, neutral);
            Need(count.EmptyObservedPages == 2 && count.NotEnteredPages == 0 && count.Failed == 1, "Neutral category changed.");
        });
        Check("json_new_count_roundtrip_and_legacy_default", () =>
        {
            var count = ShopEmptyPagePolicy.CountPage(baseline, noEntry);
            string json = JsonSerializer.Serialize(count, HubProtocol.Json);
            var restored = JsonSerializer.Deserialize<MarketScanState>(json, HubProtocol.Json)!;
            Need(restored == count && restored.NotEnteredPages == 1, "New count lost in JSON.");
            var old = JsonSerializer.Deserialize<MarketScanState>("{\"completed\":3,\"failed\":1}", HubProtocol.Json)!;
            Need(old.NotEnteredPages == 0 && old.Failed == 1, "Legacy JSON was upgraded to no-entry.");
        });
        Check("page_warning_does_not_claim_no_publication_or_success", () =>
        {
            string message = MarketFailurePresentation.Format(noEntry, true);
            Need(message.Contains("確定未執行") && message.Contains("原頁未重送") && message.Contains("子檢查未知")
                && !message.Contains("本頁查詢尚未送出") && !message.Contains("搜尋已停止"), "No-entry warning is misleading.");
            var view = SearchOverlayPresentation.Build(noEntry, true, "", false, forceDispatch: true);
            Need(view.State == "本頁確定未執行" && view.Busy && view.Detail.Contains("原頁未重送"), "Active scan state or no-entry warning lost.");
        });
        Check("contradictory_claim_remains_visible_failure", () =>
        {
            var bad = noEntry with { NativeEntered = true };
            Need(MarketFailurePresentation.Format(bad).Contains("仍未確認"), "Invalid no-entry displayed as confirmed.");
            Need(SearchOverlayPresentation.Build(bad, true, "", false).State != "本頁確定未執行", "Overlay accepted invalid no-entry.");
        });
        Check("count_visible_and_completed_round_retains_unqueried_warning", () =>
        {
            var count = ShopEmptyPagePolicy.CountPage(baseline, noEntry);
            var state = new ShopCommandResponse { State = "State", CurrentSourceSessionKey = "same-source", Scan = count };
            Need(MarketFailurePresentation.Status(state).Contains("確定未執行 1（原頁未重送）"), "Main counter hidden.");
            var view = SearchOverlayPresentation.Build(state, true, "", false);
            Need(view.Detail.Contains("確定未執行 1（原頁未重送）") && view.State == "正在搜尋", "Overlay counter/work state wrong.");
            view = SearchOverlayPresentation.Build(state with { Scan = count with { Running = false, Total = count.Completed } }, true, "", false);
            Need(view.State == "搜尋處理完成（仍有未查頁）", "Processed no-entry advertised as fully searched.");
        });
        return checks.ToArray();
    }
}
