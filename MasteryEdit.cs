// Taking a mastery and giving it back.
//
// The game has no flag for "this character is a Soldier": a mastery is taken
// when at least one point sits in its bar, and the bar is an ordinary skill —
// records/skills/playerclassNN/_classtraining_classNN.dbr, class Skill_Mastery.
// Everything else follows from that one entry.
//
// Two things do not follow from it and have to be kept in step by hand.
//
// The save carries the class name as a tag of its own (block 1, ClassTag), and
// it is built from the mastery numbers in ascending order: Soldier + Shaman is
// tagSkillClassName0106 on _Relo, and every one of the thirteen saves the game
// itself wrote follows that rule. A save whose tag disagrees with its skills
// would show the wrong class name in the game's own menu.
//
// And the points. Giving a mastery back means taking every point out of every
// skill of that folder, and those points belong to the player — they go back
// into the pool, exactly as the game's own respawn does it. Levels that gear
// pushed a skill above its cap are not in the save at all, so nothing has to be
// unpicked there.

using System;
using System.Collections.Generic;

namespace Gdc.Gui
{
    public static class MasteryEdit
    {
        // The mastery numbers the game actually ships. Probed rather than
        // hard-coded: an expansion adds one by adding the folder.
        public static List<int> All(GameData game)
        {
            List<int> made = new List<int>();
            if (game == null) return made;
            for (int n = 1; n <= 20; n++)
                if (game.Record(Bar(n)) != null) made.Add(n);
            return made;
        }

        public static string Bar(int number)
        {
            string nn = number.ToString("00");
            return "records/skills/playerclass" + nn + "/_classtraining_class" + nn + ".dbr";
        }

        public static string Folder(int number)
        { return "records/skills/playerclass" + number.ToString("00") + "/"; }

        // The mastery's name, in the language of the settings. tagSkillClassName09
        // is "?" in the Russian strings of the current patch, so a name that says
        // nothing falls back to the record's own English description rather than
        // being shown as a question mark.
        public static string Name(GameData game, int number)
        {
            string tag = game == null ? null : game.Tag("tagSkillClassName" + number.ToString("00"));
            if (!string.IsNullOrEmpty(tag) && tag != "?") return GameData.Form(tag, null);
            // The class-name string of the ninth mastery is literally "?" in the
            // Russian text of the current patch. Its bar knows its own name —
            // skillDisplayName -> tagGDX2Class09SkillName00A -> localized skill name.
            DbRecord rec = game == null ? null : game.Record(Bar(number));
            string shown = rec == null ? null : game.Tag(rec.Get("skillDisplayName"));
            if (!string.IsNullOrEmpty(shown) && shown != "?") return GameData.Form(shown, null);
            string file = rec == null ? null : rec.Get("FileDescription");
            return string.IsNullOrEmpty(file) ? Lang.T("skills.mastery.fallbackName", number) : file;
        }

        // Which masteries the character has, in ascending order — the order the
        // class tag is built in.
        public static List<int> Taken(Character c)
        {
            List<int> made = new List<int>();
            if (c == null) return made;
            for (int n = 1; n <= 20; n++)
            {
                string bar = Bar(n);
                foreach (Skill s in c.Skills)
                    if (s.Level > 0 && string.Equals(s.Name, bar, StringComparison.OrdinalIgnoreCase))
                    { made.Add(n); break; }
            }
            return made;
        }

        // Every point the character put into one mastery, bar included. Bonuses
        // from gear are not in the save, so this is exactly what would come back.
        public static int PointsIn(Character c, int number)
        {
            int sum = 0;
            string folder = Folder(number);
            foreach (Skill s in c.Skills)
                if (s.Level > 0 && s.Name != null &&
                    s.Name.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) sum += s.Level;
            return sum;
        }

        public static bool CanAdd(Character c, int number)
        { return c != null && Taken(c).Count < 2 && !Taken(c).Contains(number); }

        // The game opens the first mastery at level 2 and the second at 10. We do
        // not refuse — the editor's business is to say what the game would say and
        // let the player decide — but a character below that will not look right.
        public static int LevelFor(int slot) { return slot <= 1 ? 2 : 10; }

        public static string TooEarly(Character c, int slot)
        {
            int need = LevelFor(slot);
            if (c == null || c.Level >= need) return null;
            return Lang.T(slot <= 1 ? "skills.mastery.tooEarlyFirst" : "skills.mastery.tooEarlySecond", need, c.Level);
        }

        // A mastery is taken by putting the first point into its bar. One point,
        // out of the pool, exactly as the game spends it.
        public static void Add(Character c, GameData game, int number)
        {
            if (!CanAdd(c, number)) return;
            Skill bar = new Skill();
            bar.Name = Bar(number);
            bar.Level = 1;
            bar.Enabled = 1;
            c.Skills.Add(bar);
            // The point is spent, not conjured: the caller makes sure there is
            // one. Without this the character would have more points spent than
            // earned, and the game keeps that count.
            if (c.SkillPoints > 0) c.SkillPoints--;
            else c.SkillPoints = 0;
            Retag(c, game);
        }

        // Only the last one taken can be given back — the same order the game
        // itself allows, and the reason the buttons are laid out that way.
        public static bool CanRemove(Character c, int number)
        {
            List<int> taken = Taken(c);
            return taken.Count > 0 && taken[taken.Count - 1] == number;
        }

        // Returns how many points went back into the pool.
        public static int Remove(Character c, GameData game, int number)
        {
            int back = PointsIn(c, number);
            string folder = Folder(number);
            c.Skills.RemoveAll(delegate (Skill s)
            {
                return s.Name != null && s.Name.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
            });
            c.SkillPoints += back;
            Retag(c, game);
            return back;
        }

        // The class name the save carries. Built from the numbers in ascending
        // order; empty when the character has no mastery yet, which is what a
        // fresh character's save holds.
        public static void Retag(Character c, GameData game)
        {
            List<int> taken = Taken(c);
            if (taken.Count == 0) { c.ClassTag = ""; return; }
            string digits = "";
            foreach (int n in taken) digits += n.ToString("00");
            c.ClassTag = "tagSkillClassName" + digits;
        }
    }
}
