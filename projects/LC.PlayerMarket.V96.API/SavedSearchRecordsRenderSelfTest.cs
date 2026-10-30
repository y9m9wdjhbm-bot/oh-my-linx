using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using MemoryHub;

namespace LcPlayerMarket;

internal static class SavedSearchRecordsRenderSelfTest
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    internal static int Run(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("離線 UI 輸出已存在，拒絕覆寫：" + output);
        Directory.CreateDirectory(output);
        var checks = new List<OfflineCheck>();
        var renderEvidence = new List<object>();
        bool createdWindow = false;
        nint foreground = GetForegroundWindow();
        void Check(string name, Action action)
        {
            try { action(); checks.Add(new(name, true, "通過")); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        static void Need(bool ok, string detail) { if (!ok) throw new InvalidOperationException(detail); }
        var at = DateTimeOffset.Parse("2026-09-27T01:00:00+08:00");
        var saved = new ShopSavedScanProgress
        {
            PlanId = "offline-render-only", SourceSessionKey = "fake-render-source", CreatedAt = at, UpdatedAt = at,
            TotalPages = 6, SearchedPages = 3, ConfirmedPages = 2, RemainingPages = 2, UncertainPages = 1,
            Owners = [
                new() { OwnerId = 123456, OwnerName = "繁體中文測試店主", BuyState = "Confirmed", SellState = "Sent",
                    BuyDetail = "完整清單已與店主及頁面核對", SellDetail = "請求已返回；清單未確認，不會接續重送", UpdatedAt = at },
                new() { OwnerId = 234567, OwnerName = "等待接續的玩家", BuyState = "NotSent", SellState = "NotSent",
                    BuyDetail = "尚未送出，需同來源刷新後接續", SellDetail = "尚未送出", UpdatedAt = at },
                new() { OwnerId = 345678, OwnerName = "結果待確認的玩家", BuyState = "Confirmed", SellState = "Uncertain",
                    BuyDetail = "已確認空頁", SellDetail = "操作結果尚未確認，保留鎖定", UpdatedAt = at }
            ]
        };
        foreach (var (name, size) in new[] { ("normal", new Size(1200, 650)), ("minimum", new Size(900, 460)) })
        {
            Check("saved_records_hidden_form_render_" + name, () =>
            {
                using var form = new SavedSearchRecordsForm { Size = size };
                _ = form.Handle; createdWindow = true;
                form.UpdateProgress(saved, saved.SourceSessionKey);
                int handleCount = CreateHiddenHandles(form);
                LayoutChildren(form);
                var grid = form.Controls.Find("SearchRecordGrid", true).OfType<DataGridView>().Single();
                Need(!form.Visible, "離線渲染不得顯示視窗。");
                Need(grid.ReadOnly && !grid.AllowUserToAddRows && !grid.AllowUserToDeleteRows, "搜尋記錄不是唯讀。");
                Need(grid.Columns.Count == 7 && grid.Rows.Count == 3, "玩家記錄或必要欄位沒有綁定到實際表格。");
                Need(grid.Width >= 800 && grid.Height >= 250, "最小視窗的表格空間不足或布局被文字撐開。");
                Need(grid.Parent!.ClientRectangle.Contains(grid.Bounds), "表格越出配置範圍。");
                Need(grid.Rows[0].Cells[nameof(SavedSearchRecordRow.OwnerName)].Value?.ToString() == "繁體中文測試店主"
                    && grid.Rows[0].Cells[nameof(SavedSearchRecordRow.BuyStatus)].Value?.ToString() == "已搜尋（清單已確認）"
                    && grid.Rows[0].Cells[nameof(SavedSearchRecordRow.SellStatus)].Value?.ToString() == "已搜尋（清單未確認）",
                    "實際表格的中文或買／賣狀態錯誤。");
                // A hidden Form's WM_PRINT can draw only its frame: inherited
                // Visible=false causes its child windows to be skipped. Render
                // the actual production Label/DataGridView controls separately,
                // then compose them at their real layout positions. Never Show.
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                Point clientOffset = form.PointToScreen(Point.Empty);
                clientOffset.Offset(-form.Left, -form.Top);
                int renderedControls = 0, summaryTextPixels = 0, gridTextDifferences = 0;
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    foreach (Control control in RenderedControls(form))
                    {
                        using var child = DrawControl(control);
                        Point location = RelativeLocation(control, form);
                        location.Offset(clientOffset);
                        graphics.DrawImageUnscaled(child, location);
                        renderedControls++;
                        if (control is Label label && label.Text.StartsWith("保存進度", StringComparison.Ordinal))
                            summaryTextPixels += CountDarkPixels(child);
                        if (ReferenceEquals(control, grid))
                        {
                            child.Save(Path.Combine(output, "搜尋記錄-" + name + "-grid.png"), ImageFormat.Png);
                            // Compare an actual bound Chinese cell with the same
                            // cell drawn with text colors masked to its background.
                            // Borders alone or a blank grid cannot satisfy this.
                            var cell = grid.Rows[0].Cells[nameof(SavedSearchRecordRow.OwnerName)];
                            var originalStyle = cell.Style;
                            var maskedStyle = cell.InheritedStyle.Clone();
                            maskedStyle.ForeColor = maskedStyle.BackColor;
                            maskedStyle.SelectionForeColor = maskedStyle.SelectionBackColor;
                            try
                            {
                                cell.Style = maskedStyle;
                                using var masked = DrawControl(grid);
                                // Hidden grids can report an empty visible-cell
                                // rectangle despite drawing all bound rows. Only
                                // this cell's text colors changed, so comparing
                                // the entire grid still verifies its actual text.
                                gridTextDifferences = CountDifferentPixels(child, masked, new Rectangle(Point.Empty, child.Size));
                            }
                            finally { cell.Style = originalStyle; }
                        }
                    }
                }
                bitmap.Save(Path.Combine(output, "搜尋記錄-" + name + ".png"), ImageFormat.Png);
                Need(renderedControls >= 3 && handleCount >= renderedControls + 1, "未建立或繪製完整子控制項。");
                Need(summaryTextPixels >= 40, "摘要 PNG 未繪出文字，不能只以資料繫結判定通過。");
                Need(gridTextDifferences >= 25, "玩家名稱文字未出現在實際表格 PNG；空白圖片不得通過。");
                Need(!form.Visible && GetForegroundWindow() == foreground, "隱藏渲染顯示了視窗或改變前景。");
                renderEvidence.Add(new { Name = name, HiddenHandles = handleCount, RenderedControls = renderedControls,
                    SummaryTextPixels = summaryTextPixels, GridTextPixelDifferences = gridTextDifferences,
                    GridWidth = grid.Width, GridHeight = grid.Height, FormVisible = form.Visible });
                grid.CurrentCell = grid.Rows[1].Cells[0];
                form.UpdateProgress(saved with { }, saved.SourceSessionKey);
                Need(grid.CurrentRow?.Index == 1, "未變更的背景進度使目前選列跳動。");
            });
        }
        foreach (var (name, size) in new[] { ("normal", new Size(1200, 650)), ("minimum", new Size(900, 460)) })
        {
            Check("searched_owners_hidden_paged_render_" + name, () =>
            {
                var requests = new List<ShopCommandRequest>();
                string revision = "ledger-r1";
                ShopCommandResponse Read(ShopCommandRequest request)
                {
                    requests.Add(request);
                    bool firstPage = request.HistoryAfterOwnerId is null;
                    var owners = Enumerable.Range(firstPage ? 1000 : 1100, firstPage ? 100 : 3)
                        .Select(id => new ShopSearchedOwnerRecord { OwnerId = (ulong)id, OwnerName = "持久擺攤玩家 " + id,
                            Position = new(32001, 32002), MapName = "測試市場", ObservedAt = at, LastSearchedAt = at,
                            BuyState = "Confirmed", SellState = "Sent" }).ToArray();
                    return new() { RequestId = request.RequestId, State = "SearchedOwners", SearchHistory = new()
                        { Revision = revision, Epoch = "ledger-epoch", OwnerCount = 103, SearchedPages = 206, ConfirmedPages = 103, CanClear = true },
                        SearchedOwners = owners, NextHistoryOwnerId = firstPage ? 1099UL : null, HistoryTruncated = firstPage };
                }
                using var form = new SavedSearchRecordsForm((request, token) => Task.FromResult(Read(request))) { Size = size };
                _ = form.Handle; createdWindow = true; form.SelectHistoryTab(); form.UpdateProgress(null, null);
                CreateHiddenHandles(form); LayoutChildren(form);
                Need(requests.Count == 0, "建構／背景摘要更新自行讀取全庫。");
                form.ReloadHistoryAsync().GetAwaiter().GetResult(); LayoutChildren(form);
                var grid = form.Controls.Find("SearchedOwnersGrid", true).OfType<DataGridView>().Single();
                var previous = form.Controls.Find("HistoryPrevious", true).OfType<Button>().Single();
                var next = form.Controls.Find("HistoryNext", true).OfType<Button>().Single();
                var reload = form.Controls.Find("HistoryReload", true).OfType<Button>().Single();
                Need(grid.Columns.Count == 8 && grid.Rows.Count == 100 && grid.ReadOnly && grid.Width >= 800 && grid.Height >= 250,
                    "持久紀錄的實際表格欄位、分頁或最小尺寸不符。");
                Need(grid.Parent!.ClientRectangle.Contains(grid.Bounds) && !previous.Enabled && next.Enabled && reload.Enabled,
                    "持久紀錄表格越界或第一頁按鈕不正確。");
                Need(grid.Rows[0].Cells[nameof(SearchHistoryRecordRow.Position)].Value?.ToString() == "32001, 32002"
                    && grid.Rows[0].Cells[nameof(SearchHistoryRecordRow.OwnerName)].Value?.ToString() == "持久擺攤玩家 1000",
                    "實際表格沒有記錄座標或繁體名稱。");
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                Point clientOffset = form.PointToScreen(Point.Empty); clientOffset.Offset(-form.Left, -form.Top);
                int textDifferences = 0, textPixels = 0;
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    foreach (Control control in RenderedControls(form))
                    {
                        using var child = DrawControl(control);
                        Point position = RelativeLocation(control, form); position.Offset(clientOffset); graphics.DrawImageUnscaled(child, position);
                        if (control.Name == "SearchHistorySummary") textPixels = CountDarkPixels(child);
                        if (!ReferenceEquals(control, grid)) continue;
                        child.Save(Path.Combine(output, "已搜尋擺攤-" + name + "-grid.png"), ImageFormat.Png);
                        var cell = grid.Rows[0].Cells[nameof(SearchHistoryRecordRow.Position)];
                        var original = cell.Style; var masked = cell.InheritedStyle.Clone();
                        masked.ForeColor = masked.BackColor; masked.SelectionForeColor = masked.SelectionBackColor;
                        try
                        {
                            cell.Style = masked;
                            using var maskedBitmap = DrawControl(grid);
                            textDifferences = CountDifferentPixels(child, maskedBitmap, new Rectangle(Point.Empty, child.Size));
                        }
                        finally { cell.Style = original; }
                    }
                }
                bitmap.Save(Path.Combine(output, "已搜尋擺攤-" + name + ".png"), ImageFormat.Png);
                Need(textDifferences >= 25 && textPixels >= 40, "持久紀錄 PNG 未真正繪出座標與摘要。");
                Need(requests.Count == 1 && requests[0].HistoryLimit == 100 && requests[0].HistoryAfterOwnerId is null,
                    "第一頁自動抓取了整庫或超出上限。");
                form.LoadNextHistoryPageAsync().GetAwaiter().GetResult();
                Need(requests.Count == 2 && requests[1].HistoryAfterOwnerId == 1099 && requests[1].ExpectedHistoryRevision == "ledger-r1"
                    && requests[0].RequestId != requests[1].RequestId && grid.Rows.Count == 3 && previous.Enabled && !next.Enabled,
                    "下一頁未使用新版請求／游標／版本，或誤累積全庫。");
                form.UpdateHistorySummary(new() { Revision = "ledger-r2", Epoch = "ledger-epoch", OwnerCount = 104 });
                Need(requests.Count == 2 && !previous.Enabled && !next.Enabled && reload.Enabled && grid.Rows.Count == 3,
                    "背景新版摘要應保留原分頁，不能偷抓全庫或混合版本。");
                revision = "ledger-r2"; form.ReloadHistoryAsync().GetAwaiter().GetResult();
                Need(requests.Count == 3 && requests[2].HistoryAfterOwnerId is null && requests[2].ExpectedHistoryRevision is null
                    && grid.Rows.Count == 100 && next.Enabled, "重新載入未重置分頁游標。");
                Need(!form.Visible && GetForegroundWindow() == foreground, "持久紀錄離線渲染顯示視窗或搶前景。");
                renderEvidence.Add(new { Name = "searched-owners-" + name, GridWidth = grid.Width, GridHeight = grid.Height,
                    SummaryTextPixels = textPixels, CoordinatePixelDifferences = textDifferences, ReadOnlyPageRequests = requests.Count,
                    MaximumPageSize = 100, NativeRequests = 0, FormVisible = form.Visible });
            });
        }
        Check("searched_owners_revision_rejection_preserves_display_without_retry", () =>
        {
            int reads = 0;
            using var form = new SavedSearchRecordsForm((request, token) =>
            {
                reads++;
                return Task.FromResult(new ShopCommandResponse { RequestId = request.RequestId, State = reads == 1 ? "SearchedOwners" : "HistoryChanged",
                    SearchHistory = new() { Revision = reads == 1 ? "r1" : "r2", Epoch = "epoch", OwnerCount = 3 },
                    SearchedOwners = reads == 1 ? [new() { OwnerId = 11, Position = new(1, 2) }] : [],
                    HistoryTruncated = reads == 1, NextHistoryOwnerId = reads == 1 ? 11UL : null });
            });
            _ = form.Handle; CreateHiddenHandles(form); form.SelectHistoryTab();
            form.ReloadHistoryAsync().GetAwaiter().GetResult(); form.LoadNextHistoryPageAsync().GetAwaiter().GetResult();
            var grid = form.Controls.Find("SearchedOwnersGrid", true).OfType<DataGridView>().Single();
            Need(reads == 2 && grid.Rows.Count == 1 && grid.Rows[0].Cells[nameof(SearchHistoryRecordRow.OwnerId)].Value?.ToString() == "11",
                "版本競態使原分頁清空或自行重送。");
            Need(!form.Controls.Find("HistoryNext", true).Single().Enabled, "版本競態後仍可接下一頁。");
        });
        Check("searched_history_clear_confirmation_gate_and_one_send", () =>
        {
            var history = new ShopSearchHistorySummary { Revision = "r1", Epoch = "e1", OwnerCount = 1, UncertainPages = 1, CanClear = true };
            int sends = 0;
            Task<ShopCommandResponse> Clear(ShopCommandRequest request, CancellationToken token)
            {
                sends++;
                Need(request.Command == ShopCommands.ClearSearchedOwners && request.ExpectedHistoryRevision == "r1", "清除指令缺少版本。");
                return Task.FromResult(new ShopCommandResponse { RequestId = request.RequestId, State = "SearchedOwnersCleared", SearchHistory = history with { Revision = "r2", Epoch = "e2", OwnerCount = 0 } });
            }
            Need(!ShopSearchHistoryPresentation.CanClear(history, false, true) && !ShopSearchHistoryPresentation.CanClear(history, true, false)
                && ShopSearchHistoryPresentation.CanClear(history, true, true), "清除 gate 錯誤或誤以舊未定頁封鎖清除。");
            ShopSearchHistoryPresentation.ClearAsync(history, false, Clear, default).GetAwaiter().GetResult(); Need(sends == 0, "取消確認仍送出。");
            var response = ShopSearchHistoryPresentation.ClearAsync(history, true, Clear, default).GetAwaiter().GetResult();
            Need(sends == 1 && response?.SearchHistory?.Epoch == "e2" && history.OwnerCount == 1, "清除重送或預先清空原摘要。");
        });
        Check("saved_records_render_foreground_unchanged", () => Need(GetForegroundWindow() == foreground, "離線渲染前景視窗改變。"));
        var report = new
        {
            Version = "42.0.0", Success = checks.All(x => x.Passed), Passed = checks.Count(x => x.Passed), Failed = checks.Count(x => !x.Passed),
            ConnectedToService = false, OpenedGameProcess = false, CreatedWindow = createdWindow,
            ShownWindow = false, ConstructedMainForm = false, NativeRequests = 0, RenderEvidence = renderEvidence, Checks = checks
        };
        using var file = new FileStream(Path.Combine(output, "result.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, report, HubProtocol.Json); file.Flush(true);
        return report.Success ? 0 : 1;
    }

    private static int CreateHiddenHandles(Control control)
    {
        _ = control.Handle;
        int count = 1;
        foreach (Control child in control.Controls) count += CreateHiddenHandles(child);
        return count;
    }

    private static void LayoutChildren(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls) LayoutChildren(child);
    }

    private static IEnumerable<Control> RenderedControls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is TabControl tabs)
            {
                yield return tabs;
                if (tabs.SelectedTab is { } selected)
                    foreach (Control descendant in RenderedControls(selected)) yield return descendant;
            }
            else if (child is Label or DataGridView or Button) yield return child;
            else foreach (Control descendant in RenderedControls(child)) yield return descendant;
        }
    }

    private static Bitmap DrawControl(Control control)
    {
        var bitmap = new Bitmap(control.Width, control.Height);
        using (var background = Graphics.FromImage(bitmap)) background.Clear(Color.Magenta);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
        return bitmap;
    }

    private static Point RelativeLocation(Control control, Control ancestor)
    {
        Point point = Point.Empty;
        for (Control? item = control; item != ancestor; item = item.Parent)
        {
            if (item is null) throw new InvalidOperationException("渲染控制項不屬於指定視窗。");
            point.Offset(item.Location);
        }
        return point;
    }

    private static int CountDarkPixels(Bitmap bitmap)
    {
        int count = 0;
        for (int y = 1; y < bitmap.Height - 1; y++)
            for (int x = 1; x < bitmap.Width - 1; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.R < 150 && pixel.G < 150 && pixel.B < 150) count++;
            }
        return count;
    }

    private static int CountDifferentPixels(Bitmap first, Bitmap second, Rectangle bounds)
    {
        bounds.Intersect(new Rectangle(Point.Empty, first.Size));
        int count = 0;
        for (int y = bounds.Top; y < bounds.Bottom; y++)
            for (int x = bounds.Left; x < bounds.Right; x++)
                if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) count++;
        return count;
    }
}
