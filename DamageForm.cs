// The damage model, in its own window.
//
// It is built in layers so each one can be checked against the game on its own:
// first the character's summed bonuses (this tab — compare with the character
// sheet in the game), then the target, then the damage itself. Nothing here
// invents a number: what cannot yet be worked out is said in words instead.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public class DamageForm : Form
    {
        readonly GameData game;
        readonly Character character;

        public DamageForm(GameData game, Character character)
        {
            this.game = game;
            this.character = character;
            this.chosen = ActiveSkills.Of(character == null ? null : character.Name);

            AppIcon.Apply(this);
            // Tab, button, and list labels come from `lang/<CODE>/damage.txt`.
            Text = Lang.T("damage.title", character.Name);
            Width = 900;
            Height = 820;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(24, 22, 20);

            TabControl tabs = new TabControl { Dock = DockStyle.Fill };
            // Combat simulation is a separate tab and shares target and loadout state
            // with the other damage tabs.
            tabs.TabPages.Add(TimelinePage());
            tabs.TabPages.Add(TotalsPage());
            tabs.TabPages.Add(HitPage());
            tabs.TabPages.Add(TargetPage());
            Controls.Add(tabs);
            FormClosed += delegate { tip.Dispose(); };
        }

        TabPage TotalsPage()
        {
            TabPage page = new TabPage(Lang.T("damage.tab.totals"));
            page.BackColor = Color.FromArgb(20, 19, 18);

            CharacterTotals totals = Sum();
            List<TipLine> lines = new List<TipLine>();

            lines.Add(new TipLine(Lang.T("damage.totals.summary", totals.WornItems, totals.SetBonuses,
                                         totals.PassiveSkills, totals.Toggles, totals.DevotionStars), TipKind.Headline));
            // Range explanations are kept in README; this view shows the values.
            lines.Add(new TipLine());

            foreach (StatGroups.Group grp in StatGroups.Split(totals.Fields.Keys))
            {
                List<TipLine> body;
                if (grp.Key == StatGroups.Resistances) body = Resistances(totals);
                else
                {
                    body = new List<TipLine>();
                    // Use `AsPlayerRecord`; control effects are expanded into component
                    // fields and have no separate row.
                    ItemViewFactory.RenderStats(game, CharacterTotals.AsPlayerRecord(totals.Fields, grp.Fields),
                                                body, 1, 0);
                    AddSpread(body, totals);
                }
                if (body.Count == 0) continue;
                lines.Add(new TipLine(StatGroups.Title(game, grp.Key), TipKind.Section));
                lines.AddRange(body);
                // Armor by body zone, matching the game panel: item armor is assigned
                // to its zone, while other armor applies to all zones.
                if (grp.Key == StatGroups.Defence)
                {
                    ArmourZones z = ArmourZones.Of(game, totals);
                    if (z.Any)
                    {
                        // Keep the game-panel column order: zone, hit share, armor, absorption.
                        lines.Add(new TipLine(Lang.T("damage.totals.armourRating", Fmt(Math.Round(z.Rating))),
                                              TipKind.Table, 1));
                        for (int i = 0; i < ArmourZones.Count; i++)
                            lines.Add(new TipLine(Pad(ArmourZones.Name(game, i), 8) +
                                                  Pad(Fmt(z.Weight[i]) + "%", 6) +
                                                  Pad(Fmt(z.Zone[i]), 7) +
                                                  Lang.T("damage.totals.absorb", Fmt(z.Absorb)),
                                                  TipKind.Table, 2));
                        lines.Add(new TipLine(Lang.T("damage.totals.armourNote", Fmt(Math.Round(z.Shared)), Fmt(z.Percent)),
                                              TipKind.Subtle, 2));
                    }
                    // Block chance and blocked damage use the game's block equation.
                    if (z.HasBlock)
                    {
                        lines.Add(new TipLine(Lang.T("damage.totals.block", Fmt(z.BlockChance), Fmt(z.BlockDamage)),
                                              TipKind.Table, 1));
                        lines.Add(new TipLine(Lang.T("damage.totals.blockNote"), TipKind.Subtle, 2));
                    }
                }
                lines.Add(new TipLine());
            }

            if (totals.PetFields.Count > 0)
            {
                List<TipLine> body = new List<TipLine>();
                List<string> keys = new List<string>(totals.PetFields.Keys);
                keys.Sort(StringComparer.Ordinal);
                // Pet resistance caps are applied by `AsPetRecord`; render excess
                // above the cap as a separate row.
                List<string> capped;
                ItemViewFactory.RenderStats(game, CharacterTotals.AsPetRecord(totals.PetFields, keys, out capped),
                                            body, 1, 0);
                if (body.Count > 0)
                {
                    // Use the game's pet-bonus heading, with the language file as fallback.
                    string petsTitle = game == null ? null : game.Tag("tagCharHeaderPets");
                    lines.Add(new TipLine(string.IsNullOrEmpty(petsTitle) ? Lang.T("damage.totals.pets") : petsTitle,
                                          TipKind.Section));
                    lines.Add(new TipLine(Lang.T("damage.totals.petsNote", totals.PetSources), TipKind.Subtle, 1));
                    lines.AddRange(body);
                    if (capped.Count > 0)
                    {
                        List<string> said = new List<string>();
                        foreach (string one in capped)
                        {
                            string[] c = one.Split('\t');
                            said.Add(Lang.T("damage.totals.petCapItem", CharacterTotals.PetCapWord(game, c[0]), c[1], c[2]));
                        }
                        lines.Add(new TipLine(Lang.T("damage.totals.petCapNote", CharacterTotals.BaseResistCap,
                                                     string.Join(", ", said.ToArray())), TipKind.Subtle, 1));
                    }
                    lines.Add(new TipLine());
                }
            }

            // The honest part: everything the sum does not yet know about.
            lines.Add(new TipLine(Lang.T("damage.totals.missing"), TipKind.Section));
            AddNote(lines, Lang.T("damage.totals.missingProcs") +
                           (totals.SkippedProcs.Count > 0
                                ? " " + Lang.T("damage.totals.skipped", Join(totals.SkippedProcs)) : ""));
            AddNote(lines, Lang.T("damage.totals.missingPressed") +
                           (totals.Skipped.Count > 0 ? " " + Lang.T("damage.totals.skipped", Join(totals.Skipped)) : ""));
            if (totals.SkippedOff.Count > 0)
                AddNote(lines, Lang.T("damage.totals.toggledOff", Join(totals.SkippedOff)));
            // Distinguish the user-selected loadout from the saved loadout explicitly.
            if (totals.ChosenByHand.Count > 0)
                lines.Add(new TipLine(Lang.T("damage.totals.chosenByHand", Join(totals.ChosenByHand)), TipKind.Subtle, 1));
            AddNote(lines, Lang.T("damage.totals.missingConversion"));
            AddNote(lines, Lang.T("damage.totals.missingScaling"));
            AddNote(lines, Lang.T("damage.totals.missingCap"));
            AddNote(lines, Lang.T("damage.totals.missingDifficulty"));

            bool anyMax = false;
            foreach (KeyValuePair<string, TotalsEntry> kv in totals.Fields)
                if (kv.Value.ByMax && kv.Value.Sources > 1) anyMax = true;
            if (anyMax)
            {
                lines.Add(new TipLine());
                lines.Add(new TipLine(Lang.T("damage.totals.notStacked"), TipKind.Section));
                AddNote(lines, Lang.T("damage.totals.notStackedNote"));
                foreach (KeyValuePair<string, TotalsEntry> kv in totals.Fields)
                {
                    if (!kv.Value.ByMax || kv.Value.Sources <= 1) continue;
                    // THE GAME'S OWN WORDING, not the field name out of its database.
                    // It used to print `kv.Key` here — "offensiveTotalResistanceReductionAbsoluteMin:
                    // sources 2, strongest 20", which tells the reader nothing about which
                    // of the three kinds of resistance reduction is being talked about.
                    lines.Add(new TipLine(Lang.T("damage.totals.notStackedItem",
                                                 ItemViewFactory.FieldLabel(game, kv.Key),
                                                 kv.Value.Sources, kv.Value.Largest),
                                          TipKind.Subtle, 1));
                }
            }

            TotalsLines = lines;
            Totals = totals;
            TipView view = new TipView(lines) { Dock = DockStyle.Fill };
            view.Hovered += delegate (object s, TipHoverEventArgs e) { ShowTerms(e); };

            // Button for selecting active skills; keep it separated from the tabs.
            Panel top = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = page.BackColor };
            Button pick = new Button
            {
                Text = Lang.T("damage.pickActive"),
                Left = 8, Top = 12, Width = 220, Height = 26,
                ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(38, 35, 32), FlatStyle = FlatStyle.Flat,
            };
            Label how = new Label
            {
                Left = 236, Top = 17, AutoSize = true, ForeColor = Color.DimGray,
                Text = chosen.Count == 0
                     ? Lang.T("damage.activeFromSave")
                     : Lang.T("damage.activeChosen", chosen.Count),
            };
            Button pets = new Button
            {
                Text = Lang.T("damage.pickPets"),
                Left = 236 + TextRenderer.MeasureText(how.Text, how.Font).Width + 16,
                Top = 12, Width = 180, Height = 26,
                ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(38, 35, 32), FlatStyle = FlatStyle.Flat,
            };
            // Nearby pets can contribute allied auras. The checkbox is off by default
            // because the panel normally models the player alone.
            CheckBox near = new CheckBox
            {
                Text = Lang.T("damage.petsNear"),
                Left = pets.Left + pets.Width + 12, Top = 15, AutoSize = true,
                ForeColor = Color.Gainsboro, Checked = petsNear,
            };
            near.CheckedChanged += delegate { petsNear = near.Checked; RebuildTabs(); };
            top.Controls.AddRange(new Control[] { pick, how, pets, near });
            pick.Click += delegate { PickActive(); };
            pets.Click += delegate { PickPets(); };

            page.Controls.Add(view);
            page.Controls.Add(top);
            return page;
        }

        // User-selected active skills, kept outside the damage calculation state.
        readonly ActiveSkills chosen;

        // All tabs use this selection so their totals remain consistent.
        // Nearby pets are a comma-separated list; `*` selects all marked pets.
        //
        // The game shows nearby pet auras, but diagnostics model the player alone by
        // default. `--petauras=` supplies nearby pets for screenshots.
        public static string PetAurasByName;

        // Nearby-pets checkbox; off by default to match the standalone player model.
        bool petsNear;

        CharacterTotals Sum()
        {
            ExactRolls seeded = new ExactRolls();
            seeded.ReplaceWithSeed(game, character);
            CharacterTotals t = CharacterTotals.Of(game, character, seeded, chosen);
            string near = !string.IsNullOrEmpty(PetAurasByName) ? PetAurasByName : petsNear ? "*" : null;
            if (!string.IsNullOrEmpty(near))
                TimelinePets.ApplyAuras(game, character, t, chosen, near);
            return t;
        }

        // Pet selection dialog and immediate tab rebuild.
        void PickActive()
        {
            using (ActiveSkillsForm f = new ActiveSkillsForm(game, character, chosen))
            {
                f.ShowDialog(this);
                if (!f.Changed) return;
            }
            RebuildTabs();
        }

        // Pet pool uses the same summon-skill key as active-skill selection.
        void PickPets()
        {
            using (PetsForm f = new PetsForm(game, character, chosen))
            {
                f.ShowDialog(this);
                if (!f.Changed) return;
            }
            RebuildTabs();
        }

        // Rebuild only tabs affected by active-skill or pet selection. Preserve the
        // selected attack and clear list controls before repopulating them.
        public void RebuildTabs()
        {
            TabControl tabs = null;
            foreach (Control one in Controls) { tabs = one as TabControl; if (tabs != null) break; }
            if (tabs == null || tabs.TabPages.Count < 2) return;
            int keepTab = tabs.SelectedIndex;
            int keepHit = skillBox.SelectedIndex;
            // Rebuild totals and hit-damage tabs; simulation remains first.
            tabs.TabPages.RemoveAt(2);
            tabs.TabPages.RemoveAt(1);
            tabs.TabPages.Insert(1, HitPage());
            tabs.TabPages.Insert(1, TotalsPage());
            if (keepHit > 0 && keepHit < skillBox.Items.Count) skillBox.SelectedIndex = keepHit;
            if (keepTab >= 0 && keepTab < tabs.TabPages.Count) tabs.SelectedIndex = keepTab;
        }

        // Counts used by the rebuild test for attack and difficulty choices.
        public int AttackChoices { get { return skillBox.Items.Count; } }
        public int DifficultyChoices { get { return enemyDiff.Items.Count; } }

        CharacterTotals Totals;
        readonly ItemTooltip tip = new ItemTooltip();

        // What a summed line is made of, term by term. Without this the numbers
        // cannot be checked against the game: it is the difference between "our
        // sum disagrees" and "our sum counts this ring twice".
        void ShowTerms(TipHoverEventArgs e)
        {
            if (e.Line == null || e.Line.Explains == null || Totals == null) { tip.Hide(); return; }
            ItemView v = new ItemView();
            v.Name = e.Line.Text;
            v.Details.Add(new TipLine(e.Line.Text, TipKind.Name));

            bool anything = false;
            foreach (string field in e.Line.Explains)
            {
                TotalsEntry entry;
                bool pet = false;
                if (!Totals.Fields.TryGetValue(field, out entry))
                {
                    if (!Totals.PetFields.TryGetValue(field, out entry)) continue;
                    pet = true;
                }
                anything = true;
                v.Details.Add(new TipLine());
                // Heading of a term list: the game's own wording for the field, not its
                // name in the database. Was `field`, i.e. "offensiveFireMin".
                v.Details.Add(new TipLine(ItemViewFactory.FieldLabel(game, field) +
                                          (pet ? "   " + Lang.T("damage.terms.pet") : ""), TipKind.Section));
                v.Details.Add(new TipLine(
                    entry.ByMax
                        ? Lang.T("damage.terms.byMax", entry.Sources)
                        : Lang.T(entry.Sources == 1 ? "damage.terms.summedOne" : "damage.terms.summedMany", entry.Sources),
                    TipKind.Subtle));
                foreach (KeyValuePair<string, double> term in entry.Terms)
                {
                    bool winner = entry.ByMax && Math.Abs(term.Value - entry.Largest) < 0.0001;
                    v.Details.Add(new TipLine(
                        Fmt(term.Value) + "   " + term.Key + (winner ? "   " + Lang.T("damage.terms.winner") : ""),
                        entry.ByMax && !winner ? TipKind.Subtle : TipKind.Bonus, 1));
                }
                v.Details.Add(new TipLine(Lang.T("damage.terms.total", Fmt(entry.Value)), TipKind.Headline, 1));
            }
            if (!anything) { tip.Hide(); return; }
            tip.Owner = this;
            tip.ShowFor(v, e.ScreenPoint);
        }

        // The sum written as the span it can be. The game adds up what actually
        // rolled on each item, and no file holds those numbers — but the total has
        // to fall between "everything rolled worst" and "everything rolled best".
        // So the game's own figure can still be checked: inside the span means we
        // agree, outside means we have a mistake.
        //
        // A min-max line contains two rolled values. Bracket only the first
        // value so the displayed range remains unambiguous.
        // when 303–414 belonged to the 248 and the 962 had no bracket at all.
        static void AddSpread(List<TipLine> lines, CharacterTotals totals)
        {
            foreach (TipLine l in lines)
            {
                if (l.Explains == null || l.Explains.Length == 0) continue;
                string low = Bracket(totals, l.Explains[0]);
                string high = l.Paired && l.Explains.Length > 1 ? Bracket(totals, l.Explains[1]) : null;
                if (low == null && high == null) continue;
                l.Text += "   " + (low ?? Lang.T("damage.totals.exact")) + (high != null ? " – " + high : "");
            }
        }

        // "[303–414]", or null when the field does not roll at all.
        static string Bracket(CharacterTotals totals, string field)
        {
            TotalsEntry e;
            if (!totals.Fields.TryGetValue(field, out e)) return null;
            if (Math.Abs(e.High - e.Low) < 0.5) return null;
            return "[" + Fmt(e.Low) + "–" + Fmt(e.High) + "]";
        }

        // Resistances the way the game shows them: the value it actually uses,
        // and the surplus in brackets. The cap is
        // 80% plus whatever raises it, and the surplus is not wasted: bosses that
        // shred resistances eat into it first.
        List<TipLine> Resistances(CharacterTotals totals)
        {
            List<TipLine> made = new List<TipLine>();
            foreach (KeyValuePair<string, string> kv in StatGroups.ResistFields(game))
            {
                int cap = totals.CapFor(kv.Key);
                double raw = totals.ResistValue(kv.Key);
                double used = Math.Min(raw, cap);
                double over = raw - used;
                double lo, hi;
                totals.ResistSpread(kv.Key, out lo, out hi);
                // Use the game's over-cap resistance template and preserve
                // preformatted numeric values. Without a surplus, use the base form.
                string head = over > 0.005 && game != null
                            ? game.FormatTag("tagOverCapResist", kv.Value, Fmt(used), Fmt(over)) : null;
                if (string.IsNullOrEmpty(head))
                    head = kv.Value + " " + Fmt(used) + "%" +
                           (over > 0.005 ? " " + Lang.T("damage.totals.overCap", Fmt(over)) : "");
                string text = head +
                              (cap != CharacterTotals.BaseResistCap ? "   " + Lang.T("damage.totals.resistCap", cap) : "") +
                              (hi - lo > 0.5 ? "   [" + Fmt(lo) + "–" + Fmt(hi) + "]" : "");
                TipLine line = new TipLine(text, TipKind.Bonus, 1);
                line.Explains = StatGroups.IsElemental(kv.Key)
                    ? new[] { kv.Key, "defensiveElementalResistance", kv.Key + "MaxResist",
                              "defensiveElementalResistanceMaxResist", "defensiveAllMaxResist" }
                    : new[] { kv.Key, kv.Key + "MaxResist", "defensiveAllMaxResist" };
                made.Add(line);
            }
            return made;
        }

        // Keep armor columns aligned using fixed field widths rather than pixels.
        // Select rows by their semantic field marker (`TipLine.Explains`), not text.
        static bool Explains(TipLine line, string field)
        {
            if (line == null || line.Explains == null) return false;
            foreach (string one in line.Explains)
                if (string.Equals(one, field, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string Pad(string s, int width)
        { return s.Length >= width ? s + " " : s + new string(' ', width - s.Length); }

        static string Fmt(double v)
        {
            return Math.Abs(v - Math.Round(v)) < 0.005
                ? ((long)Math.Round(v)).ToString()
                : v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }

        // The damage of one ordinary attack, by type. Every step of it was
        // checked against the game on a bench character — see MECHANICS.md,
        // The lines are intended for numeric comparison with the game panel.
        //
        // The dropdown picks what is being swung: a plain attack, or one of the
        // character's damage skills — the activated skills of both masteries and
        // the ones the gear grants. What changes with a skill is weaponDamagePct,
        // which multiplies the weapon's damage but not the skill's own.
        readonly ComboBox skillBox = new ComboBox
        { DropDownStyle = ComboBoxStyle.DropDownList, Width = 560 };
        List<AttackSkill> attackSkills;
        List<PetAttack> petAttacks;
        TipView hitView;
        // Attach handlers once; pages are rebuilt, but the list controls persist.
        bool hitWired, enemyWired;

        TabPage HitPage()
        {
            TabPage page = new TabPage(Lang.T("damage.tab.hit"));
            page.BackColor = Color.FromArgb(20, 19, 18);

            attackSkills = AttackSkills.Of(game, character);

            // Keep the buttons separated from the tabs.
            Panel top = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = page.BackColor };
            Label what = new Label { Text = Lang.T("damage.hit.what"), Left = 8, Top = 9, AutoSize = true,
                                     ForeColor = Color.DimGray };
            // Place the list after the label and keep the established width.
            skillBox.Left = Math.Max(78, what.Left + TextRenderer.MeasureText(what.Text, what.Font).Width + 6);
            skillBox.Top = 5;
            // Rebuild the list instead of appending to the persistent control.
            // Clear old handlers as well to avoid duplicate processing.
            skillBox.Items.Clear();
            skillBox.Items.Add(Lang.T("damage.hit.plain"));
            foreach (AttackSkill a in attackSkills) skillBox.Items.Add(a);
            // Keep summoned-unit skills in a separate group at the end of the list.
            petAttacks = PetAttack.Of(game, character, chosen);
            foreach (PetAttack p in petAttacks) skillBox.Items.Add(p);
            skillBox.SelectedIndex = 0;
            top.Controls.AddRange(new Control[] { what, skillBox });

            hitView = new TipView(new List<TipLine>()) { Dock = DockStyle.Fill };
            page.Controls.Add(hitView);
            page.Controls.Add(top);
            if (!hitWired) { skillBox.SelectedIndexChanged += delegate { ShowHit(); }; hitWired = true; }
            ShowHit();
            return page;
        }

        // Pick one from the dropdown by number, so a headless run can take the
        // shot of a skill instead of the plain attack.
        public bool ChooseSkill(int index)
        {
            if (index < 0 || index >= skillBox.Items.Count) return false;
            skillBox.SelectedIndex = index;
            return true;
        }

        void ShowHit()
        {
            // Summoned-unit hits use a separate calculation and do not enter
            // the player-hit path in `ShowHit`.
            PetAttack pet = skillBox.SelectedItem as PetAttack;
            if (pet != null) { HitLines = PetHit(pet); hitView.SetLines(HitLines); return; }
            AttackSkill chosen = skillBox.SelectedItem as AttackSkill;
            CharacterTotals t = Totals ?? Sum();
            // Read rolled conversions from generated values and pass them
            // separately to the model; record defaults may differ.
            ExactRolls seeded = new ExactRolls();
            seeded.ReplaceWithSeed(game, character);
            DamageModel model = new DamageModel(game, character, t, chosen, true, seeded);
            // The same skill without the pieces that only hang for a few seconds:
            // that is what the game's panel shows, so it is the number to compare
            // with, and it is worth having both in front of you at once.
            DamageModel bare = chosen != null && chosen.HasTemporary
                             ? new DamageModel(game, character, t, chosen, false, seeded) : null;
            HitLines = Hit(game, model, bare);
            hitView.SetLines(HitLines);
        }


        // Show two value blocks for summoned-unit hits.
        //
        // The first block applies conversion and represents the calculated hit.
        // The second omits conversion and mirrors the game's displayed values.
        // Player weapon, player bonuses, and target modifiers do not apply here.
        List<TipLine> PetHit(PetAttack pet)
        {
            List<TipLine> lines = new List<TipLine>();
            lines.Add(new TipLine(Lang.T("damage.pet.title", pet.SkillName, pet.PetName, PetPool.LevelWord(pet.Level, false)),
                                  TipKind.Headline));
            lines.Add(new TipLine(Lang.T("damage.pet.summonedBy", pet.SummonTitle), TipKind.Subtle, 1));

            CharacterTotals t = Totals ?? Sum();
            List<Move> petWide = pet.ConvMoves;
            // The reference block omits summoned-unit equipment but keeps its
            // intrinsic conversion, which is included by the game display.
            List<TipLine> real = PetPool.SkillLines(game, pet.SkillDbr, pet.Level, pet.Bonus, 1,
                                                    petWide, pet.Scale, pet.OwnConv);
            List<TipLine> plain = PetPool.SkillLines(game, pet.SkillDbr, pet.Level, pet.Bonus, 1,
                                                     null, pet.Scale, pet.OwnConv);

            lines.Add(new TipLine());
            lines.Add(new TipLine(Lang.T("damage.pet.withConversion"), TipKind.Section));
            if (real.Count == 0) lines.Add(new TipLine(Lang.T("damage.pet.noNumbers"), TipKind.Subtle, 1));
            else lines.AddRange(real);

            // Print the reference block only when conversion changes the values.
            if (!Same(real, plain))
            {
                lines.Add(new TipLine());
                lines.Add(new TipLine(Lang.T("damage.pet.asGame"), TipKind.Section));
                lines.AddRange(plain);
                lines.Add(new TipLine(Lang.T("damage.pet.asGameNote"), TipKind.Subtle, 1));
            }

            List<TipLine> conv = PetPool.ConversionLines(game, pet.AllConv, 1);
            if (conv.Count > 0) { lines.Add(new TipLine()); lines.AddRange(conv); }
            lines.Add(new TipLine());
            lines.Add(new TipLine(Lang.T("damage.pet.notYours"), TipKind.Subtle, 1));
            return lines;
        }

        static bool Same(List<TipLine> a, List<TipLine> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i].Text, b[i].Text, StringComparison.Ordinal)) return false;
            return true;
        }

        static List<TipLine> Hit(GameData game, DamageModel model) { return Hit(game, model, null); }

        static List<TipLine> Hit(GameData game, DamageModel model, DamageModel bare)
        {
            List<TipLine> lines = new List<TipLine>();
            string head = model.Skill == null
                        ? (string.IsNullOrEmpty(model.WeaponName)
                               ? Lang.T("damage.hit.plainTitle") : Lang.T("damage.hit.plainWeapon", model.WeaponName))
                        : Lang.T("damage.hit.skillTitle", model.Skill.Name, model.Skill.Source, model.Skill.Level);
            lines.Add(new TipLine(head, TipKind.Headline));
            if (model.Skill != null && !string.IsNullOrEmpty(model.Skill.When))
                lines.Add(new TipLine(model.Skill.When, TipKind.Subtle, 1));
            if (model.Skill != null && !string.IsNullOrEmpty(model.WeaponName))
                lines.Add(new TipLine(Lang.T("damage.hit.weapon", model.WeaponName), TipKind.Subtle, 1));
            if (model.AttacksPerSecond > 0)
                lines.Add(new TipLine(
                    Lang.T("damage.hit.attackSpeed",
                           model.AttacksPerSecond.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                           Fmt(Math.Round(model.AttackSpeedPercent))),
                    TipKind.Subtle, 1));
            // Show casting speed next to attack speed. Channelled skills also
            // use it to calculate their tick interval.
            if (model.CastSpeedPercent > 0)
                lines.Add(new TipLine(Lang.T("damage.hit.castSpeed", Fmt(Math.Round(model.CastSpeedPercent))) +
                    (model.CastSpeedPercent >= model.CastSpeedCap - 0.001
                         ? " " + Lang.T("damage.hit.castCapped", Fmt(Math.Round(model.CastSpeedCap))) : "") +
                    (model.ChannelTick() > 0
                         ? "   " + Lang.T("damage.hit.channel", Fmt(Math.Round(model.ChannelTick(), 3)))
                         : ""),
                    TipKind.Subtle, 1));
            if (model.Lines.Count > 0)
            {
                lines.Add(new TipLine(Lang.T("damage.hit.perHit", Fmt(model.TotalMin), Fmt(model.TotalMax)),
                                      TipKind.Name));
                lines.Add(new TipLine());
                foreach (DamageLine l in model.Lines)
                    lines.Add(new TipLine(HitLineText(l), TipKind.Bonus, 1));
            }
            else lines.Add(new TipLine(Lang.T("damage.hit.none"), TipKind.Subtle, 1));
            if (bare != null)
            {
                lines.Add(new TipLine());
                lines.Add(new TipLine(Lang.T("damage.hit.bare", model.Skill.TemporaryNames,
                                             Fmt(bare.TotalMin), Fmt(bare.TotalMax)),
                                      TipKind.Subtle, 1));
                foreach (DamageLine l in bare.Lines)
                    lines.Add(new TipLine(HitLineText(l), TipKind.Subtle, 2));
            }
            // Hidden in the UI. These values are rolled per item and must be
            // calculated from the generated rolls; item multipliers truncate fractions.
            //
            // lines.Add(new TipLine());
            // lines.Add(new TipLine("Values are generated rolls, not record defaults.", TipKind.Subtle, 1));
            AddHandShare(lines, model);
            AddRetaliation(lines, model);
            AddSkillParts(game, lines, model);
            lines.Add(new TipLine());
            lines.Add(new TipLine(Lang.T("damage.hit.how"), TipKind.Section));
            AddNote(lines, Lang.T("damage.hit.howBase"));
            AddNote(lines, Lang.T("damage.hit.howConversion"));
            AddNote(lines, Lang.T("damage.hit.howMultiplier"));
            // Keep the total-damage component visible: the game combines it
            // with the damage-type percentage in the displayed value.
            AddNote(lines, TotalDamageNote(model));
            AddNote(lines, Lang.T("damage.hit.howAll"));
            // The record already stores damage per second; do not divide it again.
            AddNote(lines, Lang.T("damage.hit.howDots"));
            foreach (string note in model.Notes) AddNote(lines, note);
            return lines;
        }

        // Format one damage type, including the per-second suffix for damage over time.
        static string HitLineText(DamageLine l)
        {
            return (Math.Abs(l.Max - l.Min) < 0.5 ? Fmt(l.Min) : Fmt(l.Min) + " – " + Fmt(l.Max)) +
                   " " + l.Name +
                   (l.OverTime ? "  " + Lang.T("damage.hit.overTime",
                                               string.IsNullOrEmpty(l.SecondsText) ? Fmt(l.Seconds) : l.SecondsText)
                               : "");
        }

        // Include the actual total-damage percentage for comparison with the game panel.
        static string TotalDamageNote(DamageModel model)
        {
            double total = model == null ? 0 : model.TotalDamagePercent;
            string tail = total > 0.0001
                ? Lang.T("damage.hit.howTotalSome", total.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                : Lang.T("damage.hit.howTotalNone");
            return Lang.T("damage.hit.howTotal") + " " + tail;
        }

        // Retaliation damage is a separate block because it is not a normal hit.
        static void AddRetaliation(List<TipLine> lines, DamageModel model)
        {
            if (model.Retaliation.Count == 0) return;
            lines.Add(new TipLine());
            lines.Add(new TipLine(Lang.T("damage.retaliation.title"), TipKind.Section));
            foreach (DamageLine l in model.Retaliation)
                lines.Add(new TipLine(l.Name + "   " +
                                      (Math.Abs(l.Max - l.Min) < 0.5
                                           ? Fmt(l.Min)
                                           : Fmt(l.Min) + " – " + Fmt(l.Max)), TipKind.Bonus, 1));
            AddNote(lines, Lang.T("damage.retaliation.note", Fmt(model.RetaliationPercent)));
            AddNote(lines, Lang.T("damage.retaliation.note2"));
        }

        // Show the weapon contribution for each hand using the game's wording.
        //
        // The skill contribution is shared by both hands; displayed values differ
        // because the weapons differ. Add a second line only when the hit uses it.
        //
        // Read labels from the game data so localization changes remain consistent.
        //
        // The game tooltip may show only the skill's own contribution while the
        // calculation includes all parts. Show both values when they differ.
        static void AddHandShare(List<TipLine> lines, DamageModel model)
        {
            if (model.Skill == null || Math.Abs(model.Skill.WeaponPct) < 0.0001) return;
            if (model.HandMax[0] <= 0.5) return;

            // Show only hands used by the animation. The block mirrors the game
            // tooltip, so its hand count may differ from the damage calculation.
            double pctMain = model.ShownHands == Hands.Off ? 0 : model.Skill.WeaponPct;
            double pctOff = model.HandsKnown && model.ShownHands != Hands.Main
                          ? model.Skill.WeaponPct : 0;
            lines.Add(new TipLine());
            lines.Add(new TipLine(Lang.T("damage.hands.title"), TipKind.Section));
            if (Math.Abs(pctMain) > 0.0001)
                HandLine(lines, model, "SkillWeaponDamageMainhandFormat", pctMain, 0);
            if (Math.Abs(pctOff) > 0.0001)
                HandLine(lines, model, "SkillWeaponDamageOffhandFormat", pctOff, 1);

            bool bothHands = Math.Abs(pctMain) > 0.0001 && Math.Abs(pctOff) > 0.0001;
            if (bothHands)
                AddNote(lines, Lang.T("damage.hands.both", Fmt(pctMain)));
            else if (model.HandsKnown)
                AddNote(lines, Lang.T(Math.Abs(pctOff) > 0.0001 ? "damage.hands.oneOff" : "damage.hands.oneMain"));
            else
                AddNote(lines, Lang.T("damage.hands.single"));

            // The first part is the skill's own contribution; item modifiers are
            // shown separately and are not included in this value.
            double own = model.Skill.Parts.Count > 0
                       ? AttackSkill.Value(model.Skill.Parts[0].Rec, "weaponDamagePct",
                                           model.Skill.Parts[0].Slot) : 0;
            if (own > 0.0001 && Math.Abs(own - model.Skill.WeaponPct) > 0.0001)
            {
                lines.Add(new TipLine(Lang.T("damage.hands.ownShare", Fmt(own)), TipKind.Subtle, 1));
                if (Math.Abs(pctMain) > 0.0001)
                    HandLine(lines, model, "SkillWeaponDamageMainhandFormat", own, 0);
                if (Math.Abs(pctOff) > 0.0001)
                    HandLine(lines, model, "SkillWeaponDamageOffhandFormat", own, 1);
            }
        }

        static void HandLine(List<TipLine> lines, DamageModel model, string tag, double pct, int hand)
        {
            double lo = Math.Round(model.HandMin[hand] * pct / 100);
            double hi = Math.Round(model.HandMax[hand] * pct / 100);
            string text = model.Game == null ? null : model.Game.FormatLocalTag(tag, pct, lo, hi);
            if (string.IsNullOrEmpty(text))
                text = Lang.T(hand == 0 ? "damage.hands.lineMain" : "damage.hands.lineOff", Fmt(pct), Fmt(lo), Fmt(hi));
            // Use a distinct color so hand rows remain visible among other bonuses.
            TipLine made = new TipLine(text, TipKind.Bonus, 1);
            made.Override = TipPalette.Hand;
            lines.Add(made);
        }

        // List the sources that compose the skill on the damage tab.
        //
        // A skill can receive modifiers from its tree, transmuter, equipment,
        // and set bonuses. Keep these sources explicit for verification.
        static void AddSkillParts(GameData game, List<TipLine> lines, DamageModel model)
        {
            if (model.Skill == null || model.Skill.Parts.Count == 0) return;
            lines.Add(new TipLine());
            lines.Add(new TipLine(Lang.T("damage.parts.title"), TipKind.Section));
            // Whether the row represents one of the selected source fields.
            foreach (SkillPart p in model.Skill.Parts)
            {
                // Include the specific hand when the animation provides it.
                lines.Add(new TipLine(p.Who + (p.Temporary ? "   " + Lang.T("damage.parts.temporary") : "") +
                                      (p.WeaponOneHand
                                           ? "   " + Lang.T(p.SecondHand ? "damage.parts.oneHandOff" : "damage.parts.oneHandMain")
                                           : ""),
                                      TipKind.Name, 1));
                double w = AttackSkill.Value(p.Rec, "weaponDamagePct", p.Slot);
                if (Math.Abs(w) > 0.0001)
                    lines.Add(new TipLine(Lang.T("damage.parts.weaponPct", Fmt(w)), TipKind.Bonus, 2));
                for (int n = 0; n <= 6; n++)
                {
                    string tail = n == 0 ? "" : n.ToString();
                    string from = p.Rec.Get("conversionInType" + tail);
                    string to = p.Rec.Get("conversionOutType" + tail);
                    double pct = AttackSkill.Value(p.Rec, "conversionPercentage" + tail, 0);
                    if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || pct <= 0) continue;
                    lines.Add(new TipLine(
                        Lang.T("damage.parts.conversion", Fmt(pct), ItemViewFactory.DamageTypeRu(game, from),
                               ItemViewFactory.DamageTypeRu(game, to)), TipKind.Bonus, 2));
                }
                // Show the part's own values using the same formatter as items.
                //
                // Read values from the same record slot (`p.Slot`) used by the calculation.
                //
                // Weapon damage and conversion are shown above; skip them here
                // to avoid displaying the same contribution twice.
                List<TipLine> own = new List<TipLine>();
                ItemViewFactory.RenderStats(model.Game, p.Rec, own, 2, p.Slot);
                // Filter by semantic fields (`Explains`), not localized text.
                // This keeps the filter valid for summoned-unit wording and translations.
                foreach (TipLine one in own)
                {
                    if (string.IsNullOrEmpty(one.Text)) continue;
                    if (Explains(one, "weaponDamagePct") || ExplainsConversion(one)) continue;
                    lines.Add(one);
                }
                // The source path is hidden in the UI but remains available in
                // diagnostic `SkillsDump` output.
                // lines.Add(new TipLine(p.Rec.Path, TipKind.Subtle, 2));
            }
            foreach (string left in model.Skill.NotCounted)
                lines.Add(new TipLine(Lang.T("damage.parts.notCounted", left), TipKind.Subtle, 1));
        }

        static bool ExplainsConversion(TipLine line)
        {
            if (line == null || line.Explains == null) return false;
            foreach (string one in line.Explains)
                if (one != null && one.StartsWith("conversionPercentage", StringComparison.Ordinal)) return true;
            return false;
        }

        // The text of the hit tab, so a headless run can read it.
        public List<TipLine> HitLines;

        // Every skill the dropdown offers, with what it is made of. Written beside
        // the screenshot so the list can be checked without the window: which
        // skills got in, at what level, with what multiplier, and what was left
        // out of each and why.
        // Diagnostic export; this text is not part of the localized UI.
        #region Self-check — not interface
        public string SkillsDump()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            if (attackSkills == null) return "";
            sb.AppendLine("DAMAGE SKILLS: " + attackSkills.Count);
            foreach (AttackSkill a in attackSkills)
            {
                sb.AppendLine();
                sb.AppendLine(a.Name + "   [" + a.Source + "]   level " + a.Level +
                              (a.WeaponPct != 0 ? "   weaponDamagePct = " + Fmt(a.WeaponPct) : "") +
                              (a.FromItem ? "   (from an item)" : "") +
                              (string.IsNullOrEmpty(a.When) ? "" : "   " + a.When));
                sb.AppendLine("    " + a.Dbr);
                foreach (SkillPart p in a.Parts)
                    sb.AppendLine("    included: " + p.Who + "   " + p.Rec.Path + "   row " + (p.Slot + 1));
                foreach (string left in a.NotCounted) sb.AppendLine("    excluded: " + left);
            }
            return sb.ToString();
        }
        #endregion

        // The enemy: a list built out of the game's own records, and the level to
        // meet him at. A monster has no fixed numbers — its attributes come from
        // equations that take the level, and its armour from a passive skill
        // whose table is indexed by level — so the level is a choice, not a fact.
        readonly ComboBox enemyBox = new ComboBox
        { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
        readonly NumericUpDown enemyLevel = new NumericUpDown
        { Minimum = 1, Maximum = 120, Value = 100, Width = 70 };
        readonly TextBox enemyFind = new TextBox { Width = 200 };
        readonly ComboBox enemyDiff = new ComboBox
        { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        List<Enemy> enemies;
        TipView enemyView;

        // Keep duration and seed explicit so runs are reproducible.
        readonly NumericUpDown runSeconds = new NumericUpDown
        { Minimum = 1, Maximum = 600, Value = 30, Width = 70 };
        readonly NumericUpDown runSeed = new NumericUpDown
        { Minimum = 1, Maximum = 9999, Value = 1, Width = 70 };
        TipView timelineView;
        bool timelineWired;
        // List manually activated skills. Auras are configured separately.
        readonly CheckedListBox pressBox = new CheckedListBox
        {
            Width = 470, Height = 96, CheckOnClick = true,
            ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(28, 26, 24),
            BorderStyle = BorderStyle.FixedSingle,
        };
        readonly List<AttackSkill> pressable = new List<AttackSkill>();
        // Non-damaging active buffs precede damaging pressable skills.
        readonly List<string> buffs = new List<string>();
        // Receives periodic progress updates from the simulation engine.
        Action<double> timelineProgress;
        Action<TimelineLine> timelineLine;
        // Used by diagnostic snapshots to start the same simulation as the button.
        public Button TimelineGo;

        // Choose the normal attack source rather than using a checkbox: only one
        // weapon or assigned attack can be active at a time. Use one interval
        // field for skills without cooldowns.
        readonly NumericUpDown pressEveryBox = new NumericUpDown
        {
            // Zero uses the casting animation interval. A nonzero value inserts
            // the skill between normal attacks.
            Width = 56, Minimum = 0, Maximum = 60, DecimalPlaces = 1, Increment = 0.5M, Value = 3M,
            ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(28, 26, 24),
            BorderStyle = BorderStyle.FixedSingle,
        };

        readonly ComboBox attackBox = new ComboBox
        {
            Width = 178, DropDownStyle = ComboBoxStyle.DropDownList,
            ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(28, 26, 24),
            FlatStyle = FlatStyle.Flat,
        };
        readonly List<AttackSkill> assignable = new List<AttackSkill>();
        // Additional manually activated skills follow the damaging pressable list.
        readonly List<Timeline.Extra> extras = new List<Timeline.Extra>();
        // Include buff levels so their durations can be determined.
        readonly List<Timeline.TimedBuff> buffInfo = new List<Timeline.TimedBuff>();
        // Summoned units form the fourth checkbox group. The list controls which
        // units are explicitly summoned; triggered summons are handled separately.
        readonly List<string> petSummons = new List<string>();

        TabPage TimelinePage()
        {
            TabPage page = new TabPage(Lang.T("damage.tab.timeline"));
            page.BackColor = Color.FromArgb(20, 19, 18);

            Panel top = new Panel { Dock = DockStyle.Top, Height = 150, BackColor = page.BackColor };
            Label secs = new Label { Text = Lang.T("damage.run.seconds"), Left = 8, Top = 11, AutoSize = true,
                                     ForeColor = Color.DimGray };
            // Keep fields after their labels while preserving the existing positions.
            runSeconds.Left = Math.Max(62, secs.Left + TextRenderer.MeasureText(secs.Text, secs.Font).Width + 6);
            runSeconds.Top = 8;
            Label seedLabel = new Label { Text = Lang.T("damage.run.seed"), Left = Math.Max(148, runSeconds.Right + 12),
                                          Top = 11, AutoSize = true, ForeColor = Color.DimGray };
            runSeed.Left = Math.Max(196, seedLabel.Left + TextRenderer.MeasureText(seedLabel.Text, seedLabel.Font).Width + 6);
            runSeed.Top = 8;
            // Place the button below the fields so it does not overlap the skill list.
            Button go = new Button
            {
                Text = Lang.T("damage.run.go"),
                Left = 8, Top = 44, Width = 240, Height = 28,
                ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(38, 35, 32), FlatStyle = FlatStyle.Flat,
            };
            TimelineGo = go;
            // List damaging skills without another trigger. Weapon pools and
            // on-hit procs are scheduled by their own paths.
            Label what = new Label { Text = Lang.T("damage.run.press"), Left = 300, Top = 6,
                                     AutoSize = true, ForeColor = Color.DimGray };
            pressBox.Left = 300; pressBox.Top = 22;
            pressBox.Items.Clear();
            buffs.Clear();
            pressable.Clear();
            attackBox.Items.Clear();
            assignable.Clear();
            extras.Clear();
            buffInfo.Clear();
            petSummons.Clear();
            try
            {
                CharacterTotals t = Totals ?? Sum();
                AttackSkills.Attach attach = AttackSkills.Attach.Of(game, character);
                ItemSkillMods mods = ItemSkillMods.Of(game, character);
                SkillText.LevelBonus lb = SkillText.LevelBonus.Of(game, character);
                // Put manually activated buffs first in the same list.
                //
                // These skills are selected in the active-skill dialog but are
                // scheduled from this panel.
                //
                // They have no damage and are therefore not returned by
                // `AttackSkills.Of`; collect them separately.
                //
                // The checkbox and active-skill dialog share the same `ActiveSkills`
                // state. The list is shared with the engine and diagnostics via
                // `Timeline.TimedCandidates`.
                // (MECHANICS 76).
                foreach (Timeline.TimedBuff b in Timeline.TimedCandidates(game, character))
                {
                    double bcd = SkillTiming.Cooldown(game, character, t, attach, mods, lb, b.Dbr, b.Level);
                    buffs.Add(b.Dbr);
                    buffInfo.Add(b);
                    // Preserve an existing choice instead of overwriting the
                    // selection made in the active-skill dialog.
                    bool? said = chosen == null ? null : chosen.For(b.Dbr);
                    pressBox.Items.Add(b.Name + "   " + Lang.T("damage.press.buff") +
                                       (string.IsNullOrEmpty(b.When) ? "" : " " + Lang.T("damage.press.buffNotRolled", b.When)) +
                                       (bcd > 0.01 ? ", " + Lang.T("damage.seconds", Fmt(Math.Round(bcd, 2))) : ""),
                                       said.HasValue && said.Value);
                }
                // Casting speed supplies the tick interval for channelled skills.
                DamageModel castPanel = new DamageModel(game, character, t, null, true, null);
                List<AttackSkill> attackList = AttackSkills.Of(game, character);
                // Put forms first in the attack-source list. A form supplies the
                // attack skills; the form itself is not a hit.
                List<AttackSkills.Form> forms = AttackSkills.Forms(game, character, attackList);
                Dictionary<string, string> ofForm =
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (AttackSkills.Form f in forms)
                {
                    assignable.Add(f.Attack);
                    attackBox.Items.Add(Lang.T("damage.attack.form", f.Name, f.Attack.Name));
                    foreach (AttackSkill g in f.Granted) ofForm[g.Dbr] = f.Name;
                }
                foreach (AttackSkill a in attackList)
                {
                    // Mark skills granted by a form so they are not mistaken for
                    // ordinary character skills.
                    string formTag;
                    string formOf = ofForm.TryGetValue(a.Dbr, out formTag) ? "   " + Lang.T("damage.attack.ofForm", formTag) : "";
                    // Channelled attacks belong to the attack-source selector, not
                    // the checkbox list; they replace normal attacks while active.
                    if (a.ChannelMs > 0)
                    {
                        assignable.Add(a);
                        double tick = Combat.ChannelInterval(game, a.ChannelMs, castPanel.CastSpeedPercent);
                        attackBox.Items.Add(a.Name + "   " + Lang.T("damage.attack.channel", Fmt(Math.Round(tick, 3))));
                        continue;
                    }
                    // Assigned attacks use the selector below, not a checkbox.
                    if (a.Assigned)
                    {
                        assignable.Add(a);
                        attackBox.Items.Add(a.Name + (a.Finale
                            ? "   " + Lang.T("damage.attack.finale", a.ChargeLevels + 1)
                            : a.ChargeLevels > 0 ? "   " + Lang.T("damage.attack.charges", a.ChargeLevels) : ""));
                        continue;
                    }
                    double pressEvery = (double)pressEveryBox.Value;
                    // Automatic on-attack and on-crit effects run without a checkbox.
                    // Effects with unsupported triggers are exposed as cooldown-based
                    // checkbox entries.
                    bool auto = a.HitChance > 0.0001 || a.PoolChance > 0.0001 ||
                                a.AutoTrigger == "AttackEnemy" || a.AutoTrigger == "AttackEnemyCrit";
                    if (auto) continue;
                    // A buff is not a hit; it belongs in the cooldown-based buff list.
                    if (Timeline.IsBuffSkill(game, a.Dbr)) continue;
                    bool unmodelled = !string.IsNullOrEmpty(a.When);
                    double cd = SkillTiming.Cooldown(game, character, t, attach, mods, lb,
                                                     a.Dbr, a.Level);
                    // A no-cooldown spell is also an attack-source option: continuous
                    // use replaces normal attacks. It remains available as an
                    // intermittent checkbox action.
                    if (cd <= 0.01)
                    {
                        double castAnim = SkillHands.CastSeconds(game, game.Record(a.Dbr), character);
                        if (castAnim > 0)
                        {
                            assignable.Add(a);
                            attackBox.Items.Add(a.Name + formOf + "   " +
                                                Lang.T("damage.attack.byAnimation", Fmt(Math.Round(castPanel.CastInterval(castAnim), 3))));
                        }
                    }
                    // A SKILL WITH A COOLDOWN GOES INTO "WHAT WE STRIKE WITH" AS WELL — the
                    // user's request of 16 October: "we pick, in the list where the autoattack
                    // is picked, a skill that can deal damage (not an aura), instead of the
                    // autoattack".
                    //
                    // It stays in the tick boxes too, and the two choices mean different
                    // things: a tick means "beside the swings", this list means "INSTEAD of
                    // them, and nothing else strikes". Until today only the second was
                    // missing, and with it the case "strikes with this alone" could not be
                    // asked for at all.
                    //
                    // A skill with a condition we do not roll (`When`) is not offered: it is
                    // not a button, and the tick box with its interval is the honest place
                    // for it.
                    else if (!unmodelled)
                    {
                        assignable.Add(a);
                        attackBox.Items.Add(a.Name + formOf + "   " +
                                            Lang.T("damage.attack.byCooldown", Fmt(Math.Round(cd, 2))));
                    }
                    pressable.Add(a);
                    // The user supplies the interval for a no-cooldown skill.
                    //
                    // The interval depends on player input and casting speed, so it
                    // must be configured instead of assuming one activation.
                    double anim = cd > 0.01 || pressEvery > 0.01 ? 0
                                : SkillHands.CastSeconds(game, game.Record(a.Dbr), character);
                    pressBox.Items.Add(a.Name + formOf +
                        (unmodelled ? "   (" + Lang.T("damage.press.whenNotRolled", a.When) + ")" : "") + "   " +
                        (cd > 0.01
                        ? Lang.T("damage.seconds", Fmt(Math.Round(cd, 2)))
                        : pressEvery > 0.01
                            ? Lang.T("damage.press.every", Fmt(pressEvery))
                            : anim > 0
                                ? Lang.T("damage.press.anim", Fmt(Math.Round(castPanel.CastInterval(anim), 3)))
                                : Lang.T("damage.press.noAnim")));
                }
                // Additional manual actions follow damaging skills.
                foreach (Timeline.Extra x in Timeline.PressExtras(game, character))
                {
                    double xcd = SkillTiming.Cooldown(game, character, t, attach, mods, lb, x.Dbr, x.Level);
                    extras.Add(x);
                    pressBox.Items.Add(x.Name + "   (" + (x.Skill != null ? Lang.T("damage.press.starHits") : Timeline.EffectWord(x.Kind)) +
                                       (string.IsNullOrEmpty(x.When) ? "" : "; " + Lang.T("damage.press.whenNotRolled", x.When)) +
                                       ")   " + (xcd > 0.01 ? Lang.T("damage.seconds", Fmt(Math.Round(xcd, 2)))
                                                         : Lang.T("damage.press.noCooldown")));
                }
                // Summoned units are listed last. Checked units are summoned on
                // their cooldown; automatic summons do not require a checkbox.
                foreach (TimelinePets.Kind pk in TimelinePets.Of(game, character, t, chosen, null, null))
                {
                    petSummons.Add(pk.Summon);
                    pressBox.Items.Add(pk.Title + "   " +
                                       Lang.T("damage.press.pet", pk.Name, pk.Limit,
                                              pk.Seconds > 0 ? Lang.T("damage.press.petLives", Fmt(pk.Seconds))
                                                             : Lang.T("damage.press.petPermanent"),
                                              Fmt(Math.Round(pk.Aps, 2)), pk.Moves.Count) +
                                       (pk.SummonCooldown > 0.01 ? "   " + Lang.T("damage.seconds", Fmt(Math.Round(pk.SummonCooldown, 2))) : ""),
                                       pk.Info != null && pk.Info.Chosen);
                }
            }
            // Keep the list valid; otherwise the run falls back to normal attacks.
            catch (Exception) { }
            if (pressBox.Items.Count == 0) pressBox.Items.Add(Lang.T("damage.press.none"));

            // Select the normal attack source. Keep the weapon as the first item.
            Label atkLabel = new Label { Text = Lang.T("damage.run.attack"), Left = 8, Top = 83, AutoSize = true,
                                         ForeColor = Color.DimGray };
            // Keep the selector within the space before the checkbox list.
            attackBox.Left = 110; attackBox.Top = 80;
            // Shift and shrink the selector when localization makes the label longer.
            int atkRight = atkLabel.Left + TextRenderer.MeasureText(atkLabel.Text, atkLabel.Font).Width + 6;
            if (atkRight > attackBox.Left) { attackBox.Left = atkRight; attackBox.Width = Math.Max(100, 288 - atkRight); }
            attackBox.Items.Insert(0, Lang.T("damage.run.weapon"));
            attackBox.SelectedIndex = 0;
            attackBox.Enabled = assignable.Count > 0;
            // Interval for no-cooldown actions; it belongs to the checkbox list.
            Label everyLabel = new Label { Text = Lang.T("damage.run.everyLabel"), Left = 300, Top = 123,
                                           AutoSize = true, ForeColor = Color.DimGray };
            pressEveryBox.Left = Math.Max(480, everyLabel.Left + TextRenderer.MeasureText(everyLabel.Text, everyLabel.Font).Width + 6);
            pressEveryBox.Top = 120;
            Label everyTail = new Label { Text = Lang.T("damage.run.everyTail"), Left = Math.Max(542, pressEveryBox.Right + 6),
                                          Top = 123, AutoSize = true, ForeColor = Color.DimGray };
            top.Controls.AddRange(new Control[] { secs, runSeconds, seedLabel, runSeed, go,
                                                  what, pressBox, atkLabel, attackBox,
                                                  everyLabel, pressEveryBox, everyTail });

            timelineView = new TipView(new List<TipLine>()) { Dock = DockStyle.Fill };
            page.Controls.Add(timelineView);
            page.Controls.Add(top);
            if (!timelineWired)
            {
                // Show progress before calculation starts and keep the cursor busy
                // while the run is active.
                go.Click += delegate { RunTimelineLive(go); };
                timelineWired = true;
            }
            timelineView.SetLines(TimelineHelp());
            return page;
        }

        // Explain the simulation scope before the first run; an empty tab looks broken.
        List<TipLine> TimelineHelp()
        {
            List<TipLine> lines = new List<TipLine>();
            lines.Add(new TipLine(Lang.T("damage.help.title"), TipKind.Headline));
            lines.Add(new TipLine());
            AddNote(lines, Lang.T("damage.help.press"));
            AddNote(lines, Lang.T("damage.help.twoNumbers"));
            AddNote(lines, Lang.T("damage.help.target"));
            // Keep this note limited to current behavior; historical limitations
            // and dates do not belong in the UI description.
            AddNote(lines, Lang.T("damage.help.pets"));
            AddNote(lines, Lang.T("damage.help.counted"));
            AddNote(lines, Lang.T("damage.help.seed"));
            return lines;
        }

        // Run from the button while streaming the log tail. Diagnostic snapshots
        // use the same method through `LiveShotPath`.
        public static string LiveShotPath;

        public void RunTimelineLive(Button go)
        {
                List<TipLine> wait = new List<TipLine>();
                wait.Add(new TipLine(Lang.T("damage.run.building"), TipKind.Headline));
                wait.Add(new TipLine());
                wait.Add(new TipLine(Lang.T("damage.run.buildingNote"), TipKind.Subtle, 1));
                timelineView.SetLines(wait);
                timelineView.Refresh();
                // Append each log line immediately. This keeps the window responsive
                // during long recalculations.
                //
                // Throttle tail redraws and render only the visible tail to avoid
                // repainting the entire growing log.
                List<TipLine> tail = new List<TipLine>();
                double lastAt = 0;
                System.Diagnostics.Stopwatch paint = System.Diagnostics.Stopwatch.StartNew();
                Action<bool> push = delegate(bool force)
                {
                    if (IsDisposed) throw new OperationCanceledException();
                    if (!force && paint.ElapsedMilliseconds < 80) return;
                    paint.Restart();
                    int fit = Math.Max(20, Math.Min(200, timelineView.ClientSize.Height / 17 - 3));
                    List<TipLine> view = new List<TipLine>();
                    view.Add(new TipLine(Lang.T("damage.run.progress", Fmt(Math.Floor(lastAt)),
                                                Fmt((double)runSeconds.Value), tail.Count),
                                         TipKind.Headline));
                    view.Add(new TipLine());
                    for (int i = Math.Max(0, tail.Count - fit); i < tail.Count; i++) view.Add(tail[i]);
                    timelineView.SetLines(view);
                    timelineView.Refresh();
                    if (LiveShotPath != null && lastAt >= 5)
                {
                    using (Bitmap bmp = new Bitmap(Width, Height))
                    {
                        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                        bmp.Save(LiveShotPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    LiveShotPath = null;
                }
                // Pump the UI queue so Windows does not mark the window as hung.
                    Application.DoEvents();
                };
                timelineProgress = delegate(double at) { lastAt = at; push(false); };
                timelineLine = delegate(TimelineLine line) { tail.Add(LogTip(line)); lastAt = line.At; push(false); };
                Cursor keep = Cursor;
                Cursor = Cursors.WaitCursor;
                // Disable the button during a run to prevent concurrent simulations.
                go.Enabled = false;
                try { ShowTimeline(); }
                finally
                {
                    timelineProgress = null;
                    timelineLine = null;
                    if (!IsDisposed) { go.Enabled = true; Cursor = keep; }
                }
        }

        // Run the queue without a button click for `--damageshot`.
        public void RunTimeline() { ShowTimeline(); }

        // Diagnostic snapshots can scroll to the end to verify the complete log.
        public void ScrollTimelineToEnd() { timelineView.ScrollToEnd(); }

        public int TimelineRunSeconds { set { runSeconds.Value = Math.Min(runSeconds.Maximum, value); } }

        // Use event-specific colors for both the final log and the live tail.
        //
        // Color by `TimelineLine.Kind`, not by localized text. The mapping is
        // defined by `LogKind` in `Timeline.cs`.
        static TipLine LogTip(TimelineLine line)
        {
            TipLine tl = new TipLine(line.ToString(), TipKind.Bonus, 1);
            switch (line.Kind)
            {
                case LogKind.Tick:     tl.Override = Color.FromArgb(230, 150, 60);  break; // damage-over-time: orange
                case LogKind.Silent:   tl.Override = Color.FromArgb(165, 115, 150); break; // silent effect: muted pink
                case LogKind.Devotion: tl.Override = Color.FromArgb(250, 205, 120); break; // devotion effect: gold
                case LogKind.Proc:     tl.Override = Color.FromArgb(240, 160, 210); break; // automatic proc: pink
                case LogKind.Pool:     tl.Override = Color.FromArgb(200, 175, 245); break; // weapon pool: lilac
                case LogKind.Pressed:  tl.Override = Color.FromArgb(120, 175, 235); break; // manual action: blue
                case LogKind.Life:     tl.Override = Color.FromArgb(255, 214, 90);  break; // target health: gold
                case LogKind.Miss:     tl.Override = Color.FromArgb(130, 125, 120); break; // miss: gray
                default:               tl.Override = Color.FromArgb(225, 222, 218); break; // default: white
            }
            return tl;
        }

        void ShowTimeline()
        {
            List<TipLine> lines = new List<TipLine>();
            try
            {
                ExactRolls seeded = new ExactRolls();
                seeded.ReplaceWithSeed(game, character);

                TimelineOptions opt = new TimelineOptions();
                opt.Seconds = (double)runSeconds.Value;
                opt.Seed = (int)runSeed.Value;
                opt.OnSecond = timelineProgress;
                opt.OnLine = timelineLine;
                // List buffs before damaging skills.
                //
                // A selected long-lived buff is included in `ActiveSkills` and
                // affects `Sum()` for the run instead of becoming a separate hit.
                for (int i = 0; i < buffs.Count && i < pressBox.Items.Count; i++)
                {
                    bool on = pressBox.GetItemChecked(i);
                    chosen.Set(buffs[i], on);
                    // Schedule selected buffs for their duration and cooldown.
                    // If no duration is available, keep them active for the run.
                    if (on && i < buffInfo.Count &&
                        SkillTiming.Duration(game, buffs[i], buffInfo[i].Level) > 0)
                        opt.TimedBuffs.Add(buffs[i]);
                }
                for (int i = 0; i < pressable.Count && i + buffs.Count < pressBox.Items.Count; i++)
                    if (pressBox.GetItemChecked(i + buffs.Count))
                    {
                        opt.Pressed.Add(pressable[i].Dbr);
                        opt.PressEvery[pressable[i].Dbr] = (double)pressEveryBox.Value;
                    }
                for (int i = 0; i < extras.Count && i + buffs.Count + pressable.Count < pressBox.Items.Count; i++)
                    if (pressBox.GetItemChecked(i + buffs.Count + pressable.Count))
                    {
                        opt.Pressed.Add(extras[i].Dbr);
                        opt.PressEvery[extras[i].Dbr] = (double)pressEveryBox.Value;
                    }
                // Summoned units are controlled exclusively by the checkbox list.
                opt.PetsChosen = true;
                int petAt = buffs.Count + pressable.Count + extras.Count;
                for (int i = 0; i < petSummons.Count && i + petAt < pressBox.Items.Count; i++)
                    if (pressBox.GetItemChecked(i + petAt)) opt.Pets.Add(petSummons[i]);
                // Calculate totals after applying checkbox selections.
                //
                // Selected buffs change character stats, so rebuild the cached
                // totals after applying them.
                CharacterTotals t = Sum();
                // The zero item represents the weapon; assigned attacks follow.
                int pick = attackBox.SelectedIndex - 1;
                if (pick >= 0 && pick < assignable.Count) opt.Attack = assignable[pick].Dbr;

                // Schedule buffs whose trigger is not simulated directly.
                //
                // Selected conditional buffs are scheduled for their own duration
                // and cooldown; the damage panel still evaluates them as active.
                foreach (Timeline.Star st in Timeline.ByOtherCondition(game, character))
                {
                    // Damaging devotion effects are manual actions, not buffs.
                    if (AttackSkills.One(game, st.Dbr, Timeline.DevotionLevelOf(character, st.Dbr), Lang.T("common.source.devotion")) != null)
                        continue;
                    bool? said = chosen == null ? null : chosen.For(st.Dbr);
                    if (said.HasValue && said.Value && !opt.TimedBuffs.Contains(st.Dbr)) opt.TimedBuffs.Add(st.Dbr);
                }

                // Use the same target as the adjacent tab so both views agree.
                Enemy chosenEnemy = enemyBox.SelectedItem as Enemy;
                TimelineTarget target;
                if (chosenEnemy != null)
                    target = TimelineTarget.Of(game, chosenEnemy.Name,
                                               EnemyTable.Resolve(game, chosenEnemy, (int)enemyLevel.Value,
                                                                  enemyDiff.SelectedIndex));
                else target = TimelineTarget.Dummy(game, opt.DummyLevel, opt.Difficulty);

                TimelineRun run = Timeline.Run(game, character, t, seeded, target, opt);

                // Build the result, queue, and run-condition sections separately
                // and place them in that order.
                List<TipLine> info = new List<TipLine>();
                List<TipLine> queueLines = new List<TipLine>();
                List<TipLine> summary = new List<TipLine>();
                // Show active buffs in a separate line.
                //
                // Active buffs are not queue events: they change stats rather than
                // dealing damage, so show them explicitly in the report.
                List<string> up = new List<string>();
                for (int i = 0; i < buffs.Count && i < pressBox.Items.Count; i++)
                    if (pressBox.GetItemChecked(i))
                        up.Add(SkillText.Name(game, buffs[i]) ?? buffs[i]);
                if (up.Count > 0)
                    info.Add(new TipLine(Lang.T("damage.run.buffsUp", string.Join(", ", up.ToArray())), TipKind.Bonus));

                info.Add(new TipLine(Lang.T("damage.run.target", target.Name) +
                                      (chosenEnemy != null
                                           ? "   " + Lang.T("damage.run.targetLevel", (int)enemyLevel.Value,
                                                            enemyDiff.Text.ToLower()) : "") +
                                      "   " + Lang.T("damage.run.targetStats", Fmt(Math.Round(target.DefensiveAbility)),
                                                     Fmt(Math.Round(target.Armour))), TipKind.Headline));
                info.Add(new TipLine(Lang.T("damage.run.hitChance", Fmt(Math.Round(run.HitPercent, 1))),
                                      TipKind.Bonus, 1));
                // Report channelled attacks with the source of their tick interval.
                if (!string.IsNullOrEmpty(run.MainSpell))
                    info.Add(new TipLine(Lang.T("damage.run.mainSpell", run.MainSpellWord, run.MainSpell,
                                                Fmt(Math.Round(run.CastSpeedPercent)),
                                                Fmt(Math.Round(run.MainSpellInterval, 3)), run.MainSpellTicks),
                                         TipKind.Bonus, 1));
                // Report target resistances so difficulty-related reductions are visible.
                List<string> cut = new List<string>();
                foreach (string type in run.Types)
                {
                    double r;
                    if (!target.Resist.TryGetValue(type, out r) || Math.Abs(r) < 0.01) continue;
                    cut.Add(type + " " + Fmt(Math.Round(r, 1)) + "%");
                }
                cut.Sort(StringComparer.Ordinal);
                if (cut.Count > 0)
                    info.Add(new TipLine(Lang.T("damage.run.targetResists", string.Join(", ", cut.ToArray())),
                                          TipKind.Bonus, 1));
                // Report race bonuses and explicitly note when they do not apply
                // to the selected target.
                foreach (string one in run.RacialLines)
                    info.Add(new TipLine(one, TipKind.Bonus, 1));
                // Report item and set skills with their conditions and run state.
                if (run.GearLines.Count > 0)
                {
                    info.Add(new TipLine(Lang.T("damage.run.gear"), TipKind.Bonus, 1));
                    foreach (string one in run.GearLines)
                        info.Add(new TipLine(one, TipKind.Subtle, 2));
                }
                queueLines.Add(new TipLine(Lang.T("damage.run.queue"), TipKind.Section));
                // Use event-specific colors. Render the full log through `TipView`,
                // which measures and draws only visible rows.
                foreach (TimelineLine line in run.Log)
                    queueLines.Add(LogTip(line));

                summary.Add(new TipLine(Lang.T("damage.run.summary"), TipKind.Section));
                // Put DPS first and emphasize it for quick inspection.
                TipLine dpsLine = new TipLine(Lang.T("damage.run.dps", Fmt(Math.Round(run.Dps)), Fmt(Math.Round(run.Median))),
                                              TipKind.Headline, 1);
                dpsLine.Size = 12.5f;
                dpsLine.Override = Color.FromArgb(255, 214, 90);
                summary.Add(dpsLine);
                summary.Add(new TipLine(Lang.T("damage.run.total", Fmt(Math.Round(run.Total)), Fmt(Math.Round(opt.Seconds))),
                                        TipKind.Bonus, 1));
                // Show target health and TTK directly below DPS.
                if (run.TargetLife > 0)
                {
                    double left = Math.Max(0, run.TargetLife - run.Total) / run.TargetLife * 100;
                    TipLine ttk = new TipLine(Lang.T("damage.run.targetLife", Fmt(Math.Round(run.TargetLife))) + "   " +
                        (run.KillAt >= 0 ? Lang.T("damage.run.killedAt", Fmt(Math.Round(run.KillAt, 2))) +
                                           (run.TargetIsDummy ? " " + Lang.T("damage.run.dummyNote") : "")
                                         : Lang.T("damage.run.notKilled", Fmt(Math.Round(left, 1)))),
                        TipKind.Headline, 1);
                    ttk.Size = 11f;
                    ttk.Override = Color.FromArgb(255, 214, 90);
                    summary.Add(ttk);
                    if (run.PercentLifeTotal > 0.5 || run.PercentLifeBlocked > 0)
                        summary.Add(new TipLine(Lang.T("damage.run.percentLife", Fmt(Math.Round(run.PercentLifeTotal)),
                                                       run.PercentLifeHits) +
                            (run.PercentLifeBlocked > 0
                                 ? "; " + Lang.T("damage.run.percentLifeBlocked", run.PercentLifeBlocked,
                                                 Fmt(Math.Round(run.PercentLifeResist))) : ""), TipKind.Bonus, 2));
                }
                summary.Add(new TipLine(Lang.T("damage.run.swings", run.Swings, run.Hits, run.Misses, run.Crits),
                                        TipKind.Bonus, 1));
                if (run.Crits > 0)
                {
                    List<string> tiers = new List<string>();
                    for (int i = 2; i < run.ByTier.Count; i++)
                        if (run.ByTier[i] > 0) tiers.Add(Lang.T("damage.run.tier", i - 1, run.ByTier[i]));
                    summary.Add(new TipLine(string.Join(", ", tiers.ToArray()), TipKind.Subtle, 2));
                }
                if (run.Picked[0] + run.Picked[2] > 0)
                    summary.Add(new TipLine(Lang.T("damage.run.hands", run.Picked[0], run.Picked[1], run.Picked[2]),
                                            TipKind.Bonus, 1));
                if (run.Procs > 0 || run.PoolFired > 0 || run.ProcsSilent > 0)
                    summary.Add(new TipLine(Lang.T("damage.run.procs", run.Procs, Fmt(Math.Round(run.ProcTotal))) +
                                          (run.PoolFired > 0 ? ", " + Lang.T("damage.run.pool", run.PoolFired) : "") +
                                          (run.ProcsSilent > 0
                                               ? ", " + Lang.T("damage.run.silent", run.ProcsSilent,
                                                               string.Join(", ", run.SilentNames.ToArray()))
                                               : ""), TipKind.Bonus, 1));
                // Show summoned-unit damage in its own block; it is included in the total.
                if (run.PetLines.Count > 0)
                {
                    summary.Add(new TipLine(Lang.T("damage.run.pets", run.PetHits, run.PetSummons, Fmt(Math.Round(run.PetTotal))) +
                                          (run.Total > 0.5
                                               ? " " + Lang.T("damage.run.petsShare", Fmt(Math.Round(run.PetTotal / run.Total * 100, 1)))
                                               : ""), TipKind.Section));
                    foreach (string one in run.PetLines)
                        summary.Add(new TipLine(one, TipKind.Bonus, 1));
                }
                if (run.StarLines.Count > 0)
                {
                    summary.Add(new TipLine(Lang.T("damage.run.stars"), TipKind.Section));
                    foreach (string one in run.StarLines)
                        summary.Add(new TipLine(one, TipKind.Bonus, 1));
                }
                if (run.DotApplied > 0)
                    summary.Add(new TipLine(Lang.T("damage.run.dots", run.DotApplied, run.DotRefresh,
                                                   Fmt(Math.Round(run.DotTotal)), Fmt(Math.Round(run.Total))),
                                            TipKind.Bonus, 1));
                // Show dealt damage and pre-mitigation damage. The latter may be
                // lower than dealt damage when bonuses or negative resistances apply.
                if (Math.Abs(run.TotalBefore - run.Total) > 0.5 && run.TotalBefore > 0.5)
                    summary.Add(new TipLine(Lang.T("damage.run.before", Fmt(Math.Round(run.TotalBefore)),
                                                   Fmt(Math.Round(run.Total / run.TotalBefore * 100, 1))),
                                          TipKind.Subtle, 1));
                lines.AddRange(summary);
                lines.Add(new TipLine());
                lines.AddRange(queueLines);
                lines.Add(new TipLine());
                lines.Add(new TipLine(Lang.T("damage.run.conditions"), TipKind.Section));
                lines.AddRange(info);
                lines.Add(new TipLine());
                AddNote(lines, Lang.T("damage.run.rolls", run.Rolls));
                AddNote(lines, Lang.T("damage.run.critNote"));
                AddNote(lines, Lang.T("damage.run.dotNote"));
                foreach (string note in run.Notes)
                    lines.Add(new TipLine("• " + Lang.T("damage.run.noteRough", note), TipKind.Warning, 1));
                // Keep parsing failures and approximation notes distinguishable.
                foreach (string note in run.Unread)
                    lines.Add(new TipLine("• " + Lang.T("damage.run.noteUnread", note), TipKind.Warning, 1));
                foreach (string note in run.Hints)
                    lines.Add(new TipLine("• " + Lang.T("damage.run.noteHint", note), TipKind.Subtle, 1));
                // If a game field is unavailable, the fallback value is reported.
                //
                // Fallback values are hidden in the UI but remain available to
                // diagnostic reports. Include the source record path there.
                // foreach (string f in Combat.FallbackList())
                //     lines.Add(new TipLine("• " + Lang.T("damage.run.noteFallback", f), TipKind.Warning, 1));
                AddNote(lines, Lang.T("damage.run.pressNote"));
                AddNote(lines, Lang.T("damage.run.procNote"));
                AddNote(lines, Lang.T("damage.run.silentNote"));
            }
            // The window may close while the run is active; stop updating it.
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                if (IsDisposed) return;
                lines.Add(new TipLine(Lang.T("damage.run.failed", e.Message), TipKind.Warning));
            }
            if (IsDisposed) return;
            TimelineLines = lines;
            timelineView.SetLines(lines);
        }

        // The simulation tab contains the result, notes, and log. Diagnostic
        // snapshots can write the same content to the run report.
        public List<TipLine> TimelineLines;

        TabPage TargetPage()
        {
            TabPage page = new TabPage(Lang.T("damage.tab.target"));
            page.BackColor = Color.FromArgb(20, 19, 18);

            Panel top = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = page.BackColor };
            Button import = new Button
            {
                Text = Lang.T("damage.target.load"),
                Left = 8, Top = 6, Width = 260, Height = 26,
                ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(38, 35, 32), FlatStyle = FlatStyle.Flat,
            };
            Label lvl = new Label { Text = Lang.T("damage.target.level"), Left = 470, Top = 42, AutoSize = true, ForeColor = Color.DimGray };
            Label findLabel = new Label { Text = Lang.T("damage.target.find"), Left = 276, Top = 11, AutoSize = true,
                                          ForeColor = Color.DimGray };
            enemyFind.Left = Math.Max(380, findLabel.Left + TextRenderer.MeasureText(findLabel.Text, findLabel.Font).Width + 6);
            enemyFind.Top = 7;
            enemyBox.Left = 8; enemyBox.Top = 38;
            enemyLevel.Left = 534; enemyLevel.Top = 38;
            // Rebuild the page instead of appending to the persistent list.
            enemyDiff.Items.Clear();
            enemyDiff.Items.AddRange(new object[] { Lang.T("common.difficulty.normal"), Lang.T("common.difficulty.elite"),
                                                    Lang.T("common.difficulty.ultimate") });
            enemyDiff.SelectedIndex = 2;
            enemyDiff.Left = 620; enemyDiff.Top = 38;
            Label dl = new Label { Text = Lang.T("damage.target.difficulty"), Left = 620, Top = 18, AutoSize = true, ForeColor = Color.DimGray };
            top.Controls.AddRange(new Control[] { import, findLabel, enemyFind, enemyBox, lvl, enemyLevel,
                                                  dl, enemyDiff });

            enemyView = new TipView(new List<TipLine>()) { Dock = DockStyle.Fill };
            page.Controls.Add(enemyView);
            page.Controls.Add(top);

            import.Click += delegate { ImportEnemies(); };
            if (!enemyWired)
            {
                enemyFind.TextChanged += delegate { FillEnemies(); };
                enemyBox.SelectedIndexChanged += delegate { ShowEnemy(); };
                enemyLevel.ValueChanged += delegate { ShowEnemy(); };
                enemyDiff.SelectedIndexChanged += delegate { ShowEnemy(); };
                enemyWired = true;
            }

            enemies = EnemyTable.LoadFor(game, CachePath());
            if (enemies.Count > 0) { FillEnemies(); import.Text = Lang.T("damage.target.reload"); }
            else ShowEnemy();
            return page;
        }

        static string CachePath() { return EnemyTable.CachePath; }

        // Building the table reads every record under records/creatures/enemies,
        // so it happens on request and is kept beside the program — the same way
        // the item catalogue and the portal list are. Nobody who does not need
        // enemies pays for them.
        void ImportEnemies()
        {
            Cursor old = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                enemies = EnemyTable.Build(game, null);
                // The game's signature goes into the header so the cache goes stale by
                // itself after a patch (MECHANICS 115).
                EnemyTable.Save(CachePath(), enemies, game.Language, game.GameDir);
            }
            finally { Cursor = old; }
            FillEnemies();
        }

        void FillEnemies()
        {
            string find = (enemyFind.Text ?? "").Trim();
            enemyBox.BeginUpdate();
            enemyBox.Items.Clear();
            foreach (Enemy e in enemies)
            {
                if (find.Length > 0 && e.Name.IndexOf(find, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                enemyBox.Items.Add(e);
            }
            enemyBox.DisplayMember = "";
            enemyBox.EndUpdate();
            if (enemyBox.Items.Count > 0) enemyBox.SelectedIndex = 0; else ShowEnemy();
        }

        void ShowEnemy()
        {
            List<TipLine> lines = new List<TipLine>();
            Enemy e = enemyBox.SelectedItem as Enemy;
            if (enemies == null || enemies.Count == 0)
            {
                lines.Add(new TipLine(Lang.T("damage.target.notLoaded"), TipKind.Headline));
                lines.Add(new TipLine());
                // Keep the action hint; remove obsolete implementation details.
                lines.Add(new TipLine(Lang.T("damage.target.pressButton"), TipKind.Subtle, 1));
            }
            else if (e == null) lines.Add(new TipLine(Lang.T("damage.target.nothing"), TipKind.Subtle));
            else
            {
                int level = (int)enemyLevel.Value;
                EnemyStats s = EnemyTable.Resolve(game, e, level, enemyDiff.SelectedIndex);
                lines.Add(new TipLine(Lang.T("damage.enemy.title", e.Name, e.RankRu, level, enemyDiff.Text.ToLower()),
                                      TipKind.Headline));
                if (e.Variants > 1)
                    lines.Add(new TipLine(Lang.T("damage.enemy.variants", e.Variants), TipKind.Subtle));
                lines.Add(new TipLine());
                lines.Add(new TipLine(Lang.T("damage.enemy.combat"), TipKind.Section));
                lines.Add(new TipLine(Lang.T("damage.enemy.abilities", Fmt(Math.Round(s.DefensiveAbility)),
                                             Fmt(Math.Round(s.OffensiveAbility))), TipKind.Bonus, 1));
                lines.Add(new TipLine(Lang.T("damage.enemy.life",
                                             Math.Round(s.Life).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture)
                                                 .Replace(",", " "),
                                             Fmt(Math.Round(s.Armour))) +
                                      (s.GearArmour > 0.5
                                           ? "   " + Lang.T("damage.enemy.gearArmour", Fmt(Math.Round(s.GearArmour)))
                                           : ""), TipKind.Bonus, 1));
                AddHitChance(lines, s);
                lines.Add(new TipLine());
                lines.Add(new TipLine(Lang.T("damage.enemy.resists"), TipKind.Section));
                List<string> keys = new List<string>(s.Resist.Keys);
                keys.Sort(StringComparer.Ordinal);
                DbRecord synthetic = new DbRecord();
                synthetic.Path = "<enemy>";
                foreach (string k in keys)
                    synthetic.Fields[k] = s.Resist[k].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                List<TipLine> body = new List<TipLine>();
                ItemViewFactory.RenderStats(game, synthetic, body, 1, 0);
                if (body.Count == 0) lines.Add(new TipLine(Lang.T("damage.enemy.none"), TipKind.Subtle, 1));
                else lines.AddRange(body);

                // Detailed target-data notes are kept out of the UI. Resistances
                // and armor come from records and skills; difficulty adjustments
                // come from balancing records; race bonuses are applied at the end.
                // The target record path is `e.Path`.
            }
            enemyView.SetLines(lines);
        }

        static void AddNote(List<TipLine> lines, string text)
        { lines.Add(new TipLine("• " + text, TipKind.Subtle, 1)); }

        // Calculate hit and critical chances in both directions.
        //
        // `probabilityToHitEquation` comes from `combatformulas.dbr`. The game
        // uses a uniform [0, 150] roll; a hit is below PTH and a critical hit
        // is below the applicable critical threshold.
        //
        // The reverse direction uses the same equation with attacker and target swapped.
        //
        // Displayed ability values may be rounded, so the result can differ slightly.
        //
        // Critical thresholds use the same percentage scale as the displayed hit
        // chance; critical damage starts at the second threshold:
        //
        //     displayed hit chance P = PTH / 1.5
        //     critical chance = P - 90 (90 = pthThreshold2)
        //     chance of tier k or higher = P - threshold_k
        //
        // The equivalent expressions `135 / 1.5 = 90` produce the same displayed
        // value, but the model still has six critical tiers.
        //
        // Example: at P = 110, threshold 90 gives 20% total critical chance;
        // threshold 105 assigns 5% to tier 1.2 and the remainder to tier 1.1.
        void AddHitChance(List<TipLine> lines, EnemyStats s)
        {
            // `pthMinimum` is a percentage floor, not a PTH value.
            const double Floor = 55.0;
            CharacterTotals t = Sum();
            double oa = Ability(t, character.Cunning, "characterOffensiveAbility", "characterDexterity");
            double da = Ability(t, character.Physique, "characterDefensiveAbility", "characterStrength");

            lines.Add(new TipLine());
            lines.Add(new TipLine(Lang.T("damage.enemy.hit"), TipKind.Section));
            // Truncate displayed ability values instead of rounding them.
            lines.Add(new TipLine(Lang.T("damage.enemy.ourAttack", Fmt(Math.Floor(oa)), Fmt(Math.Floor(s.DefensiveAbility))),
                                  TipKind.Bonus, 1));
            // The displayed value is PTH without division; critical chance starts
            // at the second threshold (90).
            double pth = Pth(oa, s.DefensiveAbility);
            lines.Add(new TipLine(Lang.T("damage.enemy.hitChance", Fmt(Math.Max(Floor, Clamp(Combat.Percent(pth)))),
                                         Fmt(Clamp(Combat.Percent(pth) - 90))),
                                  TipKind.Bonus, 1));
            lines.Add(new TipLine(Lang.T("damage.enemy.ourDefence", Fmt(Math.Floor(da)), Fmt(Math.Floor(s.OffensiveAbility))),
                                  TipKind.Bonus, 1));
            double back = Pth(s.OffensiveAbility, da);
            lines.Add(new TipLine(Lang.T("damage.enemy.getHit", Fmt(Math.Max(Floor, Clamp(Combat.Percent(back)))),
                                         Fmt(Clamp(Combat.Percent(back) - 90))),
                                  TipKind.Bonus, 1));
            // Detailed hit/critical derivation is kept out of the UI. The model
            // uses fixed thresholds from `combatformulas.dbr`; unresolved tier
            // selection details remain diagnostic-only.
        }

        static double Pth(double oa, double da)
        {
            return ((oa / ((da / 3.5) + oa)) * 300 * 0.3)
                 + (((((oa * 3.25) + 10000) - (da * 3.25)) / 100) * 0.7) - 50;
        }

        static double Clamp(double v) { return v < 0 ? 0 : (v > 100 ? 100 : v); }

        // Attack and defense abilities use the same formula:
        // (flat + base + level * 12 + attribute / 2) * (1 + %) + 53.
        double Ability(CharacterTotals t, double attribute, string field, string attrField)
        {
            TotalsEntry e;
            double flat = t.Fields.TryGetValue(field, out e) ? e.Value : 0;
            double pct = t.Fields.TryGetValue(field + "Modifier", out e) ? e.Value : 0;
            double attrFlat = t.Fields.TryGetValue(attrField, out e) ? e.Value : 0;
            double attrPct = t.Fields.TryGetValue(attrField + "Modifier", out e) ? e.Value : 0;
            double attr = (attribute + attrFlat) * (1 + attrPct / 100);
            return (flat + PcBase(field) + character.Level * 12 + attr * 0.5) * (1 + pct / 100) + 53;
        }

        // Read out of the record rather than written as 65: the two sexes have
        // their own record, and a patch could move the number.
        double PcBase(string field)
        {
            DbRecord pc = game.Record("records/creatures/pc/" +
                                      (character.Sex == 0 ? "female" : "male") + "pc01.dbr");
            if (pc == null) pc = game.Record("records/creatures/pc/malepc01.dbr");
            return pc == null ? 0 : pc.GetInt(field);
        }

        static string Join(List<string> items)
        {
            List<string> uniq = new List<string>();
            foreach (string s in items) if (!uniq.Contains(s)) uniq.Add(s);
            if (uniq.Count > 6) { uniq.RemoveRange(6, uniq.Count - 6); uniq.Add("…"); }
            return string.Join(", ", uniq.ToArray());
        }

        // The text of the totals tab, so a headless run can read it.
        public List<TipLine> TotalsLines;

        // The same breakdown the hover shows, but for every field at once, so a
        // headless run can be diffed against the game's character sheet without
        // pointing at each line by hand. This is what tells "our sum disagrees"
        // apart from "our sum counts this ring twice".
        // A dump for the checks, not interface — it is not moved into the language files
        // (PLAN 7v).
        #region Self-check — not interface
        public string TermsDump()
        {
            if (Totals == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            Dump(sb, "CHARACTER BONUSES", Totals.Fields);
            sb.AppendLine();
            Dump(sb, "PET BONUSES", Totals.PetFields);
            return sb.ToString();
        }

        static void Dump(System.Text.StringBuilder sb, string title,
                         Dictionary<string, TotalsEntry> from)
        {
            sb.AppendLine(title);
            List<string> keys = new List<string>(from.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string field in keys)
            {
                TotalsEntry e = from[field];
                sb.AppendLine();
                sb.AppendLine(field + " = " + Fmt(e.Value) +
                              (Math.Abs(e.High - e.Low) < 0.0005
                                   ? ""
                                   : "   [" + Fmt(e.Low) + "–" + Fmt(e.High) + "]") +
                              (e.ByMax ? "   (strongest of " + e.Sources + " sources applies)" : ""));
                foreach (KeyValuePair<string, double> term in e.Terms)
                {
                    bool winner = e.ByMax && Math.Abs(term.Value - e.Largest) < 0.0001;
                    sb.AppendLine("    " + Fmt(term.Value) + "   " + term.Key +
                                  (winner ? "   ← applied" : ""));
                }
            }
        }
        #endregion
    }
}
