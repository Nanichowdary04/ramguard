using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

// A stand-in app for RamGuard's self-test: holds some memory, optionally shows a window
// or listens on a port, and otherwise just sits there.
//   rg-dummy.exe --mb 200 [--window] [--listen 47811]
internal static class Dummy
{
    sealed class QuietForm : Form
    {
        // Don't steal focus from whatever the user is doing.
        protected override bool ShowWithoutActivation { get { return true; } }
    }

    [STAThread]
    static int Main(string[] args)
    {
        int mb = 100, port = 0;
        bool window = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--mb" && i + 1 < args.Length) mb = int.Parse(args[++i]);
            else if (args[i] == "--listen" && i + 1 < args.Length) port = int.Parse(args[++i]);
            else if (args[i] == "--window") window = true;
            else if (args[i] == "--spin")
            {
                // Simulate an app working in the background (music, download, build).
                var spinner = new Thread(delegate() { long x = 0; while (true) { x++; } });
                spinner.IsBackground = true;
                spinner.Start();
            }
        }

        var blocks = new List<byte[]>();
        for (int i = 0; i < mb; i++)
        {
            var b = new byte[1024 * 1024];
            for (int j = 0; j < b.Length; j += 4096) b[j] = 1; // touch every page so it's really in RAM
            blocks.Add(b);
        }

        if (port > 0)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            var clients = new List<TcpClient>();
            var t = new Thread(delegate()
            {
                while (true)
                {
                    try { clients.Add(listener.AcceptTcpClient()); }
                    catch { return; }
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        if (window)
        {
            var f = new QuietForm();
            f.Text = "RamGuard test window";
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-3000, -3000); // off-screen
            f.Size = new Size(300, 150);
            Application.Run(f);
            GC.KeepAlive(blocks);
            return 0;
        }

        Thread.Sleep(TimeSpan.FromMinutes(10));
        GC.KeepAlive(blocks);
        return 0;
    }
}
