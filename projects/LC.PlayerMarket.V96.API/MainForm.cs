using System.Globalization;
using MemoryHub;

namespace LcPlayerMarket;

internal sealed partial class MainForm : Form
{
    private readonly LocalStore store;
    private readonly MarketSessionLifecycle? sessionLifecycle;
    private string SessionRoot => sessionLifecycle?.RuntimeRoot ?? store.Root;
    private readonly AppLog log;
    private readonly StandaloneMarketBackend backend;
    private readonly NavigationOverlay overlay = new();
    private readonly SearchStatusOverlay searchOverlay = new();
    private readonly CheckBox searchOverlayEnabled = Check("遊戲內搜尋狀態", true);
    private SearchOverlayView? lastSearchOverlayView;
    private string lastSearchOverlayError = "";
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? connection;
    private readonly System.Windows.Forms.Timer frameTimer = new() { Interval = 100 };
    private readonly MarketListingIndex history = new();
    private readonly HashSet<string> liveKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> uncertainSources = new(StringComparer.Ordinal);
    private readonly TextBox keyword = Entry(160), ownerFilter = Entry(150), priceMin = Entry(95), priceMax = Entry(95), distanceMax = Entry(75), unitFilter = Entry(100);
    private readonly CheckBox viewerBuy = Check("店主出售（我買入）", true), viewerSell = Check("店主收購（我賣出）", true), showHistory = Check("顯示歷史", true);
    private readonly CheckBox synchronizeItemFilters = Check("買／賣品項同步", false);
    private readonly CheckBox logEnabled = Check("記錄 LOG", true), navigationEnabled = Check("導航標記", true), researchProjection = Check("研究投影（待比對）", false);
    private readonly ComboBox origin = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 176 };
    private readonly Button refreshNearby = Button("手動刷新周遭"), scan = Button("重新搜尋"), continueSaved = Button("接續搜尋"), pause = Button("暫停搜尋"), resume = Button("繼續暫停工作"), stop = Button("停止搜尋"), reconnect = Button("重新讀取"), clearTarget = Button("取消導航"), exportExcel = Button("匯出 Excel");
    private readonly Button searchRecords = Button("搜尋記錄");
    private readonly Label savedSearchStatus = Label("尚無保存的搜尋進度");
    private SavedSearchRecordsForm? recordsWindow;
    private readonly Button clearListings = Button("清除商品");
    private bool clearingListings;
    private readonly ComboBox[] sortFields = Enumerable.Range(0, 3).Select(_ => new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 128 }).ToArray();
    private readonly ComboBox[] sortDirections = Enumerable.Range(0, 3).Select(_ => new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 96 }).ToArray();
    private readonly Dictionary<string, string> baseColumnTitles = new(StringComparer.Ordinal);
    private readonly Label sourceStatus = Label("等待程式自行讀取 LC…"), nearbyStatus = Label("尚無周遭快照：請按「手動刷新周遭」"), scanStatus = Label("尚未搜尋商店"), filterStatus = Label(""), navigationStatus = Label("尚未選擇店主"), totals = Label("");
    private readonly Label shopStateStatus = Label("擺攤狀態尚未讀取；請手動刷新周遭，由本程式直接讀取 LC。");
    private readonly ListBox messages = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false };
    private readonly MarketSideGrid buyGrid = new(ShopPageModes.ViewerBuy, "我買入｜店主出售", Color.LightCyan);
    private readonly MarketSideGrid sellGrid = new(ShopPageModes.ViewerSell, "我賣出｜店主收購", Color.LemonChiffon);
    private readonly Label searchBadge = new() { AutoSize = true, Font = new Font("Microsoft JhengHei UI", 14, FontStyle.Bold),
        Padding = new Padding(10, 6, 10, 6), Margin = new Padding(3), Text = "狀態待確認", BackColor = Color.Gainsboro };
    private MarketScanState? lastConfirmedScan;
    private bool scanControlBusy;
    private int activeActionRequests, activeCancelRequests, activeScanControlRequests;
    private long refreshRequestTicket;
    private DateTimeOffset lastConfirmedScanAt;
    private string? lastSuccessfulAction;
    private string? lastSuccessfulActionJobId;
    private IEnumerable<MarketSideGrid> Sides => [buyGrid, sellGrid];
    private HubSnapshot? snapshot;
    private ManualNearbyContext? manualNearby;
    private ShopCommandResponse? broker;
    private ShopSavedScanProgress? lastKnownSavedScan;
    private ShopSavedScanProgress? CurrentSavedScan => broker?.SavedScan ?? lastKnownSavedScan;
    private ShopSearchHistorySummary? lastKnownSearchHistory;
    private ShopSearchHistorySummary? CurrentSearchHistory => broker?.SearchHistory ?? lastKnownSearchHistory;
    private ShopNavigationSnapshot? selectedNavigation;
    private bool navigationReadBusy;
    private DateTimeOffset nextNavigationReadAt;
    private long navigationReadTicket;
    private DateTimeOffset brokerReceivedAt;
    private string? previousSource, previousMap, previousMapId, previousInstance, currentTargetKey;
    private string? localReadFailure;
    private string lastConnectionMessage = "", lastScanMessage = "", lastNavigationMessage = "", lastNativeCompatibility = "";
    private int generation;
    private bool actionBusy, cancelBusy, changingUi, changingSortUi, renderingRows, closing, exporting, closeAfterExport;
    private bool itemFilterRefreshQueued;
    private bool closeCompleted, resourcesDisposed;
    private Task? closeTask;
    private MarketSortTerm[] sortTerms = [new(MarketSortField.Item, false), new(MarketSortField.Price, false)];
    private long lastAcceptedSequence = -1;

    public MainForm() : this(new LocalStore(), null, connectOnShown: true) { }

    // The layout fixture supplies both its isolated store and a fake backend.
    // Showing it never starts the main form's watcher, poller or frame timer.
    internal MainForm(LocalStore fixtureStore, StandaloneMarketBackend fixtureBackend)
        : this(fixtureStore, fixtureBackend ?? throw new ArgumentNullException(nameof(fixtureBackend)), connectOnShown: false) { }

    private MainForm(LocalStore localStore, StandaloneMarketBackend? suppliedBackend, bool connectOnShown)
    {
        StartupTiming.Mark("constructor-enter");
        store = localStore ?? throw new ArgumentNullException(nameof(localStore));
        if (connectOnShown && suppliedBackend is null) sessionLifecycle = MarketSessionLifecycle.BeginFresh(store);
        Text = "LC 玩家商店查詢 V96 API";
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1320, 980);
        MinimumSize = new Size(Math.Min(960, workingArea.Width), Math.Min(640, workingArea.Height));
        Size = new Size(Math.Min(1320, workingArea.Width), Math.Min(980, workingArea.Height));
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft JhengHei UI", 10);
        log = new(store.Root, store.DiagnosticsRoot);
        backend = suppliedBackend ?? new(Path.Combine(SessionRoot, "standalone"), sessionLifecycle?.DiagnosticsRoot);
        StartupTiming.Mark("backend-started");
        InitializePatrol();
        store.Error += error => Report("STORAGE_ERROR", error);
        log.Failure += error => { Report("LOG_ERROR", error, writeLog: false); changingUi = true; logEnabled.Checked = false; changingUi = false; };
        StartupTiming.Mark("layout-start"); BuildLayout(); StartupTiming.Mark("layout-end");
        var preferences = store.LoadPreferences();
        changingUi = true;
        logEnabled.Checked = preferences.LogEnabled; showHistory.Checked = preferences.ShowHistory;
        synchronizeItemFilters.Checked = preferences.SynchronizeItemFilters;
        navigationEnabled.Checked = preferences.ShowNavigation; researchProjection.Checked = preferences.ResearchProjection;
        searchOverlayEnabled.Checked = preferences.ShowSearchOverlay;
        origin.SelectedIndex = preferences.BottomOrigin ? 0 : 1;
        pageWaitMilliseconds.Value = preferences.PageOpenIntervalMilliseconds;
        noAdditionalPageWait.Checked = preferences.NoAdditionalPageWait;
        backend.ConfigurePageWaitMilliseconds(preferences.EffectivePageOpenIntervalMilliseconds);
        changingUi = false; log.SetEnabled(preferences.LogEnabled);
        if (!connectOnShown) { StartupTiming.Mark("history-start"); foreach (var value in store.LoadHistory()) history.Upsert(value); StartupTiming.Mark("history-end"); }
        Report("START", $"LC 玩家商店查詢 V96 API，只查詢已確認擺攤的玩家，到定位點取得一次已確認擺攤名單，後續依名單直接送出每一買／賣頁；本次商店與商品紀錄從空白開始，關閉保留商品給網頁，下一次開啟才清除。", new
        {
            store.Root, SessionRoot, DiagnosticBaseRoot = store.DiagnosticsRoot, AppLogRoot = log.DirectoryPath, DiagnosticsRoot = sessionLifecycle?.DiagnosticsRoot, FreshSession = sessionLifecycle is not null, executable = Environment.ProcessPath,
            assembly = typeof(MainForm).Assembly.Location, buildId = typeof(MainForm).Module.ModuleVersionId
        });
        WireEvents(); StartupTiming.Mark("rows-start"); RefreshRows(); StartupTiming.Mark("rows-end");
        if (connectOnShown) StartDebugApiV1();
        if (connectOnShown) Shown += (_, _) => { StartupTiming.Mark("shown"); StartConnection(); frameTimer.Start(); StartupTiming.Mark("history-ready"); };
        FormClosing += async (_, e) =>
        {
            if (closeCompleted) return;
            e.Cancel = true;
            if (exporting)
            {
                closeAfterExport = true;
                Report("EXCEL_EXPORT_CLOSE_DEFERRED", "Excel 正在保存；完成結果紀錄後會關閉程式。");
                return;
            }
            if (closeTask is not null) return;
            closeTask = CloseWorkAsync();
            await closeTask;
            if (closeCompleted && !IsDisposed) Close();
        };
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Name = "MainLayout", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 8), ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        // Keep the wrapping strips directly in a width-constrained root column.
        // A nested AutoSize/percent table previously measured one button per row
        // and reserved 516 pixels even after the actual strips laid out in two rows.
        var searchCommands = Flow(); searchCommands.Name = "PrimaryCommands";
        searchCommands.Controls.AddRange([startSavedPatrolButton, patrolButton, refreshNearby, scan, continueSaved, pause, resume, stop]);
        var pageWaitSettings = new FlowLayoutPanel
        {
            Name = "PageWaitSettings", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Margin = new Padding(3, 0, 8, 0), Padding = new Padding(0)
        };
        pageWaitSettings.Controls.AddRange([noAdditionalPageWait, Label("開頁間隔"), pageWaitMilliseconds, Label("毫秒")]);
        var utilityCommands = Flow(); utilityCommands.Name = "UtilityCommands";
        utilityCommands.Controls.AddRange([showListingsButton, searchRecords, resetSearchButton, clearSearchHistory, reconnect, clearTarget, clearListings, exportExcel, logEnabled, pageWaitSettings, checkNativeReadinessButton]);
        var importTemplateButton=Button("匯入地址模板" ); importTemplateButton.Name="ImportAddressTemplateButton";
        importTemplateButton.Click+=(_,_)=>ImportAddressTemplate(); utilityCommands.Controls.Add(importTemplateButton);
        resetSearchButton.Name = "ResetSearchButton";
        checkNativeReadinessButton.Name = "CheckNativeReadinessButton";
        checkNativeReadinessButton.Click += async (_, _) => await CheckNativeReadinessAsync(lifetime.Token);
        var stallDraftButton=Button("擺攤設定／預覽"); stallDraftButton.Name="StallDraftButton";  stallDraftButton.Click+=(_,_)=>{using var draft=new StallDraftForm(store.Root);draft.ShowDialog(this);};
        showListingsButton.Name = "ShowListingsButton"; showListingsButton.BackColor = Color.LightSteelBlue;
        showListingsButton.UseVisualStyleBackColor = false;
        patrolButton.Name = "PatrolSettingsButton"; patrolButton.BackColor = Color.LightSteelBlue;
        patrolButton.UseVisualStyleBackColor = false;
        var scanBanner = new TableLayoutPanel { Name = "SearchProgress", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
        scanBanner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30)); scanBanner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        scanBanner.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); scanBanner.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        searchBadge.AutoSize = false; searchBadge.AutoEllipsis = true; searchBadge.Dock = DockStyle.Fill;
        searchBadge.TextAlign = ContentAlignment.MiddleLeft;
        scanBanner.Controls.Add(searchBadge, 0, 0); scanBanner.Controls.Add(scanStatus, 1, 0);
        scanBanner.Controls.Add(savedSearchStatus, 0, 1); scanBanner.SetColumnSpan(savedSearchStatus, 2);
        savedSearchStatus.Dock = DockStyle.Fill;
        savedSearchStatus.AutoSize = false; savedSearchStatus.AutoEllipsis = true; savedSearchStatus.Margin = new Padding(3);
        scanStatus.AutoSize = false; scanStatus.Dock = DockStyle.Fill; scanStatus.AutoEllipsis = true; scanStatus.Margin = new Padding(6, 4, 6, 4);
        totals.AutoSize = false; totals.AutoEllipsis = true; totals.Dock = DockStyle.Fill;
        totals.Margin = new Padding(3, 2, 3, 2); totals.TextAlign = ContentAlignment.MiddleLeft;
        var pages = new TabControl { Name = "MarketPages", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        marketPages = pages;
        var listingsPage = new TabPage("買／賣商品") { Name = "ListingsPage", AutoScroll = true, Padding = new Padding(3),
            AutoScrollMinSize = new Size(760, 320) };
        var settingsPage = new TabPage("篩選與狀態") { Name = "FiltersAndStatusPage", AutoScroll = true, Padding = new Padding(6) };
        pages.TabPages.AddRange([listingsPage, settingsPage]);
        var stallPage=new TabPage("擺攤設定"){Padding=new Padding(12)};stallDraftButton.Dock=DockStyle.Top;stallPage.Controls.Add(stallDraftButton);pages.TabPages.Add(stallPage);
        var listingPanels = new TableLayoutPanel { Name = "ListingPanels", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = new Padding(0), MinimumSize = new Size(760, 320) };
        listingPanels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); listingPanels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        listingPanels.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        listingPanels.Controls.Add(buyGrid, 0, 0); listingPanels.Controls.Add(sellGrid, 1, 0);
        listingsPage.Controls.Add(listingPanels);
        var filters = Flow(); filters.Controls.AddRange([viewerBuy, viewerSell, showHistory, synchronizeItemFilters, Label("商品關鍵字"), keyword, Label("店主（逗號分隔可多選）"), ownerFilter]);
        var numbers = Flow(); numbers.Controls.AddRange([Label("價格下限"), priceMin, Label("上限"), priceMax, Label("計價單位"), unitFilter, Label("距離上限（格）"), distanceMax, Label("物品狀態"), itemStateFilter, groupStatePrices, filterStatus]);
        origin.Items.AddRange(["下方原點（歷史候選）", "上方原點（人工比對）"]);
        var navigation = Flow(); navigation.Controls.AddRange([searchOverlayEnabled, navigationEnabled, researchProjection, origin, navigationStatus]);
        var sorting = Flow();
        for (int i = 0; i < 3; i++)
        {
            sortFields[i].Items.AddRange(Enum.GetValues<MarketSortField>().Select(f => (object)new SortChoice(f, SortLabel(f))).ToArray());
            sortDirections[i].Items.AddRange(["升冪 ↑", "降冪 ↓"]);
            sorting.Controls.AddRange([Label($"第 {i + 1} 排序"), sortFields[i], sortDirections[i]]);
        }
        sorting.Controls.Add(Label("未知數值排最後；點表頭切換主排序"));
        SyncSortControls();
        var nearbyPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
        nearbyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nearbyPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); nearbyPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        nearbyStatus.Dock = DockStyle.Fill; shopStateStatus.Dock = DockStyle.Fill;
        nearbyStatus.Margin = new Padding(3, 3, 3, 1); shopStateStatus.Margin = new Padding(3, 1, 3, 3);
        nearbyPanel.Controls.Add(nearbyStatus, 0, 0); nearbyPanel.Controls.Add(shopStateStatus, 0, 1);
        var settings = new TableLayoutPanel { Name = "FilterSettings", Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 7, Margin = new Padding(0) };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int index = 0; index < 6; index++) settings.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        sourceStatus.Dock = DockStyle.Fill;
        settings.Controls.Add(filters, 0, 0); settings.Controls.Add(numbers, 0, 1); settings.Controls.Add(sorting, 0, 2);
        settings.Controls.Add(navigation, 0, 3); settings.Controls.Add(sourceStatus, 0, 4); settings.Controls.Add(nearbyPanel, 0, 5);
        settings.Controls.Add(messages, 0, 6); settingsPage.Controls.Add(settings);
        void ConstrainDetailText()
        {
            int width = Math.Max(160, settingsPage.ClientSize.Width - settingsPage.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 12);
            foreach (var label in new[] { sourceStatus, nearbyStatus, shopStateStatus, navigationStatus }) label.MaximumSize = new Size(width, 0);
        }
        settingsPage.SizeChanged += (_, _) => ConstrainDetailText();
        ConstrainDetailText();
        layout.Controls.Add(searchCommands, 0, 0); layout.Controls.Add(utilityCommands, 0, 1);
        layout.Controls.Add(pages, 0, 2); layout.Controls.Add(scanBanner, 0, 3); layout.Controls.Add(totals, 0, 4);
        AddColumn(nameof(MarketRowView.Item), "商品名稱", 240, MarketSortField.Item);
        AddColumn(nameof(MarketRowView.ItemKind), "物品狀態", 265, MarketSortField.ItemKind);
        AddColumn(nameof(MarketRowView.Price), "金額", 110, MarketSortField.Price);
        AddColumn(nameof(MarketRowView.Unit), "計價單位", 90);
        AddColumn(nameof(MarketRowView.Quantity), "數量", 90, MarketSortField.Quantity);
        AddColumn(nameof(MarketRowView.Owner), "店主", 120, MarketSortField.Owner);
        AddColumn(nameof(MarketRowView.Position), "店主座標", 132, MarketSortField.Position);
        AddColumn(nameof(MarketRowView.Distance), "距離", 112, MarketSortField.Distance);
        AddColumn(nameof(MarketRowView.Acquired), "取得時間", 180, MarketSortField.CapturedAt);
        AddColumn(nameof(MarketRowView.Status), "狀態", 190);
        Controls.Add(layout);
        ConfigureItemStateColumnLayout();
        InitializeItemStateControls();
        var tooltip = new ToolTip { AutoPopDelay = 20000 };
        tooltip.SetToolTip(itemStateFilter, MarketItemStatePresentation.ComparisonNote);
        tooltip.SetToolTip(groupStatePrices, "依買賣方向、商品、計價單位及有效分類分組後排價格；已確認未鑑定依設定歸一般，未知各自獨立。" + MarketItemStatePresentation.ComparisonNote);
        tooltip.SetToolTip(showListingsButton, "從篩選與狀態頁回到買／賣商品表格。切換分頁不會刪除商品、重設篩選或送出遊戲操作。");
        tooltip.SetToolTip(patrolButton, "開啟定位點清單：加入目前位置、手動新增、編輯及排序；設定後可開始巡迴一輪。");
        tooltip.SetToolTip(startSavedPatrolButton, "使用已保存的定位點與重試次數，依設定輪數開始巡迴；執行中可按此按鈕停止整個巡迴。F9 也可停止。");
        foreach (var statusLabel in new[] { scanStatus, savedSearchStatus, searchBadge, totals })
        {
            tooltip.SetToolTip(statusLabel, statusLabel.Text);
            statusLabel.TextChanged += (_, _) => tooltip.SetToolTip(statusLabel, statusLabel.Text);
        }
        foreach (var side in Sides) tooltip.SetToolTip(side.Grid, "滑鼠左鍵點選商品列，只對保存名單中已確認擺攤的店主送出一次開店指令；送出依已取得擺攤名單直接送出買／賣頁，不等待前一請求確認。鍵盤選列只更新導航。右側寬捲軸可快速移動；Shift＋滾輪或下方捲軸可水平移動。");
        tooltip.SetToolTip(searchOverlayEnabled, "在 LC 前景畫面左上角顯示目前店主、商店／頁面進度及搜尋狀態；滑鼠可直接穿透，切換視窗時自動隱藏。" );
        tooltip.SetToolTip(researchProjection, "研究投影使用本程式自行讀取的候選矩形；紅點是矩形中心，並非已驗證腳點。標記只顯示於 LC 前景，不攔截滑鼠。");
        tooltip.SetToolTip(distanceMax, "使用現在自身至店主保存位置的 max(|ΔX|, |ΔY|) 格數；未對應手動快照的歷史項目不會通過距離篩選。");
        tooltip.SetToolTip(refreshNearby, "直接送出一次周遭刷新指令，不等待搜尋或前一請求確認。保存全部玩家及擺攤、非擺攤、未知分類；只有確認擺攤的玩家列入商店查詢，每一依已取得擺攤名單直接送出買／賣頁。更換 LC 來源後須重新取得名單。");
        tooltip.SetToolTip(scan, "只搜尋保存名單中已確認擺攤的玩家，每一買／賣頁送出前再核對目前擺攤狀態；不等待目前搜尋或前一請求確認。已搜尋玩家／買賣頁仍略過，要重查請先清除擺攤搜尋紀錄。");
        tooltip.SetToolTip(continueSaved, "接續目前 LC 來源保存計畫中的確認擺攤玩家，每一依已取得擺攤名單直接送出買／賣頁；不等待搜尋狀態確認，已送出頁不會再次查詢。");
        tooltip.SetToolTip(resume, "直接送出繼續指令，不等待暫停狀態確認；沿用目前批次、名單與進度，仍只查已確認擺攤玩家，後續依已取得名單直接開頁。");
        tooltip.SetToolTip(searchRecords, "查看本次啟動及跨巡迴點保存的搜尋進度、玩家 ID、名稱、座標和買／賣頁紀錄；每頁最多 100 位，不會開店或啟動搜尋。");
        tooltip.SetToolTip(resetSearchButton, "單擊停止並等待目前工作清理，清除搜尋紀錄與本次進度。已保存巡迴點時從第1輪第1點開始；沒有巡迴點則重新刷新周遭並搜尋。商品、座標與舊LOG保留；F9或停止搜尋可取消後續啟動。");
        tooltip.SetToolTip(noAdditionalPageWait, "完成本頁原生操作與清理後立即讀取一次商品；仍逐頁執行，不會並行送出。取消勾選後採用保留的毫秒設定，從下一頁起生效。");
        tooltip.SetToolTip(pageWaitMilliseconds, "每頁原生操作完成後，依100～5000毫秒間隔只讀一次商品，無道具就略過。變更從下一頁起生效，本頁保留開始時設定。");
        tooltip.SetToolTip(clearSearchHistory, "確認清除本機搜尋去重紀錄後，直接送出清除指令，不等待搜尋或巡迴狀態。商品、巡迴點、本次保存進度與原操作結果紀錄保留。");
        tooltip.SetToolTip(shopStateStatus, "這是上次手動刷新的動作觀測，不會在背景更新。非擺攤動作與未知仍列在周遭統計，但不送出商店買／賣頁；確認擺攤玩家的每一頁前會重新核對目前狀態。");
        tooltip.SetToolTip(exportExcel, "匯出所有符合目前篩選及排序的資料，不限表格顯示的 10,000 筆。取得時間、未知值與研究狀態一併保留。");
        tooltip.SetToolTip(clearListings, "清除兩側商品顯示及導航，原始商品檔與擺攤搜尋紀錄保留。要重新查詢已搜尋玩家，另按「清除擺攤搜尋紀錄」再重新搜尋。");
        tooltip.SetToolTip(synchronizeItemFilters, "啟用後，在上方道具清單勾選或取消品名，另一側若有完全相同品名就同步勾選狀態；其他選項保留。按「全部道具」會清除兩側道具篩選。切換開關不改動既有選擇，只影響後續手動操作；不會開店或更換導航目標。");
    }
    private void WireEvents()
    {
        refreshNearby.Click += async (_, _) => await RefreshNearbyAsync();
        showListingsButton.Click += (_, _) => ShowMarketListings("HomepageButton");
        marketPages.SelectedIndexChanged += (_, _) => Report("MARKET_PAGE_CHANGED",
            "目前分頁：" + marketPages.SelectedTab?.Text, new { marketPages.SelectedIndex }, show: false);
        scan.Click += async (_, _) => { ShowMarketListings("RestartSearch"); await SendAction(ShopCommands.ScanVisible); };
        continueSaved.Click += async (_, _) => { ShowMarketListings("ContinueSearch"); await SendAction(ShopCommands.ContinueSavedScan); };
        searchRecords.Click += (_, _) => ShowSearchRecords();
        resetSearchButton.Click += async (_, _) => await ResetSearchAndRestartAsync();
        pageWaitMilliseconds.ValueChanged += (_, _) =>
        {
            if (changingUi) return;
            int milliseconds = (int)pageWaitMilliseconds.Value;
            int effective = noAdditionalPageWait.Checked ? 0 : milliseconds;
            backend.ConfigurePageWaitMilliseconds(effective); SavePreferences();
            Report("PAGE_WAIT_SETTING", noAdditionalPageWait.Checked ? $"已保留 {milliseconds} 毫秒設定；目前不額外等待，從下一頁起套用。" : $"開頁間隔已設為 {milliseconds} 毫秒，從下一頁起套用；本頁沿用開始時的設定。",
                new { SavedConfiguredMs = milliseconds, NoAdditionalPageWait = noAdditionalPageWait.Checked, EffectiveIntervalMilliseconds = effective });
        };
        noAdditionalPageWait.CheckedChanged += (_, _) =>
        {
            if (changingUi) return;
            int configured = (int)pageWaitMilliseconds.Value, effective = noAdditionalPageWait.Checked ? 0 : configured;
            backend.ConfigurePageWaitMilliseconds(effective); SavePreferences();
            Report("PAGE_WAIT_SETTING", noAdditionalPageWait.Checked ? "從下一頁起不額外等待；保留原毫秒設定。" : $"從下一頁起採用 {configured} 毫秒開頁間隔。",
                new { SavedConfiguredMs = configured, NoAdditionalPageWait = noAdditionalPageWait.Checked, EffectiveIntervalMilliseconds = effective });
        };
        clearSearchHistory.Click += async (_, _) => await ClearSearchHistoryAsync();
        patrolButton.Click += (_, _) => ShowPatrolRoute();
        startSavedPatrolButton.Click += async (_, _) => await StartOrStopSavedPatrolAsync();
        stop.Click += async (_, _) => await SendCancel();
        pause.Click += async (_, _) => await SendScanControl(ShopCommands.PauseScan);
        resume.Click += async (_, _) => { ShowMarketListings("ResumeSearch"); await SendScanControl(ShopCommands.ResumeScan); };
        exportExcel.Click += async (_, _) => await ExportExcelAsync();
        clearListings.Click += async (_, _) => await ClearListingsAsync();
        reconnect.Click += (_, _) => { Report("RECONNECT", "重新接收本程式的讀取結果；先前動作不會重送。"); StartConnection(); };
        clearTarget.Click += (_, _) => { currentTargetKey = null; selectedNavigation = null; overlay.Clear(); Report("NAVIGATION_CLEAR", "已取消導航目標"); };
        foreach (var box in new[] { keyword, ownerFilter, priceMin, priceMax, distanceMax, unitFilter })
            box.TextChanged += (_, _) => { Report("FILTER", "查詢條件變更", FilterData(), show: false); RefreshRows(); };
        foreach (var box in new[] { viewerBuy, viewerSell, showHistory }) box.CheckedChanged += (_, _) =>
        { if (changingUi) return; Report("FILTER", "買賣或歷史篩選變更", FilterData(), show: false); SavePreferences(); RefreshRows(); };
        logEnabled.CheckedChanged += (_, _) => { if (!changingUi) { log.SetEnabled(logEnabled.Checked); SavePreferences(); Report("LOG_SETTING", log.Enabled ? "已啟用 LOG" : "已停用 LOG", writeLog: false); } };
        synchronizeItemFilters.CheckedChanged += (_, _) =>
        {
            if (changingUi) return;
            SavePreferences();
            Report("ITEM_FILTER_SYNC_SETTING", synchronizeItemFilters.Checked
                ? "已啟用買／賣品項同步；後續手動勾選才同步，既有選擇保留。"
                : "已關閉買／賣品項同步；兩側保留目前選擇。", FilterData());
        };
        searchOverlayEnabled.CheckedChanged += (_, _) =>
        {
            if (changingUi) return;
            searchOverlay.Clear(); SavePreferences();
            Report("SEARCH_OVERLAY_SETTING", searchOverlayEnabled.Checked ? "已開啟遊戲內搜尋狀態" : "已關閉遊戲內搜尋狀態");
        };
        foreach (var box in new[] { navigationEnabled, researchProjection }) box.CheckedChanged += (_, _) =>
        { if (!changingUi) { overlay.Clear(); SavePreferences(); Report("NAVIGATION_SETTING", "導航顯示設定已變更", new { navigationEnabled.Checked, research = researchProjection.Checked }); } };
        origin.SelectedIndexChanged += (_, _) => { if (!changingUi) { overlay.Clear(); SavePreferences(); Report("PROJECTION_ORIGIN", "投影候選原點已切換：" + origin.Text); } };
        foreach (var side in Sides) side.HeaderClicked += selected =>
        {
            sortTerms = MarketOrdering.PromotePrimary(sortTerms, selected);
            SyncSortControls(); Report("SORT", "排序已變更：" + SortDescription(), sortTerms, show: false); RefreshRows();
        };
        for (int i = 0; i < 3; i++)
        {
            int level = i;
            sortFields[i].SelectedIndexChanged += (_, _) => SortControlChanged(level);
            sortDirections[i].SelectedIndexChanged += (_, _) => SortControlChanged(level);
        }
        foreach (var side in Sides)
        {
            side.ItemFilterChanged += edit =>
            {
                if (closing || changingUi) return;
                var other = side == buyGrid ? sellGrid : buyGrid;
                bool synchronize = synchronizeItemFilters.Checked;
                string result = synchronize ? other.ApplySynchronizedItemEdit(edit) : "Disabled";
                Report(synchronize ? "ITEM_FILTER_SYNC" : "SIDE_ITEM_FILTER",
                    side.Direction == ShopPageModes.ViewerBuy ? "我買入道具篩選已變更" : "我賣出道具篩選已變更",
                    new { source = side.Direction, target = other.Direction, edit.Name, edit.IsChecked, edit.ClearAll,
                        synchronized = synchronize, result, filters = FilterData() }, show: false);
                QueueItemFilterRefresh();
            };
            side.RowSelected += row =>
            {
                if (renderingRows) return;
                foreach (var other in Sides.Where(other => other != side)) other.ClearSelection();
                currentTargetKey = LocalStore.Key(row.Listing);
            };
            side.RowClicked += async row =>
            {
                currentTargetKey = LocalStore.Key(row.Listing);
                await SendAction(ShopCommands.OpenShop, row.Listing);
            };
        }
        frameTimer.Tick += (_, _) => UpdateFrame();
    }

    private void QueueItemFilterRefresh()
    {
        if (itemFilterRefreshQueued || closing || IsDisposed || Disposing) return;
        itemFilterRefreshQueued = true;
        // Both semantic selections are already committed. Rebind only once,
        // after ItemCheck finishes, and always use the latest selections. This
        // callback never replays edits, so an older click cannot overwrite a
        // newer choice on the opposite side.
        BeginInvoke((Action)(() =>
        {
            itemFilterRefreshQueued = false;
            if (!closing && !IsDisposed && !Disposing) RefreshRows();
        }));
    }

    private void StartConnection()
    {
        if (closing) return;
        connection?.Cancel(); connection?.Dispose();
        connection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        lastNativeReadiness = null; lastNativeReadinessAt = default; observedNativeFailures.Clear();
        int run = ++generation; snapshot = null; ObservePatrolStartupContextTransition(); broker = null; liveKeys.Clear(); lastAcceptedSequence = -1;
        localReadFailure = null;
        overlay.Clear(); searchOverlay.Clear(); lastSearchOverlayView = null; RefreshRows();
        selectedNavigation = null; nextNavigationReadAt = default; navigationReadTicket++;
        navigationReadBusy = false;
        _ = WatchSnapshots(run, connection.Token); _ = PollState(run, connection.Token);
    }
    private async Task WatchSnapshots(int run, CancellationToken token)
    {
        try
        {
            await foreach (var next in backend.WatchAsync(token))
            {
                if (closing || run != generation) return;
                if (next.InstanceId == previousInstance && next.Sequence > 0 && next.Sequence < lastAcceptedSequence)
                { Report("SNAPSHOT_REJECTED", "略過倒序快照", new { next.Sequence, lastAcceptedSequence }, show: false); continue; }
                bool sourceChanged = next.Source is not null
                    && (previousSource != next.Source.SessionKey || previousInstance != next.InstanceId);
                StartupTiming.MarkOnce("first-snapshot"); snapshot = next; ObservePatrolStartupContextTransition();
                localReadFailure = null;
                manualNearby?.ObserveSource(next.Source);
                var context = Current();
                bool mapChanged = next.Source is not null && !sourceChanged
                    && (previousMapId is not null && context.MapId is not null && previousMapId != context.MapId
                        || previousMap is not null && context.MapName is not null && previousMap != context.MapName);
                if (mapChanged)
                    manualNearby?.Invalidate("地圖已變更；保留舊清單與保存座標，請手動刷新周遭後再搜尋或開店。");
                if (sourceChanged || mapChanged || next.Source is null)
                {
                    selectedNavigation = null; overlay.Clear(); liveKeys.Clear(); broker = null;
                    if (previousSource is not null && (sourceChanged || mapChanged))
                        Report("LIVE_CONTEXT_CLEARED", "來源或場景已變更，已清除即時投影與舊 ID 操作資格；保存座標另依地圖核對。");
                }
                // A temporary missing sample is not a new source or map. Keep
                // the last confirmed values so a change across the gap is seen.
                if (sourceChanged) previousMap = previousMapId = null;
                if (next.Source is not null)
                {
                    previousSource = next.Source.SessionKey; previousInstance = next.InstanceId;
                    if (context.MapName is not null) previousMap = context.MapName;
                    if (context.MapId is not null) previousMapId = context.MapId;
                }
                lastAcceptedSequence = next.Sequence;
                log.Write("SNAPSHOT", next.Detail, new { next.ServiceVersion, next.InstanceId, next.Sequence, next.Source, next.State,
                    fields = next.Fields.ToDictionary(p => p.Key, p => new { p.Value.State, p.Value.SampledAt }) });
                bool shopReadingReady = PlayerShopStatePresentation.ServiceReady(next, out string shopReadingReason);
                string status = !shopReadingReady ? shopReadingReason
                    : context.Valid ? "程式自行讀取 LC 已就緒" : context.Detail;
                if (status != lastConnectionMessage) { lastConnectionMessage = status; Report("CONNECTION", status); RefreshRows(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!closing && run == generation)
            {
                localReadFailure = "本機讀取初始化或訂閱失敗：" + ex.Message
                    + "；重新讀取僅接回現有元件，初始化失敗需關閉後重新啟動本程式。";
                snapshot = null; ObservePatrolStartupContextTransition(); broker = null; selectedNavigation = null; liveKeys.Clear(); overlay.Clear();
                // Do not poll the same failed initialization task every second.
                connection?.Cancel();
                Report("SNAPSHOT_ERROR", localReadFailure); scanStatus.Text = localReadFailure; RefreshRows();
            }
        }
    }
    private async Task PollState(int run, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                if (resettingSearch) continue;
                long resetGeneration = Volatile.Read(ref searchResetGeneration);
                try
                {
                    var request = new ShopCommandRequest { Command = ShopCommands.GetState };
                    log.Write("STATE_REQUEST", "讀取本機商店查詢狀態", request);
                    var response = await backend.SendAsync(request, token);
                    if (closing || run != generation) return;
                    MergeResponseCore(response, true, resetGeneration);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (closing || run != generation || token.IsCancellationRequested) return;
                    if (resetGeneration != Volatile.Read(ref searchResetGeneration)) continue;
                    broker = null; liveKeys.Clear(); overlay.Clear();
                    string message = ex is TimeoutException
                        ? "本機查詢回覆逾時，正在重新讀取狀態；已保存的資料保留。"
                        : "本機查詢狀態暫不可用：" + ex.Message;
                    Report("STATE_ERROR", message, new { error = ex.ToString(), RetryIsStateReadOnly = true }, show: message != lastScanMessage); lastScanMessage = message;
                    scanStatus.Text = message; RefreshRows();
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void MergeResponse(ShopCommandResponse response, bool poll)
        => MergeResponseCore(response, poll, Volatile.Read(ref searchResetGeneration));

    private void MergeResponseCore(ShopCommandResponse response, bool poll, long resetGeneration)
    {
        if (poll) log.Write("STATE_RESPONSE", response.Detail, new { response.RequestId, response.At, response.State,
            response.CurrentSourceSessionKey, response.NativeEntered, response.NativeReturned, response.ResponseVerified,
            response.SessionBlocked, response.WorkSettled, response.Scan, response.ListingsTruncated,
            savedScan = response.SavedScan is { } progress ? new { progress.PlanId, progress.SourceSessionKey,
                progress.UpdatedAt, progress.TotalPages, progress.SearchedPages, progress.ConfirmedPages,
                progress.RemainingPages, progress.UncertainPages, progress.AvailablePages, progress.DeferredPages,
                progress.HistorySkippedPages, progress.CanContinue, progress.ContinueBlockCode, progress.ContinueBlockReason } : null,
            response.SearchHistory,
            targetSnapshot = response.TargetSnapshot is { } saved ? new { saved.SnapshotId, saved.RefreshedAt, saved.SourceSessionKey, OwnerCount = saved.Owners.Length,
                saved.ShopActionObservedCount, saved.KnownOtherActionObservedCount, saved.UnknownShopStateCount, saved.CanDispatch, saved.DispatchBlockReason } : null,
            response.FailureCode, response.FailureScope, response.FailurePhase, response.FailureRva,
            listingRevisions = response.Listings.Where(LocalStore.Valid).Select(LocalStore.Key).ToArray() });
        else log.Write("ACTION_RESPONSE", response.Detail, response);
        foreach (var value in response.Listings.Where(LocalStore.Valid))
        {
            if (!store.IsListingVisible(value)) continue;
            bool saved = store.SaveListing(value);
            var update = history.Upsert(value);
            if (update.Changed)
            {
                if (update.Previous is { } previous && currentTargetKey == LocalStore.Key(previous))
                { currentTargetKey = LocalStore.Key(value); selectedNavigation = null; navigationReadTicket++; navigationReadBusy = false; overlay.Clear(); }
                Report(update.Previous is null ? "SHOP_PAGE_ADDED" : "SHOP_PAGE_UPDATED", "已更新店家最新清單，未重複加入商品。",
                    new { value.Owner.Name, value.Owner.Id, value.ViewerDirection, value.Revision, value.CapturedAt, value.OwnerAndResponseMatched, value.Complete, HistorySaved = saved }, show: false);
            }
        }
        history.Trim(LocalStore.ViewHistoryLimit);
        if (resetGeneration != Volatile.Read(ref searchResetGeneration))
        {
            // Acquisition records remain truthful; old state/progress cannot
            // repopulate the reset view or adopt the retired target snapshot.
            RefreshRows();
            return;
        }
        if (broker is null || response.At >= broker.At)
        {
            broker = response; brokerReceivedAt = DateTimeOffset.UtcNow;
            ObserveNativeReadiness(response);
            if (response.SavedScan is not null) lastKnownSavedScan = response.SavedScan;
            if (response.SearchHistory is not null) lastKnownSearchHistory = response.SearchHistory;
            if (response.At >= lastConfirmedScanAt)
            { lastConfirmedScan = response.Scan; lastConfirmedScanAt = response.At; }
            // State replies describe observations; they do not grant or revoke
            // the user's ability to submit the next forced command.
            if (poll
                && ManualNearbyContext.RestoreSaved(manualNearby, snapshot?.Source, response) is { } restored)
            {
                manualNearby = restored;
                selectedNavigation = null; navigationReadTicket++; navigationReadBusy = false; overlay.Clear();
                Report("TARGET_SNAPSHOT_RESTORED", $"已恢復本次啟動保存的全部 {restored.Count} 位周遭玩家：{PlayerShopStatePresentation.Counts(restored.ShopCounts)}；沿用原刷新時間，只查已確認擺攤玩家，後續依已取得名單直接開頁。",
                    new { restored.SnapshotId, restored.RefreshedAt, restored.Count, restored.ShopCounts, restored.Source, restored.CanDispatch, restored.DispatchBlockReason });
            }
            liveKeys.Clear();
            foreach (var value in response.Listings.Where(LocalStore.Valid)) liveKeys.Add(LocalStore.Key(value));
        }
        if (!poll)
        {
            lastSuccessfulAction = response.State == "TargetsRefreshed" && string.IsNullOrEmpty(response.FailureCode) ? response.Detail : null;
            lastSuccessfulActionJobId = lastSuccessfulAction is null ? null : response.Scan.JobId;
        }
        string message = poll && lastSuccessfulAction is not null && !response.Scan.Running && response.Scan.JobId == lastSuccessfulActionJobId
            ? lastSuccessfulAction + "　上一批搜尋紀錄：" + MarketFailurePresentation.Status(response, forceDispatch: true)
            : MarketFailurePresentation.Status(response, forceDispatch: true);
        scanStatus.Text = message;
        if (message != lastScanMessage) { lastScanMessage = message; Report("SCAN_STATE", message); }
        RefreshRows();
    }

    private bool HasCurrentBroker(out string reason)
    {
        reason = "";
        // Only the current local backend observation and session are required.
        // Field TTL and the changing nearby-player collection do not expire a
        // manually refreshed target set.
        var currentSource = snapshot?.Source;
        if (currentSource is null) { reason = "本程式尚未確認 LC 來源；保存的周遭與歷史沒有刪除。"; return false; }
        var now = DateTimeOffset.UtcNow;
        if (broker is null || broker.CurrentSourceSessionKey != currentSource.SessionKey || broker.At > now
            || now - broker.At > TimeSpan.FromSeconds(4) || now - brokerReceivedAt > TimeSpan.FromSeconds(3))
        { reason = "本機查詢狀態尚未同步；已保存的周遭快照不會因此過期。"; return false; }
        return true;
    }
    private bool IsSnapshotListing(MarketListing listing) => manualNearby?.CanUse(listing) == true;
    private bool CanCommand(MarketListing? listing, out string reason)
    {
        reason = "";
        if (!SearchMutationAllowed) { reason = "正在清除並從頭開始，請等待這次操作完成。"; return false; }
        if (snapshot?.Source is not { } currentSource)
        { reason = "目前沒有可識別的 LC 來源，無法建立商店指令。"; return false; }
        var native = MarketNativeCompatibility.Assess(snapshot?.Source);
        if (!native.CanSend) { reason = native.Message; return false; }
        if (manualNearby is null) { reason = "尚無周遭快照，請先按「手動刷新周遭」。"; return false; }
        if (manualNearby.Invalidated) { reason = manualNearby.InvalidReason; return false; }
        if (manualNearby.Source != currentSource)
        { reason = "周遭快照屬於其他 LC 來源，請手動刷新後再送出。"; return false; }
        if (listing is not null && !manualNearby.CanUse(listing))
        { reason = "這筆商品店主未對應目前來源的保存名單，無法建立開店指令。"; return false; }
        if (listing is not null && !manualNearby.CanScanSavedOwner(listing, out reason)) return false;
        if (listing is null && manualNearby.ShopCounts.ShopActionObservedCount == 0)
        { reason = "保存的周遭名單沒有已確認擺攤玩家；非擺攤動作與未知不查買／賣頁，請手動刷新周遭。"; return false; }
        return true;
    }

    private async Task RefreshNearbyAsync()
    {
        if (!SearchMutationAllowed) return;
        if (snapshot?.Source is not { } requestedSource)
        { Report("REFRESH_NOT_SENT", "目前沒有可識別的 LC 來源，無法建立刷新指令。"); return; }
        long ticket = ++refreshRequestTicket;
        long resetGeneration = Volatile.Read(ref searchResetGeneration);
        lastSuccessfulAction = null; lastSuccessfulActionJobId = null;
        var request = new ShopCommandRequest { Command = ShopCommands.RefreshTargets, ExpectedSourceSessionKey = requestedSource.SessionKey, ForceDispatch = true };
        var requestStartedAt = DateTimeOffset.UtcNow;
        actionBusy = ++activeActionRequests > 0; UpdateButtons(); Report("TARGET_REFRESH_SEND_ONCE", "直接送出刷新周遭指令，不等待搜尋或前一請求狀態確認。", request);
        try
        {
            var response = await backend.SendAsync(request, lifetime.Token);
            if (closing || ticket != refreshRequestTicket) return;
            MergeResponseCore(response, false, resetGeneration);
            if (!ManualNearbyContext.TryAdoptRefresh(manualNearby, requestedSource, snapshot?.Source,
                    request.RequestId, response, out var adopted, out _))
            {
                const string blocked = "周遭刷新未完成；保留原名單，只可查詢其中已確認擺攤的玩家，買／賣頁前仍會重新核對目前狀態。";
                lastSuccessfulAction = null; lastSuccessfulActionJobId = null;
                Report("TARGET_REFRESH_REJECTED", blocked + response.Detail, response);
                return;
            }
            patrolOverlayView = PatrolOverlayPresentation.RetireForAcceptedAction(patrolOverlayView, request.Command, request.RequestId, requestStartedAt, requestedSource.SessionKey, response);
            manualNearby = adopted!;
            selectedNavigation = null; navigationReadTicket++; navigationReadBusy = false; overlay.Clear();
            Report("TARGET_REFRESH_SAVED", $"已保存全部 {manualNearby.Count} 位周遭玩家：{PlayerShopStatePresentation.Counts(manualNearby.ShopCounts)}。只查詢已確認擺攤的玩家，每一買／賣頁前重新核對目前狀態；不等待前一請求確認。", response.TargetSnapshot);
        }
        catch (Exception ex)
        {
            lastSuccessfulAction = null; lastSuccessfulActionJobId = null;
            Report("TARGET_REFRESH_FAILED", "刷新回覆尚未確認；沒有自動重送，保留原資料；商店查詢仍只限已確認擺攤的玩家：" + ex.Message);
        }
        finally { actionBusy = --activeActionRequests > 0; if (!closing) { RefreshRows(); UpdateButtons(); } }
    }

    private void ShowSearchRecords()
    {
        if (closing) return;
        if (recordsWindow is null || recordsWindow.IsDisposed)
        {
            recordsWindow = new SavedSearchRecordsForm(backend.SendAsync);
            recordsWindow.UpdateProgress(CurrentSavedScan, snapshot?.Source?.SessionKey);
            recordsWindow.UpdateHistorySummary(CurrentSearchHistory);
            if (CurrentSavedScan is null) recordsWindow.SelectHistoryTab();
            recordsWindow.Show(this);
        }
        else { recordsWindow.UpdateProgress(CurrentSavedScan, snapshot?.Source?.SessionKey); recordsWindow.UpdateHistorySummary(CurrentSearchHistory); recordsWindow.Activate(); }
        Report("SEARCH_RECORDS_VIEW", "顯示本次進度與持久玩家 ID／座標／買賣頁記錄；未送出遊戲請求。", new { PlanId = broker?.SavedScan?.PlanId }, show: false);
    }

    private async Task SendAction(string command, MarketListing? listing = null)
    {
        if (!SearchMutationAllowed) return;
        long resetGeneration = Volatile.Read(ref searchResetGeneration);
        if (!CanCommand(listing, out string reason)) { Report("ACTION_NOT_SENT", reason); return; }
        if (command is ShopCommands.ScanVisible or ShopCommands.ContinueSavedScan)
        {
            var availability = SavedSearchPresentation.Controls(CurrentSavedScan, snapshot?.Source?.SessionKey,
                controlReady: true, dispatchReady: true, running: false, dispatchBlockReason: "", forceDispatch: true);
            if (command == ShopCommands.ContinueSavedScan ? !availability.CanContinue : !availability.CanRestart)
            { Report("SEARCH_NOT_SENT", availability.ContinueReason); return; }
        }
        string source = manualNearby!.Source.SessionKey;
        var request = new ShopCommandRequest
        {
            Command = command, ExpectedSourceSessionKey = source, OwnerId = listing?.Owner.Id,
            ExpectedOwnerIdentity = listing?.Owner.LocalIdentity, ViewerDirection = listing?.ViewerDirection,
            TargetSnapshotId = manualNearby.SnapshotId,
            ExpectedScanPlanId = command == ShopCommands.ContinueSavedScan ? CurrentSavedScan?.PlanId : null,
            ForceDispatch = true
        };
        if (!store.ResumeListings(DateTimeOffset.UtcNow)) return;
        var requestStartedAt = DateTimeOffset.UtcNow;
        actionBusy = ++activeActionRequests > 0; UpdateButtons();
        Report("ACTION_SEND_ONCE", command == ShopCommands.OpenShop ? $"送出已確認擺攤的店主「{listing!.Owner.Name}」開店指令；依已取得擺攤名單直接送出買／賣頁，不等待前一請求確認。"
            : command == ShopCommands.ContinueSavedScan ? "送出保存進度接續指令，只查已確認擺攤玩家，後續依已取得名單直接開頁；已送出頁不重複查詢，不等待前一請求確認。"
            : $"送出搜尋指令，使用 {manualNearby.RefreshedAt.ToLocalTime():HH:mm:ss} 保存的全部 {manualNearby.Count} 位玩家，僅查其中 {manualNearby.ShopCounts.ShopActionObservedCount} 位已確認擺攤玩家；後續依已取得名單直接開頁，不等待前一請求確認。", request);
        try
        {
            var response = await backend.SendAsync(request, lifetime.Token);
            if (closing) return;
            if (resetGeneration == Volatile.Read(ref searchResetGeneration))
                patrolOverlayView = PatrolOverlayPresentation.RetireForAcceptedAction(patrolOverlayView, request.Command, request.RequestId, requestStartedAt, source, response);
            MergeResponseCore(response, false, resetGeneration);
            Report("ACTION_RESULT", $"{response.Detail} 進入事件：{Yes(response.NativeEntered)}；事件返回：{Yes(response.NativeReturned)}；清單回應確認：{Yes(response.ResponseVerified)}。", response);
            if (response.SessionBlocked || response.NativeEntered && !response.NativeReturned) uncertainSources.Add(source);
        }
        catch (Exception ex)
        {
            uncertainSources.Add(source);
            Report("ACTION_UNCERTAIN_NO_RETRY", "請求回覆尚未確認，沒有自動重送；後續商店查詢仍只限已確認擺攤玩家，每頁前再核對目前狀態：" + ex.Message, request);
        }
        finally { actionBusy = --activeActionRequests > 0; if (!closing) { UpdateButtons(); RefreshRows(); } }
    }

    private async Task SendCancel()
    {
        if (closing) return;
        if (nativeReadinessBusy) { nativeReadinessCancellation?.Cancel(); return; }
        if (resettingSearch) { CancelSearchRestart(); return; }
        long resetGeneration = Volatile.Read(ref searchResetGeneration);
        string? source = snapshot?.Source?.SessionKey;
        if (string.IsNullOrEmpty(source)) { Report("CANCEL_NOT_SENT", "目前沒有可識別的來源，無法送出停止請求。"); return; }
        var request = new ShopCommandRequest { Command = ShopCommands.CancelScan, ExpectedSourceSessionKey = source, ForceDispatch = true };
        cancelBusy = ++activeCancelRequests > 0; UpdateButtons(); Report("CANCEL_SEND_ONCE", "直接送出停止搜尋指令，不等待目前搜尋或前一請求狀態確認。", request);
        try
        {
            var response = await backend.SendAsync(request, lifetime.Token);
            if (!closing) { MergeResponseCore(response, false, resetGeneration); Report("CANCEL_RESULT", response.Detail, response); }
        }
        catch (Exception ex) { Report("CANCEL_UNCERTAIN_NO_RETRY", "停止請求結果不明，未重送：" + ex.Message); }
        finally { cancelBusy = --activeCancelRequests > 0; if (!closing) UpdateButtons(); }
    }

    private async Task ClearListingsAsync()
    {
        if (!SearchMutationAllowed || clearingListings || exporting) return;
        clearingListings = true; UpdateButtons();
        try
        {
            DateTimeOffset clearedAt = DateTimeOffset.UtcNow;
            if (!store.ClearListings(clearedAt)) return;
            int removedPages = history.Count;
            history.Clear(); liveKeys.Clear(); currentTargetKey = null; selectedNavigation = null;
            navigationReadTicket++; navigationReadBusy = false; overlay.Clear();
            foreach (var side in Sides) { side.ResetItems(); side.ClearSelection(); }
            navigationStatus.Text = "清單已清除；尚未選擇店主";
            Report("LIST_CLEARED", "已清除買／賣商品顯示；原始歷史檔、本次進度與擺攤搜尋紀錄保留。要重查已搜尋玩家，請先按「清除擺攤搜尋紀錄」再重新搜尋。", new { clearedAt, removedPages, HistoryFilesDeleted = false, SearchProgressCleared = false, SearchHistoryCleared = false });
            RefreshRows();
            await SendCancel();
        }
        finally { clearingListings = false; if (!closing) UpdateButtons(); }
    }

    private async Task SendScanControl(string command)
    {
        if (!SearchMutationAllowed) return;
        long resetGeneration = Volatile.Read(ref searchResetGeneration);
        string? source = snapshot?.Source?.SessionKey;
        if (string.IsNullOrWhiteSpace(source))
        { Report("SCAN_CONTROL_NOT_SENT", "目前沒有可識別的 LC 來源，無法建立暫停／繼續指令。"); return; }
        var request = new ShopCommandRequest { Command = command, ExpectedSourceSessionKey = source, ForceDispatch = true };
        scanControlBusy = ++activeScanControlRequests > 0; UpdateButtons();
        Report("SCAN_CONTROL_SEND_ONCE", command == ShopCommands.PauseScan ? "直接送出暫停指令，不等待搜尋狀態確認。" : "直接送出繼續指令，不等待暫停狀態確認。", request);
        try
        {
            var response = await backend.SendAsync(request, lifetime.Token);
            if (!closing) { MergeResponseCore(response, false, resetGeneration); Report("SCAN_CONTROL_RESPONSE", response.Detail, response); }
        }
        catch (Exception ex)
        {
            if (resetGeneration == Volatile.Read(ref searchResetGeneration)) broker = null;
            Report("SCAN_CONTROL_UNCONFIRMED", "暫停／繼續回覆尚未確認；沒有自動重送，仍可直接送出下一指令：" + ex.Message, request);
        }
        finally { scanControlBusy = --activeScanControlRequests > 0; if (!closing) UpdateButtons(); }
    }

    private void RefreshRows()
    {
        if (closing || renderingRows) return;
        UpdateItemCatalogues();
        UpdateItemStateChoices();
        if (!TryCollectRows(out var ordered, out string reason))
        {
            filterStatus.Text = reason; filterStatus.ForeColor = Color.Firebrick;
            renderingRows = true; try { foreach (var side in Sides) side.SetRows([], 0); } finally { renderingRows = false; }
            totals.Text = "篩選值無效，請修正後再查詢。"; UpdateSortHeaders(); return;
        }
        filterStatus.Text = groupStatePrices.Checked ? "依有效分類分組比價；未鑑定確認後依設定歸一般" : "各條件同時套用"; filterStatus.ForeColor = Color.DimGray;
        renderingRows = true;
        try
        {
            foreach (var side in Sides)
            {
                var directionRows = ordered.Where(row => row.Listing.ViewerDirection == side.Direction).ToArray();
                side.SetRows(directionRows.Take(10000).ToArray(), directionRows.Length);
            }
            UpdateSortHeaders();
        }
        finally { renderingRows = false; }
        int displayed = Sides.Sum(side => Math.Min(10000, ordered.Count(row => row.Listing.ViewerDirection == side.Direction)));
        totals.Text = $"符合 {ordered.Length:N0} 筆商品；左右共顯示 {displayed:N0} 筆（每側最多 10,000），Excel 匯出全部 {ordered.Length:N0} 筆。店家買／賣頁 {history.Count:N0}（已去重），原始紀錄 {store.StoredFileCount:N0} 筆。";
        UpdateButtons();
    }

    private void UpdateItemCatalogues()
    {
        // Build options before every filter and before the per-table display
        // limit. Options therefore never shrink because of their own selection
        // or any shared keyword/owner/price/history condition.
        foreach (var side in Sides)
            side.SetItemNames(history.Values.Where(listing => listing.ViewerDirection == side.Direction)
                .SelectMany(listing => listing.Rows.Where(row => row.IncludedInListing))
                .Select(row => MarketOrdering.ItemNameKey(row.Name ?? "未取得名稱", row.Quantity)));
    }

    // Called only on the UI thread: freeze row values from the manual target set before export.
    private bool TryCollectRows(out MarketRowView[] ordered, out string reason)
    {
        ordered = []; reason = "";
        if (!ParseNumber(priceMin.Text, out ulong? minimum) || !ParseNumber(priceMax.Text, out ulong? maximum)
            || !ParseNumber(distanceMax.Text, out ulong? distance) || minimum.HasValue && maximum.HasValue && minimum.Value > maximum.Value)
        {
            reason = "請輸入有效的非負整數範圍"; return false;
        }
        string[] terms = keyword.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] owners = ownerFilter.Text.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var current = Current();
        var currentSelf = current.Valid ? current.SelfPosition : null;
        var rows = new List<MarketRowView>();
        foreach (var listing in history.Values)
        {
            if (listing.ViewerDirection == ShopPageModes.ViewerBuy && !viewerBuy.Checked || listing.ViewerDirection == ShopPageModes.ViewerSell && !viewerSell.Checked) continue;
            bool live = IsSnapshotListing(listing);
            if (!showHistory.Checked && !live) continue;
            var player = manualNearby?.Match(listing);
            bool savedNavigation = current.TrySavedPositionNavigation(listing, out _, out _);
            foreach (var item in listing.Rows.Where(r => r.IncludedInListing))
            {
                var row = new MarketRowView(listing, item, player,
                    savedNavigation ? currentSelf : null,
                    live, manuallyRefreshed: live, currentSelfPosition: true, savedPositionNavigation: savedNavigation);
                bool itemSelected = listing.ViewerDirection == ShopPageModes.ViewerBuy ? buyGrid.IncludesItem(row.Item)
                    : listing.ViewerDirection == ShopPageModes.ViewerSell && sellGrid.IncludesItem(row.Item);
                if (!itemSelected || !MarketItemStatePresentation.MatchesFilter(item, (itemStateFilter.SelectedItem as ItemStateChoice)?.Key)) continue;
                if (terms.Any(t => !row.Item.Contains(t, StringComparison.OrdinalIgnoreCase))
                    || owners.Length > 0 && !owners.Any(o => row.Owner.Contains(o, StringComparison.OrdinalIgnoreCase))
                    || unitFilter.Text.Length > 0 && !row.Unit.Contains(unitFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase)
                    || minimum.HasValue && (!row.NumericPrice.HasValue || row.NumericPrice.Value < minimum.Value)
                    || maximum.HasValue && (!row.NumericPrice.HasValue || row.NumericPrice.Value > maximum.Value)
                    || distance.HasValue && (!row.NumericDistance.HasValue || (ulong)row.NumericDistance.Value > distance.Value)) continue;
                rows.Add(row);
            }
        }
        ordered = MarketOrdering.Apply(rows, sortTerms);
        if (groupStatePrices.Checked)
            ordered = MarketItemStatePresentation.GroupForComparison(ordered,
                sortTerms.FirstOrDefault(term => term.Field == MarketSortField.Price)?.Descending ?? false)
                .SelectMany(group => group.Rows).ToArray();
        return true;
    }

    private sealed record SortChoice(MarketSortField Field, string Label)
    {
        public override string ToString() => Label;
    }
    private static string SortLabel(MarketSortField field) => field switch
    {
        MarketSortField.Item => "商品名稱", MarketSortField.Price => "金額", MarketSortField.Quantity => "數量",
        MarketSortField.Owner => "店主", MarketSortField.Direction => "買／賣方向", MarketSortField.Position => "座標（X、Y）",
        MarketSortField.Distance => "距離", MarketSortField.CapturedAt => "取得時間", MarketSortField.ItemKind => "道具類型", _ => "（無）"
    };
    private string SortDescription() => (sortTerms.Length == 0 ? "未指定欄位" : string.Join(" → ", sortTerms.Select((t, i) => $"{i + 1}. {SortLabel(t.Field)}{(t.Descending ? "↓" : "↑")}")))
        + "；未知數值排最後；相同值依取得時間↓、紀錄識別排序";
    private void SyncSortControls()
    {
        changingSortUi = true;
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var term = i < sortTerms.Length ? sortTerms[i] : new(MarketSortField.None, false);
                sortFields[i].SelectedIndex = (int)term.Field;
                sortDirections[i].SelectedIndex = term.Descending ? 1 : 0;
                sortDirections[i].Enabled = term.Field != MarketSortField.None;
            }
        }
        finally { changingSortUi = false; }
    }
    private void SortControlChanged(int changedLevel)
    {
        if (changingSortUi || closing) return;
        var values = Enumerable.Range(0, 3).Select(i => new MarketSortTerm(
            (sortFields[i].SelectedItem as SortChoice)?.Field ?? MarketSortField.None, sortDirections[i].SelectedIndex == 1)).ToArray();
        var selected = values[changedLevel].Field;
        if (selected != MarketSortField.None)
            for (int i = 0; i < 3; i++) if (i != changedLevel && values[i].Field == selected) values[i] = new(MarketSortField.None, false);
        sortTerms = MarketOrdering.Normalize(values);
        SyncSortControls(); Report("SORT", "排序已變更：" + SortDescription(), sortTerms, show: false); RefreshRows();
    }
    private void UpdateSortHeaders()
    {
        foreach (var side in Sides)
        foreach (DataGridViewColumn column in side.Grid.Columns)
        {
            var field = (MarketSortField)column.Tag!;
            int index = Array.FindIndex(sortTerms, t => t.Field == field && field != MarketSortField.None);
            column.HeaderText = baseColumnTitles[column.Name] + (index >= 0 ? $" [{index + 1}{(sortTerms[index].Descending ? "↓" : "↑")}]" : "");
            column.HeaderCell.SortGlyphDirection = index == 0 ? sortTerms[0].Descending ? SortOrder.Descending : SortOrder.Ascending : SortOrder.None;
        }
    }

    private async Task ExportExcelAsync()
    {
        if (closing || exporting) return;
        // Materialize every matching row before displaying a dialog; none of the worker accesses controls/history/current LC state.
        if (!TryCollectRows(out var rows, out string reason)) { Report("EXCEL_EXPORT_REJECTED", reason); return; }
        string filter = FilterDescription(), sorting = SortDescription();
        DateTimeOffset captured = DateTimeOffset.Now;
        using var dialog = new SaveFileDialog
        {
            Filter = "Excel 活頁簿 (*.xlsx)|*.xlsx", DefaultExt = "xlsx", AddExtension = true, CheckPathExists = true,
            OverwritePrompt = true, FileName = $"LC商店查詢_V33_{captured:yyyyMMdd_HHmmss}.xlsx",
            Title = $"匯出全部 {rows.Length:N0} 筆篩選結果（不受表格 10,000 筆上限影響）"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) { Report("EXCEL_EXPORT_CANCELLED", "已取消 Excel 匯出", new { rows = rows.Length }, show: false); return; }
        if (File.Exists(dialog.FileName)) { Report("EXCEL_EXPORT_REJECTED", "請選擇新的檔案名稱，既有 Excel 不會被覆寫。"); return; }
        string path = Path.GetFullPath(dialog.FileName);
        exporting = true; UpdateButtons();
        Report("EXCEL_EXPORT_START", $"正在匯出全部 {rows.Length:N0} 筆至 {path}", new { path, totalRows = rows.Length, snapshotAt = captured, filter, sorting });
        try
        {
            await Task.Run(() => ExcelExporter.Write(path, rows, filter, sorting));
            if (!closing) Report("EXCEL_EXPORT_SAVED", $"Excel 已保存，共 {rows.Length:N0} 筆：{path}", new { path, totalRows = rows.Length, snapshotAt = captured });
        }
        catch (Exception ex) { if (!closing) Report("EXCEL_EXPORT_FAILED", "Excel 匯出失敗：" + ex.Message, new { path, totalRows = rows.Length, error = ex.ToString() }); }
        finally
        {
            exporting = false;
            if (!closing) { UpdateButtons(); if (closeAfterExport) Close(); }
        }
    }

    private string FilterDescription() => $"物品狀態：{itemStateFilter.SelectedItem}；物品分類分組比價：{groupStatePrices.Checked}；" + $"店主出售（我買入）：{viewerBuy.Checked}；店主收購（我賣出）：{viewerSell.Checked}；顯示歷史：{showHistory.Checked}；"
        + $"買／賣品項同步：{(synchronizeItemFilters.Checked ? "開啟" : "關閉")}；"
        + $"商品關鍵字：{keyword.Text}；店主：{ownerFilter.Text}；價格：{priceMin.Text}～{priceMax.Text}；單位：{unitFilter.Text}；距離上限：{distanceMax.Text} 格；"
        + $"我買入道具（精確名稱、任一符合）：{ItemFilterDescription(buyGrid)}；我賣出道具（精確名稱、任一符合）：{ItemFilterDescription(sellGrid)}";
    private static string ItemFilterDescription(MarketSideGrid side) => side.SelectedItemNames is { Length: > 0 } names
        ? System.Text.Json.JsonSerializer.Serialize(names) : "全部道具";

    private void UpdateSearchOverlay()
    {
        if (closing || !searchOverlayEnabled.Checked) { searchOverlay.Clear(); return; }
        try
        {
            // Reuse the local backend snapshot. Only foreground window geometry is queried here.
            var source = snapshot?.Source ?? manualNearby?.Source;
            var frame = source is null ? null : WindowFrameReader.Read(source);
            if (frame is null) { searchOverlay.Clear(); return; }
            bool confirmed = HasCurrentBroker(out var reason);
            var native = MarketNativeCompatibility.Assess(snapshot?.Source);
            var view = SearchOverlayPresentation.Build(broker, confirmed, reason, actionBusy || scanControlBusy || cancelBusy,
                native.CanSend ? null : native.Message, forceDispatch: true);
            view = PatrolOverlayPresentation.Combine(view, patrolOverlayView, patrolRunning, patrolBlocked, broker?.Scan.Running == true, actionBusy || scanControlBusy || cancelBusy);
            if (WindowFrameReader.Read(source!) != frame) { searchOverlay.Clear(); return; }
            searchOverlay.Present(frame, view);
            lastSearchOverlayError = "";
            if (lastSearchOverlayView != view)
            {
                lastSearchOverlayView = view;
                Report("SEARCH_OVERLAY_STATE", view.State, view, show: false);
            }
        }
        catch (Exception error)
        {
            searchOverlay.Clear();
            if (lastSearchOverlayError != error.Message)
            {
                lastSearchOverlayError = error.Message;
                Report("SEARCH_OVERLAY_ERROR", "搜尋狀態面板暫停：" + error.Message, show: false);
            }
        }
    }

    private void UpdateFrame()
    {
        if (closing) return;
        UpdateSearchOverlay();
        try
        {
            var context = Current();
            sourceStatus.Text = localReadFailure ?? (context.Valid ? $"程式自行讀取 LC｜角色 ID {context.SelfId}｜即時座標 {Format(context.SelfPosition)}｜{context.MapName ?? "地圖名稱未取得"}"
                : "本程式即時導航資料：" + context.Detail + "；手動周遭快照另行保存。");
            UpdateButtons();
            if (!navigationEnabled.Checked || currentTargetKey is null || !history.TryGetValue(currentTargetKey, out var listing)
                || context.Source is null)
            { selectedNavigation = null; overlay.Clear(); SetNavigation("尚未選擇商店，或正在等待本程式讀取 LC"); return; }
            if (!context.TrySavedPositionNavigation(listing, out var view, out var reason))
            { overlay.Clear(); SetNavigation(reason); return; }
            string distanceHint = $"{view!.Name}：至保存位置 {view.Distance} 格；";
            var frame = WindowFrameReader.Read(context.Source);
            if (frame is null) { overlay.Clear(); SetNavigation(distanceHint + "LC 不在前景，導航標記已隱藏"); return; }
            // Optional current-actor projection enhances the saved-coordinate guide.
            // A missing actor, manual refresh, or unavailable projection never cancels that guide.
            if (researchProjection.Checked && manualNearby is { } manual && context.Source == manual.Source && IsSnapshotListing(listing))
            {
                if (HasCurrentBroker(out _) && !navigationReadBusy && DateTimeOffset.UtcNow >= nextNavigationReadAt)
                    _ = ReadSelectedNavigationAsync(listing, context.Source, manual.SnapshotId);
                if (context.TrySelectedNavigation(listing, manual, selectedNavigation, new Size(frame.Width, frame.Height),
                    true, origin.SelectedIndex == 0, out var projected, out _)) view = projected;
            }
            // Close the OS frame after parsing the snapshot, without changing foreground or generating input.
            var after = WindowFrameReader.Read(context.Source);
            if (after != frame) { overlay.Clear(); SetNavigation("視窗位置或尺寸正在變動"); return; }
            overlay.Present(frame, view!); SetNavigation($"{view!.Name}：{view.Distance} 格；{view.Evidence}");
        }
        catch (Exception ex) { overlay.Clear(); SetNavigation("導航標記暫停：" + ex.Message); }
    }

    private async Task ReadSelectedNavigationAsync(MarketListing listing, SourceIdentity expectedSource, string targetSnapshotId)
    {
        if (navigationReadBusy || closing) return;
        navigationReadBusy = true;
        long ticket = ++navigationReadTicket;
        int run = generation;
        string targetKey = LocalStore.Key(listing);
        nextNavigationReadAt = DateTimeOffset.UtcNow.AddMilliseconds(500);
        var request = new ShopCommandRequest { Command = ShopCommands.ReadNavigation, ExpectedSourceSessionKey = expectedSource.SessionKey,
            TargetSnapshotId = targetSnapshotId, OwnerId = listing.Owner.Id, ExpectedOwnerIdentity = listing.Owner.LocalIdentity };
        log.Write("NAVIGATION_REQUEST", "唯讀選中的目標投影，不刷新周遭名單", request);
        try
        {
            var response = await backend.SendAsync(request, connection?.Token ?? lifetime.Token);
            if (closing || run != generation || ticket != navigationReadTicket) return;
            // Do not merge into broker/history: this is an independent read, not
            // an action acknowledgement or an adoption of another target set.
            log.Write("NAVIGATION_RESPONSE", response.Detail, new { response.RequestId, response.State, response.CurrentSourceSessionKey,
                response.FailureCode, response.FailureScope, response.Navigation });
            var saved = manualNearby;
            if (snapshot?.Source != expectedSource || saved is null || saved.SnapshotId != targetSnapshotId
                || saved.Invalidated || currentTargetKey != targetKey || !navigationEnabled.Checked || !researchProjection.Checked)
            { selectedNavigation = null; return; }
            selectedNavigation = response.CurrentSourceSessionKey == expectedSource.SessionKey && string.IsNullOrEmpty(response.FailureCode)
                ? response.Navigation : null;
            if (selectedNavigation is null) UpdateFrame();
        }
        catch (OperationCanceledException) when (closing || run != generation) { }
        catch (Exception error)
        {
            if (closing || run != generation || ticket != navigationReadTicket) return;
            selectedNavigation = null;
            Report("NAVIGATION_READ_FAILED", "選中目標投影讀取失敗：" + error.Message, show: false);
        }
        finally
        {
            if (ticket == navigationReadTicket)
            { navigationReadBusy = false; nextNavigationReadAt = DateTimeOffset.UtcNow.AddMilliseconds(500); }
        }
    }
    private void SetNavigation(string text)
    { navigationStatus.Text = text; if (lastNavigationMessage != text) { lastNavigationMessage = text; Report("NAVIGATION_STATE", text, show: false); } }
    private void UpdateButtons()
    {
        bool ready = CanCommand(null, out string commandReason);
        bool shopReadingReady = PlayerShopStatePresentation.ServiceReady(snapshot, out string shopReadingReason);
        var native = MarketNativeCompatibility.Assess(snapshot?.Source);
        string compatibilityKey = $"{snapshot?.InstanceId}|{snapshot?.Source?.SessionKey}|{native.Code}|{snapshot?.Source?.DiskSha256}|{snapshot?.Source?.PeTimestamp}|{snapshot?.Source?.ImageSize}";
        if (compatibilityKey != lastNativeCompatibility)
        {
            lastNativeCompatibility = compatibilityKey;
            Report("NATIVE_COMPATIBILITY", native.Message, new { native.CanSend, native.Code, snapshot?.ServiceVersion,
                Source = snapshot?.Source, ExpectedSha256 = MarketNativeCompatibility.SupportedSha256,
                ExpectedTimestamp = MarketNativeCompatibility.SupportedTimestamp, ExpectedImageSize = MarketNativeCompatibility.SupportedImageSize });
        }
        bool confirmed = HasCurrentBroker(out _), active = lastConfirmedScan?.Running == true;
        bool controlReady = SearchMutationAllowed && !nativeReadinessBusy;
        checkNativeReadinessButton.Enabled = !closing && !nativeReadinessBusy && !PatrolActive && !active && !actionBusy && !scanControlBusy && !resettingSearch && native.CanSend;
        bool sourceKnown = snapshot?.Source is not null;
        refreshNearby.Enabled = controlReady && sourceKnown;
        if (refreshNearby.Enabled) StartupTiming.MarkOnce("ready-for-readonly-refresh");
        var savedControls = SavedSearchPresentation.Controls(CurrentSavedScan, snapshot?.Source?.SessionKey,
            controlReady, ready, active, commandReason, forceDispatch: true);
        scan.Enabled = savedControls.CanRestart;
        continueSaved.Enabled = savedControls.CanContinue;
        searchRecords.Enabled = !closing;
        resetSearchButton.Enabled = !closing && !resettingSearch;
        clearSearchHistory.Enabled = CanClearSearchHistory();
        savedSearchStatus.Text = (patrolRunning || patrolBlocked || homePatrolRequested ? "巡迴：" + patrolStatus + "　｜　" : "")
            + ShopSearchHistoryPresentation.Summary(CurrentSearchHistory) + "　｜　" + SavedSearchPresentation.Summary(CurrentSavedScan, snapshot?.Source?.SessionKey)
            + (!savedControls.CanContinue && CurrentSavedScan is not null && !active ? "　" + savedControls.ContinueReason : "");
        recordsWindow?.UpdateProgress(CurrentSavedScan, snapshot?.Source?.SessionKey);
        recordsWindow?.UpdateHistorySummary(CurrentSearchHistory);
        pause.Enabled = controlReady && sourceKnown;
        resume.Enabled = controlReady && sourceKnown;
        stop.Enabled = !closing && (resettingSearch || nativeReadinessBusy || sourceKnown);
        reconnect.Enabled = controlReady;
        exportExcel.Enabled = !closing && !exporting;
        clearListings.Enabled = controlReady && !clearingListings && !exporting;
        patrolButton.Enabled = controlReady;
        startSavedPatrolButton.Text = patrolRunning ? patrolStopRequested ? "巡迴停止中…" : "停止巡迴" : "開始巡迴";
        startSavedPatrolButton.Enabled = !closing && (patrolRunning ? !patrolStopRequested
            : controlReady && native.CanSend);
        if (!patrolRunning && startSavedPatrolButton.Enabled) StartupTiming.MarkOnce("ready-for-patrol-start");
        CheckPatrolStopHotkey();
        string state = lastConfirmedScan is null ? "尚無本機查詢確認"
            : lastConfirmedScan.Running ? lastConfirmedScan.Paused ? "已暫停"
                : lastConfirmedScan.PauseRequested ? "暫停中：等待本頁完成" : "正在搜尋"
            : lastConfirmedScan.Total > 0 && lastConfirmedScan.Completed >= lastConfirmedScan.Total && string.IsNullOrWhiteSpace(lastConfirmedScan.StopReason) ? "搜尋完成" : "已停止";
        searchBadge.Text = (confirmed ? !active && !native.CanSend ? "商店元件待更新" : state : "觀測待更新｜上次：" + state)
            + (native.CanSend ? "｜只查確認擺攤" : "");
        searchBadge.BackColor = !confirmed ? Color.Gainsboro : lastConfirmedScan?.Paused == true ? Color.Khaki
            : lastConfirmedScan?.PauseRequested == true ? Color.Moccasin : active ? Color.LightGreen : Color.MistyRose;
        searchBadge.ForeColor = confirmed ? Color.Black : Color.DimGray;
        nearbyStatus.Text = manualNearby is null ? "尚無周遭快照：請按「手動刷新周遭」"
            : $"周遭刷新時間 {manualNearby.RefreshedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}｜已保存 {manualNearby.Count} 位玩家｜"
                + (manualNearby.Invalidated ? manualNearby.InvalidReason
                    : "只查詢已確認擺攤玩家，每一依已取得擺攤名單直接送出買／賣頁；店主座標為刷新時資料。");
        nearbyStatus.ForeColor = manualNearby is { Invalidated: true } ? Color.Firebrick : Color.DarkSlateBlue;
        shopStateStatus.Text = !shopReadingReady ? shopReadingReason
            : manualNearby is null ? "擺攤狀態讀取已啟用；請按「手動刷新周遭」取得玩家狀態。"
            : $"上次手動刷新：{PlayerShopStatePresentation.Counts(manualNearby.ShopCounts)}。僅確認擺攤玩家可查買／賣頁，送出每頁前再核對目前狀態；歷史商品可繼續導航。";
        if (shopReadingReady && !native.CanSend) shopStateStatus.Text += "　" + native.Message;
        if (native.CanSend) shopStateStatus.Text += "　" + NativeReadinessDetail();
        shopStateStatus.ForeColor = !shopReadingReady || !native.CanSend ? Color.Firebrick
            : manualNearby?.ShopCounts.ShopActionObservedCount > 0 ? Color.DarkGreen : Color.DarkGoldenrod;
    }
    private LiveContext Current() => new(snapshot, DateTimeOffset.UtcNow);
    private void Report(string kind, string message, object? data = null, bool show = true, bool writeLog = true)
    {
        if (kind.Contains("ERROR", StringComparison.Ordinal) || kind.Contains("FAILED", StringComparison.Ordinal) || kind.Contains("REJECT", StringComparison.Ordinal)) debugApiLastError = kind + ": " + message;
        if (writeLog) log.Write(kind, message, data);
        if (!show || closing || messages.IsDisposed) return;
        messages.Items.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
        while (messages.Items.Count > 400) messages.Items.RemoveAt(0);
        messages.TopIndex = messages.Items.Count - 1;
    }
    private void SavePreferences() => store.SavePreferences(new()
    { LogEnabled = log.Enabled, ShowHistory = showHistory.Checked, SynchronizeItemFilters = synchronizeItemFilters.Checked,
        ShowSearchOverlay = searchOverlayEnabled.Checked, ShowNavigation = navigationEnabled.Checked, ResearchProjection = researchProjection.Checked, BottomOrigin = origin.SelectedIndex == 0,
        PageOpenIntervalMilliseconds = (int)pageWaitMilliseconds.Value, NoAdditionalPageWait = noAdditionalPageWait.Checked });
    private object FilterData() => new { keyword = keyword.Text, owners = ownerFilter.Text, min = priceMin.Text, max = priceMax.Text, distance = distanceMax.Text, unit = unitFilter.Text, buy = viewerBuy.Checked, sell = viewerSell.Checked, history = showHistory.Checked,
        buyItems = buyGrid.SelectedItemNames, sellItems = sellGrid.SelectedItemNames,
        synchronizeItemFilters = synchronizeItemFilters.Checked, itemMatch = "OrdinalExact_OrWithinSide_AndShared_EmptyMeansAll" };
    private async Task CloseWorkAsync()
    {
        if (!closing)
        {
            Report("CLOSE_PENDING_REQUEST", "正在停止本機搜尋並等待進行中的請求完成紀錄，完成後關閉程式；請求不會重送。");
            SavePreferences(); closing = true; frameTimer.Stop(); patrolCancellation?.Cancel(); searchResetCancellation?.Cancel();
            overlay.Clear(); searchOverlay.Clear(); UpdateButtons();
            scanStatus.Text = "正在停止本機搜尋與讀取，等待進行中的請求完成紀錄…";
        }
        try
        {
            if (searchResetTask is not null) await searchResetTask;
            await StopPatrolAsync();
            await patrolNavigation.DisposeAsync();
            lifetime.Cancel(); connection?.Cancel();
            if (historyRestoreTask is not null) await historyRestoreTask;
            await backend.DisposeAsync();
            if (sessionLifecycle is not null)
            {
                var cleanup = await sessionLifecycle.CleanupAfterStopAsync(cleanupConfirmed: true);
                if (cleanup.DeferredErrors.Length > 0)
                    log.Write("OLD_RECORDS_CLEANUP_DEFERRED", "部分舊檔暫時無法刪除；保留隔離資料，下次開啟續清，不讀取或重播。", cleanup);
                log.Write(cleanup.Success ? "SESSION_RECORDS_CLEARED" : "SESSION_RECORDS_CLEANUP_DEFERRED",
                    cleanup.Success ? "本次商店搜尋紀錄已刪除；商品保留給網頁，下一次開啟再刪除。"
                        : "後端與導航已停止；部分商店紀錄暫時無法刪除，保留隔離資料待下次開啟續清。", cleanup);
            }
            log.Write("STARTUP_TIMINGS", "本次啟動分段時間；背景副本核對與歷史就緒分開記錄。", new { stagesMs = StartupTiming.Snapshot() });
            log.Write("CLOSE_COMPLETED", "本機搜尋與讀取已停止，現在關閉程式。");
            closeCompleted = true;
        }
        catch (Exception error)
        {
            log.Write("CLOSE_FAILED", "停止流程回報錯誤；同一停止工作不會重新執行，保留視窗與 LOG 供排查。", new { error = error.ToString() });
            scanStatus.Text = "停止流程失敗，本次停止工作無法重試；請保留並檢查 LOG。";
            sourceStatus.Text = "停止失敗：" + error.Message;
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            // Normal window closing awaits this work above. An explicit Dispose
            // must also finish owned work before releasing the UI and its log.
            if (!closeCompleted)
            {
                closing = true; frameTimer.Stop(); lifetime.Cancel(); connection?.Cancel();
                patrolCancellation?.Cancel(); searchResetCancellation?.Cancel();
                Task.Run(async () =>
                {
                    // The UI continuation of RestoreHistoryAsync cannot run while
                    // synchronous Dispose blocks this thread. Join only its current
                    // owned I/O operation, after cancellation, before disposing resources.
                    if (historyIoTask is not null)
                    {
                        try { await historyIoTask.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                        catch (Exception error) { log.Write("HISTORY_IO_SHUTDOWN_ERROR", error.Message); }
                    }
                    if (patrolTask is not null) await patrolTask;
                    await patrolNavigation.DisposeAsync();
                    await backend.DisposeAsync();
                    if (sessionLifecycle is not null)
                    {
                        var cleanup = await sessionLifecycle.CleanupAfterStopAsync(cleanupConfirmed: true);
                        if (!cleanup.Success || cleanup.DeferredErrors.Length > 0)
                            log.Write("SESSION_RECORDS_CLEANUP_DEFERRED", "工作已停止；未刪除的隔離商店紀錄下次開啟續清。", cleanup);
                    }
                }).GetAwaiter().GetResult();
                closeCompleted = true;
            }
            resourcesDisposed = true; StopDebugApiV1(); debugApiTimer.Dispose();
            overlay.Dispose(); searchOverlay.Dispose(); log.Dispose();
            frameTimer.Dispose(); connection?.Dispose(); lifetime.Dispose(); patrolCancellation?.Dispose(); searchResetCancellation?.Dispose();
        }
        base.Dispose(disposing);
    }
    private static bool ParseNumber(string text, out ulong? value)
    { value = null; if (string.IsNullOrWhiteSpace(text)) return true; if (!ulong.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return false; value = n; return true; }
    private static string Format(ShopPoint? point) => point is null ? "未知" : $"{point.X},{point.Y}";
    private static string Yes(bool value) => value ? "是" : "否";
    private void AddColumn(string property, string title, int width, MarketSortField sortKey = MarketSortField.None)
    {
        baseColumnTitles[property] = title;
        foreach (var side in Sides) side.Grid.Columns.Add(new DataGridViewTextBoxColumn
        { DataPropertyName = property, Name = property, HeaderText = title, Width = width, SortMode = DataGridViewColumnSortMode.Programmatic, Tag = sortKey });
    }
    private static TextBox Entry(int width) => new() { Width = width, Margin = new Padding(3, 5, 10, 3) };
    private static CheckBox Check(string text, bool value) => new() { Text = text, Checked = value, AutoSize = true, Margin = new Padding(4, 8, 12, 6) };
    private static Button Button(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(6, 3, 6, 3), Margin = new Padding(3, 3, 8, 3) };
    private static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 6) };
    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = true, Margin = new Padding(0) };
}
