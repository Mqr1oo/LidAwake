using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LidAwake
{
    // Colours and fonts that follow the Windows light/dark setting, so the menu looks like Windows 11's own.
    static class Theme
    {
        public static readonly bool Dark = ReadDark();
        public static readonly float Scale = ReadScale();
        public static readonly Color Bg = Dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
        public static readonly Color Border = Dark ? Color.FromArgb(62, 62, 62) : Color.FromArgb(222, 222, 222);
        public static readonly Color Fore = Dark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(26, 26, 26);
        public static readonly Color Muted = Dark ? Color.FromArgb(170, 170, 170) : Color.FromArgb(96, 96, 96);
        public static readonly Color Hover = Dark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(234, 234, 234);
        public static readonly Color Accent = Color.FromArgb(217, 119, 87);
        public static readonly Color Green = Color.FromArgb(108, 203, 95);
        public static readonly Color Warn = Color.FromArgb(232, 170, 60);
        public static readonly Font Title = MakeFont(10.5f, FontStyle.Bold);
        public static readonly Font Body = MakeFont(9f, FontStyle.Regular);
        public static readonly Font Small = MakeFont(7.5f, FontStyle.Bold);

        public static int S(float v) { return (int)Math.Round(v * Scale); }

        static bool ReadDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        static float ReadScale()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
        }

        static Font MakeFont(float size, FontStyle style)
        {
            var f = new Font("Segoe UI Variable Text", size, style);
            return f.Name == "Segoe UI Variable Text" ? f : new Font("Segoe UI", size, style);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Windows 11 rounded popup corners (ignored on Windows 10).
        public static void RoundCorners(IntPtr hwnd)
        {
            int round = 2; // DWMWCP_ROUND
            try { DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, 4); } catch { }
        }
    }

    // Top of the menu: icon, name, an ON/OFF badge and two lines of status.
    sealed class MenuHeader : Control
    {
        readonly Icon icon;
        bool on;
        string status = "", problem;

        public MenuHeader(Icon icon)
        {
            this.icon = icon;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Bg;
            FitSize();
        }

        public void Set(bool on, string status, string problem)
        {
            this.on = on;
            this.status = status;
            this.problem = problem;
            FitSize();
            Invalidate();
        }

        void FitSize()
        {
            Size = new Size(Theme.S(310), Theme.S(problem == null ? 84 : 104));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);

            int pad = Theme.S(14), iconSize = Theme.S(32);
            var iconRect = new Rectangle(pad, Theme.S(14), iconSize, iconSize);
            using (var big = new Icon(icon, iconSize, iconSize))
            {
                if (on) g.DrawIcon(big, iconRect);
                else using (var gray = App.Grayed(big)) g.DrawIcon(gray, iconRect);
            }

            int x = pad + iconSize + Theme.S(12);
            int y = Theme.S(12);
            TextRenderer.DrawText(g, "LidAwake", Theme.Title, new Point(x, y), Theme.Fore, TextFormatFlags.NoPadding);

            string badgeText = on ? Strings.BadgeOn : Strings.BadgeOff;
            Size titleSize = TextRenderer.MeasureText(g, "LidAwake", Theme.Title, Size.Empty, TextFormatFlags.NoPadding);
            Size bt = TextRenderer.MeasureText(g, badgeText, Theme.Small, Size.Empty, TextFormatFlags.NoPadding);
            var badge = new RectangleF(x + titleSize.Width + Theme.S(8), y + Theme.S(2), bt.Width + Theme.S(12), bt.Height + Theme.S(4));
            using (var path = Theme.Round(badge, badge.Height / 2))
            using (var brush = new SolidBrush(on ? Theme.Accent : Theme.Hover))
                g.FillPath(brush, path);
            TextRenderer.DrawText(g, badgeText, Theme.Small, Rectangle.Round(badge), on ? Color.White : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            y += titleSize.Height + Theme.S(6);
            TextRenderer.DrawText(g, on ? Strings.MenuInfo : Strings.InfoOff, Theme.Body, new Point(x, y), Theme.Muted, TextFormatFlags.NoPadding);

            y += Theme.S(19);
            int dot = Theme.S(7);
            using (var b = new SolidBrush(on ? Theme.Green : Theme.Muted))
                g.FillEllipse(b, x, y + Theme.S(5), dot, dot);
            TextRenderer.DrawText(g, status, Theme.Body, new Point(x + dot + Theme.S(6), y), Theme.Fore, TextFormatFlags.NoPadding);

            if (problem != null)
            {
                y += Theme.S(20);
                TextRenderer.DrawText(g, "⚠ " + problem, Theme.Body, new Point(x, y), Theme.Warn, TextFormatFlags.NoPadding);
            }
        }
    }

    enum Glyph { None, Play, Stop, Check }

    // What the renderer needs to know about a menu row.
    sealed class RowInfo
    {
        public Glyph Glyph;
        public bool Checked;
        public string Hint;
    }

    // Draws the menu: flat background, thin border, rounded hover, small accent glyphs.
    sealed class MenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Bg)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Rectangle r = e.ToolStrip.ClientRectangle;
            using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(Theme.Border))
                e.Graphics.DrawLine(p, Theme.S(4), y, e.Item.Width - Theme.S(4), y);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(Theme.S(4), Theme.S(2), e.Item.Width - Theme.S(8), e.Item.Height - Theme.S(4));
            if (e.Item.Selected)
                using (var path = Theme.Round(r, Theme.S(4)))
                using (var b = new SolidBrush(Theme.Hover))
                    g.FillPath(b, path);

            var info = e.Item.Tag as RowInfo;
            if (info == null) return;
            float s = Theme.S(11), gx = Theme.S(16), gy = (e.Item.Height - s) / 2f;
            switch (info.Glyph)
            {
                case Glyph.Stop:
                    using (var path = Theme.Round(new RectangleF(gx, gy, s, s), Theme.S(2)))
                    using (var b = new SolidBrush(Theme.Accent))
                        g.FillPath(b, path);
                    break;
                case Glyph.Play:
                    using (var b = new SolidBrush(Theme.Accent))
                        g.FillPolygon(b, new[] { new PointF(gx + 1, gy), new PointF(gx + s, gy + s / 2), new PointF(gx + 1, gy + s) });
                    break;
                case Glyph.Check:
                    using (var path = Theme.Round(new RectangleF(gx - 1, gy - 1, s + 2, s + 2), Theme.S(3)))
                    {
                        if (info.Checked)
                        {
                            using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
                            using (var p = new Pen(Color.White, Theme.S(1.6f)))
                                g.DrawLines(p, new[] { new PointF(gx + s * 0.18f, gy + s * 0.52f),
                                    new PointF(gx + s * 0.42f, gy + s * 0.76f), new PointF(gx + s * 0.84f, gy + s * 0.26f) });
                        }
                        else using (var p = new Pen(Theme.Muted, Theme.S(1.2f))) g.DrawPath(p, path);
                    }
                    break;
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var r = new Rectangle(Theme.S(38), 0, e.Item.Width - Theme.S(50), e.Item.Height);
            TextRenderer.DrawText(e.Graphics, e.Text, Theme.Body, r, Theme.Fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            var info = e.Item.Tag as RowInfo;
            if (info != null && info.Hint != null)
                TextRenderer.DrawText(e.Graphics, info.Hint, Theme.Body, r, Theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }
    }

    static class TrayMenu
    {
        public static ToolStripMenuItem Row(string text, Glyph glyph, string hint, EventHandler click)
        {
            var item = new ToolStripMenuItem(text, null, click);
            var info = new RowInfo();
            info.Glyph = glyph;
            info.Hint = hint;
            item.Tag = info;
            item.AutoSize = false;
            item.Size = new Size(Theme.S(310), Theme.S(36));
            return item;
        }

        public static ContextMenuStrip Build(Icon icon, out MenuHeader header, params ToolStripItem[] rows)
        {
            var menu = new ContextMenuStrip();
            menu.Renderer = new MenuRenderer();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
            menu.BackColor = Theme.Bg;
            menu.Padding = new Padding(0, Theme.S(4), 0, Theme.S(4));
            menu.HandleCreated += delegate { Theme.RoundCorners(menu.Handle); };

            header = new MenuHeader(icon);
            var host = new ToolStripControlHost(header);
            host.AutoSize = false;
            host.Size = header.Size;
            host.Margin = Padding.Empty;
            host.Padding = Padding.Empty;
            MenuHeader h = header;
            header.Resize += delegate { host.Size = h.Size; };

            menu.Items.Add(host);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.AddRange(rows);
            return menu;
        }
    }
}
