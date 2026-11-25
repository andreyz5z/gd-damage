// The character's skills, in a window of their own: one tab per mastery plus
// devotion, riftgates and faction standings. Mostly a reading of the save; the
// three tabs that do change it are the skill trees, the riftgates and the
// factions, and each tells whoever opened the window that it needs writing.
//
// Two things make this cheaper than it looks. A skill's numbers are not computed
// from a formula: the record stores them as a list indexed by level, so the same
// stat renderer that draws an item draws a skill at any level. And the layout of
// a mastery tree is in the database too, one UI record per skill with the pixel
// position the game puts it at, so the tree is drawn where the game draws it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public partial class SkillsForm : Form
    {
        readonly GameData game;
        readonly Character character;
        readonly ItemTooltip tooltip = new ItemTooltip();
        readonly SkillText.LevelBonus bonus;

        // Map each devotion star to its activator and controller. The binding is
        // stored on the activator record (`AutoCastSkill`), not on the star.
        Dictionary<string, string> castsStar, castsBy;

        // Build the pet pool lazily once; it depends on the full character totals.
        List<PetPool.Pet> pets;
        List<PetPool.Pet> Pets()
        {
            if (pets != null) return pets;
            try { pets = PetPool.Of(game, character, ActiveSkills.Of(character == null ? null : character.Name)); }
            catch (Exception) { pets = new List<PetPool.Pet>(); }
            return pets;
        }

        // Reverse star-to-activator map, used when a star row is rendered from the
        // skill that controls its proc rate.
        Dictionary<string, string> boundOn;
        string ActivatorOf(string star)
        {
            if (boundOn == null)
            {
                MapStars();
                boundOn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> kv in castsStar)
                    if (!string.IsNullOrEmpty(kv.Value)) boundOn[kv.Value] = kv.Key;
            }
            string who;
            return boundOn.TryGetValue(star ?? "", out who) ? who : null;
        }

        void MapStars()
        {
            if (castsStar != null) return;
            castsStar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            castsBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Skill s in character.Skills)
                if (!string.IsNullOrEmpty(s.AutoCastSkill) && !string.IsNullOrEmpty(s.Name))
                { castsStar[s.Name] = s.AutoCastSkill; castsBy[s.Name] = s.AutoCastController; }
            foreach (ItemSkill s in character.ItemSkills)
                if (!string.IsNullOrEmpty(s.AutoCastSkill) && !string.IsNullOrEmpty(s.SkillName))
                { castsStar[s.SkillName] = s.AutoCastSkill; castsBy[s.SkillName] = s.AutoCastController; }
        }

        // Character-wide cooldown reduction, cached because tooltip creation may
        // query it repeatedly.
        double cooldownCut = double.NaN;
        public double CooldownCut
        {
            get
            {
                if (double.IsNaN(cooldownCut))
                {
                    cooldownCut = 0;
                    try
                    {
                        TotalsEntry e;
                        CharacterTotals t = CharacterTotals.Of(game, character);
                        if (t != null && t.Fields.TryGetValue("skillCooldownReduction", out e))
                            cooldownCut = e.Value;
                    }
                    catch (Exception) { cooldownCut = 0; }
                }
                return cooldownCut;
            }
        }

        // Skill-specific cooldown and cost modifiers. They come from the skill-tree
        // node and item skill modifiers, then combine with the character-wide cut.
        AttackSkills.Attach attach;
        ItemSkillMods itemMods;

        // Calculation lives in `SkillTiming` so timelines and this window share one rule.
        public void TreeMods(string dbr, out double cdAdd, out double manaCut)
        {
            if (attach == null && game != null && character != null)
                attach = AttackSkills.Attach.Of(game, character);
            if (itemMods == null && game != null && character != null)
                itemMods = ItemSkillMods.Of(game, character);
            SkillTiming.Mods(game, character, attach, itemMods, bonus, dbr, out cdAdd, out manaCut);
        }

        // Per-hand skill damage range: main low/high followed by off-hand low/high.
        //
        // Compute the range through `DamageModel`, keyed by skill and level. Use
        // rolled equipment conversions and exclude temporary effects to match the
        // standing tooltip. Cache once per window.
        readonly Dictionary<string, double[]> handRange =
            new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        ExactRolls handRolls;

        double[] HandRangeOf(string dbr, int level)
        {
            string key = dbr + "|" + level;
            double[] found;
            if (handRange.TryGetValue(key, out found)) return found;
            found = null;
            try
            {
                CharacterTotals totals = CharacterTotals.Of(game, character);
                if (handRolls == null)
                {
                    handRolls = new ExactRolls();
                    handRolls.ReplaceWithSeed(game, character);
                }
                foreach (AttackSkill a in AttackSkills.Of(game, character))
                {
                    if (!string.Equals(a.Dbr, dbr, StringComparison.OrdinalIgnoreCase)) continue;
                    DamageModel m = new DamageModel(game, character, totals, a, false, handRolls);
                    found = new double[] { m.HandMin[0], m.HandMax[0], m.HandMin[1], m.HandMax[1] };
                    break;
                }
            }
            // If damage cannot be calculated, keep the tooltip row without a range.
            catch (Exception) { found = null; }
            handRange[key] = found;
            return found;
        }

        public SkillsForm(GameData game, Character character)
        {
            this.game = game;
            this.character = character;
            this.bonus = SkillText.LevelBonus.Of(game, character);
            AppIcon.Apply(this);

            // UI text comes from `lang/<CODE>/skills.txt`.
            Text = Lang.T("skills.title", character.Name);
            Width = 1100;
            Height = 780;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(24, 22, 20);

            TabControl tabs = new TabControl { Dock = DockStyle.Fill };
            foreach (string folder in Masteries())
                tabs.TabPages.Add(MasteryPage(folder));
            // A slot with nothing in it still gets a tab: without it there is no
            // place to take a mastery from, and a fresh character shows no tabs
            // at all.
            if (MasteryEdit.Taken(character).Count < 2) tabs.TabPages.Add(ChoosePage());
            tabs.TabPages.Add(DevotionPage());
            tabs.TabPages.Add(PortalPage());
            tabs.TabPages.Add(FactionPage());
            Controls.Add(tabs);

            Shown += delegate { tooltip.Owner = this; };
            FormClosed += delegate { tooltip.Dispose(); devoTip.Dispose(); };
        }

        // The masteries the character took, in the order of their class number.
        List<string> Masteries()
        {
            SortedDictionary<string, string> found = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (Skill s in character.Skills)
            {
                if (s.Level <= 0) continue;
                string folder = ItemViewFactory.MasteryFolder(s.Name);
                if (folder != null) found[folder] = folder;
            }
            return new List<string>(found.Keys);
        }

        // "records/skills/playerclass06/" -> 6
        static int MasteryNumber(string folder)
        {
            if (folder == null || folder.Length < 13) return 0;
            int n;
            return int.TryParse(folder.Substring(11, 2), out n) ? n : 0;
        }

        // ------------------------------------------------------------ mastery

        // Choosing a mastery. The list is every mastery the game ships except the
        // one already taken — a character cannot have the same one twice — and
        // nothing happens until the choice is confirmed by name.
        TabPage ChoosePage()
        {
            int slot = MasteryEdit.Taken(character).Count + 1;
            TabPage page = new TabPage(Lang.T(slot == 1 ? "skills.choose.first" : "skills.choose.second"));
            page.BackColor = Color.FromArgb(20, 19, 18);

            FlowLayoutPanel flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(14, 12, 14, 12),
            };
            string early = MasteryEdit.TooEarly(character, slot);
            flow.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                ForeColor = early == null ? Color.Gainsboro : Color.Goldenrod,
                Margin = new Padding(3, 3, 3, 12),
                Text = Lang.T("skills.choose.intro") + "\n" +
                       (early ?? Lang.T("skills.choose.opensAt", MasteryEdit.LevelFor(slot))) +
                       "\n" + Lang.T("skills.choose.freePoints", character.SkillPoints),
            });

            List<int> taken = MasteryEdit.Taken(character);
            foreach (int number in MasteryEdit.All(game))
            {
                if (taken.Contains(number)) continue;
                int n = number;
                Button b = new Button
                {
                    Text = MasteryEdit.Name(game, n),
                    AutoSize = true,
                    Width = 240,
                    Margin = new Padding(3, 2, 3, 2),
                    ForeColor = Color.Gainsboro,
                    BackColor = Color.FromArgb(38, 35, 32),
                    FlatStyle = FlatStyle.Flat,
                    TextAlign = ContentAlignment.MiddleLeft,
                };
                b.Click += delegate { TakeMastery(n, slot); };
                flow.Controls.Add(b);
            }
            page.Controls.Add(flow);
            return page;
        }

        void TakeMastery(int number, int slot)
        {
            // The point has to come from somewhere. Putting one into the bar with
            // an empty pool would create a point out of nothing — the character
            // would end up with one more spent than earned, and the game counts
            // those. Better to say so and let the player add the point first.
            if (character.SkillPoints <= 0)
            {
                MessageBox.Show(Lang.T("skills.noPoints.text"), Lang.T("skills.noPoints.title"),
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string early = MasteryEdit.TooEarly(character, slot);
            string ask = Lang.T("skills.take.ask", MasteryEdit.Name(game, number), character.SkillPoints) +
                         (early == null ? "" : "\n\n" + early + " " + Lang.T("skills.take.undo"));
            if (MessageBox.Show(ask, Lang.T("skills.take.title"), MessageBoxButtons.YesNo,
                                early == null ? MessageBoxIcon.Question : MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            MasteryEdit.Add(character, game, number);
            Rebuilt();
        }

        void DropMastery(int number)
        {
            int points = MasteryEdit.PointsIn(character, number);
            if (MessageBox.Show(Lang.T("skills.drop.ask", MasteryEdit.Name(game, number), points),
                                Lang.T("skills.drop.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            MasteryEdit.Remove(character, game, number);
            Rebuilt();
        }

        // The tabs are built from what the character has, so a change means
        // building them again — and telling whoever opened the window that the
        // save needs writing.
        void Rebuilt()
        {
            if (Changed != null) Changed(this, EventArgs.Empty);
            Controls.Clear();
            Trees.Clear();
            TabControl tabs = new TabControl { Dock = DockStyle.Fill };
            foreach (string folder in Masteries()) tabs.TabPages.Add(MasteryPage(folder));
            if (MasteryEdit.Taken(character).Count < 2) tabs.TabPages.Add(ChoosePage());
            tabs.TabPages.Add(DevotionPage());
            tabs.TabPages.Add(PortalPage());
            tabs.TabPages.Add(FactionPage());
            Controls.Add(tabs);
        }

        TabPage MasteryPage(string folder)
        {
            string name = ItemViewFactory.MasteryName(game, folder) ?? folder;
            TabPage page = new TabPage(name);
            page.BackColor = Color.FromArgb(20, 19, 18);

            // Only the mastery taken last can go back — the same order the game
            // itself allows, so the button on the first one is there but says why
            // it cannot be pressed.
            int number = MasteryNumber(folder);
            Button drop = null;
            if (number > 0)
            {
                bool last = MasteryEdit.CanRemove(character, number);
                drop = new Button
                {
                    Dock = DockStyle.Bottom,
                    Height = 30,
                    Text = last ? Lang.T("skills.drop.button", MasteryEdit.Name(game, number))
                                : Lang.T("skills.drop.secondFirst"),
                    Enabled = last,
                    // Red, because it undoes a hundred points of work and sits at
                    // the bottom of a tall tree where a grey button is invisible.
                    ForeColor = last ? Color.FromArgb(232, 96, 88) : Color.Gray,
                    BackColor = Color.FromArgb(48, 32, 30),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font(Font, FontStyle.Bold),
                };
                drop.FlatAppearance.BorderColor = last ? Color.FromArgb(120, 50, 45) : Color.FromArgb(50, 46, 42);
                int n = number;
                drop.Click += delegate { DropMastery(n); };
            }

            SkillTree tree = new SkillTree(game, character, folder, bonus);
            Trees[folder] = tree;
            tree.Hovered += delegate (object s, SkillHoverEventArgs e) { ShowTip(e.Node, e.ScreenPoint); };

            // How the points are spent, over the tree. The number itself is not
            // here: the tree writes it where the game writes it, on the plate at
            // the bottom left, and saying it twice only invites the two to
            // disagree.
            Label points = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                Padding = new Padding(10, 4, 10, 0),
                ForeColor = Color.Gainsboro,
                Text = Lang.T("skills.edit.hint"),
            };
            tree.Clicked += delegate (object s, SkillClickEventArgs e) { Spend(e); };

            Panel scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = page.BackColor };
            scroller.Controls.Add(tree);
            page.Controls.Add(scroller);
            // Added after the filling panel on purpose: WinForms docks the last
            // child first, so a Bottom control put in before Fill gets no room.
            page.Controls.Add(points);
            if (drop != null) page.Controls.Add(drop);
            return page;
        }

        // A click on a button of the tree. Left puts a point in, right takes one
        // back; the rules for both are SkillEdit's, and a refusal is said out loud
        // rather than silently ignored — otherwise a button that will not take a
        // point looks broken.
        void Spend(SkillClickEventArgs e)
        {
            if (e.Node == null || e.Button == MouseButtons.Middle) return;
            string dbr = e.Node.Dbr;
            bool add = e.Button == MouseButtons.Left;
            string why = add ? SkillEdit.WhyNotAdd(game, character, dbr)
                             : SkillEdit.WhyNotRemove(game, character, dbr);
            if (why != null)
            {
                if (why.Length > 0) tooltip.ShowFor(Refusal(e.Node, why), e.ScreenPoint);
                return;
            }
            if (!(add ? SkillEdit.Add(game, character, dbr) : SkillEdit.Remove(game, character, dbr))) return;

            // Every tree, not just this one: the pool is shared, and an exclusive
            // skill taken here closes the exclusives of the other mastery too.
            foreach (KeyValuePair<string, SkillTree> kv in Trees) kv.Value.Reload(character, bonus);
            handRange.Clear();
            ShowTip(e.Node, e.ScreenPoint);
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        // Why the click did nothing — said at the end of the skill's own
        // description, in the same tooltip, so the answer appears under the cursor
        // together with what it is about.
        ItemView Refusal(SkillNode node, string why)
        {
            ItemView v = BuildTip(node);
            v.Details.Add(new TipLine("", TipKind.Plain));
            v.Details.Add(new TipLine(why, TipKind.Warning));
            return v;
        }

        static string KindName(SkillKind kind)
        {
            switch (kind)
            {
                case SkillKind.Mastery: return Lang.T("skills.kind.mastery");
                case SkillKind.Active: return Lang.T("skills.kind.active");
                case SkillKind.WeaponPool: return Lang.T("skills.kind.weaponPool");
                case SkillKind.Modifier: return Lang.T("skills.kind.modifier");
                case SkillKind.Transmuter: return Lang.T("skills.kind.transmuter");
                default: return Lang.T("skills.kind.passive");
            }
        }

        // Every weapon pool skill of every mastery the character took: they share
        // one pool, so a chance only means something next to the others.
        List<SkillNode> AllWeaponPool()
        {
            List<SkillNode> found = new List<SkillNode>();
            foreach (KeyValuePair<string, SkillTree> kv in Trees)
                foreach (SkillNode n in kv.Value.Nodes)
                    if (n.Kind == SkillKind.WeaponPool && n.Level > 0) found.Add(n);
            return found;
        }

        // Does the skill carry damage at all — the note above is only about
        // damage, and attaching it to an unrelated defensive modifier would be noise.
        static bool HasDamageLine(GameData game, string dbr, int level)
        {
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(dbr); hop++)
            {
                DbRecord rec = game.Record(dbr);
                if (rec == null) return false;
                if (AttackSkills.HasDamage(rec, SkillText.Slot(rec, level))) return true;
                dbr = ItemViewFactory.NextSkillRecord(rec);
            }
            return false;
        }

        void ShowTip(SkillNode node, Point at)
        {
            if (node == null) { tooltip.Hide(); return; }
            tooltip.ShowFor(BuildTip(node), at);
        }

        // Build the complete skill tooltip so `--skillshot ... tip=<path-part>` can
        // save it for verification.
        public ItemView BuildTip(SkillNode node)
        {
            ItemView v = new ItemView();
            v.Name = node.Name;
            v.Details.Add(new TipLine(node.Name, TipKind.Name));

            v.Details.Add(new TipLine(KindName(node.Kind), TipKind.Subtle));

            string what = SkillText.Description(game, node.Dbr);
            if (what != null) v.Details.Add(new TipLine(what, TipKind.Subtle));

            // A weapon pool skill has no button: it fires instead of a plain
            // attack, with this chance. Of all the ones taken only one can fire
            // per attack — so the chances share a single hundred per cent, and
            // that is what makes them worth showing together.
            if (node.Kind == SkillKind.WeaponPool && node.Chance > 0)
            {
                v.Details.Add(new TipLine());
                v.Details.Add(new TipLine(Lang.T("skills.tip.poolChance", node.Chance), TipKind.Headline));
                int taken = 0, sum = 0;
                foreach (SkillNode other in AllWeaponPool())
                    if (other.Chance > 0) { taken++; sum += other.Chance; }
                if (taken > 1)
                    v.Details.Add(new TipLine(Lang.T(sum >= 100 ? "skills.tip.poolSharedFull" : "skills.tip.poolShared",
                                                     taken, sum), TipKind.Subtle));
            }
            if (node.DependsOn.Count > 0)
            {
                List<string> names = new List<string>();
                foreach (string on in node.DependsOn)
                {
                    string n = SkillText.Name(game, on);
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
                if (names.Count > 0)
                    v.Details.Add(new TipLine(Lang.T("skills.tip.dependsOn", string.Join(", ", names.ToArray())),
                                              TipKind.Subtle));
            }

            v.Details.Add(new TipLine());
            v.Details.Add(new TipLine((node.Bonus > 0 ? Lang.T("skills.tip.levelWithBonus", node.Level, node.Bonus)
                                                      : Lang.T("skills.tip.level", node.Level)) + "   " +
                                      (node.UltimateLevel > node.MaxLevel
                                          ? Lang.T("skills.tip.maxWithGear", node.MaxLevel, node.UltimateLevel)
                                          : Lang.T("skills.tip.max", node.MaxLevel)),
                                      TipKind.Headline));
            // AND THE NUMBERS OF A SKILL NOT TAKEN YET, read at level one.
            //
            // Until now the whole block below hung on `node.Level > 0`, so an
            // untaken skill showed its name, its kind and its words and not one
            // number. Level one is what the first point buys and matches the value
            // shown by the mastery bar below.
            //
            // The mastery bar is left out on purpose: its own block further down
            // already prints the next level, and both would print the same thing.
            bool untaken = !BreakUntakenTip && node.Level == 0 && !node.IsMastery && node.MaxLevel > 0;
            if (node.Level > 0 || untaken)
            {
                // Bonuses cannot push a skill past the record's own ceiling.
                int shown = untaken ? 1 : node.Total;
                if (node.UltimateLevel > 0 && shown > node.UltimateLevel) shown = node.UltimateLevel;
                v.Details.Add(new TipLine());
                // Said before the numbers, so nobody reads them as what the skill
                // gives right now: at the moment it gives nothing.
                if (untaken)
                    v.Details.Add(new TipLine(Lang.T("skills.tip.nextLevel", 1), TipKind.Section));
                int before = v.Details.Count;
                // The game displays cooldown after the character-wide reduction;
                // compute it once per window.
                double keepCut = ItemViewFactory.SkillCooldownCut;
                ItemViewFactory.SkillCooldownCut = CooldownCut;
                // Apply skill-specific tree and item modifiers before the global
                // character reduction.
                double keepAdd = ItemViewFactory.SkillCooldownAdd, keepMana = ItemViewFactory.SkillManaCut;
                double cdAdd, manaCut;
                TreeMods(node.Dbr, out cdAdd, out manaCut);
                ItemViewFactory.SkillCooldownAdd = cdAdd;
                ItemViewFactory.SkillManaCut = manaCut;
                // The tooltip lists weapon rows according to animation data and
                // current equipment. Calculation may use an explicit measured rule;
                // keep these two decisions separate.
                DbRecord own = game == null ? null : game.Record(node.Dbr);
                bool weaponClass = own != null;
                bool armed = character != null && game != null &&
                             SkillHands.SetOf(game, character) != WeaponSet.Unarmed;
                ItemViewFactory.SkillHandsTwo = weaponClass && armed;
                ItemViewFactory.SkillHandsNow = ItemViewFactory.SkillHandsTwo
                    ? SkillHands.Of(game, own, character)
                    : Hands.Both;
                ItemViewFactory.SkillHandRange = ItemViewFactory.SkillHandsTwo
                    ? HandRangeOf(node.Dbr, shown) : null;
                try { SkillText.Describe(game, node.Dbr, shown, v.Details, 0, Pets()); }
                finally
                {
                    ItemViewFactory.SkillCooldownCut = keepCut;
                    ItemViewFactory.SkillCooldownAdd = keepAdd;
                    ItemViewFactory.SkillManaCut = keepMana;
                    ItemViewFactory.SkillHandsTwo = false;
                    ItemViewFactory.SkillHandsNow = Hands.Both;
                    ItemViewFactory.SkillHandRange = null;
                }
                // Pet bonuses and summoned-pet rows are rendered inside `Describe`.
                // The game's tooltip shows runtime-scaled damage, while this row
                // intentionally displays the record value for comparison.
                if (v.Details.Count > before && HasDamageLine(game, node.Dbr, shown))
                    v.Details.Add(new TipLine(
                        Lang.T("skills.tip.recordNumbers") +
                        // The hand range is calculated rather than read from the
                        // record, so label it separately.
                        (ItemViewFactory.SkillHandRange != null ? " " + Lang.T("skills.tip.recordNumbersHand") : ""),
                        TipKind.Subtle));

                // Show the devotion power attached to this skill. Its values use
                // the star's own `DevotionLevel`.
                MapStars();
                string star2, ctl2;
                if (castsStar.TryGetValue(node.Dbr, out star2) && !string.IsNullOrEmpty(star2))
                {
                    castsBy.TryGetValue(node.Dbr, out ctl2);
                    string when2 = SkillText.ConditionOf(game, star2, ctl2, node.Dbr);
                    int starLevel = 1;
                    foreach (Skill one in character.Skills)
                        if (string.Equals(one.Name, star2, StringComparison.OrdinalIgnoreCase))
                        { starLevel = one.DevotionLevel > 0 ? one.DevotionLevel : one.Level; break; }
                    v.Details.Add(new TipLine());
                    v.Details.Add(new TipLine(
                        ItemViewFactory.Word(game, "tagSkillAutoCast", Lang.T("skills.tip.celestialPower")) + ": " +
                        (SkillText.Name(game, star2) ?? star2) + (when2 == null ? "" : " " + when2) +
                        "   " + Lang.T("skills.levelParen", starLevel), TipKind.Section));
                    SkillText.Describe(game, star2, starLevel, v.Details, 1, Pets());
                }

                // What worn gear adds to this skill. It is not in the save at all
                // Item skill modifiers apply immediately when the item is equipped;
                // label each modifier by its source item.
                foreach (ItemSkillMods.Entry e in ItemSkillMods.Of(game, character).For(node.Dbr))
                {
                    v.Details.Add(new TipLine());
                    v.Details.Add(new TipLine(Lang.T("skills.tip.fromItem", e.Source), TipKind.Section));
                    ItemViewFactory.RenderStats(game, e.Modifier, v.Details, 1, 0);
                    ItemViewFactory.RenderModifierExtras(game, e.Modifier, v.Details, 1);
                }
            }
            // For the mastery bar the game also shows what the next point buys,
            // which is the whole reason to hover it.
            if (node.IsMastery && node.Level < node.MaxLevel)
            {
                v.Details.Add(new TipLine());
                v.Details.Add(new TipLine(Lang.T("skills.tip.nextLevel", node.Level + 1), TipKind.Section));
                SkillText.Values(game, node.Dbr, node.Level + 1, v.Details, 1);
            }
            return v;
        }

        // `--tipbreak` disables first-level values for untaken skills.
        public static bool BreakUntakenTip;

        // `--tiptest <save> <folder>` checks every mastery node. Untaken skills
        // must start their values with the first-level marker; taken skills must
        // not duplicate that marker. Skills with no level-one values are reported,
        // but are not errors.
        #region Self-check — not interface (the `--tiptest` report, not moved into the language files)
        #endregion

        // Build a skill tooltip from a record-path fragment for screenshots and tests.
        public ItemView TipFor(string dbrPart)
        {
            foreach (KeyValuePair<string, SkillTree> kv in Trees)
                foreach (SkillNode n in kv.Value.Nodes)
                    if (!string.IsNullOrEmpty(n.Dbr) &&
                        n.Dbr.IndexOf(dbrPart, StringComparison.OrdinalIgnoreCase) >= 0)
                        return BuildTip(n);
            return null;
        }

        // ----------------------------------------------------------- devotion

        // THE STAR MAP ON THE LEFT, THE RUNNING TOTAL ON THE RIGHT. Until
        // 30 September the left half was a list of constellations in words; it is
        // now the sky itself, drawn with the game's own pictures (`DevotionMap`),
        // and points go in and out of it with the mouse. The bonuses of every star
        // taken are still added up on the right, with the granted skills and what
        // they are bound to.
        TabPage DevotionPage()
        {
            TabPage page = new TabPage(Lang.T("skills.devotion.tab"));
            page.BackColor = Color.FromArgb(20, 19, 18);

            Dictionary<string, int> takenStars;
            List<TipLine> lines = DevotionTotals(out takenStars);
            DevotionLines = lines;
            return DevotionPageBody(page, lines, takenStars);
        }

        // THE RUNNING TOTAL OF EVERY STAR, and which stars are taken. It is a method
        // of its own because a click on the map has to build it again: the point
        // that just went in changes the sums on the right.
        List<TipLine> DevotionTotals(out Dictionary<string, int> takenStars)
        {
            devoTerms.Clear();
            List<TipLine> lines = new List<TipLine>();
            Dictionary<string, double> totals = new Dictionary<string, double>(StringComparer.Ordinal);
            List<Skill> granted = new List<Skill>();
            // Every star the save has a point in, with its level — the map on the
            // left is drawn from this.
            Dictionary<string, int> taken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int stars = 0;

            // What a class skill has a devotion skill hung on. The binding lives
            // on the class skill's entry, not on the devotion one.
            Dictionary<string, string> boundTo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> boundBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Skill s in character.Skills)
                if (!string.IsNullOrEmpty(s.AutoCastSkill))
                {
                    boundTo[s.AutoCastSkill] = s.Name;
                    boundBy[s.AutoCastSkill] = s.AutoCastController;
                }
            // A star can hang on a skill an item grants as well as on one of the
            // character's own, and those live in their own list.
            foreach (ItemSkill s in character.ItemSkills)
                if (!string.IsNullOrEmpty(s.AutoCastSkill))
                {
                    boundTo[s.AutoCastSkill] = s.SkillName;
                    boundBy[s.AutoCastSkill] = s.AutoCastController;
                }

            foreach (Skill s in character.Skills)
            {
                if (!IsDevotion(s.Name)) continue;
                DbRecord rec = game.Record(s.Name);
                if (rec == null) continue;

                // A star is one devotion point and gives a passive bonus. A star
                // that grants a skill stores that skill as a second entry, which
                // levels itself from use — it is not another point, and its
                // numbers belong to the skill, not to the running total.
                bool passive = (rec.Class ?? "").StartsWith("Skill_Passive", StringComparison.OrdinalIgnoreCase);
                if (passive)
                {
                    if (s.Level <= 0) continue;
                    stars++;
                    taken[s.Name] = s.Level;
                    SkillText.Accumulate(rec, s.Level, totals, devoTerms, WhereFrom(s.Name));
                }
                else
                {
                    // A star's effective level is `Level`, not stored devotion XP.
                    // Untaken stars are shown at level one; character totals include
                    // only taken stars.
                    if (s.Level <= 0) continue;
                    int level = s.DevotionLevel > 0 ? s.DevotionLevel : s.Level;
                    granted.Add(s);
                    taken[s.Name] = level;
                }
            }

            // Every star is one point: those that give a passive bonus and those
            // that grant a skill. The two counts together match TotalDevotion.
            lines.Add(new TipLine(Lang.T("skills.devotion.spent", stars + granted.Count, character.TotalDevotion,
                                         character.DevotionPoints), TipKind.Headline));
            lines.Add(new TipLine(Lang.T("skills.devotion.split", stars, granted.Count), TipKind.Subtle));
            lines.Add(new TipLine());
            lines.Add(new TipLine(Lang.T("skills.devotion.sums"), TipKind.Section));
            List<TipLine> sums = SkillText.RenderSums(game, totals);
            if (sums.Count == 0) lines.Add(new TipLine(Lang.T("skills.devotion.none"), TipKind.Subtle, 1));
            else foreach (TipLine l in sums) { l.Indent = 1; lines.Add(l); }

            if (granted.Count > 0)
            {
                lines.Add(new TipLine());
                lines.Add(new TipLine(Lang.T("skills.devotion.granted"), TipKind.Section));
                foreach (Skill s in granted)
                {
                    int level = s.DevotionLevel > 0 ? s.DevotionLevel : s.Level;
                    lines.Add(new TipLine());
                    // The game places the trigger condition directly after the name;
                    // keep the same ordering.
                    string ctl, owner;
                    boundBy.TryGetValue(s.Name, out ctl);
                    // Pass the bound activator so the displayed chance includes its
                    // modifiers rather than the raw `procRate`.
                    boundTo.TryGetValue(s.Name, out owner);
                    string when = SkillText.ConditionOf(game, s.Name, ctl, owner);
                    lines.Add(new TipLine("» " + SkillText.Name(game, s.Name) +
                                          (when == null ? "" : " " + when) +
                                          "   " + Lang.T("skills.levelParen", level), TipKind.Skill, 1));
                    lines.Add(owner != null
                        ? new TipLine(Lang.T("skills.devotion.boundTo", SkillText.Name(game, owner)), TipKind.Section, 2)
                        : new TipLine(Lang.T("skills.devotion.unbound"), TipKind.Warning, 2));
                    string what = SkillText.Description(game, s.Name);
                    if (what != null) lines.Add(new TipLine(what, TipKind.Subtle, 2));
                    SkillText.Describe(game, s.Name, level, lines, 2, Pets());
                }
            }

            takenStars = taken;
            return lines;
        }

        // The half of the tab that is controls rather than text.
        TabPage DevotionPageBody(TabPage page, List<TipLine> lines, Dictionary<string, int> taken)
        {
            // The map itself on the left, the running total on the right. Without
            // the map a sum of fifty stars says nothing about which constellation
            // it came from, and that is the only thing a player wants to see.
            SplitContainer split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor = Color.FromArgb(20, 19, 18),
            };
            ConstellationLines = Constellations(taken);
            // THE CONSTELLATIONS IN WORDS ARE OUT OF SIGHT since 30 September — the
            // map took their place, as the user asked. The lines are still built:
            // `--skillshot` writes them into `*.constellations.txt`, and the map's
            // reading of the database is checked against them.
            // split.Panel1.Controls.Add(new TipView(ConstellationLines) { Dock = DockStyle.Fill });
            Map = new DevotionMap(game, character) { Dock = DockStyle.Fill };
            Map.Hovered += delegate (object s, DevotionHoverEventArgs e) { ShowStarTip(e.Star, e.ScreenPoint); };
            Map.Clicked += delegate (object s, DevotionClickEventArgs e) { SpendDevotion(e); };
            split.Panel1.Controls.Add(Map);
            split.Panel1.Controls.Add(ZoomBar());
            devoTotals = new TipView(lines) { Dock = DockStyle.Fill };
            devoTotals.Hovered += delegate (object s, TipHoverEventArgs e) { ShowDevoTerms(e); };
            split.Panel2.Controls.Add(devoTotals);
            page.Controls.Add(split);
            // Edit devotion skill levels and rebuild the affected views.
            Panel top = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(20, 19, 18) };
            Button levels = new Button
            {
                Text = Lang.T("skills.devotion.levels"),
                Left = 8, Top = 6, Height = 28,
                ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(38, 35, 32), FlatStyle = FlatStyle.Flat,
            };
            levels.Width = TextRenderer.MeasureText(levels.Text, levels.Font).Width + 30;
            levels.Click += delegate { EditDevotionLevels(); };
            top.Controls.Add(levels);
            Label hint = new Label
            {
                AutoSize = true, Left = levels.Right + 14, Top = 12,
                ForeColor = Color.DimGray, Text = Lang.T("skills.devo.hint"),
            };
            top.Controls.Add(hint);
            page.Controls.Add(top);
            // The map needs the room: it is the thing being looked at, and the sums
            // beside it are a column of short lines.
            page.HandleCreated += delegate { split.SplitterDistance = (int)(split.Width * 0.62); };
            return page;
        }

        // The map's own zoom, the way the reference calculator does it: two buttons
        // and the wheel. The game has zoom buttons of its own in its side bar, which
        // we do not draw.
        Panel ZoomBar()
        {
            Panel bar = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = Color.FromArgb(20, 19, 18) };
            Button out_ = ZoomButton("−", 8);
            Button in_ = ZoomButton("+", out_.Right + 6);
            Button fit = ZoomButton(Lang.T("skills.devo.fit"), in_.Right + 10);
            fit.Width = TextRenderer.MeasureText(fit.Text, fit.Font).Width + 24;
            Label scale = new Label { AutoSize = true, Left = fit.Right + 12, Top = 9, ForeColor = Color.DimGray };
            EventHandler show = delegate { scale.Text = Lang.T("skills.devo.zoom", (int)Math.Round(Map.Zoom * 100)); };
            out_.Click += delegate { Map.ZoomBy(1 / 1.25f); };
            in_.Click += delegate { Map.ZoomBy(1.25f); };
            fit.Click += delegate { Map.Fit(); Map.Invalidate(); show(null, EventArgs.Empty); };
            Map.ZoomChanged += show;
            bar.HandleCreated += delegate { show(null, EventArgs.Empty); };
            bar.Controls.AddRange(new Control[] { out_, in_, fit, scale });
            return bar;
        }

        Button ZoomButton(string text, int left)
        {
            return new Button
            {
                Text = text, Left = left, Top = 3, Width = 30, Height = 26,
                ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(38, 35, 32), FlatStyle = FlatStyle.Flat,
            };
        }

        // A click on a star. Left puts a point in, right takes one back; the rules
        // for both are `DevotionEdit`'s, and a refusal is said out loud rather than
        // silently ignored — the same way the mastery trees answer.
        void SpendDevotion(DevotionClickEventArgs e)
        {
            if (e.Star == null || e.Button == MouseButtons.Middle) return;
            bool add = e.Button == MouseButtons.Left;
            string why = add ? DevotionEdit.WhyNotAdd(game, character, e.Star)
                             : DevotionEdit.WhyNotRemove(game, character, e.Star);
            if (why != null)
            {
                // A CLICK THE RULES TURN DOWN, ON A TAKEN STAR THAT CARRIES A
                // CELESTIAL POWER, OPENS THE CHOOSER INSTEAD. That is the same door
                // GrimTools' calculator uses (`onStarClick`: if the point neither
                // went in nor came out and the star carries a power, open it), and
                // there is no other click left to spend on it — the left button
                // already puts points in and the right takes them out.
                if (add && e.Star.Power && DevotionEdit.LevelOf(character, e.Star.Dbr) > 0)
                { ChooseBinding(e.Star, e.ScreenPoint); return; }
                if (why.Length > 0) ShowStarTip(e.Star, e.ScreenPoint, why);
                return;
            }
            if (!(add ? DevotionEdit.Add(game, character, e.Star)
                      : DevotionEdit.Remove(game, character, e.Star))) return;

            // Everything the point changed, in one pass: the sums on the right, the
            // map's own reading of what is taken, and the main window's "unsaved".
            Dictionary<string, int> taken;
            List<TipLine> fresh = DevotionTotals(out taken);
            DevotionLines = fresh;
            ConstellationLines = Constellations(taken);
            if (devoTotals != null) devoTotals.SetLines(fresh);
            Map.Reload(character);
            if (Changed != null) Changed(this, EventArgs.Empty);
            ShowStarTip(e.Star, e.ScreenPoint);
        }

        // Choosing the skill a star's celestial power hangs on. The rules are
        // `DevotionBinding`'s.
        void ChooseBinding(DevotionStar star, Point at)
        {
            devoTip.Hide();
            using (DevotionBindForm dlg = new DevotionBindForm(game, character, star))
            {
                dlg.ShowDialog(this);
                if (!dlg.Changed) return;
            }
            Dictionary<string, int> taken;
            List<TipLine> fresh = DevotionTotals(out taken);
            DevotionLines = fresh;
            if (devoTotals != null) devoTotals.SetLines(fresh);
            Map.Reload(character);
            if (Changed != null) Changed(this, EventArgs.Empty);
            ShowStarTip(star, at);
        }

        // What a star says under the cursor: its constellation, what it gives, and —
        // after a click that did nothing — why it did nothing.
        void ShowStarTip(DevotionStar star, Point at) { ShowStarTip(star, at, null); }

        void ShowStarTip(DevotionStar star, Point at, string why)
        {
            if (star == null) { devoTip.Hide(); return; }
            devoTip.Owner = this;
            devoTip.ShowFor(StarTip(star, why), at);
        }

        public ItemView StarTip(DevotionStar star, string why)
        {
            ItemView v = new ItemView();
            v.Name = DevotionEdit.StarName(game, star);
            v.Details.Add(new TipLine(v.Name, TipKind.Name));
            v.Details.Add(new TipLine(star.Home.Name, TipKind.Subtle));

            // AFFINITY AS THE GAME SHOWS IT: a coloured ball and a figure, with the
            // Use the game's own affinity headings so the view matches the game.
            TipLine gives = Affinity(star.Home.Gives, game.Tag("tagDevotionAffinityBonus"));
            if (gives != null) v.Details.Add(gives);
            TipLine needs = Affinity(star.Home.Needs, game.Tag("FailedDevotionDependency"));
            if (needs != null) v.Details.Add(needs);
            v.Details.Add(new TipLine(Lang.T("skills.devo.stars",
                                             DevotionEdit.TakenIn(character, star.Home), star.Home.Stars.Count),
                                      TipKind.Subtle));

            int level = DevotionEdit.LevelOf(character, star.Dbr);
            // A celestial power does nothing until it is tied to a skill of the
            // character's own, so the tie is said right here, with the way to change
            // it. Which skills it may be tied to is the game's own business —
            // `DevotionBinding`.
            if (star.Power)
            {
                string owner = DevotionBinding.BoundTo(character, star.Dbr);
                v.Details.Add(new TipLine());
                v.Details.Add(owner != null
                    ? new TipLine(Lang.T("skills.devotion.boundTo", SkillText.Name(game, owner) ?? owner), TipKind.Section)
                    : new TipLine(Lang.T("skills.devotion.unbound"), TipKind.Warning));
                if (level > 0) v.Details.Add(new TipLine(Lang.T("skills.devo.bindHint"), TipKind.Subtle));
            }
            v.Details.Add(new TipLine());
            string what = SkillText.Description(game, star.Dbr);
            if (what != null) v.Details.Add(new TipLine(what, TipKind.Subtle));
            SkillText.Describe(game, star.Dbr, Math.Max(1, StarLevel(star)), v.Details, 0, Pets());
            if (level <= 0) v.Details.Add(new TipLine(Lang.T("skills.constellations.notTaken"), TipKind.Subtle));
            if (why != null)
            {
                v.Details.Add(new TipLine());
                v.Details.Add(new TipLine(why, TipKind.Warning));
            }
            return v;
        }

        // One line of affinity: the game's heading, then a ball and a figure for each
        // kind the constellation names. Null when it names none.
        TipLine Affinity(Dictionary<string, int> counts, string heading)
        {
            if (counts == null || counts.Count == 0) return null;
            TipLine line = new TipLine(GameData.Form(heading, null) ?? "", TipKind.Subtle);
            line.Orbs = new List<TipOrb>();
            foreach (string name in DevotionEdit.AffinityOrder)
            {
                int many;
                if (!counts.TryGetValue(name, out many) || many <= 0) continue;
                line.Orbs.Add(new TipOrb(AffinityOrb(name), many.ToString()));
            }
            return line.Orbs.Count == 0 ? null : line;
        }

        // The five balls, read once per window: the tooltip is rebuilt on every
        // movement of the mouse.
        Dictionary<string, Image> orbs;

        Image AffinityOrb(string name)
        {
            if (orbs == null)
            {
                orbs = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
                foreach (string one in DevotionEdit.AffinityOrder)
                {
                    string path = DevotionEdit.AffinityIcon(game, one);
                    if (string.IsNullOrEmpty(path)) continue;
                    Image got = game.SkillIcon(path);
                    if (got == null && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        got = game.SkillIcon(path.Substring(0, path.Length - 4) + ".tex");
                    if (got != null) orbs[one] = got;
                }
            }
            Image found;
            return orbs.TryGetValue(name ?? "", out found) ? found : null;
        }

        // A star that grants a skill levels itself from use, and its own level is
        // what the numbers have to be read at.
        int StarLevel(DevotionStar star)
        {
            foreach (Skill s in character.Skills)
                if (string.Equals(s.Name, star.Dbr, StringComparison.OrdinalIgnoreCase))
                    return s.DevotionLevel > 0 ? s.DevotionLevel : Math.Max(1, s.Level);
            return 1;
        }

        // Open the devotion-level editor. A change rebuilds the form and marks the
        // save for writing.
        void EditDevotionLevels()
        {
            using (DevotionLevelsForm dlg = new DevotionLevelsForm(game, character))
            {
                dlg.ShowDialog(this);
                if (!dlg.Changed) return;
            }
            Rebuilt();
            foreach (Control c in Controls)
            {
                TabControl t = c as TabControl;
                if (t == null) continue;
                foreach (TabPage p in t.TabPages)
                    if (p.Text == Lang.T("skills.devotion.tab")) t.SelectedTab = p;
            }
        }

        // Every field of the devotion total, with the stars that fed it. Filled
        // while the stars are added up; read by the hover below.
        readonly Dictionary<string, List<KeyValuePair<string, double>>> devoTerms =
            new Dictionary<string, List<KeyValuePair<string, double>>>(StringComparer.Ordinal);
        readonly ItemTooltip devoTip = new ItemTooltip();

        // Which constellation a star belongs to — that is what the user wants to
        // read beside a number, not the record path.
        Dictionary<string, string> starHome;

        string WhereFrom(string starDbr)
        {
            if (starHome == null)
            {
                starHome = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Constellation c in DevotionTable.Of(game))
                {
                    int n = 0;
                    foreach (string star in c.Stars)
                    {
                        n++;
                        if (!starHome.ContainsKey(star)) starHome[star] = Lang.T("skills.devotion.starOf", c.Name, n);
                    }
                }
            }
            string home;
            if (starHome.TryGetValue(starDbr, out home)) return home;
            return SkillText.Name(game, starDbr) ?? starDbr;
        }

        void ShowDevoTerms(TipHoverEventArgs e)
        {
            if (e.Line == null || e.Line.Explains == null) { devoTip.Hide(); return; }
            ItemView v = new ItemView();
            v.Name = e.Line.Text;
            v.Details.Add(new TipLine(e.Line.Text, TipKind.Name));
            bool anything = false;
            foreach (string field in e.Line.Explains)
            {
                List<KeyValuePair<string, double>> got;
                if (!devoTerms.TryGetValue(field, out got) || got.Count == 0) continue;
                anything = true;
                double sum = 0;
                v.Details.Add(new TipLine());
                v.Details.Add(new TipLine(field, TipKind.Section));
                foreach (KeyValuePair<string, double> term in got)
                {
                    sum += term.Value;
                    v.Details.Add(new TipLine(Num(term.Value) + "   " + term.Key, TipKind.Bonus, 1));
                }
                v.Details.Add(new TipLine(Lang.T("skills.devotion.total", Num(sum)), TipKind.Headline, 1));
            }
            if (!anything) { devoTip.Hide(); return; }
            devoTip.Owner = this;
            devoTip.ShowFor(v, e.ScreenPoint);
        }

        static string Num(double v)
        {
            return Math.Abs(v - Math.Round(v)) < 0.005
                ? ((long)Math.Round(v)).ToString()
                : v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }

        // Every constellation of the game, with the stars this character has
        // taken marked. Nothing is computed: the map is read out of
        // records/ui/skills/devotion/constellations (see DevotionTable), and
        // "taken" is the star list of the save.
        List<TipLine> Constellations(Dictionary<string, int> taken)
        {
            List<TipLine> lines = new List<TipLine>();
            List<Constellation> all = DevotionTable.Of(game);
            int done = 0, started = 0;
            foreach (Constellation c in all)
            {
                int mine = 0;
                foreach (string star in c.Stars) if (taken.ContainsKey(star)) mine++;
                if (mine >= c.Stars.Count && c.Stars.Count > 0) done++;
                else if (mine > 0) started++;
            }
            // Touched constellations first: the ones with nothing in them are
            // the long tail, and scrolling past a hundred of them to find what
            // the character actually has is the wrong way round.
            all.Sort(delegate (Constellation a, Constellation b)
            {
                int ma = 0, mb = 0;
                foreach (string s in a.Stars) if (taken.ContainsKey(s)) ma++;
                foreach (string s in b.Stars) if (taken.ContainsKey(s)) mb++;
                if ((ma > 0) != (mb > 0)) return ma > 0 ? -1 : 1;
                if (a.RequiredTotal != b.RequiredTotal) return a.RequiredTotal.CompareTo(b.RequiredTotal);
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            });
            lines.Add(new TipLine(Lang.T("skills.constellations.count", all.Count, done, started), TipKind.Headline));
            lines.Add(new TipLine(Lang.T("skills.constellations.legend"), TipKind.Subtle));

            foreach (Constellation c in all)
            {
                int mine = 0;
                foreach (string star in c.Stars) if (taken.ContainsKey(star)) mine++;
                bool full = mine >= c.Stars.Count;
                lines.Add(new TipLine());
                lines.Add(new TipLine(c.Name + "   " + mine + "/" + c.Stars.Count +
                                      (full ? "  ✔" : ""),
                                      mine > 0 ? TipKind.Section : TipKind.Subtle));
                string gives = string.Join(", ", c.Given.ToArray());
                string needs = string.Join(", ", c.Required.ToArray());
                if (gives.Length > 0 || needs.Length > 0)
                    lines.Add(new TipLine(Lang.T("skills.constellations.gives", gives.Length > 0 ? gives : "—") +
                                          (needs.Length > 0 ? "    " + Lang.T("skills.constellations.needs", needs) : ""),
                                          TipKind.Subtle, 1));
                int number = 0;
                foreach (string star in c.Stars)
                {
                    number++;
                    bool has = taken.ContainsKey(star);
                    // Ordinary stars carry no name of their own — the game shows
                    // the constellation's — so numbering them is the only way to
                    // tell one from another. A star that grants a skill does have
                    // a name, and that one is worth showing.
                    DbRecord rec = game.Record(star);
                    // A star that grants a skill is not a bonus, and the panel on
                    // the right lists it separately — so it wears the same colour
                    // there and here.
                    bool grants = rec != null && !(rec.Class ?? "")
                        .StartsWith("Skill_Passive", StringComparison.OrdinalIgnoreCase);
                    string name = SkillText.Name(game, star) ?? star;
                    if (string.Equals(name, c.Name, StringComparison.CurrentCultureIgnoreCase))
                        name = Lang.T("skills.constellations.star", number);
                    // A star not taken yet is shown at its first level: that is
                    // what one point in it would buy, and it is the whole point
                    // of having the map here — deciding where the next point goes
                    // needs the numbers of the stars you do not have.
                    int level = has ? taken[star] : 1;
                    if (grants)
                    {
                        // The trigger the game writes beside the name; for a star
                        // bound to nothing it comes from templateAutoCast, which
                        // is what the game shows too.
                        // Include the bound activator so the chance matches the game.
                        string when = SkillText.ConditionOf(game, star, null, ActivatorOf(star));
                        lines.Add(new TipLine((has ? "● " : "○ ") + name +
                                              (when == null ? "" : " " + when) +
                                              "   " + (has ? Lang.T("skills.levelParen", level)
                                                           : Lang.T("skills.constellations.notTaken")),
                                              has ? TipKind.Skill : TipKind.Subtle, 1));
                    }
                    else
                        lines.Add(new TipLine((has ? "● " : "○ ") + name,
                                              has ? TipKind.Bonus : TipKind.Subtle, 1));

                    // The numbers themselves, taken or not. A granted skill has
                    // them too — they live in the record it hands off to, and
                    // SkillText.Values follows that chain.
                    List<TipLine> values = new List<TipLine>();
                    SkillText.Describe(game, star, level, values, 2, Pets());
                    // Green means "this is yours". A constellation with nothing
                    // taken has to be grey down to the last line, or the eye
                    // reads half of it as taken.
                    if (!has) foreach (TipLine l in values) l.Kind = TipKind.Subtle;
                    lines.AddRange(values);
                }
            }
            return lines;
        }

        // ----------------------------------------------------------- portals

        // Riftgates and faction standings are changed right in this window, so the
        // form has to tell whoever opened it that the save needs writing.
        public event EventHandler Changed;

        // Read difficulty names from the language file on each access.
        static string[] DifficultyNames
        {
            get { return new[] { Lang.T("common.difficulty.normal"), Lang.T("common.difficulty.elite"), Lang.T("common.difficulty.ultimate") }; }
        }

        PortalTable portals;
        readonly List<CheckBox> portalBoxes = new List<CheckBox>();
        Label portalSummary;
        ListView portalList;

        // Block 6 of the save is one list of sixteen-byte identifiers per
        // difficulty, kept flat with a null between lists. This is the same list
        // of lists, in a shape that can be worked with.
        List<List<byte[]>> PortalLists()
        {
            List<List<byte[]>> lists = new List<List<byte[]>>();
            List<byte[]> flat;
            if (!character.UidLists.TryGetValue(6, out flat)) return lists;
            List<byte[]> current = new List<byte[]>();
            foreach (byte[] uid in flat)
            {
                if (uid == null) { lists.Add(current); current = new List<byte[]>(); continue; }
                current.Add(uid);
            }
            if (current.Count > 0) lists.Add(current);
            return lists;
        }

        void SetPortalLists(List<List<byte[]>> lists)
        {
            List<byte[]> flat = new List<byte[]>();
            foreach (List<byte[]> one in lists)
            {
                flat.AddRange(one);
                flat.Add(null);
            }
            character.UidLists[6] = flat;
        }

        TabPage PortalPage()
        {
            TabPage page = new TabPage(Lang.T("skills.portals.tab"));
            page.BackColor = Color.FromArgb(20, 19, 18);

            Panel top = new Panel { Dock = DockStyle.Top, Height = 146, BackColor = page.BackColor };
            portalSummary = new Label
            {
                Left = 12, Top = 10, Width = 1000, Height = 44, AutoSize = false,
                ForeColor = Color.FromArgb(210, 205, 195)
            };
            top.Controls.Add(portalSummary);

            portalList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BackColor = Color.FromArgb(26, 24, 22),
                ForeColor = Color.FromArgb(210, 205, 195),
                BorderStyle = BorderStyle.None
            };
            portalList.Columns.Add(Lang.T("skills.portals.column"), 320);
            for (int d = 0; d < DifficultyNames.Length; d++) portalList.Columns.Add(DifficultyNames[d], 90);
            // Hide record paths from the UI, but keep reading `Gate.Dbr` for list
            // construction and `--portaltest`.
            // portalList.Columns.Add("record", 420);

            portals = LoadPortals();
            if (portals == null)
            {
                portalSummary.Text = Lang.T("skills.portals.notBuilt");
                Button build = new Button
                {
                    Left = 12, Top = 66, Width = 260, Height = 30,
                    Text = Lang.T("skills.portals.build"),
                    UseVisualStyleBackColor = false,
                    BackColor = Color.FromArgb(54, 50, 46),
                    ForeColor = Color.FromArgb(225, 220, 210)
                };
                build.Click += delegate { BuildPortals(page, build); };
                top.Controls.Add(build);
                page.Controls.Add(portalList);
                page.Controls.Add(top);
                return page;
            }

            BuildPortalControls(top);
            page.Controls.Add(portalList);
            page.Controls.Add(top);
            RefreshPortals();
            return page;
        }

        PortalTable LoadPortals()
        {
            PortalTable t = PortalTable.Load(PortalTable.CachePath);
            if (t == null) return null;
            // A patch can move gates around, so a list built from another build of
            // the game is not trusted.
            if (!t.MatchesGame(game.GameDir)) return null;
            t.Rename(game);
            return t;
        }

        void BuildPortals(TabPage page, Button trigger)
        {
            trigger.Enabled = false;
            Cursor = Cursors.WaitCursor;
            portalSummary.Text = Lang.T("skills.portals.reading");
            Application.DoEvents();
            try
            {
                portals = PortalTable.Build(game, game.GameDir, delegate (string what)
                {
                    portalSummary.Text = Lang.T("skills.portals.readingWhat", what);
                    Application.DoEvents();
                });
                portals.Save(PortalTable.CachePath);
                trigger.Visible = false;
                BuildPortalControls((Panel)trigger.Parent);
                RefreshPortals();
            }
            catch (Exception e)
            {
                portalSummary.Text = Lang.T("skills.portals.readError", e.Message);
                trigger.Enabled = true;
            }
            finally { Cursor = Cursors.Default; }
        }

        void BuildPortalControls(Panel top)
        {
            portalBoxes.Clear();
            int lists = PortalLists().Count;
            for (int d = 0; d < lists && d < DifficultyNames.Length; d++)
            {
                CheckBox box = new CheckBox
                {
                    Left = 12 + d * 260, Top = 66, Width = 250, Height = 26,
                    Text = DifficultyNames[d],
                    ForeColor = Color.FromArgb(210, 205, 195)
                };
                int which = d;
                box.Click += delegate { TogglePortals(which, box); };
                portalBoxes.Add(box);
                top.Controls.Add(box);
            }

            CheckBox all = new CheckBox
            {
                Left = 12, Top = 104, Width = 420, Height = 26,
                Text = Lang.T("skills.portals.openAll"),
                ForeColor = Color.FromArgb(210, 205, 195)
            };
            all.Click += delegate { TogglePortals(-1, all); };
            portalBoxes.Add(all);
            top.Controls.Add(all);
        }

        // Checking opens every gate of that difficulty; unchecking takes them all
        // away again, which is a real loss of the character's exploring, so it is
        // asked about first.
        void TogglePortals(int difficulty, CheckBox box)
        {
            if (portals == null) return;
            List<List<byte[]>> lists = PortalLists();
            bool open = box.Checked;

            if (!open)
            {
                string ask = difficulty < 0 ? Lang.T("skills.portals.closeAll")
                                            : Lang.T("skills.portals.closeOne", DifficultyNames[difficulty]);
                if (MessageBox.Show(this, ask, Lang.T("skills.portals.title"),
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                { box.Checked = true; return; }
            }

            for (int d = 0; d < lists.Count; d++)
            {
                if (difficulty >= 0 && d != difficulty) continue;
                if (!open) { lists[d].Clear(); continue; }

                HashSet<string> have = new HashSet<string>(StringComparer.Ordinal);
                foreach (byte[] uid in lists[d]) have.Add(BitConverter.ToString(uid).Replace("-", ""));
                foreach (Portal g in portals.Portals)
                    if (!have.Contains(g.Hex)) lists[d].Add((byte[])g.Uid.Clone());
            }

            SetPortalLists(lists);
            RefreshPortals();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        void RefreshPortals()
        {
            List<List<byte[]>> lists = PortalLists();
            List<HashSet<string>> open = new List<HashSet<string>>();
            foreach (List<byte[]> one in lists)
            {
                HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
                foreach (byte[] uid in one) set.Add(BitConverter.ToString(uid).Replace("-", ""));
                open.Add(set);
            }

            string[] difficultyNames = DifficultyNames;
            System.Text.StringBuilder opened = new System.Text.StringBuilder();
            for (int d = 0; d < lists.Count && d < difficultyNames.Length; d++)
            {
                if (d > 0) opened.Append(",   ");
                opened.Append(difficultyNames[d]).Append(' ').Append(open[d].Count);
            }
            portalSummary.Text = Lang.T("skills.portals.summary", portals.Portals.Count, opened.ToString()) +
                                 Environment.NewLine + Lang.T("skills.portals.source");

            for (int d = 0; d < portalBoxes.Count - 1 && d < open.Count; d++)
            {
                CheckBox box = portalBoxes[d];
                bool allOpen = open[d].Count >= portals.Portals.Count;
                box.Text = Lang.T("skills.portals.openDifficulty", difficultyNames[d], open[d].Count, portals.Portals.Count);
                box.Checked = allOpen;
            }
            if (portalBoxes.Count > 0)
            {
                bool everywhere = true;
                foreach (HashSet<string> set in open) if (set.Count < portals.Portals.Count) everywhere = false;
                portalBoxes[portalBoxes.Count - 1].Checked = everywhere && open.Count > 0;
            }

            portalList.BeginUpdate();
            portalList.Items.Clear();
            foreach (Portal g in portals.Portals)
            {
                ListViewItem row = new ListViewItem(g.Name);
                for (int d = 0; d < difficultyNames.Length; d++)
                    row.SubItems.Add(d < open.Count && open[d].Contains(g.Hex) ? Lang.T("skills.portals.open") : "—");
                // row.SubItems.Add(g.Dbr);   // record column hidden above
                portalList.Items.Add(row);
            }
            portalList.EndUpdate();
        }

        // ---------------------------------------------------------- factions

        // Faction standings. Everything shown here — the names, the ladder of
        // standings and the order of the rows — is read from the game's own files
        // by `FactionTable`; the shape of the tab follows the riftgates beside it.
        FactionTable factions;
        Label factionSummary;
        ListView factionList;
        ComboBox factionRankBox;
        NumericUpDown factionValueBox;
        CheckBox factionShowHidden;

        TabPage FactionPage()
        {
            TabPage page = new TabPage(Lang.T("skills.factions.tab"));
            page.BackColor = Color.FromArgb(20, 19, 18);

            // Taller than the riftgate panel beside it: the summary runs to three
            // lines when the save is older than the game's list of factions.
            Panel top = new Panel { Dock = DockStyle.Top, Height = 152, BackColor = page.BackColor };
            factionSummary = new Label
            {
                Left = 12, Top = 8, Width = 1040, Height = 58, AutoSize = false,
                ForeColor = Color.FromArgb(210, 205, 195)
            };
            top.Controls.Add(factionSummary);

            factionList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true,
                GridLines = false,
                BackColor = Color.FromArgb(26, 24, 22),
                ForeColor = Color.FromArgb(210, 205, 195),
                BorderStyle = BorderStyle.None
            };
            factionList.Columns.Add(Lang.T("skills.factions.column"), 260);
            factionList.Columns.Add(Lang.T("skills.factions.rank"), 160);
            factionList.Columns.Add(Lang.T("skills.factions.value"), 100);
            factionList.Columns.Add(Lang.T("skills.factions.boost"), 110);
            factionList.Columns.Add(Lang.T("skills.factions.notes"), 300);

            factions = FactionTable.Build(game);
            if (factions.Factions.Count == 0 || factions.Ranks.Count == 0)
            {
                factionSummary.Text = Lang.T("skills.factions.unreadable");
                page.Controls.Add(factionList);
                page.Controls.Add(top);
                return page;
            }

            // Pick a standing and the number beside it fills in; or type a number
            // of your own, because the game keeps the progress inside a standing
            // and the next-rank progress is read from it.
            Label rankLabel = new Label
            {
                Left = 12, Top = 78, Width = 80, Height = 22, AutoSize = false,
                Text = Lang.T("skills.factions.setRank"), ForeColor = Color.FromArgb(210, 205, 195)
            };
            top.Controls.Add(rankLabel);
            factionRankBox = new ComboBox
            {
                Left = 96, Top = 74, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 37, 34), ForeColor = Color.FromArgb(225, 220, 210)
            };
            foreach (FactionRank r in factions.Ranks) factionRankBox.Items.Add(r.Name + "   " + r.From);
            factionRankBox.SelectedIndexChanged += delegate
            {
                int n = factionRankBox.SelectedIndex;
                if (n >= 0 && n < factions.Ranks.Count) factionValueBox.Value = factions.Ranks[n].From;
            };
            top.Controls.Add(factionRankBox);

            Label exactLabel = new Label
            {
                Left = 330, Top = 78, Width = 80, Height = 22, AutoSize = false,
                Text = Lang.T("skills.factions.exact"), ForeColor = Color.FromArgb(210, 205, 195)
            };
            top.Controls.Add(exactLabel);
            factionValueBox = new NumericUpDown
            {
                Left = 414, Top = 74, Width = 110,
                Minimum = factions.Lowest, Maximum = factions.Highest, DecimalPlaces = 0,
                Increment = 500, Value = factions.Highest,
                BackColor = Color.FromArgb(40, 37, 34), ForeColor = Color.FromArgb(225, 220, 210),
                BorderStyle = BorderStyle.FixedSingle
            };
            top.Controls.Add(factionValueBox);
            // The box starts on the top standing, so what is picked and what is
            // about to be written agree before anything is touched.
            factionRankBox.SelectedIndex = factions.Ranks.Count - 1;

            Button apply = new Button
            {
                Left = 540, Top = 73, Width = 220, Height = 28,
                Text = Lang.T("skills.factions.apply"),
                UseVisualStyleBackColor = false,
                BackColor = Color.FromArgb(54, 50, 46),
                ForeColor = Color.FromArgb(225, 220, 210)
            };
            apply.Click += delegate { ApplyFactions(); };
            top.Controls.Add(apply);

            factionShowHidden = new CheckBox
            {
                Left = 12, Top = 114, Width = 700, Height = 26,
                Text = Lang.T("skills.factions.showHidden"),
                ForeColor = Color.FromArgb(210, 205, 195)
            };
            factionShowHidden.Click += delegate { RefreshFactions(); };
            top.Controls.Add(factionShowHidden);

            page.Controls.Add(factionList);
            page.Controls.Add(top);
            RefreshFactions();
            return page;
        }

        void ApplyFactions()
        {
            if (factions == null || factionList == null) return;
            if (factionList.SelectedItems.Count == 0)
            { factionSummary.Text = Lang.T("skills.factions.pickFirst"); return; }

            float want = (float)factionValueBox.Value;
            bool any = false;
            foreach (ListViewItem row in factionList.SelectedItems)
            {
                FactionInfo info = row.Tag as FactionInfo;
                if (info == null) continue;
                if (FactionTable.Apply(character, info.Index, want)) any = true;
            }
            RefreshFactions();
            if (any && Changed != null) Changed(this, EventArgs.Empty);
        }

        void RefreshFactions()
        {
            if (factions == null || factionList == null || character == null) return;
            int known = factions.Factions.Count;
            int inSave = character.Factions.Count;

            System.Text.StringBuilder text = new System.Text.StringBuilder();
            text.Append(Lang.T("skills.factions.summary", known, inSave));
            // A save the game has not written since the last patch holds fewer
            // entries than the game now has factions, and the ones past its end
            // cannot be edited at all — so they are not offered, and said about.
            int beyond = 0;
            foreach (FactionInfo info in factions.Factions) if (info.Index >= inSave) beyond++;
            if (beyond > 0)
                text.Append(Environment.NewLine).Append(Lang.T("skills.factions.short", inSave, known));
            text.Append(Environment.NewLine).Append(Lang.T("skills.factions.source"));
            factionSummary.Text = text.ToString();

            // The rows are built anew every time, so the selection has to be put
            // back by hand so applying changes does not clear the selection.
            HashSet<int> chosen = new HashSet<int>();
            foreach (ListViewItem old in factionList.SelectedItems)
            {
                FactionInfo was = old.Tag as FactionInfo;
                if (was != null) chosen.Add(was.Index);
            }

            bool showAll = factionShowHidden != null && factionShowHidden.Checked;

            // Sort by display name to match the game panel; keep the original slot
            // index in each row for editing.
            List<FactionInfo> rows = new List<FactionInfo>(factions.Factions);
            rows.Sort(delegate (FactionInfo a, FactionInfo b)
            { return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture); });

            factionList.BeginUpdate();
            factionList.Items.Clear();
            foreach (FactionInfo info in rows)
            {
                if (info.Index >= inSave) continue;
                Faction f = character.Factions[info.Index];
                // Match the game panel: starting factions are always shown, other
                // factions require `Unlocked == 1`, and hidden factions stay hidden.
                bool inPanel = !info.Hidden && (info.Starting || f.Unlocked == 1);
                if (!inPanel && !showAll) continue;
                FactionRank rank = factions.RankOf(f.Value);
                ListViewItem row = new ListViewItem(info.Name);
                row.SubItems.Add(rank == null ? "" : rank.Name);
                row.SubItems.Add(FactionNumber(f.Value));
                row.SubItems.Add(f.PositiveBoost > 0
                                 ? Lang.T("skills.factions.boostTimes", FactionNumber(f.PositiveBoost)) : "—");
                List<string> notes = new List<string>();
                if (!inPanel) notes.Add(Lang.T("skills.factions.hidden"));
                if (info.NoRepGain) notes.Add(Lang.T("skills.factions.noGain"));
                row.SubItems.Add(string.Join(", ", notes.ToArray()));
                row.Tag = info;
                row.Selected = chosen.Contains(info.Index);
                factionList.Items.Add(row);
            }
            factionList.EndUpdate();
        }

        // Standings are floats, and in the saves a part of them is fractional.
        static string FactionNumber(float v)
        {
            return v == (long)v ? ((long)v).ToString() : v.ToString("0.##");
        }

        // What the portal tab says, so a headless run can read it.
        public string PortalSummaryText { get { return portalSummary == null ? null : portalSummary.Text; } }

        // The devotion text, so a headless run can read what the tab says.
        public List<TipLine> DevotionLines;

        // The constellation map as text, so a headless run can read it. Without
        // this the left half of the tab could only be checked by eye on a
        // screenshot, and it is a hundred and nine constellations long.
        public List<TipLine> ConstellationLines;

        // The star map and the column of sums beside it. Both are held because a
        // click on a star changes both, and a headless run looks at the map.
        public DevotionMap Map;
        TipView devoTotals;

        // The trees by mastery folder, so a headless run can look at every node.
        public readonly Dictionary<string, SkillTree> Trees =
            new Dictionary<string, SkillTree>(StringComparer.OrdinalIgnoreCase);

        // What worn gear adds to which skill, for the same headless run.
        public ItemSkillMods ItemMods { get { return ItemSkillMods.Of(game, character); } }

        public void SelectTab(int index)
        {
            foreach (Control c in Controls)
            {
                TabControl t = c as TabControl;
                if (t != null && index >= 0 && index < t.TabPages.Count) t.SelectedIndex = index;
            }
        }

        public static bool IsDevotion(string dbr)
        {
            return dbr != null && dbr.IndexOf("/devotion/", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    // A scrolling block of tooltip lines, for panels that show the same kind of
    // text the hover description does.
    public class TipHoverEventArgs : EventArgs
    {
        public readonly TipLine Line;
        public readonly Point ScreenPoint;
        public TipHoverEventArgs(TipLine line, Point at) { Line = line; ScreenPoint = at; }
    }

    public class TipView : Panel
    {
        List<TipLine> lines;

        // For a view whose content changes without the panel being rebuilt —
        // choosing another enemy, or another level for the same one.
        public void SetLines(List<TipLine> replacement)
        { lines = replacement ?? new List<TipLine>(); tops.Clear(); AutoScrollPosition = new Point(0, 0); Invalidate(); }

        // Measure line heights once per width and draw only visible rows to avoid
        // rebuilding large tooltip lists during scrolling.
        // Top of each line from the content top; one extra entry at the end is the total.
        readonly List<int> tops = new List<int>();
        List<TipLine> laidFor;
        int laidCount = -1, laidWidth = -1;
        TipLine hovered;

        // Raised for a line that carries something to explain. A summed number is
        // useless without its terms: a summed modifier is meaningful only with its
        // contributing rows.
        public event EventHandler<TipHoverEventArgs> Hovered;

        public TipView(List<TipLine> lines)
        {
            this.lines = lines;
            AutoScroll = true;
            BackColor = Color.FromArgb(20, 19, 18);
            DoubleBuffered = true;
            Padding = new Padding(10);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            using (Font body = new Font("Segoe UI", 9f))
            using (Font head = new Font("Segoe UI", 10f, FontStyle.Bold))
            {
                int width = Math.Max(200, ClientSize.Width - 24);
                LayOutRows(g, body, head, width);
                int origin = 10 + AutoScrollPosition.Y;
                for (int i = RowAt(e.ClipRectangle.Top - origin); i < lines.Count; i++)
                {
                    int y = origin + tops[i];
                    if (y > e.ClipRectangle.Bottom) break;
                    TipLine line = lines[i];
                    if (line.Text.Length == 0) continue;
                    Font own;
                    Font f = FontOf(line, body, head, out own);
                    Color c = line.Override != Color.Empty ? line.Override : TipPalette.Of(line.Kind);
                    int x = 10 + line.Indent * 14;
                    RectangleF box = new RectangleF(x, y, width - line.Indent * 14, 10000);
                    int h = tops[i + 1] - tops[i] - 2;
                    if (line == hovered && line.Explains != null)
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(38, 36, 32)))
                            g.FillRectangle(b, 6, y - 1, width + 12, h + 2);
                    using (SolidBrush b = new SolidBrush(c)) g.DrawString(line.Text, f, b, box);
                    ItemTooltip.PaintOrbs(g, line, f, c, x + (int)Math.Ceiling(g.MeasureString(line.Text, f).Width), y);
                    if (own != null) own.Dispose();
                }
            }
        }

        static Font FontOf(TipLine line, Font body, Font head, out Font own)
        {
            own = line.Size > 0 ? new Font("Segoe UI", line.Size, FontStyle.Bold) : null;
            if (own != null) return own;
            return line.Kind == TipKind.Name || line.Kind == TipKind.Section ? head : body;
        }

        // Measures every line once per list and width; the list is also re-measured when
        // lines were added to it in place, which the summary views do before showing.
        void LayOutRows(Graphics g, Font body, Font head, int width)
        {
            if (laidFor == lines && laidCount == lines.Count && laidWidth == width && tops.Count == lines.Count + 1)
                return;
            tops.Clear();
            int y = 0;
            foreach (TipLine line in lines)
            {
                tops.Add(y);
                if (line.Text.Length == 0) { y += 8; continue; }
                Font own;
                Font f = FontOf(line, body, head, out own);
                int height = (int)Math.Ceiling(g.MeasureString(line.Text, f, width - line.Indent * 14).Height) + 2;
                if (line.Orbs != null) height = Math.Max(height, TipLine.OrbSize + 3);
                y += height;
                if (own != null) own.Dispose();
            }
            tops.Add(y);
            laidFor = lines; laidCount = lines.Count; laidWidth = width;
            AutoScrollMinSize = new Size(0, y + 20);
        }

        // Scrolls to the last line. Measures outside a paint: a form placed off the screen
        // (the snapshot) never gets one until it is drawn to a bitmap.
        public void ScrollToEnd()
        {
            using (Graphics g = CreateGraphics())
            using (Font body = new Font("Segoe UI", 9f))
            using (Font head = new Font("Segoe UI", 10f, FontStyle.Bold))
                // Twice: the first pass brings in the scroll bar, which narrows the lines.
                for (int pass = 0; pass < 2; pass++)
                    LayOutRows(g, body, head, Math.Max(200, ClientSize.Width - 24));
            AutoScrollPosition = new Point(0, tops[tops.Count - 1]);
            Invalidate();
        }

        // The line covering this offset from the content top (lines.Count past the end).
        int RowAt(int offset)
        {
            int lo = 0, hi = Math.Min(lines.Count, tops.Count - 1);
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (tops[mid + 1] > offset) hi = mid; else lo = mid + 1;
            }
            return lo;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            TipLine found = null;
            int offset = e.Y - 10 - AutoScrollPosition.Y;
            if (offset >= 0 && tops.Count == lines.Count + 1)
            {
                int i = RowAt(offset);
                if (i < lines.Count && lines[i].Text.Length > 0) found = lines[i];
            }
            if (found != null && found.Explains == null) found = null;
            if (found == hovered) return;
            hovered = found;
            Invalidate();
            if (Hovered != null) Hovered(this, new TipHoverEventArgs(found, PointToScreen(e.Location)));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hovered == null) return;
            hovered = null;
            Invalidate();
            if (Hovered != null) Hovered(this, new TipHoverEventArgs(null, Point.Empty));
        }

        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }
    }
}
