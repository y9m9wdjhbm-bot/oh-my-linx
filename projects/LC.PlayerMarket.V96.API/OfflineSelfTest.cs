using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

public sealed record OfflineCheck(string Name, bool Passed, string Detail);
public sealed record OfflineCheckReport(string Version, bool Success, int Passed, int Failed, bool ConnectedToService,
    bool OpenedGameProcess, bool CreatedWindow, OfflineCheck[] Checks);

/// <summary>Exercises production query logic without creating a Form, connecting IPC, or opening LC.</summary>
public static class OfflineSelfTest
{
    public static OfflineCheckReport Run()
    {
        var checks = new List<OfflineCheck>();
        var now = new DateTimeOffset(2026, 9, 20, 0, 30, 0, TimeSpan.FromHours(8));
        var acquired = now.AddDays(-2);
        var source = new SourceIdentity(1234, 7654321, "offline-only", "0x100000", 1, 4096, "offline", "offline-session-A:scene1");
        var owner = new MarketOwner { Id = 22, LocalIdentity = "owner-A", Name = "測試店主", Position = new(100, 102), ObservedAt = now };
        var listing = new MarketListing
        {
            SourceSessionKey = source.SessionKey, Revision = "offline-revision-A", RequestId = "offline-request-A", Owner = owner,
            CapturedAt = acquired, ViewerDirection = ShopPageModes.ViewerBuy, OwnerAndResponseMatched = true,
            DistanceAtRequest = 7, Complete = true,
            Rows = [new(0, "Observed", true, "測試商品", "123", 123, "每個", null, "Unverified", "離線資料")]
        };
        HubSnapshot Snapshot(DateTimeOffset sampled) => new()
        {
            Source = source, InstanceId = "offline-instance", Sequence = 1, PublishedAt = now, State = "Reading",
            Fields = new()
            {
                ["selfId"] = FieldReading.Observed(11UL, sampled),
                ["position"] = FieldReading.Observed(new { x = 100, y = 100 }, sampled),
                ["nearbyPlayers"] = FieldReading.Observed(new
                {
                    complete = true,
                    items = new[] { new { id = 22UL, localIdentity = "owner-A", kind = "Player", rawType = ".?AVPlayer@lineage@@",
                        name = "測試店主", nameState = "Observed", position = new { x = 100, y = 102 }, positionState = "Observed", sampledAt = sampled } }
                }, sampled),
                ["actorViewport"] = FieldReading.Observed(new
                {
                    complete = true, physicalPixelMappingVerified = false, currentCodeSemanticsVerified = false,
                    rawFrame = new { rawRectangle = new { x = 0, y = 0, width = 1280, height = 960 }, scaleX = 1, scaleY = 1, candidateWidth = 1280, candidateHeight = 960 },
                    items = new[]
                    {
                        new { localId = 11UL, kind = "MyPlayer", presentInSampledCollection = true, rectangleState = "RawRectangleObserved", correctedRectangleCandidate = new { x = 618, y = 568, width = 90, height = 93 } },
                        new { localId = 22UL, kind = "Player", presentInSampledCollection = true, rectangleState = "RawRectangleObserved", correctedRectangleCandidate = new { x = 511, y = 508, width = 20, height = 92 } }
                    }
                }, sampled)
            }
        };
        void Check(string name, Action assertion)
        {
            try { assertion(); checks.Add(new(name, true, "通過")); }
            catch (Exception ex) { checks.Add(new(name, false, ex.Message)); }
        }
        static void Require(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }

        Check("numeric_price_ascending_unknown_last", () =>
        {
            ulong?[] values = [123, null, 9, 0, ulong.MaxValue];
            var ordered = MarketOrdering.ByPrice(values, v => v, _ => acquired, false).ToArray();
            Require(ordered.SequenceEqual(new ulong?[] { 0, 9, 123, ulong.MaxValue, null }), "金額未按數值升冪或未知未排最後");
        });
        Check("numeric_price_descending_unknown_last", () =>
        {
            ulong?[] values = [123, null, 9, 0, ulong.MaxValue];
            var ordered = MarketOrdering.ByPrice(values, v => v, _ => acquired, true).ToArray();
            Require(ordered.SequenceEqual(new ulong?[] { ulong.MaxValue, 123, 9, 0, null }), "金額未按數值降冪或最高值溢位");
        });
        Check("history_acquired_time_survives_roundtrip", () =>
        {
            var restored = JsonSerializer.Deserialize<MarketListing>(JsonSerializer.Serialize(listing, HubProtocol.Json), HubProtocol.Json)!;
            var row = new MarketRowView(restored, restored.Rows[0], null, null, false);
            Require(row.CapturedAt == acquired && row.CapturedAt != now && restored.CapturedAt.Offset == acquired.Offset,
                "載入歷史不得把取得時間改為目前時間");
            Require(row.NumericDistance is null && row.Distance.Contains("取得時", StringComparison.Ordinal), "歷史距離冒充目前距離");
        });
        Check("unknown_quantity_is_not_unit_or_zero", () =>
        {
            var row = new MarketRowView(listing, listing.Rows[0], null, null, false);
            Require(row.Quantity == "未知" && row.Unit == "每個", "未確認數量被補成 0 或計價單位");
        });
        Check("fresh_source_identity_and_distance", () =>
        {
            var context = new LiveContext(Snapshot(now.AddMilliseconds(-1900)), now);
            Require(context.Valid && context.Match(listing) is not null, "有效來源與 local identity 被拒絕");
            var row = new MarketRowView(listing, listing.Rows[0], context.Match(listing), context.SelfPosition, true);
            Require(row.NumericDistance == 2, "目前格數應為 Chebyshev 距離 2");
        });
        Check("stale_fields_reject_live_owner", () =>
        {
            var context = new LiveContext(Snapshot(now.AddMilliseconds(-2001)), now);
            Require(!context.Valid && context.Match(listing) is null, "超過 2 秒的欄位仍被當作即時來源");
        });
        Check("future_clock_is_not_fresh", () =>
        {
            var context = new LiveContext(Snapshot(now.AddMilliseconds(1)), now);
            Require(!context.Valid && context.Match(listing) is null, "未來時間欄位仍被當作新鮮資料");
        });
        Check("missing_source_rejects_live_owner", () =>
        {
            var context = new LiveContext(Snapshot(now) with { Source = null }, now);
            Require(!context.Valid && context.Match(listing) is null, "缺來源仍允許對應店主");
        });
        Check("different_scene_rejects_old_listing", () =>
        {
            var context = new LiveContext(Snapshot(now) with { Source = source with { SessionKey = "offline-session-A:scene2" } }, now);
            Require(context.Valid && context.Match(listing) is null, "另一場景接受了舊清單 ID");
        });
        Check("reused_id_requires_same_local_identity", () =>
        {
            var context = new LiveContext(Snapshot(now), now);
            Require(context.Match(listing with { Owner = owner with { LocalIdentity = "owner-B" } }) is null,
                "相同數字 ID 不得取代 local identity 校對");
        });
        Check("unverified_projection_stays_hidden_by_default", () =>
        {
            var context = new LiveContext(Snapshot(now), now);
            bool drawn = context.TryNavigation(listing, new Size(1280, 960), false, true, out var view, out _);
            Require(!drawn && view is null, "false 投影旗標被升級成可顯示位置");
            bool candidate = context.TryNavigation(listing, new Size(1280, 960), true, true, out var candidateView, out _);
            Require(candidate && candidateView?.Research == true, "有效候選的對照組必須可讀且保持研究狀態，不能因假資料缺欄位而假通過");
        });
        return new("42.0.0", checks.All(c => c.Passed), checks.Count(c => c.Passed), checks.Count(c => !c.Passed), false, false, false, checks.ToArray());
    }
}

