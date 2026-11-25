// The character's headline numbers, editable in place. Every change is pushed
// straight back into the loaded save and written out by the SaveManager.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public class CharacterPanel : Panel
    {
        Character character;
        bool loading;

        // The character name is display-only, not an editable save field.
        // Renaming has been observed to make the game reject the save, so keep
        // the name read-only until all storage locations are understood.
        readonly Label nameLabel = new Label();

        // THE HARDCORE MARK AND THE REVIVE BOX, beside the name. Both appear only when they
        // mean something: the mark when the save is a hardcore one, the box when such a
        // character is also dead. An ordinary character shows neither, so nothing new appears
        // for the saves people have most of.
        //
        // WHERE THE TWO MARKS LIVE AND HOW THAT WAS ESTABLISHED (13 October). The user gave
        // the same character twice, alive and dead, and the two saves were compared field by
        // field (`--charflags` now prints both columns):
        //
        //     hardcore byte   1 for both copies, 0 for every other save we have (49 of them)
        //     deaths          0 alive, 1 dead
        //
        // The hardcore byte is read and written by the save format already, so it only had to
        // be shown. The death count is an ordinary counter for a normal character — ours run
        // up to 387 — but a hardcore character can die once, so for him "more than zero" and
        // "dead" are the same thing.
        //
        // THIS IS A CANDIDATE, NOT A MEASURED RULE, and the tooltip says so in plain words:
        // we compared two saves and found one flag-like field apart, we did not watch the
        // game read it. Until the user confirms that a revived character loads, nothing else
        // is to be written on the strength of it.
        readonly Label hardcoreLabel = new Label();
        readonly CheckBox revive = new CheckBox();

        // `--hcbreak` drops the "and dead" half of the gate, so the box also appears on a
        // LIVING hardcore character. That is what `--hctest` must go red on; without seeing it
        // red the check is worth nothing (rule 12).
        public static bool BreakHardcoreGate;

        // What the two marks are doing, for the check: a window on the screen is not needed
        // to ask whether they would be shown. These are the DECISION, kept apart from
        // `Control.Visible`, which answers false for everything while the panel is on no form
        // — a check driving the panel without a window would then pass on nothing at all.
        bool hardcoreOn, reviveOn;
        public bool HardcoreShown { get { return hardcoreOn; } }
        public bool ReviveShown { get { return reviveOn; } }

        // The same thing a person does to the box, without a window. It goes through the
        // handler, so the check tests the handler and not a copy of it.
        public void TickRevive() { revive.Checked = true; }

        readonly Label classLabel = new Label();
        readonly Label fileLabel = new Label();
        readonly Label bagsLabel = new Label();

        readonly Dictionary<string, NumericUpDown> fields = new Dictionary<string, NumericUpDown>();
        readonly Dictionary<string, Label> captions = new Dictionary<string, Label>();
        // Labels in both table columns; used to calculate their widths.
        readonly List<Label> firstColumn = new List<Label>();
        readonly List<TableLayoutPanel> tables = new List<TableLayoutPanel>();
        // Minimum label-column width.
        const int CaptionWidth = 116;
        readonly CheckBox elite = new CheckBox();
        readonly CheckBox ultimate = new CheckBox();

        // The five numbers the game works out for itself: three attributes and
        // the health and energy that follow from them. Typing into these does not
        // make the character stronger, it makes the save disagree with the game —
        // so they are shown, explained, and locked until asked otherwise.
        static readonly string[] Derived = { "physique", "cunning", "spirit", "health", "energy" };

        readonly CheckBox freeEdit = new CheckBox
        {
            AutoSize = true,
            Appearance = Appearance.Button,
            Margin = new Padding(3, 8, 3, 3),
        };

        // Importing a character rewrites the whole save
        // from an outside build, so it asks first. The work is `GrimToolsImportForm`.
        // Its look is the top row's — `MainForm.MenuLook` — and only its look: the
        // height stays the one it had, which `AutoSize` gives it.
        readonly Button importButton = new Button
        {
            AutoSize = true,
            Margin = new Padding(3, 4, 3, 3),
        };

        readonly ToolTip tip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 350, ReshowDelay = 100 };
        CharacterTotals bonuses;
        // Game data supplies per-point increments and divisors; fall back to the
        // built-in values when it is unavailable.
        GameData game;

        // Everything the character carries, so the tooltip can say what an
        // attribute actually is right now — the save keeps only the part that
        // points were spent on.
        // The game arrives AFTER `Show`, so whatever needs it has to be asked again
        // here — the import button is the one that does.
        public void SetBonuses(CharacterTotals t, GameData game)
        { bonuses = t; this.game = game; importButton.Enabled = character != null && game != null; Explain(); }

        public event EventHandler Changed;

        public CharacterPanel()
        {
            AutoScroll = true;
            Padding = new Padding(8, 6, 8, 6);
            BuildLayout();
            FitCaptions();
            SetEnabled(false);
            commitTimer.Tick += delegate { CommitTyped(); };
            // Field labels are bound by `Lang.Bind`; numeric tooltips are rebuilt
            // on language changes before recalculating the column width.
            Action relabel = delegate { FitCaptions(); Explain(); };
            Lang.Changed += relabel;
            Disposed += delegate { commitTimer.Dispose(); Lang.Changed -= relabel; };
        }

        void BuildLayout()
        {
            // Two columns of fields, so the whole block fits beside the equipment
            // without scrolling.
            FlowLayoutPanel columns = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                AutoScroll = true,
                // In a narrow window the second column drops under the first
                // instead of being cut off by a horizontal scrollbar.
                WrapContents = true
            };

            TableLayoutPanel left = NewTable();
            tables.Add(left);
            AddNameHeader(left);
            AddHeader(left, "character.class", classLabel);
            AddHeader(left, "character.file", fileLabel);
            AddSeparator(left);
            // The game caps the level at 100; higher values would be normalized
            // back by the game.
            AddField(left, "level", "character.level", 1, 100);
            AddField(left, "experience", "character.experience", 0, int.MaxValue);
            AddSeparator(left);
            AddField(left, "physique", "character.physique", 0, 100000);
            AddField(left, "cunning", "character.cunning", 0, 100000);
            AddField(left, "spirit", "character.spirit", 0, 100000);
            AddField(left, "health", "character.health", 0, 10000000);
            AddField(left, "energy", "character.energy", 0, 10000000);
            AddSeparator(left);
            // Under the attributes rather than beside the difficulties: two lines
            // of text at the bottom of the right column pushed the panel past its
            // height and put a scrollbar on the whole block.
            bagsLabel.AutoSize = true;
            bagsLabel.ForeColor = Color.DimGray;
            left.Controls.Add(bagsLabel);
            left.SetColumnSpan(bagsLabel, 2);

            TableLayoutPanel right = NewTable();
            tables.Add(right);
            AddField(right, "attributePoints", "character.attributePoints", 0, 100000);
            AddField(right, "skillPoints", "character.skillPoints", 0, 100000);
            AddField(right, "devotionPoints", "character.devotionPoints", 0, 100);
            AddField(right, "totalDevotion", "character.totalDevotion", 0, 100);
            AddSeparator(right);
            AddField(right, "money", "character.money", 0, int.MaxValue);
            // Crucible currency; the game caps it at 150 and shows it only inside
            // the arena, which is why it never appears in the normal HUD.
            AddField(right, "tributes", "character.tributes", 0, 150);
            AddSeparator(right);

            Label difficultyLabel = new Label { AutoSize = true, ForeColor = Color.DimGray };
            Lang.Bind(difficultyLabel, "character.difficulties");
            right.Controls.Add(difficultyLabel);
            firstColumn.Add(difficultyLabel);
            FlowLayoutPanel flow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0) };
            CheckBox normal = new CheckBox { Checked = true, Enabled = false, AutoSize = true };
            Lang.Bind(normal, "character.difficulty.normal");
            Lang.Bind(elite, "character.difficulty.elite");
            Lang.Bind(ultimate, "character.difficulty.ultimate");
            Lang.Bind(freeEdit, "character.freeEdit");
            elite.AutoSize = true;
            ultimate.AutoSize = true;
            flow.Controls.Add(normal);
            flow.Controls.Add(elite);
            flow.Controls.Add(ultimate);
            right.Controls.Add(flow);

            // The save keeps one cumulative byte, so the two boxes cannot be set
            // independently: opening Ultimate implies Elite, closing Elite closes
            // Ultimate with it.
            elite.CheckedChanged += delegate
            {
                if (loading) return;
                if (!elite.Checked && ultimate.Checked) { loading = true; ultimate.Checked = false; loading = false; }
                ApplyDifficulty();
            };
            ultimate.CheckedChanged += delegate
            {
                if (loading) return;
                if (ultimate.Checked && !elite.Checked) { loading = true; elite.Checked = true; loading = false; }
                ApplyDifficulty();
            };

            columns.Controls.Add(left);
            columns.Controls.Add(right);

            freeEdit.CheckedChanged += delegate
            {
                if (freeEdit.Checked && character != null &&
                    MessageBox.Show(Lang.T("character.freeEdit.warning"), Lang.T("character.freeEdit"),
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                { freeEdit.Checked = false; return; }
                ApplyLock();
            };

            // The import button sits UNDER the free-edit one, which is why the row
            // runs downwards: they are both "do something to the whole character"
            // and belong together, but the import is the heavier of the two.
            // `WrapContents` off: a bottom-docked flow that may wrap turns the second
            // row into a second COLUMN as soon as the height is tight, and the import
            // button ended up beside the checkbox instead of under it.
            FlowLayoutPanel bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, AutoSize = true,
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
            };
            bottom.Controls.Add(freeEdit);
            MainForm.MenuLook(importButton);
            Lang.Bind(importButton, "import.button");
            importButton.Click += delegate { Import(); };
            bottom.Controls.Add(importButton);
            Controls.Add(columns);
            Controls.Add(bottom);
        }

        // Raised after a build has been imported: everything about the character has
        // changed at once — gear, skills, attributes — so the window that owns this
        // panel has to build itself again rather than just mark the save dirty.
        public event EventHandler Imported;

        // IMPORTING A CHARACTER FROM GRIMTOOLS. It asks outright, because there is
        // nothing to undo it with: what is worn is destroyed and every number of the
        // character is overwritten.
        void Import()
        {
            if (character == null) return;
            if (MessageBox.Show(Lang.T("import.warning"), Lang.T("import.button"),
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            using (GrimToolsImportForm dlg = new GrimToolsImportForm(game, character))
            {
                dlg.ShowDialog(this);
                if (!dlg.Imported) return;
            }
            EventHandler h = Imported;
            if (h != null) h(this, EventArgs.Empty);
        }

        // The derived five follow the lock; everything else follows whether a
        // save is open at all.
        void ApplyLock()
        {
            bool open = character != null;
            foreach (KeyValuePair<string, NumericUpDown> kv in fields)
                kv.Value.Enabled = open && (freeEdit.Checked || Array.IndexOf(Derived, kv.Key) < 0);
            foreach (string key in Derived)
            {
                Label c;
                if (captions.TryGetValue(key, out c))
                    c.ForeColor = freeEdit.Checked ? SystemColors.ControlText : Color.DimGray;
            }
        }

        static TableLayoutPanel NewTable()
        {
            TableLayoutPanel t = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Margin = new Padding(0, 0, 10, 0)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, CaptionWidth));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }

        // Size each label column to its longest caption, with a minimum of 116 px.
        // This avoids wrapping when a translation is longer than the default.
        void FitCaptions()
        {
            foreach (TableLayoutPanel t in tables)
            {
                int widest = CaptionWidth;
                foreach (Label l in firstColumn)
                    if (l.Parent == t)
                        widest = Math.Max(widest, l.GetPreferredSize(Size.Empty).Width + l.Margin.Horizontal);
                t.ColumnStyles[0].Width = widest;
            }
        }

        // `caption` is a key in `lang/<CODE>/character.txt`.
        // The name row, which holds three things in one cell instead of one: the name, the
        // hardcore mark and the revive box. A row of its own for each would push the whole
        // left column down by two lines on every character, including the ones that have
        // neither — and `AddHeader` puts exactly two controls in a two-column table.
        void AddNameHeader(TableLayoutPanel table)
        {
            Label title = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 5, 3, 2) };
            Lang.Bind(title, "character.name");
            table.Controls.Add(title);
            firstColumn.Add(title);

            nameLabel.AutoSize = true;
            nameLabel.Margin = new Padding(3, 5, 3, 2);
            nameLabel.Font = new Font(Font, FontStyle.Bold);

            hardcoreLabel.AutoSize = true;
            hardcoreLabel.Margin = new Padding(10, 5, 3, 2);
            hardcoreLabel.Font = new Font(Font, FontStyle.Bold);
            hardcoreLabel.ForeColor = Color.Firebrick;
            hardcoreLabel.Visible = false;
            Lang.Bind(hardcoreLabel, "character.hardcore");

            revive.AutoSize = true;
            revive.Margin = new Padding(10, 3, 3, 2);
            revive.Visible = false;
            Lang.Bind(revive, "character.revive");
            tip.SetToolTip(revive, Lang.T("character.revive.tip"));

            // THE BOX GOES UNDER THE NAME, NOT AFTER IT, and that is a layout measurement,
            // not taste. All three in one line made the left table wider than the gap the
            // right column stands in, the outer flow wrapped, and the whole right column —
            // points, money, difficulties — dropped below the fold behind a scrollbar. Seen
            // on the dead save, with the live one still fine, because only the dead one shows
            // the box. Under the name the cell is no wider than "name + mark", and both
            // columns stay side by side.
            FlowLayoutPanel first = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0)
            };
            first.Controls.Add(nameLabel);
            first.Controls.Add(hardcoreLabel);

            FlowLayoutPanel row = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0)
            };
            row.Controls.Add(first);
            row.Controls.Add(revive);
            table.Controls.Add(row);

            revive.CheckedChanged += delegate
            {
                if (loading || character == null || !revive.Checked) return;
                // Ticking the box is the whole action: the death is cleared at once and the
                // box goes away with the mark it belonged to, exactly as it would if the save
                // had been loaded alive. Unticking it would have to put the death back, and
                // there is nothing to put back — the count was one.
                character.Stats.Deaths = 0;
                reviveOn = false;
                revive.Visible = false;
                Raise();
            };
        }

        void AddHeader(TableLayoutPanel table, string caption, Label value)
        {
            Label title = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 5, 3, 2) };
            Lang.Bind(title, caption);
            table.Controls.Add(title);
            firstColumn.Add(title);
            value.AutoSize = true;
            value.Margin = new Padding(3, 5, 3, 2);
            value.Font = new Font(Font, FontStyle.Bold);
            table.Controls.Add(value);
        }

        void AddSeparator(TableLayoutPanel table)
        {
            table.Controls.Add(new Label { Text = "", Height = 6, AutoSize = false, Width = 10 });
            table.Controls.Add(new Label { Text = "", Height = 6, AutoSize = false, Width = 10 });
        }

        // `caption` is a key in `lang/<CODE>/character.txt`.
        void AddField(TableLayoutPanel table, string key, string caption, decimal min, decimal max)
        {
            Label label = new Label { AutoSize = true, Margin = new Padding(3, 5, 3, 2) };
            Lang.Bind(label, caption);
            captions[key] = label;
            table.Controls.Add(label);
            firstColumn.Add(label);
            NumericUpDown box = new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Width = 104,
                ThousandsSeparator = true
            };
            box.ValueChanged += delegate
            {
                if (loading) return;
                Apply(key, box.Value);
                if (key == "level") KeepExperience((int)box.Value);
            };
            // A NumericUpDown only turns its text into a number when it loses the
            // focus, so typing a level and looking at the experience beside it
            // showed nothing until you clicked somewhere else. Two seconds of
            // quiet — or Enter — count as "done typing".
            box.TextChanged += delegate { if (!loading) RestartCommit(); };
            box.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = true; e.SuppressKeyPress = true;
                CommitTyped();
            };
            fields[key] = box;
            table.Controls.Add(box);
        }

        // Applying what was typed without waiting for the focus to go elsewhere.
        readonly Timer commitTimer = new Timer { Interval = 2000 };

        void RestartCommit() { commitTimer.Stop(); commitTimer.Start(); }

        // For the automated check: is there typing waiting to be applied.
        public bool CommitPending { get { return commitTimer.Enabled; } }

        // Reading Value is what makes the control parse its own text: the getter
        // calls ValidateEditText when the user has been typing, and that raises
        // ValueChanged, which is where everything else already hangs. So this
        // needs no second copy of the apply logic.
        public void CommitTyped()
        {
            commitTimer.Stop();
            if (character == null) return;
            foreach (KeyValuePair<string, NumericUpDown> kv in fields)
            { decimal ignored = kv.Value.Value; }
        }

        // The level in the save is not what the game believes: it recomputes the
        // level from the experience the moment the character does anything. Set
        // level 100 with no experience and the game drops the character back to
        // the first level — so raising the level has to raise the experience with
        // it, to the least the game will accept.
        //
        // The threshold comes from the game's own equation,
        // records/creatures/pc/… experienceLevelEquation:
        //
        //     E(l) = ((((l³)^1.16) × 32) + (l² × 300)) × 0.1 + 36
        //
        // and the experience needed to *be* level L is E(L−1). Checked on the
        // saves the game itself wrote: Mivan is level 14 with 30 587, and
        // E(13) = 29 186 ≤ 30 587 < E(14) = 37 082; both hundredth-level
        // characters hold exactly 28 475 316 against E(99) = 28 475 329 — thirteen
        // apart out of twenty-eight million, which is the game keeping it in a
        // narrower float than we do.
        public static long ExperienceFor(int level)
        {
            if (level <= 1) return 0;
            double l = level - 1;
            return (long)Math.Floor(((Math.Pow(l * l * l, 1.16) * 32) + (l * l * 300)) * 0.1 + 36);
        }

        // Use the game's `experienceLevelEquation` from
        // `records/creatures/pc/playerlevels.dbr`; `playerLevel` is L-1.
        // Fall back to the built-in formula when the data is unavailable or invalid.
        public static long ExperienceFor(GameData game, int level)
        {
            if (level <= 1) return 0;
            string eq = Combat.PlayerLevelsEquation(game, "experienceLevelEquation");
            double v;
            if (eq != null && Equation.TryEval(eq, delegate (string name)
                {
                    return string.Equals(name, "playerLevel", StringComparison.OrdinalIgnoreCase) ? level - 1 : 0;
                }, out v))
                return (long)Math.Floor(v);
            if (eq != null)
                Combat.NoteFallback("records/creatures/pc/playerlevels.dbr: experienceLevelEquation (could not parse)");
            return ExperienceFor(level);
        }

        // The experience has to land inside the band of the level, not merely
        // above its floor. Raising it was already here; lowering was not, and that
        // is the half that bites: drop a level 100 character to 50 and the game
        // reads twenty-eight million experience and puts them straight back at
        // 100. Both ends, or the level does not stick.
        //
        // Outside the band it is set to the **floor**, never to the ceiling. Our
        // equation is not the game's to the unit — _Relo was written by the game
        // itself at level 100 with 28 475 316 while we compute 28 475 329, thirteen
        // higher — and if ours runs a shade high, the floor is still inside the
        // game's band while the ceiling could already be the next level up. An
        // experience already inside the band is left exactly as the game wrote it.
        void KeepExperience(int level)
        {
            NumericUpDown xp;
            if (character == null || !fields.TryGetValue("experience", out xp)) return;
            long low = ExperienceFor(game, level);
            long high = ExperienceFor(game, level + 1) - 1;      // still this level at that much
            if (high < low || high > int.MaxValue) high = int.MaxValue;
            long now = (long)xp.Value;
            if (now >= low && now <= high) return;
            loading = true;
            xp.Value = Math.Max(xp.Minimum, Math.Min(xp.Maximum, low));
            loading = false;
            Apply("experience", xp.Value);
        }

        void SetEnabled(bool on)
        {
            elite.Enabled = on;
            ultimate.Enabled = on;
            freeEdit.Enabled = on;
            // Importing needs a character to import INTO, and the game's own files to
            // match the build against.
            importButton.Enabled = on && game != null;
            ApplyLock();
        }

        // What an attribute is and what it buys, in the game's own terms. Every
        // coefficient here is one we checked against the game's own tooltip on
        // two characters; see MECHANICS.md section 14v. Nothing is guessed.
        //
        // Per-point increments and divisors come from `playerlevels.dbr`,
        // `combatformulas`, and `Combat.AttributeDivisor`. Health and energy
        // regeneration values remain fixed because the game data has no fields
        // for them.
        void Explain()
        {
            if (character == null) return;
            double strUnit = Combat.PlayerLevels(game, "strengthIncrement", 8);
            double dexUnit = Combat.PlayerLevels(game, "dexterityIncrement", 8);
            double intUnit = Combat.PlayerLevels(game, "intelligenceIncrement", 8);
            double physLife = Combat.PlayerLevels(game, "lifeIncrement", 20) / strUnit;
            double cunLife = Combat.PlayerLevels(game, "lifeIncrementDexterity", 8) / dexUnit;
            double spiLife = Combat.PlayerLevels(game, "lifeIncrementIntelligence", 12) / intUnit;
            double spiMana = Combat.PlayerLevels(game, "manaIncrement", 16) / intUnit;
            double da = Combat.AbilityPerAttribute(game, false);
            double oa = Combat.AbilityPerAttribute(game, true);
            Attribute(0, "physique", Lang.T("character.physique"),
                      Lang.T("character.needs.physique"),
                      new[] { Lang.T("character.perPoint.life", N(physLife)),
                              Lang.T("character.perPoint.defense", N(da)),
                              Lang.T("character.perPoint.lifeRegen", N(0.04)) },
                      new[] { physLife, da, 0.04 }, null,
                      Combat.PcAttribute(game, "characterStrength"), strUnit);
            Attribute(1, "cunning", Lang.T("character.cunning"),
                      Lang.T("character.needs.cunning"),
                      new[] { Lang.T("character.perPoint.life", N(cunLife)),
                              Lang.T("character.perPoint.offense", N(oa)) },
                      new[] { cunLife, oa },
                      new[] { new KeyValuePair<string, double>(Lang.T("character.divided.physical"),
                                                               Combat.AttributeDivisor(game, false, false)),
                              new KeyValuePair<string, double>(Lang.T("character.divided.bleed"),
                                                               Combat.AttributeDivisor(game, false, true)) },
                      Combat.PcAttribute(game, "characterDexterity"), dexUnit);
             // The save also keeps two values beside the attributes. Their meaning
             // is unresolved; the tooltip reports them without inventing a formula.
            string kept = Lang.T("character.keptInSave");
            tip.SetToolTip(fields["health"], kept);
            tip.SetToolTip(fields["energy"], kept);
            if (captions.ContainsKey("health")) tip.SetToolTip(captions["health"], kept);
            if (captions.ContainsKey("energy")) tip.SetToolTip(captions["energy"], kept);

            Attribute(2, "spirit", Lang.T("character.spirit"),
                      Lang.T("character.needs.spirit"),
                      new[] { Lang.T("character.perPoint.life", N(spiLife)),
                              Lang.T("character.perPoint.energy", N(spiMana)),
                              Lang.T("character.perPoint.energyRegen", N(0.01)) },
                      new[] { spiLife, spiMana, 0.01 },
                      new[] { new KeyValuePair<string, double>(Lang.T("character.divided.magic"),
                                                               Combat.AttributeDivisor(game, true, false)),
                              new KeyValuePair<string, double>(Lang.T("character.divided.dots"),
                                                               Combat.AttributeDivisor(game, true, true)),
                              new KeyValuePair<string, double>(Lang.T("character.divided.energyRegen"), 384) },
                      Combat.PcAttribute(game, "characterIntelligence"), intUnit);
        }

        static readonly string[] FlatField = { "characterStrength", "characterDexterity", "characterIntelligence" };

        void Attribute(int which, string key, string title, string what,
                       string[] perPoint, double[] rate,
                       KeyValuePair<string, double>[] divided,
                       double baseValue, double unit)
        {
            double own = which == 0 ? character.Physique : which == 1 ? character.Cunning : character.Spirit;
            double flat = 0, percent = 0;
            if (bonuses != null)
            {
                TotalsEntry e;
                if (bonuses.Fields.TryGetValue(FlatField[which], out e)) flat = e.Value;
                if (bonuses.Fields.TryGetValue(FlatField[which] + "Modifier", out e)) percent = e.Value;
            }
            // Exactly the game's arithmetic: flat first, then the percentage over
            // the sum, and the fraction is dropped.
            double now = Math.Floor((own + flat) * (1 + percent / 100));
            // One attribute point is eight units and the base value is fifty;
            // both values come from `playerlevels.dbr` and `malepc01.dbr`.
            int spent = (int)Math.Round((own - baseValue) / (unit > 0 ? unit : 8));

            System.Text.StringBuilder s = new System.Text.StringBuilder();
            s.AppendLine(Lang.T("character.tip.now", title, N(now)));
            s.AppendLine("  " + Lang.T("character.tip.own", N(own), spent));
            if (flat != 0 || percent != 0)
                s.AppendLine("  " + Lang.T("character.tip.fromGear",
                                           (flat != 0 ? "+" + N(flat) : "") +
                                           (flat != 0 && percent != 0 ? ", " : "") +
                                           (percent != 0 ? "+" + N(percent) + "%" : "")));
            s.AppendLine();
            s.AppendLine(Lang.T("character.tip.gives", N(baseValue)));
            for (int i = 0; i < perPoint.Length; i++)
                s.AppendLine("  " + perPoint[i] + "  =  " + N((now - baseValue) * rate[i]));
            if (divided != null)
            {
                s.AppendLine();
                s.AppendLine(Lang.T("character.tip.percents", N(baseValue)));
                foreach (KeyValuePair<string, double> d in divided)
                    s.AppendLine("  " + Lang.T("character.tip.percentLine", N(now / d.Value * 100), d.Key) +
                                 "   (" + N(now) + " / " + (int)d.Value + ")");
            }
            s.AppendLine();
            s.Append(what);

            string text = s.ToString();
            tip.SetToolTip(fields[key], text);
            Label c;
            if (captions.TryGetValue(key, out c)) tip.SetToolTip(c, text);
        }

        static string N(double v)
        {
            return Math.Abs(v - Math.Round(v)) < 0.005
                ? ((long)Math.Round(v)).ToString()
                : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Show(Character c, string fileName, string className, string bagsSummary)
        {
            character = c;
            loading = true;
            try
            {
                nameLabel.Text = c.Name;
                hardcoreOn = c.Hardcore != 0;
                reviveOn = BreakHardcoreGate ? hardcoreOn : hardcoreOn && c.Stats.Deaths > 0;
                hardcoreLabel.Visible = hardcoreOn;
                revive.Checked = false;
                revive.Visible = reviveOn;
                classLabel.Text = className;
                fileLabel.Text = fileName;
                bagsLabel.Text = bagsSummary;

                Set("level", c.Level);
                Set("experience", c.Experience);
                Set("physique", (decimal)c.Physique);
                Set("cunning", (decimal)c.Cunning);
                Set("spirit", (decimal)c.Spirit);
                Set("health", (decimal)c.Health);
                Set("energy", (decimal)c.Energy);
                Set("attributePoints", c.AttributePoints);
                Set("skillPoints", c.SkillPoints);
                Set("devotionPoints", c.DevotionPoints);
                Set("totalDevotion", c.TotalDevotion);
                Set("money", c.Money);
                Set("tributes", c.Tributes);

                elite.Checked = c.GreatestDifficultyCompleted >= 1;
                ultimate.Checked = c.GreatestDifficultyCompleted >= 2;
                SetEnabled(true);
            }
            finally { loading = false; }
        }

        void Set(string key, decimal value)
        {
            NumericUpDown box = fields[key];
            box.Value = Math.Max(box.Minimum, Math.Min(box.Maximum, value));
        }

        // For the automated check: the same three things a person does to a box —
        // set it, type into it, read it — without a window on the screen.
        public void SetFieldValue(string key, decimal value)
        { NumericUpDown box; if (fields.TryGetValue(key, out box)) box.Value = value; }

        public void SetFieldText(string key, string text)
        { NumericUpDown box; if (fields.TryGetValue(key, out box)) box.Text = text; }

        public decimal FieldValue(string key)
        { NumericUpDown box; return fields.TryGetValue(key, out box) ? box.Value : 0; }

        void Apply(string key, decimal value)
        {
            if (character == null) return;
            switch (key)
            {
                case "level": character.Level = (int)value; break;
                case "experience": character.Experience = (int)value; break;
                case "physique": character.Physique = (float)value; break;
                case "cunning": character.Cunning = (float)value; break;
                case "spirit": character.Spirit = (float)value; break;
                case "health": character.Health = (float)value; break;
                case "energy": character.Energy = (float)value; break;
                case "attributePoints": character.AttributePoints = (int)value; break;
                case "skillPoints": character.SkillPoints = (int)value; break;
                case "devotionPoints": character.DevotionPoints = (int)value; break;
                case "totalDevotion": character.TotalDevotion = (int)value; break;
                case "money": character.Money = (int)value; break;
                case "tributes": character.Tributes = (int)value; break;
            }
            Raise();
        }

        void ApplyDifficulty()
        {
            if (character == null) return;
            character.GreatestDifficultyCompleted = ultimate.Checked ? (byte)2 : elite.Checked ? (byte)1 : (byte)0;
            Raise();
        }

        void Raise()
        {
            EventHandler h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }
    }
}
