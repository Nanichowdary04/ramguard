using System;
using System.Globalization;
using System.IO;

namespace RamGuard
{
    /// <summary>What RamGuard did today (memory freed, games boosted). Kept in %APPDATA%\RamGuard\stats.txt.</summary>
    internal sealed class Stats
    {
        public static string FilePath = Path.Combine(Settings.Folder, "stats.txt");
        public static bool Persist = true;

        public DateTime Day = DateTime.Today;
        public long FreedBytes;
        public int Frees;
        public int Games;
        public double GameMinutes;

        public static Stats Load()
        {
            var s = new Stats();
            if (!Persist) return s;
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = raw.Substring(0, eq).Trim(), v = raw.Substring(eq + 1).Trim();
                    long l; int i; double d; DateTime t;
                    if (k == "day" && DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) s.Day = t;
                    else if (k == "freedBytes" && long.TryParse(v, out l)) s.FreedBytes = l;
                    else if (k == "frees" && int.TryParse(v, out i)) s.Frees = i;
                    else if (k == "games" && int.TryParse(v, out i)) s.Games = i;
                    else if (k == "gameMinutes" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) s.GameMinutes = d;
                }
            }
            catch { }
            s.Roll();
            return s;
        }

        public void AddFree(long bytes)
        {
            Roll();
            FreedBytes += Math.Max(0, bytes);
            Frees++;
            Save();
        }

        public void AddGame(TimeSpan played)
        {
            Roll();
            Games++;
            GameMinutes += played.TotalMinutes;
            Save();
        }

        public string Summary()
        {
            Roll();
            string text = Frees == 0
                ? "Today: nothing freed yet"
                : string.Format("Today: {0} freed ({1}×)", Units.Size(FreedBytes), Frees);
            if (Games > 0)
                text += string.Format("  ·  {0} game{1} boosted ({2})", Games, Games == 1 ? "" : "s", Units.Span(TimeSpan.FromMinutes(GameMinutes)));
            return text;
        }

        void Roll()
        {
            if (Day == DateTime.Today) return;
            Day = DateTime.Today;
            FreedBytes = 0; Frees = 0; Games = 0; GameMinutes = 0;
        }

        void Save()
        {
            if (!Persist) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[]
                {
                    "day=" + Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    "freedBytes=" + FreedBytes,
                    "frees=" + Frees,
                    "games=" + Games,
                    "gameMinutes=" + GameMinutes.ToString(CultureInfo.InvariantCulture)
                });
            }
            catch { }
        }
    }
}
