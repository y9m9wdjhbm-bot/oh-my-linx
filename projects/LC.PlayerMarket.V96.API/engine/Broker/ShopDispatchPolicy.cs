namespace MemoryHub.Service;

/// <summary>Current observations authorize one page; retained manual data only identifies the requested owner.</summary>
internal static class ShopDispatchPolicy
{
    internal static MarketOwner RequireOwner(ShopRuntimeContext context, ShopTargetSnapshot saved,
        MarketOwner requested, DateTimeOffset now, bool requireDispatch = true)
    {
        if (requireDispatch && !saved.CanDispatch)
            throw new ShopRequestFailure(ShopFailure.Global("TargetRefreshRequired", saved.DispatchBlockReason));
        if (context.Source.SessionKey != saved.SourceSessionKey || context.SelfId != saved.SelfId)
            throw new ShopRequestFailure(ShopFailure.Global("SourceMismatch", "目前來源或自身角色與手動名單不同；請重新刷新。"));
        var matches = context.Players.Where(p => p.Id == requested.Id).Take(2).ToArray();
        if (matches.Length == 0)
            throw Target("TargetNoLongerNearby", "店主已不在本次完整的周遭玩家集合；略過此店，不沿用保存座標送出。");
        if (matches.Length != 1)
            throw new ShopRequestFailure(ShopFailure.Global("ContextUnavailable", "目前店主 ID 不唯一；未送出查詢。"));
        var owner = matches[0];
        if (requested.Id == context.SelfId || string.IsNullOrWhiteSpace(requested.LocalIdentity)
            || string.IsNullOrWhiteSpace(owner.LocalIdentity) || owner.LocalIdentity != requested.LocalIdentity)
            throw Target("TargetChanged", "店主的本機物件識別已變更；未沿用同數字 ID 的其他玩家。");
        if (owner.Position is null || !ShopRuntimeContext.Fresh(owner.ObservedAt, now))
            throw Target("TargetObservationExpired", "送出前店主座標觀測已過期或無效；本頁未送出。");
        string state = ShopEligibilityPolicy.State(owner);
        if (state == ShopEligibilityPolicy.Unknown)
            throw Target("TargetShopStateUnknown", "目前尚未確認此店主的擺攤狀態；略過此店，未送出查詢。");
        if (owner.ShopStateSampledAt is not { } sampled || !ShopRuntimeContext.Fresh(sampled, now))
            throw Target("TargetObservationExpired", "送出前擺攤狀態觀測已過期或無效；本頁未送出。");
        if (state != ShopEligibilityPolicy.ShopActionObserved)
            throw Target("TargetNotShoppingObserved", "目前讀到此店主為非擺攤動作；略過此店，未送出查詢。");
        return owner;
    }

    private static ShopRequestFailure Target(string code, string detail) =>
        new(new(code, "Target", "BeforePublish", null, detail));

    internal static ShopCommandResponse Unverified(ShopCommandResponse response) =>
        new ShopFailure("ShopResponseUnverified", "Target", "ResponseVerification", null, response.Detail)
            .Attach(response with { Detail = response.Detail + " 此店未取得可核對的新商品頁，略過剩餘頁面；未將此結果判成空店或遊戲拒絕原因碼。" });
}

