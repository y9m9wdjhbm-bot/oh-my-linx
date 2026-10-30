using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
namespace MemoryHub.Reader;

public sealed record ShopDispatchReadMetrics(double AdmissionWaitMs, double BodyMs, double OwnerProbeMs,
    bool CacheReused, string CacheDecision);

// The constructor is reader-only. A full observation retains its actual scope
// and original identity/shop timestamps; it is never relabelled BindingOnly.
public sealed class ShopClosedReadReuseProof
{
    private readonly ClosedShopReadCacheEntry original;
    public string Scope => "ClosedFullShopCache";
    public ulong VerifiedSelfId => original.SelfId;
    public DateTimeOffset IdentitySampledAt => original.IdentitySampledAt;
    public DateTimeOffset ShopSampledAt => original.Binding.SampledAt;
    public DateTimeOffset OwnerProbeSampledAt { get; }
    public ulong OwnerProbeRaw { get; }
    internal ShopClosedReadReuseProof(ClosedShopReadCacheEntry original, DateTimeOffset sampledAt, ulong raw)
    { this.original = original; OwnerProbeSampledAt = sampledAt; OwnerProbeRaw = raw; }

    public bool ValidFor(ShopActionSourceBinding binding, ReadResult observations, ReadSelection configuration,
        ulong expectedSelfId, DateTimeOffset now)
    {
        if (!ReferenceEquals(configuration, original.Configuration) || original.SelfId != expectedSelfId
            || binding.BindingOnly || binding.Source != original.Binding.Source || observations.Source != binding.Source
            || observations.NoSourceRead || observations.ReadFailureCode is not null || observations.ReadFailureNativeError is not null
            || binding.SampledAt != original.Binding.SampledAt || binding.CurrentOwnerIdRaw != original.Binding.CurrentOwnerIdRaw
            || binding.PlayerShopSubsystem != original.Binding.PlayerShopSubsystem || binding.ServiceRoot != original.Binding.ServiceRoot
            || binding.UISubsystem != original.Binding.UISubsystem || binding.Director != original.Binding.Director || binding.Scene != original.Binding.Scene
            || !ReferenceEquals(binding.BaselineListing, original.Binding.BaselineListing) || !original.ContentUnchanged
            || !observations.Fields.TryGetValue("selfId", out var self) || !ReferenceEquals(self, original.SelfField)
            || !observations.Fields.TryGetValue("personalShopListing", out var shop) || !ReferenceEquals(shop, original.ShopField)
            || !self.Fresh(now) || !shop.Fresh(now) || !Fresh(OwnerProbeSampledAt, now)
            || OwnerProbeRaw != binding.CurrentOwnerIdRaw || binding.BaselineListing.SampledAt != ShopSampledAt
            || binding.BaselineListing.SourceSessionKey != binding.Source.SessionKey || binding.BaselineListing.OwnerIdRaw != OwnerProbeRaw)
            return false;
        return self.Value is { ValueKind: JsonValueKind.Number } own && own.TryGetUInt64(out ulong id) && id == expectedSelfId
            && original.SelfId != 0 && original.Binding.BaselineListing.LayoutState != "BindingOnly";
    }
    private static bool Fresh(DateTimeOffset sample, DateTimeOffset now) => sample != default && sample <= now
        && now - sample <= TimeSpan.FromMilliseconds(HubProtocol.FreshMilliseconds);
}

