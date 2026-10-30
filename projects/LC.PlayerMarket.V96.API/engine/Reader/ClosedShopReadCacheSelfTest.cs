namespace MemoryHub.Reader;

public sealed record ClosedShopCacheCheck(string Name, bool Passed, string Detail);
public static class ClosedShopReadCacheSelfTest
{
    public static ClosedShopCacheCheck[] Run(Func<ShopDispatchSample, ReadSelection, ulong, DateTimeOffset, bool>? dispatchValidation = null)
    {
        var checks = new List<ClosedShopCacheCheck>();
        void Check(string name, Action body)
        { try { body(); checks.Add(new("closed_shop_cache_" + name, true, "Pure closed-read/cache policy with fake owner scalar only; no LC/native/input.")); } catch (Exception error) { checks.Add(new("closed_shop_cache_" + name, false, error.ToString())); } }
        static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
        Check("valid_reuse_preserves_full_scope_and_original_timestamps", () =>
        {
            var f = new Fixture(); var result = f.Reuse(); var sample = result.Sample!;
            Need(sample is not null && f.Probes == 1 && result.Decision == "ClosedFullShopReused" && !sample.Binding.BindingOnly
                && sample.Binding.BaselineListing.LayoutState == "Present" && sample.Binding.BaselineListing.Rows.Single().Name == "original full-shop item"
                && sample.Binding.SampledAt == f.Entry.Binding.SampledAt && sample.Observations.Fields["selfId"].SampledAt == f.Entry.IdentitySampledAt
                && sample.Binding.ClosedReadReuse is { Scope: "ClosedFullShopCache" } proof && proof.OwnerProbeRaw == 99
                && proof.ValidFor(sample.Binding, sample.Observations, f.Configuration, 11, f.Now), "Full cache provenance/scope was fabricated.");
            if (dispatchValidation is not null) Need(dispatchValidation(sample, f.Configuration, 11, f.Now), "Actual managed dispatch rejected a trusted closed full-shop cache.");
        });
        Check("original_current_owner_is_not_relabelled_as_new_target", () =>
        {
            var f = new Fixture(); var result = f.Reuse();
            Need(result.Sample is { SelectedOwnerId: 22 } sample && sample.Binding.CurrentOwnerIdRaw == 99
                && sample.Binding.BaselineListing.OwnerIdRaw == 99, "New target owner overwrote the real currently selected owner.");
        });
        foreach (string mutation in new[] { "source", "configuration", "profile", "self", "self_time", "shop_time", "future_time", "scope", "baseline_owner", "baseline_time", "disabled" })
        Check("ineligible_" + mutation + "_never_probes_or_reuses", () =>
        {
            var f = new Fixture(); var entry = f.Entry; var source = f.Source; var configured = f.Configuration; var profile = f.Profile;
            if (mutation == "source") source = source with { CreationFileTime = source.CreationFileTime + 1 };
            if (mutation == "configuration") configured = new(["selfId", "personalShopListing"]);
            if (mutation == "profile") profile = new ReaderProfile(profile.Id, profile.Identity, profile.Rvas, profile.AllowedFields, profile.RelocationEvidence, profile.TypeBindings, profile.FieldOffsets);
            if (mutation == "self") entry = entry with { SelfField = FieldReading.Observed(12UL, entry.IdentitySampledAt) };
            if (mutation == "self_time") entry = entry with { IdentitySampledAt = f.Now.AddSeconds(-3), SelfField = FieldReading.Observed(11UL, f.Now.AddSeconds(-3)) };
            if (mutation == "shop_time") entry = entry with { ShopField = FieldReading.Observed(entry.Binding.BaselineListing, f.Now.AddSeconds(-3)) };
            if (mutation == "future_time") entry = entry with { SelfField = FieldReading.Observed(11UL, f.Now.AddTicks(1)), IdentitySampledAt = f.Now.AddTicks(1) };
            if (mutation == "scope") entry = entry with { Binding = entry.Binding with { BindingOnly = true } };
            if (mutation == "baseline_owner") entry = entry with { Binding = entry.Binding with { BaselineListing = entry.Binding.BaselineListing with { OwnerIdRaw = 100 } } };
            if (mutation == "baseline_time") entry = entry with { Binding = entry.Binding with { BaselineListing = entry.Binding.BaselineListing with { SampledAt = f.Now } } };
            if (mutation == "disabled") configured = new(["selfId"]);
            var result = ClosedShopReadReusePolicy.Try(entry, source, configured, profile, 22, () => f.Now, f.Probe);
            Need(result.Sample is null && f.Probes == 0, "Invalid cached proof reached a live scalar probe.");
        });
        foreach (string mutation in new[] { "different_owner", "owner_ab_changed", "unclosed", "probe_exception", "future_probe", "expired_during_probe" })
        Check("probe_" + mutation + "_falls_back_once", () =>
        {
            var f = new Fixture(); int fresh = 0; var now = f.Now;
            var attempt = ClosedShopReadReusePolicy.Try(f.Entry, f.Source, f.Configuration, f.Profile, 22, () => now, _ =>
            {
                f.Probes++;
                if (mutation == "probe_exception") throw new IOException("fake source refusal");
                if (mutation == "expired_during_probe") now = now.AddSeconds(3);
                return mutation switch
                {
                    "different_owner" => new(true, 100, 100, f.Now, "OwnerChanged"),
                    "owner_ab_changed" => new(true, 99, 100, f.Now, "OwnerChanged"),
                    "unclosed" => new(false, 99, 99, f.Now, "ProbeSourceUnknown"),
                    "future_probe" => new(true, 99, 99, f.Now.AddTicks(1), ""),
                    _ => new(true, 99, 99, f.Now, "")
                };
            });
            var chosen = ClosedShopReadReusePolicy.Choose(attempt, () => { fresh++; return f.FreshSample; });
            Need(attempt.Sample is null && f.Probes == 1 && fresh == 1 && ReferenceEquals(chosen, f.FreshSample), "Miss retried cache/native or failed to make one fresh operation.");
        });
        Check("no_cache_calls_fresh_once_without_scalar_read", () =>
        {
            var f = new Fixture(); int fresh = 0;
            var miss = ClosedShopReadReusePolicy.Try(null, f.Source, f.Configuration, f.Profile, 22, () => f.Now, f.Probe);
            _ = ClosedShopReadReusePolicy.Choose(miss, () => { fresh++; return f.FreshSample; });
            Need(f.Probes == 0 && fresh == 1, "An absent cache caused more than one fresh operation.");
        });
        Check("cache_hit_never_calls_fresh_provider", () =>
        {
            var f = new Fixture(); int fresh = 0;
            var result = ClosedShopReadReusePolicy.Choose(f.Reuse(), () => { fresh++; return f.FreshSample; });
            Need(f.Probes == 1 && fresh == 0 && result.Binding.ClosedReadReuse is not null, "A hit also decoded a new product page.");
        });
        Check("cancelled_probe_is_not_converted_to_fresh_fallback", () =>
        {
            var f = new Fixture(); bool cancelled = false;
            try { _ = ClosedShopReadReusePolicy.Try(f.Entry, f.Source, f.Configuration, f.Profile, 22, () => f.Now, _ => throw new OperationCanceledException()); }
            catch (OperationCanceledException) { cancelled = true; }
            Need(cancelled, "Operator cancellation became another live read.");
        });
        Check("publish_rechecks_original_age_and_expected_self", () =>
        {
            var f = new Fixture(); var sample = f.Reuse().Sample!; var proof = sample.Binding.ClosedReadReuse!;
            Need(!proof.ValidFor(sample.Binding, sample.Observations, f.Configuration, 12, f.Now)
                && !proof.ValidFor(sample.Binding, sample.Observations, f.Configuration, 11, f.Now.AddSeconds(3)), "Publishing renewed a stale identity/shop proof.");
        });
        Check("proof_rejects_replaced_root_field_or_full_baseline", () =>
        {
            var f = new Fixture(); var sample = f.Reuse().Sample!; var proof = sample.Binding.ClosedReadReuse!;
            Need(!proof.ValidFor(sample.Binding with { PlayerShopSubsystem = sample.Binding.PlayerShopSubsystem + 8 }, sample.Observations, f.Configuration, 11, f.Now)
                && !proof.ValidFor(sample.Binding with { BaselineListing = sample.Binding.BaselineListing with { Complete = false } }, sample.Observations, f.Configuration, 11, f.Now), "Unbound pointers/products acquired a reused proof.");
            var changed = sample.Observations with { Fields = new(sample.Observations.Fields) { ["selfId"] = FieldReading.Observed(11UL, f.Now) } };
            Need(!proof.ValidFor(sample.Binding, changed, f.Configuration, 11, f.Now), "Identity was retimed on an unchanged copied value.");
        });
        Check("in_place_array_mutation_cannot_reuse_original_full_shop_proof", () =>
        {
            var f = new Fixture(); var hit = f.Reuse(); var sample = hit.Sample!; var proof = sample.Binding.ClosedReadReuse!;
            sample.Binding.BaselineListing.Rows[0] = sample.Binding.BaselineListing.Rows[0] with { Name = "mutated after closure" };
            Need(!proof.ValidFor(sample.Binding, sample.Observations, f.Configuration, 11, f.Now), "A mutable array retained its old closed proof.");
            int before = f.Probes; var next = f.Reuse();
            Need(next.Sample is null && f.Probes == before && next.Decision == "FullShopContentChanged", "Mutated cache reached scalar read or bypassed fallback.");
        });
        Check("timing_values_are_independent_of_sample_timestamps", () =>
        {
            var f = new Fixture(); var cached = f.Reuse();
            var metrics = new ShopDispatchReadMetrics(12, 30, cached.OwnerProbeMs, true, cached.Decision);
            var sample = cached.Sample! with { Binding = cached.Sample!.Binding with { ReadMetrics = metrics } };
            Need(metrics.AdmissionWaitMs == 12 && metrics.BodyMs == 30 && metrics.OwnerProbeMs >= 0
                && sample.Binding.SampledAt == f.Entry.Binding.SampledAt && sample.Binding.ClosedReadReuse!.IdentitySampledAt == f.Entry.IdentitySampledAt,
                "Timing diagnostics changed original read timestamps.");
        });
        return checks.ToArray();
    }

