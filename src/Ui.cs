using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RamGuard
{
    internal sealed class Theme
    {
        public bool Dark;
        public Color Back, Surface, Text, Muted, Line, Accent, AccentText, Good, Warn, Bad, Selection;

        public static Theme Detect()
        {
            bool dark = true;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    if (v is int) dark = (int)v == 0;
                }
            }
            catch { }

            var t = new Theme();
            t.Dark = dark;
            if (dark)
            {
                t.Back = Hex(0x1e1e1e); t.Surface = Hex(0x272727); t.Text = Hex(0xececec); t.Muted = Hex(0x9b9b9b);
                t.Line = Hex(0x3a3a3a); t.Accent = Hex(0x4f8cff); t.AccentText = Color.White;
                t.Good = Hex(0x3fb950); t.Warn = Hex(0xe3a008); t.Bad = Hex(0xf85149); t.Selection = Hex(0x2d3b55);
            }
            else
            {
                t.Back = Hex(0xf3f3f3); t.Surface = Color.White; t.Text = Hex(0x1b1b1b); t.Muted = Hex(0x6b6b6b);
                t.Line = Hex(0xe1e1e1); t.Accent = Hex(0x2563eb); t.AccentText = Color.White;
                t.Good = Hex(0x1a7f37); t.Warn = Hex(0xb45309); t.Bad = Hex(0xcf222e); t.Selection = Hex(0xdbe7ff);
            }
            return t;
        }

        public Color ForLoad(int pct)
        {
            return pct >= 85 ? Bad : pct >= 70 ? Warn : Good;
        }

        static Color Hex(int rgb) { return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF); }
    }

    internal static class Ui
    {
        public static int S(float dpi, int v) { return (int)Math.Round(v * dpi / 96f); }

        static int TextWidth(string text, Font font)
        {
            return TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
        }

        public static Button MakeButton(string text, bool primary, Theme t, float dpi)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(S(dpi, 10), 0, S(dpi, 10), 0);
            b.MinimumSize = new Size(0, S(dpi, 34));
            b.Margin = new Padding(0, S(dpi, 4), S(dpi, 8), S(dpi, 4));
            b.Cursor = Cursors.Hand;
            if (primary)
            {
                b.BackColor = t.Accent;
                b.ForeColor = t.AccentText;
                b.FlatAppearance.BorderColor = t.Accent;
                b.Font = new Font("Segoe UI Semibold", 9.75f);
            }
            else
            {
                b.BackColor = t.Surface;
                b.ForeColor = t.Text;
                b.FlatAppearance.BorderColor = t.Line;
            }
            b.FlatAppearance.MouseOverBackColor = Draw.Mix(b.BackColor, t.Dark ? Color.White : Color.Black, 0.08f);
            b.FlatAppearance.MouseDownBackColor = Draw.Mix(b.BackColor, t.Dark ? Color.White : Color.Black, 0.16f);
            return b;
        }

        public static Label MakeLabel(string text, Font font, Color color)
        {
            var l = new Label();
            l.Text = text;
            l.UseMnemonic = false;
            l.Font = font;
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            l.AutoSize = true;
            l.Margin = new Padding(0);
            return l;
        }

        /// <summary>A label the same height as the buttons, so a toolbar row lines up.</summary>
        public static Label InlineLabel(string text, Font font, Color color, float dpi)
        {
            var l = MakeLabel(text, font, color);
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Size = new Size(TextWidth(text, font) + S(dpi, 2), S(dpi, 34));
            l.Margin = new Padding(S(dpi, 6), S(dpi, 4), S(dpi, 2), S(dpi, 4));
            return l;
        }

        public static CheckBox MakeCheck(string text, bool on, Font font, Theme t, float dpi)
        {
            var c = new CheckBox();
            c.Text = text;
            c.Checked = on;
            c.UseMnemonic = false; // show "&" as-is
            c.Font = font;
            c.ForeColor = t.Text;
            c.BackColor = Color.Transparent;
            c.AutoSize = false;
            c.TextAlign = ContentAlignment.MiddleLeft;
            c.CheckAlign = ContentAlignment.MiddleLeft;
            c.Size = new Size(TextWidth(text, font) + S(dpi, 28), S(dpi, 34));
            c.Margin = new Padding(S(dpi, 14), S(dpi, 4), 0, S(dpi, 4));
            c.Cursor = Cursors.Hand;
            return c;
        }
    }

    /// <summary>A slim rounded bar showing RAM use.</summary>
    internal sealed class Meter : Control
    {
        int value;
        public Color Track = Color.Gray;
        public Color Fill = Color.Green;

        public Meter()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public int Value
        {
            get { return value; }
            set { int v = Math.Max(0, Math.Min(100, value)); if (v != this.value) { this.value = v; Invalidate(); } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            var all = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var path = Draw.Rounded(all, Height / 2f))
            using (var b = new SolidBrush(Track))
                g.FillPath(b, path);
            float w = (Width - 1) * value / 100f;
            if (w >= Height)
            {
                using (var path = Draw.Rounded(new RectangleF(0, 0, w, Height - 1), Height / 2f))
                using (var b = new SolidBrush(Fill))
                    g.FillPath(b, path);
            }
        }
    }

    internal static class Draw
    {
        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static Color TextOn(Color bg)
        {
            double lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
            return lum > 0.6 ? Color.FromArgb(24, 24, 24) : Color.White;
        }

        /// <summary>A rounded square with text on it, e.g. the RAM % in the tray.</summary>
        public static Icon MakeIcon(int size, string text, Color back)
        {
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    using (var path = Rounded(new RectangleF(0, 0, size - 1, size - 1), Math.Max(2f, size / 5f)))
                    using (var b = new SolidBrush(back))
                        g.FillPath(b, path);
                    float fontPx = size * (text.Length >= 3 ? 0.44f : text.Length == 2 ? 0.62f : 0.7f);
                    using (var f = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat())
                    using (var tb = new SolidBrush(TextOn(back)))
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(text, f, tb, new RectangleF(0, size * 0.03f, size, size), sf);
                    }
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();
                }
                finally { Native.DestroyIcon(h); }
            }
        }
    }
}
