using System.Collections.ObjectModel;

namespace MemoryHub;

public sealed record BackgroundReadGroup(string Key,string Label,string FieldsLabel);

public static class BackgroundReadGroups
{
    public static IReadOnlyList<BackgroundReadGroup> All {get;}=Array.AsReadOnly(new BackgroundReadGroup[]
    {
        new("hud","角色資訊","等級、經驗值、負重、飽食度、防禦、魔防、正義值、地圖名稱"),
        new("buff","BUFF","BUFF ID 清單"),
        new("hotbar","快捷欄","快捷欄頁面、焦點格、各格資料、ATS"),
        new("inventory","背包與裝備候選","背包物品、顯示穿戴標記、顯示武器標記"),
        new("actors","角色與周遭物件","角色名稱、本機物件、名稱、座標、周遭五類清單與玩家擺攤狀態（共用一次集合採樣）"),
        new("safeZone","地圖與安全區","安全區狀態、目前地磚、附近地磚候選"),
        new("party","隊伍","隊伍成員"),
        new("viewport","同畫面與游標","物件矩形、同畫面資訊、遊戲游標位置、游標輸入狀態候選"),
        new("personalShop","玩家商店","目前頁面文字、原始店主識別；數量及頁面方向未驗證時保持未知"),
        new("unsupported","未支援欄位","地圖編號、游標地圖座標、保存點擊座標")
    });
}

public sealed partial class HubRuntime
{
    private const int DefaultBackgroundIntervalMilliseconds=1000;
    private readonly object backgroundIntervalGate=new();
    // A published dictionary is never modified. Readers can take one coherent
    // configuration without holding a lock while saving or scheduling work.
    private Dictionary<string,int> backgroundIntervals=NormalizeBackgroundIntervals(null);

    public IReadOnlyDictionary<string,int> BackgroundIntervalMilliseconds=>
        new ReadOnlyDictionary<string,int>(new Dictionary<string,int>(Volatile.Read(ref backgroundIntervals),StringComparer.Ordinal));

    public int GetBackgroundIntervalMilliseconds(string key)
    {
        ValidateBackgroundGroup(key);
        return Volatile.Read(ref backgroundIntervals)[key];
    }

    public void SetBackgroundIntervalMilliseconds(string key,int value)
    {
        ValidateBackgroundGroup(key);int nextValue=Math.Clamp(value,100,5000),previousValue;
        lock(backgroundIntervalGate)
        {
            var previous=Volatile.Read(ref backgroundIntervals);previousValue=previous[key];
            if(previousValue==nextValue)return;
            var next=new Dictionary<string,int>(previous,StringComparer.Ordinal){[key]=nextValue};
            Volatile.Write(ref backgroundIntervals,next);
        }
        // Do not take the LOG/settings lock while holding the update lock.
        // Interval changes do not change source selection or clear its samples.
        WakeScheduler();
        Log("BACKGROUND_INTERVAL","背景欄位讀取間隔已更新",new{Key=key,PreviousMilliseconds=previousValue,Milliseconds=nextValue,
            FreshnessLimitMilliseconds=HubProtocol.FreshMilliseconds,AtOrBeyondFreshnessLimit=nextValue>=HubProtocol.FreshMilliseconds,
            FreshnessNotice=HubProtocol.IntervalFreshnessNotice(nextValue)});
        SaveSettings();
    }

    private static void ValidateBackgroundGroup(string key)
    {
        if(!BackgroundReadGroups.All.Any(group=>string.Equals(group.Key,key,StringComparison.Ordinal)))
            throw new ArgumentOutOfRangeException(nameof(key),key,"未知的背景欄位群組");
    }

    private static Dictionary<string,int> NormalizeBackgroundIntervals(IReadOnlyDictionary<string,int>? saved)
    {
        var values=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(var group in BackgroundReadGroups.All)
            values[group.Key]=saved is not null&&saved.TryGetValue(group.Key,out int value)
                ?Math.Clamp(value,100,5000):DefaultBackgroundIntervalMilliseconds;
        return values;
    }
}
