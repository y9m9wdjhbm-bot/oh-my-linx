using System.Text;
using System.Text.Json;
using MemoryHub;
using MemoryHub.Reader;
using MemoryHub.Service;

namespace LcPlayerMarket;

/// <summary>Fake observations and isolated progress files only; never starts LC or a native helper.</summary>
internal static class OnlyShoppingDispatchSelfTest
{
    internal static int RunReport(string output)
    {
        var checks = Run();
        var report = new OfflineCheckReport("83.0.0", checks.Length > 0 && checks.All(check => check.Passed),
            checks.Count(check => check.Passed), checks.Count(check => !check.Passed), false, false, false, checks);
        string json = JsonSerializer.Serialize(report, HubProtocol.Json);
        Console.WriteLine(json);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(file, new UTF8Encoding(false));
        writer.WriteLine(json); writer.Flush(); file.Flush(true);
        return report.Success ? 0 : 1;
    }

    internal static OfflineCheck[] Run()
    {
        var checks = new List<OfflineCheck>();
        string root = Path.Combine(Path.GetTempPath(), "LC-Market56-shopping-only-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = new SourceIdentity(4242, 987654321, "offline-only", "0x100000", 1, 4096,
            new string('A', 64), "offline-shopping-only-session");
        var at = DateTimeOffset.UtcNow;
        var shopping = new MarketOwner { Id = 22, LocalIdentity = "shopping-owner", Name = "離線擺攤玩家",
            Position = new(100, 101), ObservedAt = at, ActionType = ".?AVShopAction@lineage@@",
            ShopState = ShopEligibilityPolicy.ShopActionObserved, ShopStateSampledAt = at,
            ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence };
        var other = shopping with { Id = 23, LocalIdentity = "other-owner", ActionType = ".?AVMovingAction@lineage@@",
            ShopState = ShopEligibilityPolicy.KnownOtherActionObserved };
        var unknown = shopping with { Id = 24, LocalIdentity = "unknown-owner", ActionType = null,
            ShopState = ShopEligibilityPolicy.Unknown, ShopStateSampledAt = null, ShopStateEvidence = "" };
        var targets = new ShopTargetSnapshot { SnapshotId = "offline-shopping-only-targets", SourceSessionKey = source.SessionKey,
            RefreshedAt = at, SelfId = 11, SelfPosition = new(100, 100), Owners = [shopping, other, unknown], CanDispatch = false };
        void Check(string name, Action work)
        {
            try { work(); checks.Add(new(name, true, "通過；假觀測及隔離檔案，沒有開啟 LC")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        static void Need(bool pass, string detail) { if (!pass) throw new InvalidOperationException(detail); }
        try
        {
            Check("shopping_only_force_plan_excludes_nonshop_and_unknown", () =>
            {
                var store = new ShopScanProgressStore(Path.Combine(root, "plan"));
                var plan = store.StartNew(source, targets, forceDispatch: true);
                Need(plan.Owners.Length == 1 && plan.Owners[0].Id == shopping.Id,
                    $"強制計畫包含非擺攤／未知玩家：owners={plan.Owners.Length}；只應包含已確認擺攤玩家。");
                var pages = store.PendingPages(targets, forceDispatch: true);
                Need(pages.Length == 2 && pages.All(page => page.OwnerId == shopping.Id),
                    "擺攤玩家應只規劃買／賣各一頁。");
            });
            Check("shopping_only_force_pending_pages_skip_stopped_or_unknown_shop", () =>
            {
                var store = new ShopScanProgressStore(Path.Combine(root, "pending"));
                store.StartNew(source, targets with { Owners = [shopping] }, forceDispatch: true);
                var stopped = shopping with { ActionType = other.ActionType, ShopState = other.ShopState };
                Need(store.PendingPages(targets with { Owners = [stopped] }, forceDispatch: true).Length == 0,
                    "保存計畫的店主已停止擺攤，強制接續仍提供該玩家頁面。");
                var unconfirmed = shopping with { ActionType = null, ShopState = ShopEligibilityPolicy.Unknown,
                    ShopStateSampledAt = null, ShopStateEvidence = "" };
                Need(store.PendingPages(targets with { Owners = [unconfirmed] }, forceDispatch: true).Length == 0,
                    "保存計畫的店主擺攤狀態已未知，強制接續仍提供該玩家頁面。");
                Need(store.PendingPages(targets with { Owners = [shopping] }, forceDispatch: true).Length == 2,
                    "未送出頁在同身分玩家重新確認擺攤後應仍可查詢。");
            });
            var selected = new ReadSelection(["selfId", "position", "nearbyPlayers", "playerShopStates", "personalShopListing"]);
            var onlyShop = targets with { Owners = [shopping] };
            var sample = Sample(source, at, shopping);
            void RejectBeforeIntent(string name, ShopTargetSnapshot saved, Func<ShopDispatchSample> read,
                int expectedReads = 1, string? expectedCode = null, ReadSelection? selection = null,
                DateTimeOffset? publishAt = null)
            {
                Check(name, () =>
                {
                    var directory = Path.Combine(root, name);
                    var history = new ShopSearchedOwnerStore(Path.Combine(directory, "history"), Path.Combine(directory, "absent-import"));
                    var scans = new ShopScanProgressStore(Path.Combine(directory, "progress"));
                    scans.StartNew(source, onlyShop, forceDispatch: true);
                    var page = scans.PendingPages(onlyShop, forceDispatch: true).First();
                    scans.BeginAttempt(page, "offline-never-published");
                    int reads = 0, nativeCalls = 0;
                    Exception? rejected = null;
                    try
                    {
                        var requested = ShopForceDispatchPolicy.RequireSavedShoppingOwner(saved, shopping.Id, shopping.LocalIdentity);
                        var prepared = ShopForceDispatchPolicy.PrepareShoppingDispatch(() => { reads++; return read(); }, source,
                            selection ?? selected, saved, requested, () => at);
                        var beforePublish = publishAt ?? at;
                        prepared.RequireFresh(beforePublish);
                        var owner = ShopDispatchPolicy.RequireOwner(prepared.Context, saved, requested, beforePublish, requireDispatch: false);
                        history.Begin(source, owner, page.Direction, "offline-never-published", forceDispatch: true);
                        scans.NativeIntent("offline-never-published");
                        nativeCalls++; // Fake publisher only; no helper or game process exists in this test.
                    }
                    catch (Exception error) { rejected = error; }
                    Need(rejected is not null, "不符合擺攤／新鮮度條件的頁面竟然通過送出前檢查。");
                    if (expectedCode is not null)
                        Need(rejected is ShopRequestFailure failure && failure.Failure.Code == expectedCode,
                            $"拒絕分類應為 {expectedCode}，實際：{rejected}");
                    Need(reads == expectedReads && nativeCalls == 0,
                        $"送出前拒絕不得重採樣或進入原生派送：reads={reads}, nativeCalls={nativeCalls}。");
                    var summary = history.Summary(false, "offline");
                    Need(summary.OwnerCount == 0 && summary.SearchedPages == 0 && summary.UncertainPages == 0
                        && !history.BlocksSource(source) && !scans.BlocksSource(source),
                        "送出前拒絕被寫成已查紀錄或原生未明意圖。");
                    Need(scans.PendingPages(onlyShop, forceDispatch: true).Length == 2,
                        "送出前拒絕遺失尚未送出的買／賣頁面。");
                });
            }
            RejectBeforeIntent("shopping_only_saved_nonshop_rejected_before_read",
                onlyShop with { Owners = [shopping with { ActionType = other.ActionType, ShopState = other.ShopState }] },
                () => sample, 0, "TargetNotShoppingObserved");
            RejectBeforeIntent("shopping_only_saved_unknown_rejected_before_read",
                onlyShop with { Owners = [shopping with { ShopState = ShopEligibilityPolicy.Unknown }] },
                () => sample, 0, "TargetShopStateUnknown");
            RejectBeforeIntent("shopping_only_saved_forged_shop_evidence_rejected_before_read",
                onlyShop with { Owners = [shopping with { ShopStateEvidence = "unverified" }] },
                () => sample, 0, "TargetShopStateUnknown");
            RejectBeforeIntent("shopping_only_saved_wrong_action_type_rejected_before_read",
                onlyShop with { Owners = [shopping with { ActionType = other.ActionType }] },
                () => sample, 0, "TargetShopStateUnknown");
            RejectBeforeIntent("shopping_only_fresh_stopped_shop_rejected", onlyShop,
                () => Sample(source, at, shopping with { ActionType = other.ActionType, ShopState = other.ShopState }),
                expectedCode: "TargetNotShoppingObserved");
            RejectBeforeIntent("shopping_only_fresh_unknown_shop_rejected", onlyShop,
                () => Sample(source, at, shopping with { ShopState = ShopEligibilityPolicy.Unknown }),
                expectedCode: "TargetShopStateUnknown");
            RejectBeforeIntent("shopping_only_fresh_wrong_action_type_rejected", onlyShop,
                () => Sample(source, at, shopping with { ActionType = other.ActionType }),
                expectedCode: "TargetShopStateUnknown");
            RejectBeforeIntent("shopping_only_fresh_unclosed_action_evidence_rejected", onlyShop,
                () => Sample(source, at, shopping with { ShopStateEvidence = "not-AB-closed" }),
                expectedCode: "TargetShopStateUnknown");
            RejectBeforeIntent("shopping_only_fresh_owner_no_longer_nearby", onlyShop,
                () => Sample(source, at, shopping, nearbyOwners: []), expectedCode: "TargetNoLongerNearby");
            RejectBeforeIntent("shopping_only_fresh_reused_owner_identity_rejected", onlyShop,
                () => Sample(source, at, shopping with { LocalIdentity = "reused-owner" }), expectedCode: "TargetChanged");
            RejectBeforeIntent("shopping_only_fresh_changed_binding_source_rejected", onlyShop,
                () => sample with { Binding = sample.Binding with { Source = source with { CreationFileTime = 987654322 } } },
                expectedCode: "SourceMismatch");
            RejectBeforeIntent("shopping_only_fresh_changed_observation_source_rejected", onlyShop,
                () => sample with { Observations = sample.Observations with { Source = source with { CreationFileTime = 987654322 } } },
                expectedCode: "SourceMismatch");
            RejectBeforeIntent("shopping_only_fresh_changed_self_rejected", onlyShop,
                () => WithField(sample, "selfId", FieldReading.Observed(33UL, at)), expectedCode: "SourceMismatch");
            RejectBeforeIntent("shopping_only_disabled_shop_states_rejected_before_read", onlyShop,
                () => sample, 0, "ContextUnavailable", selected.With("playerShopStates", false));
            RejectBeforeIntent("shopping_only_disabled_nearby_rejected_before_read", onlyShop,
                () => sample, 0, "ContextUnavailable", selected.With("nearbyPlayers", false));
            RejectBeforeIntent("shopping_only_fresh_missing_shop_states_rejected", onlyShop,
                () => WithField(sample, "playerShopStates", null));
            RejectBeforeIntent("shopping_only_fresh_missing_baseline_field_rejected", onlyShop,
                () => WithField(sample, "personalShopListing", null), expectedCode: "ContextUnavailable");
            RejectBeforeIntent("shopping_only_fresh_incomplete_nearby_rejected", onlyShop,
                () => Sample(source, at, shopping, nearbyComplete: false));
            RejectBeforeIntent("shopping_only_fresh_incomplete_shop_states_rejected", onlyShop,
                () => Sample(source, at, shopping, shopStatesComplete: false));
            RejectBeforeIntent("shopping_only_fresh_different_actor_state_round_rejected", onlyShop,
                () => WithField(sample, "playerShopStates", sample.Observations.Fields["playerShopStates"] with { SampledAt = at.AddTicks(-1) }));
            RejectBeforeIntent("shopping_only_fresh_expired_collection_rejected", onlyShop,
                () => Sample(source, at.AddMilliseconds(-HubProtocol.FreshMilliseconds - 1), shopping));
            RejectBeforeIntent("shopping_only_fresh_future_sample_rejected", onlyShop,
                () => Sample(source, at.AddTicks(1), shopping));
            RejectBeforeIntent("shopping_only_fresh_mismatched_baseline_time_rejected", onlyShop,
                () => sample with { Binding = sample.Binding with { SampledAt = at.AddTicks(-1) } }, expectedCode: "ContextUnavailable");
            RejectBeforeIntent("shopping_only_fresh_mismatched_baseline_owner_rejected", onlyShop,
                () => sample with { Binding = sample.Binding with { BaselineListing = sample.Binding.BaselineListing with { OwnerIdRaw = 99 } } },
                expectedCode: "ContextUnavailable");
            RejectBeforeIntent("shopping_only_fresh_mismatched_baseline_session_rejected", onlyShop,
                () => sample with { Binding = sample.Binding with { BaselineListing = sample.Binding.BaselineListing with { SourceSessionKey = "foreign-session" } } },
                expectedCode: "ContextUnavailable");
            RejectBeforeIntent("shopping_only_expired_before_intent_rejected_without_reread", onlyShop,
                () => sample, expectedCode: "ContextUnavailable", publishAt: at.AddMilliseconds(HubProtocol.FreshMilliseconds + 1));
            RejectBeforeIntent("shopping_only_read_failure_has_no_fallback_or_intent", onlyShop,
                () => throw new IOException("offline-fresh-read-failed"));
            RejectBeforeIntent("shopping_only_collection_change_exhaustion_has_no_intent", onlyShop,
                () => throw new ShopDispatchCollectionChangedException(source, "offline-collection-changed"));
            Check("shopping_only_fresh_shop_dispatch_ignores_legacy_permission_and_saved_age", () =>
            {
                var savedOwner = shopping with { ObservedAt = at.AddYears(-1), ShopStateSampledAt = at.AddYears(-1) };
                var saved = onlyShop with { Owners = [savedOwner], RefreshedAt = at.AddYears(-1), CanDispatch = false };
                int reads = 0;
                var prepared = ShopForceDispatchPolicy.PrepareShoppingDispatch(() => { reads++; return sample; }, source,
                    selected, saved, savedOwner, () => at);
                prepared.RequireFresh(at);
                var current = ShopDispatchPolicy.RequireOwner(prepared.Context, saved, savedOwner, at, requireDispatch: false);
                Need(reads == 1 && current.Id == shopping.Id && current.LocalIdentity == shopping.LocalIdentity
                    && current.ObservedAt == at && current.ShopStateSampledAt == at && ShopEligibilityPolicy.CanScan(current),
                    "直接模式未以一次全新擺攤觀測覆蓋舊保存時間，或恢復舊CanDispatch門檻。");
                try { ShopDispatchPolicy.RequireOwner(prepared.Context, saved, savedOwner, at); }
                catch (ShopRequestFailure failure) when (failure.Failure.Code == "TargetRefreshRequired") { return; }
                throw new InvalidOperationException("一般模式的預設刷新資格檢查被放寬。");
            });
        }
        finally { Directory.Delete(root, recursive: true); }
        return checks.ToArray();
    }

    private static ShopDispatchSample Sample(SourceIdentity source, DateTimeOffset at, MarketOwner shopOwner,
        MarketOwner[]? nearbyOwners = null, bool nearbyComplete = true, bool shopStatesComplete = true)
    {
        var owners = nearbyOwners ?? [shopOwner];
        var baseline = new PersonalShopListing { SourceSessionKey = source.SessionKey, SampledAt = at, OwnerIdRaw = 0 };
        var binding = new ShopActionSourceBinding(source, at, 0x300000, 0, 0x400000, 0x500000, 0x600000, 0x700000)
            { BaselineListing = baseline };
        var fields = new Dictionary<string, FieldReading>
        {
            ["selfId"] = FieldReading.Observed(11UL, at),
            ["position"] = FieldReading.Observed(new { x = 100, y = 100 }, at),
            ["nearbyPlayers"] = FieldReading.Observed(new { complete = nearbyComplete, items = owners.Select(owner => new
                { id = owner.Id, kind = "Player", localIdentity = owner.LocalIdentity, nameState = "Observed", name = owner.Name,
                    positionState = "Observed", position = owner.Position, sampledAt = at }).ToArray() }, at),
            ["playerShopStates"] = FieldReading.Observed(new { complete = shopStatesComplete, items = owners.Select(owner => new
                { id = owner.Id, localIdentity = owner.LocalIdentity, actionType = owner.ActionType, shopState = owner.ShopState,
                    shopStateSampledAt = at, shopStateEvidence = owner.ShopStateEvidence, shopStateDetail = "offline-closed-action" }).ToArray() }, at),
            ["personalShopListing"] = FieldReading.Observed(baseline, at)
        };
        return new(binding, new ReadResult(source, fields, "Reading", "offline-closed-sample"));
    }

    private static ShopDispatchSample WithField(ShopDispatchSample sample, string key, FieldReading? field)
    {
        var fields = new Dictionary<string, FieldReading>(sample.Observations.Fields);
        if (field is null) fields.Remove(key); else fields[key] = field;
        return sample with { Observations = sample.Observations with { Fields = fields } };
    }
}
