namespace MemoryHub.Reader;

/// <summary>Only source-closed, exact action types with existing live research have known shop semantics.</summary>
internal static class PlayerShopStatePolicy
{
    internal static string ClassifyExactAction(string? exactType)=>exactType switch
    {
        ".?AVShopAction@lineage@@"=>"ShopActionObserved",
        ".?AVBreathAction@lineage@@" or ".?AVMovingAction@lineage@@"=>"KnownOtherActionObserved",
        _=>"Unknown"
    };
}
