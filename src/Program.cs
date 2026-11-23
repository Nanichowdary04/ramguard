using System;
using System.Threading;
using System.Windows.Forms;

namespace RamGuard
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // RamGuard.exe --selftest results.txt     runs the automated checks (no UI)
            if (args.Length >= 2 && args[0] == "--selftest")
            {
                ActivityLog.Enabled = false;
                Stats.Persist = false;
                return SelfTest.Run(args[1]);
            }

            // RamGuard.exe --startup-set MachineRun "Name" 0|1   (run with admin rights by the Startup apps window)
            if (args.Length >= 4 && args[0] == "--startup-set")
                return StartupManager.ElevatedMain(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // RamGuard.exe --snapshot main.png [dialog.png] [startup.png]   renders the UI to images and exits
            if (args.Length >= 2 && args[0] == "--snapshot")
            {
                ActivityLog.Enabled = false;
                Stats.Persist = false;
                string dialog = args.Length >= 3 && args[2] != "-" ? args[2] : null;
                string startup = args.Length >= 4 ? args[3] : null;
                Application.Run(new MainForm(Settings.Load(), false, args[1], dialog, startup));
                return 0;
            }

            bool minimized = Array.IndexOf(args, "--minimized") >= 0;
            bool firstInstance;
            using (var mutex = new Mutex(true, @"Local\RamGuard.SingleInstance", out firstInstance))
            using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\RamGuard.Show"))
            {
                if (!firstInstance)
                {
                    // Already running. Opened by you (shortcut, Start menu): bring its window to the front.
                    // Started in the background (sign-in): stay quiet.
                    if (!minimized) showSignal.Set();
                    return 0;
                }

                var form = new MainForm(Settings.Load(), minimized, null, null, null);
                var listener = new Thread(delegate()
                {
                    while (showSignal.WaitOne())
                    {
                        try { form.BeginInvoke((MethodInvoker)form.ShowMain); }
                        catch { return; }
                    }
                });
                listener.IsBackground = true;
                listener.Start();
                Application.Run(form);
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }
}
