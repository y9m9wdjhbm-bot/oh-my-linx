using System.Text.Json;

namespace MemoryHub.Reader;

/// <summary>
/// Service-only result of a new closed read. Never attach this object to a
/// HubSnapshot or client reply. Addresses are local to Source and are not leases:
/// the native broker must recheck identity and current-owner expectations at use.
/// </summary>
public sealed record ShopActionSourceBinding(
    SourceIdentity Source, DateTimeOffset SampledAt,
    ulong PlayerShopSubsystem, ulong CurrentOwnerIdRaw,
    ulong ServiceRoot, ulong UISubsystem, ulong Director, ulong Scene)
{
    /// <summary>The exact same closed read as the action addresses; Resolve always initializes this before returning.</summary>
    public PersonalShopListing BaselineListing { get; init; } = null!;
    public bool BindingOnly { get; init; }
    public ShopClosedReadReuseProof? ClosedReadReuse { get; init; }
    public ShopDispatchReadMetrics? ReadMetrics { get; init; }
}

/// <summary>Service-only observations from the same closed reader pass as the shop binding. Never send the binding over IPC.</summary>
public sealed record ShopDispatchSample(ShopActionSourceBinding Binding, ReadResult Observations)
{
    // Service-only partial actor leaves. Never publish as a complete nearby list.
    public ulong? SelectedOwnerId { get; init; }
}

internal sealed record ShopActionSourceCandidate(
    string SourceSessionKey, DateTimeOffset SampledAt,
    ulong PlayerShopSubsystem, ulong CurrentOwnerIdRaw,
    ulong ServiceRoot, ulong UISubsystem, ulong Director, ulong Scene)
{ internal bool BindingOnly { get; init; } }

public sealed partial class CurrentLcSource
{
    private static readonly string[] ShopDispatchFields =
        ["selfId", "position", "nearbyPlayers", "playerShopStates", "personalShopListing"];

    /// <summary>Bounded fresh passes of all prerequisites, pinned to the caller's original source.</summary>
    public ShopDispatchSample ResolveShopDispatchSource(SourceIdentity expected, CancellationToken token)
        => ResolveShopDispatchSourceCore(expected, token, null);

    public ShopDispatchSample ResolveShopDispatchSource(SourceIdentity expected, CancellationToken token, ulong ownerId)
    {
        if (ownerId is 0 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(ownerId));
        return ResolveShopDispatchSourceCore(expected, token, ownerId);
    }

