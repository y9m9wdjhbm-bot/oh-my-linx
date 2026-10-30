using MemoryHub.Reader;
namespace MemoryHub.Service;

internal sealed partial record ShopRuntimeContext
{
    private bool FrozenTargetDispatch { get; init; }
    internal static ShopRuntimeContext FromFrozenDispatchResult(ReadResult result, SourceIdentity expected,
        ReadSelection selected, string expectedSession, DateTimeOffset now, ulong expectedSelf)
    {
        Need(!result.NoSourceRead && result.ReadFailureCode is null && result.ReadFailureNativeError is null
            && result.Source == expected && expected.SessionKey == expectedSession, "固定名單開頁的完整來源不符。");
        var local = new HubSnapshot { Source = result.Source, Fields = new(result.Fields) };
        var selfValue = Field(local, "selfId", now);
        Need(selfValue.TryGetUInt64(out ulong self) && self > 0 && self == expectedSelf, "固定名單開頁的角色已改變。");
        var position = Point(Field(local, "position", now)) ?? throw new InvalidOperationException("自身座標無效。");
        return new(expected, self, position, [], false, "定位點固定名單；沒有重新讀取周遭名單或店主擺攤狀態。")
        { Observations = local, ObservedSelection = selected, FrozenTargetDispatch = true };
    }
    private void RequireFreshFrozenDispatch(DateTimeOffset now)
    {
        Need(Observations is not null && ObservedSelection is not null && Observations.Source == Source,
            "固定名單開頁缺少同輪自身來源。");
        foreach (string key in new[] { "selfId", "position", "personalShopListing" })
            Need(ObservedSelection!.Enabled(key) && Observations!.Fields.TryGetValue(key, out var f) && f.Fresh(now)
                && f.Value is not null, "固定名單開頁必要資料已過期：" + key);
        var self = Field(Observations!, "selfId", now);
        Need(self.TryGetUInt64(out ulong id) && id == SelfId && id > 0, "固定名單開頁自身角色不符。");
    }
}
