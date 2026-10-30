using MemoryHub;

namespace LcPlayerMarket;

internal sealed record LatestListingsCheck(string Name, bool Passed, string Detail);
internal sealed record LatestListingsReport(string OutputDirectory, LatestListingsCheck[] Checks)
{
    public bool Passed => Checks.All(check => check.Passed);
    public int PassedCount => Checks.Count(check => check.Passed);
    public int FailedCount => Checks.Count(check => !check.Passed);
}

/// <summary>Only V17 listing replacement and persistent clear behavior; never contacts LC or IPC.</summary>
internal static class LatestListingsSelfTest
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 20, 16, 0, 0, TimeSpan.FromHours(8));

    public static LatestListingsReport Run(string outputDirectory)
    {
        string root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root) || File.Exists(root))
            throw new IOException("測試輸出路徑必須尚未存在；不覆寫任何既有資料。");
        Directory.CreateDirectory(root);
        var checks = new List<LatestListingsCheck>();

        void Check(string name, Action body)
        {
            try { body(); checks.Add(new(name, true, "通過")); }
            catch (Exception ex) { checks.Add(new(name, false, ex.GetType().Name + ": " + ex.Message)); }
        }

        Check("rescan_replaces_same_owner_page_and_updates_price_quantity", () =>
        {
            var index = new MarketListingIndex();
            var old = Listing("first", 1);
            var updated = Listing("updated", 2) with { Rows = [Row(0, price: 27000, quantity: 93)] };
            Require(index.Upsert(old).Changed, "第一筆沒有加入");
            var change = index.Upsert(updated);
            Require(change.Changed && change.Previous?.Revision == old.Revision, "更新沒有指出被取代的舊頁");
            Require(index.Count == 1, "重掃後仍累加同店同方向的頁面");
            Require(index.Values.Single().Rows.Single() is { Price: 27000, Quantity: 93 }, "最新價格或數量未更新");
            Require(!index.Upsert(updated).Changed && index.Count == 1, "相同回覆重傳造成展示變動");
        });

        Check("older_success_cannot_replace_new_success_regardless_of_load_order", () =>
        {
            var old = Listing("older", 1);
            var latest = Listing("latest", 2);
            foreach (MarketListing[] values in new[] { new[] { old, latest }, new[] { latest, old } })
            {
                var index = Latest(values);
                Require(index.Count == 1 && index.Values.Single().Revision == latest.Revision,
                    "歷史檔案載入順序改變最新商品頁");
            }
        });

        Check("failed_partial_and_unmatched_pages_keep_confirmed_success", () =>
        {
            var success = Listing("success", 1);
            MarketListing[] uncertain =
            [
                Listing("failed", 2) with { OwnerAndResponseMatched = false, Complete = false, Rows = [], State = "Failed" },
                Listing("partial", 3) with { Complete = false, Rows = [] },
                Listing("unmatched", 4) with { OwnerAndResponseMatched = false, Rows = [] }
            ];
            foreach (var page in uncertain)
                foreach (MarketListing[] values in new[] { new[] { success, page }, new[] { page, success } })
                {
                    var index = Latest(values);
                    Require(index.Count == 1 && index.Values.Single().Revision == success.Revision,
                        "未確認頁取代已確認商品：" + page.Revision);
                }
        });

        Check("confirmed_empty_page_replaces_old_stock", () =>
        {
            var empty = Listing("empty", 2) with { Rows = [], PageEmpty = true };
            var index = Latest([Listing("stock", 1), empty]);
            Require(index.Count == 1 && index.Values.Single().Revision == empty.Revision
                && index.Values.Single().Rows.Length == 0, "已確認空頁仍保留舊商品");
        });

        Check("buy_sell_and_same_page_item_variants_are_preserved", () =>
        {
            PersonalShopRow[] variants =
            [
                Row(0, 30000, 1) with { ItemKind = "Blessed", ItemKindRaw = 0 },
                Row(1, 20000, 2) with { ItemKind = "Normal", ItemKindRaw = 1 },
                Row(2, 10000, 3) with { ItemKind = "Cursed", ItemKindRaw = 2 }
            ];
            var buy = Listing("buy", 1) with { Rows = variants };
            var sell = Listing("sell", 2) with { ViewerDirection = ShopPageModes.ViewerSell, Rows = variants };
            var index = Latest([buy, sell, buy with { Revision = "buy-latest", CapturedAt = Epoch.AddSeconds(3) }]);
            Require(index.Count == 2, "同店買賣頁互相覆蓋或買頁重複");
            foreach (var page in index.Values)
                Require(page.Rows.Length == 3 && page.Rows.Select(row => row.ItemKind).SequenceEqual(new[] { "Blessed", "Normal", "Cursed" }),
                    "同頁的同名商品變體被合併");
        });

        Check("same_named_shop_after_session_change_uses_new_navigation_identity", () =>
        {
            var previous = Listing("session-old", 1);
            var latest = Listing("session-new", 2) with
            {
                SourceSessionKey = "offline-session-B",
                Owner = previous.Owner with { Id = 9002, LocalIdentity = "new-owner-binding", ObservedAt = Epoch.AddSeconds(2) }
            };
            var index = Latest([previous, latest]);
            Require(index.Count == 1, "重啟後同名稱同地圖同位置的商店未合併");
            Require(index.TryGetValue(LocalStore.Key(latest), out var resolved) && resolved.Owner.Id == 9002
                && resolved.SourceSessionKey == latest.SourceSessionKey && resolved.Owner.LocalIdentity == "new-owner-binding",
                "最新展示頁的 revision 無法取得新 session 的導航身分");
        });

        Check("different_name_map_or_position_remains_distinct", () =>
        {
            var first = Listing("base", 1);
            var index = Latest([
                first,
                first with { Revision = "name", Owner = first.Owner with { Name = "其他店主" } },
                first with { Revision = "map", Owner = first.Owner with { MapName = "奇岩" } },
                first with { Revision = "position", Owner = first.Owner with { Position = new(34053, 32288) } }
            ]);
            Require(index.Count == 4, "不同店主、地圖或位置被錯誤合併");
        });

        Check("missing_metadata_falls_back_to_local_source_identity", () =>
        {
            foreach (string missing in new[] { "name", "map", "position" })
            {
                var original = Listing("fallback-old-" + missing, 1);
                MarketOwner owner = missing switch
                {
                    "name" => original.Owner with { Name = "" },
                    "map" => original.Owner with { MapName = null },
                    _ => original.Owner with { Position = null }
                };
                original = original with { Owner = owner };
                var sameSource = original with { Revision = "fallback-new-" + missing, CapturedAt = Epoch.AddSeconds(2) };
                var otherSource = sameSource with { Revision = "fallback-other-" + missing, SourceSessionKey = "offline-session-B" };
                var index = Latest([original, sameSource, otherSource]);
                Require(index.Count == 2 && index.Values.Any(page => page.Revision == sameSource.Revision)
                    && index.Values.Any(page => page.Revision == otherSource.Revision),
                    "缺少 " + missing + " 時沒有依本機來源隔離，或同來源未去重");
            }
        });

        Check("history_reopen_keeps_latest_success_and_both_directions", () =>
        {
            string storeRoot = Path.Combine(root, "reopen-store");
            var store = new LocalStore(storeRoot);
            Require(store.SaveListing(Listing("old", 1)), "舊頁存檔失敗");
            Require(store.SaveListing(Listing("new", 2)), "最新頁存檔失敗");
            Require(store.SaveListing(Listing("sell", 3) with { ViewerDirection = ShopPageModes.ViewerSell }), "賣頁存檔失敗");
            store.SaveListing(Listing("failed", 4) with { OwnerAndResponseMatched = false, Complete = false, Rows = [] });
            var reopened = new LocalStore(storeRoot);
            var latest = Latest(reopened.LoadHistory());
            Require(latest.Count == 2 && latest.Values.Single(page => page.ViewerDirection == ShopPageModes.ViewerBuy).Revision == "new"
                && latest.Values.Single(page => page.ViewerDirection == ShopPageModes.ViewerSell).Revision == "sell",
                "重新開啟後重現重複商品或丟失最新成功頁");
        });

        Check("clear_persists_and_rejects_service_replay_until_resume", () =>
        {
            string storeRoot = Path.Combine(root, "clear-store");
            var store = new LocalStore(storeRoot);
            var old = Listing("before-clear", 1);
            Require(store.SaveListing(old), "清除前存檔失敗");
            Require(store.ClearListings(Epoch.AddSeconds(10)), "清除切點未保存");
            Require(!store.IsListingVisible(old) && store.LoadHistory().Length == 0, "清除後仍可見舊清單");
            var replay = Listing("service-replay", 11);
            store.SaveListing(replay); // Immutable history may be kept; it must not return to the visible list.
            Require(!store.IsListingVisible(replay) && store.LoadHistory().Length == 0, "清除後服務重傳立即補回");
            var reopened = new LocalStore(storeRoot);
            Require(reopened.LoadHistory().Length == 0 && !reopened.IsListingVisible(old) && !reopened.IsListingVisible(replay),
                "程式重新開啟後清除狀態遺失");
        });

        Check("resume_accepts_only_new_request_and_capture_after_cutoff", () =>
        {
            string storeRoot = Path.Combine(root, "resume-store");
            var store = new LocalStore(storeRoot);
            Require(store.SaveListing(Listing("old", 1)), "準備歷史失敗");
            Require(store.ClearListings(Epoch.AddSeconds(10)), "清除失敗");
            DateTimeOffset cutoff = Epoch.AddSeconds(20);
            Require(store.ResumeListings(cutoff), "重新搜尋切點未保存");
            MarketListing[] rejected =
            [
                Listing("old-capture", 19) with { RequestedAt = Epoch.AddSeconds(21) },
                Listing("equal-capture", 20) with { RequestedAt = null },
                Listing("inflight-before-resume", 22) with { RequestedAt = Epoch.AddSeconds(19) },
                Listing("equal-request", 22) with { RequestedAt = cutoff }
            ];
            foreach (var page in rejected)
            {
                store.SaveListing(page);
                Require(!store.IsListingVisible(page), "舊或切點上的資料被補回：" + page.Revision);
            }
            var fresh = Listing("fresh", 23) with { RequestedAt = Epoch.AddSeconds(21) };
            Require(store.IsListingVisible(fresh) && store.SaveListing(fresh), "真正的新查詢被忽略");
            var legacyFresh = Listing("fresh-no-request-time", 24) with { RequestedAt = null, ViewerDirection = ShopPageModes.ViewerSell };
            store.SaveListing(legacyFresh);
            Require(!store.IsListingVisible(legacyFresh), "缺少請求開始時間的資料不應越過清除切點");
            var reopened = new LocalStore(storeRoot);
            var visible = Latest(reopened.LoadHistory());
            Require(visible.Count == 1 && visible.Values.Single().Revision == fresh.Revision, "重啟後未保持 resume 切點或補回缺少請求時間的舊資料");
            foreach (var page in rejected)
                Require(!reopened.IsListingVisible(page), "重啟後舊的 inflight 查詢可重新補回");
        });

        Check("second_clear_hides_previously_resumed_data", () =>
        {
            string storeRoot = Path.Combine(root, "second-clear-store");
            var store = new LocalStore(storeRoot);
            Require(store.ClearListings(Epoch.AddSeconds(10)) && store.ResumeListings(Epoch.AddSeconds(20)), "第一次清除或恢復失敗");
            var page = Listing("after-resume", 22) with { RequestedAt = Epoch.AddSeconds(21) };
            Require(store.SaveListing(page), "新頁未保存");
            Require(store.ClearListings(Epoch.AddSeconds(30)), "第二次清除失敗");
            var reopened = new LocalStore(storeRoot);
            Require(reopened.LoadHistory().Length == 0 && !reopened.IsListingVisible(page), "第二次清除沒有跨重啟生效");
        });

        Check("index_clear_and_trim_release_stale_revision_lookup", () =>
        {
            var a = Listing("trim-a", 1);
            var b = Listing("trim-b", 2) with { Owner = a.Owner with { Name = "第二商店" } };
            var index = Latest([a, b]);
            index.Trim(1);
            Require(index.Count == 1 && index.TryGetValue(LocalStore.Key(b), out _), "Trim 沒有保留最新頁");
            index.Clear();
            Require(index.Count == 0 && !index.Values.Any() && !index.TryGetValue(LocalStore.Key(b), out _), "Clear 後仍留有頁面或 revision 對照");
        });

        return new(root, checks.ToArray());
    }

    private static MarketListingIndex Latest(IEnumerable<MarketListing> pages)
    {
        var index = new MarketListingIndex();
        foreach (var page in pages) index.Upsert(page);
        return index;
    }

    private static MarketListing Listing(string revision, int second) => new()
    {
        Revision = revision,
        SourceSessionKey = "offline-session-A",
        RequestId = "request-" + revision,
        Owner = new() { Id = 1001, LocalIdentity = "owner-binding-A", Name = "測試店主", MapName = "古魯丁村",
            Position = new(34052, 32288), ObservedAt = Epoch.AddSeconds(second) },
        ViewerDirection = ShopPageModes.ViewerBuy,
        CapturedAt = Epoch.AddSeconds(second),
        RequestedAt = Epoch.AddSeconds(second).AddMilliseconds(-100),
        State = "Complete",
        OwnerAndResponseMatched = true,
        Complete = true,
        Rows = [Row(0)]
    };

    private static PersonalShopRow Row(int index, ulong price = 25000, ulong quantity = 98) =>
        new(index, "Matched", true, "夏納的變形卷軸", price.ToString(), price, "每個", quantity, "Matched", "V17 離線測試資料");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
