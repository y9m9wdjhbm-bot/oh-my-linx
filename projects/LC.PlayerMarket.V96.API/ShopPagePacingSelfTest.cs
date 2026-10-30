using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Real pacing and progress policies with fake time; no helper, LC, UI or input.</summary>
internal static class ShopPagePacingSelfTest
{
    private static readonly DateTimeOffset InitialTime = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly SourceIdentity Source = new(4242, 987654321, "offline-only", "0x100000", 1, 4096,
        new string('A', 64), "offline-page-pacing");
    private static readonly ShopNativeOutcome Returned = new(true, true, true, "offline return/cleanup", null);

    internal static async Task<OfflineCheck[]> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> action)
        {
            try { await action().WaitAsync(TimeSpan.FromSeconds(10)); checks.Add(new(name, true, "純離線；實際固定頁面等待及持久搜尋合約，沒有LC／helper／輸入。")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        await Check("page_pacing_delayed_products_are_missing_at_zero_and_captured_after_three_seconds", async () =>
        {
            var now = InitialTime; int reads = 0, waits = 0, dispatches = 1;
            PersonalShopListing Read()
            {
                reads++; bool ready = now - InitialTime >= TimeSpan.FromSeconds(3);
                return new() { SourceSessionKey = Source.SessionKey, OwnerIdRaw = ready ? 22UL : 17UL,
                    PageMode = ready ? ShopPageModes.ViewerBuy : ShopPageModes.ViewerSell, SampledAt = now,
                    Complete = ready, BankFingerprint = ready ? "new-bank" : "old-bank",
                    Rows = ready ? [PersonalShopTextRules.Decode(0, "離線商品", "123", "個")] : [] };
            }
            var zero = ShopForceDispatchPolicy.ObserveOnce(Read, out _);
            Need(zero is { Complete: false } && zero.Rows.Length == 0 && zero.OwnerIdRaw != 22, "零等待對照没有重現未載完商品。");
            bool waited = await ShopPagePacing.WaitBeforeCaptureAsync(Returned, default, (amount, token) =>
            { token.ThrowIfCancellationRequested(); waits++; now += amount; return Task.CompletedTask; });
            var captured = ShopForceDispatchPolicy.ObserveOnce(Read, out _);
            Need(waited && waits == 1 && reads == 2 && captured is { Complete: true, OwnerIdRaw: 22 }
                && captured.SourceSessionKey == Source.SessionKey && captured.PageMode == ShopPageModes.ViewerBuy
                && captured.SampledAt == now && now == InitialTime.AddSeconds(3)
                && captured.Rows.Single().Name == "離線商品" && captured.Rows.Single().Price == 123,
                "固定三秒後沒有讀到本頁同來源/店主/方向的新商品。");
            dispatches++; Need(dispatches == 2 && now - InitialTime >= TimeSpan.FromSeconds(3), "下一頁提前覆蓋本頁。");
        });
        await Check("page_pacing_native_gate_prevents_next_buy_sell_until_wait_and_capture", async () =>
        {
            using var nativeGate = new SemaphoreSlim(1, 1);
            var enteredWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var events = new List<string>(); var dispatchAt = new List<DateTimeOffset>(); var now = InitialTime; int waits = 0;
            async Task Page(string name)
            {
                await nativeGate.WaitAsync();
                try
                {
                    events.Add("dispatch-" + name); dispatchAt.Add(now); events.Add("settled-" + name);
                    await ShopPagePacing.WaitBeforeCaptureAsync(Returned, default, (amount, _) =>
                    {
                        Need(amount == TimeSpan.FromSeconds(3), "頁展示等待不是固定三秒。");
                        if (++waits == 1) { enteredWait.SetResult(); return finishWait.Task; }
                        now += amount; return Task.CompletedTask;
                    });
                    events.Add("capture-" + name);
                }
                finally { nativeGate.Release(); }
            }
            var buy = Page("buy"); await enteredWait.Task;
            var sell = Page("sell"); await Task.Yield();
            Need(dispatchAt.Count == 1 && !sell.IsCompleted && !events.Contains("capture-buy"), "未完成本頁等待就讀取或開下一頁。");
            now += TimeSpan.FromSeconds(3); finishWait.SetResult(); await Task.WhenAll(buy, sell);
            Need(events.SequenceEqual(["dispatch-buy", "settled-buy", "capture-buy", "dispatch-sell", "settled-sell", "capture-sell"])
                && dispatchAt[1] - dispatchAt[0] == TimeSpan.FromSeconds(3), "頁間等待／讀取／下一頁順序不正確。");
        });
        await Check("page_pacing_stop_cancels_wait_and_preserves_settled_sent_after_restart", async () =>
        {
            var f = new ProgressFixture(); string request = "offline-sent-page";
            f.Store.BeginAttempt(f.Buy, request); f.Store.NativeIntent(request);
            f.Store.NativeSettled(request, true, true, "complete native return and cleanup before wait");
            string pending = Path.Combine(f.Root, "page.pending.json"), resolved = Path.Combine(f.Root, "page.resolved.json");
            File.WriteAllText(pending, "offline intent"); File.Move(pending, resolved);
            using var stop = new CancellationTokenSource(); bool read = false, cancelled = false; int waitCalls = 0;
            try
            {
                await ShopPagePacing.WaitBeforeCaptureAsync(Returned, stop.Token, (_, _) =>
                {
                    Need(f.Store.View(Source, f.Targets, true, false, true)!.SearchedPages == 1 && File.Exists(resolved)
                        && !File.Exists(pending), "等待發生在NativeSettled或journal清理之前。");
                    waitCalls++; stop.Cancel(); return Task.Delay(Timeout.Infinite, stop.Token);
                });
                read = true;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { cancelled = true; }
            var response = ShopPagePacing.CancelledAfterSettlement(new() { RequestId = request }, Returned);
            f.Store.Result(request, response);
            var reloaded = new ShopScanProgressStore(f.Root);
            var progress = reloaded.View(Source, f.Targets, false, false, true)!;
            Need(cancelled && !read && waitCalls == 1 && response.State == "Cancelled" && response.NativeEntered
                && response.NativeReturned && !response.ResponseVerified && !response.SessionBlocked,
                "F9等待取消遺失實際native返回／清理結果，或仍讀取商品。");
            Need(progress.SearchedPages == 1 && progress.RemainingPages == 1 && progress.UncertainPages == 0
                && reloaded.Continue(f.PlanId, Source, f.Targets, allowEmpty: true, forceDispatch: true).All(p => p != f.Buy),
                "人工停止後Sent回退NotSent/Unknown，重啟又重送原頁。");
        });
        await Check("page_pacing_pre_cancelled_settled_page_never_waits_or_reads", async () =>
        {
            using var stop = new CancellationTokenSource(); stop.Cancel(); int waits = 0; bool cancelled = false;
            try { await ShopPagePacing.WaitBeforeCaptureAsync(Returned, stop.Token, (_, _) => { waits++; return Task.CompletedTask; }); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { cancelled = true; }
            Need(cancelled && waits == 0, "已停止仍進入三秒等待。");
        });
        await Check("page_pacing_positive_noentry_does_not_wait_or_mark_page_sent", async () =>
        {
            var f = new ProgressFixture(); string request = "offline-noentry-page"; int waits = 0;
            f.Store.BeginAttempt(f.Buy, request); f.Store.NativeIntent(request); f.Store.NativeSettled(request, false, false, "positive no-entry and cleanup");
            var noEntry = new ShopNativeOutcome(false, false, true, "positive no-entry", null) { NoEntryConfirmed = true };
            bool opened = await ShopPagePacing.WaitBeforeCaptureAsync(noEntry, default, (_, _) => { waits++; return Task.CompletedTask; });
            var progress = f.Store.View(Source, f.Targets, false, false, true)!;
            Need(!opened && waits == 0 && progress.SearchedPages == 0 && progress.RemainingPages == 2,
                "未開頁的positive no-entry被當成已送成功或加入展示等待。");
        });
        foreach (var native in new[] { Returned with { SafeToSettle = false }, new ShopNativeOutcome(false, false, true, "no positive entry proof", null) })
            await Check("page_pacing_unknown_native_never_starts_wait_" + native.SafeToSettle, async () =>
            {
                bool rejected = false; int waits = 0;
                try { await ShopPagePacing.WaitBeforeCaptureAsync(native, default, (_, _) => { waits++; return Task.CompletedTask; }); }
                catch (InvalidOperationException) { rejected = true; }
                Need(rejected && waits == 0, "3秒等待把未清理/未知native狀態當成settled。");
            });
        await Check("page_pacing_force_failed_observation_still_reads_once_without_status_poll", async () =>
        {
            int waits = 0, reads = 0;
            await ShopPagePacing.WaitBeforeCaptureAsync(Returned, default, (_, _) => { waits++; return Task.CompletedTask; });
            var result = ShopForceDispatchPolicy.ObserveOnce(() => { reads++; throw new IOException("offline capture gap"); }, out string detail);
            Need(waits == 1 && reads == 1 && result is null && detail == "offline capture gap", "固定等待改成商品狀態輪詢或自動重送。");
        });
        return checks.ToArray();
    }

    private static void Need(bool ok, string detail) { if (!ok) throw new InvalidOperationException(detail); }
    private sealed class ProgressFixture
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "LC-Market53-page-pacing", Guid.NewGuid().ToString("N"));
        internal readonly ShopScanProgressStore Store;
        internal readonly ShopTargetSnapshot Targets;
        internal readonly SavedScanPage Buy = new(22, "offline-owner", ShopPageModes.ViewerBuy);
        internal readonly string PlanId;
        internal ProgressFixture()
        {
            Targets = new() { SnapshotId = "offline-pacing-targets", SourceSessionKey = Source.SessionKey,
                SelfId = 11, SelfPosition = new(100, 100), RefreshedAt = InitialTime, CanDispatch = false,
                Owners = [new() { Id = 22, LocalIdentity = "offline-owner", Name = "離線店主", Position = new(100, 101),
                    ObservedAt = InitialTime, ShopState = ShopEligibilityPolicy.ShopActionObserved, ActionType = ".?AVShopAction@lineage@@", ShopStateSampledAt = InitialTime, ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence }] };
            Store = new(Root); PlanId = Store.StartNew(Source, Targets, forceDispatch: true).PlanId;
        }
    }
}
