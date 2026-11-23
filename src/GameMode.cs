using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace RamGuard
{
    /// <summary>
    /// Game mode. While a game is running:
    ///  - every other app gets lower CPU priority and Windows "efficiency mode" (on hybrid Intel CPUs such as
    ///    the i7-1255U that moves them to the efficiency cores and leaves the fast cores to the game),
    ///  - the memory of every other app is freed so the game (and the integrated GPU, which shares RAM) has room,
    ///  - Windows is switched to the "Best performance" power mode.
    /// Nothing is closed and the game itself is never touched (important for anti-cheat). When the game closes,
    /// everything is put back exactly as it was. If RamGuard is stopped mid-game, the next start puts it back.
    /// </summary>
    internal sealed class GameMode
    {
        // Games Windows doesn't always recognise on its own (matched by process name).
        static readonly HashSet<string> KnownGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "VALORANT-Win64-Shipping", "League of Legends", "cs2", "csgo", "dota2", "FortniteClient-Win64-Shipping",
            "GTA5", "GTA5_Enhanced", "RDR2", "r5apex", "r5apex_dx12", "Minecraft.Windows", "RobloxPlayerBeta",
            "eldenring", "sekiro", "Overwatch", "RocketLeague", "TslGame", "Cyberpunk2077", "witcher3",
            "GenshinImpact", "ZenlessZoneZero", "StarRail", "bg3", "bg3_dx11", "Palworld-Win64-Shipping",
            "MilesMorales", "Spider-Man", "BatmanAC", "ShippingPC-BmGame", "FC25", "FIFA23", "NBA2K25", "cod"
        };

        // Folders games are installed in.
        static readonly string[] GameFolders =
        {
            @"\steamapps\common\", @"\Riot Games\VALORANT\", @"\Riot Games\League of Legends\", @"\Epic Games\",
            @"\XboxGames\", @"\GOG Games\", @"\EA Games\", @"\Ubisoft Game Launcher\games\", @"\.minecraft\"
        };

        // Never treated as the game: launchers, crash reporters, browsers, everyday apps.
        static readonly HashSet<string> NeverAGame = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "RiotClientServices", "RiotClientUx", "RiotClientUxRender", "RiotClientCrashHandler", "VALORANT",
            "CrashReportClient", "EpicWebHelper", "UnrealCEFSubProcess", "steam", "steamwebhelper", "steamservice",
            "EpicGamesLauncher", "Battle.net", "Agent", "EADesktop", "EABackgroundService", "UbisoftConnect", "upc",
            "GalaxyClient", "TLauncher", "chrome", "msedge", "firefox", "brave", "opera", "claude", "Code", "Cursor",
            "Antigravity IDE", "explorer", "WhatsApp.Root", "Discord", "Spotify", "obs64", "devenv", "RamGuard",
            "Taskmgr", "notepad", "WindowsTerminal", "powershell", "cmd", "ApplicationFrameHost", "SystemSettings"
        };

        // Never slowed down while gaming: voice chat, music, streaming, overlays, launchers, anti-cheat.
        static readonly HashSet<string> NeverCalm = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Discord", "DiscordPTB", "DiscordCanary", "ts3client_win64", "TeamSpeak", "mumble", "obs64", "obs32",
            "Streamlabs OBS", "Spotify", "SpotifyLauncher", "YouTube Music", "youtube-music", "vlc", "GameBar",
            "GameBarFTServer", "GameBarPresenceWriter", "XboxGameBarWidgets", "XboxPcAppFT", "steam", "steamwebhelper",
            "RiotClientServices", "RiotClientUx", "RiotClientUxRender", "vgtray", "vgc", "EasyAntiCheat",
            "EasyAntiCheat_EOS", "BEService", "FACEIT", "faceitclient", "EpicGamesLauncher", "EpicWebHelper",
            "Battle.net", "NVIDIA Share", "nvcontainer", "RamGuard", "audiodg", "TLauncher", "javaw"
        };

        // Exe names too generic to match by name alone (must match by full path).
        static readonly HashSet<string> GenericNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "game", "javaw", "java", "launcher", "start", "play", "main", "app", "client", "setup", "unitycrashhandler64"
        };

        public static string StateFile = Path.Combine(Settings.Folder, "gamemode-state.txt");

        readonly Settings settings;
        readonly int selfPid, selfSession;
        readonly Dictionary<int, string> calmed = new Dictionary<int, string>();   // pid -> process name
        readonly HashSet<int> gamePids = new HashSet<int>();
        readonly Dictionary<int, bool> verdictCache = new Dictionary<int, bool>();
        HashSet<string> windowsGamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> windowsGameNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime windowsGamesLoaded = DateTime.MinValue;
        Guid? powerBefore;

        public bool Active { get; private set; }
        public string GameName { get; private set; }
        public string GameDisplay { get; private set; }
        public DateTime Since { get; private set; }
        public long FreedBytes;
        public bool PowerBoosted { get { return powerBefore.HasValue; } }
        public int CalmedCount { get { return calmed.Count; } }
        public ICollection<int> GamePids { get { return gamePids; } }

        public GameMode(Settings settings)
        {
            this.settings = settings;
            using (var me = Process.GetCurrentProcess())
            {
                selfPid = me.Id;
                selfSession = me.SessionId;
            }
        }

        // ------------------------------------------------------------ detection

        /// <summary>Is the process with this PID a game? (Cached per PID; call ForgetVerdicts after rule changes.)</summary>
        public bool IsGame(int pid, out string name)
        {
            name = null;
            try { using (var p = Process.GetProcessById(pid)) name = p.ProcessName; }
            catch { return false; }

            bool cached;
            if (verdictCache.TryGetValue(pid, out cached)) return cached;
            if (verdictCache.Count > 300) verdictCache.Clear();

            LoadWindowsGames();
            // Name checks first: they need no access to the game process at all.
            bool? byName = DecideByName(name, settings, windowsGameNames);
            bool result;
            if (byName.HasValue) result = byName.Value;
            else
            {
                string path = Native.GetExePath(pid);
                result = DecideByPath(name, path, Native.GetForegroundTitle(), windowsGamePaths);
            }
            verdictCache[pid] = result;
            return result;
        }

        public void ForgetVerdicts() { verdictCache.Clear(); }

        /// <summary>true/false when the name alone decides, null when the path is needed.</summary>
        public static bool? DecideByName(string name, Settings settings, ICollection<string> windowsGameNames)
        {
            if (settings.NotGames.Contains(name)) return false;
            if (settings.Games.Contains(name)) return true;
            if (NeverAGame.Contains(name)) return false;
            if (KnownGames.Contains(name)) return true;
            if (windowsGameNames != null && windowsGameNames.Contains(name)) return true;
            return null;
        }

        public static bool DecideByPath(string name, string path, string title, ICollection<string> windowsGamePaths)
        {
            if (path != null)
            {
                if (windowsGamePaths != null && windowsGamePaths.Contains(path)) return true;
                foreach (string folder in GameFolders)
                    if (path.IndexOf(folder, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            if ((string.Equals(name, "javaw", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "java", StringComparison.OrdinalIgnoreCase))
                && title != null && title.StartsWith("Minecraft", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        /// <summary>Games Windows' Game Bar has recognised on this PC (HKCU\System\GameConfigStore).</summary>
        void LoadWindowsGames()
        {
            if (DateTime.Now - windowsGamesLoaded < TimeSpan.FromMinutes(10)) return;
            windowsGamesLoaded = DateTime.Now;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children"))
                {
                    if (root != null)
                        foreach (string sub in root.GetSubKeyNames())
                            using (var k = root.OpenSubKey(sub))
                            {
                                string p = k == null ? null : k.GetValue("MatchedExeFullPath") as string;
                                if (string.IsNullOrEmpty(p)) continue;
                                string n = Path.GetFileNameWithoutExtension(p);
                                if (NeverAGame.Contains(n)) continue;
                                paths.Add(p);
                                if (!GenericNames.Contains(n)) names.Add(n);
                            }
                }
            }
            catch { }
            windowsGamePaths = paths;
            windowsGameNames = names;
        }

        // ------------------------------------------------------------ start / keep / stop

        /// <summary>Starts game mode for this game. Call CalmOthers and free memory right after.</summary>
        public void Begin(int pid, string name)
        {
            Active = true;
            GameName = name;
            Since = DateTime.Now;
            FreedBytes = 0;
            string title = Native.GetForegroundTitle().Trim();
            GameDisplay = title.Length > 0 && title.Length <= 40 ? title : Scanner.FriendlyName(name);
            RefreshGamePids(pid);

            if (settings.GamePowerBoost)
            {
                Guid? now = Native.GetPowerMode();
                if (now.HasValue && now.Value != Native.BestPerformancePowerMode && Native.SetPowerMode(Native.BestPerformancePowerMode))
                    powerBefore = now;
            }
            SaveState();
        }

        /// <summary>The game's processes. java/javaw is shared by other apps, so only the game's own PID counts there.</summary>
        void RefreshGamePids(int knownPid)
        {
            gamePids.Clear();
            if (knownPid > 0) gamePids.Add(knownPid);
            if (string.Equals(GameName, "javaw", StringComparison.OrdinalIgnoreCase) || string.Equals(GameName, "java", StringComparison.OrdinalIgnoreCase))
                return;
            foreach (var p in Process.GetProcessesByName(GameName))
            {
                gamePids.Add(p.Id);
                p.Dispose();
            }
        }

        public bool GameStillRunning()
        {
            foreach (int pid in gamePids.ToList())
            {
                try
                {
                    using (var p = Process.GetProcessById(pid))
                        if (string.Equals(p.ProcessName, GameName, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
            // The game may have restarted itself under a new PID.
            if (!string.Equals(GameName, "javaw", StringComparison.OrdinalIgnoreCase))
            {
                RefreshGamePids(0);
                return gamePids.Count > 0;
            }
            return false;
        }

        /// <summary>
        /// Slows down every other app (lower priority + efficiency mode). Called at the start and every
        /// 30 s for apps opened during the game. Returns how many processes were newly calmed.
        /// </summary>
        public int CalmOthers(Snapshot snap)
        {
            return CalmOthers(snap, null);
        }

        /// <param name="only">Optional filter (the self-test uses it to touch only its own test app).</param>
        public int CalmOthers(Snapshot snap, Func<ProcInfo, bool> only)
        {
            int n = 0;
            foreach (var g in snap.Groups)
            {
                if (g.Protected || NeverCalm.Contains(g.RuleKey)) continue;
                foreach (var p in g.Procs)
                {
                    if (p.SessionId != selfSession || p.Pid == selfPid || gamePids.Contains(p.Pid) || calmed.ContainsKey(p.Pid)) continue;
                    if (NeverCalm.Contains(p.Name)) continue;
                    if (only != null && !only(p)) continue;
                    if (Native.Calm(p.Pid))
                    {
                        calmed[p.Pid] = p.Name;
                        n++;
                    }
                }
            }
            if (n > 0) SaveState();
            return n;
        }

        /// <summary>Processes whose memory must NOT be freed while gaming: the game, voice chat, music, overlays.</summary>
        public HashSet<int> TrimExclusions(Snapshot snap)
        {
            var set = new HashSet<int>(gamePids);
            foreach (var g in snap.Groups)
                foreach (var p in g.Procs)
                    if (NeverCalm.Contains(p.Name) || NeverCalm.Contains(g.RuleKey)) set.Add(p.Pid);
            return set;
        }

        /// <summary>Puts everything back exactly as it was. Returns how many processes were restored.</summary>
        public int Stop()
        {
            int n = 0;
            foreach (var kv in calmed)
                if (StillSame(kv.Key, kv.Value))
                {
                    Native.Uncalm(kv.Key);
                    n++;
                }
            calmed.Clear();
            gamePids.Clear();
            if (powerBefore.HasValue)
            {
                Native.SetPowerMode(powerBefore.Value);
                powerBefore = null;
            }
            Active = false;
            DeleteState();
            return n;
        }

        // ------------------------------------------------------------ crash safety

        void SaveState()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile));
                var lines = new List<string>();
                if (powerBefore.HasValue) lines.Add("power\t" + powerBefore.Value);
                foreach (var kv in calmed) lines.Add("calm\t" + kv.Key + "\t" + kv.Value);
                File.WriteAllLines(StateFile, lines);
            }
            catch { }
        }

        static void DeleteState()
        {
            try { if (File.Exists(StateFile)) File.Delete(StateFile); }
            catch { }
        }

        /// <summary>If RamGuard was stopped in the middle of a game, undo what game mode changed.</summary>
        public static int RestoreLeftovers()
        {
            int n = 0;
            try
            {
                if (!File.Exists(StateFile)) return 0;
                foreach (string line in File.ReadAllLines(StateFile))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length == 2 && parts[0] == "power")
                    {
                        Guid g;
                        try { g = new Guid(parts[1]); Native.SetPowerMode(g); } catch { }
                    }
                    else if (parts.Length == 3 && parts[0] == "calm")
                    {
                        int pid;
                        if (int.TryParse(parts[1], out pid) && StillSame(pid, parts[2]) && Native.IsCalm(pid))
                        {
                            Native.Uncalm(pid);
                            n++;
                        }
                    }
                }
            }
            catch { }
            DeleteState();
            return n;
        }

        static bool StillSame(int pid, string name)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
