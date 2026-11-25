// Reading a skill: its name, its description, and what it does at a given level.
//
// The last one needs no formula. A skill record stores each value as a list
// indexed by level — "24;40;56;72;…" — exactly the shape a set record uses for
// piece counts, so the item stat renderer draws a skill by being handed the same
// record and the right index.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Gdc.Gui
{
    public static class SkillText
    {
        public static string Name(GameData game, string dbr) { return ItemViewFactory.SkillNameOf(game, dbr); }

        // The skill button picture, following the same hand-off chain as the name.
        public static string IconOf(GameData game, string dbr)
        {
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(dbr); hop++)
            {
                DbRecord rec = game.Record(dbr);
                if (rec == null) return null;
                string tex = rec.Get("skillUpBitmapName");
                if (!string.IsNullOrEmpty(tex)) return tex;
                dbr = ItemViewFactory.NextSkillRecord(rec);
            }
            return null;
        }

        public static string Description(GameData game, string dbr) { return ItemViewFactory.SkillDescriptionOf(game, dbr); }

        // Where the level limits live, following the same hand-off chain as the
        // name and the numbers. Some tree entries are controllers whose limits live in
        // the linked buff record
        // it hands off to. Reading the controller alone drew those skills as "/ 0".
        public static DbRecord LimitsOf(GameData game, string dbr)
        {
            DbRecord first = null;
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(dbr); hop++)
            {
                DbRecord rec = game.Record(dbr);
                if (rec == null) break;
                if (first == null) first = rec;
                if (rec.GetInt("skillMaxLevel") > 0) return rec;
                dbr = ItemViewFactory.NextSkillRecord(rec);
            }
            return first;
        }

        // What the worn gear adds to skill levels. Three shapes: a bonus to one
        // named skill, a bonus to every skill of a mastery, and a bonus to
        // everything. None of it is in the save — the game adds it on load — so
        // it is gathered from the records of what the character has on.
        public class LevelBonus
        {
            public readonly Dictionary<string, int> BySkill = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> ByMastery = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public int ToEverything;

            // Map a skill granted by a form to that form. Read `grantedSkills` from
            // the character's `Skill_Shapeshift` records.
            public readonly Dictionary<string, string> FormOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // A bonus only counts for a skill that already has a point in it.
            public int For(string skillDbr, int investedLevel)
            {
                if (investedLevel <= 0) return 0;
                int n = ToEverything;
                int one;
                if (BySkill.TryGetValue(skillDbr, out one)) n += one;
                // A form skill also receives bonuses addressed to the form itself.
                string form;
                if (FormOf.TryGetValue(skillDbr, out form) && BySkill.TryGetValue(form, out one)) n += one;
                string mastery = ItemViewFactory.MasteryFolder(skillDbr);
                if (mastery != null && ByMastery.TryGetValue(mastery, out one)) n += one;
                return n;
            }

            public static LevelBonus Of(GameData game, Character c)
            {
                LevelBonus b = new LevelBonus();
                if (game == null || c == null) return b;
                List<Item> worn = new List<Item>();
                // Exclude the alternate weapon set: only the active set contributes
                // to skill-level bonuses.
                worn.AddRange(c.Equipment); worn.AddRange(c.Weapons1);
                foreach (Item it in worn)
                {
                    if (string.IsNullOrEmpty(it.BaseName)) continue;
                    // Include the item, socketed records, and relic completion bonus.
                    foreach (string dbr in new[] { it.BaseName, it.PrefixName, it.SuffixName,
                                                   it.RelicName, ItemViewFactory.RelicBonusOf(game, it),
                                                   it.AugmentName, it.ModifierName, it.AscensionName })
                        b.Add(game.Record(dbr));
                }

                // Set bonuses may also grant skill levels. Use the row indexed by
                // equipped piece count minus one.
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
                    b.Add(game.Record(kv.Key), kv.Value.Count - 1);
                // Build the form-skill map from the character's skill records.
                foreach (Skill sk in c.Skills)
                {
                    if (sk.Level <= 0 || string.IsNullOrEmpty(sk.Name)) continue;
                    DbRecord fr = game.Record(sk.Name);
                    string granted = fr == null ? null : fr.Get("grantedSkills");
                    if (string.IsNullOrEmpty(granted)) continue;
                    foreach (string g in granted.Split(';'))
                    {
                        string one = g.Trim();
                        if (one.Length > 0) b.FormOf[one] = sk.Name;
                    }
                }
                return b;
            }

            void Add(DbRecord rec) { Add(rec, 0); }

            // `slot` is the table row; set records have one row per equipped-piece count.
            void Add(DbRecord rec, int slot)
            {
                if (rec == null) return;
                ToEverything += (int)Step(rec, "augmentAllLevel", slot);
                for (int i = 1; i <= 6; i++)
                {
                    string skill = rec.Get("augmentSkillName" + i);
                    int level = (int)Step(rec, "augmentSkillLevel" + i, slot);
                    if (!string.IsNullOrEmpty(skill) && level != 0)
                        BySkill[skill] = (BySkill.ContainsKey(skill) ? BySkill[skill] : 0) + level;

                    string mastery = rec.Get("augmentMasteryName" + i);
                    int mlevel = (int)Step(rec, "augmentMasteryLevel" + i, slot);
                    string folder = ItemViewFactory.MasteryFolder(mastery);
                    if (folder != null && mlevel != 0)
                        ByMastery[folder] = (ByMastery.ContainsKey(folder) ? ByMastery[folder] : 0) + mlevel;
                }
            }

            // One step of a per-slot list, or the single value when there is no list.
            static double Step(DbRecord rec, string field, int slot)
            {
                string v = rec == null ? null : rec.Get(field);
                if (string.IsNullOrEmpty(v)) return 0;
                string[] parts = v.Split(';');
                double d;
                return double.TryParse(parts[Math.Min(Math.Max(0, slot), parts.Length - 1)],
                                       NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
            }
        }

        // What activates a granted skill, in the game's own words: "(33% chance on
        // attack)". Nothing here is computed — the save names the controller record
        // of the binding, the record holds the chance and the trigger, and the
        // game ships a line per trigger. The twelve trigger names and the twelve
        // tagAutoSkillCondition lines match one to one.
        static readonly Dictionary<string, string> ConditionTag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "LowHealth",       "tagAutoSkillCondition01" },  // "(N% chance at M% health)"
            { "LowMana",         "tagAutoSkillCondition02" },
            { "HitByEnemy",      "tagAutoSkillCondition03" },
            { "HitByMelee",      "tagAutoSkillCondition04" },
            { "HitByProjectile", "tagAutoSkillCondition05" },
            { "CastBuff",        "tagAutoSkillCondition06" },
            { "AttackEnemy",     "tagAutoSkillCondition07" },
            { "OnEquip",         "tagAutoSkillCondition08" },
            { "HitByCrit",       "tagAutoSkillCondition09" },
            { "AttackEnemyCrit", "tagAutoSkillCondition10" },
            { "Block",           "tagAutoSkillCondition11" },
            { "OnKill",          "tagAutoSkillCondition12" },
        };

        // The condition to show for a skill: the controller the save recorded when
        // the star was bound, or failing that the one the skill record names
        // itself in templateAutoCast. The game shows the second even for a star
        // bound to nothing — a proc with an attack chance — because
        // that is what the star would do once it is bound.
        //
        // `activatorDbr` is the skill that owns and scales the proc. Empty means
        // the proc is unbound and its raw chance is displayed.
        public static string ConditionOf(GameData game, string skillDbr, string controllerFromSave)
        { return ConditionOf(game, skillDbr, controllerFromSave, null); }

        public static string ConditionOf(GameData game, string skillDbr, string controllerFromSave,
                                         string activatorDbr)
        {
            string text = Condition(game, controllerFromSave, activatorDbr);
            if (text != null) return text;
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(skillDbr); hop++)
            {
                DbRecord rec = game == null ? null : game.Record(skillDbr);
                if (rec == null) break;
                text = Condition(game, rec.Get("templateAutoCast"), activatorDbr);
                if (text != null) return text;
                skillDbr = ItemViewFactory.NextSkillRecord(rec);
            }
            return null;
        }

        // Resolve the same condition from the trigger name and chance stored directly
        // on a skill modifier (`refreshCooldownTrigger`, `refreshCooldownChance`).
        //
        // A trigger may be a list (`OnEquip;OnKill;…`); the game displays the first item.
        public static string TriggerWords(GameData game, string trigger, double chance)
        {
            if (game == null || string.IsNullOrEmpty(trigger)) return null;
            string first = trigger.Split(';')[0];
            string tag;
            if (!ConditionTag.TryGetValue(first, out tag)) return null;
            return game.FormatTag(tag, chance, 0);
        }

        public static string Condition(GameData game, string controllerDbr)
        { return Condition(game, controllerDbr, null); }

        // Numeric form of the condition for timeline simulation.
        //
        // Search the saved controller first, then follow `templateAutoCast` through
        // the skill record chain.
        public static bool AutoOf(GameData game, string skillDbr, string controllerFromSave,
                                  string activatorDbr, out string trigger, out double chance)
        {
            if (ConditionOf(game, controllerFromSave, activatorDbr, out trigger, out chance)) return true;
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(skillDbr); hop++)
            {
                DbRecord rec = game == null ? null : game.Record(skillDbr);
                if (rec == null) break;
                if (ConditionOf(game, rec.Get("templateAutoCast"), activatorDbr, out trigger, out chance))
                    return true;
                skillDbr = ItemViewFactory.NextSkillRecord(rec);
            }
            trigger = ""; chance = 0;
            return false;
        }

        public static bool ConditionOf(GameData game, string controllerDbr, string activatorDbr,
                                       out string trigger, out double chance)
        {
            trigger = ""; chance = 0;
            if (game == null || string.IsNullOrEmpty(controllerDbr)) return false;
            DbRecord ctl = game.Record(controllerDbr);
            if (ctl == null) return false;
            trigger = ctl.Get("triggerType") ?? "";
            if (!ConditionTag.ContainsKey(trigger)) { trigger = ""; return false; }
            chance = ProcChance(game, ctl.GetInt("chanceToRun"), activatorDbr);
            return true;
        }

        public static string Condition(GameData game, string controllerDbr, string activatorDbr)
        {
            if (game == null || string.IsNullOrEmpty(controllerDbr)) return null;
            DbRecord ctl = game.Record(controllerDbr);
            if (ctl == null) return null;
            string tag;
            if (!ConditionTag.TryGetValue(ctl.Get("triggerType") ?? "", out tag)) return null;
            // triggerParam is the health or energy share the trigger watches; the
            // lines that do not mention one simply ignore the second argument.
            return game.FormatTag(tag, ProcChance(game, ctl.GetInt("chanceToRun"), activatorDbr),
                                  ctl.GetInt("triggerParam"));
        }

        // Proc chance displayed by the game in parentheses.
        //
        // This is not the controller's raw `chanceToRun`; the game applies
        // `autoCastEquation` from `records/game/gameengine.dbr`.
        //
        //     procRate * (1 + (cooldown * 81)/100) * (1 - (attackDuration * 11)/100)
        //
        // Observed rules:
        //
        //   * the multiplier belongs to the activator, not the proc;
        //   * `cooldown` is the activator's integer-truncated `skillCooldownTime`;
        //   * `attackDuration` is the activator's `skillActiveDuration`, or zero;
        //   * cooldown is read from the record before equipment reduction;
        //   * only records whose class starts with `Skill_Attack` are scaled;
        //   * the multiplier is clamped to at least one and the result to 100.
        public static int ProcChance(GameData game, int procRate, string activatorDbr)
        {
            double mult = Multiplier(game, activatorDbr);
            int shown = (int)Math.Floor(procRate * mult);
            return shown > 100 ? 100 : shown;
        }

        // The activator's share of the equation, never below 1. Read off the
        // activator record itself and not the chain behind it: every one of the
        // twenty-five activators measured keeps its cooldown there.
        public static double Multiplier(GameData game, string activatorDbr)
        {
            if (game == null || string.IsNullOrEmpty(activatorDbr)) return 1;
            DbRecord rec = game.Record(activatorDbr);
            if (rec == null) return 1;
            if (!(rec.Class ?? "").StartsWith("Skill_Attack", StringComparison.OrdinalIgnoreCase)) return 1;
            int cooldown = (int)Number(rec, "skillCooldownTime");
            int active = (int)Number(rec, "skillActiveDuration");
            double mult = (1 + cooldown * 81 / 100.0) * (1 - active * 11 / 100.0);
            return mult < 1 ? 1 : mult;
        }

        static double Number(DbRecord rec, string field)
        {
            string v = rec.Get(field);
            if (string.IsNullOrEmpty(v)) return 0;
            double d;
            return double.TryParse(v.Split(';')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        // Additional pet bonuses and summoned-pet details are assembled here and
        // called from every skill description path. `pets` may be null.
        public static void Describe(GameData game, string dbr, int level, List<TipLine> into,
                                    int indent, List<PetPool.Pet> pets)
        {
            Values(game, dbr, level, into, indent);
            PetBonus(game, dbr, level, into, indent);
            Summoned(game, dbr, into, indent, pets);
        }

        // Find the all-pets bonus through the full record chain. Values are stored
        // per level; skills are not item instances and have no seed.
        public static void PetBonus(GameData game, string dbr, int level, List<TipLine> into, int indent)
        {
            if (game == null || string.IsNullOrEmpty(dbr) || level <= 0) return;
            DbRecord holder;
            DbRecord bonus = ItemViewFactory.PetBonusOf(game, dbr, out holder);
            if (bonus == null) return;
            List<TipLine> found = new List<TipLine>();
            ItemViewFactory.RenderStats(game, bonus, found, indent + 1, Slot(holder, level));
            if (found.Count == 0) return;
            into.Add(new TipLine());
            // The game's own word when it has the tag, our translated one otherwise — the same
            // pair `Affixes` uses for this heading, so both places say it identically.
            into.Add(new TipLine(ItemViewFactory.Word(game, "tagPetBonusNameAllPets", Lang.T("item.petBonusAll")),
                                 TipKind.Section, indent));
            into.AddRange(found);
        }

        // Describe the summoned pet using the prepared pool: count, lifetime, health,
        // and skills. This keeps pet bonuses, auras, and conversion consistent.
        public static void Summoned(GameData game, string dbr, List<TipLine> into, int indent,
                                    List<PetPool.Pet> pets)
        {
            if (pets == null || string.IsNullOrEmpty(dbr)) return;
            foreach (PetPool.Pet p in pets)
            {
                if (!string.Equals(p.Skill, dbr, StringComparison.OrdinalIgnoreCase)) continue;
                into.Add(new TipLine());
                into.Add(new TipLine(Lang.T("skills.pet.summons", p.Name), TipKind.Section, indent));
                List<string> facts = new List<string>();
                if (p.Limit > 1) facts.Add(Lang.T("skills.pet.limit", p.Limit));
                facts.Add(p.Seconds > 0 ? Lang.T("skills.pet.lives", p.Seconds) : Lang.T("skills.pet.permanent"));
                if (p.BaseLife > 0) facts.Add(Lang.T("skills.pet.life", p.Life));
                into.Add(new TipLine(string.Join(",  ", facts.ToArray()), TipKind.Bonus, indent + 1));
                // Calculated health may exceed the game value because some bonuses and
                // summon auras are not represented in the tooltip.
                into.Add(new TipLine(Lang.T("skills.pet.lifeNote"), TipKind.Subtle, indent + 1));
                for (int i = 0; i < p.SkillDbrs.Count; i++)
                {
                    List<TipLine> body = PetPool.SkillLines(game, p.SkillDbrs[i], p.SkillLevels[i],
                                                            p.Bonus, indent + 2, p.ConvMoves, p.Scale, p.OwnConv);
                    if (body.Count == 0) continue;
                    into.Add(new TipLine("» " + p.Skills[i] + "   " + PetPool.LevelWord(p.SkillLevels[i]),
                                         TipKind.Skill, indent + 1));
                    into.AddRange(body);
                }
                return;
            }
        }

        // The skill's numbers at that level, as description lines.
        public static void Values(GameData game, string dbr, int level, List<TipLine> into)
        { Values(game, dbr, level, into, 0); }

        public static void Values(GameData game, string dbr, int level, List<TipLine> into, int indent)
        {
            if (game == null || string.IsNullOrEmpty(dbr) || level <= 0) return;
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(dbr); hop++)
            {
                DbRecord rec = game.Record(dbr);
                if (rec == null) return;
                List<TipLine> found = new List<TipLine>();
                ItemViewFactory.RenderStats(game, rec, found, indent, Slot(rec, level));
                // Include cooldown-reset and duration details for skill modifiers.
                ItemViewFactory.RenderSkillExtras(game, rec, found, indent, Slot(rec, level));
                if (found.Count > 0) { into.AddRange(found); return; }
                dbr = ItemViewFactory.NextSkillRecord(rec);
            }
        }

        // Levels beyond the record's table repeat its last entry, which is what
        // the game does for a skill pushed past its cap by item bonuses.
        public static int Slot(DbRecord rec, int level)
        {
            // The cap is the table, not skillMaxLevel/skillUltimateLevel. Those
            // two say how many points may be *put into* a skill, and a devotion
            // skill takes one point while levelling itself to twenty-five from
            // use: a one-rank skill can still have tables with twenty-five
            // steps. Clamping by the declared cap pinned every granted skill to
            // its first-level numbers.
            int cap = TableLength(rec);
            int declared = rec.GetInt("skillUltimateLevel");
            if (declared <= 0) declared = rec.GetInt("skillMaxLevel");
            if (declared > cap) cap = declared;
            if (cap <= 0) cap = level;
            return Math.Max(0, Math.Min(level, cap) - 1);
        }

        // How many steps a record actually lists: the longest table of numbers it
        // holds. Fields whose semicolons separate something other than numbers
        // (lists of records, for one) are not tables and do not count.
        public static int TableLength(DbRecord rec)
        {
            int best = 0;
            if (rec == null) return 0;
            foreach (KeyValuePair<string, string> kv in rec.Fields)
            {
                string v = kv.Value;
                if (string.IsNullOrEmpty(v) || v.IndexOf(';') < 0) continue;
                string[] parts = v.Split(';');
                bool numeric = true;
                foreach (string p in parts)
                {
                    double d;
                    if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    { numeric = false; break; }
                }
                if (numeric && parts.Length > best) best = parts.Length;
            }
            return best;
        }

        // Adds one skill's numbers at its level into a running total, so a whole
        // constellation tree can be shown as a single list of bonuses.
        public static void Accumulate(DbRecord rec, int level, Dictionary<string, double> totals)
        { Accumulate(rec, level, totals, null, null); }

        // With a name for the source, the sum can also say what it is made of —
        // the same thing the damage window shows on hover, and the only way to
        // answer "which constellation did this number come from".
        public static void Accumulate(DbRecord rec, int level, Dictionary<string, double> totals,
                                      Dictionary<string, List<KeyValuePair<string, double>>> terms,
                                      string source)
        {
            if (rec == null || level <= 0) return;
            int slot = Slot(rec, level);
            foreach (KeyValuePair<string, string> kv in rec.Fields)
            {
                string v = kv.Value;
                if (string.IsNullOrEmpty(v)) continue;
                string[] parts = v.Split(';');
                string one = parts[Math.Min(slot, parts.Length - 1)];
                double d;
                if (!double.TryParse(one, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) continue;
                if (d == 0) continue;
                // A bonus against a race needs the race name beside it, and the
                // names do not add up — those lines belong to their own source.
                if (kv.Key.StartsWith("racialBonus", StringComparison.Ordinal)) continue;
                totals[kv.Key] = (totals.ContainsKey(kv.Key) ? totals[kv.Key] : 0) + d;
                if (terms == null) continue;
                if (!terms.ContainsKey(kv.Key)) terms[kv.Key] = new List<KeyValuePair<string, double>>();
                terms[kv.Key].Add(new KeyValuePair<string, double>(source ?? "", d));
            }
        }

        // The totals rendered with the ordinary stat table: a record is built out
        // of the sums and handed to the same code that describes an item.
        public static List<TipLine> RenderSums(GameData game, Dictionary<string, double> totals)
        {
            DbRecord synthetic = new DbRecord();
            synthetic.Path = "<devotion totals>";
            foreach (KeyValuePair<string, double> kv in totals)
                synthetic.Fields[kv.Key] = kv.Value.ToString("0.###", CultureInfo.InvariantCulture);
            List<TipLine> lines = new List<TipLine>();
            ItemViewFactory.RenderStats(game, synthetic, lines, 0, 0);
            return lines;
        }
    }
}
