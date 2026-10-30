using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

/// <summary>Actual MainForm layout with isolated history and fake backend; never constructs CurrentLcSource.</summary>
internal static class MainFormLayoutSelfTest
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static int Run(string directory)
    {
        string output = Path.GetFullPath(directory);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("The layout evidence directory must be new.");
        Directory.CreateDirectory(output);
        nint foreground = GetForegroundWindow();
        using var focus = new ForegroundObserver();
        var checks = new List<object>();
        var evidence = new List<object>();
        var sources = new List<LayoutSource>();
        var commands = new List<LayoutCommands>();
        int passed = 0, failed = 0;
        bool shown = false, allOffscreen = true;
        Rectangle working = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var scenarios = new[]
        {
            new Scenario("desktop-short", new(1320, 980), false, 1),
            new Scenario("desktop-long", new(1320, 980), true, 1),
            new Scenario("minimum-short", new(1120, 800), false, 1),
            new Scenario("minimum-long", new(1120, 800), true, 1),
            new Scenario("actual-minimum-long", new(960, 640), true, 1),
            new Scenario("small-work-area", new(1280, 680), true, 1),
            new Scenario("font-125-long", new(1320, 980), true, 1.25f),
            new Scenario("font-150-long", new(1320, 980), true, 1.5f),
            new Scenario("current-work-area", new(Math.Min(1320, working.Width), Math.Min(980, working.Height)), true, 1)
        };

        foreach (var scenario in scenarios)
        {
            try
            {
                string root = Path.Combine(output, scenario.Name);
                Directory.CreateDirectory(root);
                var store = new LocalStore(Path.Combine(root, "state"));
                store.SavePreferences(new() { LogEnabled = false, ShowHistory = true, ShowNavigation = false, ShowSearchOverlay = false });
                SeedHistory(store);
                var source = new LayoutSource(); sources.Add(source);
                var command = new LayoutCommands(); commands.Add(command);
                var backend = new StandaloneMarketBackend(Path.Combine(root, "fake-backend"), _ => source, (_, _, _) => command);
                try
                {
                    using var host = new OffscreenHost(scenario.Viewport);
                    using var form = new MainForm(store, backend);
                    form.TopLevel = false; form.StartPosition = FormStartPosition.Manual;
                    form.Location = Point.Empty;
                    if (scenario.FontScale != 1)
                        form.Font = new Font(form.Font.FontFamily, form.Font.Size * scenario.FontScale, form.Font.Style);
                    form.Size = scenario.Viewport;
                    host.Controls.Add(form);
                    SetStatus(form, scenario.LongStatus);
                    form.Show(); host.Show();
                    shown |= form.Visible && host.Visible;
                    CreateHandles(form); Layout(form); Application.DoEvents(); Layout(form);
                    Rectangle hostBounds = host.RectangleToScreen(host.ClientRectangle);
                    bool offscreen = Screen.AllScreens.All(screen => !screen.Bounds.IntersectsWith(hostBounds));
                    allOffscreen &= offscreen;

                    var patrol = Field<Button>(form, "patrolButton");
                    var homePatrol = Field<Button>(form, "startSavedPatrolButton");
                    var readinessButton = Field<Button>(form, "checkNativeReadinessButton");
                    var showListings = Field<Button>(form, "showListingsButton");
                    var clearSearchHistory = Field<Button>(form, "clearSearchHistory");
                    var searchRecords = Field<Button>(form, "searchRecords");
                    var resetSearch = Field<Button>(form, "resetSearchButton");
                    var pageWait = Field<NumericUpDown>(form, "pageWaitMilliseconds");
                    var pageWaitSettings = Descendants(form).OfType<FlowLayoutPanel>().Single(panel => panel.Name == "PageWaitSettings");
                    var pages = Field<TabControl>(form, "marketPages");
                    var buy = Field<MarketSideGrid>(form, "buyGrid");
                    var sell = Field<MarketSideGrid>(form, "sellGrid");
                    var rootLayout = form.Controls.OfType<TableLayoutPanel>().Single();
                    var patrolBounds = Measure(patrol, host);
                    var homePatrolBounds = Measure(homePatrol, host);
                    var showListingsBounds = Measure(showListings, host);
                    var clearHistoryBounds = Measure(clearSearchHistory, host);
                    var buyBounds = MeasureGrid(buy.Grid, host);
                    var sellBounds = MeasureGrid(sell.Grid, host);
                    var tables = Descendants(form).OfType<TableLayoutPanel>().Select(table => new
                    {
                        Bounds = Measure(table, host), RowHeights = table.GetRowHeights(), ColumnWidths = table.GetColumnWidths(),
                        RowStyles = table.RowStyles.Cast<RowStyle>().Select(style => new { Type = style.SizeType.ToString(), style.Height }).ToArray(),
                        ColumnStyles = table.ColumnStyles.Cast<ColumnStyle>().Select(style => new { Type = style.SizeType.ToString(), style.Width }).ToArray()
                    }).ToArray();
                    bool frameTimerStarted = Field<System.Windows.Forms.Timer>(form, "frameTimer").Enabled;
                    bool watchStarted = Field<CancellationTokenSource?>(form, "connection") is not null || Field<int>(form, "generation") != 0;
                    var patrolNavigation = Field<PatrolNavigationClient>(form, "patrolNavigation");
                    bool navigationStarted = Field<object?>(patrolNavigation, "process") is not null;
                    string navigationDiagnosticsRoot = Field<string>(patrolNavigation, "root");
                    string expectedNavigationDiagnosticsRoot = Path.GetFullPath(Path.Combine(store.DiagnosticsRoot, "navigation-runtime"));
                    string png = Path.Combine(root, "main-window.png");
                    using (var bitmap = new Bitmap(host.ClientSize.Width, host.ClientSize.Height))
                    {
                        host.DrawToBitmap(bitmap, new Rectangle(Point.Empty, host.Size));
                        bitmap.Save(png, ImageFormat.Png);
                    }
                    var details = new
                    {
                        scenario.Name, RequestedViewport = scenario.Viewport, ActualFormSize = form.Size, ActualClientSize = form.ClientSize,
                        ActualDpi = form.DeviceDpi, ActualFontSize = form.Font.Size, scenario.FontScale,
                        FontScaleIsLayoutStressOnly = scenario.FontScale != 1, SystemWorkingArea = working,
                        ShownWindow = form.Visible && host.Visible, Offscreen = offscreen, ForegroundUnchanged = GetForegroundWindow() == foreground,
                        OwnedProcessForegroundEvents = focus.OwnedEvents, TestProcessTookForeground = focus.OwnsForeground,
                        FrameTimerStarted = frameTimerStarted, WatchStarted = watchStarted, NavigationStarted = navigationStarted,
                        NavigationDiagnosticsRoot = navigationDiagnosticsRoot, ExpectedNavigationDiagnosticsRoot = expectedNavigationDiagnosticsRoot,
                        FakeCommandRequests = command.RequestCount, FakeReadCalls = source.ReadCount,
                        Patrol = patrolBounds, HomePatrol = homePatrolBounds, ShowListings = showListingsBounds, ClearSearchHistory = clearHistoryBounds,
                        ResetSearch = Measure(resetSearch, host), PageWait = Measure(pageWait, host),
                        PageWaitSettings = Measure(pageWaitSettings, host), Buy = buyBounds, Sell = sellBounds,
                        RootRowHeights = rootLayout.GetRowHeights(), RootColumnWidths = rootLayout.GetColumnWidths(), Tables = tables,
                        Controls = Descendants(form).Select(control => Measure(control, host)).ToArray(), Image = png
                    };
                    WriteNew(Path.Combine(root, "bounds.json"), details);
                    evidence.Add(details);

                    Need(form.Visible && host.Visible && offscreen && !focus.OwnsForeground && focus.OwnedEvents == 0,
                        "The real MainForm fixture must be visible offscreen without taking foreground focus.");
                    Need(!frameTimerStarted && !watchStarted && !navigationStarted && command.RequestCount == 0,
                        "Showing the offline MainForm started automatic observation, navigation or commands.");
                    Need(string.Equals(navigationDiagnosticsRoot, expectedNavigationDiagnosticsRoot, StringComparison.OrdinalIgnoreCase),
                        "The actual MainForm navigation diagnostics root must survive session cleanup: " + navigationDiagnosticsRoot);
                    Need(form.Size == scenario.Viewport,
                        $"The real form grew beyond the requested viewport: requested={scenario.Viewport}, actual={form.Size}.");
                    Need(patrol.Text == "巡迴點設定" && patrol.Enabled && patrolBounds.FullyVisible,
                        "The patrol settings entry is clipped or disabled: " + Describe(patrolBounds));
                    Need(homePatrol.Text == "開始巡迴" && homePatrolBounds.FullyVisible && !homePatrol.Enabled,
                        "The homepage patrol button is clipped or can start without a ready source: " + Describe(homePatrolBounds));
                    Need(showListings.Text == "買賣清單" && showListings.Enabled && showListingsBounds.FullyVisible,
                        "The persistent return-to-listings button is clipped or disabled.");
                    Need(clearSearchHistory.Text == "清除擺攤搜尋紀錄" && clearHistoryBounds.FullyVisible && !clearSearchHistory.Enabled
                        && searchRecords.Enabled && Measure(searchRecords, host).FullyVisible,
                        "History entry is clipped, or clearing is enabled without confirmed backend eligibility.");
                    Need(Measure(readinessButton, host).FullyVisible && !readinessButton.Enabled, "The readonly readiness entry is clipped or enabled without a source.");
                    Need(Field<Button>(form, "clearListings").Text == "清除商品", "Product clearing is still confused with search-history clearing.");
                    Need(resetSearch.Text == "清除並從頭開始" && resetSearch.Enabled && Measure(resetSearch, host).FullyVisible,
                        "The local reset/restart entry is clipped or requires an available game source.");
                    Need(pageWait.Visible && pageWait.Minimum == 100 && pageWait.Maximum == 5000 && pageWait.Value == 3000 && pageWait.Increment == 100 && Measure(pageWait, host).FullyVisible,
                        "The configurable page wait is clipped or missing the 100..5000 ms numeric bounds or retained 3000 ms default.");
                    var waitParts = pageWaitSettings.Controls.Cast<Control>().ToArray();
                    var noWait = Field<CheckBox>(form, "noAdditionalPageWait");
                    Need(waitParts.Length == 4 && waitParts[0] == noWait && noWait.Checked && waitParts[1].Text == "開頁間隔" && waitParts[2] == pageWait && waitParts[3].Text == "毫秒"
                        && pageWaitSettings.Parent?.Name == "UtilityCommands" && Measure(pageWaitSettings, host).FullyVisible
                        && waitParts.All(part => Measure(part, host).FullyVisible)
                        && waitParts.Max(part => part.Bounds.Top) < waitParts.Min(part => part.Bounds.Bottom)
                        && Enumerable.Range(0, waitParts.Length - 1).All(i => waitParts[i].Bounds.Right <= waitParts[i + 1].Bounds.Left),
                        "The no-extra-wait checkbox, wait label, value and millisecond unit are split across toolbar rows, clipped or out of order.");
                    Need(typeof(MainForm).GetField("miniMapHost", Fields) is null && typeof(MainForm).GetMethod("UpdateMiniMap", Fields) is null,
                        "The removed mini-map host is still compiled into MainForm.");
                    Need(buyBounds.VisibleHeaderAndThreeRows,
                        "The buy table does not visibly contain its header and three complete rows: " + DescribeGrid(buyBounds));
                    Need(sellBounds.VisibleHeaderAndThreeRows,
                        "The sell table does not visibly contain its header and three complete rows: " + DescribeGrid(sellBounds));
                    foreach (var grid in new[] { buy.Grid, sell.Grid })
                    {
                        var stateColumn = grid.Columns[nameof(MarketRowView.ItemKind)]!;
                        int requiredTextWidth = TextRenderer.MeasureText("受祝福（已鑑定；版本認證）", grid.Font).Width + 16;
                        Need(stateColumn.Width >= requiredTextWidth,
                            $"Certified item-state text is clipped by its column: width={stateColumn.Width}, required={requiredTextWidth}, font={grid.Font.Size}.");
                    }
                    var stateFilter=Field<ComboBox>(form,"itemStateFilter");
                    var grouping=Field<CheckBox>(form,"groupStatePrices");
                    Need(stateFilter.Items.Count==6 && !grouping.Checked,"Raw state choices missing or price grouping default changed");
                    stateFilter.SelectedIndex=1;Application.DoEvents();
                    Need(buy.Grid.Rows.Count==1&&sell.Grid.Rows.Count==1,"Raw state filter did not separate both buy/sell rows");
                    grouping.Checked=true;Application.DoEvents();
                    Need(buy.Grid.Rows.Count==1&&sell.Grid.Rows.Count==1&&command.RequestCount==0,"State grouping changed count or invoked command");
                    int normalIndex=Enumerable.Range(0,stateFilter.Items.Count).Single(index=>stateFilter.Items[index]!.ToString()!.StartsWith("一般",StringComparison.Ordinal));
                    stateFilter.SelectedIndex=normalIndex;Application.DoEvents();
                    Need(buy.Grid.Rows.Count==2&&sell.Grid.Rows.Count==2,"Verified unidentified did not join effective normal filter");
                    Need(buy.Grid.Rows.Cast<DataGridViewRow>().Any(row=>((MarketRowView)row.DataBoundItem).ItemKind.Contains("未鑑定")),"Policy classification lost unidentified audit display");
                    stateFilter.SelectedIndex=0;grouping.Checked=false;Application.DoEvents();
                    Need(buy.Grid.Rows.Count==6&&sell.Grid.Rows.Count==6,"Clearing state filter did not restore rows");
                    int originalBuyRows = buy.Grid.Rows.Count, originalSellRows = sell.Grid.Rows.Count;
                    pages.SelectedIndex = 1; Layout(form); Application.DoEvents();
                    Need(!buy.Visible && !sell.Visible && Measure(showListings, host).FullyVisible,
                        "The settings-page reproduction or persistent listings entry was not established.");
                    showListings.PerformClick(); Layout(form); Application.DoEvents();
                    Need(pages.SelectedIndex == 0 && MeasureGrid(buy.Grid, host).VisibleHeaderAndThreeRows
                        && MeasureGrid(sell.Grid, host).VisibleHeaderAndThreeRows
                        && buy.Grid.Rows.Count == originalBuyRows && sell.Grid.Rows.Count == originalSellRows,
                        "Returning from settings did not restore the complete tables and their existing rows.");
                    foreach (string reason in new[] { "RestartSearch", "ContinueSearch", "ResumeSearch", "StartPatrol" })
                    {
                        pages.SelectedIndex = 1;
                        typeof(MainForm).GetMethod("ShowMarketListings", Fields)!.Invoke(form, [reason]);
                        Need(pages.SelectedIndex == 0 && buy.Visible && sell.Visible,
                            "An explicit search/patrol request did not return to the listings page: " + reason);
                    }
                    // Forced history commands remain available during a patrol or
                    // its local stop cleanup. Rendering that availability must not
                    // send a command or launch navigation in this fake fixture.
                    try
                    {
                        typeof(MainForm).GetField("broker", Fields)!.SetValue(form, new ShopCommandResponse
                        { State = "State", WorkSettled = true, SearchHistory = new() { Revision = "layout-r1", Epoch = "layout-e1", CanClear = true, UncertainPages = 2 } });
                        typeof(MainForm).GetField("brokerReceivedAt", Fields)!.SetValue(form, DateTimeOffset.UtcNow);
                        // homePatrolRequested is a retained status-display marker, not an active-work flag.
                        typeof(MainForm).GetField("homePatrolRequested", Fields)!.SetValue(form, true);
                        typeof(MainForm).GetMethod("UpdateButtons", Fields)!.Invoke(form, null);
                        Need(clearSearchHistory.Enabled, "Idle local history clearing incorrectly depends on LC presence, old unknown pages or a completed homepage request.");
                        typeof(MainForm).GetField("patrolRunning", Fields)!.SetValue(form, true);
                        typeof(MainForm).GetMethod("UpdateButtons", Fields)!.Invoke(form, null);
                        Need(homePatrol.Text == "停止巡迴" && homePatrol.Enabled && Measure(homePatrol, host).FullyVisible,
                            "An active patrol cannot be stopped from the homepage during a reader gap.");
                        Need(clearSearchHistory.Enabled && Measure(clearSearchHistory, host).FullyVisible,
                            "Active patrol observation disabled or hid the explicitly authorized force-history command.");
                        Need(resetSearch.Enabled && Measure(resetSearch, host).FullyVisible,
                            "The reset transaction cannot be requested during an active patrol.");
                        Need(command.RequestCount == 0 && Field<object?>(Field<PatrolNavigationClient>(form, "patrolNavigation"), "process") is null,
                            "Rendering force-history availability during an active patrol dispatched a phantom command or started navigation.");
                        typeof(MainForm).GetField("patrolStopRequested", Fields)!.SetValue(form, true);
                        typeof(MainForm).GetMethod("UpdateButtons", Fields)!.Invoke(form, null);
                        Need(homePatrol.Text == "巡迴停止中…" && !homePatrol.Enabled && Measure(homePatrol, host).FullyVisible
                            && clearSearchHistory.Enabled && Measure(clearSearchHistory, host).FullyVisible,
                            "Local patrol-stop ownership lost its visible status or incorrectly disabled the force-history command.");
                        Need(command.RequestCount == 0 && Field<object?>(Field<PatrolNavigationClient>(form, "patrolNavigation"), "process") is null,
                            "Rendering a pending local patrol stop dispatched a phantom command or started navigation.");
                    }
                    finally
                    {
                        typeof(MainForm).GetField("patrolRunning", Fields)!.SetValue(form, false);
                        typeof(MainForm).GetField("patrolStopRequested", Fields)!.SetValue(form, false);
                        typeof(MainForm).GetField("homePatrolRequested", Fields)!.SetValue(form, false);
                        typeof(MainForm).GetField("broker", Fields)!.SetValue(form, null);
                        typeof(MainForm).GetMethod("UpdateButtons", Fields)!.Invoke(form, null);
                    }
                    var readinessSource = new SourceIdentity(8123, 4567, "LC.exe", "0x140000000",
                        MarketNativeCompatibility.SupportedTimestamp, MarketNativeCompatibility.SupportedImageSize,
                        MarketNativeCompatibility.SupportedSha256, "offline-readiness-source");
                    var now = DateTimeOffset.UtcNow;
                    var checkedReady = new ShopNativeReadiness { Source = readinessSource, BuildCompatible = true,
                        Ready = true, Code = "NativeReady", Detail = "離線已就緒", SampledAt = now, ExpiresAt = now.AddMilliseconds(HubProtocol.FreshMilliseconds) };
                    try
                    {
                        typeof(MainForm).GetField("snapshot", Fields)!.SetValue(form, new HubSnapshot { Source = readinessSource });
                        typeof(MainForm).GetField("lastNativeReadiness", Fields)!.SetValue(form,
                            checkedReady with { Ready = false, Code = "NativeInitializationRequired", Detail = "請正常開店後重新檢查" });
                        typeof(MainForm).GetMethod("UpdateButtons", Fields)!.Invoke(form, null);
                        Need(readinessButton.Enabled, "An unavailable entry permanently disabled the retry check.");
                        typeof(MainForm).GetField("lastNativeReadiness", Fields)!.SetValue(form,
                            checkedReady with { SampledAt = now.AddSeconds(-20), ExpiresAt = now.AddSeconds(-10) });
                        Need(((string)typeof(MainForm).GetMethod("NativeReadinessDetail", Fields)!.Invoke(form, null)!).Contains("過期"),
                            "Expired readiness remained displayed as current.");
                        var priorFailure = new ShopCommandResponse { Scan = new MarketScanState { JobId = "old-scan",
                            FailureCode = "NativeCodeUnavailable", FailureRva = "0x76BEB0" } };
                        typeof(MainForm).GetMethod("ObserveNativeReadiness", Fields)!.Invoke(form, [priorFailure]);
                        typeof(MainForm).GetField("lastNativeReadiness", Fields)!.SetValue(form, checkedReady);
                        typeof(MainForm).GetMethod("ObserveNativeReadiness", Fields)!.Invoke(form,
                            [new ShopCommandResponse { RequestId = "old-scan", FailureCode = "NativeCodeUnavailable", FailureRva = "0x76BEB0" }]);
                        Need(Field<ShopNativeReadiness?>(form, "lastNativeReadiness") == checkedReady,
                            "The same failure changing between scan and request envelopes revoked a newer check.");
                        foreach (string pollId in new[] { "poll-1", "poll-2" })
                        {
                            typeof(MainForm).GetMethod("ObserveNativeReadiness", Fields)!.Invoke(form,
                                [priorFailure with { RequestId = pollId, State = "State", FailureCode = priorFailure.Scan.FailureCode, FailureRva = priorFailure.Scan.FailureRva }]);
                            Need(Field<ShopNativeReadiness?>(form, "lastNativeReadiness") == checkedReady,
                                "Polling the same failed scan with a new request ID revoked a newer ready check.");
                        }
                        typeof(MainForm).GetMethod("ObserveNativeReadiness", Fields)!.Invoke(form, [new ShopCommandResponse()]);
                        typeof(MainForm).GetMethod("ObserveNativeReadiness", Fields)!.Invoke(form, [priorFailure]);
                        Need(Field<ShopNativeReadiness?>(form, "lastNativeReadiness") == checkedReady,
                            "A poll carrying an old scan failure revoked a newer explicit check.");
                        typeof(MainForm).GetMethod("ObserveNativeReadiness", Fields)!.Invoke(form,
                            [priorFailure with { Scan = priorFailure.Scan with { JobId = "new-scan" } }]);
                        Need(Field<ShopNativeReadiness?>(form, "lastNativeReadiness") is null,
                            "A new current native-code failure retained the old ready observation.");
                        typeof(MainForm).GetField("lastNativeReadiness", Fields)!.SetValue(form, checkedReady);
                        typeof(MainForm).GetField("snapshot", Fields)!.SetValue(form, new HubSnapshot { Source = readinessSource with { CreationFileTime = 9999 } });
                        Need(((string)typeof(MainForm).GetMethod("NativeReadinessDetail", Fields)!.Invoke(form, null)!).Contains("尚未檢查"),
                            "A changed source reused another process's ready observation.");
                    }
                    finally
                    {
                        typeof(MainForm).GetField("snapshot", Fields)!.SetValue(form, null);
                        typeof(MainForm).GetField("lastNativeReadiness", Fields)!.SetValue(form, null);
                        typeof(MainForm).GetMethod("UpdateButtons", Fields)!.Invoke(form, null);
                    }
                    Need(command.RequestCount == 0 && Field<object?>(Field<PatrolNavigationClient>(form, "patrolNavigation"), "process") is null,
                        "Changing homepage patrol presentation dispatched a command or started navigation.");
                }
                finally { backend.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                checks.Add(new { Name = "main_layout_" + scenario.Name, Passed = true }); passed++;
            }
            catch (Exception error)
            {
                checks.Add(new { Name = "main_layout_" + scenario.Name, Passed = false, Error = error.ToString() }); failed++;
            }
        }

        bool foregroundUnchanged = GetForegroundWindow() == foreground;
        var report = new
        {
            Version = "83.0.0", Success = failed == 0 && !focus.OwnsForeground && focus.OwnedEvents == 0 && commands.All(command => command.RequestCount == 0),
            Passed = passed, Failed = failed, ActualMainForm = true, CreatedWindow = true, ShownWindow = shown,
            Offscreen = shown && allOffscreen, NoActivateHost = true, ForegroundUnchanged = foregroundUnchanged,
            OwnedProcessForegroundEvents = focus.OwnedEvents, FocusNeverTakenByTest = !focus.OwnsForeground && focus.OwnedEvents == 0,
            ExternalForegroundChanged = !foregroundUnchanged && !focus.OwnsForeground && focus.OwnedEvents == 0,
            CurrentLcSourceConstructed = false, OpenedGameProcess = false, ProcessMemoryReads = 0, NativeRequests = 0,
            FakeReadCalls = sources.Sum(source => source.ReadCount), FakeCommandRequests = commands.Sum(command => command.RequestCount),
            Checks = checks, Scenarios = evidence
        };
        WriteNew(Path.Combine(output, "result.json"), report);
        Console.WriteLine(JsonSerializer.Serialize(new { report.Success, report.Passed, report.Failed, report.FakeCommandRequests }));
        return report.Success ? 0 : 1;
    }

    private static void SeedHistory(LocalStore store)
    {
        var at = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        foreach (string direction in new[] { ShopPageModes.ViewerBuy, ShopPageModes.ViewerSell })
        {
            var listing = new MarketListing
            {
                SourceSessionKey = "offline-main-layout", Revision = "layout-" + direction, RequestId = "fixture-" + direction,
                Owner = new() { Id = direction == ShopPageModes.ViewerBuy ? 41UL : 42UL, LocalIdentity = "fixture-" + direction,
                    Name = direction == ShopPageModes.ViewerBuy ? "離線出售店主" : "離線收購店主", Position = new(32768, 32769), ObservedAt = at },
                ViewerDirection = direction, CapturedAt = at, RequestedAt = at.AddSeconds(-1),
                Complete = true, OwnerAndResponseMatched = true, State = "Verified", Detail = "Only fake rows for layout measurement.",
                Rows = Enumerable.Range(0, 6).Select(index => new PersonalShopRow(index, "Observed", true,
                    $"{(direction == ShopPageModes.ViewerBuy ? "出售" : "收購")}測試道具 {index + 1}", "100", 100, "每個", 1,
                    "Observed", "離線測試列") { ItemKindDetailFlag=(byte)(index%2), ItemKindRaw=index%3, ItemKind=index==1?"Normal":index==3?"Blessed":index==5?"Cursed":null, ItemKindState=index%2==1?PersonalShopItemKindRules.VerifiedState:"Unknown", ItemIdentificationState=index==2?"Unidentified":"Unknown", ItemIdentificationEvidence=index==2?"Verified":"Unverified" }).ToArray()
            };
            Need(store.SaveListing(listing), "Could not seed the isolated history fixture.");
        }
    }

    private static void SetStatus(MainForm form, bool longStatus)
    {
        Field<Label>(form, "sourceStatus").Text = longStatus
            ? "程式自行讀取 LC｜角色 ID 123456789｜即時座標 32768,32769｜大型市場地區｜來源尚在核對；保留現有商品清單與上次紀錄，請查看目前角色與讀取狀態。"
            : "離線版面測試｜角色與商品均為假資料";
        Field<Label>(form, "nearbyStatus").Text = longStatus
            ? "周遭刷新時間 2026-09-27 12:34:56｜已保存 128 位玩家｜正在重新刷新周遭；成功前停止使用原名單搜尋或開店，保存商品與座標仍可查看。"
            : "已保存 2 位測試店主";
        Field<Label>(form, "shopStateStatus").Text = longStatus
            ? "上次手動刷新：確認擺攤 128 位、非擺攤 64 位、未知 32 位。只搜尋確認擺攤者；其他狀態保留，歷史商品可繼續查看。原生元件來源仍須核對，不自動重送。"
            : "確認擺攤 2 位；僅離線展示";
        Field<Label>(form, "navigationStatus").Text = longStatus
            ? "店主「離線測試商人」保存座標 32768,32769｜資料來自上次手動刷新，導航方向與距離仍須目前同地圖角色核對。"
            : "尚未選擇商店";
    }

    private static GridBounds MeasureGrid(DataGridView grid, Control host)
    {
        var bounds = Measure(grid, host);
        int required = grid.ColumnHeadersHeight + grid.Rows.Cast<DataGridViewRow>().Take(3).Sum(row => row.Height) + 2;
        bool visible = grid.Visible && grid.ColumnHeadersVisible && grid.Rows.Count >= 3
            && bounds.EffectiveClient.Top == bounds.Client.Top && bounds.EffectiveClient.Height >= required
            && bounds.EffectiveClient.Width >= Math.Min(240, bounds.Client.Width) && bounds.EffectiveClient.Width >= 160;
        return new(bounds, grid.Rows.Count, grid.ColumnHeadersHeight, required, grid.DisplayedRowCount(false), visible);
    }

    private static ControlBounds Measure(Control control, Control host)
    {
        Point origin = host.PointToScreen(Point.Empty);
        Rectangle client = control.RectangleToScreen(control.ClientRectangle);
        Rectangle effective = client;
        for (Control? parent = control.Parent; parent is not null; parent = parent.Parent)
            effective = Rectangle.Intersect(effective, parent.RectangleToScreen(parent.ClientRectangle));
        effective = Rectangle.Intersect(effective, host.RectangleToScreen(host.ClientRectangle));
        client.Offset(-origin.X, -origin.Y);
        if (!effective.IsEmpty) effective.Offset(-origin.X, -origin.Y);
        string text = control.Text.Length > 180 ? control.Text[..180] : control.Text;
        return new(control.GetType().Name, control.Name, text, control.Visible, control.Enabled,
            control.Bounds, client, effective, control.Visible && client.Width > 0 && client.Height > 0 && effective == client);
    }

    private static string Describe(ControlBounds value) => $"client={value.Client}, visible={value.EffectiveClient}, Visible={value.Visible}";
    private static string DescribeGrid(GridBounds value) => $"{Describe(value.Bounds)}, rows={value.RowCount}, requiredHeight={value.RequiredHeight}";
    private static T Field<T>(object owner, string name) => (T)(owner.GetType().GetField(name, Fields)
        ?? throw new MissingFieldException(owner.GetType().Name, name)).GetValue(owner)!;
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void WriteNew(string path, object value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true);
    }
    private static void CreateHandles(Control control) { _ = control.Handle; foreach (Control child in control.Controls) CreateHandles(child); }
    private static void Layout(Control control) { control.PerformLayout(); foreach (Control child in control.Controls) Layout(child); }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (Control nested in Descendants(child)) yield return nested; }
    }
    private sealed record Scenario(string Name, Size Viewport, bool LongStatus, float FontScale);
    // A user may switch other applications during the fixture. Observe every
    // foreground event and fail if this test process ever takes focus; preserve
    // the global before/after result as separate evidence.
    private sealed class ForegroundObserver : IDisposable
    {
        private delegate void Callback(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);
        [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module, Callback callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
        private readonly Callback callback;
        private readonly nint hook;
        private int ownedEvents;
        internal int OwnedEvents => Volatile.Read(ref ownedEvents);
        internal bool OwnsForeground => Owned(GetForegroundWindow());
        private static bool Owned(nint window)
        { if (window == 0) return false; GetWindowThreadProcessId(window, out uint pid); return pid == Environment.ProcessId; }
        internal ForegroundObserver()
        {
            callback = (_, _, window, _, _, _, _) => { if (Owned(window)) Interlocked.Increment(ref ownedEvents); };
            hook = SetWinEventHook(3, 3, 0, callback, 0, 0, 0);
            if (hook == 0) throw new InvalidOperationException("Cannot observe foreground events for the offline fixture.");
        }
        public void Dispose() { UnhookWinEvent(hook); GC.KeepAlive(callback); }
    }
    private sealed record ControlBounds(string Type, string Name, string Text, bool Visible, bool Enabled,
        Rectangle Bounds, Rectangle Client, Rectangle EffectiveClient, bool FullyVisible);
    private sealed record GridBounds(ControlBounds Bounds, int RowCount, int HeaderHeight, int RequiredHeight,
        int DisplayedRows, bool VisibleHeaderAndThreeRows);

    private sealed class OffscreenHost : Form
    {
        internal OffscreenHost(Size size)
        {
            AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
            Bounds = new Rectangle(new Point(-32000, -32000), size);
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x00000080; return value; }
        }
    }

    private sealed class LayoutSource : ISlicedMemorySource
    {
        private int reads;
        internal int ReadCount => Volatile.Read(ref reads);
        public void Configure(ReadSelection selection) { }
        public ReadResult Read(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref reads);
            return new(null, new(), "OfflineLayoutFixture", "Fake source; no process enumeration or memory access.");
        }
        public ReadResult ReadSlice(ReadSelection fields, CancellationToken token) => Read(token);
        public void Dispose() { }
    }
    private sealed class LayoutCommands : IStandaloneShopCommands
    {
        private int requests;
        internal int RequestCount => Volatile.Read(ref requests);
        public Task<ShopCommandResponse> SendAsync(ShopCommandRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref requests);
            return Task.FromException<ShopCommandResponse>(new InvalidOperationException("Layout fixture forbids automatic commands."));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
