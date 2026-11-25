using System;
using System.Collections.Generic;
using Gdc;

namespace Gdc.Gui
{
    // HITTING, CRITS AND THE TARGET'S ARMOUR — the arithmetic that used to live in the WINDOW.
    //
    // `Pth`, `Ability` and the crit steps sat inside `DamageForm` because at first only the tooltip
    // showed them. The timeline needs the same numbers without a window — the engine must run in
    // the battery, where there is no window at all. So they moved here rather than being written
    // a second time (PLAN, rule 18).
    //
    // EVERYTHING THAT CAN BE IS TAKEN FROM `records/game/combatformulas.dbr`: nothing the game
    // keeps is written as a number here (PLAN, rule 7).
    public static class Combat
    {
        // The hit die: uniform on [0, 150]. A hit when the roll is below PTH, a crit when below
        // PTH − 135. Measured on eight numbers (PLAN 3.1); the PTH equation is
        // `probabilityToHitEquation` word for word.
        public const double Roll = 150.0;

        public static double Pth(double oa, double da)
        {
            return ((oa / ((da / 3.5) + oa)) * 300 * 0.3)
                 + (((((oa * 3.25) + 10000) - (da * 3.25)) / 100) * 0.7) - 50;
        }

        // THERE IS NO CONVERSION FROM PTH TO PER CENT: the percentage the game shows IS the PTH,
        // one to one. Proved twice on 6 September (MECHANICS 35):
        //
        //   1. two points, one unknown — against the dummy the game shows 89.3% at attack 1822 and
        //      74.3% at 1372; their RATIO depends only on the target's defence, gives 1843, and the
        //      conversion factor then comes out 1.0156. Check: PTH(1372, 1843) = 74.32;
        //   2. a crit at a shown hundred per cent — the game prints crit up to 38.4%, which after a
        //      division by 1.5 could not pass 11% against ANY target.
        //
        // The old `/1.5` rule stood on ONE August measurement and survived because "hit minus crit"
        // is the same under both readings (135/1.5 = 90).
        public static double Percent(double pth) { return pth; }

        // The floor on the hit chance: the game's own tooltip says it cannot fall below 55%
        // (`pthMinimum`).
        public static double HitPercent(GameData game, double oa, double da)
        {
            double floor = Field(game, "pthMinimum", 55);
            double p = Percent(Pth(game, oa, da));
            if (p < floor) p = floor;
            return p > 100 ? 100 : p;
        }

        // THE CRIT STEPS. Thresholds and multipliers come from the game's file, not from numbers
        // here (rule 7): `pthThreshold1..6` = 70, 90, 105, 120, 130, 135 and `pthDamageModifier1..6`
        // = 1, 1.1, 1.2, 1.3, 1.4, 1.5.
        //
        // They read as PER CENT, and then everything fits: a crit starts at the SECOND threshold
        // (90), and the game's "crit chance" equals `PTH − 90`, which is what every measurement
        // shows. The first threshold (70) is not about crits at all — below it the damage itself is
        // cut (`normalPTHEquation = PTH/70`).
        //
        // OPEN: on `_Itaan` SEVEN steps were measured (up to 1.7) while the file holds six. The
        // seventh and its threshold are nowhere to be found — PLAN 3.3b.
        public static double[] Thresholds(GameData game)
        {
            List<double> got = new List<double>();
            for (int i = 1; i <= 8; i++)
            {
                double v = Field(game, "pthThreshold" + i, -1, false);
                if (v < 0) break;
                got.Add(v);
            }
            if (got.Count > 0 || game == null) return got.ToArray();
            NoteFallback("records/game/combatformulas.dbr: pthThreshold1..6");
            return new[] { 70.0, 90, 105, 120, 130, 135 };
        }

        public static double[] Multipliers(GameData game)
        {
            List<double> got = new List<double>();
            for (int i = 1; i <= 8; i++)
            {
                double v = Field(game, "pthDamageModifier" + i, -1, false);
                if (v < 0) break;
                got.Add(v);
            }
            if (got.Count > 0 || game == null) return got.ToArray();
            NoteFallback("records/game/combatformulas.dbr: pthDamageModifier1..6");
            return new[] { 1.0, 1.1, 1.2, 1.3, 1.4, 1.5 };
        }

