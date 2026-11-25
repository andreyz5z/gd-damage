// The list of every item the game knows about, for picking one to add to a bag.
// Built by walking the .arz databases once and cached on disk, because a full
// scan decompresses about ten thousand records.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gdc.Gui
{
    // One per equipment slot rather than one per broad kind: looking for
    // something to put in a specific slot is what the catalogue is for, and
    // "jewellery" mixing rings, amulets and medals made that harder than it
    // needed to be. The order is the order of the filter list.
    public enum ItemCategory
    {
        Head, Shoulders, Chest, Hands, Waist, Legs, Feet,
        Amulet, Ring, Medal,
        Relic,          // gearrelic — the big relics
        Weapon1H, Weapon2H, Ranged1H, Ranged2H,
        Shield, Offhand,
        Component,      // materia
        Augment,        // enchants
        Consumable,
        Crafting,
        Other
    }

    public class CatalogEntry
    {
        public string Dbr = "";
        public string Name = "";
        public string Classification = "";
        public string RecordClass = "";
        public ItemCategory Category;
        public int LevelRequirement;
        public int ItemLevel;
        public string Bitmap = "";
        // The tag the game names the item by — `itemNameTag`, or `description` for
        // the kinds that use that instead (components, relics, augments). Unlike
        // `Name` it does not change with the language, so it is what an outside
        // list of items is matched against (`GrimToolsMap`).
        public string NameTag = "";
        // Every bonus the item grants, worded as the description words it, so
        // the catalogue can be searched by statistics as well as by name.
        public string Stats = "";

        public override string ToString() { return Name; }
    }

    public class ItemCatalog
    {
        public List<CatalogEntry> Entries = new List<CatalogEntry>();
        public string GamePath = "";
        public string Signature = "";   // identifies the game build the cache was made from
        // Names and bonuses are stored in the language they were built in, so a
        // change of language has to rebuild the cache the same as a game patch.
        public string Language = "";

        public static string CachePath
        {
            get { return CacheFile.Of("catalog"); }
        }

        static readonly string[] Databases = {
            @"database\database.arz", @"gdx1\database\GDX1.arz",
            @"gdx2\database\GDX2.arz", @"gdx3\database\GDX3.arz" };

        // Size and timestamp of every database, so a patched game is noticed.
        public static string SignatureOf(string gameDir)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string rel in Databases)
            {
                string p = Path.Combine(gameDir ?? "", rel);
                if (!File.Exists(p)) continue;
                FileInfo fi = new FileInfo(p);
                sb.Append(rel).Append(':').Append(fi.Length).Append(':')
                  .Append(fi.LastWriteTimeUtc.Ticks).Append(';');
            }
            return sb.ToString();
        }

        // The record's own class name says exactly which slot an item belongs in,
        // the same field the equipment panel uses to decide what it accepts.
        public static ItemCategory CategoryOf(string recordClass, string dbr)
        {
            switch (recordClass ?? "")
            {
                case "ArmorProtective_Head": return ItemCategory.Head;
                case "ArmorProtective_Shoulders": return ItemCategory.Shoulders;
                case "ArmorProtective_Chest": return ItemCategory.Chest;
                case "ArmorProtective_Hands": return ItemCategory.Hands;
                case "ArmorProtective_Waist": return ItemCategory.Waist;
                case "ArmorProtective_Legs": return ItemCategory.Legs;
                case "ArmorProtective_Feet": return ItemCategory.Feet;
                case "ArmorJewelry_Amulet": return ItemCategory.Amulet;
                case "ArmorJewelry_Ring": return ItemCategory.Ring;
                case "ArmorJewelry_Medal": return ItemCategory.Medal;
                case "ItemArtifact": return ItemCategory.Relic;

                case "WeaponMelee_Axe":
                case "WeaponMelee_Dagger":
                case "WeaponMelee_Mace":
                case "WeaponMelee_Scepter":
                case "WeaponMelee_Sword":
                case "WeaponHunting_Spear": return ItemCategory.Weapon1H;
                case "WeaponMelee_Axe2h":
                case "WeaponMelee_Mace2h":
                case "WeaponMelee_Sword2h":
                case "WeaponMelee_Spear2h":
                case "WeaponMagical_Staff": return ItemCategory.Weapon2H;
                case "WeaponHunting_Ranged1h": return ItemCategory.Ranged1H;
                case "WeaponHunting_Ranged2h": return ItemCategory.Ranged2H;
                case "WeaponArmor_Shield": return ItemCategory.Shield;
                case "WeaponArmor_Offhand": return ItemCategory.Offhand;

                case "ItemRelic": return ItemCategory.Component;
                case "ItemEnchantment": return ItemCategory.Augment;
            }
            string cls = recordClass ?? "";
            if (cls.StartsWith("OneShot", StringComparison.OrdinalIgnoreCase) ||
                cls.IndexOf("Usable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.IndexOf("Potion", StringComparison.OrdinalIgnoreCase) >= 0) return ItemCategory.Consumable;
            if (dbr.IndexOf("/crafting/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.IndexOf("Formula", StringComparison.OrdinalIgnoreCase) >= 0) return ItemCategory.Crafting;
            return ItemCategory.Other;
        }

        public static bool IsWearable(ItemCategory c) { return c <= ItemCategory.Offhand; }

        // Section names come from `lang/<CODE>/catalog.txt`.
        public static string CategoryName(ItemCategory c)
        {
            switch (c)
            {
                case ItemCategory.Head: return Lang.T("catalog.category.head");
                case ItemCategory.Shoulders: return Lang.T("catalog.category.shoulders");
                case ItemCategory.Chest: return Lang.T("catalog.category.chest");
                case ItemCategory.Hands: return Lang.T("catalog.category.hands");
                case ItemCategory.Waist: return Lang.T("catalog.category.waist");
                case ItemCategory.Legs: return Lang.T("catalog.category.legs");
                case ItemCategory.Feet: return Lang.T("catalog.category.feet");
                case ItemCategory.Amulet: return Lang.T("catalog.category.amulet");
                case ItemCategory.Ring: return Lang.T("catalog.category.ring");
                case ItemCategory.Medal: return Lang.T("catalog.category.medal");
                case ItemCategory.Relic: return Lang.T("catalog.category.relic");
                case ItemCategory.Weapon1H: return Lang.T("catalog.category.weapon1h");
                case ItemCategory.Weapon2H: return Lang.T("catalog.category.weapon2h");
                case ItemCategory.Ranged1H: return Lang.T("catalog.category.ranged1h");
                case ItemCategory.Ranged2H: return Lang.T("catalog.category.ranged2h");
                case ItemCategory.Shield: return Lang.T("catalog.category.shield");
                case ItemCategory.Offhand: return Lang.T("catalog.category.offhand");
                case ItemCategory.Component: return Lang.T("catalog.category.component");
                case ItemCategory.Augment: return Lang.T("catalog.category.augment");
                case ItemCategory.Consumable: return Lang.T("catalog.category.consumable");
                case ItemCategory.Crafting: return Lang.T("catalog.category.crafting");
                default: return Lang.T("catalog.category.other");
            }
        }

        public static ItemCatalog Build(string gameDir, string language, Action<string> progress)
        {
            ItemCatalog catalog = new ItemCatalog();
            catalog.GamePath = gameDir;
            catalog.Signature = SignatureOf(gameDir);
            catalog.Language = language ?? "";

            using (GameData game = new GameData(gameDir, language))
            {
                Dictionary<string, CatalogEntry> seen = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (string rel in Databases)
                {
                    string path = Path.Combine(gameDir, rel);
                    if (!File.Exists(path)) continue;
                    if (progress != null) progress("Reading " + rel);
                    Arz.ForEachRecord(path,
                        delegate (string p) { return p.StartsWith("records/items/", StringComparison.OrdinalIgnoreCase); },
                        delegate (DbRecord rec)
                        {
                            CatalogEntry e = FromRecord(game, rec);
                            if (e != null) seen[e.Dbr] = e;
                        });
                }
                catalog.Entries.AddRange(seen.Values);
            }
            catalog.Entries.Sort(delegate (CatalogEntry a, CatalogEntry b)
            {
                int c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                return c != 0 ? c : string.Compare(a.Dbr, b.Dbr, StringComparison.OrdinalIgnoreCase);
            });
            return catalog;
        }

        static CatalogEntry FromRecord(GameData game, DbRecord rec)
        {
            // Affixes are not items; they are handled separately when an item is
            // built. Everything else with a readable name is a candidate.
            if (rec.Path.IndexOf("/lootaffixes/", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            // Monster-only gear never reaches a player bag — none of the sample
            // characters carries a single piece of it.
            if (rec.Path.IndexOf("/enemygear/", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            // Chests and scenery are records under records/items but not items.
            if (rec.Class == "FixedItemContainer" || rec.Class == "Destructible") return null;
            if (rec.Path.IndexOf("/lootchests/", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            // Crafting random-item records are templates, not usable items. The
            // game completes them only during crafting, so saving one as an item
            // can corrupt the save. Exclude them by record path and description.
            //
            // Building a complete item from a base and affixes is a separate task;
            // this filter only prevents invalid templates from being saved.
            if (rec.Path.IndexOf("/crafting/crafting_random", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            // Apply the description filter only to equippable records. Blueprints
            // may share the description but are valid inventory items.
            if (rec.Get("itemText") == "tagCraftRandomItemDesc" &&
                IsWearable(CategoryOf(rec.Class, rec.Path))) return null;

            string nameTag = rec.Get("itemNameTag");
            if (string.IsNullOrEmpty(nameTag)) nameTag = rec.Get("description");
            string raw = game.Tag(nameTag);
            if (string.IsNullOrEmpty(raw)) return null;

            string gender = GameData.GenderOf(raw);
            List<string> parts = new List<string>();
            string quality = GameData.Form(game.Tag(rec.Get("itemQualityTag")), gender);
            string styleRaw = game.Tag(rec.Get("itemStyleTag"));
            if (string.IsNullOrEmpty(styleRaw) && rec.Path.IndexOf("/awakened/", StringComparison.OrdinalIgnoreCase) >= 0)
                styleRaw = game.Tag("tagStyleUniqueAwakened");
            string style = GameData.Form(styleRaw, gender);
            if (!string.IsNullOrEmpty(quality)) parts.Add(quality);
            if (!string.IsNullOrEmpty(style)) parts.Add(style);
            parts.Add(GameData.Form(raw, gender));

            CatalogEntry e = new CatalogEntry();
            e.Dbr = rec.Path;
            e.Name = string.Join(" ", parts.ToArray());
            if (e.Name.Length > 0) e.Name = char.ToUpper(e.Name[0]) + e.Name.Substring(1);
            e.Classification = rec.Get("itemClassification") ?? "";
            e.RecordClass = rec.Class ?? "";
            e.Category = CategoryOf(e.RecordClass, e.Dbr);
            e.LevelRequirement = rec.GetInt("levelRequirement");
            e.ItemLevel = rec.GetInt("itemLevel");
            e.Bitmap = ItemViewFactory.IconField(rec) ?? "";
            e.NameTag = nameTag ?? "";
            e.Stats = ItemViewFactory.SearchableStats(game, rec);
            return e;
        }

        static string FirstOf(DbRecord rec, params string[] fields)
        {
            foreach (string f in fields)
            {
                string v = rec.Get(f);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        // ------------------------------------------------------------- cache

        public void Save(string path)
        {
            using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                // Include `itemNameTag` in the signature so changes rebuild the
                // cache used by `GrimToolsMap`.
                w.WriteLine("#gdc-catalog\t8\t" + GamePath + "\t" + Signature + "\t" + Language);
                foreach (CatalogEntry e in Entries)
                    w.WriteLine(e.Dbr + "\t" + e.Name + "\t" + e.Classification + "\t" +
                                e.RecordClass + "\t" + (int)e.Category + "\t" +
                                e.LevelRequirement + "\t" + e.ItemLevel + "\t" + e.Bitmap + "\t" +
                                e.NameTag + "\t" + e.Stats);
            }
        }

        public static ItemCatalog Load(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                ItemCatalog c = new ItemCatalog();
                using (StreamReader r = new StreamReader(path, Encoding.UTF8))
                {
                    string header = r.ReadLine();
                    if (header == null || !header.StartsWith("#gdc-catalog\t8\t")) return null;
                    string[] h = header.Split('\t');
                    if (h.Length < 5) return null;
                    c.GamePath = h[2];
                    c.Signature = h[3];
                    c.Language = h[4];
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        string[] f = line.Split('\t');
                        if (f.Length < 10) continue;
                        CatalogEntry e = new CatalogEntry();
                        e.Dbr = f[0]; e.Name = f[1]; e.Classification = f[2]; e.RecordClass = f[3];
                        e.Category = (ItemCategory)int.Parse(f[4]);
                        int.TryParse(f[5], out e.LevelRequirement);
                        int.TryParse(f[6], out e.ItemLevel);
                        e.Bitmap = f[7];
                        e.NameTag = f[8];
                        e.Stats = f[9];
                        c.Entries.Add(e);
                    }
                }
                return c;
            }
            catch (Exception) { return null; }
        }

        public bool MatchesGame(string gameDir, string language)
        {
            return string.Equals(GamePath, gameDir, StringComparison.OrdinalIgnoreCase) &&
                   Signature == SignatureOf(gameDir) &&
                   string.Equals(Language, language ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }
}
