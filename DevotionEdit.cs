// Putting a point into a star of the devotion map and taking it back.
//
// The rules are the game's own and every one of them is read out of the records,
// not written down here (the reading itself is `DevotionTable`):
//
//   the pool          `character.DevotionPoints` — a point goes in only if there is
//                     one, and comes back into the pool when taken out
//   what is open      a constellation opens when the character's affinity has
//                     reached `affinityRequired1..N`; inside an open constellation
//                     the ROOT star is open — the one no `devotionLinksN` hangs off
//                     — and every other star opens once the star it hangs off is
//                     taken
//   affinity          a constellation gives `affinityGiven1..N` ONLY when every one
//                     of its stars is taken
//   taking back       a star whose child is taken stays; and the last star of a
//                     COMPLETE constellation stays if losing that constellation's
//                     affinity would drop another taken constellation below its own
//                     requirement
//
// WHERE THE «ONLY WHEN COMPLETE» RULE COMES FROM. It was read off GrimTools'
// calculator (MECHANICS 103d) and is not ours to assume — so it is not only used,
// it is CHECKED: `--devoedittest` computes the affinity of every save this way and
// demands that every constellation the character has stars in is paid for. The
// saves were written by the game, so a rule that gives too little affinity makes
// that check go red. See the check's own comment for what it found.
//
// HOW A STAR SITS IN THE SAVE — measured on the game's own saves (`_Itaan`):
//   taken            `Level 1`, `DevotionLevel 1`, `DevotionExperience 0`
//   taken and levelled (a celestial power)   `Level 1`, `DevotionLevel 25`, experience
//   given back       the entry STAYS with `Level 0`, keeping the level and the
//                    experience the star had earned
// so taking a point back leaves the entry behind whenever there is something to
// keep, and drops it when there is not — the same way `SkillEdit.Remove` does.

using System;
using System.Collections.Generic;

namespace Gdc.Gui
{
    public static class DevotionEdit
    {
        public static int LevelOf(Character c, string dbr)
        {
            if (c == null || string.IsNullOrEmpty(dbr)) return 0;
            foreach (Skill s in c.Skills)
                if (string.Equals(s.Name, dbr, StringComparison.OrdinalIgnoreCase)) return s.Level;
            return 0;
        }

        public static bool Taken(Character c, DevotionStar star)
        {
            return star != null && LevelOf(c, star.Dbr) > 0;
        }

        public static bool Complete(Character c, Constellation k)
        {
            if (k == null || k.Stars.Count == 0) return false;
            foreach (string star in k.Stars) if (LevelOf(c, star) <= 0) return false;
            return true;
        }

        public static int TakenIn(Character c, Constellation k)
        {
            int n = 0;
            if (k != null) foreach (string star in k.Stars) if (LevelOf(c, star) > 0) n++;
            return n;
        }

        // THE CHARACTER'S AFFINITY, BY THE AFFINITY'S ENGLISH NAME. It is not in the
        // save at all: the game works it out from the map, and so do we — every
        // constellation whose stars are ALL taken hands over its `affinityGivenN`.
        public static Dictionary<string, int> Affinity(GameData game, Character c)
        {
            return Affinity(game, c, null);
        }

        // The same, with one constellation left out — what the affinity would be if
        // that one stopped being complete. Used by the refusal to take a star back.
        public static Dictionary<string, int> Affinity(GameData game, Character c, Constellation without)
        {
            Dictionary<string, int> made = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Constellation k in DevotionTable.Of(game))
            {
                if (k == without || !Complete(c, k)) continue;
                foreach (KeyValuePair<string, int> kv in k.Gives)
                {
                    int had;
                    made[kv.Key] = (made.TryGetValue(kv.Key, out had) ? had : 0) + kv.Value;
                }
            }
            return made;
        }

        static int Has(Dictionary<string, int> affinity, string name)
        {
            int v;
            return affinity != null && affinity.TryGetValue(name, out v) ? v : 0;
        }

        // The affinity this constellation still lacks, or null when it is open.
        // Named rather than counted, because that is what the refusal has to say.
        public static string Missing(GameData game, Dictionary<string, int> affinity, Constellation k)
        {
            if (k == null) return null;
            foreach (KeyValuePair<string, int> kv in k.Needs)
                if (Has(affinity, kv.Key) < kv.Value)
                    return AffinityName(game, kv.Key) + " " + Has(affinity, kv.Key) + " / " + kv.Value;
            return null;
        }

