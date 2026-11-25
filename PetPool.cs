// THE PET POOL: who can stand beside the character at all.
//
// WHY. A pet is a second body with stats of its own, fed through its own channel
// (`petBonusName`); the character's ordinary damage percentages do not reach it.
// Without the pool, half of a summoner's damage is not counted.
//
// WHICH SKILL SUMMONS WHAT: the field `spawnObjects` — pet records by skill level,
// with `petLimit` beside it (a list too) and `spawnObjectsTimeToLive` (seconds; no
// field means permanent). The record CLASS does not tell: Summon Blight Fiend is
// `Skill_TargetedSpawnPet`, Reap Spirit is `Skill_AttackSpellChaosSpawnPet`, and
// the item-granted Conjure Eldritch Webweaver has a class of its own.
//
// WHAT IS HERE: who, from where, how many at once, how long they live, which pet
// record answers this skill level, and the pet's own skills. NOT its health and
// damage — those come from `characterAttributeEquations` the way enemies do.

using System;
using System.Collections.Generic;

namespace Gdc.Gui
{
    public static class PetPool
    {
        public class Pet
        {
            public string Skill;            // the summoning skill's record — the key
            public string Title;            // what the game calls the skill
            public string Source;           // a mastery or an item
            public string Dbr;              // the pet record at this skill level
            public string Name;             // what the game calls the pet
            public int Level;               // skill level with bonuses
            public int Limit;               // how many may stand at once
            public int Seconds;             // lifetime; 0 means permanent
            public readonly List<string> Skills = new List<string>();
            // The same skills as records and levels — their numbers are drawn from these.
            public readonly List<string> SkillDbrs = new List<string>();
            public readonly List<int> SkillLevels = new List<int>();
            // EVERY record the pet really carries, including the ones with no game
            // name. The pet's "gear" conversion is counted from these.
            public readonly List<string> Passives = new List<string>();
            // Their levels, in the same order. The timeline needs them: some pets'
            // innate attack is NAMELESS (Blighted Claws), never reaches `Skills`,
            // and without a level its numbers cannot be read.
            public readonly List<int> PassiveLevels = new List<int>();
            // Of those, the ones granted by a tree node or an item, not innate.
            public readonly List<string> Granted = new List<string>();
            // Pet bonuses its damage is multiplied by (see DamageMult).
            public Dictionary<string, double> Bonus = new Dictionary<string, double>(StringComparer.Ordinal);
            // The CHARACTER'S OWN multipliers by damage field — for skills that
            // scale with the player rather than with pet bonuses (see DamageMult).
            public Dictionary<string, double> Scale = new Dictionary<string, double>(StringComparer.Ordinal);
            // Takes the PLAYER'S bonuses, not the pet ones: record class
            // `PetPlayerScaling`. The game says so in the summon's tooltip. See
            // `PlayerScale`.
            public bool PlayerScaling;
            // THE PET'S "GEAR": conversion whose numbers the game does NOT print.
            // Pet bonuses (or the player's conversion when player-scaling) and the
            // modifiers handed out by the owner's items. Measured — 17zh, six numbers.
            public List<Move> ConvMoves = new List<Move>();
            // ITS OWN: conversion of the INNATE passives, which the game DOES print.
            // Measured at `ConversionsOf`.
            public List<Move> OwnConv = new List<Move>();
            // Both halves together, for the "this pet's conversion" listing.
            public List<Move> AllConv
            {
                get
                {
                    List<Move> all = new List<Move>(ConvMoves);
                    all.AddRange(OwnConv);
                    return all;
                }
            }
            public bool Default;            // do we count it as raised by default
            public bool Chosen;             // what the user decided
            public double BaseLife;         // health from its "biography" equation
            public double LifeBonus;        // our pet health bonus, per cent
            public double OwnLife;          // its own skills, per cent
            // THE PET'S ARMOUR (user's request, 5 September). Built like health, only
            // the base is not from the "biography" but a FLAT number off its own
            // passive: `armorpets01` for ordinary pets (424 at character level 100),
            // `armorbase01` for some. Percentages add up as health does: its own plus
            // the pet bonus. The necromancer's Will of the Crypt gives +128% (the
            // 15th word of the list — exactly what the game prints), item pet bonuses
            // another +17%.
            //
            // THE TOTAL IS NOT MEASURED: the game prints a pet's armour nowhere, so
            // there is nothing to check against. The PARTS agree — the flat number
            // and both percentages.
            public double BaseArmour;
            public double OwnArmour;        // per cent from its own skills
            public double ArmourBonus;      // per cent from the pet bonus
            public double Armour
            { get { return Math.Floor(BaseArmour * (1 + (OwnArmour + ArmourBonus) / 100)); } }
            // …and what it is made of, by name, for the breakdown.
            public readonly List<string> OwnLifeFrom = new List<string>();
            public double AuraLife;         // the character's auras covering it, per cent
            // Their names, so the window shows where the gain came from.
            public readonly List<string> Auras = new List<string>();
            // The total. The aura IS included: it covers the pet on the same footing
            // as pet bonuses (user's request, 19 August). One caveat: the summon
            // skill's own tooltip prints health WITHOUT auras — measured on two shots
            // of Reap Spirit — so with an aura up our number is higher, and that is
            // not a disagreement but a different question.
            public double Life { get { return Math.Floor(BaseLife * (1 + (OwnLife + LifeBonus + AuraLife) / 100)); } }
        }

        // Does the record summon — and which list of pet records does it name.
        public static string SpawnList(DbRecord rec)
        { return rec == null ? null : rec.Get("spawnObjects"); }

        // ============ A SUMMON'S TRANSMUTER SWAPS THE PET LIST ============
        //
        // Class `Skill_SpawnPetTransmuter`, field `modSpawnObjects` — the same list
        // by level as `spawnObjects`, only with DIFFERENT pet records. Take the
        // transmuter and the summon bears those: the shaman gets `stormtotem18b`
        // instead of `stormtotem18`, and that record carries the transmuter as an
        // innate skill, which sends all the totem's damage through "100% conversion:
        // lightning into vitality".
        //
        // HOW THE GAME KNOWS WHICH SUMMON A TRANSMUTER BELONGS TO. It has no field
        // of its own, but modifier binding is already done in one place —
        // `AttackSkills.Attach` — by two roads: the game's `skillDependancy` first,
        // and failing that the tree, not by coordinates but by a MARK ON THE PARENT:
        // a skill with a transmuter bound to it carries `connectortransmuterstub` in
        // `skillConnectionOn`, and distance only chooses among the marked ones.
        // Storm Totem carries that mark — `skills_connectorontransmuterdown.tex`.
        //
        // No binding of its own here, ON PURPOSE: guessing by file name
        // (`stormtotem01` ↔ `stormtotem01b_petmodifier`) is not allowed, and two
        // bindings in two places would drift apart silently.
        // HOW MANY PETS AT ONCE: the summon record PLUS modifiers — from ITEMS and
        // from the TREE.
        //
        // Item modifiers counted from 19 August (17a: Ghol's Malice gives the Blight
        // Fiend +1); TREE NODES did not, and the number showed it — the game printed
        // "8 maximum summoned" for the necromancer where we stood at 3.
        //
        //   `summon_skeleton1`  petLimit 3                      — the record itself
        //   `summon_skeleton2`  petLimit by level, 5 at the 13th — Undead Legion
        //   3 + 5 = 8, and the node's tooltip prints exactly "+5 maximum summoned"
        //
        // The node is bound to the summon by the same shared `AttackSkills.Attach`
        // the character's own hit uses: `skillDependancy`, and failing that the mark
        // in the tree. None of its own here, and there must not be (MECHANICS 28).
        static Dictionary<string, double> TreeLimits(GameData game, Character c,
                                                     SkillText.LevelBonus bonus)
        {
            Dictionary<string, double> made =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (game == null || c == null) return made;
            AttackSkills.Attach attach = null;
            foreach (Skill s in c.Skills)
            {
                if (string.IsNullOrEmpty(s.Name) || s.Level <= 0) continue;
                DbRecord rec = game.Record(s.Name);
                if (rec == null || string.IsNullOrEmpty(rec.Get("petLimit"))) continue;
                // The summon has already given its own `petLimit` — MODIFIERS only here.
                if (!string.IsNullOrEmpty(SpawnList(rec))) continue;
                int at = s.Level + (bonus == null ? 0 : bonus.For(s.Name, s.Level));
                double add = AttackSkill.Value(rec, "petLimit", SkillText.Slot(rec, at));
                if (Math.Abs(add) < 0.0001) continue;
                if (attach == null) attach = AttackSkills.Attach.Of(game, c);
                string parent = null;
                foreach (string one in attach.Parents(game, s.Name)) { parent = one; break; }
                if (string.IsNullOrEmpty(parent)) parent = attach.Extra(s.Name);
                if (string.IsNullOrEmpty(parent)) continue;
                double had;
                made[parent] = (made.TryGetValue(parent, out had) ? had : 0) + add;
            }
            return made;
        }

