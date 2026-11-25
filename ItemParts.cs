// WHICH ITEM A COMPONENT OR AN AUGMENT MAY GO ON.
//
// Item parts can be restricted to rings, boots, any armor, one-handed weapons,
// or two-handed weapons.
//
// THE GAME SAYS IT OUTRIGHT, AND NOT IN WORDS — IN FLAGS. A component's record carries
// a field set to 1 for every kind of item it fits, and nothing else about it:
//
//     records/items/materia/compb_dreadskull.dbr    class=ItemRelic
//         amulet  1
//         medal   1
//
// An augment is the same record with another class — `ItemEnchantment` — and the same
  // flags (for example: axe, dagger, mace, offhand, ranged1h, scepter,
// shield, sword…). So one rule serves both.
//
// THE FLAGS AND THE CLASSES OF ITEMS ARE THE SAME TWENTY-THREE WORDS. Counted over
// every `ItemRelic` and `ItemEnchantment` the game ships (107 and 379): the flags are
// amulet, axe, axe2h, chest, dagger, feet, hands, head, legs, mace, mace2h, medal,
// offhand, ranged1h, ranged2h, ring, scepter, shield, shoulders, spear2h, sword,
// sword2h, waist — and the catalogue's classes are `ArmorJewelry_Amulet`,
// `WeaponMelee_Axe`, `WeaponMelee_Axe2h`, … , `ArmorProtective_Waist`. One for one,
// tail for tail. So the rule is not a table written out by hand (which would go stale
// at the next patch): THE FLAG OF AN ITEM IS ITS CLASS AFTER THE UNDERSCORE.
//
// AND IT IS NOT TAKEN ON TRUST — `--parttest` walks every save and asks the rule about
// every component and augment the GAME ITSELF attached. Red seen: `--partbreak`.

using System;
using System.Collections.Generic;

namespace Gdc.Gui
{
    public static class ItemParts
    {
        public enum Part { None, Component, Augment }

        // `--partbreak` — read the flag off the WRONG HALF of the class
        // (`armorprotective` instead of `head`), which is exactly the mistake the rule
        // below could have been written with. Everything then fits nothing, and the
        // check that walks the saves is seen going red (PLAN, rule 12).
        public static bool BreakFit;

        public static Part KindOf(string recordClass)
        {
            if (string.Equals(recordClass, "ItemRelic", StringComparison.OrdinalIgnoreCase)) return Part.Component;
            if (string.Equals(recordClass, "ItemEnchantment", StringComparison.OrdinalIgnoreCase)) return Part.Augment;
            return Part.None;
        }

        public static Part KindOf(GameData game, string dbr)
        {
            DbRecord rec = game == null || string.IsNullOrEmpty(dbr) ? null : game.Record(dbr);
            return rec == null ? Part.None : KindOf(rec.Class);
        }

        // THE FLAG AN ITEM ANSWERS TO: its class after the underscore, lowercased.
        // `WeaponMelee_Sword2h` → `sword2h`, `ArmorJewelry_Ring` → `ring`. A class with
        // no underscore is not a thing a part goes on at all — `ItemRelic`,
        // `ItemArtifact`, `QuestItem` — and gets no flag.
        public static string FlagOf(string recordClass)
        {
            if (string.IsNullOrEmpty(recordClass)) return null;
            int bar = recordClass.IndexOf('_');
            if (bar < 0 || bar + 1 >= recordClass.Length) return null;
            if (BreakFit) return recordClass.Substring(0, bar).ToLowerInvariant();
            return recordClass.Substring(bar + 1).ToLowerInvariant();
        }

        // Does this part go on an item of that class? The part's record is asked, so a
        // patch that lets a component onto one more kind of item is followed without
        // anything here being touched.
        public static bool Fits(DbRecord part, string targetClass)
        {
            if (part == null) return false;
            string flag = FlagOf(targetClass);
            if (flag == null) return false;
            return part.GetInt(flag) > 0;
        }

        public static bool Fits(GameData game, string partDbr, string targetClass)
        {
            DbRecord rec = game == null || string.IsNullOrEmpty(partDbr) ? null : game.Record(partDbr);
            return Fits(rec, targetClass);
        }

        // Every kind of item this part fits, by the game's own flags — for the line
        // that says why it did not go on. The words are the game's own names of the
        // item classes, looked up by the caller; here only the flags are collected.
        public static List<string> FlagsOf(DbRecord part)
        {
            List<string> made = new List<string>();
            if (part == null) return made;
            foreach (KeyValuePair<string, string> kv in part.Fields)
            {
                if (!Known.Contains(kv.Key)) continue;
                if (part.GetInt(kv.Key) > 0) made.Add(kv.Key);
            }
            made.Sort(StringComparer.Ordinal);
            return made;
        }

        // The flags as the game's records spell them. The list is here only so that
        // `FlagsOf` can tell a flag from the hundred other fields of a record; the
        // RULE above does not read it, and a new flag in a patch would still work.
        static readonly HashSet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "amulet", "axe", "axe2h", "chest", "dagger", "feet", "hands", "head", "legs",
            "mace", "mace2h", "medal", "offhand", "ranged1h", "ranged2h", "ring",
            "scepter", "shield", "shoulders", "spear2h", "sword", "sword2h", "waist",
        };
    }
}
