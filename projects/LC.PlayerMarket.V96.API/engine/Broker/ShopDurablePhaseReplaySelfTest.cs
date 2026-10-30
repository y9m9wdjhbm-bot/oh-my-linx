using System.Security.Cryptography;
using LcPlayerMarket;

namespace MemoryHub.Service;

internal static class ShopDurablePhaseIoVerification
{
    internal static async Task<IReadOnlyList<OfflineCheck>> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        checks.AddRange(await ShopDurablePhaseIoSelfTest.RunAsync().ConfigureAwait(false));
        checks.AddRange(await ShopDurablePhaseReplaySelfTest.RunAsync().ConfigureAwait(false));
        return checks;
    }
}

internal static class ShopDurablePhaseReplaySelfTest
{
    internal static async Task<IReadOnlyList<OfflineCheck>> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        async Task Check(string name, Func<Task> body)
        {
            try { await body(); checks.Add(new("phase_io_store_" + name, true, "Actual durable store/request files only; no reader, LC or native helper.")); }
            catch (Exception error) { checks.Add(new("phase_io_store_" + name, false, error.ToString())); }
        }
        static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static async Task Refuses(Func<Task> body)
        {
            bool refused = false;
            try { await body(); } catch (AggregateException failure) { refused = failure.Flatten().InnerExceptions.Count != 0; }
            Need(refused, "Phase did not retain a durable writer failure.");
        }
        await Check("attempt_and_child_request_precede_single_fake_native", async () =>
        {
            var f = new Fixture(); int nativeCalls = 0, ownerIntentCommits = 0;
            await ShopBroker.PersistScanAttemptPhaseAsync(f.Scans, f.Page, f.RequestId, f.RequestPath, f.Request);
            Need(File.Exists(f.RequestPath), "No durable child request.");
            await ShopBroker.PersistNativeIntentPhaseAsync(f.Owners, f.Scans, f.Source, f.Owner, ShopPageModes.ViewerBuy,
                f.RequestId, f.PendingPath, f.Pending, true, () => ownerIntentCommits++);
            Need(ownerIntentCommits == 1 && File.Exists(f.PendingPath) && f.Scans.BlocksSource(f.Source) && f.Owners.BlocksSource(f.Source),
                "All three original intents did not complete before native admission.");
            nativeCalls++;
            Need(nativeCalls == 1, "Native fake was repeated.");
            f.Reload();
            Need(f.Scans.BlocksSource(f.Source) && f.Owners.BlocksSource(f.Source), "Reload erased original uncertain native intent.");
        });
        await Check("existing_child_request_is_preserved_and_native_never_runs", async () =>
        {
            var f = new Fixture(); int nativeCalls = 0;
            byte[] original = "ORIGINAL_REQUEST_NO_REPLAY"u8.ToArray(); File.WriteAllBytes(f.RequestPath, original);
            await Refuses(async () =>
            {
                await ShopBroker.PersistScanAttemptPhaseAsync(f.Scans, f.Page, f.RequestId, f.RequestPath, f.Request);
                nativeCalls++;
            });
            Need(nativeCalls == 0 && File.ReadAllBytes(f.RequestPath).SequenceEqual(original), "Old request was replaced or replayed.");
        });
        await Check("pending_write_failure_joins_other_intents_and_keeps_uncertainty", async () =>
        {
            var f = new Fixture(); int nativeCalls = 0, committed = 0;
            await ShopBroker.PersistScanAttemptPhaseAsync(f.Scans, f.Page, f.RequestId, f.RequestPath, f.Request);
            byte[] original = "ORIGINAL_PENDING_DO_NOT_CLEAR"u8.ToArray(); File.WriteAllBytes(f.PendingPath, original);
            await Refuses(async () =>
            {
                await ShopBroker.PersistNativeIntentPhaseAsync(f.Owners, f.Scans, f.Source, f.Owner, ShopPageModes.ViewerBuy,
                    f.RequestId, f.PendingPath, f.Pending, true, () => committed++);
                nativeCalls++;
            });
            Need(nativeCalls == 0 && committed == 1 && File.ReadAllBytes(f.PendingPath).SequenceEqual(original),
                "Intent writer fault was not fully observed before native admission.");
            f.Reload();
            Need(f.Scans.BlocksSource(f.Source) && f.Owners.BlocksSource(f.Source), "Partial original intent became resend authority.");
        });
        await Check("existing_owner_intent_cannot_be_replaced_by_parallel_group", async () =>
        {
            var f = new Fixture(); int nativeCalls = 0, committed = 0;
            await ShopBroker.PersistScanAttemptPhaseAsync(f.Scans, f.Page, f.RequestId, f.RequestPath, f.Request);
            f.Owners.Begin(f.Source, f.Owner, ShopPageModes.ViewerBuy, f.RequestId, true);
            var originals = Directory.GetFiles(f.OwnerRoot, "*.json").ToDictionary(p => p, Hash);
            await Refuses(async () =>
            {
                await ShopBroker.PersistNativeIntentPhaseAsync(f.Owners, f.Scans, f.Source, f.Owner, ShopPageModes.ViewerBuy,
                    f.RequestId, f.PendingPath, f.Pending, true, () => committed++);
                nativeCalls++;
            });
            Need(nativeCalls == 0 && committed == 0 && originals.All(x => Hash(x.Key) == x.Value), "Old owner intent changed or native was replayed.");
            f.Reload(); Need(f.Scans.BlocksSource(f.Source) && f.Owners.BlocksSource(f.Source), "Existing uncertain source was reopened.");
        });
        await Check("response_result_pair_preserves_sent_page_on_reload", async () =>
        {
            var f = new Fixture(); await f.PrepareReturned();
            var response = new ShopCommandResponse { RequestId = f.RequestId, State = "Unverified", NativeEntered = true, NativeReturned = true };
            await ShopBroker.PersistScanResultPhaseAsync(f.Scans, f.RequestId, f.ResponsePath, response);
            Need(File.Exists(f.ResponsePath), "No durable response receipt.");
            f.Reload(); f.AssertReturnedNotReplayable();
        });
        await Check("response_failure_still_joins_result_without_replaying_returned_page", async () =>
        {
            var f = new Fixture(); await f.PrepareReturned();
            byte[] original = "EXISTING_RESPONSE_MUST_REMAIN"u8.ToArray(); File.WriteAllBytes(f.ResponsePath, original);
            var response = new ShopCommandResponse { RequestId = f.RequestId, State = "Unverified", NativeEntered = true, NativeReturned = true };
            await Refuses(() => ShopBroker.PersistScanResultPhaseAsync(f.Scans, f.RequestId, f.ResponsePath, response));
            Need(File.ReadAllBytes(f.ResponsePath).SequenceEqual(original), "Old response was overwritten.");
            f.Reload(); f.AssertReturnedNotReplayable();
        });
        return checks;
    }
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    private sealed class Fixture
    {
        private readonly string root = Path.Combine(@"D:\lineage\LC_PlayerMarket_LOG\maintenance\20261030-v96-phase-io-fixtures", Guid.NewGuid().ToString("N"));
        internal readonly SourceIdentity Source = new(4242, 987654321, "offline-only", "0x100000", 1, 4096, new string('A', 64), "offline-phase-io-source");
        internal readonly MarketOwner Owner;
        internal readonly ShopTargetSnapshot Targets;
        internal readonly SavedScanPage Page;
        internal readonly string RequestId = "offline-phase-request";
        internal readonly string PlanId;
        internal ShopScanProgressStore Scans;
        internal ShopSearchedOwnerStore Owners;
        internal string ProgressRoot => Path.Combine(root, "scan-progress");
        internal string OwnerRoot => Path.Combine(root, "searched-owners");
        internal string RequestPath => Path.Combine(root, "request.json");
        internal string PendingPath => Path.Combine(root, "native.pending.json");
        internal string ResponsePath => Path.Combine(root, "response.json");
        internal object Request => new { RequestId, State = "ScanChild_NoAutomaticReplay" };
        internal object Pending => new { RequestId, Source, Owner, State = "ForceNativeLaunchIntent_NoAutomaticReplay" };
        internal Fixture()
        {
            Directory.CreateDirectory(root); var at = DateTimeOffset.UtcNow.AddDays(-1);
            Owner = new() { Id = 22, LocalIdentity = "offline-phase-owner", Name = "offline", Position = new(100, 101), ObservedAt = at,
                ShopState = ShopEligibilityPolicy.ShopActionObserved, ActionType = ".?AVShopAction@lineage@@", ShopStateSampledAt = at, ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence };
            Targets = new() { SnapshotId = "offline-phase-targets", SourceSessionKey = Source.SessionKey, RefreshedAt = at,
                SelfId = 11, SelfPosition = new(100, 100), Owners = [Owner] };
            Scans = new(ProgressRoot); Owners = new(OwnerRoot, Path.Combine(root, "absent-import"));
            PlanId = Scans.StartNew(Source, Targets, true).PlanId; Page = new(Owner.Id, Owner.LocalIdentity, ShopPageModes.ViewerBuy);
        }
        internal void Reload()
        {
            Scans = new(ProgressRoot); Owners = new(OwnerRoot, ProgressRoot);
            if (Scans.Error is not null || Owners.Error is not null) throw new InvalidDataException("Fixture reload failed: " + Scans.Error + Owners.Error);
        }
        internal async Task PrepareReturned()
        {
            await ShopBroker.PersistScanAttemptPhaseAsync(Scans, Page, RequestId, RequestPath, Request);
            await ShopBroker.PersistNativeIntentPhaseAsync(Owners, Scans, Source, Owner, ShopPageModes.ViewerBuy,
                RequestId, PendingPath, Pending, true, () => { });
            Scans.NativeSettled(RequestId, true, true, "offline returned/cleanup");
            File.Move(PendingPath, PendingPath + ".resolved");
            Owners.CompletePage(RequestId, false);
        }
        internal void AssertReturnedNotReplayable()
        {
            var view = Scans.View(Source, Targets, false, false, true)!;
            if (view.SearchedPages != 1 || view.ConfirmedPages != 0 || view.UncertainPages != 0
                || Scans.Continue(PlanId, Source, Targets, allowEmpty: true, forceDispatch: true).Any(p => p == Page)
                || !Owners.ShouldSkip(Owner.Id, ShopPageModes.ViewerBuy, true))
                throw new InvalidDataException("Returned page lost original sent/no-replay proof.");
        }
    }
}
