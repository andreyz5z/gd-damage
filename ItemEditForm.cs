// Item editor: find a seed that produces the requested rolled values.
//
// The left side mirrors the item tooltip. Each rolled row has an input value and
// a required-field checkbox; the right side contains controls and the result.
//
// The edit remains game-valid: only `seed` changes in the save. Item records,
// affixes, components, augments, and ascension data are not written directly.
//
// The input value can differ from the displayed row: damage-over-time rows may
// cover the full duration, and one tooltip row can combine several item parts.
// Inputs therefore use the rolled value of the owning part and its reachable range.
//
// Search runs on a dedicated worker while the modal window is active. The cancel
// flag is checked during enumeration and stops the search at the next checkpoint.
//
// The UI works with rolled values rather than exposing the seed. The command-line
// report still includes the seed where it is useful for diagnostics.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public partial class ItemEditForm : Form
    {
        readonly GameData game;
        readonly Item item;
        readonly DbRecord rec;
        readonly ItemViewFactory.CharacterContext who;
        SeedSearch.Plan plan;
        uint candidate;

        readonly Panel list = new Panel();
        readonly Panel side = new Panel();
        readonly Label head = new Label();
        readonly TextBox log = new TextBox();
        readonly Button findByValues = new Button();
        readonly Button findMax = new Button();
        readonly Button reroll = new Button();
        readonly Button ascension = new Button();
        readonly Button relicBonus = new Button();
        readonly Button affixes = new Button();
        readonly Button apply = new Button();
        readonly Button close = new Button();
        readonly Button stop = new Button();
        readonly Label busy = new Label();

        // One editable field belonging to one item part.
        class Row
        {
            public ItemOrder.Rolled Slot;
            public CheckBox Must;
            public TextBox Box;
            public Label Range;
            public long Low, High;
        }
        readonly List<Row> rows = new List<Row>();
        // Rolled fields with no tooltip row. `--edittest` reports them explicitly
        // because the editor still exposes them at the end of the list.
        readonly List<string> loose = new List<string>();
        public List<string> LooseFields { get { return loose; } }

        volatile bool cancel;
        Thread worker;

        // Delay clamping while the user types; leaving the field clamps immediately.
        readonly System.Windows.Forms.Timer settle = new System.Windows.Forms.Timer { Interval = 2000 };
        Row pending;
        // Candidate found but not saved. A separate flag is needed because the
        // candidate temporarily replaces `item.Seed` while rebuilding the tooltip.
        bool notSaved;

        static readonly Font BodyFont = new Font("Segoe UI", 9f);
        static readonly Color Back = Color.FromArgb(24, 22, 20);
        static readonly Color Ink = Color.FromArgb(215, 212, 205);

        public bool Changed;   // seed written; the save must be persisted

        public ItemEditForm(GameData game, Item item) : this(game, item, null) { }

        // `who` supplies the character masteries used when rendering skill rows.
        public ItemEditForm(GameData game, Item item, ItemViewFactory.CharacterContext who)
        {
            this.game = game;
            this.item = item;
            this.who = who;
            rec = game == null ? null : game.Record(item.BaseName);
            candidate = item.Seed;

            // UI text comes from `lang/<CODE>/edit.txt` and `common.txt`.
            Text = Lang.T("edit.title");
            Width = 1120; Height = 800;
            StartPosition = FormStartPosition.CenterParent;
            AppIcon.Apply(this);
            BackColor = Back;
            ForeColor = Ink;
            Font = BodyFont;

            plan = SeedSearch.Build(game, item, rec);

            head.Dock = DockStyle.Top;
            head.Height = 46;
            head.Padding = new Padding(10, 8, 10, 0);
            head.Font = new Font("Segoe UI", 11f, FontStyle.Bold);

            list.Dock = DockStyle.Fill;
            list.AutoScroll = true;
            list.BackColor = Color.FromArgb(18, 16, 14);

            side.Dock = DockStyle.Right;
            side.Width = 368;
            side.Padding = new Padding(10);

            BuildSide();
            Controls.Add(list);
            Controls.Add(side);
            Controls.Add(head);

            settle.Tick += delegate { settle.Stop(); Clamp(pending); pending = null; };
            Disposed += delegate { settle.Dispose(); };

            // Closing the window cancels the worker. The two-second join is safe:
            // progress and completion use asynchronous `BeginInvoke` callbacks.
            FormClosing += delegate
            {
                cancel = true;
                Thread w = worker;
                if (w != null && w.IsAlive) w.Join(2000);
            };

            Rebuild();
        }

        // ------------------------------------------------------------- right side

        // The right side has top controls, bottom action buttons, and a fill area
        // between them. Docked controls must be added in reverse visual order.
        void BuildSide()
        {
            // Use docking rather than coordinate anchors so button widths follow
            // the final panel size. The relic-only completion button is hidden for
            // other items and therefore does not consume layout space.
            Panel top = new Panel { Dock = DockStyle.Top, Height = 258, Padding = new Padding(2) };
            AddButton(top, stop, Lang.T("edit.stop"), delegate { cancel = true; stop.Enabled = false; });
            stop.Visible = false;

            busy.Dock = DockStyle.Top;
            busy.Height = 22;
            busy.Text = "";
            busy.ForeColor = Color.FromArgb(220, 180, 110);
            top.Controls.Add(busy);

            AddButton(top, relicBonus, Lang.T("edit.relicBonus"), delegate { EditRelicBonus(); });
            relicBonus.Visible = RelicBonus.IsRelic(rec);
            // Prefixes and suffixes are available only for items supported by the
            // loot tables; hide the editor when the item cannot have affixes.
            AddButton(top, affixes, Lang.T("edit.affixes"), delegate { EditAffixes(); });
            affixes.Visible = item != null && Affixes.CanHave(game, item.BaseName);
            AddButton(top, ascension, Lang.T("edit.ascension"), delegate { EditAscension(); });
            AddButton(top, reroll, Lang.T("edit.reroll"), delegate { Reroll(); });
            AddButton(top, findMax, Lang.T("edit.findMax"), delegate { Search(true); });
            AddButton(top, findByValues, Lang.T("edit.findValues"), delegate { Search(false); });

            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            apply.SetBounds(4, 4, 150, 30);
            apply.Text = Lang.T("common.apply");
            apply.Click += delegate { Apply(); };
            bottom.Controls.Add(apply);
            close.SetBounds(162, 4, 140, 30);
            close.Text = Lang.T("edit.close");
            close.Click += delegate { Close(); };
            bottom.Controls.Add(close);

            log.Dock = DockStyle.Fill;
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.BackColor = Color.FromArgb(18, 16, 14);
            log.ForeColor = Ink;
            log.BorderStyle = BorderStyle.FixedSingle;

            side.Controls.Add(log);
            side.Controls.Add(bottom);
            side.Controls.Add(top);
        }

        // Add top-docked buttons from bottom to top. `AutoEllipsis` keeps long
        // localized labels inside the button.
        void AddButton(Panel host, Button b, string text, EventHandler onClick)
        {
            b.Dock = DockStyle.Top;
            b.Height = 34;
            b.Text = text;
            b.AutoEllipsis = true;
            b.Click += onClick;
            host.Controls.Add(b);
        }

        // -------------------------------------------------------------- left side

        // Build rows from the tooltip so the editor matches the displayed text.
        // `TipLine.Part` and `TipLine.Explains` identify the roll slots.
        void Rebuild()
        {
            list.SuspendLayout();
            list.Controls.Clear();
            rows.Clear();

            ItemView view = ItemViewFactory.Build(game, item, false, who);
            // Keep the seed out of the title; show only whether the candidate is saved.
            head.Text = view.Name + (notSaved ? "      " + Lang.T("edit.notSaved") : "");
            head.ForeColor = TipPalette.Rarity(view.Classification);

            HashSet<string> shown = new HashSet<string>(StringComparer.Ordinal);
            int y = 8, w = list.ClientSize.Width - 40;
            foreach (TipLine line in view.Details)
            {
                if (line.Kind == TipKind.Name) continue;
                List<ItemOrder.Rolled> mine = new List<ItemOrder.Rolled>();
                if (line.Part >= 0 && line.Explains != null)
                    foreach (string f in line.Explains)
                    {
                        ItemOrder.Rolled slot = plan.Slot(line.Part, f);
                        if (slot != null && !shown.Contains(Key(slot))) mine.Add(slot);
                    }

                if (mine.Count == 0)
                {
                    if (string.IsNullOrEmpty(line.Text)) { y += 8; continue; }
                    Label plain = new Label();
                    int left = 12 + line.Indent * 14;
                    // Wrap long explanatory rows as the tooltip does.
                    int wide = w - left;
                    int high = TextRenderer.MeasureText(line.Text, BodyFont,
                                                        new Size(wide, 0), TextFormatFlags.WordBreak).Height;
                    plain.SetBounds(left, y, wide, Math.Max(18, high + 2));
                    plain.Text = line.Text;
                    // Match tooltip dimming through `Override` instead of relying
                    // only on the row kind.
                    plain.ForeColor = line.Override != Color.Empty ? line.Override
                                    : line.Kind == TipKind.Section ? Color.FromArgb(220, 180, 110)
                                    : TipPalette.Of(line.Kind);
                    list.Controls.Add(plain);
                    y += plain.Height + 2;
                    continue;
                }

                for (int i = 0; i < mine.Count; i++)
                {
                    y = AddRow(mine[i], i == 0 ? line.Text : Lang.T("edit.secondEnd"),
                               line.Indent, y, w, i == 0 ? line.PerTick : 1);
                    shown.Add(Key(mine[i]));
                }
            }

            // Report rolled fields that are absent from tooltip rows; their rolls
            // still affect the seed search.
            List<ItemOrder.Rolled> rest = new List<ItemOrder.Rolled>();
            foreach (SeedSearch.Lane lane in plan.Lanes)
                foreach (ItemOrder.Rolled slot in lane.Slots)
                    if (!shown.Contains(Key(slot))) rest.Add(slot);
            if (rest.Count > 0)
            {
                y += 10;
                Label h = new Label();
                h.SetBounds(12, y, w, 18);
                h.Text = Lang.T("edit.hiddenRolls");
                h.ForeColor = Color.FromArgb(220, 180, 110);
                list.Controls.Add(h);
                y += 22;
                foreach (ItemOrder.Rolled slot in rest) y = AddRow(slot, slot.Field, 1, y, w, 1);
            }
            loose.Clear();
            foreach (ItemOrder.Rolled slot in rest) loose.Add(slot.Field);

            list.ResumeLayout();
        }

        static string Key(ItemOrder.Rolled slot) { return slot.Part + "|" + slot.Field; }

        // Clamp the input to the reachable field range. Integer item scaling can
        // skip values inside the displayed range.
        void Clamp(Row r)
        {
            if (r == null || r.Box == null || r.Box.IsDisposed) return;
            long v;
            if (!long.TryParse(r.Box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            { r.Box.Text = r.Slot.Got.ToString(CultureInfo.InvariantCulture); return; }
            long lo = Math.Min(r.Low, r.High), hi = Math.Max(r.Low, r.High);
            long want = v < lo ? lo : v > hi ? hi : v;
            string text = want.ToString(CultureInfo.InvariantCulture);
            if (r.Box.Text == text) return;
            r.Box.Text = text;
            r.Box.SelectionStart = text.Length;
        }

        // `perTick` is the duration represented by the displayed tooltip value;
        // a value of one means the row and field use the same unit.
        int AddRow(ItemOrder.Rolled slot, string text, int indent, int y, int w, double perTick)
        {
            Row r = new Row();
            r.Slot = slot;
            SeedSearch.Range(plan, slot, out r.Low, out r.High);

            // A damage-over-time row may show the full duration while the editable
            // field stores the per-second rolled value.
            if (perTick > 1.0001)
                text += "   " + Lang.T("edit.perSecond", slot.Got.ToString(CultureInfo.InvariantCulture));

            r.Must = new CheckBox();
            r.Must.SetBounds(10, y, 18, 18);
            r.Must.Checked = true;
            list.Controls.Add(r.Must);

            Label label = new Label();
            label.SetBounds(32 + indent * 14, y, w - 230 - indent * 14, 18);
            label.Text = text;
            label.ForeColor = TipPalette.Of(TipKind.Bonus);
            list.Controls.Add(label);

            r.Box = new TextBox();
            r.Box.SetBounds(w - 190, y - 2, 70, 22);
            r.Box.Text = slot.Got.ToString(CultureInfo.InvariantCulture);
            r.Box.BackColor = Color.FromArgb(30, 28, 26);
            r.Box.ForeColor = Ink;
            r.Box.BorderStyle = BorderStyle.FixedSingle;
            r.Box.TextAlign = HorizontalAlignment.Right;
            // Clamp on focus loss and after the typing delay so impossible values
            // do not enter the search.
            Row captured = r;
            r.Box.Leave += delegate { Clamp(captured); };
            r.Box.TextChanged += delegate
            {
                pending = captured;
                settle.Stop();
                settle.Start();
            };
            list.Controls.Add(r.Box);

            r.Range = new Label();
            r.Range.SetBounds(w - 112, y, 150, 18);
            r.Range.Text = r.Low == r.High ? "" : Lang.T("edit.range", r.Low, r.High);
            r.Range.ForeColor = Color.FromArgb(150, 145, 135);
            list.Controls.Add(r.Range);

            rows.Add(r);
            return y + 24;
        }

        // --------------------------------------------------------------- worker

        void Reroll()
        {
            byte[] rnd = new byte[4];
            System.Security.Cryptography.RandomNumberGenerator.Create().GetBytes(rnd);
            candidate = BitConverter.ToUInt32(rnd, 0) % 2147483647u;
            notSaved = true;
            plan.Roll(candidate);
            uint keep = item.Seed;
            item.Seed = candidate;
            Rebuild();
            item.Seed = keep;
            log.Text = Lang.WinLines(Lang.T("edit.rerolled"));
        }

        void Search(bool wantMax)
        {
            List<SeedSearch.Want> wants = new List<SeedSearch.Want>();
            foreach (Row r in rows)
            {
                if (wantMax) r.Box.Text = SeedSearch.Best(plan, r.Slot).ToString(CultureInfo.InvariantCulture);
                // Clamp again in case search starts before focus-loss processing.
                Clamp(r);
                if (!r.Must.Checked) continue;
                long v;
                if (!long.TryParse(r.Box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                {
                    log.Text = Lang.T("edit.badNumber", ItemViewFactory.FieldLabel(game, r.Slot.Field), r.Box.Text);
                    return;
                }
                SeedSearch.Want w = new SeedSearch.Want();
                w.Part = r.Slot.Part; w.Field = r.Slot.Field; w.Value = v;
                w.Label = ItemViewFactory.FieldLabel(game, r.Slot.Field);
                wants.Add(w);
            }

            cancel = false;
            Working(true);
            busy.Text = Lang.T("edit.busy");
            log.Text = Lang.WinLines(Lang.T("edit.searching")) + Environment.NewLine;

            SeedSearch.Options opt = new SeedSearch.Options();
            opt.Tick = delegate (long done)
            {
                // Do not post progress after the form is disposed; the callback runs
                // on a worker and has no UI exception handler.
                if (cancel || IsDisposed || Disposing) return false;
                BeginInvoke((MethodInvoker)delegate
                {
                    busy.Text = Lang.T("edit.busyProgress", (done / 1000000).ToString(CultureInfo.InvariantCulture));
                });
                return true;
            };

            worker = new Thread(delegate ()
            {
                SeedSearch.Report rep = SeedSearch.Find(plan, wants, opt);
                BeginInvoke((MethodInvoker)delegate { Done(rep); });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        void Done(SeedSearch.Report rep)
        {
            // A queued completion can arrive after the form closes; ignore it safely.
            if (IsDisposed || Disposing) return;
            Working(false);
            busy.Text = "";
            if (rep.Found)
            {
                candidate = rep.Seed;
                notSaved = true;
                uint keep = item.Seed;
                item.Seed = candidate;
                Rebuild();
                item.Seed = keep;
                log.Text = Lang.T("edit.found") + Environment.NewLine + Environment.NewLine + rep.Short;
                return;
            }
            plan.Roll(candidate);
            // The detailed sieve report remains in `rep.Text` for `--seedwant`;
            // the window shows only the failed field and search status.
            string why;
            if (rep.Impossible.Count > 0)
            {
                why = Lang.T("edit.impossible") + Environment.NewLine;
                foreach (string one in rep.Impossible) why += "   " + one + Environment.NewLine;
                why += Lang.T("edit.impossibleHint");
            }
            else if (rep.Cancelled)
                why = Lang.T("edit.cancelled");
            else
            {
                why = Lang.T("edit.notFound");
                if (rep.Deepest != null)
                    why += Environment.NewLine + Environment.NewLine +
                           Lang.T("edit.hardest", ItemViewFactory.FieldLabel(game, rep.Deepest));
            }
            log.Text = why + Environment.NewLine + Environment.NewLine + rep.Short;
        }

        void Working(bool on)
        {
            findByValues.Enabled = findMax.Enabled = reroll.Enabled = ascension.Enabled =
                affixes.Enabled = apply.Enabled = close.Enabled = !on;
            list.Enabled = !on;
            stop.Visible = on;
            stop.Enabled = on;
            UseWaitCursor = on;
        }

        // Only `seed` is written; all other item fields remain unchanged.
        void Apply()
        {
            if (candidate == item.Seed) { log.Text = Lang.T("edit.same"); return; }
            item.Seed = candidate;
            Changed = true;
            notSaved = false;
            plan = SeedSearch.Build(game, item, rec);
            Rebuild();
            log.Text = Lang.WinLines(Lang.T("edit.saved"));
        }

        // Used by `--editshot ... find` to run the same asynchronous search as the UI.
        public bool SearchAndWait(bool max, int seconds)
        {
            Search(max);
            DateTime until = DateTime.Now.AddSeconds(seconds);
            while (worker != null && worker.IsAlive && DateTime.Now < until)
            { Application.DoEvents(); Thread.Sleep(10); }
            for (int i = 0; i < 20; i++) Application.DoEvents();   // allow Done() to run
            return candidate != item.Seed;
        }

        public uint Candidate { get { return candidate; } }
        public string Log { get { return log.Text; } }

        // Test helper: apply the same clamping used when leaving the input field.
        public string TypeForTest(int row, string typed)
        {
            if (row < 0 || row >= rows.Count) return null;
            rows[row].Box.Text = typed;
            Clamp(rows[row]);
            return rows[row].Box.Text;
        }

        public void RangeForTest(int row, out long lo, out long hi)
        {
            lo = rows[row].Low; hi = rows[row].High;
        }

        // Used by `--edittest`: every rolled field must have a tooltip row with
        // the calculated value. `why` is diagnostic output, not localized UI text.
        #region Self-check — not interface
        #endregion

        public int RowCount { get { return rows.Count; } }

        // Relic completion bonus is rolled, so rebuild both the search plan and the
        // displayed tooltip after changing it.
        void EditRelicBonus()
        {
            using (RelicBonusForm f = new RelicBonusForm(game, item))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                Changed = true;
                plan = SeedSearch.Build(game, item, rec);
                Rebuild();
                log.Text = string.IsNullOrEmpty(item.RelicBonus)
                         ? Lang.T("edit.relicCleared")
                         : Lang.T("edit.relicSet", RelicBonus.Describe(game, item.RelicBonus, item.Seed));
            }
        }

        // Prefixes and suffixes are rolled; rebuild the search plan and tooltip after
        // changing either affix.
        void EditAffixes()
        {
            using (AffixForm f = new AffixForm(game, item))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                Changed = true;
                plan = SeedSearch.Build(game, item, rec);
                Rebuild();
                string pre = string.IsNullOrEmpty(item.PrefixName)
                           ? Lang.T("edit.affix.none") : Affixes.Describe(game, item, item.PrefixName, false);
                string suf = string.IsNullOrEmpty(item.SuffixName)
                           ? Lang.T("edit.affix.none") : Affixes.Describe(game, item, item.SuffixName, true);
                log.Text = Lang.WinLines(Lang.T("edit.affixSet", pre, suf));
            }
        }

        void EditAscension()
        {
            using (AscensionForm f = new AscensionForm(game, item))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                Changed = true;
                plan = SeedSearch.Build(game, item, rec);
                Rebuild();
                log.Text = string.IsNullOrEmpty(item.AscensionName)
                         ? Lang.T("edit.ascensionCleared")
                         : Lang.T("edit.ascensionSet", item.AscensionName);
            }
        }
    }
}
