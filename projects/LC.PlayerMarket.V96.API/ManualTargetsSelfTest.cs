using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Saved data only. Does not instantiate UI, IPC, runtime or game access.</summary>
internal static class ManualTargetsSelfTest
{
    public static OfflineCheck[] Run()
    {
        var result = new List<OfflineCheck>();
        var source = new SourceIdentity(123, 999, "offline-only", "0x10000", 1, 65536, "fixed-hash", "session-A");
        var old = DateTimeOffset.UtcNow.AddYears(-1);
        var owner = new MarketOwner { Id = 22, LocalIdentity = "local-owner-A", Name = "離線測試", Position = new(100, 105), ObservedAt = old };
        var row = new PersonalShopRow(0, "Observed", true, "測試商品", "5", 5, "每個", null, "Unknown", "test");
        var listing = new MarketListing { Revision = "history-A", SourceSessionKey = source.SessionKey, Owner = owner,
            CapturedAt = old, OwnerAndResponseMatched = true, Complete = true, ViewerDirection = ShopPageModes.ViewerBuy, Rows = [row] };
        ManualNearbyContext Saved() => new("snapshot-A", source, old, new(100, 100), [owner], true);
        void Check(string name, Action action)
        {
            try { action(); result.Add(new(name, true, "通過")); }
            catch (Exception error) { result.Add(new(name, false, error.Message)); }
        }
        static void Need(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        Check("manual_snapshot_no_age_expiry", () => Need(Saved().CanUse(listing), "一年前明確刷新且來源相同，不應因 TTL 失效"));
        Check("manual_snapshot_keeps_original_sample_time", () =>
        {
            var saved = Saved(); Need(saved.RefreshedAt == old && saved.Match(listing)?.At == old, "原始刷新與觀測時間不得被更新");
        });
        Check("manual_snapshot_ignores_temporary_missing_source", () =>
        {
            var saved = Saved(); saved.ObserveSource(null); Need(saved.CanUse(listing), "暫無背景快照不應刪除固定目標");
        });
        Check("manual_snapshot_same_source_refresh_does_not_replace_owners", () =>
        {
            var saved = Saved(); saved.ObserveSource(source with { }); Need(saved.Count == 1 && saved.CanUse(listing), "背景同來源更新不應更換候選");
        });
        Check("manual_snapshot_owns_its_candidate_array", () =>
        {
            MarketOwner[] values = [owner]; var saved = new ManualNearbyContext("copy-A", source, old, new(100, 100), values);
            values[0] = owner with { LocalIdentity = "different" }; Need(saved.CanUse(listing), "外部集合變動影響了保存快照");
        });
        Check("manual_snapshot_rejects_actual_restart_even_same_session_text", () =>
        {
            var saved = Saved(); saved.ObserveSource(source with { CreationFileTime = 1000 }); Need(!saved.CanUse(listing) && saved.Invalidated, "程序重開仍沿用舊 ID");
        });
        Check("manual_snapshot_changed_session_requires_manual_refresh", () =>
        {
            var saved = Saved(); saved.ObserveSession("session-B"); saved.ObserveSource(source);
            Need(saved.Invalidated && !saved.CanUse(listing), "回到舊來源不應自動恢復失效的目標快照");
        });
        Check("manual_snapshot_rejects_reused_id_different_identity", () => Need(!Saved().CanUse(listing with { Owner = owner with { LocalIdentity = "other-object" } }), "只比數字 ID 會誤綁"));
        Check("manual_snapshot_rejects_old_source_listing", () => Need(!Saved().CanUse(listing with { SourceSessionKey = "other-session" }), "不同來源歷史被當目前操作"));
        Check("manual_snapshot_keeps_unverified_listing_non_actionable", () => Need(!Saved().CanUse(listing with { OwnerAndResponseMatched = false }), "未核對回覆被假裝成功"));
        Check("manual_snapshot_row_marks_refresh_distance", () =>
        {
            var saved = Saved(); var view = new MarketRowView(listing, row, saved.Match(listing), saved.SelfPosition, true, true);
            Need(view.NumericDistance == 5 && view.Distance.Contains("刷新時") && view.Status.Contains("手動"), "舊座標冒充即時位置");
            Need(view.CapturedAt == old && view.Quantity == "未知", "資料时间或未知數量被改寫");
        });
        Check("manual_snapshot_rejects_duplicate_candidates", () =>
        {
            try { _ = new ManualNearbyContext("duplicate", source, old, null, [owner, owner]); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("重複店主沒有拒絕");
        });
        var persisted = new ShopTargetSnapshot { SnapshotId = "persisted-A", SourceSessionKey = source.SessionKey,
            RefreshedAt = old, SelfId = 11, SelfPosition = new(100, 100), Owners = [owner] };
        var state = new ShopCommandResponse { State = "Idle", CurrentSourceSessionKey = source.SessionKey, TargetSnapshot = persisted };
        Check("manual_restore_getstate_keeps_original_sample", () =>
        {
            var restored = ManualNearbyContext.RestoreSaved(null, source, state);
            Need(restored is not null && restored.SnapshotId == persisted.SnapshotId && restored.RefreshedAt == old
                && restored.Match(listing)?.At == old && restored.CanUse(listing), "GetState 未恢復原保存資料，或擅自更新時間");
        });
        Check("manual_restore_waits_for_source_then_succeeds", () =>
        {
            Need(ManualNearbyContext.RestoreSaved(null, null, state) is null, "來源未就緒仍恢復舊 ID");
            Need(ManualNearbyContext.RestoreSaved(null, source, state)?.CanUse(listing) == true, "下一輪來源就緒後無法恢復");
        });
        Check("manual_restore_rejects_broker_session_mismatch", () =>
            Need(ManualNearbyContext.RestoreSaved(null, source, state with { CurrentSourceSessionKey = "other" }) is null, "不同服務來源仍恢復"));
        Check("manual_restore_rejects_saved_session_mismatch", () =>
            Need(ManualNearbyContext.RestoreSaved(null, source, state with { TargetSnapshot = persisted with { SourceSessionKey = "old-session" } }) is null,
                "舊 LC 來源快照被套用到新來源"));
        Check("manual_restore_never_replaces_existing_context", () =>
        {
            var existing = Saved();
            Need(ManualNearbyContext.RestoreSaved(existing, source, state) is null && existing.SnapshotId == "snapshot-A"
                && existing.RefreshedAt == old, "背景 GetState 自動取代已採納快照");
            existing.Invalidate("source changed");
            Need(ManualNearbyContext.RestoreSaved(existing, source, state) is null && existing.Invalidated,
                "已失效快照在背景被自動恢復");
        });
        Check("manual_restore_rejects_malformed_saved_candidates", () =>
            Need(ManualNearbyContext.RestoreSaved(null, source, state with { TargetSnapshot = persisted with { Owners = [owner, owner] } }) is null,
                "服務保存的重複候選未被拒絕"));
        Check("legacy_owner_remains_navigable_but_cannot_open_without_shop_evidence", () =>
        {
            var saved = Saved();
            Need(saved.Count == 1 && saved.CanUse(listing) && saved.Match(listing)?.Position == owner.Position,
                "未確認擺攤的舊紀錄不應失去歷史與保存座標");
            Need(!saved.CanOpenShop(listing, out var reason) && reason.Contains("尚未確認")
                && saved.ShopCounts.UnknownShopStateCount == 1, "舊格式缺少狀態時不得冒充確認擺攤或非擺攤");
        });
        Check("manual_shop_observations_partition_without_expiring_or_replacing_owners", () =>
        {
            var shopping = owner with { ShopState = ShopEligibilityPolicy.ShopActionObserved, ActionType = ".?AVShopAction@lineage@@",
                ShopStateSampledAt = old, ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence };
            var other = shopping with { Id = 23, LocalIdentity = "local-owner-B", ShopState = ShopEligibilityPolicy.KnownOtherActionObserved, ActionType = ".?AVBreathAction@lineage@@" };
            var unknown = owner with { Id = 24, LocalIdentity = "local-owner-C" };
            var saved = new ManualNearbyContext("states-A", source, old, null, [shopping, other, unknown], true);
            Need(saved.Count == 3 && saved.ShopCounts == new ShopEligibilityCounts(1, 1, 1), "三類狀態沒有保留完整名單");
            Need(saved.CanOpenShop(listing, out _), "本視窗已採納名單應可請求服務重新核對目前店主狀態");
            Need(!saved.CanOpenShop(listing with { Owner = other }, out var reason) && reason.Contains("非擺攤動作"),
                "已知其他動作仍送出開店，或原因誤報為未知");
            Need(saved.RefreshedAt == old, "讀取資格時刷新了觀測時間");
        });
        Check("shop_open_uses_saved_owner_evidence_instead_of_historical_listing_flag", () =>
        {
            var historical = owner with { ShopState = ShopEligibilityPolicy.ShopActionObserved, ActionType = ".?AVShopAction@lineage@@",
                ShopStateSampledAt = old, ShopStateEvidence = ShopEligibilityPolicy.ActionEvidence };
            Need(!Saved().CanOpenShop(listing with { Owner = historical }, out _), "舊商品頁中的擺攤旗標繞過本次保存周遭狀態");
        });
        Check("shop_local_capability_ignores_service_version_but_requires_enabled_supported_field", () =>
        {
            var local = new HubSnapshot { ServiceVersion = "", Fields = new() { ["playerShopStates"] = FieldReading.Unknown("manual") } };
            Need(PlayerShopStatePresentation.ServiceReady(local, out _), "本機讀取能力仍被外部服務版本標示阻擋");
            Need(PlayerShopStatePresentation.ServiceReady(local with { ServiceVersion = "1.0.0" }, out _), "服務版本標示改變了本機欄位能力");
            Need(!PlayerShopStatePresentation.ServiceReady(local with { Fields = new() }, out var reason)
                && reason.Contains("尚未提供"), "缺少擺攤欄位仍通過本機能力檢查");
            var disabled = local with { Fields = new() { ["playerShopStates"] = FieldReading.Disabled() } };
            Need(!PlayerShopStatePresentation.ServiceReady(disabled, out reason) && reason.Contains("已停用"), "關閉讀取後仍允許開始開店");
            var unsupported = local with { Fields = new() { ["playerShopStates"] = FieldReading.Unknown("offline") with { State = "Unsupported" } } };
            Need(!PlayerShopStatePresentation.ServiceReady(unsupported, out _), "未支援的擺攤欄位仍通過本機能力檢查");
        });
        Check("shop_manual_mode_can_refresh_before_first_sample_and_after_background_expiry", () =>
        {
            var manual = new HubSnapshot { ServiceVersion = "", Fields = new() { ["playerShopStates"] = FieldReading.Unknown("等待手動刷新") } };
            Need(PlayerShopStatePresentation.ServiceReady(manual, out _), "手動模式的 Unknown 造成第一次刷新永遠無法開始");
            var stale = manual with { Fields = new() { ["playerShopStates"] = FieldReading.Unknown("stale") with { State = "Stale", SampledAt = old } } };
            Need(PlayerShopStatePresentation.ServiceReady(stale, out _), "背景資料 TTL 使保存周遭或手動刷新被拒絕");
        });
        Check("native_build_identity_remains_required_without_service_version", () =>
        {
            var supported = source with { DiskSha256 = MarketNativeCompatibility.SupportedSha256,
                PeTimestamp = MarketNativeCompatibility.SupportedTimestamp, ImageSize = MarketNativeCompatibility.SupportedImageSize };
            Need(MarketNativeCompatibility.Assess(supported).CanSend, "正確 LC 完整身分仍需要外部服務版本");
            Need(!MarketNativeCompatibility.Assess(supported with { DiskSha256 = new string('0', 64) }).CanSend,
                "移除服務門檻同時放寬了 LC SHA 核對");
            Need(!MarketNativeCompatibility.Assess(supported with { PeTimestamp = 0 }).CanSend
                && !MarketNativeCompatibility.Assess(supported with { ImageSize = 0 }).CanSend,
                "移除服務門檻同時放寬了 LC PE 核對");
        });
        Check("native_v15_candidate_requires_current_build_and_complete_package", () =>
        {
            var current = source with { DiskSha256 = MemoryHub.Service.ShopNativePackage.SupportedSha,
                PeTimestamp = MemoryHub.Service.ShopNativePackage.SupportedTimestamp, ImageSize = MemoryHub.Service.ShopNativePackage.SupportedImageSize };
            Need(MarketNativeCompatibility.Assess(current).CanSend, "Verified current LC rejected");
            var legacy = current with { DiskSha256 = "9FFC24FE304AACBB98642363F7B19FD4610B12D1FD0CB09A30C0C944F170B7AF",
                PeTimestamp = 0x6ABB9817, ImageSize = 0x3E95000 };
            Need(!MarketNativeCompatibility.Assess(legacy).CanSend, "V15 candidate accepted the historical V13 build");
            var obsolete = current with { DiskSha256 = "618714B0B4B9270C2253BB481AC559222E944D5C15E2B3095E8A3CCD927F0B3B", PeTimestamp = 0x6AB3585D, ImageSize = 0x3E9A000 };
            Need(!MarketNativeCompatibility.Assess(obsolete).CanSend, "Old LC accepted by new native package");
            string absent = Path.Combine(Path.GetTempPath(), "LC-missing-native-" + Guid.NewGuid().ToString("N"));
            var incompatible = MemoryHub.Service.ShopNativePackage.Check(obsolete, absent);
            Need(!incompatible.Ready && incompatible.Code == "NativeBuildUnsupported", "Old build dispatch not rejected");
            var missing = MemoryHub.Service.ShopNativePackage.Check(current, absent);
            Need(!missing.Ready && missing.Code == "NativeHelperMissing", "Missing current helper silently accepted");
            var complete = MemoryHub.Service.ShopNativePackage.Check(current, Path.Combine(AppContext.BaseDirectory, "ShopControl"));
            Need(complete.Ready, "Candidate V15 package failed its SHA manifest: " + complete.Detail);
        });
        return result.ToArray();
    }
}
