namespace LcPlayerMarket;

/// <summary>Only explicit button clicks invoke the supplied capture/start/stop actions.</summary>
internal sealed class PatrolRouteForm : Form
{
    private readonly PatrolRouteStore store;
    private readonly Func<CancellationToken, Task<PatrolWaypoint>> capture;
    private readonly Func<PatrolRouteSnapshot, Task> start;
    private readonly Func<Task> stop;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DataGridView grid = new() { Name = "PatrolPointsGrid", Dock = DockStyle.Fill,
        ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AutoGenerateColumns = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        BackgroundColor = Color.White };
    private readonly Button captureButton = Button("加入目前位置", "CaptureCurrentPoint"), addButton = Button("手動新增", "AddPoint"),
        editButton = Button("編輯", "EditPoint"), removeButton = Button("刪除", "RemovePoint"),
        upButton = Button("上移", "MovePointUp"), downButton = Button("下移", "MovePointDown"),
        startButton = Button("開始巡迴", "StartPatrol"), stopButton = Button("停止巡迴", "StopPatrol");
    private readonly NumericUpDown retryLimit = new() { Name = "PatrolRetryLimit", Minimum = 0, Maximum = 10,
        Value = 3, Width = 70, Margin = new Padding(4, 7, 10, 3) };
    private readonly NumericUpDown roundCount = new() { Name = "PatrolRoundCount", Minimum = 1, Maximum = 100,
        Value = 1, Width = 70, Margin = new Padding(4, 7, 10, 3) };
    private readonly NumericUpDown arrivalRadius = new() { Name = "PatrolArrivalRadius",
        Minimum = PatrolRouteRules.MinimumArrivalRadius, Maximum = PatrolRouteRules.MaximumArrivalRadius,
        Value = PatrolRouteRules.DefaultArrivalRadius, Width = 70, Margin = new Padding(4, 7, 10, 3) };
    private readonly Label savedStatus = new() { Name = "PatrolSavedStatus", Dock = DockStyle.Fill,
        Height = 40, AutoEllipsis = true, Margin = new Padding(4) };
    private readonly Label runStatus = new() { Name = "PatrolRunStatus", Dock = DockStyle.Fill,
        Height = 64, AutoEllipsis = true, Margin = new Padding(4), Text = "尚未開始巡迴；重開程式只載入定位點，不會自動行走。" };
    private PatrolRouteSnapshot route;
    private readonly string? loadError;
    private bool running, busy, startPending, stopBusy, changingUi, lifetimeDisposed;
    private long runStateVersion;

