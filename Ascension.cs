// Ascension bonuses and the item classes that can use them.
//
// The bonus is stored after `augmentSeed`, comes from
// `records/items/lootaffixes/ascended/`, and is not rolled. The game groups
// records into `LootRandomizerTable` entries under `ascended/ascendedtables/`;
// their names define the item pools.
//
//     ascendedaffixes_armor_*             armor
//     ascendedaffixes_accessory_*         accessories
//     ascendedaffixes_weaponmelee1h_*     one-handed melee
//     ascendedaffixes_weaponmelee2h_*     two-handed melee
//     ascendedaffixes_weaponrange1h/2h_*  ranged weapons
//     ascendedaffixes_weaponfocus1h_*     off-hand focus
//     ascendedaffixes_weaponshield1h_*    shield
//     ascendedaffixes_weapon1h/2h_offense shared one-/two-handed pool
//
// Do not infer pools from the last character of an affix name. There are eight
// pools, split by weapon mastery; use the table names instead.
//
// `--asctest` compares the pool mapping with ascended items in saves. A mismatch
// caused by a manually constructed test item is not evidence against the rule.
//
//     ArmorJewelry_Amulet     -> accessory
//     ArmorProtective_Chest   -> armor
//     WeaponMelee_Sword       -> weapon2h (manual test item)
//
// A manually constructed one-handed item may reference a two-handed pool; keep
// this exception out of the game-data rule.
//
// The default list is restricted to the item's pools. The optional all-pools mode
// exposes other pools for inspection and manual overrides.
//
// Shield behavior outside `weaponshield1h_*` is not inferred without a matching
// game save.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gdc.Gui
{
    public static partial class Ascension
    {
        public const string TableDir = "records/items/lootaffixes/ascended/ascendedtables/";

        // Pools available to an item class, derived from the table names:
        //
        //     ascendedaffixes_armor_*            armor
        //     ascendedaffixes_accessory_*        accessories
        //     ascendedaffixes_weaponmelee1h_*    one-handed melee
        //     ascendedaffixes_weaponmelee2h_*    two-handed melee
        //     ascendedaffixes_weaponrange1h/2h_* ranged
        //     ascendedaffixes_weaponfocus1h_*    off-hand focus
        //     ascendedaffixes_weaponshield1h_*   shield
        //     ascendedaffixes_weapon1h/2h_offense  shared weapon pool
        //
        // Weapon mastery tables are split by weapon type; `ao3xx` offensive tables
        // are shared by all one- or two-handed weapons. Item classes come from
        // `EquipRules`.
        //
        // Without enough in-game saves, the default list is restricted to the
        // item's pool; all-pools mode removes that restriction.
        public static List<string> Pools(string itemClass)
        {
            List<string> pools = new List<string>();
            if (string.IsNullOrEmpty(itemClass)) return pools;
            if (itemClass.StartsWith("ArmorJewelry", StringComparison.OrdinalIgnoreCase)) { pools.Add("accessory"); return pools; }
            if (string.Equals(itemClass, "WeaponArmor_Shield", StringComparison.OrdinalIgnoreCase)) { pools.Add("weaponshield1h"); return pools; }
            if (string.Equals(itemClass, "WeaponArmor_Offhand", StringComparison.OrdinalIgnoreCase)) { pools.Add("weaponfocus1h"); return pools; }
            if (itemClass.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase))
            {
                bool two = EquipRules.IsTwoHanded(itemClass);
                bool range = itemClass.IndexOf("Ranged", StringComparison.OrdinalIgnoreCase) >= 0;
                pools.Add((range ? "weaponrange" : "weaponmelee") + (two ? "2h" : "1h"));
                pools.Add(two ? "weapon2h" : "weapon1h");
                return pools;
            }
            if (itemClass.StartsWith("Armor", StringComparison.OrdinalIgnoreCase)) { pools.Add("armor"); return pools; }
            return pools;
        }

        // The first pool supplies the short label used by the window and tests.
        public static string PoolOf(string itemClass)
        {
            List<string> p = Pools(itemClass);
            return p.Count == 0 ? null : string.Join("+", p.ToArray());
        }

        public class Choice
        {
            public string Path;
            public string Pool;        // source selection table
            public bool Suggested;     // pool suggested for this item
        }

        // Ascension bonuses from the game's selection tables. With
        // `everything = false`, return only the item's pools; otherwise return
        // all pools and mark non-matching entries.
        public static List<Choice> Choices(GameData game, string itemClass, bool everything)
        {
            List<Choice> found = new List<Choice>();
            if (game == null) return found;
            List<string> want = Pools(itemClass);
            Dictionary<string, Choice> seen = new Dictionary<string, Choice>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in game.PathsContaining(TableDir))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (!name.StartsWith("ascendedaffixes_", StringComparison.OrdinalIgnoreCase)) continue;
                string pool = PoolOfTable(name);
                DbRecord table = game.Record(path);
                if (table == null || pool == null) continue;
                for (int i = 1; i <= 64; i++)
                {
                    string one = table.Get("randomizerName" + i);
                    if (string.IsNullOrEmpty(one)) continue;
                    Choice had;
                    bool mine = want.Contains(pool);
                    if (seen.TryGetValue(one, out had)) { if (mine) { had.Pool = pool; had.Suggested = true; } continue; }
                    if (!mine && !everything) continue;
                    Choice c = new Choice();
                    c.Path = one; c.Pool = pool; c.Suggested = mine;
                    seen[one] = c;
                    found.Add(c);
                }
            }
            found.Sort(delegate (Choice a, Choice b)
            {
                if (a.Suggested != b.Suggested) return a.Suggested ? -1 : 1;
                int c = string.Compare(a.Pool, b.Pool, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
            return found;
        }

        // «ascendedaffixes_weapon1h_mastery03a» → «weapon1h».
        static string PoolOfTable(string fileName)
        {
            int a = fileName.IndexOf('_'), b = fileName.IndexOf('_', a + 1);
            return a < 0 || b < 0 ? null : fileName.Substring(a + 1, b - a - 1);
        }

        // Selection tables that can provide this affix; used to verify the
        // item's actual pool.
        public static List<string> PoolsWith(GameData game, string affix)
        {
            List<string> pools = new List<string>();
            if (game == null || string.IsNullOrEmpty(affix)) return pools;
            foreach (string path in game.PathsContaining(TableDir))
            {
                DbRecord table = game.Record(path);
                if (table == null) continue;
                bool has = false;
                for (int i = 1; i <= 64 && !has; i++)
                    if (string.Equals(table.Get("randomizerName" + i), affix, StringComparison.OrdinalIgnoreCase))
                        has = true;
                if (!has) continue;
                string pool = PoolOfTable(Path.GetFileNameWithoutExtension(path));
                if (pool != null && !pools.Contains(pool)) pools.Add(pool);
            }
            return pools;
        }

        // Display the game name, or the granted value when no name is available.
        public static string Describe(GameData game, string affix)
        {
            DbRecord r = game == null ? null : game.Record(affix);
            if (r == null) return affix;
            string name = game.Tag(r.Get("lootRandomizerName"));
            // Use a record name only when supplied by the game. Otherwise show
            // the granted value; the record filename is a last-resort fallback.
            // Was: if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(affix);
            List<TipLine> lines = ItemViewFactory.Describe(game, r);
            List<string> what = new List<string>();
            foreach (TipLine l in lines)
                if (!string.IsNullOrEmpty(l.Text) && what.Count < 4) what.Add(l.Text.Trim());
            string gives = string.Join("; ", what.ToArray());
            if (string.IsNullOrEmpty(name))
                return what.Count > 0 ? gives : Path.GetFileNameWithoutExtension(affix);
            return name + (what.Count > 0 ? "   —   " + gives : "");
        }


        static double Value(CharacterTotals t, string field)
        {
            TotalsEntry e;
            return t != null && t.Fields.TryGetValue(field, out e) ? e.Value : 0;
        }

        static bool Overlap(List<string> a, List<string> b)
        {
            foreach (string one in a) if (b.Contains(one)) return true;
            return false;
        }

        // ============================ self-test ============================

    }
}