        // FALLBACK NUMBERS INSTEAD OF THE GAME'S FIELDS (15 September) — for the case where a game
        // update moves or renames a field. The calculation then does not break but takes the
        // previous number from the code, the one that was checked. Staying silent about it is not
        // allowed: the missing field is recorded here and the reports print the list.
        public static readonly SortedSet<string> Fallbacks = new SortedSet<string>(StringComparer.Ordinal);

        public static void NoteFallback(string what)
        {
            lock (Fallbacks) Fallbacks.Add(what);
        }

        // Fields that "do not exist" under `--pretendmissing=` — only to test the fallback.
        public static readonly HashSet<string> PretendMissing = new HashSet<string>(StringComparer.Ordinal);

        public static bool Missing(string field)
        {
            return PretendMissing.Count > 0 && PretendMissing.Contains(field);
        }

        // A snapshot of the list, so any thread can print it.
        public static List<string> FallbackList()
        {
            lock (Fallbacks) return new List<string>(Fallbacks);
        }

        // ONE ROLL DECIDES EVERYTHING: whether it hit, and with which step.
        //
        // The roll is uniform on [0, 100). A hit when below `PTH` — so above a hundred there are no
        // misses at all, which is what the game shows as 100%. The step is the highest one whose
        // threshold the roll passed: the share of step `k` and above is `PTH − threshold k`, and
        // from that comes "crit chance = PTH − 90" for every step at once.
        //
        // Returns the step: 0 a miss, 1 an ordinary hit, 2 and above a crit. THE FIRST THRESHOLD
        // (70) IS NOT ABOUT CRITS — the loop starts at the second on purpose. Below the first the
        // damage is cut (`BelowThreshold`); a crit starts at the SECOND (90). An off-by-one here
        // costs an average multiplier of 1.16 instead of 1.0955, and `--timeline` reddens on it.
        public static int Tier(double pth, double roll, double[] thresholds)
        {
            if (roll >= pth) return 0;
            int tier = 1;
            for (int i = 1; i < thresholds.Length; i++)
                if (roll < pth - thresholds[i]) tier = i + 1;
            return tier;
        }

        // The step's multiplier plus the character's "+% crit damage": it is ADDED to the
        // multiplier, not multiplied by it. Measured 10 August on `_Itaan` — the step between
        // neighbouring seen multipliers is exactly 0.10.
        public static double CritMultiplier(int tier, double[] multipliers, double critDamagePercent)
        {
            if (tier <= 1) return 1;
            int at = Math.Min(tier, multipliers.Length) - 1;
            if (at < 0) return 1;
            double m = multipliers[at];
            return m <= 1 ? 1 : m + critDamagePercent / 100.0;
        }

        // AN EXPECTATION, NOT A ROLL: the average crit multiplier per ONE HIT.
        //
        // The timeline's check needs it, and it is counted DELIBERATELY by another route than the
        // run itself — rolls there, shares here; otherwise the check would be checking itself.
        //
        //   share of step `k` and above = PTH − threshold k   (per cent, at most 100)
        //   share of exactly step k     = "k and above" minus "k+1 and above"
        public static double AverageMultiplier(GameData game, double pth, double critDamagePercent)
        {
            double[] t = Thresholds(game), m = Multipliers(game);
            double hit = Clamp(pth);
            if (hit <= 0) return 1;
            double sum = 0;
            for (int tier = 1; tier <= m.Length; tier++)
            {
                // The share of "step k and above" = `PTH − threshold k`, where the second step's
                // threshold is `t[1]` (90), not `t[0]` (70) — see `Tier`.
                double atLeast = tier == 1 ? hit : Clamp(pth - t[tier - 1]);
                double atLeastNext = tier >= m.Length || tier >= t.Length
                                   ? 0 : Clamp(pth - t[tier]);
                double share = atLeast - atLeastNext;
                if (share <= 0) continue;
                sum += share * CritMultiplier(tier, m, critDamagePercent);
            }
            return sum / hit;
        }

