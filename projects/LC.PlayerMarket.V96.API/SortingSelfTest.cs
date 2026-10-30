using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Offline user cases exercise the production multi-column comparer; no Form, IPC, file write or LC access.</summary>
public static class SortingSelfTest
{
    public static OfflineCheckReport Run()
    {
        var checks = new List<OfflineCheck>();
        var acquired = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.FromHours(8));
        MarketRowView Row(string key, string name = "清爽橙色藥水", ulong? price = 1, ulong? quantity = 1,
            string owner = "測試店主", ShopPoint? point = null, DateTimeOffset? at = null, bool live = false,
            string direction = ShopPageModes.ViewerBuy)
        {
            var item = new PersonalShopRow(0, "Observed", true, name, price?.ToString(), price, "每個", quantity,
                quantity.HasValue ? "Matched" : "Unknown", "離線排序資料");
            var listing = new MarketListing { Revision = key, SourceSessionKey = "offline-sort", Owner = new() { Id = 22,
                LocalIdentity = "offline-owner", Name = owner, Position = point }, CapturedAt = at ?? acquired,
                ViewerDirection = direction, OwnerAndResponseMatched = true, Complete = true, Rows = [item] };
            return new(listing, item, live ? new(22, "offline-owner", owner, point, acquired) : null, new(100, 100), live);
        }
        static void Need(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
        void Check(string name, Action test)
        { try { test(); checks.Add(new(name, true, "通過")); } catch (Exception ex) { checks.Add(new(name, false, ex.Message)); } }
        static string[] Keys(IEnumerable<MarketRowView> rows) => rows.Select(r => r.Listing.Revision).ToArray();

        Check("name_group_ignores_only_confirmed_inventory_suffix_then_numeric_price", () =>
        {
            var rows = new[] { Row("expensive", "清爽橙色藥水(2)", 100, 2), Row("cheap", "清爽橙色藥水(1,000)", 9, 1000) };
            var sorted = MarketOrdering.Apply(rows, [new(MarketSortField.Item, false), new(MarketSortField.Price, false)]);
            Need(Keys(sorted).SequenceEqual(new[] { "cheap", "expensive" }), "括號數量不應在名稱分群中壓過次要價格排序");
            Need(sorted[0].Item == "清爽橙色藥水" && sorted[0].NumericQuantity == 1000,
                "商品名稱應去除已確認的數量後綴，數量保留於獨立欄位");
            Need(sorted[0].Row.Name == "清爽橙色藥水(1,000)", "整理顯示名稱不得修改服務原始資料");
        });
        Check("suffix_preserves_real_name_and_unverified_quantity", () =>
        {
            foreach (string name in new[] { "魔法卷軸(體魄)", "測試(7)", "測試(1,00)", "測試(1)後綴", "測試（1)", "(1)" })
                Need(MarketOrdering.ItemNameKey(name, 1) == name, "不是匹配庫存後綴卻被改寫：" + name);
            Need(MarketOrdering.ItemNameKey("測試(1)", null) == "測試(1)", "未知數量不能從名稱推定後綴");
            Need(MarketOrdering.ItemNameKey("測試(1)", 2) == "測試(1)", "與真正數量不符的後綴必須保留");
            Need(MarketOrdering.ItemNameKey("卷軸(祝福)（ 1,000 ）  ", 1000) == "卷軸(祝福)", "只應去掉末尾匹配數量，保留道具名括號");
        });
        Check("three_levels_mixed_direction", () =>
        {
            var rows = new[] { Row("low", price: 9, quantity: 5, owner: "A"), Row("b", price: 10, quantity: 2, owner: "A"),
                Row("a", price: 10, quantity: 1, owner: "A"), Row("other", price: 999, quantity: 1, owner: "B") };
            var sorted = MarketOrdering.Apply(rows, [new(MarketSortField.Owner, false), new(MarketSortField.Price, true), new(MarketSortField.Quantity, false)]);
            Need(Keys(sorted).SequenceEqual(new[] { "a", "b", "low", "other" }), "三層排序方向或優先順序錯誤");
        });
        Check("unknown_price_and_quantity_last_both_directions_without_overflow", () =>
        {
            var rows = new[] { Row("unknown", price: null, quantity: null), Row("zero", price: 0, quantity: 0), Row("max", price: ulong.MaxValue, quantity: ulong.MaxValue) };
            foreach (var field in new[] { MarketSortField.Price, MarketSortField.Quantity })
            {
                Need(Keys(MarketOrdering.Apply(rows, [new(field, false)])).SequenceEqual(new[] { "zero", "max", "unknown" }), "升冪未知位置／最大數值錯誤");
                Need(Keys(MarketOrdering.Apply(rows, [new(field, true)])).SequenceEqual(new[] { "max", "zero", "unknown" }), "降冪未知位置／最大數值溢位");
            }
        });
        Check("unknown_secondary_value_stays_after_known_within_name", () =>
        {
            var rows = new[] { Row("unknown", price: null), Row("known", price: 9) };
            Need(Keys(MarketOrdering.Apply(rows, [new(MarketSortField.Item, false), new(MarketSortField.Price, true)]))
                .SequenceEqual(new[] { "known", "unknown" }), "次排序的未知數值不得因降冪提前");
        });
        Check("coordinates_compare_x_then_y_numerically_unknown_last", () =>
        {
            var rows = new[] { Row("unknown"), Row("ten", point: new(10, 0)), Row("two-high", point: new(2, 20)), Row("two-low", point: new(2, 9)) };
            Need(Keys(MarketOrdering.Apply(rows, [new(MarketSortField.Position, false)]))
                .SequenceEqual(new[] { "two-low", "two-high", "ten", "unknown" }), "座標不可用字串比較");
            Need(Keys(MarketOrdering.Apply(rows, [new(MarketSortField.Position, true)]))
                .SequenceEqual(new[] { "ten", "two-high", "two-low", "unknown" }), "座標降冪的未知值必須最後");
        });
        Check("distance_uses_live_numeric_value_not_historical_label", () =>
        {
            var rows = new[] { Row("history", point: new(101, 100)), Row("far", point: new(112, 100), live: true), Row("near", point: new(102, 100), live: true) };
            Need(Keys(MarketOrdering.Apply(rows, [new(MarketSortField.Distance, false)])).SequenceEqual(new[] { "near", "far", "history" }), "距離混入舊座標或字串順序");
            Need(Keys(MarketOrdering.Apply(rows, [new(MarketSortField.Distance, true)])).SequenceEqual(new[] { "far", "near", "history" }), "降冪距離未知應最後");
        });
        Check("stable_ties_use_original_acquired_time_then_ordinal_key", () =>
        {
            var rows = new[] { Row("z", at: acquired), Row("old", at: acquired.AddDays(-1)), Row("a", at: acquired) };
            var terms = new[] { new MarketSortTerm(MarketSortField.Item, false), new MarketSortTerm(MarketSortField.Price, false) };
            string[] expected = ["a", "z", "old"];
            Need(Keys(MarketOrdering.Apply(rows, terms)).SequenceEqual(expected)
                && Keys(MarketOrdering.Apply(rows.Reverse(), terms)).SequenceEqual(expected), "重讀相同資料不應依集合輸入順序跳動");
        });
        Check("acquired_time_direction_and_no_key_fallback_override", () =>
        {
            var rows = new[] { Row("later", at: acquired.AddSeconds(1)), Row("earlier", at: acquired) };
            Need(Keys(MarketOrdering.Apply(rows, [new(MarketSortField.CapturedAt, false)])).SequenceEqual(new[] { "earlier", "later" }), "取得時間升冪被預設tie降冪覆蓋");
            Need(Keys(MarketOrdering.Apply(rows, [new(MarketSortField.CapturedAt, true)])).SequenceEqual(new[] { "later", "earlier" }), "取得時間降冪錯誤");
        });
        Check("header_primary_change_preserves_secondary_and_third", () =>
        {
            MarketSortTerm[] before = [new(MarketSortField.Item, false), new(MarketSortField.Price, true), new(MarketSortField.Quantity, false)];
            var replaced = MarketOrdering.PromotePrimary(before, MarketSortField.Owner);
            Need(replaced.SequenceEqual(new MarketSortTerm[] { new(MarketSortField.Owner, false), before[1], before[2] }), "新主排序損失次／三排序");
            var swapped = MarketOrdering.PromotePrimary(before, MarketSortField.Price);
            Need(swapped.SequenceEqual(new[] { before[1], before[0], before[2] }), "已存在的次排序應與主排序互換，保留其他條件");
            var toggled = MarketOrdering.PromotePrimary(before, MarketSortField.Item);
            Need(toggled.SequenceEqual(new MarketSortTerm[] { before[0] with { Descending = true }, before[1], before[2] }), "表頭切換升降不得改其他排序");
        });
        Check("empty_duplicate_and_extra_sort_levels_are_bounded", () =>
        {
            var terms = MarketOrdering.Normalize([new(MarketSortField.None, false), new(MarketSortField.Price, true), new(MarketSortField.Price, false),
                new(MarketSortField.Item, false), new(MarketSortField.Owner, false), new(MarketSortField.Quantity, false)]);
            Need(terms.Select(t => t.Field).SequenceEqual(new[] { MarketSortField.Price, MarketSortField.Item, MarketSortField.Owner }), "排序層數或去重不符三層限制");
        });
        Check("full_filtered_result_sort_does_not_apply_display_cap", () =>
        {
            var rows = Enumerable.Range(0, 10007).Select(i => Row(i.ToString("D5"), price: (ulong)(10007 - i))).ToArray();
            var sorted = MarketOrdering.Apply(rows, [new(MarketSortField.Price, false)]);
            Need(sorted.Length == 10007 && sorted[0].NumericPrice == 1 && sorted[^1].NumericPrice == 10007,
                "排序結果不可截斷成表格10000筆顯示上限，Excel必須取得完整資料");
        });
        return new("42.0.0", checks.All(c => c.Passed), checks.Count(c => c.Passed), checks.Count(c => !c.Passed), false, false, false, checks.ToArray());
    }
}

