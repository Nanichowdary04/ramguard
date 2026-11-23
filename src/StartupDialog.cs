using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace RamGuard
{
    /// <summary>Apps that start when you sign in, with an on/off switch for each.</summary>
    internal sealed class StartupDialog : Form
    {
        readonly Theme theme;
        readonly float dpi;
        readonly DataGridView grid;
        readonly Label lblStatus, lblSummary;
        DataGridViewCheckBoxColumn colOn;
        DataGridViewTextBoxColumn colApp, colUsing, colNote;
        DataGridViewButtonColumn colFix;
        List<StartupEntry> entries = new List<StartupEntry>();
        bool loading;

        int S(int v) { return Ui.S(dpi, v); }

        public StartupDialog(Theme theme, float dpi)
        {
            this.theme = theme;
            this.dpi = dpi;
            Text = "Startup apps";
            Font = new Font("Segoe UI", 9.75f);
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ShowIcon = false;
            MinimizeBox = false;
            BackColor = theme.Back;
            ForeColor = theme.Text;
            Size = new Size(S(900), S(600));
            MinimumSize = new Size(S(700), S(420));

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.Padding = new Padding(S(18), S(16), S(18), S(12));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            Controls.Add(root);

            root.Controls.Add(Ui.MakeLabel("Apps that start when you sign in", new Font("Segoe UI Semibold", 12.5f), theme.Text), 0, 0);
            lblSummary = Ui.MakeLabel("", Font, theme.Muted);
            lblSummary.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            lblSummary.Margin = new Padding(0, S(4), 0, S(10));
            root.Controls.Add(lblSummary, 0, 1);

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = theme.Surface;
            grid.BorderStyle = BorderStyle.None;
            grid.GridColor = theme.Line;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = S(36);
            var hs = grid.ColumnHeadersDefaultCellStyle;
            hs.BackColor = theme.Surface; hs.ForeColor = theme.Muted;
            hs.SelectionBackColor = theme.Surface; hs.SelectionForeColor = theme.Muted;
            hs.Font = new Font("Segoe UI Semibold", 9f);
            hs.Padding = new Padding(S(6), 0, S(6), 0);
            var cs = grid.DefaultCellStyle;
            cs.BackColor = theme.Surface; cs.ForeColor = theme.Text;
            cs.SelectionBackColor = theme.Selection; cs.SelectionForeColor = theme.Text;
            cs.Padding = new Padding(S(6), 0, S(6), 0);
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.RowTemplate.Height = S(34);
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.ShowCellToolTips = true;
            typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(grid, true, null);

            colOn = new DataGridViewCheckBoxColumn();
            colOn.HeaderText = "On";
            colOn.FillWeight = 6;
            colOn.FlatStyle = FlatStyle.Flat;
            colApp = Col("App", 30);
            colUsing = Col("Using now", 12);
            colUsing.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            colNote = Col("Notes", 40);
            colFix = new DataGridViewButtonColumn();
            colFix.HeaderText = "";
            colFix.FillWeight = 10;
            colFix.FlatStyle = FlatStyle.Flat;
            grid.Columns.AddRange(new DataGridViewColumn[] { colOn, colApp, colUsing, colNote, colFix });
            grid.CellContentClick += OnCellContentClick;
            grid.DataError += (s, e) => { e.ThrowException = false; };
            root.Controls.Add(grid, 0, 2);

            var bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.AutoSize = true;
            bottom.ColumnCount = 2;
            bottom.Margin = new Padding(0, S(8), 0, 0);
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lblStatus = Ui.MakeLabel("Turning an app off doesn't uninstall it, and you can turn it back on any time.", Font, theme.Muted);
            lblStatus.Anchor = AnchorStyles.Left;
            var btnClose = Ui.MakeButton("Done", true, theme, dpi);
            btnClose.Margin = new Padding(0, S(4), 0, S(4));
            btnClose.Click += delegate { Close(); };
            bottom.Controls.Add(lblStatus, 0, 0);
            bottom.Controls.Add(btnClose, 1, 0);
            root.Controls.Add(bottom, 0, 3);
            CancelButton = btnClose;

            Reload();
        }

        DataGridViewTextBoxColumn Col(string header, float weight)
        {
            var c = new DataGridViewTextBoxColumn();
            c.HeaderText = header;
            c.FillWeight = weight;
            c.ReadOnly = true;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            return c;
        }

        void Reload()
        {
            loading = true;
            try
            {
                entries = StartupManager.Load();
                grid.Rows.Clear();
                foreach (var e in entries)
                {
                    int i = grid.Rows.Add();
                    var row = grid.Rows[i];
                    row.Tag = e;
                    row.Cells[colOn.Index].Value = e.Enabled;
                    row.Cells[colOn.Index].ReadOnly = e.Locked != null;
                    row.Cells[colApp.Index].Value = e.Display;
                    row.Cells[colApp.Index].ToolTipText = e.Where + "\n" + (e.Command ?? "");
                    row.Cells[colUsing.Index].Value = e.Running ? Units.Size(e.RunningBytes) : "—";
                    row.Cells[colNote.Index].Value = Note(e);
                    bool removable = e.FileMissing && e.Locked == null &&
                        (e.Source == StartupSource.UserRun || e.Source == StartupSource.UserFolder);
                    if (removable) row.Cells[colFix.Index].Value = "Remove";
                    else row.Cells[colFix.Index] = new DataGridViewTextBoxCell(); // no empty button boxes
                    Color c = e.Locked != null ? theme.Muted : e.FileMissing ? theme.Warn : !e.Enabled ? theme.Muted : theme.Text;
                    row.DefaultCellStyle.ForeColor = c;
                    row.DefaultCellStyle.SelectionForeColor = c;
                }
                int on = entries.Count(x => x.Enabled);
                long ram = entries.Where(x => x.Enabled && x.Running).Sum(x => x.RunningBytes);
                lblSummary.Text = string.Format("{0} of {1} start with Windows; the ones running now use {2}. " +
                    "Turning one off doesn't uninstall it or close it now; it just won't start next time.",
                    on, entries.Count, Units.Size(ram));
                grid.ClearSelection();
            }
            finally { loading = false; }
        }

        static string Note(StartupEntry e)
        {
            if (e.Locked != null) return e.Locked;
            if (e.FileMissing) return "Its program is gone, so it does nothing: safe to remove";
            var parts = new List<string>();
            parts.Add(e.Enabled ? (e.Running ? "Running now" : "Starts at sign-in") : "Off");
            if (e.NeedsAdmin) parts.Add("Windows asks for admin to change it");
            return string.Join(" · ", parts);
        }

        void OnCellContentClick(object sender, DataGridViewCellEventArgs a)
        {
            if (loading || a.RowIndex < 0) return;
            var row = grid.Rows[a.RowIndex];
            var e = row.Tag as StartupEntry;
            if (e == null) return;

            if (a.ColumnIndex == colFix.Index)
            {
                if (!(row.Cells[colFix.Index] is DataGridViewButtonCell) || (row.Cells[colFix.Index].Value as string) != "Remove") return;
                string err = StartupManager.RemoveBroken(e);
                SetStatus(err ?? ("Removed the broken entry \"" + e.Display + "\"."), err != null);
                if (err == null) ActivityLog.Add("STARTUP removed broken entry: " + e.Display);
                Reload();
                return;
            }

            if (a.ColumnIndex != colOn.Index) return;
            if (e.Locked != null)
            {
                SetStatus(e.Display + ": " + e.Locked + ".", true);
                return;
            }
            bool on = !e.Enabled;
            UseWaitCursor = true;
            string error = StartupManager.SetEnabled(e, on);
            UseWaitCursor = false;
            if (error == null)
            {
                SetStatus(on ? e.Display + " will start with Windows again." : e.Display + " won't start with Windows any more. It's still installed, and you can turn it back on here.", false);
                ActivityLog.Add(string.Format("STARTUP {0} turned {1}", e.Display, on ? "on" : "off"));
            }
            else SetStatus(error, true);
            Reload();
        }

        void SetStatus(string text, bool warn)
        {
            lblStatus.Text = text;
            lblStatus.ForeColor = warn ? theme.Warn : theme.Muted;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.UseDarkTitleBar(Handle, theme.Dark);
        }
    }
}
