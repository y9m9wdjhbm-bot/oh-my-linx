using MemoryHub;

namespace LcPlayerMarket;

/// <summary>One direction, with a wide row-index scrollbar and stable refresh anchors.</summary>
internal sealed class MarketSideGrid : UserControl
{
    private sealed class WheelGrid : DataGridView
    {
        internal Action<int>? WheelRows;
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Shift) != 0)
            {
                int extent = Columns.GetColumnsWidth(DataGridViewElementStates.Visible);
                HorizontalScrollingOffset = Math.Clamp(HorizontalScrollingOffset - Math.Sign(e.Delta) * 60, 0, Math.Max(0, extent - ClientSize.Width));
                return;
            }
            WheelRows?.Invoke(e.Delta);
        }
    }

    private readonly WheelGrid table = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToOrderColumns = true,
        AllowUserToResizeRows = false, MultiSelect = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        ScrollBars = ScrollBars.Horizontal, RowTemplate = { Height = 28 }
    };
    private readonly VScrollBar fastScroll = new() { Dock = DockStyle.Right, Width = 32, Minimum = 0, SmallChange = 1 };
    private readonly Label title = new() { Dock = DockStyle.Top, Height = 38, TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Microsoft JhengHei UI", 11, FontStyle.Bold), Padding = new Padding(6, 0, 0, 0) };
    private readonly ItemFilterPanel itemFilter = new();
    private readonly string caption;
    private bool rendering, syncing, dragging;
    private MarketRowView[]? pendingRows;
    private int pendingTotal;
    public string Direction { get; }
    public DataGridView Grid => table;
    public event Action<MarketRowView>? RowSelected;
    public event Action<MarketRowView>? RowClicked;
    public event Action<MarketSortField>? HeaderClicked;
    public event Action<ItemFilterEdit>? ItemFilterChanged;
    public string[] SelectedItemNames => itemFilter.SelectedNames;
    public bool IncludesItem(string name) => itemFilter.Includes(name);
    public void SetItemNames(IEnumerable<string> names) => itemFilter.SetNames(names);
    public void ResetItems() => itemFilter.Reset();
    public string ApplySynchronizedItemEdit(ItemFilterEdit edit) => itemFilter.ApplySynchronizedEdit(edit);

    public MarketSideGrid(string direction, string caption, Color color)
    {
        Direction = direction; this.caption = caption; Dock = DockStyle.Fill; Margin = new Padding(3);
        title.Text = caption; title.BackColor = color;
        var body = new Panel { Dock = DockStyle.Fill };
        body.Controls.Add(table); body.Controls.Add(fastScroll);
        Controls.Add(body); Controls.Add(itemFilter); Controls.Add(title);
        itemFilter.FilterChanged += edit => ItemFilterChanged?.Invoke(edit);
        table.WheelRows = delta =>
        {
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines == 0) return;
            SetTop(CurrentTop() - Math.Sign(delta) * (lines < 0 ? VisibleRows() : lines));
        };
        table.Scroll += (_, _) => SyncScroll();
        table.Resize += (_, _) => SyncScroll();
        table.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex >= 0 && table.Columns[e.ColumnIndex].Tag is MarketSortField field && field != MarketSortField.None)
                HeaderClicked?.Invoke(field);
        };
        table.SelectionChanged += (_, _) =>
        {
            if (!rendering && table.CurrentRow?.Selected == true && table.CurrentRow.DataBoundItem is MarketRowView row)
                RowSelected?.Invoke(row);
        };
        table.CellMouseClick += (_, e) =>
        {
            if (!rendering && e.Button == MouseButtons.Left && e.Clicks == 1 && e.RowIndex >= 0 && e.ColumnIndex >= 0
                && table.Rows[e.RowIndex].DataBoundItem is MarketRowView row) RowClicked?.Invoke(row);
        };
        fastScroll.Scroll += (_, e) =>
        {
            if (syncing) return;
            dragging = e.Type == ScrollEventType.ThumbTrack;
            SetTop(e.NewValue);
            if (!dragging && pendingRows is { } deferred)
            { pendingRows = null; SetRows(deferred, pendingTotal); }
        };
    }

    public void ClearSelection() { rendering = true; try { table.ClearSelection(); } finally { rendering = false; } }
    public void SetRows(MarketRowView[] rows, int total)
    {
        title.Text = $"{caption}　{total:N0} 筆";
        if (dragging) { pendingRows = rows; pendingTotal = total; return; }
        int oldTop = CurrentTop(), horizontal = table.HorizontalScrollingOffset;
        string? topKey = oldTop < table.Rows.Count ? (table.Rows[oldTop].DataBoundItem as MarketRowView)?.Key : null;
        string? selectedKey = table.CurrentRow?.Selected == true ? (table.CurrentRow.DataBoundItem as MarketRowView)?.Key : null;
        rendering = true;
        try
        {
            table.DataSource = rows.ToList(); table.ClearSelection();
            foreach (DataGridViewRow visual in table.Rows)
            {
                if (visual.DataBoundItem is not MarketRowView row) continue;
                if (!row.Live) visual.DefaultCellStyle.ForeColor = Color.DimGray;
                if (row.Key == selectedKey) { table.CurrentCell = visual.Cells[0]; visual.Selected = true; }
                foreach (DataGridViewCell cell in visual.Cells) cell.ToolTipText = MarketItemStatePresentation.EvidenceTooltip(row.Row) + Environment.NewLine + row.Listing.Detail + Environment.NewLine + row.Row.Detail;
            }
            int anchored = topKey is null ? -1 : Array.FindIndex(rows, row => row.Key == topKey);
            SetTop(anchored >= 0 ? anchored : oldTop);
            table.HorizontalScrollingOffset = horizontal;
        }
        finally { rendering = false; SyncScroll(); }
    }

    private int CurrentTop() => Math.Max(0, table.FirstDisplayedScrollingRowIndex);
    private int VisibleRows() => Math.Max(1, table.DisplayedRowCount(false));
    private int MaxTop() => Math.Max(0, table.Rows.Count - VisibleRows());
    private void SetTop(int value)
    {
        if (table.Rows.Count > 0 && table.IsHandleCreated)
        {
            int target = Math.Clamp(value, 0, MaxTop());
            if (table.FirstDisplayedScrollingRowIndex != target) table.FirstDisplayedScrollingRowIndex = target;
        }
        SyncScroll();
    }
    private void SyncScroll()
    {
        if (syncing || IsDisposed) return;
        syncing = true;
        try
        {
            int visible = VisibleRows(), maxTop = MaxTop();
            // WinForms' reachable maximum is Maximum - LargeChange + 1.
            fastScroll.Maximum = maxTop + visible - 1;
            fastScroll.LargeChange = visible;
            fastScroll.Value = Math.Clamp(CurrentTop(), 0, maxTop);
            fastScroll.Enabled = maxTop > 0;
        }
        finally { syncing = false; }
    }
}