        // The share of hits: the same `pthMinimum` floor and a ceiling of a hundred per cent.
        public static double HitFraction(GameData game, double pth)
        {
            double floor = Field(game, "pthMinimum", 55);
            double p = Percent(pth);
            if (p < floor) p = floor;
            return (p > 100 ? 100 : p) / 100.0;
        }

        // THE SHARE OF STRIKES THAT CAME OUT AS CRITS — of all strikes, not of hits. A crit starts
        // at the second threshold (`pthThreshold2` = 90). Needed by the `AttackEnemyCrit` condition:
        // a devotion star bound to it is rolled only on a critical strike.
        public static double CritFraction(GameData game, double pth)
        {
            double[] t = Thresholds(game);
            if (t.Length < 2) return 0;
            double p = Percent(pth);
            double over = p - t[1];
            if (over <= 0) return 0;
            return (over > 100 ? 100 : over) / 100.0;
        }

        static double Clamp(double v) { return v < 0 ? 0 : (v > 100 ? 100 : v); }

        // Below the first threshold the damage is CUT: `normalPTHEquation = PTH / 70`. Our
        // characters do not go there, but a weak one against a strong target loses up to 21%
        // (the floor `pthMinimum` 55, and 55/70 = 0.786). The floor itself is the game's field,
        // not a number here: the engine and the check both take it from here.
        public static double PthMinimum(GameData game)
        {
            return Field(game, "pthMinimum", 55);
        }

        public static double BelowThreshold(GameData game, double pth)
        {
            double first = Field(game, "pthThreshold1", 70);
            if (first <= 0 || pth >= first) return 1;
            return pth / first;
        }

        // Offensive and defensive ability — the game's own equations (`offensiveAbilityEquation`,
        // `defensiveAbilityEquation`), matched to the unit on `_Amakata`.
        public static double Ability(GameData game, Character c, CharacterTotals t,
                                     double attribute, string field, string attrField)
        {
            TotalsEntry e;
            double flat = t.Fields.TryGetValue(field, out e) ? e.Value : 0;
            double pct = t.Fields.TryGetValue(field + "Modifier", out e) ? e.Value : 0;
            double attrFlat = t.Fields.TryGetValue(attrField, out e) ? e.Value : 0;
            double attrPct = t.Fields.TryGetValue(attrField + "Modifier", out e) ? e.Value : 0;
            double attr = (attribute + attrFlat) * (1 + attrPct / 100);
            return AbilityEquation(game, field == "characterOffensiveAbility",
                                   flat + PcBase(game, c, field), c.Level, attr, pct);
        }

        // The base comes from the record rather than being written as a number: the sexes have
        // records of their own, and a patch can move it.
        public static double PcBase(GameData game, Character c, string field)
        {
            DbRecord pc = game.Record("records/creatures/pc/" +
                                      (c.Sex == 0 ? "female" : "male") + "pc01.dbr");
            if (pc == null) pc = game.Record("records/creatures/pc/malepc01.dbr");
            return pc == null ? 0 : pc.GetInt(field);
        }

        // THE TARGET'S ARMOUR. The equation is the game's own, word for word (MECHANICS 32):
        //
        //   damage ≤ armour   damage × (1 − absorption)
        //   damage > armour   armour × (1 − absorption) + (damage − armour)
        //
        // And only for PHYSICAL damage: both equations name `physicalDamageDV`, and the file holds
        // no other defence equation at all.
        public static double AfterArmour(double physical, double armour, double absorbPercent)
        {
            if (physical <= 0 || armour <= 0) return physical;
            double absorb = absorbPercent / 100.0;
            if (absorb < 0) absorb = 0;
            if (absorb > 1) absorb = 1;
            return physical <= armour
                 ? physical * (1 - absorb)
                 : armour * (1 - absorb) + (physical - armour);
        }

        // The armour's base absorption — `armorDefensiveAbsorption` from `gameengine.dbr`; the
        // defence panel holds the same number (MECHANICS 20).
        public static double BaseAbsorb(GameData game)
        { return Of(game, "records/game/gameengine.dbr", "armorDefensiveAbsorption", 70); }

