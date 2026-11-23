using System;
using System.IO;
using System.Linq;

namespace RamGuard
{
    /// <summary>
    /// A plain-text history of what RamGuard did (%APPDATA%\RamGuard\activity.log), so you can see
    /// when Auto mode freed memory and how much. Keeps the most recent 1000 lines.
    /// </summary>
    internal static class ActivityLog
    {
        public static readonly string FilePath = Path.Combine(Settings.Folder, "activity.log");
        public static bool Enabled = true;
        static int writes;

        public static void Add(string message)
        {
            if (!Enabled) return;
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                File.AppendAllText(FilePath, string.Format("{0:yyyy-MM-dd HH:mm:ss}  {1}{2}", DateTime.Now, message, Environment.NewLine));
                if (++writes % 50 == 0) Shorten();
            }
            catch { }
        }

        static void Shorten()
        {
            var lines = File.ReadAllLines(FilePath);
            if (lines.Length > 1500) File.WriteAllLines(FilePath, lines.Skip(lines.Length - 1000));
        }
    }
}
