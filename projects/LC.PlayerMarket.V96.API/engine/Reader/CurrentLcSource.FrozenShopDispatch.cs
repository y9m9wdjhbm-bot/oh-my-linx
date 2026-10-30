using System.Diagnostics;
namespace MemoryHub.Reader;

public sealed partial class CurrentLcSource
{
    private static readonly string[] FrozenShopDispatchFields = ["selfId", "personalShopListing"];

    public ShopDispatchSample ResolveFrozenShopDispatchSource(SourceIdentity expected, CancellationToken token, ulong ownerId)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (ownerId is 0 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(ownerId));
        var admission = Stopwatch.StartNew();
        using var acquired = ReadAdmission.Enter(gate, token);
        double waitMs = admission.Elapsed.TotalMilliseconds;
        var body = Stopwatch.StartNew();
        string decision = "NotChecked"; double probeMs = 0; bool reused = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var configured = Volatile.Read(ref selection); var profile = activeProfile;
            if (profileError is not null) throw new InvalidOperationException("SHOP_PROFILE_INVALID: " + profileError);
            foreach (string key in FrozenShopDispatchFields)
                if (!configured.Enabled(key) || profile is null || !profile.Allows(key))
                    throw new InvalidOperationException("SHOP_FROZEN_FIELD_UNAVAILABLE: " + key);
            var attempt = TryClosedShopRead(expected, configured, profile!, ownerId, token);
            decision = attempt.Decision; probeMs = attempt.OwnerProbeMs; reused = attempt.Sample is not null;
            var sample = ClosedShopReadReusePolicy.Choose(attempt,
                () => ResolveFreshFrozenShopDispatch(expected, configured, profile!, token, ownerId));
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(configured, Volatile.Read(ref selection)) || !ReferenceEquals(profile, activeProfile))
                throw new InvalidOperationException("SHOP_CONFIGURATION_CHANGED");
            var now = DateTimeOffset.UtcNow;
            foreach (string key in FrozenShopDispatchFields)
                if (!sample.Observations.Fields.TryGetValue(key, out var field) || !field.Fresh(now) || field.Value is null)
                    throw new InvalidOperationException("SHOP_FROZEN_SOURCE_UNAVAILABLE: " + key);
            var metrics = new ShopDispatchReadMetrics(waitMs, body.Elapsed.TotalMilliseconds, probeMs, reused, decision);
            return sample with { Binding = sample.Binding with { ReadMetrics = metrics } };
        }
        catch { InvalidateClosedShopRead(); throw; }
        finally
        {
            Emit(new(DateTimeOffset.UtcNow, "SHOP_DISPATCH_READ_COST",
                "來源鎖等待與讀取本體分列；閉合full-shop重用保留原時間，使用前仍做目前店主scalar A/B。",
                new { AdmissionWaitMs = waitMs, BodyMs = body.Elapsed.TotalMilliseconds, OwnerProbeMs = probeMs,
                    CacheReused = reused, CacheDecision = decision, OwnerId = ownerId }));
        }
    }

    private ShopDispatchSample ResolveFreshFrozenShopDispatch(SourceIdentity expected, ReadSelection configured,
        ReaderProfile profile, CancellationToken token, ulong ownerId)
    {
        ShopActionSourceBinding? captured = null;
        var result = ShopDispatchReadRecovery.Read(() =>
        {
            captured = null;
            var current = ReadProfile(token, profile, new ReadSelection(FrozenShopDispatchFields), configured,
                reuseMetadata: metadataReuse, observedShopActionSource: value => captured = value, shopBindingOnly: true);
            token.ThrowIfCancellationRequested(); SameConfiguration();
            FrozenShopDispatchSourceValidation.RequireCurrent(current, expected);
            _ = AttachBaselineListing(captured, current, DateTimeOffset.UtcNow);
            return current;
        }, expected, SameConfiguration, token,
            entry => Emit(new(DateTimeOffset.UtcNow, entry.Phase,
                "固定名單開頁：閉合full-shop快取未符合條件，執行既有有界fresh來源解析。", entry)), frozenTargets: true);
        token.ThrowIfCancellationRequested(); SameConfiguration();
        return new(AttachBaselineListing(captured, result, DateTimeOffset.UtcNow), result) { SelectedOwnerId = ownerId };
        void SameConfiguration()
        {
            if (!ReferenceEquals(configured, Volatile.Read(ref selection)) || !ReferenceEquals(profile, activeProfile))
                throw new InvalidOperationException("SHOP_CONFIGURATION_CHANGED");
        }
    }
}

internal static class FrozenShopDispatchSourceValidation
{
    internal static void RequireCurrent(ReadResult current, SourceIdentity expected)
    {
        if (current.Source is not null && current.Source != expected) throw new InvalidOperationException("SHOP_SOURCE_CHANGED");
        if (current.NoSourceRead) throw new InvalidOperationException("SHOP_DISPATCH_SOURCE_UNAVAILABLE");
        if (current.ReadFailureCode is not null || current.ReadFailureNativeError is not null)
        {
            bool timeout = current.Source is null && current.State == "Unknown"
                && current.ReadFailureCode == "TIME_LIMIT" && current.ReadFailureNativeError == 0
                && new[] { "selfId", "personalShopListing" }.All(key => current.Fields.TryGetValue(key, out var f)
                    && f.State == "Unknown" && f.Value is null && f.SampledAt is null);
            if (timeout) throw new ShopDispatchReadTimeoutException(expected, 0, 0, "SHOP_DISPATCH_READ_TIMEOUT: " + current.Detail);
            throw new InvalidOperationException("SHOP_DISPATCH_READ_FAILED: " + current.Detail);
        }
        if (current.Source is null) throw new InvalidOperationException("SHOP_DISPATCH_SOURCE_UNAVAILABLE");
    }
}
