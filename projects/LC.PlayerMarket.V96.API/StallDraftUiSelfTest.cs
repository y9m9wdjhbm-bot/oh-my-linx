using System.Text.Json;
namespace LcPlayerMarket;
internal static class StallDraftUiSelfTest
{
    internal static int Run(string directory)
    {
        if (Directory.Exists(directory)) throw new IOException("Evidence directory already exists."); Directory.CreateDirectory(directory);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
        var checks = new List<OfflineCheck>();
        void Need(string name, bool value) { checks.Add(new(name, value, "Isolated form; no backend constructed")); }
        var legacy = Path.Combine(directory, "stall-draft.json");
        var oldText = JsonSerializer.Serialize(new { Title = "舊草稿", RequestAutomatic = false, Items = new[] { new { Direction = StallDraft.Sell, ItemId = 1u, Name = "舊物品", Price = 2L, Quantity = 3 } } }); File.WriteAllText(legacy, oldText);
        using var host = new OffscreenHost(); using var form = new StallDraftForm(directory);
        form.TopLevel = false; host.Controls.Add(form); form.Show(); host.Show(); Application.DoEvents();
        Need("stall_ui_offscreen_fixture", Screen.AllScreens.All(screen => !screen.Bounds.IntersectsWith(host.Bounds)));
        form.LoadDraft(); var imported = form.CaptureDraft();
        Need("stall_ui_legacy_source_unknown_visible", imported.Items[0].Status == StallItemStatus.Unknown && imported.Items[0].SelectionSource == DraftSelectionSource.LegacyUnspecified);
        var selected = new StallDraft("不同狀態分價", true, [new(StallDraft.Sell, 1, "手動名稱", 3, 2, StallItemStatus.Normal, 11), new(StallDraft.Sell, 1, "手動名稱", 8, 1, StallItemStatus.Blessed, 12), new(StallDraft.Buy, 1, "手動名稱", 1, 5, StallItemStatus.Unknown)]);
        form.SetDraft(selected); form.SaveDraft();
        form.SetDraft(selected with { Title = "修改後再次保存" }); form.SaveDraft(); form.LoadDraft(); var after = form.CaptureDraft();
        Need("stall_ui_two_saves_and_load_preserve_status_uid_prices", after.Title == "修改後再次保存" && after.Items.SequenceEqual(selected.Items));
        Need("stall_ui_legacy_untouched_and_v2_backup_recoverable", File.ReadAllText(legacy) == oldText && Directory.GetFiles(directory, "stall-draft-v2.json.backup-*").Length == 1 && StallDraft.Load(Directory.GetFiles(directory, "stall-draft-v2.json.backup-*")[0]).Title == selected.Title);
        var result = new { success = checks.All(c => c.Passed), checks, passed = checks.Count(c => c.Passed), failed = checks.Count(c => !c.Passed), constructedGameBackends = 0, nativeCalls = 0, gameInputs = 0 };
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); return result.success ? 0 : 1;
    }
    private sealed class OffscreenHost : Form
    {
        internal OffscreenHost() { StartPosition = FormStartPosition.Manual; Location = new(-30000, -30000); Size = new(1200, 800); ShowInTaskbar = false; }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000; return cp; } }
    }
}
