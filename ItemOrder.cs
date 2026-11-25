// The order in which the game walks a record's fields while rolling them.
//
// Rolls come strictly one per rolled field, back to back, so the order is the last thing
// missing to compute a whole item's rolled numbers. It is assembled here, and **it matters
// which parts are measured and which are taken on faith**.
//
// HOW IT IS MEASURED NOW (13 August, and this removed the brute force entirely). The item's
// stream is known exactly: we know the seed and we know the generator. So an item with n
// rolled fields has n known stream words, and of every field with a number from the game one
// can ask: at which word does that number come out at all? Usually exactly one fits — and the
// field's place in the walk is MEASURED, not fitted. That is what `--orderfit`
// ([OrderFit.cs](OrderFit.cs)) does; it also prints what our order contradicts. Everything
// below is built from its relations.
//
// MEASURED — the order of GROUPS:
//       character*  →  crit damage  →  offensive* (all)  →  retaliation*
//                   →  defensive*  →  skill*
//   `retaliation*` BEFORE `defensive*` — 16 relations on six items (`_Relo` equip1/equip6,
//   `_Amakata` equip10, `_Itaan` equip4/equip6/equip10) and not one in the other direction.
//   We used to have it the other way round.
//
// MEASURED — within `offensive*` there are FOUR blocks, and they are not about damage type:
//       1) flat direct   (offensive{Type}Min/Max)
//       2) flat dot      (offensiveSlow{Type}Min/Max)
//       3) direct percent (offensiveTotalDamageModifier, then by type)
//       4) dot percent   (offensiveSlow{Type}Modifier, duration right after its own)
//   and at the very end the "other weapon" fields: offensiveLifeLeech*,
//   offensiveTotalResistanceReduction*. Block 2 before block 3 is measured three times
//   (offensiveSlowPoisonMin < offensivePoisonModifier on `_Itaan` equip5/equip6,
//   offensiveSlowBleedingMin < offensiveColdModifier on `_Jien` weapon0), block 1 before
//   block 2 twice (offensiveColdMin < offensiveSlowBleedingMin).
//
// MEASURED — the type order is THE SAME in all three places (direct damage, resistances,
// retaliation): physical, pierce, fire, cold, lightning, acid, life, aether, chaos, elemental.
// Resistances then take bleeding and after it stun/freeze. This is no guess: all 37 relations
// between resistances fit this order without exception, and the previous one (acid before
// pierce, bleeding before elemental) contradicted them.
//
// MEASURED — dots have a type order of their own: trauma, bleeding, burn, frostburn,
// electrocute, poison, vitality decay. Trauma before bleeding (`_Relo` equip3), bleeding
// before frostburn, burn before frostburn, frostburn before electrocute and poison, poison
// before decay.
//
// MEASURED — within character*: physique < life < offensive ability < defensive ability <
// life regeneration < its percent < constitution < dodge; attack speed after defensive
// ability and before cast speed; run speed before dodge.
//
// TAKEN ON FAITH (no measurement, but no contradiction either):
//   * the place of `offensiveCritDamageModifier` relative to FLAT damage: against percentages
//     it is measured (first of all), against flat damage no item was found;
//   * the place of `characterConstitutionModifier` relative to the speeds: all that is
//     measured is that it comes after the life-regeneration percent and before dodge;
//   * `offensiveBonus*` and rare fields with no measurement at all fall to the end of their
//     block.
//
// Two checks guard the order: `--ordertest` (how many items reproduce IN FULL, a number that
// must grow) and `--orderfit` (how many measured relations the order contradicts, must be 0).

using System;
using System.Collections.Generic;

namespace Gdc.Gui
{
    public static class ItemOrder
    {
        // The character-panel order.
        //
        // `HealIncrease` (increased healing effects) was added 15 August, right AFTER defensive
        // ability and BEFORE the speeds. Measured on an item of `_Amakata` (bag 3) where these
        // four fields run consecutively and all three disputed places settle at once:
        //     defensive ability(1) 32 → HEALING(2) 10 → attack speed(3) 6 → cast speed(4) 4
        // The old order (healing at the end of the group) gave 5, 6 and 8 — all three wrong.
        // Confirmed by three more items where it also fitted before: one with attack ability →
        // healing, and two with life → defensive ability → healing, every number in full.
        // TAKEN ON FAITH: the place relative to `LifeRegen`, `ManaRegen` and `Constitution` —
        // we have no item where healing meets them.
        //
        // THE PERCENTS OF THE THREE ATTRIBUTES DO NOT SIT WHERE THEIR FLAT BROTHERS DO, but
        // after life and energy. Measured 15 August on two items from `_Relo`'s stash:
        //   a belt:  life(1) 173 → physique%(2) 4;
        //   a shard: life(1) 267 → cunning%(2) 3 → spirit%(3) 5 → energy regen%(4) 56.
        // The old order (percents together with the flat ones, at the head of the group) gave
        // 219 instead of 173 on the belt, and 326/3/3 instead of 267/3/5 on the shard.
        // The flat fields stay where they were, and that is a measurement too: on a shield
        // physique(1) → life%(2) → life per second(3) → stamina%(4), every number agreeing. So
        // the rule is not "all flat, then all percent" — it is THESE THREE fields that have a
        // place of their own.
        // TAKEN ON FAITH: their place relative to the attack and defence abilities — we have no
        // item where they would meet; all that is known is that they come AFTER life and BEFORE
        // energy regeneration.
        static readonly string[] Character = {
            "Strength", "Dexterity", "Intelligence", "Life", "Mana",
            "StrengthModifier", "DexterityModifier", "IntelligenceModifier",
            "OffensiveAbility", "DefensiveAbility", "HealIncrease", "LifeRegen", "ManaRegen", "Constitution",
            "AttackSpeed", "SpellCastSpeed", "TotalSpeed", "RunSpeed",
            "DodgePercent", "DeflectProjectile", "EnergyAbsorption", "LightRadius" };

        // The game's damage-type order. The same for resistances and for retaliation.
        // `TotalDamage` (the overall damage percent) comes BEFORE the types — measured both on
        // direct damage (`_Amakata` equip11) and on retaliation (`_Relo` equip1).
        static readonly string[] Direct = {
            "TotalDamage", "Bonus", "Physical", "Pierce", "Fire", "Cold", "Lightning",
            "Poison", "Life", "Aether", "Chaos", "Elemental" };

        // Dots: trauma, bleeding, burn, frostburn, electrocute, poison, decay — and the slows
        // after them.
        // `ManaLeach` (energy leech over seconds) is a flat dot too. That it belongs to THIS
        // block was measured on an item of `_Amakata` (stash): attack speed(1) → chaos min(2) →
        // chaos max(3) → LEECH(4) → chaos%(5) → chaos resist(6), all six numbers agreeing with
        // the game. At the tail of the group it would come after the percent and both numbers
        // would disagree.
        // ITS PLACE AMONG THE DOT TYPES IS UNOBSERVABLE: in the whole game there is not one
        // record where energy leech stands next to another flat dot (checked with
        // `--fieldmates` over all 17 records carrying the field). So it sits at the end of the
        // list — and nothing can tell that apart from any other place.
        static readonly string[] Dot = {
            "Physical", "Bleeding", "Fire", "Cold", "Lightning", "Poison", "Life", "Total",
            "ManaLeach" };

