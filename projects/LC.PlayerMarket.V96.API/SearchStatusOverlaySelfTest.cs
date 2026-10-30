using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LcPlayerMarket;

internal static class SearchStatusOverlaySelfTest
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    public static IReadOnlyList<OfflineCheck> Run(string outputDirectory)
    {
        var checks = new List<OfflineCheck>();
        nint foreground = GetForegroundWindow();
        void Check(string name, Action action)
        {
            try { action(); checks.Add(new(name, true, "通過")); }
            catch (Exception error) { checks.Add(new(name, false, error.Message)); }
        }
        static void Need(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
        var cases = new[] {
            ("normal96", new ClientFrame(0, 30, 40, 1280, 960, 96)),
            ("negative144", new ClientFrame(0, -1920, -400, 1600, 900, 144)),
            ("high192", new ClientFrame(0, 80, 90, 1600, 1200, 192)),
            ("small96", new ClientFrame(0, -120, 10, 160, 90, 96)),
            ("tiny288", new ClientFrame(0, -35, -40, 32, 24, 288))
        };
        foreach (var (name, frame) in cases)
        {
            Check("search_overlay_bounds_" + name, () => {
                Rectangle panel = SearchStatusOverlay.CalculateBounds(frame);
                Need(panel.Width > 0 && panel.Height > 0 && new Rectangle(frame.X, frame.Y, frame.Width, frame.Height).Contains(panel),
                    "面板超出遊戲 client frame 或未保留有效尺寸");
            });
        }
        Check("search_overlay_dpi_scaling", () => {
            Rectangle one = SearchStatusOverlay.CalculateBounds(cases[0].Item2);
            Rectangle two = SearchStatusOverlay.CalculateBounds(cases[2].Item2);
            Need(one.Size == new Size(360, 120) && two.Size == new Size(720, 240), "96/192 DPI 尺寸不符合比例");
        });
        Check("search_overlay_invalid_and_overflow_bounds", () => {
            Need(SearchStatusOverlay.CalculateBounds(new(0, 0, 0, 0, 100, 96)).IsEmpty &&
                SearchStatusOverlay.CalculateBounds(new(0, int.MaxValue - 5, 0, 500, 500, 96)).IsEmpty,
                "無效尺寸或座標溢位未拒絕");
        });
        Check("search_overlay_readonly_window_styles", () => {
            Need((SearchStatusOverlay.RequiredExtendedStyles & (0x20 | 0x80 | 0x80000 | 0x08000000)) ==
                (0x20 | 0x80 | 0x80000 | 0x08000000), "缺少穿透／工具／分層／不啟用樣式");
        });
        foreach (var (name, frame) in cases.Take(4))
        {
            Check("search_overlay_render_" + name, () => {
                Directory.CreateDirectory(outputDirectory);
                Rectangle panel = SearchStatusOverlay.CalculateBounds(frame);
                using var bitmap = new Bitmap(panel.Width + 8, panel.Height + 8);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.Magenta);
                var renderBounds = new Rectangle(4, 4, panel.Width, panel.Height);
                SearchStatusOverlay.Render(graphics, renderBounds, new("正在搜尋", "測試店主：扭曲的空間旁的長名稱玩家",
                    "第 12／38 位・買入清單", "等待商店回應；這是一段應在窄面板省略的說明文字。", true), frame.Dpi);
                bitmap.Save(Path.Combine(outputDirectory, "搜尋面板-" + name + ".png"), ImageFormat.Png);
                Need(bitmap.GetPixel(0, 0).ToArgb() == Color.Magenta.ToArgb() &&
                    bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1).ToArgb() == Color.Magenta.ToArgb(), "繪圖超出指定矩形");
                int lightPixels = 0;
                for (int y = 6; y < bitmap.Height - 6; y++)
                    for (int x = 6; x < bitmap.Width - 6; x++)
                    {
                        Color color = bitmap.GetPixel(x, y);
                        if (color.G > 150 && color.B > 130 && color.R < 250) lightPixels++;
                    }
                Need(lightPixels > 20, "面板只有背景，未繪出可辨識文字");
            });
        }
        Check("search_overlay_optional_detail_and_stopped", () => {
            using var bitmap = new Bitmap(360, 120);
            using var graphics = Graphics.FromImage(bitmap);
            SearchStatusOverlay.Render(graphics, new Rectangle(0, 0, 360, 120), new("暫停", "", "已完成 12 位", "", false), 96);
            Directory.CreateDirectory(outputDirectory);
            bitmap.Save(Path.Combine(outputDirectory, "搜尋面板-暫停.png"), ImageFormat.Png);
        });
        Check("search_overlay_foreground_unchanged", () =>
            Need(GetForegroundWindow() == foreground, "離線繪圖期間前景視窗改變；本測試未建立或顯示 Form"));
        Check("search_overlay_incompatible_build_render", () => {
            using var bitmap = new Bitmap(360, 120);
            using var graphics = Graphics.FromImage(bitmap);
            SearchStatusOverlay.Render(graphics, new Rectangle(0, 0, 360, 120), new("商店元件待更新", "",
                "搜尋尚未可用", "目前 LC 版本尚未相容；請更新商店查詢元件。", false), 96);
            Directory.CreateDirectory(outputDirectory);
            bitmap.Save(Path.Combine(outputDirectory, "搜尋面板-元件待更新.png"), ImageFormat.Png);
        });
        return checks;
    }
}
