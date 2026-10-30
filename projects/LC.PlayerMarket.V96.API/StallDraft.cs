using System.Text.Json;
using System.Text.Json.Serialization;

namespace LcPlayerMarket;

[JsonConverter(typeof(JsonStringEnumConverter<StallItemStatus>))]
public enum StallItemStatus { Unknown, Normal, Blessed, Cursed }
[JsonConverter(typeof(JsonStringEnumConverter<DraftSelectionSource>))]
public enum DraftSelectionSource { UserProvided, LegacyUnspecified }

public sealed record StallItem(string Direction, uint ItemId, string Name, long Price, int Quantity,
    StallItemStatus Status = StallItemStatus.Unknown, ulong? InventoryInstanceUid = null,
    DraftSelectionSource SelectionSource = DraftSelectionSource.UserProvided);

public sealed record StallDraft(string Title, bool RequestAutomatic, StallItem[] Items, int SchemaVersion = 2)
{
    public const string Buy = "\u8cb7", Sell = "\u8ce3";
    public string[] Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != 2) errors.Add("不支援此草稿格式版本。");
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 50 || Title.Any(char.IsControl)) errors.Add("標題需為 1 至 50 字且不含控制字元。");
        if (Items is null || Items.Length is < 1 or > 100) { errors.Add("草稿需有 1 至 100 列商品。"); return errors.ToArray(); }
        var instances = new HashSet<ulong>();
        var unbound = new HashSet<(string Direction, uint ItemId, StallItemStatus Status)>();
        for (int i = 0; i < Items.Length; i++)
        {
            var row = Items[i];
            if (row is null) { errors.Add($"第 {i + 1} 列 無效。"); continue; }
            if (row.Direction is not (Buy or Sell) || row.ItemId == 0 || string.IsNullOrWhiteSpace(row.Name)
                || row.Name.Length > 200 || row.Name.Any(char.IsControl) || row.Price is < 1 or > int.MaxValue || row.Quantity < 1)
                errors.Add($"第 {i + 1} 列 方向、ID、名稱、價格或數量無效。");
            if (!Enum.IsDefined(row.Status) || !Enum.IsDefined(row.SelectionSource)) errors.Add($"第 {i + 1} 列 狀態或來源選項無效。");
            if (row.SelectionSource == DraftSelectionSource.LegacyUnspecified && row.Status != StallItemStatus.Unknown)
                errors.Add($"第 {i + 1} 列 舊檔狀態需保留未知，請明確編輯後再保存。");
            if (row.InventoryInstanceUid is ulong uid)
            {
                if (uid == 0 || row.Direction != Sell) errors.Add($"第 {i + 1} 列 實例 UID 僅供賣出參照，需非零或留空。");
                if (!instances.Add(uid)) errors.Add($"第 {i + 1} 列 重複使用相同背包實例 UID。");
            }
            else if (!unbound.Add((row.Direction, row.ItemId, row.Status)))
                errors.Add($"第 {i + 1} 列 未綁定實例的同方向、物品 ID 與狀態重複。");
        }
        return errors.ToArray();
    }
    internal static string StatusLabel(StallItemStatus status) => status switch { StallItemStatus.Normal => "一般", StallItemStatus.Blessed => "祝福", StallItemStatus.Cursed => "詛咒", _ => "未知" };
    public string Preview()
    {
        var errors = Validate(); if (errors.Length != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return $"標題：{Title}\n自動擺攤請求：{(RequestAutomatic ? "已勾選；僅保存" : "未勾選")}\n" +
            string.Join("\n", Items.Select(row => $"{row.Direction} {row.Name} [ID {row.ItemId}；{StatusLabel(row.Status)}；來源：{(row.SelectionSource == DraftSelectionSource.LegacyUnspecified ? "舊檔未知" : "手動選擇")}；" +
                (row.InventoryInstanceUid is ulong uid ? $"實例參照 {uid}（待驗證）" : row.Direction == Sell ? "未綁定背包實例" : "買單條件，不需背包實例") +
                $"] 價格 {row.Price}；數量 {row.Quantity}")) +
            "\n\n名稱與狀態為草稿選擇，未經遊戲讀值驗證；未知不推為一般。實例 UID 僅為選用參照，物品所有權、可用數量與原生識別映射尚未核實。\n本工具不會對 LC 輸入、上架、買賣或實際擺攤。";
    }
    internal void Save(string path)
    {
        _ = Preview(); var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) { JsonSerializer.Serialize(file, this); file.Flush(true); }
            if (File.Exists(full)) { var backup = full + ".backup-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"); File.Replace(temporary, full, backup); }
            else File.Move(temporary, full, false);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void SaveNew(string path)
    {
        _ = Preview();
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            { JsonSerializer.Serialize(file, this); file.Flush(true); }
            File.Move(temporary, full, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static StallDraft Load(string path)
    {
        if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("草稿檔案超過 256 KiB。");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        StallDraft draft;
        if (!root.TryGetProperty("SchemaVersion", out var version))
        {
            // The released v1 schema never contained status or inventory identity fields.
            var legacy = JsonSerializer.Deserialize<LegacyDraft>(root.GetRawText()) ?? throw new InvalidDataException("舊版草稿格式無效。");
            draft = new(legacy.Title, legacy.RequestAutomatic, legacy.Items?.Select(row => row is null ? null! :
                new StallItem(row.Direction, row.ItemId, row.Name, row.Price, row.Quantity, StallItemStatus.Unknown,
                    null, DraftSelectionSource.LegacyUnspecified)).ToArray()!);
        }
        else
        {
            if (!version.TryGetInt32(out int schema) || schema != 2) throw new InvalidDataException("不支援此草稿格式版本。");
            draft = JsonSerializer.Deserialize<StallDraft>(root.GetRawText()) ?? throw new InvalidDataException("草稿格式無效。");
        }
        _ = draft.Preview();
        return draft;
    }
    private sealed record LegacyItem(string Direction, uint ItemId, string Name, long Price, int Quantity);
    private sealed record LegacyDraft(string Title, bool RequestAutomatic, LegacyItem[] Items);
}


internal sealed class StallDraftForm : Form
{
    private readonly TextBox title = new() { Width = 400, MaxLength = 50 };
    private readonly CheckBox automatic = new() { Text = "請求自動擺攤（目前只保存設定）", AutoSize = true };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly TextBox preview = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Bottom, Height = 180, ScrollBars = ScrollBars.Vertical };
    private readonly string path, legacyPath;
    private bool loading;
    internal StallDraftForm(string root)
    {
        path = Path.Combine(root, "stall-draft-v2.json"); legacyPath = Path.Combine(root, "stall-draft.json");
        Text = "擺攤設定／本地草稿預覽"; Size = new(1100, 720); MinimumSize = new(900, 600); StartPosition = FormStartPosition.CenterParent;
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 80, AutoScroll = true };
        top.Controls.AddRange([new Label { Text = "攤位標題", AutoSize = true }, title, automatic]);
        var direction = new DataGridViewComboBoxColumn { Name = "direction", HeaderText = "買賣方向" }; direction.Items.AddRange(StallDraft.Buy, StallDraft.Sell); grid.Columns.Add(direction);
        grid.Columns.Add("id", "物品 ID"); grid.Columns.Add("name", "名稱（手動／待核實）");
        var status = new DataGridViewComboBoxColumn { Name = "status", HeaderText = "狀態（選擇）" }; status.Items.AddRange("未知", "一般", "祝福", "詛咒"); grid.Columns.Add(status);
        grid.Columns.Add("uid", "實例 UID（賣出選填）"); grid.Columns.Add("price", "價格"); grid.Columns.Add("quantity", "數量");
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "source", HeaderText = "草稿來源", ReadOnly = true });
        grid.DefaultValuesNeeded += (_, e) => { e.Row.Cells["status"].Value = "未知"; e.Row.Cells["source"].Value = "手動"; };
        grid.CellValueChanged += (_, e) => { if (!loading && e.RowIndex >= 0 && e.ColumnIndex >= 0 && grid.Columns[e.ColumnIndex].Name != "source") grid.Rows[e.RowIndex].Cells["source"].Value = "手動"; };
        grid.DataError += (_, e) => { e.ThrowException = false; preview.Text = "買賣方向與狀態請從選單選擇。"; };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45 };
        void Button(string text, Action action) { var b = new Button { Text = text, AutoSize = true }; b.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(this, e.Message, "設定錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning); } }; buttons.Controls.Add(b); }
        Button("保存設定", SaveDraft); Button("載入設定", LoadDraft);
        Button("預覽 / dry-run", () => preview.Text = CaptureDraft().Preview().Replace("\n", "\r\n"));
        Button("安全停止", () => { automatic.Checked = false; preview.Text = "已取消自動擺攤請求；沒有遊戲動作。"; });
        Controls.Add(grid); Controls.Add(top); Controls.Add(preview); Controls.Add(buttons);
    }
    internal void SetDraft(StallDraft draft)
    {
        _ = draft.Preview(); loading = true;
        try { title.Text = draft.Title; automatic.Checked = draft.RequestAutomatic; grid.Rows.Clear();
            foreach (var row in draft.Items) grid.Rows.Add(row.Direction, row.ItemId, row.Name, StallDraft.StatusLabel(row.Status), row.InventoryInstanceUid?.ToString() ?? "", row.Price, row.Quantity, row.SelectionSource == DraftSelectionSource.LegacyUnspecified ? "舊檔未知" : "手動");
        } finally { loading = false; }
    }
    internal void SaveDraft() { var d = CaptureDraft(); d.Save(path); preview.Text = "已保存 v2 草稿；沒有遊戲動作。\r\n" + d.Preview().Replace("\n", "\r\n"); }
    internal void LoadDraft() { var selected = File.Exists(path) ? path : legacyPath; SetDraft(StallDraft.Load(selected)); preview.Text = "已載入本地草稿；沒有遊戲動作。\r\n" + CaptureDraft().Preview().Replace("\n", "\r\n"); }
    internal StallDraft CaptureDraft()
    {
        grid.EndEdit(); var items = new List<StallItem>();
        foreach (DataGridViewRow row in grid.Rows) { if (row.IsNewRow) continue;
            string Value(string key) => Convert.ToString(row.Cells[key].Value)?.Trim() ?? "";
            if (!uint.TryParse(Value("id"), out uint id) || !long.TryParse(Value("price"), out long price) || !int.TryParse(Value("quantity"), out int quantity)) throw new InvalidDataException($"第 {row.Index + 1} 列的 ID、價格、數量需為有效整數。");
            ulong? uid = null; if (Value("uid") is string u && u.Length > 0) { if (!ulong.TryParse(u, out var parsed) || parsed == 0) throw new InvalidDataException($"第 {row.Index + 1} 列的實例 UID 需為非零整數或留空。"); uid = parsed; }
            var state = Value("status") switch { "" or "未知" => StallItemStatus.Unknown, "一般" => StallItemStatus.Normal, "祝福" => StallItemStatus.Blessed, "詛咒" => StallItemStatus.Cursed, _ => throw new InvalidDataException("狀態選擇無效。") };
            items.Add(new(Value("direction"), id, Value("name"), price, quantity, state, uid, Value("source") == "舊檔未知" ? DraftSelectionSource.LegacyUnspecified : DraftSelectionSource.UserProvided));
        }
        var draft = new StallDraft(title.Text.Trim(), automatic.Checked, items.ToArray()); _ = draft.Preview(); return draft;
    }
}