        // Resistances — the same type order, then bleeding, then the status resistances.
        // `BlockModifier` is a BONUS TO BLOCK CHANCE (+4% block chance) and is not the same as
        // a shield's base. The game has four block fields and they must be told apart (the
        // user's observation of 15 August, and a correct one):
        //     defensiveBlock               damage the block absorbs   shield base, NOT rolled
        //     defensiveBlockChance         block chance               shield base, NOT rolled
        //     defensiveBlockAmountModifier +% block amount            bonus, rolled
        //     defensiveBlockModifier       +% block chance            bonus, rolled
        // The place was measured on an item of `_Amakata` (stash): word 6 gives block chance 4
        // and word 7 pierce resistance 57, both agreeing with the game; the old order (block at
        // the end of the group) gave 5 and 47, both wrong. So the block bonus comes BEFORE the
        // resistances.
        // THE ORDER OF THESE TWO RELATIVE TO EACH OTHER WAS MEASURED ON 31 AUGUST — before that
        // it was TAKEN ON FAITH, and taken wrongly: we had no item where both fields meet. One
        // turned up on the shield user `_222` — a relic — and it turned up from the side nobody
        // was looking at: the BLOCK in the character panel did not add up.
        //
        //     block chance   ours 35 (shield) + 45 = 80          game 79
        //     block amount   ours 1230 × (1 + 312/100) = 5068    game 4932 = 1230 × 4.01
        //
        // One item causes both gaps: the game needs its +4% block chance and +45% block amount,
        // where we came out with 5 and 56. The user photographed the tooltips of THREE such
        // relics (one worn, two in the stash), and all twelve numbers fitted one arrangement —
        // chance BEFORE block amount:
        //
        //     seed          life(2)   CHANCE(3)   AMOUNT(4)   bleeding(5)
        //     354681809      1274         4          45           52
        //     2406374745      973         4          40           55
        //     4232218877     1080         3          52           60
        //
        // The old order gave 56/5, 44/3 and 40/5 — six numbers out of twelve wrong, while life
        // and bleeding fitted even then: words 2 and 5 are what they occupied.
        static readonly string[] Resist = {
            // `Absorption` (+N% armour absorption) comes right AFTER armour and BEFORE the
            // resistances. Measured 15 August on a sash (the twelfth experiment, both seeds):
            // armour%(1) 23 → ABSORPTION(2) 14 → pierce(3) 26 → bleeding(4) 22 → its
            // duration(5) 35; with the second seed 21 → 15 → 21 → 25 → 33. The old place (end
            // of the group) gave 20/20/27/18 and 30/22/33/17 — everything wrong but the first.
            "Protection", "Absorption", "BlockModifier", "BlockAmount", "Physical", "Pierce", "Fire", "Cold", "Lightning",
            "Poison", "Life", "Aether", "Chaos", "Elemental", "Bleeding",
            "Stun", "Freeze", "Trap", "Petrify", "Sleep", "Disruption",
            // Life-leech resistance then energy-leech resistance, in that order, and this is a
            // MEASUREMENT rather than alphabet: on an item of `_Amakata` (stash) both record
            // fields are 100, while the game prints 100% life leech and 80% energy leech. The
            // reverse order would give 80 and 100.
            // This pair's place among the OTHER resistances is unobservable: in the whole game
            // there is no record where energy leech stands next to an ordinary resistance
            // (checked with `--fieldmates`; there are four records in all, and all of them are
            // that same item).
            "SlowLifeLeach", "SlowManaLeach", "Total",
            // `PercentReflection` (−N% reflected damage taken) sits at the very end of the
            // group. Measured 15 August on a medal (the twelfth experiment, both seeds):
            // pierce(7) 13 → elemental(8) 29 → REFLECTION(9) 13, and with the second seed
            // 13 → 26 → 13. Before the experiment this was a guess (an unknown type fell to
            // the end) and a CORRECT one; now it is measured, and the doubt flag is honestly
            // cleared.
            "PercentReflection",
            // `PercentCurrentLife` (−N% of damage taken from current life) comes AFTER the
            // ordinary resistances. Measured 19 August on a pet bonus (`_Izaar`, item seed
            // 3888301884): the bonus record holds exactly two fields, `defensivePoison 25` and
            // `defensivePercentCurrentLife 20`, while the game prints 25% poison and acid
            // resistance and 18% resistance to life reduction. There are two arrangements and
            // EXACTLY ONE gives the game's numbers:
            //     poison first     →  25 and 18   ← the game
            //     reduction first  →  26 and 17
            // Before that the type was unknown and fell to the end of the group — CORRECTLY,
            // but the line honestly carried a red "rolled 18?" mark. Now it is measured.
            "PercentCurrentLife",
            // `Convert` (N% reduced mind-control duration) is at the end of the group too, with
            // the same history as its neighbour above: the type was unknown, fell to the end,
            // fell CORRECTLY, and the line hung with a red "rolled 88?" mark.
            //
            // MEASURED 3 September on THREE items of `_666`, every game number agreeing in full:
            //   a worn amulet, pet bonus, 6 fields
            //        bleeding 8/s → physical 6 → bleeding 32 → stun 104
            //        → MIND CONTROL 88 → conversion 100
            //   the same item in a bag, same fields: 9 → 6 → 29 → 112
            //        → MIND CONTROL 113 → conversion 85
            //   an awakened off-hand: speed 7 → life damage 11
            //        → total damage 176 → MIND CONTROL 118
            // Move the field earlier and the numbers shift, and `--ordertest` goes red.
            //
            // WHAT THE MEASUREMENT DOES NOT SAY, SAID HONESTLY: `Convert`'s place among the
            // OTHER impairments (Freeze, Trap, Petrify, Sleep, Disruption) and relative to
            // `PercentReflection` / `PercentCurrentLife` is unobservable — we have no item where
            // they would meet. All that is measured is "after stun and before conversion", and
            // that is the end of the group.
            "Convert" };

        // The tail of the offensive group — what is measured AFTER all the dot percentages.
        // Life leech and resistance reduction: `_Amakata` weapon0/weapon1. The slows:
        // `_Iman` equip11, where `offensiveSlowRunSpeedMin` sits after five percentages in a
        // row though its name makes it a flat dot field. So the slows do not go with the dots
        // but as a tail of their own.
        // TAKEN ON FAITH: the order WITHIN the tail — we have no item where two of them meet.
        //
        // `AttackSpeed` was added here on 13 August from a screenshot where
        // `retaliationSlowAttackSpeedMin` landed AFTER `retaliationTotalDamageModifier` rather
        // than before it, as its name suggested.
        //
        // `TotalDamageReduction` (reduced target damage) was added 14 August. By name the type
        // table caught it as `TotalDamage`, i.e. it fell into FLAT DIRECT DAMAGE at the very
        // head of the group, and everything after it shifted. On one weapon that cost four
        // numbers at once: bonus physical 25→23, total damage 104→109, physical 199→205,
        // trauma 149→199 — all agreeing with the game as soon as the field moved to the tail.
        // The place was chosen by data, not by eye: put it AFTER the `skill*` group and
        // `--orderfit` reports two contradictions with the measurements while `--ordertest`
        // drops from 663/80 to 660/79. In the `offensive` tail — zero contradictions.
        //
        // Confusion was added 15 August from an item of `_Amakata` (stash). It is not damage
        // but SECONDS of a condition, and by name the field was caught as flat retaliation
        // damage, so it travelled to the head of the group. The measurement is unambiguous —
        // six numbers settled at once:
        //     retal.poison(7) → retal.poison dot(8) → retal.%(9) → CONFUSION(10)
        //                     → poison res(11) → life res(12) → stun(13)
        // The old order gave 3/1169/52/37/21/24 instead of 1/1188/42/28/30/21.
        //
        // DAMAGE REDUCTION COMES BEFORE RESISTANCE REDUCTION — measured 17 August on a blade of
        // `_Amakata` (bag 4). Both fields come from the suffix, and in the stream they get two
        // neighbouring words, the tenth and the eleventh:
        //     word                                          10     11
        //     offensiveTotalResistanceReductionPercentMin    20     22    ← game 22
        //     offensiveTotalDamageReductionPercentMin        15     13    ← game 15
        // This pair has exactly two free words (the ninth is taken by the chaos percent, the
        // twelfth by aether resistance), so there are only two arrangements, and exactly one
        // gives the game's numbers: damage reduction first. The old order gave 20 and 13 —
        // both wrong.
        static readonly string[] Tail = {
            "LifeLeech", "TotalDamageReduction", "TotalResistanceReduction",
            "TotalSpeed", "AttackSpeed", "RunSpeed", "Offense", "Defense", "Confusion",
            // Reduced target defensive ability is a DEBUFF too, not damage, and its place is
            // the same as confusion's: the tail of the group. Measured 15 August on an item of
            // `_Assira` (stash):
            //     physical%(5) 316 → trauma%(6) 262 → DEFENSIVE ABILITY REDUCTION(7) 124
            // The old order (the debuff in the flat dot block, word 5) gave 103, 262 and 288 —
            // all three wrong. `OffensiveAbility` was added beside it BY SYMMETRY: it has no
            // measurement of its own, no item with that field was found.
            "DefensiveAbility", "OffensiveAbility",
            // `Freeze` and `Fumble` — target freeze and target fumble, both in SECONDS. They
            // are debuffs, and their place is confusion's: the tail of the group, after the
            // percentages. Measured 15 August, both seeds, both items in full:
            //   item one: …flat frostburn(5) → cold%(6) → frostburn%(7) → FREEZE(8)
            //             102 frostburn, 200%, 259%, 2 s — as in the game;
            //             the old order gave 33/259/226 and 2 s on the fifth word;
            //   item two: chaos 2-25(3,4) → chaos%(5) 100 → FUMBLE(6) 14;
            //             the old order gave fumble 18 and chaos% 79.
            "Freeze", "Fumble",
            // `Stun` is also a debuff and belongs at the end of the group. Once
            // item scaling is applied (`ItemView.IsDamage`), all eight values use
            // the same order as the game.
            //
            // WARNING: the relative order of tail-only fields is unmeasured because
            // no known item contains two of them. Append this field where unknown
            // types already go; measure the tail separately if such an item appears.
            "Stun" };

