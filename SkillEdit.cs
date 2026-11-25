// Putting points into a mastery skill and taking them back.
//
// The rules are the game's own and every one of them is read out of the records,
// not written down here:
//
//   the pool          `character.SkillPoints` — a point goes in only if there is one,
//                     and comes back into the pool when taken out
//   the cap           `skillMaxLevel` of the skill (what gear adds on top is not in
//                     the save and is not touched here)
//   the tier          `skillTier` of the skill is a 1-based column, and
//                     `skillMasteryTierLevel` of the engine says at which mastery
//                     level each column opens: 1;5;10;15;20;25;32;40;50
//   exclusive         `exclusiveSkill 1` — only ONE such skill may hold points, and
//                     the game counts them across BOTH masteries (PLAN 7a)
//   what hangs off    a modifier or a transmuter needs its own skill taken first;
//                     who its owner is comes from `AttackSkills.Attach`, the same
//                     mapping the damage model uses
//
// Taking points back follows the same rules backwards: a point cannot leave a skill
// while something that hangs off it still holds points, and the mastery bar cannot
// drop below the tier of a skill that has points in it.

using System;
using System.Collections.Generic;

namespace Gdc.Gui
{
    public static class SkillEdit
    {
        // Mastery levels at which each further column of skills opens; the engine
        // record spells them out as "1;5;10;15;20;25;32;40;50".
        public static int[] Tiers(GameData game)
        {
            DbRecord engine = game == null ? null : game.Record("records/game/gameengine.dbr");
            string spec = engine == null ? null : engine.Get("skillMasteryTierLevel");
            if (string.IsNullOrEmpty(spec)) return new int[0];
            List<int> list = new List<int>();
            foreach (string p in spec.Split(';'))
            {
                int v;
                if (int.TryParse(p, out v) && v > 0) list.Add(v);
            }
            return list.ToArray();
        }

        // The mastery level this skill's column needs. `skillTier` is 1-based, so
        // tier 1 is the first column and opens at the first listed level.
        public static int TierLevel(GameData game, string dbr)
        {
            DbRecord rec = Holding(game, dbr, "skillTier");
            int tier = rec == null ? 0 : rec.GetInt("skillTier");
            if (tier <= 0) return 0;
            int[] tiers = Tiers(game);
            if (tiers.Length == 0) return 0;
            return tiers[Math.Min(tier, tiers.Length) - 1];
        }

        public static int LevelOf(Character c, string dbr)
        {
            if (c == null || string.IsNullOrEmpty(dbr)) return 0;
            foreach (Skill s in c.Skills)
                if (string.Equals(s.Name, dbr, StringComparison.OrdinalIgnoreCase)) return s.Level;
            return 0;
        }

        public static int MaxLevel(GameData game, string dbr)
        {
            DbRecord limits = SkillText.LimitsOf(game, dbr);
            return limits == null ? 0 : limits.GetInt("skillMaxLevel");
        }

        // The level of the mastery bar this skill belongs to. Nothing in the tree
        // opens until a point sits in the bar, so this is the gate on everything.
        public static int BarLevel(Character c, string dbr)
        {
            int number = MasteryNumber(dbr);
            return number <= 0 ? 0 : LevelOf(c, MasteryEdit.Bar(number));
        }

        // "records/skills/playerclass06/soulsiphon1.dbr" -> 6. `MasteryFolder`
        // gives back "playerclass06", digits and all.
        public static int MasteryNumber(string dbr)
        {
            string folder = ItemViewFactory.MasteryFolder(dbr);
            if (folder == null || folder.Length < 13) return 0;
            int number;
            return int.TryParse(folder.Substring(11, 2), out number) ? number : 0;
        }

