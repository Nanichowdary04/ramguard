using System;
using System.Collections.Generic;

namespace RamGuard
{
    internal sealed class MemSample
    {
        public DateTime Time;
        public long Bytes;
        public MemSample(DateTime time, long bytes) { Time = time; Bytes = bytes; }
    }

    internal sealed class GrowthResult
    {
        public bool Growing;
        public long GrowthBytes;     // how much it grew over the window (from the fitted trend line)
        public double BytesPerHour;
        public TimeSpan Window;
    }

    /// <summary>
    /// Spots apps whose memory keeps climbing for a long time (e.g. a dev server that grows to 1.7 GB over a
    /// day). Needs at least 45 minutes of history. An app is "growing" when its memory rose by 300 MB or more,
    /// at 150 MB/h or faster, steadily (a straight-line fit explains at least 85% of the movement, so one
    /// big jump such as opening a project doesn't count), and it is still rising in the most recent stretch.
    /// </summary>
    internal static class Growth
    {
        public const double MinGrowthMb = 300;
        public const double MinMbPerHour = 150;
        public const double MinFit = 0.85;
        public static readonly TimeSpan MinWindow = TimeSpan.FromMinutes(45);

        public static GrowthResult Analyze(IList<MemSample> s)
        {
            var r = new GrowthResult();
            if (s == null || s.Count < 15) return r;
            DateTime t0 = s[0].Time;
            r.Window = s[s.Count - 1].Time - t0;
            if (r.Window < MinWindow) return r;

            // Least-squares line through (hours, MB).
            double n = s.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
            foreach (var p in s)
            {
                double x = (p.Time - t0).TotalHours, y = p.Bytes / (double)Units.MB;
                sx += x; sy += y; sxx += x * x; sxy += x * y;
            }
            double denom = n * sxx - sx * sx;
            if (denom <= 0) return r;
            double slope = (n * sxy - sx * sy) / denom;   // MB per hour
            double intercept = (sy - slope * sx) / n;

            double mean = sy / n, ssTot = 0, ssRes = 0;
            foreach (var p in s)
            {
                double x = (p.Time - t0).TotalHours, y = p.Bytes / (double)Units.MB;
                double fit = intercept + slope * x;
                ssTot += (y - mean) * (y - mean);
                ssRes += (y - fit) * (y - fit);
            }
            double r2 = ssTot <= 0 ? 0 : 1 - ssRes / ssTot;

            // Still rising lately: the last third is clearly above the middle third.
            int third = s.Count / 3;
            double mid = 0, end = 0;
            for (int i = third; i < 2 * third; i++) mid += s[i].Bytes;
            for (int i = s.Count - third; i < s.Count; i++) end += s[i].Bytes;
            mid /= Math.Max(1, third); end /= Math.Max(1, third);
            bool stillRising = (end - mid) / Units.MB >= 50;

            double growthMb = slope * r.Window.TotalHours;
            r.BytesPerHour = slope * Units.MB;
            r.GrowthBytes = (long)(growthMb * Units.MB);
            r.Growing = slope >= MinMbPerHour && growthMb >= MinGrowthMb && r2 >= MinFit && stillRising;
            return r;
        }
    }
}
