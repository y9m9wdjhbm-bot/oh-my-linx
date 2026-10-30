using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Read-only pages. Polling updates summaries without focusing the window or downloading the ledger.</summary>
internal sealed class SavedSearchRecordsForm : Form
{
    private readonly Label summary = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Margin = new Padding(6) };
    private readonly Label explanation = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Margin = new Padding(6),
        Text = "買入頁＝店主出售；賣出頁＝店主收購。已記錄略過／暫不在場不算本輪已送出；接續只查目前可安全送出的待查頁。" };
    private readonly DataGridView grid = NewGrid("SearchRecordGrid");
    private readonly DataGridView historyGrid = NewGrid("SearchedOwnersGrid");
    private readonly Label historySummary = new() { Name = "SearchHistorySummary", Dock = DockStyle.Fill, AutoEllipsis = true, Margin = new Padding(6) };
    private readonly Label pageStatus = new() { AutoSize = true, Margin = new Padding(6, 9, 3, 3), Text = "每頁最多 100 位；座標是記錄時的位置。" };
    private readonly Button previousPage = new() { Name = "HistoryPrevious", Text = "上一頁", AutoSize = true, Enabled = false };
    private readonly Button nextPage = new() { Name = "HistoryNext", Text = "下一頁", AutoSize = true, Enabled = false };
    private readonly Button reload = new() { Name = "HistoryReload", Text = "重新載入", AutoSize = true };
    private readonly TabControl pages = new() { Name = "SearchRecordPages", Dock = DockStyle.Fill };
    private readonly Func<ShopCommandRequest, CancellationToken, Task<ShopCommandResponse>>? readHistory;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<ulong?> pageCursors = [null];
    private int pageIndex;
    private ulong? nextCursor;
    private string? pageRevision, renderedVersion;
    private bool loading, pageStale;

    internal SavedSearchRecordsForm(Func<ShopCommandRequest, CancellationToken, Task<ShopCommandResponse>>? historyLoader = null)
    {
        readHistory = historyLoader;
        Text = "搜尋記錄｜本次進度與已搜尋擺攤";
        Name = "SavedSearchRecords"; StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new(900, 460); Size = new(1200, 650);
        Font = new Font("Microsoft JhengHei UI", 10); BindingContext = new BindingContext();
        var currentPage = new TabPage("本次搜尋進度");
        var ledgerPage = new TabPage("已搜尋擺攤（持久紀錄）");
        pages.TabPages.AddRange([currentPage, ledgerPage]);
        var layout = CreateLayout(48, 42);
        AddColumn(grid, nameof(SavedSearchRecordRow.OwnerId), "玩家 ID", 115);
        AddColumn(grid, nameof(SavedSearchRecordRow.OwnerName), "玩家名稱", 150);
        AddColumn(grid, nameof(SavedSearchRecordRow.BuyStatus), "買入頁（店主出售）", 200);
        AddColumn(grid, nameof(SavedSearchRecordRow.SellStatus), "賣出頁（店主收購）", 200);
        AddColumn(grid, nameof(SavedSearchRecordRow.BuyDetail), "買入頁說明", 260);
        AddColumn(grid, nameof(SavedSearchRecordRow.SellDetail), "賣出頁說明", 260);
        AddColumn(grid, nameof(SavedSearchRecordRow.UpdatedAt), "記錄更新時間", 170);
        layout.Controls.Add(summary, 0, 0); layout.Controls.Add(explanation, 0, 1); layout.Controls.Add(grid, 0, 2);
        currentPage.Controls.Add(layout);
        var historyLayout = CreateLayout(60, 38);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        toolbar.Controls.AddRange([previousPage, nextPage, reload, pageStatus]);
        AddColumn(historyGrid, nameof(SearchHistoryRecordRow.OwnerId), "玩家 ID", 115);
        AddColumn(historyGrid, nameof(SearchHistoryRecordRow.OwnerName), "玩家名稱", 155);
        AddColumn(historyGrid, nameof(SearchHistoryRecordRow.Position), "記錄座標", 120);
        AddColumn(historyGrid, nameof(SearchHistoryRecordRow.MapName), "記錄地圖", 130);
        AddColumn(historyGrid, nameof(SearchHistoryRecordRow.BuyStatus), "買入頁（店主出售）", 210);
        AddColumn(historyGrid, nameof(SearchHistoryRecordRow.SellStatus), "賣出頁（店主收購）", 210);
        AddColumn(historyGrid, nameof(SearchHistoryRecordRow.ObservedAt), "座標觀察時間", 175);
        AddColumn(historyGrid, nameof(SearchHistoryRecordRow.LastSearchedAt), "最後搜尋時間", 175);
        historyLayout.Controls.Add(historySummary, 0, 0); historyLayout.Controls.Add(toolbar, 0, 1); historyLayout.Controls.Add(historyGrid, 0, 2);
        ledgerPage.Controls.Add(historyLayout); Controls.Add(pages);
        reload.Click += async (_, _) => await ReloadHistoryAsync();
        previousPage.Click += async (_, _) => { if (pageIndex > 0) await LoadPageAsync(pageIndex - 1, pageCursors[pageIndex - 1], false); };
        nextPage.Click += async (_, _) => { if (nextCursor is { } next) await LoadPageAsync(pageIndex + 1, next, false); };
        pages.SelectedIndexChanged += async (_, _) => { if (Visible && pages.SelectedIndex == 1 && pageRevision is null) await ReloadHistoryAsync(); };
        Shown += async (_, _) => { if (pages.SelectedIndex == 1 && pageRevision is null) await ReloadHistoryAsync(); };
        FormClosed += (_, _) => lifetime.Cancel();
        Disposed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
        var tooltip = new ToolTip();
        summary.TextChanged += (_, _) => tooltip.SetToolTip(summary, summary.Text);
        historySummary.TextChanged += (_, _) => tooltip.SetToolTip(historySummary, historySummary.Text);
        pageStatus.TextChanged += (_, _) => tooltip.SetToolTip(pageStatus, pageStatus.Text);
        Disposed += (_, _) => tooltip.Dispose();
        UpdateHistorySummary(null);
    }

    private static DataGridView NewGrid(string name) => new() { Name = name, Dock = DockStyle.Fill, ReadOnly = true,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoGenerateColumns = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        BackgroundColor = Color.White, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None };
    private static TableLayoutPanel CreateLayout(int first, int second)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, first)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, second));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); return layout;
    }
    private static void AddColumn(DataGridView target, string property, string title, int width) => target.Columns.Add(new DataGridViewTextBoxColumn
        { DataPropertyName = property, Name = property, HeaderText = title, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable });

    internal void SelectHistoryTab() => pages.SelectedIndex = 1;
    internal Task ReloadHistoryAsync() => LoadPageAsync(0, null, true);
    internal Task LoadNextHistoryPageAsync() => nextCursor is { } next ? LoadPageAsync(pageIndex + 1, next, false) : Task.CompletedTask;

    private async Task LoadPageAsync(int targetIndex, ulong? cursor, bool reset)
    {
        if (loading || readHistory is null || IsDisposed || Disposing || lifetime.IsCancellationRequested || !reset && pageStale) return;
        var request = ShopSearchHistoryPresentation.ReadRequest(cursor, reset ? null : pageRevision);
        loading = true; UpdatePageButtons(); pageStatus.Text = "正在讀取本機搜尋紀錄…";
        try
        {
            var response = await readHistory(request, lifetime.Token);
            if (IsDisposed || Disposing || lifetime.IsCancellationRequested) return;
            ShopSearchHistoryPresentation.ValidatePage(request, response);
            if (reset) { pageCursors.Clear(); pageCursors.Add(null); }
            if (targetIndex == pageCursors.Count) pageCursors.Add(cursor);
            pageIndex = targetIndex; nextCursor = response.NextHistoryOwnerId; pageRevision = response.SearchHistory!.Revision; pageStale = false;
            historyGrid.DataSource = ShopSearchHistoryPresentation.Rows(response.SearchedOwners);
            UpdateHistorySummary(response.SearchHistory);
            pageStatus.Text = $"第 {pageIndex + 1} 頁，本頁 {response.SearchedOwners.Length} 位；座標為記錄位置。";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing) { pageStale = true; pageStatus.Text = error.Message + " 原顯示資料保留，請重新載入。"; }
        }
        finally { loading = false; if (!IsDisposed && !Disposing) UpdatePageButtons(); }
    }

    internal void UpdateHistorySummary(ShopSearchHistorySummary? value)
    {
        if (IsDisposed || Disposing) return;
        historySummary.Text = ShopSearchHistoryPresentation.Summary(value)
            + " 以本機玩家 ID／買賣頁去重，並非跨伺服器身分保證；重查前須清除紀錄。";
        if (value is not null && pageRevision is not null && value.Revision != pageRevision)
        { pageStale = true; pageStatus.Text = "紀錄已有更新；目前顯示原分頁，請按「重新載入」。"; }
        UpdatePageButtons();
    }

    private void UpdatePageButtons()
    {
        bool ready = !loading && readHistory is not null;
        reload.Enabled = ready; previousPage.Enabled = ready && !pageStale && pageIndex > 0;
        nextPage.Enabled = ready && !pageStale && nextCursor.HasValue;
    }

    internal void UpdateProgress(ShopSavedScanProgress? saved, string? currentSource)
    {
        if (IsDisposed || Disposing) return;
        summary.Text = SavedSearchPresentation.Summary(saved, currentSource);
        string version = saved is null ? "none" : $"{saved.PlanId}|{saved.UpdatedAt:O}|{saved.SearchedPages}|{saved.ConfirmedPages}|{saved.RemainingPages}|{saved.UncertainPages}|{saved.HistorySkippedPages}|{saved.AvailablePages}|{saved.DeferredPages}";
        if (version == renderedVersion) return;
        ulong? selected = grid.CurrentRow?.DataBoundItem is SavedSearchRecordRow row ? row.OwnerId : null;
        int first = grid.FirstDisplayedScrollingRowIndex, horizontal = grid.HorizontalScrollingOffset;
        grid.DataSource = SavedSearchPresentation.Rows(saved); renderedVersion = version;
        if (selected is not null)
            foreach (DataGridViewRow item in grid.Rows)
                if (item.DataBoundItem is SavedSearchRecordRow candidate && candidate.OwnerId == selected)
                { grid.CurrentCell = item.Cells[0]; break; }
        if (first >= 0 && first < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = first;
        grid.HorizontalScrollingOffset = horizontal;
    }
}
