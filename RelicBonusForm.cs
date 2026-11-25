// Completion-bonus selection window, using the same layout as `AscensionForm`.
// The relic supplies `bonusTableName`; no item-class pool mapping is needed.
// Values are rolled for the current item, so the list shows the values that will
// be written. Seed search is intentionally not part of this window.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public class RelicBonusForm : Form
    {
        readonly GameData game;
        readonly Item item;
        readonly DbRecord relic;
        readonly ListBox choices = new ListBox();
        readonly Label what = new Label();
        readonly List<string> paths = new List<string>();
        readonly TextBox filter = new TextBox();
        readonly CheckBox everything = new CheckBox();
        List<RelicBonus.Choice> all;

        public RelicBonusForm(GameData game, Item item)
        {
            this.game = game;
            this.item = item;
            this.relic = game == null ? null : game.Record(item.BaseName);

            // Window text comes from `lang/<CODE>/edit.txt` and `common.txt`.
            Text = Lang.T("edit.relic.title");
            Width = 980; Height = 660;
            StartPosition = FormStartPosition.CenterParent;
            AppIcon.Apply(this);
            BackColor = Color.FromArgb(24, 22, 20);
            ForeColor = Color.FromArgb(215, 212, 205);

            string table = RelicBonus.TableOf(relic);
            string kind = relic == null ? null : relic.Get("artifactClassification");

            Label head = new Label();
            head.Dock = DockStyle.Top;
            head.Height = 62;
            head.Padding = new Padding(10, 8, 10, 0);
            // Show the localized relic name instead of the internal record path.
            string relicName = relic == null ? null : ItemViewFactory.Build(game, item, false).Name;
            // Keep internal table names out of the header. The list reports when
            // the relic has no selection table.
            // Was: head.Text = Lang.WinLines(Lang.T("edit.relic.head", …,
            //           RelicBonus.Short(table) == "" ? Lang.T("edit.relic.noTable") : RelicBonus.Short(table)));
            head.Text = Lang.WinLines(Lang.T("edit.relic.head",
                string.IsNullOrEmpty(relicName) ? Lang.T("edit.relic.unknown") : relicName,
                string.IsNullOrEmpty(kind) ? Lang.T("edit.relic.noKind") : kind));

            Panel top = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(10, 4, 10, 4) };
            Label fl = new Label { Text = Lang.T("common.search"), Left = 10, Top = 8, Width = 50 };
            filter.SetBounds(64, 5, 300, 22);
            filter.BackColor = Color.FromArgb(30, 28, 26);
            filter.ForeColor = Color.FromArgb(215, 212, 205);
            filter.BorderStyle = BorderStyle.FixedSingle;
            filter.TextChanged += delegate { Fill(); };
            everything.SetBounds(380, 6, 230, 20);
            everything.Text = Lang.T("edit.relic.allTables");
            everything.CheckedChanged += delegate { Reload(); };
            // Keep the all-tables control hidden. Enable the internal override when
            // the current item contains a value outside its normal table.
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
                item.RelicBonus = paths[i];
                DialogResult = DialogResult.OK;
                Close();
            };
            clear.Click += delegate
            {
                item.RelicBonus = "";
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

            // Include the current value when it is outside the normal table so
            // the selected row remains visible.
            if (!string.IsNullOrEmpty(item.RelicBonus))
            {
                bool inTable = false;
                foreach (RelicBonus.Choice c in RelicBonus.Choices(game, relic, false))
                    if (string.Equals(c.Path, item.RelicBonus, StringComparison.OrdinalIgnoreCase)) inTable = true;
                everything.Checked = !inTable;
            }
            Reload();
        }

        void Reload()
        {
            all = RelicBonus.Choices(game, relic, everything.Checked);
            Fill();
        }

        void Fill()
        {
            string need = filter.Text.Trim();
            choices.BeginUpdate();
            choices.Items.Clear();
            paths.Clear();
            foreach (RelicBonus.Choice c in all)
            {
                // Hide internal table names; the list already contains only the
                // relevant table.
                // Was: string text = (c.Suggested ? "● " : "   ") + "[" + c.Table + "] " +
                //                     RelicBonus.Describe(game, c.Path, item.Seed);
                string text = (c.Suggested ? "● " : "   ") + RelicBonus.Describe(game, c.Path, item.Seed);
                if (need.Length > 0 && text.IndexOf(need, StringComparison.OrdinalIgnoreCase) < 0) continue;
                paths.Add(c.Path);
                choices.Items.Add(text);
                if (string.Equals(c.Path, item.RelicBonus, StringComparison.OrdinalIgnoreCase))
                    choices.SelectedIndex = choices.Items.Count - 1;
            }
            choices.EndUpdate();
            if (choices.Items.Count == 0)
                choices.Items.Add(RelicBonus.TableOf(relic) == null
                    ? Lang.T("edit.relic.noTableList")
                    : Lang.T("common.nothingFound"));
            ShowWhat();
        }

        void ShowWhat()
        {
            int i = choices.SelectedIndex;
            if (i < 0 || i >= paths.Count) { what.Text = ""; return; }
            DbRecord r = game.Record(paths[i]);
            List<string> lines = new List<string>();
            // Keep the internal record path out of the user-facing list.
            lines.Add(Lang.T("edit.relic.jitter", ItemViewFactory.JitterOf(r)));
            foreach (TipLine l in ItemViewFactory.DescribeRolled(game, r, item.Seed))
                if (!string.IsNullOrEmpty(l.Text)) lines.Add(l.Text);
            what.Text = string.Join(Environment.NewLine, lines.ToArray());
        }
    }
}
