// Available item prefixes and suffixes.
//
// Items do not store affix lists directly. The loot table (`LootItemTable_DynWeight`)
// lists item names and the affix tables that apply to them:
//
//   prefixTableName1..N       normal prefixes
//   rarePrefixTableName1..N   rare prefixes
//   suffixTableName1..N       normal suffixes
//   rareSuffixTableName1..N   rare suffixes
//
// Each name points to a `LootRandomizerTable`, whose `randomizerName1..N` fields
// list the affixes. An item may occur in several loot tables; their sets are merged.
//
// This mapping is based on saved items: game-generated items match their loot tables.
// Manually injected affixes are accepted by the game but are not offered by the editor.
//
// Each item has at most one prefix and one suffix (`Item.PrefixName`,
// `Item.SuffixName`). `bothPrefixSuffix` fields are drop probabilities, not extra slots.
//
// The index is cached beside the program as `affixes.gdd`. Its signature follows
// `ItemCatalog.SignatureOf`, so a game update rebuilds it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gdc.Gui
{
    public static partial class Affixes
    {
        public const int Version = 1;
        const string TablesDir = "items/loottables/";

        public class Choice
        {
            public string Path;    // affix record path
            public bool Rare;      // record `itemClassification`: Rare or Magical
            public int Level;      // affix `levelRequirement`
        }

        // Affix tables found for one item; normal and rare tables are combined.
        class Sets
        {
            public readonly List<string> Prefix = new List<string>();
            public readonly List<string> Suffix = new List<string>();
        }

        static Dictionary<string, Sets> index;
        static string builtFor = "";

        // Source of the current index, used in validation reports.
        public static bool CameFromCache;
        public static int ItemCount { get { return index == null ? 0 : index.Count; } }

        public static string CachePath
        {
            get { return CacheFile.Of("affixes"); }
        }

        // Whether the index is ready. It is built once per process; `progress` reports work.
        public static void Ensure(GameData game, Action<string> progress)
        {
            if (game == null) return;
            string signature = ItemCatalog.SignatureOf(game.GameDir);
            if (index != null && builtFor == signature) return;
            if (LoadCache(signature)) { builtFor = signature; CameFromCache = true; return; }
            if (progress != null) progress(Lang.T("edit.affix.building"));
            index = Build(game);
            builtFor = signature;
            CameFromCache = false;
            SaveCache(signature);
        }

        // Whether this item has any affix tables. The edit form uses the same flag
        // to show or hide its affix control.
        public static bool CanHave(GameData game, string itemPath)
        {
            Ensure(game, null);
            Sets s;
            return itemPath != null && index != null && index.TryGetValue(itemPath, out s) &&
                   (s.Prefix.Count > 0 || s.Suffix.Count > 0);
        }

        // Affixes available for this item. `suffix` selects suffixes instead of prefixes.
        public static List<Choice> Choices(GameData game, string itemPath, bool suffix)
        {
            List<Choice> found = new List<Choice>();
            if (game == null || itemPath == null) return found;
            Ensure(game, null);
            Sets s;
            if (index == null || !index.TryGetValue(itemPath, out s)) return found;
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string table in suffix ? s.Suffix : s.Prefix)
            {
                DbRecord t = game.Record(table);
                if (t == null) continue;
                foreach (KeyValuePair<string, string> kv in t.Fields)
                {
                    if (string.IsNullOrEmpty(kv.Value) || !Numbered(kv.Key, "randomizerName")) continue;
                    if (seen.ContainsKey(kv.Value)) continue;
                    seen[kv.Value] = true;
                    DbRecord a = game.Record(kv.Value);
                    if (a == null) continue;   // record from an unavailable expansion
                    Choice c = new Choice();
                    c.Path = kv.Value;
                    c.Rare = string.Equals(a.Get("itemClassification"), "Rare", StringComparison.OrdinalIgnoreCase);
                    c.Level = a.GetInt("levelRequirement");
                    found.Add(c);
                }
            }
            return found;
        }

        // Localized affix name. If the record has no name tag, the caller displays its effect.
        public static string NameOf(GameData game, string affix)
        {
            DbRecord r = game == null ? null : game.Record(affix);
            if (r == null) return "";
            string raw = game.Tag(r.Get("lootRandomizerName"));
            if (string.IsNullOrEmpty(raw)) return "";
            // The game stores affix names in lowercase because they appear inside item
            // names. In a standalone list, capitalize the first character.
            string name = GameData.Form(raw, GameData.GenderOf(raw));
            return string.IsNullOrEmpty(name) ? ""
                 : char.ToUpper(name[0], System.Globalization.CultureInfo.CurrentCulture) + name.Substring(1);
        }

        // Build an item with a substituted affix so its rolled values are calculated
        // in the same shared seed stream as the neighboring item parts.
        public static Item With(Item item, string affix, bool suffix)
        {
            Item made = new Item();
            made.BaseName = item.BaseName;
            made.Seed = item.Seed;
            made.PrefixName = suffix ? item.PrefixName : (affix ?? "");
            made.SuffixName = suffix ? (affix ?? "") : item.SuffixName;
            made.ModifierName = item.ModifierName;
            made.TransmuteName = item.TransmuteName;
            made.RelicName = item.RelicName;
            made.RelicBonus = item.RelicBonus;
            made.RelicSeed = item.RelicSeed;
            made.AugmentName = item.AugmentName;
            made.AugmentSeed = item.AugmentSeed;
            made.AscensionName = item.AscensionName;
            made.AscensionSeed = item.AscensionSeed;
            return made;
        }

        // Render the full item and return the section belonging to this affix.
        //
        // Do not rely only on `TipLine.Part`: it is set only for rolled values. An
        // affix with static fields may otherwise appear empty.
        public static List<string> Gives(GameData game, Item item, string affix, bool suffix)
        {
            List<string> made = Section(game, With(item, affix, suffix), suffix);
            if (made.Count > 0 || game == null || item == null || string.IsNullOrEmpty(affix)) return made;
            // An affix may have no own section: pet bonuses are rendered as one item-wide
            // section. Compare the item with and without the affix to isolate its lines.
            List<string> was = PetLines(game, With(item, "", suffix), suffix);
            List<string> now = PetLines(game, With(item, affix, suffix), suffix);
            string head = ItemViewFactory.Word(game, "tagPetBonusNameAllPets", Lang.T("item.petBonusAll"));
            foreach (string one in now)
                if (!was.Contains(one)) made.Add(made.Count == 0 ? head + ": " + one : one);
            return made;
        }

        // Pet-bonus section lines for the assembled item.
        static List<string> PetLines(GameData game, Item probe, bool suffix)
        {
            List<string> made = new List<string>();
            ItemView v = ItemViewFactory.Build(game, probe, false);
            string head = ItemViewFactory.Word(game, "tagPetBonusNameAllPets", Lang.T("item.petBonusAll"));
            bool inside = false;
            foreach (TipLine l in v.Details)
            {
                if (l.Kind == TipKind.Section) { inside = l.Text == head; continue; }
                if (!inside) continue;
                if (string.IsNullOrEmpty(l.Text)) { inside = false; continue; }
                made.Add(l.Text.Trim());
            }
            return made;
        }

        static List<string> Section(GameData game, Item probe, bool suffix)
        {
            List<string> made = new List<string>();
            if (game == null || probe == null) return made;
            ItemView v = ItemViewFactory.Build(game, probe, false);
            string label = Lang.T(suffix ? "item.part.suffix" : "item.part.prefix");
            bool inside = false;
            foreach (TipLine l in v.Details)
            {
                if (l.Kind == TipKind.Section)
                {
                    // A new section starts; the previous section is complete.
                    inside = l.Text != null &&
                             (l.Text == label || l.Text.StartsWith(label + ":", StringComparison.Ordinal));
                    continue;
                }
                // An empty line ends the section; following lines belong to the item footer.
                if (!inside) continue;
                if (string.IsNullOrEmpty(l.Text)) { inside = false; continue; }
                made.Add(l.Text.Trim());
            }
            return made;
        }

        // List line containing the affix name and its effect on the item.
        public static string Describe(GameData game, Item item, string affix, bool suffix)
        {
            string name = NameOf(game, affix);
            List<string> gives = Gives(game, item, affix, suffix);
            List<string> head = new List<string>();
            foreach (string s in gives) { head.Add(s); if (head.Count >= 4) break; }
            string what = string.Join(";   ", head.ToArray());
            if (string.IsNullOrEmpty(name))
                return what.Length > 0 ? what : Path.GetFileNameWithoutExtension(affix);
            return name + (what.Length > 0 ? "   —   " + what : "");
        }

        // === validation ===

        // Items injected by `--additems` may use manually selected affixes. Skip only
        // those explicitly listed as test items; all other mismatches remain errors.
        static readonly string[] Ours =
        {
            "gearaccessories/medals/a009_medal01.dbr|lootaffixes/prefix/ad002b_res_fire_06.dbr",
            "gearaccessories/medals/a009_medal01.dbr|lootaffixes/prefix/ao004b_cold_06.dbr",
            "gearaccessories/medals/a009_medal01.dbr|lootaffixes/suffix/a018a_ch_oa_07.dbr",
            "gearweapons/caster/a11_scepter004.dbr|lootaffixes/prefix/aa014a_nightblade08_je.dbr",
        };

        // Whether any current loot table can offer this affix. Only tables referenced
        // by at least one loot table are considered live.
        //
        // Old saves may contain affixes removed from current loot tables. Report them
        // as retired rather than invalid: they should not be offered in new lists.
        public static bool Offerable(GameData game, string affix)
        {
            if (game == null || string.IsNullOrEmpty(affix)) return false;
            Ensure(game, null);
            if (live == null)
            {
                live = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, bool> tables = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, Sets> kv in index)
                {
                    foreach (string t in kv.Value.Prefix) tables[t] = true;
                    foreach (string t in kv.Value.Suffix) tables[t] = true;
                }
                foreach (string path in tables.Keys)
                {
                    DbRecord t = game.Record(path);
                    if (t == null) continue;
                    foreach (KeyValuePair<string, string> kv in t.Fields)
                        if (!string.IsNullOrEmpty(kv.Value) && Numbered(kv.Key, "randomizerName"))
                            live[kv.Value] = true;
                }
            }
            return live.ContainsKey(affix);
        }
        static Dictionary<string, bool> live;




        static string ShortName(string path)
        {
            return string.IsNullOrEmpty(path) ? "" : Path.GetFileNameWithoutExtension(path);
        }

        // === index ===

        static Dictionary<string, Sets> Build(GameData game)
        {
            Dictionary<string, Sets> made = new Dictionary<string, Sets>(StringComparer.OrdinalIgnoreCase);
            List<string> items = new List<string>();
            List<string> prefix = new List<string>();
            List<string> suffix = new List<string>();
            foreach (string path in game.PathsContaining(TablesDir))
            {
                DbRecord rec = game.Record(path);
                if (rec == null) continue;
                items.Clear(); prefix.Clear(); suffix.Clear();
                foreach (KeyValuePair<string, string> kv in rec.Fields)
                {
                    string k = kv.Key, v = kv.Value;
                    if (string.IsNullOrEmpty(v)) continue;
                    if (Numbered(k, "lootName")) items.Add(v);
                    else if (Numbered(k, "prefixTableName") || Numbered(k, "rarePrefixTableName")) prefix.Add(v);
                    else if (Numbered(k, "suffixTableName") || Numbered(k, "rareSuffixTableName")) suffix.Add(v);
                }
                if (items.Count == 0 || (prefix.Count == 0 && suffix.Count == 0)) continue;
                foreach (string one in items)
                {
                    Sets s;
                    if (!made.TryGetValue(one, out s)) { s = new Sets(); made[one] = s; }
                    Add(s.Prefix, prefix);
                    Add(s.Suffix, suffix);
                }
            }
            return made;
        }

        static void Add(List<string> into, List<string> what)
        {
            foreach (string one in what)
                if (!into.Contains(one)) into.Add(one);
        }

        // `lootName12` is valid; `lootNameX` and `lootName` are not.
        static bool Numbered(string key, string name)
        {
            if (key == null || key.Length <= name.Length) return false;
            if (!key.StartsWith(name, StringComparison.Ordinal)) return false;
            for (int i = name.Length; i < key.Length; i++)
                if (key[i] < '0' || key[i] > '9') return false;
            return true;
        }

        static bool LoadCache(string signature)
        {
            try
            {
                if (!File.Exists(CachePath)) return false;
                string[] lines = File.ReadAllLines(CachePath);
                if (lines.Length == 0) return false;
                string[] head = lines[0].TrimStart('﻿').Split('\t');
                if (head.Length < 3 || head[0] != "#gdc-affixes" || head[1] != Version.ToString() ||
                    head[2] != signature) return false;
                Dictionary<string, Sets> made = new Dictionary<string, Sets>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] f = lines[i].Split('\t');
                    if (f.Length < 3) continue;
                    Sets s = new Sets();
                    Add(s.Prefix, new List<string>(f[1].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)));
                    Add(s.Suffix, new List<string>(f[2].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)));
                    made[f[0]] = s;
                }
                if (made.Count == 0) return false;
                index = made;
                return true;
            }
            catch (Exception) { return false; }
        }

        static void SaveCache(string signature)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("#gdc-affixes\t").Append(Version).Append('\t').Append(signature).Append('\n');
                foreach (KeyValuePair<string, Sets> kv in index)
                    sb.Append(kv.Key).Append('\t')
                      .Append(string.Join(";", kv.Value.Prefix.ToArray())).Append('\t')
                      .Append(string.Join(";", kv.Value.Suffix.ToArray())).Append('\n');
                File.WriteAllText(CachePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { /* cache is an optimization, not a requirement */ }
        }
    }
}
