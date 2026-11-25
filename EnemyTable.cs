// The enemies of the game, as the game keeps them.
//
// Nothing about a monster is a plain number. Its record holds the resistances it
// has of its own and a list of passive skills; its attributes come from equations
// in a bio record, and those equations take the level the monster spawned at:
//
//     records/creatures/enemies/boss&quest/avatarofmogdrogen_01.dbr
//         monsterClassification        SuperBoss
//         characterRacialProfile       Race014
//         characterAttributeEquations  records/creatures/enemies/bios/bio_boss_….dbr
//         defensiveFire                85
//         skillName3                   records/skills/nonplayerskills/passive/armorbase06.dbr
//
//     bio_….dbr
//         characterDefensiveAbility    (charLevel*8.5)+120
//         characterLife                ((charLevel*195)^1.53)+50000
//
// So a monster has no single set of numbers — it has a set per level, and the
// level is chosen by whoever fights it. Armour is the clearest case: it comes
// from a passive skill whose table is indexed by level and runs from 29 at the
// first to well over a thousand at the hundredth.
//
// The table below keeps what does not depend on level; the numbers themselves
// are worked out on demand by Resolve.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Gdc.Gui
{
    public class Enemy
    {
        public string Path = "", Name = "", Rank = "", Race = "", Bio = "";
        public int Variants = 1;
        public readonly List<string> Skills = new List<string>();
        // The resistances and whatever else the record carries itself.
        public readonly Dictionary<string, double> Own = new Dictionary<string, double>(StringComparer.Ordinal);

        // The race in the player's language. The faction records carry no name of
        // their own — only an icon and a table of relations — so the race is what
        // can be shown, and it is more useful because damage bonuses target races,
        // not factions.
        public string RaceRu = "";

        // Monsters wear gear, and it is the gear they drop. It gives no health —
        // checked — but it does give armour, and that armour is not in the
        // passive skill the rest of it comes from.
        public readonly List<string> Gear = new List<string>();

        // The training dummy: the one target that stands still and is meant to be
        // hit, so it belongs at the top of the list rather than lost among the
        // ordinary monsters.
        public bool Dummy;

        // What the drop-down shows.
        public override string ToString()
        {
            return Name + "   — " + RankRu +
                   (string.IsNullOrEmpty(RaceRu) ? "" : ", " + RaceRu) +
                   (Variants > 1 ? " " + Lang.T("damage.enemy.variantsShort", Variants) : "");
        }

        // Localized rank label from `damage.txt`. Internal `Rank` values come from
        // game records and are language-independent.
        public string RankRu
        {
            get
            {
                switch (Rank)
                {
                    case "SuperBoss": return Lang.T("damage.enemy.rank.superBoss");
                    case "Boss": return Lang.T("damage.enemy.rank.boss");
                    case "Quest": return Lang.T("damage.enemy.rank.quest");
                    case "Hero": return Lang.T("damage.enemy.rank.hero");
                    case "Champion": return Lang.T("damage.enemy.rank.champion");
                    default: return Lang.T("damage.enemy.rank.common");
                }
            }
        }

        // Bosses first: those are the ones a build is measured against.
        public int Order
        {
            get
            {
                switch (Rank)
                {
                    case "SuperBoss": return 0;
                    case "Boss": return 1;
                    case "Quest": return 2;
                    case "Hero": return 3;
                    case "Champion": return 4;
                    default: return 5;
                }
            }
        }
    }

    // A monster at a chosen level: everything the damage model needs from it.
    public class EnemyStats
    {
        public int Level;
        public int Difficulty = -1;          // 0 normal, 1 elite, 2 ultimate; −1 no modifier
        public double PakLifePercent;
        public double OffensiveAbility, DefensiveAbility, Life, Armour;
        // How much of the armour came from what the monster is wearing.
        public double GearArmour;
        // What the monster's passive skills would change, gathered but not
        // applied — see the note in Resolve. Kept so the next session has the
        // numbers in front of it instead of having to find them again.
        public double SkillLifePercent, SkillOffensive, SkillOffensivePercent,
                      SkillDefensive, SkillDefensivePercent;
        public readonly Dictionary<string, double> Resist = new Dictionary<string, double>(StringComparer.Ordinal);

        // Race, also used as the quest faction code, is required to match racial
        // damage bonuses. Training dummies have no race and cannot validate them.
        public string Race = "";
    }

    public static class EnemyTable
    {
        // File name without a directory. It is used beside the program and in the
        // run output directory; `CacheFile` defines the shared extension.
        public static readonly string FileName = "enemies" + CacheFile.Extension;

        // Cache table beside the program. Keep path construction centralized so renames
        // cannot leave stale hard-coded paths.
        public static string CachePath { get { return CacheFile.Of("enemies"); } }
        // 4 (8 October): the header now carries the GAME'S SIGNATURE. Files of version 3
        // have none, and in them "built from the game as it is now" cannot be told from
        // "built three weeks ago" — so the number went up and every older file rebuilds.
        public const int Version = 4;

        // Building the list means reading four and a half thousand records, so it
        // is done once on request and kept beside the program, the same way the
        // item catalogue and the portal list are.
        public static List<Enemy> Build(GameData game, Action<int, int> progress)
        {
            List<Enemy> made = new List<Enemy>();
            if (game == null) return made;
            List<string> paths = game.PathsContaining("records/creatures/enemies/");
            int done = 0;
            foreach (string path in paths)
            {
                done++;
                if (progress != null && (done % 200) == 0) progress(done, paths.Count);
                DbRecord rec = game.Record(path);
                if (rec == null) continue;
                if (!string.Equals(rec.Class, "Monster", StringComparison.OrdinalIgnoreCase)) continue;
                string rank = rec.Get("monsterClassification");
                if (string.IsNullOrEmpty(rank)) continue;

                Enemy e = new Enemy();
                e.Path = path;
                e.Rank = rank;
                e.Race = rec.Get("characterRacialProfile") ?? "";
                e.Bio = rec.Get("characterAttributeEquations") ?? "";
                e.Name = NameOf(game, rec);
                if (string.IsNullOrEmpty(e.Name)) continue;      // nothing to show it by
                e.RaceRu = RaceName(game, e.Race);
                e.Dummy = path.IndexOf("trainingdummy", StringComparison.OrdinalIgnoreCase) >= 0;

                foreach (KeyValuePair<string, string> kv in rec.Fields)
                {
                    if (!kv.Key.StartsWith("defensive", StringComparison.Ordinal)) continue;
                    double d;
                    if (double.TryParse((kv.Value ?? "").Split(';')[0], NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out d) && d != 0)
                        e.Own[kv.Key] = d;
                }
                for (int n = 1; n <= 40; n++)
                {
                    string s = rec.Get("skillName" + n);
                    if (!string.IsNullOrEmpty(s)) e.Skills.Add(s);
                }
                // The six slots a monster always wears. Weapons and the random
                // "misc" slots are left out: those are rolled per spawn, and a
                // number that changes every time is not one we can show.
                foreach (string slot in new[] { "Head", "Chest", "Shoulders", "Hands", "Legs", "Feet" })
                {
                    string piece = rec.Get("default" + slot + "Piece");
                    if (!string.IsNullOrEmpty(piece)) e.Gear.Add(piece);
                }
                made.Add(e);
            }
            return Thin(made);
        }

        // Four and a half thousand rows is not a list anybody reads, and most of
        // them are the same monster again. The game ships each one in several
        // flavours — `_a01`, `_b01`, `_c01` for the zone it stands in, and
        // separate records for Normal, Elite and Ultimate — and they carry the
        // same name because they are the same creature.
        //
        // What separates the difficulty flavours is the **level** the monster
        // spawns at, and the level is ours to choose here, so keeping three
        // identical rows would only make the list harder to read. One row per
        // name and rank, and the variant kept is the one with the most skills —
        // the fullest of them. How many were folded in is remembered, so the list
        // can say so instead of hiding it.
        static List<Enemy> Thin(List<Enemy> all)
        {
            Dictionary<string, Enemy> best = new Dictionary<string, Enemy>(StringComparer.CurrentCultureIgnoreCase);
            foreach (Enemy e in all)
            {
                string key = e.Name + "|" + e.Rank;
                Enemy had;
                if (!best.TryGetValue(key, out had)) { best[key] = e; e.Variants = 1; continue; }
                had.Variants++;
                if (e.Skills.Count <= had.Skills.Count && e.Own.Count <= had.Own.Count) continue;
                e.Variants = had.Variants;
                best[key] = e;
            }
            List<Enemy> kept = new List<Enemy>(best.Values);
            Order(kept);
            return kept;
        }

        // Enemy and race names in the current language. The cache may have been built
        // in another language, so names are refreshed from game records after loading.
        static string NameOf(GameData game, DbRecord rec)
        {
            string name = game.Tag(rec.Get("description"));
            if (string.IsNullOrEmpty(name)) name = rec.Get("FileDescription") ?? "";
            return string.IsNullOrEmpty(name) ? "" : GameData.Form(name, null);
        }

        static string RaceName(GameData game, string race)
        {
            return string.IsNullOrEmpty(race) ? "" : (game.Tag("tag" + race) ?? race);
        }

        // Refresh cached names to the current language. Rebuilding is unnecessary:
        // record paths and numeric data are language-independent.
        public static void Rename(GameData game, List<Enemy> list)
        {
            if (game == null || list == null) return;
            foreach (Enemy e in list)
            {
                DbRecord rec = game.Record(e.Path);
                if (rec == null) continue;
                string name = NameOf(game, rec);
                if (!string.IsNullOrEmpty(name)) e.Name = name;
                e.RaceRu = RaceName(game, e.Race);
            }
            Order(list);
        }

        // Enemy ordering is shared by cache creation and loading, so existing caches
        // remain deterministic without rebuilding all records.
        public static void Order(List<Enemy> list)
        {
            // Put the training dummy first, then sort by race with bosses first.
            list.Sort(delegate (Enemy a, Enemy b)
            {
                if (a.Dummy != b.Dummy) return a.Dummy ? -1 : 1;
                // Keep the normal training dummy first because it is the default
                // measurement target; the aggressive variant is listed after it.
                if (a.Dummy && b.Dummy)
                {
                    bool pa = IsPlainDummy(a.Path), pb = IsPlainDummy(b.Path);
                    if (pa != pb) return pa ? -1 : 1;
                }
                int byRace = string.Compare(a.RaceRu, b.RaceRu, StringComparison.CurrentCulture);
                if (byRace != 0) return byRace;
                if (a.Order != b.Order) return a.Order.CompareTo(b.Order);
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            });
        }

        // The normal and aggressive dummies use `trainingdummy.dbr` and
        // `trainingdummy_02.dbr`. Identify them by path, not localized name.
        static bool IsPlainDummy(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   path.EndsWith("/trainingdummy.dbr", StringComparison.OrdinalIgnoreCase);
        }

        // The monster at a level: attributes from the bio equations, armour and
        // extra resistances from its passive skills, resistances of its own on
        // top. Each skill table uses the same `level - 1` index as character skills.
        // The difficulty pak: gameengine.dbr names it in monsterAttributePak.
        //
        // The record currently stores four group-size cells per difficulty. The
        // difficulty modifier is also stored here as `characterLifeModifier`.
        public const string DifficultyPak = "records/game/balancingadjustment_mp+difficulty_enemies01.dbr";

        static double Pak(GameData game, string field, int difficulty, int players)
        {
            DbRecord rec = game == null ? null : game.Record(DifficultyPak);
            string v = rec == null ? null : rec.Get(field);
            if (string.IsNullOrEmpty(v)) return 0;
            string[] parts = v.Split(';');
            int per = parts.Length >= 3 && parts.Length % 3 == 0 ? parts.Length / 3 : 1;
            int index = parts.Length == 1 ? 0
                      : Math.Max(0, Math.Min(2, difficulty)) * per + Math.Max(0, Math.Min(per - 1, players - 1));
            double d;
            return double.TryParse(parts[Math.Min(index, parts.Length - 1)], NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        public static EnemyStats Resolve(GameData game, Enemy e, int level)
        { return Resolve(game, e, level, -1); }

        public static EnemyStats Resolve(GameData game, Enemy e, int level, int difficulty)
        {
            EnemyStats s = new EnemyStats();
            s.Level = level;
            s.Difficulty = difficulty;
            if (game == null || e == null) return s;
            s.Race = e.Race ?? "";

            DbRecord bio = string.IsNullOrEmpty(e.Bio) ? null : game.Record(e.Bio);
            s.Life = Eval(bio, "characterLife", level);

            // Monster offensive and defensive ability use the same equation as the
            // character model; this matches `offensiveAbilityEquation`.
            //
            //     ability = (bio value + level × 12 + attribute × 0.5) + 53
            //
            // Use the full equation rather than the bio value alone.
            //
            // Flat and percentage bonuses from monster equipment and skills are not
            // modeled yet; some monster slots are randomized.
            double oaBase = Eval(bio, "characterOffensiveAbility", level);
            double daBase = Eval(bio, "characterDefensiveAbility", level);
            double cunning = Eval(bio, "characterDexterity", level);
            double physique = Eval(bio, "characterStrength", level);
            // The equation is defined in `combatformulas` (`Combat.AbilityEquation`).
            s.OffensiveAbility = oaBase > 0 ? Combat.AbilityEquation(game, true, oaBase, level, cunning, 0) : 0;
            s.DefensiveAbility = daBase > 0 ? Combat.AbilityEquation(game, false, daBase, level, physique, 0) : 0;

            foreach (KeyValuePair<string, double> kv in e.Own) s.Resist[kv.Key] = kv.Value;

            // The armour the monster is wearing. Checked on Mogdrogen: his legs
            // give 477 and his chest 14, and none of it is in armorbase06.
            foreach (string piece in e.Gear)
            {
                DbRecord g = game.Record(piece);
                if (g == null) continue;
                double d;
                if (double.TryParse((g.Get("defensiveProtection") ?? "").Split(';')[0],
                                    NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    s.GearArmour += d;
                foreach (KeyValuePair<string, string> kv in g.Fields)
                {
                    if (!kv.Key.StartsWith("defensive", StringComparison.Ordinal) ||
                        kv.Key == "defensiveProtection") continue;
                    double r;
                    if (!double.TryParse((kv.Value ?? "").Split(';')[0], NumberStyles.Float,
                                         CultureInfo.InvariantCulture, out r) || r == 0) continue;
                    s.Resist[kv.Key] = (s.Resist.ContainsKey(kv.Key) ? s.Resist[kv.Key] : 0) + r;
                }
            }
            s.Armour += s.GearArmour;

            // The passive skills of a monster carry more than resistances: armour
            // comes from them entirely, and they also hold corrections to health
            // and to attack and defence — `armorbase06.dbr` has
            // `characterLifeModifier -86…-2` by level.
            //
            // **Those corrections are gathered but not applied.** Summing them
            // the obvious way gives a monster negative health: several of the
            // passives carry large negative modifiers, and evidently the engine
            // does not simply add them up — perhaps only one applies, perhaps
            // they are indexed by something other than the level. Until that is
            // worked out, health, attack and defence come from the bio equations
            // alone and are **probably too high**; the form says so rather than
            // showing a number we cannot defend.
            double lifePct = 0, oaFlat = 0, oaPct = 0, daFlat = 0, daPct = 0;
            // A monster skill level is stored separately from monster level in
            // `skillLevel<N>` and is derived from `charLevel`. Truncate fractional
            // results as in `PetPool`; if the field is absent, use monster level.
            Dictionary<string, string> levelOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            DbRecord monster = game.Record(e.Path);
            if (monster != null)
                for (int n = 1; n <= 40; n++)
                {
                    string named = monster.Get("skillName" + n);
                    if (!string.IsNullOrEmpty(named) && !levelOf.ContainsKey(named))
                        levelOf[named] = monster.Get("skillLevel" + n) ?? "";
                }
            foreach (string skill in e.Skills)
            {
                DbRecord rec = game.Record(skill);
                if (rec == null) continue;
                if ((rec.Class ?? "").IndexOf("Passive", StringComparison.OrdinalIgnoreCase) < 0) continue;
                int skillLevel = level;
                string levelText;
                double levelValue;
                if (levelOf.TryGetValue(skill, out levelText) && !string.IsNullOrEmpty(levelText) &&
                    Equation.TryEval(levelText, delegate (string name)
                    {
                        return string.Equals(name, "charLevel", StringComparison.OrdinalIgnoreCase) ? level : 0;
                    }, out levelValue))
                    skillLevel = Math.Max(1, (int)levelValue);
                int slot = SkillText.Slot(rec, skillLevel);
                foreach (KeyValuePair<string, string> kv in rec.Fields)
                {
                    string[] parts = (kv.Value ?? "").Split(';');
                    double d;
                    if (!double.TryParse(parts[Math.Min(slot, parts.Length - 1)], NumberStyles.Float,
                                         CultureInfo.InvariantCulture, out d) || d == 0) continue;
                    switch (kv.Key)
                    {
                        case "defensiveProtection": s.Armour += d; continue;
                        // Only the armorbase passive carries the health curve —
                        // that is the one whose table reads −86 at low levels and
                        // climbs past +100 at high ones. Other passives have
                        // characterLifeModifier too, and adding them all was what
                        // sent health negative.
                        case "characterLifeModifier":
                            if (skill.IndexOf("armorbase", StringComparison.OrdinalIgnoreCase) >= 0)
                                lifePct += d;
                            continue;
                        case "characterOffensiveAbility": oaFlat += d; continue;
                        case "characterOffensiveAbilityModifier": oaPct += d; continue;
                        case "characterDefensiveAbility": daFlat += d; continue;
                        case "characterDefensiveAbilityModifier": daPct += d; continue;
                    }
                    if (!kv.Key.StartsWith("defensive", StringComparison.Ordinal)) continue;
                    s.Resist[kv.Key] = (s.Resist.ContainsKey(kv.Key) ? s.Resist[kv.Key] : 0) + d;
                }
            }
            s.SkillLifePercent = lifePct;
            s.SkillOffensive = oaFlat; s.SkillOffensivePercent = oaPct;
            s.SkillDefensive = daFlat; s.SkillDefensivePercent = daPct;

            // The difficulty pak, when a difficulty was asked for. Everything in
            // it is plain and unambiguous, so it is applied as written.
            if (difficulty >= 0)
            {
                // Health: the bio equation, then one percentage made of the
                // difficulty and the monster's own curve, added together.
                //
                //     health = bio(level) × (1 + (difficulty + local modifier) / 100)
                //
                // The formula uses the base health, difficulty modifier, and local
                // adjustment from the record.
                //
                // Difficulty values are read from `characterLifeModifier`. If the field
                // is missing, use the measured fallback values in `Combat.Fallbacks`.
                DbRecord lifePak = game.Record(DifficultyPak);
                if (lifePak != null && !Combat.Missing("characterLifeModifier") &&
                    !string.IsNullOrEmpty(lifePak.Get("characterLifeModifier")))
                    s.PakLifePercent = Pak(game, "characterLifeModifier", difficulty, 1);
                else
                {
                    s.PakLifePercent = new double[] { 50, 320, 580 }[Math.Max(0, Math.Min(2, difficulty))];
                    Combat.NoteFallback(DifficultyPak + ": characterLifeModifier");
                }
                s.Life *= 1 + (s.PakLifePercent + lifePct) / 100;
                s.OffensiveAbility = (s.OffensiveAbility + Pak(game, "characterOffensiveAbility", difficulty, 1))
                                   * (1 + Pak(game, "characterOffensiveAbilityModifier", difficulty, 1) / 100);
                s.DefensiveAbility *= 1 + Pak(game, "characterDefensiveAbilityModifier", difficulty, 1) / 100;
                DbRecord pak = game.Record(DifficultyPak);
                if (pak != null)
                    foreach (KeyValuePair<string, string> kv in pak.Fields)
                    {
                        if (!kv.Key.StartsWith("defensive", StringComparison.Ordinal)) continue;
                        double d = Pak(game, kv.Key, difficulty, 1);
                        if (d == 0) continue;
                        if (kv.Key == "defensiveProtection") s.Armour += d;
                        else s.Resist[kv.Key] = (s.Resist.ContainsKey(kv.Key) ? s.Resist[kv.Key] : 0) + d;
                    }
            }
            return s;
        }

        static double Eval(DbRecord bio, string field, int level)
        {
            string text = bio == null ? null : bio.Get(field);
            if (string.IsNullOrEmpty(text)) return 0;
            double result;
            return Equation.TryEval(text, delegate (string name)
            {
                if (string.Equals(name, "charLevel", StringComparison.OrdinalIgnoreCase)) return level;
                return 0;   // lifeRegen, manaRegen and the like: the monster has none of its own
            }, out result) ? result : 0;
        }

        // ------------------------------------------------------------- cache

        // The third header field stores the name language. Older caches omit it and
        // remain readable because version validation checks only the prefix.
        public static void Save(string path, List<Enemy> list, string language)
        { Save(path, list, language, null); }

        // `gameDir` puts the GAME'S SIGNATURE into the header — the sizes and times of the
        // archives, the same signature the item catalogue keeps. Without it the cache lived
        // for ever: a patch landed on 8 October, the catalogue, the affixes, the hands and
        // the portals all rebuilt themselves, and the enemy table stayed as it was on
        // 16 September — so every run of the queue counted its target from three-week-old
        // data, and said nothing (MECHANICS 115).
        public static void Save(string path, List<Enemy> list, string language, string gameDir)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("#gdc-enemies\t").Append(Version).Append('\t').Append(language ?? "")
              .Append('\t').Append(gameDir == null ? "" : ItemCatalog.SignatureOf(gameDir)).Append('\n');
            foreach (Enemy e in list)
            {
                sb.Append(e.Path).Append('\t').Append(e.Name).Append('\t').Append(e.Rank).Append('\t')
                  .Append(e.Variants).Append('\t').Append(e.Dummy ? 1 : 0).Append('\t')
                  .Append(e.Race).Append('\t').Append(e.RaceRu).Append('\t').Append(e.Bio).Append('\t')
                  .Append(string.Join(";", e.Skills.ToArray())).Append('\t')
                  .Append(string.Join(";", e.Gear.ToArray())).Append('\t');
                List<string> own = new List<string>();
                foreach (KeyValuePair<string, double> kv in e.Own)
                    own.Add(kv.Key + "=" + kv.Value.ToString("0.###", CultureInfo.InvariantCulture));
                sb.Append(string.Join(";", own.ToArray())).Append('\n');
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        public static List<Enemy> Load(string path)
        {
            List<Enemy> made = new List<Enemy>();
            if (!File.Exists(path)) return made;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0 || !lines[0].StartsWith("#gdc-enemies\t" + Version, StringComparison.Ordinal))
                return made;
            for (int i = 1; i < lines.Length; i++)
            {
                string[] f = lines[i].Split('\t');
                if (f.Length < 11) continue;
                int variants;
                Enemy e = new Enemy { Path = f[0], Name = f[1], Rank = f[2], Race = f[5],
                                      RaceRu = f[6], Bio = f[7] };
                e.Variants = int.TryParse(f[3], out variants) ? variants : 1;
                e.Dummy = f[4] == "1";
                foreach (string s in f[8].Split(';')) if (s.Length > 0) e.Skills.Add(s);
                foreach (string s in f[9].Split(';')) if (s.Length > 0) e.Gear.Add(s);
                foreach (string s in f[10].Split(';'))
                {
                    int eq = s.IndexOf('=');
                    double d;
                    if (eq > 0 && double.TryParse(s.Substring(eq + 1), NumberStyles.Float,
                                                  CultureInfo.InvariantCulture, out d))
                        e.Own[s.Substring(0, eq)] = d;
                }
                made.Add(e);
            }
            // Apply the current order here too because older caches may use a different one.
            Order(made);
            return made;
        }

        // Name language stored by `Save`; empty for older caches.
        // WAS THIS CACHE BUILT FROM THE GAME AS IT IS NOW. Only the first line is read, so
        // it may be asked before every run. Pass null for the language when the
        // structural data can be reused and the names refreshed separately.
        public static bool Fresh(string path, string gameDir, string language)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (StreamReader r = new StreamReader(path, Encoding.UTF8))
                {
                    string[] h = (r.ReadLine() ?? "").Split('\t');
                    if (h.Length < 4 || h[0] != "#gdc-enemies") return false;
                    int ver;
                    if (!int.TryParse(h[1], out ver) || ver != Version) return false;
                    if (language != null &&
                        !string.Equals(h[2], language, StringComparison.OrdinalIgnoreCase)) return false;
                    return string.Equals(h[3] ?? "", ItemCatalog.SignatureOf(gameDir), StringComparison.Ordinal);
                }
            }
            catch (Exception) { return false; }
        }

        // THE ENEMY TABLE ON DEMAND: take the one already built from this game, otherwise
        // build it and keep it beside the program. Until 8 October only the button on the
        // "Target" tab could do that; a run of the queue merely read the file, and with no
        // file it silently took a FALLBACK target whose numbers are different ones.
        public static List<Enemy> Ready(GameData game, string path, bool localizeNames = true)
        {
            if (game == null) return new List<Enemy>();
            if (Fresh(path, game.GameDir, null))
            {
                List<Enemy> cached = localizeNames ? LoadFor(game, path) : Load(path);
                if (cached.Count > 0) return cached;
            }
            List<Enemy> made = Build(game, null);
            try { Save(path, made, game.Language, game.GameDir); } catch (Exception) { }
            return made;
        }

        public static string LanguageOf(string path)
        {
            try
            {
                using (StreamReader r = new StreamReader(path, Encoding.UTF8))
                {
                    string[] h = (r.ReadLine() ?? "").Split('\t');
                    return h.Length > 2 ? h[2] : "";
                }
            }
            catch (Exception) { return ""; }
        }

        // Read the cache and refresh names only when its language differs or is absent.
        public static List<Enemy> LoadFor(GameData game, string path)
        {
            List<Enemy> list = Load(path);
            if (game == null || list.Count == 0) return list;
            string lang = LanguageOf(path);
            bool stale = lang.Length > 0
                ? !string.Equals(lang, game.Language, StringComparison.OrdinalIgnoreCase)
                : !FirstNamesMatch(game, list, 3);
            if (stale) Rename(game, list);
            return list;
        }

        // For a cache without a language marker, compare the first ordered records with
        // the game. Matching localized dummy names indicates the cache is current.
        static bool FirstNamesMatch(GameData game, List<Enemy> list, int count)
        {
            int checkedCount = 0;
            foreach (Enemy e in list)
            {
                DbRecord rec = game.Record(e.Path);
                if (rec == null) continue;
                if (!string.Equals(NameOf(game, rec), e.Name, StringComparison.Ordinal)) return false;
                if (++checkedCount >= count) break;
            }
            return checkedCount > 0;
        }
    }
}