        // CAST SPEED — three of the game's numbers, all from records (MECHANICS 74):
        //   cap    `playerSpellCastSpeedCapMax` 200   gameengine.dbr
        //   floor  `playerSpellCastSpeedCapMin`  20   gameengine.dbr
        //   base   `characterSpellCastSpeed`   1.25   malepc01/femalepc01 — the same in all four
        //          databases for both, so the male one is taken.
        // The base is what the game calls "100%": a 300 ms channel divided by it gives the very
        // "0.24 s at 100% cast speed" the skill's tooltip prints.
        public static double CastSpeedCap(GameData game)
        { return Of(game, "records/game/gameengine.dbr", "playerSpellCastSpeedCapMax", 200); }

        public static double CastSpeedFloor(GameData game)
        { return Of(game, "records/game/gameengine.dbr", "playerSpellCastSpeedCapMin", 20); }

        public static double BaseCastSpeed(GameData game)
        { return Of(game, "records/creatures/pc/malepc01.dbr", "characterSpellCastSpeed", 1.25); }

        // The character's base ATTACK speed — the twin of cast speed, from the same record. Needed
        // by the animation length of a skill that strikes with the weapon (MECHANICS 87): such a
        // skill plays at attack speed, like a swing.
        public static double BaseAttackSpeed(GameData game)
        { return Of(game, "records/creatures/pc/malepc01.dbr", "characterAttackSpeed", 1.25); }

        // The tick interval of a channelled skill, in seconds: `timeBetweenAttacks` (ms) divided by
        // the character's base and by the cast speed percentage.
        //   `_Satirita` at 200%:  300 / (1.25 × 2.00) = 120 ms
        //   at 100%:              300 / (1.25 × 1.00) = 240 ms — the game's own word
        public static double ChannelInterval(GameData game, double channelMs, double castPercent)
        {
            if (channelMs <= 0 || castPercent <= 0) return 0;
            return channelMs / 1000.0 / (BaseCastSpeed(game) * castPercent / 100.0);
        }

        // AURA PULSE AND DOT TICK — one second each. THE ONE PLACE TO CHANGE THEM.
        //
        // NEITHER NUMBER IS IN THE GAME DATABASE — the engine holds both. Checked by dump
        // on 28 September: the toggle `veilofshadows1` (`Skill_BuffRadiusToggled`), its buff
        // `veilofshadows1_buff` and the modifier `veilofshadows2` ("Night's Chill", the part
        // that actually deals the damage) carry the damage itself (`offensiveColdMin`
        // 10…152), the dot (`offensiveSlowColdMin`) and the dot's duration
        // (`offensiveSlowColdDurationMin` 2) — and NO interval field at all. `gameengine.dbr`
        // has none either: nothing there matches interval/pulse/tick. So there is nothing
        // for `Of(game, …)` to read, and these two stay plain numbers, unlike everything
        // else in this file (rule 7 of PLAN: take it from the game — here the game does not
        // tell).
        //
        // WHERE THE SECOND COMES FROM: measured by the user on `_Amakata` (MECHANICS 41) —
        // "he has veil of shadow and spectral binding, once per 1 sec I see 2 different
        // damage numbers", i.e. auras do not merge into one strike and all beat by the
        // second. Dots tick by the same second (MECHANICS 70, partial last tick).
        //
        // WHY VARIABLES AT ALL (user's request, 28 September): the number lives in the game
        // engine, so a patch can move it — and then it must be changed in ONE place, not
        // hunted down across `Timeline`. Both the engine and the EXPECTATION of the check
        // read these constants (`Timeline`: aura, pet aura, dot tick and the tick share;
        // `TimelineTest`: the per-second expectation of an aura and the partial-tick print),
        // so a change keeps them in step instead of reddening the check on a mismatch.
        //
        // STARTING MOMENT IS NOT AFFECTED: auras enter the queue at second 0 whatever the
        // period is — they are already up when the fight starts.
        public const double AuraPulseSeconds = 1;
        public const double DotTickSeconds = 1;

