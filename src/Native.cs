using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RamGuard
{
    internal struct MemoryStatus
    {
        public int LoadPercent;
        public long TotalBytes;
        public long AvailableBytes;
        public long UsedBytes { get { return TotalBytes - AvailableBytes; } }
    }

    internal sealed class TcpInfo
    {
        // Ports each process is listening on.
        public readonly Dictionary<int, SortedSet<int>> ListeningPorts = new Dictionary<int, SortedSet<int>>();
        // Processes that are serving a live connection right now (e.g. a dev server with a browser tab open).
        public readonly HashSet<int> ServingPids = new HashSet<int>();
    }

    internal static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        const uint GW_OWNER = 4;
        public const uint WM_CLOSE = 0x0010;
        const int DWMWA_CLOAKED = 14;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr hProcess);
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint PROCESS_SET_QUOTA = 0x0100;

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
        const uint TH32CS_SNAPPROCESS = 0x2;

        [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);
        const int TCP_TABLE_OWNER_PID_ALL = 5;
        const int MIB_TCP_STATE_LISTEN = 2;
        const int MIB_TCP_STATE_ESTAB = 5;

        public static MemoryStatus GetMemory()
        {
            var m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            var s = new MemoryStatus();
            if (GlobalMemoryStatusEx(ref m))
            {
                s.LoadPercent = (int)m.dwMemoryLoad;
                s.TotalBytes = (long)m.ullTotalPhys;
                s.AvailableBytes = (long)m.ullAvailPhys;
            }
            return s;
        }

        public static Dictionary<int, int> GetParentMap()
        {
            var map = new Dictionary<int, int>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var e = new PROCESSENTRY32();
                e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (Process32First(snap, ref e))
                {
                    do { map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; }
                    while (Process32Next(snap, ref e));
                }
            }
            finally { CloseHandle(snap); }
            return map;
        }

        /// <summary>
        /// Visible, titled, non-cloaked top-level windows, grouped by owning process.
        /// With includeOwned=false only main app windows are returned (what you'd close);
        /// with includeOwned=true dialogs such as "Save changes?" are included too.
        /// </summary>
        public static Dictionary<int, List<IntPtr>> GetAppWindows(bool includeOwned)
        {
            var result = new Dictionary<int, List<IntPtr>>();
            EnumWindowsProc cb = delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd)) return true;
                if (!includeOwned && GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true;
                if (GetWindowTextLength(hWnd) == 0) return true;
                int cloaked;
                if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                List<IntPtr> list;
                if (!result.TryGetValue((int)pid, out list)) { list = new List<IntPtr>(); result[(int)pid] = list; }
                list.Add(hWnd);
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return result;
        }

        /// <summary>The process that owns the window you're looking at (resolves Store apps hosted in ApplicationFrameHost).</summary>
        public static int GetForegroundPid()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return 0;
            uint pid;
            GetWindowThreadProcessId(fg, out pid);
            if (IsProcessNamed((int)pid, "ApplicationFrameHost"))
            {
                uint outer = pid, inner = 0;
                EnumWindowsProc cb = delegate(IntPtr child, IntPtr l)
                {
                    uint cp;
                    GetWindowThreadProcessId(child, out cp);
                    if (cp != outer) { inner = cp; return false; }
                    return true;
                };
                EnumChildWindows(fg, cb, IntPtr.Zero);
                GC.KeepAlive(cb);
                if (inner != 0) pid = inner;
            }
            return (int)pid;
        }

        static bool IsProcessNamed(int pid, string name)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static TcpInfo GetTcpInfo()
        {
            var info = new TcpInfo();
            var listening = new HashSet<long>();
            var established = new List<long>();
            // IPv4 rows: state@0 localPort@8 pid@20 (24 bytes). IPv6 rows: localPort@20 state@48 pid@52 (56 bytes).
            ReadTcpTable(2, 24, 0, 8, 20, listening, established, info);
            ReadTcpTable(23, 56, 48, 20, 52, listening, established, info);
            foreach (long key in established)
                if (listening.Contains(key)) info.ServingPids.Add((int)(key >> 16));
            return info;
        }

        static void ReadTcpTable(int af, int rowSize, int stateOff, int portOff, int pidOff,
                                 HashSet<long> listening, List<long> established, TcpInfo info)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
            for (int attempt = 0; attempt < 3 && size > 0; attempt++)
            {
                size += 32 * rowSize; // connections may appear between the two calls
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0) != 0) continue;
                    int count = Marshal.ReadInt32(buf);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr row = IntPtr.Add(buf, 4 + i * rowSize);
                        int state = Marshal.ReadInt32(row, stateOff);
                        int raw = Marshal.ReadInt32(row, portOff);
                        int port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                        int pid = Marshal.ReadInt32(row, pidOff);
                        long key = ((long)pid << 16) | (uint)port;
                        if (state == MIB_TCP_STATE_LISTEN)
                        {
                            listening.Add(key);
                            SortedSet<int> ports;
                            if (!info.ListeningPorts.TryGetValue(pid, out ports))
                            {
                                ports = new SortedSet<int>();
                                info.ListeningPorts[pid] = ports;
                            }
                            ports.Add(port);
                        }
                        else if (state == MIB_TCP_STATE_ESTAB)
                        {
                            established.Add(key);
                        }
                    }
                    return;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }

        [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        /// <summary>Total CPU time the process has used, in 100 ns ticks, or -1 if it can't be read.</summary>
        public static long GetCpuTicks(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return -1;
            try
            {
                long creation, exit, kernel, user;
                return GetProcessTimes(h, out creation, out exit, out kernel, out user) ? kernel + user : -1;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>Moves a process's idle pages out of RAM (they come back on demand).</summary>
        public static bool TrimWorkingSet(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, pid);
            if (h == IntPtr.Zero) return false;
            try { return EmptyWorkingSet(h); }
            finally { CloseHandle(h); }
        }

        // ---------------------------------------------------------------- game mode helpers

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("kernel32.dll")] static extern uint GetPriorityClass(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool SetPriorityClass(IntPtr h, uint cls);
        [StructLayout(LayoutKind.Sequential)]
        struct POWER_THROTTLING_STATE { public uint Version; public uint ControlMask; public uint StateMask; }
        [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int infoClass, ref POWER_THROTTLING_STATE info, int size);
        [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid scheme);

        const uint PROCESS_SET_INFORMATION = 0x0200;
        const uint NORMAL_PRIORITY_CLASS = 0x20;
        const uint BELOW_NORMAL_PRIORITY_CLASS = 0x4000;
        const int ProcessPowerThrottling = 4;
        const uint THROTTLE_EXECUTION_SPEED = 0x1;

        /// <summary>Windows' "Best performance" power mode (Settings → System → Power).</summary>
        public static readonly Guid BestPerformancePowerMode = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");

        public static string GetExePath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        public static string GetForegroundTitle()
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "";
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>
        /// Gives a process lower CPU priority and Windows "efficiency mode" (on hybrid Intel CPUs it then runs
        /// on the efficiency cores). Only touches processes at normal priority. Nothing is closed.
        /// </summary>
        public static bool Calm(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                if (GetPriorityClass(h) != NORMAL_PRIORITY_CLASS) return false;
                if (!SetPriorityClass(h, BELOW_NORMAL_PRIORITY_CLASS)) return false;
                var s = new POWER_THROTTLING_STATE();
                s.Version = 1;
                s.ControlMask = THROTTLE_EXECUTION_SPEED;
                s.StateMask = THROTTLE_EXECUTION_SPEED;
                SetProcessInformation(h, ProcessPowerThrottling, ref s, Marshal.SizeOf(typeof(POWER_THROTTLING_STATE)));
                return true;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>Undoes Calm: normal priority again, and Windows decides about efficiency mode as usual.</summary>
        public static void Uncalm(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return;
            try
            {
                if (GetPriorityClass(h) == BELOW_NORMAL_PRIORITY_CLASS) SetPriorityClass(h, NORMAL_PRIORITY_CLASS);
                var s = new POWER_THROTTLING_STATE();
                s.Version = 1;
                SetProcessInformation(h, ProcessPowerThrottling, ref s, Marshal.SizeOf(typeof(POWER_THROTTLING_STATE)));
            }
            finally { CloseHandle(h); }
        }

        public static bool IsCalm(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try { return GetPriorityClass(h) == BELOW_NORMAL_PRIORITY_CLASS; }
            finally { CloseHandle(h); }
        }

        public static Guid? GetPowerMode()
        {
            try
            {
                Guid g;
                return PowerGetEffectiveOverlayScheme(out g) == 0 ? g : (Guid?)null;
            }
            catch { return null; }
        }

        public static bool SetPowerMode(Guid mode)
        {
            try { return PowerSetActiveOverlayScheme(mode) == 0; }
            catch { return false; }
        }

        public static void UseDarkTitleBar(IntPtr hwnd, bool dark)
        {
            int v = dark ? 1 : 0;
            try { DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4); }
            catch { }
        }
    }
}
