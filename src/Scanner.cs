using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace RamGuard
{
    internal sealed class ProcInfo
    {
        public int Pid;
        public int ParentPid;
        public string Name;
        public int SessionId;
        public long PrivateBytes;
        public long WorkingSet;
        public long CpuTicks = -1;   // total CPU time used so far (100 ns ticks), -1 = unknown
        public double CpuPercent = -1; // average CPU over about the last minute (% of one core), -1 = not known yet
    }

    /// <summary>One row in the list: an app and all of its processes.</summary>
    internal sealed class AppGroup
    {
        public string Key;          // unique per app instance, e.g. "chrome" or "node#1234"
        public string RuleKey;      // what the Keep/Ask/Auto-close rule is stored under, e.g. "node"
        public string Display;
        public bool IsTree;         // a dev tool / script started from a terminal (its own row per launch)
        public string RootName;
        public int RootPid;
        public readonly List<ProcInfo> Procs = new List<ProcInfo>();
        public readonly SortedSet<int> Ports = new SortedSet<int>();
        public long PrivateBytes;   // memory the app holds; freed when it closes
        public long WorkingSet;     // the part of it currently in physical RAM
        public bool HasWindow;
        public bool IsForeground;
        public bool Serving;        // a client (e.g. a browser tab) is connected to it right now
        public double CpuPercent;   // average CPU over about the last minute, in % of one core
        public bool Busy;           // doing real work in the background (music, download, build...)
        public GrowthResult GrowthInfo; // memory trend over the last hours (null = not enough history yet)
        public bool Growing { get { return GrowthInfo != null && GrowthInfo.Growing; } }
        public bool EverUsed;       // seen in the foreground / serving since RamGuard started
        public bool InUse;
        public string InUseReason;
        public bool Protected;
        public string ProtectReason;
        public DateTime LastUsed;
        public Rule Rule;

        public TimeSpan IdleFor { get { return DateTime.Now - LastUsed; } }

        public bool IsCandidate(Settings s)
        {
            return !Protected && !InUse && Rule != Rule.Keep && PrivateBytes >= s.MinMb * Units.MB;
        }
    }

    internal sealed class Snapshot
    {
        public List<AppGroup> Groups = new List<AppGroup>();
        public MemoryStatus Memory;
        public int ForegroundPid;   // the process that owns the window in front of you

        public AppGroup Find(string key)
        {
            return Groups.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        public AppGroup FindByPid(int pid)
        {
            return Groups.FirstOrDefault(g => g.Procs.Any(p => p.Pid == pid));
        }
    }

    internal static class Units
    {
        public const long MB = 1024L * 1024L;
        public const long GB = 1024L * MB;

        public static string Size(long bytes)
        {
            if (bytes >= GB) return string.Format("{0:0.0} GB", bytes / (double)GB);
            return string.Format("{0:0} MB", bytes / (double)MB);
        }

        public static string Span(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "under a minute";
            if (t.TotalMinutes < 60) return string.Format("{0} min", (int)t.TotalMinutes);
            if (t.TotalHours < 24) return string.Format("{0} h {1} min", (int)t.TotalHours, t.Minutes);
            return string.Format("{0} days", (int)t.TotalDays);
        }

        public static string Ago(DateTime when)
        {
            TimeSpan t = DateTime.Now - when;
            if (t.TotalMinutes < 1) return "Just now";
            return Span(t) + " ago";
        }
    }

    /// <summary>
    /// Takes a picture of every running process, groups processes into apps, and works out
    /// which apps you are actually using.
    /// </summary>
    internal sealed class Scanner
    {
        public const string WslKey = "wsl-docker";

        // Parts of Windows. Never touched.
        static readonly HashSet<string> SystemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Secure System", "Memory Compression", "smss", "csrss", "wininit",
            "winlogon", "services", "lsass", "lsaiso", "svchost", "fontdrvhost", "dwm", "explorer", "sihost",
            "ctfmon", "taskhostw", "RuntimeBroker", "dllhost", "ShellExperienceHost", "StartMenuExperienceHost",
            "SearchHost", "SearchApp", "SearchIndexer", "SearchProtocolHost", "SearchFilterHost", "TextInputHost",
            "ShellHost", "LockApp", "LogonUI", "userinit", "SecurityHealthSystray", "SecurityHealthService",
            "smartscreen", "ApplicationFrameHost", "audiodg", "WUDFHost", "MsMpEng", "NisSrv",
            "MpDefenderCoreService", "SgrmBroker", "spoolsv", "sppsvc", "UserOOBEBroker", "dasHost", "WmiPrvSE",
            "CompPkgSrv", "backgroundTaskHost", "MoUsoCoreWorker", "TiWorker", "TrustedInstaller", "conhost",
            "OpenConsole", "AggregatorHost", "unsecapp", "SystemSettingsBroker", "CrossDeviceService",
            "Widgets", "WidgetService", "taskmgr", "msiexec", "rundll32", "consent", "WerFault", "wermgr"
        };

        // Helpers that ship with hardware drivers (audio, graphics, touchpad...). Closing them can break the device.
        static readonly HashSet<string> DriverHelperNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WavesSvc64", "WavesSysSvc64", "WavesAudioService", "MaxxAudioPro", "RtkAudUService64", "RtkBtManServ",
            "RAVBg64", "RAVCpl64", "NahimicSvc64", "NahimicSvc32", "igfxEM", "igfxHK", "igfxTray", "IGCCTray",
            "IGCC", "IntelGraphicsSoftware", "IntelAudioService", "OneApp.IGCC.WinService", "jhi_service", "esif_uf",
            "SynTPEnh", "SynTPHelper", "ETDCtrl", "NVDisplay.Container", "nvcontainer", "AMDRSServ", "atieclxx",
            "RadeonSoftware", "RstMwService", "cplspcon", "LMS"
        };

        // Anti-cheat that games need running. Never touched.
        static readonly HashSet<string> AntiCheatNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "vgtray", "vgc", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BEService_x64", "FACEIT", "faceitclient", "GameGuard"
        };

        // Terminals and shells. Your commands run inside them, so they're never touched.
        static readonly HashSet<string> ShellNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cmd", "powershell", "pwsh", "bash", "sh", "zsh", "WindowsTerminal", "wt", "wsl", "mintty"
        };

        // Dev runtimes. Each launch (e.g. one "npm run dev") becomes its own row.
        static readonly HashSet<string> RuntimeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node", "python", "pythonw", "py", "uv", "java", "javaw", "deno", "bun", "ruby", "php", "dotnet",
            "esbuild", "go", "cargo"
        };

        // Skipped over when working out who started a process.
        static readonly HashSet<string> PassThroughNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cmd", "conhost", "OpenConsole"
        };

        // Helpers that belong to whichever app started them.
        static readonly HashSet<string> HelperNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "msedgewebview2"
        };

        static readonly HashSet<string> WslDockerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "vmmemWSL", "vmmem", "wslhost", "wslrelay", "Docker Desktop", "com.docker.backend",
            "com.docker.build", "com.docker.dev-envs", "com.docker.extensions", "com.docker.cli"
        };

        static readonly Dictionary<string, string> Friendly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "chrome", "Google Chrome" }, { "msedge", "Microsoft Edge" }, { "firefox", "Firefox" },
            { "brave", "Brave" }, { "opera", "Opera" }, { "claude", "Claude" }, { "chatgpt", "ChatGPT" },
            { "code", "VS Code" }, { "cursor", "Cursor" }, { WslKey, "WSL & Docker" }, { "onedrive", "OneDrive" },
            { "whatsapp", "WhatsApp" }, { "whatsapp.root", "WhatsApp" }, { "node", "Node.js" },
            { "python", "Python" }, { "pythonw", "Python" }, { "py", "Python" }, { "java", "Java" },
            { "javaw", "Java" }, { "dotnet", ".NET" }, { "esbuild", "esbuild" }, { "mysqld", "MySQL" },
            { "explorer", "File Explorer" }, { "spotify", "Spotify" }, { "discord", "Discord" },
            { "ms-teams", "Teams" }, { "msedgewebview2", "Edge WebView" }, { "phoneexperiencehost", "Phone Link" },
            { "systemsettings", "Settings" }, { "taskmgr", "Task Manager" }, { "svchost", "Windows services" },
            { "winword", "Word" }, { "excel", "Excel" }, { "powerpnt", "PowerPoint" }, { "outlook", "Outlook" },
            { "olk", "Outlook" }, { "vlc", "VLC" }, { "obs64", "OBS Studio" }, { "steam", "Steam" },
            { "steamwebhelper", "Steam" }, { "zoom", "Zoom" }, { "slack", "Slack" }, { "notion", "Notion" },
            { "figma", "Figma" }, { "blender", "Blender" }, { "ramguard", "RamGuard" }
        };

        sealed class KeyInfo
        {
            public string Key, RuleKey, RootName;
            public bool IsTree;
            public int RootPid;
        }

        readonly Settings settings;
        readonly DateTime baseline;
        readonly int selfPid, selfSession;
        readonly Dictionary<int, DateTime> lastForegroundByPid = new Dictionary<int, DateTime>();
        readonly Dictionary<string, DateTime> lastUsedByKey = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> everUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool firstScanDone;

        // CPU is measured over a window of at least a minute, so short bursts (an app checking for
        // messages, a GC) don't count as "working". Idle apps measured on this PC use < 2% of a core.
        public const double BusyCpuPercent = 5.0;
        static readonly TimeSpan CpuWindow = TimeSpan.FromSeconds(60);
        public TimeSpan MinCpuWindow = TimeSpan.FromSeconds(20); // don't judge an app on less than this
        sealed class CpuSample { public long Ticks; public DateTime Time; }
        readonly Dictionary<int, List<CpuSample>> cpuHistory = new Dictionary<int, List<CpuSample>>();

        // Memory history per app for spotting apps that keep growing: one sample a minute, three hours kept.
        readonly Dictionary<string, List<MemSample>> memHistory = new Dictionary<string, List<MemSample>>(StringComparer.OrdinalIgnoreCase);
        static readonly TimeSpan MemSampleEvery = TimeSpan.FromSeconds(60);
        static readonly TimeSpan MemHistoryLength = TimeSpan.FromHours(3);

        /// <summary>The running game's processes: never opened for CPU sampling (anti-cheat friendly).</summary>
        public readonly HashSet<int> DoNotTouch = new HashSet<int>();

        /// <param name="baseline">Apps already running at the first scan count as last used at this time.</param>
        public Scanner(Settings settings, DateTime baseline)
        {
            this.settings = settings;
            this.baseline = baseline;
            using (var me = Process.GetCurrentProcess())
            {
                selfPid = me.Id;
                selfSession = me.SessionId;
            }
        }

        public static string FriendlyName(string processName)
        {
            string f;
            return Friendly.TryGetValue(processName, out f) ? f : processName;
        }

        /// <summary>Called every couple of seconds: remembers which app you're looking at.</summary>
        public void NoteForeground()
        {
            NoteForeground(Native.GetForegroundPid());
        }

        public void NoteForeground(int pid)
        {
            if (pid > 0) lastForegroundByPid[pid] = DateTime.Now;
        }

        public Snapshot Scan()
        {
            DateTime now = DateTime.Now;
            var parents = Native.GetParentMap();
            var windows = Native.GetAppWindows(false);
            var tcp = Native.GetTcpInfo();
            int fgPid = Native.GetForegroundPid();
            if (fgPid > 0) lastForegroundByPid[fgPid] = now;

            var procs = new Dictionary<int, ProcInfo>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == 0) continue;
                    var info = new ProcInfo();
                    info.Pid = p.Id;
                    info.Name = p.ProcessName;
                    info.SessionId = p.SessionId;
                    info.PrivateBytes = p.PrivateMemorySize64;
                    info.WorkingSet = p.WorkingSet64;
                    int parent;
                    info.ParentPid = parents.TryGetValue(info.Pid, out parent) ? parent : 0;
                    if (info.SessionId == selfSession && !DoNotTouch.Contains(info.Pid)) info.CpuTicks = Native.GetCpuTicks(info.Pid);
                    procs[info.Pid] = info;
                }
                catch { }
                finally { p.Dispose(); }
            }

            var cache = new Dictionary<int, KeyInfo>();
            var groups = new Dictionary<string, AppGroup>(StringComparer.OrdinalIgnoreCase);
            bool wslTerminalOpen = false;
            foreach (var info in procs.Values)
            {
                if (info.SessionId == selfSession && string.Equals(info.Name, "wsl", StringComparison.OrdinalIgnoreCase))
                    wslTerminalOpen = true;

                KeyInfo k = Resolve(info, procs, cache);
                AppGroup g;
                if (!groups.TryGetValue(k.Key, out g))
                {
                    g = new AppGroup();
                    g.Key = k.Key;
                    g.RuleKey = k.RuleKey;
                    g.IsTree = k.IsTree;
                    g.RootName = k.RootName;
                    g.RootPid = k.RootPid;
                    groups.Add(k.Key, g);
                }
                g.Procs.Add(info);
                g.PrivateBytes += info.PrivateBytes;
                g.WorkingSet += info.WorkingSet;
                if (windows.ContainsKey(info.Pid)) g.HasWindow = true;
                if (info.Pid == fgPid) g.IsForeground = true;
                if (tcp.ServingPids.Contains(info.Pid)) g.Serving = true;
                SortedSet<int> ports;
                if (tcp.ListeningPorts.TryGetValue(info.Pid, out ports)) g.Ports.UnionWith(ports);
            }

            foreach (var g in groups.Values)
            {
                MeasureCpu(g, now);
                Classify(g, now, wslTerminalOpen);
                TrackGrowth(g, now);
            }
            foreach (string key in memHistory.Keys.Where(k => !groups.ContainsKey(k)).ToList())
                memHistory.Remove(key);
            foreach (int pid in cpuHistory.Keys.Where(x => !procs.ContainsKey(x)).ToList())
                cpuHistory.Remove(pid);

            // Forget processes and apps that have gone away.
            foreach (int pid in lastForegroundByPid.Keys.Where(x => !procs.ContainsKey(x)).ToList())
                lastForegroundByPid.Remove(pid);
            foreach (string key in lastUsedByKey.Keys.Where(x => !groups.ContainsKey(x)).ToList())
            {
                lastUsedByKey.Remove(key);
                everUsed.Remove(key);
            }

            firstScanDone = true;
            var snap = new Snapshot();
            snap.Groups = groups.Values.ToList();
            snap.Memory = Native.GetMemory();
            snap.ForegroundPid = fgPid;
            return snap;
        }

        void TrackGrowth(AppGroup g, DateTime now)
        {
            List<MemSample> h;
            if (!memHistory.TryGetValue(g.Key, out h))
            {
                h = new List<MemSample>();
                memHistory[g.Key] = h;
            }
            if (h.Count == 0 || now - h[h.Count - 1].Time >= MemSampleEvery) h.Add(new MemSample(now, g.PrivateBytes));
            while (h.Count > 0 && now - h[0].Time > MemHistoryLength) h.RemoveAt(0);
            if (!g.Protected) g.GrowthInfo = Growth.Analyze(h);
        }

        /// <summary>Average CPU of the app since a sample taken at least a minute ago.</summary>
        void MeasureCpu(AppGroup g, DateTime now)
        {
            long used = 0;
            double seconds = 0;
            foreach (var p in g.Procs)
            {
                if (p.CpuTicks < 0) continue;
                List<CpuSample> history;
                if (!cpuHistory.TryGetValue(p.Pid, out history))
                {
                    history = new List<CpuSample>();
                    cpuHistory[p.Pid] = history;
                }
                // Keep exactly one sample from before the window start, so we always measure ~a full minute.
                while (history.Count >= 2 && now - history[1].Time >= CpuWindow) history.RemoveAt(0);
                if (history.Count > 0 && p.CpuTicks < history[0].Ticks) history.Clear(); // PID was reused

                if (history.Count > 0)
                {
                    double span = (now - history[0].Time).TotalSeconds;
                    if (span >= MinCpuWindow.TotalSeconds)
                    {
                        long delta = p.CpuTicks - history[0].Ticks;
                        used += delta;
                        seconds = Math.Max(seconds, span);
                        p.CpuPercent = 100.0 * delta / TimeSpan.FromSeconds(span).Ticks;
                    }
                }
                var sample = new CpuSample();
                sample.Ticks = p.CpuTicks;
                sample.Time = now;
                history.Add(sample);
            }
            g.CpuPercent = seconds > 0 ? 100.0 * used / TimeSpan.FromSeconds(seconds).Ticks : 0;
            g.Busy = g.CpuPercent >= BusyCpuPercent;
        }

        void Classify(AppGroup g, DateTime now, bool wslTerminalOpen)
        {
            g.Rule = settings.RuleFor(g.RuleKey);
            g.Display = DisplayFor(g);

            if (g.Key == WslKey) { /* handled specially: closing it runs "wsl --shutdown" */ }
            else if (g.Procs.Any(p => p.Pid == selfPid)) Protect(g, "this is RamGuard");
            else if (string.Equals(g.Key, "msedgewebview2", StringComparison.OrdinalIgnoreCase)) Protect(g, "shared by other apps");
            else if (g.Procs.Any(p => SystemNames.Contains(p.Name))) Protect(g, "part of Windows");
            else if (g.Procs.Any(p => DriverHelperNames.Contains(p.Name))) Protect(g, "part of a driver");
            else if (g.Procs.Any(p => AntiCheatNames.Contains(p.Name))) Protect(g, "anti-cheat your games need");
            else if (ShellNames.Contains(g.RuleKey)) Protect(g, "terminal");
            else if (g.Procs.All(p => p.SessionId != selfSession)) Protect(g, "background service");

            DateTime last;
            if (!lastUsedByKey.TryGetValue(g.Key, out last))
                last = firstScanDone ? now : baseline; // new apps get a full idle period from when they appear
            foreach (var p in g.Procs)
            {
                DateTime t;
                if (lastForegroundByPid.TryGetValue(p.Pid, out t) && t > last)
                {
                    last = t;
                    everUsed.Add(g.Key);
                }
            }

            if (DoNotTouch.Count > 0 && g.Procs.Any(p => DoNotTouch.Contains(p.Pid))) g.InUseReason = "your game (game mode on)";
            else if (g.IsForeground) g.InUseReason = "open now";
            else if (g.Serving) g.InUseReason = "browser/app connected";
            else if (g.Key == WslKey && wslTerminalOpen) g.InUseReason = "a WSL terminal is open";
            else if (g.Busy && !g.Protected) g.InUseReason = "working in the background";

            if (g.InUseReason != null)
            {
                last = now;
                everUsed.Add(g.Key);
            }
            else
            {
                double left = settings.IdleMinutes - (now - last).TotalMinutes;
                if (left > 0) g.InUseReason = string.Format("idle in {0} min", Math.Max(1, (int)Math.Ceiling(left)));
            }

            g.LastUsed = last;
            g.EverUsed = everUsed.Contains(g.Key);
            g.InUse = g.InUseReason != null;
            lastUsedByKey[g.Key] = last;
        }

        static void Protect(AppGroup g, string reason)
        {
            g.Protected = true;
            g.ProtectReason = reason;
        }

        string DisplayFor(AppGroup g)
        {
            if (!g.IsTree)
                return FriendlyName(g.Key == WslKey ? WslKey : g.RootName); // the app itself, not one of its helpers

            var names = new List<string> { FriendlyName(g.RootName) };
            foreach (var p in g.Procs)
            {
                string n = FriendlyName(p.Name);
                if (!names.Contains(n)) names.Add(n);
            }
            string label = string.Join(" + ", names.Take(3));
            if (g.Ports.Count == 1) label += " · port " + g.Ports.First();
            else if (g.Ports.Count > 1) label += " · ports " + string.Join(", ", g.Ports.Take(3));
            else label += " · pid " + g.RootPid;
            return label;
        }

        /// <summary>Works out which app a process belongs to.</summary>
        KeyInfo Resolve(ProcInfo p, Dictionary<int, ProcInfo> procs, Dictionary<int, KeyInfo> cache)
        {
            KeyInfo k;
            if (cache.TryGetValue(p.Pid, out k)) return k;

            if (WslDockerNames.Contains(p.Name))
            {
                k = Simple(WslKey);
            }
            else if (RuntimeNames.Contains(p.Name) || HelperNames.Contains(p.Name))
            {
                // Walk up the parents: the top-most runtime is the "root" of this launch, and the first
                // real program above it is whoever started it.
                ProcInfo root = RuntimeNames.Contains(p.Name) ? p : null;
                ProcInfo anchor = null;
                ProcInfo cur = p;
                for (int i = 0; i < 32; i++)
                {
                    ProcInfo parent;
                    if (cur.ParentPid <= 0 || cur.ParentPid == cur.Pid || !procs.TryGetValue(cur.ParentPid, out parent)) break;
                    if (RuntimeNames.Contains(parent.Name)) { root = parent; cur = parent; continue; }
                    if (PassThroughNames.Contains(parent.Name) || HelperNames.Contains(parent.Name)) { cur = parent; continue; }
                    anchor = parent;
                    break;
                }

                bool startedByApp = anchor != null
                    && !ShellNames.Contains(anchor.Name)
                    && !string.Equals(anchor.Name, "explorer", StringComparison.OrdinalIgnoreCase);

                if (startedByApp)
                {
                    // e.g. an editor's language server or an app's helper servers: part of that app.
                    k = Resolve(anchor, procs, cache);
                }
                else if (root != null)
                {
                    // e.g. "npm run dev" typed in a terminal: its own row.
                    k = new KeyInfo();
                    k.RuleKey = root.Name.ToLowerInvariant();
                    k.Key = k.RuleKey + "#" + root.Pid;
                    k.IsTree = true;
                    k.RootName = root.Name;
                    k.RootPid = root.Pid;
                }
                else
                {
                    k = Simple(p.Name);
                }
            }
            else
            {
                k = Simple(p.Name);
            }

            cache[p.Pid] = k;
            return k;
        }

        static KeyInfo Simple(string name)
        {
            var k = new KeyInfo();
            k.Key = name.ToLowerInvariant();
            k.RuleKey = k.Key;
            k.RootName = name;
            return k;
        }
    }
}