    private sealed class Fixture
    {
        internal DateTimeOffset Now = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        internal readonly ReaderProfile Profile = ReaderProfile.Builtin;
        internal readonly ReadSelection Configuration = new(["selfId", "personalShopListing"]);
        internal readonly SourceIdentity Source;
        internal readonly ClosedShopReadCacheEntry Entry;
        internal readonly ShopDispatchSample FreshSample;
        internal int Probes;
        internal Fixture()
        {
            Source = new(4242, 123456, "offline-cache-LC", "0x100000", Profile.Identity.PeTimestamp, Profile.Identity.ImageSize, Profile.Sha256, "closed-shop-cache-fixture");
            var baseline = new PersonalShopListing { SourceSessionKey = Source.SessionKey, SampledAt = Now.AddMilliseconds(-600), OwnerIdRaw = 99,
                LayoutState = "Present", Complete = true, BankState = "Closed", PageMode = ShopPageModes.ViewerSell,
                Rows = [new(0, "Observed", true, "original full-shop item", "100", 100, "個", 1, "Observed", "offline")] };
            var binding = new ShopActionSourceBinding(Source, baseline.SampledAt, 0x300000, 99, 0x400000, 0x500000, 0x600000, 0x700000)
                { BaselineListing = baseline, BindingOnly = false };
            var ownAt = Now.AddMilliseconds(-800); var self = FieldReading.Observed(11UL, ownAt); var shop = FieldReading.Observed(baseline, baseline.SampledAt);
            Entry = new(binding, 11, ownAt, Configuration, Profile, self, shop, 12345, DateTime.UnixEpoch);
            FreshSample = new(binding, new(Source, new() { ["selfId"] = self, ["personalShopListing"] = shop }, "Reading", "fresh provider fixture")) { SelectedOwnerId = 22 };
        }
        internal ClosedShopOwnerProbe Probe(ClosedShopReadCacheEntry _)
        { Probes++; return new(true, 99, 99, Now.AddTicks(-1), "OwnerScalarABClosed"); }
        internal ClosedShopReuseAttempt Reuse() => ClosedShopReadReusePolicy.Try(Entry, Source, Configuration, Profile, 22, () => Now, Probe);
    }
}
