// Which skills the damage tab can be asked about, and what each of them carries.
//
// The list is what the character can actually swing with: the activated skills of
// both masteries, and the skills their gear grants. A skill gets in only if it
// deals damage — either through weaponDamagePct, which multiplies the weapon, or
// through flat damage of its own. A buff has no "damage per hit" to show.
//
// Two kinds of record hang off a skill in the tree and belong to it rather than
// to the character:
//
//   Skill_Modifier      — part of the attack. Its percentages are the attack's:
//                         CharacterTotals leaves them out of the character's sum
//                         for that very reason (MECHANICS 14b).
//   Skill_Transmuter,
//   SkillSecondary_*    — named here but NOT counted. A transmuter's parent is
//                         not derivable from the layout (see Attach below), and a
//                         SkillSecondary is usually a buff that lands *after* the
//                         hit, so folding it into that same hit would be wrong.
//
// Nothing here computes damage: that is DamageModel's job. This file only says
// which records make up a skill and at what level to read them.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Gdc.Gui
{
    // One record that makes up a skill, with the row of its per-level tables to
    // read. A skill is more than one record: the tree modifiers are separate
    // records at levels of their own.
    public class SkillPart
    {
        public DbRecord Rec;
        public int Slot;
        public string Who = "";     // what to call it when listing the terms
        // A piece that is only there for a few seconds after the skill lands — Deadly Momentum
        // hangs for five. It counts while you are swinging, but the game's own panel shows the
        // skill without it, so the two numbers are kept apart.
        public bool Temporary;
        // TWO SEPARATE MARKS, NOT ONE — split on 24 August for the measurement 14v5.
        //
        // Until that day one mark ruled both things at once: the share of weapon damage and the
        // part's own flat damage. The Explosive Strike measurement showed they DIVERGE, and one
        // field cannot hold both.
        //
        // A part that strikes with ONE weapon rather than with the average of both hands: the blast
        // at the point of impact happens once per shot, however many barrels fired. Its share of
        // weapon damage goes onto ONE hand's pool, not onto `0.75 × (both)`. Measured on `_Iman`'s
        // Fire Strike — see `DamageModel.WeaponPctOneHand`.
        public bool WeaponOneHand;
        // …and the SECOND hand specifically (`weapon1[1]`), not the first. The game calls it the
        // "off-hand weapon", and the mark is set from the skill's animation rather than from a list
        // of names — see `SkillHands` and MECHANICS 14r. Measured on two mirrored pairs of states:
        // Amarasta's Blade Burst on `_Itaan` and on `_Viliya`, and Aspect of Beronath on `_Viliya`
        // — in both the game's tooltip says "off-hand" and the panel follows the save's second
        // weapon entry.
        public bool SecondHand;
        // A PART'S OWN DAMAGE ENTERS ONCE, without the `2 × 0.75 = 1.5` factor the skill's direct
        // damage takes when dual wielding (see `DamageModel.ownShare`). This is the SECOND half of
        // the former single mark: Explosive Strike's own physical (554 in the tooltip) enters
        // exactly once, and the reading where the blast was merely marked "strikes with both" gave
        // vitality 181 against the game's 161 — its own damage drifted into the common pool and was
        // multiplied by one and a half. Measured — MECHANICS 14v5.
        public bool OwnOnce;
        // …AND WITH NO HAND NAMED, the BEST ONE PER DAMAGE TYPE is taken, each type on its own.
        //
        // This is not "some one hand": on `_Iman` the first hand is best for cold and lightning and
        // the second for fire and vitality, and the measurement demands BOTH at once. No named hand
        // can do that, and "both hands" (`0.75 × sum`) cannot either — it breaks pierce and cold,
        // which match exactly.
        //
        // The sense behind it: the blast at the point of impact happens ONCE per shot, but either
        // hand can fire — the game's tooltip prints both weapon lines ("34% main hand (2555 –
        // 3263)", "34% off hand (2694 – 3312)"). One blast reaches the panel, and for each type the
        // game shows the BETTER of the two.
        //
        // All nine numbers — MECHANICS 14v5. Set only for Explosive Strike
        // (`SkillSecondary_AttackRadius`); Ring of Steel (`Skill_AttackRadius`) has a NAMED hand,
        // the first, and there the best-hand reading is refuted by number (`_Hamarina`, bleeding
        // 42959 with the hand's dot at 171 against 177 for the better hand).
        public bool BestHand;
        // AN ITEM'S MODIFIER AIMED AT THE FORM ITSELF, NOT AT ITS SKILL. Its flat damage and dots
        // go INTO THE CHARACTER'S SUM rather than into the skill's own damage: a form is a buff,
        // and a modifier of a buff adds to you, not to the strike.
        //
        // The difference is not cosmetic, it decides the number. A skill's own damage is neither
        // multiplied by the weapon share nor averaged across hands, while the character's sum does
        // both — and the game demands the SECOND. Both readings — MECHANICS 22i.
        public bool FromForm;

        // THE PART CAME FROM THE FORM ITSELF, so its PERCENTAGES belong to the CHARACTER,
        // not to the skill. Set on everything that reaches a skill only because the parent
        // is a `Skill_Shapeshift`: the form's tree modifiers, its transmuter, and an item
        // modifier aimed at the form.
        //
        // This is the question `FormFlat` above left open in so many words — "NOT MEASURED
        // and therefore left as is: such a modifier's PERCENTAGES" — and on 21 October
        // `_555` measured it. The difference shows only where damage ARRIVES BY CONVERSION:
        // a skill's percentage is withheld from it (rule 19i), a character's is not.
        //
        //   acid of Rip and Tear, counted with the full multiplier
        //     0.75 × (21.8797 + 28.5605) × 3.55 × 8.9135 = 1196.95   game 1197
        //     0.75 × (33.07   + 34.5732) × 3.55 × 8.9135 = 1605.1    game 1605
        //   and of Feral Claws (weapon share 182%)         613.7     game 614
        //                                                  822.9     game 823
        //
        // As the skill's own it came out 1012 / 1358 and 519 / 696 — short by exactly
        // 1.65 − 0.2756, that is Form of the Beast minus what rule 19i carries over.
        // Analysis — MECHANICS 169.
        public bool FormWide;
        public SkillPart(DbRecord rec, int slot, string who) { Rec = rec; Slot = slot; Who = who; }
        public SkillPart(DbRecord rec, int slot, string who, bool temporary)
        { Rec = rec; Slot = slot; Who = who; Temporary = temporary; }
    }

    public class AttackSkill
    {
        public string Dbr = "";
        public string Name = "";
        public string Source = "";        // the mastery, or the item that grants it
        public int Level;
        public double WeaponPct;          // weaponDamagePct at this level, 0 if none
        public bool FromItem;
        // What sets it off, when it is not a button you press: the game's own
        // wording for a granted skill, the share of plain attacks a weapon pool
        // skill takes over, the chance a proc fires.
        public string When = "";
        // THE SAME AS `When`, BUT AS A NUMBER: the timeline needs the chance itself, not a caption.
        //   `PoolChance` — the share in the WEAPON POOL (`skillChanceWeight`): the skill REPLACES
        //     the ordinary attack, and no more than one of the pool fires;
        //   `HitChance`  — the chance to fire ON A HIT (`onHitActivationChance`): an addition to
        //     the strike, each with a roll of its own.
        // Zero means the skill is not launched that way.
        public double PoolChance, HitChance;
        // AN ASSIGNED ATTACK — the third way, and it is not a chance but the PLAYER'S CHOICE.
        //
        // `Skill_WeaponPool_*` (Savagery, Cadence, Righteous Fervor) is a skill PUT on the attack
        // button in place of the ordinary strike. It has no weight (`skillChanceWeight`) at all and
        // is not part of the chance pool: either you strike with it or you do not. The user,
        // 7 September: "we can put both it and the auto attack on the bar… it counts as an auto
        // attack and will trigger everything that fires on one". The save agrees: `_Jien` has both
        // Savagery and Weapon Attack on the bar at once.
        //
        // Two kinds, counted differently:
        //   CHARGES THAT SCALE (`ChargedScaling`, Savagery, Righteous Fervor) — EVERY swing strikes
        //     with the skill and the power grows with the charges: `ChargeMultipliers` is the
        //     ladder of percentages, `ChargeLevels` how many steps this skill level allows;
        //   CHARGES WITH A FINALE (`ChargedFinale`, Cadence) — the swings are ordinary and build a
        //     charge, and swing number `ChargeLevels + 1` strikes WITH THE SKILL ("every 3rd hit"
        //     at two charges).
        // `ChargeDuration` is how many seconds the charges last without a strike.
        public bool Assigned, Finale;
        // A CHANNELLED SKILL is held down and strikes once per interval rather than all at once.
        // The interval is the record's `timeBetweenAttacks` in MILLISECONDS (300 in all three
        // channel classes) DIVIDED by the character's cast speed: "deals damage every 0.24 s at
        // 100% cast speed" is 300 ms over the character's base `characterSpellCastSpeed` 1.25.
        // Zero means not channelled. See MECHANICS 74.
        public double ChannelMs;
        public int ChargeLevels;
        public double ChargeDuration;
        public double[] ChargeMultipliers;
        // A skill granted by an ITEM or a devotion star, with a trigger of its own: `AutoTrigger` is
        // the kind of condition (`AttackEnemy`, `LowHealth` and the other twelve) and `AutoChance`
        // the chance in per cent, already computed by the game's formula (MECHANICS 18b) rather
        // than the raw `chanceToRun`.
        public string AutoTrigger = "";
        public double AutoChance;
        public readonly List<SkillPart> Parts = new List<SkillPart>();
        // Records that hang off this skill in the tree and are deliberately not
        // counted, with the reason. Shown on the tab so the gap is visible.
        public readonly List<string> NotCounted = new List<string>();

        public override string ToString()
        {
            // The words come from the language file `lang/<CODE>/attack.txt`.
            return Lang.T("attack.title", Name, Source, Level) +
                   (WeaponPct > 0 ? "   " + Lang.T("attack.weaponPct", WeaponPct.ToString("0.#", CultureInfo.InvariantCulture)) : "") +
                   (string.IsNullOrEmpty(When) ? "" : "   " + When);
        }

        // The value of one field summed over every record of the skill, each at
        // its own level. This is what makes a modifier part of the attack.
        public double Sum(string field) { return Sum(field, true); }

        public double Sum(string field, bool withTemporary)
        {
            double total = 0;
            foreach (SkillPart p in Parts)
                if (withTemporary || !p.Temporary) total += Value(p.Rec, field, p.Slot);
            return total;
        }

        // The same, but only over the parts that strike with one weapon (or only over the rest).
        // A single quantity has to be split this way — the share of weapon damage: for those parts
        // it lands on a different pool.
        public double SumHand(string field, bool withTemporary, bool oneHand)
        {
            double total = 0;
            foreach (SkillPart p in Parts)
                if ((withTemporary || !p.Temporary) && p.WeaponOneHand == oneHand)
                    total += Value(p.Rec, field, p.Slot);
            return total;
        }

        // THE SHARE OF WEAPON DAMAGE THAT LANDS ON THIS HAND. Zero means the hand takes no part in
        // the strike, and then the game prints no line for it at all.
        //
        // A part that strikes with one hand goes to that hand only; a part that strikes with both
        // goes to each. They must not be added into one number: Amarasta's Blade Burst has its
        // whole share on the off hand, and "as much on the main" would be invention. This is
        // printed by `DamageForm.AddHandShare` in the game's own words.
        public double WeaponPctOf(bool offHand, bool withTemporary)
        {
            double total = 0;
            foreach (SkillPart p in Parts)
            {
                if (!withTemporary && p.Temporary) continue;
                if (p.WeaponOneHand && p.SecondHand != offHand) continue;
                total += Value(p.Rec, "weaponDamagePct", p.Slot);
            }
            return total;
        }

        // The part of the "one hand" share that belongs to the SECOND hand.
        public double SumSecondHand(string field, bool withTemporary)
        {
            double total = 0;
            foreach (SkillPart p in Parts)
                if ((withTemporary || !p.Temporary) && p.WeaponOneHand && p.SecondHand)
                    total += Value(p.Rec, field, p.Slot);
            return total;
        }

        // The part of the "one hand" share whose hand is NOT NAMED (see `BestHand`).
        public double SumBestHand(string field, bool withTemporary)
        {
            double total = 0;
            foreach (SkillPart p in Parts)
                if ((withTemporary || !p.Temporary) && p.WeaponOneHand && p.BestHand)
                    total += Value(p.Rec, field, p.Slot);
            return total;
        }

        public bool HasTemporary
        {
            get { foreach (SkillPart p in Parts) if (p.Temporary) return true; return false; }
        }

        public string TemporaryNames
        {
            get
            {
                List<string> names = new List<string>();
                foreach (SkillPart p in Parts) if (p.Temporary) names.Add(p.Who);
                return string.Join(", ", names.ToArray());
            }
        }

        // A CHARGED SKILL: how many times over its base the panel shows the damage.
        //
        // The MASTERIES hold two of them — Savagery (`playerclass06/savagery1`) and Righteous
        // Fervor (`playerclass09/righteousfervor1`), both of class
        // `Skill_WeaponPool_ChargedScaling`. Every attack adds a charge and one burns off every
        // `skillChargeDuration` seconds without attacking. A THIRD comes from an ITEM — Runic Bolts
        // from the Runebinder's Spellthrower (`itemskillsgdx1/legendary/runicbolts`), the same
        // class, 8 steps.
        //
        // `skillChargeMultipliers` is a list by CHARGE NUMBER (not by skill level), and the game's
        // own template says so: `templatebase/skill_charge.tpl`, "Damage multiplier per charge, in
        // percent". How many charges there are is `skillChargeLevel`, and that one is by skill
        // level. For Savagery at level 23 it is 8, and the eighth value of
        // `30;50;65;80;90;100;108;115;120` is 115.
        //
        // THE PANEL SHOWS THE LAST CHARGE, not the one the character currently holds: the
        // screenshot was taken standing still (zero charges) and this very multiplier is still
        // needed. Measured on `_Jien` with four lines of Savagery:
        //
        //   pierce      1145 × 1.15 = 1317   game 1326   (−0.7%, our constant pierce offset)
        //   poison       123 × 1.15 =  142   game  141
        //   decay         95 × 1.15 =  109   game  109
        //   frostburn    684 × 1.15 + 2629 = 3416   game 3420, and 5341 against 5345 at the high
        //                end — both points within 0.1%
        //
        // No other value of the list fits: 108% would give pierce 1237 (−6.7%), 120% 1374 (+3.6%).
        //
        // READ THIS MEASUREMENT WITH CARE: it was taken with Savagery at LEVEL 23, and it has since
        // been reset to 8, where `skillChargeLevel` gives six charges and the sixth step is 100%.
        // So `_Jien` has no charge today and nothing to check with. The charged skills live in the
        // stands are Righteous Fervor on `_Itaan` (120%) and Runic Bolts on `_Iminika` (115%).
        public double ChargeMultiplier() { return ChargeMultiplier(true); }

        public double ChargeMultiplier(bool withTemporary)
        {
            double mult = 1;
            foreach (SkillPart p in Parts)
            {
                if (!withTemporary && p.Temporary) continue;
                mult *= ChargeOf(p);
            }
            return mult;
        }

        // The charge of ONE part of a skill. The multiplier sits on the record that carries it
        // (`skillChargeMultipliers` is its field) and multiplies what THAT record gives: its share
        // of weapon damage, its flat damage, its dots and its percentages. Modifier records of the
        // same skill are separate records, carry no charge, and their damage is not multiplied.
        //
        // Measured 22 August on `_Iminika`'s Runic Bolts (MECHANICS 19z): its three dots have a
        // flat 100 per second of their own, and the ratio of the panel's two ends settles it —
        // 115.05 against 115 = 100 × 1.15, and 100 without the charge. All three dots match to the
        // unit after the fix.
        public static double ChargeOf(SkillPart p) { return ChargeOf(p, 0); }

        // `atCharge` — count NOT at full charge but at this step. Zero (and anything above the
        // limit) means full charge, as before.
        //
        // Full charge is right for the panel: it shows a skill at its best, and so does the game's
        // tooltip. The queue, however, must show what happens: Savagery builds its charge strike by
        // strike and the first strikes are weaker. ONE function counts it either way — the one
        // measured on 22 August; the step is simply passed in rather than read from the record.
        public static double ChargeOf(SkillPart p, int atCharge)
        {
            string list = p == null || p.Rec == null ? null : p.Rec.Get("skillChargeMultipliers");
            if (string.IsNullOrEmpty(list)) return 1;
            int charges = (int)Math.Round(Value(p.Rec, "skillChargeLevel", p.Slot));
            if (atCharge > 0 && atCharge < charges) charges = atCharge;
            string[] steps = list.Split(';');
            if (charges <= 0 || steps.Length == 0) return 1;
            if (charges > steps.Length) charges = steps.Length;
            double d;
            if (double.TryParse(steps[charges - 1], NumberStyles.Float,
                                CultureInfo.InvariantCulture, out d) && d > 0)
                return d / 100.0;
            return 1;
        }

        public static double Value(DbRecord rec, string field, int slot)
        {
            if (rec == null) return 0;
            string v = rec.Get(field);
            if (string.IsNullOrEmpty(v)) return 0;
            string[] parts = v.Split(';');
            double d;
            return double.TryParse(parts[Math.Min(Math.Max(0, slot), parts.Length - 1)],
                                   NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
        }
    }

    public static class AttackSkills
    {
        // A fragment of a record path; parts matching it are dropped from skills (`--dropskill=`).
        // See where it is applied, at the end of `Of`.
        public static string DropPart;

        // THE FORM THE CHARACTER IS STANDING IN, by the name the game prints for it
        // (for example, `#form <name>` in a stand). Empty means "not in a form", and then everything
        // works exactly as before: a form's modifiers reach only the skills the form grants.
        public static string InForm = "";

        static bool IsFormWeAreIn(GameData game, string formDbr)
        {
            if (string.IsNullOrEmpty(InForm) || string.IsNullOrEmpty(formDbr)) return false;
            string title = SkillText.Name(game, formDbr) ?? formDbr;
            return string.Equals(title, InForm, StringComparison.CurrentCultureIgnoreCase);
        }

        // THE WHOLE ROW OF A FIELD, not one value by level. The charge ladder needs it
        // (`skillChargeMultipliers` = "30;50;65;…;120"): there the semicolon separates CHARGE
        // STEPS, not skill levels, and reading it by the level slot is a sure way to take someone
        // else's number.
        static double[] Series(DbRecord rec, string field)
        {
            string v = rec == null ? null : rec.Get(field);
            if (string.IsNullOrEmpty(v)) return null;
            string[] parts = v.Split(';');
            List<double> made = new List<double>();
            foreach (string one in parts)
            {
                double d;
                if (double.TryParse(one, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    made.Add(d);
            }
            return made.Count == 0 ? null : made.ToArray();
        }

        // The eleven damage roots, as the record names them.
        static readonly string[] Types = {
            "Physical", "Pierce", "Fire", "Cold", "Lightning", "Poison",
            "Life", "Aether", "Chaos", "Bleeding", "Elemental" };

        // A skill granted by an item or a set — all of them, no class filter.
        //
        // `Of` keeps only what HITS, so the `Choosable` filter drops summons, debuffs and
        // buffs — right, they deal no damage. But they still fire in the queue and must not
        // vanish: that is how Conjure Living Blossom (a set's summon) and Annihilation
        // (a debuff on crit) of `_Acid Vines` were missing from log and report.
        public sealed class GrantedSkill
        {
            public string Dbr, Name, Source, Class, When;
            public int Level;
            public string Trigger = "";   // `AttackEnemy`, `HitByEnemy` etc.; empty — no condition
            public double Chance;         // chance in percent, by the game's formula
        }

        public static List<GrantedSkill> GearGranted(GameData game, Character c)
        {
            List<GrantedSkill> made = new List<GrantedSkill>();
            if (game == null || c == null) return made;
            List<Item> worn = new List<Item>();
            worn.AddRange(c.Equipment); worn.AddRange(c.Weapons1);
            foreach (Item it in worn)
            {
                if (string.IsNullOrEmpty(it.BaseName)) continue;
                string itemName = ItemViewFactory.Build(game, it, false).Name;
                if (string.IsNullOrEmpty(itemName)) itemName = it.BaseName;
                foreach (string dbr in new[] { it.BaseName, it.PrefixName, it.SuffixName,
                                               it.RelicName, ItemViewFactory.RelicBonusOf(game, it),
                                               it.AugmentName, it.ModifierName, it.AscensionName })
                {
                    DbRecord r = string.IsNullOrEmpty(dbr) ? null : game.Record(dbr);
                    if (r == null) continue;
                    string granted = r.Get("itemSkillName");
                    if (string.IsNullOrEmpty(granted)) continue;
                    AddGranted(game, made, granted, GrantedLevel(r, game.Record(it.BaseName)),
                               itemName, r.Get("itemSkillAutoController"));
                }
            }
            foreach (KeyValuePair<string, int> kv in ItemSkillMods.SetSizes(game, worn))
            {
                DbRecord sr = game.Record(kv.Key);
                if (sr == null) continue;
                string granted = sr.Get("itemSkillName");
                if (string.IsNullOrEmpty(granted)) continue;
                int lvl = ItemSkillMods.GrantedLevelOfSet(sr, kv.Value);
                if (lvl <= 0) continue;
                AddGranted(game, made, granted, lvl,
                           Lang.T("totals.setShort", ItemViewFactory.SetNameOf(game, sr) ?? kv.Key),
                           sr.Get("itemSkillAutoController"));
            }
            return made;
        }

        static void AddGranted(GameData game, List<GrantedSkill> made, string granted, int level,
                               string source, string controller)
        {
            foreach (GrantedSkill one in made)
                if (string.Equals(one.Dbr, granted, StringComparison.OrdinalIgnoreCase)) return;
            DbRecord gr = game.Record(granted);
            if (gr == null) return;
            GrantedSkill g = new GrantedSkill();
            g.Dbr = granted;
            g.Name = SkillText.Name(game, granted) ?? granted;
            g.Source = source;
            g.Class = gr.Class ?? "";
            g.Level = level > 0 ? level : 1;
            g.When = SkillText.ConditionOf(game, granted, controller) ?? "";
            string trg; double chn;
            if (SkillText.AutoOf(game, granted, controller, null, out trg, out chn))
            { g.Trigger = trg ?? ""; g.Chance = chn; }
            made.Add(g);
        }

        // Which skills are CHANNELLED. The mark is the class, and there are exactly three:
        //   Skill_AttackSpellBeam    Albrecht's Aether Ray, Oblivion
        //   Skill_AttackSpellCone    Flames of Ignaffar, a component's acid spray
        //   Skill_AttackSpellDrain   Drain Essence
        // All carry `timeBetweenAttacks 300` and `skillCooldownTime 0.3`. Two more classes
        // have `timeBetweenAttacks 200` (the spins of Eye of Reckoning and Winds of
        // Asterkarn) — NOT taken here: the game says nothing about their interval, and
        // whether casting or attack speed drives them is unknown. No guessing.
        public static double ChannelMsOf(DbRecord rec)
        {
            if (rec == null) return 0;
            string cls = rec.Class ?? "";
            if (cls != "Skill_AttackSpellBeam" && cls != "Skill_AttackSpellCone" &&
                cls != "Skill_AttackSpellDrain") return 0;
            double ms;
            string v = rec.Get("timeBetweenAttacks");
            return !string.IsNullOrEmpty(v) &&
                   double.TryParse(v.Split(';')[0], System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out ms) && ms > 0
                 ? ms : 0;
        }

        // A FORM IS A CHOICE IN "WHAT WE HIT WITH" — the user's decision: a form sits in the
        // drop-down list next to the auto-attack, we pick one of the two, and in the timeline
        // it hits with its own skills.
        //
        // `Skill_Shapeshift` itself does not hit and carries no combat numbers at all —
        // Werewolf's record holds only `grantedSkills`, `replacementAnims` and meshes. What
        // hits are the skills it GRANTS, long counted here and checked against the game on
        // `_555` (MECHANICS 22). The one missing piece was saying we ARE in the form — and
        // that is exactly the choice in the list.
        //
        // Form name → its MAIN granted hit (first by record):
        //
        //   Werewolf   werewolf1_skill01_claws     Feral Claws   Skill_AttackWeapon
        //   Wereraven  wereraven1_skill01_icicles  Ice Talons    Skill_AttackWave
        //
        // The whole game database has exactly two forms, both on the berserker (census).
        public sealed class Form
        {
            public string Dbr = "";        // the form's own record
            public string Name = "";       // "Werewolf"
            public int Level;
            public AttackSkill Attack;     // what it hits with instead of the auto-attack
            public readonly List<AttackSkill> Granted = new List<AttackSkill>();
        }

        public static List<Form> Forms(GameData game, Character c, List<AttackSkill> known)
        {
            List<Form> made = new List<Form>();
            if (game == null || c == null) return made;
            SkillText.LevelBonus bonus = SkillText.LevelBonus.Of(game, c);
            foreach (Skill s in c.Skills)
            {
                if (string.IsNullOrEmpty(s.Name) || s.Level <= 0) continue;
                DbRecord rec = game.Record(s.Name);
                if (rec == null ||
                    !(rec.Class ?? "").StartsWith("Skill_Shapeshift", StringComparison.OrdinalIgnoreCase)) continue;
                string granted = rec.Get("grantedSkills");
                if (string.IsNullOrEmpty(granted)) continue;
                Form f = new Form();
                f.Dbr = s.Name;
                f.Name = SkillText.Name(game, s.Name) ?? s.Name;
                f.Level = s.Level + (bonus == null ? 0 : bonus.For(s.Name, s.Level));
                foreach (string g in granted.Split(';'))
                {
                    string gd = g.Trim();
                    if (gd.Length == 0) continue;
                    foreach (AttackSkill a in known ?? new List<AttackSkill>())
                    {
                        if (!string.Equals(a.Dbr, gd, StringComparison.OrdinalIgnoreCase)) continue;
                        f.Granted.Add(a);
                        if (f.Attack == null) f.Attack = a;
                        break;
                    }
                }
                if (f.Attack != null) made.Add(f);
            }
            return made;
        }

        public static List<AttackSkill> Of(GameData game, Character c)
        {
            List<AttackSkill> made = new List<AttackSkill>();
            if (game == null || c == null) return made;

            SkillText.LevelBonus bonus = SkillText.LevelBonus.Of(game, c);
            Attach attach = Attach.Of(game, c);
            // THE FORM'S MODIFIERS, TO BE LAID ON EVERY SKILL once the list is complete.
            // Filled only when `#form` names the form these belong to; see where it is
            // emptied, at the end of the build.
            List<SkillPart> spread = new List<SkillPart>();

            // The character's own skills, mastery by mastery.
            foreach (Skill s in c.Skills)
            {
                if (s.Level <= 0 || string.IsNullOrEmpty(s.Name)) continue;
                string folder = ItemViewFactory.MasteryFolder(s.Name);
                if (folder == null) continue;
                DbRecord rec = game.Record(s.Name);
                if (rec == null || !Choosable(rec.Class)) continue;
                int level = s.Level + bonus.For(s.Name, s.Level);
                AttackSkill a = Make(game, s.Name, level,
                                     ItemViewFactory.MasteryName(game, folder) ?? folder, false);
                if (a == null) continue;
                int trigSlot = SkillText.Slot(rec, level);
                a.When = Trigger(rec, trigSlot);
                // The same two marks as numbers — for the timeline (see the fields).
                if ((rec.Class ?? "").StartsWith("Skill_WPAttack", StringComparison.OrdinalIgnoreCase))
                    a.PoolChance = AttackSkill.Value(rec, "skillChanceWeight", trigSlot);
                // An ASSIGNED ATTACK is known by its class, not by weight (it has none).
                if ((rec.Class ?? "").StartsWith("Skill_WeaponPool", StringComparison.OrdinalIgnoreCase))
                {
                    a.Assigned = true;
                    a.Finale = (rec.Class ?? "").IndexOf("Finale", StringComparison.OrdinalIgnoreCase) >= 0;
                    a.ChargeLevels = (int)Math.Round(AttackSkill.Value(rec, "skillChargeLevel", trigSlot));
                    a.ChargeDuration = AttackSkill.Value(rec, "skillChargeDuration", trigSlot);
                    // The multiplier ladder is ONE per skill, not per level: these are the
                    // charge steps, and the level slot does not apply to them.
                    a.ChargeMultipliers = Series(rec, "skillChargeMultipliers");
                }
                a.HitChance = rec.GetInt("onHitActivationChance");
                made.Add(a);
            }

            // The modifiers, onto the skill they belong to.
            foreach (Skill s in c.Skills)
            {
                if (s.Level <= 0 || string.IsNullOrEmpty(s.Name)) continue;
                DbRecord rec = game.Record(s.Name);
                if (rec == null) continue;
                string cls = rec.Class ?? "";
                int level = s.Level + bonus.For(s.Name, s.Level);
                string title = SkillText.Name(game, s.Name) ?? s.Name;

                TreeRole role = RoleOf(cls);
                if (role == TreeRole.Part &&
                    !cls.StartsWith("SkillSecondary", StringComparison.OrdinalIgnoreCase))
                {
                    // A MODIFIER CAN BELONG TO SEVERAL SKILLS AT ONCE, and the game says so
                    // itself through `skillDependancy`. Ulzuin's Chosen is named by three
                    // (Stun Jacks, Grenado, Canister Bomb), Nidalla's Hidden Hand by three
                    // as well, Artifact Handling by two — and the game's own description
                    // lists them.
                    //
                    // The parent used to be found ONLY by position in the tree, where a
                    // button has a single neighbour, so the modifier reached one skill out
                    // of three; worse, Artifact Handling's skills are not even joined to it
                    // by a line.
                    //
                    // Position is kept as the fallback: in 43 records out of 47 the field
                    // names exactly the parent the tree does, and for a modifier without
                    // the field there is no other source.
                    foreach (string ownerDbr in attach.Parents(game, s.Name))
                    {
                        AttackSkill owner = Find(made, ownerDbr);
                        if (owner == null)
                        {
                            // A FORM PARENT DOES NOT HIT ITSELF — the skills it GRANTS do,
                            // so the modifier must land on them.
                            //
                            // The berserker's Form of the Beast (`playerclass10/passive04`)
                            // is a plain `Skill_Modifier` and names its parents itself:
                            // `skillDependancy` = wereraven and werewolf. Both are
                            // `Skill_Shapeshift`, never enter the "what we hit with" list,
                            // so the modifier was lost entirely — and it carries a lot:
                            // `offensiveDamageMultModifier` (total damage ×1.12 at level 16)
                            // and +24% to both the attack- and cast-speed caps. The game
                            // states plainly that the bonuses apply while in werewolf or
                            // wereraven form.
                            //
                            // Which skills a form grants is also in the record, field
                            // `grantedSkills`; no other source is needed.
                            //
                            // MEASURED — `saves/_555`, Feral Claws: with this fix and with
                            // the form's level (see `SkillText.LevelBonus.For`) four types
                            // match to the unit at once:
                            //     physical 3251 – 3935, cold 321, poison 511 – 685
                            // Without the multiplier it came out exactly 12% lower.
                            DbRecord od = game.Record(ownerDbr);
                            string granted = od == null ? null : od.Get("grantedSkills");
                            if (string.IsNullOrEmpty(granted)) continue;
                            // …BUT NOT ONTO EVERY GRANTED SKILL: IF THE MODIFIER NAMES A
                            // SKILL IN `subSkillName`, IT EDITS THAT ONE ONLY.
                            //
                            // A form grants TWO skills, and a form's modifier is either
                            // general or addressed — the record itself tells them apart:
                            //
                            //   passive04  Form of the Beast  no subSkillName → both
                            //   werewolf2  Voracity          → werewolf1_skill01_claws
                            //   wereraven2 Glacial Talons    → wereraven1_skill01_icicles
                            //   werewolf3  Recklessness      no subSkillName → both
                            //   wereraven3 Everwinter        no subSkillName → both
                            //
                            // In the WHOLE game `subSkillName` is set on exactly two records,
                            // and they are these two (census over all `records/skills/`), so
                            // the rule is narrow and risks exactly what it was measured on.
                            //
                            // MEASURED — `saves/_555`, Cold Snap
                            // (`wereraven1_skill02_icering`, lvl 18). Freeze:
                            //     own      408 (offensiveSlowColdMin[18])
                            //     + alien  230 (wereraven2, Ice Talons)  = 638
                            //     638 × 10.9592 = 6992   ← what we had
                            //     408 × 10.9592 = 4471   ← the game prints 4471
                            // The same skill's pierce matched even before the fix, and Ice
                            // Talons (which the 230 belongs to) matches on both lines — so
                            // there was exactly one alien addend.
                            //
                            // Second witness — Rip and Tear (`werewolf1_skill02_charge`,
                            // lvl 20): its own bleeding is 346, we counted 531 = 346 + 185,
                            // the 185 being Voracity, addressed to Feral Claws.
                            string only = rec.Get("subSkillName");
                            // WHO GETS IT. Normally the skills the form GRANTS, and no one
                            // else: outside the form its modifier is not on anything.
                            //
                            // BUT `#form` SAYS THE CHARACTER IS IN THE FORM — and then the
                            // form's modifier lies on EVERY blow, not only on the form's own
                            // skills. Measured on `_555`, 21 October, on the three skills
                            // that work in both states (Guillotine, Leap, Vampiric Shadows):
                            // each of them gains in the form EXACTLY Form of the Beast's
                            // +165% total damage, to the fifth digit on six bracket ends —
                            // the arithmetic is in MECHANICS 169.
                            //
                            // An ADDRESSED modifier (`subSkillName`) is not spread: it edits
                            // the skill it names and nothing else, in the form as out of it.
                            List<AttackSkill> takers = new List<AttackSkill>();
                            foreach (string g in granted.Split(';'))
                            {
                                string gd = g.Trim();
                                if (gd.Length == 0) continue;
                                if (!string.IsNullOrEmpty(only) &&
                                    only.IndexOf(gd, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                AttackSkill ga = Find(made, gd);
                                if (ga != null && !takers.Contains(ga)) takers.Add(ga);
                            }
                            // ...AND IT IS SPREAD AT THE END, not here: the list is still
                            // being built at this point and holds only the class skills,
                            // while the ones the GEAR grants are added below. Spreading on
                            // the spot gave the form's bonus to Leap and to nothing else.
                            if (string.IsNullOrEmpty(only) && IsFormWeAreIn(game, ownerDbr))
                                spread.Add(new SkillPart(rec, SkillText.Slot(rec, level),
                                                         Lang.T("attack.part", title, level)));
                            foreach (AttackSkill ga in takers)
                            {
                                SkillPart mp = new SkillPart(rec, SkillText.Slot(rec, level),
                                                             Lang.T("attack.part", title, level));
                                // Its PERCENTAGES are the character's while the form is up —
                                // see SkillPart.FormWide. Its flat damage is NOT touched by
                                // this mark and stays the skill's, as it was.
                                mp.FormWide = true;
                                ga.Parts.Add(mp);
                            }
                            continue;
                        }
                        owner.Parts.Add(new SkillPart(rec, SkillText.Slot(rec, level),
                                                      Lang.T("attack.part", title, level)));
                    }
                }
                else if (role == TreeRole.Transmuter)
                {
                    // A TRANSMUTER FOLDS INTO THE HIT.
                    //
                    // It used to be only named ("which skill it is tied to is not derivable
                    // from the files"). It is derivable: the parent lists
                    // `connectortransmuterstub` in `skillConnectionOn`, and the nearest of
                    // the marked ones is taken (see Attach). Folding it in matters because it
                    // carries real numbers: Ring of Frost — the transmuter of Ring of Steel —
                    // gives 100% pierce → cold conversion, and the skill panel then has no
                    // pierce line AT ALL, exactly as it should.
                    //
                    // "NO PARENT" AND "PARENT DOES NOT HIT" ARE DIFFERENT, and the difference
                    // matters. Without it Breath of Belgothian (the transmuter of Pneumatic
                    // Burst, `nightbladeenchant1b.dbr`) was reported as lost on EVERY
                    // nightblade hit. Its parent is found just fine — it is simply a 60 s
                    // self-buff (`Skill_BuffSelfDuration`), not a hit, so it never enters the
                    // "what we hit with" list and `Find` returns null.
                    //
                    // Such a transmuter has no business in the hit window: it changes a
                    // temporary buff, and the buff itself is outside the stat panel — the
                    // game counts that panel without applied buffs.
                    string byLayout = attach.Extra(s.Name);
                    AttackSkill owner = Find(made, byLayout);
                    if (owner == null)
                    {
                        // A FORM PARENT DOES NOT HIT ITSELF — same as for the modifier
                        // above: a form's transmuter lands on the skills the form GRANTS
                        // (`grantedSkills`).
                        //
                        // The game has two forms with a transmuter, both on the berserker:
                        //   werewolf1b   pierce → chaos 100%
                        //   wereraven1b  COLD   → CHAOS 100%
                        //
                        // MEASURED — `saves/_555`, both wereraven skills. Our numbers had no
                        // chaos line AT ALL while the game prints one, and its three numbers
                        // fit ONE chaos multiplier, 11.1486:
                        //
                        //   Ice Talons  cold base 451 – 483 → 5028 – 5384
                        //   Cold Snap   cold base 518       → 5775
                        //   the game                          5028 – 5384 and 5775
                        //
                        // Our cold multiplier is 10.7895 — so it is not the base but the
                        // receiver: chaos has its own character percentages (+32% over cold)
                        // and its own stat.
                        //
                        // THE DOT DOES NOT MOVE ALONG, and the game confirms it: freeze
                        // stayed freeze on both skills (2521 and 4471). Chaos is a type
                        // WITHOUT a dot, and conversion goes only to receivers that have one
                        // (the same rule as in `Conversion.Pass` and `LeftForPercent`).
                        DbRecord pd = string.IsNullOrEmpty(byLayout) ? null : game.Record(byLayout);
                        string grantedT = pd == null ? null : pd.Get("grantedSkills");
                        if (!string.IsNullOrEmpty(grantedT))
                        {
                            foreach (string g in grantedT.Split(';'))
                            {
                                AttackSkill ga = Find(made, g.Trim());
                                if (ga == null) continue;
                                SkillPart tp = new SkillPart(rec, SkillText.Slot(rec, level),
                                                             Lang.T("attack.transmuter", title, level));
                                tp.FormWide = true;   // the form's, not the skill's — see FormWide
                                ga.Parts.Add(tp);
                            }
                            // ...AND A FORM'S TRANSMUTER IS ON EVERY BLOW TOO WHILE THE FORM
                            // IS UP. Yesterday this said the opposite — "not spread" — and
                            // said plainly why: the only transmuter `_555` has belongs to the
                            // WERECROW, while the numbers in hand were the werewolf's, so the
                            // two readings could not be told apart (MECHANICS 169a).
                            //
                            // The werecrow numbers came the next day and answer at a glance:
                            // in that form all three outside skills LOSE their cold line and
                            // GAIN a chaos one, which is exactly X'thon's Claws
                            // (`wereraven1b`, cold → chaos 100%):
                            //
                            //     out of form   Guillotine cold 167    no chaos at all
                            //     in werecrow   no cold line          chaos 236
                            //
                            // So it is spread, and `check Cold 0 0` in `stand-555-raven.tsv`
                            // is what guards it.
                            if (IsFormWeAreIn(game, byLayout))
                            {
                                SkillPart sp = new SkillPart(rec, SkillText.Slot(rec, level),
                                                             Lang.T("attack.transmuter", title, level));
                                sp.FormWide = true;
                                spread.Add(sp);
                            }
                            continue;
                        }
                        // The parent WAS found but does not hit — stay silent, nothing lost.
                        if (!string.IsNullOrEmpty(byLayout)) continue;
                        string why = Lang.T("attack.transmuterOrphan", title, level);
                        foreach (AttackSkill a in made)
                            if (ItemViewFactory.MasteryFolder(a.Dbr) == ItemViewFactory.MasteryFolder(s.Name))
                                a.NotCounted.Add(why);
                        continue;
                    }
                    owner.Parts.Add(new SkillPart(rec, SkillText.Slot(rec, level),
                                                  Lang.T("attack.transmuter", title, level)));
                }
                else if (cls.StartsWith("SkillSecondary", StringComparison.OrdinalIgnoreCase))
                {
                    // A secondary effect is usually a buff the skill hangs on you
                    // when it lands, so at first it was left out of the same hit.
                    // The game says otherwise in its own words: Deadly Momentum affects
                    // all attacks while active — it goes up on the first Cadence finale
                    // and stays up for five seconds, which is longer than the gap between
                    // swings. So while you are hitting, it is on, and the hit it belongs
                    // to is the one to show it in.
                    //
                    // But a "secondary" is not always a buff: `SkillSecondary_AttackRadius`
                    // is the blast at the impact point of the same shot — part of the hit,
                    // not something hung on you for seconds. RoleOf tells them apart, and
                    // the difference shows in numbers: `_Iman`'s Fire Strike burn is 678
                    // without Explosive Strike against the game's 3035, and exactly 3035
                    // with it.
                    AttackSkill owner = Find(made, attach.Extra(s.Name));
                    if (owner == null) continue;
                    SkillPart part = PartOf(game, s.Name, level, Lang.T("attack.part", title, level));
                    if (part != null)
                    {
                        part.Temporary = role == TreeRole.Buff;
                        // The blast happens once per shot: the weapon share is taken from
                        // ONE hand, and its own damage enters ONCE, without the 1.5
                        // multiplier. Both marks are set separately here — the measurement
                        // in MECHANICS 14v5 speaks of two distinct things. See DamageModel.
                        part.WeaponOneHand = role == TreeRole.Part;
                        part.OwnOnce = role == TreeRole.Part;
                        part.BestHand = role == TreeRole.Part;
                        owner.Parts.Add(part);
                    }
                }
            }

            // The skills the gear grants. Their level is the item's, not the
            // character's, and it is sometimes an equation over the item's level.
            List<Item> worn = new List<Item>();
            // THE SPARE WEAPON SET IS NOT INCLUDED — MECHANICS 14d8. The game counts only
            // the worn set; an item in the second set gives nothing until you switch to it.
            // Found on `_222` and cleaned out in every place at once: two of them had
            // already cost wrong numbers (the bonus sum and skill levels).
            worn.AddRange(c.Equipment); worn.AddRange(c.Weapons1);
            foreach (Item it in worn)
            {
                if (string.IsNullOrEmpty(it.BaseName)) continue;
                string itemName = ItemViewFactory.Build(game, it, false).Name;
                if (string.IsNullOrEmpty(itemName)) itemName = it.BaseName;
                foreach (string dbr in new[] { it.BaseName, it.PrefixName, it.SuffixName,
                                               it.RelicName, ItemViewFactory.RelicBonusOf(game, it),
                                               it.AugmentName, it.ModifierName, it.AscensionName })
                {
                    DbRecord r = string.IsNullOrEmpty(dbr) ? null : game.Record(dbr);
                    if (r == null) continue;
                    string granted = r.Get("itemSkillName");
                    if (string.IsNullOrEmpty(granted)) continue;
                    // The same filter as for a class skill: plenty of items grant
                    // a buff (Chilling Weapons, Bloodthirster), and a buff's numbers
                    // are already in the character's sum.
                    DbRecord gr = game.Record(granted);
                    if (gr == null || !Choosable(gr.Class)) continue;
                    // The level comes from an equation over the BASE item's `itemLevel`:
                    // an affix has no `itemLevel` of its own (see `GrantedLevel`).
                    AttackSkill a = Make(game, granted, GrantedLevel(r, game.Record(it.BaseName)),
                                         itemName, true);
                    if (a == null || Find(made, a.Dbr) != null) continue;
                    a.When = SkillText.ConditionOf(game, granted, r.Get("itemSkillAutoController")) ?? "";
                    // The same condition as numbers — for the timeline (see the fields).
                    string trg; double chn;
                    if (SkillText.AutoOf(game, granted, r.Get("itemSkillAutoController"),
                                         null, out trg, out chn))
                    { a.AutoTrigger = trg; a.AutoChance = chn; }
                    made.Add(a);
                }
            }

            // ...AND A SKILL GRANTED BY A SET — the same kind, the same rule, only the
            // level is taken from a list by the number of worn pieces. The walk went over
            // the ITEM's parts, while `itemSkillName` also sits on the SET's record: on 144
            // sets out of 244. Details — PLAN, the set census.
            foreach (KeyValuePair<string, int> kv in ItemSkillMods.SetSizes(game, worn))
            {
                DbRecord sr = game.Record(kv.Key);
                if (sr == null) continue;
                string granted = sr.Get("itemSkillName");
                if (string.IsNullOrEmpty(granted)) continue;
                int lvl = ItemSkillMods.GrantedLevelOfSet(sr, kv.Value);
                if (lvl <= 0) continue;
                DbRecord gr = game.Record(granted);
                if (gr == null || !Choosable(gr.Class)) continue;
                AttackSkill a = Make(game, granted, lvl,
                                     Lang.T("totals.setShort", ItemViewFactory.SetNameOf(game, sr) ?? kv.Key), true);
                if (a == null || Find(made, a.Dbr) != null) continue;
                a.When = SkillText.ConditionOf(game, granted, sr.Get("itemSkillAutoController")) ?? "";
                // THE CONDITION AS NUMBERS — FOR A SET TOO. An item's skill always got it,
                // a set's skill did not: the caption "20% chance on attack" was there, the
                // timeline did not see it, and such a skill NEVER fired in the queue. Found
                // on `_Acid Vines`' Conjure Living Blossom.
                string strg; double schn;
                if (SkillText.AutoOf(game, granted, sr.Get("itemSkillAutoController"),
                                     null, out strg, out schn))
                { a.AutoTrigger = strg; a.AutoChance = schn; }
                made.Add(a);
            }

            // The modifiers the gear brings. They are not in the save at all — the
            // records of what is worn are the only place they exist — and they
            // apply the moment the item goes on, measured in the game: Whirling Death
            // reads 22% chance without `_Amakata`'s ascended amulet and 27% with it. Their level is the first row of their own table; every one
            // seen so far has skillMaxLevel 1.
            foreach (ItemSkillMods.Entry e in ItemSkillMods.Of(game, c).All)
            {
                AttackSkill owner = Find(made, e.Skill);
                if (owner == null)
                {
                    // ...AND THE SAME THING A THIRD TIME: A FORM PARENT DOES NOT HIT.
                    // A modifier from an ITEM aimed at `Skill_Shapeshift` was lost in
                    // exactly the same way as a tree modifier and a transmuter — the third
                    // place of one rule in one file.
                    //
                    // MEASURED — The Crimson Moon on `_555`. The amulet modifies Werewolf
                    // (`modifiedSkillName` → `werewolf1.dbr`) and its modifier gives 50
                    // bleeding per second. The game prints it in the amulet's tooltip:
                    // 3651 bleeding over 3 sec., i.e. 1217 per second, and
                    // 1217 / 24.3328 = exactly 50.
                    //
                    // Without those 50 the bleeding of BOTH form skills did not match, and
                    // the two equations named the same number independently:
                    //
                    //   Feral Claws   0.75 × 298 × 1.66 × 27.2527 + own = 13892
                    //                 with base 248 we got              12196
                    //   Rip and Tear  0.75 × 298 × 3.31 × 27.2527 + own = 27233
                    //                 we got                             23850
                    //
                    // where 298 = 248 (the character's sum outside the form) + 50. Outside
                    // the form the base 248 is confirmed three times — ordinary attack,
                    // Decapitate and Vampiric Shadows — so the 50 is the form's alone.
                    DbRecord fr = game.Record(e.Skill);
                    if (fr == null) continue;
                    if ((fr.Class ?? "").Equals("Skill_Shapeshift", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string g in (fr.Get("grantedSkills") ?? "").Split(';'))
                        {
                            AttackSkill ga = Find(made, g.Trim());
                            if (ga == null) continue;
                            SkillPart fp = new SkillPart(e.Modifier, 0, Lang.T("attack.fromItemInForm", e.Source));
                            fp.FromForm = true;
                            fp.FormWide = true;   // its percentages are the character's too — see FormWide
                            ga.Parts.Add(fp);
                        }
                        // ...AND ON EVERY OTHER BLOW TOO, WHEN WE ARE STANDING IN THAT FORM.
                        // An item modifier aimed at a form is the form's, exactly like the
                        // form's own tree modifiers above, and `_555`'s bleeding says so in
                        // numbers: without the Crimson Moon's 50 the three in-form skills
                        // were short by 11–13%, with it they match to the unit.
                        if (IsFormWeAreIn(game, e.Skill))
                        {
                            SkillPart sp = new SkillPart(e.Modifier, 0,
                                                         Lang.T("attack.fromItemInForm", e.Source));
                            sp.FromForm = true;
                            sp.FormWide = true;
                            spread.Add(sp);
                        }
                        continue;
                    }
                    // ...AND A FOURTH PLACE FOR THE SAME RULE, FOUND ON `_Troll` ON
                    // 20 OCTOBER. The three cases above redirect a modifier whose parent
                    // cannot hit; this one is their opposite — THE PARENT DOES NOT HIT
                    // UNTIL THE ITEM MAKES IT HIT. There is nothing to redirect it to, so
                    // the skill itself has to be built.
                    //
                    // War Cry (`playerclass01/warcry1.dbr`, `Skill_AttackRadius`) carries no
                    // damage of its own at all — a taunt, a damage-reduction debuff and
                    // nothing else — so `Make` refused it and the skill was never on the
                    // list. The helmet's modifier `head_b014_warcry` gives it
                    // `weaponDamagePct 50`, and from that moment the game prints it among
                    // the skills that hit. We dropped the modifier (no owner to put it on)
                    // and showed no skill.
                    //
                    // MEASURED — `_Troll`, 20 October, six lines at once, each EXACTLY half
                    // of the stats panel (one two-hander, so the panel IS that one hand,
                    // and a radius hit strikes with one):
                    //     physical  12385 – 19770   panel  24769 – 39540
                    //     pierce      334 –   376   panel    668 –   751
                    //     lightning           437   panel           874
                    //     chaos        14 –    37   panel     28 –    74
                    //     trauma     1549 –  1856   panel   3099 –  3711
                    //     bleeding            993   panel          1987
                    owner = Promote(game, c, bonus, e);
                    if (owner == null) continue;
                    made.Add(owner);
                }
                owner.Parts.Add(new SkillPart(e.Modifier, 0, Lang.T("attack.fromItem", e.Source)));
            }

            // THE FORM IS ON EVERY BLOW (`#form`). The list is complete here — the class
            // skills, the ones the gear grants and all the modifiers — so this is where the
            // form's own modifiers are laid on the skills that are not the form's. The ones
            // it grants already have them from the branch above, and a record that is
            // already among a skill's parts is not added twice.
            //
            // MEASURED on `_555` (MECHANICS 169): in the werewolf form Guillotine, Leap and
            // Vampiric Shadows each gain exactly Form of the Beast's +165% total damage.
            foreach (SkillPart t in spread)
                foreach (AttackSkill a in made)
                {
                    bool already = false;
                    foreach (SkillPart p in a.Parts) if (p.Rec == t.Rec) { already = true; break; }
                    if (already) continue;
                    SkillPart mp = new SkillPart(t.Rec, t.Slot, t.Who);
                    mp.FormWide = true;
                    // An item's form modifier keeps its other mark as well: its FLAT damage
                    // belongs to the character's sum, not to the skill's pool (MECHANICS 22i).
                    mp.FromForm = t.FromForm;
                    a.Parts.Add(mp);
                }

            // A RADIUS HIT STRIKES WITH ONE HAND, not with the average of both.
            //
            // The same thing already measured on Explosive Strike's blast
            // (`SkillSecondary_AttackRadius`), only here the whole skill is built that way.
            // Measured on `_Jien`'s Ring of Steel — on physical, because nothing converts
            // into it and there is nothing to argue about:
            //
            //     physical per ordinary attack  480 – 902   (per HAND 320 – 601)
            //     physical of Ring of Steel    1537 – 2887
            //     1537 / 320 = 4.804    2887 / 601 = 4.804    ← constant
            //
            // and 4.80 is this skill's weapon share (370% of its own plus 110% from a set
            // modifier) taken from ONE hand. With 1.5 hands it would have been 2306.
            //
            // ALL parts of such a skill are marked: its own record, the modifiers and the
            // item modifiers — they are all parts of one radius hit.
            //
            // AND THE SAME FOR AN ARC SWING — measured on `_Amakata`.
            //
            // Reaping Arc (Uroboruuk's Reaping, `relics/reapingarc.dbr`) is class
            // `Skill_AttackWeapon`, so it missed the old "radius hit" mark and we counted it
            // as the average over hands. The game's panel says otherwise, on four lines at
            // once:
            //
            //     physical  ours 521 – 687      game 348 – 458     ×1.497
            //     pierce    ours 1608           game 1072          ×1.5006
            //     aether    ours 1235           game  823          ×1.5006
            //     life      ours 23566 – 26016  game 16072 – 17705 ×1.466
            //
            // The 1.5 is `2 × 0.75`: we split over both hands what hits with one. Life's
            // ratio is below 1.5 exactly because part of it is the skill's own damage
            // (100 flat), averaged by its own multiplier. Two lines of the same hit that do
            // NOT depend on the weapon (poison 305 and decay 12030) matched to the unit even
            // before.
            //
            // THE MARK: the record has an ATTACK ANGLE and MORE THAN ONE TARGET — one swing
            // cutting an arc, not a hit on a target. Reaping Arc has angle 210 and 6
            // targets; Execution and Reaping Strike have 1 target and no angle at all, and
            // stay the average over hands.
            //
            // THERE IS ONLY ONE MEASUREMENT, and that must be known. The mark also touches
            // three skills we have no game numbers for: Belgothian's Shears (angle 180,
            // 3 targets), Whirling Death (360, 8) and Necrotic Edge (180, 2–7). The last one
            // is on `_Amakata` and has been asked of the user, with both numbers named in
            // advance.
            //
            // HOW MANY HANDS AND WHICH — A MARK OF THE GAME.
            //
            // The mark lives in the skill's ANIMATION, not in its record: the record has
            // `skillSpecialAnimationName`, the player animation table gives an `.anm` file
            // for EVERY equipment set, and inside the file are the hit events
            // `LeftHandHit` / `RightHandHit`. SkillHands reads this — the whole chain is
            // described there; analysis and all measurements — MECHANICS 14r.
            //
            // The right hand is `weapon1[0]`, the "main weapon"; the left is `[1]`, the
            // "off-hand". Matches THIRTEEN measurements, including both mirrored stand pairs
            // and all three states of Blitz.
            //
            // THE ONLY CONTRADICTION TURNED OUT TO BE OUR OWN ERROR IN A STAND. Aspect of
            // Beronath was recorded as hitting with the FIRST hand, which argued with the
            // animation for half a day. The user's tooltip from that very character said
            // "170% off-hand weapon damage", while the stand said main hand: the numbers of
            // two experience states were filed under the wrong saves —
            // `stand-viliya-hands` held the second state's numbers and `-hands2` the first.
            // Fixed, and everything matches after that.
            //
            // WHAT HERE COMES FROM MEASUREMENT AND NOT FROM THE MARK. Classes without the
            // word `Weapon` in the name (`Skill_AttackRadius…`, `SkillSecondary_…`) do not
            // obey the mark: Ring of Steel's animation says BOTH and its tooltip prints two
            // weapon lines, yet the damage is measured as ONE hand — twice, on `_Jien`
            // (ratio 4.804 at both ends) and on `_Hamarina`. It also carries
            // `leftHandWeaponDisplay = 1`, which the game's own template calls "include left
            // hand weapon damage display in tooltip" — the field edits the TOOLTIP, not the
            // hit. The same holds for Explosive Strike (`SkillSecondary_AttackRadius`). For
            // these classes the old rule stands: one hand, and it is the first.
            //
            // A SKILL GRANTED BY A FORM HITS WITH BOTH HANDS, and the "one hand" mark does
            // not apply to it AT ALL.
            //
            // The reason is in the form's own record: `replacementAnims` replaces the whole
            // ANIMATION SET (`records/creatures/pc/anm_werewolf.dbr`). Our hand mark is read
            // from the ORDINARY set's animations, which can say nothing about a form: other
            // body, other swings.
            //
            // MEASURED — `saves/_555`, Rip and Tear (`werewolf1_skill02_charge`, weapon
            // share 331%). The ordinary set's "Rush" animation said main hand, and FOUR
            // types went wrong at once. The check is direct: the same Feral Claws numbers
            // (share 166%, both hands), corrected for the share, give exactly what the game
            // prints:
            //
            //   physical  3251 – 3935 × 3.31/1.66 = 6482 – 7847   game 6481 – 7846
            //   cold            321.46 × 3.31/1.66 = 641          game 641
            //   poison     511 – 685  × 3.31/1.66 = 1019 – 1366   game 1019 – 1366
            //   pierce    weapon 18753 – 23667 + own 491 × 1.5 × 27.5568
            //                          = 39048 – 43961            game 39048 – 43961
            //
            // The 1.5 on own damage is `2 × 0.75` of both hands (MECHANICS 14v); with one
            // hand it would be 1 and pierce would come out 32514 instead of 39048 — so the
            // measurement names both hands twice.
            HashSet<string> byForm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Skill s in c.Skills)
            {
                if (s.Level <= 0 || string.IsNullOrEmpty(s.Name)) continue;
                DbRecord fr = game.Record(s.Name);
                if (fr == null ||
                    !(fr.Class ?? "").Equals("Skill_Shapeshift", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string g in (fr.Get("grantedSkills") ?? "").Split(';'))
                    if (g.Trim().Length > 0) byForm.Add(g.Trim());
            }
            WeaponSet set = SkillHands.SetOf(game, c);
            foreach (AttackSkill a in made)
            {
                if (byForm.Contains(a.Dbr)) continue;
                DbRecord own = game.Record(a.Dbr);
                if (own == null) continue;
                string cls = own.Class ?? "";
                bool weaponClass = SkillHands.ByAnimation(cls);
                bool measuredOneHand = IsRadiusHit(cls) || IsSweep(own, SkillText.Slot(own, a.Level));

                Hands hands = weaponClass ? SkillHands.Of(game, own, set) : Hands.Both;
                bool oneHand, second;
                if (weaponClass)
                {
                    oneHand = hands != Hands.Both;
                    second = hands == Hands.Off;
                }
                else
                {
                    // Not a weapon class: the animation here is about the picture, not
                    // the hit — stay with the measurement.
                    oneHand = measuredOneHand;
                    second = false;
                }
                if (!oneHand) continue;
                // `BestHand` is NOT set here, and that is measurement, not a default:
                // Ring of Steel's hand is named, and it is the first. `_Hamarina` decides
                // it — bleeding 42959 counts from the FIRST hand's dot (171), while the
                // best hand there is 177 and would give 43369. Checked by trying it: with
                // the best hand `_Jien`'s Ring of Steel cold drifts from +0.7% to +1.0%,
                // also the worse way.
                foreach (SkillPart p in a.Parts)
                { p.WeaponOneHand = true; p.OwnOnce = true; p.SecondHand = second; }
            }

            // The modifiers are in now, and a modifier carries weaponDamagePct of
            // its own — a transmuter can even take it away (Frenetic Throw,
            // `phantomblade_mod0.dbr`, has −55). So the share of the weapon is the sum over everything
            // that makes up the skill, not the number on its first record.
            foreach (AttackSkill a in made) a.WeaponPct = a.Sum("weaponDamagePct");

            // CHANNELLING — ONE PASS OVER THE FINISHED LIST, not inside the mastery loop:
            // channelled skills also come from ITEMS (Oblivion, a component's acid spray),
            // and the mark must find them all.
            foreach (AttackSkill a in made)
            {
                DbRecord rec = game.Record(a.Dbr);
                a.ChannelMs = ChannelMsOf(rec);
            }

            // THE EXPERIMENT `--dropskill=<path fragment>`: pretend no point is spent on
            // this part of the skill. Added to repeat what the user did in the game — they
            // refunded the Dreeg's Reproach transmuter and re-took the tooltip. The save had
            // not reached us yet while the numbers had to be checked at once, and that is
            // legitimate exactly because the transmuter's record carries NOTHING but two
            // conversions (checked with `--dbrdump`: `conversionInType Physical/Fire →
            // Poison`, 100%). For records that carry anything else the experiment is wrong,
            // and that is said here on purpose.
            if (!string.IsNullOrEmpty(DropPart))
                foreach (AttackSkill a in made)
                    a.Parts.RemoveAll(delegate (SkillPart p)
                    {
                        return p.Rec != null && !string.IsNullOrEmpty(p.Rec.Path) &&
                               p.Rec.Path.IndexOf(DropPart, StringComparison.OrdinalIgnoreCase) >= 0;
                    });

            made.Sort(delegate (AttackSkill x, AttackSkill y)
            {
                if (x.FromItem != y.FromItem) return x.FromItem ? 1 : -1;
                int bySource = string.Compare(x.Source, y.Source, StringComparison.CurrentCulture);
                return bySource != 0 ? bySource
                     : string.Compare(x.Name, y.Name, StringComparison.CurrentCulture);
            });
            return made;
        }

        // What a mastery-tree record is to its skill.
        //
        // Not decoration but a split the numbers depend on: `Skill_Modifier` and
        // `Skill_ProjectileModifier` are PARTS OF THE SAME HIT, while
        // `SkillSecondary_BuffSelfDuration` is a buff hung on you AFTER the hit and held
        // for some seconds.
        //
        // We did not read `Skill_ProjectileModifier` at all, and that cost a whole
        // modifier: Brimstone (`flamestrike4`) is part of Fire Strike's shot, and the game
        // shows it itself — the skill tooltip says "4 - 5 fragments", which is
        // `projectileFragmentsLaunchNumber` from the fragment record (level 13, row 12:
        // 4 and 5).
        //
        // `SkillSecondary_AttackRadius` is part of the hit too, not a buff: it is the blast
        // at the impact point. Checked the same way as everything else — by number: `_Iman`'s
        // Fire Strike burn is 678 without it against the game's 3035, and exactly 3035 with
        // it. Buffs (`SkillSecondary_BuffSelfDuration`, `_Relo`'s Deadly Momentum) carry
        // `skillActiveDuration` in the record; the blast does not.
        public enum TreeRole
        {
            Skill,        // the skill itself: it is picked from the list
            Part,         // part of its own skill's hit
            Buff,         // temporary effect: counted as a separate line
            Transmuter,   // part of the hit, but attached its own way
            Aside,        // not about the hit: mastery, passive, pet
        }

        // "Secondary" records that HAVE a measurement, and what it showed.
        static readonly string[] SecondaryIsPart = {
            // The blast at the impact point. `_Iman`, Fire Strike: burn 678 without it
            // against the game's 3035, exactly 3035 with it.
            "SkillSecondary_AttackRadius",
            // A RING OF PROJECTILES FROM THE IMPACT POINT — measured on `_555`, Fault Line
            // (`leap2`, lvl 18) under Leap (lvl 19). Before that the class sat in
            // `SecondaryUnmeasured` ("no measurement, treat as temporary") and its damage
            // did not enter the panel.
            //
            // The measurement is direct — two types with exactly two addends:
            //
            //   bleeding  weapon 238 × 24.3328 × 2.89 = 16737
            //             own (284 Leap + 277 Fault Line) × 24.3328
            //                                         = 13651
            //             total                         30388   game 30387
            //             without Fault Line it would be 23647
            //   pierce    weapon 208.12 × 24.6043 × 2.89 = 14802
            //             own (201 + 207 + 110 pierced physical)
            //                   × 24.6043               = 12744
            //             total                           27546  game 27543
            //             top 271.93 → 19339 + 12744 =    32083  game 32080
            //
            // Both ends of pierce give THE SAME own part (12744 and 12741), so the addend
            // really is flat and not a weapon share.
            "SkillSecondary_AttackProjectileRing",
            // A BUFF HUNG ON THE BLOW, AND ITS NUMBERS ARE PART OF THE BLOW. Measured on
            // `_RuneTask`, Judgment and its part Wrathful Heart (`judgment3`, lvl 4), from the
            // user's screenshot of the skill's panel — the very screenshot the comment below
            // said was missing before this class could be decided.
            //
            // The record gives THREE separate quantities at that level, and all three land:
            //
            //   own fire   51 – 64        gap 1356 / 1702 → multiplier 26.6 on both ends
            //   own burn   50             gap 1147        → multiplier 22.9, the same the
            //                                               ordinary attack's burn uses
            //   retaliationDamagePct 10   acid    0.10 × 8328      = 832.8   game 833
            //                             lightning 0.10 × 10–5867 = 1 – 587 game 1 – 587
            //                             physical 369 + 0.10 × 5324 = 901.4 game 902
            //
            // Three quantities, six lines, no free number anywhere: the share is fixed by the
            // record and the multipliers are the character's own. Without the record Judgment
            // came out at physical 369 against 902 and fire 1195 against 20473, and its acid
            // and lightning rows did not exist at all.
            //
            // `skillActiveDuration 3` does NOT make it temporary for the panel: the game prints
            // these numbers in the skill's own panel, read standing still.
            "SkillSecondary_BuffAttackRadiusDuration",
        };
        static readonly string[] SecondaryIsBuff = {
            // A buff for some seconds: the record carries `skillActiveDuration`.
            // `_Relo`, Deadly Momentum — 5 seconds.
            "SkillSecondary_BuffSelfDuration",
        };
        // "Secondary" records measured to be COUNTED SEPARATELY, outside the parent skill's
        // panel.
        static readonly string[] SecondaryIsSeparate = {
            // A SEPARATE PROJECTILE. `_222`, Vire's Might and its part Volcanic Stride
            // (`viremight2`): the stride has its own burn and the game rates it itself —
            // the tooltip says 6984 burn over 3 sec., i.e. 2328 per second, while the
            // record gives 137 at its level: 137 × 16.99 = 2328 EXACTLY. So the same
            // multiplier applies to it.
            //
            // AND THAT BURN DOES NOT ENTER VIRE'S MIGHT'S PANEL: the game prints 8332,
            // exactly our number WITHOUT the stride; with it we would get 10354. All five
            // lines of Vire's Might match to the unit.
            //
            // The user pointed the way: it is a projectile, so it may well count as
            // unrelated to the skill. That is how it turned out.
            "SkillSecondary_AttackProjectileAreaEffect",
        };
        // NO MEASUREMENT. The class occurs in the trees, but on none of our saves does it
        // enter a checked hit, so we have never seen it as a number. Left on the old
        // behaviour — a separate "with temporary effects" line, outside the panel check —
        // which is the SAFE side: nothing extra gets into the hit. **NOT ENTERED and why:**
        // deciding whether it is part of the hit needs a character with such a skill and a
        // screenshot of their panel.
        //
        // THERE IS ALSO A TELLTALE FIELD, found by a census over the whole database:
        // `specialSecondaryActivation 1` sits on 24 records and on NONE of the twelve
        // `SkillSecondary_AttackRadius` — the only class measured as PART of the hit. So the
        // field is a sufficient mark of "fires separately", but not a necessary one:
        // `AttackProjectileDrop`, `Orbiting`, `ChainLightning` and others lack it while,
        // judging by their names, they should be separate too. That is why it is NOT made a
        // mark in code — the list stays by name.
        static readonly string[] SecondaryUnmeasured = {
            "SkillSecondary_AttackProjectileDrop",
            "SkillSecondary_AttackProjectileOrbiting",
            // `SkillSecondary_BuffAttackRadiusDuration` MOVED OUT OF THIS LIST 13 October —
            // it is now measured, see `SecondaryIsPart` above.
            "SkillSecondary_ChainLightning",
            "SkillSecondary_ForkLightning", "SkillSecondary_OnHitBuffRadius",
            "SkillSecondary_OnKillAttackRadius", "SkillSecondary_PetModifier",
            "SkillSecondary_Tether",
        };

        static bool In(string[] list, string cls)
        {
            foreach (string k in list)
                if (string.Equals(k, cls, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static TreeRole RoleOf(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return TreeRole.Aside;
            if (cls.Equals("Skill_Modifier", StringComparison.OrdinalIgnoreCase) ||
                cls.Equals("Skill_ProjectileModifier", StringComparison.OrdinalIgnoreCase))
                return TreeRole.Part;
            // A TRANSMUTER OF A THROWN SKILL HAS ITS OWN CLASS NAME, and the pair is exactly
            // the same as for a modifier: `Skill_Modifier` / `Skill_ProjectileModifier` are
            // both parts, so `Skill_Transmuter` / `Skill_ProjectileTransmuter` are both
            // transmuters. The second name was missing here, and the record fell through to
            // the last line — that is, it was taken for a SKILL you press.
            //
            // IN THE WHOLE GAME THERE IS EXACTLY ONE SUCH RECORD: `playerclass02/grenado1b`,
            // Alchemical Grenado, "100% conversion: physical damage to lightning damage"
            // (census over all `records/skills/`). So this costs nothing anywhere else and
            // could only ever be found on a character who has it — `_Bombaster`, 21 October.
            //
            // WHAT IT COST WHILE IT WAS MISSING: Grenado's own physical (2475 of base at
            // level 26) stayed physical and took the physical multiplier instead of the
            // lightning one, and the game's panel shows no physical line for that skill at
            // all. Lightning came out 37996 against the game's 173189 — short by 78%.
            if (cls.Equals("Skill_Transmuter", StringComparison.OrdinalIgnoreCase) ||
                cls.Equals("Skill_ProjectileTransmuter", StringComparison.OrdinalIgnoreCase))
                return TreeRole.Transmuter;
            if (cls.StartsWith("SkillSecondary", StringComparison.OrdinalIgnoreCase))
                return In(SecondaryIsPart, cls) ? TreeRole.Part : TreeRole.Buff;
            return Choosable(cls) ? TreeRole.Skill : TreeRole.Aside;
        }

        // The role's name for the `--skillcoverage` census, or null for a class we have
        // decided nothing about. The list is by name on purpose: if a patch adds a class,
        // the census must go red instead of falling back to a default.
        public static string RoleName(string cls)
        {
            // Records with no class at all are `*_petbonus`: the bonus set a skill passes
            // to its pets, unrelated to the hit.
            if (string.IsNullOrEmpty(cls)) return "excluded from the hit (record has no class)";
            if (cls.StartsWith("SkillSecondary", StringComparison.OrdinalIgnoreCase))
            {
                if (In(SecondaryIsPart, cls)) return "part of the hit";
                if (In(SecondaryIsBuff, cls)) return "temporary effect";
                if (In(SecondaryIsSeparate, cls)) return "calculated separately (measured)";
                if (In(SecondaryUnmeasured, cls)) return "treated as temporary pending measurement";
                return null;
            }
            switch (RoleOf(cls))
            {
                case TreeRole.Skill: return "listed skill";
                case TreeRole.Part: return "part of the hit";
                case TreeRole.Buff: return "temporary effect";
                case TreeRole.Transmuter: return "transmuter (listed only)";
                default: return "excluded from the hit";
            }
        }

        // A skill that hits in a circle around itself. Measured ONLY on
        // `Skill_AttackRadius` (Ring of Steel) and on `SkillSecondary_AttackRadius`
        // (Explosive Strike); the related classes — `…RadiusSpin`, `…RadiusLeap`,
        // `…RadiusGrow`, `…RadiusLightning` — are deliberately NOT included: there is no
        // measurement for them, and guessing by class name has burnt us before.
        //
        // THE SECOND CLASS, `Skill_AttackWeaponRadius`, was added by measurement too, not
        // by name kinship.
        //
        // Amarasta's Blade Burst on `_Itaan` (weapon share 400%: 320 its own, 40 from the
        // set, 20 each from the mace and the sword). Its pierce is purely from the weapon
        // and nothing converts into it:
        //
        //     ours 4100      game 2743      4100 / 2743 = 1.4947
        //
        // The 1.5 is exactly `2 × 0.75` — we took the average over both hands where the
        // game takes one. The tooltip says the same in words: Righteous Fervor prints TWO
        // weapon-damage lines (main and off-hand), Blade Burst prints ONE. **A handy mark:
        // one line in the tooltip = a one-hand hit.**
        //
        // The flag also fixes the skill's own damage: it stops being multiplied by
        // `ownShare = 1.5` and goes into one hand's pool. Poison then matches within 0.8%
        // and bleeding within 1.6% — against +51% and +52% before.
        //
        // THE THIRD CLASS, `Skill_AttackRadiusLeap`, was added again BY MEASUREMENT, not by
        // name kinship (it is named above as one we deliberately left out while no
        // measurement existed). Measured on `_555`'s Leap (lvl 19, weapon share 289%), and
        // decided by TWO types where the skill has no own damage at all, so only the hand
        // can be in question:
        //
        //   cold    14 × 8.2335 × 2.89 = 333           game 333
        //           with both hands it would be 0.75 × 28 → 500
        //   poison  21.88 × 7.2635 × 2.89 + own 160 = 619   game 619
        //           top 33.07 × 7.2635 × 2.89 + 160  = 854   game 854
        //           with both hands it would be 1034 – 1305
        //
        // The tooltip's TWO weapon lines do not argue here but confirm: `leap1` carries
        // `leftHandWeaponDisplay 1` — the very field that edits the TOOLTIP, not the hit
        // (exactly as on Ring of Steel, above).
        static bool IsRadiusHit(string cls)
        {
            return cls.Equals("Skill_AttackRadius", StringComparison.OrdinalIgnoreCase) ||
                   cls.Equals("Skill_AttackWeaponRadius", StringComparison.OrdinalIgnoreCase) ||
                   cls.Equals("Skill_AttackRadiusLeap", StringComparison.OrdinalIgnoreCase);
        }

        // AN ARC SWING: one sweep cutting several targets. It hits with ONE hand — the same
        // rule as a radius hit (see the call site). An angle without a second target does
        // not count: Execution has one target.
        //
        // WEAPON-POOL SKILLS ARE EXCLUDED — the user's objection, and it was right. Such a
        // skill IS your ordinary attack: it fires instead of it by chance (Necrotic Edge
        // 27%, Reaping Strike 60%), and the ordinary attack alternates hands with dual
        // weapons, so the cycle averages it. Our measurement is only on a BUTTON skill
        // (Reaping Arc), and carrying it over to the pool would be a guess.
        //
        // The game also prints the difference: Reaping Arc's tooltip has ONE weapon line
        // ("210% main-hand weapon damage"), Necrotic Edge and Reaping Strike have TWO
        // ("157% main" and "157% off-hand"). It cannot serve as the mark — Ring of Steel
        // also prints two lines while hitting with one hand (measured twice) — but it is a
        // fair argument against extending the rule to the pool.
        static bool IsSweep(DbRecord rec, int slot)
        {
            if ((rec.Class ?? "").IndexOf("WPAttack", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return AttackSkill.Value(rec, "skillTargetAngle", slot) > 0.0001 &&
                   AttackSkill.Value(rec, "skillTargetNumber", slot) > 1.0001;
        }

        static AttackSkill Find(List<AttackSkill> made, string dbr)
        {
            if (string.IsNullOrEmpty(dbr)) return null;
            foreach (AttackSkill a in made)
                if (string.Equals(a.Dbr, dbr, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        // The level an item grants its skill at. Usually a plain number; a few
        // items scale it with the item's own level, and the game ships that as an
        // equation the same evaluator reads everywhere else.
        public static int GrantedLevel(DbRecord item) { return GrantedLevel(item, null); }

        // `holder` is the item's BASE record, when the skill comes from an affix.
        //
        // A SKILL'S LEVEL CAN BE AN EQUATION OVER THE ITEM'S LEVEL, AND `itemLevel` SITS ON
        // THE ITEM, NOT ON THE AFFIX. Found on `_Acid Vines`: Poison Bolt comes from the
        // suffix `lootaffixes/suffix/b_sh040_d.dbr`, which carries
        //
        //     itemSkillLevelEq   itemLevel/4+1
        //
        // We did read the equation, but asked the SUFFIX for `itemLevel`, which it does not
        // have at all. That gave zero, 0/4+1 = 1, and the skill was taken at level one:
        // 444 damage against the game's 6158 — the first row of a sixty-row array instead
        // of the twenty-fourth.
        public static int GrantedLevel(DbRecord item, DbRecord holder)
        {
            int flat = item.GetInt("itemSkillLevel");
            if (flat > 0) return flat;
            string eq = item.Get("itemSkillLevelEq");
            double v;
            if (!string.IsNullOrEmpty(eq) &&
                Equation.TryEval(eq, delegate (string name)
                {
                    if (!name.Equals("itemLevel", StringComparison.OrdinalIgnoreCase)) return 0;
                    int own = item.GetInt("itemLevel");
                    return own > 0 || holder == null ? own : holder.GetInt("itemLevel");
                }, out v) && v >= 1)
                return (int)Math.Floor(v);
            return 1;
        }

        // The record a skill's numbers actually live on. A skill named by an item
        // or by the tree is often only a shell; the numbers are on the buff it
        // hands off to, the same chain the name and the description follow.
        //
        // The skill keeps the name of the record it was **named by**, not of the
        // one the numbers were found on. That is its identity everywhere else: the
        // tree lays a modifier out beside the button, so Blood Boil points at
        // soulsiphon1.dbr, while Siphon Souls' numbers live on soulsiphon1_buff.dbr.
        // Registering the skill under the buff lost every modifier of every skill whose
        // button is a shell — and that is exactly what dropped Siphon Souls' bleeding and
        // decay.

        // ONE skill by its record, exposed. The timeline needs it for devotion stars: they
        // are tied to skills by the save (`AutoCastSkill`), never enter the common `Of`
        // list, and their damage is counted the same way.
        public static AttackSkill One(GameData game, string dbr, int level, string source)
        { return Make(game, dbr, level, source, true); }

        static AttackSkill Make(GameData game, string named, int level, string source, bool fromItem)
        {
            string dbr = named;
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(dbr); hop++)
            {
                DbRecord rec = game.Record(dbr);
                if (rec == null) return null;
                int slot = SkillText.Slot(rec, level);
                if (HasDamage(rec, slot))
                {
                    AttackSkill a = new AttackSkill();
                    a.Dbr = named;
                    a.Name = SkillText.Name(game, named) ?? named;
                    a.Source = source;
                    a.Level = level;
                    a.FromItem = fromItem;
                    a.WeaponPct = AttackSkill.Value(rec, "weaponDamagePct", slot);
                    a.Parts.Add(new SkillPart(rec, slot, Lang.T("attack.part", a.Name, level)));
                    return a;
                }
                dbr = ItemViewFactory.NextSkillRecord(rec);
            }
            return null;
        }

        // A SKILL THE CHARACTER HAS WHOSE OWN RECORD CARRIES NO DAMAGE, built as a shell so
        // that an item modifier which BRINGS the damage has something to land on. The whole
        // reason it exists is in the comment at the call (War Cry on `_Troll`).
        //
        // Narrow on purpose, three conditions, and each one is there to keep something out:
        //   * the character must really have the skill — the modifier is worn either way,
        //     and an item that edits a skill nobody has adds nothing to the lists;
        //   * the skill's class must be one the player hits with (`Choosable`) — that is
        //     what keeps toggles, passives and summons out, as in the mastery loop;
        //   * the modifier must carry damage of its own — a modifier that only shortens a
        //     cooldown or widens a radius still does not make the skill hit.
        // Its own record goes in as a part all the same: it holds the skill's percentages,
        // and the weapon share is summed over all the parts at the end of `Of`.
        static AttackSkill Promote(GameData game, Character c, SkillText.LevelBonus bonus,
                                   ItemSkillMods.Entry e)
        {
            DbRecord rec = game.Record(e.Skill);
            if (rec == null || !Choosable(rec.Class)) return null;
            if (!HasDamage(e.Modifier, 0)) return null;
            foreach (Skill s in c.Skills)
            {
                if (s.Level <= 0 || string.IsNullOrEmpty(s.Name)) continue;
                if (!string.Equals(s.Name, e.Skill, StringComparison.OrdinalIgnoreCase)) continue;
                int level = s.Level + bonus.For(s.Name, s.Level);
                int slot = SkillText.Slot(rec, level);
                string folder = ItemViewFactory.MasteryFolder(s.Name);
                AttackSkill a = new AttackSkill();
                a.Dbr = e.Skill;
                a.Name = SkillText.Name(game, e.Skill) ?? e.Skill;
                a.Source = folder == null ? e.Source
                                          : (ItemViewFactory.MasteryName(game, folder) ?? folder);
                a.Level = level;
                a.WeaponPct = AttackSkill.Value(rec, "weaponDamagePct", slot);
                a.When = Trigger(rec, slot);
                a.HitChance = rec.GetInt("onHitActivationChance");
                a.Parts.Add(new SkillPart(rec, slot, Lang.T("attack.part", a.Name, level)));
                return a;
            }
            return null;
        }

        // Not every skill on the list is a button. A weapon pool skill fires in
        // place of a plain attack with a chance of its own — and only one of them
        // can fire per swing, so the chances share a single hundred per cent. A
        // proc fires on a trigger. Both numbers are in the record.
        static string Trigger(DbRecord rec, int slot)
        {
            // Only Skill_WPAttack_* is the weapon pool proper — the same class the
            // skill tree uses to draw it, checked against the game's own tooltip
            // (22% for Execution on screen6.jpg). Skill_WeaponPool_* is a different
            // thing despite the name: Cadence, Savagery and Fire Strike are put on the
            // mouse button by hand, they do not fire by chance.
            string cls = rec.Class ?? "";
            if (cls.StartsWith("Skill_WPAttack", StringComparison.OrdinalIgnoreCase))
            {
                double chance = AttackSkill.Value(rec, "skillChanceWeight", slot);
                return chance > 0
                     ? Lang.T("attack.pool", chance.ToString("0.#", CultureInfo.InvariantCulture))
                     : Lang.T("attack.poolNoChance");
            }
            int onHit = rec.GetInt("onHitActivationChance");
            if (onHit > 0) return Lang.T("attack.onHit", onHit);
            return "";
        }

        // The record of a piece of a skill, found the same way as the skill's own:
        // down the hand-off chain to the first one that carries damage. A
        // secondary effect is almost always a shell — cadence3.dbr holds nothing
        // but buffSkillName, and the numbers are on cadence3_buff.dbr.
        static SkillPart PartOf(GameData game, string dbr, int level, string who)
        {
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(dbr); hop++)
            {
                DbRecord rec = game.Record(dbr);
                if (rec == null) return null;
                int slot = SkillText.Slot(rec, level);
                if (HasDamage(rec, slot) || HasPercent(rec, slot))
                    return new SkillPart(rec, slot, who);
                dbr = ItemViewFactory.NextSkillRecord(rec);
            }
            return null;
        }

        // Does the record carry a percentage that belongs to the attack. A piece
        // of a skill can be all percentages and no flat damage at all.
        static bool HasPercent(DbRecord rec, int slot)
        {
            if (rec == null) return false;
            if (Math.Abs(AttackSkill.Value(rec, "offensiveTotalDamageModifier", slot)) > 0.0001) return true;
            foreach (string t in Types)
                if (Math.Abs(AttackSkill.Value(rec, "offensive" + t + "Modifier", slot)) > 0.0001 ||
                    Math.Abs(AttackSkill.Value(rec, "offensiveSlow" + t + "Modifier", slot)) > 0.0001)
                    return true;
            return false;
        }

        // A skill belongs on the damage tab if it hits: it either multiplies the
        // weapon or carries damage of its own.
        public static bool HasDamage(DbRecord rec, int slot)
        {
            if (rec == null) return false;
            if (Math.Abs(AttackSkill.Value(rec, "weaponDamagePct", slot)) > 0.0001) return true;
            foreach (string t in Types)
                if (Math.Abs(AttackSkill.Value(rec, "offensive" + t + "Min", slot)) > 0.0001 ||
                    Math.Abs(AttackSkill.Value(rec, "offensiveSlow" + t + "Min", slot)) > 0.0001)
                    return true;
            return false;
        }

        // Records the player never picks from a list: the mastery bar, passives,
        // the pieces that hang off another skill, and anything belonging to a pet.
        //
        // Buffs are left out for a second reason as well: a stance or an aura adds
        // its flat damage to whatever you swing, and CharacterTotals already has
        // it — Oleron's Rage is switched on in `_Relo`'s save and its numbers are
        // in the character's sum. Offering it as a thing to hit with would count
        // it twice. The exception is the class that says "Attack" in its own name:
        // Devastation is Skill_BuffAttackRadiusDrop and it really does hit.
        static bool Choosable(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            if (cls.IndexOf("Mastery", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (cls.StartsWith("Skill_Passive", StringComparison.OrdinalIgnoreCase)) return false;
            // A BUFF CLASS ENTERS THE LIST ONLY IF IT HITS AN AREA AND IS NOT A TOGGLE.
            // The exception used to be the word `Attack` in the class name alone
            // (Devastation, `Skill_BuffAttackRadiusDrop`); now there are two words,
            // `Attack` and `Radius`, and the second was added by measurement.
            //
            // MEASURED on `_222`: a relic grants Menhir's Bastion (`Skill_BuffRadius`, the
            // damage sitting on the buff `relic_menhirbastion_buff`, `34–56 physical` of
            // its own). The game prints it while we said "skill not found". After the fix
            // all four numbers match to the last digit:
            //     physical  34 × 0.216 × 14.62  = 107.4 → 108   game 108
            //               56 × 0.216 × 14.62  = 176.8 → 177   game 177
            //     fire      34 × 0.784 × 16.877 = 449.9 → 450   game 450
            //               56 × 0.784 × 16.877 = 741.0 → 741   game 741
            // (0.216 is what survives the character's physical → fire conversion of 78.4%;
            //  each share then takes the multiplier of ITS OWN type.)
            //
            // A TOGGLE MUST NOT BE LET IN — that is the real reason for the refusal: a
            // stance or an aura lays its flat damage on any swing, and `CharacterTotals`
            // has already summed it (it sums exactly the toggles).
            //
            // `SelfDuration` must not be let in either: a self-buff such as Bloodthirster
            // folds into the PARENT skill as temporary (`SkillPart.Temporary`), and a
            // separate line would double it. The first version of this fix let in every
            // non-toggle, and `--skilldmgtest` caught it at once: seven buffs in the lists
            // of four characters.
            if (cls.StartsWith("SkillBuff", StringComparison.OrdinalIgnoreCase) ||
                (cls.StartsWith("Skill_Buff", StringComparison.OrdinalIgnoreCase) &&
                 cls.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) < 0))
                return cls.IndexOf("Radius", StringComparison.OrdinalIgnoreCase) >= 0 &&
                       !CharacterTotals.IsToggleClass(cls);
            if (cls.StartsWith("SkillSecondary", StringComparison.OrdinalIgnoreCase)) return false;
            if (cls.StartsWith("Pet", StringComparison.OrdinalIgnoreCase)) return false;
            if (cls.StartsWith("SkillTree", StringComparison.OrdinalIgnoreCase)) return false;
            if (cls.StartsWith("Skill_Shapeshift", StringComparison.OrdinalIgnoreCase)) return false;
            if (cls.Equals("Skill_Modifier", StringComparison.OrdinalIgnoreCase)) return false;
            if (cls.Equals("Skill_Transmuter", StringComparison.OrdinalIgnoreCase)) return false;
            // ...and the thrown skill's own name for the same thing — see RoleOf, where the
            // whole story is. Without this line Alchemical Grenado stayed in the "what we
            // hit with" list as a skill you press, with no damage of its own.
            if (cls.Equals("Skill_ProjectileTransmuter", StringComparison.OrdinalIgnoreCase)) return false;
            if (cls.IndexOf("ProjectileModifier", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (cls.IndexOf("SpawnPet", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        // Which skill the pieces in the tree hang off.
        //
        // Nothing in a record says so — the layout does. A modifier sits in the
        // same row as its skill, to the right of it (fieldcommand1 at x=566,
        // fieldcommand2 at x=726, both at y=389), and that rule is the one
        // CharacterTotals.ModifierParents already uses and that was checked
        // against the game (MECHANICS 14b).
        //
        // A transmuter does NOT follow it: it sits in a row of its own, offset by
        // about half a step (cadence1 at 246;319, its transmuter at 326;281). Two
        // different skills can be equally close, so the parent is genuinely
        // ambiguous — Blade Arc and the weapon-pool skill above it both sit
        // one step to the left of the transmuter of the first. So a transmuter is
        // only ever *named*, never folded into a number.
        public class Attach
        {
            readonly Dictionary<string, string> parent =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, string> extra =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string Parent(string dbr) { string p; return parent.TryGetValue(dbr, out p) ? p : null; }

            // Every skill this modifier touches. The game names them itself through
            // `skillDependancy`; the tree layout stays the fallback for records without
            // the field. Details at the call site.
            public List<string> Parents(GameData game, string dbr)
            {
                List<string> made = new List<string>();
                DbRecord rec = game == null ? null : game.Record(dbr);
                string named = rec == null ? null : rec.Get("skillDependancy");
                foreach (string one in (named ?? "").Split(';'))
                {
                    string trimmed = one.Trim();
                    if (trimmed.Length > 0 && !made.Contains(trimmed)) made.Add(trimmed);
                }
                if (made.Count > 0) return made;
                string byTree = Parent(dbr);
                if (byTree != null) made.Add(byTree);
                return made;
            }
            public string Extra(string dbr) { string p; return extra.TryGetValue(dbr, out p) ? p : null; }

            // 0 — a skill of its own; 1 — a modifier (same row, to the left);
            // 2 — a "secondary" (same row); 3 — a TRANSMUTER: it sits diagonally and is
            // resolved its own way, by the parent's `connectortransmuterstub`.
            static int KindOf(string cls)
            {
                if (cls.Equals("Skill_Modifier", StringComparison.OrdinalIgnoreCase) ||
                    cls.Equals("Skill_ProjectileModifier", StringComparison.OrdinalIgnoreCase)) return 1;
                if (cls.IndexOf("Transmuter", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
                if (cls.StartsWith("SkillSecondary", StringComparison.OrdinalIgnoreCase)) return 2;
                return 0;
            }

            // One whole mastery — for the `--modtest` census, which walks every tree in the
            // game, not only the ones the character took.
            public static Attach OfFolder(GameData game, string folder)
            {
                Attach m = new Attach();
                Fill(m, game, folder);
                return m;
            }

            // Which skills stand in this mastery's tree at all.
            public static List<string> SkillsOfFolder(GameData game, string folder)
            {
                List<string> made = new List<string>();
                string number = folder != null && folder.Length >= 12 ? folder.Substring(11) : "";
                for (int i = 1; i <= 99; i++)
                {
                    DbRecord ui = game.Record("records/ui/skills/class" + number +
                                              "/skill" + i.ToString("00") + ".dbr");
                    if (ui == null) continue;
                    string dbr = ui.Get("skillName");
                    if (!string.IsNullOrEmpty(dbr) && !made.Contains(dbr)) made.Add(dbr);
                }
                return made;
            }

            public static Attach Of(GameData game, Character c)
            {
                Attach m = new Attach();
                List<string> folders = new List<string>();
                foreach (Skill s in c.Skills)
                {
                    string f = ItemViewFactory.MasteryFolder(s.Name);
                    if (f != null && !folders.Contains(f)) folders.Add(f);
                }
                foreach (string folder in folders) Fill(m, game, folder);
                return m;
            }

            static void Fill(Attach m, GameData game, string folder)
            {
                {
                    string number = folder.Length >= 12 ? folder.Substring(11) : "";
                    List<string> dbrs = new List<string>();
                    List<int> xs = new List<int>(), ys = new List<int>(), kinds = new List<int>();
                    // Whether this button carries the "a transmuter is tied to me" mark.
                    List<bool> stub = new List<bool>();
                    for (int i = 1; i <= 99; i++)
                    {
                        DbRecord ui = game.Record("records/ui/skills/class" + number +
                                                  "/skill" + i.ToString("00") + ".dbr");
                        if (ui == null) continue;
                        string dbr = ui.Get("skillName");
                        DbRecord skill = string.IsNullOrEmpty(dbr) ? null : game.Record(dbr);
                        if (skill == null) continue;
                        dbrs.Add(dbr);
                        xs.Add(ui.GetInt("bitmapPositionX"));
                        ys.Add(ui.GetInt("bitmapPositionY"));
                        kinds.Add(KindOf(skill.Class ?? ""));
                        stub.Add((skill.Get("skillConnectionOn") ?? "")
                                 .IndexOf("transmuter", StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                    for (int i = 0; i < dbrs.Count; i++)
                    {
                        if (kinds[i] == 0) continue;
                        if (kinds[i] == 3)
                        {
                            // A TRANSMUTER. Neither it nor the skill has an explicit parent
                            // field; the game keeps the link in the layout alone. But there
                            // IS a mark in the records, and it sits on the PARENT: a skill
                            // with a transmuter lists `connectortransmuteronstub` in
                            // `skillConnectionOn` — "a transmuter is tied to me".
                            //
                            // So the search is: the nearest button carrying that mark.
                            // Distance is not the rule here but the way to choose among the
                            // marked ones; the mark is the rule. If the marked neighbour
                            // turns out to be a modifier, its skill becomes the owner.
                            int near = -1; double bestD = 0;
                            for (int j = 0; j < dbrs.Count; j++)
                            {
                                if (j == i || !stub[j]) continue;
                                double dx = xs[j] - xs[i], dy = ys[j] - ys[i];
                                double d = dx * dx + dy * dy;
                                if (near < 0 || d < bestD) { near = j; bestD = d; }
                            }
                            if (near < 0) continue;
                            string owner = dbrs[near];
                            if (kinds[near] == 1 && m.parent.ContainsKey(owner)) owner = m.parent[owner];
                            m.extra[dbrs[i]] = owner;
                            continue;
                        }
                        int best = -1;
                        for (int j = 0; j < dbrs.Count; j++)
                            if (j != i && kinds[j] == 0 && ys[j] == ys[i] && xs[j] < xs[i] &&
                                (best < 0 || xs[j] > xs[best])) best = j;
                        if (best < 0) continue;
                        if (kinds[i] == 1) m.parent[dbrs[i]] = dbrs[best];
                        else m.extra[dbrs[i]] = dbrs[best];
                    }
                }
            }
        }
    }
}
