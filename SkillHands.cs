// Skill hand usage comes from the game's animation data.
// A skill record names an animation; the gear-set table maps that name to an
// `.anm` file, whose textual hit events identify the hands used.
// `weapon1[0]` is the main hand and `weapon1[1]` the off hand. Without a skill
// animation reference, the default attack is used.
// Build the table once for all gear sets. Each animation file is read once and
// archives are opened only while the table is being built.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gdc.Gui
{
    // Animation set used to resolve a file. Names are local keys; adjacent strings
    // are prefixes from the game table.
    public enum WeaponSet
    {
        Unarmed, OneHand, DualWield, Sword2h, Axe2h, Mace2h, Spear2h,
        Staff, Spear, Ranged1h, Ranged2h, DualRanged
    }

    public enum Hands { Both, Main, Off }

    public static class SkillHands
    {
        // Key: `set-prefix\animation`; value: hand usage.
        static Dictionary<string, Hands> table;
        static string builtFor;

        // Animation duration at cast speed 1. Uses the table key plus one fallback
        // key per set: `set-prefix\#spell`.
        //
        // `.anm` headers contain bone count, frame count, and frame rate. Duration
        // is frames / rate / the table animation speed.
        static Dictionary<string, double> secs;

        // Cache version with the duration column; older caches invalidate cleanly.
        const string CacheVersion = "|animation-seconds-1";

        static readonly string[] SetPrefix =
        {
            "unarmed", "sHanded", "dHanded", "sword2h", "axe2h", "mace2h", "spear2h",
            "staff", "spear", "ranged1h", "ranged2h", "dualRanged"
        };

        public static string PrefixOf(WeaponSet set) { return SetPrefix[(int)set]; }

        // Number of distinct animation files read, used by `--handtest`.
        public static int FilesRead;

        // Cache location and validity signature.
        //
        // Use the same database signature as `ItemCatalog.SignatureOf`, plus the
        // creature archives that contain the animation files.
        public static string CachePath
        {
            get { return CacheFile.Of("hands"); }
        }

        static readonly string[] Archives = {
            @"resources\Creatures.arc", @"gdx1\resources\Creatures.arc",
            @"gdx2\resources\Creatures.arc", @"gdx3\resources\Creatures.arc" };

        public static string SignatureOf(string gameDir)
        {
            StringBuilder sb = new StringBuilder(ItemCatalog.SignatureOf(gameDir));
            foreach (string rel in Archives)
            {
                string p = Path.Combine(gameDir ?? "", rel);
                if (!File.Exists(p)) continue;
                FileInfo fi = new FileInfo(p);
                sb.Append(rel).Append(':').Append(fi.Length).Append(':')
                  .Append(fi.LastWriteTimeUtc.Ticks).Append(';');
            }
            return sb.ToString();
        }

        // Source of the current table, for diagnostics.
        public static bool CameFromCache;

        public static void Build(GameData game)
        {
            if (game == null) return;
            if (table != null && builtFor == game.GameDir) return;
            string signature = SignatureOf(game.GameDir) + CacheVersion;
            Dictionary<string, double> cachedSecs;
            Dictionary<string, Hands> cached = LoadCache(signature, out cachedSecs);
            if (cached != null)
            {
                table = cached;
                secs = cachedSecs;
                builtFor = game.GameDir;
                CameFromCache = true;
                FilesRead = 0;
                return;
            }
            int files;
            // Use the male model; `ModelsDiffer` separately verifies that gender
            // does not change hand assignment.
            Dictionary<string, double> built;
            table = BuildFrom(game, "records/creatures/pc/anm_malepc.dbr", out files, out built);
            secs = built;
            FilesRead = files;
            builtFor = game.GameDir;
            CameFromCache = false;
            SaveCache(signature);
        }

        static Dictionary<string, Hands> LoadCache(string signature, out Dictionary<string, double> seconds)
        {
            seconds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(CachePath)) return null;
                string[] rows = File.ReadAllLines(CachePath, Encoding.UTF8);
                if (rows.Length < 1 || rows[0] != signature) return null;
                Dictionary<string, Hands> made =
                    new Dictionary<string, Hands>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < rows.Length; i++)
                {
                    string[] cell = rows[i].Split('\t');
                    if (cell.Length < 2) continue;
                    double sec;
                    if (cell.Length >= 3 && double.TryParse(cell[2], System.Globalization.NumberStyles.Float,
                                                            System.Globalization.CultureInfo.InvariantCulture, out sec) &&
                        sec > 0)
                        seconds[cell[0]] = sec;
                    // The spell fallback row carries duration only, not hand data.
                    if (cell[1] == "-") continue;
                    made[cell[0]] = cell[1] == "Main" ? Hands.Main
                                  : cell[1] == "Off" ? Hands.Off : Hands.Both;
                }
                // An empty file is an invalid cache, not an empty animation set.
                return made.Count > 0 ? made : null;
            }
            catch (Exception) { return null; }
        }

        static void SaveCache(string signature)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(signature);
                foreach (KeyValuePair<string, Hands> kv in table)
                {
                    double sec;
                    sb.Append(kv.Key).Append('\t').Append(kv.Value.ToString());
                    if (secs != null && secs.TryGetValue(kv.Key, out sec))
                        sb.Append('\t').Append(sec.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    sb.AppendLine();
                }
                // Spell fallbacks use separate rows without hand data.
                if (secs != null)
                    foreach (KeyValuePair<string, double> kv in secs)
                        if (kv.Key.EndsWith("\\#spell", StringComparison.Ordinal))
                            sb.Append(kv.Key).Append("\t-\t")
                              .Append(kv.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                              .AppendLine();
                File.WriteAllText(CachePath, sb.ToString(), new UTF8Encoding(false));
            }
            // A read-only install is valid; rebuild without writing a cache.
            catch (Exception) { }
        }

        static Dictionary<string, Hands> BuildFrom(GameData game, string model, out int filesRead)
        {
            Dictionary<string, double> unused;
            return BuildFrom(game, model, out filesRead, out unused);
        }

        static Dictionary<string, Hands> BuildFrom(GameData game, string model, out int filesRead,
                                                   out Dictionary<string, double> seconds)
        {
            Dictionary<string, Hands> made = new Dictionary<string, Hands>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Hands> byFile = new Dictionary<string, Hands>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> fileSecs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            seconds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            filesRead = 0;

            List<ArcIndex> creatures = new List<ArcIndex>();
            string[] arcs = { @"resources\Creatures.arc", @"gdx1\resources\Creatures.arc",
                              @"gdx2\resources\Creatures.arc", @"gdx3\resources\Creatures.arc" };
            foreach (string rel in arcs)
            {
                string p = Path.Combine(game.GameDir, rel);
                if (File.Exists(p)) { try { creatures.Add(new ArcIndex(p)); } catch (Exception) { } }
            }
            try
            {
                DbRecord anm = game.Record(model);
                if (anm != null)
                    foreach (KeyValuePair<string, string> kv in anm.Fields)
                    {
                        int cut = kv.Key.IndexOf("SpecialAnimRef", StringComparison.Ordinal);
                        if (cut <= 0 || string.IsNullOrEmpty(kv.Value)) continue;
                        string prefix = kv.Key.Substring(0, cut);
                        string number = kv.Key.Substring(cut + "SpecialAnimRef".Length);
                        string file = anm.Get(prefix + "SpecialAnim" + number);
                        if (string.IsNullOrEmpty(file)) continue;

                        Hands hands;
                        if (!byFile.TryGetValue(file, out hands))
                        {
                            double len;
                            hands = Read(creatures, file, out len);
                            byFile[file] = hands;
                            fileSecs[file] = len;
                        }
                        made[prefix + "\\" + kv.Value] = hands;
                        double fl;
                        if (fileSecs.TryGetValue(file, out fl) && fl > 0)
                            seconds[prefix + "\\" + kv.Value] =
                                fl / SpeedOf(anm, prefix + "SpecialAnimSpeed" + number);
                    }
                // Each set has a spell fallback when no skill animation exists.
                // Its duration is the weighted average of the fallback variants.
                if (anm != null)
                    foreach (string prefix in SetPrefix)
                    {
                        double sum = 0, weights = 0;
                        foreach (string n in new[] { "", "2", "3" })
                        {
                            string file = anm.Get(prefix + "SpellAttackAnim" + n);
                            if (string.IsNullOrEmpty(file)) continue;
                            double len;
                            if (!fileSecs.TryGetValue(file, out len))
                            {
                                Read(creatures, file, out len);
                                fileSecs[file] = len;
                            }
                            if (len <= 0) continue;
                            double w = SpeedOf(anm, prefix + "SpellAttackAnimWeight" + n);
                            sum += w * len / SpeedOf(anm, prefix + "SpellAttackAnimSpeed" + n);
                            weights += w;
                        }
                        if (weights > 0) seconds[prefix + "\\#spell"] = sum / weights;
                    }
            }
            finally { foreach (ArcIndex a in creatures) { try { a.Dispose(); } catch (Exception) { } } }
            filesRead = byFile.Count;
            return made;
        }

        // Count gear-set/animation pairs whose hand assignment differs between
        // player models. Compare hands, not reference indices.
        public static int ModelsDiffer(GameData game)
        {
            int a, b;
            Dictionary<string, Hands> his = BuildFrom(game, "records/creatures/pc/anm_malepc.dbr", out a);
            Dictionary<string, Hands> hers = BuildFrom(game, "records/creatures/pc/anm_femalepc.dbr", out b);
            int differ = 0;
            foreach (KeyValuePair<string, Hands> kv in his)
            {
                Hands mine;
                // A missing animation reference is not a hand mismatch; both models
                // use the default attack.
                if (!hers.TryGetValue(kv.Key, out mine)) continue;
                if (mine != kv.Value) differ++;
            }
            return differ;
        }

        // Hand fractions for the default dual-wield attack. Read hit events rather
        // than file names, then apply the animation weights.
        //
        // If table data, weights, or hand events are incomplete, use the 50/25/25
        // fallback and expose it through `Combat.Fallbacks`.
        public sealed class DualSplit
        {
            public double Both, Main, Off;
            // Fraction of attacks using this hand: two-handed attacks plus attacks
            // using the hand alone. The panel uses the same fraction for both hands.
            public double PerHand { get { return Both + (Main + Off) / 2; } }
        }

        static readonly Dictionary<string, DualSplit> dualSplits =
            new Dictionary<string, DualSplit>(StringComparer.Ordinal);

        public static DualSplit DualSwing(GameData game, bool ranged)
        {
            string prefix = ranged ? "dualRanged" : "dHanded";
            DualSplit got;
            lock (dualSplits) if (dualSplits.TryGetValue(prefix, out got)) return got;
            got = ReadDualSwing(game, prefix);
            if (got == null)
            {
                got = new DualSplit { Both = 0.5, Main = 0.25, Off = 0.25 };
                if (game != null)
                    Combat.NoteFallback("records/creatures/pc/anm_malepc.dbr: " + prefix +
                                        "AttackAnim/AttackAnimWeight (hand shares 50/25/25)");
            }
            if (game != null) lock (dualSplits) dualSplits[prefix] = got;
            return got;
        }

        // Raw weights; null means the table could not be read. Exposed for
        // `--handtest` to verify fallback behavior.
        public static DualSplit ReadDualSwing(GameData game, string prefix)
        {
            if (game == null) return null;
            DbRecord anm = game.Record("records/creatures/pc/anm_malepc.dbr");
            if (anm == null) return null;
            List<ArcIndex> creatures = Creatures(game);
            try
            {
                double[] weight = new double[3];
                int[] seen = new int[3];
                for (int n = 1; ; n++)
                {
                    string file = anm.Get(prefix + "AttackAnim" + n);
                    if (string.IsNullOrEmpty(file)) break;
                    string ws = Combat.Missing(prefix + "AttackAnimWeight" + n) ? null
                              : anm.Get(prefix + "AttackAnimWeight" + n);
                    double w;
                    if (string.IsNullOrEmpty(ws) ||
                        !double.TryParse(ws.Split(';')[0], System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture, out w) || w <= 0)
                        return null;
                    Hands h = Read(creatures, file);
                    weight[(int)h] += w;
                    seen[(int)h]++;
                }
                if (seen[(int)Hands.Both] == 0 || seen[(int)Hands.Main] == 0 || seen[(int)Hands.Off] == 0)
                    return null;
                double sum = weight[0] + weight[1] + weight[2];
                return new DualSplit
                {
                    Both = weight[(int)Hands.Both] / sum,
                    Main = weight[(int)Hands.Main] / sum,
                    Off = weight[(int)Hands.Off] / sum
                };
            }
            finally { foreach (ArcIndex a in creatures) { try { a.Dispose(); } catch (Exception) { } } }
        }

        // Duration of the default attack for a creature using this animation table.
        //
        // Used for pet attack speed; duration is derived from animation frames and
        // the table's attack speed in the same way as player attack speed.
        //
        // Player and pet tables use different prefixes, so select the prefix with
        // the largest set of numbered `*AttackAnim<n>` fields.
        public static double CreatureAttackSeconds(GameData game, string tableDbr)
        {
            if (game == null || string.IsNullOrEmpty(tableDbr)) return 0;
            DbRecord anm = game.Record(tableDbr);
            if (anm == null) return 0;
            List<ArcIndex> creatures = Creatures(game);
            if (creatures.Count == 0) return 0;
            double sumW = 0, sumWT = 0;
            foreach (KeyValuePair<string, string> kv in anm.Fields)
            {
                int cut = kv.Key.IndexOf("AttackAnim", StringComparison.Ordinal);
                if (cut <= 0 || string.IsNullOrEmpty(kv.Value)) continue;
                string tail = kv.Key.Substring(cut + "AttackAnim".Length);
                // Only numbered attack animations such as `...AttackAnim1`; speed,
                // weight, and idle fields are handled separately.
                int number;
                if (!int.TryParse(tail, out number)) continue;
                string prefix = kv.Key.Substring(0, cut);
                double len;
                Read(creatures, kv.Value, out len);
                if (len <= 0) continue;
                double speed = SpeedOf(anm, prefix + "AttackAnimSpeed" + number);
                double weight = SpeedOf(anm, prefix + "AttackAnimWeight" + number);
                if (weight <= 0) weight = 100;
                sumW += weight;
                sumWT += weight * len / (speed > 0 ? speed : 1);
            }
            return sumW > 0 ? sumWT / sumW : 0;
        }

        // Skill duration for a creature at speed 1.
        //
        // Resolve `skillSpecialAnimationName` through the table's
        // `...SpecialAnimRef<n>` / `...SpecialAnim<n>` pairs and read the matching
        // speed. Skills without a named animation use the `...SpellAttackAnim`
        // fallback. Return zero when no animation is found.
        public static double CreatureSkillSeconds(GameData game, string tableDbr, string animName)
        {
            if (game == null || string.IsNullOrEmpty(tableDbr)) return 0;
            DbRecord anm = game.Record(tableDbr);
            if (anm == null) return 0;
            List<ArcIndex> creatures = Creatures(game);
            if (creatures.Count == 0) return 0;
            if (!string.IsNullOrEmpty(animName))
                foreach (KeyValuePair<string, string> kv in anm.Fields)
                {
                    int cut = kv.Key.IndexOf("SpecialAnimRef", StringComparison.Ordinal);
                    if (cut <= 0 || !string.Equals(kv.Value, animName, StringComparison.OrdinalIgnoreCase)) continue;
                    string prefix = kv.Key.Substring(0, cut);
                    string number = kv.Key.Substring(cut + "SpecialAnimRef".Length);
                    string file = anm.Get(prefix + "SpecialAnim" + number);
                    if (string.IsNullOrEmpty(file)) continue;
                    double len;
                    Read(creatures, file, out len);
                    if (len > 0) return len / SpeedOf(anm, prefix + "SpecialAnimSpeed" + number);
                }
            double sum = 0, weights = 0;
            foreach (KeyValuePair<string, string> kv in anm.Fields)
            {
                int cut = kv.Key.IndexOf("SpellAttackAnim", StringComparison.Ordinal);
                if (cut <= 0 || string.IsNullOrEmpty(kv.Value)) continue;
                string tail = kv.Key.Substring(cut + "SpellAttackAnim".Length);
                if (tail != "" && tail != "2" && tail != "3") continue;
                string prefix = kv.Key.Substring(0, cut);
                double len;
                Read(creatures, kv.Value, out len);
                if (len <= 0) continue;
                double w = SpeedOf(anm, prefix + "SpellAttackAnimWeight" + tail);
                sum += w * len / SpeedOf(anm, prefix + "SpellAttackAnimSpeed" + tail);
                weights += w;
            }
            return weights > 0 ? sum / weights : 0;
        }

        // Creature archives containing the animation files.
        static List<ArcIndex> Creatures(GameData game)
        {
            List<ArcIndex> creatures = new List<ArcIndex>();
            string[] arcs = { @"resources\Creatures.arc", @"gdx1\resources\Creatures.arc",
                              @"gdx2\resources\Creatures.arc", @"gdx3\resources\Creatures.arc" };
            foreach (string rel in arcs)
            {
                string p = Path.Combine(game.GameDir, rel);
                if (File.Exists(p)) { try { creatures.Add(new ArcIndex(p)); } catch (Exception) { } }
            }
            return creatures;
        }

        // Read hand events from an animation file. Other data contains bone tracks.
        static Hands Read(List<ArcIndex> creatures, string anmPath)
        {
            double unused;
            return Read(creatures, anmPath, out unused);
        }

        // Also return duration in seconds (frames / rate); return zero for an invalid
        // header. The header contains `ANM`, a version byte, and three integer counts.
        static Hands Read(List<ArcIndex> creatures, string anmPath, out double seconds)
        {
            seconds = 0;
            string entry = (anmPath ?? "").Replace('\\', '/');
            if (entry.StartsWith("creatures/", StringComparison.OrdinalIgnoreCase))
                entry = entry.Substring("creatures/".Length);
            byte[] data = null;
            for (int i = creatures.Count - 1; i >= 0 && data == null; i--)
                try { data = creatures[i].Get(entry); } catch (Exception) { }
            // A missing file provides no hand data; preserve the default behavior.
            if (data == null) return Hands.Both;
            if (data.Length >= 16 && data[0] == 0x41 && data[1] == 0x4E && data[2] == 0x4D)
            {
                int frames = BitConverter.ToInt32(data, 8);
                int fps = BitConverter.ToInt32(data, 12);
                if (frames > 0 && fps > 0 && fps < 1000) seconds = (double)frames / fps;
            }
            string text = Encoding.ASCII.GetString(data);
            bool left = text.IndexOf("\"LeftHandHit\"", StringComparison.Ordinal) >= 0;
            bool right = text.IndexOf("\"RightHandHit\"", StringComparison.Ordinal) >= 0;
            if (left && right) return Hands.Both;
            if (right) return Hands.Main;
            if (left) return Hands.Off;
            return Hands.Both;
        }

        // Return whether animation data determines hand usage. Two class families
        // use explicit measured rules; all other skills use animation data.
        // Do not infer the result from class-name substrings.
        public static bool ByAnimation(string recordClass)
        {
            string cls = recordClass ?? "";
            if (cls.StartsWith("Skill_AttackRadius", StringComparison.OrdinalIgnoreCase)) return false;
            if (cls.StartsWith("SkillSecondary_", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        // Whether the gear set has an off hand. Two-handed gear occupies both hands,
        // so a two-handed animation still produces one weapon row.
        static bool CanHaveOffHand(WeaponSet set)
        {
            switch (set)
            {
                case WeaponSet.Sword2h:
                case WeaponSet.Axe2h:
                case WeaponSet.Mace2h:
                case WeaponSet.Spear2h:
                case WeaponSet.Spear:
                case WeaponSet.Staff:
                case WeaponSet.Ranged2h:
                case WeaponSet.Unarmed: return false;
            }
            return true;
        }

        // Read an animation-table field; missing values default to one.
        static double SpeedOf(DbRecord anm, string field)
        {
            string v = anm == null ? null : anm.Get(field);
            double d;
            return !string.IsNullOrEmpty(v) &&
                   double.TryParse(v.Split(';')[0], System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out d) && d > 0
                 ? d : 1;
        }

        // Skill cast duration at speed 1: use the named animation or the set's
        // spell fallback. Return zero when neither is available.
        //
        // The database does not distinguish fallback animation types for buffs and
        // debuffs; their frame counts are currently equivalent for this calculation.
        public static double CastSeconds(GameData game, DbRecord skill, Character c)
        {
            if (game == null || skill == null) return 0;
            Build(game);
            if (secs == null) return 0;
            string prefix = PrefixOf(SetOf(game, c));
            string anim = skill.Get("skillSpecialAnimationName");
            double v;
            if (!string.IsNullOrEmpty(anim) && secs.TryGetValue(prefix + "\\" + anim, out v)) return v;
            return secs.TryGetValue(prefix + "\\#spell", out v) ? v : 0;
        }

        // Hands used by a skill with the current gear set.
        public static Hands Of(GameData game, DbRecord skill, WeaponSet set)
        {
            Hands hands = FromAnimation(game, skill, set);
            // This gear set has no off hand; assign the result to the main hand.
            if (hands != Hands.Main && !CanHaveOffHand(set)) return Hands.Main;
            return hands;
        }

        // For a one-handed set, the off hand exists only when occupied. A shield
        // produces a second row; a single weapon does not.
        public static Hands Of(GameData game, DbRecord skill, Character c)
        {
            WeaponSet set = SetOf(game, c);
            Hands hands = Of(game, skill, set);
            if (hands != Hands.Main && !HasOffHand(game, c)) return Hands.Main;
            // A SKILL WITH NO SWING OF ITS OWN PLAYS THE ORDINARY ATTACK — AND THE ORDINARY
            // ATTACK NEVER SWINGS A SHIELD (14d7 rule 3). So "both hands" from the fallback
            // means "both" only where both hands hold WEAPONS, that is when dual wielding.
            //
            // MEASURED ON `_RuneTask` (13 October, ten shots, `stand-runetask.tsv`), an
            // Oathkeeper with a sceptre and a shield. Two of his skills decide it between
            // them, and they differ in exactly this one thing:
            //
            //   Aegis of Menhir   has its own animation, and it says "one-handed (and with a
            //                     shield): LEFT — off-hand"; the game prints ONE weapon line
            //                     and it is the off-hand one          — we were already right
            //   Righteous Fervor  has NO animation of its own; the game prints ONE weapon
            //                     line and it is the MAIN-hand one    — we printed two
            //
            // The shield entered the pool as a second hand, the hit line got the dual-wield
            // 0.75 averaging over both, and three damage types came out exactly one and a
            // half times the game's: physical 1497 against 998, cold 161 against 107,
            // lightning 60–855 against 40–570. That is `0.75 x (hand + hand)` where the game
            // takes one hand, and 0.75 x 2 = 1.5.
            //
            // WHY THIS IS NOT THE SAME AS `CanHaveOffHand` ABOVE: that one asks whether the
            // GEAR can have a second hand at all (a two-hander cannot), this one asks whether
            // the second hand SWINGS when no animation named it. The two-hander case was
            // found the same way, on 23 August.
            //
            // AND NOT A NARROWING OF 14d7 RULE 2: a shield is still a hand for a skill whose
            // animation names it — with both hands or with it alone. `_222`'s Forcewave and
            // Aegis of Menhir go on reading as before, and `_Itaan`, who dual wields, keeps
            // two brackets for Righteous Fervor (MECHANICS 95zh).
            if (hands != Hands.Main && !HasOwnSwing(game, skill, set) && !WeaponInOffHand(game, c))
                return Hands.Main;
            return hands;
        }

        static bool HasOffHand(GameData game, Character c)
        {
            return ClassOf(game, c, 1) != null;
        }

        // Whether the off hand holds a WEAPON, as opposed to a shield or a caster's off-hand.
        // The same question `DamageModel.IsWeapon` asks, asked here of the record's class.
        static bool WeaponInOffHand(GameData game, Character c)
        {
            string cls = ClassOf(game, c, 1);
            return cls != null &&
                   cls.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase) &&
                   cls.IndexOf("Shield", StringComparison.OrdinalIgnoreCase) < 0 &&
                   cls.IndexOf("Offhand", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // Whether the skill has a swing of its own for THIS gear — the same lookup
        // `FromAnimation` makes, asked as a yes/no. A "no" here is what the dump calls
        // "no animation of its own — the ordinary attack is played".
        static bool HasOwnSwing(GameData game, DbRecord skill, WeaponSet set)
        {
            if (skill == null) return false;
            string anim = skill.Get("skillSpecialAnimationName");
            if (string.IsNullOrEmpty(anim)) return false;
            Build(game);
            if (table == null) return false;
            Hands ignored;
            return table.TryGetValue(PrefixOf(set) + "\\" + anim, out ignored);
        }

        static Hands FromAnimation(GameData game, DbRecord skill, WeaponSet set)
        {
            if (skill == null) return Hands.Both;
            string anim = skill.Get("skillSpecialAnimationName");
            // No skill animation is defined; use the default attack.
            if (string.IsNullOrEmpty(anim)) return Hands.Both;
            Build(game);
            if (table == null) return Hands.Both;
            Hands hands;
            // The gear set has no such animation; use the default attack.
            if (!table.TryGetValue(PrefixOf(set) + "\\" + anim, out hands)) return Hands.Both;
            return hands;
        }

        // Animation sets available to the player. The list follows the game table;
        // some entries are not reachable through normal equipment.
        public static WeaponSet SetOf(GameData game, Character c)
        {
            string one = ClassOf(game, c, 0), two = ClassOf(game, c, 1);
            if (one == null && two == null) return WeaponSet.Unarmed;
            // Two-handed gear occupies the first slot and both hands.
            switch (one)
            {
                case "WeaponMelee_Sword2h": return WeaponSet.Sword2h;
                case "WeaponMelee_Axe2h": return WeaponSet.Axe2h;
                case "WeaponMelee_Mace2h": return WeaponSet.Mace2h;
                case "WeaponMelee_Spear2h": return WeaponSet.Spear2h;
                case "WeaponHunting_Ranged2h": return WeaponSet.Ranged2h;
            }
            bool oneRanged = one == "WeaponHunting_Ranged1h";
            bool twoRanged = two == "WeaponHunting_Ranged1h";
            bool oneMelee = IsMelee1h(one), twoMelee = IsMelee1h(two);
            if (oneRanged && twoRanged) return WeaponSet.DualRanged;
            if (oneRanged || twoRanged) return WeaponSet.Ranged1h;
            if (oneMelee && twoMelee) return WeaponSet.DualWield;
            if (oneMelee || twoMelee) return WeaponSet.OneHand;
            // A shield or focus alone still uses a one-handed attack.
            return WeaponSet.OneHand;
        }

        static bool IsMelee1h(string cls)
        {
            switch (cls)
            {
                case "WeaponMelee_Axe":
                case "WeaponMelee_Dagger":
                case "WeaponMelee_Mace":
                case "WeaponMelee_Scepter":
                case "WeaponMelee_Sword": return true;
            }
            return false;
        }

        static string ClassOf(GameData game, Character c, int slot)
        {
            if (game == null || c == null || c.Weapons1 == null || c.Weapons1.Count <= slot) return null;
            string name = c.Weapons1[slot].BaseName;
            if (string.IsNullOrEmpty(name)) return null;
            DbRecord rec = game.Record(name);
            return rec == null ? null : rec.Class;
        }
    }
}