    internal PatrolRouteForm(PatrolRouteStore store, Func<CancellationToken, Task<PatrolWaypoint>> capture,
        Func<PatrolRouteSnapshot, Task> start, Func<Task> stop)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        this.start = start ?? throw new ArgumentNullException(nameof(start));
        this.stop = stop ?? throw new ArgumentNullException(nameof(stop));
        Text = "定位點巡迴｜依清單搜尋商店"; Name = "PatrolRouteEditor";
        MinimumSize = new(960, 540); Size = new(1160, 680);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft JhengHei UI", 10);
        BindingContext = new BindingContext();
        var loaded = store.Load(); route = loaded.Snapshot ?? new(); loadError = loaded.Error;
        BuildLayout(); WireEvents(); RenderRoute(0);
        if (loadError is not null) SetStatus(loadError, true);
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var editCommands = Flow(); editCommands.Controls.AddRange([captureButton, addButton, editButton, removeButton, upButton, downButton]);
        AddColumn(nameof(WaypointRow.Order), "順序", 50, 55);
        AddColumn(nameof(WaypointRow.Name), "定位點名稱", 170, 150);
        AddColumn(nameof(WaypointRow.MapId), "地圖 ID", 120, 115);
        AddColumn(nameof(WaypointRow.MapName), "地圖名稱", 170, 150);
        AddColumn(nameof(WaypointRow.X), "X", 85, 80); AddColumn(nameof(WaypointRow.Y), "Y", 85, 80);
        var options = Flow(); options.Controls.AddRange([Label("巡迴輪數（1–100）"), roundCount, Label("可恢復錯誤：不限次數自動重試；停止可取消"),
            Label("到點容差（格）"), arrivalRadius, Label("X、Y 座標差皆不超過設定格數；完成設定輪數後停止。")]);
        retryLimit.Visible = false; options.Controls.Add(retryLimit);
        var runCommands = Flow(); runCommands.Controls.AddRange([startButton, stopButton]);
        layout.Controls.Add(editCommands, 0, 0); layout.Controls.Add(savedStatus, 0, 1);
        layout.Controls.Add(grid, 0, 2); layout.Controls.Add(options, 0, 3);
        layout.Controls.Add(runCommands, 0, 4); layout.Controls.Add(runStatus, 0, 5);
        Controls.Add(layout);
        var tips = new ToolTip { AutoPopDelay = 20000 };
        tips.SetToolTip(captureButton, "由目前導航來源取得已核對的地圖 ID 與座標；不使用地區名稱猜測地圖。");
        tips.SetToolTip(roundCount, "完整跑完所有定位點才計一輪；中途停止或失敗不計。輪數只控制巡迴，已查商人頁仍跳過。");
        tips.SetToolTip(retryLimit, "每輪每個定位點遇到允許接續的錯誤時，持續冷卻重試，不設次數上限；停止可取消。舊次數欄位僅保留路線相容性。");
        tips.SetToolTip(arrivalRadius, "預設 2 格，可設 1 至 10 格。格距取 X、Y 座標差的較大值（Chebyshev 距離），包含對角線；站定與清理完成後才能搜尋。");
        tips.SetToolTip(savedStatus, "新增、編輯、排序、刪除、輪數、接續次數與到點容差均自動保存為新版本；既有修訂保留。");
    }

    private void WireEvents()
    {
        captureButton.Click += async (_, _) => await CapturePointAsync();
        addButton.Click += (_, _) => EditPoint(false);
        editButton.Click += (_, _) => EditPoint(true);
        removeButton.Click += (_, _) => ChangeOrder(0, remove: true);
        upButton.Click += (_, _) => ChangeOrder(-1);
        downButton.Click += (_, _) => ChangeOrder(1);
        startButton.Click += async (_, _) => await StartAsync();
        stopButton.Click += async (_, _) => await StopAsync();
        grid.SelectionChanged += (_, _) => UpdateButtons();
        roundCount.ValueChanged += (_, _) =>
        {
            if (!changingUi && CanEdit) Persist(route.Points, route.RetryLimit, route.ArrivalRadius, SelectedIndex, (int)roundCount.Value);
        };
        retryLimit.ValueChanged += (_, _) =>
        {
            if (!changingUi && CanEdit) Persist(route.Points, (int)retryLimit.Value, route.ArrivalRadius, SelectedIndex);
        };
        arrivalRadius.ValueChanged += (_, _) =>
        {
            if (!changingUi && CanEdit) Persist(route.Points, route.RetryLimit, (int)arrivalRadius.Value, SelectedIndex);
        };
    }

    private bool CanEdit => loadError is null && !running && !busy && !startPending && !stopBusy;
    private int SelectedIndex => grid.CurrentRow?.Index ?? -1;

    private async Task CapturePointAsync()
    {
        if (!CanEdit || route.Points.Length >= PatrolRouteRules.MaximumPoints) return;
        busy = true; UpdateButtons(); SetStatus("正在讀取目前定位點…首次讀取需要初始化導航元件，可能較久，請稍候。");
        try
        {
            var point = await capture(lifetime.Token);
            if (IsDisposed || Disposing) return;
            if (running) { SetStatus("巡迴已開始，本次捕捉沒有更改清單。"); return; }
            PatrolRouteRules.ValidateWaypoint(point);
            Persist([.. route.Points, point], route.RetryLimit, route.ArrivalRadius, route.Points.Length);
        }
        catch (OperationCanceledException) { if (!IsDisposed) SetStatus("取得位置已取消；定位點未改變。"); }
        catch (Exception error) { if (!IsDisposed) SetStatus("無法加入目前位置；定位點未改變：" + error.Message, true); }
        finally { busy = false; if (!IsDisposed) UpdateButtons(); }
    }

    private void EditPoint(bool existing)
    {
        if (!CanEdit || !existing && route.Points.Length >= PatrolRouteRules.MaximumPoints) return;
        int selected = SelectedIndex;
        if (existing && (selected < 0 || selected >= route.Points.Length)) return;
        var initial = existing ? route.Points[selected] : new PatrolWaypoint(Guid.NewGuid().ToString("N"),
            $"定位點 {route.Points.Length + 1}", "", "", 0, 0);
        using var editor = new WaypointEditor(initial, existing);
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Result is not { } point || !CanEdit) return;
        var changed = route.Points.ToList();
        if (existing) changed[selected] = point; else { selected = changed.Count; changed.Add(point); }
        Persist(changed, route.RetryLimit, route.ArrivalRadius, selected);
    }

    private void ChangeOrder(int direction, bool remove = false)
    {
        if (!CanEdit) return;
        int index = SelectedIndex;
        if (index < 0 || index >= route.Points.Length) return;
        var changed = route.Points.ToList();
        if (remove) changed.RemoveAt(index);
        else
        {
            int next = index + direction;
            if (next < 0 || next >= changed.Count) return;
            (changed[index], changed[next]) = (changed[next], changed[index]); index = next;
        }
        Persist(changed, route.RetryLimit, route.ArrivalRadius, Math.Min(index, changed.Count - 1));
    }

    private void Persist(IReadOnlyList<PatrolWaypoint> points, int retries, int radius, int selected, int? rounds = null)
    {
        if (!store.TrySave(points, retries, radius, out var saved, out string error, expectedRevision: route.Revision, roundCount: rounds ?? route.RoundCount))
        {
            var latest = store.Load();
            if (latest.Success && latest.Snapshot!.Revision != route.Revision)
            {
                route = PatrolRouteRules.Copy(latest.Snapshot);
                RenderRoute(Math.Min(selected, route.Points.Length - 1));
                error += " 已重新載入最新清單；本次修改未套用，請確認後再操作。";
            }
            else
            {
                changingUi = true; retryLimit.Value = route.RetryLimit; arrivalRadius.Value = route.ArrivalRadius; roundCount.Value = route.RoundCount; changingUi = false;
            }
            SetStatus(error, true); UpdateButtons(); return;
        }
        route = saved!; RenderRoute(selected);
        SetStatus("定位點、輪數、接續次數與到點容差已自動保存；尚未啟動巡迴。");
    }

    private async Task StartAsync()
    {
        if (!CanEdit || route.Points.Length == 0) return;
        busy = startPending = true; long observed = runStateVersion;
        SetStatus("正在要求開始巡迴…"); UpdateButtons();
        try
        {
            PatrolRouteRules.Validate(route);
            await start(PatrolRouteRules.Copy(route));
            if (!IsDisposed && observed == runStateVersion) SetStatus("開始要求已處理，等待主視窗確認巡迴狀態。");
        }
        catch (Exception error) { if (!IsDisposed) SetStatus("巡迴要求未完成；未自動重送：" + error.Message, true); }
        finally { busy = startPending = false; if (!IsDisposed) UpdateButtons(); }
    }

    private async Task StopAsync()
    {
        if ((!running && !startPending) || stopBusy) return;
        stopBusy = true; SetStatus("正在要求停止巡迴，等待目前工作完成清理…"); UpdateButtons();
        try { await stop(); }
        catch (Exception error) { if (!IsDisposed) SetStatus("停止結果尚未確認；未自動重送：" + error.Message, true); }
        finally { stopBusy = false; if (!IsDisposed) UpdateButtons(); }
    }

    internal void SetRunState(bool isRunning, string status)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { BeginInvoke((Action)(() => SetRunState(isRunning, status))); return; }
        running = isRunning; runStateVersion++;
        SetStatus(status); UpdateButtons();
    }

    private void RenderRoute(int selected)
    {
        changingUi = true;
        try
        {
            roundCount.Value = route.RoundCount;
            retryLimit.Value = route.RetryLimit;
            arrivalRadius.Value = route.ArrivalRadius;
            grid.DataSource = route.Points.Select((point, index) => new WaypointRow(index + 1, point.Name,
                point.MapId, point.MapName, point.X, point.Y)).ToArray();
            if (selected >= 0 && selected < grid.Rows.Count) grid.CurrentCell = grid.Rows[selected].Cells[0];
            savedStatus.Text = loadError is not null ? "清單載入失敗；原檔保留，修改與啟動已停用。"
                : route.SavedAt == default ? "尚無定位點；請加入目前位置或手動新增，修改會立即保存。"
                : $"已保存 {route.Points.Length} 個定位點｜{route.RoundCount} 輪｜容差 {route.ArrivalRadius} 格｜{route.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}｜修改會另存新版本。";
        }
        finally { changingUi = false; UpdateButtons(); }
    }

    private void UpdateButtons()
    {
        bool edit = CanEdit, selected = SelectedIndex >= 0 && SelectedIndex < route.Points.Length;
        captureButton.Enabled = addButton.Enabled = edit && route.Points.Length < PatrolRouteRules.MaximumPoints;
        editButton.Enabled = removeButton.Enabled = edit && selected;
        upButton.Enabled = edit && selected && SelectedIndex > 0;
        downButton.Enabled = edit && selected && SelectedIndex < route.Points.Length - 1;
        retryLimit.Enabled = arrivalRadius.Enabled = roundCount.Enabled = edit;
        startButton.Enabled = edit && route.Points.Length > 0;
        stopButton.Enabled = (running || startPending) && !stopBusy;
    }

    private void SetStatus(string text, bool failed = false)
    { runStatus.Text = text; runStatus.ForeColor = failed ? Color.Firebrick : Color.DarkSlateBlue; }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !lifetimeDisposed) { lifetimeDisposed = true; lifetime.Cancel(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }

    private void AddColumn(string name, string title, int weight, int minimum) => grid.Columns.Add(new DataGridViewTextBoxColumn
        { DataPropertyName = name, Name = name, HeaderText = title, FillWeight = weight, MinimumWidth = minimum, SortMode = DataGridViewColumnSortMode.NotSortable });
    private static Button Button(string text, string name) => new() { Text = text, Name = name, AutoSize = true,
        Padding = new Padding(6, 3, 6, 3), Margin = new Padding(3, 3, 8, 3) };
    private static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(4, 9, 4, 5) };
    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
    private sealed record WaypointRow(int Order, string Name, string MapId, string MapName, int X, int Y);

    private sealed class WaypointEditor : Form
    {
        internal PatrolWaypoint? Result { get; private set; }

        internal WaypointEditor(PatrolWaypoint initial, bool existing)
        {
            Text = existing ? "編輯定位點" : "手動新增定位點";
            Font = new Font("Microsoft JhengHei UI", 10);
            FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false; ClientSize = new(510, 280);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < 6; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var name = new TextBox { Text = initial.Name, MaxLength = 80, Dock = DockStyle.Fill };
            var mapId = new TextBox { Text = initial.MapId, MaxLength = 128, Dock = DockStyle.Fill };
            var mapName = new TextBox { Text = initial.MapName, MaxLength = 128, Dock = DockStyle.Fill };
            var x = new NumericUpDown { Minimum = 0, Maximum = 65535, Value = initial.X, Dock = DockStyle.Fill };
            var y = new NumericUpDown { Minimum = 0, Maximum = 65535, Value = initial.Y, Dock = DockStyle.Fill };
            var controls = new Control[] { name, mapId, mapName, x, y };
            var titles = new[] { "定位點名稱", "地圖 ID（必填）", "地圖顯示名稱", "X 座標", "Y 座標" };
            for (int row = 0; row < controls.Length; row++) { layout.Controls.Add(Label(titles[row]), 0, row); layout.Controls.Add(controls[row], 1, row); }
            var ok = Button("保存", "SaveWaypoint"); var cancel = Button("取消", "CancelWaypoint"); cancel.DialogResult = DialogResult.Cancel;
            var actions = Flow(); actions.Controls.AddRange([ok, cancel]); layout.Controls.Add(actions, 1, 5);
            ok.Click += (_, _) =>
            {
                var next = initial with { Name = name.Text.Trim(), MapId = mapId.Text.Trim(), MapName = mapName.Text.Trim(), X = (int)x.Value, Y = (int)y.Value };
                try { PatrolRouteRules.ValidateWaypoint(next); Result = next; DialogResult = DialogResult.OK; }
                catch (Exception error) { MessageBox.Show(this, error.Message, "定位點尚未保存", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            };
            AcceptButton = ok; CancelButton = cancel; Controls.Add(layout);
        }
    }
}
