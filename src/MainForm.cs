using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RamGuard
{
    internal sealed class MainForm : Form
    {
        // Shown names for Rule.Ask / Rule.Keep / Rule.AutoClose (the settings file keeps the enum names).
        static readonly string[] RuleNames = { "Free memory", "Keep open", "Auto-close" };
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "RamGuard";

        readonly Settings settings;
        readonly Scanner scanner;
        readonly Theme theme;
        readonly float dpi;
        readonly string snapshotPath, snapshotDialogPath, snapshotStartupPath;

        bool startHidden, exiting, busy, suppressEvents, trayHintShown;
        DateTime lastAutoTrim = DateTime.MinValue;
        readonly Dictionary<int, DateTime> lastTrimmed = new Dictionary<int, DateTime>();
        string lastFreed;   // shown in the tray tooltip, e.g. "freed 1.2 GB 20:31"
        int iconPct = -1;
        Snapshot last = new Snapshot();

        Label lblRam, lblDetail, lblHint, lblStatus;
        RamGraph graph;
        Label lblToday;
        Button btnStartup;
        Stats stats;
        readonly List<GraphPoint> history = new List<GraphPoint>();
        readonly List<GraphEvent> freeEvents = new List<GraphEvent>();
        readonly List<GameSpan> gameSpans = new List<GameSpan>();
        readonly HashSet<string> growthReported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int fgTicks;
        Button btnClean, btnTrim;
        NumericUpDown numIdle;
        CheckBox chkAuto, chkShowAll, chkGame;
        GameMode game;
        DateTime lastGameCheck = DateTime.MinValue;
        ToolStripMenuItem trayGame, miIsGame;
        DataGridView grid;
        DataGridViewTextBoxColumn colApp, colStatus, colMem, colRam, colProcs, colLast;
        DataGridViewComboBoxColumn colRule;
        ContextMenuStrip rowMenu;
        ToolStripMenuItem miKeep, miAsk, miAuto;
        NotifyIcon tray;
        ToolStripMenuItem trayAuto;
        System.Windows.Forms.Timer fgTimer, uiTimer, autoTimer;
        ToolTip tips;

        public MainForm(Settings settings, bool startHidden, string snapshotPath, string snapshotDialogPath, string snapshotStartupPath)
        {
            this.settings = settings;
            this.startHidden = startHidden;
            this.snapshotPath = snapshotPath;
            this.snapshotDialogPath = snapshotDialogPath;
            this.snapshotStartupPath = snapshotStartupPath;
            stats = Stats.Load();
            var first = new GraphPoint();
            first.Time = DateTime.Now;
            first.Load = Native.GetMemory().LoadPercent;
            history.Add(first);
            theme = Theme.Detect();
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX;
            // A preview pretends RamGuard has been watching for 30 minutes, so idle apps show up.
            scanner = new Scanner(settings, snapshotPath != null ? DateTime.Now.AddMinutes(-30) : DateTime.Now);
            game = new GameMode(settings);
            if (snapshotPath == null)
            {
                // If RamGuard was stopped in the middle of a game, put the other apps back to normal first.
                int restored = GameMode.RestoreLeftovers();
                if (restored > 0) ActivityLog.Add("GAME   Restored " + restored + " apps to normal speed (RamGuard had stopped during a game).");
            }

            BuildUi();
            BuildTray();

            fgTimer = new System.Windows.Forms.Timer();
            fgTimer.Interval = 2000;
            fgTimer.Tick += delegate
            {
                scanner.NoteForeground();
                CheckGame();
                UpdateTrayIcon();
                if (++fgTicks % 5 == 0) SampleHistory(); // every 10 s
            };
            fgTimer.Start();

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 3000;
            uiTimer.Tick += delegate { if (Visible && WindowState != FormWindowState.Minimized && !busy) RefreshNow(); };
            uiTimer.Start();

            autoTimer = new System.Windows.Forms.Timer();
            autoTimer.Interval = 30000;
            autoTimer.Tick += delegate { AutoTick(); };
            autoTimer.Start();

            UpdateTrayIcon();
            if (snapshotPath == null)
                ActivityLog.Add(string.Format("RamGuard started. Auto mode {0}, frees memory at {1}% RAM or more. RAM now {2}%.",
                    settings.AutoMode ? "on" : "off", settings.TrimAbovePercent, Native.GetMemory().LoadPercent));
        }

        int S(int v) { return Ui.S(dpi, v); }

        /// <summary>Adds a point to the RAM graph and forgets anything older than an hour.</summary>
        void SampleHistory()
        {
            if (snapshotPath != null) return;
            DateTime now = DateTime.Now;
            var p = new GraphPoint();
            p.Time = now;
            p.Load = Native.GetMemory().LoadPercent;
            history.Add(p);
            DateTime cutoff = now - TimeSpan.FromMinutes(62);
            history.RemoveAll(x => x.Time < cutoff);
            freeEvents.RemoveAll(x => x.Time < cutoff);
            gameSpans.RemoveAll(x => x.End.HasValue && x.End.Value < cutoff);
            if (Visible && WindowState != FormWindowState.Minimized) graph.Invalidate();
        }

        /// <summary>Notes in the activity log, once per app, when an app starts growing steadily.</summary>
        void ReportGrowth()
        {
            foreach (var g in last.Groups)
            {
                if (!g.Growing || growthReported.Contains(g.Key)) continue;
                growthReported.Add(g.Key);
                ActivityLog.Add(string.Format("GROWING {0} grew by {1} over {2} ({3}/h) and is still growing. Restarting it gives the memory back.",
                    g.Display, Units.Size(g.GrowthInfo.GrowthBytes), Units.Span(g.GrowthInfo.Window), Units.Size((long)g.GrowthInfo.BytesPerHour)));
            }
        }

        /// <summary>The app icon embedded in the exe (all sizes, so the title bar and taskbar stay sharp).</summary>
        static Icon AppIcon()
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("RamGuard.ico"))
                    return s == null ? null : new Icon(s);
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ layout

        void BuildUi()
        {
            Text = "RamGuard";
            Font = new Font("Segoe UI", 9.75f);
            AutoScaleMode = AutoScaleMode.None;
            BackColor = theme.Back;
            ForeColor = theme.Text;
            Size = new Size(S(1060), S(700));
            MinimumSize = new Size(S(800), S(500));
            StartPosition = FormStartPosition.CenterScreen;
            Icon = AppIcon() ?? Draw.MakeIcon(32, "R", theme.Accent);
            if (snapshotPath != null)
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-20000, -20000);
                ShowInTaskbar = false;
            }
            tips = new ToolTip();

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.BackColor = theme.Back;
            root.Padding = new Padding(S(20), S(16), S(20), S(8));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            Controls.Add(root);

            // Header: RAM in use + today's savings, the last hour as a graph, and what's going on.
            var header = new TableLayoutPanel();
            header.Dock = DockStyle.Fill;
            header.AutoSize = true;
            header.ColumnCount = 1;
            header.Margin = new Padding(0);
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            var titleRow = new TableLayoutPanel();
            titleRow.Dock = DockStyle.Fill;
            titleRow.AutoSize = true;
            titleRow.ColumnCount = 2;
            titleRow.Margin = new Padding(0);
            titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lblRam = Ui.MakeLabel("", new Font("Segoe UI Semibold", 16f), theme.Text);
            lblToday = Ui.MakeLabel("", Font, theme.Good);
            lblToday.Anchor = AnchorStyles.Right;
            titleRow.Controls.Add(lblRam, 0, 0);
            titleRow.Controls.Add(lblToday, 1, 0);

            graph = new RamGraph();
            graph.Theme = theme;
            graph.UiScale = dpi / 96f;
            graph.BackColor = theme.Back;
            graph.Height = S(96);
            graph.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            graph.Margin = new Padding(0, S(8), 0, S(8));
            graph.Points = history;
            graph.Frees = freeEvents;
            graph.Games = gameSpans;
            tips.SetToolTip(graph, "RAM over the last hour. Green dots: RamGuard freed memory. Green band: Game mode was on. Dashed line: the Auto mode limit.");

            lblDetail = Ui.MakeLabel("", Font, theme.Muted);
            header.Controls.Add(titleRow, 0, 0);
            header.Controls.Add(graph, 0, 1);
            header.Controls.Add(lblDetail, 0, 2);
            root.Controls.Add(header, 0, 0);

            // Toolbar.
            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.AutoSize = true;
            bar.WrapContents = true;
            bar.BackColor = theme.Back;
            bar.Margin = new Padding(0, S(10), 0, 0);

            btnClean = Ui.MakeButton("Close idle apps…", true, theme, dpi);
            btnClean.Click += delegate { CleanClicked(); };
            tips.SetToolTip(btnClean, "Shows the idle apps first, so you can untick anything you want to keep.");

            btnTrim = Ui.MakeButton("Free memory now", false, theme, dpi);
            btnTrim.Click += delegate { FreeMemory(false, true); };
            tips.SetToolTip(btnTrim, "Frees the memory of apps in the background without closing them. " +
                "They keep their windows and data, and get the memory back as soon as you use them.");

            var lblIdle = Ui.InlineLabel("Idle after", Font, theme.Text, dpi);
            lblIdle.Margin = new Padding(S(16), S(4), S(4), S(4));
            numIdle = new NumericUpDown();
            numIdle.Minimum = 1;
            numIdle.Maximum = 240;
            numIdle.Value = Math.Max(1, Math.Min(240, settings.IdleMinutes));
            numIdle.Width = S(60);
            numIdle.BackColor = theme.Surface;
            numIdle.ForeColor = theme.Text;
            numIdle.BorderStyle = BorderStyle.FixedSingle;
            numIdle.Margin = new Padding(0, S(4) + Math.Max(0, (S(34) - numIdle.PreferredHeight) / 2), S(2), S(4));
            numIdle.ValueChanged += delegate
            {
                settings.IdleMinutes = (int)numIdle.Value;
                settings.Save();
                RefreshNow();
            };
            var lblMin = Ui.InlineLabel("min", Font, theme.Text, dpi);

            chkAuto = Ui.MakeCheck("Auto mode", settings.AutoMode, Font, theme, dpi);
            chkAuto.CheckedChanged += delegate { if (!suppressEvents) SetAuto(chkAuto.Checked); };
            tips.SetToolTip(chkAuto, "Checks every 30 seconds. Whenever RAM is at " + settings.TrimAbovePercent + "% or more, frees as much " +
                "memory as it can: every background app, plus the quiet tabs and helpers of the apps you're using. " +
                "Apps stay open and nothing is lost. Only apps you set to \"Auto-close\" are ever closed.");

            chkShowAll = Ui.MakeCheck("Show all", settings.ShowAll, Font, theme, dpi);
            tips.SetToolTip(chkShowAll, "Also list Windows parts, terminals and apps under 30 MB.");
            chkShowAll.CheckedChanged += delegate
            {
                settings.ShowAll = chkShowAll.Checked;
                settings.Save();
                RefreshNow();
            };

            chkGame = Ui.MakeCheck("Game mode", settings.GameMode, Font, theme, dpi);
            chkGame.CheckedChanged += delegate { if (!suppressEvents) SetGameMode(chkGame.Checked); };
            tips.SetToolTip(chkGame, "When a game is in front of you: other apps are slowed down (lower priority + efficiency mode, " +
                "so the game gets the fast CPU cores), their memory is freed, and Windows switches to Best performance. " +
                "Nothing is closed and the game itself is never touched. Everything goes back to normal when the game closes.");

            btnStartup = Ui.MakeButton("Startup apps…", false, theme, dpi);
            btnStartup.Click += delegate { ShowStartupApps(); };
            tips.SetToolTip(btnStartup, "See which apps start with Windows and switch off the ones you don't need (RamGuard itself is in the list too).");

            bar.Controls.AddRange(new Control[] { btnClean, btnTrim, btnStartup, lblIdle, numIdle, lblMin, chkAuto, chkGame, chkShowAll });
            root.Controls.Add(bar, 0, 1);

            lblHint = Ui.MakeLabel("", new Font("Segoe UI", 9f), theme.Muted);
            lblHint.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            lblHint.Margin = new Padding(0, S(6), 0, S(10));
            root.Controls.Add(lblHint, 0, 2);

            BuildGrid();
            root.Controls.Add(grid, 0, 3);

            lblStatus = Ui.MakeLabel("Watching. Apps count as idle after you haven't used them for a while.", Font, theme.Muted);
            lblStatus.AutoSize = false;
            lblStatus.AutoEllipsis = true;
            lblStatus.Dock = DockStyle.Fill;
            lblStatus.Height = S(28);
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            root.Controls.Add(lblStatus, 0, 4);
        }

        void BuildGrid()
        {
            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.Margin = new Padding(0);
            grid.BackgroundColor = theme.Surface;
            grid.BorderStyle = BorderStyle.None;
            grid.GridColor = theme.Line;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = S(38);
            var hs = grid.ColumnHeadersDefaultCellStyle;
            hs.BackColor = theme.Surface;
            hs.ForeColor = theme.Muted;
            hs.SelectionBackColor = theme.Surface;
            hs.SelectionForeColor = theme.Muted;
            hs.Font = new Font("Segoe UI Semibold", 9f);
            hs.Padding = new Padding(S(8), 0, S(8), 0);
            var cs = grid.DefaultCellStyle;
            cs.BackColor = theme.Surface;
            cs.ForeColor = theme.Text;
            cs.SelectionBackColor = theme.Selection;
            cs.SelectionForeColor = theme.Text;
            cs.Padding = new Padding(S(8), 0, S(8), 0);
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.RowTemplate.Height = S(34);
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.ShowCellToolTips = true;
            typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(grid, true, null);

            colApp = TextCol("App", 30, false, null);
            colStatus = TextCol("Status", 26, false, null);
            colMem = TextCol("Memory", 11, true, "Memory the app is holding. This is what you get back when it closes.");
            colRam = TextCol("In RAM", 10, true, "The part of that memory sitting in physical RAM right now.");
            colProcs = TextCol("Processes", 9, true, null);
            colLast = TextCol("Last used", 13, false, "When you last had this app in front of you.");
            colMem.DefaultCellStyle.Format = "#,0' MB'";
            colRam.DefaultCellStyle.Format = "#,0' MB'";

            colRule = new DataGridViewComboBoxColumn();
            colRule.HeaderText = "Rule";
            colRule.Items.AddRange(RuleNames);
            colRule.FlatStyle = FlatStyle.Flat;
            colRule.FillWeight = 12;
            colRule.SortMode = DataGridViewColumnSortMode.Automatic;
            colRule.DisplayStyleForCurrentCellOnly = true;
            colRule.ToolTipText = "Free memory: stays open; its memory is freed when idle.  Keep open: never closed.  " +
                "Auto-close: closed automatically when idle (Auto mode).";

            grid.Columns.AddRange(new DataGridViewColumn[] { colApp, colStatus, colMem, colRam, colProcs, colLast, colRule });

            grid.CellClick += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex != colRule.Index) return;
                if (!(grid.Rows[e.RowIndex].Cells[e.ColumnIndex] is DataGridViewComboBoxCell)) return;
                grid.BeginEdit(true);
                var cb = grid.EditingControl as ComboBox;
                if (cb != null) cb.DroppedDown = true;
            };
            grid.CurrentCellDirtyStateChanged += delegate
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewComboBoxCell)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.CellValueChanged += OnCellValueChanged;
            grid.DataError += (s, e) => { e.ThrowException = false; };
            // Scrolling the mouse wheel over an open Rule dropdown must not change the rule by accident.
            grid.EditingControlShowing += (s, e) =>
            {
                var cb = e.Control as ComboBox;
                if (cb == null) return;
                cb.MouseWheel -= SwallowWheel;
                cb.MouseWheel += SwallowWheel;
            };
            grid.CellMouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
                grid.ClearSelection();
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells[colApp.Index];
                grid.Rows[e.RowIndex].Selected = true;
            };

            rowMenu = new ContextMenuStrip();
            var miClose = new ToolStripMenuItem("Close this app now…");
            miClose.Click += delegate { CloseSelected(); };
            miKeep = new ToolStripMenuItem("Keep open (never close)");
            miKeep.Click += delegate { SetRuleForSelected(Rule.Keep); };
            miAsk = new ToolStripMenuItem("Free memory when idle (stays open)");
            miAsk.Click += delegate { SetRuleForSelected(Rule.Ask); };
            miAuto = new ToolStripMenuItem("Auto-close when idle");
            miAuto.Click += delegate { SetRuleForSelected(Rule.AutoClose); };
            miIsGame = new ToolStripMenuItem("This is a game (use Game mode)");
            miIsGame.Click += delegate { ToggleIsGame(); };
            rowMenu.Items.AddRange(new ToolStripItem[] { miClose, new ToolStripSeparator(), miKeep, miAsk, miAuto, new ToolStripSeparator(), miIsGame });
            rowMenu.Opening += (s, e) =>
            {
                var g = SelectedGroup();
                if (g == null || g.Protected) { e.Cancel = true; return; }
                miKeep.Checked = g.Rule == Rule.Keep;
                miAsk.Checked = g.Rule == Rule.Ask;
                miAuto.Checked = g.Rule == Rule.AutoClose;
                string name = g.Procs[0].Name;
                bool? byName = GameMode.DecideByName(name, settings, null);
                miIsGame.Checked = byName.HasValue && byName.Value;
            };
            grid.ContextMenuStrip = rowMenu;
        }

        static void SwallowWheel(object sender, MouseEventArgs e)
        {
            var h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;
        }

        DataGridViewTextBoxColumn TextCol(string header, float weight, bool numeric, string tip)
        {
            var c = new DataGridViewTextBoxColumn();
            c.HeaderText = header;
            c.FillWeight = weight;
            c.ReadOnly = true;
            c.SortMode = DataGridViewColumnSortMode.Automatic;
            if (tip != null) c.ToolTipText = tip;
            if (numeric)
            {
                c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
            return c;
        }

        void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Text = "RamGuard";
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open RamGuard", null, delegate { ShowMain(); });
            menu.Items.Add("Close idle apps…", null, delegate { ShowMain(); CleanClicked(); });
            menu.Items.Add("Free memory now", null, delegate { FreeMemory(false, true); });
            menu.Items.Add("Startup apps…", null, delegate { ShowStartupApps(); });
            menu.Items.Add("Show activity log", null, delegate
            {
                try
                {
                    if (!System.IO.File.Exists(ActivityLog.FilePath)) ActivityLog.Add("Activity log started.");
                    Process.Start("notepad.exe", "\"" + ActivityLog.FilePath + "\"");
                }
                catch { }
            });
            menu.Items.Add(new ToolStripSeparator());
            trayAuto = new ToolStripMenuItem("Auto mode");
            trayAuto.Checked = settings.AutoMode;
            trayAuto.Click += delegate { SetAuto(!settings.AutoMode); };
            menu.Items.Add(trayAuto);
            trayGame = new ToolStripMenuItem("Game mode");
            trayGame.Checked = settings.GameMode;
            trayGame.Click += delegate { SetGameMode(!settings.GameMode); };
            menu.Items.Add(trayGame);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit RamGuard", null, delegate { ExitApp(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
            tray.Visible = snapshotPath == null;
        }

        // ------------------------------------------------------------------ refresh

        void RefreshNow()
        {
            last = scanner.Scan();
            if (snapshotPath == null) ReportGrowth();
            UpdateHeader();
            if (!grid.IsCurrentCellInEditMode) UpdateGrid();
        }

        void UpdateHeader()
        {
            var m = last.Memory;
            lblRam.Text = string.Format("{0} of {1} RAM in use  ·  {2}%", Units.Size(m.UsedBytes), Units.Size(m.TotalBytes), m.LoadPercent);
            lblToday.Text = stats.Summary();
            graph.Threshold = settings.TrimAbovePercent;
            graph.Invalidate();

            var cands = last.Groups.Where(g => g.IsCandidate(settings)).ToList();
            var growing = last.Groups.Where(g => g.Growing).OrderByDescending(g => g.GrowthInfo.GrowthBytes).ToList();
            if (!game.Active && growing.Count > 0)
            {
                var top = growing[0];
                lblDetail.ForeColor = theme.Warn;
                lblDetail.Text = string.Format("{0} keeps growing: +{1} in {2} and still climbing{3}. Restarting it gives that memory back.",
                    top.Display, Units.Size(top.GrowthInfo.GrowthBytes), Units.Span(top.GrowthInfo.Window),
                    growing.Count > 1 ? string.Format(" (and {0} more)", growing.Count - 1) : "");
            }
            else if (game.Active)
            {
                lblDetail.ForeColor = theme.Good;
                lblDetail.Text = string.Format(
                    "Game mode on: {0}. {1} background processes slowed down, {2} freed{3}. Nothing was closed; it all goes back to normal when the game closes.",
                    game.GameDisplay, game.CalmedCount, Units.Size(game.FreedBytes), game.PowerBoosted ? ", Best performance power mode" : "");
            }
            else
            {
                lblDetail.ForeColor = theme.Muted;
                lblDetail.Text = cands.Count == 0
                    ? "Nothing idle to close right now."
                    : string.Format("{0} idle app{1} could be closed to free about {2}.",
                        cands.Count, cands.Count == 1 ? "" : "s", Units.Size(cands.Sum(g => g.PrivateBytes)));
            }

            lblHint.Text = string.Format(
                "Auto mode checks every 30 seconds and, whenever RAM is at {1}% or more, frees as much memory as it can without closing " +
                "anything: apps keep their windows, tabs and data. An app is idle after {0} min unused. " +
                "Only apps set to \"Auto-close\" are ever closed. Windows parts and terminals are never touched.",
                settings.IdleMinutes, settings.TrimAbovePercent);
        }

        void UpdateGrid()
        {
            suppressEvents = true;
            try
            {
                var visible = last.Groups
                    .Where(g => settings.ShowAll || (!g.Protected && g.PrivateBytes >= 30 * Units.MB))
                    .ToList();

                var rows = new Dictionary<string, DataGridViewRow>(StringComparer.OrdinalIgnoreCase);
                foreach (DataGridViewRow r in grid.Rows) rows[(string)r.Tag] = r;

                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in visible)
                {
                    keep.Add(g.Key);
                    DataGridViewRow row;
                    if (!rows.TryGetValue(g.Key, out row))
                    {
                        row = grid.Rows[grid.Rows.Add()];
                        row.Tag = g.Key;
                    }
                    FillRow(row, g);
                }
                for (int i = grid.Rows.Count - 1; i >= 0; i--)
                    if (!keep.Contains((string)grid.Rows[i].Tag)) grid.Rows.RemoveAt(i);

                DataGridViewColumn sortCol = grid.SortedColumn ?? colMem;
                var dir = grid.SortedColumn != null && grid.SortOrder == SortOrder.Ascending
                    ? ListSortDirection.Ascending : ListSortDirection.Descending;
                grid.Sort(sortCol, dir);
            }
            finally { suppressEvents = false; }
        }

        void FillRow(DataGridViewRow row, AppGroup g)
        {
            Set(row.Cells[colApp.Index], g.Display);
            Set(row.Cells[colStatus.Index], StatusText(g));
            Set(row.Cells[colMem.Index], g.PrivateBytes / Units.MB);
            Set(row.Cells[colRam.Index], g.WorkingSet / Units.MB);
            Set(row.Cells[colProcs.Index], g.Procs.Count);
            Set(row.Cells[colLast.Index], g.IsForeground ? "Now" : g.EverUsed ? Units.Ago(g.LastUsed) : "Not yet");

            int ri = colRule.Index;
            if (g.Protected)
            {
                if (!(row.Cells[ri] is DataGridViewTextBoxCell)) row.Cells[ri] = new DataGridViewTextBoxCell();
                Set(row.Cells[ri], "Protected");
                row.Cells[ri].ReadOnly = true;
                row.Cells[ri].ToolTipText = "Never touched: " + g.ProtectReason;
            }
            else
            {
                if (!(row.Cells[ri] is DataGridViewComboBoxCell))
                {
                    var c = new DataGridViewComboBoxCell();
                    c.Items.AddRange(RuleNames);
                    c.FlatStyle = FlatStyle.Flat;
                    row.Cells[ri] = c;
                }
                Set(row.Cells[ri], RuleName(g.Rule));
                row.Cells[ri].ToolTipText = g.IsTree
                    ? "Applies to every " + Scanner.FriendlyName(g.RootName) + " app you start from a terminal."
                    : "";
            }

            Color statusColor = g.Protected ? theme.Muted
                : g.Growing ? theme.Bad
                : g.InUse ? theme.Good
                : g.Rule == Rule.Keep ? theme.Muted
                : theme.Warn;
            var sc = row.Cells[colStatus.Index].Style;
            if (sc.ForeColor != statusColor) { sc.ForeColor = statusColor; sc.SelectionForeColor = statusColor; }
            Color text = g.Protected ? theme.Muted : theme.Text;
            if (row.DefaultCellStyle.ForeColor != text) row.DefaultCellStyle.ForeColor = text;
            row.Cells[colApp.Index].ToolTipText = string.Join(", ", g.Procs.Take(12).Select(p => p.Name + " (" + p.Pid + ")"));
        }

        static void Set(DataGridViewCell c, object v)
        {
            if (!Equals(c.Value, v)) c.Value = v;
        }

        string StatusText(AppGroup g)
        {
            if (g.Protected) return "Protected · " + g.ProtectReason;
            if (g.Growing)
                return string.Format("Growing +{0}/h · restart it to free memory", Units.Size((long)g.GrowthInfo.BytesPerHour));
            if (g.IsForeground || g.Serving || g.Busy || (g.InUse && g.Key == Scanner.WslKey && g.InUseReason.StartsWith("a WSL")))
                return "In use · " + g.InUseReason;
            if (g.InUse) return (g.EverUsed ? "Recently used" : "Watching") + " · " + g.InUseReason;
            string idle = "Idle " + Units.Span(g.IdleFor);
            if (g.Rule == Rule.Keep) return idle + " · kept";
            if (g.PrivateBytes < settings.MinMb * Units.MB) return idle + " · small";
            return idle;
        }

        static string RuleName(Rule r) { return r == Rule.Keep ? RuleNames[1] : r == Rule.AutoClose ? RuleNames[2] : RuleNames[0]; }

        static Rule ParseRule(string s) { return s == RuleNames[1] ? Rule.Keep : s == RuleNames[2] ? Rule.AutoClose : Rule.Ask; }

        AppGroup SelectedGroup()
        {
            if (grid.CurrentRow == null) return null;
            return last.Find(grid.CurrentRow.Tag as string);
        }

        // ------------------------------------------------------------------ rules

        void OnCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (suppressEvents || e.RowIndex < 0 || e.ColumnIndex != colRule.Index) return;
            var row = grid.Rows[e.RowIndex];
            var g = last.Find(row.Tag as string);
            if (g == null || g.Protected) return;
            Rule chosen = ParseRule(row.Cells[e.ColumnIndex].Value as string);
            // Finish the edit first, then (maybe) ask, so the dropdown isn't left open behind a message box.
            BeginInvoke((MethodInvoker)delegate
            {
                grid.EndEdit();
                SetRule(g, chosen);
                RefreshNow();
            });
        }

        void SetRuleForSelected(Rule r)
        {
            var g = SelectedGroup();
            if (g == null || g.Protected) return;
            SetRule(g, r);
            RefreshNow();
        }

        /// <summary>The only rule that can lose data is Auto-close, so it always asks first.</summary>
        bool ConfirmAutoClose(AppGroup g)
        {
            string msg = string.Format(
                "Auto-close will CLOSE {0} by itself after {1} minutes without use.\n\n" +
                "Anything unsaved in it could be lost. The safe choice is \"Free memory\": it stays open and only its memory is freed.\n\n" +
                "Set {0} to Auto-close?", g.Display, settings.IdleMinutes);
            return MessageBox.Show(this, msg, "RamGuard", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        void SetRule(AppGroup g, Rule r)
        {
            if (r == Rule.AutoClose && settings.RuleFor(g.RuleKey) != Rule.AutoClose && !ConfirmAutoClose(g))
            {
                SetStatus(g.Display + " was left as it was.");
                return;
            }
            settings.Rules[g.RuleKey] = r;
            settings.Save();
            string who = g.IsTree ? Scanner.FriendlyName(g.RootName) + " apps started from a terminal" : g.Display;
            string what = r == Rule.Keep ? "will never be closed"
                : r == Rule.AutoClose ? (settings.AutoMode ? "will be closed automatically when idle"
                                                           : "will be closed automatically when idle once Auto mode is on")
                : "stays open; its memory is freed when it's idle";
            SetStatus(who + " " + what + ".");
        }

        // ------------------------------------------------------------------ actions

        void CleanClicked()
        {
            if (busy) return;
            RefreshNow();
            var cands = last.Groups.Where(g => g.IsCandidate(settings)).OrderByDescending(g => g.PrivateBytes).ToList();
            if (cands.Count == 0)
            {
                SetStatus("Nothing to close: everything using memory is in use, protected, or set to Keep.");
                return;
            }
            using (var dlg = new CleanDialog(cands, theme, dpi))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Selected.Count == 0) return;
                if (dlg.AlwaysAuto)
                {
                    foreach (var g in dlg.Selected) settings.Rules[g.RuleKey] = Rule.AutoClose;
                    settings.Save();
                    SetAuto(true);
                }
                RunClose(dlg.Selected.ToList(), false);
            }
        }

        void CloseSelected()
        {
            var g = SelectedGroup();
            if (g == null || g.Protected || busy) return;
            string msg = string.Format("Close {0}? It's holding {1}.\n\n{2}", g.Display, Units.Size(g.PrivateBytes), Cleaner.Describe(g));
            if (MessageBox.Show(this, msg, "RamGuard", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                RunClose(new List<AppGroup> { g }, false);
        }

        void RunClose(List<AppGroup> groups, bool fromAuto)
        {
            SetBusy(true);
            SetStatus(string.Format("Closing {0} app{1}…", groups.Count, groups.Count == 1 ? "" : "s"));
            ThreadPool.QueueUserWorkItem(delegate
            {
                var results = new List<Cleaner.Result>();
                foreach (var g in groups) results.Add(Cleaner.Close(g));
                BeginInvoke((MethodInvoker)delegate
                {
                    SetBusy(false);
                    ReportClose(results, fromAuto);
                    RefreshNow();
                });
            });
        }

        void ReportClose(List<Cleaner.Result> results, bool fromAuto)
        {
            var closed = results.Where(r => r.Closed).ToList();
            var parts = new List<string>();
            if (closed.Count > 0)
                parts.Add(string.Format("Closed {0}. About {1} freed.",
                    string.Join(", ", closed.Select(r => r.Group.Display)), Units.Size(closed.Sum(r => r.FreedBytes))));
            foreach (var r in results.Where(x => !x.Closed))
                parts.Add(r.Group.Display + " " + r.Message + ".");
            string msg = string.Join(" ", parts);
            SetStatus(msg);
            ActivityLog.Add((fromAuto ? "AUTO   " : "MANUAL ") + "close: " + msg);
            if (fromAuto && closed.Count > 0 && !game.Active)
                tray.ShowBalloonTip(5000, "RamGuard closed idle apps", msg.Length > 240 ? msg.Substring(0, 240) + "…" : msg, ToolTipIcon.Info);
        }

        /// <summary>
        /// Frees apps' memory WITHOUT closing them: every app keeps its window, state and data.
        /// includeRecent=false: only idle apps.  true: every app in the background (not the one in
        /// front of you, and never one that's working, e.g. playing music or building).
        /// </summary>
        /// <returns>true if it started freeing something.</returns>
        bool FreeMemory(bool fromAuto, bool includeRecent)
        {
            return FreeMemory(fromAuto, includeRecent, false);
        }

        bool FreeMemory(bool fromAuto, bool includeRecent, bool skipCooldown)
        {
            if (busy) return false;
            last = scanner.Scan();
            DateTime now = DateTime.Now;
            // While gaming: never touch the game itself, voice chat, music or overlays.
            var plan = Cleaner.TrimPlan(last, includeRecent, game.Active ? game.TrimExclusions(last) : null);
            if (fromAuto && !skipCooldown)
            {
                // Don't keep squeezing the same process: once every 2 minutes at most.
                plan = plan.Where(p =>
                {
                    DateTime t;
                    return !lastTrimmed.TryGetValue(p.Pid, out t) || now - t >= TimeSpan.FromMinutes(2);
                }).ToList();
            }
            foreach (int pid in lastTrimmed.Keys.Where(k => now - lastTrimmed[k] > TimeSpan.FromHours(1)).ToList())
                lastTrimmed.Remove(pid);
            if (plan.Count == 0)
            {
                if (!fromAuto) SetStatus("Nothing to free right now: everything using memory is in front of you, working, or protected.");
                return false;
            }
            foreach (var p in plan) lastTrimmed[p.Pid] = now;
            var pids = new HashSet<int>(plan.Select(p => p.Pid));
            int apps = last.Groups.Count(g => g.Procs.Any(p => pids.Contains(p.Pid)));

            SetBusy(true, !fromAuto);
            if (!fromAuto) SetStatus("Freeing memory…");
            var memBefore = Native.GetMemory();
            int selfPid;
            using (var me = Process.GetCurrentProcess()) selfPid = me.Id;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Cleaner.Trim(plan);
                Native.TrimWorkingSet(selfPid);
                Thread.Sleep(3000); // Windows needs a moment to hand the pages back
                var memAfter = Native.GetMemory();
                long freed = Math.Max(0, memAfter.AvailableBytes - memBefore.AvailableBytes);
                BeginInvoke((MethodInvoker)delegate
                {
                    SetBusy(false, !fromAuto);
                    string summary = string.Format("{0} freed from {1} background process{2} in {3} app{4}",
                        Units.Size(freed), plan.Count, plan.Count == 1 ? "" : "es", apps, apps == 1 ? "" : "s");
                    SetStatus(string.Format("{0}{1}. Nothing was closed: every app keeps its windows, tabs and data.",
                        fromAuto ? string.Format("{0:HH:mm} Auto mode: ", DateTime.Now) : "", summary));
                    ActivityLog.Add(string.Format("{0}  RAM {1}% -> {2}%  {3}  (nothing closed)",
                        game.Active ? "GAME  " : fromAuto ? "AUTO  " : "MANUAL", memBefore.LoadPercent, memAfter.LoadPercent, summary));
                    if (game.Active) game.FreedBytes += freed;
                    stats.AddFree(freed);
                    var ev = new GraphEvent();
                    ev.Time = DateTime.Now;
                    ev.Bytes = freed;
                    freeEvents.Add(ev);
                    lblToday.Text = stats.Summary();
                    graph.Invalidate();
                    lastFreed = string.Format("freed {0} {1:HH:mm}", Units.Size(freed), DateTime.Now);
                    iconPct = -1;
                    UpdateTrayIcon();
                    if (Visible) RefreshNow();
                });
            });
            return true;
        }

        void AutoTick()
        {
            if (game.Active)
            {
                GameTick();
                return;
            }
            if (busy) return;
            last = scanner.Scan();
            ReportGrowth();
            if (!settings.AutoMode) return;

            // Only apps the user explicitly set to "Auto-close" are ever closed automatically.
            var toClose = last.Groups.Where(g => g.Rule == Rule.AutoClose && g.IsCandidate(settings)).ToList();
            if (toClose.Count > 0)
            {
                RunClose(toClose, true);
                return;
            }

            // Everything else: when RAM is at or above the limit (80%), free as much as possible while
            // keeping every app open: all background apps that aren't working, plus the quiet tabs and
            // helpers of the apps you're using. Checked every 30 s, acted on at most once a minute.
            int load = last.Memory.LoadPercent;
            if (load < settings.TrimAbovePercent) return;
            if ((DateTime.Now - lastAutoTrim).TotalSeconds < 60) return;
            // Only start the wait once something was actually freed (right after start-up RamGuard
            // hasn't measured CPU yet, so the first check may find nothing).
            if (FreeMemory(true, true)) lastAutoTrim = DateTime.Now;
        }

        // ------------------------------------------------------------------ game mode

        /// <summary>Every 2 s: start game mode when a game comes to the front, stop it when the game closes.</summary>
        void CheckGame()
        {
            if (snapshotPath != null) return;
            if (game.Active)
            {
                if ((DateTime.Now - lastGameCheck).TotalSeconds < 5) return;
                lastGameCheck = DateTime.Now;
                if (!game.GameStillRunning()) StopGame(game.GameDisplay + " closed");
                return;
            }
            if (!settings.GameMode || busy) return;
            int fg = Native.GetForegroundPid();
            string name;
            if (fg > 0 && game.IsGame(fg, out name)) StartGame(fg, name);
        }

        void StartGame(int pid, string name)
        {
            game.Begin(pid, name);
            var span = new GameSpan();
            span.Start = DateTime.Now;
            span.Name = game.GameDisplay;
            gameSpans.Add(span);
            foreach (int p in game.GamePids) scanner.DoNotTouch.Add(p);
            last = scanner.Scan();
            int calmed = game.CalmOthers(last);
            ActivityLog.Add(string.Format("GAME   Game mode ON for {0}: {1} background processes slowed down{2}. RAM {3}%.",
                game.GameDisplay, calmed, game.PowerBoosted ? ", power mode set to Best performance" : "", last.Memory.LoadPercent));
            // Free every other app's memory right away, so the game starts with room.
            FreeMemory(true, true, true);
            lastAutoTrim = DateTime.Now;
            SetStatus(string.Format("Game mode on for {0}. Other apps are slowed down and their memory is freed. Nothing was closed.", game.GameDisplay));
            iconPct = -1;
            UpdateTrayIcon();
            if (Visible) RefreshNow();
        }

        /// <summary>Every 30 s while gaming: slow down newly opened apps, keep RAM under the gaming limit.</summary>
        void GameTick()
        {
            if (busy) return;
            foreach (int p in game.GamePids) scanner.DoNotTouch.Add(p);
            last = scanner.Scan();
            int calmed = game.CalmOthers(last);
            if (calmed > 0) ActivityLog.Add("GAME   Slowed down " + calmed + " more background processes that started during the game.");
            if (last.Memory.LoadPercent >= settings.GameRamLimit && (DateTime.Now - lastAutoTrim).TotalSeconds >= 60)
                if (FreeMemory(true, true)) lastAutoTrim = DateTime.Now;
            if (Visible) UpdateHeader();
        }

        void StopGame(string why)
        {
            TimeSpan played = DateTime.Now - game.Since;
            string display = game.GameDisplay;
            long freed = game.FreedBytes;
            int restored = game.Stop();
            scanner.DoNotTouch.Clear();
            foreach (var s in gameSpans) if (!s.End.HasValue) s.End = DateTime.Now;
            stats.AddGame(played);
            ActivityLog.Add(string.Format("GAME   Game mode OFF ({0}, after {1}). {2} apps back to normal speed, power mode restored. {3} was freed during the game.",
                why, Units.Span(played), restored, Units.Size(freed)));
            SetStatus(string.Format("Game mode off: {0}. Everything is back to normal speed.", why));
            iconPct = -1;
            UpdateTrayIcon();
            if (Visible) RefreshNow();
        }

        void SetGameMode(bool on)
        {
            settings.GameMode = on;
            settings.Save();
            suppressEvents = true;
            chkGame.Checked = on;
            suppressEvents = false;
            trayGame.Checked = on;
            if (!on && game.Active) StopGame("you turned Game mode off");
            SetStatus(on
                ? "Game mode is on: when a game is in front of you, other apps are slowed down and their memory is freed. Nothing is closed."
                : "Game mode is off.");
        }

        void ToggleIsGame()
        {
            var g = SelectedGroup();
            if (g == null) return;
            string name = g.Procs[0].Name;
            bool? now = GameMode.DecideByName(name, settings, null);
            if (now.HasValue && now.Value)
            {
                settings.Games.Remove(name);
                settings.NotGames.Add(name);
                SetStatus(g.Display + " will no longer start Game mode.");
                if (game.Active && string.Equals(game.GameName, name, StringComparison.OrdinalIgnoreCase)) StopGame("not a game");
            }
            else
            {
                settings.NotGames.Remove(name);
                settings.Games.Add(name);
                SetStatus(g.Display + " is now treated as a game: Game mode starts when it's in front of you.");
            }
            game.ForgetVerdicts();
            settings.Save();
        }

        void SetAuto(bool on)
        {
            settings.AutoMode = on;
            settings.Save();
            suppressEvents = true;
            chkAuto.Checked = on;
            suppressEvents = false;
            trayAuto.Checked = on;
            if (!on)
            {
                SetStatus("Auto mode is off. Nothing is closed unless you ask.");
                return;
            }
            SetStatus("Auto mode is on: when RAM is above " + settings.TrimAbovePercent + "%, background apps' memory is freed. " +
                "They stay open and nothing is lost. Only apps set to \"Auto-close\" are closed.");
            BeginInvoke((MethodInvoker)AutoTick);
        }

        void SetBusy(bool on)
        {
            SetBusy(on, true);
        }

        // Background (auto) work doesn't touch the window: no disabled buttons, no wait cursor.
        void SetBusy(bool on, bool showInUi)
        {
            busy = on;
            if (!showInUi) return;
            btnClean.Enabled = !on;
            btnTrim.Enabled = !on;
            UseWaitCursor = on;
        }

        void SetStatus(string text)
        {
            lblStatus.Text = text;
            tips.SetToolTip(lblStatus, text);
        }

        // ------------------------------------------------------------------ tray & window

        void UpdateTrayIcon()
        {
            var m = Native.GetMemory();
            int pct = Math.Min(99, m.LoadPercent);
            if (pct == iconPct) return;
            iconPct = pct;
            Icon old = tray.Icon;
            tray.Icon = Draw.MakeIcon(SystemInformation.SmallIconSize.Width, pct.ToString(), theme.ForLoad(pct));
            string tip = string.Format("RamGuard · RAM {0}% · {1} of {2}", m.LoadPercent, Units.Size(m.UsedBytes), Units.Size(m.TotalBytes));
            if (game.Active) tip += "\nGame mode: " + game.GameDisplay;
            else if (lastFreed != null) tip += "\n" + lastFreed;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            if (old != null) old.Dispose();
        }

        public void ShowMain()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            RefreshNow();
        }

        void ExitApp()
        {
            if (game.Active) StopGame("RamGuard was closed");
            exiting = true;
            tray.Visible = false;
            settings.Save();
            Close();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.UseDarkTitleBar(Handle, theme.Dark);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RefreshNow();
            grid.ClearSelection();
            if (snapshotPath != null)
            {
                var t = new System.Windows.Forms.Timer();
                t.Interval = 1500;
                t.Tick += delegate
                {
                    t.Stop();
                    t.Dispose();
                    TakeSnapshots();
                    exiting = true;
                    Close();
                };
                t.Start();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                // The X button hides to the tray; "Exit RamGuard" in the tray menu really quits.
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(4000, "RamGuard is still running",
                        "It keeps watching from the tray. Right-click the icon to exit.", ToolTipIcon.Info);
                }
                using (var me = Process.GetCurrentProcess()) Native.TrimWorkingSet(me.Id);
                return;
            }
            // Shutting down / signing out: put the other apps and power mode back first.
            if (game != null && game.Active) StopGame("Windows is shutting down");
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (game != null && game.Active) game.Stop();
                if (fgTimer != null) fgTimer.Dispose();
                if (uiTimer != null) uiTimer.Dispose();
                if (autoTimer != null) autoTimer.Dispose();
                if (tray != null) { tray.Visible = false; if (tray.Icon != null) tray.Icon.Dispose(); tray.Dispose(); }
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ startup & previews

        void ShowStartupApps()
        {
            if (!Visible) ShowMain();
            using (var dlg = new StartupDialog(theme, dpi)) dlg.ShowDialog(this);
        }

        /// <summary>Preview only: fills the graph with a made-up last hour so the picture shows every part of it.</summary>
        void SeedPreviewHistory()
        {
            history.Clear(); freeEvents.Clear(); gameSpans.Clear();
            DateTime now = DateTime.Now;
            var rnd = new Random(7);
            for (int i = 360; i >= 0; i--)
            {
                DateTime t = now.AddSeconds(-10 * i);
                double m = 60 - i / 6.0;
                double load = m > 40 && m < 52 ? 62 : 70 + 12 * Math.Sin(m / 7.0) + rnd.Next(-2, 3);
                if (m > 18 && m < 20) load = 86 - (m - 18) * 6;
                var p = new GraphPoint();
                p.Time = t;
                p.Load = (int)load;
                history.Add(p);
            }
            foreach (int ago in new[] { 41, 33, 26, 9 })
            {
                var e = new GraphEvent();
                e.Time = now.AddMinutes(-ago);
                e.Bytes = 400 * Units.MB;
                freeEvents.Add(e);
            }
            var span = new GameSpan();
            span.Start = now.AddMinutes(-20);
            span.End = now.AddMinutes(-8);
            span.Name = "VALORANT";
            gameSpans.Add(span);
        }

        void TakeSnapshots()
        {
            SeedPreviewHistory();
            RefreshNow();
            grid.ClearSelection();
            graph.Invalidate();
            Application.DoEvents();
            SaveImage(this, snapshotPath);
            if (snapshotStartupPath != null)
                using (var sd = new StartupDialog(theme, dpi))
                {
                    sd.StartPosition = FormStartPosition.Manual;
                    sd.Location = new Point(-20000, -20000);
                    sd.Show();
                    Application.DoEvents();
                    SaveImage(sd, snapshotStartupPath);
                    sd.Close();
                }
            if (snapshotDialogPath == null) return;
            var cands = last.Groups.Where(g => g.IsCandidate(settings)).OrderByDescending(g => g.PrivateBytes).ToList();
            using (var dlg = new CleanDialog(cands, theme, dpi))
            {
                dlg.StartPosition = FormStartPosition.Manual;
                dlg.Location = new Point(-20000, -20000);
                dlg.Show();
                Application.DoEvents();
                SaveImage(dlg, snapshotDialogPath);
                dlg.Close();
            }
        }

        static void SaveImage(Control c, string path)
        {
            using (var bmp = new Bitmap(c.Width, c.Height))
            {
                c.DrawToBitmap(bmp, new Rectangle(Point.Empty, c.Size));
                bmp.Save(path, ImageFormat.Png);
            }
        }
    }
}
