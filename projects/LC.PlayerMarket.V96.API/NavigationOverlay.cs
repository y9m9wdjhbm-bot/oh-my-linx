using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using MemoryHub;

namespace LcPlayerMarket;

internal sealed record ClientFrame(nint Window, int X, int Y, int Width, int Height, uint Dpi);

internal static class WindowFrameReader
{
    private static string? checkedSession;
    private static nint checkedWindow;
    public static ClientFrame? Read(SourceIdentity source)
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0 || GetWindow(hwnd, 4) != 0 || !IsWindowVisible(hwnd) || IsIconic(hwnd)
            || GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid != source.ProcessId) return null;
        // OS process identity only. This client never opens LC memory or requests VM_READ.
        if (checkedSession != source.SessionKey || checkedWindow != hwnd)
        {
            try
            {
                using var process = Process.GetProcessById(source.ProcessId);
                if (process.HasExited || process.StartTime.ToUniversalTime().ToFileTimeUtc() != source.CreationFileTime || process.MainWindowHandle != hwnd) return null;
                checkedSession = source.SessionKey; checkedWindow = hwnd;
            }
            catch { return null; }
        }
        if (!GetClientRect(hwnd, out var rect)) return null;
        var p = new NativePoint();
        if (!ClientToScreen(hwnd, ref p)) return null;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        uint dpi = GetDpiForWindow(hwnd);
        if (width is < 100 or > 32000 || height is < 100 or > 32000 || dpi == 0 || GetForegroundWindow() != hwnd) return null;
        return new(hwnd, p.X, p.Y, width, height, dpi);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint hwnd, ref NativePoint p);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}

internal sealed class NavigationOverlay : Form
{
    private NavigationView? view;
    private ClientFrame? frame;
    private readonly Font big = new("Microsoft JhengHei UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font small = new("Microsoft JhengHei UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x20 | 0x80 | 0x80000 | 0x08000000; return p; }
    }
    public NavigationOverlay()
    {
        Text = "LC 玩家商店 V17 導航"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.Manual;
        BackColor = TransparencyKey = Color.Magenta; TopMost = true; DoubleBuffered = true;
    }
    public void Clear()
    {
        view = null; frame = null;
        if (Visible) Hide();
    }
    public void Present(ClientFrame current, NavigationView value)
    {
        frame = current; view = value;
        if (!Visible) Show();
        if (!SetWindowPos(Handle, (nint)(-1), current.X, current.Y, current.Width, current.Height, 0x0010 | 0x0040 | 0x0200))
        { Clear(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "導航標記定位失敗"); }
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (view is null || frame is null) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var target = new PointF(view.Target.X + view.Target.Width / 2, view.Target.Y + view.Target.Height / 2);
        bool centerOnScreen = !view.CoordinateOnly && target.X >= 0 && target.Y >= 0 && target.X < ClientSize.Width && target.Y < ClientSize.Height;
        if (centerOnScreen)
        {
            g.FillEllipse(Brushes.Black, target.X - 12, target.Y - 12, 24, 24);
            g.FillEllipse(Brushes.Red, target.X - 10, target.Y - 10, 20, 20);
            DrawArrow(g, new(target.X, Math.Max(8, target.Y - 76)), new(target.X, Math.Max(8, target.Y - 20)), Color.Red, 9);
        }
        else if (!view.CoordinateOnly)
        {
            // An out-of-frame rectangle only supplies direction: no clamped red target dot is invented.
            var d = Normalize(view.Direction);
            if (d is { } direction)
            {
                float cx = ClientSize.Width / 2f, cy = ClientSize.Height / 2f;
                float tx = Math.Abs(direction.X) < .0001f ? float.MaxValue : (cx - 26) / Math.Abs(direction.X);
                float ty = Math.Abs(direction.Y) < .0001f ? float.MaxValue : (cy - 26) / Math.Abs(direction.Y);
                float length = Math.Min(tx, ty);
                PointF tip = new(cx + direction.X * length, cy + direction.Y * length);
                DrawArrow(g, new(tip.X - direction.X * 58, tip.Y - direction.Y * 58), tip, Color.Red, 9);
            }
        }
        int panelWidth = Math.Min(300, ClientSize.Width - 16), left = ClientSize.Width - panelWidth - 8;
        g.FillRectangle(Brushes.Black, left, 8, panelWidth, 146);
        using var white = new Pen(Color.White, 1); g.DrawRectangle(white, left, 8, panelWidth, 146);
        var origin = new PointF(left + 57, 84);
        var unit = Normalize(view.Direction);
        if (unit is { } u) DrawArrow(g, new(origin.X - u.X * 24, origin.Y - u.Y * 24), new(origin.X + u.X * 32, origin.Y + u.Y * 32), Color.Red, 8);
        else g.DrawString(view.DistanceToSavedPosition ? "已到保存點" : "已抵達", small, Brushes.White, left + 8, 72);
        g.DrawString(Clip(view.Name, 16), big, Brushes.White, left + 12, 16);
        g.DrawString($"{(view.DistanceToSavedPosition ? "至保存點" : "相距")} {view.Distance} 格", big, Brushes.White, left + 108, 64);
        g.DrawString(view.CoordinateOnly ? $"{view.SavedPosition?.X},{view.SavedPosition?.Y}" : centerOnScreen ? "目標矩形在畫面範圍內" : "畫面邊緣顯示目標方向", small, Brushes.White, left + 108, 91);
        if (view.CoordinateOnly) g.DrawString(view.CoordinateNote, small, Brushes.Gold, left + 12, 111);
        g.DrawString(view.CoordinateOnly ? "保存點方向（推估）；店主可能已搬走" : view.Research ? "研究投影：位置仍待人工比對" : "MemoryHub 已驗證投影", small,
            view.Research ? Brushes.Gold : Brushes.LightGreen, left + 12, 128);
    }
    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "…";
    private static PointF? Normalize(PointF direction)
    {
        double length = Math.Sqrt((double)direction.X * direction.X + (double)direction.Y * direction.Y);
        return length < .01 || !double.IsFinite(length) ? null : new PointF((float)(direction.X / length), (float)(direction.Y / length));
    }
    private static void DrawArrow(Graphics graphics, PointF from, PointF to, Color color, float width)
    {
        using var cap = new AdjustableArrowCap(2.2f, 2.6f, true);
        using var outline = new Pen(Color.Black, width + 4) { StartCap = LineCap.Round, CustomEndCap = cap };
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, CustomEndCap = cap };
        graphics.DrawLine(outline, from, to); graphics.DrawLine(pen, from, to);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84) { m.Result = (nint)(-1); return; }
        if (m.Msg == 0x21) { m.Result = (nint)3; return; }
        base.WndProc(ref m);
    }
    protected override void Dispose(bool disposing)
    { if (disposing) { big.Dispose(); small.Dispose(); } base.Dispose(disposing); }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}

