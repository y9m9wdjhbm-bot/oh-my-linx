using MemoryHub;

namespace LcPlayerMarket;

public static class SavedPositionNavigationSelfTest
{
    public static OfflineCheck[] Run()
    {
        var checks = new List<OfflineCheck>();
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var source = new SourceIdentity(123, 999, "offline", "0x10000", 1, 65536, "offline", "current-source");
        var owner = new MarketOwner { Id = 22, LocalIdentity = "old-actor", Name = "已離畫面的商店",
            Position = new(110, 105), MapName = "古魯丁村", ObservedAt = now.AddDays(-1) };
        var item = new PersonalShopRow(0, "Observed", true, "商品", "50", 50, "每個", 1, "Matched", "offline");
        var listing = new MarketListing { Revision = "record-A", SourceSessionKey = "old-source", Owner = owner,
            CapturedAt = now.AddDays(-1), OwnerAndResponseMatched = true, Complete = true, Rows = [item], ViewerDirection = ShopPageModes.ViewerBuy };
        HubSnapshot Snapshot(int x = 108, int y = 104, string? map = "古魯丁村") => new()
        {
            Source = source, State = "Reading", PublishedAt = now, Fields = new()
            {
                ["selfId"] = FieldReading.Observed(11UL, now), ["position"] = FieldReading.Observed(new { x, y }, now),
                ["mapName"] = map is null ? FieldReading.Unknown("未知地圖") : FieldReading.Observed(map, now),
                ["nearbyPlayers"] = FieldReading.Unknown("商店不在畫面"), ["actorViewport"] = FieldReading.Unknown("目標不存在")
            }
        };
        void Check(string name, Action assertion)
        { try { assertion(); checks.Add(new(name, true, "通過")); } catch (Exception e) { checks.Add(new(name, false, e.Message)); } }
        void Need(bool ok, string detail) { if (!ok) throw new InvalidOperationException(detail); }
        Check("saved_coordinate_without_nearby_or_projection", () =>
        {
            var c = new LiveContext(Snapshot(), now);
            Need(c.TrySavedPositionNavigation(listing, out var v, out _) && v is { CoordinateOnly: true, Research: false, Distance: 2 }
                && v.Direction == new PointF(6, -1) && v.Target == RectangleF.Empty && v.SavedPosition == owner.Position,
                "店主不在畫面仍依賴投影或捏造畫面點");
        });
        Check("refresh_removes_actor_but_saved_coordinate_survives", () =>
        {
            var localListing = listing with { SourceSessionKey = source.SessionKey };
            var before = new ManualNearbyContext("before", source, now, new(108, 104), [owner]);
            var after = new ManualNearbyContext("after", source, now, new(108, 104), []);
            Need(before.CanUse(localListing) && !after.CanUse(localListing)
                && new LiveContext(Snapshot(), now).TrySavedPositionNavigation(localListing, out _, out _),
                "刷新後仍把開店資格當成座標導航資格");
        });
        Check("new_source_and_unknown_old_actor_id_do_not_block_coordinates", () =>
        {
            Need(new LiveContext(Snapshot(), now).TrySavedPositionNavigation(listing with
                { Owner = owner with { Id = 0, LocalIdentity = "" } }, out _, out _), "導航仍要求舊 ID 或舊來源");
        });
        Check("expired_and_future_self_cannot_guide", () =>
        {
            Need(!new LiveContext(Snapshot(), now.AddSeconds(3)).TrySavedPositionNavigation(listing, out _, out _)
                && !new LiveContext(Snapshot(), now.AddMilliseconds(-1)).TrySavedPositionNavigation(listing, out _, out _), "接受不新鮮自身座標");
        });
        Check("unknown_self_shows_reason_and_keeps_saved_record", () =>
        {
            var snapshot = Snapshot(); snapshot.Fields["position"] = FieldReading.Unknown("暫時讀不到");
            Need(!new LiveContext(snapshot, now).TrySavedPositionNavigation(listing, out var v, out var why)
                && v is null && why.Contains("自身座標") && listing.Owner.Position == new ShopPoint(110, 105), "未知自身仍導航或改寫紀錄");
        });
        Check("different_region_names_do_not_claim_map_mismatch_or_block", () =>
        {
            Need(new LiveContext(Snapshot(map: "銀騎士村"), now).TrySavedPositionNavigation(listing, out var v, out var why)
                && v?.CoordinateNote == "地區名稱不同；地圖未確認" && why.Contains("地圖未確認"), "把地區名稱不同誤當不同座標空間");
        });
        Check("unknown_map_is_never_assumed_equal", () =>
        {
            var c = new LiveContext(Snapshot(), now);
            Need(new LiveContext(Snapshot(map: null), now).TrySavedPositionNavigation(listing, out var a, out _)
                && c.TrySavedPositionNavigation(listing with { Owner = owner with { MapName = null } }, out var b, out _)
                && c.TrySavedPositionNavigation(listing with { Owner = owner with { MapName = "未知" } }, out var d, out _)
                && a?.CoordinateNote == "地圖未確認" && b?.CoordinateNote == "地圖未確認" && d?.CoordinateNote == "地圖未確認",
                "未知地圖阻擋座標方向或被當同地圖");
        });
        Check("arrival_is_saved_point_not_owner_presence", () =>
        {
            Need(new LiveContext(Snapshot(110, 105), now).TrySavedPositionNavigation(listing, out var v, out _)
                && v is { Distance: 0, CoordinateOnly: true, DistanceToSavedPosition: true } && v.Direction == PointF.Empty,
                "到達座標仍有方向／距離或冒充店主仍在");
        });
        Check("self_movement_updates_distance_and_keeps_record_immutable", () =>
        {
            Need(new LiveContext(Snapshot(100, 100), now).TrySavedPositionNavigation(listing, out var a, out _)
                && new LiveContext(Snapshot(109, 105), now).TrySavedPositionNavigation(listing, out var b, out _)
                && a?.Distance == 10 && b?.Distance == 1 && listing.Owner == owner && owner.ObservedAt == now.AddDays(-1), "距離不更新或改寫紀錄時間");
        });
        Check("eight_isometric_directions_from_captured_coordinate_relation", () =>
        {
            // Explicit expected screen vectors independently transcribed from
            // Coordinate40 P/Q and bottom-origin conversion; not the production formula.
            foreach (var (d, screen) in new[] {
                (new Point(-1,-1), new PointF(-4,0)), (new Point(0,-1), new PointF(-2,-1)),
                (new Point(1,-1), new PointF(0,-2)), (new Point(1,0), new PointF(2,-1)),
                (new Point(1,1), new PointF(4,0)), (new Point(0,1), new PointF(2,1)),
                (new Point(-1,1), new PointF(0,2)), (new Point(-1,0), new PointF(-2,1)) })
            {
                var target = listing with { Owner = owner with { Position = new(100 + d.X, 100 + d.Y) } };
                Need(new LiveContext(Snapshot(100, 100), now).TrySavedPositionNavigation(target, out var v, out _)
                    && v?.Direction == screen && v.Distance == 1, "等角畫面方位不符");
            }
        });
        Check("invalid_target_and_self_coordinates_are_rejected", () =>
        {
            var c = new LiveContext(Snapshot(), now);
            foreach (var p in new ShopPoint?[] { null, new(0, 1), new(-1, 1), new(1, 1000001), new(int.MaxValue, int.MinValue) })
                Need(!c.TrySavedPositionNavigation(listing with { Owner = owner with { Position = p } }, out _, out _), "接受無效保存座標");
            Need(!new LiveContext(Snapshot(-1, 1), now).TrySavedPositionNavigation(listing, out _, out _), "接受無效自身座標");
        });
        Check("saved_distance_row_updates_without_live_actor", () =>
        {
            var row = new MarketRowView(listing, item, null, new(108, 104), false, savedPositionNavigation: true);
            Need(row.NumericDistance == 2 && row.Distance.Contains("保存位置") && row.Status.Contains("可依座標導航")
                && row.SortPosition == owner.Position && !row.Live, "列表保存距離錯誤或誤標為即時店主");
        });
        return checks.ToArray();
    }
}