        // The longest match, not the first: otherwise `characterLifeRegen` catches on `Life`
        // and travels five places forward. That is what kept the attribute order wrong.
        static int Index(string[] table, string rest)
        {
            int best = -1, bestLen = -1;
            for (int i = 0; i < table.Length; i++)
                if (rest.StartsWith(table[i], StringComparison.Ordinal) && table[i].Length > bestLen)
                { best = i; bestLen = table[i].Length; }
            return best;
        }

        // Two fields are the ends of ONE pair: `…Min` and `…Max` with the same stem. Needed by
        // the rule that one part's pair is not split by another's (RollWhole).
        static bool SamePair(string a, string b)
        {
            if (a == null || b == null) return false;
            string na = Base(a), nb = Base(b);
            return na != null && na == nb && a != b;
        }

        static string Base(string f)
        {
            if (f.EndsWith("Min", StringComparison.Ordinal)) return f.Substring(0, f.Length - 3);
            if (f.EndsWith("Max", StringComparison.Ordinal)) return f.Substring(0, f.Length - 3);
            return null;
        }

        static bool IsFlat(string f)
        {
            return f.EndsWith("Min", StringComparison.Ordinal) || f.EndsWith("Max", StringComparison.Ordinal);
        }

        // Whether the record whose fields are being laid out is a relic. Set in `Fields` for
        // the duration of the sort, like `ItemViewFactory.RollJitter`. Why it is needed: the
        // long discussion at energy cost below.
        static bool artifact;

        static int Group(string f)
        {
            // CONVERSION COMES AFTER `defensive*` AND BEFORE `skill*`, as a group of its own.
            //
            // That it stands in the common stream at all and takes its own word was measured by
            // the tenth experiment (seed-test10): a ring has seven fields and the conversion
            // takes the eighth word; a medal with a prefix has two and it takes the third. Its
            // own roll rule is different, see [ItemRoll.ConvValue](ItemRoll.cs).
            //
            // But WHERE exactly it stands the tenth experiment could not show: neither the ring
            // nor the medal has any group-7 field, and "last" is indistinguishable from "before
            // skill*". A helmet of `_Amakata` (equip0) told them apart — the very item the notes
            // described as "the cooldown only finds itself on the eighth word out of seven
            // fields, ONE FIELD IS MISSING FROM THE SET". What was missing was the conversion:
            //     …defensive(6) → CONVERSION(7) → skill cooldown(8)
            // Both numbers settle at once — conversion 25 and cooldown 7, as in the game — and
            // the item becomes whole. Put the conversion AFTER `skill*` and both miss, and
            // `--ordertest` drops from 695/81 to 693/80.
            if (f.StartsWith("conversionPercentage", StringComparison.Ordinal)) return 6;
            if (f.StartsWith("character", StringComparison.Ordinal)) return 0;
            // ENERGY COST SITS IN TWO DIFFERENT PLACES, AND THAT IS MEASURED, NOT GUESSED.
            //
            // ON RELICS (`Class = ItemArtifact`) it comes RIGHT AFTER the attributes. Measured
            // on a relic of `_Assira` (equip11), where the arrangement is unique and all ten
            // fields are known:
            //   spirit(1) → total speed(2) → ENERGY COST(3) → fire(4) → cold(5)
            //   → lightning(6) → elemental(7) → burn(8) → frostburn(9) → electrocute(10).
            //
            // ON EVERY OTHER ITEM it is at the very tail, after everything. Measured 20 August
            // in `_Satirita`'s bags: the user put everything carrying this field there and
            // photographed the tooltips. It split exactly by record class:
            //
            //   relics — our old order is right: 8 copies, 4 records, every number to the unit,
            //     including the energy cost itself;
            //   everything else is shifted by exactly one word: 12 copies, 5 records
            //     (a sceptre ×4, a sword ×2, three off-hand books ×2 each, an amulet ×2).
            //
            // The shift shows most cleanly where neighbouring fields have THE SAME bracket: one
            // book has four consecutive fields at [134–201], and the number the game prints
            // second stood first for us, the third second, and so on. That is, the game's
            // damage block starts one word earlier: the word we spend on energy cost the game
            // spends on the first damage field.
            //
            // CHECKED FROM BOTH ENDS. With rank 7 the sceptre of `_Satirita` agrees in full
            // (aether 340, conversion 48, life 414, speed 22, energy −8 — all five) while the
            // relic falls apart (fire 181 against 200). With rank 1 it is the other way round.
            // One rank cannot satisfy both measurements.
            //
            // The skill cooldown (`skillCooldownReduction`) meanwhile always comes last — that
            // is measured too, and more than once. Two different fields with a common name stem
            // and different places.
            if (f == "skillManaCostReduction" || f == "skillManaCostReductionModifier")
                return artifact ? 1 : 7;
            // Crit damage no longer has a group of its own — it lives inside offensive, between
            // the flat damage and the percentages (DamageRank). Measured, see there.
            if (f.StartsWith("offensive", StringComparison.Ordinal)) return 3;
            if (f.StartsWith("retaliation", StringComparison.Ordinal)) return 4;
            if (f.StartsWith("defensive", StringComparison.Ordinal)) return 5;
            // 6 is taken by the conversion, see above; the tail (`skill*` and anything
            // unknown) is 7.
            return 7;
        }

