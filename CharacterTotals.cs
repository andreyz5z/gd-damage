// Everything the character's bonuses add up to, in one place.
//
// This is the first half of the damage model: gather every field from every
// source the character carries, and print it with the same stat table that
// describes an item. The second half — conversion, resistances of the target,
// chance to hit — comes later; see MECHANICS.md.
//
// Two things make this more than a sum.
//
// Some bonuses do not stack. "24 reduced resistances" from two sources is not 48: only the
// strongest is in effect. Those fields are collected by maximum, not by sum, and the form says so —
// adding them would quietly inflate every number downstream. The list is in MECHANICS, section 7.
//
// And the sum is only honest about what went into it, so every field remembers
// how many sources contributed and which was the largest.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Gdc.Gui
{
    public class TotalsEntry
    {
        public double Value;
        // The same sum if every item had rolled its worst, and if every item had
        // rolled its best. The game's character sheet adds up what actually
        // rolled, and that number is not in any file — but it has to lie between
        // these two. That makes the sum checkable against the game without
        // knowing a single roll.
        public double Low, High;
        public int Sources;
        public double Largest;
        public bool ByMax;      // taken as "the strongest wins", not summed
        // Every term, with the name of what it came from. Without these a summed number cannot be
        // checked against the game: "+1437% physical damage" says nothing until the eight things it
        // was made of can be seen.
        public readonly List<KeyValuePair<string, double>> Terms =
            new List<KeyValuePair<string, double>>();

        // A TERM'S CHANCE SITS BESIDE IT, UNDER THE SAME INDEX (MECHANICS 89b). The sum adds a
        // chance-based field by its AVERAGE (`Terms` are already multiplied by the chance) — that
        // is how the game's panel prints it, and it is checked. A strike needs the other thing:
        // whether it landed. So a term keeps its chance (0 = not chance-based) and a roll key: for
        // fields sharing one chance of a record (`…Global 1`) the key is one per record, for the
        // rest one per field, and the low and high ends of a span are rolled by one die. Filled in
        // one place, next to `Terms` (`CharacterTotals.Add`).
        public readonly List<double> TermChance = new List<double>();
        public readonly List<string> TermRoll = new List<string>();

        public bool HasChance
        {
            get { foreach (double c in TermChance) if (c > 0) return true; return false; }
        }

        // Term `i` for this strike: not chance-based — as it is; chance-based — in full if it
        // landed, else zero. With no roll (`landed == null`) the average, as in the panel.
        public double TermOn(int i, Func<string, double, bool> landed)
        {
            double v = Terms[i].Value;
            if (landed == null || i >= TermChance.Count || TermChance[i] <= 0) return v;
            return landed(TermRoll[i], TermChance[i]) ? v * 100 / TermChance[i] : 0;
        }

        // The sum for this strike — added or taken by the strongest, exactly as the sum itself is.
        public double ValueOn(Func<string, double, bool> landed)
        {
            if (landed == null || !HasChance) return Value;
            double sum = 0, best = 0;
            for (int i = 0; i < Terms.Count; i++)
            {
                double v = TermOn(i, landed);
                sum += v;
                if (Math.Abs(v) > Math.Abs(best)) best = v;
            }
            return ByMax ? best : sum;
        }
    }

    public class CharacterTotals
    {
        public readonly Dictionary<string, TotalsEntry> Fields =
            new Dictionary<string, TotalsEntry>(StringComparer.Ordinal);

        // THE BONUS AGAINST A RACE LIVES APART FROM EVERY OTHER PERCENTAGE, for two reasons, both
        // from MECHANICS 7b:
        //
        //   1. it is NOT a term of the common sum but a MULTIPLIER of its own. The game's order is
        //      `... × (1 + % of type + % from the attribute) × (1 + % against the race)`, so 1500%
        //      of a type and 50% against a race give ×16 × ×1.5, not 1550%;
        //   2. without the race's name it means nothing at all — "+12% damage" is not a number
        //      until it is said to whom.
        //
        // The key is the game's race code (`Race004`), the value a pair [percent, flat]. Whether
        // TWO items against one race add up is not visible in the files (the family has no
        // exclusivity flag — the same blindness as with resistance reduction, MECHANICS 69a); we
        // take the sum, as with ordinary percentages.
        public readonly Dictionary<string, double[]> Racial =
            new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);

        // Records with an "one of" (XOR) group on dots — a strike rolls the group anew
        // (`XorSource`).
        public readonly List<XorSource> XorSources = new List<XorSource>();

        public int WornItems, PassiveSkills, DevotionStars;

        // The eight parts of an item in the order they add up, and the label of each. The item's
        // name plus this label makes the SOURCE NAME under which a term sits in
        // `TotalsEntry.Terms` — and by that same name a strike's roll finds its row (`HitRoll`,
        // MECHANICS 31). So the name must be assembled THE SAME WAY in both places, hence one
        // shared array. The words come from `lang/<CODE>/totals.txt` on every call, so both places
        // agree in any language; the array is kept until the language changes, because a source
        // name is also assembled on every strike of a run.
        public static string[] PartNames
        {
            get
            {
                string[] made = partNames;
                if (made == null || partNamesVersion != Lang.Version)
                {
                    made = new[] { "", " " + Lang.T("totals.part.prefix"), " " + Lang.T("totals.part.suffix"),
                                   " " + Lang.T("totals.part.component"), " " + Lang.T("totals.part.relicBonus"),
                                   " " + Lang.T("totals.part.augment"), " " + Lang.T("totals.part.modifier"),
                                   " " + Lang.T("totals.part.ascension") };
                    partNames = made;
                    partNamesVersion = Lang.Version;
                }
                return made;
            }
        }
        static string[] partNames;
        static int partNamesVersion = -1;

        // The labels of a skill's terms, in one place: `--modleak` looks for a modifier by them.
        public static string SkillLabel(string title, int level) { return Lang.T("totals.skill", title, level); }
        public static string ToggleLabel(string title, int level) { return Lang.T("totals.toggled", title, level); }

        // The head of the label, before the level number — for a caller that does not know the
        // level (`--modleak`).
        public static string LabelHead(bool toggled, string title)
        {
            const string mark = "";
            string full = Lang.T(toggled ? "totals.toggled" : "totals.skill", title, mark);
            int at = full.IndexOf(mark, StringComparison.Ordinal);
            return at < 0 ? full : full.Substring(0, at);
        }
        // The armour belonging to EACH equipment slot (twelve of them, in the order the save keeps
        // them). Needed by armour per body zone — MECHANICS 20 and `ArmourZones` below.
        public readonly double[] SlotArmour = new double[12];
        public readonly List<string> Skipped = new List<string>();

        // Fields where several sources do not add up — the game keeps only the
        // strongest. Matched as prefixes, because each comes in a family of
        // Min/Max/Chance/Duration/Modifier.
        static readonly string[] StrongestWins = {
            "offensiveTotalResistanceReductionAbsolute",
            "offensiveTotalResistanceReductionPercent",
            "offensiveElementalResistanceReductionAbsolute",
            "offensiveElementalResistanceReductionPercent",
            "offensivePhysicalResistanceReductionAbsolute",
            "offensivePhysicalResistanceReductionPercent",
            "offensiveTotalDamageReductionPercent",
            "offensivePhysicalReductionPercent",
            "offensiveElementalReductionPercent",
            "offensiveFumble",
            "offensiveProjectileFumble",
            "offensiveSlowOffensiveAbility",
            "offensiveSlowDefensiveAbility",
            "offensiveSlowDefensiveReduction",
        };

        public static bool IsStrongestWins(string field)
        {
            foreach (string p in StrongestWins)
                if (field.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        // Fields that describe a skill rather than the character: how long it
        // lasts, what it costs, how far it reaches. They belong to whatever skill
        // carries them and adding them across sources is meaningless — a summed
        // "17.6 s duration" is not a property of anybody.
        static readonly string[] SkillOwn = {
            "skillActiveDuration", "skillCooldownTime", "skillTargetRadius", "skillTargetNumber",
            "skillTargetAngle", "skillProjectileNumber", "projectileLaunchNumber",
            "projectilePiercingChance", "weaponDamagePct", "retaliationDamagePct",
            "skillActiveLifeCost", "skillActiveManaCost", "skillManaCost",
            "skillLifeBonus", "skillLifePercent", "skillManaBonus", "skillManaPercent",
            "skillChanceWeight", "skillExperienceLevels", "skillMaxLevel", "skillUltimateLevel",
            "skillTier",
        };

        static bool IsSkillOwn(string field)
        {
            foreach (string s in SkillOwn) if (field == s) return true;
            return false;
        }

        void Add(DbRecord rec, int slot, string source)
        { Add(Fields, rec, slot, source, false, 0, null, Racial, XorSources); }

        // rolls == true means the source is an item instance whose numbers the
        // game draws from a range; the sum then also tracks the worst and the
        // best it could be. attributeScalePercent is only the damage half of that
        // rule — an item without the field still rolls everything else, which is
        // what Oleron's Wrath showed: no such field, and the game still rolled
        // its bleeding duration 30 → 24.
        void Add(DbRecord rec, int slot, string source, bool rolls, int scale)
        { Add(Fields, rec, slot, source, rolls, scale, null, Racial, XorSources); }

        // A field's number as it stands, with no roll and no multipliers: that is how armour and
        // the armour bonus are taken, neither of which is rolled at all.
        static double Plain(DbRecord rec, string field)
        {
            if (rec == null) return 0;
            string v = rec.Get(field);
            if (string.IsNullOrEmpty(v)) return 0;
            double d;
            return double.TryParse(v.Split(';')[0], NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        static void Add(Dictionary<string, TotalsEntry> into, DbRecord rec, int slot, string source,
                        bool rolls, int scale)
        { Add(into, rec, slot, source, rolls, scale, null, null, null); }

        static void Add(Dictionary<string, TotalsEntry> into, DbRecord rec, int slot, string source,
                        bool rolls, int scale, Dictionary<string, double> exact)
        { Add(into, rec, slot, source, rolls, scale, exact, null, null); }

        // exact, when given, holds the numbers the game actually rolled on this
        // item — read off its own tooltip. A field that is in there is taken from
        // there and stops being a span; the rest of the record is unchanged.
        // `racial`, when given, collects the bonuses against a race by race, past the common sum.
        // Who passes it: the character's items and auras, which strike the enemy with our blow. Who
        // does NOT: pets — their damage is counted in a layer of its own, and the owner's bonuses
        // must not be mixed into a pet's.
        static void Add(Dictionary<string, TotalsEntry> into, DbRecord rec, int slot, string source,
                        bool rolls, int scale, Dictionary<string, double> exact,
                        Dictionary<string, double[]> racial, List<XorSource> xor)
        {
            if (rec == null) return;
            foreach (KeyValuePair<string, string> kv in rec.Fields)
            {
                string v = kv.Value;
                if (string.IsNullOrEmpty(v)) continue;
                string[] parts = v.Split(';');
                string one = parts[Math.Min(Math.Max(0, slot), parts.Length - 1)];
                double d;
                if (!double.TryParse(one, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) continue;
                bool known = false;
                if (exact != null)
                {
                    double got;
                    if (exact.TryGetValue(kv.Key, out got)) { d = got; known = true; }
                }
                if (d == 0) continue;
                // A chance-based percentage enters by its expected value, as chance-based flat
                // damage does. Iskandra's Balance prints "25% chance of +162% burn, +226% frostburn,
                // +191% electrocute…", and in the record that is `offensiveSlowFireModifier` beside
                // `offensiveSlowFireModifierChance 25`. We added them in full, and every elemental
                // percentage of `_Assira` was high by the same amount.
                //
                // THE SAME HOLDS FOR FLAT DAMAGE AND DOTS FROM ITEMS (9 August 2026). The rule sat
                // in three places and all three were incomplete: `SklFlat` and `SkillDot` applied
                // it to skills, while here it stood only on fields whose name ends in `Modifier`.
                //
                // Measured: Mighty Sacred Hammer of Eternal Wrath on `_Assira`, "25% chance of
                // 96 – 285 electrocute over 3 s"; the panel shows `170 – 506` per second, four
                // times less than without the chance.
                //
                // "Chance of one of": the losing field of a group does not reach the panel at all,
                // but a strike needs the whole group — so the record is remembered, for dots
                // (MECHANICS 85a). No record in the game has an "one of" group on direct damage
                // (census 85).
                if (xor != null && kv.Key.EndsWith("Min", StringComparison.Ordinal))
                {
                    string xp, xm;
                    if (InXor(rec, kv.Key, slot, out xp, out xm) && xp == "offensiveSlow" &&
                        !xor.Exists(delegate (XorSource s) { return s.Rec == rec && s.Slot == slot && s.Source == source; }))
                        xor.Add(new XorSource { Source = source, Rec = rec, Slot = slot });
                }
                if (XorLoses(rec, kv.Key, slot)) continue;
                string chanceField = ChanceSibling(kv.Key);
                double termChance = 0;
                string termRoll = null;
                if (chanceField != null)
                {
                    double c = Chance(rec, chanceField, slot);
                    if (c > 0 && c < 100)
                    {
                        d *= c / 100.0;
                        // The strike gets the chance itself and a roll key (MECHANICS 89b,
                        // `TotalsEntry.TermOn`) — except for "one of" fields: their chance is
                        // played out by the group's roll (85b), and a second roll of the same
                        // chance would land rarer than in the game.
                        string xorPrefix, xorRoot;
                        if (!InXor(rec, kv.Key, slot, out xorPrefix, out xorRoot))
                        {
                            termChance = c;
                            termRoll = ChanceKey(rec, chanceField, slot, source);
                        }
                    }
                }
                // A bonus against a race needs the race name beside it, and names
                // do not add up — the same reason the devotion total leaves them out.
                // It stays out of the COMMON sum, rightly so — it is a multiplier of its own, not a
                // term — but it is no longer thrown away: it goes into its own count by race
                // (MECHANICS 7b).
                if (kv.Key.StartsWith("racialBonus", StringComparison.Ordinal))
                {
                    bool pct = kv.Key == "racialBonusPercentDamage";
                    if (racial != null && (pct || kv.Key == "racialBonusAbsoluteDamage"))
                    {
                        // One item can name SEVERAL races at once ("Race003;Race005") and the bonus
                        // applies to each, exactly as its tooltip reads them
                        // (`ItemView.AppendRacial`).
                        foreach (string race in (rec.Get("racialBonusRace") ?? "").Split(';'))
                        {
                            if (race.Length == 0) continue;
                            double[] pair;
                            if (!racial.TryGetValue(race, out pair)) racial[race] = pair = new double[2];
                            pair[pct ? 0 : 1] += d;
                        }
                    }
                    continue;
                }
                if (IsSkillOwn(kv.Key)) continue;

                TotalsEntry e;
                if (!into.TryGetValue(kv.Key, out e))
                {
                    e = new TotalsEntry();
                    e.ByMax = IsStrongestWins(kv.Key);
                    into[kv.Key] = e;
                }
                e.Sources++;
                e.Terms.Add(new KeyValuePair<string, double>(source, d));
                e.TermChance.Add(termChance);
                e.TermRoll.Add(termRoll);
                if (Math.Abs(d) > Math.Abs(e.Largest)) e.Largest = d;
                e.Value = e.ByMax ? e.Largest : e.Value + d;

                double lo = d, hi = d;
                if (rolls && !known) ItemViewFactory.RollBounds(kv.Key, d, scale, out lo, out hi);
                e.Low = e.ByMax ? Math.Min(e.Low == 0 ? lo : e.Low, lo) : e.Low + lo;
                e.High = e.ByMax ? Math.Max(e.High, hi) : e.High + hi;
            }
        }

        // Everything worn, everything socketed into it, every passive skill with a
        // point in it, and every devotion star.
        // WITH NO TABLE THE NUMBERS COME FROM THE SEED, NOT FROM THE RECORD.
        //
        // This used to be `Of(game, c, null)`, i.e. the whole editor counted by the record's
        // numbers — the MIDDLE of the span. That was honest while the field order was not fully
        // derived, but since 15 August it is: all 83 items of the table are reproduced whole, and
        // `--ordertrust` names the rare fields whose place is still a guess. Now the panel, the
        // damage and the tooltip take THE SAME numbers — the ones the game would show for this
        // instance. Fields with a guessed place are not substituted (`ExactRolls.ReplaceWithSeed`
        // skips them) and the tooltip marks those lines red, so a guess spreads nowhere.
        public static CharacterTotals Of(GameData game, Character c)
        {
            if (game == null || c == null) return Of(game, c, null);
            ExactRolls seeded = new ExactRolls();
            seeded.ReplaceWithSeed(game, c);
            return Of(game, c, seeded);
        }

        // With exact rolls given, the base record of every item the table covers
        // is read from the table instead — see ExactRolls. Only the base record:
        // a prefix or a suffix rolls separately and the tooltip does not tell them
        // apart, and the thirteen items of `_Relo` carry neither.
        public static CharacterTotals Of(GameData game, Character c, ExactRolls exact)
        { return Of(game, c, exact, null); }

        // `chosen` — what is raised BY THE USER'S DECISION (the "choose active skills" window,
        // `ActiveSkills`). It is passed only from where it was asked: stands have their own way of
        // saying what is up (`#toggle`), and mixing window clicks into them would make the battery
        // depend on settings. Nothing chosen, or `null`, counts exactly as before.
        public static CharacterTotals Of(GameData game, Character c, ExactRolls exact, ActiveSkills chosen)
        {
            CharacterTotals t = new CharacterTotals();
            if (game == null || c == null) return t;
            // The resistance cap comes from `gameengine.dbr` (`playerDefenseCap`).
            BaseResistCap = (int)Math.Round(Combat.ResistCap(game));

            List<Item> worn = new List<Item>();
            worn.AddRange(c.Equipment); worn.AddRange(c.Weapons1);
            // THE SECOND WEAPON SET (`Weapons2`) DOES NOT COUNT. Found 29 August on the shield user
            // `_222`: his sum held a mace from the SECOND set with its 80 physical, 10 aether and
            // 16.1% conversion. It is in neither hand, and `DamageModel.OtherHand` subtracts flat
            // damage only from `Weapons1`, so there was nothing to take it out with: it settled in
            // the character and went into EVERY hand.
            //
            // The model counts hands as `Weapons1` everywhere — speed, dual wielding, which hand,
            // the subtraction of flat damage. So the sum must take the same set or it describes a
            // different character.
            //
            // CAVEAT: the save has a `UseAlternate` switch. If the game marks with it that the
            // character stands on the SECOND set, the change belongs not only here but everywhere
            // `Weapons1` is taken. In all our saves it is zero (checked with `--charflags`), so
            // there is no measurement for the other case and it must not be touched.
            // Which sets the worn gear belongs to, and how many pieces of each.
            // A set bonus is an ordinary record whose fields are lists indexed by
            // the number of pieces — the same shape as a skill indexed by level.
            Dictionary<string, List<string>> setPieces =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            // HOW MANY PIECES OF EACH SET ARE WORN — counted UP FRONT, before the walk. `setPieces`
            // below fills AS the walk goes and is still short when the set's first item is read,
            // while the level of a granted skill depends on the piece count — so the question must
            // go to a finished count, not a growing one. The counting rule itself is one for the
            // whole editor (`ItemSkillMods.SetSizes`), so four places cannot drift apart.
            Dictionary<string, int> setSize = ItemSkillMods.SetSizes(game, worn);
            HashSet<string> setGrantDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Two of the same item are two different rolls, so the exact table is
            // asked for the first, the second and so on by the order they are worn.
            Dictionary<string, int> seenNames =
                new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            // ARMOUR LANDS PER BODY ZONE, NOT IN A COMMON POT, so it is also counted by slot here:
            // an item's own armour goes to ITS zone, everything else to all six at once
            // (MECHANICS 20).
            for (int wi = 0; wi < worn.Count; wi++)
            {
                Item it = worn[wi];
                int armourSlot = wi < c.Equipment.Count ? wi : -1;
                if (string.IsNullOrEmpty(it.BaseName)) continue;
                t.WornItems++;
                string name = ItemViewFactory.Build(game, it, false).Name;
                if (string.IsNullOrEmpty(name)) name = it.BaseName;
                int ordinal = seenNames.ContainsKey(name) ? seenNames[name] : 0;
                seenNames[name] = ordinal + 1;
                // The completion bonus of a relic — see ItemViewFactory.RelicBonusOf
                // for why only a relic's counts. The save has held it all along and
                // nothing read it, so those bonuses were missing everywhere; on
                // `_Relo` it is Oleron's Wrath giving +1 to Fighting Form (MECHANICS 14v).
                string[] parts = { it.BaseName, it.PrefixName, it.SuffixName,
                                   it.RelicName, ItemViewFactory.RelicBonusOf(game, it),
                                   it.AugmentName, it.ModifierName, it.AscensionName };
                string[] what = PartNames;
                // The item itself and its affixes roll; a component, an augment and
                // the ascension bonus do not — theirs are the same numbers on every
                // copy. The ascension is checked against the game: `ao305c.dbr` holds
                // 4/4/4/4 and the tooltip of the ascended sword prints 4/4/4/4 with
                // no span (screen-amakata-eq17.jpg).
                //
                // A RELIC'S COMPLETION BONUS IS ROLLED — measured 26 August 2026 on
                // a `_Viliya` weapon-test save. Until that day it was the only one of the eight
                // parts standing here as "not rolled" WITHOUT a measurement, next to the component
                // and the augment (those have one — the pet bonus panel, 19 August).
                //
                // It is the same `LootRandomizer` as a prefix or a suffix, only with a spread of
                // its own: `ac07a_phycunspi` carries `lootRandomizerJitter 50`, three times the
                // usual span.
                //
                // HOW IT WAS MEASURED. On that character the WHOLE stand report sat 0.1–0.4% below
                // the game — all 42 numbers at once, and the dots missed the threshold. Only a
                // multiplier present on every line gives such an even shortfall, i.e. an attribute:
                // `spirit/215` for direct damage and `spirit/200` for a dot. The game needs spirit
                // 873.75, we had 866.76, and the only whole "flat + percent" pair giving 873.75 is
                // (170 + 529) × 1.25 — so the spirit percentage is 25, not 24.
                //
                //     our roll of the completion bonus    physique 3   cunning 3   spirit 3
                //     the roll of that part from the seed physique 2   cunning 3   spirit 4
                //
                // Cunning DOES NOT MOVE, and that is the other half of the measurement: the same
                // character's pierce damage matched before the fix, so cunning was right. A fix
                // that shifted both would have broken it; this one does not.
                bool[] rolls = { true, true, true, false, true, false, true, false };
                for (int i = 0; i < parts.Length; i++)
                {
                    if (string.IsNullOrEmpty(parts[i])) continue;
                    DbRecord pr = game.Record(parts[i]);
                    // Rolled numbers are taken per part: on a rare item "+205% physical" belongs to
                    // the base and "+109% total damage" to the prefix, and each must be
                    // substituted into its own record.
                    Dictionary<string, double> known = exact == null ? null : exact.For(name, ordinal, i);
                    Add(t.Fields, pr, 0, name + what[i], rolls[i],
                        pr == null ? 0 : pr.GetInt("attributeScalePercent"), known, t.Racial, t.XorSources);
                    // The item's seed, so the pet bonus is counted from its stream — and THE SAME
                    // "is this part rolled" flag as for ordinary fields.
                    //
                    // Until 19 August the pet bonus was rolled for ALL parts, the component, the
                    // augment and the ascension included, none of which roll at all. Found by
                    // comparison with the game's pet bonus panel: three of its twenty-four lines
                    // differed by exactly the augments —
                    //     crit damage        game 34, ours 36 (augments 7+6+7)
                    //     offensive ability  game 58, ours 57 (augments 2+2+4)
                    //     total speed        game 23, ours 24 (augment 9)
                    t.AddPet(game, game.Record(parts[i]), 0, name + what[i], it.Seed, rolls[i]);
                    // THIS item's armour goes into ITS zone. Both fields at once: the record's own
                    // armour (`defensiveProtection`) and a component's "+N armour bonus"
                    // (`defensiveBonusProtection`) — the `_Relo` measurement showed the component's
                    // bonus lands in the zone the item sits in, not in all of them. Armour is not
                    // rolled, so it is taken from the record as it is, without the seed
                    // (MECHANICS 14a).
                    if (armourSlot >= 0 && pr != null)
                        t.SlotArmour[armourSlot] += Plain(pr, "defensiveProtection") +
                                                    Plain(pr, "defensiveBonusProtection");
                }

                // Permanent buffs granted by items. Chilling Weapons from the Coldstone component is
                // a `Skill_BuffSelfToggled` with `characterManaLimitReserve 150`, and the game keeps
                // it on by itself ("buffs are activated automatically", says its tooltip). Its +75%
                // cold and +75% frostburn never reached our sum, because granted skills were walked
                // only for the list of skills, not for their bonuses.
                //
                // A proc is told apart by the item's own record: a proc carries
                // `itemSkillAutoController` with a trigger and a chance (Golemfist Gavel —
                // `cast_@selfonanyhit_30%`), a permanent buff has none.
                for (int i = 0; i < parts.Length; i++)
                    GrantedInto(string.IsNullOrEmpty(parts[i]) ? null : game.Record(parts[i]),
                                name + what[i], -1);

                // …AND THE SKILL GRANTED BY A SET. Nobody took it: the walk went over the parts of
                // an ITEM, while `itemSkillName` also sits on a SET's record — on 144 sets of 244
                // (census 1 September). Worldkeeper's Garden grants Conjure Living Bloom at four
                // pieces, Bloodlust on `_555` grants `bloodlust` at four as well.
                //
                // The level is taken BY THE SAME INDEX as every other list of a set (pieces minus
                // one): both hold `itemSkillLevel 0;0;0;1`, i.e. at three pieces there is no skill
                // AT ALL and at four it is level one. A zero here means "not granted yet", which is
                // not the same as "granted at level one".
                //
                // Counted ONCE per set rather than per worn piece — hence `setGrantDone`.
                DbRecord setOwn = game.Record(it.BaseName);
                string setOfIt = setOwn == null ? null : setOwn.Get("itemSetName");
                if (!string.IsNullOrEmpty(setOfIt) && setGrantDone.Add(setOfIt))
                {
                    DbRecord setRec = game.Record(setOfIt);
                    int pieces;
                    if (setRec != null && setSize.TryGetValue(setOfIt, out pieces))
                    {
                        int lvl = ItemSkillMods.GrantedLevelOfSet(setRec, pieces);
                        if (lvl > 0)
                            GrantedInto(setRec,
                                        Lang.T("common.source.set", ItemViewFactory.SetNameOf(game, setRec) ?? setOfIt, pieces),
                                        lvl);
                    }
                }

                // ONE BODY FOR BOTH CASES — the parts of an item and a set's record. They must not
                // be split into two lists of rules: a rule that stands in one place of two is a
                // rule that does not exist. `level < 0` means "take the level from the record
                // itself" (an item keeps it there), otherwise the passed one is used (for a set, by
                // the number of pieces).
                void GrantedInto(DbRecord pr, string where, int level)
                {
                    if (pr == null) return;
                    string granted = pr.Get("itemSkillName");
                    if (string.IsNullOrEmpty(granted)) return;
                    DbRecord gr = game.Record(granted);
                    if (gr == null) return;
                    // THE USER'S DECISION OUTWEIGHS EVERY TEST BELOW. The tests ("this is a proc",
                    // "it reserves no energy") are ours, not the game's, and have room to be wrong;
                    // the person has the game in front of them. The weapon gate stays, though: it
                    // comes from the game and is checked — a two-handed skill cannot be raised with
                    // an axe on.
                    //
                    // AN AURA THAT STRIKES BY ITSELF: ITS DAMAGE IS NOT YOURS, ITS BONUSES ARE.
                    //
                    // Until 31 August such an aura from an ITEM was skipped whole, which was half a
                    // rule; the other half already stood next to it for MASTERY auras
                    // (`OnWhen.AuraAttack` below): Vindictive Flame enters the sum with
                    // `ownDamage = true` — its `offensive*` is dropped (that is damage it deals
                    // itself) while its +17% total speed and +206 health per second count.
                    //
                    // THE MEASUREMENT THAT CLOSED IT (31 August, `_Hamarina`): she carries TWO
                    // swords with the Seal of Blades component, each granting Whirling Blades
                    // (`Skill_BuffAttackRadiusToggled`) with `defensiveProtectionModifier 8`. The
                    // game shows armour per zone at +47% while we had +31% — short by exactly
                    // 8 + 8. With them all SIX zones match to the unit:
                    //     2568 / 2568 / 2856 / 2543 / 3430 / 2198, rating 2768.
                    //
                    // The 20 August measurement still holds and is explained by the same split: the
                    // aura's damage does not go to the character (with it all her numbers doubled).
                    // The difference is not "aura or not" but between its DAMAGE and its BONUSES —
                    // exactly what `AddOwnBonuses` does.
                    //
                    // WHAT RIDES ALONG BUT IS NOT MEASURED: the same record holds
                    // `defensivePierce 15`, now counted too — on `_Hamarina` that is +30 pierce
                    // resistance. We have no screenshot of her resistances; one look at the panel
                    // would check it.
                    bool auraAttack = IsToggle(gr.Class ?? "") &&
                                      (gr.Class ?? "").IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0;
                    // A PASSIVE GRANTED BY AN ITEM IS ALWAYS ON — there is nothing to switch.
                    //
                    // The filter below asks "is it a proc, and if not, does it reserve energy",
                    // because those are the marks of an aura that is up. A `Skill_Passive` has
                    // neither: it is not cast, not toggled and reserves nothing, it simply applies
                    // while the item is worn. It was therefore filed under "fires on a condition"
                    // and dropped whole.
                    //
                    // MEASURED ON `_Amitohx` (13 October, the user's screenshots). His shield
                    // `gearweapons/shields/d109_shield.dbr` grants Gunner's Aegis
                    // (`itemskillsgdx1/legendary/gunneraegis.dbr`, `Skill_Passive`), and the record
                    // holds exactly what the item's tooltip prints: `characterLife 1200`,
                    // `defensiveLife 15`, `defensivePierce 25` and `offensiveFireMin 80`.
                    //
                    // Those 80 were the whole discrepancy, and they were visible as a FLAT one —
                    // the same number at both ends of both hands:
                    //
                    //   plain attack   (473.0696 + 80) × 31.7914 = 17583   game 17583
                    //                  (537.8522 + 80) × 31.7914 = 19642   game 19642
                    //   shield hand    the game's per-hand panel wants 608.06 – 665.84,
                    //                  ours was 528.07 – 585.85 — again 80 at both ends
                    //
                    // Flat and equal on both hands is the signature of a CHARACTER bonus, which is
                    // what a passive is. Pierce, cold, lightning, chaos, burn and frostburn of the
                    // same three panels agreed to the unit throughout, before and after.
                    //
                    // THE WEAPON GATE STILL APPLIES and is what makes this safe: the record carries
                    // `Ranged1h 1`, so the passive counts only with a one-handed ranged weapon in
                    // hand — `WeaponClassGate` below already knew how to read that.
                    bool passive = string.Equals(gr.Class, "Skill_Passive", StringComparison.OrdinalIgnoreCase);
                    bool? said = chosen == null ? null : chosen.For(granted);
                    if (said.HasValue)
                    {
                        t.ChosenByHand.Add(Lang.T(said.Value ? "totals.chosenOn" : "totals.chosenOff",
                                                  SkillText.Name(game, granted) ?? granted));
                        if (!said.Value)
                        { t.SkippedProcs.Add(SkillText.Name(game, granted) ?? granted); return; }
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(pr.Get("itemSkillAutoController")))
                        { t.SkippedProcs.Add(SkillText.Name(game, granted) ?? granted); return; }
                        // The mark of "always up" is the energy reservation: an aura holds it while
                        // switched on. A temporary buff (Presence of Might from a component) has
                        // none and does not come here: with it the checked Cadence numbers of
                        // `_Relo` drifted by 2–7%.
                        //
                        // …OR IT IS A TOGGLED BUFF, and those raise themselves (the same rule as
                        // for mastery skills — see the `active` map below). Frostdread Presence is
                        // a `Skill_BuffRadiusToggled` whose tooltip says "buffs are activated
                        // automatically"; the old condition demanded `BuffSelf` and such an aura
                        // vanished entirely.
                        //
                        // …AND AN AURA THAT STRIKES BY ITSELF IS ALWAYS UP TOO, being toggled.
                        // Until 31 August it was cut off here; now it enters the sum but WITHOUT
                        // its own damage (`auraAttack` above).
                        //
                        // …AND A PASSIVE IS UP TOO, without being any of those (see `passive`
                        // above). `--passiveoff` puts it back under the old rule, so the check can
                        // be seen red.
                        if (!(passive && !BreakGrantedPassive) && !IsToggle(gr.Class ?? "") &&
                            ((gr.Class ?? "").IndexOf("BuffSelf", StringComparison.OrdinalIgnoreCase) < 0 ||
                             gr.GetInt("characterManaLimitReserve") <= 0))
                        { t.SkippedProcs.Add(SkillText.Name(game, granted) ?? granted); return; }
                    }
                    // Granted skills have a weapon gate as well, and this branch used to bypass it.
                    // Both records are checked — the one that grants and the buff itself: the mark
                    // can sit on either.
                    string itemGate = DamageModel.WeaponGate(game, c, gr) ??
                                      DamageModel.WeaponGate(game, c, pr);
                    if (itemGate != null)
                    {
                        t.SkippedNoDualWield.Add(Lang.T("totals.gateItem", SkillText.Name(game, granted) ?? granted, itemGate));
                        return;
                    }
                    t.Toggles++;
                    // THE LEVEL AT WHICH THE ITEM GRANTS THE SKILL, NOT ONE. A mythical item grants
                    // it at level two, where the numbers differ: Frostdread Presence holds
                    // `offensiveColdModifier 35;75` and `offensiveColdMin 12;21`. We took the first
                    // column, and on `_Iminika` that was exactly what was missing: cold percentage
                    // 995 instead of 1035 (the game's panel "+2612%") and nine units of flat cold.
                    // Both numbers were explained by one level.
                    t.AddSkill(game, gr, level < 0 ? AttackSkills.GrantedLevel(pr) : level,
                               Lang.T("totals.fromItem", SkillText.Name(game, granted) ?? granted, where), auraAttack);
                }

                DbRecord own = game.Record(it.BaseName);
                string set = own == null ? null : own.Get("itemSetName");
                if (!string.IsNullOrEmpty(set))
                {
                    if (!setPieces.ContainsKey(set)) setPieces[set] = new List<string>();
                    if (!setPieces[set].Contains(it.BaseName)) setPieces[set].Add(it.BaseName);
                }
            }

            foreach (KeyValuePair<string, List<string>> kv in setPieces)
            {
                DbRecord set = game.Record(kv.Key);
                if (set == null) continue;
                t.SetBonuses++;
                string name = ItemViewFactory.SetNameOf(game, set) ?? kv.Key;
                // Index = pieces − 1: "0;24;24;24" is nothing for one piece and
                // 24 from two on.
                t.Add(set, kv.Value.Count - 1, Lang.T("common.source.set", name, kv.Value.Count));
                t.AddPet(game, set, kv.Value.Count - 1, Lang.T("totals.setShort", name));
            }

            SkillText.LevelBonus bonus = SkillText.LevelBonus.Of(game, c);
            ModifierParents parents = ModifierParents.Of(game, c);
            Dictionary<string, int> active = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> levelOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // A TOGGLED BUFF RAISES ITSELF, AND THE GAME SAYS SO.
            //
            // We read the `act` flag from the save, and that is wrong: the game has no memory of
            // raised buffs, it takes them from the skills themselves, and it writes this in the
            // tooltip as `tagAutoToggledSkill` ("Buffs are activated automatically."). The mark is
            // the record's CLASS, and it separated the six cases of `_Iminika` cleanly (screenshots
            // 21 August 2026):
            //
            //   Maiven's Sphere of Protection   Skill_BuffSelfToggled     line PRESENT
            //   Star Pact                       Skill_BuffSelfToggled     line PRESENT
            //   Iskandra's Elemental Exchange   Skill_BuffRadiusToggled   line PRESENT
            //   Frostdread Presence             Skill_BuffRadiusToggled   line PRESENT (from an item)
            //   Mirror of Ereoctes              Skill_BuffSelfDuration    line ABSENT
            //   Deadly Aim                      a proc on crit            line ABSENT
            //
            // Elemental Balance and Conversion, which we filed as switched-off toggles, were never
            // toggles at all: they are the `Skill_Modifier` and `Skill_Transmuter` of that same
            // Iskandra's Elemental Exchange and rise with it.
            //
            // WHAT IT COST: in `_Iminika`'s save EVERYTHING is off while four are up in the game,
            // and all her damage came out 29% low.
            //
            // The `act` flag is not thrown away: it still holds for non-toggles and is what the
            // "choose active skills" window shows, and the user's decision is still laid on top.
            foreach (Skill s in c.Skills)
                if (!string.IsNullOrEmpty(s.Name))
                {
                    DbRecord r = game.Record(s.Name);
                    bool auto = r != null && IsToggle(r.Class ?? "");
                    active[s.Name] = auto ? 1 : s.Active;
                    levelOf[s.Name] = s.Level;
                }
            // THE USER'S DECISION ON TOP OF THE SAVE. It goes into the same map as the flag from
            // the file: beyond this point neither `Kind` nor `On` tells where "on" came from, and
            // the difference belongs in one place.
            //
            // What was raised BY HAND, as opposed to by the file's flag, is kept apart from the
            // `active` map: a pressable buff (Pneumatic Burst) enters the sum ONLY by a person's
            // decision and must not be confused with the save's unreliable flag.
            HashSet<string> raisedByHand = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (chosen != null)
                foreach (Skill s in c.Skills)
                {
                    if (string.IsNullOrEmpty(s.Name)) continue;
                    bool? said = chosen.For(s.Name);
                    if (said.HasValue)
                    {
                        active[s.Name] = said.Value ? 1 : 0;
                        if (said.Value) raisedByHand.Add(s.Name);
                        t.ChosenByHand.Add(Lang.T(said.Value ? "totals.chosenOn" : "totals.chosenOff",
                                                  SkillText.Name(game, s.Name) ?? s.Name));
                    }
                }

            foreach (Skill s in c.Skills)
            {
                if (string.IsNullOrEmpty(s.Name)) continue;
                DbRecord rec = game.Record(s.Name);
                if (rec == null) continue;
                string cls = rec.Class ?? "";

                if (SkillsForm.IsDevotion(s.Name))
                {
                    // A devotion star is one point and its bonuses are permanent.
                    // A star that grants a skill is a skill, not a bonus — it is
                    // shown on its own tab and is not part of the sum.
                    //
                    // …BUT IF IT WAS TICKED BY HAND, it counts. Ghoulish Hunger has the condition
                    // "100% at 45% health", and the enemy's damage to us is beyond our count — as
                    // are our health and our life steal. The decision was taken in advance: give a
                    // checkbox "treat as permanently active". Its bonuses (+22% attack speed, +22%
                    // cast speed, 18% physical resistance) then enter the sum.
                    if (!cls.StartsWith("Skill_Passive", StringComparison.OrdinalIgnoreCase) &&
                        !(raisedByHand != null && raisedByHand.Contains(s.Name))) continue;
                    if (s.Level <= 0) continue;
                    // A devotion can be bound to a weapon too: Hydra's record holds `Ranged1h` and
                    // `Ranged2h`, so its +5% attack speed works only with ranged weapons. The
                    // devotion branch used to bypass the weapon gate and counted those always.
                    string devGate = DamageModel.WeaponGate(game, c, rec);
                    if (devGate != null)
                    {
                        t.SkippedNoDualWield.Add(Lang.T("totals.gateDevotion", SkillText.Name(game, s.Name) ?? s.Name, devGate));
                        continue;
                    }
                    t.DevotionStars++;
                    string star = Lang.T("totals.devotion", SkillText.Name(game, s.Name) ?? s.Name);
                    // A STAR'S LEVEL IS `DevotionLevel`, NOT THE POINTS PUT IN. One point goes in
                    // and the star itself grows to twenty-five; the save keeps that in a field of
                    // its own. The skills window has read it for a long time, while the sum read
                    // the points — i.e. took the first row of every table. On stars with a single
                    // number there is no difference (which is why it went unnoticed), on tabular
                    // ones there is: for Bysmiel's Command the game prints "20 / 20".
                    int devLevel = s.DevotionLevel > 0 ? s.DevotionLevel : s.Level;
                    // A BUFF STAR DOES NOT CARRY ITS BONUSES IN ITSELF. Hungering Void
                    // (`Skill_BuffRadius`) is a stub with one `buffSkillName`, and all its
                    // percentages sit in the child record. Adding the top record's fields gave
                    // ZERO, and the buff rose changing nothing.
                    //
                    // The way in is `AddSkill`: it walks the chain and can also tell a debuff (what
                    // is hung on the ENEMY) from bonuses to oneself. An ordinary passive star is
                    // not hurt by the chain — everything is in its first record.
                    if (cls.StartsWith("Skill_Passive", StringComparison.OrdinalIgnoreCase))
                        t.Add(rec, SkillText.Slot(rec, devLevel), star);
                    else
                        t.AddSkill(game, rec, devLevel, star, false);
                    t.AddPet(game, rec, SkillText.Slot(rec, devLevel), star);
                    continue;
                }

                if (s.Level <= 0) continue;
                // `dualWieldOnly` — the skill works only with two one-handers. Dual Blades give 66
                // pierce and +106% to it, and we counted them always. In the game with a two-handed
                // axe the pierce line is NOT IN the damage panel at all (screen-amakata-dmg3),
                // while with two swords it is there and matches to the unit. The gate is read from
                // the record as a whole, not only from `dualWieldOnly`: ranged skills have a field
                // of their own — see `DamageModel.WeaponGate`.
                string gate = DamageModel.WeaponGate(game, c, rec);
                if (gate != null)
                { t.SkippedNoDualWield.Add((SkillText.Name(game, s.Name) ?? s.Name) + " — " + gate); continue; }
                // The mastery bar takes points and nothing else: "+3 to all skills" from gear raises
                // the skills, not the bar. Reading it at level+bonus took the wrong row of every
                // table it holds.
                bool isMastery = cls.IndexOf("Mastery", StringComparison.OrdinalIgnoreCase) >= 0;
                int level = s.Level + (bonus == null || isMastery ? 0 : bonus.For(s.Name, s.Level));
                string title = SkillText.Name(game, s.Name) ?? s.Name;

                // A modifier of a debuff aura is enemy-side too, and the record itself does not say
                // so: Night's Chill is a plain `Skill_Modifier` holding `defensiveCold −26` and
                // three more like it. Those are the resistances of whoever stands in Veil of
                // Shadow, and we were taking 26 points off four of `_Amakata`'s own. Which side it
                // belongs to is decided by the parent: the modifier of an aura does what the aura
                // does. Measured 8 August 2026 — resetting Veil of Shadow entirely moved neither
                // attack speed nor any percentage in the game.
                if ((cls.Equals("Skill_Modifier", StringComparison.OrdinalIgnoreCase)) &&
                    IsDebuffAura(game, parents.Of(s.Name)))
                { t.DebuffAuras.Add(title); continue; }

                switch (Kind(rec, s.Name, parents, active, raisedByHand))
                {
                    case OnWhen.Always:
                        t.PassiveSkills++;
                        t.AddSkill(game, rec, level, SkillLabel(title, level));
                        break;
                    case OnWhen.Toggled:
                        // The save does record which toggles are running: the skill entry carries a
                        // byte for it. Oleron's Rage on `_Relo` is act=1, and its +188% pierce
                        // damage is exactly what the game's sheet shows over our sum.
                        t.Toggles++;
                        t.AddSkill(game, rec, level, ToggleLabel(title, level));
                        // AN AURA THAT LANDS ON ALLIES LANDS ON A PET TOO. It is collected in a
                        // count of its own, during this same walk where the parents of modifiers
                        // are already known — otherwise the link Heart of the Wild → Mogdrogen's
                        // Pact cannot be rebuilt from outside. The test is `IsAllyAura`.
                        if (IsAllyAura(game, s.Name) || IsAllyAura(game, parents.Of(s.Name)))
                            t.AddAuraChain(game, rec, level, title);
                        break;
                    case OnWhen.ToggleOff:
                        t.SkippedOff.Add(title);
                        break;
                    case OnWhen.AuraAttack:
                        t.PassiveSkills++;
                        t.AddSkill(game, rec, level, SkillLabel(title, level), true);
                        break;
                    case OnWhen.Proc:
                        t.SkippedProcs.Add(title);
                        break;
                    default:
                        // Only real mastery skills are worth naming. The save also
                        // carries the engine's own entries — plain attack, movement,
                        // "pets attack", the potion buttons — and listing those as
                        // "not counted" would be noise.
                        if (ItemViewFactory.MasteryFolder(s.Name) != null)
                            t.Skipped.Add(title);
                        break;
                }
            }

            // The modifiers worn gear brings to a skill. The save knows nothing
            // about them — see ItemSkillMods — and they went missing from every
            // sum. The rule is the tree modifier's rule: a modifier reaches the
            // character only through the skill it changes, so it counts when that
            // skill is a passive or a toggle that is switched on, and belongs to
            // the attack rather than the character otherwise.
            foreach (ItemSkillMods.Entry e in ItemSkillMods.Of(game, c).All)
            {
                DbRecord target = game.Record(e.Skill);
                if (target == null) continue;
                int invested;
                if (!levelOf.TryGetValue(e.Skill, out invested) || invested <= 0) continue;
                OnWhen when = Kind(target, e.Skill, parents, active, raisedByHand);
                string what = (SkillText.Name(game, e.Skill) ?? e.Skill) + " — " + e.Source;
                // A MODIFIER OF A CURSE AURA ACTS ON ENEMIES, NOT ON YOU — the same rule as for
                // modifiers from the tree above, only this path did not ask it. Measured on
                // `_Viliya` 21 August: The Mageslayer set modifies Veil of Shadow and gives
                // `300 frostburn per second`; our frostburn base came out 544 where the game needs
                // 244 — exactly 544 − 300. With it removed the panel matches: 7917 against 7917.
                if (IsDebuffAura(game, e.Skill)) { t.DebuffAuras.Add(what); continue; }
                if (when == OnWhen.Always) t.Add(e.Modifier, 0, Lang.T("totals.skillMod", what));
                else if (when == OnWhen.Toggled) t.Add(e.Modifier, 0, Lang.T("totals.toggledMod", what));
                else if (when == OnWhen.ToggleOff) t.SkippedOff.Add(what);
                // The conversion of such a modifier was read NOWHERE: flat numbers and percentages
                // were added a line above, while `conversionInType` was looked at by no one —
                // neither this list nor `Conversions()` in the damage model, which walks only
                // `ActiveRecords`, and these were never put there.
                //
                // On `_Iman` that was exactly the hole that kept the ordinary attack from matching:
                // both pistols carry a modifier of Flame Touched — "50% conversion: chaos to
                // lightning" and "…to fire" — so all chaos leaves into the elements. The game's
                // panel has no chaos line at all while ours had one, and fire and lightning ran
                // 1–4% low because of it. After the fix both lines match at both ends to the unit.
                //
                // An attacking aura (`OnWhen.AuraAttack`, Vindictive Flame) does not come here and
                // must not: its "100% conversion of fire into lightning" converts its own damage,
                // not our strike's.
                if ((when == OnWhen.Always || when == OnWhen.Toggled) &&
                    !t.ActiveRecords.Contains(e.Modifier))
                { t.ActiveRecords.Add(e.Modifier); t.ActiveSlots.Add(0); }
            }
            return t;
        }

        // A skill's bonuses reach the character always, only while it is switched
        // on, or only for the few seconds a trigger fires it.
        // `AuraAttack` is a permanent aura that strikes an area BY ITSELF: its bonuses to the
        // character count, while its `offensive*` is damage it deals, not an addition to your blow.
        enum OnWhen { No, Always, Toggled, ToggleOff, Proc, AuraAttack }

        // `--modleakbreak` — a modifier enters the sum ALWAYS, whoever's it is, only so that the
        // `--modleak` check can be seen red.
        public static bool BreakModifierRule;

        static OnWhen Kind(DbRecord rec, string dbr, ModifierParents parents, Dictionary<string, int> active,
                           HashSet<string> raisedByHand)
        {
            string cls = rec.Class ?? "";
            if (cls.IndexOf("Mastery", StringComparison.OrdinalIgnoreCase) >= 0) return OnWhen.Always;
            if (IsToggle(cls))
                return On(active, dbr) ? OnWhen.Toggled : OnWhen.ToggleOff;
            // A proc: it hangs on the tree as a passive but fires on a trigger and lasts seconds.
            // Fighting Spirit has `onHitActivationChance` 45 and its +170% total damage is not in
            // the game's sheet.
            if (rec.GetInt("onHitActivationChance") > 0 ||
                cls.IndexOf("OnHit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.IndexOf("OnLife", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.IndexOf("OnLowHealth", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // …BUT not everything that fires on a hit is a proc. Vindictive Flame
                // (`Skill_OnHitAttackRadius`) hangs permanently: its tooltip says "buffs are
                // activated automatically" and the panel shows "115 energy reserved". The mark is
                // the one already checked on auras from items: THE ENERGY RESERVATION. A proc
                // reserves none, an aura holds it while it hangs. Its +17% total speed at level 19
                // never reached the sum, and without it a stripped `_Iman` ran 132% against the
                // game's 122.
                //
                // This must be tested ONLY here. The first attempt put the test before all others,
                // and the energy reservation began switching on whatever came along — modifiers of
                // switched-off toggles and other people's auras: `_Relo` physical grew 2.2 times,
                // `_Amakata` cold from 15 to 811. Two conditions are needed, both. Fighting Spirit
                // also reserves energy but fires at chance 45, and its +170% total damage is
                // checked NOT to be in the game's panel. Vindictive Flame has chance 100: it hangs
                // after the first strike and does not fall off. The reservation alone is not
                // enough: with it Fighting Spirit, Menhir's Will and Counter Strike switched on too
                // and `_Relo` physical grew by 44%.
                if (rec.GetInt("onHitActivationChance") >= 100 && FirstOf(rec, "characterManaLimitReserve") > 0)
                    return OnWhen.AuraAttack;
                return cls.StartsWith("Skill_Passive", StringComparison.OrdinalIgnoreCase)
                     ? OnWhen.Proc : OnWhen.No;
            }
            if (cls.StartsWith("Skill_Passive", StringComparison.OrdinalIgnoreCase)) return OnWhen.Always;
            // A modifier belongs to the skill it hangs off in the tree, and it
            // reaches the character only if that one does. A modifier of a toggled buff is part of
            // the buff; a modifier of an attack — Cadence has three — belongs to that attack, and
            // its damage is the attack's, not the character's.
            if (cls.Equals("Skill_Modifier", StringComparison.OrdinalIgnoreCase))
            {
                if (BreakModifierRule) return OnWhen.Always;
                string parent = parents.Of(dbr);
                if (parent == null) return OnWhen.No;
                // A MODIFIER FOLLOWS ITS PARENT, AND A PARENT IS NOT ALWAYS A TOGGLE.
                //
                // Elemental Awakening (`Skill_Modifier`) hangs off Pneumatic Burst
                // (`Skill_BuffSelfDuration`), which is a PRESSED 60-second buff, not a toggle.
                // Such a modifier used to be dropped together with its parent, and its +100%
                // elemental damage was counted NEVER — not even when the buff was raised.
                //
                // A buff like that can only be raised BY HAND (the "choose active skills" window):
                // it is temporary, and the game's panel at rest shows the damage without it. Until
                // it is raised everything goes as before.
                if (parents.ToggleParent(parent)) return On(active, parent) ? OnWhen.Toggled : OnWhen.ToggleOff;
                if (raisedByHand != null && raisedByHand.Contains(parent)) return OnWhen.Toggled;
                return OnWhen.No;
            }
            // A PRESSED BUFF RAISED BY HAND. On its own it does not enter the sum — it is
            // temporary, and the game's panel at rest shows the damage without it. But if the user
            // ticked it in the "choose active skills" window, it counts like a toggle.
            //
            // ONLY THE TICK DECIDES, NOT THE SAVE'S FLAG. The "active" flag in the file is
            // unreliable — we were burned by it three times (see the head of `ActiveSkills`) — and
            // taking it would move the stands' numbers silently.
            //
            // An area buff that is not a toggle counts the same way — a berserker's war cry, a word
            // of renewal: pressed like a `BuffSelf` and raised by the same tick (MECHANICS 76). Its
            // bonuses come through the `AddSkill` chain, where debuff records are skipped.
            if ((cls.StartsWith("Skill_BuffSelf", StringComparison.OrdinalIgnoreCase) ||
                 cls.Equals("Skill_BuffRadius", StringComparison.OrdinalIgnoreCase)) &&
                raisedByHand != null && raisedByHand.Contains(dbr))
                return OnWhen.Toggled;
            return OnWhen.No;
        }

        // The name of the chance field for this field, or null if it has no chance at all.
        //
        // The list is deliberately narrow, by damage family. The temptation to generalise to "any
        // field with a `Chance` neighbour" is strong and wrong: `offensiveStunMin` does have
        // `offensiveStunChance` beside it, but `Min` there is the stun's DURATION in seconds, not
        // damage, and multiplying it by the chance would spoil the tooltip. Damage may be
        // multiplied because it enters the sheet as an expected value (checked three times).
        static readonly string[] ChanceTypes = {
            "Physical", "Pierce", "Fire", "Cold", "Lightning",
            "Poison", "Life", "Aether", "Chaos", "Bleeding", "Elemental",
        };

        // The chance for a field: its own, and with none of its own the record's common chance —
        // BUT ONLY IF the field itself carries the "I am rolled by the common die" flag.
        //
        // The flag is required, and that is proved by measurement. The rule was first written
        // without it ("no own chance, take the common one") and the game refuted it with the very
        // first screenshot. The relic Oblivion (`d011_relic`) carries `offensiveGlobalChance 5` and
        // chaos damage with no `…Chance` of its own; it rolled `2 – 36 chaos damage` and `+80%
        // chaos damage`. On a stripped `_111` the panel showed 14 – 244, which is `2 × 6.7628` and
        // `36 × 6.7628` — the damage in full, without any five per cent. The record says why:
        // `offensiveChaosGlobal 0`, so the field is NOT rolled by the common die and the five does
        // not concern it.
        //
        // `d021_relic` is the opposite case: every field there carries `offensive{T}Global 1`
        // beside `offensiveGlobalChance 25`, and the common roll does rule. (They also have their
        // own `…ModifierChance 25`, so the average is unaffected; the flag only says the die is ONE
        // for all, which is about spread, not expectation.)
        //
        // Found by the `--itemstats` census, corrected by a measurement the same evening.
        // "CHANCE OF ONE OF": ONLY ONE FIELD OF THE GROUP REACHES THE PANEL — THE FIRST IN THE
        // GAME'S ORDER OF DAMAGE TYPES. The others do not reach it at all.
        //
        // The mark is `<field>XOR 1` beside `<field>Global 1`; the GDStash format notes know it as
        // "only one of these will fire". No item record carries it; skills do.
        //
        // MEASURED ON TWO CHARACTERS, AND THE SECOND WAS A PREDICTION.
        //
        // `_Iminika`, Overload (`playerclass05/elementalinfusion2`, a modifier of Iskandra's
        // Elemental Exchange): `offensiveGlobalChance 100` and three dots of 33% each with the XOR
        // flag — fire 43, cold 58.7, lightning 15 at level 12. The ordinary attack's panel:
        //
        //   burn        306 – 505   ours 14.19–23.43 × 21.571 = 306 – 505   EXACT
        //   frostburn   1565        needs base 52.7; 40 from Star Pact + 12 from devotion = 52,
        //                           while with Overload it would be 71.38
        //   electrocute NO LINE     we had 115 – 760
        //
        // So of its three dots only FIRE reached the panel. Fire stands before cold and lightning
        // in the game's order of types (physical, pierce, fire, cold, lightning, …) — the same
        // order we use everywhere.
        //
        // THE CHECK ON `_Itaan` WAS MADE BEFORE THE FIX AND MATCHED: Presence of Virtue has
        // bleeding and internal trauma under XOR at 50% each with a common roll of 100; trauma
        // matched for us while the bleeding of Righteous Fervor ran 500 against the game's 328 —
        // exactly 30.5/20 times. Physical (trauma) comes before bleeding, so by this rule the
        // bleeding of Presence of Virtue does not reach the panel, the base drops from 30.5 to 20,
        // and 328 comes out. So it did: the line went green and the stand's errors fell to five.
        public static bool XorLoses(DbRecord rec, string field, int slot)
        {
            string prefix, mine;
            if (!InXor(rec, field, slot, out prefix, out mine)) return false;
            return !string.Equals(XorFirst(rec, prefix, slot), mine, StringComparison.Ordinal);
        }

        // Whether a field stands in an "one of" group: the family prefix (`offensive` or
        // `offensiveSlow`) and the damage root whose `XOR 1` is in the record.
        public static bool InXor(DbRecord rec, string field, int slot, out string prefix, out string mine)
        {
            prefix = null; mine = null;
            if (rec == null || field == null) return false;
            prefix = field.StartsWith("offensiveSlow", StringComparison.Ordinal) ? "offensiveSlow"
                   : field.StartsWith("offensive", StringComparison.Ordinal) ? "offensive" : null;
            if (prefix == null) return false;
            foreach (string root in Conversion.Roots)
                if (field.StartsWith(prefix + root, StringComparison.Ordinal) &&
                    (mine == null || root.Length > mine.Length)) mine = root;
            return mine != null && Math.Abs(Pick(rec, prefix + mine + "XOR", slot)) >= 0.0001;
        }

        // The group's first field in the order of damage types — the one that reaches the PANEL
        // (MECHANICS 19b).
        public static string XorFirst(DbRecord rec, string prefix, int slot)
        {
            foreach (string root in Conversion.Roots)
                if (Math.Abs(Pick(rec, prefix + root + "XOR", slot)) >= 0.0001) return root;
            return null;
        }

        // IN COMBAT an "one of" group is a roll `u` on [0, 100) walked across the group's chances
        // in the same order of types (MECHANICS 85). In all five damage-carrying skills of the
        // database the group's chances sum to exactly a hundred (Overload 33 + 34 + 33, Presence of
        // Virtue 50 + 50) and the record's common chance is 100, so "nothing landed" and a common
        // roll on top do not occur here. `chance` is the chance of the field that landed (or what
        // is left of a hundred if none did).
        public static string XorRolled(DbRecord rec, string prefix, int slot, double u, out double chance)
        {
            double cum = 0;
            foreach (string root in Conversion.Roots)
            {
                if (Math.Abs(Pick(rec, prefix + root + "XOR", slot)) < 0.0001) continue;
                double c = Chance(rec, prefix + root + "Chance", slot);
                cum += c;
                if (u < cum) { chance = c; return root; }
            }
            chance = Math.Max(0, 100 - cum);
            return null;
        }

        // A record with an "one of" group whose first field the sum added as an expectation: a
        // strike needs the record whole to roll the group anew (`DamageModel.GearDot`,
        // MECHANICS 85a).
        public sealed class XorSource
        {
            public string Source;
            public DbRecord Rec;
            public int Slot;
        }

        // The roll key of a term's chance (MECHANICS 89b): fields sharing the record's common
        // chance use one die per record, the rest one per field. One place for the sum and for the
        // subtraction of the off hand, or the two would roll differently.
        public static string ChanceKey(DbRecord rec, string chanceField, int slot, string source)
        {
            return source + "|" + (Pick(rec, chanceField, slot) > 0 ? chanceField : "offensiveGlobalChance");
        }

        public static double Chance(DbRecord rec, string chanceField, int slot)
        {
            double own = Pick(rec, chanceField, slot);
            if (own > 0) return own;
            return Pick(rec, GlobalFlag(chanceField), slot) != 0
                 ? Pick(rec, "offensiveGlobalChance", slot) : 0;
        }

        // The name of the "rolled by the common die" flag for a chance field: it carries neither
        // `Modifier` nor `Chance` — `offensiveFireModifierChance` and `offensiveFireChance` both
        // look at `offensiveFireGlobal`.
        static string GlobalFlag(string chanceField)
        {
            string s = chanceField;
            if (s.EndsWith("Chance", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 6);
            if (s.EndsWith("Modifier", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 8);
            return s + "Global";
        }

        static double Pick(DbRecord rec, string field, int slot)
        {
            string v = rec == null ? null : rec.Get(field);
            if (string.IsNullOrEmpty(v)) return 0;
            string[] parts = v.Split(';');
            double c;
            return double.TryParse(parts[Math.Min(Math.Max(0, slot), parts.Length - 1)],
                                   NumberStyles.Float, CultureInfo.InvariantCulture, out c) ? c : 0;
        }

        public static string ChanceSibling(string field)
        {
            if (field.EndsWith("Modifier", StringComparison.Ordinal)) return field + "Chance";
            string bare = field.EndsWith("Min", StringComparison.Ordinal) ||
                          field.EndsWith("Max", StringComparison.Ordinal)
                        ? field.Substring(0, field.Length - 3) : null;
            if (bare == null) return null;
            foreach (string t in ChanceTypes)
                if (bare == "offensive" + t || bare == "offensiveBonus" + t ||
                    bare == "offensiveBase" + t || bare == "offensiveSlow" + t)
                    return bare + "Chance";
            // RETALIATION DEBUFFS CARRY A CHANCE TOO, and until 17 August we lost it: a prefix
            // giving attack and run slow with `retaliationSlowAttackSpeedChance 18` printed the
            // line without "18% chance of …", i.e. as permanent. The rule is the same as for every
            // other chance: the field is the bonus's name plus `Chance`.
            if (bare.StartsWith("retaliation", StringComparison.Ordinal)) return bare + "Chance";
            // AND THE CUTS LAID ON THE TARGET CARRY ONE AS WELL — lost until 7 October, for the
            // same reason the retaliation ones were: this list knew damage types and nothing else.
            if (!BreakCutChance)
                foreach (string one in CutChanceFields)
                    if (bare == one) return bare + "Chance";
            return null;
        }

        // The target-cut fields whose chance sits in the record beside them (MECHANICS 113v).
        //
        // Counted, not guessed — the census of item fields (`--itemstats`, `item-stats.txt`):
        //
        //     offensiveSlowDefensiveAbilityChance               16 records
        //     offensiveTotalResistanceReductionAbsoluteChance   16
        //     offensiveSlowDefensiveReductionChance             14   (the armour cut)
        //     offensiveTotalResistanceReductionPercentChance     8
        //
        // What it cost: the augment on _Amakata's swords (`b09a_enchant.dbr`) says "15% chance of
        // 15% reduced target's resistances for 5s", and we counted those 15% as PERMANENT — in the
        // item's own tooltip and in the character's sum alike, since both ends ask this one list.
        //
        // The Elemental and Physical families are named here too. The game keeps the same three
        // fields for them (MECHANICS 69a) and no item record carries their chance today, so the
        // line costs nothing and stops the next such hole before it opens.
        static readonly string[] CutChanceFields = {
            "offensiveTotalResistanceReductionAbsolute", "offensiveTotalResistanceReductionPercent",
            "offensiveElementalResistanceReductionAbsolute", "offensiveElementalResistanceReductionPercent",
            "offensivePhysicalResistanceReductionAbsolute", "offensivePhysicalResistanceReductionPercent",
            "offensiveSlowDefensiveAbility", "offensiveSlowOffensiveAbility", "offensiveSlowDefensiveReduction",
        };

        // `--rrchancebreak` — lose the chance again, the way it was lost before 7 October, so that
        // the guard over it can be seen going red. Nothing else reads this.
        public static bool BreakCutChance;

        // The first number of a list field: `characterManaLimitReserve` holds one value per level,
        // "25;30;35;…", and an ordinary integer parse of such a string gives zero.
        static double FirstOf(DbRecord rec, string field)
        {
            string v = rec == null ? null : rec.Get(field);
            double d;
            return !string.IsNullOrEmpty(v) &&
                   double.TryParse(v.Split(';')[0], System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        static bool IsToggle(string cls)
        { return cls.IndexOf("Toggled", StringComparison.OrdinalIgnoreCase) >= 0; }

        // The same from outside: the "choose active skills" window builds its list of toggles and
        // must decide by the same mark as the count does.
        // `--passiveoff` — the experiment "an item-granted passive does not count", i.e. the rule
        // as it stood before 13 October. With it `_Amitohx` loses his 80 flat fire and the stand
        // goes red on three lines instead of none; that is how the rule was seen to matter.
        public static bool BreakGrantedPassive;

        public static bool IsToggleClass(string cls)
        { return IsToggle(cls ?? ""); }

        static bool On(Dictionary<string, int> active, string dbr)
        { int v; return active.TryGetValue(dbr, out v) && v != 0; }

        // A toggled buff usually holds nothing itself: the numbers live in the
        // buff record it casts (fieldcommand1 → fieldcommand1buff), the same
        // hand-off chain that gives a skill its name.
        void AddSkill(GameData game, DbRecord rec, int level, string who)
        { AddSkill(game, rec, level, who, false); }

        // `ownDamage` marks a permanent aura that strikes an area by itself (Vindictive Flame). Its
        // bonuses to the character — +17% total speed, +206 health per second, retaliation — count;
        // its `offensive*` does NOT: that is damage it deals itself, not an addition to your blow.
        // Entered without the distinction it lifted `_Iman` fire from 6010 to 14196 against the
        // game's 6081, and the check caught it at once.
        void AddSkill(GameData game, DbRecord rec, int level, string who, bool ownDamage)
        {
            List<string> seen = new List<string>();
            for (int hop = 0; hop < 3 && rec != null; hop++)
            {
                if (seen.Contains(rec.Path)) return;
                seen.Add(rec.Path);
                // A debuff record says what happens to whoever stands in the aura, not to its
                // caster. Veil of Shadow is a `SkillBuff_Debuf` with `debufSkill 1` holding
                // −26% total speed and −216 offensive ability: we were taking both off `_Amakata`
                // herself, which knocked her attack speed down by a quarter. The chain is still
                // followed past it, in case a caster-side record hangs below.
                if (!IsDebuff(rec))
                {
                    if (ownDamage) AddOwnBonuses(rec, SkillText.Slot(rec, level), who);
                    else Add(rec, SkillText.Slot(rec, level), who);
                    AddPet(game, rec, SkillText.Slot(rec, level), who);
                    // Skill conversions: the damage model reads them, and one place must decide
                    // which skill is in effect — this one. An attacking aura's conversions are its
                    // own as well, not the character's.
                    if (!ownDamage && !ActiveRecords.Contains(rec))
                    { ActiveRecords.Add(rec); ActiveSlots.Add(SkillText.Slot(rec, level)); }
                }
                else DebuffAuras.Add(who);
                // The whole chain, not just the first link that carries numbers:
                // Field Command itself holds only `instantCast`, and its offensive and defensive
                // ability sit in `fieldcommand1buff`. A record is never added twice, so nothing is
                // counted twice.
                string next = ItemViewFactory.NextSkillRecord(rec);
                rec = string.IsNullOrEmpty(next) ? null : game.Record(next);
            }
        }

        // The same addition, but without the `offensive*` family, which belongs to the aura itself.
        // A copy of the record without those fields, so as not to breed a second parser.
        void AddOwnBonuses(DbRecord rec, int slot, string source)
        {
            DbRecord trimmed = new DbRecord();
            trimmed.Path = rec.Path;
            foreach (KeyValuePair<string, string> kv in rec.Fields)
                if (!kv.Key.StartsWith("offensive", StringComparison.Ordinal))
                    trimmed.Fields[kv.Key] = kv.Value;
            Add(trimmed, slot, source);
        }

        static bool IsDebuff(DbRecord rec)
        {
            return rec.GetInt("debufSkill") != 0 ||
                   (rec.Class ?? "").IndexOf("Debuf", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // AN AURA THAT LANDS ON ALLIES — AND THEREFORE ON PETS.
        //
        // The question (19 August) was whether this can be told FROM THE RECORD rather than from
        // the tooltip's words. It can, from two things we already have:
        //
        //   1) the skill's class contains `Radius`, i.e. the buff is hung around rather than on
        //      oneself: `Skill_BuffRadiusToggled` for Mogdrogen's Pact, Field Command and Presence
        //      of Virtue, while Master of Death is a `Skill_BuffSelfToggled` and hangs nothing
        //      around;
        //   2) its buff is NOT marked hostile (`debufSkill`, a `*Debuf*` class) — otherwise the
        //      circle covers enemies, not allies: Veil of Shadow is a `Skill_BuffRadiusToggled`
        //      too, but its buff is a `SkillBuff_Debuf`.
        //
        // Checked on five auras at once: Mogdrogen's Pact, Field Command and Presence of Virtue
        // land on allies; Veil of Shadow on enemies; Master of Death only on oneself.
        //
        // THE NUMBER IS NOT APPLIED, AND THAT IS A MEASUREMENT, NOT CAUTION. That such an aura
        // lands on an ally is visible in the game's words ("you and your nearby allies") and in the
        // panel: +29% health from Heart of the Wild, +18% pierce from Oak Skin and +12% speed from
        // a set appear in the pet bonuses exactly when a pet is summoned.
        //
        // BUT IT DOES NOT MOVE THE HEALTH NUMBER IN A SKILL'S TOOLTIP. Measured 19 August with two
        // screenshots of Reap Spirit, Mogdrogen's Pact up and down: the Sundered Wraith showed
        // 27260 both times, to the unit. So a summon's tooltip shows health WITHOUT auras, and the
        // 15% we cannot account for comes from somewhere else. The rule therefore only NAMES such
        // auras.
        public static bool IsAllyAura(GameData game, string dbr)
        {
            if (game == null || string.IsNullOrEmpty(dbr)) return false;
            DbRecord top = game.Record(dbr);
            if (top == null) return false;
            if ((top.Class ?? "").IndexOf("Radius", StringComparison.OrdinalIgnoreCase) < 0) return false;
            return !IsDebuffAura(game, dbr);
        }

        // Whether a skill is an aura that lands on the enemy. The toggle itself says nothing — Veil
        // of Shadow holds only `buffSkillName` — so the chain is walked to the record that carries
        // the numbers, the same hop `AddSkill` makes.
        static bool IsDebuffAura(GameData game, string dbr)
        {
            if (game == null || string.IsNullOrEmpty(dbr)) return false;
            List<string> seen = new List<string>();
            DbRecord rec = game.Record(dbr);
            for (int hop = 0; hop < 3 && rec != null; hop++)
            {
                if (seen.Contains(rec.Path)) break;
                seen.Add(rec.Path);
                if (IsDebuff(rec)) return true;
                string next = ItemViewFactory.NextSkillRecord(rec);
                rec = string.IsNullOrEmpty(next) ? null : game.Record(next);
            }
            return false;
        }

        public int SetBonuses, Toggles;
        // Auras whose numbers land on the enemy; listed so the form can say the
        // skill is on and deliberately absent from the sum, rather than lost.
        public readonly List<string> DebuffAuras = new List<string>();
        // Skills that do not work without dual wielding and are therefore not summed.
        public readonly List<string> SkippedNoDualWield = new List<string>();

        // PET-BONUS RECORDS WHOLE — FOR THE SAKE OF CONVERSION.
        //
        // `PetFields` adds numbers, and a conversion cannot be kept that way: it has three fields
        // side by side and two of them are TYPE NAMES (`conversionInType`, `conversionOutType`),
        // which do not add up. The sum left one percentage with no "from" and no "to", so a pet's
        // conversion could only be read by eye in an item's tooltip and was not applied to the
        // damage of its skills at all (noticed 20 August). Harbinger of Souls carries
        // "physical → vitality 15%" the same way, and conversions were read only from items.
        //
        // Beside the record what it ROLLED is kept: an item's conversion share is rolled like every
        // other number, and the rolled value is the one to take.
        public class PetSource
        {
            public DbRecord Rec;                      // the pet-bonus record itself
            public Dictionary<string, double> Known;  // what it rolled
            public string Name;                       // whose it is: an item or a skill
        }

        public readonly List<PetSource> PetRecords = new List<PetSource>();

        // The records of skills, devotions and mastery bars that are in effect right now.
        public readonly List<DbRecord> ActiveRecords = new List<DbRecord>();
        // The table row for each of them: a conversion share can be a list by skill level (see
        // `Conversion.Read`).
        public readonly List<int> ActiveSlots = new List<int>();
        public readonly List<string> SkippedOff = new List<string>();
        public readonly List<string> SkippedProcs = new List<string>();

        // WHAT AURAS THAT LAND ON ALLIES GIVE. Counted apart from the common sum because the
        // addressee differs: for the character it is already in `Fields`, and the same thing is
        // gathered here for a PET, who is an ally and gets the same aura.
        //
        // A separate sum rather than an addition to `PetFields`, on purpose: a pet bonus and an
        // aura that covered a pet are different things and the game shows them apart (the bonus
        // panel shows the aura only while a pet is summoned). Mix them and a number's origin can no
        // longer be told.
        public readonly Dictionary<string, TotalsEntry> AuraFields =
            new Dictionary<string, TotalsEntry>(StringComparer.Ordinal);

        void AddAura(DbRecord rec, int slot, string source)
        { Add(AuraFields, rec, slot, source, false, 0, null, Racial, null); }

        // THE SAME AURA, BUT ALONG THE CHAIN OF RECORDS — the way `AddSkill` adds it.
        //
        // The top record usually holds NO numbers of its own: Blood of Dreeg (`Skill_BuffRadius`)
        // has only `instantCast` there, while its offensive ability and flat damage sit in
        // `bloodofdreeg1_buff`. While `AuraFields` read the single top record the ally-aura bucket
        // stood nearly empty and pets got nothing from it (found 12 September, when Blood of Dreeg
        // did not reach the pets).
        //
        // Debuff records on the way are skipped: they are about whoever stands in the aura, not
        // about allies (the same rule as in `AddSkill`).
        void AddAuraChain(GameData game, DbRecord rec, int level, string source)
        {
            List<string> seen = new List<string>();
            for (int hop = 0; hop < 3 && rec != null; hop++)
            {
                if (seen.Contains(rec.Path)) return;
                seen.Add(rec.Path);
                if (!IsDebuff(rec)) AddAura(rec, SkillText.Slot(rec, level), source);
                string next = ItemViewFactory.NextSkillRecord(rec);
                rec = string.IsNullOrEmpty(next) ? null : game.Record(next);
            }
        }

        // A PET'S AURA THAT COVERS ALLIES ACTS ON US IN FULL.
        //
        // This is the other side of `AuraFields`: there lies what WE give pets (Blood of Dreeg),
        // here what a pet gives US. Measured 12 September, four screenshots of `_666`'s damage
        // panel with pets beside him and without:
        //
        //   no auras               physical 1612–2748, cold 160, acid 321–503, chaos 358–610
        //   +Hellfire              fire 250 appeared; chaos 358–610 → 860–1140
        //   +Emboldening Presence  EVERYTHING grew, each type by its own factor (physical ×1.128,
        //                          acid ×1.106) — the mark of "+% TOTAL damage"
        //   +Storm Spirit          cold 180→368, fire 280→482, lightning 0→161
        //
        // So the numbers go into `Fields`, as for any raised buff, and through `AddSkill` along the
        // whole chain: the aura itself (`Skill_BuffRadiusToggled`) holds no numbers, they are in
        // `buffSkillName`.
        //
        // THIS IS CALLED FROM THE QUEUE, NOT FROM `Of`: the aura hangs WHILE THE PET LIVES, so it
        // belongs to a moment's loadout, not to the permanent sum. The character panel does not add
        // them yet — it has no "a pet is nearby" knob, and all our stands were taken without pets.
        public void AddPetAura(GameData game, string dbr, int level, string who)
        {
            DbRecord rec = game == null || string.IsNullOrEmpty(dbr) ? null : game.Record(dbr);
            if (rec == null || PetAuras.Contains(who)) return;
            PetAuras.Add(who);
            AddSkill(game, rec, level, who);
            // AND TO THE OTHER PETS TOO — it acts on us and on pets alike (12 September). A pet's
            // strike reads the ally-aura bucket (`TimelinePets.AuraFlat` and `AuraAbility`), so the
            // record goes there as well.
            //
            // ROUGHENED THE SAME WAY AS OUR OWN ALLY AURAS: FLAT damage and offensive ability reach
            // the pet, the aura's percentages (Emboldening Presence's "+% total damage") do not —
            // a pet's strike numbers are gathered in advance and a multiplier cannot be applied to
            // them afterwards without knowing their base. That is the remaining gap on `_Izaar`'s
            // plague beast (ours ~8 thousand against the game's ~14.7 with the crit matching to the
            // hundredth).
            AddAuraChain(game, rec, level, who);
        }

        // Which pet auras are folded into this loadout — for the report.
        public readonly List<string> PetAuras = new List<string>();

        // What was raised or lowered BY HAND in the "choose active skills" window. Written into the
        // report on a line of its own: a sum counted by a person's decision and a sum counted by
        // the save are different things, and the numbers alone do not show it.
        public readonly List<string> ChosenByHand = new List<string>();

        // Which skill a Skill_Modifier hangs off. Nothing in the record says so —
        // the tree does: the modifier sits in the same row as its parent, to the
        // right of it (fieldcommand1 at x=566, fieldcommand2 at x=726, both at
        // y=389). So the parent is the nearest button to the left on that row
        // that is not itself a modifier.
        public class ModifierParents
        {
            readonly Dictionary<string, string> parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, bool> toggle = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            public string Of(string dbr) { string p; return parent.TryGetValue(dbr, out p) ? p : null; }
            public bool ToggleParent(string dbr) { bool b; return toggle.TryGetValue(dbr, out b) && b; }

            public static ModifierParents Of(GameData game, Character c)
            {
                ModifierParents m = new ModifierParents();
                List<string> folders = new List<string>();
                foreach (Skill s in c.Skills)
                {
                    string f = ItemViewFactory.MasteryFolder(s.Name);
                    if (f != null && !folders.Contains(f)) folders.Add(f);
                }
                foreach (string folder in folders)
                {
                    string number = folder.Length >= 12 ? folder.Substring(11) : "";
                    List<string> dbrs = new List<string>();
                    List<int> xs = new List<int>(), ys = new List<int>();
                    List<bool> mods = new List<bool>();
                    for (int i = 1; i <= 99; i++)
                    {
                        DbRecord ui = game.Record("records/ui/skills/class" + number + "/skill" + i.ToString("00") + ".dbr");
                        if (ui == null) continue;
                        string dbr = ui.Get("skillName");
                        DbRecord skill = string.IsNullOrEmpty(dbr) ? null : game.Record(dbr);
                        if (skill == null) continue;
                        dbrs.Add(dbr); xs.Add(ui.GetInt("bitmapPositionX")); ys.Add(ui.GetInt("bitmapPositionY"));
                        mods.Add((skill.Class ?? "").Equals("Skill_Modifier", StringComparison.OrdinalIgnoreCase));
                        m.toggle[dbr] = IsToggle(skill.Class ?? "");
                    }
                    for (int i = 0; i < dbrs.Count; i++)
                    {
                        if (!mods[i]) continue;
                        int best = -1;
                        for (int j = 0; j < dbrs.Count; j++)
                            if (j != i && !mods[j] && ys[j] == ys[i] && xs[j] < xs[i] &&
                                (best < 0 || xs[j] > xs[best])) best = j;
                        if (best >= 0) m.parent[dbrs[i]] = dbrs[best];
                    }
                }
                return m;
            }
        }

        // Bonuses that go to summoned creatures instead of to you. The record
        // names a separate one in petBonusName, and everything inside it applies
        // to pets only — mixing it into the character's own numbers would be
        // wrong in both directions.
        public readonly Dictionary<string, TotalsEntry> PetFields =
            new Dictionary<string, TotalsEntry>(StringComparer.Ordinal);

        // A PET BONUS FROM A SKILL, A DEVOTION OR A SET IS NOT ROLLED.
        //
        // Only an ITEM's is: there the bonus has a seed and a span of its own. A skill has neither
        // — the record holds a list by level, and the word of that level is the one to take, as in
        // the tooltip.
        //
        // Until 19 August this went down the common path with seed = 0, and a skill's bonus WAS
        // rolled from the list's first number. A seed of 0 puts every roll at the low end of the
        // span, and nonsense came out: Master of Death at level 15 gives "+97% total damage to
        // pets" (the list `8;15;22;…;97;…`, the 15th word), the skill's tooltip printed 97
        // correctly, and the SUM got 7 — ceil(8 × 0.8) of the very first word. Found when the pet
        // damage was compared with auras raised: switching Master of Death on added 7% instead of
        // 97%.
        void AddPet(GameData game, DbRecord rec, int slot, string source)
        { AddPet(game, rec, slot, source, 0, false); }

        // AN ITEM'S PET BONUS IS ROLLED, and it has a stream of its own from the item's seed.
        //
        // Measured 12 August on a cultist's grimoire and confirmed by number on 14 August on two
        // items of `_Amakata` — seven numbers in a row, all matching:
        //   a blood-oath ring (seed 1566260536): health% 9 on the first roll, total damage% 49 on
        //       the second — our order;
        //   a voidmage cord (seed 164465271): total damage 66, lightning% 1415, pierce 40,
        //       lightning 43, vitality 28 — rolls 1–5 in a row, our order again.
        //
        // THE ITEM'S SCALE DOES NOT APPLY TO IT: the cord's scale is 30 while the game prints the
        // lightning as `[1040-1560]`, i.e. exactly ±20% of the record's 1300, with no ×1.3. So the
        // scale stays zero here.
        //
        // Before this the numbers were taken straight from the record, i.e. every pet bonus stood
        // at the middle of its span instead of what was rolled.
        void AddPet(GameData game, DbRecord rec, int slot, string source, uint seed)
        { AddPet(game, rec, slot, source, seed, true); }

        void AddPet(GameData game, DbRecord rec, int slot, string source, uint seed, bool rolled)
        {
            if (rec == null) return;
            DbRecord pet = game.Record(rec.Get("petBonusName"));
            if (pet == null) return;
            PetSources++;
            Dictionary<string, double> known = null;
            if (rolled)
            {
                // THE SPREAD COMES FROM THE RECORD THAT NAMED THE BONUS (`rec`), not from the bonus
                // itself: a bonus never has a `lootRandomizerJitter` of its own, and 20% must not
                // be assumed. Measured on a toxic groble rod of `_Relo` (in the stash): the bonus
                // was named by a PREFIX with spread 10, and the game prints "+73% total damage
                // [66-80]"; at 20% it would have been [59-87] and the number 84.
                //
                // A seed of zero comes here too: it is a lawful seed, and with it every roll sits
                // at the low end of its span (see `ItemViewFactory.Build`).
                known = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, long> kv in
                         ItemOrder.RollAll(pet, seed, 0, ItemViewFactory.JitterOf(rec)))
                    known[kv.Key] = kv.Value;
            }
            Add(PetFields, pet, slot, source, false, 0, known);
            PetSource one = new PetSource();
            one.Rec = pet; one.Known = known; one.Name = source;
            PetRecords.Add(one);
        }

        public int PetSources;

        // The sum as a record, so the ordinary stat table can describe it.
        public DbRecord AsRecord(IEnumerable<string> only) { return AsRecord(Fields, only); }

        public static DbRecord AsRecord(Dictionary<string, TotalsEntry> from, IEnumerable<string> only)
        {
            DbRecord synthetic = new DbRecord();
            synthetic.Path = "<character bonus totals>";
            foreach (string field in only)
            {
                TotalsEntry e;
                if (!from.TryGetValue(field, out e)) continue;
                synthetic.Fields[field] = e.Value.ToString("0.###", CultureInfo.InvariantCulture);
            }
            return synthetic;
        }

        // THE SAME FOR THE PET PANEL: "crowd control resistance" IS SPREAD OVER THE INDIVIDUAL
        // EFFECTS and has no line of its own.
        //
        // Measured 3 September on `_666`, whose `defensiveCrowdControl` is 23; the game's panel
        // prints
        //     freeze     48 = 25 ours + 23
        //     trap       23 =  0      + 23
        //     petrify    23 =  0      + 23
        //     stun       80 — the cap (ours 144 + 23 = 167, everything above 80 is cut)
        // and there is NO "crowd control reduction" line in the panel at all: with no common line,
        // it goes into the individual ones.
        //
        // TWO EXCEPTIONS, BOTH MEASURED RATHER THAN DEDUCED:
        //   SLEEP   the game prints 0 where the rule would give 23. Why is not known; the rule is
        //           to write down the measurement, not the guess;
        //   SLOW    the game prints 48 — our number with nothing added (48 + 23 = 71 does not
        //           match), so slow does not belong to "crowd control".
        // Skill disruption (`defensiveDisruption`) has never been observed — we have no item
        // carrying it together with crowd control — and is left alone.
        static readonly string[] CrowdControlled =
            { "defensiveStun", "defensiveTrap", "defensivePetrify", "defensiveFreeze" };

        public static DbRecord AsPetRecord(Dictionary<string, TotalsEntry> from, IEnumerable<string> only)
        { List<string> ignore; return AsPetRecord(from, only, out ignore); }

        // THE SAME RULE FOR THE CHARACTER. Until 25 September it stood for pets ONLY: `_666` had no
        // `defensiveCrowdControl` of his own, so that case told nothing apart, and carrying a rule
        // over without a measurement is not allowed.
        //
        // MEASURED 25 September on `_111`, wearing a relic with `defensiveCrowdControl` (rolled
        // 13). The game's panel prints:
        //     stun 13%   trap 13%   petrify 13%   freeze 13%
        //     slow 0%    skill disruption 0%   life and energy leech 0%
        // There is no "crowd control reduction" line in the panel, and no SLEEP line in the
        // character panel at all. `_111` has none of those resistances of his own, so all four 13%
        // are exactly the relic's contribution: the same rule as for pets, through the same four
        // fields.
        public static DbRecord AsPlayerRecord(Dictionary<string, TotalsEntry> from, IEnumerable<string> only)
        {
            DbRecord made = AsRecord(from, only);
            SpreadCrowdControl(from, made);
            return made;
        }

        // Spread "crowd control" over the four effects and drop the common line.
        static void SpreadCrowdControl(Dictionary<string, TotalsEntry> from, DbRecord made)
        {
            TotalsEntry cc;
            if (!from.TryGetValue("defensiveCrowdControl", out cc) || Math.Abs(cc.Value) < 0.0001) return;
            made.Fields.Remove("defensiveCrowdControl");
            foreach (string field in CrowdControlled)
            {
                string had;
                double was = 0;
                if (made.Fields.TryGetValue(field, out had))
                    double.TryParse(had, NumberStyles.Float, CultureInfo.InvariantCulture, out was);
                made.Fields[field] = (was + cc.Value).ToString("0.###", CultureInfo.InvariantCulture);
            }
        }

        // WHAT HITS THE 80% CAP FOR A PET, AND WHY IT IS HERE.
        //
        // The game's panel prints pets an already CLAMPED number while we printed the raw one:
        // "167% stun duration reduction" where the game writes 80%. Asked for on 3 September in the
        // same shape the CHARACTER's stats use ("Poison and acid: 93% (+75% over the cap), cap
        // 93%").
        //
        // MEASURED (screenshots of the pet panel):
        //   19 August, `_Izaar`   aether 159 → 80, chaos 177 → 80, stun 147 → 80
        //    3 September, `_666`  stun 167 → 80; fire, cold, lightning and aether 80 against our 97
        //                         (elemental) and 99 (aether)
        // So the cap is the same for resistances to DAMAGE and for STUN duration reduction — one
        // family, as with the character.
        //
        // WHAT IS TAKEN BY ANALOGY AND HAS NO MEASUREMENT (said here so it need not be hunted):
        //   * the other effects of the same family (`defensiveFreeze`, `…Trap`, `…Petrify`,
        //     `…Convert`) — only stun was measured, but it moves together with them under one
        //     `defensiveCrowdControl`, and separating them would be stranger;
        //   * raising the cap itself ("+N% to maximum resistance" in a pet bonus) — counted by the
        //     same `CapFor` as for the character, where the rule is measured (MECHANICS 17d); we
        //     have no pet bonus carrying that field yet.
        // What is NOT touched at all: `defensiveTotalSpeedResistance`, `defensivePercentCurrentLife`
        // and `defensiveProtectionModifier` — they are not resistances and were not in the
        // measurement.
        public static DbRecord AsPetRecord(Dictionary<string, TotalsEntry> from, IEnumerable<string> only,
                                           out List<string> capped)
        {
            DbRecord made = AsRecord(from, only);
            capped = new List<string>();
            SpreadCrowdControl(from, made);
            foreach (string field in Capped)
            {
                string had;
                double raw;
                if (!made.Fields.TryGetValue(field, out had) ||
                    !double.TryParse(had, NumberStyles.Float, CultureInfo.InvariantCulture, out raw)) continue;
                int cap = PetCapFor(from, field);
                if (raw <= cap + 0.005) continue;
                made.Fields[field] = cap.ToString(CultureInfo.InvariantCulture);
                capped.Add(field + "\t" + cap.ToString(CultureInfo.InvariantCulture) + "\t" +
                           (raw - cap).ToString("0.#", CultureInfo.InvariantCulture));
            }
            return made;
        }

        // How to name a clamped field in the "over the cap" line. The words come from the game by
        // the same tags the line itself uses; no names of our own are invented here.
        public static string PetCapWord(GameData game, string field)
        {
            foreach (KeyValuePair<string, string> kv in StatGroups.ResistFields(game))
                if (kv.Key == field) return kv.Value.ToLowerInvariant();
            // The words below come from the language file: the game has no short tags for all six.
            if (field == "defensiveElementalResistance") return Lang.T("totals.capWord.elemental");
            if (field == "defensiveStun") return Lang.T("totals.capWord.stun");
            if (field == "defensiveFreeze") return Lang.T("totals.capWord.freeze");
            if (field == "defensiveTrap") return Lang.T("totals.capWord.trap");
            if (field == "defensivePetrify") return Lang.T("totals.capWord.petrify");
            if (field == "defensiveConvert") return Lang.T("totals.capWord.convert");
            return field;
        }

        // The fields the panel clamps — resistances to damage, elemental as one number (a pet bonus
        // keeps it in a field of its own) and the crowd-control family.
        static readonly string[] Capped =
            { "defensiveFire", "defensiveCold", "defensiveLightning", "defensivePoison",
              "defensivePierce", "defensiveAether", "defensiveChaos", "defensiveLife",
              "defensiveBleeding", "defensivePhysical", "defensiveElementalResistance",
              "defensiveStun", "defensiveTrap", "defensivePetrify", "defensiveFreeze",
              "defensiveConvert" };

        // The cap for one field of a PET BONUS. Counted in the same order as `CapFor` for the
        // character but from its own dictionary: pets have a channel of their own.
        static int PetCapFor(Dictionary<string, TotalsEntry> pet, string field)
        {
            int cap = BaseResistCap;
            TotalsEntry e;
            if (pet.TryGetValue(field + "MaxResist", out e)) cap += (int)Math.Round(e.Value);
            if ((StatGroups.IsElemental(field) || field == "defensiveElementalResistance") &&
                pet.TryGetValue("defensiveElementalResistanceMaxResist", out e)) cap += (int)Math.Round(e.Value);
            if (pet.TryGetValue("defensiveAllMaxResist", out e)) cap += (int)Math.Round(e.Value);
            return Math.Min(100, cap);
        }

        // Resistances: a cap of 80% plus whatever raises it. The game shows the clamped value and
        // the surplus separately — "Chaos 84% (+53% over the cap)". The cap itself is
        // `playerDefenseCap` from `gameengine.dbr`, filled in while the sum is built (`Of`); 80 is
        // the fallback, the number that used to stand here as a constant.
        public static int BaseResistCap = 80;

        public int CapFor(string resistField)
        {
            int cap = BaseResistCap;
            TotalsEntry e;
            if (Fields.TryGetValue(resistField + "MaxResist", out e)) cap += (int)Math.Round(e.Value);
            if (StatGroups.IsElemental(resistField) &&
                Fields.TryGetValue("defensiveElementalResistanceMaxResist", out e)) cap += (int)Math.Round(e.Value);
            if (Fields.TryGetValue("defensiveAllMaxResist", out e)) cap += (int)Math.Round(e.Value);
            return Math.Min(100, cap);
        }

        // What the character has of one resistance. Elemental resistance is not a resistance of its
        // own: it counts towards fire, cold and lightning together, which is why the game's panel
        // has no row for it.
        // The same span for a resistance, elemental folded in.
        public void ResistSpread(string resistField, out double lo, out double hi)
        {
            lo = hi = 0;
            TotalsEntry e;
            if (Fields.TryGetValue(resistField, out e)) { lo += e.Low; hi += e.High; }
            if (StatGroups.IsElemental(resistField) &&
                Fields.TryGetValue("defensiveElementalResistance", out e)) { lo += e.Low; hi += e.High; }
        }

        public double ResistValue(string resistField)
        {
            double v = 0;
            TotalsEntry e;
            if (Fields.TryGetValue(resistField, out e)) v += e.Value;
            if (StatGroups.IsElemental(resistField) &&
                Fields.TryGetValue("defensiveElementalResistance", out e)) v += e.Value;
            return v;
        }
    }

    // Which heading a field belongs under. Grouping is by the same naming system
    // the stat table is built on, so a field the game adds in a patch lands in the
    // right place without anyone touching a list.
    public static class StatGroups
    {
        // A GROUP KNOWS ITS INTERNAL NAME, NOT ITS HEADING (17 September, the translation). The
        // window used to recognise sections by their heading — `Title == "Defence"` — and in another
        // language the armour zones and the block would have vanished silently. The heading is
        // printed by `Title(game, key)`.
        public class Group
        {
            public readonly string Key;
            public readonly List<string> Fields = new List<string>();
            public Group(string key) { Key = key; }
        }

        public const string Resistances = "resist";
        public const string Defence = "defence";

        // A GROUP'S HEADING. Where the game has the same word in its character panel we take it
        // from there (then it is translated into all of its languages); where it does not, from the
        // language file.
        public static string Title(GameData game, string key)
        {
            switch (key)
            {
                case "damage":      return Lang.T("totals.group.damage");
                case "overTime":    return Lang.T("totals.group.overTime");
                case "retaliation": return GameWord(game, "tagCharHeaderRetaliation", "totals.group.retaliation");
                case "debuff":      return Lang.T("totals.group.debuff");
                case Resistances:   return GameWord(game, "tagCharHeaderResistance", "totals.group.resist");
                case Defence:       return GameWord(game, "tagCharHeaderDefenseSection", "totals.group.defence");
                case "attributes":  return GameWord(game, "tagCharAttributeTitle", "totals.group.attributes");
                case "skills":      return Lang.T("totals.group.skills");
                default:            return Lang.T("totals.group.other");
            }
        }

        // The game's word, or the language file's text when there is no game or no tag.
        static string GameWord(GameData game, string tag, string langKey)
        {
            string v = game == null ? null : game.Tag(tag);
            return string.IsNullOrEmpty(v) ? Lang.T(langKey) : v;
        }

        // The ten resistances, in the order the game's panel lists them, with the wording it uses.
        // The block is drawn by hand because every number needs its cap beside it — and because
        // elemental resistance is not a row of its own: it goes into fire, cold and lightning at
        // once.
        //
        // THE NAMES COME FROM THE GAME'S PANEL, `tagStatsResistance01…10` (decided 17 September, so
        // that the game's own translation is used). Before that we had words of our own and two
        // differed — "Pierce" and "Physical" against the game's "Pierce Damage" and "Physical
        // Damage". The tag number follows the game's order, not ours: 01 fire, 02 lightning,
        // 03 cold, 04 poison, 05 pierce, 06 bleeding, 07 vitality, 08 aether, 09 physical,
        // 10 chaos. With no game or no tag, the language file.
        public static List<KeyValuePair<string, string>> ResistFields(GameData game)
        {
            string[,] t = {
                { "defensiveFire", "tagStatsResistance01" }, { "defensiveCold", "tagStatsResistance03" },
                { "defensiveLightning", "tagStatsResistance02" }, { "defensivePoison", "tagStatsResistance04" },
                { "defensivePierce", "tagStatsResistance05" }, { "defensiveAether", "tagStatsResistance08" },
                { "defensiveChaos", "tagStatsResistance10" }, { "defensiveLife", "tagStatsResistance07" },
                { "defensiveBleeding", "tagStatsResistance06" }, { "defensivePhysical", "tagStatsResistance09" },
            };
            List<KeyValuePair<string, string>> made = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < t.GetLength(0); i++)
            {
                string name = game == null ? null : game.Tag(t[i, 1]);
                if (string.IsNullOrEmpty(name)) name = ResistFallback(t[i, 0]);
                made.Add(new KeyValuePair<string, string>(t[i, 0], name));
            }
            return made;
        }

        static string ResistFallback(string field)
        {
            switch (field)
            {
                case "defensiveFire": return Lang.T("totals.resist.fire");
                case "defensiveCold": return Lang.T("totals.resist.cold");
                case "defensiveLightning": return Lang.T("totals.resist.lightning");
                case "defensivePoison": return Lang.T("totals.resist.poison");
                case "defensivePierce": return Lang.T("totals.resist.pierce");
                case "defensiveAether": return Lang.T("totals.resist.aether");
                case "defensiveChaos": return Lang.T("totals.resist.chaos");
                case "defensiveLife": return Lang.T("totals.resist.life");
                case "defensiveBleeding": return Lang.T("totals.resist.bleeding");
                default: return Lang.T("totals.resist.physical");
            }
        }

        public static bool IsElemental(string field)
        {
            return field == "defensiveFire" || field == "defensiveCold" || field == "defensiveLightning";
        }

        public static List<Group> Split(IEnumerable<string> fields)
        {
            Group damage = new Group("damage");
            Group overTime = new Group("overTime");
            Group retaliation = new Group("retaliation");
            Group debuff = new Group("debuff");
            Group resist = new Group(Resistances);
            Group defence = new Group(Defence);
            Group attributes = new Group("attributes");
            Group skills = new Group("skills");
            Group other = new Group("other");

            List<string> sorted = new List<string>(fields);
            sorted.Sort(StringComparer.Ordinal);
            foreach (string f in sorted)
            {
                if (f.StartsWith("augment", StringComparison.Ordinal)) skills.Fields.Add(f);
                else if (f.StartsWith("retaliation", StringComparison.Ordinal)) retaliation.Fields.Add(f);
                else if (f.StartsWith("offensiveSlow", StringComparison.Ordinal)) overTime.Fields.Add(f);
                else if (CharacterTotals.IsStrongestWins(f)) debuff.Fields.Add(f);
                else if (f.StartsWith("offensive", StringComparison.Ordinal)) damage.Fields.Add(f);
                else if (f.StartsWith("defensive", StringComparison.Ordinal))
                {
                    // Resistances are the defensive family named after a damage
                    // type; the rest of it is armour, block and crowd control.
                    if (IsResistance(f)) resist.Fields.Add(f); else defence.Fields.Add(f);
                }
                else if (f.StartsWith("character", StringComparison.Ordinal) ||
                         f.StartsWith("skillCooldown", StringComparison.Ordinal) ||
                         f.StartsWith("skillMana", StringComparison.Ordinal) ||
                         f.StartsWith("skillLife", StringComparison.Ordinal)) attributes.Fields.Add(f);
                else other.Fields.Add(f);
            }

            List<Group> all = new List<Group> { damage, overTime, retaliation, debuff,
                                                resist, defence, attributes, skills, other };
            all.RemoveAll(delegate (Group g) { return g.Fields.Count == 0; });
            return all;
        }

        static readonly string[] Types = {
            "Physical", "Pierce", "Fire", "Cold", "Lightning", "Poison",
            "Life", "Aether", "Chaos", "Bleeding", "Elemental" };

        static bool IsResistance(string field)
        {
            foreach (string t in Types)
                if (field.StartsWith("defensive" + t, StringComparison.Ordinal)) return true;
            return field.StartsWith("defensiveAllMaxResist", StringComparison.Ordinal)
                || field.StartsWith("defensiveAllResistance", StringComparison.Ordinal);
        }
    }


    // ================== ARMOUR BY BODY ZONE ==================
    //
    // The game shows armour as SIX numbers, one per body zone, with an "armour rating" beside them
    // — their average weighted by how often that zone is hit. The weights lie openly in
    // `records/game/combatformulas.dbr` and the game prints them in the same panel: head 15%,
    // shoulders 15%, chest 26%, arms 12%, legs 20%, feet 12%.
    //
    // THE RULE, MEASURED 31 AUGUST ON TWO CHARACTERS (twelve zone numbers and both ratings, all to
    // the unit — MECHANICS 20):
    //
    //     zone   = (the armour of THIS slot's item + all other armour) × (1 + % armour)
    //     rating = Σ zone × weight / 100
    //
    // The game says it in the panel itself: "Bonuses from skills and items are added to all armour
    // zones" (`screen-relo-armor1.jpg`). "Its own" includes the armour of a COMPONENT screwed into
    // the item: on `_Relo` the helm and the shoulders carry 1666 armour each, but the shoulders
    // also have "+24 armour bonus" from a component — and their zone is exactly 24 × 1.53 = 37
    // above the helm's (3106 against 3069).
    //
    // A SHIELD'S ARMOUR GOES INTO THE SHARED PART — MEASURED, not assumed. A shield has no zone of
    // its own, and its armour lands in all six, like a belt's, an amulet's, a ring's, a medal's or
    // a relic's.
    //
    // The measurement (31 August): no shield in the game has armour of its own (a census of 469
    // `shields` records finds `defensiveProtection` on none), but it can be put there by a
    // component (+24, chest/shoulders/SHIELD) or an augment (+35, shield only). The component went
    // into `_222`'s shield with nothing else changed, and both answers were named BEFORE the
    // screenshot:
    //
    //     before            3674 3674 4054 2793 3496 3162
    //     as shared         3711 3711 4091 2831 3534 3200   ← the game
    //     not counted       the numbers do not move at all
    //
    // No fix was needed: a shield lies in `Weapons1`, has no zone slot, and its armour already fell
    // into the shared part. The experiment confirmed the default and thereby made it a measurement.
    public class ArmourZones
    {
        // The save slots whose armour belongs to a zone, and each zone's weight, in the order the
        // game prints them. The zone names come from the game's armour panel ("Head:",
        // "Shoulders:" …; the colon is trimmed, ours is a table with columns). With no game or no
        // tag, the language file. The order matches `Slots`.
        static readonly string[] NameTags =
            { "tagCharStatsArmorHead", "tagCharStatsArmorShoulders", "tagCharStatsArmorChest",
              "tagCharStatsArmorArms", "tagCharStatsArmorLegs", "tagCharStatsArmorFeet" };
        public static int Count { get { return NameTags.Length; } }

        public static string Name(GameData game, int i)
        {
            string v = game == null ? null : game.Tag(NameTags[i]);
            if (!string.IsNullOrEmpty(v)) return v.TrimEnd().TrimEnd(':').TrimEnd();
            switch (i)
            {
                case 0: return Lang.T("totals.zone.head");
                case 1: return Lang.T("totals.zone.shoulders");
                case 2: return Lang.T("totals.zone.chest");
                case 3: return Lang.T("totals.zone.arms");
                case 4: return Lang.T("totals.zone.legs");
                default: return Lang.T("totals.zone.feet");
            }
        }
        static readonly int[] Slots = { 0, 9, 2, 5, 3, 4 };
        static readonly string[] Weights =
            { "combatRegionHeadChance", "combatRegionShouldersChance", "combatRegionTorsoChance",
              "combatRegionArmsChance", "combatRegionLegsChance", "combatRegionFeetChance" };

        public readonly double[] Zone = new double[6];
        public readonly double[] Weight = new double[6];
        public double Shared;      // armour belonging to no zone
        public double Percent;     // "+N% armour"
        public double Rating;      // the armour rating — the weighted average of the zones
        // BLOCK. Here rather than in a class of its own: these are two more numbers of the same
        // defence panel, made of the shield and the bonuses, counted by the game's own equation.
        //
        //     block chance = defensiveBlockChance + defensiveBlockModifier
        //                    (`meleeBlockEquation` in combatformulas.dbr — ADDITION)
        //     blocked      = defensiveBlock × (1 + defensiveBlockAmountModifier/100)
        //
        // Checked 31 August on `_222`: 35 + 44 = 79 against the game's 79, and
        // 1230 × 4.01 = 4932.3 against the game's 4932. These two numbers are what led to the roll
        // order of a relic (`ItemOrder`): before that fix they came out 80 and 5068.
        //
        // WHAT THE MEASUREMENT DOES NOT SAY: whether the blocked amount is rounded or truncated.
        // `_222` gives 4932.3 and both ways give 4932; we have no second character with a shield —
        // `_Relo` blocks 0% (`screen-relo-def1.jpg`). Truncation is taken, as for offensive and
        // defensive ability, where it IS measured.
        public double BlockChance, BlockDamage;
        // ARMOUR ABSORPTION — the panel's third column. The base is in the game
        // (`records/game/gameengine.dbr`, `armorDefensiveAbsorption 70`) and the bonuses MULTIPLY
        // it rather than add to it:
        //
        //     absorption = min(100, 70 × (1 + defensiveAbsorptionModifier/100))
        //
        // Measured 31 August on SIX characters, four of which tell the two readings apart (addition
        // would give `_Amakata` 98% instead of 90):
        //
        //     _Jien      +0%   → 70        game 70     _Viliya    +12% → 78.4 → 78    game 78
        //     _Amakata  +28%   → 89.6 → 90 game 90     _Relo-shield +38% → 96.6 → 97  game 97
        //     _222      +48%   → 103.6     game 100    _Hamarina  +62% → 113.4        game 100 (cap)
        //
        // AND IT IS ONE FOR ALL SIX ZONES, not per item. That is a measurement of its own and it
        // argues with the common article on armour, where a helm's absorption is applied to the
        // head alone: `_Amakata`'s +28% comes from components in the FEET (8) and SHOULDERS (20),
        // and the game prints 90% on all six lines.
        public double Absorb;
        public bool HasBlock;
        public bool Any;

        public static ArmourZones Of(GameData game, CharacterTotals t)
        {
            ArmourZones z = new ArmourZones();
            if (game == null || t == null) return z;
            // Block is counted apart from armour: a character can have a shield and no armour, and
            // the other way round.
            double blockBase = Value(t, "defensiveBlock");
            if (blockBase > 0)
            {
                z.BlockChance = Value(t, "defensiveBlockChance") + Value(t, "defensiveBlockModifier");
                z.BlockDamage = Math.Floor(blockBase * (1 + Value(t, "defensiveBlockAmountModifier") / 100));
                z.HasBlock = true;
            }
            double total = Value(t, "defensiveProtection") + Value(t, "defensiveBonusProtection");
            if (total <= 0) return z;
            z.Percent = Value(t, "defensiveProtectionModifier");
            double mine = 0;
            for (int i = 0; i < Slots.Length; i++) mine += t.SlotArmour[Slots[i]];
            z.Shared = total - mine;
            if (z.Shared < 0) z.Shared = 0;
            DbRecord cf = game.Record("records/game/combatformulas.dbr");
            double sum = 0, weights = 0;
            for (int i = 0; i < Slots.Length; i++)
            {
                // THE RATING IS COUNTED FROM THE EXACT ZONES, NOT THE ROUNDED ONES, and that is a
                // measurement of its own. For the first six characters both readings gave the same
                // number; the shield experiment of 31 August told them apart: a +24 armour
                // component went into `_222`'s shield and the game showed a rating of 3608 with
                // zones 3711 / 3711 / 4091 / 2831 / 3534 / 3200. From the rounded zones that is
                // 3607.48 → 3607, from the exact ones 3607.67 → 3608. So only the display of a
                // zone is rounded.
                double exact = (t.SlotArmour[Slots[i]] + z.Shared) * (1 + z.Percent / 100);
                z.Zone[i] = Math.Round(exact);
                z.Weight[i] = cf == null ? 0 : cf.GetInt(Weights[i]);
                sum += exact * z.Weight[i];
                weights += z.Weight[i];
            }
            z.Rating = weights > 0 ? sum / weights : 0;
            DbRecord ge = game.Record("records/game/gameengine.dbr");
            double baseAbsorb = ge == null ? 70 : ge.GetInt("armorDefensiveAbsorption");
            z.Absorb = Math.Round(baseAbsorb * (1 + Value(t, "defensiveAbsorptionModifier") / 100));
            if (z.Absorb > 100) z.Absorb = 100;
            z.Any = true;
            return z;
        }

        static double Value(CharacterTotals t, string field)
        {
            TotalsEntry e;
            return t.Fields.TryGetValue(field, out e) ? e.Value : 0;
        }
    }
}
