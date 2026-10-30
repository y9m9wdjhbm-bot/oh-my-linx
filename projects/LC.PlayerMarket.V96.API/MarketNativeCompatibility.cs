using MemoryHub;
using MemoryHub.Service;

namespace LcPlayerMarket;

internal sealed record MarketNativeReadiness(bool CanSend, string Code, string Message);

/// <summary>
/// The local reading profile does not establish native-action compatibility.
/// Uses the same native-package build contract as the local broker.
/// Never reads LC, launches a helper, or replaces the helper's own preflight.
/// </summary>
internal static class MarketNativeCompatibility
{
    internal const string SupportedSha256 = ShopNativePackage.SupportedSha;
    internal const uint SupportedTimestamp = ShopNativePackage.SupportedTimestamp;
    internal const uint SupportedImageSize = ShopNativePackage.SupportedImageSize;
    // Keep the optional argument for existing offline callers. Local capability
    // is checked from the reader and broker, never from a service version label.
    internal static MarketNativeReadiness Assess(SourceIdentity? source, string? serviceVersion = null)
    {
        if (source is null)
            return new(false, "SourceUnconfirmed", "等待本程式確認目前 LC 版本。");
        if (ShopNativePackage.SupportsBuild(source))
        {
            return new(true, "CompatibleBuild", $"目前 LC 與{ShopNativePackage.DisplayName}版本相符；開始查詢時會重新檢查，收到匹配的商品清單才記為完成。");
        }
        return new(false, "GameBuildUnsupported",
            "目前 LC 版本與商店查詢元件尚未相容，需要更新商店查詢元件後才能搜尋或開啟商店。周遭刷新、歷史商品與保存座標導航仍可使用。");
    }
}
