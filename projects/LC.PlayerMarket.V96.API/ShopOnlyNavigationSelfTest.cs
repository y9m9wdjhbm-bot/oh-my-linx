using MemoryHub;
using System.Text.Json;

namespace LcPlayerMarket;

/// <summary>Self position and saved-owner distance remain usable with no automatic nearby fields.</summary>
public static class ShopOnlyNavigationSelfTest
{
    public static OfflineCheck[] Run()
    {
        var checks = new List<OfflineCheck>();
        var now = new DateTimeOffset(2026, 9, 20, 6, 0, 0, TimeSpan.FromHours(8));
        var savedAt = now.AddHours(-2);
        var source = new SourceIdentity(123, 999, "offline-only", "0x10000", 1, 65536, "offline", "source-A");
        var owner = new MarketOwner { Id = 22, LocalIdentity = "owner-A", Name = "保存店主", Position = new(110, 105), ObservedAt = savedAt };
        var item = new PersonalShopRow(0, "Observed", true, "商品", "50", 50, "每個", 1, "Matched", "offline");
        var listing = new MarketListing { Revision = "saved-page", SourceSessionKey = source.SessionKey, Owner = owner,
            ViewerDirection = ShopPageModes.ViewerBuy, CapturedAt = savedAt, OwnerAndResponseMatched = true, Complete = true, Rows = [item] };
        var saved = new ManualNearbyContext("manual-A", source, savedAt, new(100, 100), [owner]);
        HubSnapshot SelfOnly(int x = 108, int y = 104) => new()
        {
            Source = source, PublishedAt = now, State = "Reading", Fields = new()
            {
                ["selfId"] = FieldReading.Observed(11UL, now), ["position"] = FieldReading.Observed(new { x, y }, now),
                ["mapName"] = FieldReading.Observed("古魯丁村", now),
                ["nearbyPlayers"] = FieldReading.Unknown("等待商店手動刷新"), ["actorViewport"] = FieldReading.Unknown("只按需查詢選中目標")
            }
        };
        void Check(string name, Action assertion)
        {
            try { assertion(); checks.Add(new(name, true, "通過")); }
            catch (Exception error) { checks.Add(new(name, false, error.Message)); }
        }
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        Check("shop_only_self_and_map_valid_without_nearby_players", () =>
        {
            var live = new LiveContext(SelfOnly(), now);
            Require(live.Valid && live.SelfId == 11 && live.SelfPosition == new ShopPoint(108, 104)
                && live.MapName == "古魯丁村" && !live.NearbyAvailable && live.Match(listing) is null,
                "自身有效性仍依賴自動周遭，或手動名單冒充即時玩家來源");
        });
        Check("saved_owner_distance_uses_current_self_not_refresh_origin", () =>
        {
            var live = new LiveContext(SelfOnly(), now);
            var row = new MarketRowView(listing, item, saved.Match(listing), live.SelfPosition, true, true, true);
            Require(row.NumericDistance == 2 && row.Distance.Contains("至店主保存位置") && !row.Distance.Contains("刷新時")
                && saved.SelfPosition == new ShopPoint(100, 100) && saved.Match(listing)?.At == savedAt,
                "距離仍使用兩小時前自身座標，或改寫保存名單");
        });
        Check("moving_self_changes_distance_without_changing_saved_owner", () =>
        {
            var first = new MarketRowView(listing, item, saved.Match(listing), new LiveContext(SelfOnly(), now).SelfPosition, true, true, true);
            var second = new MarketRowView(listing, item, saved.Match(listing), new LiveContext(SelfOnly(110, 105), now).SelfPosition, true, true, true);
            Require(first.NumericDistance == 2 && second.NumericDistance == 0 && saved.Match(listing)?.Position == owner.Position
                && saved.RefreshedAt == savedAt, "自身移動未更新距離，或動到手動候選");
        });
        Check("expired_self_disables_current_distance_without_expiring_saved_targets", () =>
        {
            var live = new LiveContext(SelfOnly(), now.AddSeconds(3));
            var row = new MarketRowView(listing, item, saved.Match(listing), live.Valid ? live.SelfPosition : null, true, true, true);
            Require(!live.Valid && row.NumericDistance is null && saved.CanUse(listing), "過期自身座標仍作即時距離，或一併取消保存目標");
        });
        Check("missing_self_position_does_not_fake_valid_navigation", () =>
        {
            var snapshot = SelfOnly(); snapshot.Fields["position"] = FieldReading.Unknown("暫無座標");
            Require(!new LiveContext(snapshot, now).Valid && saved.CanUse(listing), "缺少自身座標卻宣稱可導航");
        });
        Check("self_only_snapshot_cannot_invent_a_screen_marker", () =>
        {
            var live = new LiveContext(SelfOnly(), now);
            Require(!live.TryNavigation(listing, new Size(1280, 960), true, true, out var view, out _) && view is null,
                "缺少目標投影時自行捏造紅點");
        });
        JsonElement Viewport(bool found = true) => JsonSerializer.SerializeToElement(new
        {
            complete = true, collectionEnumerated = false, physicalPixelMappingVerified = false, currentCodeSemanticsVerified = false,
            rawFrame = new { rawRectangle = new { x = 0, y = 0, width = 1280, height = 960 }, scaleX = 1, scaleY = 1, candidateWidth = 1280, candidateHeight = 960 },
            items = new[]
            {
                new { localId = 11UL, kind = "MyPlayer", presentInSampledCollection = false, presentInSelectedLookup = true,
                    rectangleState = "RawRectangleObserved", correctedRectangleCandidate = new { x = 618, y = 568, width = 90, height = 93 } },
                new { localId = 22UL, kind = "Player", presentInSampledCollection = false, presentInSelectedLookup = found,
                    rectangleState = "RawRectangleObserved", correctedRectangleCandidate = new { x = 511, y = 508, width = 20, height = 92 } }
            }
        }, HubProtocol.Json);
        ShopNavigationSnapshot Projection() => new()
        {
            SourceSessionKey = source.SessionKey, TargetSnapshotId = saved.SnapshotId, OwnerId = owner.Id, OwnerIdentity = owner.LocalIdentity,
            SelfId = 11, SampledAt = now, SavedTargetAt = owner.ObservedAt, SelfPosition = new(108, 104), SavedOwnerPosition = owner.Position,
            MapName = "古魯丁村", State = "ResearchCandidate", Detail = "兩個指定 ID 閉合，沒有枚舉周遭", RawViewport = Viewport(),
            CurrentActorIdentityVerified = false, PhysicalPixelMappingVerified = false
        };
        bool Try(ShopNavigationSnapshot value, out NavigationView? view, bool candidate = true, bool bottom = true) =>
            new LiveContext(SelfOnly(), now).TrySelectedNavigation(listing, saved, value, new Size(1280, 960), candidate, bottom, out view, out _);
        Check("selected_projection_works_without_automatic_nearby_collection", () =>
        {
            Require(Try(Projection(), out var view) && view is { Research: true, Distance: 2, DistanceToSavedPosition: true }
                && view.Target == new RectangleF(511, 360, 20, 92) && view.Evidence.Contains("保存位置"),
                "兩個 ID 的候選投影仍依賴完整周遭，或錯用刷新時自身距離");
        });
        Check("selected_projection_respects_research_opt_in_and_origin", () =>
        {
            Require(!Try(Projection(), out _, candidate: false), "未勾研究投影仍畫未校準紅點");
            Require(Try(Projection(), out var view, bottom: false) && view?.Target == new RectangleF(511, 508, 20, 92), "原點選項失效");
        });
        Check("selected_projection_rejects_old_target_or_snapshot", () =>
        {
            Require(!Try(Projection() with { TargetSnapshotId = "replaced" }, out _)
                && !Try(Projection() with { OwnerId = 23 }, out _)
                && !Try(Projection() with { OwnerIdentity = "other" }, out _), "另一目標或舊快照的回覆仍可畫點");
        });
        Check("selected_projection_rejects_old_source_or_self", () =>
        {
            Require(!Try(Projection() with { SourceSessionKey = "other-source" }, out _)
                && !Try(Projection() with { SelfId = 99 }, out _), "跨來源／自身的投影仍可畫點");
        });
        Check("selected_projection_requires_recent_observation_but_saved_owner_can_be_old", () =>
        {
            Require(Try(Projection(), out _) && !Try(Projection() with { SampledAt = now.AddMilliseconds(-2001) }, out _)
                && !Try(Projection() with { SampledAt = now.AddMilliseconds(1) }, out _), "投影 TTL 套錯到舊名單，或過期／未來投影未拒絕");
        });
        Check("selected_projection_does_not_adopt_changed_saved_position_or_time", () =>
        {
            Require(!Try(Projection() with { SavedTargetAt = now }, out _)
                && !Try(Projection() with { SavedOwnerPosition = new(111, 105) }, out _), "投影回覆暗中換掉保存目標的位置或採樣時間");
        });
        Check("selected_projection_unavailable_or_missing_hash_entry_hides_marker", () =>
        {
            Require(!Try(Projection() with { State = "ProjectionUnavailable" }, out _)
                && !Try(Projection() with { RawViewport = null }, out _)
                && !Try(Projection() with { RawViewport = Viewport(false) }, out _), "欠缺投影仍虛構畫面紅點");
        });
        return checks.ToArray();
    }
}
