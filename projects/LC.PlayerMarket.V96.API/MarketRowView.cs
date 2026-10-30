using MemoryHub;

namespace LcPlayerMarket;

internal sealed class MarketRowView(MarketListing listing, PersonalShopRow row, LivePlayer? player, ShopPoint? self, bool live, bool manuallyRefreshed = false, bool currentSelfPosition = false, bool savedPositionNavigation = false)
{
    public MarketListing Listing { get; } = listing;
    public PersonalShopRow Row { get; } = row;
    public string Key => LocalStore.Key(Listing) + "|" + Row.RowIndex;
    public string Direction => Listing.ViewerDirection == ShopPageModes.ViewerBuy ? "店主出售（我買入）" : "店主收購（我賣出）";
    public string Item => SortItemName;
    public int? NumericItemKind => Row.ItemKind switch { "Blessed" => 0, "Normal" => 1, "Cursed" => 2, _ => null };
    public string ItemKind => MarketItemStatePresentation.Display(Row);
    public string SortItemName { get; } = MarketOrdering.ItemNameKey(row.Name ?? "未取得名稱", row.Quantity);
    public ulong? NumericQuantity => Row.Quantity;
    public ShopPoint? SortPosition => savedPositionNavigation ? Listing.Owner.Position : live ? player?.Position : Listing.Owner.Position;
    public ulong? NumericPrice => Row.Price;
    public string Price => Row.Price?.ToString("N0") ?? Row.PriceRaw ?? "未知";
    public string Unit => Row.UnitRaw ?? "未知";
    public string Quantity => Row.Quantity?.ToString("N0") ?? "未知";
    public string Owner => Listing.Owner.Name.Length > 0 ? Listing.Owner.Name : $"ID {Listing.Owner.Id}";
    public string Position => SortPosition is { } p ? $"{p.X},{p.Y}" : "未知";
    public long? NumericDistance => savedPositionNavigation && Listing.Owner.Position is { } saved && self is not null ? self.GridDistance(saved)
        : live && player?.Position is { } p && self is not null ? self.GridDistance(p) : null;
    public string Distance => NumericDistance is { } d ? $"{d} 格{(savedPositionNavigation ? "（至店主保存位置）" : manuallyRefreshed ? currentSelfPosition ? "（至店主保存位置）" : "（刷新時）" : "")}" : Listing.DistanceAtRequest is { } old ? $"{old} 格（取得時）" : "未知";
    public DateTimeOffset CapturedAt => Listing.CapturedAt;
    public string Acquired => CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public bool Live => live;
    public string Status => !Listing.OwnerAndResponseMatched ? "回覆歸屬待確認" : live
        ? manuallyRefreshed ? "手動周遭快照／保存至下次刷新" : "店主目前資料有效" : savedPositionNavigation ? "保存紀錄／可依座標導航" : "歷史／尚未對應手動周遭快照";
}
