namespace LcPlayerMarket;

internal sealed record ItemFilterEdit(string? Name, bool IsChecked, bool ClearAll);

/// <summary>Independent exact-name OR filter. An empty selection accepts every item.</summary>
internal sealed class ItemFilterPanel : UserControl
{
    private readonly CheckedListBox items = new()
    {
        Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false,
        HorizontalScrollbar = true, ScrollAlwaysVisible = true, BorderStyle = BorderStyle.FixedSingle
    };
    private readonly Button all = new() { Text = "全部道具", Dock = DockStyle.Right, Width = 94, TabIndex = 0 };
    private readonly Label summary = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly HashSet<string> selected = new(StringComparer.Ordinal);
    private readonly HashSet<string> actualNames = new(StringComparer.Ordinal);
    private string[] names = [];
    private bool updating;
    public event Action<ItemFilterEdit>? FilterChanged;
    public string[] SelectedNames => selected.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    public bool Includes(string name) => selected.Count == 0 || selected.Contains(name);

    public ItemFilterPanel()
    {
        Dock = DockStyle.Top; Height = 140; Padding = new Padding(3, 1, 3, 5);
        var header = new Panel { Dock = DockStyle.Top, Height = 30 };
        header.Controls.Add(summary); header.Controls.Add(all);
        Controls.Add(items); Controls.Add(header);
        items.ItemCheck += (_, e) =>
        {
            if (updating || e.Index < 0 || items.Items[e.Index] is not string name || e.NewValue == e.CurrentValue) return;
            // ItemCheck precedes the control's CheckedItems change. Commit the
            // semantic selection now; observers never read that stale collection.
            if (e.NewValue == CheckState.Unchecked) selected.Remove(name); else selected.Add(name);
            UpdateSummary();
            // Publish the semantic edit immediately, preserving user order even
            // when several clicks arrive before the result tables are rebound.
            FilterChanged?.Invoke(new(name, e.NewValue != CheckState.Unchecked, false));
        };
        all.Click += (_, _) =>
        {
            ClearCheckedNames();
            // An already-clear side can still explicitly clear the other side.
            FilterChanged?.Invoke(new(null, false, true));
        };
        UpdateSummary();
    }

    public void Reset()
    {
        selected.Clear();
        SetNames([]);
        UpdateSummary();
    }

    public void SetNames(IEnumerable<string> catalogue)
    {
        actualNames.Clear();
        actualNames.UnionWith(catalogue.Where(name => !string.IsNullOrWhiteSpace(name)));
        // Retain checked names even if a later history-window update removes
        // their last row, but keep those retained filters separate from the
        // catalogue so synchronization cannot mistake them for available items.
        string[] next = actualNames.Concat(selected)
            .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.CurrentCulture)
            .ThenBy(name => name, StringComparer.Ordinal).ToArray();
        if (next.SequenceEqual(names, StringComparer.Ordinal)) { UpdateSummary(); return; }
        int oldTop = items.Items.Count == 0 ? 0 : items.TopIndex;
        string? topName = oldTop >= 0 && oldTop < items.Items.Count ? items.Items[oldTop] as string : null;
        string? currentName = items.SelectedItem as string;
        updating = true; items.BeginUpdate();
        try
        {
            items.Items.Clear(); names = next;
            foreach (string name in names) items.Items.Add(name, selected.Contains(name));
            if (names.Length > 0)
            {
                int current = currentName is null ? -1 : Array.IndexOf(names, currentName);
                if (current >= 0) items.SelectedIndex = current;
                int top = topName is null ? -1 : Array.IndexOf(names, topName);
                items.TopIndex = Math.Clamp(top >= 0 ? top : oldTop, 0, names.Length - 1);
            }
        }
        finally { items.EndUpdate(); updating = false; UpdateSummary(); }
    }

    public string ApplySynchronizedEdit(ItemFilterEdit edit)
    {
        if (edit.ClearAll)
        {
            bool changed = selected.Count > 0;
            ClearCheckedNames();
            return changed ? "AllCleared" : "AlreadyClear";
        }
        if (edit.Name is not { } name || !actualNames.Contains(name)) return "MissingFromCatalogue";
        if (selected.Contains(name) == edit.IsChecked) return "AlreadyMatched";
        int index = Array.IndexOf(names, name);
        if (index < 0) return "MissingFromCatalogue";
        if (edit.IsChecked) selected.Add(name); else selected.Remove(name);
        updating = true;
        try { items.SetItemChecked(index, edit.IsChecked); }
        finally { updating = false; UpdateSummary(); }
        // Programmatic synchronization never publishes a second user edit.
        return edit.IsChecked ? "ItemChecked" : "ItemUnchecked";
    }

    private void ClearCheckedNames()
    {
        selected.Clear(); updating = true;
        try
        {
            foreach (int index in items.CheckedIndices.Cast<int>().ToArray()) items.SetItemChecked(index, false);
        }
        finally { updating = false; UpdateSummary(); }
    }
    private void UpdateSummary() => summary.Text = selected.Count == 0
        ? $"已載入道具 {actualNames.Count:N0} 種｜全部顯示"
        : $"已載入道具 {actualNames.Count:N0} 種｜勾選 {selected.Count:N0} 種（符合任一）";
}
