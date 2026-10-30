namespace MemoryHub;

/// <summary>Policy over a saved manual observation; never reads memory or adds an age TTL.</summary>
public static class ShopEligibilityPolicy
{
    public const string ShopActionObserved = "ShopActionObserved";
    public const string KnownOtherActionObserved = "KnownOtherActionObserved";
    public const string Unknown = "Unknown";
    public const string ActionEvidence = "ExactPlayerId_ComponentOwner_DirectAction_PrimaryRtti_ActionBaseHierarchy_ABClosed";

    public static string State(MarketOwner owner)
    {
        if (owner.ShopState is not (ShopActionObserved or KnownOtherActionObserved)
            || owner.ActionType is not { Length: > 0 and <= 256 }
            || owner.ShopStateSampledAt is null || owner.ShopStateSampledAt == default(DateTimeOffset)
            || owner.ShopStateEvidence != ActionEvidence) return Unknown;
        if (owner.ShopState == ShopActionObserved && owner.ActionType != ".?AVShopAction@lineage@@") return Unknown;
        if (owner.ShopState == KnownOtherActionObserved
            && owner.ActionType is not (".?AVBreathAction@lineage@@" or ".?AVMovingAction@lineage@@")) return Unknown;
        return owner.ShopState;
    }

    public static bool CanScan(MarketOwner owner) => State(owner) == ShopActionObserved;

    public static ShopEligibilityCounts Count(IEnumerable<MarketOwner> owners)
    {
        int shopping = 0, other = 0, unknown = 0;
        foreach (var owner in owners)
            switch (State(owner))
            {
                case ShopActionObserved: shopping++; break;
                case KnownOtherActionObserved: other++; break;
                default: unknown++; break;
            }
        return new(shopping, other, unknown);
    }

    public static string RejectionCode(MarketOwner owner) => State(owner) == KnownOtherActionObserved
        ? "TargetNotShoppingObserved" : "TargetShopStateUnknown";

    public static string RejectionDetail(MarketOwner owner) => State(owner) == KnownOtherActionObserved
        ? "上次手動刷新時觀察到非擺攤動作；已略過此玩家，不送出商店請求。玩家開始擺攤後，請手動刷新。"
        : "上次手動刷新尚未確認此玩家的擺攤狀態；未送出商店請求。請啟用擺攤狀態讀取並手動刷新。";
}

public sealed record ShopEligibilityCounts(int ShopActionObservedCount, int KnownOtherActionObservedCount, int UnknownShopStateCount);
