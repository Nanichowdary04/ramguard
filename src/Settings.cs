using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RamGuard
{
    internal enum Rule { Ask, Keep, AutoClose }

    internal sealed class Settings
    {
        public static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RamGuard");

        public int IdleMinutes = 10;        // an app counts as idle after this long without use
        public bool AutoMode = false;       // close "Auto-close" apps and trim idle apps on its own
        public int TrimAbovePercent = 80;   // auto mode frees as much memory as it can when RAM load is at/above this
        public int MinMb = 100;             // ignore apps smaller than this when cleaning
        public bool ShowAll = false;        // show Windows parts and small processes in the list
        public bool GameMode = true;        // detect games and give them the CPU and RAM
        public bool GamePowerBoost = true;  // switch Windows to "Best performance" while a game runs
        public int GameRamLimit = 70;       // while gaming, free memory whenever RAM is at/above this
        public readonly Dictionary<string, Rule> Rules = new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);     // marked "this is a game"
        public readonly HashSet<string> NotGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // marked "not a game"

        string path; // null = in-memory only (tests, previews)

        public Settings()
        {
            // Sensible defaults: long-running work apps stay open, OneDrive keeps files in sync.
            Rules["claude"] = Rule.Keep;
            Rules["onedrive"] = Rule.Keep;
        }

        public Rule RuleFor(string ruleKey)
        {
            Rule r;
            return Rules.TryGetValue(ruleKey, out r) ? r : Rule.Ask;
        }

        public static Settings Load()
        {
            var s = new Settings();
            s.path = Path.Combine(Folder, "settings.txt");
            try
            {
                if (!File.Exists(s.path)) return s;
                foreach (string raw in File.ReadAllLines(s.path))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (line.Length == 0 || line[0] == '#' || eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    int n;
                    Rule r;
                    if (key == "idleMinutes" && int.TryParse(val, out n)) s.IdleMinutes = Clamp(n, 1, 240);
                    else if (key == "autoMode") s.AutoMode = IsTrue(val);
                    else if (key == "trimAbovePercent" && int.TryParse(val, out n)) s.TrimAbovePercent = Clamp(n, 50, 99);
                    else if (key == "minMb" && int.TryParse(val, out n)) s.MinMb = Clamp(n, 10, 8192);
                    else if (key == "showAll") s.ShowAll = IsTrue(val);
                    else if (key == "gameMode") s.GameMode = IsTrue(val);
                    else if (key == "gamePowerBoost") s.GamePowerBoost = IsTrue(val);
                    else if (key == "gameRamLimit" && int.TryParse(val, out n)) s.GameRamLimit = Clamp(n, 40, 95);
                    else if (key.StartsWith("game.") && IsTrue(val)) s.Games.Add(key.Substring(5));
                    else if (key.StartsWith("notgame.") && IsTrue(val)) s.NotGames.Add(key.Substring(8));
                    else if (key.StartsWith("rule.") && Enum.TryParse(val, true, out r)) s.Rules[key.Substring(5)] = r;
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            if (path == null) return;
            try
            {
                Directory.CreateDirectory(Folder);
                var sb = new StringBuilder();
                sb.AppendLine("# RamGuard settings");
                sb.AppendLine("idleMinutes=" + IdleMinutes);
                sb.AppendLine("autoMode=" + (AutoMode ? 1 : 0));
                sb.AppendLine("trimAbovePercent=" + TrimAbovePercent);
                sb.AppendLine("minMb=" + MinMb);
                sb.AppendLine("showAll=" + (ShowAll ? 1 : 0));
                sb.AppendLine("gameMode=" + (GameMode ? 1 : 0));
                sb.AppendLine("gamePowerBoost=" + (GamePowerBoost ? 1 : 0));
                sb.AppendLine("gameRamLimit=" + GameRamLimit);
                foreach (string g in Games.OrderBy(x => x)) sb.AppendLine("game." + g + "=1");
                foreach (string g in NotGames.OrderBy(x => x)) sb.AppendLine("notgame." + g + "=1");
                foreach (var kv in Rules.OrderBy(k => k.Key))
                    sb.AppendLine("rule." + kv.Key + "=" + kv.Value);
                File.WriteAllText(path, sb.ToString());
            }
            catch { }
        }

        static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }
        static bool IsTrue(string v) { return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); }
    }
}
