using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RamGuard
{
    internal enum StartupSource { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder, StoreApp }

    internal sealed class StartupEntry
    {
        public StartupSource Source;
        public string Name;        // registry value name, .lnk file name, or "PackageFamily\TaskId"
        public string Display;
        public string Command;     // command line / shortcut target
        public string ExePath;     // the program it starts (may be null)
        public bool Enabled;
        public bool NeedsAdmin;
        public bool FileMissing;
        public string Locked;      // why RamGuard won't switch it off (null = can be switched)
        public bool Running;
        public long RunningBytes;

        public string Where
        {
            get
            {
                switch (Source)
                {
                    case StartupSource.UserRun: return "Your startup list";
                    case StartupSource.MachineRun:
                    case StartupSource.MachineRun32: return "All users (needs admin)";
                    case StartupSource.UserFolder: return "Startup folder";
                    case StartupSource.CommonFolder: return "All users' startup folder (needs admin)";
                    default: return "Store app";
                }
            }
        }
    }

    /// <summary>
    /// Reads and switches the apps that start when you sign in, the same way Task Manager's Startup page does
    /// (an on/off flag in "StartupApproved"), so nothing is deleted and everything can be switched back on.
    /// Entries for all users need admin rights: Windows asks once per change.
    /// </summary>
    internal static class StartupManager
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
        const string StoreTasksKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

        static readonly Dictionary<string, string> StoreNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "WhatsAppDesktop", "WhatsApp" }, { "SpotifyMusic", "Spotify" }, { "YourPhone", "Phone Link" },
            { "MSTeams", "Microsoft Teams" }, { "Teams", "Microsoft Teams" }, { "OutlookForWindows", "Outlook" },
            { "Todos", "Microsoft To Do" }, { "Copilot", "Copilot" }, { "Claude", "Claude" }, { "ChatGPT-Desktop", "ChatGPT" },
            { "XboxApp", "Xbox" }, { "GamingApp", "Xbox" }, { "Disney", "Disney+" }, { "Netflix", "Netflix" }
        };

        // ------------------------------------------------------------ reading

        public static List<StartupEntry> Load()
        {
            var list = new List<StartupEntry>();
            ReadRun(Registry.CurrentUser, RunKey, StartupSource.UserRun, "Run", list);
            ReadRun(Registry.LocalMachine, RunKey, StartupSource.MachineRun, "Run", list);
            ReadRun(Registry.LocalMachine, Run32Key, StartupSource.MachineRun32, "Run32", list);
            ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.UserFolder, Registry.CurrentUser, list);
            ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.CommonFolder, Registry.LocalMachine, list);
            ReadStoreTasks(list);
            foreach (var e in list) e.Locked = LockReason(e);
            FillRunning(list);
            return list.OrderBy(e => e.Locked != null).ThenByDescending(e => e.Enabled).ThenBy(e => e.Display).ToList();
        }

        static void ReadRun(RegistryKey hive, string key, StartupSource src, string approvedSub, List<StartupEntry> list)
        {
            try
            {
                using (var k = hive.OpenSubKey(key))
                using (var approved = hive.OpenSubKey(ApprovedKey + "\\" + approvedSub))
                {
                    if (k == null) return;
                    foreach (string name in k.GetValueNames())
                    {
                        string cmd = k.GetValue(name) as string;
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(cmd)) continue;
                        var e = new StartupEntry();
                        e.Source = src;
                        e.Name = name;
                        e.Command = cmd;
                        e.ExePath = ExeFromCommand(cmd);
                        e.Display = Pretty(name, e.ExePath);
                        e.Enabled = !IsDisabled(approved == null ? null : approved.GetValue(name) as byte[]);
                        e.NeedsAdmin = hive == Registry.LocalMachine;
                        e.FileMissing = e.ExePath != null && !File.Exists(e.ExePath);
                        list.Add(e);
                    }
                }
            }
            catch { }
        }

        static void ReadFolder(string folder, StartupSource src, RegistryKey hive, List<StartupEntry> list)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
                using (var approved = hive.OpenSubKey(ApprovedKey + "\\StartupFolder"))
                    foreach (string file in Directory.GetFiles(folder))
                    {
                        string fileName = Path.GetFileName(file);
                        if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        var e = new StartupEntry();
                        e.Source = src;
                        e.Name = fileName;
                        e.Command = file;
                        e.ExePath = fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) : file;
                        e.Display = Pretty(Path.GetFileNameWithoutExtension(fileName), e.ExePath);
                        e.Enabled = !IsDisabled(approved == null ? null : approved.GetValue(fileName) as byte[]);
                        e.NeedsAdmin = src == StartupSource.CommonFolder;
                        e.FileMissing = e.ExePath != null && !File.Exists(e.ExePath);
                        list.Add(e);
                    }
            }
            catch { }
        }

        static void ReadStoreTasks(List<StartupEntry> list)
        {
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(StoreTasksKey))
                {
                    if (root == null) return;
                    foreach (string family in root.GetSubKeyNames())
                        using (var fam = root.OpenSubKey(family))
                        {
                            if (fam == null) continue;
                            foreach (string task in fam.GetSubKeyNames())
                                using (var t = fam.OpenSubKey(task))
                                {
                                    object state = t == null ? null : t.GetValue("State");
                                    if (!(state is int)) continue;
                                    var e = new StartupEntry();
                                    e.Source = StartupSource.StoreApp;
                                    e.Name = family + "\\" + task;
                                    e.Command = family;
                                    e.Display = StoreDisplayName(family);
                                    e.Enabled = (int)state == 2 || (int)state == 4;
                                    if ((int)state == 3 || (int)state == 4) e.Locked = "Set by your organisation";
                                    list.Add(e);
                                }
                        }
                }
            }
            catch { }
        }

        static string StoreDisplayName(string family)
        {
            string core = family.Split('_')[0];              // 5319275A.WhatsAppDesktop
            string last = core.Substring(core.LastIndexOf('.') + 1);
            string nice;
            return StoreNames.TryGetValue(last, out nice) ? nice : SplitWords(last);
        }

        /// <summary>"IntelGraphicsExperience" → "Intel Graphics Experience".</summary>
        static string SplitWords(string s)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (i > 0 && char.IsUpper(c) && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(s[i - 1]))))
                    sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        static string Pretty(string name, string exe)
        {
            string n = name;
            if (n.StartsWith("MicrosoftEdgeAutoLaunch", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge (starts in background)";
            if (n.StartsWith("GoogleChromeAutoLaunch", StringComparison.OrdinalIgnoreCase)) return "Google Chrome (starts in background)";
            try
            {
                if (exe != null && File.Exists(exe))
                {
                    string desc = FileVersionInfo.GetVersionInfo(exe).FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 50 && !desc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        return desc.Trim();
                }
            }
            catch { }
            return n;
        }

        // ------------------------------------------------------------ helpers

        public static bool IsDisabled(byte[] approved)
        {
            return approved != null && approved.Length > 0 && (approved[0] & 1) == 1;
        }

        public static byte[] ApprovedBytes(bool enabled)
        {
            var b = new byte[12];
            b[0] = (byte)(enabled ? 2 : 3);
            if (!enabled) Array.Copy(BitConverter.GetBytes(DateTime.Now.ToFileTimeUtc()), 0, b, 4, 8);
            return b;
        }

        /// <summary>The program a startup command line runs ("C:\x\a.exe" --min → C:\x\a.exe).</summary>
        public static string ExeFromCommand(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            string c = Environment.ExpandEnvironmentVariables(cmd.Trim());
            if (c.StartsWith("\""))
            {
                int end = c.IndexOf('"', 1);
                return end > 1 ? c.Substring(1, end - 1) : c.Trim('"');
            }
            int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0) return c.Substring(0, exe + 4);
            int space = c.IndexOf(' ');
            return space > 0 ? c.Substring(0, space) : c;
        }

        static string ShortcutTarget(string lnk)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                string target = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
                return string.IsNullOrEmpty(target) ? null : target;
            }
            catch { return null; }
        }

        /// <summary>Things RamGuard won't switch off: Windows Security, driver helpers, anti-cheat.</summary>
        static string LockReason(StartupEntry e)
        {
            if (e.Locked != null) return e.Locked;
            string all = (e.Name + " " + (e.ExePath ?? "") + " " + e.Display).ToLowerInvariant();
            if (all.Contains("securityhealth")) return "Windows Security: keep it on";
            if (all.Contains("\\driverstore\\") || all.Contains("waves") || all.Contains("rtkaud") || all.Contains("igfx") ||
                all.Contains("nahimic") || all.Contains("synaptics") || all.Contains("syntp") || all.Contains("etdctrl") ||
                all.Contains("nvidia") || all.Contains("radeon"))
                return "Part of a driver (sound, graphics or touchpad): keep it on";
            if (all.Contains("vgtray") || all.Contains("riot vanguard") || all.Contains("easyanticheat") || all.Contains("battleye"))
                return "Anti-cheat your games need: keep it on";
            return null;
        }

        static void FillRunning(List<StartupEntry> list)
        {
            var bytesByExe = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<KeyValuePair<string, long>>();
            int session;
            using (var me = Process.GetCurrentProcess()) session = me.SessionId;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.SessionId != session) continue;
                    string path = Native.GetExePath(p.Id);
                    if (path == null) continue;
                    long b;
                    bytesByExe.TryGetValue(path, out b);
                    bytesByExe[path] = b + p.PrivateMemorySize64;
                    paths.Add(new KeyValuePair<string, long>(path, p.PrivateMemorySize64));
                }
                catch { }
                finally { p.Dispose(); }
            }
            foreach (var e in list)
            {
                if (e.Source == StartupSource.StoreApp)
                {
                    string core = e.Command.Split('_')[0];
                    foreach (var kv in paths)
                        if (kv.Key.IndexOf("\\WindowsApps\\" + core + "_", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            e.Running = true;
                            e.RunningBytes += kv.Value;
                        }
                }
                else if (e.ExePath != null)
                {
                    long b;
                    if (bytesByExe.TryGetValue(e.ExePath, out b)) { e.Running = true; e.RunningBytes = b; }
                }
            }
        }

        // ------------------------------------------------------------ changing

        /// <summary>Switches an entry on or off. Returns null on success, otherwise a message.</summary>
        public static string SetEnabled(StartupEntry e, bool on)
        {
            if (e.Locked != null && !on) return e.Display + ": " + e.Locked + ".";
            try
            {
                switch (e.Source)
                {
                    case StartupSource.UserRun:
                        WriteApproved(Registry.CurrentUser, "Run", e.Name, on);
                        return null;
                    case StartupSource.UserFolder:
                        WriteApproved(Registry.CurrentUser, "StartupFolder", e.Name, on);
                        return null;
                    case StartupSource.StoreApp:
                        using (var k = Registry.CurrentUser.OpenSubKey(StoreTasksKey + "\\" + e.Name, true))
                        {
                            if (k == null) return "Couldn't find " + e.Display + "'s startup setting.";
                            k.SetValue("State", on ? 2 : 1, RegistryValueKind.DWord);
                        }
                        return null;
                    default:
                        return RunElevated(e, on);
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Removes a broken entry (its program no longer exists). Only for your own entries.</summary>
        public static string RemoveBroken(StartupEntry e)
        {
            if (!e.FileMissing) return "It still works, so it was left alone.";
            try
            {
                if (e.Source == StartupSource.UserRun)
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue(e.Name, false);
                    using (var a = Registry.CurrentUser.OpenSubKey(ApprovedKey + "\\Run", true)) if (a != null) a.DeleteValue(e.Name, false);
                    return null;
                }
                if (e.Source == StartupSource.UserFolder)
                {
                    File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), e.Name));
                    return null;
                }
                return "Entries for all users can only be removed with admin rights; switch it off instead.";
            }
            catch (Exception ex) { return ex.Message; }
        }

        static void WriteApproved(RegistryKey hive, string sub, string name, bool on)
        {
            using (var k = hive.CreateSubKey(ApprovedKey + "\\" + sub))
                k.SetValue(name, ApprovedBytes(on), RegistryValueKind.Binary);
        }

        /// <summary>All-users entries: RamGuard runs itself with admin rights for this one change (Windows asks).</summary>
        static string RunElevated(StartupEntry e, bool on)
        {
            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath,
                    string.Format("--startup-set {0} \"{1}\" {2}", e.Source, e.Name.Replace("\"", ""), on ? 1 : 0));
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(60000)) return "Windows didn't finish the change in time.";
                    return p.ExitCode == 0 ? null : "The change didn't go through (code " + p.ExitCode + ").";
                }
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) return "Cancelled: Windows needs your OK (admin) to change apps that start for all users.";
                return ex.Message;
            }
        }

        /// <summary>Runs with admin rights: RamGuard.exe --startup-set MachineRun "Name" 0|1</summary>
        public static int ElevatedMain(string[] args)
        {
            try
            {
                if (args.Length < 4) return 2;
                StartupSource src;
                if (!Enum.TryParse(args[1], out src)) return 2;
                bool on = args[3] == "1";
                string sub;
                if (src == StartupSource.MachineRun) sub = "Run";
                else if (src == StartupSource.MachineRun32) sub = "Run32";
                else if (src == StartupSource.CommonFolder) sub = "StartupFolder";
                else return 2; // only all-users entries are changed this way
                WriteApproved(Registry.LocalMachine, sub, args[2], on);
                return 0;
            }
            catch { return 1; }
        }
    }
}