internal sealed record ClosedShopReadCacheEntry(ShopActionSourceBinding Binding, ulong SelfId,
    DateTimeOffset IdentitySampledAt, ReadSelection Configuration, ReaderProfile Profile,
    FieldReading SelfField, FieldReading ShopField, long DiskLength, DateTime DiskModifiedUtc)
{
    private readonly string? baselineFingerprint = Fingerprint(Binding.BaselineListing);
    internal bool ContentUnchanged => baselineFingerprint is not null && Fingerprint(Binding.BaselineListing) == baselineFingerprint;
    private static string? Fingerprint(PersonalShopListing? baseline)
    {
        if (baseline is null) return null;
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(baseline, HubProtocol.Json);
            return bytes.Length <= 4 * 1024 * 1024 ? Convert.ToHexString(SHA256.HashData(bytes)) : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return null; }
    }
    internal string? Ineligible(SourceIdentity expected, ReadSelection configuration, ReaderProfile profile, DateTimeOffset now)
    {
        if (Binding.Source != expected) return "SourceChanged";
        if (!ContentUnchanged) return "FullShopContentChanged";
        if (!ReferenceEquals(Configuration, configuration)) return "ConfigurationChanged";
        if (!ReferenceEquals(Profile, profile) || Profile.Sha256 != expected.DiskSha256) return "ProfileChanged";
        if (!configuration.Enabled("selfId") || !configuration.Enabled("personalShopListing")) return "RequiredFieldDisabled";
        if (SelfId == 0 || SelfField.Value is not { ValueKind: JsonValueKind.Number } own || !own.TryGetUInt64(out ulong id) || id != SelfId) return "SelfProofInvalid";
        if (SelfField.SampledAt != IdentitySampledAt || !SelfField.Fresh(now) || !ShopField.Fresh(now)) return "OriginalSampleExpired";
        var baseline = Binding.BaselineListing;
        if (Binding.BindingOnly || baseline is null || baseline.LayoutState == "BindingOnly"
            || baseline.SampledAt != Binding.SampledAt || ShopField.SampledAt != Binding.SampledAt
            || baseline.SourceSessionKey != expected.SessionKey || baseline.OwnerIdRaw != Binding.CurrentOwnerIdRaw)
            return "FullShopScopeInvalid";
        return null;
    }
}

internal sealed record ClosedShopOwnerProbe(bool Closed, ulong Before, ulong After, DateTimeOffset SampledAt, string Detail);
internal sealed record ClosedShopReuseAttempt(ShopDispatchSample? Sample, string Decision, double OwnerProbeMs);

internal static class ClosedShopReadReusePolicy
{
    internal static ClosedShopReuseAttempt Try(ClosedShopReadCacheEntry? entry, SourceIdentity expected,
        ReadSelection configuration, ReaderProfile profile, ulong ownerId, Func<DateTimeOffset> now,
        Func<ClosedShopReadCacheEntry, ClosedShopOwnerProbe> probe)
    {
        if (entry is null) return new(null, "NoClosedFullShop", 0);
        if (entry.Ineligible(expected, configuration, profile, now()) is { } reason) return new(null, reason, 0);
        var timer = Stopwatch.StartNew(); ClosedShopOwnerProbe observed;
        try { observed = probe(entry); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { return new(null, "OwnerProbeFailed:" + error.GetType().Name, timer.Elapsed.TotalMilliseconds); }
        double milliseconds = timer.Elapsed.TotalMilliseconds;
        if (!observed.Closed || observed.Before != observed.After || observed.After != entry.Binding.CurrentOwnerIdRaw)
            return new(null, observed.Detail.Length == 0 ? "OwnerChanged" : observed.Detail, milliseconds);
        if (entry.Ineligible(expected, configuration, profile, now()) is { } expired) return new(null, expired, milliseconds);
        var proof = new ShopClosedReadReuseProof(entry, observed.SampledAt, observed.After);
        var binding = entry.Binding with { ClosedReadReuse = proof };
        var observations = new ReadResult(expected, new() { ["selfId"] = entry.SelfField, ["personalShopListing"] = entry.ShopField },
            "Reading", "Reuse of the original closed full-shop read; original identity/shop timestamps retained.");
        if (!proof.ValidFor(binding, observations, configuration, entry.SelfId, now())) return new(null, "ReuseProofInvalid", milliseconds);
        return new(new(binding, observations) { SelectedOwnerId = ownerId }, "ClosedFullShopReused", milliseconds);
    }
    internal static ShopDispatchSample Choose(ClosedShopReuseAttempt cached, Func<ShopDispatchSample> fresh) => cached.Sample ?? fresh();
}
