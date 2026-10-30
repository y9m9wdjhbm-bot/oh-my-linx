using MemoryHub.Reader;
using System.Text.Json;
namespace MemoryHub.Service;

internal sealed partial record ShopPreparedDispatch
{
    private bool FrozenTargetDispatch { get; init; }
    private ulong FrozenSelfId { get; init; }
    private ReadSelection? FrozenSelection { get; init; }
    private static readonly string[] FrozenRequiredFields = ["selfId", "personalShopListing"];
    private SourceIdentity DispatchSource => Binding.Source;

    internal static ShopPreparedDispatch CaptureFrozen(Func<ShopDispatchSample> read, SourceIdentity expected,
        ReadSelection selected, ShopTargetSnapshot targets, MarketOwner owner, Func<DateTimeOffset> utcNow)
    {
        foreach (string key in FrozenRequiredFields)
            if (!selected.Enabled(key)) throw Failure("固定名單開頁的必要欄位已停用：" + key);
        if (targets.SourceSessionKey != expected.SessionKey || targets.SelfId == 0 || owner.Id == targets.SelfId)
            throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "固定名單與目前來源或自身角色不符。"));
        var sample = read();
        if (sample.Binding.Source != expected || sample.Observations.Source != expected || sample.SelectedOwnerId != owner.Id)
            throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "本頁必要採樣與固定名單的來源／請求識別不符。"));
        // A frozen dispatch has no current position or actor context. Do not
        // invent coordinates or give a saved point the timestamp of this read.
        var prepared = new ShopPreparedDispatch(sample.Binding, null!)
        { Observations = sample.Observations, FrozenTargetDispatch = true, FrozenSelfId = targets.SelfId, FrozenSelection = selected };
        prepared.RequireFresh(utcNow());
        return prepared;
    }

    private void RequireFreshFrozenBinding(DateTimeOffset now)
    {
        if (Observations is null || Observations.NoSourceRead || Observations.ReadFailureCode is not null
            || Observations.ReadFailureNativeError is not null || Observations.Source != Binding.Source
            || FrozenSelection is null)
            throw Failure("固定名單開頁沒有本輪完成的 binding-only 來源。");
        if (Binding.ClosedReadReuse is { } reuse)
        {
            if (!reuse.ValidFor(Binding, Observations, FrozenSelection, FrozenSelfId, now))
                throw Failure("閉合full-shop快取的原始來源／自身／時間／店主scalar證據不符；未送出。");
            return;
        }
        if (!Binding.BindingOnly) throw Failure("full-shop觀測缺少reader核發的重用證據，未冒充binding-only。");
        foreach (string key in FrozenRequiredFields)
            if (!FrozenSelection.Enabled(key) || !Observations.Fields.TryGetValue(key, out var field)
                || !field.Fresh(now) || field.Value is null) throw Failure("固定名單開頁必要資料已過期：" + key);
        var self = Observations.Fields["selfId"].Value!.Value;
        if (self.ValueKind != JsonValueKind.Number || !self.TryGetUInt64(out ulong id) || id == 0 || id != FrozenSelfId)
            throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "當輪自身角色不符固定名單。"));
        var baseline = Binding.BaselineListing;
        if (baseline is null || baseline.LayoutState != "BindingOnly" || baseline.Complete
            || baseline.Rows.Length != 0 || baseline.SlotRows.Length != 0 || baseline.RawBankSummary.Length != 0
            || baseline.PageMode is not null || baseline.BankState != "NotSampled")
            throw Failure("binding-only 被錯標為商品／空頁觀測，未送出。");
    }
}
