using System.Globalization;
using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Offline rule tests only; verified-unidentified cases below are synthetic, not current-game evidence.</summary>
internal static class MarketItemStateSelfTest
{
    internal static MarketItemStateCheckReport Run()
    {
        var checks = new List<MarketItemStateCheck>();
        static void Need(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
        void Check(string name, Action body)
        { try { body(); checks.Add(new(name, true, "通過")); } catch (Exception ex) { checks.Add(new(name, false, ex.Message)); } }
        PersonalShopRow Item(byte? flag = 1, int? kind = 1, string? name = "+4 抗魔法斗篷", string? unit = "金幣", ulong? price = 10,
            string kindState = "CurrentItemEntityUserCorroborated", string identification = "Unknown", string identificationEvidence = "Unverified", ulong? quantity = 1) =>
            new(0, "Observed", true, name, price?.ToString(CultureInfo.InvariantCulture), price, unit, quantity, "Observed", "離線合成資料")
            { ItemKindDetailFlag = flag, ItemKindRaw = kind, ItemKind = "Unknown", ItemKindState = kindState,
                ItemIdentificationState = identification, ItemIdentificationEvidence = identificationEvidence };
        MarketRowView View(PersonalShopRow row, string direction = ShopPageModes.ViewerBuy) => new(new MarketListing {
            Revision = "same-revision", SourceSessionKey = "offline-state", ViewerDirection = direction,
            CapturedAt = DateTimeOffset.UnixEpoch, Owner = new() { Id = 1, Name = "離線店主" }, Rows = [row] }, row, null, null, false);

        Check("flag1_three_states_require_current_corroboration_marker", () =>
        {
            string[] kinds = ["Blessed", "Normal", "Cursed"], labels = ["受祝福", "一般", "受詛咒"];
            for (int i = 0; i < 3; i++)
            {
                var row = Item(1, i);
                Need(MarketItemStatePresentation.EffectiveKind(row) == kinds[i], "三態原始值對應錯");
                Need(MarketItemStatePresentation.Display(row) == labels[i] && MarketItemStatePresentation.EvidenceTooltip(row).Contains("鑑定證據=Unverified"), "把類型當鑑定狀態");
                Need(MarketItemStatePresentation.EffectiveKindReason(row) == "CurrentItemEntityUserCorroborated", "缺證據理由");
            }
        });
        Check("legacy_and_unverified_labels_do_not_authorize_mapping", () =>
        {
            foreach (string state in new[] { "Unknown", "MemoryValueCandidate", "Verified", "currentitementityusercorroborated" })
            foreach (string legacy in new[] { "Normal", "Blessed", "Cursed" })
                Need(MarketItemStatePresentation.EffectiveKind(Item(kindState: state) with { ItemKind = legacy }) == "Unknown", "舊label繞過新evidence gate");
        });
        Check("flag0_never_means_unidentified_or_normal_by_itself", () =>
        {
            foreach (int raw in new[] { 0, 1, 2 })
            {
                var row = Item(0, raw);
                Need(MarketItemStatePresentation.EffectiveKind(row) == "Unknown", "flag0猜一般/未鑑定");
                Need(MarketItemStatePresentation.EvidenceTooltip(row).Contains("旗標=0／原始類型=" + raw), "未知raw未保留");
            }
        });
        Check("verified_unidentified_user_rule_only_with_supported_complete_raw", () =>
        {
            foreach (byte flag in new byte[] { 0, 1 }) foreach (int raw in new[] { 0, 1, 2 })
            {
                var row = Item(flag, raw, kindState: "Unknown", identification: "Unidentified", identificationEvidence: "Verified");
                Need(MarketItemStatePresentation.EffectiveKind(row) == "Normal", "明確已證未鑑定未套用user rule");
                Need(MarketItemStatePresentation.EffectiveKindReason(row) == "UserRuleUnidentifiedAsNormal", "user rule不可冒稱原始三態實證");
                Need(MarketItemStatePresentation.Display(row) == "未知（未鑑定）", "user rule未明示");
            }
        });
        Check("missing_or_unsupported_raw_stays_unknown_even_with_identification_claim", () =>
        {
            foreach (var row in new[] { Item(null, 1), Item(1, null), Item(null, null), Item(2, 1), Item(255, 0), Item(1, -1), Item(0, 3), Item(1, int.MaxValue) })
            {
                var claimed = row with { ItemIdentificationState = "Unidentified", ItemIdentificationEvidence = "Verified" };
                Need(MarketItemStatePresentation.EffectiveKind(claimed) == "Unknown", "缺值/不支持raw被洗成一般");
                Need(MarketItemStatePresentation.ComparisonKey(View(claimed)).IsIsolated, "無效契約參與一般分組");
            }
        });
        Check("identification_requires_exact_positive_evidence", () =>
        {
            foreach (var pair in new[] { ("Unidentified", "Unverified"), ("Unknown", "Verified"), ("unidentified", "Verified"), ("Unidentified", "verified") })
                Need(MarketItemStatePresentation.EffectiveKind(Item(0, 1, identification: pair.Item1, identificationEvidence: pair.Item2)) == "Unknown", "非已證未鑑定被套user rule");
            Need(MarketItemStatePresentation.Display(Item(1, 2, identification: "Identified", identificationEvidence: "Verified")) == "受詛咒（已鑑定）", "明確已鑑定資料顯示失真");
        });
        Check("effective_filters_merge_only_known_normal_rule_and_preserve_unknown_raw", () =>
        {
            var normal = Item(); var unidentified = Item(0, 2, identification: "Unidentified", identificationEvidence: "Verified");
            Need(MarketItemStatePresentation.FilterKey(normal) == "kind=Normal" && MarketItemStatePresentation.FilterKey(unidentified) == "kind=Normal", "有效一般無法共同篩選");
            Need(MarketItemStatePresentation.MatchesFilter(unidentified, "一般") && MarketItemStatePresentation.MatchesFilter(normal, "kind=Normal"), "精準label/key篩選失效");
            Need(!MarketItemStatePresentation.MatchesFilter(Item(0, 1), "一般"), "未知混入一般");
            Need(MarketItemStatePresentation.FilterKey(Item(0, 1)) == "kind=Unknown;flag=0;raw=1", "未知rawkey不明確");
            Need(MarketItemStatePresentation.FilterOptions([normal, unidentified, Item(1, 0), Item(1, 2), Item(0, 1), Item(null, 1)]).Length == 5, "有效/未知篩選目錄合併錯");
        });
        Check("typed_comparison_key_contains_direction_name_currency_effective_kind", () =>
        {
            var baseline = MarketItemStatePresentation.ComparisonKey(View(Item(1, 0)));
            foreach (var other in new[] { View(Item(1, 1)), View(Item(1, 2)), View(Item(1, 0, name: "+5 抗魔法斗篷")), View(Item(1, 0, unit: "點數")), View(Item(1, 0), ShopPageModes.ViewerSell) })
                Need(baseline != MarketItemStatePresentation.ComparisonKey(other), "方向/名稱/貨幣/有效三態未隔離");
            Need(MarketItemStatePresentation.ComparisonKey(View(Item(name: "a|b", unit: "c"))) != MarketItemStatePresentation.ComparisonKey(View(Item(name: "a", unit: "b|c"))), "字串分隔符碰撞");
        });
        Check("verified_unidentified_groups_with_normal_but_not_blessed_or_unknown", () =>
        {
            var normal = View(Item(1, 1)); var unidentified = View(Item(0, 0, identification: "Unidentified", identificationEvidence: "Verified"));
            var groups = MarketItemStatePresentation.GroupForComparison([normal, unidentified, View(Item(1, 0)), View(Item(0, 1))], false);
            Need(groups.Length == 3 && groups.Single(g => g.Key.EffectiveKind == "Normal").Rows.Length == 2, "user rule一般比價群組錯誤");
            Need(groups.All(g => g.Note.Contains("相同有效類型不保證隱藏屬性相同")), "比價冒稱hidden屬性相同");
        });
        Check("all_unknown_or_missing_components_isolate_even_identical_view_keys", () =>
        {
            foreach (var row in new[] { Item(0, 1), Item(1, 1, kindState: "Unknown"), Item(null, 1), Item(1, null), Item(name: null), Item(name: " "), Item(unit: null), Item(unit: " ") })
            {
                var a = View(row); var b = View(row);
                Need(a.Key == b.Key && MarketItemStatePresentation.ComparisonKey(a) != MarketItemStatePresentation.ComparisonKey(b), "未知列用共通Unknown/舊Key合組");
                Need(MarketItemStatePresentation.ComparisonKey(a) == MarketItemStatePresentation.ComparisonKey(a), "same view key不穩定");
            }
            Need(MarketItemStatePresentation.GroupForComparison([View(Item(), ""), View(Item(), "")], false).Length == 2, "方向缺值合組");
        });
        Check("effective_sort_unknown_last_both_directions_and_global_price_unchanged", () =>
        {
            var items = new[] { Item(0, 1), Item(1, 2), Item(1, 0), Item(1, 1), Item(null, null) };
            foreach (bool descending in new[] { false, true })
            {
                var sorted = items.OrderBy(r => r, Comparer<PersonalShopRow>.Create((a, b) => MarketItemStatePresentation.CompareEffective(a, b, descending))).ToArray();
                Need(sorted.Take(3).All(r => MarketItemStatePresentation.EffectiveKind(r) != "Unknown"), "降冪把未知類型提前");
                Need(MarketItemStatePresentation.EffectiveKind(sorted[0]) == (descending ? "Cursed" : "Blessed"), "有效類型排序錯");
            }
            Need(MarketOrdering.Apply([View(Item(1, 0, price: 100)), View(Item(1, 2, price: 1))], [new(MarketSortField.Price, false)])[0].NumericPrice == 1, "一般全價排序被強制分組");
        });
        Check("within_group_price_unknown_last_without_overflow", () =>
        {
            var rows = new[] { View(Item(price: null)), View(Item(price: ulong.MaxValue)), View(Item(price: 0)) };
            foreach (bool descending in new[] { false, true })
            {
                var group = MarketItemStatePresentation.GroupForComparison(rows, descending).Single();
                Need(group.Rows[^1].NumericPrice is null && group.Rows[0].NumericPrice == (descending ? ulong.MaxValue : 0), "組內價格排序失效");
            }
        });
        Check("confirmed_quantity_name_rules_retained", () =>
        {
            var a = View(Item(name: "+4 斗篷(2)", quantity: 2)); var b = View(Item(name: "+4 斗篷(3)", quantity: 3));
            Need(MarketItemStatePresentation.ComparisonKey(a) == MarketItemStatePresentation.ComparisonKey(b), "已確認數量干擾同名");
            Need(MarketItemStatePresentation.ComparisonKey(a) != MarketItemStatePresentation.ComparisonKey(View(Item(name: "+4 斗篷(2)", quantity: null))), "未確認數量被去掉");
        });
        Check("raw_and_evidence_audit_fields_preserved_after_user_rule", () =>
        {
            var row = Item(0, 2, kindState: "Unknown", identification: "Unidentified", identificationEvidence: "Verified"); var original = row with { };
            _ = MarketItemStatePresentation.Display(row); _ = MarketItemStatePresentation.FilterOptions([row]); _ = MarketItemStatePresentation.GroupForComparison([View(row)], false);
            Need(row == original && row.ItemKindRaw == 2 && row.ItemKindDetailFlag == 0 && row.ItemKindState == "Unknown" && row.State == "Observed", "改寫raw/State");
            Need(MarketItemStatePresentation.RawFilterKey(row) == "flag=0;raw=2" && MarketItemStatePresentation.EffectiveKindReason(row) == "UserRuleUnidentifiedAsNormal", "稽核raw與effective理由不可重建");
        });
        return new(checks.All(c => c.Passed), checks.Count(c => c.Passed), checks.Count(c => !c.Passed), false, false, checks.ToArray());
    }
}
internal sealed record MarketItemStateCheck(string Name, bool Passed, string Detail);
internal sealed record MarketItemStateCheckReport(bool Success, int Passed, int Failed, bool ReadGameProcess, bool GameActions, MarketItemStateCheck[] Checks);
