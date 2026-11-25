// Skills modified by equipped items or item sets.
//
// A piece of gear can carry pairs of `modifiedSkillName{i}` (which skill) and
// `modifierSkillName{i}` (a Skill_Modifier record with what is added to it). It
// is the same mechanism as a modifier bought in the skill tree, except that the
// save knows nothing about it: block 8 holds only what the character invested,
// and these have to be worked out from the records of what is worn.
//
// We handled the tree kind and not this one, which meant a whole class of bonus
// went missing everywhere — the description, the sums, the damage of the skill.
// This also covers ascension, where the modifier may be the entire affix and is
// applied as soon as the item is equipped.
//
// The level is the first row of the modifier's table: every such record seen so
// far has `skillMaxLevel 1` and single values, and the game's 22 → 27 is exactly
// the first entry of `skillChanceWeight`.

using System;
using System.Collections.Generic;

namespace Gdc.Gui
{
    public class ItemSkillMods
    {
        public class Entry
        {
            public string Skill = "";      // records/skills/... the skill being changed
            public DbRecord Modifier;      // the Skill_Modifier record with the numbers
            public string Source = "";     // the item it came from, for the "where from" lists
        }

        readonly List<Entry> all = new List<Entry>();
        public List<Entry> All { get { return all; } }

        public List<Entry> For(string skillDbr)
        {
            List<Entry> found = new List<Entry>();
            foreach (Entry e in all)
                if (string.Equals(e.Skill, skillDbr, StringComparison.OrdinalIgnoreCase)) found.Add(e);
            return found;
        }

        // The same field summed over every modifier of one skill, read at the
        // modifier's own first row.
        public double Sum(string skillDbr, string field)
        {
            double total = 0;
            foreach (Entry e in For(skillDbr)) total += AttackSkill.Value(e.Modifier, field, 0);
            return total;
        }

        public static ItemSkillMods Of(GameData game, Character c)
        {
            ItemSkillMods made = new ItemSkillMods();
            if (game == null || c == null) return made;
            List<Item> worn = new List<Item>();
            worn.AddRange(c.Equipment); worn.AddRange(c.Weapons1);
            SetMods(game, worn, made);
            foreach (Item it in worn)
            {
                if (string.IsNullOrEmpty(it.BaseName)) continue;
                string itemName = ItemViewFactory.Build(game, it, false).Name;
                if (string.IsNullOrEmpty(itemName)) itemName = it.BaseName;
                foreach (string dbr in new[] { it.BaseName, it.PrefixName, it.SuffixName,
                                               it.RelicName, ItemViewFactory.RelicBonusOf(game, it),
                                               it.AugmentName, it.ModifierName, it.AscensionName })
                {
                    DbRecord r = string.IsNullOrEmpty(dbr) ? null : game.Record(dbr);
                    if (r == null) continue;
                    for (int i = 1; i <= 8; i++)
                    {
                        string target = r.Get("modifiedSkillName" + i);
                        string mod = r.Get("modifierSkillName" + i);
                        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(mod)) continue;
                        DbRecord mr = game.Record(mod);
                        if (mr == null) continue;
                        Entry e = new Entry();
                        e.Skill = target; e.Modifier = mr;
                        e.Source = itemName +
                                   (string.Equals(dbr, it.AscensionName, StringComparison.OrdinalIgnoreCase)
                                        ? " (" + Lang.T("item.part.ascension") + ")" : "");
                        made.all.Add(e);
                    }
                }
            }
            return made;
        }

        // Set-bonus skill modifiers are stored on the set record, not on each
        // item. Read all `modifiedSkillName` entries from the set record.
        //
        // The activation threshold comes from the game's
        // `itemSkillModifierControl` field, indexed by equipped pieces.
        //
        // Do not require a complete set: the game can activate modifiers at an
        // intermediate piece count.
        //
        // A complete-set threshold may be unreachable when `setMembers` contains
        // alternate versions that cannot be equipped together.
        //
        // Use the same zero-based piece-count index as other set lists.
        static void SetMods(GameData game, List<Item> worn, ItemSkillMods made)
        {
            Dictionary<string, List<string>> pieces =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (Item it in worn)
            {
                if (string.IsNullOrEmpty(it.BaseName)) continue;
                DbRecord own = game.Record(it.BaseName);
                string set = own == null ? null : own.Get("itemSetName");
                if (string.IsNullOrEmpty(set)) continue;
                if (!pieces.ContainsKey(set)) pieces[set] = new List<string>();
                if (!pieces[set].Contains(it.BaseName)) pieces[set].Add(it.BaseName);
            }
            foreach (KeyValuePair<string, List<string>> kv in pieces)
            {
                DbRecord set = game.Record(kv.Key);
                if (set == null) continue;
                // Ask game data whether the modifier is active at this piece count.
                // If the field is absent, do not invent a threshold.
                if (!ModifiersOn(set, kv.Value.Count)) continue;
                string name = ItemViewFactory.SetNameOf(game, set) ?? kv.Key;
                for (int i = 1; i <= 8; i++)
                {
                    string target = set.Get("modifiedSkillName" + i);
                    string mod = set.Get("modifierSkillName" + i);
                    if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(mod)) continue;
                    DbRecord mr = game.Record(mod);
                    if (mr == null) continue;
                    Entry e = new Entry();
                    e.Skill = target; e.Modifier = mr;
                    e.Source = Lang.T("common.source.setName", name);
                    made.all.Add(e);
                }
            }
        }

        // Whether set skill modifiers are active at `pieces` equipped pieces.
        //
        // `itemSkillModifierControl` is indexed by `pieces - 1`;
        // zero means inactive and one means active.
        //
        // If more pieces are equipped than entries in the list, use the last entry.
        // The tooltip uses the same lookup so display and totals cannot diverge.
        public static bool ModifiersOnAt(DbRecord set, int pieces) { return ModifiersOn(set, pieces); }

        // Keep the equipped-piece count in one shared implementation. It controls
        // set bonuses, skill modifiers, and granted-skill level.
        //
        // Duplicate equipped items count once; distinct set records count separately.
        public static Dictionary<string, int> SetSizes(GameData game, List<Item> worn)
        {
            Dictionary<string, List<string>> pieces =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (game != null && worn != null)
                foreach (Item it in worn)
                {
                    if (it == null || string.IsNullOrEmpty(it.BaseName)) continue;
                    DbRecord own = game.Record(it.BaseName);
                    string set = own == null ? null : own.Get("itemSetName");
                    if (string.IsNullOrEmpty(set)) continue;
                    if (!pieces.ContainsKey(set)) pieces[set] = new List<string>();
                    if (!pieces[set].Contains(it.BaseName)) pieces[set].Add(it.BaseName);
                }
            Dictionary<string, int> made = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<string>> kv in pieces) made[kv.Key] = kv.Value.Count;
            return made;
        }

        // Granted-skill level uses the same piece-count index; zero means the set
        // does not grant the skill at that count.
        public static int GrantedLevelOfSet(DbRecord set, int pieces)
        {
            if (set == null || string.IsNullOrEmpty(set.Get("itemSkillName"))) return 0;
            double v = AttackSkill.Value(set, "itemSkillLevel", Math.Max(0, pieces - 1));
            return v > 0 ? (int)v : 0;
        }

        static bool ModifiersOn(DbRecord set, int pieces)
        {
            string s = set.Get("itemSkillModifierControl");
            if (string.IsNullOrEmpty(s)) return false;
            string[] parts = s.Split(';');
            int i = Math.Min(Math.Max(pieces - 1, 0), parts.Length - 1);
            double v;
            return double.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out v) && v > 0;
        }
    }
}
