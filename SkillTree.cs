// One mastery tree, drawn where the game draws it.
//
// The layout is not invented: for every skill of a mastery the database holds a
// UI record with the pixel position the game puts the button at —
//     records/ui/skills/class04/skill04.dbr
//         skillName        records/skills/playerclass04/passive1.dbr
//         bitmapPositionX  406
//         bitmapPositionY  459
// so the nodes go at those coordinates and the picture matches the game's.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Gdc.Gui
{
    // Mastery, passive, activated, or weapon-pool auto-attack skill.
    public enum SkillKind { Mastery, Passive, Active, WeaponPool, Modifier, Transmuter }

    public class SkillNode
    {
        public string Dbr = "", Name = "";
        public int Level, Bonus, MaxLevel, UltimateLevel, Tier;
        public int Total { get { return Level + Bonus; } }
        public Rectangle Bounds;
        public bool IsMastery;
        // The game draws passives as circles and activated skills as squares;
        // the UI record says which with "isCircular".
        public bool Circular = true;
        public Image Icon;

        // What kind of skill this is. The one that matters for a damage model is
        // the weapon pool skill: it does not sit on the skill bar, it fires by
        // itself in place of a plain attack, with a chance of its own.
        public SkillKind Kind = SkillKind.Passive;

        // For a weapon pool skill: the chance it replaces a plain attack, in per
        // cent, at this skill's level. Field skillChanceWeight.
        public int Chance;

        // Skills this one depends on; the database stores this in skillDependancy.
        public readonly List<string> DependsOn = new List<string>();

        // The connector tiles the game lays to the right of this skill, in the
        // order of `skillConnectionOff` / `skillConnectionOn`. See SkillLink.
        public readonly List<SkillLink> Connectors = new List<SkillLink>();
    }

    // ONE TILE OF THE LINE BETWEEN SKILLS, in both of its states: dim while the
    // skill has no points, lit once it has.
    //
    // `Step` is the thing that is easy to get wrong. A BRANCH TILE DOES NOT MOVE
    // THE RUN ALONG: its picture is the corner where a branch leaves, and the
    // tile after it is laid ON TOP of it, in the same cell, to carry the
    // horizontal piece through. Without that the line has a small gap at every
    // branch — which is exactly what the user saw — and everything past the branch
    // stands one cell too far right.
    public sealed class SkillLink
    {
        public Image Off, On;
        public bool Step;
    }

    public class SkillHoverEventArgs : EventArgs
    {
        public readonly SkillNode Node;
        public readonly Point ScreenPoint;
        public SkillHoverEventArgs(SkillNode n, Point p) { Node = n; ScreenPoint = p; }
    }

    // A click on a button of the tree: which one and with which mouse button. The
    // tree itself changes nothing — the rules and the character live elsewhere
    // (SkillEdit), and the window that owns the tree decides what a click means.
    public class SkillClickEventArgs : EventArgs
    {
        public readonly SkillNode Node;
        public readonly MouseButtons Button;
        public readonly Point ScreenPoint;
        public SkillClickEventArgs(SkillNode n, MouseButtons b, Point p)
        { Node = n; Button = b; ScreenPoint = p; }
    }

    public class SkillTree : Control
    {
        // Icons at the size the game draws them, with the level counter under
        // each, so the tree keeps the game's proportions and the name lives in
        // the hover description where there is room for it.
        // THE GAME'S OWN SIZES, not ours: a button is 40×40 and the icon inside it
        // 32×32 with four pixels of margin, and the level counter is a 60-wide box
        // under it. Read off the game's pictures and confirmed against GrimTools'
        // layout (MECHANICS 102a) — the same numbers on both sides.
        // `LevelWidth` is wider than the game's 60: our font is not the game's
        // condensed one, and "14+12 / 16" has to fit without being cut off.
        const int IconSize = 32, IconInset = 4, NodeWidth = 40, NodeHeight = 40, LevelWidth = 84;

        readonly List<SkillNode> nodes = new List<SkillNode>();
        SkillNode hovered;

        public List<SkillNode> Nodes { get { return nodes; } }

        public event EventHandler<SkillHoverEventArgs> Hovered;

        // Kept for `Reload`: after a point goes in, the chance of a weapon pool
        // skill is a different number, and it is read the same way it was built.
        GameData game;

        public SkillTree(GameData game, Character character, string masteryFolder, SkillText.LevelBonus bonus)
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(20, 19, 18);
            this.game = game;
            Build(game, character, masteryFolder, bonus);
        }

        void Build(GameData game, Character character, string masteryFolder, SkillText.LevelBonus bonus)
        {
            // Worn gear can change a skill without the save recording it — see
            // ItemSkillMods. Their skillChanceWeight adds to the skill's own:
            // Item modifiers are added to the skill's own value.
            ItemSkillMods itemMods = ItemSkillMods.Of(game, character);
            PointsLeft = character.SkillPoints;

            // Levels the character actually put in, by record path.
            Dictionary<string, int> invested = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Skill s in character.Skills)
                if (!string.IsNullOrEmpty(s.Name)) invested[s.Name] = s.Level;

            // playerclass04 -> ui/skills/class04.
            string number = masteryFolder.Length >= 12 ? masteryFolder.Substring(11) : "";
            Panel(game, number);
            Dressing(game);
            foreach (DbRecord ui in Buttons(game, number))
            {
                string dbr = ui.Get("skillName");
                if (string.IsNullOrEmpty(dbr)) continue;
                DbRecord skill = game.Record(dbr);
                if (skill == null) continue;

                SkillNode n = new SkillNode();
                n.Dbr = dbr;
                n.Name = SkillText.Name(game, dbr);
                DbRecord limits = SkillText.LimitsOf(game, dbr);
                n.MaxLevel = limits.GetInt("skillMaxLevel");
                n.UltimateLevel = limits.GetInt("skillUltimateLevel");
                DbRecord tierRec = Holding(game, dbr, "skillTier");
                n.Tier = tierRec == null ? 0 : tierRec.GetInt("skillTier");
                n.IsMastery = (skill.Class ?? "").IndexOf("Mastery", StringComparison.OrdinalIgnoreCase) >= 0;
                int level;
                n.Level = invested.TryGetValue(dbr, out level) ? level : 0;
                n.Bonus = bonus == null ? 0 : bonus.For(dbr, n.Level);

                string cls = skill.Class ?? "";
                n.Kind = n.IsMastery ? SkillKind.Mastery
                       : cls.StartsWith("Skill_WPAttack", StringComparison.OrdinalIgnoreCase) ? SkillKind.WeaponPool
                       : cls.Equals("Skill_Transmuter", StringComparison.OrdinalIgnoreCase) ? SkillKind.Transmuter
                       : cls.Equals("Skill_Modifier", StringComparison.OrdinalIgnoreCase) ? SkillKind.Modifier
                       : ui.GetInt("isCircular") != 0 ? SkillKind.Passive : SkillKind.Active;
                if (n.Kind == SkillKind.WeaponPool) n.Chance = ChanceOf(n, itemMods);
                DbRecord depRec = Holding(game, dbr, "skillDependancy");
                foreach (string on in ((depRec == null ? null : depRec.Get("skillDependancy")) ?? "").Split(';'))
                    if (!string.IsNullOrEmpty(on)) n.DependsOn.Add(on);
                Tiles(game, dbr, n.Connectors);
                n.Circular = ui.GetInt("isCircular") != 0;
                n.Icon = Texture(SkillText.IconOf(game, dbr));
                // The mastery bar's button names its picture without the state:
                // the record says `…trainingbutton.tex` and the archive holds
                // `…trainingbuttonup.tex`. Every other icon already carries the
                // state in its name, so the fallback is only ever used here.
                if (n.Icon == null && n.IsMastery)
                    n.Icon = Texture(WithState(SkillText.IconOf(game, dbr)));
                // THE GAME'S OWN COORDINATES, UNTOUCHED. The background picture of
                // the panel is drawn from the same origin, so a shifted button
                // would stand off its frame.
                n.Bounds = new Rectangle(ui.GetInt("bitmapPositionX"), ui.GetInt("bitmapPositionY"),
                                         n.IsMastery && n.Icon != null ? n.Icon.Width : NodeWidth,
                                         n.IsMastery && n.Icon != null ? n.Icon.Height : NodeHeight);
                if (n.IsMastery) mastery = n;
                nodes.Add(n);
            }

            columns = ColumnCentres();
            tiers = Tiers(game);
            // The panel is as big as the game's own picture of it, and at least as
            // big as the lowest counter under the lowest button.
            int bottom = 0;
            foreach (SkillNode n in nodes) bottom = Math.Max(bottom, n.Bounds.Bottom + 20);
            Size = new Size(background != null ? background.Width : 983,
                            Math.Max(background != null ? background.Height : 605, bottom));
        }

        // The buttons of one mastery, in the order the game lists them.
        //
        // `classtable.dbr` names them itself — `tabSkillButtons`, starting with the
        // mastery bar's own button — so nothing has to be probed. The old way
        // (trying `skill01`…`skill99`) is kept as a fallback: it found the same
        // buttons for every mastery the game ships, only without the bar.
        static List<DbRecord> Buttons(GameData game, string number)
        {
            List<DbRecord> made = new List<DbRecord>();
            string folder = "records/ui/skills/class" + number + "/";
            DbRecord table = game.Record(folder + "classtable.dbr");
            string list = table == null ? null : table.Get("tabSkillButtons");
            if (!string.IsNullOrEmpty(list))
                foreach (string one in list.Split(';'))
                {
                    DbRecord ui = string.IsNullOrEmpty(one) ? null : game.Record(one.Trim());
                    if (ui != null) made.Add(ui);
                }
            if (made.Count > 0) return made;
            for (int i = 1; i <= 99; i++)
            {
                DbRecord ui = game.Record(folder + "skill" + i.ToString("00") + ".dbr");
                if (ui != null) made.Add(ui);
            }
            return made;
        }

        // The pictures of the panel, and where the mastery bar's fill goes — all of
        // it named by `classtable.dbr`, so a mastery added by a patch brings its own
        // artwork with it.
        void Panel(GameData game, string number)
        {
            DbRecord table = game.Record("records/ui/skills/class" + number + "/classtable.dbr");
            if (table == null) return;
            background = Texture(BitmapOf(game, table.Get("skillPaneBaseBitmap"), "bitmapName"));
            classArt = Texture(BitmapOf(game, table.Get("skillPaneMasteryBitmap"), "bitmapName"));
            DbRecord bar = game.Record(table.Get("masteryBar") ?? "");
            if (bar == null) return;
            barFill = Texture(bar.Get("bitmapFullName"));
            barLeft = bar.GetInt("bitmapPositionX");
            barTop = bar.GetInt("bitmapPositionY");
        }

        static string BitmapOf(GameData game, string dbr, string field)
        {
            DbRecord rec = string.IsNullOrEmpty(dbr) ? null : game.Record(dbr);
            return rec == null ? null : rec.Get(field);
        }

        // The two lists of one skill, side by side: the game keeps them the same
        // length and the same order, and swaps the picture by state.
        void Tiles(GameData game, string dbr, List<SkillLink> into)
        {
            List<string> off = Names(game, dbr, "skillConnectionOff");
            List<string> on = Names(game, dbr, "skillConnectionOn");
            for (int i = 0; i < Math.Max(off.Count, on.Count); i++)
            {
                SkillLink link = new SkillLink();
                if (i < off.Count) link.Off = Texture(off[i]);
                if (i < on.Count) link.On = Texture(on[i]);
                link.Step = !IsBranch(i < on.Count ? on[i] : off[i]);
                into.Add(link);
            }
        }

        static List<string> Names(GameData game, string dbr, string field)
        {
            List<string> made = new List<string>();
            DbRecord rec = Holding(game, dbr, field);
            foreach (string tile in ((rec == null ? null : rec.Get(field)) ?? "").Split(';'))
                if (!string.IsNullOrEmpty(tile)) made.Add(tile.Trim());
            return made;
        }

        static bool IsBranch(string name)
        {
            return name.IndexOf("branchup", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("branchdown", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("branchboth", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The records name their pictures as `.png`, the archives hold `.tex`.
        Image Texture(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Image got = game.SkillIcon(path);
            if (got != null) return got;
            return path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                 ? game.SkillIcon(path.Substring(0, path.Length - 4) + ".tex") : null;
        }

        static string WithState(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int dot = path.LastIndexOf('.');
            return dot < 0 ? path + "up" : path.Substring(0, dot) + "up" + path.Substring(dot);
        }

        // The chance a weapon pool skill replaces a plain attack, at the level it
        // is at now. The list is indexed by level exactly like every other
        // per-level table, so the same reader gives it, and what worn gear adds
        // (`ItemSkillMods`) is applied after the skill-tree value.
        int ChanceOf(SkillNode n, ItemSkillMods itemMods)
        {
            if (n.Total <= 0) return 0;
            int made = 0;
            DbRecord rec = Holding(game, n.Dbr, "skillChanceWeight");
            if (rec != null)
            {
                string[] weights = (rec.Get("skillChanceWeight") ?? "").Split(';');
                int slot = SkillText.Slot(rec, n.Total);
                int one;
                if (weights.Length > 0 && int.TryParse(weights[Math.Min(slot, weights.Length - 1)], out one))
                    made = one;
            }
            return made + (int)Math.Round(itemMods.Sum(n.Dbr, "skillChanceWeight"));
        }

        // The record in the hand-off chain that actually carries a field.
        //
        // A button in the tree can point at a record that is only a shell.
        // Some granted skills use a shell record: the main record holds only
        // buffSkillName, and the connector list, the tier and every number sit on
        // soulsiphon1_buff.dbr. Reading the named record alone drew that skill
        // with no dependency line even when the game displays one. The name,
        // the icon and the level limits already followed this chain; the layout
        // did not.
        static DbRecord Holding(GameData game, string dbr, string field)
        {
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(dbr); hop++)
            {
                DbRecord rec = game.Record(dbr);
                if (rec == null) return null;
                if (!string.IsNullOrEmpty(rec.Get(field))) return rec;
                dbr = ItemViewFactory.NextSkillRecord(rec);
            }
            return null;
        }

        // The x of every column of buttons, left to right. Read off the buttons
        // themselves rather than assumed, so a mastery laid out differently still
        // lines up.
        int[] ColumnCentres()
        {
            List<int> xs = new List<int>();
            foreach (SkillNode n in nodes)
            {
                // The mastery bar's button stands at the far left, under the bar
                // and not in any column: counting it would shift every tier number
                // one column to the right.
                if (n.IsMastery) continue;
                int cx = n.Bounds.X + NodeWidth / 2;
                bool seen = false;
                foreach (int x in xs) if (Math.Abs(x - cx) <= GridStep / 2) { seen = true; break; }
                if (!seen) xs.Add(cx);
            }
            xs.Sort();
            return xs.ToArray();
        }

        // Where the mastery bar's fill goes, from the bar's own record.
        int barTop = 573, barLeft = 187;
        int[] columns = new int[0];
        SkillNode mastery;
        int[] tiers = new int[0];
        Image background, classArt, barFill;

        // The pictures every button is dressed in, named by the game's own panel
        // configuration (`skills_classpanelconfiguration.dbr`): a frame over the
        // icon, a grey frame when nothing is spent, and a blue glow behind it when
        // gear pushed the skill above its cap. Square for activated skills, round
        // for passives — the game ships both.
        Image frame, frameRound, grey, greyRound, blue, blueRound;

        // The two lines the game writes on the plate at the bottom left: the level
        // of the mastery beside its button, and how many points are left to spend.
        // Both boxes come from the panel configuration; the values here are the
        // game's own, kept as the fallback.
        Rectangle levelBox = new Rectangle(166, 571, 50, 20);
        Rectangle pointsBox = new Rectangle(0, 569, 157, 22);

        // The free skill points, for the line the game writes under the tree. The
        // tree does not own them — the window sets it, and again after every click.
        public int PointsLeft;

        void Dressing(GameData game)
        {
            DbRecord cfg = game.Record("records/ui/skills/classcommon/skills_classpanelconfiguration.dbr");
            if (cfg == null) return;
            grey = Texture(BitmapOf(game, cfg.Get("grayBitmap"), "bitmapName"));
            greyRound = Texture(BitmapOf(game, cfg.Get("grayRoundBitmap"), "bitmapName"));
            blue = Texture(BitmapOf(game, cfg.Get("blueBitmap"), "bitmapName"));
            blueRound = Texture(BitmapOf(game, cfg.Get("blueRoundBitmap"), "bitmapName"));
            // The border record carries only its template, so the picture is the
            // one beside the grey and blue frames in the same folder.
            frame = Texture("ui/skills/skillallocation/skills_buttonborder01.tex");
            frameRound = Texture("ui/skills/skillallocation/skills_buttonborderround01.tex");
            DbRecord tier1 = game.Record(cfg.Get("masteryMilestoneNumber1") ?? "");
            if (tier1 != null && tier1.GetInt("textBoxY") > 0) TierTextY = tier1.GetInt("textBoxY");
            levelBox = TextBox(game, cfg.Get("masterySkillLevel"), levelBox);
            pointsBox = TextBox(game, cfg.Get("pointsRemaining"), pointsBox);
        }

        // Where a line of text goes, as the game's own record puts it: `textBoxX`,
        // `textBoxY` and the two sizes.
        static Rectangle TextBox(GameData game, string dbr, Rectangle fallback)
        {
            DbRecord rec = string.IsNullOrEmpty(dbr) ? null : game.Record(dbr);
            if (rec == null || rec.GetInt("textBoxYSize") <= 0) return fallback;
            return new Rectangle(rec.GetInt("textBoxX"), rec.GetInt("textBoxY"),
                                 rec.GetInt("textBoxXSize"), rec.GetInt("textBoxYSize"));
        }

        // Mastery levels at which each further tier of skills opens; the engine
        // record spells them out as "1;5;10;15;20;25;32;40;50". The reading lives
        // in SkillEdit, where the rule that uses it lives too.
        static int[] Tiers(GameData game) { return SkillEdit.Tiers(game); }

        public SkillNode Mastery { get { return mastery; } }

        public SkillNode HitTest(Point p)
        {
            foreach (SkillNode n in nodes) if (n.Bounds.Contains(p)) return n;
            return null;
        }

        // HOW FAR THE BAR IS FILLED AT THIS MASTERY LEVEL.
        //
        // The marks along it are evenly spaced because the columns are, but the
        // levels behind them are not (1, 5, 10, …, 32, 40, 50). So the fill runs
        // from mark to mark: inside a tier it interpolates between that tier's
        // level and the next one's, and a level of 15 lands exactly under the mark
        // that says 15.
        int MarkX(int level)
        {
            if (columns.Length == 0 || tiers.Length == 0) return barLeft;
            if (level <= tiers[0]) return columns[0];
            for (int i = 0; i + 1 < tiers.Length && i + 1 < columns.Length; i++)
            {
                if (level < tiers[i] || level >= tiers[i + 1]) continue;
                double share = (double)(level - tiers[i]) / (tiers[i + 1] - tiers[i]);
                return columns[i] + (int)Math.Round((columns[i + 1] - columns[i]) * share);
            }
            return columns[Math.Min(columns.Length, tiers.Length) - 1];
        }

        // The mastery bar: the game's own fill picture, cut off where the level
        // has got to, and the tier number under each column.
        //
        // The numbers stand where the game stands them — `classtiernumber1…9` in
        // the panel configuration carry `textBoxX/Y`, and those are the columns of
        // the tree itself. The colours are the game's own too
        // (`skillLevelCompleteColor` for an open tier, `…UnavailableColor` for a
        // closed one).
        void PaintBar(Graphics g)
        {
            if (barFill != null && mastery != null && mastery.Level > 0)
            {
                int end = MarkX(mastery.Level);
                int width = Math.Min(barFill.Width, Math.Max(0, end - barLeft));
                if (width > 0)
                    g.DrawImage(barFill, new Rectangle(barLeft, barTop, width, barFill.Height),
                                0, 0, width, barFill.Height, GraphicsUnit.Pixel);
            }
            using (Font f = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (StringFormat mid = new StringFormat { Alignment = StringAlignment.Center })
            {
                for (int i = 0; i < tiers.Length && i < columns.Length; i++)
                {
                    bool open = mastery != null && mastery.Level >= tiers[i];
                    using (SolidBrush b = new SolidBrush(open ? Color.FromArgb(140, 235, 64)
                                                              : Color.FromArgb(115, 115, 115)))
                        g.DrawString(tiers[i].ToString(), f, b,
                                     new RectangleF(columns[i] - 20, TierTextY, 40, 16), mid);
                }
            }
        }

        // The two lines on the plate, drawn AFTER the buttons so the bar's fill
        // cannot run over them.
        void PaintBarText(Graphics g, Font f, StringFormat mid)
        {
            // The level of the mastery, right after its button — without it the bar
            // says how far along it is but not how far that is.
            //
            // The game's own box for this line (`masterySkillLevel`, x 166) sits on
            // top of the "+" square at the right end of the plate, so it is not used
            // as the x: the number goes just past the button we have actually drawn.
            // The y is the game's.
            if (mastery != null)
                using (SolidBrush b = new SolidBrush(Color.FromArgb(235, 235, 235)))
                using (StringFormat left = new StringFormat { Alignment = StringAlignment.Near })
                    g.DrawString(mastery.Level + " / " + (mastery.MaxLevel > 0 ? mastery.MaxLevel : 50),
                                 f, b, new RectangleF(mastery.Bounds.Right + 8, levelBox.Y,
                                                      levelBox.Width + 20, levelBox.Height), left);
            // And how many points are left, where the game writes it.
            using (SolidBrush b = new SolidBrush(PointsLeft > 0 ? Color.FromArgb(235, 235, 235)
                                                                : Color.FromArgb(255, 66, 0)))
                g.DrawString(Lang.T("skills.edit.points", PointsLeft), f, b,
                             new RectangleF(pointsBox.X, pointsBox.Y, pointsBox.Width, pointsBox.Height), mid);
        }

        // Where the tier numbers sit, from `classtiernumber1` of the panel
        // configuration; the game's own value is 550.
        int TierTextY = 550;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            SkillNode n = HitTest(e.Location);
            if (n == hovered) return;
            hovered = n;
            Invalidate();
            EventHandler<SkillHoverEventArgs> h = Hovered;
            if (h != null) h(this, new SkillHoverEventArgs(n, PointToScreen(e.Location)));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hovered == null) return;
            hovered = null;
            Invalidate();
            EventHandler<SkillHoverEventArgs> h = Hovered;
            if (h != null) h(this, new SkillHoverEventArgs(null, Point.Empty));
        }

        public event EventHandler<SkillClickEventArgs> Clicked;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            SkillNode n = HitTest(e.Location);
            if (n == null) return;
            EventHandler<SkillClickEventArgs> h = Clicked;
            if (h != null) h(this, new SkillClickEventArgs(n, e.Button, PointToScreen(e.Location)));
        }

        // The levels read again out of the character, after something put a point
        // in or took one out. Only the numbers change — the layout, the icons and
        // the connector lists are the game's and do not move.
        public void Reload(Character character, SkillText.LevelBonus bonus)
        {
            Dictionary<string, int> invested = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Skill s in character.Skills)
                if (!string.IsNullOrEmpty(s.Name)) invested[s.Name] = s.Level;
            ItemSkillMods itemMods = ItemSkillMods.Of(game, character);
            PointsLeft = character.SkillPoints;
            foreach (SkillNode n in nodes)
            {
                int level;
                n.Level = invested.TryGetValue(n.Dbr, out level) ? level : 0;
                n.Bonus = bonus == null ? 0 : bonus.For(n.Dbr, n.Level);
                if (n.Kind == SkillKind.WeaponPool) n.Chance = ChanceOf(n, itemMods);
            }
            if (mastery != null)
            {
                int level;
                mastery.Level = invested.TryGetValue(mastery.Dbr, out level) ? level : 0;
            }
            Invalidate();
        }

        // THE LINES BETWEEN SKILLS ARE NOT LINES — THEY ARE THE GAME'S OWN TILES.
        //
        // The game keeps them as a list of connector pictures on the skill record,
        // one picture per step of the layout grid to the right of the button:
        //
        //     phantomblade.dbr
        //         skillConnectionOff  offbranchdown; transmuteroffstub;
        //                             offcenter; offcenter; offcenter; offcenter
        //
        // Each is a 78×80 picture: "offcenter" a plain horizontal piece,
        // "offbranchdown" the corner where a branch leaves, and so on. The run
        // starts at the button's right edge and half a button up.
        //
        // A BRANCH TILE DOES NOT MOVE THE RUN ALONG — see SkillLink. Drawing the
        // tiles instead of our own lines also settles the thing noticed earlier: a
        // piece that leads nowhere is a short stub in the game's own artwork, so
        // nothing can hang in the void.
        const int GridStep = 80, TileStep = 78, TileUp = 21;

        void PaintLinks(Graphics g)
        {
            foreach (SkillNode n in nodes)
            {
                if (n.IsMastery) continue;
                int x = n.Bounds.X + NodeWidth, y = n.Bounds.Y - TileUp;
                foreach (SkillLink link in n.Connectors)
                {
                    Image art = n.Level > 0 && link.On != null ? link.On : link.Off;
                    if (art != null) g.DrawImage(art, x, y, art.Width, art.Height);
                    if (link.Step) x += TileStep;
                }
            }
        }

        // THE WHOLE PANEL, IN THE ORDER THE GAME STACKS IT: the frame of the
        // window, the artwork of the mastery, the connector tiles, then the
        // buttons over them.
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            if (background != null) g.DrawImage(background, 0, 0, background.Width, background.Height);
            if (classArt != null) g.DrawImage(classArt, 0, 0, classArt.Width, classArt.Height);
            // THE BAR GOES UNDER THE BUTTONS, and that is not a detail: its fill
            // starts at x 187 while the mastery's own button stands at 0 and is 197
            // wide, so the two overlap by ten pixels. The game draws the bar first
            // and the button over it — in their page the bar's element comes before
            // the button's, same thing. Drawn the other way round, the fill runs
            // across the "+" (noticed by the user).
            PaintBar(g);
            PaintLinks(g);

            using (Font small = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (StringFormat centre = new StringFormat { Alignment = StringAlignment.Center })
            {
                foreach (SkillNode n in nodes) PaintNode(g, n, small, centre);
                PaintBarText(g, small, centre);
            }
        }

        void PaintNode(Graphics g, SkillNode n, Font small, StringFormat centre)
        {
            bool taken = n.Total > 0;
            // The mastery bar's button is one picture and nothing else: no frame
            // around it, and its level is told by the bar rather than a counter.
            if (n.IsMastery)
            {
                if (n.Icon != null) g.DrawImage(n.Icon, n.Bounds.X, n.Bounds.Y, n.Bounds.Width, n.Bounds.Height);
                if (n == hovered)
                    using (Pen p = new Pen(Color.FromArgb(150, 218, 165, 32), 2f)) g.DrawRectangle(p, n.Bounds);
                return;
            }

            Rectangle art = new Rectangle(n.Bounds.X + IconInset, n.Bounds.Y + IconInset, IconSize, IconSize);
            if (n.Icon != null) g.DrawImage(n.Icon, art);
            // Round for a passive, square for an activated skill — the game ships
            // both frames and says which with `isCircular`.
            Image over = n.Circular ? frameRound : frame;
            if (over != null) g.DrawImage(over, n.Bounds.X, n.Bounds.Y, NodeWidth, NodeHeight);
            // Gear can push a skill above its own cap, and the game marks that with
            // a glow behind the button — 52×52 against the button's 40, so it is
            // drawn from six pixels out (`skillbuttons_bluebitmap`, positions −6).
            if (n.Bonus > 0 && n.MaxLevel > 0 && n.Total > n.MaxLevel)
            {
                Image glow = n.Circular ? blueRound : blue;
                if (glow != null) g.DrawImage(glow, n.Bounds.X - 6, n.Bounds.Y - 6, glow.Width, glow.Height);
            }
            // Nothing spent — the game lays a grey frame over the icon.
            if (!taken)
            {
                Image out_ = n.Circular ? greyRound : grey;
                if (out_ != null) g.DrawImage(out_, n.Bounds.X, n.Bounds.Y, NodeWidth, NodeHeight);
                else using (SolidBrush b = new SolidBrush(Color.FromArgb(140, 20, 19, 18)))
                    g.FillRectangle(b, n.Bounds);
            }
            if (n == hovered)
                using (Pen p = new Pen(Color.FromArgb(180, 218, 165, 32), 2f))
                {
                    if (n.Circular) g.DrawEllipse(p, n.Bounds); else g.DrawRectangle(p, n.Bounds);
                }

            // The counter under the button, in the game's own three colours:
            // greenish at the cap, blue when gear lifted it over, grey when empty
            // (`skillLevelCompleteColor` and friends of the panel configuration).
            string counter = (n.Bonus > 0 ? n.Level + "+" + n.Bonus : n.Level.ToString()) + " / " + n.MaxLevel;
            Color colour = !taken ? Color.FromArgb(115, 115, 115)
                         : n.MaxLevel > 0 && n.Total > n.MaxLevel ? Color.FromArgb(166, 209, 242)
                         : n.MaxLevel > 0 && n.Level >= n.MaxLevel ? Color.FromArgb(255, 240, 143)
                         : Color.FromArgb(220, 220, 220);
            using (SolidBrush b = new SolidBrush(colour))
                g.DrawString(counter, small, b,
                             new RectangleF(n.Bounds.X + (NodeWidth - LevelWidth) / 2, n.Bounds.Bottom - 2,
                                            LevelWidth, 18), centre);
        }
    }
}
