using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace LcPlayerMarket;

internal sealed record SearchOverlayView(string State, string Player, string Progress, string Detail, bool Busy)
{
    internal string? PatrolState { get; init; }
    internal DateTimeOffset? TransitionAt { get; init; }
}

internal sealed class SearchStatusOverlay : Form
{
    private SearchOverlayView? view;
    private uint dpi = 96;
    internal const int RequiredExtendedStyles = 0x20 | 0x80 | 0x80000 | 0x08000000;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ExStyle |= RequiredExtendedStyles; return value; }
    }

    public SearchStatusOverlay()
    {
        Text = "LC 玩家商店 V17 搜尋狀態";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = TransparencyKey = Color.Magenta;
        TopMost = true;
        DoubleBuffered = true;
        Opacity = 0.50;
    }

    public void Clear()
    {
        view = null;
        if (Visible) Hide();
    }

    public void Present(ClientFrame frame, SearchOverlayView value)
    {
        Rectangle target = CalculateBounds(frame);
        if (target.IsEmpty) { Clear(); return; }
        view = value;
        dpi = NormalizeDpi(frame.Dpi);
        // Set the first bounds before Show so the overlay never flashes at a default location.
        if (Bounds != target) Bounds = target;
        if (!Visible) Show();
        if (!SetWindowPos(Handle, (nint)(-1), target.X, target.Y, target.Width, target.Height, 0x0010 | 0x0040 | 0x0200))
        {
            int error = Marshal.GetLastWin32Error();
            Clear();
            throw new System.ComponentModel.Win32Exception(error, "搜尋狀態面板定位失敗");
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (view is not null) Render(e.Graphics, ClientRectangle, view, dpi);
    }

    internal static Rectangle CalculateBounds(ClientFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Width > 32000 || frame.Height > 32000)
            return Rectangle.Empty;
        float scale = NormalizeDpi(frame.Dpi) / 96f;
        int margin = Math.Min((int)Math.Round(8 * scale), Math.Max(0, (Math.Min(frame.Width, frame.Height) - 1) / 2));
        int width = Math.Min((int)Math.Round(360 * scale), frame.Width - margin * 2);
        int height = Math.Min((int)Math.Round(120 * scale), frame.Height - margin * 2);
        long x = (long)frame.X + margin, y = (long)frame.Y + margin;
        if (x < int.MinValue || y < int.MinValue || x + width > int.MaxValue || y + height > int.MaxValue)
            return Rectangle.Empty;
        return new Rectangle((int)x, (int)y, width, height);
    }

    internal static void Render(Graphics graphics, Rectangle bounds, SearchOverlayView value, uint currentDpi)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        GraphicsState saved = graphics.Save();
        try
        {
            graphics.SetClip(bounds, CombineMode.Intersect);
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using var background = new SolidBrush(Color.FromArgb(22, 28, 35));
            using var border = new Pen(Color.FromArgb(124, 142, 159));
            using var highlight = new SolidBrush(value.Busy ? Color.FromArgb(106, 231, 171) : Color.FromArgb(215, 220, 226));
            using var detailBrush = new SolidBrush(Color.FromArgb(199, 209, 220));
            graphics.FillRectangle(background, bounds);
            graphics.DrawRectangle(border, bounds.X, bounds.Y, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
            float scale = NormalizeDpi(currentDpi) / 96f;
            int lines = string.IsNullOrWhiteSpace(value.Detail) ? 3 : 4;
            float padding = Math.Min(10 * scale, Math.Max(1, Math.Min(bounds.Width, bounds.Height) / 10f));
            float textWidth = Math.Max(0, bounds.Width - 2 * padding);
            float rowHeight = Math.Max(0, (bounds.Height - 2 * padding) / lines);
            if (textWidth < 1 || rowHeight < 1) return;
            float fontSize = Math.Max(1, Math.Min(16 * scale, rowHeight * 0.73f));
            using var body = new Font("Microsoft JhengHei UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            using var heading = new Font("Microsoft JhengHei UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat(StringFormat.GenericDefault) {
                Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap
            };
            string[] text = ["搜尋：" + Clean(value.State, "停止"), "玩家：" + Clean(value.Player, "—"),
                "進度：" + Clean(value.Progress, "—"), Clean(value.Detail, "")];
            for (int index = 0; index < lines; index++)
            {
                var row = new RectangleF(bounds.X + padding, bounds.Y + padding + index * rowHeight, textWidth, rowHeight);
                graphics.DrawString(text[index], index == 0 ? heading : body, index == 0 ? highlight : detailBrush, row, format);
            }
        }
        finally { graphics.Restore(saved); }
    }

    private static string Clean(string? text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text.Replace('\r', ' ').Replace('\n', ' ').Trim();
    private static uint NormalizeDpi(uint value) => value == 0 ? 96 : Math.Clamp(value, 48u, 768u);

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x84) { message.Result = (nint)(-1); return; }
        if (message.Msg == 0x21) { message.Result = (nint)3; return; }
        base.WndProc(ref message);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
