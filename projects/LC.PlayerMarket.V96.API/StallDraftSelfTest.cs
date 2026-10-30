namespace LcPlayerMarket;
internal static class StallDraftSelfTest
{
    internal static IEnumerable<OfflineCheck> Run()
    {
        var draft = new StallDraft("測試攤位", true, [new(StallDraft.Sell, 1, "測試名稱", 2, 3)]);
        yield return new("stall_valid_draft_preview_has_unverified_execution_notice", draft.Validate().Length == 0 && draft.Preview().Contains("不會對 LC"), "");
        yield return new("stall_reject_negative_price", (draft with { Items = [new(StallDraft.Sell, 1, "物品", -1, 3)] }).Validate().Length > 0, "");
        yield return new("stall_reject_zero_quantity", (draft with { Items = [new(StallDraft.Buy, 1, "物品", 2, 0)] }).Validate().Length > 0, "");
        yield return new("stall_reject_duplicate_direction_id", (draft with { Items = [draft.Items[0], draft.Items[0]] }).Validate().Length > 0, "");
        yield return new("stall_reject_invalid_title", (draft with { Title = "\n" }).Validate().Length > 0, "");
        var root = Path.Combine(Path.GetTempPath(), "LC-StallDraft-Test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            string file = Path.Combine(root, "draft.json"); draft.Save(file); var loaded = StallDraft.Load(file);
            yield return new("stall_save_load_roundtrip_without_game_entry", loaded.Title == draft.Title && loaded.Items.SequenceEqual(draft.Items) && loaded.RequestAutomatic, "");
            var row = draft.Items[0] with { Status = StallItemStatus.Normal };
            var split = draft with { Items = [row, row with { Status = StallItemStatus.Blessed, Price = 7 }, row with { Status = StallItemStatus.Cursed, Price = 1 }] };
            yield return new("stall_v2_same_id_different_status_independent_price", split.Validate().Length == 0, "");
            yield return new("stall_v2_same_uid_changed_status_rejected", (draft with { Items = [row with { InventoryInstanceUid = 11 }, row with { InventoryInstanceUid = 11, Status = StallItemStatus.Blessed }] }).Validate().Length > 0, "");
            yield return new("stall_v2_different_uid_same_id_allowed", (draft with { Items = [row with { InventoryInstanceUid = 11 }, row with { InventoryInstanceUid = 12 }] }).Validate().Length == 0, "");
            yield return new("stall_v2_buy_no_uid_and_sell_unbound", (draft with { Items = [row with { Direction = StallDraft.Buy }] }).Validate().Length == 0 && draft.Preview().Contains("未綁定背包實例"), "");
            yield return new("stall_v2_buy_uid_and_zero_uid_rejected", (draft with { Items = [row with { Direction = StallDraft.Buy, InventoryInstanceUid = 11 }] }).Validate().Length > 0 && (draft with { Items = [row with { InventoryInstanceUid = 0 }] }).Validate().Length > 0, "");
            yield return new("stall_v2_unknown_retained_and_invalid_enum_rejected", draft.Items[0].Status == StallItemStatus.Unknown && draft.Preview().Contains("未知") && (draft with { Items = [row with { Status = (StallItemStatus)99 }] }).Validate().Length > 0, "");
            var legacy = Path.Combine(root, "legacy.json"); var oldText = System.Text.Json.JsonSerializer.Serialize(new { draft.Title, draft.RequestAutomatic, Items = new[] { new { row.Direction, row.ItemId, row.Name, row.Price, row.Quantity } } }); File.WriteAllText(legacy, oldText);
            var migrated = StallDraft.Load(legacy); migrated.Save(Path.Combine(root, "v2.json"));
            yield return new("stall_v2_legacy_load_unknown_preserves_file", migrated.Items[0].Status == StallItemStatus.Unknown && migrated.Items[0].SelectionSource == DraftSelectionSource.LegacyUnspecified && File.ReadAllText(legacy) == oldText, "");
            split.Save(file); var backups = Directory.GetFiles(root, "draft.json.backup-*");
            yield return new("stall_v2_repeated_atomic_save_keeps_recoverable_backup", StallDraft.Load(file).Items.SequenceEqual(split.Items) && backups.Length == 1 && StallDraft.Load(backups[0]).Items.SequenceEqual(draft.Items), "");
            yield return new("stall_v2_null_row_rejected", (draft with { Items = [null!] }).Validate().Length > 0, "");
        } finally { Directory.Delete(root, true); }
    }
}
