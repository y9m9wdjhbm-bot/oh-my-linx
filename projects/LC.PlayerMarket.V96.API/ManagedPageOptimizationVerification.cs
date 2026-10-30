using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;
namespace LcPlayerMarket;

internal static class ManagedPageOptimizationVerification
{
    internal static async Task<int> RunAsync(string output)
    {
        var checks = new List<OfflineCheck>();
        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "managed-page-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        async Task Check(string name, Func<Task> body)
        {
            try { await body().WaitAsync(TimeSpan.FromSeconds(15)); checks.Add(new("managed_page_" + name, true, "Isolated managed policies/files only; LC/native/input are prohibited.")); }
            catch (Exception error) { checks.Add(new("managed_page_" + name, false, error.ToString())); }
        }
        Task Sync(Action body) { body(); return Task.CompletedTask; }
        static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Refuse(Action action)
        { bool refused = false; try { action(); } catch (InvalidOperationException) { refused = true; } if (!refused) throw new InvalidOperationException("Invalid transition was admitted."); }
        string NewDir(string name) { string path = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        var source = new SourceIdentity(4242, 123456, "offline-lc", "0x100000", 1, 4096, new string('A', 64), "managed-page-fixture");
        var owner = new MarketOwner { Id = 22, Name = "offline owner", LocalIdentity = "managed-owner-22", Position = new(100, 101), ObservedAt = DateTimeOffset.UtcNow,
            ShopState = ShopEligibilityPolicy.ShopActionObserved, ActionType = ".?AVShopAction@lineage@@", ShopStateSampledAt = DateTimeOffset.UtcNow, ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence };
        ShopSearchedOwnerStore Store(string path) => new(path, Path.Combine(path, "missing-legacy"));
        var returned = new ShopNativeOutcome(true, true, true, "closed fixture native result", null);

        await Check("default_zero_mode_preserves_numeric_default", () => Sync(() =>
        { var p = new Preferences(); Need(p.NoAdditionalPageWait && p.EffectivePageOpenIntervalMilliseconds == 0 && p.PageOpenIntervalMilliseconds == 3000, "V96 zero mode changed saved numeric range."); }));
        await Check("legacy_500ms_is_preserved_without_rewriting", () => Sync(() =>
        {
            string path = NewDir("old-settings"), file = Path.Combine(path, "settings.json"), json = "{\"pageOpenIntervalMilliseconds\":500,\"logEnabled\":false}";
            File.WriteAllText(file, json); var p = new LocalStore(path).LoadPreferences();
            Need(p.NoAdditionalPageWait && p.EffectivePageOpenIntervalMilliseconds == 0 && p.PageOpenIntervalMilliseconds == 500 && !p.LogEnabled && File.ReadAllText(file) == json,
                "Loading modified numeric/other settings or original bytes.");
        }));
        foreach (bool noWait in new[] { true, false }) await Check("zero_mode_roundtrip_" + noWait, () => Sync(() =>
        {
            var store = new LocalStore(NewDir("roundtrip")); store.SavePreferences(new() { PageOpenIntervalMilliseconds = 500, NoAdditionalPageWait = noWait });
            var p = store.LoadPreferences(); using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(store.Root, "settings.json")));
            Need(p.NoAdditionalPageWait == noWait && p.PageOpenIntervalMilliseconds == 500 && p.EffectivePageOpenIntervalMilliseconds == (noWait ? 0 : 500)
                && document.RootElement.GetProperty("pageOpenIntervalMilliseconds").GetInt32() == 500, "Zero mode destroyed rollback numeric setting.");
        }));
        await Check("zero_safe_return_reads_without_extra_delay", async () =>
        { int delays = 0; bool ready = await ShopPagePacing.WaitBeforeCaptureMillisecondsAsync(returned, default, (_, _) => { delays++; return Task.CompletedTask; }, 0); Need(ready && delays == 0, "Zero interval still waited or lost safe return."); });
        await Check("zero_positive_noentry_is_not_an_opened_page", async () =>
        { int delays = 0; bool ready = await ShopPagePacing.WaitBeforeCaptureMillisecondsAsync(new(false, false, true, "no entry", null) { NoEntryConfirmed = true }, default, (_, _) => { delays++; return Task.CompletedTask; }, 0); Need(!ready && delays == 0, "No-entry became an opened page."); });
        foreach (var unsafeResult in new[] { returned with { SafeToSettle = false }, new ShopNativeOutcome(false, false, true, "unknown", null) })
        await Check("zero_never_relaxes_native_proof_" + unsafeResult.Entered, async () =>
        {
            int delays = 0; bool refused = false;
            try { await ShopPagePacing.WaitBeforeCaptureMillisecondsAsync(unsafeResult, default, (_, _) => { delays++; return Task.CompletedTask; }, 0); }
            catch (InvalidOperationException) { refused = true; }
            Need(refused && delays == 0, "Zero interval bypassed native settlement.");
        });
        await Check("zero_respects_operator_cancel", async () =>
        {
            using var stop = new CancellationTokenSource(); stop.Cancel(); bool cancelled = false;
            try { await ShopPagePacing.WaitBeforeCaptureMillisecondsAsync(returned, stop.Token, waitMilliseconds: 0); } catch (OperationCanceledException) { cancelled = true; }
            Need(cancelled, "Zero interval ignored cancellation.");
        });
        await Check("zero_native_gate_keeps_page_capture_serial", async () =>
        {
            using var gate = new SemaphoreSlim(1, 1); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var order = new List<string>();
            async Task Page(string id, bool hold)
            {
                await gate.WaitAsync(); try { order.Add("native-" + id); if (hold) { entered.SetResult(); await release.Task; }
                    Need(await ShopPagePacing.WaitBeforeCaptureMillisecondsAsync(returned, default, waitMilliseconds: 0), "Page not safe"); order.Add("capture-" + id); }
                finally { gate.Release(); }
            }
            var first = Page("one", true); await entered.Task; var second = Page("two", false);
            Need(!second.IsCompleted && order.SequenceEqual(["native-one"]), "Another native page overlapped."); release.SetResult(); await Task.WhenAll(first, second);
            Need(order.SequenceEqual(["native-one", "capture-one", "native-two", "capture-two"]), "Zero mode reordered native/capture.");
        });
        foreach (bool confirmed in new[] { false, true }) await Check("one_durable_completion_reload_" + confirmed, () => Sync(() =>
        {
            string path = NewDir("owner-complete"); var store = Store(path); store.Begin(source, owner, ShopPageModes.ViewerBuy, "single-page");
            int before = Directory.EnumerateFiles(path, "*.json").Count(); store.CompletePage("single-page", confirmed);
            Need(Directory.EnumerateFiles(path, "*.json").Count() == before + (confirmed ? 2 : 1), "Completion wrote duplicate settlement/confirmation events.");
            var reloaded = ShopSearchedOwnerStore.OpenReadOnly(path); var view = reloaded.Summary(true, "");
            Need(reloaded.Error is null && !reloaded.BlocksSource(source) && reloaded.ShouldSkip(owner.Id, ShopPageModes.ViewerBuy, true)
                && view.SearchedPages == 1 && view.ConfirmedPages == (confirmed ? 1 : 0) && view.UncertainPages == 0, "Reload lost sent/confirmed accounting or allowed replay.");
            using var document = JsonDocument.Parse(File.ReadAllText(Directory.EnumerateFiles(path, "*.json").Order(StringComparer.Ordinal).Last()));
            Need(document.RootElement.GetProperty("kind").GetString() == (confirmed ? "Confirmed" : "Settled"), "V95-compatible completion kind missing.");
        }));
        await Check("intent_crash_remains_uncertain_and_blocks_session", () => Sync(() =>
        {
            string path = NewDir("intent-only"); var store = Store(path); store.Begin(source, owner, ShopPageModes.ViewerBuy, "unreturned");
            var reloaded = ShopSearchedOwnerStore.OpenReadOnly(path); Need(reloaded.Error is null && reloaded.BlocksSource(source) && reloaded.Summary(false, "").UncertainPages == 1, "Crash erased uncertain native intent.");
            Refuse(() => store.Begin(source, owner, ShopPageModes.ViewerBuy, "unreturned", true));
        }));
        await Check("completion_without_original_intent_is_rejected", () => Sync(() => Refuse(() => Store(NewDir("no-intent")).CompletePage("missing", false))));
        await Check("completed_request_cannot_complete_again", () => Sync(() =>
        { var store = Store(NewDir("twice")); store.Begin(source, owner, ShopPageModes.ViewerBuy, "twice"); store.CompletePage("twice", false); Refuse(() => store.CompletePage("twice", true)); }));
        await Check("completion_missing_head_stays_fail_closed", () => Sync(() =>
        {
            string path = NewDir("missing-head"); var store = Store(path); store.Begin(source, owner, ShopPageModes.ViewerBuy, "head"); store.CompletePage("head", true);
            File.Delete(Path.Combine(path, "head.checkpoint")); var reloaded = ShopSearchedOwnerStore.OpenReadOnly(path);
            Need(reloaded.Error is not null && reloaded.BlocksSource(source), "Uncommitted completion was accepted without durable head.");
        }));
        foreach (bool confirmed in new[] { false, true }) await Check("durable_legal_tail_keeps_v95_replay_semantics_" + confirmed, () => Sync(() =>
        {
            string path = NewDir("old-head"); var store = Store(path); store.Begin(source, owner, ShopPageModes.ViewerBuy, "durable-tail");
            byte[] priorHead = File.ReadAllBytes(Path.Combine(path, "head.checkpoint")); store.CompletePage("durable-tail", confirmed);
            // Existing V95 accepts a legal durable tail beyond an older valid
            // head. Preserve that behavior; do not pretend it was new logic.
            File.WriteAllBytes(Path.Combine(path, "head.checkpoint"), priorHead);
            var reloaded = ShopSearchedOwnerStore.OpenReadOnly(path); var view = reloaded.Summary(false, "");
            Need(reloaded.Error is null && !reloaded.BlocksSource(source) && reloaded.ShouldSkip(owner.Id, ShopPageModes.ViewerBuy, true)
                && view.SearchedPages == 1 && view.ConfirmedPages == (confirmed ? 1 : 0) && view.UncertainPages == 0,
                "Legal durable tail lost V95 accounting or became replayable.");
        }));
        await Check("combined_completion_archive_replays_original_events", () => Sync(() =>
        {
            string path = NewDir("archive-source"), archive = NewDir("archive-target"); var store = Store(path);
            store.Begin(source, owner, ShopPageModes.ViewerBuy, "archived"); store.CompletePage("archived", true); SearchedOwnerArchive.CreateFromLegacy(path, archive);
            var replayed = ShopSearchedOwnerStore.OpenReadOnly(archive); Need(replayed.Error is null && replayed.Summary(false, "").ConfirmedPages == 1 && replayed.ShouldSkip(owner.Id, ShopPageModes.ViewerBuy, true), "Archive rejected/rewrote combined completion semantics.");
        }));
        foreach (string kind in new[] { "SHOP_FORCE_NATIVE_INTENT", "SHOP_NATIVE_END", "SHOP_NATIVE_RESULT", "SHOP_SCAN_PAGE_CYCLE_TIMING", "WM_NULL_POST_RESULT", "NATIVE_INTENT", "NATIVE_RESULT", "END", "UNKNOWN_NEW_KIND" })
        await Check("verbose_never_accepts_critical_" + kind, () => Sync(() =>
        {
            using var sink = new OptionalLogFixture(NewDir("critical-log"), _ => { });
            Need(!OptionalLogFixture.OptionalKinds.Contains(kind), "Critical kind entered optional allowlist.");
            Refuse(() => sink.Append(new(DateTimeOffset.UtcNow, kind, "critical fixture")));
        }));
        await Check("verbose_normal_dispose_drains_original_payload", () => Sync(() =>
        {
            string path = NewDir("verbose-drain"); string? error = null;
            using (var sink = new OptionalLogFixture(path, value => error = value))
                for (int i = 0; i < 8; i++) sink.Append(new(DateTimeOffset.UtcNow, "READ_CYCLE", "fixture-" + i, new { Sequence = i }));
            var lines = Directory.EnumerateFiles(path, "*.jsonl").SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line)).ToArray();
            try { Need(error is null && lines.Length == 8 && lines.Select(d => d.RootElement.GetProperty("data").GetProperty("sequence").GetInt32()).SequenceEqual(Enumerable.Range(0, 8)), "Normal drain dropped/reordered original verbose payload."); }
            finally { foreach (var line in lines) line.Dispose(); }
        }));
        await Check("verbose_oversize_is_bounded_and_drop_audited", () => Sync(() =>
        {
            string path = NewDir("verbose-drop"); var sink = new OptionalLogFixture(path, _ => { });
            sink.Append(new(DateTimeOffset.UtcNow, "READ_CYCLE", new string('x', 262145)));
            sink.Append(new(DateTimeOffset.UtcNow, "READ_CYCLE", "flush drop count")); sink.Dispose();
            Need(sink.Dropped == 1 && Directory.EnumerateFiles(path, "*.jsonl").SelectMany(File.ReadAllLines).Any(line => line.Contains("VERBOSE_LOG_DROPPED", StringComparison.Ordinal)), "Bounded optional drop counter was not retained.");
        }));
        await Check("cycle_includes_response_and_result_boundary", () => Sync(() =>
        {
            object? captured = null; string? kind = null;
            using (var cycle = new ShopScanPageCycleTiming((k, _, payload) => { kind = k; captured = payload; }, "actual-cycle", 22, ShopPageModes.ViewerBuy))
            {
                cycle.Enter(); ShopScanPageCycleTiming.CaptureInterval("actual-cycle", 0);
                using var durable = new FileStream(Path.Combine(NewDir("cycle"), "result.receipt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read); durable.WriteByte(1); durable.Flush(true);
            }
            var value = JsonSerializer.SerializeToElement(captured, HubProtocol.Json);
            Need(kind == "SHOP_SCAN_PAGE_CYCLE_TIMING" && value.GetProperty("pageCycleMs").GetDouble() >= 0
                && value.GetProperty("effectiveIntervalMilliseconds").GetInt32() == 0 && value.GetProperty("noAdditionalPageWait").GetBoolean()
                && value.GetProperty("includesBeginAttemptAndRequestDurability").GetBoolean() && value.GetProperty("includesResponseAndResultDurability").GetBoolean(), "Cycle audit dropped persistence/zero interval fields.");
        }));
        var report = new { Version = "96.0.0", Success = checks.Count > 0 && checks.All(c => c.Passed),
            Passed = checks.Count(c => c.Passed), Failed = checks.Count(c => !c.Passed), Checks = checks.ToArray(),
            IsAdministrator = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),
            ConnectedToService = false, OpenedGameProcess = false, CreatedWindow = false,
            LcReads = 0, NativeCalls = 0, GameInputs = 0 };
        using var file = new FileStream(Path.GetFullPath(output), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(file, report, HubProtocol.Json); file.Flush(true); return report.Success ? 0 : 1;
    }

    // Exercise the actual Core logger without widening its product API or
    // granting Main access to unrelated internal reader/runtime members.
    private sealed class OptionalLogFixture : IDisposable
    {
        private static readonly Type SinkType = typeof(HubRuntime).Assembly.GetType("MemoryHub.BufferedDiagnosticLog", true)!;
        private static readonly MethodInfo AppendMethod = SinkType.GetMethod("Append", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly PropertyInfo DroppedProperty = SinkType.GetProperty("Dropped", BindingFlags.Instance | BindingFlags.NonPublic)!;
        internal static HashSet<string> OptionalKinds => (HashSet<string>)SinkType.GetField("OptionalKinds", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        private readonly IDisposable sink;
        internal OptionalLogFixture(string directory, Action<string?> error) => sink = (IDisposable)Activator.CreateInstance(SinkType,
            BindingFlags.Instance | BindingFlags.NonPublic, null, [directory, error], null)!;
        internal long Dropped => (long)DroppedProperty.GetValue(sink)!;
        internal void Append(HubLog entry)
        {
            try { AppendMethod.Invoke(sink, [entry]); }
            catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
        }
        public void Dispose() => sink.Dispose();
    }
}
