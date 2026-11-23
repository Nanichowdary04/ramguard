using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RamGuard
{
    /// <summary>Shows the idle apps that would be closed and lets you untick any you want to keep.</summary>
    internal sealed class CleanDialog : Form
    {
        sealed class Item
        {
            public AppGroup Group;
            public string Text;
            public override string ToString() { return Text; }
        }

        readonly Theme theme;
        readonly CheckedListBox list;
        readonly CheckBox chkAlways;
        readonly Label lblTotal;
        readonly Button btnOk;

        public readonly List<AppGroup> Selected = new List<AppGroup>();
        public bool AlwaysAuto { get { return chkAlways.Checked; } }

        public CleanDialog(List<AppGroup> candidates, Theme theme, float dpi)
        {
            this.theme = theme;
            Text = "Close idle apps";
            Font = new Font("Segoe UI", 9.75f);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = theme.Back;
            ForeColor = theme.Text;
            ClientSize = new Size(Ui.S(dpi, 640), Ui.S(dpi, 440));

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.Padding = new Padding(Ui.S(dpi, 18), Ui.S(dpi, 16), Ui.S(dpi, 18), Ui.S(dpi, 14));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            Controls.Add(root);

            var title = Ui.MakeLabel("These apps haven't been used for a while", new Font("Segoe UI Semibold", 12.5f), theme.Text);
            var sub = Ui.MakeLabel(
                "Apps with a window are asked to close, so they can still ask you to save. Apps without a window " +
                "(dev servers, background tools, WSL & Docker) would be ended right away, so they start unticked: tick them only if you're sure.",
                Font, theme.Muted);
            sub.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            sub.Margin = new Padding(0, Ui.S(dpi, 4), 0, Ui.S(dpi, 10));
            root.Controls.Add(title, 0, 0);
            root.Controls.Add(sub, 0, 1);

            list = new CheckedListBox();
            list.Dock = DockStyle.Fill;
            list.BorderStyle = BorderStyle.None;
            list.BackColor = theme.Surface;
            list.ForeColor = theme.Text;
            list.CheckOnClick = true;
            list.IntegralHeight = false;
            list.Font = new Font("Segoe UI", 10.5f);
            foreach (var g in candidates)
            {
                var item = new Item();
                item.Group = g;
                item.Text = string.Format("{0}   —   {1}  ·  idle {2}  ·  {3}",
                    g.Display, Units.Size(g.PrivateBytes), Units.Span(g.IdleFor), Cleaner.ShortAction(g));
                // Only apps that can close themselves politely are pre-ticked.
                list.Items.Add(item, g.HasWindow && g.Key != Scanner.WslKey);
            }
            list.ItemCheck += delegate { BeginInvoke((MethodInvoker)UpdateTotal); };
            var listHost = new Panel();
            listHost.Dock = DockStyle.Fill;
            listHost.BackColor = theme.Surface;
            listHost.Padding = new Padding(Ui.S(dpi, 10), Ui.S(dpi, 8), Ui.S(dpi, 10), Ui.S(dpi, 8));
            listHost.Margin = new Padding(0);
            listHost.Controls.Add(list);
            root.Controls.Add(listHost, 0, 2);

            chkAlways = Ui.MakeCheck("Always close these automatically when they're idle (turns on Auto mode)", false, Font, theme, dpi);
            chkAlways.Margin = new Padding(0, Ui.S(dpi, 8), 0, 0);
            root.Controls.Add(chkAlways, 0, 3);

            var bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.AutoSize = true;
            bottom.ColumnCount = 3;
            bottom.RowCount = 1;
            bottom.Margin = new Padding(0, Ui.S(dpi, 6), 0, 0);
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lblTotal = Ui.MakeLabel("", Font, theme.Muted);
            lblTotal.Anchor = AnchorStyles.Left;
            btnOk = Ui.MakeButton("Close selected", true, theme, dpi);
            var btnCancel = Ui.MakeButton("Cancel", false, theme, dpi);
            btnCancel.Margin = new Padding(0, Ui.S(dpi, 4), 0, Ui.S(dpi, 4));
            btnCancel.DialogResult = DialogResult.Cancel;
            btnOk.Click += delegate
            {
                Selected.Clear();
                foreach (Item it in list.CheckedItems) Selected.Add(it.Group);
                DialogResult = DialogResult.OK;
            };
            bottom.Controls.Add(lblTotal, 0, 0);
            bottom.Controls.Add(btnOk, 1, 0);
            bottom.Controls.Add(btnCancel, 2, 0);
            root.Controls.Add(bottom, 0, 4);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
            UpdateTotal();
        }

        void UpdateTotal()
        {
            var picked = list.CheckedItems.Cast<Item>().ToList();
            long total = picked.Sum(i => i.Group.PrivateBytes);
            lblTotal.Text = picked.Count == 0
                ? "Nothing selected"
                : string.Format("{0} app{1} selected · frees about {2}", picked.Count, picked.Count == 1 ? "" : "s", Units.Size(total));
            btnOk.Enabled = picked.Count > 0;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.UseDarkTitleBar(Handle, theme.Dark);
        }
    }
}