        public static bool Unlocked(GameData game, Dictionary<string, int> affinity, Constellation k)
        {
            return Missing(game, affinity, k) == null;
        }

        // The star this one hangs off, or null when it is the root of its
        // constellation.
        public static DevotionStar ParentOf(DevotionStar star)
        {
            if (star == null || star.Home == null) return null;
            if (star.Parent < 0 || star.Parent >= star.Home.Buttons.Count) return null;
            return star.Home.Buttons[star.Parent];
        }

        // A star that hangs off this one and is taken, or null.
        public static DevotionStar TakenChild(Character c, DevotionStar star)
        {
            if (star == null || star.Home == null) return null;
            foreach (DevotionStar other in star.Home.Buttons)
                if (other.Parent == star.Index && LevelOf(c, other.Dbr) > 0) return other;
            return null;
        }

        // Can a point go into this star at all — leaving the pool out of it. This is
        // what the map asks to decide which stars to draw as reachable.
        public static bool Open(GameData game, Character c, Dictionary<string, int> affinity, DevotionStar star)
        {
            if (star == null || star.Home == null) return false;
            if (!BreakAffinity && !Unlocked(game, affinity, star.Home)) return false;
            DevotionStar parent = ParentOf(star);
            return parent == null || LevelOf(c, parent.Dbr) > 0;
        }

        // Why a point cannot go into this star, in the player's own language, or
        // null when it can.
        public static string WhyNotAdd(GameData game, Character c, DevotionStar star)
        {
            return WhyNotAdd(game, c, star, Affinity(game, c));
        }

        // The same, with the affinity already worked out. Asking the same question
        // of all five hundred stars — which the check does, and which the map would
        // do if it asked per star — costs a full sweep of the sky each time
        // otherwise, and the answer cannot change while nothing has been spent.
        public static string WhyNotAdd(GameData game, Character c, DevotionStar star,
                                       Dictionary<string, int> affinity)
        {
            if (game == null || c == null || star == null) return "";
            if (c.DevotionPoints <= 0) return Lang.T("skills.devo.noPoints");
            if (LevelOf(c, star.Dbr) > 0) return Lang.T("skills.devo.already");

            string missing = Missing(game, affinity, star.Home);
            if (missing != null && !BreakAffinity)
                return Lang.T("skills.devo.locked", star.Home.Name, missing);

            DevotionStar parent = ParentOf(star);
            if (parent != null && LevelOf(c, parent.Dbr) <= 0)
                return Lang.T("skills.devo.parentFirst", StarName(game, parent));
            return null;
        }

        // Why a point cannot come back out, or null when it can.
        public static string WhyNotRemove(GameData game, Character c, DevotionStar star)
        {
            if (game == null || c == null || star == null) return "";
            if (LevelOf(c, star.Dbr) <= 0) return Lang.T("skills.devo.empty");

            DevotionStar child = TakenChild(c, star);
            if (child != null) return Lang.T("skills.devo.childFirst", StarName(game, child));

            // A COMPLETE CONSTELLATION IS PAYING FOR OTHERS. Take a star out of it
            // and its affinity goes away with it, so anything that was standing on
            // that affinity has to be given back first — the game refuses the click
            // rather than quietly unlocking nothing.
            if (Complete(c, star.Home) && star.Home.Gives.Count > 0)
            {
                Dictionary<string, int> after = Affinity(game, c, star.Home);
                foreach (Constellation k in DevotionTable.Of(game))
                {
                    if (TakenIn(c, k) <= 0) continue;
                    string missing = Missing(game, after, k);
                    if (missing != null) return Lang.T("skills.devo.affinityHolds", k.Name, missing);
                }
            }
            return null;
        }

        // One point in, out of the pool. Returns true when something changed.
        public static bool Add(GameData game, Character c, DevotionStar star)
        {
            if (WhyNotAdd(game, c, star) != null) return false;
            foreach (Skill s in c.Skills)
                if (string.Equals(s.Name, star.Dbr, StringComparison.OrdinalIgnoreCase))
                {
                    // The entry was left behind when the star was given back, with
                    // the level and the experience it had earned; the point goes
                    // straight back on top of them.
                    s.Level = 1;
                    s.Enabled = 1;
                    if (s.DevotionLevel < 1) s.DevotionLevel = 1;
                    c.DevotionPoints--;
                    return true;
                }
            Skill made = new Skill();
            made.Name = star.Dbr;
            made.Level = 1;
            made.Enabled = 1;
            // What the game writes for a star just taken, measured on its own saves:
            // level one of the star's own ladder and no experience yet.
            made.DevotionLevel = 1;
            made.DevotionExperience = 0;
            c.Skills.Add(made);
            c.DevotionPoints--;
            return true;
        }