        public static bool IsBar(string dbr)
        {
            return !string.IsNullOrEmpty(dbr) &&
                   dbr.IndexOf("_classtraining_", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsExclusive(GameData game, string dbr)
        {
            DbRecord rec = Holding(game, dbr, "exclusiveSkill");
            return rec != null && rec.GetInt("exclusiveSkill") != 0;
        }

        // The record of the hand-off chain that actually carries a field: a button
        // in the tree can point at a shell whose numbers live on the buff it hands
        // off to (the same chain SkillTree and the damage model walk).
        static DbRecord Holding(GameData game, string dbr, string field)
        {
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(dbr); hop++)
            {
                DbRecord rec = game == null ? null : game.Record(dbr);
                if (rec == null) return null;
                if (!string.IsNullOrEmpty(rec.Get(field))) return rec;
                dbr = ItemViewFactory.NextSkillRecord(rec);
            }
            return null;
        }

        // Which skills this one hangs off. A modifier and a transmuter are attached
        // in different ways, and `AttackSkills.Attach` already knows both:
        // `skillDependancy` when the record names its owners, the tree layout when
        // it does not, and the `connectortransmuterstub` mark for a transmuter.
        //
        // THERE CAN BE SEVERAL, and that is not a detail: Ulzuin's Chosen is named
        // by Stun Jacks, Grenado and Canister Bomb at once (MECHANICS 28). Taking
        // the first of them as "the" owner would refuse a modifier whose other
        // skill is the one actually taken — and `_Iman`, who has Grenado and not
        // Stun Jacks, is exactly that case.
        public static List<string> OwnersOf(GameData game, string dbr)
        {
            List<string> made = new List<string>();
            string folder = ItemViewFactory.MasteryFolder(dbr);
            if (folder == null) return made;
            AttackSkills.Attach a = AttackSkills.Attach.OfFolder(game, folder);
            made.AddRange(a.Parents(game, dbr));
            if (made.Count == 0)
            {
                string extra = a.Extra(dbr);
                if (!string.IsNullOrEmpty(extra)) made.Add(extra);
            }
            return made;
        }

        // The owner that is actually taken, or null when none of them is.
        public static string TakenOwner(GameData game, Character c, string dbr)
        {
            foreach (string one in OwnersOf(game, dbr))
                if (LevelOf(c, one) > 0) return one;
            return null;
        }

        // The same question the rule asks itself, for the check that guards it.
        public static bool NeedsOwnerOutside(GameData game, string dbr) { return NeedsOwner(game, dbr); }

        static bool NeedsOwner(GameData game, string dbr)
        {
            DbRecord rec = game == null ? null : game.Record(dbr);
            string cls = rec == null ? "" : (rec.Class ?? "");
            return cls.Equals("Skill_Modifier", StringComparison.OrdinalIgnoreCase) ||
                   cls.IndexOf("ProjectileModifier", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   cls.IndexOf("Transmuter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   cls.StartsWith("SkillSecondary", StringComparison.OrdinalIgnoreCase);
        }

        // Why a point cannot go into this skill, in the player's own language, or
        // null when it can.
        public static string WhyNotAdd(GameData game, Character c, string dbr)
        {
            if (game == null || c == null || string.IsNullOrEmpty(dbr)) return "";
            if (c.SkillPoints <= 0) return Lang.T("skills.edit.noPoints");

            int max = MaxLevel(game, dbr);
            int level = LevelOf(c, dbr);
            if (max > 0 && level >= max) return Lang.T("skills.edit.atMax", max);

            if (IsBar(dbr))
            {
                // The bar itself is only gated by its own cap: taking the mastery
                // is a separate step, and it is what put the first point in.
                return level <= 0 ? Lang.T("skills.edit.masteryFirst") : null;
            }

            int bar = BarLevel(c, dbr);
            if (bar <= 0) return Lang.T("skills.edit.masteryFirst");
            int need = TierLevel(game, dbr);
            if (need > bar && !BreakTier) return Lang.T("skills.edit.tierClosed", need, bar);

            if (NeedsOwner(game, dbr))
            {
                List<string> owners = OwnersOf(game, dbr);
                if (owners.Count > 0 && TakenOwner(game, c, dbr) == null)
                    return Lang.T("skills.edit.ownerFirst", SkillText.Name(game, owners[0]) ?? owners[0]);
            }

            // ONE EXCLUSIVE SKILL ACROSS BOTH MASTERIES. The game says it in the
            // tooltip of every such skill, and the flag that marks them is
            // `exclusiveSkill 1` (PLAN 7a, checked against the game on three
            // masteries). Adding a second point to the one already taken is fine —
            // what is refused is a point into a different exclusive skill.
            if (level <= 0 && IsExclusive(game, dbr))
                foreach (Skill s in c.Skills)
                {
                    if (s.Level <= 0 || string.IsNullOrEmpty(s.Name)) continue;
                    if (string.Equals(s.Name, dbr, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!IsExclusive(game, s.Name)) continue;
                    return Lang.T("skills.edit.exclusive", SkillText.Name(game, s.Name) ?? s.Name);
                }
            return null;
        }

        // Why a point cannot come back out, or null when it can.
        public static string WhyNotRemove(GameData game, Character c, string dbr)
        {
            if (game == null || c == null || string.IsNullOrEmpty(dbr)) return "";
            int level = LevelOf(c, dbr);
            if (level <= 0) return Lang.T("skills.edit.empty");

            // The last point of a skill cannot leave while something that hangs off
            // it still holds points — the game does not let a modifier outlive its
            // skill, and a save where it does is not one the game wrote.
            if (level == 1)
            {
                string held = HeldBy(game, c, dbr);
                if (held != null) return Lang.T("skills.edit.ownerBusy", held);
            }

            // And the bar cannot drop below the tier of a skill that has points in
            // it: those skills would stand in a column the mastery has not opened.
            if (IsBar(dbr))
            {
                if (level <= 1) return Lang.T("skills.edit.barLast");
                string folder = ItemViewFactory.MasteryFolder(dbr);
                foreach (Skill s in c.Skills)
                {
                    if (s.Level <= 0 || string.IsNullOrEmpty(s.Name)) continue;
                    if (ItemViewFactory.MasteryFolder(s.Name) != folder) continue;
                    if (IsBar(s.Name)) continue;
                    if (TierLevel(game, s.Name) > level - 1)
                        return Lang.T("skills.edit.barHolds", SkillText.Name(game, s.Name) ?? s.Name,
                                      TierLevel(game, s.Name));
                }
            }
            return null;
        }

        // The name of a skill that hangs off this one and would be left with no
        // taken owner at all, or null. Only skills of the same mastery hang off it,
        // and a modifier named by several skills is held up by any one of them —
        // so it only blocks when THIS is its last one.
        static string HeldBy(GameData game, Character c, string dbr)
        {
            string folder = ItemViewFactory.MasteryFolder(dbr);
            if (folder == null) return null;
            foreach (Skill s in c.Skills)
            {
                if (s.Level <= 0 || string.IsNullOrEmpty(s.Name)) continue;
                if (ItemViewFactory.MasteryFolder(s.Name) != folder) continue;
                if (string.Equals(s.Name, dbr, StringComparison.OrdinalIgnoreCase)) continue;
                if (!NeedsOwner(game, s.Name)) continue;
                bool mine = false, other = false;
                foreach (string owner in OwnersOf(game, s.Name))
                {
                    if (string.Equals(owner, dbr, StringComparison.OrdinalIgnoreCase)) { mine = true; continue; }
                    if (LevelOf(c, owner) > 0) other = true;
                }
                if (mine && !other) return SkillText.Name(game, s.Name) ?? s.Name;
            }
            return null;
        }

        // One point in, out of the pool. Returns true when something changed.
        public static bool Add(GameData game, Character c, string dbr)
        {
            if (WhyNotAdd(game, c, dbr) != null) return false;
            foreach (Skill s in c.Skills)
                if (string.Equals(s.Name, dbr, StringComparison.OrdinalIgnoreCase))
                { s.Level++; c.SkillPoints--; return true; }
            // A skill with no points in it is not in the save at all, so the first
            // point makes the entry. `Enabled` is what the game writes for a skill
            // the character has — the same thing `MasteryEdit.Add` does for a bar.
            Skill made = new Skill();
            made.Name = dbr;
            made.Level = 1;
            made.Enabled = 1;
            c.Skills.Add(made);
            c.SkillPoints--;
            return true;
        }

        // One point back into the pool.
        //
        // The last point takes the entry with it, the same way giving a mastery back
        // does (`MasteryEdit.Remove`): a skill with no points is not in the game's
        // own save either. The one exception is an entry carrying a devotion binding
        // — dropping it would lose which star hangs on that skill, so it stays at
        // zero and the binding is left alone.
        public static bool Remove(GameData game, Character c, string dbr)
        {
            if (WhyNotRemove(game, c, dbr) != null) return false;
            for (int i = 0; i < c.Skills.Count; i++)
            {
                Skill s = c.Skills[i];
                if (!string.Equals(s.Name, dbr, StringComparison.OrdinalIgnoreCase)) continue;
                s.Level--;
                c.SkillPoints++;
                if (s.Level <= 0 && string.IsNullOrEmpty(s.AutoCastSkill)) c.Skills.RemoveAt(i);
                return true;
            }
            return false;
        }

        // `--skilleditbreak` — the tier gate switched off on purpose, so the check
        // that guards it can be seen going red (PLAN, rule 24: a check nobody has
        // seen fail is not a check).
        public static bool BreakTier;
    }
}