        // The place inside the whole `offensive` group and inside retaliation, in blocks: flat
        // direct, flat dot, direct percentages, dot percentages, tail.
        static int DamageRank(string f, int prefixLen)
        {
            bool dot = f.Length > prefixLen + 4 &&
                       string.CompareOrdinal(f, prefixLen, "Slow", 0, 4) == 0;
            string rest = f.Substring(dot ? prefixLen + 4 : prefixLen);
            // CRIT DAMAGE SITS BETWEEN FLAT DAMAGE AND THE PERCENTAGES — measured 13 August on
            // a green medal in `_111`'s bag (seed 1934710279). It has exactly three rolled
            // fields and two of them were wrong while crit stood BEFORE the flat damage:
            //     flat life damage     game 9,  was 10, now 9
            //     crit damage          game 6,  was  5, now 6
            //     life damage percent  game 40, was 40
            // Before that, crit's place relative to FLAT damage was taken on faith: against the
            // percentages it had long been measured (before every type), and among the 83
            // legendaries no item had crit and flat damage side by side. All 83 stayed put
            // after the fix — 663 numbers, 80 items.
            //
            // THE OVERALL DAMAGE PERCENT COMES BEFORE CRIT. Measured 3 September on a mythical
            // relic of `_666` (seed 1249050575), and the measurement came of itself: the user's
            // "Pet bonuses" panel did not add up — damage 1269 against our 1262 and crit 71
            // against 69. Both gaps turned out to be this one item.
            //
            // Its pet bonus has exactly THREE rolled fields, and three words:
            //     attack ability  game 21   ours 21   ← the first word is right
            //     crit            game 11   ours  9
            //     total damage    game 83   ours 76
            // Swap those two and both agree. Crit's place relative to the TYPES was measured
            // long ago (13 August) and does not change; its place relative to the OVERALL
            // percent stood TAKEN ON FAITH — as the header of this file said — and the faith
            // turned out to be wrong.
            if (!dot && Index(Direct, rest) == 0 && !IsFlat(f)) return 298;
            if (f == "offensiveCritDamageModifier") return 299;
            int t = Index(Tail, rest);
            if (t >= 0) return 500 + t;
            int type = Index(dot ? Dot : Direct, rest);
            if (type < 0) type = 90;
            bool flat = IsFlat(f);
            return (dot ? (flat ? 200 : 400) : (flat ? 100 : 300)) + type;
        }

        static int Within(string f, int group)
        {
            switch (group)
            {
                case 0: { int i = Index(Character, f.Substring(9)); return i < 0 ? 90 : i; }
                case 3: return DamageRank(f, 9);           // "offensive".Length
                case 4: return DamageRank(f, 11);          // "retaliation".Length
                case 5: { int i = Index(Resist, f.Substring(9)); return i < 0 ? 90 : i; }
                default: return 99;
            }
        }

        // A record's fields in the order the game rolls them. Non-rolled and degenerate ones do
        // not get here: they take no rolls.
        public static List<string> Fields(DbRecord rec) { return Fields(rec, -1); }

        // `jitter` >= 0 is ANOTHER record's spread. Needed by the pet bonus: it has no
        // `lootRandomizerJitter` field of its own and is rolled with the spread of THE record
        // that named it (see ItemView.AppendPetBonus).
        public static List<string> Fields(DbRecord rec, int jitter)
        {
            List<string> list = new List<string>();
            if (rec == null) return list;
            // A COMPONENT IS NOT ROLLED AT ALL — neither inserted into an item nor lying loose.
            // For the inserted case this had been written down since 6 August; for the loose
            // case there was no measurement, and we were spending rolls on it because it has a
            // seed of its own. Measured 15 August by the twelfth experiment: two copies of the
            // same component with seeds 1 and 2 showed THE SAME — 120 life, 25 defensive
            // ability, −15% block recovery, 180 retaliation physical — and those are exactly
            // the record's numbers, with not one bracket in the tooltip.
            if (string.Equals(rec.Class, "ItemRelic", StringComparison.OrdinalIgnoreCase)) return list;
            // Whether a bracket is degenerate depends on the spread, and an affix has its own —
            // so the set of fields is computed with THIS record's spread.
            int keep = ItemViewFactory.RollJitter;
            ItemViewFactory.RollJitter = jitter >= 0 ? jitter : ItemViewFactory.JitterOf(rec);
            try {
            foreach (KeyValuePair<string, string> kv in rec.Fields)
            {
                double v;
                if (!double.TryParse((kv.Value ?? "").Split(';')[0], System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out v) || v == 0) continue;
                double lo, hi;
                // Base weapon damage has no bracket and takes no roll: the tooltip prints
                // "82-112 physical damage" with no range around it. On armour the same fields
                // are an ordinary bonus, and there they are rolled.
                if (rec.Class != null && rec.Class.StartsWith("Weapon", StringComparison.Ordinal) &&
                    (kv.Key == "offensivePhysicalMin" || kv.Key == "offensivePhysicalMax" ||
                     kv.Key.StartsWith("offensiveBase", StringComparison.Ordinal)))
                    continue;
                // THE CONVERSION IS HERE — since 15 August. Before that it was thrown out, and
                // here is why that was exactly half right. We twice tried letting it into the
                // common stream and ran ALL seven possible group places:
                //
                //     conversion's place   first  after character  …  after defensive  last
                //     items in full          52         52         …        54          54
                //     (with the conversion out of the stream — 80)
                //
                // No place worked. The error was not that the conversion is outside the stream
                // but that "last" meant "last in ITS OWN group" for us and was treated as the
                // general roll rule. In fact it takes the NEXT word after all the item's fields
                // (Group gives it 9) and is rolled by its own rule, ItemRoll.ConvValue.
                if (!ItemViewFactory.RollBounds(kv.Key, v, 0, out lo, out hi)) continue;
                if (Math.Abs(hi - lo) < 0.0001) continue;
                list.Add(kv.Key);
            }
            bool keepArt = artifact;
            artifact = rec != null && string.Equals(rec.Class, "ItemArtifact", StringComparison.Ordinal);
            try { list.Sort(Compare); } finally { artifact = keepArt; }
            return list;
            } finally { ItemViewFactory.RollJitter = keep; }
        }

        // The order of two fields relative to each other — one rule for everything, and the
        // walk over a WHOLE item with affixes takes the same one (RollWhole).
        public static int Compare(string a, string b)
        { bool g; return Compare(a, b, true, out g); }

        static int Compare(string a, string b, bool withDuration)
        { bool g; return Compare(a, b, withDuration, out g); }

