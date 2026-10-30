using System.Reflection;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal static class FrozenShoppingDispatchSelfTest
{
    internal static Task<IReadOnlyList<OfflineCheck>> RunAsync()
    {
        var checks = new List<OfflineCheck>();
        void Check(string name, Action body)
        {
            try { body(); checks.Add(new("fixed_shopping_" + name, true, "Real frozen preparation and empty-page policy with fake closed reads only; no LC/native/input.")); }
            catch (Exception error) { checks.Add(new("fixed_shopping_" + name, false, error.ToString())); }
        }
        static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
        static void Refuse(Action action)
        { bool refused = false; try { action(); } catch (InvalidOperationException) { refused = true; } if (!refused) throw new InvalidOperationException("Invalid page was admitted."); }
        Check("reader_thin_contract_excludes_owner_actor_state", () =>
        {
            var field = typeof(CurrentLcSource).GetField("FrozenShopDispatchFields", BindingFlags.NonPublic | BindingFlags.Static)!;
            var keys = (string[])field.GetValue(null)!;
            Need(keys.SequenceEqual(new[] { "selfId", "personalShopListing" }), "Thin reader includes owner actor/state fields.");
            Need(typeof(CurrentLcSource).GetMethod("ResolveFrozenShopDispatchSource", [typeof(SourceIdentity), typeof(CancellationToken), typeof(ulong)]) is not null,
                "Actual thin reader entry is missing.");
        });
        Check("saved_shopping_list_survives_age_and_four_distinct_pages", () =>
        {
            var f = new Fixture(); int reads = 0;
            foreach (var owner in f.Targets.Owners)
            foreach (string direction in new[] { ShopPageModes.ViewerBuy, ShopPageModes.ViewerSell })
            {
                var prepared = ShopForceDispatchPolicy.PrepareFrozenShoppingDispatch(() => { reads++; return f.Sample(owner.Id); }, f.Source,
                    f.Selection, f.Targets, owner, () => f.Now);
                prepared.RequireFresh(f.Now);
                Need(prepared.Context is null && prepared.Binding.BindingOnly
                    && owner.ObservedAt == f.Now.AddHours(-1) && owner.ShopStateSampledAt == f.Now.AddHours(-1),
                    "Historical owner was refreshed/retimed or a current actor list was required.");
            }
            Need(reads == 4, "More than one fresh closed read was taken per page.");
        });
        Check("initial_nonshopping_owner_rejected_before_page_read", () =>
        {
            var f = new Fixture(); int reads = 0;
            var owner = f.Targets.Owners[0] with { ShopState = ShopEligibilityPolicy.KnownOtherActionObserved, ActionType = ".?AVMovingAction@lineage@@" };
            var targets = f.Targets with { Owners = [owner] };
            Refuse(() => ShopForceDispatchPolicy.PrepareFrozenShoppingDispatch(() => { reads++; return f.Sample(owner.Id); },
                f.Source, f.Selection, targets, owner, () => f.Now));
            Need(reads == 0, "Initial list eligibility was skipped.");
        });
        foreach (string mutation in new[] { "source", "self", "missing_self", "old_self", "baseline_time", "baseline_owner", "binding_scope", "binding_layout", "binding_complete", "binding_bank", "binding_rows" })
        Check("thin_reject_" + mutation, () =>
        {
            var f = new Fixture(); var owner = f.Targets.Owners[0]; var sample = f.Sample(owner.Id);
            var fields = new Dictionary<string, FieldReading>(sample.Observations.Fields);
            if (mutation == "source") sample = sample with { Binding = sample.Binding with { Source = f.Source with { CreationFileTime = 2 } } };
            if (mutation == "self") fields["selfId"] = FieldReading.Observed(12UL, f.Now);
            if (mutation == "missing_self") fields.Remove("selfId");
            if (mutation == "old_self") fields["selfId"] = FieldReading.Observed(11UL, f.Now.AddSeconds(-3));
            if (mutation == "baseline_time") sample = sample with { Binding = sample.Binding with { BaselineListing = sample.Binding.BaselineListing with { SampledAt = f.Now.AddTicks(-1) } } };
            if (mutation == "baseline_owner") sample = sample with { Binding = sample.Binding with { BaselineListing = sample.Binding.BaselineListing with { OwnerIdRaw = 999 } } };
            if (mutation == "binding_scope") sample = sample with { Binding = sample.Binding with { BindingOnly = false } };
            if (mutation == "binding_layout") sample = sample with { Binding = sample.Binding with { BaselineListing = sample.Binding.BaselineListing with { LayoutState = "Present" } } };
            if (mutation == "binding_complete") sample = sample with { Binding = sample.Binding with { BaselineListing = sample.Binding.BaselineListing with { Complete = true } } };
            if (mutation == "binding_bank") sample = sample with { Binding = sample.Binding with { BaselineListing = sample.Binding.BaselineListing with { BankState = "Closed" } } };
            if (mutation == "binding_rows") sample = sample with { Binding = sample.Binding with { BaselineListing = sample.Binding.BaselineListing with { Rows = [new PersonalShopRow(0, "Observed", true, "old goods", "100", 100, "個", 1, "Observed", "fake")] } } };
            sample = sample with { Observations = sample.Observations with { Fields = fields } };
            Refuse(() => ShopForceDispatchPolicy.PrepareFrozenShoppingDispatch(() => sample, f.Source, f.Selection, f.Targets, owner, () => f.Now));
        });
        Check("thin_freshness_is_rechecked_at_publish_without_cached_fallback", () =>
        {
            var f = new Fixture(); var owner = f.Targets.Owners[0]; int reads = 0;
            var prepared = ShopForceDispatchPolicy.PrepareFrozenShoppingDispatch(() => { reads++; return f.Sample(owner.Id); }, f.Source,
                f.Selection, f.Targets, owner, () => f.Now);
            Refuse(() => prepared.RequireFresh(f.Now.AddSeconds(3)));
            Need(reads == 1, "Expired proof was replaced implicitly.");
        });
        foreach (string layout in new[] { "Present", "Absent" })
        Check("one_read_empty_" + layout + "_ignores_raw_owner_and_direction", () =>
        {
            var f = new Fixture(); var after = f.Empty(layout);
            var e = ShopSingleReadPagePolicy.TryObserveEmpty(f.Request, f.Source, f.Baseline, after, f.Native, f.Now, f.Now.AddSeconds(1));
            Need(e is { Schema: 2, ReadSucceeded: true, IncludedItems: 0 } && e.ObservedOwnerIdRaw == ulong.MaxValue
                && e.ObservedPageDirection is null && !e.DirectionVerified && !e.ResponseMatched,
                "Empty page required an owner/direction recheck or lost raw evidence.");
            var response = new ShopCommandResponse { RequestId = f.Request.RequestId, CurrentSourceSessionKey = f.Source.SessionKey,
                At = e!.ValidatedAt, State = ShopEmptyPageRules.SingleReadState, NativeEntered = true, NativeReturned = true,
                EmptyPageObserved = true, EmptyPageObservation = e };
            Need(ShopEmptyPageRules.IsNeutralResponse(response) && ShopScanPolicy.Decide(response, true, true) == ShopScanDisposition.Continue,
                "Empty page stopped the batch.");
        });
        foreach (string mutation in new[] { "null", "incomplete", "unknown_layout", "stale", "foreign_source", "items", "native_unsettled" })
        Check("one_read_not_empty_on_" + mutation, () =>
        {
            var f = new Fixture(); PersonalShopListing? after = f.Empty("Present"); var native = f.Native;
            after = mutation switch
            {
                "null" => null, "incomplete" => after with { IncompleteReasons = ["READ_PARTIAL"] },
                "unknown_layout" => after with { LayoutState = "Unknown" }, "stale" => after with { SampledAt = f.Now.AddSeconds(-3) },
                "foreign_source" => after with { SourceSessionKey = "other" },
                "items" => after with { Rows = [new PersonalShopRow(0, "Observed", true, "actual item", "100", 100, "個", 1, "Observed", "fake") ] },
                _ => after
            };
            if (mutation == "native_unsettled") native = new(true, true, false, "not settled", null) { SourceMatched = true };
            Need(ShopSingleReadPagePolicy.TryObserveEmpty(f.Request, f.Source, f.Baseline, after, native, f.Now, f.Now.AddSeconds(1)) is null,
                "Failed/incomplete/product-bearing observation became empty.");
        });
        Check("post_read_exception_is_one_attempt_and_never_empty", () =>
        {
            int reads = 0;
            var after = ShopForceDispatchPolicy.ObserveOnce(() => { reads++; throw new IOException("real read failure"); }, out var detail);
            Need(reads == 1 && after is null && detail.Contains("real read failure"), "Read failed but was retried or hidden.");
        });
        Check("products_require_actual_owner_and_direction_but_no_generation_poll", () =>
        {
            var f = new Fixture();
            var page = f.Empty("Present") with { OwnerIdRaw = 22, PageMode = ShopPageModes.ViewerBuy,
                PageModeState = "MatchedExclusiveBankAndRows",
                Rows = [new PersonalShopRow(0, "Observed", true, "actual item", "100", 100, "個", 1, "Observed", "fake")] };
            Need(ShopSingleReadPagePolicy.ProductsMatch(f.Request, f.Source, page, f.Now, f.Now.AddSeconds(1)), "A fresh matching product page was rejected.");
            Need(!ShopSingleReadPagePolicy.ProductsMatch(f.Request, f.Source, page with { OwnerIdRaw = 23 }, f.Now, f.Now.AddSeconds(1))
                && !ShopSingleReadPagePolicy.ProductsMatch(f.Request, f.Source, page with { PageMode = ShopPageModes.ViewerSell }, f.Now, f.Now.AddSeconds(1)),
                "A different owner/direction was added to the requested shop.");
        });
        return Task.FromResult<IReadOnlyList<OfflineCheck>>(checks);
    }

    private sealed class Fixture
    {
        internal readonly DateTimeOffset Now = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        internal readonly SourceIdentity Source = new(4242, 123456, "offline-LC", "0x100000", 1, 4096, new string('A', 64), "fixed-shopping-source");
        internal ReadSelection Selection => new(["selfId", "personalShopListing"]);
        internal ShopTargetSnapshot Targets => new() { SnapshotId = "fixed-point-targets", SourceSessionKey = Source.SessionKey,
            RefreshedAt = Now.AddHours(-1), SelfId = 11, SelfPosition = new(100, 100), CanDispatch = true,
            Owners = new[] { 22UL, 23UL }.Select(id => new MarketOwner { Id = id, LocalIdentity = "fixed-owner-" + id, Position = new(100, 101),
                ObservedAt = Now.AddHours(-1), ShopStateSampledAt = Now.AddHours(-1), ShopState = ShopEligibilityPolicy.ShopActionObserved,
                ActionType = ".?AVShopAction@lineage@@", ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence }).ToArray() };
        internal ShopCommandRequest Request => new() { RequestId = "single-read-empty", ExpectedSourceSessionKey = Source.SessionKey,
            OwnerId = 22, ViewerDirection = ShopPageModes.ViewerBuy };
        internal PersonalShopListing Baseline => new() { SourceSessionKey = Source.SessionKey, SampledAt = Now.AddSeconds(-1), OwnerIdRaw = 99 };
        internal ShopNativeOutcome Native => new(true, true, true, "settled", null) { SourceMatched = true };
        internal PersonalShopListing Empty(string layout) => new() { SourceSessionKey = Source.SessionKey, OwnerIdRaw = ulong.MaxValue,
            SampledAt = Now.AddMilliseconds(500), LayoutState = layout, Complete = layout == "Present", BankState = "Closed", Rows = [] };
        internal ShopDispatchSample Sample(ulong owner)
        {
            var baseline = Baseline with { SampledAt = Now, LayoutState = "BindingOnly", Complete = false, PageModeState = "NotSampledBindingOnly", BankState = "NotSampled", Rows = [], SlotRows = [], RawBankSummary = [] };
            var binding = new ShopActionSourceBinding(Source, Now, 0x300000, baseline.OwnerIdRaw!.Value, 0x400000, 0x500000, 0x600000, 0x700000)
                { BaselineListing = baseline, BindingOnly = true };
            return new(binding, new ReadResult(Source, new() { ["selfId"] = FieldReading.Observed(11UL, Now),
                ["personalShopListing"] = FieldReading.Observed(baseline, Now) }, "Reading", "same pass thin fields"))
                { SelectedOwnerId = owner };
        }
    }
}
