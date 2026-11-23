using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace RamGuard
{
    internal struct GraphPoint { public DateTime Time; public int Load; }
    internal struct GraphEvent { public DateTime Time; public long Bytes; }
    internal sealed class GameSpan { public DateTime Start; public DateTime? End; public string Name; }

    /// <summary>
    /// RAM use over the last hour: a line coloured by how full RAM was, a dashed line at the Auto mode limit,
    /// a green dot each time RamGuard freed memory, and a shaded band while a game was running.
    /// </summary>
    internal sealed class RamGraph : Control
    {
        public Theme Theme;
        public float UiScale = 1f;
        public int Threshold = 80;
        public List<GraphPoint> Points = new List<GraphPoint>();
        public List<GraphEvent> Frees = new List<GraphEvent>();
        public List<GameSpan> Games = new List<GameSpan>();
        public TimeSpan Window = TimeSpan.FromMinutes(60);

        public RamGraph()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        float S(float v) { return v * UiScale; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);
            if (Theme == null || Width < 50 || Height < 30) return;

            using (var panel = Draw.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), S(8)))
            using (var b = new SolidBrush(Theme.Surface))
                g.FillPath(b, panel);

            var plot = new RectangleF(S(10), S(10), Width - S(10) - S(64), Height - S(10) - S(22));
            DateTime now = DateTime.Now, start = now - Window;
            Func<DateTime, float> X = t => plot.Left + (float)((t - start).TotalSeconds / Window.TotalSeconds) * plot.Width;
            Func<double, float> Y = v => plot.Bottom - (float)(Math.Max(0, Math.Min(100, v)) / 100.0) * plot.Height;

            using (var small = new Font("Segoe UI", 8f))
            using (var muted = new SolidBrush(Theme.Muted))
            {
                // Faint guides at 25 / 50 / 75 %.
                using (var guide = new Pen(Color.FromArgb(Theme.Dark ? 28 : 18, Theme.Text), 1f))
                    foreach (int v in new[] { 25, 50, 75 })
                        g.DrawLine(guide, plot.Left, Y(v), plot.Right, Y(v));

                // Games.
                using (var band = new SolidBrush(Color.FromArgb(Theme.Dark ? 45 : 35, Theme.Good)))
                using (var goodText = new SolidBrush(Theme.Good))
                    foreach (var span in Games)
                    {
                        DateTime s0 = span.Start < start ? start : span.Start;
                        DateTime s1 = span.End.HasValue ? span.End.Value : now;
                        if (s1 <= start) continue;
                        float x0 = X(s0), x1 = X(s1);
                        g.FillRectangle(band, x0, plot.Top, Math.Max(2, x1 - x0), plot.Height);
                        if (x1 - x0 > S(40)) g.DrawString("game", small, goodText, x0 + S(3), plot.Top + S(1));
                    }

                // Auto mode limit (its label moves out of the way of the "now" label).
                float nowLabelY = float.NaN;
                for (int i = Points.Count - 1; i >= 0; i--)
                    if (Points[i].Time >= start) { nowLabelY = Math.Max(plot.Top, Math.Min(plot.Bottom - S(16), Y(Points[i].Load) - S(9))); break; }
                using (var dash = new Pen(Theme.Muted, 1f))
                {
                    dash.DashStyle = DashStyle.Dash;
                    float ty = Y(Threshold);
                    g.DrawLine(dash, plot.Left, ty, plot.Right, ty);
                    float labelY = ty - S(8);
                    if (!float.IsNaN(nowLabelY) && Math.Abs(labelY - nowLabelY) < S(15))
                        labelY = nowLabelY + (labelY > nowLabelY ? S(15) : -S(15));
                    labelY = Math.Max(0, Math.Min(Height - S(14), labelY));
                    g.DrawString("Auto " + Threshold + "%", small, muted, plot.Right + S(6), labelY);
                }

                // The RAM line.
                var pts = new List<PointF>();
                var loads = new List<int>();
                foreach (var p in Points)
                    if (p.Time >= start)
                    {
                        pts.Add(new PointF(X(p.Time), Y(p.Load)));
                        loads.Add(p.Load);
                    }
                if (pts.Count >= 2)
                {
                    var area = new List<PointF>(pts);
                    area.Add(new PointF(pts[pts.Count - 1].X, plot.Bottom));
                    area.Add(new PointF(pts[0].X, plot.Bottom));
                    using (var fill = new LinearGradientBrush(new PointF(0, plot.Top), new PointF(0, plot.Bottom + 1),
                        Color.FromArgb(Theme.Dark ? 70 : 55, Theme.Accent), Color.FromArgb(0, Theme.Accent)))
                        g.FillPolygon(fill, area.ToArray());
                    for (int i = 1; i < pts.Count; i++)
                        using (var pen = new Pen(Theme.ForLoad(Math.Max(loads[i - 1], loads[i])), S(2f)))
                        {
                            pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                            g.DrawLine(pen, pts[i - 1], pts[i]);
                        }
                }

                // Each time RamGuard freed memory.
                using (var dot = new SolidBrush(Theme.Good))
                using (var ring = new Pen(Theme.Surface, S(1.5f)))
                    foreach (var f in Frees)
                    {
                        if (f.Time < start) continue;
                        float fx = X(f.Time), fy = Y(LoadAt(f.Time));
                        float r = S(4f);
                        g.FillEllipse(dot, fx - r, fy - r, 2 * r, 2 * r);
                        g.DrawEllipse(ring, fx - r, fy - r, 2 * r, 2 * r);
                    }

                // Now.
                if (pts.Count > 0)
                {
                    int nowLoad = loads[loads.Count - 1];
                    var p = pts[pts.Count - 1];
                    using (var b = new SolidBrush(Theme.ForLoad(nowLoad)))
                    using (var bold = new Font("Segoe UI Semibold", 10f))
                    {
                        float r = S(4.5f);
                        g.FillEllipse(b, p.X - r, p.Y - r, 2 * r, 2 * r);
                        g.DrawString(nowLoad + "%", bold, b, plot.Right + S(6), Math.Max(plot.Top, Math.Min(plot.Bottom - S(16), p.Y - S(9))));
                    }
                }

                // Time axis.
                float by = plot.Bottom + S(4);
                g.DrawString((int)Window.TotalMinutes + " min ago", small, muted, plot.Left, by);
                var mid = (int)(Window.TotalMinutes / 2) + " min";
                g.DrawString(mid, small, muted, plot.Left + plot.Width / 2 - g.MeasureString(mid, small).Width / 2, by);
                g.DrawString("now", small, muted, plot.Right - g.MeasureString("now", small).Width, by);
            }
        }

        int LoadAt(DateTime t)
        {
            int best = Points.Count > 0 ? Points[Points.Count - 1].Load : 0;
            double bestGap = double.MaxValue;
            foreach (var p in Points)
            {
                double gap = Math.Abs((p.Time - t).TotalSeconds);
                if (gap < bestGap) { bestGap = gap; best = p.Load; }
            }
            return best;
        }
    }
}