        // `withDuration = false` is the same comparison with the rule "duration after its own
        // dot" TURNED OFF: two such fields are declared equal in rank. Needed by the walk over
        // an item with affixes, where the duration comes not after every part's dot but after
        // the dot of ITS OWN part — see RollWhole.
        //
        // `guessed` says WHETHER THE COMPARISON WAS SETTLED BY MEASUREMENT OR BY ALPHABET.
        // Every rule above is measured by name (and the comments say by what), while the very
        // last line — `string.CompareOrdinal` — is a pure guess from the name. That is what gets
        // flagged: it shows which numbers in a tooltip can be trusted and which cannot yet.
        // See Doubts below.
        static int Compare(string a, string b, bool withDuration, out bool guessed)
            {
                guessed = false;
                int ga = Group(a), gb = Group(b);
                if (ga != gb) return ga - gb;
                int wa = Within(a, ga), wb = Within(b, gb);
                if (wa != wb) return wa - wb;
                // AN ATTRIBUTE'S "MAX" COMES AFTER THE ATTRIBUTE ITSELF, not before it. The
                // alphabet gave the opposite ("Max" < "Mod"), and one pair of boots of `_Relo`
                // (equip4) stood on it — the last of the 83 items that did not fit in full:
                //     characterRunSpeedModifier      record 8, bracket [7,9]
                //     characterRunSpeedMaxModifier   record 3, bracket [2,4]
                //     words 2 and 3 give remainders 1 and 2
                //     our order (Max first):  3 and 9   game: 4 and 8
                //     the reverse:            8 and 4   game: 8 and 4   ← both at once
                // There is no third order here, so the relation is MEASURED: of the two
                // possible ones, one gives 2 numbers out of 2 and the other 0 out of 2. Across
                // all saves exactly one item carries both fields, so there is no further prop —
                // and that is written honestly rather than passed off as a broad measurement.
                // The comparison is exact: the name with "Max" must match the name without it,
                // otherwise the rule is not about this pair and does not apply.
                if (ga == 0)
                {
                    int ia = a.IndexOf("Max", StringComparison.Ordinal);
                    int ib = b.IndexOf("Max", StringComparison.Ordinal);
                    if (ia >= 0 && ib < 0 && a.Remove(ia, 3) == b) return 1;
                    if (ib >= 0 && ia < 0 && b.Remove(ib, 3) == a) return -1;
                }
                // THE ATTRIBUTE ITSELF COMES BEFORE ITS PERCENT: `X` before `XModifier`. The
                // alphabet gives that order anyway (the short name is the start of the long
                // one), so the rule CHANGES NOTHING in the calculation. It stands here for the
                // honesty of the flag: without it the comparison was settled by the last
                // resort, and `--ordertrust` counted these pairs as guessed although they are
                // measured. `--orderfit` knows them by name:
                //     characterLifeRegen < characterLifeRegenModifier              x4
                //     characterDefensiveAbility < characterDefensiveAbilityModifier x2
                //     characterOffensiveAbility < characterOffensiveAbilityModifier x1
                // Not one relation runs the other way. To other pairs of the same shape (for
                // example `characterLife` / `characterLifeModifier`) the rule is extended BY
                // GENERALISATION — they have no measurement of their own, but no contradiction
                // either: `--orderfit` holds 847 relations and zero disagreements.
                if (b.Length == a.Length + 8 && b.StartsWith(a, StringComparison.Ordinal) &&
                    string.CompareOrdinal(b, a.Length, "Modifier", 0, 8) == 0) return -1;
                if (a.Length == b.Length + 8 && a.StartsWith(b, StringComparison.Ordinal) &&
                    string.CompareOrdinal(a, b.Length, "Modifier", 0, 8) == 0) return 1;
                // SKILL COOLDOWN COMES BEFORE ENERGY COST. It agrees with the alphabet and
                // stands here for the flag: until 20 August these two fields never met in one
                // group at all (energy cost was rank 1), and as soon as the cost moved to the
                // tail the pair met the cooldown, and `--ordertrust` fairly called their order
                // guessed.
                //
                // MEASURED 20 AUGUST ON TWELVE ITEMS IN `_Satirita`'s BAGS — twenty-four
                // numbers, all agreeing:
                //   three off-hand books ×2 each   14/9 and 14/9, 13/9 and 17/8, 16/7 and 18/7
                //   another book ×2                19/7 and 13/9
                //   an amulet ×2                    4/7 and  4/8
                //   a sword and its mythical twin  11/9 and 21/7
                if (a == "skillCooldownReduction" && b == "skillManaCostReduction") return -1;
                if (a == "skillManaCostReduction" && b == "skillCooldownReduction") return 1;
                // THE FIRST CONVERSION COMES BEFORE THE SECOND. Also agrees with the alphabet
                // and also stands for the flag. Measured twice: `--orderfit` gives
                // `conversionPercentage < conversionPercentage2` on worn items, and the
                // eleventh experiment of 15 August used a mace with TWO conversions of 10 each
                // and chosen seeds: the game gave 11/12 with seed 1 and 9/11 with seed 2, where
                // the reverse order would give 12/11 and 11/9.
                if (b == a + "2") return -1;
                if (a == b + "2") return 1;
                // The low end of a pair comes before the high one — measured on `_Relo` equip11.
                // The alphabet would give the opposite: Max < Min.
                bool fa = IsFlat(a), fb = IsFlat(b);
                if (fa && fb && a.Length == b.Length)
                {
                    bool amin = a.EndsWith("Min", StringComparison.Ordinal);
                    bool bmin = b.EndsWith("Min", StringComparison.Ordinal);
                    if (amin != bmin) return amin ? -1 : 1;
                }
                // A dot's duration comes RIGHT AFTER the dot itself, not as a separate group:
                // on one off-hand fire(1) → burn(2) → burn duration(3), on a medal
                // bleeding(3) → its duration(4). The alphabet would give the opposite.
                // The same on defence: on one pair of legs the reduced electrocute duration
                // settled AFTER the resistances — run(1), physical(2), bleeding(3),
                // duration(4). One rule for both groups.
                bool da = a.IndexOf("Duration", StringComparison.Ordinal) >= 0;
                bool db = b.IndexOf("Duration", StringComparison.Ordinal) >= 0;
                if (da != db) return withDuration ? (da ? 1 : -1) : 0;
                // Beyond this there is no measurement — only the name. Identical fields (a == b)
                // arrive here too, but there is nothing to guess there: they are equal.
                guessed = !string.Equals(a, b, StringComparison.Ordinal);
                return string.CompareOrdinal(a, b);
            }

        // ===================== WHAT CAN BE TRUSTED HERE =====================
        //
        // The order is built from measurements, but NOT ALL of it: where there is no
        // measurement, fields are laid out by name. That is an honest guess, and it must be
        // showable — otherwise the editor's tooltip passes guesswork off as computation.
        //
        // There are exactly two kinds of doubtful place, and both are visible from the
        // comparison itself:
        //   1) THE FIELD'S TYPE IS NOT IN OUR TABLES. Then Within returns the spare 90 and the
        //      field lands somewhere at the end of its group. Caught by RankKnown;
        //   2) TWO NEIGHBOURS WERE LAID OUT BY ALPHABET rather than by a rule. Caught by the
        //      `guessed` flag from Compare.
        // Everything above the alphabet is measured by name, and the comments say by what.
        public static bool RankKnown(string f)
        {
            switch (Group(f))
            {
                case 0: return Index(Character, f.Substring(9)) >= 0;
                case 3: return DamageTypeKnown(f, 9);
                case 4: return DamageTypeKnown(f, 11);
                case 5: return Index(Resist, f.Substring(9)) >= 0;
                // 1 (energy cost), 6 (conversion) and 7 (the tail) are whole groups with no
                // place INSIDE them, so there is nothing to get wrong.
                default: return true;
            }
        }

        static bool DamageTypeKnown(string f, int prefixLen)
        {
            if (f == "offensiveCritDamageModifier") return true;
            bool dot = f.Length > prefixLen + 4 &&
                       string.CompareOrdinal(f, prefixLen, "Slow", 0, 4) == 0;
            string rest = f.Substring(dot ? prefixLen + 4 : prefixLen);
            if (Index(Tail, rest) >= 0) return true;
            return Index(dot ? Dot : Direct, rest) >= 0;
        }

        // The item's fields whose PLACE IN THE STREAM is guessed rather than measured. An empty
        // list means this item's numbers are computed by rules in full, and a question mark in
        // the tooltip has nowhere to come from.
        //
        // Important: doubt is contagious FORWARD. If two fields cannot be told apart, not only
        // their order relative to each other may be wrong but everything after them — the rolls
        // run back to back. So the tail is part of the answer.
        // THE CAUSE and THE TAIL must be kept apart. The cause is one pair of fields or one
        // unknown field and is fixed by a single measurement; the tail is everything that
        // shifted behind it and fixes itself. Putting them in one list gives a report in which
        // it is unclear what to measure.
        public class Doubt
        {
            public int At = -1;          // the first doubt's place, from zero
            public string Cause;         // the field that causes it
            public string Why;           // wording ready to print as it is
            public List<string> Tail = new List<string>();   // the cause and everything after
            public bool Any { get { return At >= 0; } }
        }

        public static Doubt DoubtOf(List<Rolled> all)
        {
            Doubt d = new Doubt();
            if (all == null || all.Count == 0) return d;
            // ONE ROLLED FIELD — nothing to doubt: there is nothing to swap it with, and the
            // order does not enter the answer at all. It was on exactly such items that the
            // roll itself was worked out. An unknown type is harmless here too.
            if (all.Count == 1) return d;
            for (int i = 0; i < all.Count; i++)
            {
                // An unknown type is dangerous ONLY if the field shares its group with someone
                // else: inside a group it can only move relative to neighbours, while the
                // groups themselves are ordered as wholes and measured. A field alone in its
                // group stands unambiguously, however unknown its type. That is how the doubt
                // was lifted from `retaliationFearMin` on one item: its retaliation damage is
                // the only one on the whole item, and the item fits in full.
                if (!RankKnown(all[i].Field) && Alone(all, i))
                {
                    d.At = i; d.Cause = all[i].Field;
                    d.Why = "Field type is missing from the tables, so it was placed at the end of its group";
                    break;
                }
                if (i + 1 >= all.Count) continue;
                bool g;
                Compare(all[i].Field, all[i + 1].Field, true, out g);
                // The alphabet between TWO DIFFERENT fields is a guess. The same field coming
                // from different parts of the item does not get here: their order is measured
                // (the base is last for `character*`, first for the rest).
                if (g)
                {
                    d.At = i; d.Cause = all[i].Field + " / " + all[i + 1].Field;
                    d.Why = "Field order for this pair has not been measured, so the fields were sorted alphabetically";
                    break;
                }
            }
            if (!d.Any) return d;
            // Doubt is contagious FORWARD: the rolls run back to back, so if a pair cannot be
            // told apart, everything after it may be wrong.
            for (int i = d.At; i < all.Count; i++)
                if (!d.Tail.Contains(all[i].Field)) d.Tail.Add(all[i].Field);
            return d;
        }

