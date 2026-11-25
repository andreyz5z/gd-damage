// Ascension bonus selection.
//
// The list is built from the game's selection tables through `Ascension.cs`, so
// it follows game data changes. Ascension values are not rolled; only the
// `AscensionName` save field is changed.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public class AscensionForm : Form
    {
        readonly GameData game;
        readonly Item item;
        readonly ListBox choices = new ListBox();
        readonly Label what = new Label();
        readonly List<string> paths = new List<string>();
        readonly TextBox filter = new TextBox();
        readonly CheckBox everything = new CheckBox();
        string forClass;
        List<Ascension.Choice> all;

        public AscensionForm(GameData game, Item item)
        {
            this.game = game;
            this.item = item;

            // Window text comes from `lang/<CODE>/edit.txt` and `common.txt`.
            Text = Lang.T("edit.asc.title");
            Width = 900; Height = 640;
            StartPosition = FormStartPosition.CenterParent;
            AppIcon.Apply(this);
            BackColor = Color.FromArgb(24, 22, 20);
            ForeColor = Color.FromArgb(215, 212, 205);

            DbRecord rec = game == null ? null : game.Record(item.BaseName);
            string itemClass = rec == null ? null : rec.Class;
            string pool = Ascension.PoolOf(itemClass);

            Label head = new Label();
            head.Dock = DockStyle.Top;
            head.Height = 40;
            head.Padding = new Padding(10, 8, 10, 0);
            // Keep internal item classes and pool names out of the user-facing header.
            // The previous header included both values and implied an obsolete
            // all-pools option.
            // Was: head.Text = Lang.WinLines(Lang.T("edit.asc.head", itemClass ?? Lang.T("edit.asc.unknownClass"),
            //                                        pool ?? Lang.T("edit.asc.noPool")));
            head.Text = Lang.WinLines(Lang.T("edit.asc.head"));

            Panel top = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(10, 4, 10, 4) };
            Label fl = new Label { Text = Lang.T("common.search"), Left = 10, Top = 8, Width = 50 };
            filter.SetBounds(64, 5, 300, 22);
            filter.BackColor = Color.FromArgb(30, 28, 26);
            filter.ForeColor = Color.FromArgb(215, 212, 205);
            filter.BorderStyle = BorderStyle.FixedSingle;
            filter.TextChanged += delegate { Fill(); };
            everything.SetBounds(380, 6, 200, 20);
            everything.Text = Lang.T("edit.asc.allPools");
            everything.CheckedChanged += delegate { Reload(); };
            // Keep the control hidden to prevent assigning a bonus to an invalid
            // item class. It is still enabled internally when the current item
            // already contains a bonus outside its normal pool.
            everything.Visible = false;
            top.Controls.Add(fl); top.Controls.Add(filter); top.Controls.Add(everything);

            what.Dock = DockStyle.Bottom;
            what.Height = 120;
            what.Padding = new Padding(10, 6, 10, 6);
            what.ForeColor = Color.FromArgb(150, 205, 140);

            Panel buttons = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(10, 8, 10, 8) };
            Button ok = new Button { Text = Lang.T("common.put"), Width = 130, Height = 28, Left = 10, Top = 8 };
            Button clear = new Button { Text = Lang.T("common.clearBonus"), Width = 130, Height = 28, Left = 150, Top = 8 };
            Button cancel = new Button { Text = Lang.T("common.cancel"), Width = 110, Height = 28, Left = 290, Top = 8 };
            ok.Click += delegate
            {
                int i = choices.SelectedIndex;
                if (i < 0 || i >= paths.Count) return;
                item.AscensionName = paths[i];
                DialogResult = DialogResult.OK;
                Close();
            };
            clear.Click += delegate
            {
                item.AscensionName = "";
                DialogResult = DialogResult.OK;
                Close();
            };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            buttons.Controls.Add(ok); buttons.Controls.Add(clear); buttons.Controls.Add(cancel);

            choices.Dock = DockStyle.Fill;
            choices.BackColor = Color.FromArgb(18, 16, 14);
            choices.ForeColor = Color.FromArgb(215, 212, 205);
            choices.BorderStyle = BorderStyle.FixedSingle;
            choices.SelectedIndexChanged += delegate { ShowWhat(); };

            // Add controls in reverse layout order: the `Fill` control must be
            // added first or it will cover the header and search row.
            Controls.Add(choices);
            Controls.Add(what);
            Controls.Add(buttons);
            Controls.Add(top);
            Controls.Add(head);

            forClass = itemClass;
            // Include the current value when it is outside the normal pool, so
            // the selected row remains visible.
            if (!string.IsNullOrEmpty(item.AscensionName))
            {
                bool inPool = false;
                foreach (Ascension.Choice c in Ascension.Choices(game, itemClass, false))
                    if (string.Equals(c.Path, item.AscensionName, StringComparison.OrdinalIgnoreCase)) inPool = true;
                everything.Checked = !inPool;
            }
            Reload();
        }

        void Reload()
        {
            all = Ascension.Choices(game, forClass, everything.Checked);
            Fill();
        }

        // Rebuild the filtered view for each search change; the pool is large.
        void Fill()
        {
            string need = filter.Text.Trim();
            choices.BeginUpdate();
            choices.Items.Clear();
            paths.Clear();
            foreach (Ascension.Choice c in all)
            {
                // Hide internal pool names; the list already contains only the
                // pool relevant to this item.
                // Was: string text = (c.Suggested ? "● " : "   ") + "[" + c.Pool + "] " + Ascension.Describe(game, c.Path);
                string text = (c.Suggested ? "● " : "   ") + Ascension.Describe(game, c.Path);
                if (need.Length > 0 && text.IndexOf(need, StringComparison.OrdinalIgnoreCase) < 0) continue;
                paths.Add(c.Path);
                choices.Items.Add(text);
                if (string.Equals(c.Path, item.AscensionName, StringComparison.OrdinalIgnoreCase))
                    choices.SelectedIndex = choices.Items.Count - 1;
            }
            choices.EndUpdate();
            if (choices.Items.Count == 0) choices.Items.Add(Lang.T("common.nothingFound"));
            ShowWhat();
        }

        void ShowWhat()
        {
            int i = choices.SelectedIndex;
            if (i < 0 || i >= paths.Count) { what.Text = ""; return; }
            DbRecord r = game.Record(paths[i]);
            List<string> lines = new List<string>();
            // Keep the internal record path out of the user-facing list.
            // lines.Add(paths[i]);
            foreach (TipLine l in ItemViewFactory.Describe(game, r))
                if (!string.IsNullOrEmpty(l.Text)) lines.Add(l.Text);
            what.Text = string.Join(Environment.NewLine, lines.ToArray());
        }
    }
}
