using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Capability/configuration only. Manual observations have no age TTL.</summary>
internal static class PlayerShopStatePresentation
{
    internal static bool ServiceReady(HubSnapshot? snapshot, out string reason)
    {
        if (snapshot is null)
        { reason = "等待本程式初始化 LC 讀取，完成後可手動刷新周遭。"; return false; }
        if (!snapshot.Fields.TryGetValue("playerShopStates", out var field))
        { reason = "本程式尚未提供玩家擺攤狀態讀取能力；請檢查完整新版程式與 LOG。"; return false; }
        if (field.State == "Disabled")
        { reason = "本程式的玩家擺攤狀態讀取已停用；請檢查本機讀取設定後再手動刷新周遭。"; return false; }
        if (field.State == "Unsupported")
        { reason = "本程式尚不支援目前 LC 的玩家擺攤狀態讀取；請檢查新版讀取設定與重新定位結果。" + field.Detail; return false; }
        // In shop-only mode, the field deliberately remains Unknown until the
        // user explicitly refreshes. Requiring Fresh here would block that refresh.
        if (field.State is not ("Observed" or "Unknown" or "Stale"))
        { reason = "本程式的玩家擺攤狀態讀取尚未就緒，請等待讀取初始化後再手動刷新。"; return false; }
        reason = ""; return true;
    }

    internal static string Counts(ShopEligibilityCounts counts) =>
        $"確認擺攤 {counts.ShopActionObservedCount} 位／非擺攤動作 {counts.KnownOtherActionObservedCount} 位／未知 {counts.UnknownShopStateCount} 位";
}
