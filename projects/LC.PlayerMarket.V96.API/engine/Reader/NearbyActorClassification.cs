namespace MemoryHub.Reader;

/// <summary>
/// Exact, case-sensitive loaded RTTI classification. A name that merely contains
/// Player/Monster/Item is not evidence of that actor layout or game semantics.
/// </summary>
internal static class NearbyActorClassification
{
    internal static readonly string[] Keys =
        ["nearbyMonsters", "nearbyNpcs", "nearbyPlayers", "nearbyUnknown"];

    internal static bool IsNearbyKey(string key) => key is
        "nearbyMonsters" or "nearbyNpcs" or "nearbyPlayers" or "nearbyUnknown";

    internal static string? KnownKind(string rawType) => rawType switch
    {
        ".?AVMyPlayer@lineage@@" => "MyPlayer",
        ".?AVPlayer@lineage@@" => "Player",
        ".?AVMonster@lineage@@" => "Monster",
        ".?AVInteractiveNPC@lineage@@" => "InteractiveNPC",
        _ => null
    };

    internal static string? FieldForKind(string kind) => kind switch
    {
        "Monster" => "nearbyMonsters",
        "InteractiveNPC" => "nearbyNpcs",
        "Player" => "nearbyPlayers",
        "Unknown" => "nearbyUnknown",
        _ => null // MyPlayer is never included in the surrounding player list.
    };

    internal static HashSet<ulong> AmbiguousUnknownIds(IEnumerable<ulong?> candidateIds,IReadOnlySet<ulong> knownIds)
    {
        var seen=new HashSet<ulong>();
        var ambiguous=new HashSet<ulong>();
        foreach(var candidate in candidateIds)
        {
            if(candidate is not {} id)continue;
            if(!seen.Add(id)||knownIds.Contains(id))ambiguous.Add(id);
        }
        return ambiguous;
    }

    internal const string GroundItemsUnsupportedDetail =
        "尚未驗證地面物品的精確 RTTI／集合讀取契約；此清單不是背包，也不把未確認當作附近沒有物品。";
}