    private ShopDispatchSample ResolveShopDispatchSourceCore(SourceIdentity expected, CancellationToken token, ulong? ownerId)
    {
        ArgumentNullException.ThrowIfNull(expected);
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            var configured = Volatile.Read(ref selection);
            foreach (string key in ShopDispatchFields)
                if (!configured.Enabled(key))
                    throw new InvalidOperationException($"SHOP_FIELD_DISABLED：{key} 已停用；本輪未開啟 LC。");
            if (profileError is not null)
                throw new InvalidOperationException("SHOP_PROFILE_INVALID：" + profileError);
            var profile = activeProfile;
            foreach (string key in ShopDispatchFields)
                if (profile is null || !profile.Allows(key))
                    throw new InvalidOperationException($"SHOP_PROFILE_UNCONFIRMED：{key} 尚無已保存的確認版型；本輪未開啟 LC。");

            ShopActionSourceBinding? captured = null;
            var result = ShopDispatchReadRecovery.Read(() =>
            {
                // Discard every previous pass, including its binding and baseline.
                // ReadProfile always closes its read session before returning;
                // only its complete typed deadline envelope may re-enter this callback.
                captured = null;
                var current = ReadProfile(token, profile!, new ReadSelection(ShopDispatchFields), configured,
                    reuseMetadata: metadataReuse, observedShopActionSource: binding => captured = binding,
                    dispatchTargetOwnerId: ownerId);
                token.ThrowIfCancellationRequested();
                RequireSameConfiguration();
                ShopDispatchSourceValidation.RequireCurrent(current, expected);
                // Even a retryable actor failure requires a closed current shop root.
                _ = AttachBaselineListing(captured, current, DateTimeOffset.UtcNow);
                return current;
            }, expected, RequireSameConfiguration, token,
                entry => Emit(new(DateTimeOffset.UtcNow, entry.Phase,
                    "送出前完整重新採樣；未送出原生請求。", entry)));
            token.ThrowIfCancellationRequested();
            RequireSameConfiguration();
            RequireFreshFields();
            var binding = AttachBaselineListing(captured, result, DateTimeOffset.UtcNow);
            token.ThrowIfCancellationRequested();
            RequireSameConfiguration();
            RequireFreshFields();
            return new(binding, result) { SelectedOwnerId = ownerId };

            void RequireSameConfiguration()
            {
                if (!ReferenceEquals(configured, Volatile.Read(ref selection)) || !ReferenceEquals(profile, activeProfile))
                    throw new InvalidOperationException("SHOP_CONFIGURATION_CHANGED：同輪商店讀取期間，欄位設定或版型已改變。");
            }
            void RequireFreshFields()
            {
                var now = DateTimeOffset.UtcNow;
                var invalid = ShopDispatchFields.Where(key => !result.Fields.TryGetValue(key, out var field)
                    || !field.Fresh(now) || field.Value is null).Select(key =>
                    {
                        var field = result.Fields.GetValueOrDefault(key);
                        return $"{key}={field?.State ?? "Missing"}, sampledAt={field?.SampledAt:O}, ageMs={(now-field?.SampledAt)?.TotalMilliseconds:F1}, detail={field?.Detail}";
                    }).ToArray();
                if (result.Source is null || result.NoSourceRead || invalid.Length != 0)
                    throw new InvalidOperationException("SHOP_DISPATCH_SOURCE_UNAVAILABLE：同輪必要資料未完整取得或已過期；" + string.Join("；", invalid) + "；" + result.Detail);
            }
        }
    }

    /// <summary>
    /// Reads the currently enabled, confirmed shop field again under the source
    /// gate. No old pointer cache, implicit field enable, profile application,
    /// game input, or memory write occurs here.
    /// </summary>
    public ShopActionSourceBinding ResolveShopActionSource(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            var configured = Volatile.Read(ref selection);
            if (!configured.Enabled("personalShopListing"))
                throw new InvalidOperationException("SHOP_FIELD_DISABLED：玩家商店欄位已停用，未開啟 LC。");
            if (profileError is not null)
                throw new InvalidOperationException("SHOP_PROFILE_INVALID：" + profileError);
            var profile = activeProfile;
            if (profile is null || !profile.Allows("personalShopListing"))
                throw new InvalidOperationException("SHOP_PROFILE_UNCONFIRMED：目前版本尚未保存玩家商店欄位的真實校對結果，未開啟 LC。");

            ShopActionSourceBinding? captured = null;
            var result = ReadProfile(token, profile, new ReadSelection(["personalShopListing"]), configured,
                reuseMetadata: metadataReuse, observedShopActionSource: binding => captured = binding);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(configured, Volatile.Read(ref selection)) || !ReferenceEquals(profile, activeProfile))
                throw new InvalidOperationException("SHOP_CONFIGURATION_CHANGED：商店讀取選項或版型在本次解析中改變。");
            var binding = AttachBaselineListing(captured, result, DateTimeOffset.UtcNow);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(configured, Volatile.Read(ref selection)) || !ReferenceEquals(profile, activeProfile))
                throw new InvalidOperationException("SHOP_CONFIGURATION_CHANGED：商店讀取選項或版型在本次解析中改變。");
            if (!result.Fields["personalShopListing"].Fresh(DateTimeOffset.UtcNow))
                throw new InvalidOperationException("SHOP_SOURCE_UNAVAILABLE：同輪商店基準值在交付前已過期。");
            return binding;
        }
    }

    // Pure validation of the result already returned by ReadProfile; no second
    // read, cache lookup, profile change or address exposure through Hub IPC.
    private static ShopActionSourceBinding AttachBaselineListing(ShopActionSourceBinding? captured, ReadResult result, DateTimeOffset now)
    {
        var field = result.Fields.GetValueOrDefault("personalShopListing");
        if (captured is null || result.Source is null || result.Source != captured.Source || field is null ||
            !field.Fresh(now) || field.SampledAt != captured.SampledAt || field.Value is not {} raw || raw.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("SHOP_SOURCE_UNAVAILABLE：本次未取得新鮮且完整閉合的商店來源；" + (field?.Detail ?? result.Detail));
        PersonalShopListing baseline;
        try { baseline = raw.Deserialize<PersonalShopListing>(HubProtocol.Json) ?? throw new JsonException("空白玩家商店值。"); }
        catch (JsonException error) { throw new InvalidOperationException("SHOP_BASELINE_INVALID：同輪商店資料格式無效。", error); }
        if (string.IsNullOrEmpty(captured.Source.SessionKey) || baseline.SourceSessionKey != captured.Source.SessionKey ||
            baseline.SampledAt != captured.SampledAt || baseline.OwnerIdRaw is null || baseline.OwnerIdRaw != captured.CurrentOwnerIdRaw)
            throw new InvalidOperationException("SHOP_BASELINE_MISMATCH：同輪商店基準值的來源、採樣時間或店主與操作來源不一致。");
        // Layout may be absent or empty: that is a valid pre-action baseline,
        // not permission to relabel it as an observed or complete new page.
        return captured with { BaselineListing = baseline };
    }
}
