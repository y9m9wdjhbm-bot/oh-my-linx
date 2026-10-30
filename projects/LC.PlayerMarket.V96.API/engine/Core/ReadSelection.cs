namespace MemoryHub;

public sealed record FieldDefinition(string Key,string Label,string Group);

public static class FieldCatalog
{
    public static IReadOnlyList<FieldDefinition> All {get;} = Array.AsReadOnly(new FieldDefinition[]
    {
        new("hp","HP","角色"), new("mp","MP","角色"),
        new("selfId","自身角色 ID","角色"), new("characterName","角色名稱","角色"),
        new("position","自身座標","角色"), new("buffIds","BUFF ID 清單","角色"),
        new("level","等級","資訊"), new("experiencePercent","經驗值（%）","資訊"),
        new("weightPercent","負重（%）","資訊"), new("foodPercent","飽食度（%）","資訊"),
        new("armorClass","防禦（AC）","資訊"), new("magicResistancePercent","魔防（%）","資訊"),
        new("alignment","正義值","資訊"), new("mapName","地圖名稱","地圖"),
        new("safeZone","安全區","地圖"),
        new("mapTile","目前地磚候選","地圖"), new("mapNeighborhood","附近地磚候選","地圖"),
        new("hotbarPage","快捷欄目前頁面","快捷欄"), new("hotbarFocus","快捷欄焦點格","快捷欄"),
        new("hotbarSlots","快捷欄各格資料","快捷欄"), new("atsEnabled","ATS 狀態","快捷欄"),
        new("weapon","武器候選（顯示標記）","背包"), new("party","隊伍名單候選／本機對應","目標"),
        new("actors","本機角色物件與型別","目標"), new("actorNames","物件名稱","目標"),
        new("actorPositions","物件座標","目標"), new("actorViewport","物件矩形與同畫面資訊","目標"),
        new("nearbyMonsters","周遭怪物清單","周遭清單"), new("nearbyNpcs","周遭 NPC 清單","周遭清單"),
        new("nearbyPlayers","周遭玩家清單","周遭清單"),
        new("nearbyUnknown","周遭未知物件清單","周遭清單"),
        new("cursorPosition","遊戲游標位置","游標"), new("cursorInputState","游標輸入狀態候選","游標"),
        new("inventory","背包物品候選","背包"), new("equippedItems","穿戴裝備候選（顯示標記）","背包"),
        new("personalShopListing","玩家商店目前頁面","商店"),
        new("playerShopStates","玩家擺攤狀態","商店")
    });
    public static bool Contains(string key)=>All.Any(f=>f.Key==key);
}

/// <summary>Immutable per-cycle field selection. Shared identity checks remain dependencies.</summary>
public sealed class ReadSelection
{
    private readonly HashSet<string> enabled;
    public ReadSelection(IEnumerable<string>? enabledKeys=null)
    {
        enabled=new(enabledKeys??FieldCatalog.All.Select(f=>f.Key),StringComparer.Ordinal);
        if(enabled.Any(key=>!FieldCatalog.Contains(key)))throw new ArgumentException("未知的資料欄位");
    }
    public bool Enabled(string key)=>enabled.Contains(key);
    public bool Any=>enabled.Count>0;
    public IReadOnlyList<string> EnabledKeys=>Array.AsReadOnly(enabled.Order(StringComparer.Ordinal).ToArray());
    public ReadSelection With(string key,bool value)
    {
        if(!FieldCatalog.Contains(key))throw new ArgumentException("未知的資料欄位",nameof(key));
        var next=new HashSet<string>(enabled,StringComparer.Ordinal);
        if(value)next.Add(key);else next.Remove(key);
        return new(next);
    }
    public Dictionary<string,FieldReading> Empty(string detail)=>FieldCatalog.All.ToDictionary(f=>f.Key,
        f=>Enabled(f.Key)?FieldReading.Unknown(detail):FieldReading.Disabled());
    public Dictionary<string,FieldReading> Filter(IReadOnlyDictionary<string,FieldReading> fields)
    {
        var result=Empty("等待本欄位的新採樣");
        foreach(var field in fields)if(Enabled(field.Key))result[field.Key]=field.Value;
        return result;
    }
}

public interface ISelectableMemorySource : IMemorySource
{
    void Configure(ReadSelection selection);
}
