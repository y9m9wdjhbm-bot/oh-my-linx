using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Coordinate guidance only: no actor lookup, identity lease, projection or game input.</summary>
internal static class SavedPositionNavigation
{
    public static bool TryCreate(MarketListing listing, ShopPoint self, string? currentMap,
        out NavigationView? view, out string reason)
    {
        view = null;
        reason = "商店紀錄沒有有效的保存座標";
        if (listing.Owner?.Position is not { } target || !Valid(target) || !Valid(self)) return false;
        string? savedMap = Map(listing.Owner.MapName), map = Map(currentMap);
        // These are HUD region names, not verified map/instance IDs. They may
        // differ across a continuous coordinate plane and must not gate guidance.
        string mapNote = savedMap is null || map is null ? "地圖未確認" : string.Equals(savedMap, map, StringComparison.Ordinal)
            ? "地區名稱相同；地圖未確認" : "地區名稱不同；地圖未確認";
        long dx = (long)target.X - self.X, dy = (long)target.Y - self.Y;
        string name = string.IsNullOrWhiteSpace(listing.Owner.Name) ? "商店保存位置" : listing.Owner.Name;
        // Coordinate40: P=(X+Y-65535)*48, Q=(X-Y)*24. Screen Y runs
        // downward, so the directional delta is (48*(dx+dy),24*(dy-dx)).
        // Only the ratio 2:1 matters here; no camera origin or pixel point is invented.
        // Evidence: verification/座標方向依據-v2.md. This is guidance, not pixel projection.
        view = new(RectangleF.Empty, new PointF(2 * (dx + dy), dy - dx), self.GridDistance(target), name, false,
            $"保存座標 {target.X},{target.Y}／等角方向推估／ΔX={dx}，ΔY={dy}／{mapNote}（目前：{map ?? "未知"}；保存：{savedMap ?? "未知"}）")
        { CoordinateOnly = true, DistanceToSavedPosition = true, SavedPosition = target, CoordinateNote = mapNote };
        reason = view.Evidence;
        return true;
    }

    private static bool Valid(ShopPoint p) => p.X is > 0 and <= 1000000 && p.Y is > 0 and <= 1000000;
    private static string? Map(string? name) => string.IsNullOrWhiteSpace(name) || name.Trim() is "Unknown" or "未知" ? null : name.Trim();
}
