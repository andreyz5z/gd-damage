// Prefix and suffix selection window, using the same layout as the other item
// bonus editors: list, search, apply, remove, and cancel.
//
// Prefix and suffix are separate save fields, but share one window. Item values
// are rolled from one seed stream, so each candidate is evaluated with the full
// item and cached because the calculation is expensive.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public class AffixForm : Form
    {
        readonly GameData game;
        readonly Item item;
        readonly ListBox choices = new ListBox();
        readonly Label what = new Label();
        readonly Label head = new Label();
        readonly List<string> paths = new List<string>();
        readonly TextBox filter = new TextBox();
        readonly RadioButton prefixKind = new RadioButton();
        readonly RadioButton suffixKind = new RadioButton();
        // Cached list rows. Building a row requires evaluating the item and must
        // not be repeated for every search keystroke.
        readonly Dictionary<string, string> textOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<Affixes.Choice> all;
        bool loading;

        bool Suffix { get { return suffixKind.Checked; } }

        public AffixForm(GameData game, Item item)
        {
            this.game = game;
            this.item = item;

            // Window text comes from `lang/<CODE>/edit.txt` and `common.txt`.
            Text = Lang.T("edit.affix.title");
            Width = 980; Height = 660;
            StartPosition = FormStartPosition.CenterParent;
            AppIcon.Apply(this);
            BackColor = Color.FromArgb(24, 22, 20);
            ForeColor = Color.FromArgb(215, 212, 205);

            head.Dock = DockStyle.Top;
            head.Height = 62;
            head.Padding = new Padding(10, 8, 10, 0);

            Panel top = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(10, 4, 10, 4) };
            Label fl = new Label { Text = Lang.T("common.search"), Left = 10, Top = 8, Width = 50 };
            filter.SetBounds(64, 5, 300, 22);
            filter.BackColor = Color.FromArgb(30, 28, 26);
            filter.ForeColor = Color.FromArgb(215, 212, 205);
            filter.BorderStyle = BorderStyle.FixedSingle;
            filter.TextChanged += delegate { Fill(); };
            // Place the part selector after the search field using its localized
            // label width; translations may be longer than the default text.
            prefixKind.Text = Lang.T("edit.affix.prefix");
            suffixKind.Text = Lang.T("edit.affix.suffix");
            prefixKind.AutoSize = true; suffixKind.AutoSize = true;
            prefixKind.Left = filter.Right + 18; prefixKind.Top = 7;
            prefixKind.Checked = true;
            suffixKind.Left = prefixKind.Right + 14; suffixKind.Top = 7;
            EventHandler switched = delegate { if (!loading) Reload(); };
            prefixKind.CheckedChanged += switched;
            suffixKind.CheckedChanged += switched;
            top.Controls.Add(fl); top.Controls.Add(filter);
            top.Controls.Add(prefixKind); top.Controls.Add(suffixKind);

            what.Dock = DockStyle.Bottom;
            what.Height = 120;
            what.Padding = new Padding(10, 6, 10, 6);
            what.ForeColor = Color.FromArgb(150, 205, 140);

            Panel buttons = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(10, 8, 10, 8) };
            Button ok = new Button { Text = Lang.T("common.put"), Width = 130, Height = 28, Left = 10, Top = 8 };
            Button clear = new Button { Text = Lang.T("edit.affix.clear"), Width = 170, Height = 28, Left = 150, Top = 8 };
            Button cancel = new Button { Text = Lang.T("common.cancel"), Width = 110, Height = 28, Left = 330, Top = 8 };
            ok.Click += delegate
            {
                int i = choices.SelectedIndex;
                if (i < 0 || i >= paths.Count) return;
                Set(paths[i]);
                DialogResult = DialogResult.OK;
                Close();
            };
            clear.Click += delegate
            {
                Set("");
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

            // Add controls in this order: the `Fill` control must be added first,
            // as WinForms lays controls out in reverse order.
            Controls.Add(choices);
            Controls.Add(what);
            Controls.Add(buttons);
            Controls.Add(top);
            Controls.Add(head);

            Reload();
        }

        void Set(string affix)
        {
            if (Suffix) item.SuffixName = affix;
            else item.PrefixName = affix;
        }

        string Current { get { return Suffix ? item.SuffixName : item.PrefixName; } }

        void Reload()
        {
            Cursor keep = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                all = Affixes.Choices(game, item.BaseName, Suffix);
                all.Sort(delegate (Affixes.Choice a, Affixes.Choice b)
                {
                    return string.Compare(TextOf(a.Path), TextOf(b.Path), StringComparison.CurrentCultureIgnoreCase);
                });
                Fill();
                ShowHead();
            }
            finally { Cursor = keep; }
        }

        void ShowHead()
        {
            head.Text = Lang.WinLines(Lang.T("edit.affix.head", Worn(item.PrefixName, false), Worn(item.SuffixName, true)));
        }

        // Current item value. An affix no longer offered by the game is marked
        // separately because it is absent from the selectable list.
        string Worn(string affix, bool suffix)
        {
            if (string.IsNullOrEmpty(affix)) return Lang.T("edit.affix.none");
            string name = NameOrGives(affix, suffix);
            return Affixes.Offerable(game, affix) ? name : name + " " + Lang.T("edit.affix.gone");
        }

        // Prefer the record name for the header; fall back to the first granted
        // value instead of displaying a file name.
        string NameOrGives(string affix, bool suffix)
        {
            string name = Affixes.NameOf(game, affix);
            if (!string.IsNullOrEmpty(name)) return name;
            List<string> gives = Affixes.Gives(game, item, affix, suffix);
            return gives.Count > 0 ? gives[0] : Lang.T("edit.affix.none");
        }

        string TextOf(string affix)
        {
            string had;
            if (textOf.TryGetValue(affix, out had)) return had;
            string made = Affixes.Describe(game, item, affix, Suffix);
            textOf[affix] = made;
            return made;
        }

        // Rebuild only the filtered view; rows have already been evaluated.
        void Fill()
        {
            string need = filter.Text.Trim();
            loading = true;
            choices.BeginUpdate();
            choices.Items.Clear();
            paths.Clear();
            foreach (Affixes.Choice c in all)
            {
                string text = (c.Rare ? "● " : "   ") + TextOf(c.Path);
                if (need.Length > 0 && text.IndexOf(need, StringComparison.OrdinalIgnoreCase) < 0) continue;
                paths.Add(c.Path);
                choices.Items.Add(text);
                if (string.Equals(c.Path, Current, StringComparison.OrdinalIgnoreCase))
                    choices.SelectedIndex = choices.Items.Count - 1;
            }
            choices.EndUpdate();
            loading = false;
            if (choices.Items.Count == 0) choices.Items.Add(Lang.T("common.nothingFound"));
            ShowWhat();
        }

        void ShowWhat()
        {
            int i = choices.SelectedIndex;
            if (i < 0 || i >= paths.Count) { what.Text = ""; return; }
            List<string> lines = new List<string>();
            int level = 0;
            foreach (Affixes.Choice c in all)
                if (string.Equals(c.Path, paths[i], StringComparison.OrdinalIgnoreCase)) level = c.Level;
            if (level > 0) lines.Add(Lang.T("edit.affix.level", level));
            lines.AddRange(Affixes.Gives(game, item, paths[i], Suffix));
            what.Text = string.Join(Environment.NewLine, lines.ToArray());
        }
    }
}