        // Whether two fields share a group — needed by the search for items to measure
        // ([FieldMates.cs](FieldMates.cs)): only a neighbour from ITS group can take a word
        // away from a field, the rest stand entirely before or after it.
        public static bool SameGroup(string a, string b) { return Group(a) == Group(b); }

        // Whether a field has neighbours in its group — that is, whether its place can shift at
        // all. `true` here means "a neighbour EXISTS"; the name reads the other way round:
        // alone is about solitude, and we are asking whether it is NOT alone.
        static bool Alone(List<Rolled> all, int at)
        {
            for (int i = 0; i < all.Count; i++)
                if (i != at && SameGroup(all[i].Field, all[at].Field)) return true;
            return false;
        }

        // A short entry point for display: which of the item's fields are computed unreliably.
        public static List<string> Doubts(List<Rolled> all) { return DoubtOf(all).Tail; }

        // The same for ONE record with a stream of its own — the pet bonus: it is not part of
        // the item's common stream but separate, from the same seed (measured, MECHANICS 14a).
        public static List<string> Doubts(DbRecord rec)
        {
            List<Rolled> all = new List<Rolled>();
            if (rec == null) return Doubts(all);
            int jitter = ItemViewFactory.JitterOf(rec);
            foreach (string f in Fields(rec))
            {
                Rolled r = new Rolled();
                r.Field = f; r.Jitter = jitter; r.Value = Num(rec, f);
                all.Add(r);
            }
            return Doubts(all);
        }

        // One field of a whole item: whose it is (base / prefix / suffix), what the record says,
        // what that record's spread is and what was rolled.
        public class Rolled
        {
            public int Part;            // 0 base, 1 prefix, 2 suffix
            public string Field;
            public double Value;
            public int Jitter;
            public long Got;      // as shown: the multiplier is already applied
            public long Raw;      // before the multiplier — the game adds parts with this
            // THE NUMBER BEFORE ROUNDING. For every field it equals Got: the roll is integral
            // by construction (`low + x mod size`), there is nothing to round. For a CONVERSION
            // the last step is a round, and before it the value is fractional. The game SHOWS
            // the rounded one (measured on 24 numbers); whether it computes with the rounded or
            // the fractional one a tooltip cannot say, and only a damage comparison checks that.
            public double Exact;
            // THE HIGH END OF A PAIR, computed from the already rolled low end (PairMax). For
            // such fields `Raw` equals `Got` — both in game numbers, the multiplier is not
            // applied to the gap at all (measured 15 August, ItemRoll.PairMax). So they must
            // not enter the sum's correction: it multiplies RAW rolls and would multiply such a
            // number a second time. See RollPlan for the details.
            public bool PairTop;
        }

        // WALKING A WHOLE ITEM WITH AFFIXES — ONE STREAM FOR EVERYTHING.
        //
        // Until 13 August we held that every part of an item has its own stream from the same
        // seed (that is also what `GDItem.applyRNG` in GD Stash does: a new generator per field
        // list). **That is wrong.** The seed-test9 experiment — ten medals with seeds 1 and 2,
        // the base and each affix carrying exactly one rolled field — reads unambiguously only
        // one way:
        //
        //     the fields of the BASE, the PREFIX and the SUFFIX go into ONE list,
        //     are walked in the same order as the fields of a single record,
        //     and take rolls back to back from ONE stream off the item's seed.
        //
        // Everything that used to look like a riddle follows from this:
        //   bare medal              base(1)                          47 / 50 ✔
        //   + attack suffix         attack ability(1) → base(2)      75/51 and 79/47 ✔
        //   + fire-resist prefix    base(1) → fire resist(2)         50/94 and 47/93 ✔
        //   + both                  attack(1) → base(2) → fire(3)    ✔ on both seeds
        //   + cold prefix           cold(1) → base(2)                ✔ on both seeds
        // The order here is not "base, then affixes" but EXACTLY OUR FIELD ORDER: attack
        // ability is `character*` and comes first; fire resistance is `defensive*` and comes
        // last; flat cold comes before a percent.
        //
        // The same rule explained the rare item we were stuck on: the suffix's attack speed(1)
        // → the base's cast speed(2) → the suffix's flat cold(3) → the prefix's acid(4) → its
        // width(5). In the game 14, 6, 21, 26 and a width of 12 — all five agree.
        //
        // EVERY FIELD HAS ITS OWN SPREAD, from ITS record (`lootRandomizerJitter` on an affix,
        // 20% on a base). The `attributeScalePercent` multiplier is taken from the BASE and
        // applies to any part's damage: on the medal the prefix's cold is printed by the game
        // as [16-19], i.e. [12,14] × 1.4, not [12-14].
        //
        // WHAT IS NOT MEASURED HERE: the order of TWO IDENTICAL fields from different parts (on
        // one dagger both the base and the suffix give `offensiveColdModifier`). At present the
        // base goes first. Read that way the dagger's cold percent agrees (310 in the game)
        // while the frostburn and its duration do not, so the rule there is a different one and
        // still has to be measured.
        public static List<Rolled> RollWhole(GameData game, Item item, DbRecord baseRec)
        {
            List<Rolled> all = WholePlan(game, item, baseRec);
            RollPlan(all, item == null ? 0u : item.Seed,
                     baseRec == null ? 0 : baseRec.GetInt("attributeScalePercent"));
            return all;
        }