        // CAPS FROM `gameengine.dbr` (15 September): attack speed `playerAttackSpeedCapMax` 200 and
        // resistances `playerDefenseCap` 80;80;80 (per difficulty, all three equal — the first is
        // taken). Both used to be written as numbers here.
        public static double AttackSpeedCap(GameData game)
        { return Of(game, "records/game/gameengine.dbr", "playerAttackSpeedCapMax", 200); }

        public static double ResistCap(GameData game)
        { return Of(game, "records/game/gameengine.dbr", "playerDefenseCap", 80); }

        // WHAT ONE ATTRIBUTE POINT GIVES — `records/creatures/pc/playerlevels.dbr` (15 September).
        // A point is `strengthIncrement` / `dexterityIncrement` / `intelligenceIncrement` = 8 units;
        // health per point is `lifeIncrement` 20 (physique), `lifeIncrementDexterity` 8,
        // `lifeIncrementIntelligence` 12; energy is `manaIncrement` 16. Per unit that is exactly the
        // 2.5 / 1 / 1.5 and 2 measured in August (MECHANICS 14v).
        public static double PlayerLevels(GameData game, string field, double fallback)
        { return Of(game, "records/creatures/pc/playerlevels.dbr", field, fallback); }

        // The same record's equation as text — `experienceLevelEquation`. No record or no field
        // gives null and a fallback note, and the caller falls back to its own former formula.
        public static string PlayerLevelsEquation(GameData game, string field)
        {
            if (game == null) return null;
            DbRecord r = game.Record("records/creatures/pc/playerlevels.dbr");
            string text = r == null || Missing(field) ? null : r.Get(field);
            if (string.IsNullOrEmpty(text))
            {
                NoteFallback("records/creatures/pc/playerlevels.dbr: " + field);
                return null;
            }
            return text;
        }

        // A new character's base attribute — `characterStrength` and the rest in `malepc01` (50).
        public static double PcAttribute(GameData game, string field)
        { return Of(game, "records/creatures/pc/malepc01.dbr", field, 50); }

        // Offensive or defensive ability per unit of an attribute — out of the equation itself: the
        // difference at attribute 1000 and 0 divided by 1000 (it is linear; currently `*0.5`).
        public static double AbilityPerAttribute(GameData game, bool offensive)
        {
            return (AbilityEquation(game, offensive, 0, 0, 1000, 0) - AbilityEquation(game, offensive, 0, 0, 0, 0)) / 1000;
        }

        // HOW AN ATTRIBUTE GROWS THE DAMAGE — the divisor comes from the game's equation, not from a
        // number here: `physicalDamageEquation` = damage × (cunning/245 + 1), for dots
        // `physicalDurationDamageEquation` (/215), magical `magicalDamageEquation` (/215) and its
        // dots `magicalDurationDamageEquation` (/200). The equation is linear in the attribute, so
        // one substitution gives the divisor: damage 1, attribute 1000. If it does not parse, the
        // fallback is the number that used to stand in the code.
        public static double AttributeDivisor(GameData game, bool magic, bool overTime)
        {
            string field = magic ? (overTime ? "magicalDurationDamageEquation" : "magicalDamageEquation")
                                 : (overTime ? "physicalDurationDamageEquation" : "physicalDamageEquation");
            double got;
            lock (divisors) if (divisors.TryGetValue(field, out got)) return got;
            double div = magic ? (overTime ? 200 : 215) : (overTime ? 215 : 245);
            DbRecord r = game == null ? null : game.Record("records/game/combatformulas.dbr");
            string text = r == null || Missing(field) ? null : r.Get(field);
            double v;
            if (Equation.TryEval(text ?? "", delegate (string name)
                {
                    if (name.EndsWith("DamageDV", StringComparison.Ordinal)) return 1;
                    if (name == "dexterityDV" || name == "intelligenceDV") return 1000;
                    return 0;
                }, out v) && v > 1.0001)
                div = Math.Round(1000 / (v - 1), 6);
            else if (game != null) NoteFallback("records/game/combatformulas.dbr: " + field);
            if (game != null) lock (divisors) divisors[field] = div;
            return div;
        }

