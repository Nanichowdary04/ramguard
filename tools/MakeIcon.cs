using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Draws the RamGuard app icon (a RAM stick with a green shield) and writes a multi-size .ico.
//   MakeIcon.exe out.ico [preview.png]
internal static class MakeIcon
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
    static readonly Color Top = Color.FromArgb(0x5B, 0x9B, 0xFF);
    static readonly Color Bottom = Color.FromArgb(0x2B, 0x55, 0xD6);
    static readonly Color Green = Color.FromArgb(0x3F, 0xB9, 0x50);

    static int Main(string[] args)
    {
        var pngs = new List<byte[]>();
        foreach (int s in Sizes)
            using (var bmp = Render(s))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
            }

        using (var fs = File.Create(args[0]))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)Sizes.Length);
            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                int s = Sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length);
                w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }

        if (args.Length > 1) SavePreview(args[1]);
        return 0;
    }

    static Bitmap Render(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            // Rounded blue tile
            float pad = s <= 20 ? 0.5f : s * 0.04f;
            var tile = new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad);
            using (var path = Rounded(tile, s * 0.22f))
            using (var br = new LinearGradientBrush(new PointF(0, tile.Top), new PointF(0, tile.Bottom + 1), Top, Bottom))
                g.FillPath(br, path);

            bool badge = s >= 32;
            // RAM stick
            float sw = s * (badge ? 0.62f : 0.70f), sh = s * (badge ? 0.26f : 0.30f);
            float sx = badge ? s * 0.13f : (s - sw) / 2f;
            float sy = badge ? s * 0.22f : s * 0.30f;
            using (var p = Rounded(new RectangleF(sx, sy, sw, sh), Math.Max(1f, s * 0.035f)))
            using (var b = new SolidBrush(Color.White))
                g.FillPath(b, p);

            // Memory chips on the stick
            int chips = s >= 24 ? 3 : 2;
            float gap = sw * 0.09f;
            float cw = (sw - gap * (chips + 1)) / chips;
            using (var cb = new SolidBrush(Bottom))
                for (int i = 0; i < chips; i++)
                    g.FillRectangle(cb, sx + gap + i * (cw + gap), sy + sh * 0.24f, cw, sh * 0.52f);

            // Gold-finger pins under the stick
            if (s >= 24)
            {
                int pins = s >= 128 ? 9 : s >= 48 ? 6 : 4;
                float step = sw / pins;
                float pw = step * 0.5f;
                float py = sy + sh + Math.Max(1f, s * 0.025f);
                float ph = s * 0.07f;
                using (var pb = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                    for (int i = 0; i < pins; i++)
                        g.FillRectangle(pb, sx + step * i + (step - pw) / 2f, py, pw, ph);
            }

            // Green shield = "guard"
            if (badge)
            {
                float bw = s * 0.40f;
                float cx = s * 0.70f, cy = s * 0.68f;
                using (var shield = Shield(cx, cy, bw))
                {
                    using (var fill = new SolidBrush(Green)) g.FillPath(fill, shield);
                    using (var pen = new Pen(Color.White, Math.Max(1.5f, s * 0.04f))) g.DrawPath(pen, shield);
                }
                if (s >= 48)
                {
                    using (var pen = new Pen(Color.White, s * 0.045f))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                        g.DrawLines(pen, new[]
                        {
                            new PointF(cx - bw * 0.22f, cy + bw * 0.02f),
                            new PointF(cx - bw * 0.05f, cy + bw * 0.18f),
                            new PointF(cx + bw * 0.24f, cy - bw * 0.16f)
                        });
                    }
                }
            }
        }
        return bmp;
    }

    static GraphicsPath Shield(float cx, float cy, float w)
    {
        float h = w * 1.15f, l = cx - w / 2f, t = cy - h / 2f;
        var p = new GraphicsPath();
        p.AddBezier(cx, t, cx + w * 0.2f, t + h * 0.08f, l + w * 0.8f, t + h * 0.12f, l + w, t + h * 0.12f);
        p.AddBezier(l + w, t + h * 0.12f, l + w, t + h * 0.62f, cx + w * 0.22f, t + h * 0.86f, cx, t + h);
        p.AddBezier(cx, t + h, cx - w * 0.22f, t + h * 0.86f, l, t + h * 0.62f, l, t + h * 0.12f);
        p.AddBezier(l, t + h * 0.12f, l + w * 0.2f, t + h * 0.12f, cx - w * 0.2f, t + h * 0.08f, cx, t);
        p.CloseFigure();
        return p;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void SavePreview(string path)
    {
        int[] show = { 16, 24, 32, 48, 64, 128, 256 };
        int w = 40; foreach (int s in show) w += s + 24;
        using (var bmp = new Bitmap(w, 2 * (256 + 40)))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(32, 32, 32));
            g.FillRectangle(Brushes.WhiteSmoke, 0, 296, w, 296);
            for (int row = 0; row < 2; row++)
            {
                int x = 20;
                foreach (int s in show)
                {
                    using (var icon = Render(s)) g.DrawImage(icon, x, row * 296 + 20 + (256 - s));
                    x += s + 24;
                }
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }
}