        static Dictionary<string, string> SpawnSwaps(GameData game, Character c,
                                                     SkillText.LevelBonus bonus)
        {
            Dictionary<string, string> made =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (game == null || c == null) return made;
            AttackSkills.Attach attach = null;
            foreach (Skill s in c.Skills)
            {
                if (string.IsNullOrEmpty(s.Name) || s.Level <= 0) continue;
                DbRecord rec = game.Record(s.Name);
                if (rec == null) continue;
                if ((rec.Class ?? "").IndexOf("SpawnPetTransmuter", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string list = rec.Get("modSpawnObjects");
                if (string.IsNullOrEmpty(list)) continue;
                if (attach == null) attach = AttackSkills.Attach.Of(game, c);
                // ASK `Extra`, NOT `Parents`, AND THAT COST ONE RUN: `Attach` keeps
                // parents in TWO dictionaries — modifiers in `parent`
                // (`skillDependancy`, or the left neighbour in the same row), and
                // TRANSMUTERS and "secondaries" in `extra` (the parent's
                // `connectortransmuterstub` mark). `Parents` reads only the first and
                // on a transmuter silently hands back an EMPTY list, not an error.
                // The character's hit asks the same way (`AttackSkills`, the
                // `TreeRole.Transmuter` branch): one binding for everyone.
                string byField = null;
                foreach (string one in attach.Parents(game, s.Name)) { byField = one; break; }
                string parent = byField ?? attach.Extra(s.Name);
                if (!string.IsNullOrEmpty(parent)) made[parent] = list;
            }
            return made;
        }

        // DOES THE CHARACTER SUMMON ANYTHING AT ALL — a cheap question for the
        // timeline: building the whole pool just to answer "no pets" is dear, it
        // counts bonuses, conversion and modifiers. The summon may sit anywhere in
        // the record chain (Reap Spirit has it in the record itself), so the rule is
        // the same as for names and numbers: walk the chain.
        public static bool AnySummon(GameData game, Character c)
        {
            if (game == null || c == null) return false;
            foreach (Skill s in c.Skills)
                if (s.Level > 0 && !string.IsNullOrEmpty(s.Name) && SummonOf(game, s.Name) != null) return true;
            foreach (ItemSkill it in c.ItemSkills)
                if (!string.IsNullOrEmpty(it.SkillName) && SummonOf(game, it.SkillName) != null) return true;
            return false;
        }

        static DbRecord SummonOf(GameData game, string dbr)
        {
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(dbr); hop++)
            {
                DbRecord rec = game == null ? null : game.Record(dbr);
                if (rec == null) break;
                if (!string.IsNullOrEmpty(SpawnList(rec))) return rec;
                dbr = ItemViewFactory.NextSkillRecord(rec);
            }
            return null;
        }

        // THE PET'S OWN SKILLS: one field summed over all its passives.
        //
        // Each passive has a level of its own, written beside it (`skillLevelN`) and
        // not always as a number: an innate passive takes the SUMMON SKILL'S level,
        // the summoned armour `charLevel*1`, the spider `charLevel/4+1`. So the line
        // is read as an equation, like an enemy's "biography", with `charLevel` the
        // character's level; a plain number is an equation too.
        //
        // The game prints this sum as "Skills <pet>": a Sundered Wraith at level 19
        // reads "+108% health", and `petskill_raven_innate1` gives exactly 108 at its
        // 19th word.
        //
        // A SLOT LEVEL OF ZERO MEANS "THE CHARACTER DOES NOT HAVE THIS MODIFIER", and
        // that rule was missing here. Found 5 September on the necromancer: the game
        // printed "Skills Skeletal Warrior: +126% health" where we counted 206%.
        //
        //   126  slot 1, lvl 19   petskill_skeleton_basescaling      ← the real one
        //    60  slot 7, lvl  0   modifier_set_d103_raiseskeletons   ← modifiers the
        //     8  slot 11, lvl 0   modifier_a303a_skeleton               character has NOT
        //    12  slot 12, lvl 0   modifier_a303b_skeleton
        //
        // A zero slot took the FIRST step of the list (index `max(0, 0-1)` = 0), the
        // weakest word of the modifier — which is why the gap was small and did not
        // catch the eye. With the rule: 5942 × (1 + 126% + 203%) = 25491 against the
        // game's 25491.
        //
        // THE RULE IS NOT NEW: measured 20 August, written down as MECHANICS 17z and
        // applied then to the pet's SKILL LIST (`Add`, the `PetMods` branch). It just
        // never reached the health percentages — the third case that week of "put the
        // rule in one place where there were two".
        static double OwnPercent(GameData game, DbRecord pet, int charLevel, int skillLevel, string field,
                                 PetMods mods)
        { List<string> ignore; return OwnPercent(game, pet, charLevel, skillLevel, field, mods, out ignore); }

        // `from` — what "its own" is made of, by name. Without it there is no way to
        // check an "own 206%": a skeleton has fourteen slots, and which of them added
        // up is visible only by counting across four files in your head.
        static double OwnPercent(GameData game, DbRecord pet, int charLevel, int skillLevel, string field,
                                 PetMods mods, out List<string> from)
        {
            from = new List<string>();
            double total = 0;
            for (int i = 1; i <= 12; i++)
            {
                string dbr = pet.Get("skillName" + i);
                if (string.IsNullOrEmpty(dbr)) continue;
                DbRecord rec = game.Record(dbr);
                if (rec == null) continue;
                string raw = rec.Get(field);
                if (string.IsNullOrEmpty(raw)) continue;
                double at;
                string when = pet.Get("skillLevel" + i);
                if (string.IsNullOrEmpty(when) ||
                    !Equation.TryEval(when, delegate (string name)
                    {
                        return string.Equals(name, "charLevel", StringComparison.OrdinalIgnoreCase)
                             ? charLevel : 0;
                    }, out at)) at = skillLevel;
                // A modifier slot: the level comes from whoever handed it out, and
                // with no giver there is no skill (17z, see above).
                if (at <= 0)
                {
                    if (mods == null || !mods.Has(dbr)) continue;
                    at = mods.LevelOf(dbr);
                    if (at <= 0) continue;
                }
                string[] steps = raw.Split(';');
                int slot = Math.Min(Math.Max(0, (int)at - 1), steps.Length - 1);
                double one;
                if (double.TryParse(steps[slot].Trim(), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out one))
                {
                    total += one;
                    from.Add(one.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                             "  (slot " + i + ", level " + ((int)at) + ")  " + dbr);
                }
            }
            return total;
        }

        // ============ THE DAMAGE OF A PET'S SKILL ============
        //
        // MEASURED 19 August on three pets and seven numbers, and it came out exactly
        // as the user guessed: like a weapon, only with pet bonuses in place of the
        // bonuses from gear.
        //
        //     damage = the record's number × (1 + total% to pets + the OWN type's %)
        //     dot    = the same, times its own seconds (the duration bonus is NOT in
        //              the printed number)
        //
        // Checked on `_Izaar` (pet bonuses: total 1223%, physical 100%, elemental
        // 351%, vitality 109%, poison 304%, bleeding 200%):
        //
        //   Wraith's Touch   cold  245 × 15.74 = 4101   game 4101
        //                    vit   184 × 14.32 = 2635   game 2635
        //                    vit   287 × 14.32 = 4110   game 4110
        //   Blighted Claws   phys  347 × 14.23 = 4938   game 4938
        //                    phys  473 × 14.23 = 6731   game 6731
        //                    pois  190 × 16.27 × 5 s = 15456   game 15455
        //   Thorned Claws    phys  280 × 14.23 = 3984   game 3984
        //                    phys  346 × 14.23 = 4924   game 4924
        //                    bleed 187 × 15.23 × 3 s = 8544    game 8544
        //
        // COLD TAKES THE ELEMENTAL PERCENTAGE, and the same lines show it: `_Izaar`
        // has no cold percentage at all, yet 15.74 = 1 + 12.23 + 3.51, where 3.51 is
        // "+351% elemental damage to pets". So elemental lies on fire, cold and
        // lightning just as it does for the character.
        static readonly string[] Elemental = { "Fire", "Cold", "Lightning" };

        // ============ TWO KINDS OF PET: WHOSE BONUSES THEY TAKE ============
        //
        // THE GAME SAYS SO ITSELF, and the user noticed it on 4 September: most
        // summon tooltips read "<pet> scales with PET bonuses", while Storm Totem and
        // Wendigo Totem read "scales with PLAYER bonuses". A whole second kind of
        // summoned creature, and we were not looking at it at all.
        //
        // THE FILES CARRY THE MARK — two of them, and they agree:
        //
        //   * THE PET RECORD'S CLASS: `Pet` against `PetPlayerScaling`. Of `_666`'s
        //     ten summons exactly one is `PetPlayerScaling` — the totem;
        //   * THE FIELD `isPetBonusScaling` ON THE SKILL RECORD: present on all nine
        //     skills that agreed under the pet rule, and on none of Storm Strike's
        //     four records.
        //
        // THEY ARE TWO MARKS, NOT ONE, AND THEY DISAGREE. A census of the whole game
        // (6423 records under `pets/`): of `PetPlayerScaling`'s 4127 pairs 52 carry
        // `isPetBonusScaling`, and of `Pet`'s 4340 pairs 248 do not. They agree 97
        // times in a hundred, but the `pet_raven_*_player` family and the
        // necromancer's skeletons are mixed. So:
        //
        //   THE MULTIPLIER — by the SKILL's field (here): it is needed on the skill
        //     record, was measured that way, and `--petdmgtest` walks skills knowing
        //     no pet;
        //   CONVERSION — by the PET's class (`ConversionsOf`): it comes from "gear",
        //     lies on the creature as a whole, and the tooltip line sits on the SUMMON.
        //
        // On `_666`'s ten pets both marks agree, so the stand's numbers do NOT tell
        // these two apart. See MECHANICS 27zh.
        //
        // THE MEASUREMENT THAT SETTLED IT (`_666`, Storm Strike lvl 18, spirit ≈ 1507):
        //
        //   hit  126 × (1 + spirit/215 + 24% vitality + 443% total) = 1598   game 1598
        //        351 × the same                                     = 4450   game 4450
        //   dot  124 × (1 + spirit/200 + 0% decay + 443%) × 2 s     = 3216   game 3216
        //
        // The multipliers DIFFER between hit and dot (12.679 and 12.968), and that is
        // not an error but exactly how they differ for the character: a dot has its
        // own attribute divisor (spirit/200 against /215, MECHANICS 20). Both
        // equations yield the same spirit, 1507.7 and 1507.5 — that is the proof. The
        // pet rule would have given 13.69 and 1725–4805, off by 7%.
        //
        // WHY THIS IS THE CHARACTER'S OWN FUNCTION: the multipliers come from
        // `DamageModel.TypeMultiplier`, the very one an item tooltip prints a granted
        // skill's numbers with (MECHANICS 22k). No copy here, and there must not be.
        public static Dictionary<string, double> PlayerScale(GameData game, Character c,
                                                             CharacterTotals totals)
        {
            Dictionary<string, double> map = new Dictionary<string, double>(StringComparer.Ordinal);
            if (game == null || c == null || totals == null) return map;
            try
            {
                DamageModel dm = new DamageModel(game, c, totals);
                foreach (string type in new[] { "Physical", "Pierce", "Fire", "Cold", "Lightning",
                                                "Poison", "Life", "Aether", "Chaos", "Bleeding" })
                {
                    double m = dm.TypeMultiplier(type, false);
                    map["offensive" + type + "Min"] = m;
                    map["offensive" + type + "Max"] = m;
                    double md = dm.TypeMultiplier(type, true);
                    map["offensiveSlow" + type + "Min"] = md;
                    map["offensiveSlow" + type + "Max"] = md;
                }
            }
            // The display must not fall over because of the arithmetic: with no map
            // the skill prints the record's own numbers, as it did before 4 September.
            catch (Exception) { map.Clear(); }
            return map;
        }

        // Does a pet skill scale with PET bonuses? If not, with the player's.
        // THE VALUE, NOT THE PRESENCE: the template puts the field on every record
        // and most hold zero, while `--dbrdump` hides zeros — see
        // `ItemViewFactory.IsPetSkill`, where the same mistake broke the display.
        public static bool ScalesWithPetBonus(DbRecord rec)
        { return rec == null || AttackSkill.Value(rec, "isPetBonusScaling", 0) > 0.5; }

        // The multiplier for a damage field: the total percentage plus the type's own.
        static double DamageMult(Dictionary<string, double> pet, string field)
        {
            double m = 1 + Get(pet, "offensiveTotalDamageModifier") / 100;
            bool dot = field.StartsWith("offensiveSlow", StringComparison.Ordinal);
            string rest = field.Substring(dot ? 13 : 9);
            foreach (string tail in new[] { "Min", "Max" })
                if (rest.EndsWith(tail, StringComparison.Ordinal))
                { rest = rest.Substring(0, rest.Length - tail.Length); break; }
            m += Get(pet, "offensive" + (dot ? "Slow" : "") + rest + "Modifier") / 100;
            foreach (string one in Elemental)
                if (one == rest) { m += Get(pet, "offensive" + (dot ? "Slow" : "") + "ElementalModifier") / 100; break; }

            // "TOTAL DAMAGE MODIFIED BY N%" IS A SEPARATE MULTIPLIER OVER EVERYTHING,
            // for pets too. Field `offensiveDamageMultModifier`; the rule is the hit's
            // own (MECHANICS 19k), measured separately because the pet count goes its
            // own way.
            //
            // MEASURED 23 August by the user on the Rune of Kalastor — shots before
            // and after points went into Artifact Handling
            // (`playerclass07/pets/petskill_relictraining`, "total damage modified by
            // 6%"):
            //
            //   physical  533 – 710  →  565 – 753    533×1.06 = 565.0   710×1.06 = 752.6
            //   fire      2465       →  2613         2465×1.06 = 2612.9
            //
            // The ratio is exactly 1.06 on both lines — a multiplier, not an addend,
            // just as in the hit. The game counts runes as pets, and this record is
            // not in the attack skill tree at all: it is attached through
            // `PetPlayerScaling`. So the modifier-binding fix (19l) never touched it
            // and the field had to be brought in here separately.
            double mult = Get(pet, "offensiveDamageMultModifier");
            if (Math.Abs(mult) > 0.0001) m *= 1 + mult / 100.0;
            return m;
        }

        static double Get(Dictionary<string, double> map, string field)
        { double v; return map.TryGetValue(field, out v) ? v : 0; }

        // WHAT IS MULTIPLIED: FLAT damage only. One place must decide what damage is
        // — `ItemViewFactory.IsDamageField` — or everything merely beginning with
        // `offensive` goes under the multiplier. The first version did just that, and
        // Tooth and Claw came out with "2381 reduced offensive ability" instead of
        // 180, and "330 reduced resistances" instead of 25.
        //
        // Percentages (`…Modifier`) are not multiplied either: Plague Breath's "155%
        // weapon damage" is a share, not a damage number.
        static bool IsFlatDamage(string field)
        {
            if (!ItemViewFactory.IsDamageField(field)) return false;
            if (field.IndexOf("Modifier", StringComparison.Ordinal) >= 0) return false;
            return field.EndsWith("Min", StringComparison.Ordinal) ||
                   field.EndsWith("Max", StringComparison.Ordinal);
        }

        // One number of a pet skill, for `--petdmgtest`. Counted by the same code as
        // the display (`Numbers`), or the guard would watch something else.
        public static double DamageAt(GameData game, string skillDbr, int level,
                                      Dictionary<string, double> petBonus, string field)
        { return DamageAt(game, skillDbr, level, petBonus, field, null, null); }

        public static double DamageAt(GameData game, string skillDbr, int level,
                                      Dictionary<string, double> petBonus, string field,
                                      List<Move> petWide)
        { return DamageAt(game, skillDbr, level, petBonus, field, petWide, null); }

        public static double DamageAt(GameData game, string skillDbr, int level,
                                      Dictionary<string, double> petBonus, string field,
                                      List<Move> petWide, Dictionary<string, double> playerScale)
        { return DamageAt(game, skillDbr, level, petBonus, field, petWide, playerScale, null); }

        public static double DamageAt(GameData game, string skillDbr, int level,
                                      Dictionary<string, double> petBonus, string field,
                                      List<Move> petWide, Dictionary<string, double> playerScale,
                                      List<Move> always)
        {
            DbRecord made = Numbers(game, skillDbr, level, petBonus, petWide, playerScale, always);
            return made == null ? 0 : Num(made, field);
        }

        // A pet skill's lines, worded like everything else: the numbers are
        // multiplied into a record, and the ordinary `RenderStats` draws it.
        //
        // `petWide` — conversion that acts on the WHOLE pet (pet bonuses and its
        // passives). Empty means the numbers as the game prints them, with no
        // conversion at all. The SKILL'S OWN conversion `Numbers` takes from its
        // record and applies in the first pass — see below.
        public static List<TipLine> SkillLines(GameData game, string skillDbr, int level,
                                               Dictionary<string, double> petBonus, int indent)
        { return SkillLines(game, skillDbr, level, petBonus, indent, null, null); }

        public static List<TipLine> SkillLines(GameData game, string skillDbr, int level,
                                               Dictionary<string, double> petBonus, int indent,
                                               List<Move> petWide)
        { return SkillLines(game, skillDbr, level, petBonus, indent, petWide, null); }

        public static List<TipLine> SkillLines(GameData game, string skillDbr, int level,
                                               Dictionary<string, double> petBonus, int indent,
                                               List<Move> petWide, Dictionary<string, double> playerScale)
        { return SkillLines(game, skillDbr, level, petBonus, indent, petWide, playerScale, null); }

        public static List<TipLine> SkillLines(GameData game, string skillDbr, int level,
                                               Dictionary<string, double> petBonus, int indent,
                                               List<Move> petWide, Dictionary<string, double> playerScale,
                                               List<Move> always)
        {
            List<TipLine> lines = new List<TipLine>();
            DbRecord made = Numbers(game, skillDbr, level, petBonus, petWide, playerScale, always);
            if (made == null) return lines;
            // "THIS IS A PET SKILL" is said from here: the record does not give it
            // away (see `ItemViewFactory.IsPetSkill`). Cleared right after drawing.
            bool wasPet = ItemViewFactory.SkillOfPet;
            ItemViewFactory.SkillOfPet = true;
            try { ItemViewFactory.RenderStats(game, made, lines, indent, 0); }
            finally { ItemViewFactory.SkillOfPet = wasPet; }
            return lines;
        }

        // A pet skill's numbers at its level: the table's word for that level, then
        // conversion, then the pet-bonus multiplier.
        //
        // THE ORDER MATTERS AND WAS MEASURED ON THE CHARACTER: conversion comes
        // BEFORE the percentages (MECHANICS 6), and the SKILL'S OWN conversion goes
        // ahead of all the rest and takes its type whole (MECHANICS 14g, items 1 and
        // 3; the wiki names the same order "Conversion on the Skill or Transmuter →
        // Conversion on Equipment and Buffs"). The shared `Conversion.Pass` does both.
        //
        // A PET SPLITS THE SAME WAY, by the same mark as the character:
        //
        //   * an ATTACK record (the class holds `Attack`: `Skill_AttackRadius`,
        //     `Skill_BuffAttackRadiusToggled` and the like) is a skill of the pet's
        //     own, and its conversion acts ONLY on itself;
        //   * a PASSIVE record (`Skill_Passive`) is the pet's "gear": its innate claw
        //     damage (Blighted Claws is `Skill_Passive`) and the modifiers from the
        //     owner's items. Their conversion acts on ALL its damage, the way an
        //     item's or an aura's does for the character.
        //
        // The user said the same on 20 August: a pet skill independent of its claws,
        // with conversion of its own, converts only for that skill.
        public static DbRecord Numbers(GameData game, string skillDbr, int level,
                                       Dictionary<string, double> petBonus,
                                       List<Move> petWide)
        { return Numbers(game, skillDbr, level, petBonus, petWide, null); }

        public static DbRecord Numbers(GameData game, string skillDbr, int level,
                                       Dictionary<string, double> petBonus,
                                       List<Move> petWide, Dictionary<string, double> playerScale)
        { return Numbers(game, skillDbr, level, petBonus, petWide, playerScale, null); }

        // `always` — conversion of the pet's INNATE passives (a transmuter and kin).
        // It is applied in the "as the game prints it" block too, because the game
        // DOES print it, unlike the pet's "gear" (see `ConversionsOf`).
        public static DbRecord Numbers(GameData game, string skillDbr, int level,
                                       Dictionary<string, double> petBonus,
                                       List<Move> petWide, Dictionary<string, double> playerScale,
                                       List<Move> always)
        {
            DbRecord rec = game == null ? null : game.Record(skillDbr);
            if (rec == null) return null;
            DbRecord made = new DbRecord();
            made.Path = rec.Path; made.Class = rec.Class;
            foreach (KeyValuePair<string, string> kv in rec.Fields)
            {
                string[] steps = (kv.Value ?? "").Split(';');
                made.Fields[kv.Key] = steps[Math.Min(Math.Max(0, level - 1), steps.Length - 1)].Trim();
            }
            // FLAT ELEMENTAL DAMAGE IS THREE EQUAL SHARES — fire, cold and lightning.
            //
            // The character has had it that way from the start (`DamageModel.Elemental`:
            // lo/3 into each); the pet had not. `offensiveElementalMin` sat as a field
            // of its own and took one "elemental" multiplier across all 600.
            //
            // THE GAP COST MORE THAN A MULTIPLIER. "Elemental into physical" conversion
            // did NOT REACH these numbers at all: the moves are written into fire, cold
            // and lightning (`Conversion` splits "Elemental" three ways), while the pool
            // held a separate `Elemental` root that no move ever names. So World Gate's
            // 600 elemental converted nowhere, although Mythical Nosferattis takes
            // elemental into physical whole.
            //
            // THE GAME'S NUMBER THAT HOLDS THIS UP: World Gate's Unravel prints "3464
            // elemental damage" against 600 in the record. Our shares of 200 give 3094
            // fire, 3192 cold and 3464 lightning — the game prints exactly ONE OF THE
            // THREE. A summon's split by type cannot be seen in the game at all, so it
            // sums them into one line; that does not touch the arithmetic, only the
            // display, which is why the number stays in the stand as a comment and not
            // as a check.
            // SPLIT THREE WAYS ONLY FOR THE CONVERSION; PRINTED AS ONE LINE. The user
            // asked for that on 5 September: the game shows plain elemental damage,
            // so let it stay that way, and split by three only for the arithmetic. So
            // the field is broken into types ONLY when a conversion really takes fire,
            // cold or lightning away; otherwise the line stays elemental and the
            // shares are counted in its MULTIPLIER (below).
            List<Move> second = new List<Move>();
            if (petWide != null) second.AddRange(petWide);
            if (always != null) second.AddRange(always);
            bool movesElemental = false;
            foreach (Move m in second)
                foreach (string one in Elemental)
                    if (string.Equals(m.From, one, StringComparison.OrdinalIgnoreCase)) movesElemental = true;

            double elemLo = Num(made, "offensiveElementalMin");
            double elemHi = Num(made, "offensiveElementalMax");
            if (elemHi < elemLo) elemHi = elemLo;
            if (movesElemental && (elemLo > 0.0001 || elemHi > 0.0001))
            {
                made.Fields.Remove("offensiveElementalMin");
                made.Fields.Remove("offensiveElementalMax");
                foreach (string one in Elemental)
                {
                    double lo = Num(made, "offensive" + one + "Min") + elemLo / 3;
                    double hi = Math.Max(Num(made, "offensive" + one + "Max"), Num(made, "offensive" + one + "Min"))
                              + elemHi / 3;
                    made.Fields["offensive" + one + "Min"] =
                        lo.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
                    if (hi > lo + 0.0001)
                        made.Fields["offensive" + one + "Max"] =
                            hi.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            {
                // Own conversion belongs to an ATTACK only. A passive's is already in
                // `petWide`, and applying it twice would count it twice.
                List<Move> own = new List<Move>();
                if (IsOwnSkill(rec)) Conversion.Read(made, null, own);
                if (own.Count > 0 || second.Count > 0) ConvertRecord(made, own, second);
            }
            // WHOSE BONUSES MULTIPLY is decided by the game's `isPetBonusScaling` on
            // the skill record; measured at `PlayerScale`. With no player multiplier
            // map (nobody was there to build it) we count as before, by pet bonuses:
            // the old number beats no number.
            bool byPet = ScalesWithPetBonus(rec) || playerScale == null || playerScale.Count == 0;
            foreach (string key in new List<string>(made.Fields.Keys))
            {
                double v;
                if (!double.TryParse(made.Fields[key], System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out v)) continue;
                if (IsFlatDamage(key))
                {
                    double m;
                    // AN ELEMENTAL LINE IS THREE EQUAL SHARES, AND ITS MULTIPLIER IS
                    // THE MEAN OF THE THREE TYPES: the fire share takes the fire
                    // percentage, cold takes cold, lightning takes lightning. Printed
                    // as one line, counted as three; World Gate's Unravel gives
                    // 600 × (15.47 + 16.96 + 17.32) / 3 = 9950, where one "elemental"
                    // percentage would have given 9282.
                    if (key == "offensiveElementalMin" || key == "offensiveElementalMax")
                    {
                        m = 0;
                        foreach (string one in Elemental)
                        {
                            string asType = (key.EndsWith("Max", StringComparison.Ordinal)
                                             ? "offensive" + one + "Max" : "offensive" + one + "Min");
                            double part;
                            if (byPet) part = DamageMult(petBonus, asType);
                            else if (!playerScale.TryGetValue(asType, out part)) part = 1;
                            m += part / 3;
                        }
                    }
                    else if (byPet) m = DamageMult(petBonus, key);
                    else if (!playerScale.TryGetValue(key, out m)) m = 1;
                    v = Math.Round(v * m, MidpointRounding.AwayFromZero);
                }
                made.Fields[key] = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return made;
        }

        // A skill of the pet's own, not its "gear". The mark is `Attack` in the record
        // class: the same one that tells a hit from a summon in the devotion chance
        // (SkillText.Multiplier), and here it tells a skill with its own conversion
        // from a passive whose conversion goes over all the pet's damage.
        public static bool IsOwnSkill(DbRecord rec)
        {
            return rec != null && (rec.Class ?? "").IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Conversion over the numbers of a pet skill's record.
        //
        // NO MECHANICS HERE — they are one for the whole editor and live in
        // `Conversion.Pass`, the same that counts the character's. This only moves
        // things: record fields into the pool by root and back again.
        //
        // Low and high ends travel apart: the share is one, the numbers differ. The
        // dot's seconds go into the pool so a receiver with none of its own takes
        // them from the source — a dot number without seconds means nothing. There is
        // no separate measurement for carrying the seconds; the rule is taken by
        // analogy with carrying the dot itself, which IS measured on the character.
        static void ConvertRecord(DbRecord made, List<Move> own, List<Move> petWide)
        {
            List<Move> moves = new List<Move>(own);
            moves.AddRange(petWide);
            DamagePool pool = new DamagePool();
            foreach (string root in Conversion.Roots)
            {
                // There is no flat bleeding damage — bleeding is a dot only.
                if (root != "Bleeding")
                {
                    pool.Lo[root] = Num(made, "offensive" + root + "Min");
                    pool.Hi[root] = Num(made, "offensive" + root + "Max");
                    if (pool.Hi[root] == 0) pool.Hi[root] = pool.Lo[root];
                }
                if (!Conversion.HasDot(root)) continue;
                pool.DotLo[root] = Num(made, "offensiveSlow" + root + "Min");
                pool.DotHi[root] = Num(made, "offensiveSlow" + root + "Max");
                if (pool.DotHi[root] == 0) pool.DotHi[root] = pool.DotLo[root];
                pool.DotSecs[root] = Num(made, "offensiveSlow" + root + "DurationMin");
            }
            // The skill's own conversion first and whole, the pet's "gear" conversion
            // second, over the remainder. The same as `DamageModel.Convert` does.
            if (own.Count > 0) Conversion.Pass(pool, own, true);
            Conversion.Pass(pool, petWide, false);
            foreach (string root in Conversion.Roots)
            {
                if (root != "Bleeding")
                {
                    Put(made, "offensive" + root + "Min", pool.Lo[root], 0);
                    Put(made, "offensive" + root + "Max", pool.Hi[root], pool.Lo[root]);
                }
                if (!Conversion.HasDot(root)) continue;
                Put(made, "offensiveSlow" + root + "Min", pool.DotLo[root], 0);
                Put(made, "offensiveSlow" + root + "Max", pool.DotHi[root], pool.DotLo[root]);
                // The dot left whole — its seconds have nothing to stand for, or the
                // tooltip keeps an orphan "5 s duration".
                if (Math.Abs(pool.DotLo[root]) < 0.0000001 && Math.Abs(pool.DotHi[root]) < 0.0000001)
                    made.Fields.Remove("offensiveSlow" + root + "DurationMin");
            }
            // Seconds to the receiver from the source, when it has none of its own.
            foreach (Move m in moves)
            {
                if (!Conversion.HasDot(m.From) || !Conversion.HasDot(m.To)) continue;
                string to = "offensiveSlow" + m.To + "DurationMin";
                if (Num(made, to) > 0 || pool.DotSecs[m.From] <= 0) continue;
                if (Num(made, "offensiveSlow" + m.To + "Min") <= 0) continue;
                made.Fields[to] = pool.DotSecs[m.From].ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        static double Num(DbRecord rec, string field)
        {
            string v = rec.Get(field);
            double d;
            return double.TryParse(v ?? "", System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        // `other` — the number that, when equal, means the field need not be written:
        // 0 for the low end, the low end itself for the high one. A zero is not
        // written into a record that had no such field (the game shows no "0 vitality
        // damage"), and neither is a high end equal to the low one — the game prints
        // "4254", not "4254–4254".
        static void Put(DbRecord rec, string field, double value, double other)
        {
            bool had = !string.IsNullOrEmpty(rec.Get(field));
            if (!had && (Math.Abs(value) < 0.0000001 || Math.Abs(value - other) < 0.0000001)) return;
            if (had && Math.Abs(value) < 0.0000001) { rec.Fields.Remove(field); return; }
            rec.Fields[field] = value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ============ WHICH OF A PET'S SKILLS IT REALLY HAS ============
        //
        // FOUND 20 AUGUST, BY THE USER: "it feels like we mixed both kinds of
        // conversion, there is too much of it". There was — not from mixing channels,
        // but because a pet record's `skillNameN` list names EVERY modifier the pet
        // could ever be given in the game, not the ones this character has. The
        // Briarthorn has four, each carrying a "100% conversion" from four different
        // items, none of which the character wears.
        //
        // THE RECORD CARRIES THE MARK, AND IT IS EXACT: `skillLevelN` EQUALS ZERO.
        //
        // A slot with a non-zero level is an innate skill, always there. A slot with
        // zero is a place for a MODIFIER: it is handed out by a record of class
        // `SkillSecondary_PetModifier` (field `petSkillName`), and the level comes
        // from that record, not from the pet — hence the zero.
        //
        // Checked on three pets and confirmed by shots from the game:
        //
        //   Blight Fiend     slots 5 and 6 are zero = Rotting Fumes and Blight Burst,
        //                    handed out by `summon_blightbeast2_petmodifier` and
        //                    `summon_blightbeast3_petmodifier`, tree nodes under
        //                    Summon Blight Fiend. The game prints Blight Burst as
        //                    "1 + 8" — the NODE'S own level, not the summon's (which
        //                    stood at "1 + 9" at the time);
        //   Briarthorn       slots 6 and 7 are zero = Ground Slam and the roar, handed
        //                    out by `summon_briarthorn2/3_petmodifier`. The user had
        //                    put no points into that node — and the game's tooltip has
        //                    NO Ground Slam at all, while we printed it (at the
        //                    summon's level);
        //   Sundered Wraith  every slot non-zero, all innate — and the game prints
        //                    both its skills, as we do.
        //
        // THIS ALSO SETTLED THE "lvl 0" QUESTION: a zero level does not mean "level
        // unknown", it means "this skill is not from here".
        public class PetMods
        {
            // A pet modifier's record → its level for this character.
            readonly Dictionary<string, int> level = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, string> from = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public bool Has(string petSkill)
            { return petSkill != null && level.ContainsKey(petSkill); }

            public int LevelOf(string petSkill)
            { int v; return level.TryGetValue(petSkill ?? "", out v) ? v : 0; }

            public string SourceOf(string petSkill)
            { string v; return from.TryGetValue(petSkill ?? "", out v) ? v : ""; }

            void Take(GameData game, string modifierDbr, int at, string source)
            {
                DbRecord mod = game == null ? null : game.Record(modifierDbr);
                if (mod == null) return;
                if (!string.Equals(mod.Class, "SkillSecondary_PetModifier", StringComparison.OrdinalIgnoreCase)) return;
                string target = mod.Get("petSkillName");
                if (string.IsNullOrEmpty(target)) return;
                // Two modifiers on one skill — take the higher: there is no ground for
                // adding levels and no measurement for it.
                if (level.ContainsKey(target) && level[target] >= at) return;
                level[target] = at;
                from[target] = source;
                // We do NOT apply such a modifier's `petChanges` field — the game's
                // numbers refuted it; the figures are at the swap site in `Add`.
            }

            public static PetMods Of(GameData game, Character c, SkillText.LevelBonus bonus)
            {
                PetMods made = new PetMods();
                if (game == null || c == null) return made;
                // 1. TREE NODES with points in them. The level is what was put in plus
                //    "+N to skills" from gear, as for any skill.
                foreach (Skill s in c.Skills)
                {
                    if (string.IsNullOrEmpty(s.Name) || s.Level <= 0) continue;
                    int at = s.Level + (bonus == null ? 0 : bonus.For(s.Name, s.Level));
                    made.Take(game, s.Name, at, "skill tree: " + (SkillText.Name(game, s.Name) ?? s.Name));
                }
                // 2. WORN ITEMS AND COMPLETE SETS. An item's modifier has one level,
                //    the first: an item record has no steps.
                List<Item> worn = new List<Item>();
            // THE SPARE WEAPON SET IS NOT INCLUDED — MECHANICS 14d8. The game counts
            // only the set in hand; an item in the second set gives nothing until it
            // is switched to. Found 29 August on `_222` and swept out of every place
            // at once: two of them had already cost wrong numbers (the bonus sum and
            // skill levels), the rest were cleaned up so as not to hunt them one by one.
                worn.AddRange(c.Equipment); worn.AddRange(c.Weapons1);
                foreach (Item it in worn)
                {
                    if (it == null || string.IsNullOrEmpty(it.BaseName)) continue;
                    string name = ItemViewFactory.Build(game, it, false).Name ?? it.BaseName;
                    foreach (string dbr in new[] { it.BaseName, it.PrefixName, it.SuffixName,
                                                   it.RelicName, ItemViewFactory.RelicBonusOf(game, it),
                                                   it.AugmentName, it.ModifierName, it.AscensionName })
                    {
                        DbRecord r = string.IsNullOrEmpty(dbr) ? null : game.Record(dbr);
                        if (r == null) continue;
                        for (int i = 1; i <= 8; i++)
                            made.Take(game, r.Get("modifierSkillName" + i), 1, Lang.T("common.source.item", name));
                    }
                }
                foreach (KeyValuePair<string, string> kv in FullSets(game, worn))
                    for (int i = 1; i <= 8; i++)
                    {
                        DbRecord set = game.Record(kv.Key);
                        if (set == null) continue;
                        made.Take(game, set.Get("modifierSkillName" + i), 1, Lang.T("common.source.setName", kv.Value));
                    }
                return made;
            }

            // COMPLETE sets only, by the same threshold as `ItemSkillMods`: the record
            // does not say how many pieces switch a modifier on, so we take the full
            // set. The same caveat is stated there: this is an assumption.
            static Dictionary<string, string> FullSets(GameData game, List<Item> worn)
            {
                Dictionary<string, List<string>> pieces =
                    new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (Item it in worn)
                {
                    if (it == null || string.IsNullOrEmpty(it.BaseName)) continue;
                    DbRecord own = game.Record(it.BaseName);
                    string set = own == null ? null : own.Get("itemSetName");
                    if (string.IsNullOrEmpty(set)) continue;
                    if (!pieces.ContainsKey(set)) pieces[set] = new List<string>();
                    if (!pieces[set].Contains(it.BaseName)) pieces[set].Add(it.BaseName);
                }
                Dictionary<string, string> full = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, List<string>> kv in pieces)
                {
                    DbRecord set = game.Record(kv.Key);
                    if (set == null) continue;
                    string members = set.Get("setMembers") ?? "";
                    int total = members.Length == 0 ? 0 : members.Split(';').Length;
                    if (total == 0 || kv.Value.Count < total) continue;
                    full[kv.Key] = ItemViewFactory.SetNameOf(game, set) ?? kv.Key;
                }
                return full;
            }
        }

        // ============ A PET'S CONVERSION ============
        //
        // WHERE IT COMES FROM. Gathered here is the kind that acts on ALL the pet's
        // damage — its "gear". Two places, both needed:
        //
        //   1. THE PET BONUS of a worn item or a skill (`petBonusName`). Goes to ALL
        //      pets. For `_Izaar` that is Ghol's Mark: `item_petbonusd_gholmedal_01`
        //      carries "50% conversion: vitality into acid";
        //   2. THE PET'S OWN PASSIVES — records from its `skillNameN` whose class does
        //      NOT hold `Attack`. Go to it alone. This covers its innate claw damage
        //      (Blighted Claws is `Skill_Passive`) and the modifiers from the owner's
        //      items: the same Ghol's Mark hangs `modifier_medal_d213_blightfiend`
        //      with "100% conversion: vitality into acid" on the Blight Fiend only.
        //
        // A SINGLE PET SKILL'S CONVERSION DOES NOT COME HERE — it acts on that skill
        // alone, and `Numbers` takes it from its record in the first pass. Same rule
        // as the character's (MECHANICS 14g, item 3: "On Skills, the conversion will
        // only affect that skill itself. Exceptions are buffs/auras").
        //
        // None of this was read before: `PetFields` adds NUMBERS, while a conversion
        // carries two type NAMES beside its number, and names do not add — the
        // percentage was left without a "from" and a "to".
        //
        // ============================================================
        // THE GAME PRINTS A PET'S DAMAGE BEFORE CONVERSION — MEASURED.
        // ============================================================
        //
        // The experiment ran 20 August on numbers already collected
        // (`stand-izaar-petskills.tsv`, seven numbers from three pets, shot 19 August
        // with Ghol's Mark WORN): conversion was applied and `--petdmgtest` run. SIX
        // OF THE SEVEN disagreed:
        //
        //   Wraith's Touch   vitality  game 2635 / 4110   with conversion 1317 / 2055
        //   Blighted Claws   physical  game 4938 / 6731   with conversion 0 / 0
        //   Thorned Claws    physical  game 3984 / 4924   with conversion 0 / 0
        //
        // The halves in the first are that "50% vitality into acid"; the zeros in the
        // other two because more than a hundred per cent of `_Izaar`'s pets' physical
        // leaves (58% and 53% into elemental, 92% into vitality, 10% into acid, from
        // four items). THE GAME STILL PRINTS THE FULL NUMBERS.
        //
        // The user confirmed it with shots on 20 August: with Ghol's Mark and without
        // it the skill's description in the game is THE SAME. The game simply never
        // shows a pet's conversion — but it works, as the character's does.
        //
        // SO WE SHOW TWO BLOCKS: the counted one (with conversion — what the pet
        // really deals) and the reference one (without — what the game prints, and
        // what the numbers are checked against). `--petdmgtest` guards the reference
        // block: it goes through `Numbers` with an empty conversion.
        public static List<Move> ConversionsOf(GameData game, CharacterTotals totals, Pet pet)
        { return ConversionsOf(game, totals, pet, null); }

        // `playerMoves` — the CHARACTER'S OWN conversion. It goes to pets that take
        // the player's bonuses (`PetPlayerScaling`, see `PlayerScale`) INSTEAD of the
        // conversion from the pet bonus: the user put it plainly on 5 September — if
        // it says player bonuses, then the player's; if pet, then the pet's. The
        // pet's own passives stay in both cases: they belong to it, not to a channel.
        public static List<Move> ConversionsOf(GameData game, CharacterTotals totals, Pet pet,
                                               List<Move> playerMoves)
        {
            List<Move> made = new List<Move>();
            bool byPlayer = pet != null && pet.PlayerScaling && playerMoves != null;
            if (byPlayer) made.AddRange(playerMoves);
            else if (totals != null)
                foreach (CharacterTotals.PetSource one in totals.PetRecords)
                    Conversion.Read(one.Rec, one.Known, made, "pet bonus: " + one.Name);
            if (game == null || pet == null) return made;
            // ======== WHAT THE GAME PRINTS AND WHAT IT APPLIES IN SILENCE ========
            //
            // A pet's passives fall in two, and the line is MEASURED on both sides:
            //
            //   INNATE (non-zero `skillLevel` in the pet record) — the game PRINTS it.
            //     The Corrupted Storm transmuter on `stormtotem18b` gives "100%
            //     conversion: lightning into vitality", and the game's tooltip prints
            //     "1598 – 4450 VITALITY damage" though the skill record says LIGHTNING;
            //   GRANTED BY THE OWNER'S ITEM (`pet.Granted`) — it does NOT print.
            //     Ghol's Mark hangs `modifier_medal_d213_blightfiend` with "100%
            //     conversion: vitality into acid" on the Blight Fiend, and the game
            //     still prints the full numbers (17zh, six numbers, 20 August).
            //
            // So the innate half is kept APART and applied ALWAYS — in the counted
            // block and in the "as the game prints it" one alike.
            foreach (string dbr in pet.Passives)
            {
                DbRecord own = game.Record(dbr);
                // A pet's attack converts itself only — `Numbers` reads its record,
                // not this list.
                if (own == null || IsOwnSkill(own)) continue;
                bool granted = pet.Granted.Contains(dbr);
                Conversion.Read(own, null, granted ? made : pet.OwnConv,
                                (granted ? "pet modifier: " : "innate: ") + dbr);
            }
            return made;
        }

        // A PET'S CONVERSION IN WORDS, to show beside its hit.
        //
        // The shares are brought to what will really leave: no more than a hundred
        // per cent out of one type, the hundred split between the takers by their
        // shares. `Conversion.Effective` does it — the character's own function — and
        // what was recorded is printed in brackets so the difference is visible.
        // `game` is for the game's own words for the damage types.
        public static List<TipLine> ConversionLines(GameData game, List<Move> moves, int indent)
        {
            List<TipLine> lines = new List<TipLine>();
            List<Move> raw = Conversion.Merge(moves ?? new List<Move>());
            if (raw.Count == 0) return lines;
            List<Move> real = Conversion.Effective(raw);
            // Words from `lang/<CODE>/pets.txt` (PLAN 7v); the damage type names
            // (`Conversion.RootName`) are ours and will move into the file with the
            // tooltips.
            lines.Add(new TipLine(Lang.T("pets.conversion.title"), TipKind.Section, indent));
            bool over = false;
            for (int i = 0; i < raw.Count; i++)
                if (Math.Abs(raw[i].Percent - real[i].Percent) > 0.0001) over = true;
            if (over)
                lines.Add(new TipLine(Lang.T("pets.conversion.capped"), TipKind.Subtle, indent + 1));
            for (int i = 0; i < raw.Count; i++)
                lines.Add(new TipLine(
                    Lang.T("pets.conversion.line",
                           real[i].Percent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                           Conversion.RootName(game, raw[i].From), Conversion.RootName(game, raw[i].To)) +
                    (Math.Abs(raw[i].Percent - real[i].Percent) > 0.0001
                        ? "   " + Lang.T("pets.conversion.recorded", raw[i].Percent.ToString("0.#",
                              System.Globalization.CultureInfo.InvariantCulture))
                        : ""),
                    TipKind.Bonus, indent + 1));
            return lines;
        }

        // A PET SKILL'S LEVEL IN WORDS. Zero is not "level zero" but "the pet record
        // carries no level": the Blight Fiend's `skillLevel5` and `skillLevel6` are
        // zeros, and what the game takes there is NOT ESTABLISHED. Measured 20 August:
        // the game shows Rotting Fumes at level one and Blight Burst at twelve (its
        // "70% pet base damage" is exactly the twelfth word of `weaponDamagePct`). One
        // rule does not yet explain both numbers, so we do not guess — we say so, and
        // the numbers are counted from the table's first word. Open question in PLAN.
        public static string LevelWord(int level) { return LevelWord(level, true); }

        // A suffix for the item name saying which part the skill came from, in part
        // order (base, prefix, suffix, component, augment, ascension). Shared with
        // `ActiveSkills`.
        public static string[] ItemPartWords()
        {
            return new[] { "", " " + Lang.T("common.part.prefix"), " " + Lang.T("common.part.suffix"),
                           " " + Lang.T("common.part.component"), " " + Lang.T("common.part.augment"),
                           " " + Lang.T("common.part.ascension") };
        }

        public static string LevelWord(int level, bool brackets)
        {
            string word = level > 0 ? Lang.T("pets.level", level) : Lang.T("pets.levelUnknown");
            return brackets ? "(" + word + ")" : word;
        }

        // WHERE A PET SKILL'S NUMBERS REALLY ARE — AND IT IS NOT ALWAYS THE TOP RECORD.
        //
        // A whole family of pet skills is a two-record chain: the top one only says
        // "this is a toggle / a radius strike" and names the second through
        // `buffSkillName`, and ALL the numbers — damage, dot, the enemy's resistance
        // cut — live in that second one. We read only the top one, found nothing and
        // honestly reported "no skill with damage" (`log.unread.petNoMoves`).
        //
        // Two whole kinds of object were silent because of it (20 October, the user's
        // three tooltips):
        //
        //   Thermite Mine     thermitemine_skill_flare1 (Skill_BuffRadiusToggled)
        //                     → …_flare1_buff: 301 fire, 163 burn over 2 s,
        //                       −35% elemental resistance, radius 2.4
        //   Arcane Bomb       petskill_wizardstrap_detonate01 (Skill_AttackBuffRadius)
        //                     → …_detonate01_buff: lightning + aether, −95 offensive
        //                       ability, −35% lightning and −40% aether resistance,
        //                       radius 6, 4 seconds
        //
        // THIS IS THE THIRD PLACE OF ONE KIND, and the first two are written down:
        // `PetBonusOf` lost a pet bonus the same way (18 August, `_Izaar`), and
        // `DebuffOf` was taught the same hop for a pet's debuff aura. Rule "to do" 18 —
        // count how many such places there are — is what finally found this one.
        //
        // The hop is the same one the name, the limits and the pet bonus already walk
        // (`ItemViewFactory.NextSkillRecord`: `buffSkillName`, `petSkillName`,
        // `skillBaseName`), and it is bounded by three records, as they are.
        public static string DamageRecord(GameData game, string skillDbr)
        {
            string at = skillDbr;
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(at); hop++)
            {
                DbRecord rec = game == null ? null : game.Record(at);
                if (rec == null) break;
                if (Carries(rec)) return at;
                at = ItemViewFactory.NextSkillRecord(rec);
            }
            return skillDbr;
        }

        static bool Carries(DbRecord rec)
        {
            foreach (KeyValuePair<string, string> kv in rec.Fields)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                if (kv.Key == "weaponDamagePct" || IsFlatDamage(kv.Key)) return true;
            }
            return false;
        }

        // Does a pet skill deal damage at all — by the same mark as the multiplier:
        // flat damage or a share of weapon damage. A passive ("+108% health") has no
        // business in the list of hits. Asked of the record that CARRIES the numbers,
        // not of the one that merely names it (see `DamageRecord`).
        public static bool HasDamage(GameData game, string skillDbr)
        {
            DbRecord rec = game == null ? null : game.Record(DamageRecord(game, skillDbr));
            if (rec == null) return false;
            foreach (KeyValuePair<string, string> kv in rec.Fields)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                if (kv.Key == "weaponDamagePct" || IsFlatDamage(kv.Key)) return true;
            }
            return false;
        }

        // The list's word for a skill level: `spawnObjects` and `petLimit` are built
        // like every other table in a record — one value per level.
        static string Step(string list, int slot)
        {
            if (string.IsNullOrEmpty(list)) return null;
            string[] parts = list.Split(';');
            return parts[Math.Min(Math.Max(0, slot), parts.Length - 1)].Trim();
        }

        public static List<Pet> Of(GameData game, Character c, ActiveSkills chosen)
        { return Of(game, c, chosen, null); }

        // `extraPercents` — DAMAGE PERCENTAGES THAT REACH A PET FROM OUTSIDE.
        //
        // These are auras on allies: the Briarthorn's Emboldening Presence gives +220%
        // TOTAL damage to everyone nearby, to us and to the other pets (MECHANICS 81).
        // They are not in "pet bonuses": such an aura has no `petBonusName` record of
        // its own, the numbers sit in it directly and reach every ally. And a pet's
        // damage multiplier is counted from exactly those bonuses, so this is the only
        // honest place to bring them in.
        //
        // They cannot be summed inside `PetPool`: to know which auras the pets have
        // you must first build the pool, and this function is what builds it. So
        // whoever already holds the pool counts them (`TimelinePets.AuraPercents`) and
        // hands them over as a ready dictionary on the second build.
        public static List<Pet> Of(GameData game, Character c, ActiveSkills chosen,
                                   Dictionary<string, double> extraPercents)
        {
            List<Pet> list = new List<Pet>();
            if (game == null || c == null) return list;
            SkillText.LevelBonus bonus = SkillText.LevelBonus.Of(game, c);
            ItemSkillMods mods = ItemSkillMods.Of(game, c);
            // Which pet modifiers the character HAS — tree nodes and worn items.
            // Without this every modifier in the game went into the pool (see PetMods).
            PetMods petMods = PetMods.Of(game, c, bonus);
            // Our pet health bonus comes from the shared total, so the window and the
            // damage tab take the same number.
            CharacterTotals petTotals = null;
            double petLife = 0, auraLife = 0;
            Dictionary<string, double> petBonus = new Dictionary<string, double>(StringComparer.Ordinal);
            try
            {
                // TOTALS ARE COUNTED FROM THE ITEMS' ROLLED NUMBERS, not the records.
                // Until 5 September a `null` stood here instead of the rolls, and the
                // pet window counted by record while EVERY check counted by seed. It
                // showed only on the totem: that one takes the CHARACTER'S multiplier,
                // and for the character "record against rolled" is a wide gap (4.6% on
                // `_666`), while pet bonuses have almost none.
                ExactRolls rolled = new ExactRolls();
                try { rolled.ReplaceWithSeed(game, c); } catch (Exception) { rolled = null; }
                CharacterTotals t = petTotals = CharacterTotals.Of(game, c, rolled, chosen);
                TotalsEntry e;
                if (t != null && t.PetFields.TryGetValue("characterLifeModifier", out e)) petLife = e.Value;
                if (t != null && t.AuraFields.TryGetValue("characterLifeModifier", out e)) auraLife = e.Value;
                // Every pet bonus as a number — their skills' damage is multiplied by these.
                if (t != null)
                    foreach (KeyValuePair<string, TotalsEntry> kv in t.PetFields) petBonus[kv.Key] = kv.Value.Value;
            }
            catch (Exception) { petLife = 0; auraLife = 0; }
            // …AND ALLY AURA PERCENTAGES ON TOP. Only `offensive…Modifier`: an ally
            // aura's FLAT damage the timeline applies itself on every pet hit
            // (`TimelinePets.AuraFlat`), and added here too it would count twice.
            if (extraPercents != null)
                foreach (KeyValuePair<string, double> kv in extraPercents)
                {
                    if (!kv.Key.StartsWith("offensive", StringComparison.Ordinal) ||
                        !kv.Key.EndsWith("Modifier", StringComparison.Ordinal)) continue;
                    double had;
                    petBonus.TryGetValue(kv.Key, out had);
                    petBonus[kv.Key] = had + kv.Value;
                }
            // Summon transmuters taken: which list of pet records they swap in.
            Dictionary<string, string> swaps = SpawnSwaps(game, c, bonus);
            // …and how many pets TREE NODES add (Undead Legion gives +5).
            Dictionary<string, double> limits = TreeLimits(game, c, bonus);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Mastery skills and DEVOTION. A star summons too: `_Izaar`'s Eldritch
            //    Hound comes from devotion and stands beside the other pets in the
            //    game (noticed by the user, 19 August). Devotion takes no level
            //    bonuses — its star levels on its own.
            foreach (Skill s in c.Skills)
            {
                if (string.IsNullOrEmpty(s.Name) || s.Level <= 0) continue;
                DbRecord summon = SummonOf(game, s.Name);
                if (summon == null || !seen.Add(s.Name)) continue;
                bool devotion = SkillsForm.IsDevotion(s.Name);
                string swap;
                double more;
                Add(game, mods, petMods, list, s.Name, summon,
                    // A devotion star's level is `DevotionLevel`: one point goes in and
                    // it levels itself. See CharacterTotals, the devotion branch.
                    devotion ? (s.DevotionLevel > 0 ? s.DevotionLevel : s.Level)
                             : s.Level + bonus.For(s.Name, s.Level),
                    Lang.T(devotion ? "common.source.devotion" : "common.source.mastery"), true, c.Level, petLife,
                    swaps.TryGetValue(s.Name, out swap) ? swap : null,
                    limits.TryGetValue(s.Name, out more) ? more : 0);
            }

            // 2. Skills granted by items: Conjure Eldritch Webweaver off Ghol's Claws.
            List<Item> worn = new List<Item>();
            // THE SPARE WEAPON SET IS NOT INCLUDED — MECHANICS 14d8; see `PetMods.Of`
            // for the measurement.
            worn.AddRange(c.Equipment); worn.AddRange(c.Weapons1);
            foreach (Item it in worn)
            {
                if (it == null || string.IsNullOrEmpty(it.BaseName)) continue;
                string itemName = ItemViewFactory.Build(game, it, false).Name ?? it.BaseName;
                string[] parts = { it.BaseName, it.PrefixName, it.SuffixName,
                                   it.RelicName, it.AugmentName, it.AscensionName };
                string[] what = ItemPartWords();
                for (int i = 0; i < parts.Length; i++)
                {
                    DbRecord pr = string.IsNullOrEmpty(parts[i]) ? null : game.Record(parts[i]);
                    if (pr == null) continue;
                    string granted = pr.Get("itemSkillName");
                    if (string.IsNullOrEmpty(granted)) continue;
                    DbRecord summon = SummonOf(game, granted);
                    if (summon == null || !seen.Add(granted)) continue;
                    // A GRANTED SKILL'S LEVEL BELONGS TO THE ITEM, AND IT IS NOT
                    // ALWAYS ONE. Until 4 September a 1 stood here, with the words
                    // "an item record has no steps". It has: the field
                    // `itemSkillLevelEq` (more rarely `itemSkillLevel`), and on
                    // MYTHICAL items it holds two. `AttackSkills.GrantedLevel` reads
                    // it — the same function that levels a granted skill for the
                    // character; here it simply was not called.
                    //
                    // MEASURED ON SEVEN OF THE GAME'S NUMBERS on `_666`
                    // (stand-666-petskills): Mythical Heart of the Mountain and
                    // Mythical Black Hand of Sanar'Siin carry `itemSkillLevelEq 2`,
                    // and at the second step the summon bears a DIFFERENT pet record
                    // — `itempet_yeti_02` for `itempet_yeti`,
                    // `itempet_chthoniandevourer_a02` for `…_a01`. Its passive levels
                    // differ too (`skillLevel5 = 2` against 1), so the numbers come
                    // from the list's second word:
                    //   Chillmane Brute  phys 180 (not 146) × 15.67 = 2821   game 2821
                    //                         310 (not 283) × 15.67 = 4858   game 4858
                    //                    cold 222 (not 184) × 16.96 = 3765   game 3765
                    //   devourer         phys  40 (not  33) × 15.67 =  627   game  627
                    //                          60 (not  52) × 15.67 =  940   game  940
                    //                    chaos 10 (not   6) × 13.69 =  137   game  137
                    //                         122 (not  92) × 13.69 = 1670   game 1670
                    // The first word would have given 2288 / 4434 / 3121 and
                    // 517 / 815 / 82 / 1259.
                    Add(game, mods, petMods, list, granted, summon,
                        AttackSkills.GrantedLevel(pr), Lang.T("common.source.item", itemName + what[i]), false, c.Level, petLife);
                }
            }

            // …AND A SUMMON GRANTED BY A SET. The fourth and last place where
            // `itemSkillName` was taken from an ITEM's parts only. For Worldkeeper's
            // Garden that is a summon — Conjure Living Bloom — and the game's record
            // says outright that the Living Bloom scales with player bonuses.
            foreach (KeyValuePair<string, int> kv in ItemSkillMods.SetSizes(game, worn))
            {
                DbRecord sr = game.Record(kv.Key);
                if (sr == null) continue;
                string granted = sr.Get("itemSkillName");
                if (string.IsNullOrEmpty(granted)) continue;
                int lvl = ItemSkillMods.GrantedLevelOfSet(sr, kv.Value);
                if (lvl <= 0) continue;
                DbRecord summon = SummonOf(game, granted);
                if (summon == null || !seen.Add(granted)) continue;
                Add(game, mods, petMods, list, granted, summon, lvl,
                    Lang.T("common.source.set", ItemViewFactory.SetNameOf(game, sr) ?? kv.Key, kv.Value),
                    false, c.Level, petLife);
            }

            // AURAS THAT LIE ON ALLIES — and a pet is an ally. Counted as raised by
            // the same mark as everything else: the save plus the user's decision.
            // Their numbers are not applied yet (see CharacterTotals.IsAllyAura) but
            // they are named: without that it is not visible that the pet has one
            // more source, nor where a disagreement comes from.
            List<string> auras = new List<string>();
            foreach (Skill s in c.Skills)
            {
                if (string.IsNullOrEmpty(s.Name) || s.Level <= 0) continue;
                if (!CharacterTotals.IsAllyAura(game, s.Name)) continue;
                bool? said = chosen == null ? null : chosen.For(s.Name);
                if (!(said ?? s.Active != 0)) continue;
                string title = SkillText.Name(game, s.Name) ?? s.Name;
                if (!auras.Contains(title)) auras.Add(title);
            }

            // The character's own multipliers — one set for all, counted once: pet
            // skills without `isPetBonusScaling` scale by them (totems, see
            // `PlayerScale`).
            Dictionary<string, double> playerScale = PlayerScale(game, c, petTotals);
            // And the character's conversion, to the same pets for the same reason.
            List<Move> playerMoves = null;
            try
            {
                if (petTotals != null)
                    playerMoves = new DamageModel(game, c, petTotals).ConversionsForShow();
            }
            catch (Exception) { playerMoves = null; }
            foreach (Pet p in list)
            {
                bool? said = chosen == null ? null : chosen.For(p.Skill);
                p.Chosen = said ?? p.Default;
                p.Auras.AddRange(auras);
                p.AuraLife = auraLife;
                p.Bonus = petBonus;
                // The armour percentage from the pet bonus, by the same route as the
                // health one: both halves add up the same way (see `Pet`).
                double armourPct;
                if (petBonus.TryGetValue("defensiveProtectionModifier", out armourPct)) p.ArmourBonus = armourPct;
                p.Scale = playerScale;
                p.ConvMoves = ConversionsOf(game, petTotals, p, playerMoves);
            }
            list.Sort(delegate (Pet a, Pet b)
            {
                int by = string.Compare(a.Source, b.Source, StringComparison.CurrentCulture);
                return by != 0 ? by : string.Compare(a.Title, b.Title, StringComparison.CurrentCulture);
            });
            return list;
        }

        static void Add(GameData game, ItemSkillMods itemMods, PetMods mods, List<Pet> list, string skillDbr,
                        DbRecord summon, int level, string source, bool onByDefault, int charLevel,
                        double petLifeBonus)
        { Add(game, itemMods, mods, list, skillDbr, summon, level, source, onByDefault, charLevel, petLifeBonus, null, 0); }

        static void Add(GameData game, ItemSkillMods itemMods, PetMods mods, List<Pet> list, string skillDbr,
                        DbRecord summon, int level, string source, bool onByDefault, int charLevel,
                        double petLifeBonus, string spawnSwap, double treeLimit)
        {
            int slot = SkillText.Slot(summon, level);
            // A taken summon transmuter swaps the whole list — see SpawnSwaps.
            string petDbr = Step(string.IsNullOrEmpty(spawnSwap) ? SpawnList(summon) : spawnSwap, slot);
            if (string.IsNullOrEmpty(petDbr)) return;
            DbRecord pet = game.Record(petDbr);
            if (pet == null) return;

            // ========= `petChanges` — A SWAP THE GAME DOES NOT MAKE =========
            //
            // Skill modifiers FROM ITEMS carry a field `petChanges` — the same list of
            // pet records by level as a transmuter's `modSpawnObjects` (28g). Forty-six
            // records have it. The Eldrun's Ruminations set would swap the Great Raven
            // for `pet_raven_a26_player`, whose class is `PetPlayerScaling` — that is,
            // an item would move the pet from "takes pet bonuses" to "takes the
            // player's".
            //
            // THE CHANGE WAS WRITTEN AND WITHDRAWN THE SAME HOUR — the game's numbers
            // refuted it. A test save with the FULL set (4
            // pieces, the focus in a weapon slot), and the game's tooltip says:
            //
            //   "Skills Great Raven: +150% health"  ← that is `petskill_raven_innate1`,
            //       which only the ORDINARY record has; `_player` carries
            //       `armorbase01` with 50% instead
            //   67057 health                         the ordinary record gives 67057 ✔
            //                                        `_player`              gives 51988 ✘
            //
            // Damage tells the two records apart in no way: the multiplier is chosen by
            // the SKILL'S own field (27zh), and Storm Orb and Lightning Strike carry
            // `isPetBonusScaling`, so both variants give the same 3167–3430 and
            // 8437–11711, as in the game. Health alone tells them apart, and it speaks
            // for the ordinary one.
            //
            // The binding itself IS there and is beyond doubt: the set record names the
            // pair "which skill is modified / by what" (`modifiedSkillName<N>` beside
            // `modifierSkillName<N>`), and `ItemSkillMods` reads it. We CAN swap — the
            // game simply does not do it in the tooltip, and neither do we.

            Pet made = new Pet();
            made.Skill = skillDbr;
            made.Title = SkillText.Name(game, skillDbr) ?? skillDbr;
            made.Source = source;
            made.Dbr = petDbr;
            made.Level = level;
            made.Default = onByDefault;
            made.PlayerScaling = (pet.Class ?? "").IndexOf("PetPlayerScaling",
                                                           StringComparison.OrdinalIgnoreCase) >= 0;
            // The pet's name comes from its own record, not from the skill: Reap
            // Spirit summons a Sundered Wraith, and that is what the game's tooltip
            // shows.
            string tag = pet.Get("description");
            made.Name = (game.Tag(tag) ?? "").Trim();
            if (made.Name.Length == 0) made.Name = made.Title;

            // HOW MANY AT ONCE — THE RECORD PLUS SKILL MODIFIERS FROM ITEMS AND SETS.
            // Measured 19 August: Summon Blight Fiend's record says `petLimit 1`, the
            // game prints "2 maximum summoned". The extra one came from the Ghol's
            // Malice set: its modifier `set_d212_summonblightfiend` carries
            // `petLimit 1`, and the set's tooltip says exactly that — "Summon Blight
            // Fiend: +1 maximum summoned". The user sent that shot when the number
            // did not add up.
            double lim;
            double.TryParse(Step(summon.Get("petLimit"), slot) ?? "",
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out lim);
            made.Limit = (int)(lim + (itemMods == null ? 0 : itemMods.Sum(skillDbr, "petLimit")) + treeLimit);
            made.Seconds = summon.GetInt("spawnObjectsTimeToLive");

            // ONLY SKILLS THAT HAVE A NAME IN THE GAME. The pet's other ones are
            // housekeeping — damage modifiers, summoned armour, inner passives — and
            // carry no caption at all: `SkillText.Name` hands back a file name. The
            // game does not print them either, and a list out of
            // `damage_totaladjuster.dbr` cannot be read. They will all enter the
            // arithmetic when it gets that far.
            //
            // AND ONLY THE ONES THIS CHARACTER HAS. A slot with `skillLevel` zero is a
            // place for a modifier, not a skill at level zero; the whole of it is at
            // `PetMods`. No modifier, no skill: that is how we printed Ground Slam for
            // `_Izaar`'s Briarthorn, which it does not have in the game.
            //
            // THERE CAN BE MORE THAN TWENTY-FOUR SLOTS. `for (i = 1; i <= 24)` stood
            // here, and it sufficed until the third expansion: the Great Raven's
            // `petskill_raven_stormspirit1` in `gdx3` sits in SLOT 27, and the Storm
            // Spirit vanished entirely — no aura on us, none of its numbers. Found 12
            // September from the user's shot of the panel. So no limit is written at
            // all: the slots are walked off the record itself.
            List<int> slots = new List<int>();
            foreach (string key in pet.Fields.Keys)
            {
                if (!key.StartsWith("skillName", StringComparison.OrdinalIgnoreCase)) continue;
                int n;
                if (!int.TryParse(key.Substring(9), out n) || n <= 0) continue;
                slots.Add(n);
            }
            slots.Sort();
            foreach (int i in slots)
            {
                string one = pet.Get("skillName" + i);
                if (string.IsNullOrEmpty(one)) continue;
                // A pet skill's level sits in a line of its own beside it, and that
                // line can be an equation (`charLevel/4+1`), so it is evaluated.
                double at;
                string when = pet.Get("skillLevel" + i);
                if (string.IsNullOrEmpty(when) ||
                    !Equation.TryEval(when, delegate (string nm)
                    {
                        return string.Equals(nm, "charLevel", StringComparison.OrdinalIgnoreCase)
                             ? charLevel : 0;
                    }, out at)) at = 0;
                bool granted = false;
                if (at <= 0)
                {
                    // A modifier slot: the level comes from whoever handed it out.
                    if (mods == null || !mods.Has(one)) continue;
                    at = mods.LevelOf(one);
                    granted = true;
                    if (at <= 0) continue;
                }
                if (!made.Passives.Contains(one))
                { made.Passives.Add(one); made.PassiveLevels.Add((int)Math.Max(1, Math.Round(at))); }
                if (granted) made.Granted.Add(one);
                string name = SkillText.Name(game, one);
                if (string.IsNullOrEmpty(name) ||
                    name.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase)) continue;
                if (made.Skills.Contains(name)) continue;
                made.Skills.Add(name);
                made.SkillDbrs.Add(one);
                made.SkillLevels.Add((int)at);
            }
            // A PET'S HEALTH IS COUNTED LIKE AN ENEMY'S — by the equation from its
            // "biography" (`characterAttributeEquations`), where `charLevel` is the
            // CHARACTER'S level: the pet record says so, `charLevel*1`. The Sundered
            // Wraith's biography gives `((charLevel*10)^1.29)+35`, which at level 94
            // is 6879.
            //
            // THE PERCENTAGES ON TOP ADD UP, THEY DO NOT MULTIPLY — MEASURED ON THREE
            // PETS AT ONCE, and this is the case where three numbers beat any amount
            // of reasoning. The user's guess (base health, pet bonus, and scaling by
            // skill level) held entirely:
            //
            //     health = base × (1 + (own innate% + pet bonus%)/100)
            //
            // The innate half is the pet's own passive, and its level comes from the
            // SUMMONING SKILL: `petskill_raven_innate1` carries
            // `characterLifeModifier 0;6;12;…`, giving 108 at level 19 and 144 at 25;
            // for the spider it is `petskill_summonedpetinnate` at `charLevel/4+1`.
            // The game prints these percentages as the line "Skills <pet>".
            //
            // Checked on `_Izaar`, shots of 19 August:
            //     Sundered Wraith    27260 / 7448  = 3.660 → 266% total, 108 its own
            //     Blight Fiend      109002 / 27115 = 4.020 → 302% total, 144 its own
            //     Eldritch Webweaver 47662 / 13618 = 3.500 → 250% total,  92 its own
            // The remainder is EXACTLY 158% in all three — one and the same pet bonus,
            // as it should be. Multiplying the percentages fits none of them.
            //
            // WHAT IS MISSING: we count 143% for this character where the game wants
            // 158 — exactly 15 unaccounted for. The sources we do see agree with the
            // item tooltips one by one (Bysmiel's Iris 23, Mogdrogen's Ardor 25,
            // one item skill, the set, and the devotion skill), so the shortfall is in the
            // set of sources, not in the rule. The total is shown anyway: the rule is
            // measured, and the shortfall is visible as a number.
            DbRecord bio = game.Record(pet.Get("characterAttributeEquations"));
            string life = bio == null ? null : bio.Get("characterLife");
            double got;
            if (!string.IsNullOrEmpty(life) &&
                Equation.TryEval(life, delegate (string name)
                {
                    return string.Equals(name, "charLevel", StringComparison.OrdinalIgnoreCase)
                         ? charLevel : 0;
                }, out got)) made.BaseLife = got;
            made.LifeBonus = petLifeBonus;
            List<string> lifeFrom;
            made.OwnLife = OwnPercent(game, pet, charLevel, level, "characterLifeModifier", mods, out lifeFrom);
            made.OwnLifeFrom.AddRange(lifeFrom);
            // Armour by the same walk: a flat number off the passives and the
            // percentages off the same ones.
            made.BaseArmour = OwnPercent(game, pet, charLevel, level, "defensiveProtection", mods);
            made.OwnArmour = OwnPercent(game, pet, charLevel, level, "defensiveProtectionModifier", mods);

            list.Add(made);
        }
    }

    // ONE HIT OF A PET'S OWN — a row of the "what do we hit with" list.
    //
    // WHY A KIND APART AND NOT BESIDE YOUR OWN HITS. Decided 19 August (PLAN, P4a)
    // and put in on the 20th: Blighted Claws is the PET'S hit. It does not come from
    // your weapon, your damage percentages do not reach it, pet bonuses do (the rule
    // is measured on seven numbers, above). Folding it into one row with your hit
    // would add up things that cannot be added, so every such row carries the pet's
    // name, and `SkillLines` counts the numbers — the same count as the active pets
    // window.
    //
    // ONLY SUMMONED PETS GO INTO THE LIST — the ones ticked in that window: asking
    // "what do we hit with" about a pet that is not there makes no sense.
    //
    // AND ONLY SKILLS THAT DEAL DAMAGE. A pet has innate passives ("+108% health")
    // and housekeeping modifiers; they have no business in a list of hits.
    public class PetAttack
    {
        public string PetName;          // "Blight Fiend"
        public string SummonTitle;      // "Summon Blight Fiend"
        public string SkillName;        // "Blighted Claws"
        public string SkillDbr;
        public string PetDbr;           // the pet's own record — it has modifiers of its own
        public List<Move> ConvMoves;    // the pet's "gear" — the game does not print it
        public List<Move> OwnConv;      // the pet's innate — the game DOES print it
        public List<Move> AllConv       // both halves, for the conversion list in the window
        {
            get
            {
                List<Move> all = new List<Move>(ConvMoves ?? new List<Move>());
                if (OwnConv != null) all.AddRange(OwnConv);
                return all;
            }
        }
        public int Level;
        public Dictionary<string, double> Bonus;
        public Dictionary<string, double> Scale;   // the character's multipliers, for totems

        public override string ToString()
        {
            return Lang.T("pets.attack", SkillName, PetName, PetPool.LevelWord(Level, false));
        }

        public static List<PetAttack> Of(GameData game, Character c, ActiveSkills chosen)
        {
            List<PetAttack> list = new List<PetAttack>();
            if (game == null || c == null) return list;
            foreach (PetPool.Pet p in PetPool.Of(game, c, chosen))
            {
                if (!p.Chosen) continue;
                for (int i = 0; i < p.SkillDbrs.Count; i++)
                {
                    if (!PetPool.HasDamage(game, p.SkillDbrs[i])) continue;
                    PetAttack one = new PetAttack();
                    one.PetName = p.Name;
                    one.SummonTitle = p.Title;
                    one.SkillName = p.Skills[i];
                    one.SkillDbr = p.SkillDbrs[i];
                    one.PetDbr = p.Dbr;
                    one.ConvMoves = p.ConvMoves;
                    one.OwnConv = p.OwnConv;
                    one.Level = p.SkillLevels[i];
                    one.Bonus = p.Bonus;
                    one.Scale = p.Scale;
                    list.Add(one);
                }
            }
            return list;
        }
    }
}