        // One point back into the pool.
        //
        // The entry stays behind at level zero whenever it carries something worth
        // keeping — a star that levelled itself from use, or a celestial power bound
        // to a class skill — because that is what the game's own saves hold. When
        // there is nothing to keep, the entry goes, so that a point in and straight
        // back out leaves the save exactly as it was.
        public static bool Remove(GameData game, Character c, DevotionStar star)
        {
            if (WhyNotRemove(game, c, star) != null) return false;
            for (int i = 0; i < c.Skills.Count; i++)
            {
                Skill s = c.Skills[i];
                if (!string.Equals(s.Name, star.Dbr, StringComparison.OrdinalIgnoreCase)) continue;
                s.Level = 0;
                c.DevotionPoints++;
                // A binding to a star the character no longer has is not something
                // the game writes, so it goes with the point.
                Unbind(c, star.Dbr);
                if (s.DevotionLevel <= 1 && s.DevotionExperience == 0) c.Skills.RemoveAt(i);
                return true;
            }
            return false;
        }

        // The star hangs on a class skill, and the binding lives on THAT skill's
        // entry (`AutoCastSkill`), not on the star's — the same way round the
        // devotion tab has always read it.
        static void Unbind(Character c, string starDbr)
        {
            foreach (Skill s in c.Skills)
                if (string.Equals(s.AutoCastSkill, starDbr, StringComparison.OrdinalIgnoreCase))
                { s.AutoCastSkill = ""; s.AutoCastController = ""; }
            foreach (ItemSkill s in c.ItemSkills)
                if (string.Equals(s.AutoCastSkill, starDbr, StringComparison.OrdinalIgnoreCase))
                { s.AutoCastSkill = ""; s.AutoCastController = ""; }
        }

        // The star's own name, for a refusal. An ordinary star has no name of its
        // own in the game — it is called after its constellation — so the number it
        // stands at is added.
        public static string StarName(GameData game, DevotionStar star)
        {
            if (star == null) return "";
            string name = SkillText.Name(game, star.Dbr);
            if (string.IsNullOrEmpty(name) || name == star.Home.Name)
                return Lang.T("skills.devotion.starOf", star.Home.Name, star.Index + 1);
            return name;
        }

        // The affinity's name as the game writes it: the records say "Primordial",
        // the player reads the localized name. Numbered strings, in the order the strings
        // themselves are listed.
        static readonly string[] Affinities =
            { "Ascendant", "Chaos", "Eldritch", "Order", "Primordial" };

        // THE GAME NEVER WRITES THE NAME OF AN AFFINITY BESIDE A NUMBER — it draws a
        // coloured ball. The pictures are the master table's own
        // (`affinity01Bitmap`…`affinity05Bitmap`), in the same order as the strings.
        public static string AffinityIcon(GameData game, string english)
        {
            for (int i = 0; i < Affinities.Length; i++)
            {
                if (!string.Equals(Affinities[i], english, StringComparison.OrdinalIgnoreCase)) continue;
                DbRecord master = game == null ? null : game.Record(DevotionTable.MasterTable);
                DbRecord bitmap = master == null ? null
                                : game.Record(master.Get("affinity" + (i + 1).ToString("00") + "Bitmap"));
                return bitmap == null ? null : bitmap.Get("bitmapName");
            }
            return null;
        }

        // The five, in the order the game lists them — for whoever shows all of them
        // at once.
        public static string[] AffinityOrder { get { return Affinities; } }

        public static string AffinityName(GameData game, string english)
        {
            for (int i = 0; i < Affinities.Length; i++)
                if (string.Equals(Affinities[i], english, StringComparison.OrdinalIgnoreCase))
                {
                    string text = game == null ? null : game.Tag("tagDevotionAffinity" + (i + 1).ToString("00"));
                    if (!string.IsNullOrEmpty(text)) return GameData.Form(text, null);
                }
            return english ?? "";
        }

        // `--devoeditbreak` — the affinity gate switched off on purpose, so the check
        // that guards it can be seen going red (PLAN, rule 12: a check nobody has
        // seen fail is not a check).
        public static bool BreakAffinity;
    }
}
