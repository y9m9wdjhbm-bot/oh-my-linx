using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;

namespace LcPlayerMarket;

/// <summary>Exercises real UI entrypoints against pending fake responses; never opens LC.</summary>
internal static class ForceDispatchUiSelfTest
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static int Run(string output)
    {
        if (Directory.Exists(output)) throw new IOException("Evidence directory already exists.");
        Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        var checks = new List<OfflineCheck>();
        void Check(string name, Action<Fixture> action)
        {
            using var fixture = new Fixture(Path.Combine(output, name));
            try { action(fixture); checks.Add(new(name, true, "Fake backend; no game process or native call.")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }

        Check("force_ui_missing_state_accepts_all_pending_commands", f =>
        {
            Set(f.Form, "broker", null);
            Set(f.Form, "actionBusy", true); Set(f.Form, "cancelBusy", true); Set(f.Form, "scanControlBusy", true);
            Set(f.Form, "patrolRunning", true);
            Set(f.Form, "lastConfirmedScan", new MarketScanState { Running = true, Paused = true });
            Set(f.Form, "lastKnownSavedScan", f.Progress);
            Call(f.Form, "UpdateButtons");
            foreach (string button in new[] { "refreshNearby", "scan", "continueSaved", "pause", "resume", "stop", "startSavedPatrolButton" })
                Need(Field<Button>(f.Form, button).Enabled, button + " was disabled by unconfirmed/busy state.");
            f.Start("RefreshNearbyAsync");
            f.Start("SendAction", ShopCommands.ScanVisible, null);
            f.Start("SendAction", ShopCommands.OpenShop, f.Listing);
            f.Start("SendAction", ShopCommands.ContinueSavedScan, null);
            f.Start("SendScanControl", ShopCommands.PauseScan);
            f.Start("SendScanControl", ShopCommands.ResumeScan);
            f.Start("SendCancel");
            PumpUntil(() => f.Commands.Requests.Count == 7, "All seven commands did not reach the pending fake backend.");
            Need(f.Commands.Requests.All(r => r.ForceDispatch), "Mutation was sent without ForceDispatch.");
            Need(f.Commands.Requests.All(r => r.Command != ShopCommands.GetState), "Command performed a prerequisite GetState.");
            Need(f.Tasks.All(t => !t.IsCompleted), "Pending fake replies unexpectedly completed a command.");
            Need(Field<int>(f.Form, "activeActionRequests") == 4, "Concurrent action requests lost ownership.");
            Need(f.Commands.Requests.Select(r => r.RequestId).Distinct().Count() == 7, "Separate clicks reused a request ID.");
            Set(f.Form, "patrolRunning", false);
        });

        Check("force_ui_stale_blocked_broker_does_not_gate_commands", f =>
        {
            Set(f.Form, "broker", new ShopCommandResponse
            {
                At = DateTimeOffset.UtcNow.AddMinutes(-5), CurrentSourceSessionKey = f.Source.SessionKey,
                SessionBlocked = true, WorkSettled = false, SavedScan = f.Progress,
                Scan = new MarketScanState { Running = true, PauseRequested = true, Paused = false },
                TargetSnapshot = new ShopTargetSnapshot { SnapshotId = "different-snapshot", CanDispatch = false }
            });
            Set(f.Form, "brokerReceivedAt", DateTimeOffset.UtcNow.AddMinutes(-5));
            Field<HashSet<string>>(f.Form, "uncertainSources").Add(f.Source.SessionKey);
            Call(f.Form, "UpdateButtons");
            Need(Field<Button>(f.Form, "scan").Enabled && Field<Button>(f.Form, "continueSaved").Enabled,
                "Stale SessionBlocked/uncertain progress disabled search commands.");
            f.Start("SendAction", ShopCommands.ScanVisible, null);
            f.Start("SendAction", ShopCommands.OpenShop, f.Listing);
            f.Start("SendAction", ShopCommands.ContinueSavedScan, null);
            f.Start("SendScanControl", ShopCommands.PauseScan);
            f.Start("SendScanControl", ShopCommands.ResumeScan);
            f.Start("SendCancel");
            PumpUntil(() => f.Commands.Requests.Count == 6, "Blocked observation prevented direct dispatch.");
            Need(f.Commands.Requests.All(r => r.ForceDispatch), "Blocked-state mutation lost its force flag.");
            Need(f.Commands.Requests.Where(r => r.Command is ShopCommands.PauseScan or ShopCommands.ResumeScan or ShopCommands.CancelScan)
                .All(r => r.ExpectedScanJobId is null && r.TargetSnapshotId is null), "Control copied stale job/snapshot status.");
        });

        Check("force_ui_pending_actions_keep_counter_until_last_reply", f =>
        {
            f.Start("SendAction", ShopCommands.OpenShop, f.Listing);
            PumpUntil(() => f.Commands.Requests.Count == 1, "First click did not reach the fake backend.");
            f.Start("SendAction", ShopCommands.OpenShop, f.Listing);
            PumpUntil(() => f.Commands.Requests.Count == 2, "Second click waited for the first reply.");
            var requests = f.Commands.Requests.ToArray();
            f.Commands.Complete(requests[0]);
            PumpUntil(() => f.Tasks[0].IsCompleted, "First fake reply did not complete.");
            Need(Field<bool>(f.Form, "actionBusy") && Field<int>(f.Form, "activeActionRequests") == 1,
                "First reply incorrectly retired the other pending request.");
            Need(!f.Tasks[1].IsCompleted && Field<Button>(f.Form, "scan").Enabled,
                "Remaining reply disabled the next search command.");
            f.Commands.Complete(requests[1]);
            PumpUntil(() => f.Tasks[1].IsCompleted, "Second fake reply did not complete.");
            Need(!Field<bool>(f.Form, "actionBusy") && Field<int>(f.Form, "activeActionRequests") == 0,
                "Action counter did not settle after both replies.");
        });

        Check("force_ui_newest_refresh_wins_out_of_order_replies", f =>
        {
            f.Start("RefreshNearbyAsync");
            PumpUntil(() => f.Commands.Requests.Count == 1, "First refresh did not reach fake backend.");
            f.Start("RefreshNearbyAsync");
            PumpUntil(() => f.Commands.Requests.Count == 2, "Second refresh waited for first response.");
            var requests = f.Commands.Requests.ToArray();
            f.Commands.Complete(requests[1], f.RefreshResponse(requests[1], "newest-refresh"));
            PumpUntil(() => f.Tasks[1].IsCompleted, "Newest refresh did not complete.");
            f.Commands.Complete(requests[0], f.RefreshResponse(requests[0], "older-refresh"));
            PumpUntil(() => f.Tasks[0].IsCompleted, "Old refresh did not complete.");
            Need(Field<ManualNearbyContext>(f.Form, "manualNearby").SnapshotId == "newest-refresh",
                "An older refresh response replaced the most recent requested list.");
        });

        Check("force_ui_missing_or_foreign_source_cannot_reuse_ids", f =>
        {
            f.Start("SendAction", ShopCommands.OpenShop, f.Listing with { SourceSessionKey = "foreign-source" });
            f.Start("SendAction", ShopCommands.OpenShop, f.Listing with { Owner = f.Listing.Owner with { LocalIdentity = "reused-id" } });
            Set(f.Form, "snapshot", new HubSnapshot { Source = f.Source with { SessionKey = "new-source" } });
            f.Start("SendAction", ShopCommands.ScanVisible, null);
            Set(f.Form, "snapshot", new HubSnapshot { Source = null });
            f.Start("SendAction", ShopCommands.ScanVisible, null);
            f.Start("RefreshNearbyAsync"); f.Start("SendCancel");
            f.Start("SendScanControl", ShopCommands.PauseScan);
            PumpUntil(() => f.Tasks.All(t => t.IsCompleted), "Invalid identity command did not return.");
            Need(f.Commands.Requests.IsEmpty, "Missing/foreign identity dispatched an old actor ID.");
            Call(f.Form, "UpdateButtons");
            Need(!Field<Button>(f.Form, "scan").Enabled && !Field<Button>(f.Form, "stop").Enabled,
                "No source still enabled source-specific commands.");
        });

        Check("force_ui_confirmed_shop_ignores_pending_confirmation_only", f =>
        {
            Need(ShopEligibilityPolicy.CanScan(f.Listing.Owner), "Fixture must contain complete saved shop-action evidence.");
            Need(!Field<ManualNearbyContext>(f.Form, "manualNearby").CanDispatch, "Fixture must lack old dispatch authorization.");
            f.Start("SendAction", ShopCommands.OpenShop, f.Listing);
            PumpUntil(() => f.Commands.Requests.Count == 1, "Pending confirmation blocked an eligible explicit forced command.");
            Need(f.Commands.Requests.Single().OwnerId == f.Listing.Owner.Id, "Forced open lost selected owner identity.");
        });

        Check("force_ui_nonshop_unknown_and_incomplete_saved_evidence_cannot_open", f =>
        {
            var rejected = new[]
            {
                f.Listing.Owner with { ShopState = ShopEligibilityPolicy.KnownOtherActionObserved, ActionType = ".?AVBreathAction@lineage@@" },
                f.Listing.Owner with { ShopState = ShopEligibilityPolicy.Unknown },
                f.Listing.Owner with { ShopStateEvidence = "" },
                f.Listing.Owner with { ShopStateSampledAt = null },
                f.Listing.Owner with { ActionType = ".?AVMovingAction@lineage@@" }
            };
            foreach (var owner in rejected)
            {
                Set(f.Form, "manualNearby", new ManualNearbyContext("ineligible-saved", f.Source, DateTimeOffset.UtcNow, new(100, 100), [owner], false));
                // The historical row still says shop. Eligibility must come
                // from the saved current owner, not from that row's old flag.
                f.Start("SendAction", ShopCommands.OpenShop, f.Listing);
            }
            PumpUntil(() => f.Tasks.All(task => task.IsCompleted), "Ineligible UI command did not return.");
            Need(f.Commands.Requests.IsEmpty, "ForceDispatch submitted a nonshop/unknown/malformed saved owner.");
            Call(f.Form, "UpdateButtons");
            Need(!Field<Button>(f.Form, "scan").Enabled && !Field<Button>(f.Form, "continueSaved").Enabled,
                "An all-ineligible saved target set enabled new shop pages.");
            Need(Field<Button>(f.Form, "refreshNearby").Enabled && Field<Button>(f.Form, "stop").Enabled,
                "An all-ineligible target set blocked read-only refresh or stop.");
        });

        Check("force_ui_preserves_all_nearby_counts_and_uses_saved_shop_owner", f =>
        {
            var nonshop = f.Listing.Owner with { Id = 23, LocalIdentity = "offline-owner-23", ShopState = ShopEligibilityPolicy.KnownOtherActionObserved, ActionType = ".?AVMovingAction@lineage@@" };
            var unknown = f.Listing.Owner with { Id = 24, LocalIdentity = "offline-owner-24", ShopState = ShopEligibilityPolicy.Unknown };
            var manual = new ManualNearbyContext("mixed-targets", f.Source, DateTimeOffset.UtcNow, new(100, 100), [f.Listing.Owner, nonshop, unknown], false);
            Set(f.Form, "manualNearby", manual);
            Need(manual.Count == 3 && manual.ShopCounts.ShopActionObservedCount == 1 && manual.ShopCounts.KnownOtherActionObservedCount == 1 && manual.ShopCounts.UnknownShopStateCount == 1,
                "Filtering eligible pages deleted nearby players or classification counts.");
            f.Start("SendAction", ShopCommands.OpenShop, f.Listing with { Owner = nonshop });
            f.Start("SendAction", ShopCommands.OpenShop, f.Listing with { Owner = unknown });
            Need(f.Commands.Requests.IsEmpty, "Mixed target set opened a nonshop or unknown owner.");
            // A matching old row may have an unknown action. The saved owner
            // supplies eligibility; the backend supplies current-page proof.
            f.Start("SendAction", ShopCommands.OpenShop, f.Listing with { Owner = f.Listing.Owner with { ShopState = ShopEligibilityPolicy.Unknown } });
            PumpUntil(() => f.Commands.Requests.Count == 1, "The eligible saved owner was replaced by the old listing flag.");
            Need(f.Commands.Requests.Single().OwnerId == f.Listing.Owner.Id && f.Commands.Requests.Single().ForceDispatch,
                "Eligible owner lost its local identity or direct-dispatch flag.");
        });

        Check("force_saved_controls_ignore_pending_confirmation_only", f =>
        {
            var result = SavedSearchPresentation.Controls(f.Progress, f.Source.SessionKey, true, true, true, "", forceDispatch: true);
            Need(result.CanRestart && result.CanContinue, "Running/uncertain/CanContinue observation gated forced controls.");
            Need(!SavedSearchPresentation.Controls(f.Progress, "different-source", true, true, true, "", forceDispatch: true).CanContinue,
                "Forced continue reused another LC source.");
            Need(!SavedSearchPresentation.Controls(f.Progress with { RemainingPages = 0 }, f.Source.SessionKey, true, true, true, "", forceDispatch: true).CanContinue,
                "Forced continue invented remaining pages.");
            Need(!SavedSearchPresentation.Controls(f.Progress, f.Source.SessionKey, false, true, false, "", forceDispatch: true).CanRestart,
                "Closing UI became command-ready.");
        });

        Check("force_ui_observation_text_does_not_claim_command_lock", f =>
        {
            var response = new ShopCommandResponse { SessionBlocked = true, CurrentSourceSessionKey = f.Source.SessionKey,
                Scan = new MarketScanState { SourceSessionKey = f.Source.SessionKey, Running = true } };
            var blocked = SearchOverlayPresentation.Build(response, true, "", false, forceDispatch: true);
            Need(blocked.State == "上次操作結果未確認" && blocked.Detail.Contains("可直接下達") && !blocked.State.Contains("已停止"),
                "Old blocked-session text still claims commands are stopped.");
            var pending = SearchOverlayPresentation.Build(response, true, "", true, forceDispatch: true);
            Need(pending.State == "指令已送出" && pending.Detail.Contains("可直接下達") && pending.Busy,
                "Pending reply text suggests the next command must wait.");
            var missing = SearchOverlayPresentation.Build(null, false, "觀測暫不可用", true, forceDispatch: true);
            Need(missing.State == "指令已送出" && missing.Detail.Contains("可直接下達"), "Missing poll gated pending-command text.");
            var incompatible = SearchOverlayPresentation.Build(response, true, "", false, "LC 版本不相容", forceDispatch: true);
            Need(!incompatible.Detail.Contains("可直接下達"), "ABI mismatch was mislabeled as command-ready.");
            foreach (string code in new[] { "SourceUnavailable", "NativeCodeUnavailable", "TargetRefreshRequired" })
            {
                string text = MarketFailurePresentation.Status(new() { FailureCode = code, State = "Rejected", Detail = "Fake result" }, forceDispatch: true);
                Need(text.Contains("下一") && !text.Contains("搜尋已停止") && !text.Contains("名單不能用於搜尋"),
                    "Forced failure text retained a command lock for " + code);
            }
        });

        Check("force_ui_native_abi_gate_is_preserved", f =>
        {
            Set(f.Form, "snapshot", new HubSnapshot { Source = f.Source with { DiskSha256 = "unsupported-build" } });
            Call(f.Form, "UpdateButtons");
            Need(!Field<Button>(f.Form, "scan").Enabled && Field<Button>(f.Form, "refreshNearby").Enabled,
                "ABI guard disabled read-only refresh or enabled incompatible native actions.");
            f.Start("SendAction", ShopCommands.OpenShop, f.Listing);
            Need(f.Commands.Requests.IsEmpty && f.Tasks.Single().IsCompleted, "Forced open bypassed native ABI validation.");
        });

        Check("force_history_clear_sends_without_can_clear_state", f =>
        {
            var history = new ShopSearchHistorySummary { Revision = "old-revision", Epoch = "old-epoch", CanClear = false };
            Set(f.Form, "broker", new ShopCommandResponse { At = DateTimeOffset.UtcNow.AddMinutes(-5), SearchHistory = history, SessionBlocked = true });
            Set(f.Form, "actionBusy", true); Set(f.Form, "cancelBusy", true); Set(f.Form, "scanControlBusy", true);
            Call(f.Form, "UpdateButtons");
            Need(Field<Button>(f.Form, "clearSearchHistory").Enabled, "Stale broker/CanClear/busy flags blocked the clear button.");
            Set(f.Form, "broker", null); Set(f.Form, "lastKnownSearchHistory", history);
            Call(f.Form, "UpdateButtons");
            Need(Field<Button>(f.Form, "clearSearchHistory").Enabled, "Failed state poll discarded known history-clear revision.");
            var task = ShopSearchHistoryPresentation.ClearAsync(history, true, f.Backend.SendAsync, default, forceDispatch: true);
            PumpUntil(() => f.Commands.Requests.Count == 1, "CanClear=false blocked confirmed forced history clear.");
            var request = f.Commands.Requests.Single();
            Need(request.ForceDispatch && request.Command == ShopCommands.ClearSearchedOwners, "Clear helper omitted forced mutation.");
            f.Commands.Complete(request, new ShopCommandResponse { RequestId = request.RequestId, State = "SearchedOwnersCleared",
                SearchHistory = history with { Revision = "new-revision", Epoch = "new-epoch" } });
            PumpUntil(() => task.IsCompleted, "History clear reply did not complete.");
            Need(task.IsCompletedSuccessfully, "Valid forced history clear response was rejected.");
        });

        var report = new { Version = "83.0.0", Success = checks.All(c => c.Passed), Passed = checks.Count(c => c.Passed),
            Failed = checks.Count(c => !c.Passed), OpenedGameProcess = false, NativeCalls = 0, GameInputs = 0,
            AutomaticGetStateRequests = 0, Checks = checks };
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.Success ? 0 : 1;
    }

    private static void PumpUntil(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(8)) { Application.DoEvents(); Thread.Sleep(5); }
        Need(condition(), message);
    }
    private static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    private static T Field<T>(MainForm form, string name) => (T)(form.GetType().GetField(name, Hidden)
        ?? throw new MissingFieldException(name)).GetValue(form)!;
    private static void Set(MainForm form, string name, object? value) => (form.GetType().GetField(name, Hidden)
        ?? throw new MissingFieldException(name)).SetValue(form, value);
    private static object? Call(MainForm form, string name, params object?[] arguments) => (form.GetType().GetMethod(name, Hidden)
        ?? throw new MissingMethodException(name)).Invoke(form, arguments);

    private sealed class Fixture : IDisposable
    {
        internal readonly SourceIdentity Source = new(7100, 9001, "offline-only", "0x100000", MarketNativeCompatibility.SupportedTimestamp,
            MarketNativeCompatibility.SupportedImageSize, MarketNativeCompatibility.SupportedSha256, "force-ui-offline-source");
        internal readonly PendingCommands Commands;
        internal readonly StandaloneMarketBackend Backend;
        internal readonly MainForm Form;
        internal readonly MarketListing Listing;
        internal readonly ShopSavedScanProgress Progress;
        internal readonly List<Task> Tasks = [];
        internal Fixture(string directory)
        {
            Directory.CreateDirectory(directory);
            Commands = new PendingCommands(Source.SessionKey);
            Backend = new StandaloneMarketBackend(Path.Combine(directory, "backend"), _ => new FakeSource(), (_, _, _) => Commands);
            var store = new LocalStore(Path.Combine(directory, "state"));
            store.SavePreferences(new() { LogEnabled = true, ShowNavigation = false, ShowSearchOverlay = false });
            Form = new MainForm(store, Backend);
            DateTimeOffset at = DateTimeOffset.UtcNow;
            var owner = new MarketOwner { Id = 22, LocalIdentity = "offline-owner-22", Name = "離線店主", Position = new(100, 102), ObservedAt = at,
                ShopState = ShopEligibilityPolicy.ShopActionObserved, ActionType = ".?AVShopAction@lineage@@", ShopStateSampledAt = at, ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence };
            Listing = new MarketListing { SourceSessionKey = Source.SessionKey, Revision = "offline-revision", RequestId = "offline-request",
                Owner = owner, CapturedAt = at, ViewerDirection = ShopPageModes.ViewerBuy, OwnerAndResponseMatched = true, Complete = true,
                Rows = [new(0, "Observed", true, "離線商品", "123", 123, "每個", null, "Unverified", "Fake fixture")] };
            Progress = new ShopSavedScanProgress { PlanId = "offline-plan", SourceSessionKey = Source.SessionKey,
                CreatedAt = at, UpdatedAt = at, TotalPages = 4, RemainingPages = 2, UncertainPages = 1,
                CanContinue = false, ContinueBlockReason = "Fake pending confirmation" };
            Set(Form, "snapshot", new HubSnapshot { Source = Source, Fields = new() { ["playerShopStates"] = FieldReading.Unknown("Fake unknown shop state") } });
            Set(Form, "manualNearby", new ManualNearbyContext("offline-targets", Source, at, new(100, 100), [owner], canDispatch: false));
            Set(Form, "lastKnownSavedScan", Progress);
        }
        internal void Start(string method, params object?[] arguments) => Tasks.Add((Task)Call(Form, method, arguments)!);
        internal ShopCommandResponse RefreshResponse(ShopCommandRequest request, string snapshotId) => new()
        {
            RequestId = request.RequestId, State = "TargetsRefreshed", CurrentSourceSessionKey = Source.SessionKey,
            TargetSnapshot = new() { SnapshotId = snapshotId, SourceSessionKey = Source.SessionKey, RefreshedAt = DateTimeOffset.UtcNow,
                SelfId = 11, Owners = [Listing.Owner], CanDispatch = true }
        };
        public void Dispose()
        {
            Set(Form, "patrolRunning", false);
            Commands.CompleteAll();
            PumpUntil(() => { Commands.CompleteAll(); return Tasks.All(t => t.IsCompleted); }, "Pending fake UI operations did not settle before disposal.");
            Form.Dispose();
        }
    }

    private sealed class PendingCommands(string source) : IStandaloneShopCommands
    {
        internal readonly ConcurrentQueue<ShopCommandRequest> Requests = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ShopCommandResponse>> pending = new();
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            var completion = new TaskCompletionSource<ShopCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(request.RequestId, completion)) throw new InvalidOperationException("Request ID was reused.");
            Requests.Enqueue(request);
            return completion.Task.WaitAsync(token);
        }
        internal void Complete(ShopCommandRequest request, ShopCommandResponse? response = null)
        {
            if (pending.TryRemove(request.RequestId, out var completion))
                completion.TrySetResult(response ?? new ShopCommandResponse { RequestId = request.RequestId, State = "Rejected",
                    Detail = "Fake backend recorded explicit command.", CurrentSourceSessionKey = source });
        }
        internal void CompleteAll() { foreach (var request in Requests) Complete(request); }
        public ValueTask DisposeAsync() { CompleteAll(); return ValueTask.CompletedTask; }
    }

    private sealed class FakeSource : ISlicedMemorySource
    {
        public void Configure(ReadSelection selection) { }
        public ReadResult Read(CancellationToken token) { token.ThrowIfCancellationRequested(); return new(null, new(), "OfflineForceUi", "Fake reader; no process or memory access."); }
        public ReadResult ReadSlice(ReadSelection fields, CancellationToken token) => Read(token);
        public void Dispose() { }
    }
}