        // THE WALK WITHOUT THE ARITHMETIC — who follows whom in the stream, but without numbers
        // yet.
        //
        // Split out on 16 August for the SEED SEARCH ([SeedSearch.cs](SeedSearch.cs)): the
        // search tries seeds, and the field order never changes while it does, whereas the
        // numbers must be recomputed for every seed. The arrangement stayed exactly the same —
        // not one line of the comparison was rewritten here, only moved; so the search computes
        // with THE SAME code as the tooltip, not with a copy of its own.
        public static List<Rolled> WholePlan(GameData game, Item item, DbRecord baseRec)
        {
            List<Rolled> all = new List<Rolled>();
            if (item == null || baseRec == null) return all;
            // THE FOURTH PART — THE "EDIT" (`ModifierName`), THE CRAFTING AFFIX OF AWAKENING.
            // It was absent here altogether, and because of that EVERY number of an awakened
            // item shifted: its field takes a word from the common stream and moves everything
            // that comes after it.
            //
            // WHAT IT IS: an awakened item is a CRAFT from a mythical one, and the craft hangs
            // on a `LootRandomizer` from `records/items/lootaffixes/crafting/` with a rolled
            // bonus of its own. An item "awakened" by our editor gets no such affix — which
            // separates the two cases the rule was measured on.
            //
            // MEASURED 1 September on `_Acid Vines`, THREE COPIES OF ONE RECORD (24 game
            // numbers):
            //
            //   no edit                   ONE arrangement, and it fits in full
            //   edit "+defensive ability" ZERO arrangements while its word is missing
            //   edit "+life"              ZERO arrangements while its word is missing
            //
            // AND IT IS NOT INSERTED AT AN EDGE BUT BY THE COMMON FIELD ORDER — exactly what
            // the sort below already does. Three records say so in three voices, and the edit's
            // place differs every time:
            //
            //   a ring   edit "defensive ability" → word 1  (before attack speed)
            //   a robe   the same edit            → word 3  (after life and attack ability)
            //   a mace   the same edit            → word 2  (after life)
            //
            // Read this way, ALL numbers of all six items fall into place; read as "append at
            // the end" or "put first", not one item fits.
            string[] parts = { item.BaseName, item.PrefixName, item.SuffixName, item.ModifierName };
            for (int p = 0; p < parts.Length; p++)
            {
                if (string.IsNullOrEmpty(parts[p])) continue;
                DbRecord rec = p == 0 ? baseRec : (game == null ? null : game.Record(parts[p]));
                if (rec == null) continue;
                int jitter = ItemViewFactory.JitterOf(rec);
                foreach (string f in Fields(rec))
                {
                    Rolled r = new Rolled();
                    r.Part = p; r.Field = f; r.Value = Num(rec, f); r.Jitter = jitter;
                    all.Add(r);
                }
            }
            // The relic marker is needed here too: `Fields` sets it for its own sort, while
            // this pass lays out ALL the parts afresh and calls `Compare` itself.
            bool keepArt2 = artifact;
            artifact = string.Equals(baseRec.Class, "ItemArtifact", StringComparison.Ordinal);
            try {
            all.Sort(delegate (Rolled a, Rolled b)
            {
                // A DOT'S DURATION FOLLOWS ITS OWN PART'S DOT, NOT ALL THREE. So the first
                // comparison is made WITHOUT the duration rule: a dot and its duration are
                // declared equal in rank and the PART decides.
                // Measured on a blade of `_Amakata` (bag5[3], seed 600625480). All three parts
                // give it electrocute while only the base gives the duration, and words 9–12
                // hold exactly these four fields (the thirteenth is taken by the conversion).
                // Of ALL 24 arrangements, EXACTLY ONE gives the game's 401% electrocute:
                //     base dot(9) → base DURATION(10) → prefix dot(11) → suffix dot(12)
                // The old order (all three dots, then the duration) gave 420.
                // ONE PART'S "MIN–MAX" PAIR IS NOT SPLIT BY ANOTHER PART.
                //
                // Measured 17 August on a blade of `_Amakata` (bag 4).
                // `offensiveBonusPhysical` comes from the prefix as a pair (39/77) and from the
                // suffix as a lone low end (240). The game prints "72-109 physical damage" and
                // "10% chance of 466":
                //
                //     word         4      5      6
                //     prefix Min   72     68     68      ← game 72
                //     prefix Max   111    109    109     ← game 109
                //     suffix Min   388    428    466     ← game 466
                //
                // The old order (all the low ends, then all the high ones) gave the prefix 4
                // and 6 and the suffix 5 — and the suffix came out 428. The only arrangement
                // giving all three game numbers at once: prefix Min(4) → prefix Max(5) →
                // suffix Min(6). That is, the pair runs consecutively and the other part
                // follows.
                //
                // The same reading closes a weapon of `_Mivan` that had been the single item
                // not fitting without a mark since 15 August: there both parts give
                // `offensivePierceMin` while only the prefix gives `Max`, and the game prints
                // 10-14 against our 11-14. It contradicted the old rules; it does not
                // contradict this one.
                if (a.Part != b.Part && SamePair(a.Field, b.Field)) return a.Part - b.Part;
                int c = Compare(a.Field, b.Field, false);
                if (c != 0) return c;
                // THE SAME FIELD FROM DIFFERENT PARTS. The order here depends on the GROUP, and
                // this is no guess but the only reading explaining both items:
                //   in `character*` the base goes AFTER the affixes — a medal of `_Amakata`
                //     (bag5[1]): three parts give `characterOffensiveAbility` 52/34/34, the
                //     game showed 116, and of the six layouts only those with the base last
                //     give 116;
                //   in the other groups the base is FIRST — the same medal on acid (159) and
                //     poison (177), and the seed-test9 experiment on pierce resistance: a
                //     cuirass 26 + prefix 49 gave 70 and 72, where prefix-first would give
                //     67 and 77.
                // Among the affixes the prefix comes before the suffix — the same medal's
                // spirit (40 + 22 = 62, where the reverse order would give 64).
                if (a.Field.StartsWith("character", StringComparison.Ordinal))
                {
                    bool ab = a.Part == 0, bb = b.Part == 0;
                    if (ab != bb) return ab ? 1 : -1;
                }
                if (a.Part != b.Part) return a.Part - b.Part;
                // Same part — here the duration again comes after its own dot.
                return Compare(a.Field, b.Field, true);
            });
            } finally { artifact = keepArt2; }
            return all;
        }

        // THE ARITHMETIC OVER A READY ARRANGEMENT. The i-th field's word is a^(i+2)·seed, and
        // this is the only place the seed takes part at all: the arrangement itself does not
        // depend on it. The seed search uses exactly that — it builds the plan once and
        // computes for every seed.
        public static void RollPlan(List<Rolled> all, uint seed, int scale)
        {
            if (all == null || all.Count == 0) return;
            int keep = ItemViewFactory.RollJitter;
            try
            {
                // THE WORDS ARE HANDED OUT SEPARATELY FROM THE ARITHMETIC, and that is
                // necessity, not elegance. A pair's high end is computed from the ALREADY
                // ROLLED low one, yet its word may come EARLIER than the low one's (measured
                // 15 August, see Compare). While the arithmetic ran in one pass in order, such
                // a case produced zero instead of the low end. Now: first a word for every
                // field, then all the single ones, and only then the high ends of pairs.
                long word = ItemRoll.Seed(seed);
                long[] words = new long[all.Count];
                for (int i = 0; i < all.Count; i++) { word = ItemRoll.Next(word); words[i] = word; }

                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].Field.EndsWith("Max", StringComparison.Ordinal)) continue;
                    ItemViewFactory.RollJitter = all[i].Jitter;
                    all[i].Raw = ItemRoll.Raw(all[i].Field, all[i].Value, words[i]);
                    all[i].Got = ItemRoll.Value(all[i].Field, all[i].Value, scale, words[i]);
                    all[i].Exact = ExactRolls.ExactScale || ItemRoll.IsConversion(all[i].Field)
                                 ? ItemRoll.ValueExact(all[i].Field, all[i].Value, scale, words[i])
                                 : all[i].Got;
                }
                // A "Max" WITHOUT ITS "Min" is an ordinary single field and is computed like
                // all the rest. It is kept apart from real pair tops because those need an
                // already CORRECTED low end (see below), while this one is entitled to the
                // correction like everything else.
                for (int i = 0; i < all.Count; i++)
                {
                    if (!all[i].Field.EndsWith("Max", StringComparison.Ordinal)) continue;
                    if (PairLow(all, i) != null) { all[i].PairTop = true; continue; }
                    ItemViewFactory.RollJitter = all[i].Jitter;
                    all[i].Raw = ItemRoll.Raw(all[i].Field, all[i].Value, words[i]);
                    all[i].Got = ItemRoll.Value(all[i].Field, all[i].Value, scale, words[i]);
                    all[i].PairTop = false;
                }

