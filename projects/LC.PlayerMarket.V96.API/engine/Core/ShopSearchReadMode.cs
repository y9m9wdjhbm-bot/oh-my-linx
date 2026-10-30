namespace MemoryHub;

/// <summary>Exact union of ShopBroker.EnsureFields and screen-search CaptureFresh dependencies.</summary>
public static class ShopSearchReadMode
{
    public static IReadOnlyList<string> RequiredFields {get;}=Array.AsReadOnly(new[]
    {"selfId","position","mapName","nearbyPlayers","actorViewport","personalShopListing","playerShopStates"});
    public static IReadOnlyList<string> ManualFields {get;}=Array.AsReadOnly(new[]{"nearbyPlayers","actorViewport","playerShopStates"});
    public const string Label="商店搜尋專用";
    public const string Description="已啟用玩家擺攤狀態讀取。定時讀取自身 ID、自身座標、地圖名稱與商店頁面；周遭名單、畫面位置與擺攤狀態只在商店程式按「手動刷新」時建立。搜尋只嘗試已確認擺攤的玩家；未知狀態另列待確認。關閉本模式還原原本勾選；間隔與 LOG 不變。";
    public const string ManualRefreshNotice="等待商店手動刷新；專用模式不定時讀取本欄，已保存的目標請查看商店程式。";
    public const string CustomSelectionNotice="商店搜尋專用模式已開啟；請先關閉此模式，再自訂欄位或使用全部開啟／關閉。";
    public const string MonsterLockNotice="商店搜尋專用模式已停用怪物研究鎖定；關閉本模式後可自行使用。";
}

public sealed partial class HubRuntime
{
    // This immutable object is one persistence snapshot. SaveSettings holds the
    // log lock, so it must never acquire selectionGate in the reverse order.
    private sealed record SelectionSettingsState(bool ShopSearchOnly,ReadSelection Selection,ReadSelection? PreviousSelection);
    private SelectionSettingsState selectionSettings=new(false,new ReadSelection(),null);
    public bool ShopSearchOnlyEnabled=>Volatile.Read(ref selectionSettings).ShopSearchOnly;

    public void SetShopSearchOnly(bool enabled)
    {
        lock(selectionGate)
        {
            var previous=Volatile.Read(ref selectionSettings);
            if(previous.ShopSearchOnly==enabled)return;
            if(enabled)RequireSlicedShopSource();
            var next=enabled
                ?new SelectionSettingsState(true,new ReadSelection(ShopSearchReadMode.RequiredFields),selection)
                :new SelectionSettingsState(false,previous.PreviousSelection??throw new InvalidOperationException("缺少專用模式啟用前的欄位設定，未猜測還原內容。"),null);
            // Even an identical effective selection is a mode transition: clear
            // prior values and invalidate any in-flight pre-transition result.
            ApplySelection(next.Selection,next,force:true);
        }
    }
    private void RequireCustomSelection()
    {
        if(Volatile.Read(ref selectionSettings).ShopSearchOnly)throw new InvalidOperationException(ShopSearchReadMode.CustomSelectionNotice);
    }
    private void RequireSlicedShopSource()
    {
        if(source is not ISlicedMemorySource)throw new NotSupportedException("此讀取來源不支援分欄採樣，無法啟用商店搜尋專用模式；未改成完整讀取。");
    }
    private Dictionary<string,FieldReading> MarkManualShopFields(Dictionary<string,FieldReading> fields)
    {
        if(ShopSearchOnlyEnabled)
            foreach(string key in ShopSearchReadMode.ManualFields)fields[key]=FieldReading.Unknown(ShopSearchReadMode.ManualRefreshNotice);
        return fields;
    }
    private void RestoreSelectionSettings(HubSettings settings)
    {
        // V30 removes this retired field without resetting the user's remaining
        // selection, log preference or timing settings from an older release.
        static IEnumerable<string>? Current(IEnumerable<string>? keys)=>keys?.Where(k=>k!="learnedSkills");
        var saved=new ReadSelection(Current(settings.EnabledFields));
        // A mode saved without a prior selection cannot promise restoration.
        // Keep the explicitly saved field selection as ordinary custom mode.
        var restored=settings.ShopSearchOnly&&settings.PreviousEnabledFields is not null
            ?new SelectionSettingsState(true,new ReadSelection(ShopSearchReadMode.RequiredFields),new ReadSelection(Current(settings.PreviousEnabledFields)))
            :new SelectionSettingsState(false,saved,null);
        if(restored.ShopSearchOnly)RequireSlicedShopSource();
        selection=restored.Selection;selectionSettings=restored;
    }
}