        static readonly Dictionary<string, double> divisors = new Dictionary<string, double>(StringComparer.Ordinal);

        // HIT CHANCE — the game's `probabilityToHitEquation`. The text in the file matches the
        // fallback `Pth(oa, da)` sign for sign, same order of operations; with no game or no parse
        // the fallback is used.
        public static double Pth(GameData game, double oa, double da)
        {
            string eq = Text(game, "probabilityToHitEquation");
            double v;
            if (eq != null && Equation.TryEval(eq, delegate (string name)
                {
                    return name == "offensiveAbilityDV" ? oa : name == "defensiveAbilityDV" ? da : 0;
                }, out v)) return v;
            if (eq != null) NoteFallback("records/game/combatformulas.dbr: probabilityToHitEquation (could not parse)");
            return Pth(oa, da);
        }

        // OFFENSIVE AND DEFENSIVE ABILITY — `offensiveAbilityEquation` / `defensiveAbilityEquation`:
        // (base + level×12 + attribute×0.5) × (1 + modifier/100) + 53. One place for the character,
        // the enemy and a pet; the fallback is the formula each of them used to carry as a number.
        public static double AbilityEquation(GameData game, bool offensive, double baseValue, double level,
                                             double attribute, double modifierPercent)
        {
            string eq = Text(game, offensive ? "offensiveAbilityEquation" : "defensiveAbilityEquation");
            double v;
            if (eq != null && Equation.TryEval(eq, delegate (string name)
                {
                    switch (name)
                    {
                        case "offensiveAbilityDV": case "defensiveAbilityDV": return baseValue;
                        case "characterLevelDV": return level;
                        case "dexterityDV": case "strengthDV": return attribute;
                        case "offensiveAbilityModifierDV": case "defensiveAbilityModifierDV": return modifierPercent;
                        default: return 0;
                    }
                }, out v)) return v;
            if (eq != null)
                NoteFallback("records/game/combatformulas.dbr: " +
                             (offensive ? "offensiveAbilityEquation" : "defensiveAbilityEquation") + " (could not parse)");
            return (baseValue + level * 12 + attribute * 0.5) * (1 + modifierPercent / 100) + 53;
        }

        // The equation's text from `combatformulas`, read once per field.
        static string Text(GameData game, string field)
        {
            if (game == null) return null;
            string got;
            lock (texts) if (texts.TryGetValue(field, out got)) return got;
            DbRecord r = game.Record("records/game/combatformulas.dbr");
            got = r == null || Missing(field) ? null : r.Get(field);
            if (string.IsNullOrEmpty(got))
            {
                got = null;
                NoteFallback("records/game/combatformulas.dbr: " + field);
            }
            lock (texts) texts[field] = got;
            return got;
        }

        static readonly Dictionary<string, string> texts = new Dictionary<string, string>(StringComparer.Ordinal);

        static double Field(GameData game, string name, double fallback, bool note = true)
        { return Of(game, "records/game/combatformulas.dbr", name, fallback, note); }

        // A number from the game's record; with none of its own the fallback is taken, and those
        // are exactly the cases where the fallback has been checked (`pthMinimum` 55, absorption
        // 70). A list field ("80;80;80") is read by its first value. Taking the fallback is
        // recorded (`Fallbacks`); `note == false` is for the places where a missing field is lawful
        // (the loop over threshold numbers ends at the first one absent).
        static double Of(GameData game, string record, string field, double fallback, bool note = true)
        {
            DbRecord r = game == null ? null : game.Record(record);
            string s = r == null || Missing(field) ? null : r.Get(field);
            if (!string.IsNullOrEmpty(s)) s = s.Split(';')[0];
            double v;
            if (string.IsNullOrEmpty(s) ||
                !double.TryParse(s, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out v) ||
                Math.Abs(v) <= 0.0001)
            {
                if (note && game != null) NoteFallback(record + ": " + field);
                return fallback;
            }
            return v;
        }
    }
}