                // THE GAME MULTIPLIES THE SUM, NOT THE PARTS. When several parts of an item
                // give the same field, the tooltip prints one line, and in it
                // `floor((j₁ + j₂ + …) × k)` rather than the sum of separately multiplied
                // values. The difference is exactly one unit, but it is there: a chest piece
                // with a suffix carrying the same field gave 152 in the game where the parts
                // separately give 151. The per-part numbers are left as they are — the game
                // prints them nowhere — and the remainder is put on the last part so that the
                // SUM agrees.
                if (scale != 0)
                {
                    Dictionary<string, long> rawSum = new Dictionary<string, long>(StringComparer.Ordinal);
                    Dictionary<string, long> gotSum = new Dictionary<string, long>(StringComparer.Ordinal);
                    Dictionary<string, int> count = new Dictionary<string, int>(StringComparer.Ordinal);
                    Dictionary<string, int> last = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (int i = 0; i < all.Count; i++)
                    {
                        // A PAIR'S HIGH END DOES NOT ENTER THE CORRECTION — and that is not a
                        // matter of taste but the fix of a real bug (17 August).
                        //
                        // The correction exists because the game multiplies the SUM of the raw
                        // rolls by `attributeScalePercent`, not each one separately. A pair's
                        // high end has no raw roll: it is computed as "the rolled low end plus
                        // the gap", and the multiplier is not applied to the gap at all
                        // (measured 15 August: 50–70 → 106–127 with a multiplier of 80, the gap
                        // of 21 NOT multiplied). Its `Raw` therefore equals `Got` — already in
                        // game numbers — and the correction multiplied such a number A SECOND
                        // TIME.
                        //
                        // It showed on a blade of `_Amakata` (bag 4): chaos comes from both the
                        // prefix and the suffix, the item's multiplier is 45, and the suffix
                        // was getting 65 although it never exceeds 38 FOR ANY seed. A number
                        // outside its own bracket is not a "disputable reading" but an
                        // impossible value, so the fix is no fitting: the old behaviour refuted
                        // itself. The item editor's check (`--edittest`) found it when it was
                        // widened from sixty items to all 822.
                        if (all[i].PairTop) continue;
                        string f = all[i].Field;
                        if (!count.ContainsKey(f)) { count[f] = 0; rawSum[f] = 0; gotSum[f] = 0; }
                        count[f]++; rawSum[f] += all[i].Raw; gotSum[f] += all[i].Got; last[f] = i;
                    }
                    foreach (KeyValuePair<string, int> kv in count)
                    {
                        if (kv.Value < 2) continue;
                        long want = ItemRoll.Scale(kv.Key, rawSum[kv.Key], scale);
                        long have = gotSum[kv.Key];
                        if (want == have) continue;
                        // …AND THE CORRECTION MUST REACH `Exact`, BECAUSE THAT IS WHAT THE
                        // DAMAGE MODEL COUNTS BY.
                        //
                        // `ExactRolls.ReplaceWithSeed` feeds `Exact` into the sum (`UseExact`
                        // is on by default), while the tooltip shows `Got`. Moving only `Got`
                        // left the editor with TWO answers for one roll: the item's tooltip
                        // agreed with the game and the stats panel did not. The same slip was
                        // already fixed once for a pair's high end (`Exact = Got` below,
                        // 20 August) — this is the second place of the same kind.
                        //
                        // MEASURED on `_FenrisWerwolf` (20 October, fourteen item tooltips).
                        // Three items carry cold on the base, the prefix AND the suffix, and
                        // on all three the game's own merged line was above our sum:
                        //     Icy yeti horn   cold  278 → 280   frostburn  298 → 299
                        //     Frosted ring    cold  188 → 189   frostburn  167 → 168
                        //     Chilling ring   cold  188 → 189   frostburn  174 → 175
                        // Those remainders are exactly the +4% cold and +3% frostburn the
                        // character's panel was short — and with them fourteen numbers of
                        // `stand-fenris.tsv` land on the game to the unit.
                        //
                        // The delta is added rather than assigned, so a conversion keeps its
                        // fractional `Exact` (MECHANICS 14z) instead of being flattened.
                        long delta = want - have;
                        all[last[kv.Key]].Got += delta;
                        all[last[kv.Key]].Exact += delta;
                    }
                }

                // THE HIGH ENDS OF PAIRS COME LAST, OFF THE ALREADY CORRECTED LOW END.
                //
                // A high end is computed as "the rolled low end plus the gap", and the
                // correction above may have moved the low end. While the top was computed
                // BEFORE the correction, one and the same line had a corrected bottom and an
                // uncorrected top, and the bracket came out one unit narrower than its gap.
                //
                // Measured on a blade of `_Amakata` (bag 4, screenshot of 17 August): chaos
                // comes from both parts, the item's multiplier is 45, the raw rolls are prefix
                // 11 with a gap of 29 and suffix 3 with a gap of 26.
                //   low   floor((11 + 3) × 1.45) = 20   ← the game prints 20
                //   high  20 + (29 + 26)         = 75   ← the game prints 75
                // Separately multiplied lows give 15 + 4 = 19, and the top off them came out
                // 44 + 30 = 74 — the very unit that did not add up. The correction put the
                // missing unit on the suffix (4 → 5) while its top stayed computed off the
                // four, and the line read "5-30" with a rolled gap of 26. That is, it
                // contradicted itself, not only the game.
                //
                // The gap is not multiplied at all — measured 15 August (50–70 → 106–127 with a
                // multiplier of 80, the gap of 21 not multiplied) — so the high ends still stay
                // out of the correction: they have no raw roll of their own.
                for (int i = 0; i < all.Count; i++)
                {
                    if (!all[i].PairTop) continue;
                    Rolled low = PairLow(all, i);
                    if (low == null) continue;
                    ItemViewFactory.RollJitter = all[i].Jitter;
                    all[i].Got = ItemRoll.PairMax(low.Value, all[i].Value, low.Got, words[i]);
                    all[i].Raw = all[i].Got;   // the high end is already in game numbers
                    // It needs an `Exact` too. A high end has no FRACTIONAL roll of its own —
                    // it is computed from the already rolled low end — so the exact value
                    // equals the shown one.
                    //
                    // WITHOUT THIS LINE THE WHOLE HIGH END OF FLAT ITEM DAMAGE WAS LOST.
                    // `ExactRolls.ReplaceWithSeed` feeds `Exact` into the model (under
                    // `UseExact`, which is on by default); for pairs it stayed zero, and
                    // `CharacterTotals.Add` skips zeros — so the field vanished from the sum
                    // altogether. Found 20 August on `_Satirita`: an amulet reading "15-22
                    // aether damage" contributed only 15, and the panel's high end was short
                    // by exactly seven units of base.
                    all[i].Exact = all[i].Got;
                }
            }
            finally { ItemViewFactory.RollJitter = keep; }

        }

        // The low end of the same pair on the same part of the item, or null when this "Max"
        // has no "Min" of its own at all.
        static Rolled PairLow(List<Rolled> all, int top)
        {
            string min = all[top].Field.Substring(0, all[top].Field.Length - 3) + "Min";
            foreach (Rolled o in all)
                if (o.Part == all[top].Part && o.Field == min) return o;
            return null;
        }

        static double Num(DbRecord rec, string field)
        {
            double v;
            double.TryParse((rec.Get(field) ?? "").Split(';')[0], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out v);
            return v;
        }

        // A whole record's rolled numbers from the item's seed. Each part of the item has its
        // own stream, starting afresh from the same seed — measured on the pet bonus.
        public static Dictionary<string, long> RollAll(DbRecord rec, uint seed, int scale)
        { return RollAll(rec, seed, scale, -1); }

        public static Dictionary<string, long> RollAll(DbRecord rec, uint seed, int scale, int jitter)
        {
            Dictionary<string, long> got = new Dictionary<string, long>(StringComparer.Ordinal);
            if (rec == null) return got;
            // The spread is taken from THE SAME record as the fields: 20% on a base, its own
            // `lootRandomizerJitter` on an affix. Set for the duration of the arithmetic and
            // restored, so a call from the tooltip does not disturb a neighbouring part.
            int keep = ItemViewFactory.RollJitter;
            ItemViewFactory.RollJitter = jitter >= 0 ? jitter : ItemViewFactory.JitterOf(rec);
            try {
            long word = ItemRoll.Seed(seed);
            foreach (string f in Fields(rec, jitter))
            {
                word = ItemRoll.Next(word);
                double v = Num(rec, f);
                // A pair's high end is computed from the already rolled low one: the game rolls
                // the gap, not the high end itself (ItemRoll.PairMax).
                string min = f.EndsWith("Max", StringComparison.Ordinal)
                             ? f.Substring(0, f.Length - 3) + "Min" : null;
                long lower;
                if (min != null && got.TryGetValue(min, out lower))
                    got[f] = ItemRoll.PairMax(Num(rec, min), v, lower, word);
                else
                    got[f] = ItemRoll.Value(f, v, scale, word);
            }
            return got;
            } finally { ItemViewFactory.RollJitter = keep; }
        }
    }
}
