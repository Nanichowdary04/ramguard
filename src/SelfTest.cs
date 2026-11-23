using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RamGuard
{
    /// <summary>
    /// Automated checks. Only ever closes processes it started itself (rg-dummy.exe and a test node.exe).
    /// </summary>
    internal static class SelfTest
    {
        static StringBuilder log;
        static int failures;

        static void Check(bool ok, string what)
        {
            log.AppendLine((ok ? "PASS  " : "FAIL  ") + what);
            if (!ok) failures++;
        }

        public static int Run(string outPath)
        {
            log = new StringBuilder();
            failures = 0;
            string dummy = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rg-dummy.exe");
            var settings = new Settings();   // in-memory: never touches the user's saved settings
            settings.IdleMinutes = 10;
            settings.MinMb = 100;
            int selfPid;
            using (var me = Process.GetCurrentProcess()) selfPid = me.Id;

            try
            {
                // 1. What is protected, on the real machine.
                var snap = new Scanner(settings, DateTime.Now.AddHours(-1)).Scan();
                Check(snap.Groups.Count > 10, "scan found " + snap.Groups.Count + " apps");
                Check(snap.Memory.TotalBytes > 0, "RAM: " + Units.Size(snap.Memory.UsedBytes) + " of " + Units.Size(snap.Memory.TotalBytes) + " in use (" + snap.Memory.LoadPercent + "%)");
                var explorer = snap.Find("explorer");
                Check(explorer != null && explorer.Protected, "File Explorer is protected");
                var svchost = snap.Find("svchost");
                Check(svchost != null && svchost.Protected, "Windows services (svchost) are protected");
                var self = snap.FindByPid(selfPid);
                Check(self != null && self.Protected, "RamGuard never closes itself");
                Check(!snap.Groups.Any(g => g.Protected && g.IsCandidate(settings)), "no protected app is ever offered for closing");
                var waves = snap.Find("wavessvc64");
                if (waves != null) Check(waves.Protected, "Waves audio driver helper is protected");
                var shells = snap.Groups.Where(g => g.RuleKey == "powershell" || g.RuleKey == "bash" || g.RuleKey == "cmd").ToList();
                Check(shells.All(g => g.Protected), "terminals are protected (" + shells.Count + " found)");

                log.AppendLine();
                log.AppendLine("      Apps on this PC over 50 MB (as if nothing had been used for an hour):");
                foreach (var g in snap.Groups.Where(x => !x.Protected && x.PrivateBytes >= 50 * Units.MB).OrderByDescending(x => x.PrivateBytes))
                    log.AppendLine(string.Format("        {0,-44} {1,9}  rule={2,-9} {3}{4}", g.Display, Units.Size(g.PrivateBytes), g.Rule,
                        g.InUse ? "in use (" + g.InUseReason + ")" : "idle", g.IsCandidate(settings) ? "  -> would be offered" : ""));
                log.AppendLine();

                // 2. A windowless idle app is offered and ended.
                using (var p1 = Process.Start(dummy, "--mb 200"))
                {
                    Thread.Sleep(2500);
                    var sc = new Scanner(settings, DateTime.Now.AddHours(-1));
                    var g = sc.Scan().FindByPid(p1.Id);
                    Check(g != null && g.PrivateBytes >= 190 * Units.MB, "test app found, holding " + (g == null ? "?" : Units.Size(g.PrivateBytes)));
                    Check(g != null && !g.Protected && !g.InUse && g.IsCandidate(settings), "idle test app is offered for closing");
                    sc.NoteForeground(p1.Id);
                    var again = sc.Scan().FindByPid(p1.Id);
                    Check(again != null && again.InUse && !again.IsCandidate(settings), "once you use an app it counts as in use (" + (again == null ? "?" : again.InUseReason) + ")");
                    var r = Cleaner.Close(g);
                    Check(p1.WaitForExit(5000), "windowless test app was ended (" + r.Message + ")");
                }

                // 3. A windowed app is asked to close (exit code 0 = it closed itself, it wasn't killed).
                using (var p2 = Process.Start(dummy, "--mb 120 --window"))
                {
                    WaitForWindow(p2, 8000);
                    var g = new Scanner(settings, DateTime.Now.AddHours(-1)).Scan().FindByPid(p2.Id);
                    Check(g != null && g.HasWindow, "test window detected");
                    var r = Cleaner.Close(g);
                    bool exited = p2.WaitForExit(8000);
                    Check(exited && p2.ExitCode == 0, "windowed test app closed politely (" + r.Message + ", exit code " + (exited ? p2.ExitCode.ToString() : "still running") + ")");
                    if (!exited) p2.Kill();
                }

                // 4. A server with a connected client counts as in use.
                using (var p3 = Process.Start(dummy, "--mb 120 --listen 47811"))
                {
                    Thread.Sleep(2500);
                    var sc = new Scanner(settings, DateTime.Now.AddHours(-1));
                    var g = sc.Scan().FindByPid(p3.Id);
                    Check(g != null && g.Ports.Contains(47811), "listening port detected");
                    Check(g != null && !g.Serving && !g.InUse, "server with nobody connected is idle");
                    using (var client = new TcpClient())
                    {
                        client.Connect(IPAddress.Loopback, 47811);
                        Thread.Sleep(500);
                        var g2 = sc.Scan().FindByPid(p3.Id);
                        Check(g2 != null && g2.Serving && g2.InUse, "server with a connected client is in use (" + (g2 == null ? "?" : g2.InUseReason) + ")");
                    }
                    p3.Kill();
                    p3.WaitForExit(3000);
                }

                // 5. Trimming moves an idle app's memory out of RAM.
                using (var p4 = Process.Start(dummy, "--mb 150"))
                {
                    Thread.Sleep(2500);
                    var sc = new Scanner(settings, DateTime.Now.AddHours(-1));
                    var g = sc.Scan().FindByPid(p4.Id);
                    long before = g.WorkingSet;
                    Cleaner.Trim(new[] { g });
                    Thread.Sleep(300);
                    var g2 = sc.Scan().FindByPid(p4.Id);
                    Check(g2 != null && g2.WorkingSet < before / 2,
                        string.Format("trim moved idle memory out of RAM ({0} -> {1})", Units.Size(before), g2 == null ? "?" : Units.Size(g2.WorkingSet)));
                    p4.Kill();
                    p4.WaitForExit(3000);
                }

                // 5b. Freeing memory never closes an app: it keeps running, keeps its window, keeps its data.
                using (var p5 = Process.Start(dummy, "--mb 150 --window"))
                {
                    WaitForWindow(p5, 8000);
                    Thread.Sleep(1500);
                    var sc = new Scanner(settings, DateTime.Now.AddHours(-1));
                    var g = sc.Scan().FindByPid(p5.Id);
                    long before = g == null ? 0 : g.WorkingSet;
                    var targets = Cleaner.TrimTargets(new[] { g }, true);
                    Check(targets.Count == 1, "an app in the background can have its memory freed");
                    Cleaner.Trim(targets);
                    Thread.Sleep(500);
                    var g2 = sc.Scan().FindByPid(p5.Id);
                    Check(!p5.HasExited && g2 != null && g2.HasWindow,
                        "after freeing memory the app is still running with its window open (nothing closed)");
                    Check(g2 != null && g2.WorkingSet < before / 2,
                        string.Format("its RAM went {0} -> {1}", Units.Size(before), g2 == null ? "?" : Units.Size(g2.WorkingSet)));
                    // Touching the data again brings it back: the app's memory content is intact.
                    Check(g2 != null && g2.PrivateBytes >= 140 * Units.MB, "its data is still there (" + (g2 == null ? "?" : Units.Size(g2.PrivateBytes)) + " held)");
                    p5.CloseMainWindow();
                    if (!p5.WaitForExit(5000)) p5.Kill();
                }

                // 5c. An app working in the background (CPU busy) counts as in use: never closed, never trimmed.
                using (var p6 = Process.Start(dummy, "--mb 120 --spin"))
                {
                    var sc = new Scanner(settings, DateTime.Now.AddHours(-1));
                    sc.MinCpuWindow = TimeSpan.FromSeconds(2);
                    sc.Scan();
                    Thread.Sleep(3500);
                    var g = sc.Scan().FindByPid(p6.Id);
                    Check(g != null && g.Busy && g.InUse && !g.IsCandidate(settings),
                        string.Format("a busy app counts as in use ({0}, {1:0}% CPU)", g == null ? "?" : g.InUseReason, g == null ? 0 : g.CpuPercent));
                    var snapBusy = new Snapshot();
                    if (g != null) snapBusy.Groups.Add(g);
                    Check(g != null && Cleaner.TrimPlan(snapBusy, true).Count == 0, "a busy app's memory is left alone");
                    p6.Kill();
                    p6.WaitForExit(3000);
                }

                // 5d. An app you're using (like Chrome): its busy part is left alone,
                //     only its quiet helper processes are freed, and both keep running.
                using (var busyPart = Process.Start(dummy, "--mb 60 --spin"))
                using (var quietPart = Process.Start(dummy, "--mb 150"))
                {
                    Thread.Sleep(3000); // let both finish starting up (filling their memory) before measuring
                    var sc = new Scanner(settings, DateTime.Now.AddHours(-1));
                    sc.MinCpuWindow = TimeSpan.FromSeconds(2);
                    sc.Scan();
                    Thread.Sleep(3500);
                    var snapUse = sc.Scan();
                    var g = snapUse.FindByPid(busyPart.Id);
                    Check(g != null && g.InUse && g.Procs.Any(p => p.Pid == quietPart.Id),
                        "two-process test app is in use because one part is working");
                    var plan = Cleaner.TrimPlan(snapUse, false).Where(p => p.Pid == busyPart.Id || p.Pid == quietPart.Id).ToList();
                    Check(plan.Count == 1 && plan[0].Pid == quietPart.Id, "only its quiet helper is chosen; the busy part is left alone");
                    var qBefore = g == null ? null : g.Procs.FirstOrDefault(p => p.Pid == quietPart.Id);
                    Cleaner.Trim(plan);
                    Thread.Sleep(500);
                    var gAfter = sc.Scan().FindByPid(quietPart.Id);
                    var qAfter = gAfter == null ? null : gAfter.Procs.FirstOrDefault(p => p.Pid == quietPart.Id);
                    Check(qBefore != null && qAfter != null && qAfter.WorkingSet < qBefore.WorkingSet / 2 && !busyPart.HasExited && !quietPart.HasExited,
                        string.Format("quiet helper's RAM {0} -> {1}, and both parts are still running",
                            qBefore == null ? "?" : Units.Size(qBefore.WorkingSet), qAfter == null ? "?" : Units.Size(qAfter.WorkingSet)));
                    busyPart.Kill(); quietPart.Kill();
                    busyPart.WaitForExit(3000); quietPart.WaitForExit(3000);
                }

                var snapAll = new Scanner(settings, DateTime.Now.AddHours(-1)).Scan();
                var protectedPids = new HashSet<int>(snapAll.Groups.Where(x => x.Protected).SelectMany(x => x.Procs).Select(p => p.Pid));
                bool touchesProtected = false;
                foreach (ProcInfo tp in Cleaner.TrimPlan(snapAll, true))
                    if (protectedPids.Contains(tp.Pid) || tp.Pid == snapAll.ForegroundPid) touchesProtected = true;
                Check(!touchesProtected, "Windows parts, terminals and the window in front of you are never trimmed");

                // 5e. Game mode.
                settings.GamePowerBoost = false; // never change the real power mode during a test
                GameMode.StateFile = Path.Combine(Path.GetTempPath(), "rg-gamemode-test.txt");
                Check(GameMode.DecideByName("VALORANT-Win64-Shipping", settings, null) == true, "VALORANT is recognised as a game");
                Check(GameMode.DecideByName("chrome", settings, null) == false && GameMode.DecideByName("claude", settings, null) == false
                      && GameMode.DecideByName("Antigravity IDE", settings, null) == false && GameMode.DecideByName("RiotClientServices", settings, null) == false,
                    "Chrome, Claude, Antigravity and game launchers are never treated as the game");
                Check(GameMode.DecideByPath("javaw", @"C:\Users\x\AppData\Roaming\.minecraft\runtime\java-runtime-epsilon\windows\java-runtime-epsilon\bin\javaw.exe", "", null),
                    "Minecraft (Java from the .minecraft folder) is recognised as a game");
                string gameExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rg-gametest.exe");
                File.Copy(dummy, gameExe, true);
                settings.Games.Add("rg-gametest");
                using (var gameProc = Process.Start(gameExe, "--mb 150"))
                using (var other = Process.Start(dummy, "--mb 150"))
                {
                    Thread.Sleep(3000);
                    Func<ProcInfo, bool> onlyTestApp = x => x.Pid == other.Id;
                    var gm = new GameMode(settings);
                    string detected;
                    Check(gm.IsGame(gameProc.Id, out detected), "test game is detected as a game");
                    Check(!gm.IsGame(other.Id, out detected), "test background app is not a game");

                    gm.Begin(gameProc.Id, "rg-gametest");
                    var sc = new Scanner(settings, DateTime.Now.AddHours(-1));
                    foreach (int gp in gm.GamePids) sc.DoNotTouch.Add(gp);
                    var snapGame = sc.Scan();
                    gm.CalmOthers(snapGame, onlyTestApp);
                    other.Refresh(); gameProc.Refresh();
                    Check(other.PriorityClass == ProcessPriorityClass.BelowNormal, "background app is slowed down (below-normal priority + efficiency mode)");
                    Check(gameProc.PriorityClass == ProcessPriorityClass.Normal, "the game itself is never touched");
                    var gamePlan = Cleaner.TrimPlan(snapGame, true, gm.TrimExclusions(snapGame));
                    bool trimsOther = false, trimsGame = false;
                    foreach (ProcInfo tp in gamePlan) { if (tp.Pid == other.Id) trimsOther = true; if (tp.Pid == gameProc.Id) trimsGame = true; }
                    Check(trimsOther && !trimsGame, "memory is freed from other apps, never from the game");
                    Check(File.Exists(GameMode.StateFile), "game mode remembers what it changed, in case RamGuard stops mid-game");

                    int fixedAfterCrash = GameMode.RestoreLeftovers();
                    other.Refresh();
                    Check(fixedAfterCrash >= 1 && other.PriorityClass == ProcessPriorityClass.Normal, "if RamGuard stopped mid-game, the next start puts apps back to normal");

                    var gm2 = new GameMode(settings);
                    gm2.Begin(gameProc.Id, "rg-gametest");
                    gm2.CalmOthers(snapGame, onlyTestApp);
                    int restored = gm2.Stop();
                    other.Refresh();
                    Check(restored == 1 && other.PriorityClass == ProcessPriorityClass.Normal && !File.Exists(GameMode.StateFile),
                        "when the game closes, everything goes back to normal");

                    other.Kill(); gameProc.Kill();
                    other.WaitForExit(3000); gameProc.WaitForExit(3000);
                }
                try { File.Delete(gameExe); } catch { }
                settings.Games.Remove("rg-gametest");

                // 5f. Growing apps.
                {
                    DateTime t0 = DateTime.Now.AddMinutes(-90);
                    var rnd = new Random(1);
                    var steady = new List<MemSample>();
                    var flat = new List<MemSample>();
                    var oneJump = new List<MemSample>();
                    var grewThenStopped = new List<MemSample>();
                    for (int i = 0; i <= 90; i++)
                    {
                        DateTime t = t0.AddMinutes(i);
                        steady.Add(new MemSample(t, (300 + i * 9 + rnd.Next(-15, 15)) * Units.MB));          // +810 MB in 1.5 h
                        flat.Add(new MemSample(t, (420 + (i % 10) * 6 + rnd.Next(-10, 10)) * Units.MB));     // garbage-collection sawtooth
                        oneJump.Add(new MemSample(t, (i < 45 ? 400 : 900) * Units.MB));                      // opened something big once
                        grewThenStopped.Add(new MemSample(t, (i < 45 ? 300 + i * 15 : 975) * Units.MB));     // grew, then levelled off
                    }
                    var rs = Growth.Analyze(steady);
                    Check(rs.Growing, string.Format("an app climbing steadily is flagged as growing (+{0} in {1}, {2}/h)",
                        Units.Size(rs.GrowthBytes), Units.Span(rs.Window), Units.Size((long)rs.BytesPerHour)));
                    Check(!Growth.Analyze(flat).Growing, "normal up-and-down memory is not flagged");
                    Check(!Growth.Analyze(oneJump).Growing, "one big jump (opening something) is not flagged");
                    Check(!Growth.Analyze(grewThenStopped).Growing, "an app that grew and then levelled off is not flagged");
                    Check(!Growth.Analyze(steady.GetRange(0, 30)).Growing, "nothing is flagged before 45 minutes of history");
                }

                // 5g. Today's savings.
                {
                    var st = new Stats();
                    st.AddFree(300 * Units.MB);
                    st.AddFree(900 * Units.MB);
                    st.AddGame(TimeSpan.FromMinutes(75));
                    Check(st.Frees == 2 && st.FreedBytes == 1200 * Units.MB && st.Games == 1, "savings add up: " + st.Summary());
                }

                // 5h. Startup apps: read, switch off/on, remove a broken entry (on a temporary test entry only).
                {
                    const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
                    const string approvedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
                    Check(StartupManager.ExeFromCommand("\"C:\\Program Files\\App\\a.exe\" --min") == @"C:\Program Files\App\a.exe"
                          && StartupManager.ExeFromCommand(@"C:\WINDOWS\system32\SecurityHealthSystray.exe") == @"C:\WINDOWS\system32\SecurityHealthSystray.exe",
                        "startup command lines are read correctly");
                    Check(StartupManager.IsDisabled(StartupManager.ApprovedBytes(false)) && !StartupManager.IsDisabled(StartupManager.ApprovedBytes(true)),
                        "on/off flags use the same format as Task Manager");
                    try
                    {
                        using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKey))
                        {
                            k.SetValue("RamGuardSelfTest", "\"" + Environment.ExpandEnvironmentVariables(@"%windir%\notepad.exe") + "\" /test");
                            k.SetValue("RamGuardSelfTestBroken", @"C:\RamGuard-does-not-exist\nothing.exe");
                        }
                        var entries = StartupManager.Load();
                        var test = entries.FirstOrDefault(x => x.Name == "RamGuardSelfTest");
                        var broken = entries.FirstOrDefault(x => x.Name == "RamGuardSelfTestBroken");
                        Check(entries.Count >= 3 && test != null && test.Enabled && !test.FileMissing, "startup list found " + entries.Count + " apps, including the test entry");
                        Check(entries.Any(x => x.Locked != null && x.Display.IndexOf("Security", StringComparison.OrdinalIgnoreCase) >= 0)
                              || !entries.Any(x => x.Name == "SecurityHealth"), "Windows Security can't be switched off here");
                        string err = StartupManager.SetEnabled(test, false);
                        var afterOff = StartupManager.Load().FirstOrDefault(x => x.Name == "RamGuardSelfTest");
                        err = err ?? StartupManager.SetEnabled(afterOff, true);
                        var afterOn = StartupManager.Load().FirstOrDefault(x => x.Name == "RamGuardSelfTest");
                        Check(err == null && afterOff != null && !afterOff.Enabled && afterOn != null && afterOn.Enabled,
                            "an app can be switched off and back on (it stays installed)");
                        Check(broken != null && broken.FileMissing && StartupManager.RemoveBroken(broken) == null
                              && StartupManager.Load().All(x => x.Name != "RamGuardSelfTestBroken"),
                            "a broken entry (program gone) can be removed");
                    }
                    finally
                    {
                        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, true))
                            if (k != null) { k.DeleteValue("RamGuardSelfTest", false); k.DeleteValue("RamGuardSelfTestBroken", false); }
                        using (var a = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(approvedKey, true))
                            if (a != null) { a.DeleteValue("RamGuardSelfTest", false); a.DeleteValue("RamGuardSelfTestBroken", false); }
                    }
                }

                // 6. Grouping: a dev server started from a terminal gets its own row,
                //    while a helper started directly by an app stays part of that app.
                string node = FindOnPath("node.exe");
                if (node == null)
                {
                    log.AppendLine("SKIP  node.exe not installed; grouping test skipped");
                }
                else
                {
                    var psiShell = new ProcessStartInfo("powershell.exe",
                        "-NoProfile -Command \"& '" + node + "' -e 'setTimeout(function(){}, 600000)'\"");
                    psiShell.UseShellExecute = false;
                    psiShell.CreateNoWindow = true;
                    var psiDirect = new ProcessStartInfo(node, "-e \"setTimeout(function(){}, 600000)\"");
                    psiDirect.UseShellExecute = false;
                    psiDirect.CreateNoWindow = true;
                    using (var shell = Process.Start(psiShell))
                    using (var direct = Process.Start(psiDirect))
                    {
                        int child = 0;
                        for (int i = 0; i < 40 && child == 0; i++)
                        {
                            Thread.Sleep(250);
                            child = Native.GetParentMap().Where(kv => kv.Value == shell.Id).Select(kv => kv.Key)
                                .FirstOrDefault(pid => IsNamed(pid, "node"));
                        }
                        var snap6 = new Scanner(settings, DateTime.Now.AddHours(-1)).Scan();
                        var tg = snap6.FindByPid(child);
                        Check(tg != null && tg.IsTree && !tg.Protected && tg.Key.StartsWith("node#"),
                            "node started from a terminal gets its own row: " + (tg == null ? "missing" : tg.Display));
                        var dg = snap6.FindByPid(direct.Id);
                        Check(dg != null && dg.Procs.Any(p => p.Pid == selfPid),
                            "node started by an app is grouped with that app (" + (dg == null ? "missing" : dg.Display) + ")");
                        KillQuietly(child);
                        KillQuietly(shell.Id);
                        KillQuietly(direct.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                Check(false, "crashed: " + ex);
            }

            log.AppendLine();
            log.AppendLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
            File.WriteAllText(outPath, log.ToString(), new UTF8Encoding(true));
            return failures;
        }

        static void WaitForWindow(Process p, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (Native.GetAppWindows(false).ContainsKey(p.Id)) return;
                Thread.Sleep(200);
            }
        }

        static bool IsNamed(int pid, string name)
        {
            try { using (var p = Process.GetProcessById(pid)) return string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        static void KillQuietly(int pid)
        {
            if (pid <= 0) return;
            try { using (var p = Process.GetProcessById(pid)) { p.Kill(); p.WaitForExit(3000); } }
            catch { }
        }

        static string FindOnPath(string exe)
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    if (dir.Trim().Length == 0) continue;
                    string full = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }
    }
}
