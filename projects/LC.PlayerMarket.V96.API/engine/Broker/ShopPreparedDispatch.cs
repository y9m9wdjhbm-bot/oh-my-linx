using MemoryHub.Reader;

namespace MemoryHub.Service;

/// <summary>A closed read performed after preflight. A usable background snapshot never substitutes for this read.</summary>
internal sealed partial record ShopPreparedDispatch(ShopActionSourceBinding Binding, ShopRuntimeContext Context)
{
    private ReadResult Observations { get; init; } = null!;
    private static readonly string[] RequiredFields =
        ["selfId", "position", "nearbyPlayers", "playerShopStates", "personalShopListing"];

    internal static ShopPreparedDispatch Capture(Func<ShopDispatchSample> read, SourceIdentity expected,
        ReadSelection selected, string expectedSession, Func<DateTimeOffset> utcNow, FieldReading? map = null,
        ulong? selectedOwnerId = null)
    {
        foreach (string key in RequiredFields)
            if (!selected.Enabled(key)) throw Failure($"{key} 已停用；本頁未送出。");
        // Exactly one reader call. This cannot launch or retry the native helper.
        var sample = read();
        if (sample.Binding.Source != expected || sample.Observations.Source != expected || expected.SessionKey != expectedSession)
            throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "商店根與必要欄位未來自本次指定的同一個 LC 來源。"));
        var now = utcNow();
        if (sample.SelectedOwnerId != selectedOwnerId)
            throw Failure("指定店主的專用採樣範圍與本頁請求不符；未以部分資料冒充完整名單。");
        var context = selectedOwnerId is {} owner
            ? ShopRuntimeContext.FromSelectedDispatchResult(sample.Observations, expected, selected, expectedSession, now, owner, map)
            : ShopRuntimeContext.FromFreshResult(sample.Observations, expected, selected, expectedSession, now, map, requireShopStates: true);
        var prepared = new ShopPreparedDispatch(sample.Binding, context) { Observations = sample.Observations };
        prepared.RequireFresh(now);
        return prepared;
    }

    internal void RequireFresh(DateTimeOffset now)
    {
        if (FrozenTargetDispatch) RequireFreshFrozenBinding(now); else Context.RequireFreshForDispatch(now);
        var baseline = Binding.BaselineListing;
        var field = Observations?.Fields.GetValueOrDefault("personalShopListing");
        if (Observations is null || Observations.NoSourceRead || Observations.Source != DispatchSource
            || Binding.Source != DispatchSource || baseline is null || field is null || field.Value is null
            || !field.Fresh(now) || !ShopRuntimeContext.Fresh(Binding.SampledAt, now)
            || field.SampledAt != Binding.SampledAt || baseline.SampledAt != Binding.SampledAt
            || baseline.SourceSessionKey != Binding.Source.SessionKey || baseline.OwnerIdRaw != Binding.CurrentOwnerIdRaw)
            throw Failure($"商店根與同輪基準頁未完整閉合或已過期；sampledAt={Binding.SampledAt:O}，ageMs={(now-Binding.SampledAt).TotalMilliseconds:F1}；本頁未送出。");
    }

    internal object Evidence(DateTimeOffset now) => new
    {
        At = now, Source = DispatchSource.SessionKey, BindingSampledAt = Binding.SampledAt,
        BindingAgeMs = (now - Binding.SampledAt).TotalMilliseconds,
        SamplingScope = Binding.ClosedReadReuse?.Scope ?? (Binding.BindingOnly ? "BindingOnly" : "FreshFullShop"),
        ReadMetrics = Binding.ReadMetrics,
        IdentitySampledAt = Binding.ClosedReadReuse?.IdentitySampledAt,
        CurrentOwnerProbeAt = Binding.ClosedReadReuse?.OwnerProbeSampledAt,
        Fields = (FrozenTargetDispatch ? FrozenRequiredFields : RequiredFields).Select(key =>
        {
            var field = Observations.Fields.GetValueOrDefault(key);
            return new { Key = key, State = field?.State ?? "Missing", SampledAt = field?.SampledAt,
                AgeMs = (now - field?.SampledAt)?.TotalMilliseconds, Fresh = field?.Fresh(now) == true };
        }).ToArray()
    };

    private static ShopRequestFailure Failure(string detail) => new(ShopFailure.Global("ContextUnavailable", detail));
}

