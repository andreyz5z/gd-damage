// The damage of one hit, worked out the way the game works it out.
//
// Every step here was checked against the game on a bench character and is
// written up in MECHANICS 14v, steps 1–6. Nothing is guessed; where a piece is
// still unverified the model says so out loud instead of inventing it.
//
//     base        = weapon damage + items' flat additions
//     conversions take shares of the base; the shares add up
//     armour piercing is the last conversion, over what is left
//     flat elemental splits into three equal shares
//     each part × (1 + attribute/divider + % type + % elemental + % total)
//     a dot is divided by the base duration from the record
//     "ordinary attack" = the sum of all types, rounded to nearest
//
// With a skill chosen there are two pools instead of one, and the only thing
// that tells them apart is weaponDamagePct (MECHANICS 14v, step 4):
//
//     weapon damage    × (1 + …) × weaponDamagePct
//     the skill's own damage × (1 + …)   — the multiplier does NOT apply to it
//
// The percentages of the skill's own tree modifiers go into the multiplier of
// both pools: they belong to the attack, which is why the character's summed
// bonuses deliberately leave them out (MECHANICS 14b).

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Gdc.Gui
{
    public class DamageLine
    {
        public string Type = "";     // "Physical"
        public string Name = "";     // the game's wording for the type
        public double Min, Max;      // what the game shows
        public bool OverTime;
        public double Seconds;       // base duration for a dot
        // The durations behind that number when they are not all the same: the
        // weapon's bleeding may run three seconds and the skill's one, and each
        // was divided by its own. Showing a single figure there reads as if both
        // had it.
        public string SecondsText = "";
        public readonly List<string> Why = new List<string>();
    }

    public class DamageModel
    {
        public readonly List<DamageLine> Lines = new List<DamageLine>();
        public readonly List<string> Notes = new List<string>();
        public string WeaponName = "";
        public double TotalMin, TotalMax;
        // The skill this is the damage of, or null for a plain attack.
        public readonly AttackSkill Skill;

        // The eleven types, with the divider the attribute scaling uses for the
        // direct damage and for the damage over time of the same root.
        // combatformulas.dbr: cunning/245 for physical and pierce, cunning/215 for their
        // dots, spirit/215 for the magic types, spirit/200 for their dots.
        //
        // TYPE NAMES ARE LANGUAGE-FILE KEYS (`model.txt`; PLAN, the interface-translation
        // section, stage 5): the line "1234 fire damage" is assembled in `Add`, where the
        // key becomes a word. The type's internal name (`Field`) comes from the game's
        // records and is not translated.
        struct Kind
        {
            public string Field, RuKey;
            public bool Magic;       // scales from spirit rather than cunning
            public bool Elemental;   // fire, cold, lightning: also take % elemental
            public Kind(string field, string ruKey, bool magic, bool elemental)
            { Field = field; RuKey = ruKey; Magic = magic; Elemental = elemental; }
        }

        static readonly Kind[] Kinds = {
            new Kind("Physical",  "model.type.Physical",  false, false),
            new Kind("Pierce",    "model.type.Pierce",    false, false),
            new Kind("Fire",      "model.type.Fire",      true,  true),
            new Kind("Cold",      "model.type.Cold",      true,  true),
            new Kind("Lightning", "model.type.Lightning", true,  true),
            new Kind("Poison",    "model.type.Poison",    true,  false),
            new Kind("Life",      "model.type.Life",      true,  false),
            new Kind("Aether",    "model.type.Aether",    true,  false),
            new Kind("Chaos",     "model.type.Chaos",     true,  false),
            new Kind("Bleeding",  "model.type.Bleeding",  false, false),
        };

        // The damage-over-time of each root, and the name the game gives it (language key).
        static readonly Dictionary<string, string> DotName = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Physical", "model.dot.Physical" }, { "Fire", "model.dot.Fire" }, { "Cold", "model.dot.Cold" },
            { "Lightning", "model.dot.Lightning" }, { "Poison", "model.dot.Poison" }, { "Life", "model.dot.Life" },
            { "Bleeding", "model.dot.Bleeding" },
        };

        readonly GameData game;
        // Exposed only so the window can print the records of a skill's parts with the
        // same code it prints items with (DamageForm.AddSkillParts).
        public GameData Game { get { return game; } }
        readonly Character c;
        readonly CharacterTotals totals;
        readonly DbRecord weapon;
        readonly double cunning, spirit;

        public DamageModel(GameData game, Character c, CharacterTotals totals)
            : this(game, c, totals, null) { }

        public DamageModel(GameData game, Character c, CharacterTotals totals, AttackSkill skill)
            : this(game, c, totals, skill, true) { }

        // withTemporary == false leaves out the pieces that only hang for a few
        // seconds after the skill lands. The game's own panel shows a skill
        // standing still, so that is the number to hold the panel up against;
        // with them is what the skill does once you are actually swinging.
        public DamageModel(GameData game, Character c, CharacterTotals totals, AttackSkill skill,
                           bool withTemporary)
            : this(game, c, totals, skill, withTemporary, null) { }

        // exact, when given, replaces the conversion percentages with the ones the
        // game rolled. Conversions are read straight off the records rather than
        // through the summed bonuses, so they need telling separately — and they
        // matter: on `_Relo` the record says 50/50/30/30 and the game rolled
        // 54/43/30/26.
        public DamageModel(GameData game, Character c, CharacterTotals totals, AttackSkill skill,
                           bool withTemporary, ExactRolls exact)
            : this(game, c, totals, skill, withTemporary, exact, null) { }

        // `roll`, when given, turns EVERY `min–max` range into one rolled number — its own
        // roll per printed line (MECHANICS 31). The panel calls without it and gets the
        // range as before; the timeline calls with it and gets one concrete hit. The
        // calculation is THE SAME either way — the user's decision, analysis in PLAN.
        public DamageModel(GameData game, Character c, CharacterTotals totals, AttackSkill skill,
                           bool withTemporary, ExactRolls exact, HitRoll roll)
            : this(game, c, totals, skill, withTemporary, exact, roll, 0) { }

        // `atCharge` — WHICH CHARGE STEP to count the skill at. Zero is what the panel has
        // always done: FULL charge (`AttackSkills.ChargeOf` takes the last step, measured on
        // Runic Bolts, MECHANICS 19z).
        //
        // The queue needs it: Savagery builds charge with every hit, and the first hits are
        // weaker than the full one. The panel is right to show a skill at its best; the
        // queue must show what actually happens.
        public DamageModel(GameData game, Character c, CharacterTotals totals, AttackSkill skill,
                           bool withTemporary, ExactRolls exact, HitRoll roll, int atCharge)
        {
            this.game = game; this.c = c; this.totals = totals; this.Skill = skill;
            this.withTemporary = withTemporary; this.exact = exact; this.roll = roll;
            this.atCharge = atCharge;
            weapon = c.Weapons1.Count > 0 && !string.IsNullOrEmpty(c.Weapons1[0].BaseName)
                   ? game.Record(c.Weapons1[0].BaseName) : null;
            if (weapon != null)
                WeaponName = NameOf(c.Weapons1[0]);

            cunning = Attribute(c.Cunning, "characterDexterity");
            spirit = Attribute(c.Spirit, "characterIntelligence");
            Speed();
            Build();
        }

        // The attribute as the game has it: flat bonuses first, then the
        // percentage over the sum. Checked to the unit on _Relo.
        double Attribute(double own, string field)
        {
            double flat = 0, percent = 0;
            TotalsEntry e;
            if (totals.Fields.TryGetValue(field, out e)) flat = e.ValueOn(Landing);
            if (totals.Fields.TryGetValue(field + "Modifier", out e)) percent = e.ValueOn(Landing);
            return (own + flat) * (1 + percent / 100);
        }

        // The chance-based addend of the sum for this hit (MECHANICS 89b): in the queue it
        // is one roll per key per hit; without a roll it is the average (null), and for the
        // tick check either all landed or none (`ChanceAllAs`), so the range knows both
        // edges.
        public static bool? ChanceAllAs;
        Func<string, double, bool> Landing
        {
            get
            {
                if (roll != null) return roll.Landed;
                if (ChanceAllAs == null) return null;
                bool all = ChanceAllAs.Value;
                return delegate (string key, double chance) { return all; };
            }
        }

        double Sum(string field)
        { TotalsEntry e; return totals.Fields.TryGetValue(field, out e) ? e.ValueOn(Landing) : 0; }

        // The character's "% total damage" — the damage tab needs it to explain why our
        // type percentage differs from the game panel's: the panel prints them on one line,
        // type plus total (MECHANICS 14d).
        public double TotalDamagePercent { get { return Sum("offensiveTotalDamageModifier"); } }

        // THE TYPE PERCENTAGE AS THE STAT PANEL PRINTS IT: the type's own percentage PLUS
        // "% total damage", on one line (MECHANICS 14d). Added for the `check Pct:<Type>`
        // check.
        //
        // WHY. These game numbers used to sit in the stands as COMMENTS, guarded by
        // nothing: thirteen of them on `_Itaan`, eight on `_Acid Vines`, checked by hand —
        // that is, only when someone asked. And an error in a type percentage moves EVERY
        // line of that type at once, after which it is hunted through the lines for half a
        // day.
        //
        // `<Type>Dot` is the dot percentage: internal trauma is `PhysicalDot`, freeze
        // `ColdDot`, decay `LifeDot`, poison `PoisonDot`, bleeding `BleedingDot`, burn
        // `FireDot`.
        public double PanelPercent(string type)
        {
            bool dot = type.EndsWith("Dot", StringComparison.Ordinal);
            string bare = dot ? type.Substring(0, type.Length - 3) : type;
            double pct = Sum((dot ? "offensiveSlow" : "offensive") + bare + "Modifier") + TotalDamagePercent;
            // The panel adds "% elemental damage" to fire, cold and lightning — and only to
            // the direct damage, not to their dots (MECHANICS 14v, three dots measured).
            if (!dot)
                foreach (Kind k in Kinds)
                    if (k.Field == bare && k.Elemental) pct += Sum("offensiveElementalModifier");
            return pct;
        }

        // One field of one record, first step of its per-level list.
        static double Own(DbRecord rec, string field)
        {
            if (rec == null) return 0;
            string v = rec.Get(field);
            double d;
            return !string.IsNullOrEmpty(v) &&
                   double.TryParse(v.Split(';')[0], NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        readonly bool withTemporary = true;
        readonly ExactRolls exact;
        // The roll of one concrete hit, when a hit is counted rather than the panel.
        // See HitRoll.
        readonly HitRoll roll;
        // The charge step to count the skill at; 0 — full charge, as the panel does.
        readonly int atCharge;

        // AN ITEM'S NAME IS BUILT ONCE PER MODEL, not five times.
        //
        // The name is needed by the off-hand subtraction, by the rolled numbers
        // (`exact.For`) and by the roll key. Resolve the same name the tooltip
        // displays without building its details and icon on every simulated hit.
        readonly Dictionary<Item, string> nameOf = new Dictionary<Item, string>();

        string NameOf(Item it)
        {
            string got;
            if (!nameOf.TryGetValue(it, out got))
            {
                DbRecord record = game.Record(it.BaseName);
                got = record == null ? it.BaseName : ItemViewFactory.FullName(game, it, record) ?? it.BaseName;
                nameOf[it] = got;
            }
            return got;
        }

        // What an attack comes to when both hands swing: three quarters of the sum
        // of the two, the average over the dual-wield cycle. The three quarters are not a
        // number in the code but the set's attack-animation weights
        // (`SkillHands.DualSwing`, MECHANICS 89o): "both hands" 50 + "one hand" 25; if they
        // cannot be read, the old 0.75 from the fallback layout.
        double DualShare
        {
            get
            {
                if (dualShare < 0)
                    dualShare = SkillHands.DualSwing(game, IsDualRanged(game, c)).PerHand;
                return dualShare;
            }
        }
        double dualShare = -1;

        // Attacks per second, and the per cent the game shows beside it.
        //
        // The base is the weapon's kind, and it is a WHITE list, not a black one:
        // 1.973684311 goes to the five one-handed melee classes and to bare hands,
        // 1.7857143 to everything else — including **one-handed ranged**. We had it
        // the other way round ("everything that is not two-handed"), and so gave a pistol
        // the melee base. The game: the tooltip of both `_Iman` pistols says 1.70 attacks
        // per second at `characterBaseAttackSpeed −0.05`, and 1.70 / 0.95 = 1.789 — that is
        // 1.7857143, while our old base would have given 1.875.
        //
        // The full list of weapon classes in the game database (counted over all
        // `gearweapons` records) — eleven, plus shield and off-hand, which do not count as
        // weapons:
        //
        //     1.9737   Sword  Axe  Mace  Dagger  Scepter          (one-handed melee)
        //     1.7857   Sword2h  Axe2h  Mace2h  Spear2h            (two-handed melee)
        //     1.7857   Ranged1h  Ranged2h                         (ranged)
        //
        // There is NO `Staff` class in the database at all — the old check for it was dead.
        // The record's characterBaseAttackSpeed is an offset from that base.
        //
        // Dual wielding averages three swings of the cycle. The rule and both constants
        // come from the analysis of the GrimTools calculator (SITE-VS-US.md, § 3).
        // **The character panel's number does not follow this formula** — neither with one
        // hand nor with two; analysis in HANDOFF, "attacks per second".
        const double OneHandAPS = 1.973684311, TwoHandAPS = 1.7857143;

        // The classes that get the fast base. The comparison is exact: the name
        // `WeaponMelee_Sword` is a prefix of `WeaponMelee_Sword2h`.
        static readonly string[] FastOneHand = {
            "WeaponMelee_Sword", "WeaponMelee_Axe", "WeaponMelee_Mace",
            "WeaponMelee_Dagger", "WeaponMelee_Scepter",
        };

        public double AttacksPerSecond, AttackSpeedPercent, WeaponsAPS;
        // Cast speed in per cent — the "cast speed 200%" of the character panel. Counted
        // THE SAME WAY as attack speed but without a weapon: 100 + total speed + cast
        // speed, capped by the game's cap plus "to the cast speed cap" (the game's tooltip
        // says plainly "including total speed"). Analysis — MECHANICS 74.
        public double CastSpeedPercent, CastSpeedCap;

        // HOW LONG ONE CAST TAKES when the skill has no cooldown — from its animation
        // (`SkillHands.CastSeconds`, seconds at speed "1"):
        //
        //     interval = animation / (1.25 × cast speed per cent / 100)
        //
        // THE USER'S MEASUREMENT — Bloody Pox on `_Acid Vines` (150%): about 29 casts in
        // 10 seconds. The timer path (as for channelling, 300 ms / 1.25) gives 29.6, the
        // attack path (× 0.76, as in the attack panel) gives 22.5. So casting has NO 0.76
        // panel multiplier: the animation is divided by the same character base as the
        // channelling timer. The first attempt here used the attack path, by analogy, and
        // the analogy failed again (MECHANICS 74a).
        //   animation 19 frames (0.633 s): at 100% — 0.507 s, at 150% — 0.338 s
        public double CastInterval(double animSecs)
        {
            if (animSecs <= 0 || CastSpeedPercent <= 0) return 0;
            return animSecs / (Combat.BaseCastSpeed(game) * CastSpeedPercent / 100.0);
        }

        // HOW LONG THE ANIMATION OF A WEAPON SKILL TAKES — the same path as the swing
        // itself, only with its own length:
        //
        //     interval = animation / (attack base × 0.76 × attack speed per cent / 100)
        //
        // Not a new formula but the one behind `AttacksPerSecond`: with the ordinary
        // attack's animation (19 frames, 0.6333 s) it returns exactly
        // `1 / AttacksPerSecond`. A weapon skill plays at attack speed, a spell at cast
        // speed (`CastInterval`, which has no 0.76 multiplier — MECHANICS 74a). We have NO
        // measurement of a weapon skill's own length; this is reading by analogy with the
        // swing, and it is said so in the help (MECHANICS 87).
        public double AttackInterval(double animSecs)
        {
            if (animSecs <= 0 || AttackSpeedPercent <= 0) return 0;
            return animSecs / (Combat.BaseAttackSpeed(game) * PanelAPSFactor * AttackSpeedPercent / 100.0);
        }

        // The tick of this model's CHANNELLED skill at its cast speed, in seconds. Zero
        // means the skill is not channelled (or this is a character model, with no skill).
        public double ChannelTick()
        {
            return Skill == null || Skill.ChannelMs <= 0 ? 0
                 : Combat.ChannelInterval(game, Skill.ChannelMs, CastSpeedPercent);
        }

        // THE WEAPON'S OWN SPEED — the line the game prints in the item's tooltip:
        // "1.39 attacks per second". This is NOT the character's speed: their percentages do
        // not enter it AT ALL, measured twice on two different weapon classes:
        //
        //     `_Iman` pistols  `characterBaseAttackSpeed −0.05`  1.7857143 × 0.95 = 1.696 → 1.70
        //     `_Mivan` gun     `characterBaseAttackSpeed −0.22`  1.7857143 × 0.78 = 1.393 → 1.39
        //
        // `_Mivan` has +16% attack speed from gear (the gun itself +10%, the gloves +6%),
        // and the tooltip does not show it. So the record's offset is a MULTIPLIER over the
        // class base, while the character's percentages stay with the panel.
        //
        // This is printed in the tooltip (ItemView), and the line pays off directly: the
        // one remaining red calculation check is DUAL weapon speed
        // (`stand-iman-speed`), and now each weapon's base is visible to the eye.
        public static double? WeaponTipAPS(DbRecord rec)
        {
            if (rec == null || !IsWeapon(rec)) return null;
            string cls = rec.Class ?? "";
            double b = TwoHandAPS;
            foreach (string one in FastOneHand)
                if (string.Equals(cls, one, StringComparison.OrdinalIgnoreCase)) { b = OneHandAPS; break; }
            return b * (1 + Own(rec, "characterBaseAttackSpeed"));
        }

        double BaseAPS(int hand)
        {
            DbRecord rec = Hand(game, c, hand);
            if (rec == null) return OneHandAPS;          // bare hands
            string cls = rec.Class ?? "";
            foreach (string one in FastOneHand)
                if (string.Equals(cls, one, StringComparison.OrdinalIgnoreCase)) return OneHandAPS;
            return TwoHandAPS;
        }

        // The character panel counts "attacks per second" NOT from the weapon's speed
        // directly, but by that speed times a constant factor. Measured on `_111` — level one, no
        // speed bonuses at all, so the percentage equals the base and the factor reads
        // straight off:
        //
        //     in hands                 item tooltip   panel   panel/tooltip
        //     nothing (base 1.973684311)   1.97        1.50       0.7600
        //     a mace                       1.84        1.40       0.7609
        //     another mace                 1.80        1.37       0.7611
        //     an axe                       1.93        1.47       0.7617
        //
        // The 0.76 is exact: `1.973684311 × 0.76 = 1.5000` on the nose, and the spread in
        // the fourth digit is the rounding of both numbers to hundredths.
        //
        // Checked on numbers that took no part in deriving it (those panels were taken
        // before this factor appeared):
        //   `_Amakata`, two swords:   0.76 × 1.9737 × 0.96 × 2.05 = 2.95   game 2.95
        //   `_Iman`, two pistols:     0.76 × 1.9737 × 0.95 × 1.98 = 2.82   game 2.90
        // The remainder on `_Iman` is not in the factor but in the percentage: ours 188%,
        // the game's 194%.
        //
        // A players' analysis (m2.txt, 2022) gives the same formula and the same classes but
        // 0.95 instead of 0.76: a bare character reads 1.88 there and 1.50 here, a ratio of
        // exactly 0.8 — the constant was probably moved by a patch. Their numbers still
        // confirm TWO things we take from here: `base = 0.95 × the class's APS` in form,
        // and **with dual wielding the class becomes the "fast" one** (two pistols read
        // 3.75 there, like one-handed melee, one pistol 3.39, like two-handed). The second
        // is confirmed by our `_Iman` as well: with its own class base it would be 2.56
        // against the game's 2.90.
        //
        // WHAT IS MEASURED AND WHAT IS PREDICTED: the 0.76 factor is measured only on the
        // fast class (one-handed melee and bare hands). For the slow one it gives
        // `1.7857143 × 0.76 = 1.3571`, and that is a PREDICTION. One screenshot settles it:
        // the same `_111` with any two-hander or with a pistol — the panel must show
        // "item tooltip × 0.76".
        const double PanelAPSFactor = 0.76;

        void Speed()
        {
            double s1, s2;
            bool have1 = HandSpeed(0, out s1), have2 = HandSpeed(1, out s2);
            // With dual wielding the class is always the fast one, whatever is in the
            // hands — see the factor analysis above.
            double b = DualWield() ? OneHandAPS : BaseAPS(0);
            if (!have1) WeaponsAPS = OneHandAPS;
            else if (DualWield())
            // THE DUAL-WIELD FACTOR IS AN AVERAGE OF THE OFFSETS, and it is MEASURED:
            // four characters, eight panel numbers, all to the last digit
            // (`stand-jien-speed`, `-hamarina-`, `-itaan-`, `-amakata-`):
            //   _Jien −0.08/−0.08 → 178% 2.66   _Hamarina −0.06/−0.06 → 162% 2.43
            //   _Itaan −0.05/−0.03 → 150% 2.25  _Amakata −0.04/−0.04 → 199% 2.98
            // Adding the offsets would give 162/151/144/190, multiplying them
            // 178/152/144/191 — both dead. The expression below (the average of three
            // swings) is identically `1 + (s1+s2)/2`; it is left as written because that is
            // how it was measured. Do not change without a new measurement:
            // MECHANICS 14r3, "measured, do not reopen".
            //
            // NOT ENTERED: DUAL RANGED. `_Iman`'s two `Ranged1h` pistols (both −0.05) give
            // 204% by this rule while the game shows 194%. From two states of the same day
            // (with and without gloves, exactly 25% apart) the factor comes out
            // unambiguously: 0.8983…0.8993 instead of 0.95. TWO readings fall inside that,
            // and `_Iman` cannot tell them apart:
            //   sum      1 + s1 + s2   = 0.9000
            //   product  (1+s1)(1+s2)  = 0.9025
            // — both pistols share one offset, and the readings differ by one hundredth of
            // an attack, right on the rounding boundary. So NEITHER is entered here. A dual
            // gunner with DIFFERENT pistols (different `characterBaseAttackSpeed`) will
            // settle it, the way `_Itaan` settled it for melee. Analysis: HANDOFF, the
            // "dual wielding closed" entry.
                // TWO BRANCHES, BOTH MEASURED. The weapon CLASS tells them apart: a
                // one-handed melee weapon's own base is already the fast one, while
                // `Ranged1h` is a slow class (1.7857) that the dual rule replaces with the
                // fast base. Exactly these cases behave differently, and whether that is
                // coincidence or cause cannot be told from the numbers: `Ranged1h` is the
                // only slow class you can hold two of.
                WeaponsAPS = BaseAPS(0) < OneHandAPS
                    // DUAL RANGED: the offsets ADD UP. Measured on `_Iman` — eight setups,
                    // seven different second pistols. THREE of them settled it, having no
                    // speed percentage of their own, so the character's percentage sum is
                    // the same for all three (210) and ONLY the second barrel's offset
                    // differs:
                    //   Arcanum Electrollis  −0.08 → 183%  2.74   game 183  2.74
                    //   Cindercore           −0.07 → 185%  2.77   game 185  2.77
                    //   Mutiny               −0.06 → 187%  2.80   game 187  2.80
                    // The product would give 184 / 186 / 188 — off by 1 on all three.
                    // Averaging gives 196 / 197 / 198 — off by thirteen.
                    // The sum of 210 itself is held by the single-pistol setup: one pistol
                    // reads 199% and 2.71, which is `210 × 0.95 = 199.5` and
                    // `1.3571 × 1.995`; at 209 it would be 2.69, so it cannot be fitted.
                    ? b * (1 + s1 + s2)
                    // DUAL MELEE: the offsets are AVERAGED. Measured on four characters,
                    // see the comment above. The "average of three swings" form is
                    // identically `1 + (s1+s2)/2`; left as written because that is how it
                    // was measured.
                    : (b * (1 + s1) + b * (1 + s2) + b * (1 + (s1 + s2) / 2)) / 3;
            else WeaponsAPS = (1 + s1) * b;

            CastSpeedPercent = 100 + Sum("characterTotalSpeedModifier") + Sum("characterSpellCastSpeedModifier");
            CastSpeedCap = Combat.CastSpeedCap(game) + Sum("characterSpellCastSpeedMaxModifier");
            if (CastSpeedPercent > CastSpeedCap) CastSpeedPercent = CastSpeedCap;
            double castFloor = Combat.CastSpeedFloor(game);
            if (CastSpeedPercent < castFloor) CastSpeedPercent = castFloor;

            double mod = Sum("characterTotalSpeedModifier") + Sum("characterAttackSpeedModifier");
            // The percentage is a different quantity: it carries the weapon's own
            // speed as a ratio to the base of its kind, and it is capped. The panel factor
            // is not in it — it cancels out, being in the numerator as well.
            AttackSpeedPercent = have1 ? (100 + mod) * (WeaponsAPS / b) : 100 + mod;
            double cap = Combat.AttackSpeedCap(game) + Sum("characterAttackSpeedMaxModifier");
            if (AttackSpeedPercent > cap) AttackSpeedPercent = cap;
            // Capping the percentage caps the attack count too: they are the same quantity
            // in different units.
            AttacksPerSecond = PanelAPSFactor * b * AttackSpeedPercent / 100;

            // THE SPEED OF ONE SWING — the timeline needs it, the panel does not print it.
            //
            // Our measured DUAL MELEE formula is written as the average of THREE swings,
            // and that is not decoration but HOW IT WAS MEASURED:
            //     (b(1+s1) + b(1+s2) + b(1+(s1+s2)/2)) / 3  ≡  b(1 + (s1+s2)/2)
            //      └ first hand only └ second only └ both
            // So every outcome of the hand layout has ITS OWN speed, and the panel shows
            // their average. Analysis — PLAN.
            //
            // DUAL RANGED does not split that way: there the offsets ADD UP and the total
            // speed is faster than either hand alone. There is nothing to split, so every
            // swing there takes the panel speed.
            swingBase = b; swingMod = mod; swingCap = cap;
            swingSplit = have1 && have2 && DualWield() && !(BaseAPS(0) < OneHandAPS);
            swingOffset[0] = s1; swingOffset[1] = s2;
        }

        double swingBase, swingMod, swingCap;
        bool swingSplit;
        readonly double[] swingOffset = new double[2];

        // Attacks per second for one swing: 0 — both hands, 1 — first hand only,
        // 2 — second only. Where there is nothing to split, the answer is the same for all
        // three: the panel's number.
        public double SwingAPS(int kind)
        {
            if (!swingSplit || kind == 0) return AttacksPerSecond;
            double own = swingBase * (1 + swingOffset[kind - 1]);
            double percent = (100 + swingMod) * (own / swingBase);
            if (percent > swingCap) percent = swingCap;
            return PanelAPSFactor * swingBase * percent / 100;
        }

        bool HandSpeed(int hand, out double offset)
        {
            offset = 0;
            if (hand >= c.Weapons1.Count || string.IsNullOrEmpty(c.Weapons1[hand].BaseName)) return false;
            DbRecord rec = game.Record(c.Weapons1[hand].BaseName);
            if (rec == null || !IsWeapon(rec)) return false;
            offset = Own(rec, "characterBaseAttackSpeed");
            return true;
        }

        // The same field over the records that make up the chosen skill.
        //
        // A CHANCE ON A PERCENTAGE APPLIES HERE TOO. The rule "a chance-based value enters
        // as its expectation" lived in three places, all of them incomplete. There turned
        // out to be FOUR: `CharacterTotals` applies the chance to the character's
        // percentages, `SklFlat` and `SkillDot` to the skill's flat damage and dots, while
        // the skill's OWN percentages were added whole.
        //
        // There is a live example: Storm Touched (`savagery3`) carries
        // `offensiveLightningModifier 245` next to `offensiveLightningModifierChance 40`.
        // It was invisible on `_Jien` only because his lightning is wholly converted to cold
        // by the helmet, so its percentage never enters the hit. Found by a census of skill
        // fields, not by a disagreement with the game.
        double Skl(string field)
        {
            if (Skill == null) return 0;
            double total = 0;
            foreach (SkillPart p in Skill.Parts)
            {
                if (p.Temporary && !withTemporary) continue;
                // A PERCENTAGE THAT CAME FROM THE FORM IS NOT THE SKILL'S — see SklForm
                // just below, and `SkillPart.FormWide`, where the measurement stands.
                if (p.FormWide && !BreakFormPercentAsSkill) continue;
                if (XorOut(p.Rec, field, p.Slot)) continue;
                double one = AttackSkill.Value(p.Rec, field, p.Slot) * AttackSkill.ChargeOf(p, atCharge);
                if (Math.Abs(one) < 0.0001) continue;
                total += one * PartShare(p.Rec, p.Slot, field, field + "Chance", field);
            }
            return total;
        }

        // THE SAME WALK OVER THE PARTS THAT CAME FROM THE FORM — and their percentages are
        // added to the multiplier THE WAY THE CHARACTER'S ARE: whole, with no `Left`, and
        // also for damage that ARRIVED BY CONVERSION (where a skill's percentage is withheld
        // by rule 19i). That difference is the whole point, and it is what `_555` measured
        // on 21 October — the arithmetic is at `SkillPart.FormWide` and in MECHANICS 169.
        //
        // The mark is on the PART, not on the field, so a form that carries both kinds is
        // split correctly: Form of the Beast gives the character +165% total damage, while
        // Ice Talons' own percentage, which sits on the skill's own record, stays the
        // skill's.
        double SklForm(string field)
        {
            if (Skill == null || BreakFormPercentAsSkill) return 0;
            double total = 0;
            foreach (SkillPart p in Skill.Parts)
            {
                if (!p.FormWide) continue;
                if (p.Temporary && !withTemporary) continue;
                if (XorOut(p.Rec, field, p.Slot)) continue;
                double one = AttackSkill.Value(p.Rec, field, p.Slot) * AttackSkill.ChargeOf(p, atCharge);
                if (Math.Abs(one) < 0.0001) continue;
                total += one * PartShare(p.Rec, p.Slot, field, field + "Chance", field);
            }
            return total;
        }

        // THE SAME WALK, BUT THE PARTS MULTIPLY INSTEAD OF ADDING UP.
        //
        // Only "total damage modified by N%" is read this way, and why is written where it is
        // used (`Multiplier`): two such modifiers on one skill give the game's number only as a
        // product. Everything else about a part — charges, the share of a chance-based one — is
        // counted exactly as in `Skl`, so the two differ in one thing alone.
        double SklProduct(string field)
        {
            if (Skill == null) return 1;
            double product = 1;
            foreach (SkillPart p in Skill.Parts)
            {
                if (p.Temporary && !withTemporary) continue;
                if (XorOut(p.Rec, field, p.Slot)) continue;
                double one = AttackSkill.Value(p.Rec, field, p.Slot) * AttackSkill.ChargeOf(p, atCharge);
                if (Math.Abs(one) < 0.0001) continue;
                product *= 1 + one * PartShare(p.Rec, p.Slot, field, field + "Chance", field) / 100.0;
            }
            return product;
        }

        // `--multadd` — the OLD reading: "total damage modified" adds up. Kept so the rule
        // can be broken on purpose and the `_Telianor` stand seen going red (PLAN, rule 12).
        public static bool MultModifiersAdd;

        // `--dotoffhandbreak` — give a skill that strikes with the OFF HAND the FIRST hand's
        // dot pool again, i.e. the rule as it stood before 16 October. `_RuneTask`'s Aegis
        // goes back to 29077 against the game's 26027 (MECHANICS 150). Nothing else reads it.
        public static bool BreakOffHandDot;

        // `--formskillpct` — count a FORM's percentages as THE SKILL'S OWN again, i.e. the
        // rule as it stood before 21 October. `_555` goes back to its six errors: physical
        // −2.0%, acid −15.4% (both werewolf skills) and chaos −4.5% (both werecrow ones).
        // Read in `Skl` and `SklForm`, which are the only two places that tell them apart.
        public static bool BreakFormPercentAsSkill;

        // `--blastbesthand` — give the BLAST share the BEST HAND again instead of the
        // unsplit sum, i.e. the rule as it stood before 21 October. `_Tapina`'s Shadow
        // Strike goes back to −3.4% / −4.2% on pierce and −1.4% on bleeding (MECHANICS 167).
        // Read in two places, the direct damage and the dot — both are the same share.
        public static bool BreakBlastBestHand;

        // `--handdotbreak` — put the PER-HAND PANEL's dot back into the whole-hand bracket
        // that a skill TOOLTIP prints, i.e. the rule as it stood before 15 October. The
        // bracket then loses a dot's conversion inflow wherever the hand does not carry the
        // dot itself (MECHANICS 149). Nothing else reads this.
        public static bool BreakHandDotWindow;

        // "ONE OF" (XOR): THE PANEL TAKES THE GROUP'S FIRST FIELD, A HIT TAKES THE ROLLED
        // ONE.
        //
        // The panel is measured (MECHANICS 19b): only the first field by type order goes in,
        // with its own chance as expectation. The queue used to do the same — every Overload
        // hit knew burn alone (MECHANICS 85a). The user's decision is to roll: one die per
        // group per hit, the field falls out by the group's chances, and the rolled one goes
        // in whole (its own chance is already played by that die).
        //
        // `--xorfirst` restores the old "always the first" in the queue too — so the share
        // guard in `--timeline` can be broken on purpose and seen going red.
        public static bool XorFirstOnly;

        // EXPERIMENT (`--arrivedonly`): count the PER-HAND bracket the way the hit line is
        // counted — a skill percentage that moved over lands ONLY ON WHAT ARRIVED, not on
        // the whole hand pool. Added to test a suspect: on `_Itaan` the brackets differ only
        // for skills with a conversion of THEIR OWN. The default rule is unchanged (the
        // `_Jien` measurement, MECHANICS 25v) — the flag does not change it but lets it be
        // broken on purpose to see where the numbers go.
        public static bool ProbeArrivedOnly;

        // FOR THE TICK CHECK (`--timeline`): count without a roll as if THIS field came up
        // whole in every "one of" group ("" — nothing came up). A rolled dot is legitimately
        // above the panel's expectation, and below it when another field came up; the tick's
        // range must know both edges, or 165 `_Viliya` ticks read as "off" with a correct
        // engine.
        public static string XorAs;

        // The "the group is played whole" mode: either the queue's roll or the check's
        // substitution.
        bool XorWhole { get { return roll != null ? !XorFirstOnly : XorAs != null; } }

        // Which field of the group came up on this hit; without a roll, and with
        // `--xorfirst`, the first one.
        string XorPick(DbRecord rec, int slot, string prefix)
        {
            if (roll == null)
                return XorAs == null ? CharacterTotals.XorFirst(rec, prefix, slot)
                     : XorAs.Length == 0 ? null : XorAs;
            bool fresh;
            double u = roll.XorShare(rec.Path + "|" + slot + "|" + prefix, out fresh) * 100;
            double chance = 0;
            string root = XorFirstOnly ? CharacterTotals.XorFirst(rec, prefix, slot)
                                       : CharacterTotals.XorRolled(rec, prefix, slot, u, out chance);
            if (XorFirstOnly) chance = CharacterTotals.Chance(rec, prefix + root + "Chance", slot);
            if (fresh)
            {
                string name = rec.Path.Substring(rec.Path.LastIndexOfAny(new[] { '/', '\\' }) + 1);
                roll.CountXor(name.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name,
                              root, chance);
            }
            return root;
        }

        // A skill part's field lost its "one of" group on this hit.
        bool XorOut(DbRecord rec, string field, int slot)
        {
            string prefix, mine;
            return CharacterTotals.InXor(rec, field, slot, out prefix, out mine) &&
                   !string.Equals(XorPick(rec, slot, prefix), mine, StringComparison.Ordinal);
        }

        // The chance share of a skill part's field. Call AFTER `XorOut`: a group field that
        // got here in the queue has already come up on the group's die, and its chance is
        // not rolled a second time.
        double PartShare(DbRecord rec, int slot, string field, string chanceField, string shareField)
        {
            string prefix, mine;
            if (XorWhole && CharacterTotals.InXor(rec, field, slot, out prefix, out mine))
                return 1;
            return Share(CharacterTotals.Chance(rec, chanceField, slot), rec.Path, shareField);
        }

        // Chance-based damage enters as its expectation, not at its peak.
        //
        // `_Assira`'s Shattered Star says "50% chance of 11906 – 23101 lightning damage",
        // and in the record that is `offensiveLightningChance 50` next to the ordinary
        // Min/Max. We counted it whole and lightning came out 37–48% above the game. The
        // rule comes from the site analysis (step 5); now there is a measurement behind it.
        //
        // The chance belongs to the record, not to the field, so the sum runs over sources:
        // one may have it, another may not.
        double SklFlat(string type, string end) { return SklFlat(type, end, false); }

        // THE FLAT DAMAGE OF AN ITEM'S FORM MODIFIER (`SkillPart.FromForm`). It goes not
        // into the skill's pool but into the CHARACTER'S SUM — that is, it is multiplied by
        // the weapon share and averaged over hands like every other character addend.
        //
        // THE MEASUREMENT AND BOTH READINGS — MECHANICS 22i. There were exactly two
        // readings, and they differ threefold, so they cannot be confused:
        //
        //   as the skill's own damage   0.75 × 235 × 27.2527 + weapon = 13217
        //   as the character's sum      0.75 × 298 × 1.66 × 27.2527 + own = 13892
        //   the game (Feral Claws)                                         13892
        //
        // The form's second skill confirms the same with a DIFFERENT weapon share (3.31
        // against 1.66): 27233 against the game's 27233.
        //
        // NOT MEASURED and therefore left as is: such a modifier's PERCENTAGES. They are
        // still taken by `Skl()` as skill percentages. The Crimson Moon has none in that
        // record at all (only flat bleeding and health restored), so there is nothing to
        // tell the two readings apart; and across all our saves there is exactly one item
        // modifier aimed at a form.
        double FormFlat(string field)
        {
            if (Skill == null) return 0;
            double total = 0;
            foreach (SkillPart p in Skill.Parts)
            {
                if (!p.FromForm) continue;
                if (p.Temporary && !withTemporary) continue;
                total += AttackSkill.Value(p.Rec, field, p.Slot);
            }
            return total;
        }

        // THE TOP END ADDS UP OVER RECORDS, not over fields — the same rule as for the
        // weapon pool (MECHANICS 14v2), which was missing here.
        //
        // A record may carry only `Min` — that is a FLAT number whose top equals its bottom
        // (and the game prints it without a range: "6970 lightning damage"). If the top end
        // were the plain sum of the `Max` fields, such records would not enter it at all and
        // the top would lose exactly their size. This used to be patched over with
        // `Math.Max(low, high)`, which lies as soon as a skill has both a flat record and a
        // ranged one:
        //
        //   `_Iman`'s Fire Strike, lightning: Static Charge 236 flat
        //   (×0.76 chance = 179.36) and Dagallon's Faceguard 1 – 32.
        //     was    bottom 180.36   top max(180.36, 32) = 180.36   — the 32 vanished
        //     now    bottom 180.36   top 179.36 + 32 = 211.36
        //
        // Checked against the game: Fire Strike's lightning top was 20265 against the game's
        // 22530 (−10.1%), and is now 21661 (−3.9%).
        double SklFlat(string type, string end, bool oneHand)
        {
            if (Skill == null) return 0;
            bool top = end == "Max";
            double total = 0;
            foreach (SkillPart p in Skill.Parts)
            {
                if (p.Temporary && !withTemporary) continue;
                if (p.FromForm) continue;   // a form modifier goes into the character's sum, see FormFlat
                if (p.OwnOnce != oneHand) continue;
                double one = SklField(p, "offensive" + type + end);
                // A flat record: its top is the same as its bottom.
                if (top) one = Math.Max(one, SklField(p, "offensive" + type + "Min"));
                if (Math.Abs(one) < 0.0001) continue;
                // THE SKILL'S OWN DAMAGE IS ROLLED TOO. MECHANICS 31 is not about items but
                // about EVERY line with a range: "552 – 804 physical" of a proc is the same
                // kind of range as a weapon's. Without this a proc hit for the same number
                // every time, which never happens in the game. The roll's source is the
                // skill part's record plus its slot.
                if (roll != null)
                {
                    double lo = SklField(p, "offensive" + type + "Min");
                    double hi = Math.Max(SklField(p, "offensive" + type + "Max"), lo);
                    one = roll.Pick(p.Rec == null ? "skill" : p.Rec.Path + "|" + p.Slot,
                                    "offensive" + type + "Min", lo, hi);
                }
                // Its own chance, or, failing that, the record's global one
                // (`offensiveGlobalChance`), see CharacterTotals.Chance.
                total += one * PartShare(p.Rec, p.Slot, "offensive" + type + "Min",
                                         "offensive" + type + "Chance", "offensive" + type);
            }
            return total;
        }

        // One field of one skill record, with the charge multiplier and the XOR check.
        double SklField(SkillPart p, string field)
        {
            if (XorOut(p.Rec, field, p.Slot)) return 0;
            return AttackSkill.Value(p.Rec, field, p.Slot) * AttackSkill.ChargeOf(p, atCharge);
        }

        // A chance of zero means "always": the field is absent on most records.
        //
        // THE PANEL TAKES THE EXPECTATION, A HIT TAKES A ROLL. "25% chance of 99–189 chaos
        // damage" enters the panel as a share (0.25 × the range) — that is how the game
        // prints it too, measured three times. In the queue such damage must come up WHOLE
        // or not at all: otherwise the spread the queue exists for is lost (the user's rule,
        // MECHANICS 31).
        //
        // The presence of a roll decides, and only a hit has one. The roll is per field per
        // source, under the same key as the ranges, so the same field asked twice during one
        // hit gives one answer.
        double Share(double chance, string source, string field)
        {
            if (chance <= 0 || chance >= 100) return 1;
            if (roll == null) return chance / 100.0;      // the panel: the expectation
            return roll.Share(source, field + "|" + HitRoll.Chance) * 100 < chance ? 1 : 0;
        }

        // One damage pool: the numbers of one origin, before the multipliers. The
        // weapon's pool and the skill's own flat damage go through exactly the
        // same conversions and the same multiplier — the one thing that separates
        // them is weaponDamagePct, which only the weapon's takes.
        //
        // The pool itself and the whole conversion machinery now live in `Conversion.cs` —
        // ONE place for the character and for pets alike (the user's request). Only the name
        // `Pool` stays here, to avoid rewriting a couple of dozen call sites.
        class Pool : DamagePool { }

        // ONE HAND'S WEAPON DAMAGE — what the game prints in a skill's tooltip as a separate
        // line per hand: "160% main-hand weapon damage (12127 – 15480)" and "160% off-hand
        // weapon damage (12774 – 15728)".
        //
        // This is the only place where the game names the hands' numbers SEPARATELY, and so
        // the only chance to check the per-hand calculation without depending on how a hit
        // adds the two. It is counted exactly like a panel line, only without the
        // `0.75 × (both)` averaging and without dots: the sum over types of this hand's pool
        // after conversions, each with its own multiplier.
        //
        // What sits here is ONE HUNDRED PER CENT of this hand's weapon damage — the skill's
        // share is not applied. That is right: the tooltip prints its share on each line
        // separately ("160% main hand" is Fire Strike's own share, while the "34%" of its
        // Explosive Strike stands in that modifier's tooltip), and the sum of shares over
        // the whole skill is a different number.
        public readonly double[] HandMin = new double[2], HandMax = new double[2];
        public bool HandsKnown;
        // WHETHER TWO HANDS ACTUALLY STRIKE, as against two hands being SHOWN. The per-hand
        // panel prints the shield's hand always, even when nobody swings it (14d11), so
        // `HandsKnown` cannot answer this. The stats panel needs the answer: it unites the
        // ranges of the hands that STRIKE (14d2), and with a shield there is nothing to
        // unite — one hand swings (129a). Set where the second striking pool is built.
        public bool TwoHandsStrike;
        // How many weapon lines the game's TOOLTIP prints — from the skill's animation.
        // Not to be confused with what the skill hits with: on Ring of Steel they differ.
        public Hands ShownHands = Hands.Both;

        // The same, but per damage type: the character panel shows "life damage — right
        // hand: 5121 – 5746, left hand: 4837 – 5462", and that is the best reference we
        // have — hand AND type at once, without averaging and without the skill's share.
        // A dot's key is "LifeDot" and so on.
        public readonly Dictionary<string, double>[] HandTypeMin =
            { new Dictionary<string, double>(StringComparer.Ordinal),
              new Dictionary<string, double>(StringComparer.Ordinal) };
        public readonly Dictionary<string, double>[] HandTypeMax =
            { new Dictionary<string, double>(StringComparer.Ordinal),
              new Dictionary<string, double>(StringComparer.Ordinal) };
        // THE HIT'S DAMAGE PER HAND, WITH THE SKILL'S SHARE AND ITS OWN DAMAGE, WITHOUT
        // AVERAGING. For the timeline only; the panel does not use it. How it differs from
        // `HandTypeMin` is said where it is filled.
        public readonly Dictionary<string, double>[] HitHandMin =
            { new Dictionary<string, double>(StringComparer.Ordinal),
              new Dictionary<string, double>(StringComparer.Ordinal) };
        public readonly Dictionary<string, double>[] HitHandMax =
            { new Dictionary<string, double>(StringComparer.Ordinal),
              new Dictionary<string, double>(StringComparer.Ordinal) };

        // This hand's dot duration in seconds, as in the records — without
        // "+% duration". The timeline alone needs it (see below).
        public readonly Dictionary<string, double>[] HandTypeSecs =
            { new Dictionary<string, double>(StringComparer.Ordinal),
              new Dictionary<string, double>(StringComparer.Ordinal) };

        // The same, but for the dot THIS SKILL'S HIT applies. It differs from
        // `HandTypeSecs` wherever the skill carries a dot of its own: the duration is then
        // its own, not the weapon's. The companion of `HitHandMin`/`HitHandMax` — read the
        // same three together.
        public readonly Dictionary<string, double>[] HitHandSecs =
            { new Dictionary<string, double>(StringComparer.Ordinal),
              new Dictionary<string, double>(StringComparer.Ordinal) };

        // How the number was arrived at, type by type: what went into the base,
        // what left it in conversion, and what the multiplier was made of. Filled
        // while building, read by --exacttest when the answer disagrees with the
        // game and the question is which step is wrong.
        public readonly List<string> Trace = new List<string>();

        // The dot lines that were merged by the dual-wield rule — only so they can be named
        // in the note under the window.
        readonly List<string> dualDot = new List<string>();

        void Build()
        {
            // Empty hands are a hit too. The character still swings, and every flat bonus
            // of the skills and the devotions lands on it: stripped of all gear, `_Amakata`
            // still shows 322 life damage, 219 aether and both dots in the game's panel. We
            // used to answer "nothing to count" and print nothing at all.
            //
            // What is NOT modelled is the damage of the bare fist itself — the game shows
            // 4 physical there and where that number comes from is not worked out.
            // Everything else is honest.
            if (weapon == null)
                Notes.Add(Lang.T("model.bareHands"));

            // What the character carries: the weapon's own damage plus every flat
            // bonus of the gear. A flat elemental bonus is three equal parts
            // (checked: 27/27/27).
            // offensiveBase{T} is the weapon's own built-in damage of a type other
            // than physical — "55-95 cold damage" at the top of a dagger's
            // tooltip, printed without a bracket because it does not roll. It is a
            // separate family from offensive{T}, and we were not reading it at all:
            // a weapon whose whole base is elemental counted as dealing nothing.
            Pool gear = new Pool();
            foreach (Kind k in Kinds)
            {
                double lo = 0, hi = 0, l, h;
                foreach (string family in new[] { "offensive", "offensiveBonus", "offensiveBase" })
                {
                    PairSum(family + k.Field + "Min", family + k.Field + "Max", out l, out h);
                    lo += l; hi += h;
                }
                // An item's form modifier is a CHARACTER addend, not a skill one (FormFlat).
                double fLo = FormFlat("offensive" + k.Field + "Min");
                double fHi = FormFlat("offensive" + k.Field + "Max");
                lo += fLo; hi += Math.Max(fHi, fLo);
                gear.Lo[k.Field] = lo; gear.Hi[k.Field] = hi;
            }
            // A hand swings only its own weapon: the flat damage of the other one
            // — its base and its own bonuses alike — is not in this swing. The
            // summed bonuses hold every worn item, so it has to come back out.
            //
            //     one-hand swing = the sum of everything − the other hand's flat damage
            //     dual wielding  = 0.75 × (right + left)
            //
            // The three quarters are the average of the dual-wield attack cycle.
            // Checked against the game on `_Amakata`, who wields two of the same
            // sword, on the two lines that go through no conversion and so leave
            // nothing to argue about: pierce 0.75 × 2 × 479 = 718.5 against the
            // game's 718, aether 0.75 × 2 × 369 = 553.5 against 554.
            //
            // AND BEFORE THAT SUBTRACTION, THE UNSPLIT SUM IS KEPT: `whole` holds BOTH
            // weapons at once and the character's own flat damage ONCE. The dots have been
            // counted from it since August (see AddDot, where the reason is the same: in the
            // sum of the two hands the character's damage would stand twice). The DIRECT
            // damage needs it too — for the BLAST share (`wpctBest` below, measured on
            // `_Tapina` 21 October).
            Pool whole = new Pool();
            foreach (Kind k in Kinds)
            { whole.Lo[k.Field] = gear.Lo[k.Field]; whole.Hi[k.Field] = gear.Hi[k.Field]; }
            OtherHand(gear, 1);
            // A SHIELD BECOMES THE SECOND HAND NOT ALWAYS, BUT ONLY FOR A SKILL THAT HITS
            // WITH BOTH. That is measurement, not caution.
            //
            // On a `_Relo` one-hand-and-shield save: Blitz's animation
            // `hero01_sword1h_skill_blitz01.anm` has TWO hit events, and the tooltip prints
            // two weapon lines — "180% main hand" and "180% off-hand". But the ORDINARY
            // ATTACK with a shield goes with the right hand ONLY: the panel prints physical
            // 2159 – 6165, exactly the right hand (per hand: right 2159 – 6165, left
            // 2926 – 3661). There is no `0.75 × (both)` averaging there at all — counting a
            // shield as the second hand EVERYWHERE would be wrong.
            //
            // The mark is the same as for everything else about hands — the skill's
            // ANIMATION (`SkillHands`, MECHANICS 14r). The ordinary attack has no skill, so
            // it never has a shield as its second hand.
            //
            // AND THE SKILL RECORD MUST BE A REAL ONE. `SkillHands.Of` answers "both" for an
            // empty record — a sensible default for DUAL WIELDING, where both hands swing
            // anyway, but for a shield it would be invention: a shield hits only where the
            // skill says so. `--skilldmgtest` caught this: it builds a "multiplier only"
            // skill on a record that does not exist in the database and compares its 100%
            // with the ordinary attack. With a shield the comparison broke — the synthetic
            // skill came out with both hands, the ordinary attack with one.
            Pool second = null;
            DbRecord skillRec = Skill == null || game == null ? null : game.Record(Skill.Dbr);
            // A SHIELD ALSO BECOMES A HAND WHEN THE SKILL HITS WITH IT ALONE.
            // Added on the shield-bearing `_222`. Before that only `Hands.Both` stood here,
            // which was right exactly as far as the measurement went: at the time the only
            // case at hand was Blitz, which hits with both.
            //
            // `_222` brought the second case. Forcewave and Aegis of Menhir print ONE weapon
            // line, and it is "OFF-HAND weapon damage" (345% and 441%). The animation says
            // the same by itself:
            //     --handdump shieldhammer1  →  one-handed (and with a shield): LEFT — off-hand
            // So we HAD the mark and it was right, but no second-hand pool was built for it,
            // and `wpctTwo` was zeroed by the `second == null` condition below. The whole
            // share fell on the FIRST hand, that is on the sword instead of the shield:
            // Forcewave's physical base came out 245–283 (sword 106–144 + 89 + 50) instead
            // of 315 (shield 176 + 89 + 50).
            //
            // Rule 14d7 №2 does not change from this but gets sharper: a shield becomes a
            // hand for a skill that HITS WITH IT — with both hands or with it alone. The
            // ordinary attack still goes with the right hand only: it has no skill at all.
            Hands skillHands = skillRec == null ? Hands.Both : SkillHands.Of(game, skillRec, c);
            if (DualWield() || (ShieldSecond() && skillRec != null &&
                                (skillHands == Hands.Both || skillHands == Hands.Off)))
            {
                second = new Pool();
                TwoHandsStrike = true;
                foreach (Kind k in Kinds)
                {
                    second.Lo[k.Field] = gear.Lo[k.Field]; second.Hi[k.Field] = gear.Hi[k.Field];
                }
                // The second hand is the same pool with the roles swapped: put the
                // first weapon's flat damage back and take the second one's out.
                OtherHand(second, 0);
                OtherHand(second, 1, +1);
            }
            // The maximum can only be clamped to the minimum once the hands are
            // settled: doing it while building leaves the two ends of a range
            // paired with different sources.
            foreach (Kind k in Kinds)
            {
                gear.Hi[k.Field] = Math.Max(gear.Hi[k.Field], gear.Lo[k.Field]);
                whole.Hi[k.Field] = Math.Max(whole.Hi[k.Field], whole.Lo[k.Field]);
                if (second != null) second.Hi[k.Field] = Math.Max(second.Hi[k.Field], second.Lo[k.Field]);
            }

            double elemLo, elemHi;
            ElementalPair(out elemLo, out elemHi);
            Elemental(gear, elemLo, elemHi);
            // The unsplit sum takes the same flat elemental bonus: it is the character's,
            // and in `whole` the character stands exactly once (declared above, before the
            // other hand was subtracted). Its DOT half is filled just below.
            Elemental(whole, elemLo, elemHi);
            foreach (KeyValuePair<string, string> kv in DotName)
            { GearDot(gear, kv.Key); GearDot(whole, kv.Key); }
            // And this is the dot THE HAND ITSELF carries: the weapon's record with its
            // prefix and suffix AND THE COMPONENT SCREWED INTO IT with its bonus. A dot's
            // "per hit" line decides by this whether to be averaged (ByWeaponDot).
            //
            // THE COMPONENT WAS INCLUDED LATER; before that it was excluded on a measurement
            // that turned out to be READING THE WRONG PANEL (MECHANICS 14v3). The augment
            // and the item modifier still do not count: there is no measurement for them.
            Pool weaponOwn = new Pool();
            OtherHandDot(weaponOwn, 0, +1, 5);
            OtherHandDot(weaponOwn, 1, +1, 5);
            // A dot belongs to a hand too. `GearDot` takes the character's sum, which holds
            // BOTH weapons, and it has to be split by hands exactly as the flat direct
            // damage is.
            OtherHandDot(gear, 1, -1);
            if (second != null)
            {
                // The swap above already moved this hand's elemental across; here
                // only the character's own flat elemental and the dots are added,
                // the same way as for the first hand.
                Elemental(second, elemLo, elemHi);
                foreach (KeyValuePair<string, string> kv in DotName) GearDot(second, kv.Key);
                // This hand's dot: the character's sum minus the FIRST weapon's dot. The
                // direct damage above has to be recovered differently (the pool was copied
                // with the second hand already taken out), while the dot is filled afresh,
                // so here one subtraction is enough.
                OtherHandDot(second, 0, -1);
            }

            // The skill's own flat damage. It is a pool of its own only because
            // weaponDamagePct does not reach it — everything else it shares with
            // the weapon's.
            // A skill has TWO own pools, split along the same line as the weapon share: what
            // flies with the shot, and what happens once at the impact point. With dual
            // wielding the first goes from each hand and is averaged by the cycle just like
            // weapon damage (that is, ×1.5), the second goes as it is. See OwnShare.
            Pool own = new Pool(), ownOne = new Pool();
            if (Skill != null)
            {
                foreach (Kind k in Kinds)
                {
                    own.Lo[k.Field] = SklFlat(k.Field, "Min");
                    own.Hi[k.Field] = Math.Max(own.Lo[k.Field], SklFlat(k.Field, "Max"));
                    ownOne.Lo[k.Field] = SklFlat(k.Field, "Min", true);
                    ownOne.Hi[k.Field] = Math.Max(ownOne.Lo[k.Field], SklFlat(k.Field, "Max", true));
                }
                Elemental(own, SklFlat("Elemental", "Min"), SklFlat("Elemental", "Max"));
                Elemental(ownOne, SklFlat("Elemental", "Min", true), SklFlat("Elemental", "Max", true));
                foreach (KeyValuePair<string, string> kv in DotName)
                { SkillDot(own, kv.Key); SkillDot(ownOne, kv.Key, true); }
            }

            // The attributes are printed BROKEN DOWN: the save's own, the flat additions of
            // gear and skills, the percentage. They multiply EVERY line at once
            // (cunning/245, spirit/215, /215 and /200 for dots), so a shortfall of a few
            // points of spirit tints the whole report a little and, without the breakdown,
            // is indistinguishable from an error in a type percentage.
            Trace.Add("attributes: cunning " + Fmt(cunning) +
                      " (own " + Fmt(c.Cunning) + " + gear " + Fmt(Sum("characterDexterity")) +
                      ", +" + Fmt(Sum("characterDexterityModifier")) + "%)" +
                      ", spirit " + Fmt(spirit) +
                      " (own " + Fmt(c.Spirit) + " + gear " + Fmt(Sum("characterIntelligence")) +
                      ", +" + Fmt(Sum("characterIntelligenceModifier")) + "%)");
            // The skill's parts by name, with what each carries. Without this the trace was
            // silent about the most important thing: a lost SET-bonus modifier and an
            // uncounted TRANSMUTER had to be hunted through the game's tooltips, although
            // both sit right here. A silent trace costs more than any find.
            if (Skill != null)
            {
                foreach (SkillPart p in Skill.Parts)
                {
                    if (p.Temporary && !withTemporary) continue;
                    Trace.Add("skill part: " + p.Who + (p.WeaponOneHand ? "  [hits with one hand]" : "") +
                              "   " + p.Rec.Path);
                    List<Move> hers =
                        new List<Move>();
                    Conversion.Read(p.Rec, null, hers);
                    foreach (Move m in hers)
                        Trace.Add("    its conversion " + m.From + " → " + m.To +
                                  "  " + Fmt(m.Percent) + "%   (applies FIRST)");
                    double w = AttackSkill.Value(p.Rec, "weaponDamagePct", p.Slot);
                    if (Math.Abs(w) > 0.0001) Trace.Add("    its weapon damage share " + Fmt(w) + "%");
                }
                foreach (string left in Skill.NotCounted) Trace.Add("NOT ADDED: " + left);
            }
            // WHERE A CONVERSION COMES FROM is in the trace. Without the source the user's
            // question about ordering (gear apart, auras apart) cannot be answered: `_Itaan`
            // has two fire→poison conversions, 25.2% and 50%, and the whole question is
            // whether they belong to one rank or to two.
            foreach (Move m in Conversions())
                Trace.Add("character conversion " + m.From + " → " + m.To + "  " + Fmt(m.Percent) + "%" +
                          (string.IsNullOrEmpty(m.Source) ? "" : "   ← " + m.Source));
            foreach (Kind k in Kinds)
                if (gear.Hi[k.Field] > 0 || own.Hi[k.Field] > 0)
                    Trace.Add("base " + k.Field + ": weapon " + Exact(gear.Lo[k.Field]) + "–" +
                              Exact(gear.Hi[k.Field]) + ", skill " + Exact(own.Lo[k.Field]) + "–" +
                              Exact(own.Hi[k.Field]) +
                              (gear.DotHi.ContainsKey(k.Field) &&
                               (own.DotHi[k.Field] > 0 || gear.DotHi[k.Field] > 0)
                                   ? "   dot: weapon " + Exact(gear.DotLo[k.Field]) + "–" +
                                     Exact(gear.DotHi[k.Field]) + ", skill " + Exact(own.DotLo[k.Field]) +
                                     "–" + Exact(own.DotHi[k.Field]) : ""));

            Retaliate();
            Sources(gear, own, ownOne);

            // A dot WITHOUT direct damage of the same root used not to be printed at all:
            // the "base X" line appears only when the type has direct damage. `_Itaan`'s
            // Righteous Fervor has exactly such an own dot — 199 internal trauma with the
            // weapon carrying no physical dot at all — and it was invisible.
            foreach (KeyValuePair<string, string> kv in DotName)
                if (gear.DotHi[kv.Key] > 0 || own.DotHi[kv.Key] > 0 || ownOne.DotHi[kv.Key] > 0)
                    Trace.Add("dot base " + kv.Key + ": weapon " + Exact(gear.DotLo[kv.Key]) + "–" +
                              Exact(gear.DotHi[kv.Key]) + ", skill " + Exact(own.DotLo[kv.Key]) + "–" +
                              Exact(own.DotHi[kv.Key]) + ", one-hand skill " +
                              Exact(ownOne.DotLo[kv.Key]) + "–" + Exact(ownOne.DotHi[kv.Key]));

            // A hand's pool carries ITS OWN armour piercing: it sits on the weapon itself,
            // not on the character (see PierceRatio). `gear` is hand 0, `second` is hand 1.
            //
            // THE PER-HAND PANEL ALWAYS SHOWS THE SHIELD HAND — even when the shield is not
            // swung. Measured on `_222`, fire damage:
            //     right hand 3862 – 4449   left hand 5564 – 5834
            // The right one is exactly the ordinary attack, and it matches. The left is
            // printed even for the ordinary attack, which goes with the right hand ALONE
            // (MECHANICS 14d7, rule 2).
            //
            // So the hand pool FOR DISPLAY is built apart from `second`: `second` takes part
            // in the hit (the `wpctTwo` and `wpctBest` shares, the 0.75 averaging) and must
            // not be let in where the shield is not swung, or the ordinary attack breaks
            // again. Display needs it always.
            //
            // It is built HERE, before the conversions, and converted along with everything
            // else: the first attempt assembled it AFTER the conversions, from an already
            // converted `gear`, and converted it all a second time.
            Pool shown = second;
            if (shown == null && ShieldSecond())
            {
                shown = new Pool();
                foreach (Kind kk in Kinds)
                { shown.Lo[kk.Field] = gear.Lo[kk.Field]; shown.Hi[kk.Field] = gear.Hi[kk.Field]; }
                OtherHand(shown, 0);
                OtherHand(shown, 1, +1);
                foreach (Kind kk in Kinds)
                    shown.Hi[kk.Field] = Math.Max(shown.Hi[kk.Field], shown.Lo[kk.Field]);
                // ...AND IT NEEDS THE DOTS TOO. Only `Lo`/`Hi` were copied, that is the
                // direct damage, and the shield hand's dot pool stayed empty — so the
                // per-hand panel printed ZERO for every dot of that hand.
                //
                // It is filled the same way as `second` twenty lines above: the whole
                // character sum (`GearDot`) minus the FIRST weapon's own dot.
                //
                // MEASURED — `_Acid Vines` (the sword Suffering + the shield Bramblevine),
                // per-hand panel, poison:
                //
                //   hand 1 (Suffering, carries no dot of its own)  188   game 6041
                //   hand 2 (the shield, a component worth 4)       192   game 6170
                //   192 × 32.1338 = 6170   ← the multiplier with the PLAN correction
                //
                // We had zero in the second hand against the game's 6170 — what was wrong
                // was not a number but the construction.
                foreach (KeyValuePair<string, string> kv in DotName) GearDot(shown, kv.Key);
                OtherHandDot(shown, 0, -1);
                Convert(shown, 1);
            }
            // The per-hand panel knows the second hand if its pool was built even for
            // display alone.
            HandsKnown = shown != null;
            Convert(gear, second != null ? 0 : -1);
            if (second != null) Convert(second, 1);
            // HOW MUCH OWN DOT EACH TYPE HAD BEFORE THE CONVERSIONS — `SklCarried` needs the
            // snapshot: a percentage that moved travels with ITS OWN source, and to add them
            // correctly one must know how much came from each. Analysis and numbers —
            // MECHANICS 25d.
            foreach (Kind kk in Kinds)
                if (own.DotHi.ContainsKey(kk.Field))
                    dotBeforeOwn[kk.Field] = Math.Max(own.DotHi[kk.Field], ownOne.DotHi[kk.Field]);
            foreach (Kind kk in Kinds)
                beforeOwn[kk.Field] = Math.Max(own.Hi[kk.Field], ownOne.Hi[kk.Field]);
            // ARMOUR PIERCING FOR THE SKILL'S OWN DAMAGE IS TAKEN FROM THE HAND THAT
            // STRIKES — not from the character's sum, which is what `Convert(pool)` means.
            //
            // The field sits on the weapon (measured on a damage-over-time case for
            // the hand pools), and a blow struck with one hand carries that hand's piercing
            // and no other. We were taking the sum over all the gear, so a second weapon's
            // piercing turned the skill's own physical into pierce even when the skill
            // never swung that weapon.
            //
            // MEASURED — `_BloodAxe`, Leap, 21 October. The axe in hand 1 has no piercing,
            // the Mythical Bloodsong in hand 2 has 100%, and Leap strikes with ONE hand:
            //
            //   own physical 27, minus the axe component's 10% into acid = 24.3
            //     ours      0 (it had all gone into pierce)
            //     the game  24.3 × 8.9194 = 216.7 on top of the weapon part
            //               1372.2 + 216.7 = 1588.9   game 1589
            //               1965.6 + 216.7 = 2182.3   game 2182
            //   own pierce 42, minus the character's pierce → life 24.8% = 31.58
            //     ours      55.876 (= 31.58 + the 24.3 that had leaked in from physical)
            //     the game  612.4 + 31.58 × 13.9194 = 1052.0   game 1052
            //
            // FIVE numbers of one skill, two of them opposite in sign — physical short by
            // 13.7% while pierce stood 32.1% high (rule "what to do" 29: the opposite sign
            // decides at once, nothing is being added or dropped, it is in the wrong type).
            //
            // WHAT THIS MEASUREMENT DOES NOT TELL APART, and it must be said: here the
            // striking hand's piercing is ZERO, so "the striking hand's" and "none at all
            // for a skill's own damage" give the same answer. A character with piercing on
            // the hand that swings and a skill with its own physical would tell them apart;
            // we have none. The hand is chosen because the field is the hand's everywhere
            // else (149), not because it was measured against the other reading.
            Convert(own, OwnHand(second));
            Convert(ownOne, OwnHand(second));
            // The unsplit sum goes through the same conversions as the hand pools: otherwise,
            // for a character with a dot conversion, the merge would take it from the
            // PRE-CONVERSION type. The "the weapon carries it" mark goes the same way: the
            // type a dot moved into is the weapon's exactly as far as its root was.
            if (second != null) { Convert(whole); Convert(weaponOwn); }

            // A plain attack is a hundred per cent of the weapon by definition. A
            // skill takes the share its record names — and a skill that names none
            // takes **nothing**: it is a spell, and the weapon has no part in it.
            // Reading a missing weaponDamagePct as "a hundred" would hand every
            // spell the whole weapon on top of its own damage.
            double wpct = Skill == null ? 1 : Skill.Sum("weaponDamagePct", withTemporary) / 100.0;
            // The weapon share splits in two: the part that goes on the SWING (with dual
            // wielding that is `0.75 × (both hands)`) and the part that goes on ONE HAND.
            //
            // The blast at the impact point happens once per shot, however many barrels
            // fired, so it takes one weapon's damage, not the average of both. Measured on
            // `_Iman`'s Fire Strike, on lines where the skill has no own damage and there is
            // nothing to argue about:
            //
            //   pierce     1.60 × 103.518 + 0.34 × 69     = 189.09 × 9.3414 = 1766   game 1766
            //   cold       1.60 × 16.524  + 0.34 × 11.846 + 2.22 own        =  675   game 675
            //   cold (top) 1.60 × 25.010  + 0.34 × 17.513 + 2.22            =  995   game 995
            //
            // Three equations in two unknowns solve uniquely and give 1.591 and 0.352
            // against the recorded 1.60 and 0.34. **The averaged hand does not fit here:**
            // it is proportional to the swing, which makes the system degenerate; the second
            // hand gives a negative share. So it is hand 1 specifically.
            //
            // With a single weapon and with a two-hander both shares land on the same pool
            // and everything reduces to the old sum — the other stands do not move.
            //
            // WHICH HAND IT IS IS DECIDED BY A MARK OF THE GAME: the hit events in the
            // skill's animation (`SkillHands`, MECHANICS 14r). The mark is set in
            // AttackSkills and arrives here ready. Hand 1 is measured on `_Iman`'s Fire
            // Strike (the three equations above), hand 2 on Amarasta's Blade Burst and
            // Aspect of Beronath, where the game itself writes "off-hand weapon". The old
            // reading "the weakest hand is taken" is dropped: it fitted one measurement and
            // disagreed with the second.
            double wpctOne = Skill == null ? 0 : Skill.SumHand("weaponDamagePct", withTemporary, true) / 100.0;
            wpct -= wpctOne;
            // The part of the "one hand" share that goes on the SECOND hand (see
            // SkillPart.SecondHand and AttackSkills, where the mark is set). With a single
            // weapon there is no second hand and both shares land on the same pool.
            double wpctTwo = Skill == null || second == null
                           ? 0 : Skill.SumSecondHand("weaponDamagePct", withTemporary) / 100.0;
            wpctOne -= wpctTwo;
            // The "one hand" share whose hand is NOT NAMED: the BEST hand is taken PER
            // DAMAGE TYPE SEPARATELY (see SkillPart.BestHand, MECHANICS 14v5).
            //
            // Measured on `_Iman`'s Explosive Strike, the contribution taken by subtracting
            // two states (with the part refunded and with it). Nine numbers, and they demand
            // DIFFERENT hands for different types:
            //
            //             game's part  hand 1    hand 2    BEST
            //   pierce         219      219 ✔    219 ✔     219 ✔   (hands equal)
            //   cold           129      129 ✔    117       129 ✔   (best is the first)
            //   cold (top)     168      168 ✔    157       168 ✔
            //   lightning     2193     2194 ✔   1424      2194 ✔   (best is the first)
            //   lightning (t) 2637     2637 ✔   1855      2637 ✔
            //   life            69       50      69 ✔       69 ✔   (best is the SECOND)
            //   fire          2903     2057     2951      2951     (best is the second,
            //   fire (top)    3329     2400     3388      3388      off by 1.6% — that is
            //                                                       fire's own remainder,
            //                                                       see below)
            //
            // No NAMED hand can do this: cold and lightning demand the first, life and fire
            // the second. "Both hands" (`0.75 × the sum`) cannot either: it gives pierce
            // 1876 against the game's 1766 and cold 708 against 675, breaking two lines that
            // matched EXACTLY. Both experiments were run and both rejected by number.
            //
            // Why the BEST and not "some hand": the blast happens ONCE per shot, but either
            // hand may fire — the game's tooltip prints both weapon lines with the same 34%
            // share. One blast goes into the panel, and per type the game takes the larger
            // of the two.
            //
            // THE FIRE REMAINDER IS NOT CLOSED BY THIS AND DOES NOT BELONG TO THIS RULE:
            // `_Iman`'s fire does not match even WITHOUT the blast (18942 against the game's
            // 19853, −4.6%), so it must be looked for in Fire Strike's own damage, not in
            // the blast's hand.
            double wpctBest = Skill == null || second == null
                            ? 0 : Skill.SumBestHand("weaponDamagePct", withTemporary) / 100.0;
            wpctOne -= wpctBest;

            // The charge multiplier — see AttackSkill.ChargeOf. It lands on EVERYTHING the
            // record carrying it gives: on its weapon share (here), on its flat damage and
            // dots (`SklFlat`, `SkillDot`) and on its percentages (`Skl`). The same skill's
            // modifier records are separate records and carry no charge.
            //
            // Measured on `_Iminika`'s Runic Bolts (MECHANICS 19z). The ratio of the panel's
            // two ends decides it: the skill's own flat dot is 100 per second, and the ratio
            // gives 115.05 — that is 100 × 1.15, not 100. All three of its dots match to the
            // unit after the fix, and the skill's percentages are confirmed independently by
            // screenshots of Amatok's Breath at different charges (MECHANICS 19e): the gain
            // there is exactly `1.8 × the charge share` on every line, where 1.8 is Runic
            // Bolts' own `offensiveElementalModifier 180`.
            //
            // `_Jien`'s Savagery does not contradict this: at its current level 8
            // `skillChargeLevel` is six, the sixth step of the list is 100%, so it has NO
            // multiplier at all right now and can say nothing about its own damage.
            double charge = Skill == null ? 1 : Skill.ChargeMultiplier(withTemporary);
            if (Math.Abs(charge - 1) > 0.0001)
            {
                Trace.Add("charge multiplier " + Fmt(charge * 100) + "% (the last step of " +
                          "skillChargeMultipliers) — it lands on everything this record gives: " +
                          "the weapon damage share, its own flat damage, the dots and the percentages");
                wpct *= charge; wpctOne *= charge;
            }

            // A skill's own flat damage also goes from each hand with dual wielding and is
            // averaged by the cycle — that is, it enters the hit ONE AND A HALF times, not
            // once. Measured on `_Iman`'s Sacred Strike: it has no modifiers at all, both
            // hands match within 0.2%, and yet the panel shows 46240 – 60101 against our
            // 44043 – 56696. The whole difference falls on its own elemental damage, and
            // falls EQUALLY at both ends:
            //
            //   short by 2266 with own 4477  →  1.506
            //   short by 3474 with own 6891  →  1.504
            //
            // The 1.5 is `2 × 0.75`: the same cycle averaging as for the weapon. With a
            // single weapon the multiplier is one, so the other stands do not move.
            //
            // BUT IT IS AVERAGED ONLY FOR SKILLS THAT HIT WITH A WEAPON. Measured on
            // `_Jien`'s Devouring Swarm — it has no weapon share at all, and both panel
            // lines are purely its own:
            //
            //   life damage  150 × (1 + 487/215 + 1.50 + 0.30) = 760    game 760
            //   bleeding     134 × (1 + 798/215 + 7.35 + 0.30) = 1656.5 game 1656
            //
            // Both multipliers are built from numbers we already had (the same bleeding
            // multiplier gives 779 for the ordinary attack against the game's 779), so this
            // is a prediction, not a fit. With 1.5 it would be 1140 against the game's 760.
            //
            // The sense is plain: 1.5 is the average over a cycle of TWO SWINGS, and a spell
            // does not swing at all, however many weapons are held. The mark is whether the
            // skill has a weapon share. Shadow Strike, with a share of 524%, confirms the
            // rule's other half: 14 559 of its 36 786 cold is its own, and with 1.5 it
            // matches within 0.1%, while with one it would give 31 988 against 36 786.
            //
            // THERE IS NO STANDING CHECK UNDER THIS HALF OF THE RULE, and that must be
            // known: Devouring Swarm was refunded after that measurement and is no longer in
            // the save, and no other skill without a weapon share was found on a dual-wield
            // character in the stands. The numbers are written down in screen-jien-eq.tsv;
            // the check will appear as soon as the points go back in.
            double ownShare = second != null && Math.Abs(wpct + wpctOne) > 0.0001 ? 2 * DualShare : 1;

            // Both hands separately — see HandMin/HandMax. The game's bracket includes this
            // hand's DOT as well: without it `_Iman`'s anchors are off by exactly the burn
            // (both hands, both ends — four numbers), and with it they match within 0.2%.
            // The dot is taken as it is, per second: the panel prints it the same way.
            // (the value is already set above, together with the display pool: the per-hand
            //  panel knows the second hand even when the shield is not swung)
            //
            // HOW MANY HANDS THE GAME SHOWS is not the same as what it hits with, and on one
            // skill the two parted ways. Ring of Steel's tooltip prints TWO weapon lines
            // (the user took it with two one-handers), while the damage is measured as ONE
            // hand — twice, on `_Jien` and `_Hamarina`. It also carries
            // `leftHandWeaponDisplay = 1`, which the game's template says is about the
            // TOOLTIP.
            //
            // So the ANIMATION is taken here, always and without exceptions: it answers for
            // what is printed. The calculation (`OneHand`/`SecondHand` in AttackSkills) for
            // the `Skill_AttackRadius…` and `SkillSecondary_…` classes still follows the
            // measurement. The two parting ways is not our liberty but what the game does.
            ShownHands = Skill == null || game == null || c == null
                       ? Hands.Both
                       : SkillHands.Of(game, game.Record(Skill.Dbr), c);
            foreach (Kind k in Kinds)
            {
                // THE PER-HAND PANEL APPLIES A SKILL PERCENTAGE THAT MOVED TO THE WHOLE
                // POOL, WHILE THE HIT LINE APPLIES IT ONLY TO WHAT ARRIVED. The last
                // argument is exactly that difference; the analysis with numbers is
                // MECHANICS 25v.
                //
                // MEASURED — `_Jien`, Ring of Steel. One skill, one character, four game
                // numbers on each side, and the two sides demand DIFFERENT things:
                //
                //   hit line, cold        game 58334 – 70531
                //       arrived only       58334 – 70531  EXACT
                //       whole pool         58911 – 71384  (+1.0% / +1.2%)
                //   per hand (370% weapon) game 31674 and 31943
                //       arrived only       31209 and 31452  (−1.5%)
                //       whole pool         31653 and 31903  (−0.1%)  exact within rounding
                //
                // That these two panels count DIFFERENT things is not news (MECHANICS 14d1),
                // and this is their third disagreement: the first is that a dot's inflow
                // enters the per-hand panel only if the hand itself carries the dot
                // (MECHANICS 14v3), the second is the 0.75 averaging. This is the same, only
                // about a percentage.
                bool onWholePool = !ProbeArrivedOnly;   // "a percentage that moved — on the whole pool"
                double m = Multiplier(k, false, true, true, onWholePool),
                       md = Multiplier(k, true, true, true, onWholePool);
                double mIn = Multiplier(k, false, false), mdIn = Multiplier(k, true, false);
                double mSk = Multiplier(k, false, true, false), mdSk = Multiplier(k, true, true, false);
                for (int hand = 0; hand < 2; hand++)
                {
                    Pool p = hand == 0 ? gear : shown;
                    if (p == null) continue;
                    // The arrived part's multiplier is THIS pool's: a percentage that moved
                    // travels with its own source, and the hands' sources differ (analysis
                    // at `MIn`).
                    double hLo = Both(p.Lo[k.Field], In(p.InLo, k.Field), In(p.CameLo, k.Field),
                                      m, MIn(mIn, k, false, false, p, null), mSk);
                    double hHi = Both(p.Hi[k.Field], In(p.InHi, k.Field), In(p.CameHi, k.Field),
                                      m, MIn(mIn, k, false, true, p, null), mSk);
                    HandMin[hand] += hLo;
                    HandMax[hand] += hHi;
                    HandTypeMin[hand][k.Field] = hLo;
                    HandTypeMax[hand][k.Field] = hHi;
                    if (!p.DotLo.ContainsKey(k.Field)) continue;
                    // A DOT'S INFLOW ENTERS THE PER-HAND PANEL ONLY IF THE HAND ITSELF
                    // CARRIES A DOT OF THAT TYPE (the weapon's record or a screwed-in
                    // component).
                    //
                    // Measured on THREE characters, the third of them made for this question
                    // on purpose:
                    //
                    //   `_Itaan`  poison: the hand carries 4 (the Envenomed Weapons
                    //             component)
                    //             per hand 7444 = (219 own + 7.18 inflow) × 32.9129
                    //             THE INFLOW ENTERS
                    //   `_Jien`   freeze: the hand carries nothing
                    //             per hand 312 = 12 own × 26.025, the inflow 4.75 did NOT
                    //             enter
                    //   `_111`    freeze: no own dot at all, the whole dot is inflow
                    //             per hand 0 against 8 in the hit line — the inflow did NOT
                    //             enter
                    //
                    // The third measurement decides it: its cold dot consists of inflow
                    // ALONE (8 internal trauma from a medal, 20% into cold by two Chilled
                    // Steels), and the per-hand panel showed ZERO while the hit line showed
                    // 8. Both numbers were named before the screenshot.
                    //
                    // The "the hand carries the dot" mark here is the very one that decides
                    // the 0.75 averaging in MECHANICS 14v3 (`ByWeaponDot`), and that is no
                    // coincidence: both panels ask the dot the same thing — whose it is.
                    //
                    // FOURTH MEASUREMENT: BOTH HANDS MUST CARRY IT, NOT JUST ONE.
                    //
                    //   `_Acid Vines`  poison: the dot is carried by the SECOND hand only
                    //                  (a shield with a component worth 4), the first
                    //                  carries nothing
                    //                  per hand 6041 = 188 × 32.1338 and 6170 = 192 × …
                    //                  the inflow 22 entered NEITHER hand
                    //
                    // Before that the mark was per character ("at least one hand carries
                    // it"), which gave 6748 and 6877 — 11.7% above the game. `_Acid Vines`
                    // is exactly what tells the two readings apart: on `_Itaan` BOTH hands
                    // carry the dot and the inflow enters; here one does, and the inflow
                    // enters neither.
                    //
                    // WHAT THIS MEASUREMENT DOES NOT DISTINGUISH, said honestly: the second
                    // hand here is a SHIELD, and the same could be explained otherwise — "a
                    // shield never counts as carrying" (then no hand carries and the inflow
                    // stays out by the old rule). Both readings give exactly the same three
                    // numbers. "Both hands" is taken because it introduces no new notion and
                    // rests on the already measured "only one hand carries the dot"
                    // (MECHANICS 14d6, the `_Hamarina` dot save). A character with TWO
                    // WEAPONS where one carries the dot and that dot has a conversion inflow
                    // would tell them apart; we have no such stand.
                    //
                    // This does not touch the HIT line: `oneWeaponDot` stands there and
                    // decides about the quarter, not about the inflow. `_Acid Vines`' hit
                    // line 6748 matches the game TO THE UNIT both before and after the fix.
                    //
                    // We ask `shown`, not `second`: there are two hands here exactly when a
                    // second DISPLAY pool exists. For the ordinary attack with a shield
                    // `second` is empty (a shield becomes a hand only for a skill that hits
                    // with it), yet the per-hand panel prints the shield anyway — and the
                    // argument is about that panel.
                    bool bothHandsCarry = shown == null ||
                                          (OtherHandDotOf(k.Field, 0) > 0.0001 &&
                                           OtherHandDotOf(k.Field, 1) > 0.0001);
                    bool dotByWeapon = ByWeaponDot(k, weaponOwn) && bothHandsCarry;
                    double mdInLo = MIn(mdIn, k, true, false, p, null);
                    double mdInHi = MIn(mdIn, k, true, true, p, null);
                    double dotLo = dotByWeapon
                                 ? Both(p.DotLo[k.Field], In(p.InDotLo, k.Field),
                                        In(p.CameDotLo, k.Field), md, mdInLo, mdSk)
                                 : Both(Math.Max(0, p.DotLo[k.Field] - In(p.InDotLo, k.Field)),
                                        0, In(p.CameDotLo, k.Field), md, mdInLo, mdSk);
                    double dotHi = dotByWeapon
                                 ? Both(p.DotHi[k.Field], In(p.InDotHi, k.Field),
                                        In(p.CameDotHi, k.Field), md, mdInHi, mdSk)
                                 : Both(Math.Max(0, p.DotHi[k.Field] - In(p.InDotHi, k.Field)),
                                        0, In(p.CameDotHi, k.Field), md, mdInHi, mdSk);
                    // THE WHOLE-HAND BRACKET AND THE PER-HAND PANEL ROW ARE NOT THE SAME
                    // NUMBER (15 October, MECHANICS 147b/149).
                    //
                    // `HandMin` is what a SKILL TOOLTIP prints — "112% main-hand weapon damage
                    // (7851 – 9079)" — and a tooltip is a WINDOW: a window counts a dot's
                    // conversion inflow (MECHANICS 145). `HandTypeMin` is the row of the
                    // PER-HAND PANEL, and there the inflow enters only if the hand itself
                    // carries the dot (the fourth boundary of 14d1, measured just above).
                    //
                    // Until today both took `dotLo`, that is, the panel's rule stood inside the
                    // tooltip's bracket as well.
                    //
                    // THE TWO MARKS DIFFER BY EXACTLY ONE CONDITION, AND TWO CHARACTERS WITH
                    // OPPOSITE SIGNS SAY WHICH:
                    //
                    //   bracket (tooltip)  the inflow enters if THE WEAPON ITSELF carries this
                    //                      dot — `ByWeaponDot`, and nothing about the other hand
                    //   panel row          the same AND both hands carry it (`bothHandsCarry`,
                    //                      the `_Acid Vines` measurement above)
                    //
                    //   `_RuneTask`  Righteous Fervor, scepter + shield. The scepter's PREFIX
                    //                carries 27 burn, so the weapon does carry the dot; the
                    //                shield does not, so the panel row drops the inflow (928,
                    //                measured). The tooltip's bracket needs it: ours was
                    //                6956 – 8183 against 7851 – 9079, SHORT by a flat 799–800
                    //                at both ends — and 800 is what the inflow contributes.
                    //                With the inflow in: 7849 – 9076.
                    //   `_Jien`      two axes, and the hand carries NO freeze at all
                    //                (`ByWeaponDot` false, MECHANICS 14d1). Here the bracket
                    //                must drop the inflow too: with it in, 32119 – 42774 against
                    //                the game's 31674 – 42116, OVER by 1.4%.
                    //
                    // The opposite sign is the whole argument (rule 29): one shared rule cannot
                    // be short on one character and long on the other, and the only mark that
                    // separates them is "does the weapon itself carry this dot".
                    double dotWinLo = BreakHandDotWindow || !ByWeaponDot(k, weaponOwn)
                                    ? dotLo
                                    : Both(p.DotLo[k.Field], In(p.InDotLo, k.Field),
                                           In(p.CameDotLo, k.Field), md, mdInLo, mdSk);
                    double dotWinHi = BreakHandDotWindow || !ByWeaponDot(k, weaponOwn)
                                    ? dotHi
                                    : Both(p.DotHi[k.Field], In(p.InDotHi, k.Field),
                                           In(p.CameDotHi, k.Field), md, mdInHi, mdSk);
                    HandMin[hand] += dotWinLo;
                    HandMax[hand] += dotWinHi;
                    HandTypeMin[hand][k.Field + "Dot"] = dotLo;
                    HandTypeMax[hand][k.Field + "Dot"] = dotHi;
                    // THIS HAND'S DOT DURATION as it stands in the records — without
                    // "+% duration". The panel does not need it at all (a dot is printed
                    // per second there), the timeline does: it places ticks in time and must
                    // know when the dot ends.
                    HandTypeSecs[hand][k.Field + "Dot"] = p.DotSecs[k.Field];

                    // THE DOT THIS HAND'S HIT APPLIES — with the skill's share.
                    //
                    // The user's measurement, and it is unambiguous: `_777`'s ordinary
                    // attack applies 398 burn per second, and Shadow Strike with a weapon
                    // share of 125% applies **497**. The ratio 497/398 = 1.249, so the
                    // skill's share multiplies the weapon's DOT as well, not only the direct
                    // damage. Before that we had no dot from a skill at all.
                    //
                    // The skill's own dot is added here too: it flies with each hand, like
                    // its own direct damage (the Righteous Fervor measurement,
                    // MECHANICS 14d9).
                    double dotShare = wpct + (hand == 0 ? wpctOne : wpctTwo);
                    double ownDotLo = Both(own.DotLo[k.Field], In(own.InDotLo, k.Field),
                                           In(own.CameDotLo, k.Field), md,
                                           MIn(mdIn, k, true, false, own, null), mdSk) +
                                      Both(ownOne.DotLo[k.Field], In(ownOne.InDotLo, k.Field),
                                           In(ownOne.CameDotLo, k.Field), md,
                                           MIn(mdIn, k, true, false, ownOne, null), mdSk);
                    double ownDotHi = Both(own.DotHi[k.Field], In(own.InDotHi, k.Field),
                                           In(own.CameDotHi, k.Field), md,
                                           MIn(mdIn, k, true, true, own, null), mdSk) +
                                      Both(ownOne.DotHi[k.Field], In(ownOne.InDotHi, k.Field),
                                           In(ownOne.CameDotHi, k.Field), md,
                                           MIn(mdIn, k, true, true, ownOne, null), mdSk);
                    HitHandMin[hand][k.Field + "Dot"] = dotLo * dotShare + ownDotLo;
                    HitHandMax[hand][k.Field + "Dot"] = dotHi * dotShare + ownDotHi;
                    // THIS HIT'S DOT DURATION IS THE SKILL'S OWN, NOT THE GEAR'S.
                    //
                    // `HandTypeSecs` is the duration of the dot THE HAND'S WEAPON carries,
                    // which is foreign to a skill's hit. Night's Chill carries
                    // `offensiveSlowColdDurationMin 2` in its record while `_777`'s weapon
                    // deals no cold at all — the duration came out zero and was replaced by
                    // one. It surfaced as an aura's "0.0 sec. left" line, but it concerns
                    // any skill with a dot of its own: Meteor took its burn duration from
                    // the weapon instead of its own.
                    //
                    // We take the larger of the two: the skill may have a weapon share, and
                    // then the weapon's dot is applied by the same hit.
                    double ownSecs = own.DotHi[k.Field] > 0 ? own.DotSecs[k.Field]
                                   : ownOne.DotHi[k.Field] > 0 ? ownOne.DotSecs[k.Field] : 0;
                    double gearSecs = Math.Abs(dotShare) > 0.0001 ? p.DotSecs[k.Field] : 0;
                    HitHandSecs[hand][k.Field + "Dot"] = Math.Max(ownSecs, gearSecs);
                }
            }

            // RETALIATION DAMAGE ADDED TO A HIT — `retaliationDamagePct`, MECHANICS 14d9.
            //
            // The field was found on `_Itaan` (Retribution) and was not entered for three
            // weeks for one reason: it was unknown WHICH DAMAGE TYPE the retaliation is
            // added to. On `_Itaan` it is all poison, with nothing to tell apart.
            //
            // `_222` told it apart — it has retaliation damage of THREE types, and the
            // game's panel matched ours to the unit (physical 949, fire 3240, poison 267).
            // Two game numbers, both independent:
            //
            //   Aegis of Menhir has no poison of its own and none from the weapon, yet the
            //   game prints 69 — exactly 26% × 266.8 of our retaliation poison;
            //   Reprisal's own tooltip "(1159 – 1159)" =
            //   26% × (948.71 + 3240.36 + 266.80) = 1158.5.
            //
            // So the share is taken from the retaliation damage of EACH type and goes into
            // the attack of THE SAME type. The character's conversions are NOT applied a
            // second time — they are already inside the retaliation damage (`Retaliate` runs
            // them); the skill's OWN conversions do apply, and that is measured too: the
            // Aegis' set modifier `set_d206_aegis` moves 100% physical into fire, and
            // without it fire was short by 246 out of 1092.
            //
            // AN UNCLOSED REMAINDER: for the Aegis' fire the rule gives 1089.16 while the
            // game needs 1092.23 — off by 3.07, that is 0.28%. The retaliation panel is
            // checked in full and matches, so it is not that. What it is has not been worked
            // out; written down in MECHANICS 14d9.
            Pool retal = null;
            double retalPct = Skill == null
                            ? 0 : Skill.Sum("retaliationDamagePct", withTemporary) / 100.0;
            if (retalPct > 0.0001 && Retaliation.Count > 0)
            {
                retal = new Pool();
                foreach (DamageLine r in Retaliation)
                    if (retal.Lo.ContainsKey(r.Type))
                    { retal.Lo[r.Type] = r.Min * retalPct; retal.Hi[r.Type] = r.Max * retalPct; }
                List<Move> mine = SkillConversions();
                if (mine.Count > 0) Conversion.Pass(retal, mine, true);
                foreach (Kind kk in Kinds)
                    if (retal.Hi[kk.Field] > 0.5)
                        Trace.Add("retaliation into the strike " + kk.Field + ": " +
                                  Exact(retal.Lo[kk.Field]) + "–" + Exact(retal.Hi[kk.Field]) +
                                  "   (" + Exact(retalPct * 100) + "% of the retaliation)");
            }
            // ...AND THE SAME SHARE OF THE RETALIATION'S DOT PART — it goes into the hit's
            // DOT line. Analysis and numbers at `Retaliate` above.
            Pool retalDot = null;
            if (retalPct > 0.0001 && RetaliationDot.Count > 0)
            {
                retalDot = retalIntoDot = new Pool();
                foreach (DamageLine r in RetaliationDot)
                    if (retalDot.DotLo.ContainsKey(r.Type))
                    { retalDot.DotLo[r.Type] = r.Min * retalPct; retalDot.DotHi[r.Type] = r.Max * retalPct; }
                List<Move> mine2 = SkillConversions();
                if (mine2.Count > 0) Conversion.Pass(retalDot, mine2, true);
                foreach (Kind kk in Kinds)
                    if (retalDot.DotHi.ContainsKey(kk.Field) && retalDot.DotHi[kk.Field] > 0.5)
                        Trace.Add("retaliation DOT into the strike " + kk.Field + ": " +
                                  Exact(retalDot.DotLo[kk.Field]) + "–" + Exact(retalDot.DotHi[kk.Field]) +
                                  "   (" + Exact(retalPct * 100) + "% of the retaliation dot)");
            }
            foreach (Kind k in Kinds)
            {
                double m = Multiplier(k, false);
                if (gear.Hi[k.Field] > 0 || own.Hi[k.Field] > 0)
                    Trace.Add("after conversions " + k.Field + ": weapon " + Exact(gear.Lo[k.Field]) + "–" +
                              Exact(gear.Hi[k.Field]) +
                              // THE SECOND HAND'S POOL. It was printed nowhere, which hurt
                              // exactly where the hand is the second one: for a skill that
                              // hits with the shield alone all the weapon damage comes from
                              // THIS pool while the trace showed the neighbouring one.
                              (second != null
                                   ? ", SECOND hand " + Exact(second.Lo[k.Field]) + "–" +
                                     Exact(second.Hi[k.Field]) : "") +
                              ", skill " + Exact(own.Lo[k.Field]) + "–" +
                              Exact(own.Hi[k.Field]) +
                              // ...and the "skill, ONE hand" pool — the dot line printed it,
                              // the direct-damage line did not, which made Explosive
                              // Strike's 78 physical look lost: the line said "skill 0"
                              // while the damage sat in the neighbouring pool.
                              (ownOne.Hi[k.Field] > 0.0001 || ownOne.Lo[k.Field] > 0.0001
                                   ? ", one-hand skill " + Exact(ownOne.Lo[k.Field]) + "–" +
                                     Exact(ownOne.Hi[k.Field]) : "") +
                              "   multiplier " + Exact(m) +
                              " = 1 + " + Exact((k.Magic ? spirit : cunning) / Combat.AttributeDivisor(game, k.Magic, false)) +
                              " + " + Exact((Sum("offensive" + k.Field + "Modifier") +
                                             SklForm("offensive" + k.Field + "Modifier") +
                                             Skl("offensive" + k.Field + "Modifier")) / 100.0) +
                              (k.Elemental ? " + " + Exact((Sum("offensiveElementalModifier") +
                                                            SklForm("offensiveElementalModifier") +
                                                            Skl("offensiveElementalModifier")) / 100.0) : "") +
                              " + " + Exact((Sum("offensiveTotalDamageModifier") +
                                             SklForm("offensiveTotalDamageModifier") +
                                             Skl("offensiveTotalDamageModifier")) / 100.0));
                    // WHERE IT CAME FROM — by name, and what each source brought with it.
                    // Without this line "a percentage that moved" cannot be checked: the
                    // pool shows only the total that arrived, while the sources' percentages
                    // DIFFER.
                    string whence = Whence(gear, own, "offensive", k.Field);
                    if (whence.Length > 0) Trace.Add("    where " + k.Field + " came from: " + whence);
                // The dot AFTER conversion used to be printed only before it, which hid the
                // most interesting part: a conversion drags the root's dot along, and a
                // skill's dot can move into another type entirely. `_Itaan`'s Righteous
                // Fervor has its whole own dot in Retribution's 199 internal trauma, which a
                // transmuter turns into poison, and without this line "the wrong thing
                // moved" cannot be told from "the wrong amount moved".
                if (gear.DotHi.ContainsKey(k.Field) &&
                    (gear.DotHi[k.Field] > 0 || own.DotHi[k.Field] > 0 || ownOne.DotHi[k.Field] > 0))
                    Trace.Add("after conversions " + k.Field + "Dot: weapon " +
                              Exact(gear.DotLo[k.Field]) + "–" + Exact(gear.DotHi[k.Field]) +
                              // HOW MUCH OF IT ARRIVED BY CONVERSION — that is, another
                              // type's dot that moved here with its root. This very number
                              // is what the dispute is about: `_Jien`'s per-hand panel does
                              // NOT show it, `_Itaan`'s does.
                              " (arrived by conversion " + Exact(In(gear.InDotLo, k.Field)) + "–" +
                              Exact(In(gear.InDotHi, k.Field)) + ")" +
                              ", skill " + Exact(own.DotLo[k.Field]) + "–" + Exact(own.DotHi[k.Field]) +
                              (ownOne.DotHi[k.Field] > 0
                                   ? ", one-hand skill " + Exact(ownOne.DotLo[k.Field]) + "–" +
                                     Exact(ownOne.DotHi[k.Field]) : "") +
                              // THE DOT MULTIPLIER BROKEN DOWN INTO ADDENDS — as for direct
                              // damage a line above. `_Jien`'s freeze per hit and per hand
                              // differ by a factor of 1.404 over ONE base (12 per second
                              // from devotion), and without the breakdown it is invisible
                              // which addend the game leaves out of the per-hand panel —
                              // the attribute, the type percentage or the total.
                              "   multiplier " + Exact(Multiplier(k, true)) +
                              // A DOT HAS ITS OWN ATTRIBUTE DIVIDER: spirit/200 and
                              // cunning/215, not /215 and /245 (`combatformulas.dbr`,
                              // MECHANICS 1). The DIRECT damage divider used to stand here,
                              // and the printed sum of addends did not match the multiplier
                              // itself — by exactly the difference of the two dividers. The
                              // dot breakdown was unreadable: the hunt for the missing
                              // spirit began with this line looking broken.
                              " = 1 + " + Exact((k.Magic ? spirit : cunning) / Combat.AttributeDivisor(game, k.Magic, true)) +
                              " + " + Exact((Sum("offensiveSlow" + k.Field + "Modifier") +
                                             SklForm("offensiveSlow" + k.Field + "Modifier") +
                                             Skl("offensiveSlow" + k.Field + "Modifier")) / 100.0) +
                              (k.Elemental ? " + " + Exact((Sum("offensiveSlowElementalModifier") +
                                                            SklForm("offensiveSlowElementalModifier") +
                                                            Skl("offensiveSlowElementalModifier")) / 100.0) : "") +
                              " + " + Exact((Sum("offensiveTotalDamageModifier") +
                                             SklForm("offensiveTotalDamageModifier") +
                                             Skl("offensiveTotalDamageModifier")) / 100.0));
                    // WHERE THE DOT CAME FROM — by name, as for direct damage above.
                    string whenceDot = Whence(gear, own, "offensiveSlow", k.Field);
                    if (whenceDot.Length > 0)
                        Trace.Add("    where " + k.Field + "Dot came from: " + whenceDot);
                // A SKILL'S OWN PERCENTAGE DOES NOT APPLY TO WHAT ARRIVED BY THE CHARACTER'S
                // CONVERSION (MECHANICS 19i) — so every pool enters the line as two addends:
                // the native part with its own multiplier, the arrived part with a
                // multiplier that has no skill percentages.
                double mIn = Multiplier(k, false, false);
                double mSk = Multiplier(k, false, true, false);
                double gLo = gear.Lo[k.Field], gHi = gear.Hi[k.Field];
                double gInLo = In(gear.InLo, k.Field), gInHi = In(gear.InHi, k.Field);
                double gCaLo = In(gear.CameLo, k.Field), gCaHi = In(gear.CameHi, k.Field);
                if (second != null)
                {
                    // A TYPE ONLY ONE HAND CARRIES GETS NO AVERAGING.
                    //
                    // The `0.75 × (both hands)` averaging is the average over a cycle of TWO
                    // swings (MECHANICS 14d1). If exactly one hand carries a type, the
                    // second swing does not deal it AT ALL, there is nothing to average
                    // with — and the panel prints it whole. Analysis — MECHANICS 14d4.
                    //
                    // `_Viliya`'s ordinary attack settled it, where the per-hand panel and
                    // the hit line stand side by side and there is nothing to argue about:
                    //
                    //   per hand  right 175, left 0
                    //   per hit   175          ← whole; averaging would give 131
                    //
                    // And a control on another character, where both hands carry the type
                    // (`_Amakata`, ordinary attack, life damage):
                    //
                    //   per hand  6289 – 7067 and 5946 – 6723
                    //   per hit   9176 – 10343 = 0.75 × (6289+5946) and 0.75 × (7067+6723)
                    //                            ← averaging applies, to the unit
                    //
                    // The same two sides show on skills: `_Viliya`'s Brutal Slam deals its
                    // life damage from ONE weapon and it enters whole
                    // (35 × 1.8 × 11.4907 = 724 against the game's 724 – 931, while the
                    // quarter would give 543), while its aether comes from BOTH and the
                    // quarter does apply (1346 – 1965, exactly). `_Iman`'s Sacred Strike: 8
                    // from one weapon's component, the inflow killed by the skill's own
                    // conversion, and 8 × 3.5 × 6.924 = 194 against the game's 194.
                    //
                    // THE BOUNDARY: "nobody else" includes THE SKILL'S OWN DAMAGE — it flies
                    // with every swing, so the second swing does deal that type and the
                    // averaging applies. In that same Brutal Slam the physical comes from
                    // one weapon's component, but the skill has 270 – 312 of its own, and
                    // there the quarter matches (1524 against the game's 1524; without it,
                    // 1536).
                    bool oneSwing = own.Hi[k.Field] <= 0.0001 && ownOne.Hi[k.Field] <= 0.0001 &&
                                    (gear.Hi[k.Field] <= 0.0001) != (second.Hi[k.Field] <= 0.0001);
                    double dual = oneSwing ? 1 : DualShare;
                    gLo = dual * (gLo + second.Lo[k.Field]);
                    gHi = dual * (gHi + second.Hi[k.Field]);
                    gInLo = dual * (gInLo + In(second.InLo, k.Field));
                    gInHi = dual * (gInHi + In(second.InHi, k.Field));
                    gCaLo = dual * (gCaLo + In(second.CameLo, k.Field));
                    gCaHi = dual * (gCaHi + In(second.CameHi, k.Field));
                }
                // The arrived part's multiplier belongs to THE POOL that goes into this
                // addend: a percentage that moved belongs to its source, and the sources of
                // the hands and of the skill's own damage differ (analysis at `MIn`).
                double mInBothLo = MIn(mIn, k, false, false, gear, second);
                double mInBothHi = MIn(mIn, k, false, true, gear, second);
                double mInGearLo = MIn(mIn, k, false, false, gear, null);
                double mInGearHi = MIn(mIn, k, false, true, gear, null);
                double fromWeaponLo = Both(gLo, gInLo, gCaLo, m, mInBothLo, mSk) * wpct +
                                      Both(gear.Lo[k.Field], In(gear.InLo, k.Field),
                                           In(gear.CameLo, k.Field), m, mInGearLo, mSk) * wpctOne;
                double fromWeaponHi = Both(gHi, gInHi, gCaHi, m, mInBothHi, mSk) * wpct +
                                      Both(gear.Hi[k.Field], In(gear.InHi, k.Field),
                                           In(gear.CameHi, k.Field), m, mInGearHi, mSk) * wpctOne;
                if (wpctTwo > 0.0001 && second != null)
                {
                    fromWeaponLo += Both(second.Lo[k.Field], In(second.InLo, k.Field),
                                         In(second.CameLo, k.Field), m,
                                         MIn(mIn, k, false, false, second, null), mSk) * wpctTwo;
                    fromWeaponHi += Both(second.Hi[k.Field], In(second.InHi, k.Field),
                                         In(second.CameHi, k.Field), m,
                                         MIn(mIn, k, false, true, second, null), mSk) * wpctTwo;
                }
                if (wpctBest > 0.0001 && second != null)
                {
                    // THE BLAST TAKES THE UNSPLIT SUM — BOTH WEAPONS, THE CHARACTER ONCE.
                    //
                    // It fires once per shot, so it is not averaged over the swing cycle
                    // (no 0.75) and it is not one hand's pool either: both weapons are in
                    // that one blast, while the character's own flat damage belongs to it
                    // once. That pool is `whole`, the very one the dots have been counted
                    // from since August for the same reason.
                    //
                    // MEASURED — `_Tapina`, Shadow Strike with Nightfall
                    // (`SkillSecondary_AttackRadius`, 56% weapon damage), 21 October. Three
                    // numbers, two of them the ends of one bracket, all to within 0.01%:
                    //
                    //            addends: hand 1   hand 2   UNSPLIT     the share needs
                    //   pierce             389      386      523        523.04
                    //   pierce (top)       477      473      673        672.98
                    //   bleeding           143      143      153        153.02
                    //
                    // (the "needs" column is the game's number minus everything that is not
                    //  this share, divided by 0.56; the unsplit sum is what the breakdown
                    //  prints as "sum of the addends").
                    //
                    // WHAT THIS REJECTS, on the same three numbers: the best hand (the rule
                    // until today) gives 389/477/143 and falls short by 3.4% / 4.2% / 1.4% —
                    // exactly the debt that stood on this character since 24 September; the
                    // sum of the two hands (775/950/286) overshoots by a mile, because the
                    // character's flat damage would stand twice; and `0.75 × the sum` gives
                    // 581/712/214.5, which is not it either.
                    //
                    // The earlier reading was "the BEST hand, chosen per damage type"
                    // (MECHANICS 14v5, `_Iman`'s Explosive Strike, nine numbers). On that
                    // character it is INDISTINGUISHABLE from this one: there every type is
                    // carried by the character or by one weapon alone, and then the unsplit
                    // sum IS the better hand. `_Tapina` is the first case where both weapons
                    // carry the same type — and it tells them apart (rule "what to do" 31).
                    // `BestOf` is kept below: `--blastbesthand` puts the old rule back, and
                    // with it both lines go red again.
                    if (BreakBlastBestHand)
                    {
                        fromWeaponLo += BestOf(k.Field, gear.Lo, gear.InLo, gear.CameLo,
                                               second.Lo, second.InLo, second.CameLo,
                                               m, mInBothLo, mSk) * wpctBest;
                        fromWeaponHi += BestOf(k.Field, gear.Hi, gear.InHi, gear.CameHi,
                                               second.Hi, second.InHi, second.CameHi,
                                               m, mInBothHi, mSk) * wpctBest;
                    }
                    else
                    {
                        fromWeaponLo += Both(whole.Lo[k.Field], In(whole.InLo, k.Field),
                                             In(whole.CameLo, k.Field), m, mInBothLo, mSk) * wpctBest;
                        fromWeaponHi += Both(whole.Hi[k.Field], In(whole.InHi, k.Field),
                                             In(whole.CameHi, k.Field), m, mInBothHi, mSk) * wpctBest;
                    }
                }
                double fromSkillLo = Both(own.Lo[k.Field], In(own.InLo, k.Field),
                                          In(own.CameLo, k.Field), m,
                                          MIn(mIn, k, false, false, own, null), mSk) * ownShare +
                                     Both(ownOne.Lo[k.Field], In(ownOne.InLo, k.Field),
                                          In(ownOne.CameLo, k.Field), m,
                                          MIn(mIn, k, false, false, ownOne, null), mSk);
                double fromSkillHi = Both(own.Hi[k.Field], In(own.InHi, k.Field),
                                          In(own.CameHi, k.Field), m,
                                          MIn(mIn, k, false, true, own, null), mSk) * ownShare +
                                     Both(ownOne.Hi[k.Field], In(ownOne.InHi, k.Field),
                                          In(ownOne.CameHi, k.Field), m,
                                          MIn(mIn, k, false, true, ownOne, null), mSk);
                // The FINISHED line broken into its two addends. Without it the trace names
                // only the total, and when the game disagrees there is no way to tell "the
                // weapon share is too small" from "the skill's own damage is too small" —
                // different errors in different places. On `_Jien`'s Savagery this is
                // exactly what showed that the whole remainder sits in the weapon part.
                if (fromWeaponHi > 0.5 || fromSkillHi > 0.5)
                    Trace.Add("line " + k.Field + ": from the weapon " + Exact(fromWeaponLo) + "–" +
                              Exact(fromWeaponHi) + ", the skill's own " + Exact(fromSkillLo) + "–" +
                              Exact(fromSkillHi));
                // ...and the RETALIATION share, if the skill gives one (MECHANICS 14d9). It
                // is already finished: the retaliation multipliers are inside it and the
                // hit's multipliers are not applied to it — otherwise the Aegis' poison
                // would come out in the hundreds instead of 69.
                //
                // ...AND WITH DUAL WIELDING IT IS AVERAGED BY THE CYCLE JUST LIKE THE
                // SKILL'S OWN DAMAGE — by the same `ownShare` (2 × 0.75 = 1.5).
                //
                // MEASURED — `_Itaan`, Righteous Fervor. The poison hit line had not matched
                // since August, and the shortfall turned out to be EXACTLY HALF of what we
                // were adding from retaliation:
                //
                //   ours     29733 – 36935     short by 599 and 767
                //   the share                  1198.56 – 1535.27
                //   599.28 = 1198.56 / 2       767.64 = 1535.27 / 2
                //   with ×1.5 30332 – 37702    game 30332 – 37702   EXACT at both ends
                //
                // So the retaliation share enters the hit WITH EACH HAND, like the skill's
                // own damage, not once per hit. The dot line is untouched: a dot is not
                // doubled with dual wielding (see AddDot).
                // THE HIT'S DAMAGE PER HAND SEPARATELY — for the timeline.
                //
                // The line above adds both hands and averages them by the cycle (0.75) —
                // that is the PANEL's rule (MECHANICS 14d1). The timeline needs something
                // else: what EACH hand dealt in this swing, because the game prints two
                // numbers over the target and the hands miss separately.
                //
                // It is assembled from the same addends as the panel line, only without the
                // averaging:
                //     hand = its pool × (the both-hands share + this hand's share)
                //            + the skill's OWN damage (it flies with every swing — the
                //              Righteous Fervor measurement, hence `ownShare` = 2 × 0.75)
                //            + the retaliation share, by the same rule
                //
                // `HandTypeMin` next to it is SOMETHING ELSE: a hundred per cent of the
                // weapon without the skill's share, as the game's tooltip prints it. The two
                // must not be confused.
                {
                    double ownLo = Both(own.Lo[k.Field], In(own.InLo, k.Field),
                                        In(own.CameLo, k.Field), m,
                                        MIn(mIn, k, false, false, own, null), mSk) +
                                   Both(ownOne.Lo[k.Field], In(ownOne.InLo, k.Field),
                                        In(ownOne.CameLo, k.Field), m,
                                        MIn(mIn, k, false, false, ownOne, null), mSk);
                    double ownHi = Both(own.Hi[k.Field], In(own.InHi, k.Field),
                                        In(own.CameHi, k.Field), m,
                                        MIn(mIn, k, false, true, own, null), mSk) +
                                   Both(ownOne.Hi[k.Field], In(ownOne.InHi, k.Field),
                                        In(ownOne.CameHi, k.Field), m,
                                        MIn(mIn, k, false, true, ownOne, null), mSk);
                    double rLo = retal == null ? 0 : retal.Lo[k.Field];
                    double rHi = retal == null ? 0 : retal.Hi[k.Field];
                    for (int hand = 0; hand < 2; hand++)
                    {
                        Pool p = hand == 0 ? gear : second;
                        if (p == null) continue;
                        double share = wpct + (hand == 0 ? wpctOne : wpctTwo);
                        double wLo = Both(p.Lo[k.Field], In(p.InLo, k.Field),
                                          In(p.CameLo, k.Field), m,
                                          MIn(mIn, k, false, false, p, null), mSk) * share;
                        double wHi = Both(p.Hi[k.Field], In(p.InHi, k.Field),
                                          In(p.CameHi, k.Field), m,
                                          MIn(mIn, k, false, true, p, null), mSk) * share;
                        HitHandMin[hand][k.Field] = wLo + ownLo + rLo;
                        HitHandMax[hand][k.Field] = wHi + ownHi + rHi;
                    }
                }

                double retalLo = retal == null ? 0 : retal.Lo[k.Field] * ownShare;
                double retalHi = retal == null ? 0 : retal.Hi[k.Field] * ownShare;
                Add(k.Field, k.RuKey, fromWeaponLo + fromSkillLo + retalLo,
                                   fromWeaponHi + fromSkillHi + retalHi, false, 0);

                string dot;
                if (!DotName.TryGetValue(k.Field, out dot)) continue;
                if (k.Field == "Bleeding") continue;   // bleeding is its own dot already
                // Both weapon shares go into AddDot SEPARATELY: with dual wielding a dot
                // counts them from different pools — see AddDot. Since 16 October the OFF
                // HAND's share goes separately too, from its own pool (MECHANICS 150), and
                // since 21 October so does the BLAST's, from the unsplit sum (MECHANICS 167).
                AddDot(k, dot, gear, second, whole, weaponOwn, own, ownOne,
                       wpct, wpctOne, wpctTwo, wpctBest);
            }
            // Bleeding is stored as a dot and shown per second like the others.
            AddDot(Kinds[Kinds.Length - 1], "model.dot.Bleeding", gear, second, whole, weaponOwn, own, ownOne,
                   // THE OFF-HAND SHARE NOW TAKES THE OFF HAND'S POOL (16 October). The line
                   // here used to read "for a dot the hands are not separated at all", and it
                   // named the case that would expose it: "as soon as a character turns up
                   // whose hands differ in dot AND whose skill hits with one hand, this will
                   // show as a number". `_RuneTask` is that character and the number was
                   // +11.7% — MECHANICS 150.
                   //
                   // `wpctBest` WENT WITH THE FIRST HAND UNTIL 21 OCTOBER, and the line here
                   // said why: no measurement of it existed. On `_Iman` the blast's whole dot
                   // is its own (94 burn) with no weapon dot under the 34% share, and on
                   // `_Itaan` the hands' dots are equal — neither tells the pools apart.
                   // `_Tapina`'s bleeding does: the share needs 153, the unsplit sum, where
                   // the first hand holds 143 (MECHANICS 167).
                   wpct, wpctOne, wpctTwo, wpctBest);

            // Without this line the numbers look self-contradictory: the per-hand panel
            // shows one thing, the "per hit" line another, and there is nothing to explain
            // why the latter is sometimes above and sometimes below a hand.
            if (dualDot.Count > 0)
                Notes.Add(Lang.T("model.dualDot", string.Join(", ", dualDot.ToArray())));

            // The notes' wording comes from the language file `lang/<CODE>/model.txt`.
            if (Skill == null)
                Notes.Add(Lang.T("model.plainAttack"));
            else
            {
                Notes.Add(Lang.T("model.skill", Skill.Name, Skill.Level) + " " +
                          (Math.Abs(Skill.WeaponPct) < 0.0001
                               ? Lang.T("model.skillNoWeapon")
                               : Lang.T("model.skillWeapon", Fmt(Skill.WeaponPct))));
                // Charges are visible only here: in a skill's tooltip the game prints the
                // weapon share WITHOUT the charge multiplier while the panel prints it with,
                // and without this line its two numbers look self-contradictory.
                if (Math.Abs(charge - 1) > 0.0001)
                    Notes.Add(Lang.T("model.charge", Fmt(Skill.ChargeMultiplier(withTemporary) * 100), Fmt(Skill.WeaponPct)));
                // Each hand's damage separately is the only game number by which the
                // per-hand calculation can be checked by eye, and it is now printed by a
                // SEPARATE section of the window in the game's own words: see
                // DamageForm.AddHandShare. There used to be a note line here saying the same
                // thing, only smaller and lost among the rest.
                if (Skill.Parts.Count > 1)
                {
                    List<string> who = new List<string>();
                    for (int i = 1; i < Skill.Parts.Count; i++)
                        if (!Skill.Parts[i].Temporary) who.Add(Skill.Parts[i].Who);
                    if (who.Count > 0)
                        Notes.Add(Lang.T("model.modifiers", string.Join(", ", who.ToArray())));
                }
                if (Skill.HasTemporary)
                    Notes.Add(withTemporary
                        ? Lang.T("model.temporaryIn", Skill.TemporaryNames)
                        : Lang.T("model.temporaryOut", Skill.TemporaryNames));
                // The retaliation damage added to a hit is counted now (MECHANICS 14d9). The
                // calculation itself is above, at the `retal` pool; here it only remains to
                // say out loud how much was added.
                double retalPart = Skill.Sum("retaliationDamagePct", withTemporary);
                if (Math.Abs(retalPart) > 0.0001)
                    Notes.Add(Lang.T("model.retaliation", Fmt(retalPart)));
                foreach (string left in Skill.NotCounted)
                    Notes.Add(Lang.T("model.notCounted", left));
            }
            Notes.Add(Lang.T("model.notModelled"));
        }

        // RETALIATION DAMAGE — the "retaliation damage" panel in the character stats.
        //
        // It is a SEPARATE kind of damage and is not counted like a hit: neither attributes
        // nor the percentages of damage types touch it — only "% retaliation damage"
        // (`retaliationTotalDamageModifier` and the per-type `retaliation{T}Modifier`). The
        // article says so in words (MECHANICS 14g0) and the numbers confirm it: `_Itaan`'s
        // retaliation panel reads +441% while the same character's ordinary poison panel
        // reads +2953%.
        //
        // CONVERSIONS DO APPLY TO IT, and the very same ones. That is not obvious and was
        // checked by two experiments:
        //
        //   `_Itaan`, with trousers giving 133–329 retaliation physical put on: 0.684 of the
        //   gain went into poison and 0.316 stayed physical — with the character's
        //   physical → poison conversion at 68%. Both ends, both types.
        //
        //   `_Amakata`, three lines out of three with no modifier at all:
        //     physical 150 (the devotion Targo the Builder), 74% goes into life
        //       stayed   39 × 1.49 =  58.1   game  60
        //       moved   111 × 1.49 = 165.4   game 164
        //     poison   615 (a medal, the ROLLED value, 540 in the record) × 1.49 = 916.4
        //                                                                 game 916
        //     the multiplier +49% = 19 Merciless Repertoire + 30 Targo the Builder
        //
        // So the same `Convert` as for a hit is reused here.
        public readonly List<DamageLine> Retaliation = new List<DamageLine>();
        // THE DOT PART OF THE RETALIATION DAMAGE. Kept as a separate list, not together with
        // the direct part: our retaliation panel is checked per type (`Retal:Poison`), and
        // mixing a dot in would change the meaning of the measurements already standing. So
        // far it is needed for exactly one thing — the share a skill adds to the attack
        // (MECHANICS 14d9).
        public readonly List<DamageLine> RetaliationDot = new List<DamageLine>();
        // The finished share of the retaliation DOT that a skill adds to the attack. Set in
        // the same place where the direct one is counted (`retal`), read in `AddDot`.
        Pool retalIntoDot;
        // The skill's own damage BEFORE the conversions, per type: how much the source had.
        // `SklCarried` needs it so that a percentage that moved is counted per source rather
        // than as one total over the whole arrived pool (MECHANICS 25d).
        readonly Dictionary<string, double> beforeOwn = new Dictionary<string, double>(StringComparer.Ordinal);
        readonly Dictionary<string, double> dotBeforeOwn = new Dictionary<string, double>(StringComparer.Ordinal);
        public double RetaliationPercent;

        void Retaliate()
        {
            Pool pool = new Pool();
            bool any = false;
            foreach (Kind k in Kinds)
            {
                double lo, hi;
                PairSum("retaliation" + k.Field + "Min", "retaliation" + k.Field + "Max", out lo, out hi);
                pool.Lo[k.Field] = lo; pool.Hi[k.Field] = Math.Max(lo, hi);
                if (hi > 0 || lo > 0) any = true;
            }
            if (!any) return;
            foreach (Kind k in Kinds)
                if (pool.Hi[k.Field] > 0)
                    Trace.Add("retaliation, base " + k.Field + ": " + Exact(pool.Lo[k.Field]) + "–" +
                              Exact(pool.Hi[k.Field]));
            // The character's conversions ONLY. The retaliation panel is a character-stat
            // panel: it does not know which skill is picked in the list, and the chosen
            // skill's transmuter has nothing to do with it. Through the common `Convert`,
            // `_Itaan`'s poison came out 9273 against the game's 9227 — exactly by the 9.6
            // that Righteous Fervor's transmuter was taking out of physical.
            Conversion.Pass(pool, Conversions(), false);
            double total = Sum("retaliationTotalDamageModifier");
            RetaliationPercent = total;
            Trace.Add("retaliation: overall percent " + Exact(total) + "%");
            foreach (Kind k in Kinds)
            {
                double m = 1 + (total + Sum("retaliation" + k.Field + "Modifier")) / 100.0;
                double lo = pool.Lo[k.Field] * m, hi = pool.Hi[k.Field] * m;
                if (lo < 0.5 && hi < 0.5) continue;
                Retaliation.Add(new DamageLine
                { Type = k.Field, Name = Lang.T(k.RuKey), Min = Math.Round(lo), Max = Math.Round(hi) });
                Trace.Add("retaliation " + k.Field + ": " + Exact(pool.Lo[k.Field]) + "–" +
                          Exact(pool.Hi[k.Field]) + " × " + Exact(m) + " = " +
                          Fmt(Math.Round(lo)) + "–" + Fmt(Math.Round(hi)));
            }

            // RETALIATION DAMAGE HAS A DOT PART, AND WE DID NOT COUNT IT AT ALL.
            //
            // The `retaliationSlow<Type>Min` fields were collected nowhere, and they exist:
            // on `_Acid Vines` the relic skill Malediction gives "3300 retaliation poison
            // damage over 5 sec.", that is 660 per second. The construction is the same as
            // for the direct part — the same character conversions and the same total
            // percentage.
            //
            // WHY IT IS NEEDED (MECHANICS 14d9): the retaliation share a skill adds to the
            // attack must be taken from the dot as well — it goes into the hit's DOT line.
            // In numbers, `_Acid Vines`' Grasping Vines:
            //
            //   660 × 5.09 (total percentage 409%) × 20% (`retaliationDamagePct`) = 671.9
            //   was  63239.3                      game 63911
            //   now  63239.3 + 671.9 = 63911.2    game 63911   EXACT
            //
            // AND THE ITEMS' OWN TOOLTIPS NAME THE SAME NUMBER: the shield says "5% of
            // retaliation damage added to attack (917)", Entangling Vines says "15% …
            // (2752)", together 3669 against our 2998 from the direct part. The difference
            // of 671 is exactly the missing dot — a second, independent count of the same
            // thing.
            Pool dots = new Pool();
            bool anyDot = false;
            foreach (Kind k in Kinds)
            {
                double lo, hi;
                PairSum("retaliationSlow" + k.Field + "Min", "retaliationSlow" + k.Field + "Max",
                        out lo, out hi);
                dots.Lo[k.Field] = lo; dots.Hi[k.Field] = Math.Max(lo, hi);
                if (hi > 0 || lo > 0) anyDot = true;
            }
            if (!anyDot) return;
            Conversion.Pass(dots, Conversions(), false);
            foreach (Kind k in Kinds)
            {
                double m = 1 + (total + Sum("retaliationSlow" + k.Field + "Modifier")) / 100.0;
                double lo = dots.Lo[k.Field] * m, hi = dots.Hi[k.Field] * m;
                if (lo < 0.5 && hi < 0.5) continue;
                RetaliationDot.Add(new DamageLine
                { Type = k.Field, Name = Lang.T(k.RuKey), Min = Math.Round(lo), Max = Math.Round(hi), OverTime = true });
                Trace.Add("retaliation DOT " + k.Field + ": " + Exact(dots.Lo[k.Field]) + "–" +
                          Exact(dots.Hi[k.Field]) + " × " + Exact(m) + " = " +
                          Fmt(Math.Round(lo)) + "–" + Fmt(Math.Round(hi)));
            }
        }

        // WHERE THE BASE CAME FROM — by name, with the sum checked.
        //
        // Why. The trace already splits a finished line into "from the weapon" and "the
        // skill's own", and that split alone gave three finds in a row. But inside "from the
        // weapon" sit a dozen items, auras and constellations, and when a line disagrees it
        // is unclear which of them enters wrongly. On `_Itaan`'s bleeding that cost a whole
        // session: the source turned out to be a constellation, not the weapon, and the only
        // way to see it was to write the addends out.
        //
        // The numbers come FROM THE SAME PLACE the calculation takes them
        // (`CharacterTotals.Terms`), so they must agree by construction — and the "sum of
        // addends" line checks that out loud. If they ever disagree, the calculation is
        // taking the base past the addend census, which is a find in itself.
        //
        // The BASE is printed, before conversions and before the multiplier: both of those
        // have lines of their own further on, so the chain "source → base → conversion →
        // multiplier → panel line" is visible end to end.
        void Sources(Pool gear, Pool own, Pool ownOne)
        {
            foreach (Kind k in Kinds)
                for (int dot = 0; dot < 2; dot++)
                {
                    if (dot == 1 && !gear.DotLo.ContainsKey(k.Field)) continue;
                    string field = (dot == 1 ? "offensiveSlow" : "offensive") + k.Field + "Min";
                    double pool = dot == 1 ? gear.DotLo[k.Field] : gear.Lo[k.Field];
                    double skill = dot == 1 ? own.DotLo[k.Field] + ownOne.DotLo[k.Field]
                                            : own.Lo[k.Field] + ownOne.Lo[k.Field];
                    TotalsEntry e;
                    List<string> terms = new List<string>();
                    double sum = 0;
                    // Flat direct damage arrives in THREE field families, and all three go
                    // into one pool: `offensive{T}` is the ordinary bonus,
                    // `offensiveBonus{T}` comes from sets and skills, `offensiveBase{T}` is
                    // the weapon's own non-physical damage ("55-95 cold damage" at the top
                    // of a dagger). Missing one of them means missing a source: on `_Relo`,
                    // without `offensiveBase`, 106 physical was short.
                    string[] families = dot == 1 ? new[] { "offensiveSlow" }
                                                 : new[] { "offensive", "offensiveBonus", "offensiveBase" };
                    foreach (string family in families)
                        if (totals.Fields.TryGetValue(family + k.Field + "Min", out e))
                            foreach (KeyValuePair<string, double> t in e.Terms)
                            {
                                if (Math.Abs(t.Value) < 0.0001) continue;
                                terms.Add(Exact(t.Value) + " " + t.Key +
                                          (family == "offensiveBase" ? " (the weapon's base)" : ""));
                                sum += t.Value;
                            }
                    // Flat elemental arrives as one field for three types: it has addends of
                    // its own, and without them fire, cold and lightning would look as if
                    // they came from nowhere.
                    if (k.Elemental && dot == 0)
                        foreach (string family in new[] { "offensive", "offensiveBonus" })
                            if (totals.Fields.TryGetValue(family + "ElementalMin", out e))
                                foreach (KeyValuePair<string, double> t in e.Terms)
                                {
                                    if (Math.Abs(t.Value) < 0.0001) continue;
                                    terms.Add(Exact(t.Value / 3) + " " + t.Key + " (elemental, a third)");
                                    sum += t.Value / 3;
                                }
                    // An item's form modifier sits in the CHARACTER'S POOL, so it belongs in
                    // the addend census too — otherwise the sum will not match the pool and
                    // the trace will print a false disagreement.
                    if (Skill != null)
                        foreach (SkillPart p in Skill.Parts)
                        {
                            if (!p.FromForm) continue;
                            if (p.Temporary && !withTemporary) continue;
                            double v = AttackSkill.Value(p.Rec, field, p.Slot);
                            if (Math.Abs(v) < 0.0001) continue;
                            terms.Add(Exact(v) + " " + p.Who);
                            sum += v;
                        }
                    if (terms.Count == 0 && Math.Abs(skill) < 0.0001) continue;

                    Trace.Add("where the base of " + k.Field + (dot == 1 ? "Dot" : "") + " comes from: " +
                              (terms.Count > 0 ? string.Join(" + ", terms.ToArray()) : "nothing from gear"));
                    // The skill's own damage is not included here: it has its own pool and
                    // its own "base … skill" line, while the sum of addends is checked
                    // against the WEAPON'S POOL. The pool already has the other hand taken
                    // out, so the check must be made before that subtraction — hence
                    // "+ what went to the second hand".
                    double back = OtherHandFlat(field, k, dot == 1);
                    bool same = Math.Abs(sum - (pool + back)) < 0.01;
                    Trace.Add("    sum of the addends " + Exact(sum) +
                              (Math.Abs(back) > 0.0001
                                   ? ", of them on the second hand " + Exact(back) + ", leaving " +
                                     Exact(sum - back) : "") +
                              "   in the calculation " + Exact(pool) +
                              (Math.Abs(skill) > 0.0001 ? "   plus the skill's own " + Exact(skill) : "") +
                              (same ? "   agrees" : "   DISAGREEMENT"));

                    // THE TOP END. Only the bottom used to be checked here — and it was in
                    // the top that `_Tapina`'s disagreement with the game turned up
                    // (MECHANICS 95): five extra points of base in each hand while the
                    // minimums matched to the unit. Counted the way `Top` counts: a source's
                    // "Max", or, failing that, the same "Min".
                    if (dot == 0)
                    {
                        // TWO IDENTICAL ITEMS ARE TWO DIFFERENT ADDENDS. `_Tapina` holds two
                        // Mythical Brutallax, and they must not be merged by name: the first
                        // attempt collapsed both into one and gave a sum BELOW the bottom
                        // end. So for each name we count how many times it occurred in the
                        // top, and only the sources with no top at all are taken from the
                        // bottom (a flat number without "Max" has its top equal to its
                        // bottom — the way `Top` counts it).
                        double sumTop = 0;
                        List<string> topTerms = new List<string>();
                        foreach (string family in families) TopOf(family + k.Field, ref sumTop, topTerms, 1);
                        if (k.Elemental)
                            foreach (string family in new[] { "offensive", "offensiveBonus" })
                                TopOf(family + "Elemental", ref sumTop, topTerms, 3);
                        if (Skill != null)
                            foreach (SkillPart p in Skill.Parts)
                            {
                                if (!p.FromForm) continue;
                                if (p.Temporary && !withTemporary) continue;
                                double top = AttackSkill.Value(p.Rec, field.Replace("Min", "Max"), p.Slot);
                                if (Math.Abs(top) < 0.0001) top = AttackSkill.Value(p.Rec, field, p.Slot);
                                sumTop += top;
                            }
                        double backTop = OtherHandTop(k);
                        double poolTop = gear.Hi[k.Field];
                        bool sameTop = Math.Abs(sumTop - (poolTop + backTop)) < 0.01;
                        if (topTerms.Count > 0)
                            Trace.Add("    where the top of " + k.Field + " comes from: " +
                                      string.Join(" + ", topTerms.ToArray()));
                        Trace.Add("    the top end: addends " + Exact(sumTop) +
                                  (Math.Abs(backTop) > 0.0001
                                       ? ", of them on the second hand " + Exact(backTop) + ", leaving " +
                                         Exact(sumTop - backTop) : "") +
                                  "   in the calculation " + Exact(poolTop) +
                                  (sameTop ? "   agrees" : "   DISAGREEMENT"));
                    }
                }
        }

        // How much the SECOND hand's weapon gives on this field — what `OtherHand` took out
        // of the first hand's pool. Needed only so the sum of addends matches the pool: the
        // census knows both hands, the pool knows one.
        double OtherHandFlat(string field, Kind k, bool dot)
        {
            if (c.Weapons1.Count < 2) return 0;
            Item it = c.Weapons1[1];
            DbRecord rec = string.IsNullOrEmpty(it.BaseName) ? null : game.Record(it.BaseName);
            if (rec == null || !IsWeapon(rec)) return 0;
            string name = NameOf(it);
            int ordinal = OrdinalOf(it, name);
            string[] parts = { it.BaseName, it.PrefixName, it.SuffixName,
                               it.RelicName, ItemViewFactory.RelicBonusOf(game, it),
                               it.AugmentName, it.ModifierName, it.AscensionName };
            double total = 0;
            for (int part = 0; part < parts.Length; part++)
            {
                DbRecord pr = string.IsNullOrEmpty(parts[part]) ? null : game.Record(parts[part]);
                if (pr == null) continue;
                Dictionary<string, double> known = exact == null ? null : exact.For(name, ordinal, part);
                if (dot)
                    total += Flat(pr, known, field) *
                             Share(CharacterTotals.Chance(pr, field.Replace("Min", "Chance"), 0), pr.Path, field);
                else
                {
                    foreach (string family in new[] { "offensive", "offensiveBonus", "offensiveBase" })
                        total += Flat(pr, known, family + k.Field + "Min");
                    if (k.Elemental)
                        total += Flat(pr, known, "offensiveElementalMin") / 3;
                }
            }
            return total;
        }

        // The top ends of one field family: every "Max" is added, and after them those "Min"
        // sources that have no "Max" of their own. `share` is the divider for flat
        // elemental, which goes in thirds to three types.
        void TopOf(string prefix, ref double sum, List<string> terms, int share)
        {
            TotalsEntry hi, lo;
            Dictionary<string, int> had = new Dictionary<string, int>(StringComparer.Ordinal);
            if (totals.Fields.TryGetValue(prefix + "Max", out hi))
                foreach (KeyValuePair<string, double> t in hi.Terms)
                {
                    int was;
                    had.TryGetValue(t.Key, out was);
                    had[t.Key] = was + 1;
                    if (Math.Abs(t.Value) < 0.0001) continue;
                    sum += t.Value / share;
                    terms.Add(Exact(t.Value / share) + " " + t.Key);
                }
            if (!totals.Fields.TryGetValue(prefix + "Min", out lo)) return;
            foreach (KeyValuePair<string, double> t in lo.Terms)
            {
                int left;
                if (had.TryGetValue(t.Key, out left) && left > 0) { had[t.Key] = left - 1; continue; }
                if (Math.Abs(t.Value) < 0.0001) continue;
                sum += t.Value / share;
                terms.Add(Exact(t.Value / share) + " " + t.Key + " (no top end)");
            }
        }

        // The same as `OtherHandFlat` but for the TOP end: how much of this type went to the
        // second hand at the maximum. Taken by the very same `Top` that `OtherHand`
        // subtracts with — otherwise the top-end check would be checking something other
        // than what the calculation does.
        double OtherHandTop(Kind k)
        {
            if (c.Weapons1.Count < 2) return 0;
            Item it = c.Weapons1[1];
            DbRecord rec = string.IsNullOrEmpty(it.BaseName) ? null : game.Record(it.BaseName);
            if (rec == null || !IsWeapon(rec)) return 0;
            string name = NameOf(it);
            int ordinal = OrdinalOf(it, name);
            string[] parts = { it.BaseName, it.PrefixName, it.SuffixName,
                               it.RelicName, ItemViewFactory.RelicBonusOf(game, it),
                               it.AugmentName, it.ModifierName, it.AscensionName };
            double total = 0;
            for (int part = 0; part < parts.Length; part++)
            {
                DbRecord pr = string.IsNullOrEmpty(parts[part]) ? null : game.Record(parts[part]);
                if (pr == null) continue;
                Dictionary<string, double> known = exact == null ? null : exact.For(name, ordinal, part);
                foreach (string family in new[] { "offensive", "offensiveBonus", "offensiveBase" })
                    total += Top(pr, known, family + k.Field);
                if (k.Elemental)
                {
                    double lo = Flat(pr, known, "offensiveElementalMin");
                    double hi = Math.Max(Flat(pr, known, "offensiveElementalMax"), lo);
                    total += hi / 3;
                }
            }
            return total;
        }

        // Takes the flat damage of one hand's weapon out of a pool (sign −1), or
        // puts it back in (sign +1).
        //
        // Everything the weapon carries counts, not just its own record: its
        // affixes, the component screwed into it with the component's completion
        // bonus, its augment and its ascension. The twelve points of life damage
        // that an augment gives are dealt by the sword it is on; the other hand
        // swings a different sword and does not carry them.
        //
        // We used to take the base record alone, on the reasoning that a component
        // is worn rather than swung. The game says otherwise. `_Amakata` wields two
        // Mythical Edge of Death; their life damage is 21 + 8 (the component) + 12
        // (the augment) on the right and 22 + 8 + 0 on the left. With the component
        // and the augment left in both pools her life damage came out 8038 – 8984
        // against the game's 7469 – 8407; taking them out gives 7495 – 8442, +0.4%
        // at both ends.
        //
        // Only flat damage is a property of the hand. Percentages and conversions
        // belong to the character and stay whole — see Conversions().
        // AN ITEM FIELD'S CHANCE ON THIS HIT — on the same die as the sum's addend
        // (`CharacterTotals.ChanceKey`, MECHANICS 89b): 1 if it came up or has no chance,
        // 0 if it did not. Without a roll, 1: the second hand is subtracted whole, as before.
        //
        // SUSPECTED BY READING THE CODE, NOT VERIFIED: the sum puts a chance field in as its
        // average while it is subtracted here whole, so with dual wielding and chance-based
        // flat damage the hand panel comes out lower. We have no stand with such an item.
        double GearLanded(DbRecord rec, string source, string field)
        {
            Func<string, double, bool> landing = Landing;
            if (landing == null) return 1;
            string chanceField = CharacterTotals.ChanceSibling(field);
            if (chanceField == null) return 1;
            double c = CharacterTotals.Chance(rec, chanceField, 0);
            if (c <= 0 || c >= 100) return 1;
            return landing(CharacterTotals.ChanceKey(rec, chanceField, 0, source), c) ? 1 : 0;
        }

        void OtherHand(Pool pool, int hand) { OtherHand(pool, hand, -1); }

        void OtherHand(Pool pool, int hand, int sign)
        {
            if (hand < 0 || hand >= c.Weapons1.Count) return;
            Item it = c.Weapons1[hand];
            DbRecord rec = string.IsNullOrEmpty(it.BaseName) ? null : game.Record(it.BaseName);
            if (!IsHeld(rec)) return;   // a shield is held by a hand too — see IsHeld
            string name = NameOf(it);
            int ordinal = OrdinalOf(it, name);
            // The same eight parts CharacterTotals adds up, so what goes out here is
            // exactly what went in there.
            string[] parts = { it.BaseName, it.PrefixName, it.SuffixName,
                               it.RelicName, ItemViewFactory.RelicBonusOf(game, it),
                               it.AugmentName, it.ModifierName, it.AscensionName };
            for (int part = 0; part < parts.Length; part++)
            {
                DbRecord pr = string.IsNullOrEmpty(parts[part]) ? null : game.Record(parts[part]);
                if (pr == null) continue;
                Dictionary<string, double> known = exact == null ? null : exact.For(name, ordinal, part);
                // Each field falls back to its own minimum when the record has no
                // maximum — "21 cold damage" is one number, not a range. Taking
                // the larger of the two sums instead pairs the wrong ends and can
                // leave a pool whose maximum is below its minimum.
                //
                // THE SOURCE NAME IS ASSEMBLED THE SAME WAY as in `CharacterTotals`,
                // or this line's roll would differ here and there, and the second
                // hand's subtraction would take a different number out of the pool
                // than the one put in.
                string source = name + CharacterTotals.PartNames[part];
                foreach (Kind k in Kinds)
                    foreach (string family in new[] { "offensive", "offensiveBonus", "offensiveBase" })
                    {
                        double landed = GearLanded(pr, source, family + k.Field + "Min");
                        double one = Flat(pr, known, family + k.Field + "Min") * landed;
                        double top = Top(pr, known, family + k.Field) * landed;
                        if (roll != null)
                            one = top = roll.Pick(source, family + k.Field + "Min", one, top);
                        pool.Lo[k.Field] += sign * one;
                        pool.Hi[k.Field] += sign * top;
                    }
                double lo = Flat(pr, known, "offensiveElementalMin");
                double hi = Math.Max(Flat(pr, known, "offensiveElementalMax"), lo);
                if (roll != null) lo = hi = roll.Pick(source, "offensiveElementalMin", lo, hi);
                foreach (string t in new[] { "Fire", "Cold", "Lightning" })
                { pool.Lo[t] += sign * lo / 3; pool.Hi[t] += sign * hi / 3; }
            }
        }

        // The same as OtherHand but for a dot: takes out of the pool (sign −1) or puts back
        // in (+1) the damage over time THIS HAND'S WEAPON gives — with all of its parts,
        // like the flat direct damage.
        //
        // Found on `_Jien`. He holds two axes with different rolled bleeding, 16 and 19 per
        // second, and the panel shows them per hand SEPARATELY: 779 and 816. The difference
        // is exactly `3 × the multiplier` — the difference of the rolled numbers. We gave
        // both hands the same (383 and 383), because `GearDot` takes the character's sum,
        // where both axes sit.
        //
        // This was done for flat direct damage first (`OtherHand`), and the dot stayed
        // behind unnoticed, because `_Amakata`'s two swords are identical while `_Relo` and
        // `_Iman` have no weapon dot at all.
        void OtherHandDot(Pool pool, int hand, int sign) { OtherHandDot(pool, hand, sign, 8); }

        // `upto` — how many of the item's parts to count. Eight (all) is "what the hand
        // carries"; THREE (record, prefix, suffix) is "what THE WEAPON ITSELF carries", and
        // the difference between them decides whether a dot line is averaged with dual
        // wielding. See ByWeaponDot.
        void OtherHandDot(Pool pool, int hand, int sign, int upto)
        {
            if (hand < 0 || hand >= c.Weapons1.Count) return;
            Item it = c.Weapons1[hand];
            DbRecord rec = string.IsNullOrEmpty(it.BaseName) ? null : game.Record(it.BaseName);
            if (!IsHeld(rec)) return;   // a shield is held by a hand too — see IsHeld
            string name = NameOf(it);
            int ordinal = OrdinalOf(it, name);
            string[] parts = { it.BaseName, it.PrefixName, it.SuffixName,
                               it.RelicName, ItemViewFactory.RelicBonusOf(game, it),
                               it.AugmentName, it.ModifierName, it.AscensionName };
            for (int part = 0; part < parts.Length && part < upto; part++)
            {
                DbRecord pr = string.IsNullOrEmpty(parts[part]) ? null : game.Record(parts[part]);
                if (pr == null) continue;
                Dictionary<string, double> known = exact == null ? null : exact.For(name, ordinal, part);
                string source = name + CharacterTotals.PartNames[part];
                foreach (KeyValuePair<string, string> kv in DotName)
                {
                    string field = "offensiveSlow" + kv.Key;
                    double lo = Flat(pr, known, field + "Min");
                    if (Math.Abs(lo) < 0.0001) continue;
                    // A chance on an item's dot enters as its expectation — the same rule
                    // CharacterTotals added it by, or we would subtract something other
                    // than what was put in.
                    if (CharacterTotals.XorLoses(pr, field + "Min", 0)) continue;
                    // In the queue, on the same die as the sum's addend (MECHANICS 89b);
                    // in the panel, as the average, as before.
                    double share = Landing != null ? GearLanded(pr, source, field + "Min")
                                 : Share(CharacterTotals.Chance(pr, field + "Chance", 0), pr.Path, field);
                    double top = Top(pr, known, field);
                    // The same roll that put this line into the pool (see GearDot).
                    if (roll != null) lo = top = roll.Pick(source, field + "Min", lo, top);
                    pool.DotLo[kv.Key] += sign * lo * share;
                    pool.DotHi[kv.Key] += sign * top * share;
                }
            }
        }

        // How much dot of this type THE WEAPON ITSELF of this hand carries (record, prefix,
        // suffix — the component does not count, as in ByWeaponDot). Needed to tell "a dot
        // on both weapons" from "a dot on one only": the averaging applies in the first case
        // alone — see AddDot.
        double OtherHandDotOf(string field, int hand)
        {
            Pool one = new Pool();
            OtherHandDot(one, hand, +1, 5);
            return one.DotHi[field];
        }

        static bool IsWeapon(DbRecord rec)
        {
            string cls = rec.Class ?? "";
            return cls.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase) &&
                   cls.IndexOf("Shield", StringComparison.OrdinalIgnoreCase) < 0 &&
                   cls.IndexOf("Offhand", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // WHAT A HAND HOLDS AT ALL — a weapon OR a shield (and a caster's off-hand).
        //
        // It differs from `IsWeapon` in exactly that, and is needed wherever the question is
        // whether FLAT DAMAGE BELONGS TO A HAND, not whether the thing is a weapon. A shield
        // counts as a weapon nowhere — not for attack speed, not for "dual wielding", not
        // for weapon skills — but the 80 physical damage on it belongs to ITS hand exactly
        // as the 33 on a mace belongs to its own.
        //
        // MEASURED on a `_Relo` one-hand-and-shield save, and the measurement is direct: the
        // ordinary attack with a shield goes with the right hand ONLY, and the panel prints
        // it separately (right 2159 – 6165, left 2926 – 3661). We were putting the shield's
        // flat damage on the CHARACTER — and the character enters both hands — so the
        // ordinary attack came out 3442 – 7526, +59% at the bottom.
        //
        //     our physical base                 220 – 481
        //     minus the shield (Legionnaire's Rebuke)   82 –  87
        //     leaves                            138 – 394
        //     138 × 0.83927 × 18.6431 = 2159.2      game 2159
        //     394 × 0.83927 × 18.6431 = 6164.8      game 6165
        //
        // Both ends to the unit, and that with the multiplier and the conversion untouched —
        // they are the same as before.
        //
        // A CASTER'S OFF-HAND IS NOT INCLUDED HERE, AND THAT IS MEASUREMENT TOO, not caution.
        // `_Satirita` holds Uroboruuk's Effigy (`WeaponArmor_Offhand`) in the second hand,
        // and the flat elemental damage its component brings is kept by the game ON THE
        // CHARACTER: the ordinary attack shows fire 160, cold 146, lightning 145, and all
        // three matched to the unit while it stayed there. As soon as the off-hand was let
        // into a hand they dropped to 57 / 52 / 51.
        //
        // It reads exactly as it looks: a shield is SWUNG (Blitz has "180% off-hand weapon
        // damage"), while an off-hand swings nothing — it is merely carried, and its bonuses
        // belong to the character.
        static bool IsHeld(DbRecord rec)
        {
            string cls = rec == null ? "" : (rec.Class ?? "");
            return cls.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase) &&
                   cls.IndexOf("Offhand", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // Whether the second hand holds a SHIELD (or a caster's off-hand) — a thing that
        // belongs to the hand but does not count as a weapon. `DualWield` says "no" about
        // such a hand, and rightly: there is no dual wielding here. But there IS a second
        // hand.
        bool ShieldSecond()
        {
            if (c.Weapons1.Count < 2) return false;
            DbRecord rec = string.IsNullOrEmpty(c.Weapons1[1].BaseName)
                         ? null : game.Record(c.Weapons1[1].BaseName);
            return IsHeld(rec) && !IsWeapon(rec);
        }

        // Two one-handed weapons at once. The game only lets that happen when the
        // character has the skill for it, so having them on is the test.
        bool DualWield() { return IsDualWielding(game, c); }

        // A skill's weapon restriction. The game marks it with a field in the record itself,
        // so such skills identify themselves and no hand-written list is needed. The
        // database has four such fields (counted over all records/skills):
        //
        //     dualWieldOnly              53 skills  — two one-handers only
        //     unarmedOnly                27         — unarmed only
        //     dualRangedOnly             26         — two one-handed ranged only
        //     dualRangedOrAllRangedOnly   6         — two pistols OR any ranged
        //
        // Only the first was read. Ranged Expertise (`playerclass07/passive01`) is exactly
        // the Dual Blades case but for pistols: 645 pierce, +134% to it, +90% elemental,
        // +17% attack speed. It carries `dualRangedOrAllRangedOnly`, and its tooltip says
        // the same in words — a bonus for any ranged weapon, which also allows dual pistols.
        // With a sword in hand we were counting that bonus and the game was not.
        //
        // Returns the reason the skill does not apply, or null.
        public static string WeaponGate(GameData game, Character c, DbRecord rec)
        {
            if (rec == null) return null;
            // TWO FLAGS AT ONCE MEAN "OR", NOT "AND".
            //
            // `dualWieldOnly` (two one-handed melee) and `dualRangedOnly` (two one-handed
            // ranged) stand together on SIX records in the whole game, and read as "and"
            // they are satisfiable by NOBODY: no one holds dual melee and dual ranged at
            // once. One of the six is the berserker's Implements of War
            // (`playerclass10/passive01`), a first-row passive everybody takes.
            //
            // MEASURED — `saves/_555`, the percentage panel: the game prints
            // "+bleeding damage +1673%" while we had 1574. The shortfall is exactly 99 —
            // that passive's `offensiveSlowBleedingModifier` at its level (12 spent + 2 from
            // gear = 14, the fourteenth number of the row is 99). The "or" reading matches.
            bool dualMelee = rec.GetInt("dualWieldOnly") != 0;
            bool dualRanged = rec.GetInt("dualRangedOnly") != 0;
            if (dualMelee && dualRanged)
            {
                if (!IsDualWielding(game, c) && !IsDualRanged(game, c))
                    return Lang.T("totals.gate.needDualAny");
            }
            else
            {
                if (dualMelee && !IsDualWielding(game, c)) return Lang.T("totals.gate.needDualMelee");
                if (dualRanged && !IsDualRanged(game, c)) return Lang.T("totals.gate.needDualRanged");
            }
            if (rec.GetInt("dualRangedOrAllRangedOnly") != 0 &&
                !IsDualRanged(game, c) && !HasRanged(game, c))
                return Lang.T("totals.gate.needRanged");
            // "Two pistols OR a gun" — a fifth way, found by the `--skillcoverage` census
            // (4 skills). A single pistol does not satisfy it.
            if (rec.GetInt("dualRangedOrRanged2hOnly") != 0 &&
                !IsDualRanged(game, c) && !HasTwoHandedRanged(game, c))
                return Lang.T("totals.gate.needGuns");
            if (rec.GetInt("unarmedOnly") != 0 && !IsUnarmed(game, c))
                return Lang.T("totals.gate.needEmptyHands");
            return WeaponClassGate(game, c, rec);
        }

        // A sixth way the game restricts a skill by weapon, and it is not one field but a
        // SET of fields: the record lists the weapon classes it works with. The devotion
        // Hydra carries `Ranged1h 1` and `Ranged2h 1` — its `+5% attack speed` applies only
        // with ranged weapons. We counted it always, and on a bare `_Iman` the percentage
        // came out above the game's.
        //
        // The same names appear on ITEM records, where they mean something else — which
        // weapon a component can go into. Only skill records are looked at here.
        //
        // Thirteen flags — every class a weapon can have, including shield and off-hand:
        // 51 skills carry `Shield`, 19 carry `Offhand`. A shield and an off-hand do not
        // count as weapons and are invisible through `Hand()`, so the hands are inspected
        // directly here.
        static readonly string[] WeaponClasses = {
            "WeaponMelee_Sword", "WeaponMelee_Axe", "WeaponMelee_Mace",
            "WeaponMelee_Dagger", "WeaponMelee_Scepter",
            "WeaponMelee_Sword2h", "WeaponMelee_Axe2h", "WeaponMelee_Mace2h",
            "WeaponMelee_Spear2h", "WeaponHunting_Ranged1h", "WeaponHunting_Ranged2h",
            "WeaponArmor_Shield", "WeaponArmor_Offhand",
        };
        static readonly string[] ClassFlags = {
            "Sword", "Axe", "Mace", "Dagger", "Scepter",
            "Sword2h", "Axe2h", "Mace2h", "Spear2h", "Ranged1h", "Ranged2h",
            "Shield", "Offhand",
        };

        static string WeaponClassGate(GameData game, Character c, DbRecord rec)
        {
            bool any = false;
            for (int i = 0; i < ClassFlags.Length; i++)
                if (rec.GetInt(ClassFlags[i]) != 0) { any = true; break; }
            if (!any) return null;
            for (int hand = 0; hand < c.Weapons1.Count && hand < 2; hand++)
            {
                if (string.IsNullOrEmpty(c.Weapons1[hand].BaseName)) continue;
                DbRecord w = game.Record(c.Weapons1[hand].BaseName);
                string cls = w == null ? "" : (w.Class ?? "");
                for (int i = 0; i < WeaponClasses.Length; i++)
                    if (string.Equals(cls, WeaponClasses[i], StringComparison.OrdinalIgnoreCase) &&
                        rec.GetInt(ClassFlags[i]) != 0)
                        return null;
            }
            return Lang.T("totals.gate.wrongWeapon");
        }

        // The record of what is in that hand, or null when the hand is empty or
        // holds something that is not a weapon (a shield, an offhand).
        static DbRecord Hand(GameData game, Character c, int hand)
        {
            if (game == null || c == null || hand >= c.Weapons1.Count) return null;
            if (string.IsNullOrEmpty(c.Weapons1[hand].BaseName)) return null;
            DbRecord rec = game.Record(c.Weapons1[hand].BaseName);
            return rec != null && IsWeapon(rec) ? rec : null;
        }

        static bool IsRanged(DbRecord rec)
        { return rec != null && (rec.Class ?? "").IndexOf("Ranged", StringComparison.OrdinalIgnoreCase) >= 0; }

        public static bool IsDualRanged(GameData game, Character c)
        {
            return IsDualWielding(game, c) &&
                   IsRanged(Hand(game, c, 0)) && IsRanged(Hand(game, c, 1));
        }

        public static bool HasRanged(GameData game, Character c)
        { return IsRanged(Hand(game, c, 0)) || IsRanged(Hand(game, c, 1)); }

        public static bool HasTwoHandedRanged(GameData game, Character c)
        {
            for (int i = 0; i < 2; i++)
            {
                DbRecord r = Hand(game, c, i);
                if (r != null && string.Equals(r.Class ?? "", "WeaponHunting_Ranged2h",
                                               StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static bool IsUnarmed(GameData game, Character c)
        { return Hand(game, c, 0) == null && Hand(game, c, 1) == null; }

        // Holds two one-handers — the bonus sum needs the same question: a skill with
        // dualWieldOnly does not work at all without dual weapons.
        public static bool IsDualWielding(GameData game, Character c)
        {
            if (game == null || c == null || c.Weapons1.Count < 2) return false;
            for (int i = 0; i < 2; i++)
            {
                DbRecord rec = string.IsNullOrEmpty(c.Weapons1[i].BaseName)
                             ? null : game.Record(c.Weapons1[i].BaseName);
                if (rec == null || !IsWeapon(rec)) return false;
                if ((rec.Class ?? "").IndexOf("2h", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if ((rec.Class ?? "").IndexOf("Staff", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }
            return true;
        }

        // Which copy of that name this item is, counting the worn ones in the order
        // CharacterTotals walks them — the exact table is keyed the same way, so two
        // identical swords get the numbers each of them actually rolled.
        int OrdinalOf(Item it, string name)
        {
            List<Item> worn = new List<Item>();
            worn.AddRange(c.Equipment); worn.AddRange(c.Weapons1);
            int ordinal = 0;
            foreach (Item other in worn)
            {
                if (ReferenceEquals(other, it)) break;
                if (string.IsNullOrEmpty(other.BaseName)) continue;
                string n = NameOf(other);
                if (string.Equals(n, name, StringComparison.CurrentCultureIgnoreCase)) ordinal++;
            }
            return ordinal;
        }

        static double Flat(DbRecord rec, Dictionary<string, double> known, string field)
        {
            double got;
            if (known != null && known.TryGetValue(field, out got)) return got;
            return Own(rec, field);
        }

        // The top of one field's range: its maximum, or its minimum when there is
        // no maximum at all.
        static double Top(DbRecord rec, Dictionary<string, double> known, string prefix)
        {
            double max = Flat(rec, known, prefix + "Max");
            return Math.Abs(max) > 0.0001 ? max : Flat(rec, known, prefix + "Min");
        }

        // `offensiveSlow{T}Min` IS the damage per second — the duration beside it
        // says how long it runs, and dividing by it was wrong.
        //
        // Measured on `_Amakata` stripped of EVERYTHING, so the experiment has no spread at
        // all: the panel shows 25 poison and 410 decay per second while we gave 5 and 137 —
        // exactly five and three times less, and five and three are those dots' durations.
        // The decisive screenshot is Merciless Repertoire's tooltip on the same bare
        // character: 125 over 5 seconds there. So a skill's tooltip shows the dot over its
        // whole time (7 × m × 5) and the panel per second (7 × m), and the record holds the
        // latter.
        //
        // The old division survived because `_Relo`'s Cadence check took its numbers from
        // the panel while the block was not marked "per second" — the division and the
        // multiplication back cancelled out, and the error stayed invisible as long as every
        // dot source had the same duration. Both are fixed now.
        //
        // The sources' durations still do not add up: the duration is needed only to write
        // "over N sec." and to count the damage over the whole dot.
        void GearDot(Pool pool, string type)
        {
            TotalsEntry min, max, dur;
            totals.Fields.TryGetValue("offensiveSlow" + type + "Min", out min);
            totals.Fields.TryGetValue("offensiveSlow" + type + "Max", out max);
            totals.Fields.TryGetValue("offensiveSlow" + type + "DurationMin", out dur);

            // "ONE OF" FROM THE CHARACTER'S SUM — BY THE GROUP'S ROLL (MECHANICS 85a). The
            // sum added only the group's first field, and already as an expectation
            // (Overload's burn at a third); the other fields are not in it at all. A hit
            // removes that first field and puts in whole whatever came up on the group's
            // die. Without a roll (the panel) everything stays as before.
            double xLo = 0, xHi = 0, xSecs = 0;
            HashSet<string> redrawn = null;
            if (XorWhole)
                foreach (CharacterTotals.XorSource g in totals.XorSources)
                {
                    if (string.Equals(CharacterTotals.XorFirst(g.Rec, "offensiveSlow", g.Slot), type, StringComparison.Ordinal))
                    {
                        if (redrawn == null) redrawn = new HashSet<string>(StringComparer.Ordinal);
                        redrawn.Add(g.Source);
                    }
                    if (!string.Equals(XorPick(g.Rec, g.Slot, "offensiveSlow"), type, StringComparison.Ordinal)) continue;
                    double low = AttackSkill.Value(g.Rec, "offensiveSlow" + type + "Min", g.Slot);
                    if (Math.Abs(low) < 0.0001) continue;
                    double high = Math.Max(AttackSkill.Value(g.Rec, "offensiveSlow" + type + "Max", g.Slot), low);
                    // Without a roll (the check's substitution) — the field's whole range.
                    double gotLo = roll != null ? roll.Pick(g.Source, "offensiveSlow" + type + "Min", low, high) : low;
                    double gotHi = roll != null ? gotLo : high;
                    xLo += gotLo; xHi += gotHi;
                    double gd = AttackSkill.Value(g.Rec, "offensiveSlow" + type + "DurationMin", g.Slot);
                    xSecs = Math.Max(xSecs, gd > 0 ? gd : 1);
                }
            if (min == null && max == null && xHi <= 0) return;

            Dictionary<string, double> maxOf = FirstOfEach(max), secsOf = FirstOfEach(dur);
            TotalsEntry from = min ?? max;
            Func<string, double, bool> landing = Landing;
            double lo = xLo, hi = xHi, secs = xSecs;
            for (int i = 0; from != null && i < from.Terms.Count; i++)
            {
                KeyValuePair<string, double> term = from.Terms[i];
                if (redrawn != null && redrawn.Contains(term.Key)) continue;
                // AN ITEM'S CHANCE-BASED DOT IS ALL OR NOTHING (MECHANICS 89b). If it did
                // not come up there is neither damage nor duration: it must not extend
                // someone else's duration.
                double on = from.TermOn(i, landing);
                if (Math.Abs(on) < 1e-12 && Math.Abs(term.Value) > 1e-12) continue;
                double scale = Math.Abs(term.Value) > 1e-12 ? on / term.Value : 1;
                double d;
                if (!secsOf.TryGetValue(term.Key, out d) || d <= 0) d = 1;
                double top;
                if (!maxOf.TryGetValue(term.Key, out top) || top < term.Value) top = term.Value;
                top *= scale;
                // An item's dot is rolled like direct damage (MECHANICS 31): the line has a
                // range of its own, and one number comes out of it per hit.
                double partLo = roll != null ? roll.Pick(term.Key, "offensiveSlow" + type + "Min", on, top) : on;
                lo += partLo;
                hi += roll != null ? partLo : top;
                secs = Math.Max(secs, d);
            }
            // A FORM modifier's dot is a character addend too, not a skill one (FormFlat).
            double fLo = FormFlat("offensiveSlow" + type + "Min");
            double fHi = FormFlat("offensiveSlow" + type + "Max");
            if (Math.Abs(fLo) > 0.0001 || Math.Abs(fHi) > 0.0001)
            {
                lo += fLo; hi += Math.Max(fHi, fLo);
                double fd = FormFlat("offensiveSlow" + type + "DurationMin");
                if (fd > 0) secs = Math.Max(secs, fd);
            }
            pool.DotLo[type] = lo; pool.DotHi[type] = hi; pool.DotSecs[type] = secs;
        }

        // The same for the chosen skill: every record of it is one source with a
        // duration of its own.
        void SkillDot(Pool pool, string type) { SkillDot(pool, type, false); }

        void SkillDot(Pool pool, string type, bool oneHand)
        {
            double lo = 0, hi = 0, secs = 0;
            foreach (SkillPart p in Skill.Parts)
            {
                if (p.Temporary && !withTemporary) continue;
                if (p.FromForm) continue;   // a form modifier goes into the character's sum, see FormDot
                if (p.OwnOnce != oneHand) continue;
                double charge = AttackSkill.ChargeOf(p, atCharge);
                double one = AttackSkill.Value(p.Rec, "offensiveSlow" + type + "Min", p.Slot) * charge;
                double top = AttackSkill.Value(p.Rec, "offensiveSlow" + type + "Max", p.Slot) * charge;
                if (top < one) top = one;
                if (Math.Abs(one) < 0.0001 && Math.Abs(top) < 0.0001) continue;
                double d = AttackSkill.Value(p.Rec, "offensiveSlow" + type + "DurationMin", p.Slot);
                if (d <= 0) d = 1;
                if (XorOut(p.Rec, "offensiveSlow" + type + "Min", p.Slot)) continue;
                double share = PartShare(p.Rec, p.Slot, "offensiveSlow" + type + "Min",
                                         "offensiveSlow" + type + "Chance", "offensiveSlow" + type);
                lo += one * share; hi += top * share;
                secs = Math.Max(secs, d);
            }
            pool.DotLo[type] = lo; pool.DotHi[type] = hi; pool.DotSecs[type] = secs;
        }

        // A "min – max" pair summed source by source rather than field by field.
        //
        // Adding the two fields separately loses the pairing: a source that gives
        // "21 cold damage" has a minimum and no maximum, so it lands in one sum
        // and not the other, and the range comes out narrower than it is — or
        // upside down. On `_Amakata`'s dagger the cold base 55–95 collapsed to a
        // single number that way. Each source's own top is its maximum, or its
        // minimum when it has none.
        void PairSum(string minField, string maxField, out double lo, out double hi)
        {
            lo = hi = 0;
            TotalsEntry min, max;
            totals.Fields.TryGetValue(minField, out min);
            totals.Fields.TryGetValue(maxField, out max);
            if (min == null && max == null) return;
            // Pairs are matched BY ORDER, not by name: two identical weapons each give their
            // own number for a rolled field (MECHANICS 95).
            Dictionary<string, List<double>> tops = EachOf(max), bottoms = EachOf(min);
            Dictionary<string, int> tookTop = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, int> tookBottom = new Dictionary<string, int>(StringComparer.Ordinal);
            Func<string, double, bool> landing = Landing;
            if (min != null)
                for (int i = 0; i < min.Terms.Count; i++)
                {
                    KeyValuePair<string, double> term = min.Terms[i];
                    double top = NextOf(tops, tookTop, term.Key);
                    if (double.IsNaN(top) || top < term.Value) top = term.Value;
                    // A CHANCE-BASED ADDEND IS ALL OR NOTHING (MECHANICS 89b): the sum added
                    // it as an average, a hit rolls the chance, and the top of the range
                    // takes the same share.
                    double on = min.TermOn(i, landing);
                    if (Math.Abs(term.Value) > 1e-12) top *= on / term.Value;
                    // A HIT is being counted, not the panel: the line's own range is rolled
                    // and both ends become the rolled number (MECHANICS 31).
                    if (roll != null)
                    {
                        double got = roll.Pick(term.Key, minField, on, top);
                        lo += got; hi += got;
                    }
                    else { lo += on; hi += top; }
                }
            // A source with only a maximum is not a thing the records do, but if
            // one turns up it belongs in both ends rather than nowhere.
            if (max != null)
                for (int i = 0; i < max.Terms.Count; i++)
                {
                    // This addend has no bottom of its own — then it goes into both ends;
                    // counted by order, so a second copy of an item does not hide behind
                    // the first.
                    if (!double.IsNaN(NextOf(bottoms, tookBottom, max.Terms[i].Key))) continue;
                    double v = max.TermOn(i, landing);
                    lo += v; hi += v;
                }
        }

        // The first value each source contributed. Two copies of the same item
        // give the same number twice, so the first of them is the one that goes
        // with the first of its damage terms.
        //
        // THAT IS HOW IT USED TO BE, AND IT IS WRONG WHEN A FIELD IS ROLLED: `_Tapina` holds
        // two Mythical Brutallax whose second flat addition rolled DIFFERENTLY — 16–24 and
        // 12–19. Matching by name gave both the first one's top (24 and 24), and the damage
        // top came out five points of base above the game's on every skill (analysis —
        // MECHANICS 95). Pairs are now matched BY ORDER: `EachOf` + `NextOf`.
        static Dictionary<string, double> FirstOfEach(TotalsEntry e)
        {
            Dictionary<string, double> made = new Dictionary<string, double>(StringComparer.Ordinal);
            if (e == null) return made;
            foreach (KeyValuePair<string, double> t in e.Terms)
                if (!made.ContainsKey(t.Key)) made[t.Key] = t.Value;
            return made;
        }

        // Every number of each source IN ORDER — so a second copy of an item takes its own
        // second number, not the first one.
        static Dictionary<string, List<double>> EachOf(TotalsEntry e)
        {
            Dictionary<string, List<double>> made =
                new Dictionary<string, List<double>>(StringComparer.Ordinal);
            if (e == null) return made;
            foreach (KeyValuePair<string, double> t in e.Terms)
            {
                List<double> list;
                if (!made.TryGetValue(t.Key, out list)) { list = new List<double>(); made[t.Key] = list; }
                list.Add(t.Value);
            }
            return made;
        }

        // This source's next number in order; NaN when it has no number left.
        static double NextOf(Dictionary<string, List<double>> all, Dictionary<string, int> seen, string key)
        {
            int at;
            seen.TryGetValue(key, out at);
            seen[key] = at + 1;
            List<double> list;
            if (!all.TryGetValue(key, out list) || at >= list.Count) return double.NaN;
            return list[at];
        }

        // Flat elemental also adds up BY SOURCE, not by field.
        //
        // One component has only a minimum (5 elemental), Ignaffar's Combustion has the pair
        // 8–25, a devotion has both. Adding the fields separately puts a source without a
        // maximum into one sum and not the other, and on `_Iman` the maximum comes out BELOW
        // the minimum — 34 against 35 — after which `Math.Max` collapses the range into a
        // single number.
        //
        // This is the very error the per-type fields had and where it was fixed long ago
        // (see PairSum); elemental stayed on the old path. In the game it showed like this:
        // `_Iman`'s cold read a flat 341 against the game's 341 – 517, and the tops of fire
        // and lightning were 6 and 9 per cent low.
        void ElementalPair(out double lo, out double hi)
        {
            lo = hi = 0;
            foreach (string family in new[] { "offensive", "offensiveBonus" })
            {
                double l, h;
                PairSum(family + "ElementalMin", family + "ElementalMax", out l, out h);
                lo += l; hi += h;
            }
        }

        // A flat elemental bonus is three equal parts, one to each of fire, cold
        // and lightning; the percentage bonus goes to each of them whole.
        static void Elemental(Pool pool, double lo, double hi)
        {
            hi = Math.Max(lo, hi);
            foreach (string t in new[] { "Fire", "Cold", "Lightning" })
            { pool.Lo[t] += lo / 3; pool.Hi[t] += hi / 3; }
        }

        // A dot is shown per second, which is what the record holds outright. A
        // bonus to the duration does not change this number — it makes the dot run
        // longer, and the total over it goes up by exactly that much.
        //
        // A DOT WITH DUAL WIELDING (MECHANICS 14v3, measured twice). The "per hit" line takes
        // not the sum of the hands but the UNSPLIT character sum (`whole`: both weapons and
        // the character's dot, each source ONCE) and multiplies it by the same `0.75` as the
        // direct damage. That is why it is below either hand: the per-hand panel shows "this
        // hand's dot + the character's dot" whole.
        //
        //   `_Hamarina`, two Mythical Bloodsong, bleeding, m = 31.08:
        //     right hand   100 + 71 = 171  × 31.08 = 5315   game 5315
        //     left hand    100 + 77 = 177  × 31.08 = 5501   game 5501
        //     per hit  0.75 × (100+71+77) = 186  × 31.08 = 5781   game 5781
        //   `_Jien` by the same rule (by switching Primal Bond off):
        //     0.75 × (16 + 19 + 47) × 12.3616 = 760.2   game 760
        //     0.75 × (16 + 19)      × 10.9375 = 287.1   game 287   (the aura taken off)
        //
        // THE QUARTER APPLIES TO THE SKILL'S OWN DOT TOO — the same cycle averaging.
        // `_Hamarina`'s Cadence (weapon share 442%, own dot 418):
        //     (0.75 × 248 × 4.42 + 0.75 × 418) × 31.08 = 35295   game 35295
        // while without the quarter on the own part it would be 38543. The own dot is NOT
        // doubled, unlike direct damage (`ownShare` = 1.5): a dot from one source does not
        // stack, the second hit only refreshes its duration.
        //
        // THREE BOUNDARIES, EACH MEASURED:
        //  * the quarter applies ONLY WHEN THE WEAPON ITSELF CARRIES THIS DOT TYPE (its
        //    record, not a component screwed into it — see ByWeaponDot). On `_Amakata` with
        //    two swords the poison and decay come from auras, and the panel shows them whole
        //    (57 and 1834) — with the quarter it would be 43 and 1375;
        //  * the skill's own part takes the quarter only if the skill HAS a weapon share.
        //    `_Jien`'s Devouring Swarm makes no swings, and the game prints its 915 bleeding
        //    whole;
        //  * a share that hits with ONE hand (`wpctOne`) gets no averaging at all — it
        //    multiplies its own hand's pool, like direct damage. `_Hamarina`'s Ring of Steel
        //    (220% one hand): 171 × 31.08 × 2.2 + own = 42959 against the game's 42959,
        //    while with the quarter and the sum of both hands it would be 46744.
        //
        // A SECOND READING OF THE MARK WAS DISPROVED BY EXPERIMENT, worth remembering so it
        // is not reopened. The reading was "it is averaged when THE HANDS' DOTS DIFFER"
        // (`_Hamarina` 71 and 77, `_Jien` 16 and 19 — averaging; `_Itaan` 4 and 4,
        // `_Amakata` 0 and 0 — none). No earlier stand told it apart from ours. The user
        // built a dot save — the same `_Hamarina` but with IDENTICAL bleeding of 237 over
        // 3 sec. on both swords — and both numbers were named before the screenshot:
        //
        //   per hand  100 + 89 = 189             × 31.08 = 5874   game 5874
        //   per hit   0.75 × (100+89+89) = 208.5 × 31.08 = 6480   game 6480
        //   "the hands differ" would give                  5874   off by 10%
        //
        // `stand-dot-equal.tsv` guards this — the only check in the battery that goes red if
        // the mark is swapped.
        //
        // This used to say "not measured": what to do when only ONE of two weapons carries
        // this dot type. The user built the case (a `_Hamarina` dot save) and the quarter
        // does not apply there — analysis below, at the mark itself, and in MECHANICS 14d6.
        // `wpctTwo` — THE SHARE THAT STRIKES WITH THE OFF HAND, and it takes the OFF HAND'S
        // dot pool. Until 16 October there was no such parameter: every one-hand share went
        // into the dot from the FIRST hand's pool, and the comment at the call site said what
        // would expose it — "as soon as a character turns up whose hands differ in dot AND
        // whose skill hits with one hand, this will show as a number". `_RuneTask` is that
        // character, and the number is +11.7% (MECHANICS 150).
        void AddDot(Kind k, string name, Pool gear, Pool second, Pool whole, Pool weaponOwn,
                    Pool own, Pool ownOne, double wpct, double wpctOne, double wpctTwo,
                    double wpctBest)
        {
            // With no second pool there is no off hand to take it from, and the old lumping
            // is the only thing left. The break flag restores it everywhere.
            if (second == null || BreakOffHandDot) { wpctOne += wpctTwo; wpctTwo = 0; }
            // The BLAST's share takes the unsplit sum (MECHANICS 167). With a single weapon
            // that sum IS the hand, so the two readings coincide and nothing moves; the
            // break flag puts the share back on the first hand everywhere.
            if (second == null || BreakBlastBestHand) { wpctOne += wpctBest; wpctBest = 0; }
            double m = Multiplier(k, true);
            double gLo = gear.DotLo[k.Field], gHi = gear.DotHi[k.Field];
            // The pool of the share that hits with BOTH hands. With a single weapon it is
            // the same hand pool (there is nothing to subtract, `whole` equals it), so the
            // other stands do not move.
            double bothLo = gLo, bothHi = gHi, ownK = 1;
            if (second != null && ByWeaponDot(k, weaponOwn))
            {
                // THE QUARTER DOES NOT APPLY IF ONLY ONE OF THE TWO WEAPONS CARRIES THE DOT.
                //
                // The boundary was named but NOT MEASURED for a while. Then the case turned
                // up: the user built a `_Hamarina` dot save, replacing one of the two
                // identical swords with a Mythical Wrath of the Ascendant that has no
                // bleeding. The answer is the same as for direct damage (MECHANICS 14d4):
                // the second swing does not deal this dot, there is nothing to average with.
                //
                //   per hand   right 5358, left 2835          both match EXACTLY
                //   per hit    5358 = 189 × 28.35             game 5358
                //   with the quarter 0.75 × 189 × 28.35 = 4019   off by exactly 0.75
                //
                // The breakdown: 100 is the CHARACTER's dot (it is in both hands), 89 is the
                // sword's own dot with its component. `stand-hamarina-dot` guards it.
                bool oneWeaponDot = weaponOwn.DotHi[k.Field] > 0.0001 &&
                                    (OtherHandDotOf(k.Field, 0) <= 0.0001) !=
                                    (OtherHandDotOf(k.Field, 1) <= 0.0001);
                double dual = oneWeaponDot ? 1 : DualShare;
                bothLo = dual * whole.DotLo[k.Field];
                bothHi = dual * whole.DotHi[k.Field];
                if (Math.Abs(wpct + wpctOne + wpctTwo) > 0.0001) ownK = dual;
                if (whole.DotHi[k.Field] > 0.0001 && Math.Abs(wpct) > 0.0001 && !oneWeaponDot)
                    // `name` is a language-file key; the note takes the word.
                    dualDot.Add(Lang.T(name));
            }
            // What arrived by the character's conversion gets no skill percentage — the same
            // rule as for direct damage (MECHANICS 19i). The both-hands pool takes its
            // arrived part from `whole`, the pool it was taken from itself.
            //
            // ON A DOT THIS IS NOT MEASURED, and MECHANICS 19i says so: all twelve numbers
            // the rule was derived from are direct damage and "per hand" lines. It is
            // applied here because the rule is one, not two; no stand moved because of it.
            // The measurement will turn up where the "two panels count dots differently"
            // question is settled.
            double mIn = Multiplier(k, true, false);
            double mSk = Multiplier(k, true, true, false);
            double gInLo = In(gear.InDotLo, k.Field), gInHi = In(gear.InDotHi, k.Field);
            double bothInLo = gInLo, bothInHi = gInHi;
            double gCaLo = In(gear.CameDotLo, k.Field), gCaHi = In(gear.CameDotHi, k.Field);
            double bothCaLo = gCaLo, bothCaHi = gCaHi;
            // THE BLAST'S POOL — the same unsplit sum, but WITHOUT the 0.75: it fires once
            // per shot, so there is no swing cycle to average over (MECHANICS 167). Where
            // the dot is not the weapon's at all, the hand pool and the unsplit sum hold the
            // same character dot and the two readings coincide.
            double blastLo = gLo, blastHi = gHi;
            double blastInLo = gInLo, blastInHi = gInHi, blastCaLo = gCaLo, blastCaHi = gCaHi;
            if (second != null && ByWeaponDot(k, weaponOwn))
            {
                bothInLo = DualShare * In(whole.InDotLo, k.Field);
                bothInHi = DualShare * In(whole.InDotHi, k.Field);
                bothCaLo = DualShare * In(whole.CameDotLo, k.Field);
                bothCaHi = DualShare * In(whole.CameDotHi, k.Field);
                blastLo = whole.DotLo[k.Field];
                blastHi = whole.DotHi[k.Field];
                blastInLo = In(whole.InDotLo, k.Field);
                blastInHi = In(whole.InDotHi, k.Field);
                blastCaLo = In(whole.CameDotLo, k.Field);
                blastCaHi = In(whole.CameDotHi, k.Field);
            }
            // The arrived part's multiplier is PER POOL (a percentage that moved travels with
            // its source, analysis at `MIn`). A dot has the same four pools.
            //
            // Taking the weights from `whole` (the both-hands pool the arrived part itself
            // comes from for a dual dot) was tried — the numbers did NOT change by a single
            // unit, so it is left as it is: no change without a measurement. If the dot
            // remainder (+0.1% on `_Itaan`) is ever taken up, this is the first thing to
            // check again, together with the fact that a dot's 0.75 pairing is counted
            // differently (MECHANICS 14d1).
            double mInBothLo = MIn(mIn, k, true, false, gear, second);
            double mInBothHi = MIn(mIn, k, true, true, gear, second);
            double mInGearLo = MIn(mIn, k, true, false, gear, null);
            double mInGearHi = MIn(mIn, k, true, true, gear, null);
            double fromWeaponLo = Both(bothLo, bothInLo, bothCaLo, m, mInBothLo, mSk) * wpct +
                                  Both(gLo, gInLo, gCaLo, m, mInGearLo, mSk) * wpctOne;
            double fromWeaponHi = Both(bothHi, bothInHi, bothCaHi, m, mInBothHi, mSk) * wpct +
                                  Both(gHi, gInHi, gCaHi, m, mInGearHi, mSk) * wpctOne;
            if (wpctBest > 0.0001)
            {
                fromWeaponLo += Both(blastLo, blastInLo, blastCaLo, m, mInBothLo, mSk) * wpctBest;
                fromWeaponHi += Both(blastHi, blastInHi, blastCaHi, m, mInBothHi, mSk) * wpctBest;
            }
            // THE OFF-HAND SHARE, FROM THE OFF HAND'S OWN POOL — the same thing the direct
            // damage above has done since August (`wpctTwo` with `second`), only the dot had
            // never been given it.
            //
            // MEASURED ON `_RuneTask`, Aegis of Menhir (MECHANICS 150). It strikes with the
            // shield with an additional weapon, and that character's hands differ in burn:
            // 928 on the scepter hand against 309 on the shield's, because the scepter's
            // PREFIX carries 27 burn and the shield carries none of its own.
            //
            //   pool from the first hand   40.5 + 66.5 (the shield's own Physical → Fire 100%
            //                              turns the character's physical dot into fire) = 107
            //   pool from the off hand     13.5 + 66.5 = 80
            //   ours was      107 × 22.9015 × 4.95 = 12129.78  + own 16947.11 = 29077  (+11.7%)
            //   with the off hand  80 × … = 9069.0             + own 16947.11 = 26016
            //                                                       the game prints 26027
            //
            // AND THE SKILL'S OWN HALF IS CONFIRMED FROM THE OTHER SIDE, by the user's two
            // tooltips: Reprisal gives "35742 burn over 3 sec." and the shield's own Aegis
            // modifier "15123 over 3 sec.", that is 11914 + 5041 = 16955 per second against
            // our 16947.11. So the half that was NOT in question is right, and the whole gap
            // sat in the weapon pool.
            if (wpctTwo > 0.0001)
            {
                fromWeaponLo += Both(second.DotLo[k.Field], In(second.InDotLo, k.Field),
                                     In(second.CameDotLo, k.Field), m,
                                     MIn(mIn, k, true, false, second, null), mSk) * wpctTwo;
                fromWeaponHi += Both(second.DotHi[k.Field], In(second.InDotHi, k.Field),
                                     In(second.CameDotHi, k.Field), m,
                                     MIn(mIn, k, true, true, second, null), mSk) * wpctTwo;
            }
            double fromSkillLo = Both(own.DotLo[k.Field], In(own.InDotLo, k.Field),
                                      In(own.CameDotLo, k.Field), m,
                                      MIn(mIn, k, true, false, own, null), mSk) * ownK +
                                 Both(ownOne.DotLo[k.Field], In(ownOne.InDotLo, k.Field),
                                      In(ownOne.CameDotLo, k.Field), m,
                                      MIn(mIn, k, true, false, ownOne, null), mSk);
            double fromSkillHi = Both(own.DotHi[k.Field], In(own.InDotHi, k.Field),
                                      In(own.CameDotHi, k.Field), m,
                                      MIn(mIn, k, true, true, own, null), mSk) * ownK +
                                 Both(ownOne.DotHi[k.Field], In(ownOne.InDotHi, k.Field),
                                      In(ownOne.CameDotHi, k.Field), m,
                                      MIn(mIn, k, true, true, ownOne, null), mSk);
            // ...and the retaliation DOT share, if the skill gives one (MECHANICS 14d9). It
            // is already finished: the retaliation multipliers are inside it and the hit's
            // multipliers are not applied — exactly as for the direct part above.
            double retalDotLo = retalIntoDot == null || !retalIntoDot.DotLo.ContainsKey(k.Field)
                              ? 0 : retalIntoDot.DotLo[k.Field];
            double retalDotHi = retalIntoDot == null || !retalIntoDot.DotHi.ContainsKey(k.Field)
                              ? 0 : retalIntoDot.DotHi[k.Field];
            double lo = fromWeaponLo + fromSkillLo + retalDotLo;
            double hi = fromWeaponHi + fromSkillHi + retalDotHi;
            if (hi > 0.5)
                Trace.Add("line " + k.Field + "Dot: from the weapon " + Exact(fromWeaponLo) + "–" +
                          Exact(fromWeaponHi) + ", the skill's own " + Exact(fromSkillLo) + "–" +
                          Exact(fromSkillHi) +
                          // Only when the both-hands pool was actually used: for a skill
                          // that hits with ONE hand it is computed but unused, and a
                          // caption about it would mislead.
                          (Math.Abs(bothLo - gLo) > 0.0001 && Math.Abs(wpct) > 0.0001
                               ? "   (dual wield: 0.75 × " + Exact(whole.DotLo[k.Field]) + " instead of " +
                                 Exact(gLo) + " per hand)" : ""));
            // The seconds to print: the longest of the sources that carry it, and
            // both when they disagree. A skill with no share of the weapon takes
            // nothing from the gear pool, so its duration has no business here —
            // Siphon Souls printed "1 and 3 s." while the three belonged to a
            // weapon that does not enter this hit at all.
            double fromGear = (gear.DotHi[k.Field] > 0 || (wpctTwo > 0.0001 && second.DotHi[k.Field] > 0)) &&
                              wpct + wpctOne + wpctTwo != 0 ? gear.DotSecs[k.Field] : 0;
            double fromSkill = own.DotHi[k.Field] > 0 ? own.DotSecs[k.Field]
                             : ownOne.DotHi[k.Field] > 0 ? ownOne.DotSecs[k.Field] : 0;
            double secs = Math.Max(fromGear, fromSkill);
            string text = Fmt(secs);
            if (fromGear > 0 && fromSkill > 0 && Math.Abs(fromGear - fromSkill) > 0.01)
                text = Lang.T("damage.secondsPair", Fmt(Math.Min(fromGear, fromSkill)), Fmt(secs));
            Add(k.Field + "Dot", name, lo, hi, true, secs, text);
        }

        // Whether THE WEAPON ITSELF carries this dot type — the mark by which the "per hit"
        // line decides whether to be averaged (see AddDot).
        //
        // "The weapon itself" is its record with prefix and suffix. A COMPONENT screwed into
        // it does not count, and that is measured: `_Itaan`'s two swords carry no poison of
        // their own, and all the weapon poison is 4 per second from two identical Envenomed
        // Weapons components. The panel shows the same 7623 (= 219 × 34.8) both per hand and
        // per hit, so there is no averaging at all; with averaging it would be
        // 0.75 × 223 × 34.8 = 5817, off by 24%.
        //
        // The component does BELONG to its hand, and the same 7623 proves it: were its 4
        // shared, the per-hand figure would be 223, that is 7763. So the "the weapon carries
        // it" mark and the "it belongs to the hand" mark are DIFFERENT and need not agree.
        //
        // The `weaponOwn` pool is built from the first three parts of both weapons and went
        // through the same conversions as the hand pools — so a type a dot MOVED INTO counts
        // as the weapon's exactly as far as its root did.
        bool ByWeaponDot(Kind k, Pool weaponOwn)
        {
            return weaponOwn.DotLo[k.Field] > 0.0001 || weaponOwn.DotHi[k.Field] > 0.0001;
        }

        // Conversions take shares of the base, and the shares add up rather than
        // apply one after another. Armour piercing is the last one and takes what
        // is left of the physical.
        // Conversion runs in TWO PASSES, and the first is THE SKILL'S OWN conversion.
        //
        // It takes its type whole and ahead of everyone, leaving the character's conversions
        // only the remainder. Measured on `_Iman`'s Sacred Strike: it has
        // `physical → elemental 100%`, while the character already moves 117% out of
        // physical (49% into lightning, 48% into fire, 10% into elemental, 10% into life).
        // Three panel lines settle it unambiguously — each shift is CONSTANT at both ends of
        // the range, so it is a move, not a multiplier:
        //
        //   cold       was 2898 – 4432    game 3493 – 5027   short by exactly 595
        //   fire       was 23854 – 30267  game 23543 – 29956 over by exactly 311
        //   lightning  was 14461 – 20375  game 14143 – 20057 over by exactly 318
        //
        // All the physical goes into elemental, elemental splits into three equal shares,
        // and the predicted shifts come out −9.94 / −10.74 / +28.84 in units of base against
        // the needed −9.97 / −10.77 / +28.81. Three numbers to 0.1%.
        //
        // Both other orders were tried and both are wrong: "shares into a common pot" (217%)
        // gives half the shift, "the skill gets the remainder" gives none.
        //
        // The second pass does NOT touch what arrived in the first — the same "no chains"
        // rule, only between passes.
        void Convert(Pool pool) { Convert(pool, -1); }

        // WHICH HAND THE SKILL'S OWN DAMAGE IS STRUCK WITH — 0, 1, or −1 for "not one hand",
        // where the character's sum is taken as before. Read from the skill's OWN record,
        // which is always the first of its parts: a blast marked "one hand" hangs on a skill
        // that swings with both (Explosive Strike on Fire Strike), and that must not make
        // the whole blow one-handed. A hand that is not named (`BestHand`) names none here
        // either. With one weapon there is no choice to make.
        int OwnHand(Pool second)
        {
            if (second == null || Skill == null || Skill.Parts.Count == 0) return -1;
            SkillPart own = Skill.Parts[0];
            if (!own.WeaponOneHand || own.BestHand) return -1;
            return own.SecondHand ? 1 : 0;
        }

        // ARMOUR PIERCING BELONGS TO THE HAND, NOT TO THE CHARACTER.
        //
        // `offensivePierceRatioMin` is a WEAPON field: Mythical Bloodsong carries 100, that
        // is, it turns all of its physical into pierce. We summed it over all the gear, and
        // an earlier `_Hamarina` with TWO such swords came out at 200% — nonsense that
        // stayed invisible only because the share is capped at a hundred anyway and both
        // hands gave the same answer.
        //
        // It became visible as soon as the character had DIFFERENT swords. The user built a
        // `_Hamarina` dot save: Mythical Bloodsong (piercing 100) and Mythical Wrath of the
        // Ascendant (no piercing at all). It must be counted per hand, and then both ends of
        // pierce match TO THE UNIT:
        //
        //   hand 1: own pierce 72 – 80 + all of its physical 81 – 124 = 153 – 204
        //   hand 2: own pierce 62 – 70, its physical STAYS physical
        //   0.75 × (153 + 62) × 13.1424 = 2119     game 2119   EXACT
        //   0.75 × (204 + 70) × 13.1424 = 2701     game 2701   EXACT
        //
        // The old count (100% on everything) gave 2563 – 3184, off by 21 and 18%, and there
        // was no physical line at all against the game's 694 – 755.
        //
        // `hand` = 0 or 1 — whose piercing to take; −1 means "the pool does not belong to a
        // hand" (the skill's own damage, the character's sum), where the total sum is taken
        // as before.
        void Convert(Pool pool, int hand)
        {
            // Both passes and all the armour piercing are done by ONE function for the whole
            // editor — `Conversion` (see its file). What stays here is only what the
            // character's calculation knows: which pairs to take and where the piercing
            // comes from.
            List<Move> own = SkillConversions();
            if (own.Count > 0) Conversion.Pass(pool, own, true);
            Conversion.Pass(pool, Conversions(), false);
            Conversion.Pierce(pool, PierceRatio(hand) / 100.0);
        }

        // This hand's armour piercing. With no hand (the pool is not a hand's) — the
        // character's sum.
        double PierceRatio(int hand)
        {
            if (hand < 0 || hand >= c.Weapons1.Count) return Sum("offensivePierceRatioMin");
            if (string.IsNullOrEmpty(c.Weapons1[hand].BaseName)) return Sum("offensivePierceRatioMin");
            DbRecord rec = game.Record(c.Weapons1[hand].BaseName);
            if (rec == null || !IsWeapon(rec)) return Sum("offensivePierceRatioMin");
            // The field sits on the weapon itself; no other gear gives it (checked by the
            // source breakdown: on `_Hamarina` all 100 come from the sword).
            return Own(rec, "offensivePierceRatioMin");
        }

        // THE CHOSEN SKILL'S OWN conversions: they sit on its record and on its parts. Read
        // apart from the character's because they fire earlier.
        List<Move> skillConversions;

        List<Move> SkillConversions()
        {
            if (skillConversions != null) return skillConversions;
            List<Move> made = new List<Move>();
            skillConversions = made;
            if (Skill == null) return made;
            foreach (SkillPart p in Skill.Parts)
            {
                if (p.Temporary && !withTemporary) continue;
                Conversion.Read(p.Rec, null, made, "the skill itself: " + p.Who, p.Slot);
            }
            return made;
        }

        // What an item's part is called in a source caption: the same order as `partsOf`
        // below.
        static string Part(int i)
        {
            string[] what = { "", " (prefix)", " (suffix)", " (component)", " (relic bonus)",
                              " (augment)", " (modifier)", " (ascension)" };
            return i >= 0 && i < what.Length ? what[i] : "";
        }

        // Both halves of the conversion as one list — for the `--convtable` table. The
        // calculation does not use it: it calls them separately, because the skill's own
        // conversion runs in the first pass.
        public List<Move> ConversionsForShow()
        {
            List<Move> made = new List<Move>();
            foreach (List<Move> source in new[] { SkillConversions(), Conversions() })
                foreach (Move move in source)
                    made.Add(new Move(move.From, move.To, move.Percent, move.Source)
                    { Late = move.Late, FromElemental = move.FromElemental });
            return made;
        }

        // Every conversion the character carries, as "from -> (to, percent)".
        List<Move> gearConversions;

        List<Move> Conversions()
        {
            // The gear and active records are fixed for this model. Each conversion
            // pass and multiplier can share the same inputs.
            if (gearConversions != null) return gearConversions;
            List<Move> made = new List<Move>();
            List<Item> worn = new List<Item>();
            worn.AddRange(c.Equipment); worn.AddRange(c.Weapons1);
            // Same order as CharacterTotals walks them, so the same instance of a
            // doubled item gets the same numbers in both places.
            Dictionary<string, int> seenNames =
                new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            foreach (Item it in worn)
            {
                if (string.IsNullOrEmpty(it.BaseName)) continue;
                string name = NameOf(it);
                int ordinal = seenNames.ContainsKey(name) ? seenNames[name] : 0;
                seenNames[name] = ordinal + 1;
                string[] partsOf = { it.BaseName, it.PrefixName, it.SuffixName,
                                     it.RelicName, ItemViewFactory.RelicBonusOf(game, it),
                                     it.AugmentName, it.ModifierName, it.AscensionName };
                for (int part = 0; part < partsOf.Length; part++)
                {
                    string dbr = partsOf[part];
                    // A conversion share is rolled too and belongs to its own part: on a
                    // rare item it can sit both on the base and on an affix.
                    Dictionary<string, double> known = exact == null ? null : exact.For(name, ordinal, part);
                    DbRecord r = string.IsNullOrEmpty(dbr) ? null : game.Record(dbr);
                    if (r == null) continue;
                    Conversion.Read(r, known, made, "item: " + name + Part(part));
                }
            }

            // A SET BONUS'S CONVERSION — nobody read it, and it was found on the user's hint
            // to check set properties.
            //
            // The set's other bonuses have long been summed (`CharacterTotals`), the
            // conversion was not: only an ITEM's parts and active skills got here.
            // Worldkeeper's Garden (`itemset_d313`) carries
            // `conversionInType Life → conversionOutType Poison` with the share `0;0;0;50`,
            // that is, at four pieces half the life damage goes into poison — and it was not
            // in our list for `_Acid Vines` at all.
            //
            // The share is taken by the same index as every other set list (the number of
            // pieces minus one, see `CharacterTotals` and `ItemSkillMods`): for this set it
            // is ZERO at three pieces and 50 at four, and those columns must not be
            // confused.
            Dictionary<string, List<string>> setPieces =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (Item it in worn)
            {
                if (string.IsNullOrEmpty(it.BaseName)) continue;
                DbRecord ownRec = game.Record(it.BaseName);
                string setPath = ownRec == null ? null : ownRec.Get("itemSetName");
                if (string.IsNullOrEmpty(setPath)) continue;
                if (!setPieces.ContainsKey(setPath)) setPieces[setPath] = new List<string>();
                if (!setPieces[setPath].Contains(it.BaseName)) setPieces[setPath].Add(it.BaseName);
            }
            foreach (KeyValuePair<string, List<string>> kv in setPieces)
            {
                DbRecord setRec = game.Record(kv.Key);
                if (setRec == null) continue;
                Conversion.Read(setRec, null, made,
                                "set: " + (ItemViewFactory.SetNameOf(game, setRec) ?? kv.Key) +
                                " (" + kv.Value.Count + ")", kv.Value.Count - 1);
            }

            // Skills convert too, and we did not read that at all. `_Amakata` has Harbinger
            // of Souls on, with "physical → life damage 15%", and without it the physical
            // came out 17% above the game while the life damage was three times below — one
            // and the same shortfall seen from two sides. Which skills are active is decided
            // by CharacterTotals; only their fields are read here.
            int beforeActive = made.Count;
            for (int i = 0; i < totals.ActiveRecords.Count; i++)
                Conversion.Read(totals.ActiveRecords[i], null, made,
                                "active skill: " +
                                (SkillText.Name(game, totals.ActiveRecords[i].Path) ?? totals.ActiveRecords[i].Path),
                                i < totals.ActiveSlots.Count ? totals.ActiveSlots[i] : 0);
            // Auras are marked — only the `--stagedconv` experiment separates them from
            // items (analysis at Conversion.Staged); the ordinary count does not read the
            // mark.
            for (int i = beforeActive; i < made.Count; i++) made[i].Late = true;

            // The CHOSEN SKILL'S own conversion is not here on purpose: it runs in the first
            // pass, ahead of the character's, and is read by `SkillConversions` (the
            // measurement is at `Convert`).
            gearConversions = made;
            return made;
        }


        // WHERE THIS TYPE CAME FROM AND WHAT THE SOURCE BROUGHT WITH IT — a trace line.
        // Beside each source it prints the skill percentage that travels with it
        // (`Skl(...) × share × remainder`) — the very thing that adds up into the moved
        // percentage. For now that is applied to the WHOLE arrived pool as one figure; this
        // line shows how rough that is (analysis — MECHANICS 95m).
        string Whence(DamagePool g, DamagePool o, string prefix, string field)
        {
            bool dot = prefix == "offensiveSlow";
            Dictionary<string, double> fromGear = From(dot ? g.FromDotLo : g.FromLo, field);
            Dictionary<string, double> fromOwn = From(dot ? o.FromDotLo : o.FromLo, field);
            List<string> names = new List<string>();
            foreach (KeyValuePair<string, double> kv in fromGear) if (!names.Contains(kv.Key)) names.Add(kv.Key);
            foreach (KeyValuePair<string, double> kv in fromOwn) if (!names.Contains(kv.Key)) names.Add(kv.Key);
            List<string> parts = new List<string>();
            foreach (string src in names)
            {
                double a, b;
                fromGear.TryGetValue(src, out a);
                fromOwn.TryGetValue(src, out b);
                if (a < 0.0001 && b < 0.0001) continue;
                parts.Add(src + " " + Exact(a) + " (weapon) + " + Exact(b) + " (skill), carries " +
                          Exact(CarryOf(prefix, src, field)));
            }
            return string.Join("; ", parts.ToArray());
        }

        static Dictionary<string, double> From(Dictionary<string, Dictionary<string, double>> all, string field)
        {
            Dictionary<string, double> inner;
            return all != null && all.TryGetValue(field, out inner)
                 ? inner : new Dictionary<string, double>(StringComparer.Ordinal);
        }

        // ONE source's moved percentage — the same quantity `SklCarriedByCharacter` adds up,
        // only for one conversion instead of all at once.
        double CarryOf(string prefix, string from, string to)
        {
            double total = 0;
            foreach (Move m in Conversions())
            {
                if (!string.Equals(m.To, to, StringComparison.Ordinal)) continue;
                if (!string.Equals(m.From, from, StringComparison.Ordinal)) continue;
                if (prefix == "offensiveSlow" && !DotName.ContainsKey(to)) continue;
                double share = m.Percent / 100.0;
                if (share > 1) share = 1;
                share *= LeftBeforeElemental(m);
                double left = 1 - SkillOut(m.From);
                if (left <= 0) continue;
                if (Math.Abs(Sum(prefix + m.From + "Min") + Skl(prefix + m.From + "Min")) <= 0.0001) continue;
                total += Skl(prefix + m.From + "Modifier") * share * left;
                total += Skl("offensiveTotalDamageModifier") * share * left;
            }
            return total / 100.0;
        }

        // A MOVED PERCENTAGE TRAVELS WITH ITS SOURCE, NOT AS ONE FIGURE OVER THE WHOLE
        // ARRIVED POOL. For a SKILL's conversion that rule was entered earlier (MECHANICS 25,
        // `SklCarried`), while for the CHARACTER's conversion it stayed "one average figure
        // over everything that arrived" — which is exactly what made `_Itaan`'s poison line
        // sit above the game's.
        //
        // THE SAME IS COUNTED HERE, BUT WEIGHTED by how much came from each source
        // (`DamagePool.From*`). An average weighted that way gives EXACTLY the same as
        // counting the sources separately: their multiplier is shared. The pools differ
        // (the weapon and the skill's own) and so do their weights — that is why the arrived
        // part's multiplier is counted PER POOL, not once per type.
        double MIn(double uniform, Kind k, bool overTime, bool hi, DamagePool a, DamagePool b)
        {
            string prefix = overTime ? "offensiveSlow" : "offensive";
            return uniform - SklCarriedByCharacter(prefix, k.Field) / 100.0 +
                   CarryMix(prefix, k.Field, overTime, hi, a, b);
        }

        double CarryMix(string prefix, string field, bool dot, bool hi, DamagePool a, DamagePool b)
        {
            double sum = 0, weight = 0;
            for (int i = 0; i < 2; i++)
            {
                DamagePool p = i == 0 ? a : b;
                if (p == null) continue;
                Dictionary<string, double> map =
                    From(dot ? (hi ? p.FromDotHi : p.FromDotLo) : (hi ? p.FromHi : p.FromLo), field);
                foreach (KeyValuePair<string, double> kv in map)
                {
                    if (kv.Value <= 0.0001) continue;
                    sum += kv.Value * CarryOf(prefix, kv.Key, field);
                    weight += kv.Value;
                }
            }
            return weight > 0.0001 ? sum / weight : 0;
        }

        // How much of this type arrived BY THE CHARACTER'S CONVERSION — see DamagePool.In*.
        static double In(Dictionary<string, double> came, string field)
        {
            double v;
            return came != null && came.TryGetValue(field, out v) ? v : 0;
        }

        // The pool as one line: the native part on its own multiplier, the part that arrived
        // by conversion on a multiplier without the skill's percentages. `total` is the whole
        // pool after the conversions, `arrived` is the part that came in.
        static double Both(double total, double arrived, double m, double mIn)
        {
            if (arrived <= 0) return total * m;
            if (arrived > total) arrived = total;
            return (total - arrived) * m + arrived * mIn;
        }

        // THE SAME, BUT THE POOL SPLITS IN THREE INSTEAD OF TWO: the native part, the part
        // that arrived by the CHARACTER's conversion, and the part that arrived by THE
        // SKILL'S OWN conversion (`came`, the `Came*` pool).
        //
        // WHY A THIRD SHARE. Two rules about a skill's percentage stood side by side and
        // added up, although they speak of the same damage:
        //
        //   MECHANICS 19i  a skill's own percentage does NOT apply to what arrived by
        //                  conversion
        //   MECHANICS 6    a skill's percentage MOVES with the damage its own conversion
        //                  took away (`SklCarried`)
        //
        // As long as the receiving type had no skill percentage of its own there was no
        // difference: the moved percentage was the only one there. As soon as the receiver
        // is owed something too, one and the same modifier is counted TWICE — as the
        // receiver's own percentage and as the source's moved percentage.
        //
        // MEASURED — `saves/_555`, the WERERAVEN form. Its transmuter Talons of Ch'thon
        // (`wereraven1b`) turns ALL the cold into chaos, while the modifier Everwinter
        // (`wereraven3`, lvl 14) gives EXACTLY 140% to both cold and chaos. Three game
        // numbers from two skills fit one multiplier:
        //
        //   the game's chaos multiplier 11.1479 = (1 + 4.9535 + 2.45 + 1.40 + 0.15) × 1.12
        //   Ice Talons  base 451 – 483  → 5028 – 5384   game 5028 – 5384
        //   Cold Snap   base 518        → 5775          game 5775
        //
        // where 2.45 is the CHARACTER's chaos percentage and 1.40 is Everwinter, taken ONCE.
        // We had 12.2202: the receiver's own chaos 1.40 (cut by the character's
        // chaos → pierce conversion, 1.40 × 0.684 = 0.9576) PLUS the moved cold percentage
        // 1.40. Exactly 9.6% above all three lines.
        //
        // WHAT THE MEASUREMENT DISTINGUISHES AND WHAT IT DOES NOT. Taking only the moved
        // 1.40 or only the receiver's own 1.40 is indistinguishable here: Everwinter gives
        // both types the same. The older `_Hamarina` measurement distinguishes it (Ring of
        // Steel with the Ring of Frost transmuter): there the receiver has no skill
        // percentage of its own at all, yet the moved +185% is needed — so the moved one
        // MUST be taken. Hence the rule: the moved percentage stays, the receiver's own does
        // not apply to the arrived part. That is, MECHANICS 19i is extended from the
        // character's conversion to the skill's — one rule instead of two.
        static double Both(double total, double arrived, double came,
                           double m, double mIn, double mSk)
        {
            if (arrived < 0) arrived = 0;
            if (arrived > total) arrived = total;
            double rest = total - arrived;
            if (came < 0) came = 0;
            if (came > rest) came = rest;
            if (came <= 0) return rest * m + arrived * mIn;
            return (rest - came) * m + came * mSk + arrived * mIn;
        }

        // THE BEST HAND, CHOSEN SEPARATELY FOR THE NATIVE AND THE ARRIVED PART.
        //
        // The share that hits with the "best hand" (SkillPart.BestHand) does not take the
        // larger hand whole but THE LARGER NATIVE plus THE LARGER ARRIVED — which can come
        // out above either hand. Measured on a `_Viliya` weapon-test save, where the aether
        // arrives almost entirely by the fire → aether conversion while the native aether
        // sits on the other weapon:
        //
        //   hand 0 (Mythical Dracarris, a fire weapon)    almost all the aether ARRIVED
        //   hand 1 (Mythical Dreadfire, an aether weapon) the aether is mostly NATIVE
        //
        //   the game needs  X = 124.5 (bottom) and 166.5 (top)
        //   the whole hand     82.21            105.61      ← off by 11%
        //   best separately   124.5             167.0       ← matches
        //
        // WHAT IT IS NOT: the sum of the hands (145.07 and 187.62 — too much) and
        // `0.75 × the sum` (108.8 and 140.7 — too little). Both readings were tried and
        // rejected by number.
        //
        // WHY THE OTHER TYPES DID NOT SHOW IT: for the same character's cold and fire the
        // inflow is small, the native and the arrived parts sit in ONE hand, and "best
        // separately" coincides with "the best hand whole". They parted only on aether,
        // where the sources are split between hands. Analysis — MECHANICS 14v5zh. The third
        // share — what arrived by THE SKILL'S OWN conversion — is taken separately for the
        // same reason: it has a multiplier of its own (see the six-argument `Both`).
        static double BestOf(string field,
                             Dictionary<string, double> a, Dictionary<string, double> aIn,
                             Dictionary<string, double> aCame,
                             Dictionary<string, double> b, Dictionary<string, double> bIn,
                             Dictionary<string, double> bCame,
                             double m, double mIn, double mSk)
        {
            double ai = In(aIn, field), bi = In(bIn, field);
            double an = a[field] - ai, bn = b[field] - bi;
            if (an < 0) an = 0;
            if (bn < 0) bn = 0;
            double ac = In(aCame, field), bc = In(bCame, field);
            if (ac > an) ac = an;
            if (bc > bn) bc = bn;
            return Math.Max(an - ac, bn - bc) * m + Math.Max(ac, bc) * mSk + Math.Max(ai, bi) * mIn;
        }

        // THE CHARACTER'S MULTIPLIER FOR ONE DAMAGE TYPE — exposed, for DISPLAY.
        //
        // An item's tooltip prints a skill's numbers (granted or modified) already
        // multiplied by the character's bonuses, while we printed the record's number.
        // Measured — MECHANICS 22k, six numbers from three records:
        //
        //   Bloodlust Blade → Bloodfangs        60 pierce → 1476 = × 24.6043
        //   the Bloodlust set → the same skill 140 pierce → 3445 = × 24.6043
        //   The Crimson Moon → Werewolf         50 bleed  → 1217 = × 24.3328
        //
        // The model for this is built WITHOUT A SKILL (`skill == null`), and then `Skl()`
        // and `SklCarried*` give zero — leaving exactly the character's multiplier, the one
        // in the measurement.
        //
        // Returns 1 for a type it does not know: display had better print the record's
        // number than invent a multiplier.
        public double TypeMultiplier(string field, bool overTime)
        {
            foreach (Kind k in Kinds)
                if (string.Equals(k.Field, field, StringComparison.Ordinal))
                {
                    if (overTime && !DotName.ContainsKey(field)) return 1;
                    return Multiplier(k, overTime);
                }
            return 1;
        }

        double Multiplier(Kind k, bool overTime) { return Multiplier(k, overTime, true, true); }
        double Multiplier(Kind k, bool overTime, bool withSkill) { return Multiplier(k, overTime, withSkill, true); }
        double Multiplier(Kind k, bool overTime, bool withSkill, bool ownTypePct)
        { return Multiplier(k, overTime, withSkill, ownTypePct, false); }

        // `withSkill == false` — the same multiplier WITHOUT the skill's own percentages. It
        // is used for the part of the pool that arrived by THE CHARACTER'S CONVERSION: a
        // skill percentage does not apply to it (MECHANICS 19i).
        //
        // `ownTypePct == false` — the third multiplier, for what arrived by THE SKILL'S OWN
        // conversion (a transmuter). Skill percentages ARE there, but taken from the SOURCE
        // type (`SklCarried`), while the RECEIVING type's own percentage is not: otherwise
        // one modifier is counted twice. Analysis and measurement at the six-argument `Both`
        // below.
        //
        // `carriedOnWholePool` — the moved skill percentage is applied to the receiver's
        // WHOLE pool, not only to the part that arrived by conversion. The PER-HAND PANEL
        // counts that way and only it; the measurement and both tables of numbers are at the
        // place this flag is set (the hand loop above) and in MECHANICS 25v.
        double Multiplier(Kind k, bool overTime, bool withSkill, bool ownTypePct,
                          bool carriedOnWholePool)
        {
            // The divider comes from the `combatformulas` equation, not as a literal.
            double attr = (k.Magic ? spirit : cunning) / Combat.AttributeDivisor(game, k.Magic, overTime);
            string prefix = overTime ? "offensiveSlow" : "offensive";
            double pct = (Sum(prefix + k.Field + "Modifier") +
                          // THE FORM'S PERCENTAGE STANDS WITH THE CHARACTER'S, not with the
                          // skill's: whole, and also for what arrived by conversion.
                          SklForm(prefix + k.Field + "Modifier") +
                          // A SKILL'S TYPE PERCENTAGE ALSO LOSES THE SHARE THE CHARACTER'S
                          // CONVERSION TOOK AWAY — the same `Left` that already stood at
                          // "+% total damage" (measured on `_Assira`). Extended on a `_Relo`
                          // one-hand-and-shield save: Blindside gives +80% physical and +80%
                          // internal trauma while the character moves 16.1% of the physical
                          // into poison. 80 × 0.161 = 12.88 is exactly the amount that was
                          // missing, and it is needed by FOUR Blitz lines at once:
                          //
                          //     physical  11174 → 11100    game 11100
                          //     poison      703 →   717    game   717
                          //     trauma     3329 →  3306    game  3306
                          //     decay       231 →   236    game   236
                          //
                          // With two skill fields (+80% physical and +80% trauma) and ONE
                          // conversion share — there is no free number here.
                          (withSkill && ownTypePct
                               ? Skl(prefix + k.Field + "Modifier") * LeftForPercent(prefix, k.Field)
                               : 0)) / 100.0;
            // A SKILL PERCENTAGE MOVES ALONG WITH THE DAMAGE ITS CONVERSION TOOK AWAY.
            //
            // Measured on `_Hamarina`'s Ring of Steel. The skill has its own transmuter Ring
            // of Frost — "100% conversion: pierce damage to cold damage" — and its own
            // modifier Circle of Slaughter with "+185% pierce damage". All the pierce goes
            // into cold, and this percentage goes with it:
            //
            //     the cold multiplier needed  6.6453
            //     ours was                    4.7963 = 1 + 2.1163 + 0.62 + 1.06
            //     the difference              1.8490  — exactly Circle of Slaughter
            //     1 + 2.1163 + 0.62 + 1.849 + 1.06 = 6.6453   matches to the fourth digit
            //
            // The same measurement says that ONLY the skill's percentage moves, not the
            // character's: the character has +1162% pierce, and had that been converted too
            // the multiplier would be entirely different. Exactly 185 moved.
            //
            // It also confirmed that the attribute is taken from the RECEIVING type: cold
            // counts from spirit (2.1163), not from cunning as pierce does.
            //
            // This is exactly the MECHANICS 6 line that stood marked "[article]" and was
            // never entered: percentage bonuses tied to a skill are converted by transmuters
            // and modifiers too.
            //
            // WHAT THE MEASUREMENT DOES NOT SAY: what to do with a PARTIAL conversion. Here
            // it is a hundred per cent, and "everything moved" is indistinguishable from "a
            // share moved". The share is taken — it agrees with a full move at 100% and
            // invents nothing beyond what was measured; the line below does exactly that
            // (`m.Percent / 100`). A skill with a transmuter for half a type will settle it.
            // THE OTHER HALF OF THE SAME: A MOVED PERCENTAGE APPLIES EXACTLY TO WHAT ARRIVED
            // BY CONVERSION — and to nothing else.
            //
            // It mirrors MECHANICS 19i ("a skill's own percentage does NOT apply to what the
            // character's conversion brought"), and together they split the pool exactly
            // where `Both(total, arrived, m, mIn)` splits it:
            //
            //     the receiver's native part  own skill percentage YES, moved one NO
            //     the arrived part            own percentage NO, moved one YES
            //
            // Arrived poison is physical damage in disguise: the percentage that travelled
            // with it stays with it, and that damage never had a poison percentage of its
            // own.
            //
            // WHAT THIS SEPARATED. The first version applied the moved percentage to the
            // receiver's WHOLE pool, and two stands demanded different things:
            //
            //                    Blitz poison  decay   `_Iman` fire  `_Iman` lightning
            //     whole pool         717 ✔      236 ✔     23095 ✘        18426 ✘
            //     not at all         703 ✘      231 ✘     22756 ✔        18145 ✔
            //     ARRIVED only       717 ✔      236 ✔     22756 ✔        18145 ✔
            //     the game           717        236       22756          18145
            //
            // The difference was not in the rule but in the characters: on `_Relo` Blitz's
            // poison arrived by conversion ENTIRELY, while on `_Iman` the fire is mostly its
            // own (51.3% of physical is only an addition to it). "Whole pool" means giving
            // the percentage to fire that never moved anywhere.
            //
            // ...AND THAT STOOD HERE AS ONLY HALF: the words were written, the condition was
            // not. `SklCarried` was added for any `withSkill`, that is into the NATIVE
            // part's multiplier too — exactly what the paragraph above calls wrong. It could
            // be told apart only on a receiver that has BOTH native damage AND damage that
            // arrived by ITS OWN conversion; none of the earlier characters had that (on
            // `_Hamarina` and `_555` the receiver arrived by conversion entirely, `_Relo` and
            // `_Iman` have no own conversion at all — `SklCarriedByCharacter` works there).
            //
            // MEASURED — `_Acid Vines`' Grasping Vines, and it matches TO THE UNIT in two
            // game numbers at once. That skill's poison pool splits exactly in half: 360 its
            // own (flat poison from four modifiers) and 167 arrived by its own conversion
            // (all the skill's physical, 100% → poison). The moved percentage is Entangling
            // Vines' `offensivePhysicalModifier 200`.
            //
            //   native 360 × (1 + 2.6919 + 27.9 + 0.15) × 3        = 34281.3
            //   arrived 167 × (1 + 2.6919 + 27.9 + 2 + 0.15) × 3   = 16904.7
            //   retaliation added to the hit                       =  2998.0
            //   TOTAL 54184   game 54184   (was 62668, +15.7%)
            //
            // AND A SECOND, INDEPENDENT NUMBER: the skill's own tooltip prints "16905 poison
            // damage" — exactly the arrived part, 167 × 101.2257 (MECHANICS 22k: a tooltip
            // prints a skill's numbers with the character's multipliers). It also confirms
            // the clamp in `SklCarried`: with the old 600% it would be 18909.
            //
            // AND A THIRD, FROM ANOTHER CHARACTER: on `_Jien` Ring of Steel's cold line was
            // +1.0% / +1.2% (58911 – 71384 against the game's 58334 – 70531) and became
            // EXACT at both ends. There the skill's whole own damage arrives as cold out of
            // pierce, while the WEAPON's cold is partly native — and that part must not get
            // Circle of Slaughter's moved +185%.
            //
            // AND THAT IS ALSO WHAT PARTED THE TWO PANELS: on the same `_Jien` the PER-HAND
            // lines of Ring of Steel demand THE OPPOSITE — they need the moved percentage on
            // the whole pool (31653 and 31903 against the game's 31674 and 31943; arrived
            // only would give 31209 and 31452, off by 1.5%). Hence the flag:
            // `carriedOnWholePool` is set ONLY in the hand loop, where both tables of numbers
            // are written out. Analysis — MECHANICS 25v.
            if (withSkill && (!ownTypePct || carriedOnWholePool))
                pct += SklCarried(prefix, k.Field) / 100.0;
            // `--carry=<share>` — an EXPERIMENT: carry not the whole moved percentage but a
            // share of it. It exists to MEASURE how much should travel instead of guessing:
            // the line is linear in that share, so two runs (0 and 1) are enough to compute
            // the needed value from the game's number. Analysis — MECHANICS 95m.
            if (!withSkill) pct += SklCarriedByCharacter(prefix, k.Field) * CarryShare / 100.0;
            // "% elemental damage" applies to direct damage and NOT to burn, freeze and
            // electrocute. The old note claimed the opposite and rested on a guess about a
            // rolled percentage; there are two measurements now and both are against it
            // (MECHANICS 14v, "% elemental on dots"):
            //
            //   a mace with a Chilled Steel component: freeze 52 and 117 at a multiplier of
            //   exactly 1 + spirit/200, without a single percentage; with the component's
            //   +12% cold the top end would be 119 against the game's 117.
            //
            //   an axe with shoulders (+78% elemental): the game shows three elemental dots
            //   as 21 / 22 / 21, while with those 78% we had 25 each.
            if (k.Elemental && !overTime)
                pct += (Sum("offensiveElementalModifier") + SklForm("offensiveElementalModifier") +
                        (withSkill ? Skl("offensiveElementalModifier") : 0)) / 100.0;
            double m = 1 + attr + pct +
                       (Sum("offensiveTotalDamageModifier") + SklForm("offensiveTotalDamageModifier") +
                        (withSkill ? Skl("offensiveTotalDamageModifier") * Left(k.Field, overTime) : 0)) / 100.0;

            // "TOTAL DAMAGE MODIFIED BY N%" IS A SEPARATE MULTIPLIER OVER EVERYTHING, not
            // another addend to the percentages. The field is `offensiveDamageMultModifier`.
            //
            // MEASURED by the user on an `_Iman` bomb-test save: they took Canister Bomb
            // BEFORE and AFTER putting points into Ulzuin's Chosen
            // (`playerclass02/passive2`, exactly 11 at level 5). Four numbers, and all four
            // give THE SAME ratio:
            //
            //   pierce   392 – 542  →  435 – 601     392×1.11 = 435.1   542×1.11 = 601.6
            //   fire     1654       →  1836          1654×1.11 = 1835.9
            //   burn     1954       →  2169          1954×1.11 = 2168.9
            //
            // EXACTLY 1.11 ON EVERY LINE — which proves the multiplier is separate. Were
            // those 11% another addend to the character's percentages, the ratio would be
            // below 1.11 by as much as the other percentages are large, and on this
            // character they are not small.
            //
            // Before this measurement the field was listed as "display only: crits"
            // (ItemStatUse), and Ulzuin's Chosen folded into the skill giving nothing: our
            // numbers matched the game WITHOUT the passive and were 11% off with it.
            //
            // Only the SKILL's percentage is taken: on an item this field is about critical
            // damage, and the measurement does not carry over there — there is none of its
            // own.
            //
            // AND IT APPLIES TO THE WHOLE POOL, INCLUDING WHAT ARRIVED BY THE CHARACTER'S
            // CONVERSION — that is, it is counted OUTSIDE `withSkill`. Measured on the
            // berserker `_555`, Feral Claws (in form, Form of the Beast lvl 16 gives ×1.12):
            //
            //   type        ours     game     ratio
            //   physical    3251     3251     1.0000  matches
            //   pierce     22177    22177     1.0000  matches
            //   cold         321      321     1.0000  matches
            //   POISON       456      511     1.1203  ← exactly ×1.12
            //
            // This character's poison arrives ENTIRELY by conversion (physical → poison
            // 16.7%), it has no base of its own at all. Three types with their own base
            // match to the unit, and the only purely arrived one is off by exactly the
            // multiplier. In numbers: 0.75 × (21.8797 + 28.5605) × 1.66 = 62.798, and
            //   62.798 × 7.2635 (without the multiplier) = 456.1   ← what we had
            //   62.798 × 8.1351 (with it)                = 510.9   ← the game prints 511
            //
            // THE OLD MEASUREMENT ABOVE SAYS THE SAME, we simply did not read it that way:
            // `_Iman`'s Canister Bomb fire is only half its own (51.3% of physical is an
            // addition to it), yet the ratio before and after Ulzuin's Chosen came out
            // EXACTLY 1.11 (1654 → 1836). If the multiplier did not apply to the arrived
            // part, the ratio would be visibly below 1.11.
            //
            // WHY THIS DOES NOT CONTRADICT MECHANICS 19i ("a skill's own percentage does not
            // apply to what arrived by conversion"): 19i is about PERCENTAGES tied to a
            // damage type (`offensive<Type>Modifier`) — those convert along with their type.
            // "Total damage modified by N%" belongs to no type at all: it is a multiplier
            // over the whole hit, and there is nothing to split by type.
            //
            // AND TWO OF THEM MULTIPLY, THEY DO NOT ADD UP. Measured on the new `_Telianor`
            // (Oathkeeper + Occultist, shield), whose Aegis of Menhir carries TWO such
            // modifiers at once: the set The Voidsoul gives +125% and the amulet −36%.
            //
            //   adding them     1 + 1.25 − 0.36        = 1.8900   ← what we did
            //   multiplying     (1 + 1.25) × (1 − 0.36) = 1.4400   ← what the game does
            //   ratio                                    1.3125
            //
            // And the game's panel says the ratio outright — our numbers stood above its
            // numbers by exactly that, on the lines whose damage goes wholly through the
            // weapon:
            //
            //   poison  5645 / 4301 = 1.3125      burn    5011 / 3818 = 1.3125
            //   trauma   718 /  547 = 1.3126      pierce  9600 / 7319 = 1.3117
            //
            // The four lines that carry something besides the weapon (life, chaos, decay,
            // bleeding) stand lower — 1.24…1.30 — which is what a partly multiplied pool
            // looks like, and they are the next thing to read.
            //
            // THE OLD MEASUREMENTS ARE NOT TOUCHED: with ONE modifier a sum and a product
            // are the same number, and both `_Iman` (×1.11) and `_555` (×1.12) had exactly
            // one. `--multadd` puts the addition back, so the stand can be seen red.
            {
                double mult = MultModifiersAdd
                                  ? 1 + Skl("offensiveDamageMultModifier") / 100.0
                                  : SklProduct("offensiveDamageMultModifier");
                if (Math.Abs(mult - 1) > 0.0001) m *= mult;
            }
            return m;
        }

        // THE SHARE OF A TYPE THAT SURVIVES THE CHARACTER'S CONVERSIONS — and THE SKILL'S
        // OWN PERCENTAGE is multiplied by it.
        //
        // Measured on `_Assira`'s Bone Harvest by two experiments of the user: they took off
        // a ring (pierce into cold 14.26%) and separately refunded Spectral Binding. Both
        // states are saved, and by them the error turned out to be EXACTLY a function of the
        // conversion share:
        //
        //   type       share moved   was       became
        //   pierce        0         +4.8%     EXACT   (the ring off — no share)
        //   life         14.5%      +3.2%     predicted +3.1%
        //   aether       60%        +11.7%    predicted +11.7%
        //   decay        14.5%      +2.9%     predicted +2.9%
        //
        // As a formula: we had `base × (1−share) × (m0 + P)` while the game needs
        // `base × (1−share) × (m0 + P × (1−share))`, where P is THE SKILL's percentage
        // (Dread gives +189% total damage). So a skill's percentage loses exactly the share
        // the conversion takes out of its type: it is itself a source of that type's damage
        // and converts along with the base.
        //
        // THE CHARACTER'S PERCENTAGE does NOT behave that way — visible from the same
        // numbers: were it cut too, the multipliers would be entirely different. The same
        // was already measured on Ring of Steel (see SklCarried above).
        //
        // FOR A DOT NOT ALL CONVERSIONS COUNT, ONLY THOSE WHOSE RECEIVER HAS A DOT — the
        // same rule as at `LeftForPercent`, at `Conversion.Pass` and at
        // `SklCarriedByCharacter`. It was NOT here, and this was the last place it was not
        // applied: a rule applied in one place is a rule not applied.
        //
        // MEASURED — `_222`'s Forcewave, the SECOND weapon set. There the physical goes into
        // THREE types at once: fire 10.8%, lightning 17.5% and AETHER 16.1%, and aether has
        // no dot. So internal trauma loses only 28.2% instead of all 44.4% — and the skill's
        // total percentage from Rending Force (203%) was wrong by exactly that difference:
        //
        //     share for direct damage 0.5562 → multiplier 16.3978 → 3702 – 4455
        //     share for the dot       0.7175 → multiplier 16.7252 → 3776 – 4544
        //     the game                                              3776 – 4544
        //
        // The error was exactly ×1.02 at BOTH ends, so it is a share, not an addend. With
        // the FIRST set the same line matched exactly: there the physical went only into
        // fire, which has a dot, and both shares coincided.
        double Left(string field, bool dot)
        {
            double out_ = 0;
            foreach (Move m in Conversions())
                if (string.Equals(m.From, field, StringComparison.Ordinal) &&
                    (!dot || DotName.ContainsKey(m.To))) out_ += m.Percent / 100.0;
            return out_ >= 1 ? 0 : 1 - out_;
        }

        // THE SAME, BUT FOR A TYPE PERCENTAGE, with two caveats, each of them measured.
        //
        // 1. FOR A DOT NOT ALL CONVERSIONS COUNT, ONLY THOSE WHOSE RECEIVER HAS A DOT AT
        //    ALL. A dot moves with its root, but it has nowhere to move if the receiving
        //    type has no dot: aether, chaos and pierce have none.
        //    Measured — `_Satirita`, Drain Essence: the character moves 59.6% of the life
        //    damage into aether, while DECAY (`offensiveSlowLife`) stays put entirely —
        //    11597 against the game's 11597. Cut its percentage by the same 59.6% and you
        //    get 10971.
        //
        // 2. THE SKILL'S CONVERSION FIRES FIRST and the character gets the remainder. The
        //    same `_Satirita`: Drain Essence's own transmuter moves ALL the life damage into
        //    aether, and the character's conversions have nothing left to take.
        double LeftForPercent(string prefix, string field)
        {
            bool dot = prefix == "offensiveSlow";
            double sOut = 0, cOut = 0;
            foreach (Move m in SkillConversions())
                if (string.Equals(m.From, field, StringComparison.Ordinal) &&
                    (!dot || DotName.ContainsKey(m.To))) sOut += m.Percent / 100.0;
            if (sOut > 1) sOut = 1;
            // THE SAME PASS ORDER AS FOR THE DAMAGE ITSELF (`Conversion.Pass`) — otherwise
            // the share a type percentage loses would be counted by a different rule than
            // the damage that left. The elemental share goes first and multiplies, the rest
            // add up; `--stagedconv` is the experiment with auras as a separate pass (the
            // article, MECHANICS 95z).
            double lateOut = 0, elemOut = 0;
            foreach (Move m in Conversions())
                if (string.Equals(m.From, field, StringComparison.Ordinal) &&
                    (!dot || DotName.ContainsKey(m.To)))
                {
                    if (Conversion.Staged && m.Late) lateOut += m.Percent / 100.0;
                    else if (Conversion.ElementalFirst && m.FromElemental) elemOut += m.Percent / 100.0;
                    else cOut += m.Percent / 100.0;
                }
            if (cOut > 1) cOut = 1;
            if (lateOut > 1) lateOut = 1;
            if (elemOut > 1) elemOut = 1;
            if (elemOut > 0) cOut = 1 - (1 - elemOut) * (1 - cOut);
            if (lateOut > 0) cOut = 1 - (1 - cOut) * (1 - lateOut);
            // THIS `+ sOut` IS THE ONE PLACE WHERE WE DIFFER FROM THE PLAYERS' FORMULA, and
            // it is NOT MEASURED (analysis — MECHANICS 95k, the user's question).
            //
            // In the players' formula (the Crate forum thread on conversion) the remainder is
            // split strictly by shares:
            // `DmgPhys = PHYS·(1−CONVS)·(1−CONVE)·(1 + BSP·(1−CONVS)·(1−CONVE) + CUNB + BEP)`
            // — the skill's percentage is divided between three parts (what stayed, what its
            // own conversion took, what the character's conversion took) and sums to one. We
            // do not subtract what the SKILL's own conversion took (`+ sOut`), so our sum is
            // above one.
            //
            // OUR RULE IS THE SAME, ONLY SHORTER:   Left = 1 − cOut × (1 − sOut)
            // — a skill's percentage loses exactly the share the CHARACTER's conversion took
            // OUT OF THE REMAINDER left by its own conversion. Its own conversion does not
            // reduce it at all: it COPIES it into the receiving type (`SklCarried`) instead
            // of dividing it.
            //
            // AND THAT IS MEASURED, not chosen: `--strictleft` (strict division by shares,
            // as in the players' formula) breaks `_Hamarina` and the dot save — Ring of
            // Steel's pierce moves from the exact 3052–4673 to 2723–4168, and the difference
            // in the multiplier is exactly 1.85, that is Circle of Slaughter (+185% pierce).
            //
            // WHY IT COPIES INSTEAD OF DIVIDING IS VISIBLE THERE: the Ring of Frost
            // transmuter moves ALL the original pierce INTO COLD (76–84 of base), while the
            // pierce standing in the line (81–124) is PHYSICAL that passed through 100%
            // armour piercing after all the conversions. The type was refilled from scratch,
            // and the game gives it the skill's +185% whole. There is nothing to divide "by
            // shares of the original": none of the original is left in the line.
            //
            // WHAT THE MEASUREMENT DOES NOT SAY, and it must be remembered: the case where
            // the skill's own conversion is PARTIAL and the type is NOT refilled. Then the
            // remainder gets the percentage whole with us and only its share by the players'
            // formula, and the numbers would differ. A character whose skill has a partial
            // transmuter (not 100%), a percentage on the source type, and neither armour
            // piercing nor a conversion into that type would tell them apart.
            return StrictLeft ? (1 - sOut) * (1 - cOut)
                              : (1 - sOut) * (1 - cOut) + sOut;
        }

        // `--strictleft` — count the percentage remainder strictly by shares, as in the
        // players' formula. Analysis at `LeftForPercent` itself.
        public static bool StrictLeft;

        // `--carry=<share>` — an EXPERIMENT: what SHARE of the moved percentage travels to
        // the receiver. One is what we count now, zero is not carrying it at all. Added to
        // measure the needed value instead of guessing: the line is linear in that share, so
        // two runs (0 and 1) compute it from the game's number. Analysis at the place the
        // share is applied (`Multiplier`) and in MECHANICS 95m.
        public static double CarryShare = 1;


        // THE SAME MOVED PERCENTAGE, BUT THROUGH THE CHARACTER'S CONVERSION, NOT THE
        // SKILL'S (the skill's own is `SklCarried` below; analysis at Multiplier).
        //
        // A skill percentage used to move only through ITS OWN transmuter, and that was
        // right exactly by half: Ring of Steel's conversion is a hundred per cent, so "moved
        // through its own" is indistinguishable from "moves through any".
        //
        // A `_Relo` one-hand-and-shield save separated them. Blitz has NO conversions of its
        // own, while its modifier Blindside gives +80% physical and +80% internal trauma;
        // what moves the physical into poison is THE CHARACTER'S CONVERSION, 16.1%. Four
        // panel lines demand those 12.88 at once — as a minus at the source and a plus at
        // the receiver (the numbers are at `Left` in Multiplier).
        //
        // WHAT THIS MEASUREMENT DOES NOT SAY: whether a skill percentage is reduced by its
        // OWN conversion the way it is by the character's. On Ring of Steel it is 100%, the
        // source is zeroed entirely and its multiplier affects nothing — it cannot be told
        // apart. So `SklCarried` is left as it was, additive only.

        // By how much this character conversion's share is below the recorded one because
        // the same source's elemental conversion fired earlier. For the elemental one
        // itself, and where there is no elemental share, it is one.
        double LeftBeforeElemental(Move m)
        {
            if (!Conversion.ElementalFirst || m.FromElemental) return 1;
            double elemOut = 0;
            foreach (Move x in Conversions())
                if (x.FromElemental && string.Equals(x.From, m.From, StringComparison.Ordinal))
                    elemOut += x.Percent / 100.0;
            if (elemOut <= 0) return 1;
            return elemOut >= 1 ? 0 : 1 - elemOut;
        }

        double SklCarriedByCharacter(string prefix, string field)
        {
            double total = 0;
            foreach (Move m in Conversions())
            {
                if (!string.Equals(m.To, field, StringComparison.Ordinal)) continue;
                if (prefix == "offensiveSlow" && !DotName.ContainsKey(field)) continue;
                double share = m.Percent / 100.0;
                if (share > 1) share = 1;
                // THE SHARE IS THE SAME AS FOR THE DAMAGE ITSELF: the elemental conversion
                // goes first, and a type's own conversion gets only the REMAINDER after it
                // (Conversion.ElementalFirst). Without this correction the percentage moves
                // by the old, added-up shares while the damage moves by the new ones, and
                // the receiver comes out high.
                //
                // Found the same day as the rule itself: `_Itaan`'s fire goes into poison by
                // two shares (25.2% elemental from a hood and 50% fire from an aura), and
                // after the fix the poison stayed +1.4% and the poison dot +1.3% above the
                // game — while ALL the source lines matched to the unit. The difference sat
                // exactly here: it carried 75.2% of the skill percentage instead of 62.6%.
                share *= LeftBeforeElemental(m);
                // THE SKILL'S CONVERSION FIRES FIRST (MECHANICS 19), so the character gets
                // only the REMAINDER of its source. Without this correction the percentage
                // is counted twice: on `_Jien`'s Ring of Steel the Ring of Frost transmuter
                // moves ALL the pierce into cold, `SklCarried` has already brought Circle of
                // Slaughter's +185% there, and the character's Pierce → Cold conversions
                // brought it a second time — the cold drifted from 58334 to 60701 (+4.1%).
                double left = 1 - SkillOut(m.From);
                if (left <= 0) continue;
                // A PERCENTAGE TRAVELS ONLY WITH DAMAGE, NOT BY ITSELF. With no damage of
                // the source type the conversion has nothing to carry, and neither has the
                // percentage. This check stood here from the start, but only for the skill's
                // TOTAL percentage; the TYPE percentage had none, and that was a hole.
                //
                // THE MEASUREMENT THAT EXPOSED IT (`_Itaan` without the transmuter):
                // Retribution gives +70% FIRE damage, the character has not a single point
                // of direct fire, and a fire→poison conversion exists — so those
                // 0.70 × 0.626 = 0.438 travelled into poison with nothing, lifting the line
                // above the game's.
                if (Math.Abs(Sum(prefix + m.From + "Min") + Skl(prefix + m.From + "Min")) <= 0.0001)
                    continue;
                total += Skl(prefix + m.From + "Modifier") * share * left;
                // AND THE SKILL'S TOTAL PERCENTAGE MOVES EXACTLY THE SAME WAY
                // (MECHANICS 14d12).
                //
                // Half of this rule stood earlier: at the SOURCE the skill's total
                // percentage is cut by the share that left (`Left` in the multiplier). But
                // nobody gave it to the receiver — the same asymmetry that was fixed for the
                // TYPE percentage.
                //
                // MEASURED — `_222`'s Forcewave. It has NO fire of its own (all three
                // records dumped), all the fire arrives by conversion out of physical, 78.4%.
                // Its part Rending Force gives `offensiveTotalDamageModifier` 217, and
                // 217 × 0.784 = 170.13 moves:
                //     burn  4956 and 5967   game 4956 and 5966
                //     fire 33265 and 35284  game 33278 and 35305   (off by 0.04% and 0.06%)
                //     it was 4505 – 5424 (−9.1%) and 30737 – 32674 (−7.6%)
                // The FULL percentage must not be applied — by the same measurement: without
                // the share the fire is 33963 against the game's 33278, 2.1% high.
                //
                // WHY THE SOURCE CHECK IS HERE. The TYPE percentage a line above is harmless
                // for foreign conversions: no damage of the source type, no percentage of
                // it, the addend is zero. The TOTAL percentage is never zero, and without the
                // check it moves along every conversion leading into our type even when
                // nothing arrived from there. On `_222` TWO lead into fire: physical → fire
                // 78.4% and PIERCE → fire 25.3%, while it has no pierce damage at all (the
                // game's panel says "pierce damage 0"). The first version of the fix added
                // both shares (1.037 instead of 0.784) and Forcewave went from −7.6% to
                // +2.4%.
                //
                // The check is approximate, said honestly: the exact thing would be knowing
                // how much came FROM EACH SOURCE, while the pool keeps one number per type
                // (`InLo`/`InHi`). What is asked here is cruder — whether the source has any
                // flat damage at all, on the character or on the skill. A zero there means
                // nothing could have arrived from it either.
                double had = Sum(prefix + m.From + "Min") + Skl(prefix + m.From + "Min");
                if (Math.Abs(had) > 0.0001)
                    total += Skl("offensiveTotalDamageModifier") * share * left;
            }
            return total;
        }

        // What share of this type THE SKILL'S OWN conversions take away.
        double SkillOut(string field)
        {
            double out_ = 0;
            foreach (Move m in SkillConversions())
                if (string.Equals(m.From, field, StringComparison.Ordinal)) out_ += m.Percent / 100.0;
            return out_ > 1 ? 1 : out_;
        }

        // ONE SOURCE'S SHARE IS SUMMED AND CLAMPED TO ONE, not taken per conversion. Each
        // one used to be clamped separately, and the same share was counted as many times as
        // there were items declaring the same conversion.
        //
        // MEASURED on `_Acid Vines`' Grasping Vines, and the number matches to the hundredth.
        // THREE different items give that skill the same hundred-per-cent
        // physical → poison conversion (Worldkeeper's Gaze, Suffering, Mythical
        // Bramblevine), while the skill has a single physical percentage of its own —
        // Entangling Vines' `offensivePhysicalModifier 200` at level 22:
        //
        //   was   3 × 200% = +600%   multiplier (31.7419 + 6) × 3 = 113.2257
        //   now       200% = +200%   multiplier (31.7419 + 2) × 3 = 101.2257
        //
        // That it must move ONCE is visible from the calculation itself: the DAMAGE is
        // carried once by those three conversions (`SkillOut` clamps the source's outflow to
        // one, and the skill's 167 physical arrives in poison as exactly 167, not 501). The
        // percentage must travel by the same share as the damage, or the two argue inside
        // one formula.
        //
        // WHY IT DID NOT SURFACE EARLIER: to diverge you need TWO items with THE SAME
        // conversion on ONE skill, plus a percentage of its own at the source. No earlier
        // character had that — on `_Jien` (Ring of Steel) and `_222` (Forcewave) the skill
        // has a single conversion.
        //
        // IT IS CLAMPED PER "SOURCE → THIS TYPE" PAIR, not per source as a whole: we have no
        // measurement of one source splitting between two receivers with a sum above a
        // hundred, and there is nothing to base a division on. `SklCarriedByCharacter` is
        // built the same way and left so for the same reason: the character has many
        // conversions into one type, but no measurement of them.
        double SklCarried(string prefix, string field)
        {
            Dictionary<string, double> share = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (Move m in SkillConversions())
            {
                if (!string.Equals(m.To, field, StringComparison.Ordinal)) continue;
                double had;
                share.TryGetValue(m.From, out had);
                share[m.From] = had + m.Percent / 100.0;
            }
            // A MOVED PERCENTAGE TRAVELS WITH ITS SOURCE, NOT AS ONE TOTAL OVER THE WHOLE
            // ARRIVED POOL. With TWO sources whose percentages DIFFER a sum gives the wrong
            // thing: each piece of arrived damage is owed the percentage of the type it came
            // from. They can be added only WEIGHTED by how much came from each — and then
            // one average figure over the pool gives exactly the same as counting the
            // sources separately (their multiplier is shared).
            //
            // MEASURED — `_Itaan`, Righteous Fervor, TWO game tooltips, each naming ITS OWN
            // source on a separate line:
            //
            //   "3646 poison damage"      = 108 physical × (31.9585 + 1.80)
            //   "11559 poison over 3 sec" = 111 burn     × (32.9129 + 1.80) × 3
            //
            // where 1.80 is the percentage of ITS OWN type only (`offensivePhysicalModifier`
            // 150 and `offensiveSlowFireModifier` 150, both with the charge multiplier 1.2).
            // Add them, as we did, and each line would carry the OTHER type's percentage
            // too.
            //
            // THE WEIGHT COMES FROM THE SKILL'S OWN DAMAGE BEFORE THE CONVERSIONS
            // (`beforeOwn`): that is what moves. With no snapshot (an empty pool) the
            // weights are equal, so the old behaviour remains and no stand without two
            // sources moves.
            Dictionary<string, double> from = prefix == "offensiveSlow" ? dotBeforeOwn : beforeOwn;
            double sum = 0, weight = 0;
            foreach (KeyValuePair<string, double> kv in share)
            {
                double cap = kv.Value > 1 ? 1 : kv.Value;
                double had;
                if (!from.TryGetValue(kv.Key, out had)) had = 0;
                sum += Skl(prefix + kv.Key + "Modifier") * cap * had;
                weight += had * cap;
            }
            if (weight > 0.0001) return sum / weight;
            // Nothing of its own moved — count as before, by the sum: harmless, because
            // there will be nothing to multiply it by either.
            double total = 0;
            foreach (KeyValuePair<string, double> kv in share)
                total += Skl(prefix + kv.Key + "Modifier") * (kv.Value > 1 ? 1 : kv.Value);
            return total;
        }

        void Add(string type, string name, double min, double max, bool dot, double secs)
        { Add(type, name, min, max, dot, secs, Fmt(secs)); }

        // `nameKey` is a language-file key ("model.type.Fire"); the word is resolved here.
        void Add(string type, string nameKey, double min, double max, bool dot, double secs, string secsText)
        {
            if (min < 0.5 && max < 0.5) return;
            DamageLine l = new DamageLine
            { Type = type, Name = Lang.T(nameKey), Min = Math.Round(min), Max = Math.Round(max),
              OverTime = dot, Seconds = secs, SecondsText = secsText };
            Lines.Add(l);
            TotalMin += l.Min; TotalMax += l.Max;
        }

        // For the trace, not for display: rounding to a tenth hides exactly what the trace
        // is read for.
        static string Exact(double v)
        { return v.ToString("0.####", CultureInfo.InvariantCulture); }

        static string Fmt(double v)
        {
            return Math.Abs(v - Math.Round(v)) < 0.005
                ? ((long)Math.Round(v)).ToString()
                : v.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
