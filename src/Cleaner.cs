using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace RamGuard
{
    /// <summary>Closes and trims apps. Safe to call from a background thread.</summary>
    internal static class Cleaner
    {
        internal sealed class Result
        {
            public AppGroup Group;
            public bool Closed;
            public string Message;
            public long FreedBytes;
        }

        static readonly int selfPid, selfSession;

        static Cleaner()
        {
            using (var me = Process.GetCurrentProcess())
            {
                selfPid = me.Id;
                selfSession = me.SessionId;
            }
        }

        public static string Describe(AppGroup g)
        {
            if (g.Key == Scanner.WslKey)
                return "Docker will be stopped and WSL shut down (this also closes any open Ubuntu/WSL terminals).";
            if (g.HasWindow)
                return "It will be asked to close, so it can still ask you to save.";
            return "It has no window, so it will be ended right away. Anything unsaved in it is lost.";
        }

        public static string ShortAction(AppGroup g)
        {
            if (g.Key == Scanner.WslKey) return "stops Docker + WSL";
            return g.HasWindow ? "asks it to close" : "ends it";
        }

        public static Result Close(AppGroup g)
        {
            var r = new Result();
            r.Group = g;
            try
            {
                if (g.Key == Scanner.WslKey)
                {
                    CloseWslDocker(g, r);
                }
                else
                {
                    var mine = g.Procs.Where(p => p.SessionId == selfSession && p.Pid != selfPid).ToList();
                    if (mine.Count == 0)
                    {
                        r.Message = "RamGuard isn't allowed to close it";
                        return r;
                    }

                    var windows = Native.GetAppWindows(false);
                    var windowed = mine.Where(p => windows.ContainsKey(p.Pid)).ToList();
                    if (windowed.Count > 0)
                    {
                        // Polite close: same as clicking the window's X.
                        foreach (var p in windowed)
                            foreach (IntPtr h in windows[p.Pid])
                                Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

                        if (WaitGone(windowed, 8000))
                        {
                            r.Closed = true;
                            r.Message = "closed";
                        }
                        else
                        {
                            var still = mine.Where(IsAlive).ToList();
                            var anyWindow = Native.GetAppWindows(true);
                            if (still.Any(p => anyWindow.ContainsKey(p.Pid)))
                            {
                                // Something is still on screen, e.g. "Save changes?". Leave it to the user.
                                r.Message = "is still open, it may be asking you to save";
                            }
                            else
                            {
                                // Window closed but the app kept running in the tray.
                                foreach (var p in still) Kill(p);
                                r.Closed = WaitGone(still, 5000);
                                r.Message = r.Closed ? "closed (it was hiding in the tray)" : "could not be ended";
                            }
                        }
                    }
                    else
                    {
                        foreach (var p in mine) Kill(p);
                        r.Closed = WaitGone(mine, 5000);
                        r.Message = r.Closed ? "ended" : "could not be fully ended (it may need admin rights)";
                    }
                }
                if (r.Closed) r.FreedBytes = g.PrivateBytes;
            }
            catch (Exception ex)
            {
                r.Message = ex.Message;
            }
            return r;
        }

        /// <summary>
        /// Apps whose memory can be freed while they stay open: not protected, not in front of you,
        /// and not working in the background. With includeRecent, apps you used a few minutes ago are
        /// included too (used when RAM is very full).
        /// </summary>
        public static List<AppGroup> TrimTargets(IEnumerable<AppGroup> groups, bool includeRecent)
        {
            return groups.Where(g => !g.Protected && !g.IsForeground && !g.Busy
                                     && (includeRecent || !g.InUse)
                                     && g.WorkingSet >= 20 * Units.MB).ToList();
        }

        /// <summary>A process this quiet (CPU over the last minute, % of one core) is just sitting there.</summary>
        public const double QuietCpuPercent = 2.0;

        /// <summary>
        /// The processes whose memory can be freed without closing anything:
        ///  - every process of an idle app (or, with includeRecent, of any app in the background), and
        ///  - the quiet helper processes of apps you ARE using (background tabs, extensions, hidden
        ///    windows), as long as they're not the window in front of you and used no CPU in the last minute.
        /// Big apps like Chrome or VS Code-style editors run 10-20 processes, and most of them are quiet.
        /// </summary>
        public static List<ProcInfo> TrimPlan(Snapshot snap, bool includeRecent)
        {
            return TrimPlan(snap, includeRecent, null);
        }

        /// <param name="exclude">PIDs never to touch (the running game, voice chat, music...).</param>
        public static List<ProcInfo> TrimPlan(Snapshot snap, bool includeRecent, ICollection<int> exclude)
        {
            var plan = new List<ProcInfo>();
            foreach (var g in snap.Groups)
            {
                if (g.Protected) continue;
                bool wholeApp = !g.IsForeground && !g.Busy && (includeRecent || !g.InUse);
                foreach (var p in g.Procs)
                {
                    if (p.SessionId != selfSession || p.Pid == selfPid) continue;
                    if (exclude != null && exclude.Contains(p.Pid)) continue;
                    if (p.Pid == snap.ForegroundPid) continue;
                    if (p.WorkingSet < 15 * Units.MB) continue;
                    bool quiet = p.CpuPercent >= 0 && p.CpuPercent < QuietCpuPercent;
                    if (wholeApp || quiet) plan.Add(p);
                }
            }
            return plan;
        }

        public static int Trim(IEnumerable<ProcInfo> procs)
        {
            int count = 0;
            foreach (var p in procs)
                if (p.SessionId == selfSession && p.Pid != selfPid && Native.TrimWorkingSet(p.Pid))
                    count++;
            return count;
        }

        /// <summary>
        /// Moves apps' memory out of physical RAM. Nothing is closed: each app keeps its window,
        /// its state and its data, and Windows brings the memory back the moment the app needs it.
        /// </summary>
        public static int Trim(IEnumerable<AppGroup> groups)
        {
            int count = 0;
            foreach (var g in groups)
                foreach (var p in g.Procs)
                    if (p.SessionId == selfSession && p.Pid != selfPid && Native.TrimWorkingSet(p.Pid))
                        count++;
            return count;
        }

        static void CloseWslDocker(AppGroup g, Result r)
        {
            RunQuiet("docker", "desktop stop", 90000);
            foreach (var p in g.Procs)
                if (p.SessionId == selfSession && !p.Name.StartsWith("vmmem", StringComparison.OrdinalIgnoreCase))
                    Kill(p);
            RunQuiet("wsl.exe", "--shutdown", 60000);

            bool vmGone = false;
            for (int i = 0; i < 20 && !vmGone; i++)
            {
                vmGone = !AnyProcessNamed("vmmemWSL") && !AnyProcessNamed("vmmem");
                if (!vmGone) Thread.Sleep(500);
            }
            r.Closed = vmGone;
            r.Message = vmGone ? "Docker stopped and WSL shut down" : "WSL is still running";
        }

        static bool AnyProcessNamed(string name)
        {
            var list = Process.GetProcessesByName(name);
            foreach (var p in list) p.Dispose();
            return list.Length > 0;
        }

        static void RunQuiet(string file, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (var p = Process.Start(psi)) p.WaitForExit(timeoutMs);
            }
            catch { }
        }

        static void Kill(ProcInfo p)
        {
            try
            {
                using (var proc = Process.GetProcessById(p.Pid))
                {
                    // Make sure the PID wasn't reused by a different program.
                    if (string.Equals(proc.ProcessName, p.Name, StringComparison.OrdinalIgnoreCase)) proc.Kill();
                }
            }
            catch { }
        }

        static bool IsAlive(ProcInfo p)
        {
            try
            {
                using (var proc = Process.GetProcessById(p.Pid))
                    return string.Equals(proc.ProcessName, p.Name, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static bool WaitGone(List<ProcInfo> procs, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                if (!procs.Any(IsAlive)) return true;
                if (sw.ElapsedMilliseconds > timeoutMs) return false;
                Thread.Sleep(200);
            }
        }
    }
}
