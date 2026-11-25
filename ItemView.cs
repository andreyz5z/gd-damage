// Turns a saved inventory entry into something displayable: full name, icon,
// footprint in inventory cells and the description shown on hover.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;

namespace Gdc.Gui
{
    public class ItemView
    {
        public Item Source;
        // An item FROM A SAVE (so it has a seed, even if zero) or from the catalog,
        // where there is no instance yet and nothing to roll. See Build.
        public bool HasSeed = true;
        public string Name = "";
        public string Classification = "";      // what the item reads as in game
        public string BaseClassification = "";  // what its own record says
        // The record's own class name — ArmorProtective_Head, WeaponMelee_Sword2h
        // and so on. This, not the save, is what decides where the item can be worn.
        public string ItemClass = "";
        // Level and attribute requirements, computed from the game's equations.
        public Requirements Needs = new Requirements();
        public int RareAffixCount;              // 0, 1 or 2 — the diamonds in game
        public int LevelRequirement;
        public Bitmap Icon;
        public int CellsWide = 1, CellsHigh = 1;
        // False when the icon could not be found, which means the footprint is a
        // guess. Such an item must never be placed: putting a two-cell item in
        // the last column pushes it outside the bag and the game crashes on it.
        public bool SizeKnown;
        public int X, Y;
        public uint Stack = 1;
        public List<TipLine> Details = new List<TipLine>();
        public string Missing;   // set when the record could not be found

        public Rectangle Cells { get { return new Rectangle(X, Y, CellsWide, CellsHigh); } }
    }

    public static class ItemViewFactory
    {
        // One line of an item description: which record field carries it, the
        // game's own template for the wording, a Russian fallback for the lines
        // the game does not translate, and the paired field when the record
        // stores a range.
        struct Stat
        {
            public string Field, Pair, Tag, Label;
            public bool Percent;
            public string Unit;
            // A duration that belongs to this line and to no other, so the game
            // writes it on the same line: "8127 bleeding over 3 sec.". Holds
            // the prefix of the duration pair, e.g. offensiveSlowBleedingDuration.
            public string Attach;
            // A plain fact rather than a bonus: "2.2 sec. skill cooldown" is
            // not something a plus sign belongs in front of.
            public bool NoSign;
            // This field's value is SECONDS, not damage: "stun for 1.5 s". The game's
            // template expects a ready phrase in its place, not a number.
            public bool Time;

            public Stat(string field, string pair, string tag, string label, bool percent)
            { Field = field; Pair = pair; Tag = tag; Label = label; Percent = percent; Unit = null;
              NoSign = false; Attach = null; Time = false; }
        }

        // The eleven damage types, and how the game names each one. Every damage
        // family below is the same handful of fields with the type spliced in, so
        // the table is generated rather than written out: a hand-written list is
        // exactly how a third of the bonuses came to be missing.
        struct DamageType
        {
            public string Field;    // the piece in the middle of the field name
            public string Tag;      // the piece in the middle of the tag name
            public string Flat;     // the game's tag for plain damage of this type
            // Our own words come from the language file (`lang/<CODE>/item.txt`, PLAN 7c,
            // stage 5): the full form for lines the game does not translate, and a short form
            // for the duration caption, where the full one does not fit the phrase.
            public string RuKey, ShortKey;
            public string Ru { get { return Lang.T(RuKey); } }
            public string Short { get { return Lang.T(ShortKey); } }
            public DamageType(string field, string tag, string flat, string ruKey, string shortKey)
            { Field = field; Tag = tag; Flat = flat; RuKey = ruKey; ShortKey = shortKey; }
        }

        static readonly DamageType[] Types = {
            new DamageType("Physical",  "Physical",  "DamageBasePhysical", "item.type.Physical", "item.typeShort.Physical"),
            new DamageType("Pierce",    "Pierce",    "DamagePierce",       "item.type.Pierce", "item.typeShort.Pierce"),
            new DamageType("Fire",      "Fire",      "DamageFire",         "item.type.Fire", "item.typeShort.Fire"),
            new DamageType("Cold",      "Cold",      "DamageCold",         "item.type.Cold", "item.typeShort.Cold"),
            new DamageType("Lightning", "Lightning", "DamageLightning",    "item.type.Lightning", "item.typeShort.Lightning"),
            new DamageType("Poison",    "Poison",    "DamagePoison",       "item.type.Poison", "item.typeShort.Poison"),
            new DamageType("Life",      "Life",      "DamageLife",         "item.type.Life", "item.typeShort.Life"),
            new DamageType("Aether",    "Aether",    "DamageAether",       "item.type.Aether", "item.typeShort.Aether"),
            new DamageType("Chaos",     "Chaos",     "DamageChaos",        "item.type.Chaos", "item.typeShort.Chaos"),
            new DamageType("Bleeding",  "Bleeding",  "DamageDurationBleeding", "item.type.Bleeding", "item.typeShort.Bleeding"),
            new DamageType("Elemental", "Elemental", "DamageElemental",    "item.type.Elemental", "item.typeShort.Elemental"),
        };

        // THE DAMAGE TYPE'S NAME FOR THE CONVERSION LINE — in the game's own words
        // (`tagConversionFire` is "Fire Damage"). Needed where the type arrives as a string
        // from a record rather than as a field.
        //
        // Until 17 September the words were cut out of our own phrase by string replacement.
        // That was a translation trap (in another language there is nothing to cut) and on top
        // of that produced two mangled Russian words.
        // THE ASCENDED BONUS TITLE LIVES IN ONE PLACE: the `--geartest` check uses it to tell
        // that the bonus reached the tooltip (`MainForm`), so the comparison must be against
        // this same text rather than a Russian string inside the check's code. The game has no
        // words for this title.
        public static string AscensionTitle { get { return Lang.T("item.part.ascension"); } }

        public static string DamageTypeRu(GameData game, string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            return DamageName(game, field);
        }

        static Stat Flat(string field, string tag, string label)
        { return new Stat(field + "Min", field + "Max", tag, label, false); }

        // A hindrance on the target: the number is SECONDS and prints as a phrase (see
        // Stat.Time).
        static Stat Effect(string field, string tag, string label)
        { Stat s = Flat(field, tag, label); s.Time = true; s.NoSign = true; return s; }

        static Stat Percent(string field, string tag, string label)
        { return new Stat(field, null, tag, label, true); }

        static Stat Seconds(string field, string label)
        { Stat s = new Stat(field + "Min", field + "Max", null, label, false); s.Unit = " " + Lang.T("item.unit.s"); return s; }

        // Damage the item deals, in the order the game lists it.
        //
        // THE TABLES ARE REBUILT WHEN THE LANGUAGE CHANGES (stage 5): their labels come from
        // the language file but are built once — so we must remember which language they were
        // built in (`Lang.Version`).
        static Stat[] offenceStats; static int offenceVer = -1;
        static Stat[] OffenceStats
        {
            get
            {
                int v = Lang.Version;
                if (offenceVer != v) { offenceStats = BuildOffence(); offenceVer = v; }
                return offenceStats;
            }
        }

        static Stat[] BuildOffence()
        {
            List<Stat> t = new List<Stat>();
            foreach (DamageType d in Types)
            {
                t.Add(Flat("offensive" + d.Field, d.Flat, d.Ru));
                t.Add(Flat("offensiveBase" + d.Field, d.Flat, d.Ru));
            }
            t.Add(Flat("offensiveBonusPhysical", "DamageBonusPhysical", Lang.T("item.stat.offensiveBonusPhysical")));
            t.Add(Flat("offensivePierceRatio", "DamageBasePierceRatio", Lang.T("item.stat.offensivePierceRatio")));
            t.Add(Flat("offensiveLifeLeech", "DamageLifeLeech", Lang.T("item.stat.offensiveLifeLeech")));
            t.Add(Flat("offensiveManaLeech", "DamageManaDrain", Lang.T("item.stat.offensiveManaLeech")));
            t.Add(Flat("offensivePercentCurrentLife", "DamagePercentCurrentLife", Lang.T("item.stat.offensivePercentCurrentLife")));

            t.Add(Percent("offensiveTotalDamageModifier", "tagDamageModifierTotalDamage", Lang.T("item.stat.offensiveTotalDamageModifier")));
            foreach (DamageType d in Types)
                t.Add(Percent("offensive" + d.Field + "Modifier", "DamageModifier" + d.Tag, d.Ru));
            t.Add(Percent("offensiveCritDamageModifier", "tagDamageModifierCritDamage", Lang.T("item.stat.offensiveCritDamageModifier")));
            // "TOTAL DAMAGE MODIFIED BY N%" — the line was MISSING ENTIRELY, although the
            // field has gone into the calculation since 23 August (a separate multiplier on
            // top of everything, MECHANICS 19k and 22g). So the display fell behind the
            // calculation again — the second such case in a day, and the user found both.
            //
            // THE MEASUREMENT — `_Acid Vines`, 1 September: the Worldkeeper's Garden set
            // modifies Grasping Vines through `set_d313_graspingvines.dbr`, which holds
            // `offensiveDamageMultModifier 200`, and the game prints "total damage modified by
            // 200%" under the set. In our own block there stood only the skill cooldown and
            // the energy-cost line.
            //
            // WHY `--saidtest` DID NOT CATCH IT: it walks the fields of the ITEMS' OWN records
            // (base, prefix, suffix, relic, augment, ascension), while this field sits in a
            // separate MODIFIER record the item merely points to. The gap in coverage is named
            // here on purpose: such records are not covered by the check at all.
            t.Add(Percent("offensiveDamageMultModifier", "tagDamageModifierDamageMult",
                          Lang.T("item.stat.offensiveDamageMultModifier")));
            t.Add(Percent("offensivePierceRatioModifier", "DamageModifierPierceRatio", Lang.T("item.stat.offensivePierceRatioModifier")));

            // Damage over time: the same types again, dealt across a duration.
            foreach (DamageType d in Types)
            {
                // Each damage-over-time type carries its own duration field, so
                // the seconds belong to that one line and the game writes them
                // there: "8127 bleeding over 3 sec.". A duration with no damage
                // of its type beside it still gets a line of its own.
                Stat dot = Flat("offensiveSlow" + d.Field, "DamageDuration" + d.Tag,
                                Lang.T("item.label.overTime", d.Ru));
                dot.Attach = "offensiveSlow" + d.Field + "Duration";
                t.Add(dot);
                t.Add(Seconds("offensiveSlow" + d.Field + "Duration", Lang.T("item.label.action")));
                t.Add(Percent("offensiveSlow" + d.Field + "Modifier", "DamageDurationModifier" + d.Tag,
                              Lang.T("item.label.overTime", d.Ru)));
                // The short form of the name, for "duration of fire damage". The full form used
                // to be cut down here by string replacement — in another language there would
                // be nothing to cut.
                t.Add(Percent("offensiveSlow" + d.Field + "DurationModifier", null,
                              Lang.T("item.label.dotDuration", d.Short)));
            }

            // MANA LEECH OVER TIME is the same kind of dot, but it has no damage type of its
            // own, so the loop above does not create it, and until 17 August the tooltip had NO
            // line for it at all. Noticed by the user on a Mythical Witchstalker (`_Amakata`,
            // bag 4): the game prints "108 mana leech over 3 s [108-159] [3-3]" and we had no
            // such line.
            //
            // THE NUMBER WAS COMPUTED CORRECTLY, AND HAD BEEN FOR LONG:
            // `offensiveSlowManaLeachMin` stands fourth in the stream and gives 36 per second,
            // i.e. exactly those 108 over three seconds (measured 15 August, ItemOrder.Dot).
            // Only the display was missing — the field appeared in no line table and nobody
            // printed it. That is the very kind of stat we "covered up": computed, compared by
            // dump, and never shown to the user.
            //
            // The words come from the game: the tags `DamageDurationManaLeach` and
            // `DamageDurationModifierManaLeach`.
            Stat leach = Flat("offensiveSlowManaLeach", "DamageDurationManaLeach", Lang.T("item.stat.offensiveSlowManaLeach"));
            leach.Attach = "offensiveSlowManaLeachDuration";
            t.Add(leach);
            t.Add(Seconds("offensiveSlowManaLeachDuration", Lang.T("item.label.action")));
            t.Add(Percent("offensiveSlowManaLeachModifier", "DamageDurationModifierManaLeach",
                          Lang.T("item.stat.offensiveSlowManaLeach")));
            t.Add(Percent("offensiveSlowManaLeachDurationModifier", null, Lang.T("item.stat.offensiveSlowManaLeachDurationModifier")));

            // Debuffs a skill lays on its target.
            t.Add(Flat("offensiveFumble", "DamageDurationFumble", Lang.T("item.stat.offensiveFumble")));
            t.Add(Seconds("offensiveFumbleDuration", Lang.T("item.label.action")));
            t.Add(Flat("offensiveProjectileFumble", "DamageDurationProjectileFumble",
                       Lang.T("item.stat.offensiveProjectileFumble")));
            t.Add(Seconds("offensiveProjectileFumbleDuration", Lang.T("item.label.action")));
            // …AND WITH ITS DURATION, the way the game does it: "270 reduced defensive ability
            // FOR 3 S". The duration sits alongside (`…DurationMin`) and until 12 September was
            // not printed: on the berserker's Recklessness the tooltip broke off at the number,
            // and it was unclear whether the cut was permanent or three seconds long. The
            // binding uses the same `Attach` as resistance reduction.
            Stat oaCut = Flat("offensiveSlowOffensiveAbility", "DamageDurationOffensiveAbility",
                              Lang.T("item.stat.offensiveSlowOffensiveAbility"));
            oaCut.Attach = "offensiveSlowOffensiveAbilityDuration";
            t.Add(oaCut);
            Stat daCut = Flat("offensiveSlowDefensiveAbility", "DamageDurationDefensiveAbility",
                              Lang.T("item.stat.offensiveSlowDefensiveAbility"));
            daCut.Attach = "offensiveSlowDefensiveAbilityDuration";
            t.Add(daCut);
            t.Add(Flat("offensiveSlowRunSpeed", "DamageDurationRunSpeed", Lang.T("item.stat.offensiveSlowRunSpeed")));
            t.Add(Flat("offensiveSlowAttackSpeed", "DamageDurationAttackSpeed", Lang.T("item.stat.offensiveSlowAttackSpeed")));
            t.Add(Flat("offensiveSlowSpellCastSpeed", "DamageDurationSpellCastSpeed", Lang.T("item.stat.offensiveSlowSpellCastSpeed")));
            // Hindrances the item applies to a target on a strike. They do not go into the damage
            // calculation and must not — they are NOT damage but a duration in seconds
            // ("knockdown for 1 s") — but they must be shown: the player sees them in the game's
            // tooltip and we had none of them. The `--itemstats` census found it: the fields were
            // listed as going NOWHERE while the game does have a caption for them.
            t.Add(Effect("offensiveKnockdown", "DamageKnockdown", Lang.T("item.stat.offensiveKnockdown")));
            t.Add(Effect("offensiveStun", "DamageStun", Lang.T("item.stat.offensiveStun")));
            // "+10% stun duration" is a PERCENT, not seconds, and it has a field of its own:
            // `offensiveStunModifier` beside the `offensiveStun` that holds seconds. Found
            // 26 August by `--saidtest`, when `compa_hellbaneammo` appeared in the saves: the
            // field is there, the game's caption is there (`DamageModifierStun`), and not one of
            // our lines spoke of it.
            t.Add(Percent("offensiveStunModifier", "DamageModifierStun", Lang.T("item.stat.offensiveStunModifier")));
            t.Add(Effect("offensiveFreeze", "DamageFreeze", Lang.T("item.stat.offensiveFreeze")));
            t.Add(Effect("offensivePetrify", "DamagePetrify", Lang.T("item.stat.offensivePetrify")));
            t.Add(Effect("offensiveSleep", "DamageSleep", Lang.T("item.stat.offensiveSleep")));
            t.Add(Effect("offensiveTrap", "DamageTrap", Lang.T("item.stat.offensiveTrap")));
            t.Add(Effect("offensiveConfusion", "DamageConfusion", Lang.T("item.stat.offensiveConfusion")));
            t.Add(Flat("offensiveTaunt", "DamageTaunt", Lang.T("item.stat.offensiveTaunt")));
            t.Add(Effect("offensiveFear", "DamageFear", Lang.T("item.stat.offensiveFear")));
            t.Add(Effect("offensiveConvert", "DamageConvert", Lang.T("item.stat.offensiveConvert")));
            t.Add(Effect("offensiveDisruption", "DamageDisruption", Lang.T("item.stat.offensiveDisruption")));
            t.Add(Flat("offensiveManaBurnDrain", "DamageManaBurn", Lang.T("item.stat.offensiveManaBurnDrain")));
            t.Add(Percent("offensiveManaBurnDamageRatio", "DamageManaBurnRatio",
                          Lang.T("item.stat.offensiveManaBurnDamageRatio")));

            t.Add(Percent("offensiveTotalResistanceReductionPercent", "DamageTotalResistanceReductionPercent",
                          Lang.T("item.label.resCutAll")));
            t.Add(Flat("offensiveTotalResistanceReductionAbsolute", "DamageTotalResistanceReductionAbsolute",
                       Lang.T("item.label.resCutAll")));

            // RESISTANCE AND DAMAGE REDUCTION ALSO COME AS A "min–max" PAIR WITH SECONDS.
            // The line above catches the single `…Percent` field, while the records also hold
            // another form — `…PercentMin` with its own duration (`…PercentDurationMin`) — and
            // nobody caught it: a skill modifier may provide this value indirectly.
            // (`_Amakata`, bag 4) the suffix gives 20% reduced resistances for 5 seconds, and
            // the tooltip had no such line at all. The words are the game's own.
            Stat resCut = Flat("offensiveTotalResistanceReductionPercent",
                               "DamageTotalResistanceReductionPercent", Lang.T("item.label.resCutAllOf"));
            resCut.Attach = "offensiveTotalResistanceReductionPercentDuration";
            t.Add(resCut);
            t.Add(Seconds("offensiveTotalResistanceReductionPercentDuration", Lang.T("item.label.action")));
            // REDUCTION OF A SINGLE TYPE'S RESISTANCE, not only of "all at once".
            //
            // The game has exactly three families, and this is a census of its own tags rather
            // than a guess: `DamageTotalResistanceReduction…`, `DamagePhysical…` and
            // `DamageElemental…`, each in two forms — `Percent` and `Absolute`. "All at once"
            // had stood here since 18 August; nobody caught the other two.
            //
            // `--saidtest` found this on 1 September, as soon as a new character appeared in
            // `saves/`: the quest item Huge Wood (`quest_areah_woodcarving_02.dbr`) holds
            // `offensivePhysicalResistanceReductionPercentMin 20` with its own 2-second
            // duration, and the tooltip said nothing of it. That is exactly why the check
            // exists: new items bring new fields.
            foreach (string kind in new[] { "Physical", "Elemental" })
            {
                Stat cutP = Flat("offensive" + kind + "ResistanceReductionPercent",
                                 "Damage" + kind + "ResistanceReductionPercent",
                                 Lang.T("item.label.resCutOf"));
                cutP.Attach = "offensive" + kind + "ResistanceReductionPercentDuration";
                t.Add(cutP);
                t.Add(Seconds("offensive" + kind + "ResistanceReductionPercentDuration", Lang.T("item.label.action")));
                Stat cutA = Flat("offensive" + kind + "ResistanceReductionAbsolute",
                                 "Damage" + kind + "ResistanceReductionAbsolute",
                                 Lang.T("item.label.resCutOf"));
                cutA.Attach = "offensive" + kind + "ResistanceReductionAbsoluteDuration";
                t.Add(cutA);
                t.Add(Seconds("offensive" + kind + "ResistanceReductionAbsoluteDuration", Lang.T("item.label.action")));
                t.Add(Percent("offensive" + kind + "ResistanceReductionPercent",
                              "Damage" + kind + "ResistanceReductionPercent",
                              Lang.T("item.label.resCut")));
                t.Add(Flat("offensive" + kind + "ResistanceReductionAbsolute",
                           "Damage" + kind + "ResistanceReductionAbsolute",
                           Lang.T("item.label.resCut")));
            }

            Stat dmgCut = Flat("offensiveTotalDamageReductionPercent",
                               "DamageTotalDamageReductionPercent", Lang.T("item.stat.offensiveTotalDamageReductionPercent"));
            dmgCut.Attach = "offensiveTotalDamageReductionPercentDuration";
            t.Add(dmgCut);
            t.Add(Seconds("offensiveTotalDamageReductionPercentDuration", Lang.T("item.label.action")));

            // TOTAL SLOW ON THE TARGET is also a pair with seconds; its shelf-mates (run
            // speed, attack speed, casting) were added long ago, this one was not.
            t.Add(Flat("offensiveSlowTotalSpeed", "DamageDurationTotalSpeed", Lang.T("item.stat.offensiveSlowTotalSpeed")));

            // What the item throws back at whoever hit it.
            foreach (DamageType d in Types)
                t.Add(Flat("retaliation" + d.Field, "Retaliation" + d.Tag, Lang.T("item.label.retaliation", d.Ru)));
            // RETALIATION HINDRANCES. The loop above makes only retaliation DAMAGE by type,
            // while retaliation freeze, fear, slow and retaliation poison over time are
            // separate fields, and until 17 August nobody printed them. The edit-window check
            // found them once it was widened to every item in the saves.
            t.Add(Flat("retaliationFreeze", "RetaliationFreeze", Lang.T("item.stat.retaliationFreeze")));
            t.Add(Flat("retaliationConfusion", "RetaliationConfusion", Lang.T("item.stat.retaliationConfusion")));
            // Retaliation lightning over time — the same shape as retaliation poison below;
            // `--saidtest` found it on the Mythical Divinesteel Shoulderguard.
            Stat retLightning = Flat("retaliationSlowLightning", "RetaliationDurationLightning",
                                     Lang.T("item.stat.retaliationSlowLightning"));
            retLightning.Attach = "retaliationSlowLightningDuration";
            t.Add(retLightning);
            t.Add(Seconds("retaliationSlowLightningDuration", Lang.T("item.label.action")));
            t.Add(Flat("retaliationFear", "RetaliationFear", Lang.T("item.stat.retaliationFear")));
            t.Add(Flat("retaliationSlowAttackSpeed", "RetaliationDurationAttackSpeed",
                       Lang.T("item.stat.retaliationSlowAttackSpeed")));
            t.Add(Flat("retaliationSlowRunSpeed", "RetaliationDurationRunSpeed",
                       Lang.T("item.stat.retaliationSlowRunSpeed")));
            Stat retPoison = Flat("retaliationSlowPoison", "RetaliationDurationPoison", Lang.T("item.stat.retaliationSlowPoison"));
            retPoison.Attach = "retaliationSlowPoisonDuration";
            t.Add(retPoison);
            t.Add(Seconds("retaliationSlowPoisonDuration", Lang.T("item.label.action")));
            // RETALIATION MANA LEECH — the same shape as retaliation poison and lightning
            // above. `--saidtest` found it on 9 September: one item in the whole base
            // (`gearhead/d001_head.dbr`), and the game prints "34 retaliation mana leech over
            // 2 s" for it. Noticed by the user in game.
            //
            // THE GAME SPELLS THE FIELD WITH `a`: `…ManaLeach`, not `Leech`. The tag of the
            // same family is `RetaliationDurationManaLeach`.
            Stat retLeach = Flat("retaliationSlowManaLeach", "RetaliationDurationManaLeach",
                                 Lang.T("item.stat.retaliationSlowManaLeach"));
            retLeach.Attach = "retaliationSlowManaLeachDuration";
            t.Add(retLeach);
            t.Add(Seconds("retaliationSlowManaLeachDuration", Lang.T("item.label.action")));
            t.Add(Percent("retaliationTotalDamageModifier", "tagRetaliationModifierTotalDamage",
                          Lang.T("item.stat.retaliationTotalDamageModifier")));
            // "TOTAL RETALIATION DAMAGE MODIFIED BY N%" — the twin of
            // `offensiveDamageMultModifier` above, and until 28 September the only field in the
            // whole base the tooltip did not say: `--saidtest` reddened on it (one item —
            // an item modifier may carry `retaliationDamageMultModifier -50`. The game prints
            // it — screenshot of 28 September — and the wording is the game's own tag
            // `tagRetaliationModifierDamageMult` with the corresponding percentage.
            t.Add(Percent("retaliationDamageMultModifier", "tagRetaliationModifierDamageMult",
                          Lang.T("item.stat.retaliationDamageMultModifier")));
            foreach (DamageType d in Types)
                t.Add(Percent("retaliation" + d.Field + "Modifier", "RetaliationModifier" + d.Tag,
                              Lang.T("item.label.retaliation", d.Ru)));
            return t.ToArray();
        }

        // Armour, resistances and everything about the character.
        static Stat[] defenceStats; static int defenceVer = -1;
        static Stat[] DefenceStats
        {
            get
            {
                int v = Lang.Version;
                if (defenceVer != v) { defenceStats = BuildDefence(); defenceVer = v; }
                return defenceStats;
            }
        }

        static Stat[] BuildDefence()
        {
            List<Stat> t = new List<Stat>();
            // defensiveProtection is deliberately absent: armour is already the
            // headline above the bonuses, and listing it twice reads as a bug.
            t.Add(Percent("defensiveProtectionModifier", "DefenseProtectionModifier", Lang.T("item.label.armour")));
            t.Add(Percent("defensiveAbsorptionModifier", "DefenseAbsorptionModifier", Lang.T("item.stat.defensiveAbsorptionModifier")));
            // On a shield these three go together: how often it blocks, how much
            // it blocks, and how long it takes to be ready again.
            t.Add(Percent("defensiveBlockChance", "DefenseBlockChance", Lang.T("item.stat.defensiveBlockChance")));
            t.Add(new Stat("defensiveBlock", null, "DefenseBlock", Lang.T("item.stat.defensiveBlock"), false));
            t.Add(new Stat("blockRecoveryTime", null, "ShieldBlockRecoveryTime", Lang.T("item.stat.blockRecoveryTime"), false));
            t.Add(Percent("defensiveBlockModifier", "DefenseBlockModifier", Lang.T("item.stat.defensiveBlockModifier")));
            t.Add(Percent("defensiveBlockAmountModifier", "DefenseBlockAmountModifier", Lang.T("item.stat.defensiveBlockAmountModifier")));
            t.Add(new Stat("blockAbsorption", null, null, Lang.T("item.stat.blockAbsorption"), true));
            t.Add(Percent("skillComboChargeSpendReduction", "SkillComboChargeSpendReduction",
                          Lang.T("item.stat.skillComboChargeSpendReduction")));
            t.Add(Percent("characterDefensiveBlockRecoveryReduction", "tagCharDefensiveBlockRecoveryReduction",
                          Lang.T("item.stat.characterDefensiveBlockRecoveryReduction")));

            foreach (DamageType d in Types)
            {
                t.Add(Percent("defensive" + d.Field, "Defense" + d.Tag, Lang.T("item.label.resist", d.Ru)));
                t.Add(Percent("defensive" + d.Field + "Modifier", "Defense" + d.Tag + "Modifier",
                              Lang.T("item.label.resist", d.Ru)));
                t.Add(Percent("defensive" + d.Field + "MaxResist", "Defense" + d.Tag + "MaxResist",
                              Lang.T("item.label.maxResist", d.Ru)));
                // How long the type's lingering damage lasts on you, cut by a
                // percentage. A modifier can affect both poison and
                // bleeding duration, and neither line was showing.
                t.Add(Percent("defensive" + d.Field + "Duration", "Defense" + d.Tag + "Duration",
                              Lang.T("item.label.duration", d.Ru)));
                t.Add(Percent("defensive" + d.Field + "DurationModifier", "Defense" + d.Tag + "DurationModifier",
                              Lang.T("item.label.duration", d.Ru)));
            }
            t.Add(Percent("defensiveElementalResistance", "DefenseElementalResistance", Lang.T("item.stat.defensiveElementalResistance")));
            t.Add(Percent("defensiveAllResistance", "DefenseAllResistance", Lang.T("item.stat.defensiveAllResistance")));
            t.Add(Percent("defensiveAllMaxResist", "DefenseAllMaxResist", Lang.T("item.stat.defensiveAllMaxResist")));
            // REFLECTION RESISTANCE AND LIFE-LEECH-OVER-TIME RESISTANCE — two fields the table
            // lacked; the edit-window check found them across every item in the saves. The game
            // has its own words: `DefenseReflectResist` prints the whole line, `DefenseLifeLeach`
            // prints the percent one.
            t.Add(Percent("defensivePercentReflectionResistance", "DefenseReflectResist",
                          Lang.T("item.stat.defensivePercentReflectionResistance")));
            t.Add(Percent("defensiveSlowLifeLeach", "DefenseLifeLeach", Lang.T("item.stat.defensiveSlowLifeLeach")));
            t.Add(Percent("defensiveSlowManaLeach", "DefenseManaLeach", Lang.T("item.stat.defensiveSlowManaLeach")));
            // "+N armour bonus" on components — the game's tag
            // `DefenseAbsorptionProtectionBonus`. Twenty-nine items in the saves, and not one
            // of them showed this line.
            t.Add(new Stat("defensiveBonusProtection", null, "DefenseAbsorptionProtectionBonus",
                           Lang.T("item.stat.defensiveBonusProtection"), false));

            string[,] control = {
                { "defensiveStun", "DefenseStun", Lang.T("item.stat.defensiveStun") },
                { "defensiveFreeze", "DefenseFreeze", Lang.T("item.stat.defensiveFreeze") },
                { "defensivePetrify", "DefensePetrify", Lang.T("item.stat.defensivePetrify") },
                { "defensiveSleep", "DefenseSleep", Lang.T("item.stat.defensiveSleep") },
                { "defensiveTrap", "DefenseTrap", Lang.T("item.stat.defensiveTrap") },
                { "defensiveDisruption", "DefenseDisruption", Lang.T("item.stat.defensiveDisruption") },
                { "defensiveKnockdown", "DefenseKnockdown", Lang.T("item.stat.defensiveKnockdown") },
                { "defensiveConfusion", "DefenseConfusion", Lang.T("item.stat.defensiveConfusion") },
                { "defensiveFear", "DefenseFear", Lang.T("item.stat.defensiveFear") },
                { "defensiveTaunt", "DefenseTaunt", Lang.T("item.stat.defensiveTaunt") },
                { "defensiveLifeLeach", "DefenseLifeLeach", Lang.T("item.stat.defensiveLifeLeach") },
                { "defensiveManaLeach", "DefenseManaLeach", Lang.T("item.stat.defensiveManaLeach") },
                { "defensiveReflect", "DefenseReflect", Lang.T("item.stat.defensiveReflect") },
                // The field does have a tag — `tagTotalSpeedResistance` — and it carries NO
                // SIGN. While `null` stood here, the line was built from our own words and
                // got a "+" the game never prints: a tooltip shot (`_Amakata`, bag 2) read
                // "33% slow resistance [24-36]" where we had "+33%".
                { "defensiveTotalSpeedResistance", "tagTotalSpeedResistance", Lang.T("item.stat.defensiveTotalSpeedResistance") },
                { "defensivePercentCurrentLife", "DefensePercentCurrentLife", Lang.T("item.stat.defensivePercentCurrentLife") },
                // Found by the user on 22 August on a relic: the line was printed as the field
                // name, without the game's words. Its tag is its own and is not about
                // resistance but about DURATION — "17% reduced crowd-control duration"; the
                // tooltip shot shows exactly that.
                { "defensiveCrowdControl", "DefenseCrowdControl", Lang.T("item.stat.defensiveCrowdControl") },
                // The line was missing entirely and was found the same way as its neighbour:
                // the user re-took `saves/_Itaan` on 3 September, the fresh save brought THREE
                // items with this field, and `--edittest` went red. The game's tag is its own
                // and is also about duration rather than resistance.
                { "defensiveConvert", "DefenseConvert", Lang.T("item.stat.defensiveConvert") },
            };
            for (int i = 0; i < control.GetLength(0); i++)
                t.Add(Percent(control[i, 0], control[i, 1], control[i, 2]));

            string[,] chars = {
                { "characterStrength", "tagCharAttribute02", Lang.T("item.stat.characterStrength") },
                { "characterDexterity", "tagCharAttribute01", Lang.T("item.stat.characterDexterity") },
                { "characterIntelligence", "tagCharAttribute03", Lang.T("item.stat.characterIntelligence") },
                { "characterLife", "tagCharAttribute04", Lang.T("item.stat.characterLife") },
                { "characterMana", "tagCharAttribute05", Lang.T("item.stat.characterMana") },
                { "characterLifeRegen", "tagCharLifeRegen", Lang.T("item.stat.characterLifeRegen") },
                { "characterManaRegen", "tagCharManaRegen", Lang.T("item.stat.characterManaRegen") },
                { "characterOffensiveAbility", "tagCharOffensiveAbility", Lang.T("item.stat.characterOffensiveAbility") },
                { "characterDefensiveAbility", "tagCharDefensiveAbility", Lang.T("item.stat.characterDefensiveAbility") },
                { "characterConstitution", "tagCharConstitution", Lang.T("item.stat.characterConstitution") },
                { "characterRunSpeedMax", "tagCharRunSpeedMax", Lang.T("item.stat.characterRunSpeedMax") },
                { "characterManaLimitReserve", "tagCharManaLimitReserve", Lang.T("item.stat.characterManaLimitReserve") },
            };
            for (int i = 0; i < chars.GetLength(0); i++)
                t.Add(new Stat(chars[i, 0], null, chars[i, 1], chars[i, 2], false));

            string[,] charPercent = {
                { "characterStrengthModifier", "tagCharAttribute02Modifier", Lang.T("item.stat.characterStrengthModifier") },
                { "characterDexterityModifier", "tagCharAttribute01Modifier", Lang.T("item.stat.characterDexterityModifier") },
                { "characterIntelligenceModifier", "tagCharAttribute03Modifier", Lang.T("item.stat.characterIntelligenceModifier") },
                { "characterLifeModifier", "tagCharAttribute04Modifier", Lang.T("item.stat.characterLifeModifier") },
                { "characterManaModifier", "tagCharAttribute05Modifier", Lang.T("item.stat.characterManaModifier") },
                { "characterLifeRegenModifier", "tagCharLifeRegenModifier", Lang.T("item.stat.characterLifeRegenModifier") },
                { "characterManaRegenModifier", "tagCharManaRegenModifier", Lang.T("item.stat.characterManaRegenModifier") },
                { "characterOffensiveAbilityModifier", "tagCharOffensiveAbilityModifier", Lang.T("item.stat.characterOffensiveAbilityModifier") },
                { "characterDefensiveAbilityModifier", "tagCharDefensiveAbilityModifier", Lang.T("item.stat.characterDefensiveAbilityModifier") },
                { "characterAttackSpeedModifier", "tagCharAttackSpeedModifier", Lang.T("item.stat.characterAttackSpeedModifier") },
                { "characterSpellCastSpeedModifier", "tagCharSpellCastSpeedModifier", Lang.T("item.stat.characterSpellCastSpeedModifier") },
                { "characterRunSpeedModifier", "tagCharRunSpeedModifier", Lang.T("item.stat.characterRunSpeedModifier") },
                { "characterTotalSpeedModifier", "tagCharTotalSpeedModifier", Lang.T("item.stat.characterTotalSpeedModifier") },
                { "characterDodgePercent", "tagCharDodgeModifier", Lang.T("item.stat.characterDodgePercent") },
                { "characterDeflectProjectile", "tagCharDeflectModifier", Lang.T("item.stat.characterDeflectProjectile") },
                { "characterHealIncreasePercent", "tagCharHealIncrease", Lang.T("item.stat.characterHealIncreasePercent") },
                { "characterEnergyAbsorptionPercent", "tagCharEnergyAbsorption", Lang.T("item.stat.characterEnergyAbsorptionPercent") },
                { "characterIncreasedExperience", "tagCharIncreasedExperience", Lang.T("item.stat.characterIncreasedExperience") },
                { "characterLightRadius", "tagCharLightRadius", Lang.T("item.stat.characterLightRadius") },
                { "characterGlobalReqReduction", "tagCharItemGlobalReduction", Lang.T("item.stat.characterGlobalReqReduction") },
                { "characterLevelReqReduction", "tagCharLevelReqReduction", Lang.T("item.stat.characterLevelReqReduction") },
                { "characterManaLimitReserveReduction", "tagCharManaLimitReserveReduction", Lang.T("item.stat.characterManaLimitReserveReduction") },
                { "characterConstitutionModifier", "tagCharConstitutionModifier", Lang.T("item.stat.characterConstitutionModifier") },
                { "characterRunSpeedMaxModifier", "tagCharRunSpeedMaxModifier", Lang.T("item.stat.characterRunSpeedMaxModifier") },
                // The two caps the ascension bonuses raise. The attack-speed one
                // was already used in the damage model (cap = 200 + …) but had no
                // line of its own, so an item carrying it showed nothing.
                { "characterAttackSpeedMaxModifier", "tagCharAttackSpeedMaxModifier", Lang.T("item.stat.characterAttackSpeedMaxModifier") },
                { "characterSpellCastSpeedMaxModifier", "tagCharSpellCastSpeedMaxModifier", Lang.T("item.stat.characterSpellCastSpeedMaxModifier") },
                { "characterArmorStrengthReqReduction", "tagCharArmorStrengthReqReduction", Lang.T("item.stat.characterArmorStrengthReqReduction") },
                { "characterMeleeStrengthReqReduction", "tagCharMeleeStrengthReqReduction", Lang.T("item.stat.characterMeleeStrengthReqReduction") },
                { "characterMeleeDexterityReqReduction", "tagCharMeleeDexterityReqReduction", Lang.T("item.stat.characterMeleeDexterityReqReduction") },
                { "characterWeaponStrengthReqReduction", "tagCharWeaponStrengthReqReduction", Lang.T("item.stat.characterWeaponStrengthReqReduction") },
                { "characterHuntingDexterityReqReduction", "tagCharHuntingDexterityReqReduction", Lang.T("item.stat.characterHuntingDexterityReqReduction") },
                { "characterJewelryIntelligenceReqReduction", "tagCharJewelryIntelligenceReqReduction", Lang.T("item.stat.characterJewelryIntelligenceReqReduction") },
                { "characterShieldStrengthReqReduction", "tagCharShieldStrengthReqReduction", Lang.T("item.stat.characterShieldStrengthReqReduction") },
                { "skillCooldownReduction", "SkillCooldownReduction", Lang.T("item.stat.skillCooldownReduction") },
                { "skillManaCostReduction", "SkillManaCostReduction", Lang.T("item.stat.skillManaCostReduction") },
            };
            for (int i = 0; i < charPercent.GetLength(0); i++)
                t.Add(Percent(charPercent[i, 0], charPercent[i, 1], charPercent[i, 2]));
            return t.ToArray();
        }

        // What a skill costs and how it reaches: the lines the game puts above the
        // bonuses. None of this belongs to an item — an item has no cooldown — so
        // it is a table of its own, drawn only when a skill is being described.
        //
        // Some labels carry no placeholder, so the unit is emitted separately:
        // "2.2 sec. skill cooldown".
        static Stat[] skillFacts; static int skillFactsVer = -1;
        static Stat[] SkillFacts
        {
            get
            {
                int v = Lang.Version;
                if (skillFactsVer != v) { skillFacts = BuildSkillFacts(); skillFactsVer = v; }
                return skillFacts;
            }
        }

        static Stat[] BuildSkillFacts()
        {
            List<Stat> t = new List<Stat>();
            t.Add(Unit("skillActiveLifeCost", "ActiveLifeCost", Lang.T("item.stat.skillActiveLifeCost"), ""));
            t.Add(Unit("skillActiveManaCost", "ActiveManaCost", Lang.T("item.stat.skillActiveManaCost"), ""));
            t.Add(Unit("skillManaCost", "ManaCost", Lang.T("item.stat.skillManaCost"), ""));
            t.Add(Unit("skillCooldownTime", "CooldownTime", Lang.T("item.label.cooldown"), " " + Lang.T("item.unit.sec")));
            t.Add(Unit("skillActiveDuration", "ActiveDuration", Lang.T("item.stat.skillActiveDuration"), " " + Lang.T("item.unit.sec")));
            t.Add(Unit("skillTargetRadius", "ExplosionRadius", Lang.T("item.label.radius"), " " + Lang.T("item.unit.m")));
            t.Add(Unit("skillProjectileNumber", "ProjectileLaunchNumber", Lang.T("item.label.projectiles"), ""));
            t.Add(Unit("projectileLaunchNumber", "ProjectileLaunchNumber", Lang.T("item.label.projectiles"), ""));
            t.Add(Unit("skillTargetNumber", "TargetNumber", Lang.T("item.stat.skillTargetNumber"), ""));
            t.Add(Unit("projectilePiercingChance", "ProjectilePiercingChance", Lang.T("item.label.pierceChance"), "%"));
            t.Add(Unit("weaponDamagePct", "SkillWeaponDamageFormat", Lang.T("item.stat.weaponDamagePct"), "%"));
            t.Add(Unit("retaliationDamagePct", "SkillRetaliationDamageFormat",
                       Lang.T("item.stat.retaliationDamagePct"), "%"));
            return t.ToArray();
        }

        // Flat armour. On an item this is the headline above the bonuses, so it is
        // left out of the shared tables; a skill has no headline, and a flat armor
        // bonus was going missing. The game puts it after the
        // line about restored health, so it is a step of its own.
        static Stat[] skillArmour; static int skillArmourVer = -1;
        static Stat[] SkillArmour
        {
            get
            {
                int v = Lang.Version;
                if (skillArmourVer != v)
                {
                    skillArmour = new Stat[] {
                        new Stat("defensiveProtection", null, "DefenseAbsorptionProtectionPlus",
                                 Lang.T("item.label.armour"), false) };
                    skillArmourVer = v;
                }
                return skillArmour;
            }
        }

        static Stat Unit(string field, string tag, string label, string unit)
        { Stat s = new Stat(field, null, tag, label, false); s.Unit = unit; s.NoSign = true; return s; }

        // The same, but signed: on a skill MODIFIER the number is an addition, and the game
        // prints it with a plus ("+1.5 m radius").
        static Stat Signed(string field, string tag, string label, string unit)
        { Stat s = new Stat(field, null, tag, label, false); s.Unit = unit; return s; }

        // FIELDS THAT OCCUR ONLY ON A SKILL MODIFIER (`Skill_Modifier`), not on the skill
        // itself. A separate table on purpose: the game has two tags for them —
        // `CooldownCharges` ("Charges: 3") and `CooldownChargesMod` ("Charges: +1") — and on a
        // modifier record the second is the right one. Put them in the shared `SkillFacts` and
        // a granted skill's tooltip starts printing "Charges: +3" instead of "3".
        //
        // EVERY WORDING HERE IS THE GAME'S, not ours: the tag is named next to the field, and
        // the text comes from the installed language (rule 9). Ten of them were found on
        // 2 September, when `--saidtest` learned to ask modifier records: 20 kinds of unspoken
        // field across 493 items, and exactly those ten had a caption of their own in the game.
        // TWO MORE CAME ON 11 OCTOBER — the beam fields at the end of the list; the count is
        // deliberately not written as a number here, because the same check will bring more.
        static Stat[] modifierFacts; static int modifierFactsVer = -1;
        static Stat[] ModifierFacts
        {
            get
            {
                int v = Lang.Version;
                if (modifierFactsVer != v) { modifierFacts = BuildModifierFacts(); modifierFactsVer = v; }
                return modifierFacts;
            }
        }

        static Stat[] BuildModifierFacts()
        {
            List<Stat> t = new List<Stat>();
            t.Add(Unit("skillChanceWeight", "SkillChanceWeightMod", Lang.T("item.stat.skillChanceWeight"), "%"));
            t.Add(Unit("skillTargetAngle", "TargetAngleMod", Lang.T("item.stat.skillTargetAngle"), ""));
            t.Add(Unit("cooldownCharges", "CooldownChargesMod", Lang.T("item.stat.cooldownCharges"), ""));
            // On a modifier the cooldown is an ADDITION IN SECONDS and usually negative
            // (`cooldownTime -6` on one summon skill's modifier), so the sign comes from the
            // number itself, and the game's tag has no room for one.
            t.Add(Unit("cooldownTime", "CooldownTime", Lang.T("item.label.cooldown"), " " + Lang.T("item.unit.sec")));
            t.Add(Unit("petLimit", "SkillPetLimitMod", Lang.T("item.stat.petLimit"), ""));
            t.Add(Unit("projectilePiercing", "ProjectilePiercingChanceMod", Lang.T("item.label.pierceChance"), "%"));
            t.Add(Unit("damageAbsorptionPercent", "SkillDamageAbsorptionPercent", Lang.T("item.stat.damageAbsorptionPercent"), "%"));
            // The radius tag has no room for a number (just the word), and the game builds the
            // line with the `SkillDistanceFormatMod` template. The result is the same word for
            // word, so we place the sign ourselves.
            t.Add(Signed("explosionRadius", "ExplosionRadius", Lang.T("item.label.radius"), " " + Lang.T("item.unit.m")));
            t.Add(Unit("projectileLaunchRotation", "ProjectileLaunchRotation", Lang.T("item.stat.projectileLaunchRotation"), ""));
            // BEAM ARC AND BEAM COUNT — the twelfth and eleventh wordings, found 11 October by
            // `--saidtest` exactly as the others were: 45 new items appeared in the saves (4720 →
            // 4765) and one medal's modifier record brought two fields no line spoke of
            // (`records/skills/itemskillsgdx3/skillmodifiers/mi/medal_b307_aetherray.dbr`,
            // `beamAngle 25` and `beamNumber 1`). Nothing had to be invented: the game's own tags
            // are `BeamAngleMod` and `BeamNumberMod`, i.e. the `Mod` forms, because these fields
            // sit on a MODIFIER record — the same reasoning as `cooldownCharges` above.
            t.Add(Unit("beamAngle", "BeamAngleMod", Lang.T("item.stat.beamAngle"), ""));
            t.Add(Unit("beamNumber", "BeamNumberMod", Lang.T("item.stat.beamNumber"), ""));
            // THE ONE FIELD OF TWENTY LEFT UNENTERED ON 2 SEPTEMBER — entered on the 3rd from
            // a screenshot. I had reasoned that `tagSkillPetTimeToLiveMod` belongs to another
            // field (`petTimeToLive`) and that `spawnObjectsTimeToLive` has no key of its own
            // in the game, so it stayed silent. The user sent a tooltip where the game prints
            // "+10 s lifetime" — exactly this tag, on exactly this field.
            //
            // THE LESSON IS RECORDED IN HANDOFF: "the field has no key of its own" is a reason
            // to stay silent, not a proof. The game picks a tag BY MEANING, not by field name.
            t.Add(Unit("spawnObjectsTimeToLive", "tagSkillPetTimeToLiveMod", Lang.T("item.stat.spawnObjectsTimeToLive"), ""));
            return t.ToArray();
        }

        // Which hands the skill whose tooltip is being printed strikes with, and whether there
        // is a second hand at all. Set by whoever knows the character (the skills window) for
        // the duration of printing — the same trick as SkillCooldownCut. Without a character it
        // stays Both/false and the line prints as before, naming no hand: inventing a hand
        // where the gear is unknown is not allowed.
        public static Hands SkillHandsNow = Hands.Both;
        public static bool SkillHandsTwo;

        // EACH HAND's damage per strike — {main low, main high, off low, off high}. The bracket
        // the game prints next to the share comes from it: "180% main-hand weapon damage
        // (6196 - 14530)". Empty means we print without the bracket (as it was until
        // 23 August: the tree showed the skill's record, not a calculation).
        public static double[] SkillHandRange;

        // "180% main-hand weapon damage (6196 - 14530)" — IN THE GAME'S OWN WORDS AND SHAPE.
        //
        // The `SkillWeaponDamageMainhandFormat` template holds the share and the bracket with
        // the hand's damage. The bracket is filled from `SkillHandRange`, which the skills
        // window sets after computing the strike. With no numbers the bracket is cut off and
        // only the share remains: inventing them is not allowed, and the game's words are
        // better than ours anyway — at a patch they change together with the game, and the
        // translation stays the game's own.
        // The hand line gets its own colour, the same as on the damage tab.
        static TipLine HandTip(string text, int indent)
        {
            TipLine made = new TipLine(text, TipKind.Bonus, indent);
            made.Override = TipPalette.Hand;
            return made;
        }

        static string HandWords(GameData game, string tag, double pct, string fallback, int hand)
        {
            double lo = 0, hi = 0;
            bool haveNumbers = SkillHandRange != null && SkillHandRange.Length >= 4;
            if (haveNumbers)
            {
                lo = Math.Round(SkillHandRange[hand * 2] * pct / 100);
                hi = Math.Round(SkillHandRange[hand * 2 + 1] * pct / 100);
                if (hi < 0.5) haveNumbers = false;
            }
            string text = game == null ? null : game.FormatLocalTag(tag, pct, lo, hi);
            if (!string.IsNullOrEmpty(text))
            {
                if (!haveNumbers)
                {
                    int cut = text.LastIndexOf('(');
                    if (cut > 0) text = text.Substring(0, cut).TrimEnd();
                }
                return text;
            }
            return Lang.T("item.weaponDamageHand", Fmt(pct), fallback) +
                   (haveNumbers ? " (" + Fmt(lo) + " - " + Fmt(hi) + ")" : "");
        }

        public static ItemView Build(GameData game, Item item, bool showRawFields)
        {
            return Build(game, item, showRawFields, null);
        }

        // With a character the description also says which requirements they do
        // not meet, and which skill bonuses belong to masteries they never took.
        // Without one it just states the numbers.
        public static ItemView Build(GameData game, Item item, bool showRawFields, CharacterContext who)
        { return Build(game, item, showRawFields, who, true); }

        // A ZERO IN THE SEED FIELD IS A LAWFUL SEED, NOT "NO SEED".
        //
        // Measured 15 August on two items in `_Relo`'s stash: both have seed 0 in the save, and
        // the game shows EVERY number of theirs at the bracket's low end — 248 of [248-372],
        // 27 of [27-39], 12 of [12-18] and so on, without a single exception. That is how it
        // must be: in Park–Miller zero maps to itself, so every word of the stream is zero, and
        // the roll `low + (x mod size)` with x = 0 gives exactly the low end. We, meanwhile,
        // treated zero as "nothing to compute with" and showed the bracket's midpoint — 310
        // instead of 248 on the doublet. There are 25 such items across the saves, and not one
        // is worn.
        //
        // `hasSeed = false` remains for items FROM THE CATALOG: there is no instance there at
        // all, nothing to roll, and the record's number must be shown with its bracket.
        public static ItemView Build(GameData game, Item item, bool showRawFields, CharacterContext who,
                                     bool hasSeed)
        {
            ItemView v = new ItemView();
            v.HasSeed = hasSeed;
            v.Source = item;
            v.X = (int)item.XRaw;
            v.Y = (int)item.YRaw;
            v.Stack = item.StackCount;

            DbRecord rec = game == null ? null : game.Record(item.BaseName);
            if (rec == null)
            {
                v.Name = item.BaseName;
                v.Missing = Lang.T(game == null ? "item.missing.noGame" : "item.missing.noRecord");
                v.Details.Add(new TipLine(v.Name, TipKind.Name));
                v.Details.Add(new TipLine(v.Missing, TipKind.Warning));
                return v;
            }

            v.Name = FullName(game, item, rec);
            v.ItemClass = rec.Class ?? "";
            v.BaseClassification = rec.Get("itemClassification") ?? "";
            v.Classification = EffectiveClassification(game, item, v.BaseClassification, out v.RareAffixCount);
            v.LevelRequirement = rec.GetInt("levelRequirement");
            v.Needs = ItemRequirements.For(game, rec);

            v.Icon = game.Icon(IconField(rec));
            if (v.Icon != null)
            {
                v.CellsWide = Math.Max(1, v.Icon.Width / GameData.CellPixels);
                v.CellsHigh = Math.Max(1, v.Icon.Height / GameData.CellPixels);
                v.SizeKnown = true;
            }

            BuildDetails(game, item, rec, v, showRawFields, who, hasSeed);
            return v;
        }

        // "records/skills/playerclass07/purifyingflame4.dbr" -> "playerclass07".
        // The mastery a skill belongs to is not a field anywhere: it is the
        // folder the skill record sits in.
        public static string MasteryFolder(string skillDbr)
        {
            if (string.IsNullOrEmpty(skillDbr)) return null;
            int i = skillDbr.IndexOf("playerclass", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int j = i;
            while (j < skillDbr.Length && skillDbr[j] != '/') j++;
            return skillDbr.Substring(i, j - i);
        }

        // What the open character brings to an item description: their masteries
        // and the numbers requirements are checked against.
        public class CharacterContext
        {
            public HashSet<string> Masteries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public ItemRequirements.Have Have = new ItemRequirements.Have();
            // Records the character is wearing, so the pieces of a set they do
            // not have on can be dimmed.
            public HashSet<string> WornRecords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // THE CHARACTER'S MULTIPLIERS PER DAMAGE FIELD — for showing a SKILL's numbers in
            // the tooltip (MECHANICS 22k). Empty means "we do not know", and then the record's
            // number is printed, as it always was.
            public Dictionary<string, double> SkillScale =
                new Dictionary<string, double>(StringComparer.Ordinal);

            // A skill's mastery is the folder its record lives in, so the set is
            // just the folders of every skill with points in it.
            public static CharacterContext Of(Character c) { return Of(null, c); }

            // With the game data on top — then the display multipliers are filled too. Split
            // into two signatures on purpose: the multipliers are needed only by the tooltip,
            // while five other places call `Of` and have no use for an extra full calculation.
            public static CharacterContext Of(GameData game, Character c)
            {
                CharacterContext ctx = new CharacterContext();
                if (c == null) return ctx;
                if (game != null) FillScale(game, c, ctx);
                ctx.Have = ItemRequirements.Have.Of(c);
                foreach (Skill s in c.Skills)
                {
                    if (s.Level <= 0) continue;
                    string folder = MasteryFolder(s.Name);
                    if (folder != null) ctx.Masteries.Add(folder);
                }
                List<Item> worn = new List<Item>();
            // THE SPARE WEAPON SET IS NOT INCLUDED HERE — MECHANICS 14d8. The game counts only
            // the active set; an item in the second set gives nothing until it is switched to.
            // Found 29 August on `_222` and cleaned out of every place at once: two of them had
            // already cost errors in numbers (the bonus sum and skill levels), the rest were
            // cleaned along with them so they would not have to be hunted separately.
                worn.AddRange(c.Equipment); worn.AddRange(c.Weapons1);
                foreach (Item it in worn)
                    if (!string.IsNullOrEmpty(it.BaseName)) ctx.WornRecords.Add(it.BaseName);
                return ctx;
            }

            // One full calculation of the character WITHOUT A SKILL — the multipliers come from
            // it: with no skill `Skl()` gives zero, and exactly the character's multiplier
            // remains (see DamageModel.TypeMultiplier).
            static void FillScale(GameData game, Character c, CharacterContext ctx)
            {
                try
                {
                    CharacterTotals totals = CharacterTotals.Of(game, c);
                    DamageModel dm = new DamageModel(game, c, totals);
                    foreach (string type in new[] { "Physical", "Pierce", "Fire", "Cold", "Lightning",
                                                    "Poison", "Life", "Aether", "Chaos", "Bleeding" })
                    {
                        double m = dm.TypeMultiplier(type, false);
                        if (Math.Abs(m - 1) > 0.0001)
                        {
                            ctx.SkillScale["offensive" + type + "Min"] = m;
                            ctx.SkillScale["offensive" + type + "Max"] = m;
                        }
                        double md = dm.TypeMultiplier(type, true);
                        if (Math.Abs(md - 1) > 0.0001)
                        {
                            ctx.SkillScale["offensiveSlow" + type + "Min"] = md;
                            ctx.SkillScale["offensiveSlow" + type + "Max"] = md;
                        }
                    }
                }
                catch (Exception)
                {
                    // The display must not fall over because of a calculation: without the
                    // multipliers the tooltip simply stays as it was before 1 September.
                    ctx.SkillScale.Clear();
                }
            }
        }

        // The game names masteries by number: playerclass07 -> tagSkillClassName07
        // -> "Inquisitor". Combined class names use both numbers, which is where
        // "tagSkillClassName0106" = Warder comes from.
        // A mastery's name. THE NINTH NAME IN THE GAME'S RUSSIAN TEXT IS LITERALLY "?", and
        // because of that the skills tab used to show `playerclass09` instead of the mastery's
        // name. A fallback was already written — in MasteryEdit.Name — but it was not called
        // from here, and only one place out of four had been fixed. Now there is one shared
        // fallback.
        public static string MasteryName(GameData game, string folder)
        {
            if (game == null || folder == null || folder.Length < 12) return null;
            string digits = folder.Substring(11);
            int number;
            if (!int.TryParse(digits, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out number))
                return null;
            string name = MasteryEdit.Name(game, number);
            // MasteryEdit.Name never returns empty: as a last resort it gives "mastery N". Such
            // a name is worse than the record path, so let the caller show the folder, as
            // before. The comparison uses the same text from the language file that
            // `MasteryEdit.Name` takes it from (until 16 September a Russian string stood here
            // literally, and in another language the fallback name would have passed for a real
            // one).
            return string.IsNullOrEmpty(name) || name == Lang.T("skills.mastery.fallbackName", number) ? null : name;
        }

        // The game builds a name as prefix + quality + style + noun + suffix, and
        // in inflected languages the adjectives have to agree with the noun's
        // gender, which the noun's own tag carries.
        // The gender of the item's own noun, which every adjective around it has
        // to agree with — the quality, the style, the affixes and the rarity.
        public static string GenderOfItem(GameData game, DbRecord rec)
        {
            string nameTag = rec.Get("itemNameTag");
            if (string.IsNullOrEmpty(nameTag)) nameTag = rec.Get("description");
            return GameData.GenderOf(game == null ? null : game.Tag(nameTag));
        }

        public static string FullName(GameData game, Item item, DbRecord rec)
        {
            string nameTag = rec.Get("itemNameTag");
            if (string.IsNullOrEmpty(nameTag)) nameTag = rec.Get("description");
            string nounRaw = game.Tag(nameTag);
            string gender = GameData.GenderOf(nounRaw);
            string noun = string.IsNullOrEmpty(nounRaw) ? ShortPath(rec.Path) : GameData.Form(nounRaw, gender);

            List<string> parts = new List<string>();
            AddAffix(game, parts, item.PrefixName, gender);

            string quality = GameData.Form(game.Tag(rec.Get("itemQualityTag")), gender);
            string styleRaw = game.Tag(rec.Get("itemStyleTag"));
            // Awakened items carry no style tag; the game infers it from the path.
            if (string.IsNullOrEmpty(styleRaw) && rec.Path.IndexOf("/awakened/", StringComparison.OrdinalIgnoreCase) >= 0)
                styleRaw = game.Tag("tagStyleUniqueAwakened");
            string style = GameData.Form(styleRaw, gender);

            if (!string.IsNullOrEmpty(quality)) parts.Add(quality);
            if (!string.IsNullOrEmpty(style)) parts.Add(style);
            parts.Add(noun);
            AddAffix(game, parts, item.SuffixName, gender);

            return Capitalize(string.Join(" ", parts.ToArray()));
        }

        static void AddAffix(GameData game, List<string> parts, string dbr, string gender)
        {
            if (string.IsNullOrEmpty(dbr)) return;
            DbRecord r = game.Record(dbr);
            string raw = r == null ? null : game.Tag(r.Get("lootRandomizerName"));
            string n = GameData.Form(raw, gender);
            if (!string.IsNullOrEmpty(n)) parts.Add(n);
        }

        static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0], CultureInfo.CurrentCulture) + s.Substring(1);
        }

        // A word from the game's text with its grammatical marker removed, so a
        // noun like "[fs]component" reads as "component".
        public static string Word(GameData game, string key, string fallback)
        {
            if (game == null) return fallback;
            string raw = game.Word(key, null);
            if (string.IsNullOrEmpty(raw)) return fallback;
            string form = GameData.Form(raw, GameData.GenderOf(raw));
            return string.IsNullOrEmpty(form) ? fallback : Capitalize(form);
        }

        static string ShortPath(string p)
        {
            int i = p.LastIndexOf('/');
            return i < 0 ? p : p.Substring(i + 1);
        }

        // Every field the database uses for an inventory icon, in the order the
        // game itself prefers them. The 3D model textures (baseTexture, bumpTexture
        // and friends) are deliberately absent: they are not inventory pictures
        // and their size says nothing about the item's footprint.
        public static readonly string[] IconFields = {
            "bitmap",                    // gear, consumables, most things
            "relicBitmap",               // a completed component
            "artifactBitmap",            // relics
            "artifactFormulaBitmapName", // blueprints
            "noteBitmap",                // lore notes
            "fullBitmap",                // transmuters
            "shardBitmap",               // an incomplete component
            "bitmapFemale"
        };

        public static string IconField(DbRecord rec) { return FirstOf(rec, IconFields); }

        static string FirstOf(DbRecord rec, params string[] fields)
        {
            foreach (string f in fields)
            {
                string v = rec.Get(f);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        // An item reads as the best rarity among its own record and its affixes:
        // a plain base with a rare prefix and a rare suffix shows up green, the
        // same as a monster infrequent whose own record is already Rare. The
        // diamonds the game draws before the name count the rare affixes.
        static int Rank(string classification)
        {
            switch (classification)
            {
                case "Magical": return 1;
                case "Rare": return 2;
                case "Epic": return 3;
                case "Legendary": return 4;
                default: return 0;
            }
        }

        public static string EffectiveClassification(GameData game, Item item, string baseClass, out int rareAffixes)
        {
            rareAffixes = 0;
            if (baseClass == "Quest") return baseClass;
            string best = baseClass;
            string[] affixes = { item.PrefixName, item.SuffixName };
            foreach (string dbr in affixes)
            {
                if (string.IsNullOrEmpty(dbr)) continue;
                DbRecord r = game.Record(dbr);
                string c = r == null ? null : r.Get("itemClassification");
                if (string.IsNullOrEmpty(c)) continue;
                if (c == "Rare") rareAffixes++;
                if (Rank(c) > Rank(best)) best = c;
            }
            return best;
        }

        // THE MARK BEFORE THE NAME IS THE GAME'S OWN, AND THERE ARE TWO DIFFERENT ONES.
        //
        // The user's screenshots of 24 September, nine items:
        //   a monster infrequent (whose RECORD is already `Rare`) gets a framed gem, and it is
        //   always there: with no rare affixes at all, with one, and with two;
        //   an ordinary item with two rare affixes gets TWO diamonds.
        // So the gem speaks about the ITEM while the diamonds speak about rare affixes on an
        // ordinary base. We used to place diamonds by the number of rare affixes always, and on
        // monster infrequents showed two diamonds where the game shows a gem.
        //
        // The game's gem is a picture, ours is text: we take "◈", and the diamonds stay "◆".
        public static string Marks(string baseClassification, int rareAffixes)
        {
            if (string.Equals(baseClassification, "Rare", StringComparison.OrdinalIgnoreCase)) return "◈ ";
            return Diamonds(rareAffixes);
        }

        public static string Diamonds(int count)
        {
            return count <= 0 ? "" : new string('◆', Math.Min(count, 2)) + " ";
        }

        // The rarity is an adjective, so it carries every gender form and has to
        // agree with the item's noun like any other adjective in the name.
        public static string RarityName(GameData game, string classification, string gender)
        {
            return GameData.Form(RarityName(game, classification), gender);
        }

        public static string RarityName(GameData game, string classification)
        {
            // The game spells the rarities out, but under several different tag
            // names, so each one is tried in turn before the Russian fallback.
            string fallback;
            string[] keys;
            switch (classification)
            {
                case "Common": fallback = Lang.T("item.rarity.common"); keys = new[] { "tagCommon", "tagCharHeaderCommon" }; break;
                case "Magical": fallback = Lang.T("item.rarity.magical"); keys = new[] { "tagCharHeaderMagical", "tagMagical" }; break;
                case "Rare": fallback = Lang.T("item.rarity.rare"); keys = new[] { "tagRare", "tagLootFilter03" }; break;
                case "Epic": fallback = Lang.T("item.rarity.epic"); keys = new[] { "tagEpic", "tagLootFilter05" }; break;
                case "Legendary": fallback = Lang.T("item.rarity.legendary"); keys = new[] { "tagLegend", "tagLootFilter06" }; break;
                case "Quest": fallback = Lang.T("item.rarity.quest"); keys = new[] { "tagQuest" }; break;
                default: return classification;
            }
            if (game == null) return fallback;
            foreach (string k in keys)
            {
                string v = game.Word(k, null);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return fallback;
        }

        // ------------------------------------------------------------ details

        static void BuildDetails(GameData game, Item item, DbRecord rec, ItemView v, bool raw,
                                 CharacterContext who, bool hasSeed)
        {
            List<TipLine> t = v.Details;
            t.Add(new TipLine(Marks(v.BaseClassification, v.RareAffixCount) + v.Name +
                              (v.Stack > 1 ? "  (" + v.Stack + ")" : ""), TipKind.Name));

            string gender = GenderOfItem(game, rec);
            // TWO RARE AFFIXES HAVE A WORD OF THEIR OWN IN THE GAME, `tagDoubleRare`. Noticed
            // in the user's screenshots of 24 September: both an ordinary base and a monster
            // infrequent are captioned that way — so the word speaks about the AFFIXES, not
            // about the base.
            string kind = v.RareAffixCount >= 2 && game != null
                        ? GameData.Form(game.Word("tagDoubleRare", null) ?? "", gender)
                        : "";
            if (string.IsNullOrEmpty(kind)) kind = RarityName(game, v.Classification, gender);
            if (v.Classification != v.BaseClassification && !string.IsNullOrEmpty(v.BaseClassification))
                kind += "  (" + RarityName(game, v.BaseClassification, gender) + ")";
            if (!string.IsNullOrEmpty(kind)) t.Add(new TipLine(kind, TipKind.Subtle));

            double? armour = Num(rec, "defensiveProtection");
            if (armour.HasValue && armour.Value > 0)
                t.Add(new TipLine(Lang.T("item.head.armour", Fmt(armour.Value)), TipKind.Headline));

            // "N attacks per second" — the same line the game prints on a weapon, and in the
            // same place: right below armour and base damage. `DamageModel.WeaponTipAPS`
            // computes it, and both measurements are explained there. The character's percents
            // are NOT in it — this is the weapon's own speed.
            double? aps = DamageModel.WeaponTipAPS(rec);
            if (aps.HasValue)
                t.Add(new TipLine(Lang.T("item.head.aps",
                                  aps.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)),
                                  TipKind.Headline));

            t.Add(new TipLine());
            // Only an item instance rolls: a skill or a set bonus is the same for
            // everyone, so the range belongs here and nowhere else.
            rolls = true;
            rollScale = rec == null ? 0 : rec.GetInt("attributeScalePercent");
            // The WHOLE item has a single scale, taken from the base record — the same way
            // --seeddump takes it. An affix or a component has no scale of its own.
            // The item is computed AS A WHOLE: base and affixes draw rolls from one stream.
            wholeItem = hasSeed ? ItemOrder.RollWhole(game, item, rec) : null;
            // Where exactly the order is a guess is computed once per item. The question mark
            // goes only on those lines, not on every line.
            rolledDoubt = new HashSet<string>(ItemOrder.Doubts(wholeItem), StringComparer.Ordinal);
            RolledPart(rec, 0);
            AppendStats(game, rec, t, 0);
            rolls = false;
            rolled = null;
            AppendSkills(game, rec, t, 0, who);
            // The pet block comes straight after the item's own skills, as the game prints it.
            AppendPetBonus(game, item, rec, t);

            List<TipLine> requirements = new List<TipLine>();
            // Only the level: the attribute requirements the game shows are not
            // in the data, and our reading of the equations that produce them is
            // not trustworthy yet — see ItemRequirements.cs.
            ItemRequirements.Have have = who == null ? null : who.Have;
            Requirement(requirements, Word(game, "LevelRequirement", Lang.T("item.need.level")),
                        v.Needs.Level, have == null ? -1 : have.Level);
            // AN ATTRIBUTE REQUIREMENT IS SHOWN ONLY WHEN THE RECORD STATES IT OUTRIGHT, not
            // when it is derived from the equations. The caveat above ("our reading of the
            // equations is not trustworthy yet") still stands and concerns the equations alone;
            // an explicit number raises no doubt.
            //
            // Such records are rare but they exist: `quest_areah_woodcarving_02.dbr` holds
            // `strengthRequirement 800`. `--saidtest` found this on 1 September, as soon as a
            // new character brought the item into `saves/` — before that not one of our items
            // carried an explicit requirement.
            Requirement(requirements, Word(game, "Strength", Lang.T("item.need.strength")),
                        rec.GetInt("strengthRequirement"), have == null ? -1 : -1,
                        "strengthRequirement");
            Requirement(requirements, Word(game, "Dexterity", Lang.T("item.need.dexterity")),
                        rec.GetInt("dexterityRequirement"), have == null ? -1 : -1,
                        "dexterityRequirement");
            Requirement(requirements, Word(game, "Intelligence", Lang.T("item.need.intelligence")),
                        rec.GetInt("intelligenceRequirement"), have == null ? -1 : -1,
                        "intelligenceRequirement");

            // These labels are nouns in the game's own text and carry their
            // gender marker, which has to be stripped before they are shown.
            AppendSet(game, rec, t, who);

            // A prefix and a suffix share the item's seed, but each part's stream starts from it
            // AFRESH — measured on the pet bonus (MECHANICS 14a) and written the same way in GD
            // Stash's `GDItem.applyRNG`: a new generator per list of fields.
            // IT DOES NOT MATCH, AND THAT IS MEASURED. A rare item's tooltip (`_Amakata`, bag 1,
            // item 16) was shot on 13 August: not one number agreed — not in the prefix (acid
            // 24–36 against the game's 26–38), not in the suffix (attack speed 15 against 14),
            // NOR IN THE BASE ITSELF (cast speed 7 against 6, and that field belongs to the base
            // record alone). Every one of the game's numbers lies inside our brackets, so both
            // the roll and the bracket are right and the harness is wrong — it looks as though
            // the presence of affixes changes the traversal of THE BASE itself. The analysis and
            // what to do next are in HANDOFF, "AFFIXES DO NOT MATCH".
            // We do not stop showing the rolled numbers on an item with affixes: without them
            // the discrepancy cannot be seen, and a "?" already stands beside each of them.
            AppendAttachment(game, t, item.PrefixName, Lang.T("item.part.prefix"), who, 1);
            AppendAttachment(game, t, item.SuffixName, Lang.T("item.part.suffix"), who, 2);
            // …AND THE MODIFIER — the awakened crafting affix. Its block was missing entirely:
            // there simply was no call for `ModifierName` here, and the bonus vanished from the
            // display altogether (on an awakened ring from `records/items/awakened/` the game
            // prints "+9 defensive ability [9-15]" and we had no such line).
            //
            // Its part number is THREE, the same as in `ItemOrder.WholePlan`: its rolled number
            // is taken from the item's COMMON stream, not from one of its own (analysis is
            // there too). So we pass no separate seed — otherwise the number would be computed
            // from the wrong stream and diverge from the game.
            //
            // The heading is ours: the game prints no such line, it writes the bonus straight
            // into the item's list. We cannot yet place it into the common list — the lines are
            // assembled part by part — but the number is already right, and the heading shows
            // where it comes from.
            AppendAttachment(game, t, item.ModifierName, Lang.T("item.part.modifier"), who, 3);
            // A COMPONENT, ITS COMPLETION BONUS, AN AUGMENT AND AN ASCENSION ARE NOT ROLLED AT
            // ALL: their bonuses are the same on every instance and sit in the record as those
            // very numbers (written down when the `_Amakata` screenshots were worked through on
            // 6 August, and never refuted since). So seed 0 goes here — "nothing to compute" —
            // and no rolled value is printed for them.
            AppendAttachment(game, t, item.RelicName, Word(game, "tagCraftingComponent", Lang.T("item.part.component")), who, -1);
            // The completion bonus of that component: the game rolls one out of a
            // list when the component is finished and shows it on the tooltip. The
            // save has always held it in RelicBonus and nothing read it, so it was
            // missing from the description and every total: relic completion bonuses
            // can grant skill levels and must be included.
            AppendAttachment(game, t, RelicBonusOf(game, item), Lang.T("item.part.relicBonus"), who, -1, item.Seed);
            AppendAttachment(game, t, item.AugmentName, Word(game, "tagEnchantmentName", Lang.T("item.part.augment")), who, -1);
            // The ascension: the game prints it under its own heading at the very bottom of the
            // tooltip, after the granted skills.
            AppendAttachment(game, t, item.AscensionName, AscensionTitle, who, -1);

            // WHAT THIS PART GOES INTO — IN THE GAME'S OWN WORDS.
            //
            // The user's request of 2 October: "it would be good to add which part of an item
            // this component/augment can be applied to". Nothing had to be invented for it: the
            // game writes it in the part's own `itemText`, as the last piece of that same
            // string — e.g. `tagCompB018Desc` ends with "(Used in amulets and medals)" and
            // `tagCompA001Desc` with "(Used in all armor)".
            //
            // That is, in all twelve languages at once and in exactly the words the player sees
            // in game. Our own filtering (`ItemParts`) goes by the record's flags, not by this
            // string — the string is only displayed.
            if (rec != null && ItemParts.KindOf(rec.Class) != ItemParts.Part.None)
            {
                string said = game.Tag(rec.Get("itemText"));
                if (!string.IsNullOrEmpty(said))
                {
                    t.Add(new TipLine());
                    t.Add(new TipLine(said, TipKind.Subtle));
                }
            }

            if (requirements.Count > 0)
            {
                t.Add(new TipLine());
                t.AddRange(requirements);
            }

            t.Add(new TipLine());
            // THE NOTE ABOUT ROLLED VALUES REMAINS ONLY WHERE IT ADDS SOMETHING.
            //
            // There used to be two lines: one said the numbers are what THIS item rolled rather
            // than the bracket's midpoint, the other also warned about the lines with a red
            // mark. The user asked for the first to go on 2 October — "that is obvious anyway" —
            // and it stood on EVERY item with a roll. The second stayed: it speaks of a MEASURED
            // inaccuracy (the field's place in the traversal order is unknown, so the number may
            // diverge from the game by one step), and nothing in the tooltip implies that. A
            // caveat without a condition does not go out by itself — rule 25.
            if (hasSeed && rec != null)
            {
                bool doubt = false;
                foreach (TipLine one in t) if (one.Doubt) { doubt = true; break; }
                if (doubt)
                {
                    t.Add(new TipLine(Lang.T("item.rolled.noteDoubt"), TipKind.Subtle));
                    t.Add(new TipLine());
                }
            }
            // WE DO NOT SHOW THE SEED TO THE USER (16 August): there is no need to know it, and
            // the edit window speaks to them in stat numbers rather than in the game's
            // internals. In the debug dumps (`--wholedump`, `--ordertrust`) the seed remains.
            t.Add(new TipLine(Lang.T("item.cells", v.CellsWide + "x" + v.CellsHigh), TipKind.Subtle));
            // RECORD PATHS ONLY UNDER THE "all record fields" TICK (16 September, the user's
            // request: "the paths to the game's files are visible — better hide them"; and
            // earlier, "do not show the user mentions of the game's files"). They are still
            // there for analysis: turn the tick on in the settings.
            if (raw)
            {
                t.Add(new TipLine(rec.Path, TipKind.Subtle));
                // The parts' records go under the item itself. Without them there is no finding
                // WHAT exactly to compute on a composite item: the affix's name is in the
                // tooltip, while its record path had to be dug out of the save by hand.
                string[,] parts = { { item.PrefixName, Lang.T("item.part.prefix") }, { item.SuffixName, Lang.T("item.part.suffix") },
                                    { item.RelicName, Lang.T("item.part.component") }, { item.AugmentName, Lang.T("item.part.augment") },
                                    { item.ModifierName, Lang.T("item.part.modifier") }, { item.AscensionName, Lang.T("item.part.ascension") } };
                for (int i = 0; i < parts.GetLength(0); i++)
                    if (!string.IsNullOrEmpty(parts[i, 0]))
                        t.Add(new TipLine(parts[i, 1] + ": " + parts[i, 0], TipKind.Subtle));
            }

            if (raw)
            {
                t.Add(new TipLine());
                t.Add(new TipLine(Lang.T("item.allFields"), TipKind.Subtle));
                List<string> keys = new List<string>(rec.Fields.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string k in keys)
                {
                    string val = rec.Fields[k];
                    if (val == "0" || val == "" || val == "0.0") continue;
                    t.Add(new TipLine(k + " = " + val, TipKind.Subtle, 1));
                }
            }
        }

        // Openings for the skills window, which describes skill records with the
        // same machinery: a skill stores its numbers per level exactly the way a
        // set stores them per piece count.
        public static void RenderStats(GameData game, DbRecord rec, List<TipLine> lines, int indent, int slot)
        {
            AppendTable(game, rec, lines, indent, SkillFacts, slot);
            AppendRestore(game, rec, lines, indent, slot, "Life", Lang.T("item.restore.life"));
            AppendRestore(game, rec, lines, indent, slot, "Mana", Lang.T("item.restore.mana"));
            AppendTable(game, rec, lines, indent, SkillArmour, slot);
            AppendStats(game, rec, lines, indent, slot);
        }

        // A recovery line may contain both a percentage and a flat amount, e.g. "10% + 848 health".
        // the game says in one line when a skill carries both, and in a shorter
        // line when it carries one. Three of its own templates, so this cannot go
        // in the table.
        static void AppendRestore(GameData game, DbRecord rec, List<TipLine> lines, int indent,
                                  int slot, string which, string what)
        {
            double percent = Num(rec, "skill" + which + "Percent", slot) ?? 0;
            double flat = Num(rec, "skill" + which + "Bonus", slot) ?? 0;
            if (percent <= 0 && flat <= 0) return;

            string tag = percent > 0 && flat > 0 ? "tagBonus" + which + "PercentAbs"
                       : percent > 0 ? "tagBonus" + which + "Percent"
                       : "tagBonus" + which + "Abs";
            string text = game == null ? null
                : game.FormatLocalTag(tag, percent > 0 ? percent : flat, flat);
            if (text == null)
            {
                text = Lang.T("item.restore.head") + " " +
                       (percent > 0 ? Fmt(percent) + "%" : "") +
                       (percent > 0 && flat > 0 ? " + " : "") +
                       (flat > 0 ? Fmt(flat) : "") + " " + what;
            }
            // Which fields this line explains — for `--saidtest`. Without the marker
            // `skillLifePercent` counted as "unspoken" on eleven items although it had been
            // printed all along: the line exists but has no way to say so about itself.
            TipLine made = new TipLine(text, TipKind.Bonus, indent);
            made.Explains = new string[] { "skill" + which + "Percent", "skill" + which + "Bonus" };
            lines.Add(made);
        }

        // The completion bonus of a relic, or null when there is none.
        //
        // Every item in the save carries a RelicBonus field, and only a relic ever
        // means anything by it: in Grim Dawn a relic is the one thing with a
        // completion bonus, and an ordinary item with a component socketed has
        // none. The field is filled on those items too, and taking it at face
        // value adds bonuses the game does not give.
        //
        // Both halves are checked against the game. A relic completion bonus can grant
        // skill levels, and RelicBonus records can grant physique and spirit; counting
        // those
        // pushed the attributes to 1038 / 489 against the game's 1015 / 470, which
        // both come out exact when they are left alone.
        public static string RelicBonusOf(GameData game, Item item)
        {
            if (game == null || item == null || string.IsNullOrEmpty(item.RelicBonus)) return null;
            DbRecord own = string.IsNullOrEmpty(item.BaseName) ? null : game.Record(item.BaseName);
            if (own == null) return null;
            return (own.Class ?? "").Equals("ItemArtifact", StringComparison.OrdinalIgnoreCase)
                 ? item.RelicBonus : null;
        }

        public static string SkillNameOf(GameData game, string dbr)
        { return game == null || string.IsNullOrEmpty(dbr) ? dbr : SkillName(game, dbr); }

        public static string SkillDescriptionOf(GameData game, string dbr)
        { return SkillDescription(game, dbr); }

        // The record a skill hands off to when the one named holds no numbers.
        public static string NextSkillRecord(DbRecord rec)
        { return FirstOf(rec, "buffSkillName", "petSkillName", "skillBaseName"); }

        // A SKILL'S PET BONUS DOES NOT NECESSARILY SIT IN THE FIRST RECORD OF THE CHAIN.
        // On one skill `petBonusName` stands in the skill itself, while on another the top
        // record is only a toggle (`Skill_BuffRadiusToggled`, holding a single
        // `buffSkillName`) and the whole bonus, including "+100% physical damage to pets",
        // sits in `natureblessing1_buff`. We looked only at the top one — and lost the line
        // entirely. Found from a skills-window shot of `_Izaar` on 18 August. We walk the same
        // route as the skill's name, numbers and limits (LimitsOf, ConditionOf).
        public static DbRecord PetBonusOf(GameData game, string skillDbr, out DbRecord holder)
        {
            holder = null;
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(skillDbr); hop++)
            {
                DbRecord rec = game == null ? null : game.Record(skillDbr);
                if (rec == null) break;
                DbRecord pet = game.Record(rec.Get("petBonusName"));
                if (pet != null) { holder = rec; return pet; }
                skillDbr = NextSkillRecord(rec);
            }
            return null;
        }

        // Every record field the description knows how to say something about.
        // The coverage check compares this against the fields items actually
        // carry, so a bonus the game adds in a patch cannot go missing quietly.
        // A field's caption in words — the same one drawn in the item's tooltip. The stat
        // census (`--itemstats`) needs it: reading bare field names and guessing what they are
        // is a sure way to err, while `offensiveSlowFireMin` next to the word for burn leaves
        // no questions.
        public static string CaptionOf(GameData game, string field)
        {
            foreach (Stat[] table in new Stat[][] { OffenceStats, DefenceStats, SkillFacts, SkillArmour })
                foreach (Stat s in table)
                {
                    if (s.Field != field && s.Pair != field) continue;
                    if (!string.IsNullOrEmpty(s.Label)) return s.Label;
                    if (game != null && !string.IsNullOrEmpty(s.Tag))
                    {
                        string t = game.Tag(s.Tag);
                        if (!string.IsNullOrEmpty(t)) return Clean(t);
                    }
                    return s.Tag;
                }
            return null;
        }

        // The game's strings carry formatting ("{%+.0f0}%") and line breaks.
        static string Clean(string s)
        {
            int brace = s.IndexOf('{');
            while (brace >= 0)
            {
                int end = s.IndexOf('}', brace);
                if (end < 0) break;
                s = s.Remove(brace, end - brace + 1);
                brace = s.IndexOf('{');
            }
            return s.Replace("\n", " ").Replace("  ", " ").Trim();
        }

        public static List<string> KnownFields()
        {
            List<string> all = new List<string>();
            foreach (Stat[] table in new Stat[][] { OffenceStats, DefenceStats, SkillFacts, SkillArmour })
                foreach (Stat s in table)
                {
                    all.Add(s.Field);
                    if (s.Pair != null) all.Add(s.Pair);
                }
            all.AddRange(new string[] {
                "conversionPercentage", "conversionInType", "conversionOutType",
                "conversionPercentage2", "conversionInType2", "conversionOutType2",
                "racialBonusRace", "racialBonusPercentDamage", "racialBonusAbsoluteDamage",
                "racialBonusPercentDefense", "racialBonusAbsoluteDefense",
                "itemSetName", "itemSkillName", "itemSkillLevel", "itemSkillAutoController",
                "augmentAllLevel", "defensiveProtection",
                "skillLifePercent", "skillLifeBonus", "skillManaPercent", "skillManaBonus",
            });
            for (int i = 1; i <= 6; i++)
            {
                all.Add("augmentSkillName" + i); all.Add("augmentSkillLevel" + i);
                all.Add("augmentMasteryName" + i); all.Add("augmentMasteryLevel" + i);
                all.Add("modifiedSkillName" + i); all.Add("modifierSkillName" + i);
            }
            return all;
        }

        // Every bonus of a record as one line of text, in the same words the
        // description shows, so the catalogue can be searched by what an item
        // does rather than only by what it is called.
        public static string SearchableStats(GameData game, DbRecord rec)
        {
            List<TipLine> lines = new List<TipLine>();
            AppendStats(game, rec, lines, 0);
            AppendSkills(game, rec, lines, 0, null);
            // THE PET BONUS AND SKILL MODIFIERS ARE AN ITEM'S LINES TOO, AND MUST BE SEARCHABLE.
            //
            // Found by the user on 3 September while building a pet occultist: filtering the
            // catalog by a pet-related word over leg armour gave ZERO items, while the same
            // unfiltered list held legs with a whole "bonus to all pets" block. The reason is
            // simple: only the record's own lines came here, while the pet block lives in a
            // SEPARATE record (`petBonusName`) and never reached the searched text at all.
            //
            // Skill modifiers were added in the same pass: a modified skill with its own
            // numbers is just as much an item's line, and searching items by a modified skill is
            // just as necessary.
            DbRecord pet = game == null ? null : game.Record(rec.Get("petBonusName"));
            if (pet != null)
            {
                lines.Add(new TipLine(Word(game, "tagPetBonusNameAllPets", Lang.T("item.petBonusAll")),
                                      TipKind.Section));
                AppendStats(game, pet, lines, 0);
                AppendSkills(game, pet, lines, 0, null);
            }
            AppendSkillModifiers(game, rec, lines, 0, null);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (TipLine l in lines)
            {
                if (string.IsNullOrEmpty(l.Text)) continue;
                if (sb.Length > 0) sb.Append("; ");
                foreach (char c in l.Text) sb.Append(c == '\t' || c == '\n' || c == '\r' ? ' ' : c);
            }
            return sb.ToString();
        }

        // A requirement line, red when the open character does not meet it.
        // "have" of -1 means no character is open, so nothing is judged.
        static void Requirement(List<TipLine> lines, string label, int need, int have)
        { Requirement(lines, label, need, have, null); }

        // `field` — which record field this line explains; needed by `--saidtest`, which counts
        // a field as spoken only if some line has named it.
        static void Requirement(List<TipLine> lines, string label, int need, int have, string field)
        {
            if (need <= 0) return;
            bool short_ = have >= 0 && have < need;
            TipLine made = new TipLine(label + ": " + need + (short_ ? "  " + Lang.T("item.need.short", have) : ""),
                                       short_ ? TipKind.Warning : TipKind.Subtle);
            if (field != null) made.Explains = new string[] { field };
            lines.Add(made);
        }

        // The set an item belongs to: its pieces, and what wearing two, three or
        // four of them adds. The set record is part of the game database, so a
        // patch that rebalances a set shows up here without anything to update.
        //
        // Its bonus fields are semicolon lists indexed by how many pieces are
        // The set's own name, for anyone who has the set record in hand.
        public static string SetNameOf(GameData game, DbRecord set)
        {
            if (game == null || set == null) return null;
            string name = game.Tag(set.Get("setName"));
            return string.IsNullOrEmpty(name) ? null : GameData.Form(name, null);
        }

        // worn, which is why the whole stat table can be read at an offset.
        static void AppendSet(GameData game, DbRecord itemRec, List<TipLine> t, CharacterContext who)
        {
            if (game == null) return;
            DbRecord set = game.Record(itemRec.Get("itemSetName"));
            if (set == null) return;

            string[] members = (set.Get("setMembers") ?? "").Split(';');
            string name = game.Tag(set.Get("setName"));
            t.Add(new TipLine());
            t.Add(new TipLine(string.IsNullOrEmpty(name) ? Lang.T("item.set") : Lang.T("item.setNamed", name),
                              TipKind.Section));

            foreach (string member in members)
            {
                if (string.IsNullOrEmpty(member)) continue;
                DbRecord m = game.Record(member);
                string label = m == null ? ShortPath(member) : Capitalize(FullNameOfRecord(game, m));
                TipLine line = new TipLine(label, TipKind.Subtle, 1);
                // Pieces the character is not wearing are dimmed, as in game.
                if (who != null && !who.WornRecords.Contains(member))
                    line.Override = Color.FromArgb(110, 108, 102);
                t.Add(line);
            }

            // How many pieces of this set the character has on. A tier above that
            // is not in effect, and showing its bonuses in the same colour as the
            // ones that are reads as "you have this" when you do not.
            int have = 0;
            foreach (string member in members)
                if (!string.IsNullOrEmpty(member) && who != null && who.WornRecords.Contains(member)) have++;

            // Piece counts start at two: one piece is the item itself. A bonus
            // repeats itself at every higher count ("0;24;24;24"), so each tier
            // shows only what it adds over the one below, the way the game does.
            for (int worn = 2; worn <= members.Length; worn++)
            {
                List<TipLine> previous = new List<TipLine>();
                AppendStats(game, set, previous, 2, worn - 2);
                AppendSkills(game, set, previous, 2, who, worn - 2);
                HashSet<string> already = new HashSet<string>(StringComparer.Ordinal);
                foreach (TipLine l in previous) already.Add(l.Text);

                List<TipLine> bonuses = new List<TipLine>();
                AppendStats(game, set, bonuses, 2, worn - 1);
                AppendSkills(game, set, bonuses, 2, who, worn - 1);
                bonuses.RemoveAll(delegate (TipLine l) { return already.Contains(l.Text); });
                // THE SET'S OWN SKILL MODIFIERS. Nobody showed them, and that is obvious the
                // moment the game's tooltip is laid beside ours: for one set the game prints
                // four modifier blocks under it, while under our "(4) Set" there stood only
                // the "+3 to skill" lines. Noticed by the user on 1 September.
                //
                // They had long been in the calculation (`ItemSkillMods.SetMods`, 24 August) —
                // so the display fell behind the calculation, not the other way round. The
                // field that decides the tier is THE SAME one the calculation uses
                // (`itemSkillModifierControl`), so the two cannot diverge: on `itemset_d306`
                // it is `0;0;0;1`, and the blocks line up under "(4)".
                if (ItemSkillMods.ModifiersOnAt(set, worn) &&
                    !ItemSkillMods.ModifiersOnAt(set, worn - 1))
                    AppendSkillModifiers(game, set, bonuses, 2, who);
                if (bonuses.Count == 0) continue;

                bool active = who != null && have >= worn;
                TipLine head = new TipLine(Lang.T("item.setWorn", worn), TipKind.Section, 1);
                if (!active) head.Override = Color.FromArgb(110, 108, 102);
                t.Add(head);
                foreach (TipLine l in bonuses)
                {
                    if (!active) l.Override = Color.FromArgb(110, 108, 102);
                    t.Add(l);
                }
            }
        }

        // The item's assembled name without needing a saved instance, for naming
        // the pieces of a set.
        static string FullNameOfRecord(GameData game, DbRecord rec)
        {
            string nameTag = rec.Get("itemNameTag");
            if (string.IsNullOrEmpty(nameTag)) nameTag = rec.Get("description");
            string raw = game.Tag(nameTag);
            string gender = GameData.GenderOf(raw);
            List<string> parts = new List<string>();
            string quality = GameData.Form(game.Tag(rec.Get("itemQualityTag")), gender);
            string style = GameData.Form(game.Tag(rec.Get("itemStyleTag")), gender);
            if (!string.IsNullOrEmpty(quality)) parts.Add(quality);
            if (!string.IsNullOrEmpty(style)) parts.Add(style);
            parts.Add(string.IsNullOrEmpty(raw) ? ShortPath(rec.Path) : GameData.Form(raw, gender));
            return string.Join(" ", parts.ToArray());
        }

        // `seed` is the item's seed, and it is given ONLY to a relic's completion bonus: the
        // one attachment that IS ROLLED (found 26 August, MECHANICS 14a). A component, an
        // augment and an ascension pull no roll; zero is passed for them and they are shown as
        // the record's number, as before.
        //
        // That the completion bonus is rolled is visible in the game itself: it prints that
        // line WITH A BRACKET — "+4% defensive ability [2-4]" — while a component's or an
        // augment's line never has a bracket. Its stream is its own, from the item's seed
        // (`ItemOrder.RollAll`), like the pet bonus.
        static void AppendAttachment(GameData game, List<TipLine> t, string dbr, string label,
                                     CharacterContext who, int whichPart)
        { AppendAttachment(game, t, dbr, label, who, whichPart, 0); }

        static void AppendAttachment(GameData game, List<TipLine> t, string dbr, string label,
                                     CharacterContext who, int whichPart, uint seed)
        {
            if (string.IsNullOrEmpty(dbr)) return;
            DbRecord r = game.Record(dbr);
            t.Add(new TipLine());
            if (r == null) { t.Add(new TipLine(label + ": " + ShortPath(dbr), TipKind.Section)); return; }
            string raw = game.Tag(r.Get("lootRandomizerName"));
            if (string.IsNullOrEmpty(raw)) raw = game.Tag(r.Get("itemNameTag"));
            if (string.IsNullOrEmpty(raw)) raw = game.Tag(r.Get("description"));
            string name = Capitalize(GameData.Form(raw, GameData.GenderOf(raw)));
            // A relic's completion bonus has no name of its own — the game shows
            // only what it gives — so the heading stands alone rather than
            // carrying a record path where a name should be.
            bool nameless = string.IsNullOrEmpty(name) &&
                            !string.IsNullOrEmpty(r.Get("lootRandomizerCost"));
            t.Add(new TipLine(nameless ? label
                                       : label + ": " + (string.IsNullOrEmpty(name) ? ShortPath(dbr) : name),
                              TipKind.Section));
            // We do not show the range bracket on parts, as before; the computed rolled number
            // we do show, and it is what all the computing was for. A part's jitter is ITS OWN
            // (`lootRandomizerJitter`), so we set it temporarily and put it back: otherwise it
            // leaks onto the next part and onto the item itself.
            int keepJitter = RollJitter, keepScale = rollScale;
            bool keepRolls = rolls;
            HashSet<string> keepDoubt = rolledDoubt;
            RolledPart(r, whichPart);
            if (seed != 0)
            {
                // On this line we DO show the bracket — the game shows it too.
                rolls = true; rollScale = 0;
                rolled = ItemOrder.RollAll(r, seed, 0);
                rolledDoubt = new HashSet<string>(ItemOrder.Doubts(r), StringComparer.Ordinal);
            }
            AppendStats(game, r, t, 1);
            rolled = null; rolledDoubt = keepDoubt;
            rolls = keepRolls; rollScale = keepScale;
            RollJitter = keepJitter;
            AppendSkills(game, r, t, 1, who);
        }

        // THE BONUS TO ALL PETS — a separate record and a SEPARATE STREAM from the same seed.
        //
        // We learned to compute it on 14 August (`CharacterTotals.AddPet`) but did not show it
        // at all: the editor's tooltip simply had no such block, although the game prints one.
        // Added on 15 August — and as a check along the way, since its numbers are computed by
        // the same traversal order as everything else.
        //
        // Two caveats, both measured:
        //   * its stream is ITS OWN, from the item's seed, not a continuation of the item's
        //     stream — hence ItemOrder.RollAll rather than the common traversal's lines;
        //   * the item's `attributeScalePercent` does NOT apply to it: on one item the scale is
        //     30 while the game prints lightning as [1040-1560], i.e. exactly ±20% of the
        //     record's 1300.
        static void AppendPetBonus(GameData game, Item item, DbRecord rec, List<TipLine> lines)
        {
            if (game == null || rec == null || item == null) return;
            // A PET BONUS CAN SIT NOT ONLY ON THE BASE BUT ON AN AFFIX. The first version
            // looked only at the base record, and on one item of `_Relo`'s stash the block
            // vanished entirely: there it sits in the PREFIX `aa017a_miniondmg_08`. The
            // calculation (`CharacterTotals`) had been walking the parts all along — only the
            // display was missing.
            string[] parts = { item.BaseName, item.PrefixName, item.SuffixName };
            List<DbRecord> pets = new List<DbRecord>();
            List<int> owners = new List<int>();
            for (int p = 0; p < parts.Length; p++)
            {
                if (string.IsNullOrEmpty(parts[p])) continue;
                DbRecord part = p == 0 ? rec : game.Record(parts[p]);
                DbRecord pet = part == null ? null : game.Record(part.Get("petBonusName"));
                if (pet != null) { pets.Add(pet); owners.Add(JitterOf(part)); }
            }
            if (pets.Count == 0) return;
            lines.Add(new TipLine());
            lines.Add(new TipLine(Word(game, "tagPetBonusNameAllPets", Lang.T("item.petBonusAll")), TipKind.Section));

            int keepJitter = RollJitter, keepScale = rollScale;
            Dictionary<string, long> keepRolled = rolled;
            HashSet<string> keepDoubt = rolledDoubt;
            bool keepRolls = rolls;
            rolls = true;
            rollScale = 0;
            for (int i = 0; i < pets.Count; i++)
            {
                // THE JITTER COMES FROM THE RECORD THAT NAMED THE BONUS, not from the bonus
                // itself: it never has a `lootRandomizerJitter` of its own. Measured on the
                // item above — the bonus is named by a prefix with jitter 10, and the game
                // prints "+73% total damage [66-80]", i.e. 73 ± 10%. At 20% it would have been
                // [59-87] and the number would have drifted to 84.
                RollJitter = owners[i];
                // The pets' stream is a separate lane of the search plan, numbered in the same
                // order (SeedSearch.Build): base, prefix, suffix.
                linePart = SeedSearch.PetPart + i;
                rolled = ItemOrder.RollAll(pets[i], item.Seed, 0, owners[i]);
                rolledDoubt = new HashSet<string>(ItemOrder.Doubts(pets[i]), StringComparer.Ordinal);
                AppendStats(game, pets[i], lines, 1);
            }
            rolled = keepRolled; rolledDoubt = keepDoubt;
            RollJitter = keepJitter; rollScale = keepScale; rolls = keepRolls;
        }

        // WHAT THIS FIELD IS CALLED IN HUMAN WORDS — the game's words, not the database's name.
        //
        // The edit window needs it: when the search refuses, telling the user
        // `conversionPercentage` is as good as telling them nothing. The words come from where
        // the tooltip takes them: first the game's template in the chosen language, then our
        // own fallback, and only if neither exists — the field's name itself. That way the line
        // is translated together with the game instead of getting stuck.
        public static string FieldLabel(GameData game, string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (ItemRoll.IsConversion(field))
                return Word(game, "tagDamageConversion", Lang.T("item.conversionField"));
            Stat[][] tables = { OffenceStats, DefenceStats, SkillFacts, SkillArmour };
            foreach (Stat[] table in tables)
                foreach (Stat s in table)
                {
                    if (s.Field != field && s.Pair != field) continue;
                    string label = s.Label;
                    if (game != null && s.Tag != null)
                    {
                        // A template with "{%…}" is a whole line with a number; the words cannot
                        // be dug out of it, so we take it only when it has no placeholder.
                        string template = game.LocalTag(s.Tag);
                        if (!string.IsNullOrEmpty(template) &&
                            !GameData.HasPlaceholder(template))
                            label = template.Trim();
                    }
                    if (!string.IsNullOrEmpty(label)) return label.Trim();
                }
            return field;
        }

        // WHAT A RECORD GIVES — the same lines as in the tooltip but dry: no brackets and no
        // rolled values. Added 16 August for the list of ascended bonuses: they are not rolled
        // at all (measured 8 August), and showing a bracket on them would be a lie. The list
        // keeps no layout of its own — it uses this.
        // THE SAME, BUT WITH THIS INSTANCE'S ROLLED NUMBERS. The completion-bonus window needs
        // it: the bonus is rolled, and showing the RECORD's number in the list promises the
        // user something other than what they will get. The bonus's stream is its own, from the
        // item's seed (`ItemOrder.RollAll`), and the jitter is the record's own.
        public static List<TipLine> DescribeRolled(GameData game, DbRecord rec, uint seed)
        {
            List<TipLine> lines = new List<TipLine>();
            if (rec == null) return lines;
            bool keepRolls = rolls;
            int keepScale = rollScale;
            Dictionary<string, long> keepRolled = rolled;
            HashSet<string> keepDoubt = rolledDoubt;
            int keepJitter = RollJitter;
            rolls = true; rollScale = 0;
            RollJitter = JitterOf(rec);
            rolled = ItemOrder.RollAll(rec, seed, 0);
            rolledDoubt = new HashSet<string>(ItemOrder.Doubts(rec), StringComparer.Ordinal);
            try
            {
                AppendStats(game, rec, lines, 0);
                AppendSkills(game, rec, lines, 0, null);
            }
            finally
            {
                rolls = keepRolls; rolled = keepRolled; rollScale = keepScale;
                rolledDoubt = keepDoubt; RollJitter = keepJitter;
            }
            return lines;
        }

        public static List<TipLine> Describe(GameData game, DbRecord rec)
        {
            List<TipLine> lines = new List<TipLine>();
            if (rec == null) return lines;
            bool keepRolls = rolls;
            int keepScale = rollScale;
            Dictionary<string, long> keepRolled = rolled;
            rolls = false; rolled = null; rollScale = 0;
            try
            {
                AppendStats(game, rec, lines, 0);
                AppendSkills(game, rec, lines, 0, null);
            }
            finally { rolls = keepRolls; rolled = keepRolled; rollScale = keepScale; }
            return lines;
        }

        static void AppendStats(GameData game, DbRecord rec, List<TipLine> lines, int indent) { AppendStats(game, rec, lines, indent, 0); }

        static void AppendStats(GameData game, DbRecord rec, List<TipLine> lines, int indent, int slot)
        {
            // THIS record's lines are assembled separately: the chance group ("25% chance of:")
            // has to be gathered in one place, while the common list already holds the lines of
            // the item's other parts.
            List<TipLine> mine = new List<TipLine>();
            AppendTable(game, rec, mine, indent, OffenceStats, slot);
            AppendConversion(game, rec, mine, indent, "", slot);
            AppendConversion(game, rec, mine, indent, "2", slot);
            AppendRacial(game, rec, mine, indent, slot);
            AppendTable(game, rec, mine, indent, DefenceStats, slot);
            AppendUnknown(game, rec, mine, indent, slot);
            AppendGlobalGroup(game, rec, mine, indent, slot);
            lines.AddRange(mine);
        }

        // "25% CHANCE OF:" — A GROUP OF BONUSES SHARING ONE ROLL.
        //
        // A record may mark some of its bonuses with the flag `<field>Global 1` and put
        // `offensiveGlobalChance` (or `retaliationGlobalChance`) beside them. The game prints
        // such a bundle under ONE HEADER carrying the chance, with the bonuses below it and no
        // chance repeated per line. Until 18 August we printed every bonus as its own line with
        // its own "25% chance of …": the numbers were right, the shape was not, and the flag
        // itself reached no line at all — which is what `--saidtest` caught (15 fields of eight
        // kinds, every flag).
        //
        // THE HEADER IS TAKEN FROM THE GAME, NOT INVENTED. It has four tags:
        //   GlobalChanceOfAllTag         "Chance of:"
        //   GlobalPercentChanceOfAllTag  "{%.1f0}% chance of:"
        //   GlobalChanceOfOneTag         "Chance of one of:"
        //   GlobalPercentChanceOfOneTag  "{%.1f0}% chance of one of:"
        //
        // WE PICK "of:", NOT "of one of:", and that is not a guess. GDStash's format analysis
        // (`ARZDecompress.fillAttribStatDetails`) knows TWO neighbouring markers on a bonus:
        // `<field>Global` and `<field>XOR` — the second meaning "only one of them will fire".
        // NOT ONE item record in the game carries it: a census of all 30723 records under
        // `records/items/**` gives zero occurrences of `XOR` (`--dbrdump records/items/`,
        // 18 August). So "one of" is never printed for items.
        //
        // And that agrees with the only screenshot we have: an amulet of `_Assira` prints
        // "25% CHANCE of: +160% fire, +190% cold, +228% lightning, +162% burn, +226% frostburn,
        // +191% electrocute" (transcript in screen-assira-eq.tsv, 13 August). One header, one
        // chance in it, no six repetitions.
        //
        // The number in the header is substituted in OUR format (`Fmt`): the game's template
        // reads "{%.1f0}%", i.e. with a tenth, while in the screenshots the chance is always
        // printed whole ("10% chance of +1133% lightning damage", `_Izaar`). `FormatTag` passes
        // the string through as is, so the chance arrives there already assembled.
        //
        // OPEN AND UNMEASURED: on one item of `_Amakata`'s bag 3 the game, by a 17 August
        // screenshot, prints neither of the two retaliation-hindrance lines at all — neither
        // grouped nor separately. We do place the header on those too (the fields and tags of
        // both families are built alike), but there is no measurement of the retaliation group
        // itself; one screenshot closes the question.
        static void AppendGlobalGroup(GameData game, DbRecord rec, List<TipLine> lines, int indent, int slot)
        {
            string[,] families = {
                { "offensive",   "offensiveGlobalChance"   },
                { "retaliation", "retaliationGlobalChance" },
            };
            for (int f = 0; f < families.GetLength(0); f++)
            {
                double chance = Num(rec, families[f, 1], slot) ?? 0;
                if (chance < 0.0001) continue;
                List<TipLine> members = new List<TipLine>();
                List<string> flags = new List<string>();
                flags.Add(families[f, 1]);
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    if (!GroupMember(rec, lines[i], families[f, 0], slot, flags)) continue;
                    lines[i].Indent = indent + 1;
                    members.Insert(0, lines[i]);
                    lines.RemoveAt(i);
                }
                if (members.Count == 0) continue;
                // THE GAME DOES NOT PRINT THE RETALIATION GROUP AT ALL — neither header nor
                // lines. Measured 18 August from a screenshot of one item in `_Amakata`'s
                // bag 3: the game's tooltip holds nine lines and neither retaliation hindrance
                // is among them. Meanwhile the neighbouring numbers of the same part agree to
                // the unit (constitution 4, aether 30, freeze 27, cold 56, frostburn 58) — so
                // those fields DO consume words of the stream and must not be dropped from the
                // calculation. We stay silent in the display only; why the game is silent we
                // do not know.
                //
                // The silence is narrow: `retaliationSlowAttackSpeedMin` exists on thirty
                // records while the global-roll flag is on only six, and the measurement speaks
                // of those. On the rest the line prints as usual.
                // The lines are already taken out of the list — it is enough not to put them
                // back. What is silent on purpose and what by omission is answered by `Muted`
                // below, which `--saidtest` asks.
                if (families[f, 0] == "retaliation") continue;
                // "ONE OF" APPLIES WHEN THE GROUP'S FIELDS CARRY `XOR`. ITEMS never have it (a
                // census of all 30723 records under `records/items/**` gives zero occurrences,
                // 18 August), but SKILLS do: `playerclass05/elementalinfusion2` carries
                // `offensiveSlowFireXOR 1`, `…ColdXOR` and `…LightningXOR` with
                // `offensiveGlobalChance 100` and three chances of 33 — and the game prints it
                // as "Chance of one of:", which the user's screenshot shows.
                //
                // This does not affect the CALCULATION: the shares already split one hundred
                // and each field enters by its own. Whether XOR really affects the expectation
                // is an open question (see SkillStatUse: one skill of `_Itaan` does not match).
                bool one = false;
                foreach (string flag in flags)
                    if (flag.EndsWith("Global", StringComparison.Ordinal) &&
                        Math.Abs(Num(rec, flag.Substring(0, flag.Length - 6) + "XOR", slot) ?? 0) > 0.0001)
                        one = true;
                string head = game == null ? null
                            : game.FormatLocalTag(one ? "GlobalPercentChanceOfOneTag"
                                                      : "GlobalPercentChanceOfAllTag", Fmt(chance));
                TipLine header = new TipLine(
                    head ?? Lang.T(one ? "item.chanceOfOne" : "item.chanceOfAll", Fmt(chance)),
                    TipKind.Bonus, indent);
                header.Explains = flags.ToArray();
                lines.Add(header);
                lines.AddRange(members);
            }
        }

        // SILENT ON PURPOSE: the field is in the record and consumes a word of the stream, but
        // the game does not print it. There is one case so far — the RETALIATION chance group
        // (`retaliationGlobalChance` and everything marked by its flag); the measurement is
        // described at `AppendGlobalGroup`. `--saidtest` asks this predicate: without it
        // "deliberately not printed" and "the line was forgotten" are indistinguishable.
        public static bool Muted(DbRecord rec, string field, int slot)
        {
            if (rec == null || field == null) return false;
            if (!field.StartsWith("retaliation", StringComparison.Ordinal)) return false;
            if ((Num(rec, "retaliationGlobalChance", slot) ?? 0) < 0.0001) return false;
            if (field == "retaliationGlobalChance") return true;
            // The flag itself: `retaliationSlowRunSpeedGlobal`.
            if (field.EndsWith("Global", StringComparison.Ordinal))
                return Math.Abs(Num(rec, field, slot) ?? 0) > 0.0001;
            return Math.Abs(Num(rec, GlobalFlagName(field), slot) ?? 0) > 0.0001;
        }

        // Whether the line is built from at least one field marked with the global-roll flag.
        // The flags found are appended to `flags` — they are what the header "says" in place of
        // the fields themselves.
        static bool GroupMember(DbRecord rec, TipLine line, string family, int slot, List<string> flags)
        {
            if (line.Explains == null) return false;
            bool mine = false;
            foreach (string field in line.Explains)
            {
                if (!field.StartsWith(family, StringComparison.Ordinal)) continue;
                string flag = GlobalFlagName(field);
                if (Math.Abs(Num(rec, flag, slot) ?? 0) < 0.0001) continue;
                mine = true;
                if (!flags.Contains(flag)) flags.Add(flag);
            }
            return mine;
        }

        // The global-roll flag's name for a field: `offensiveSlowFireDurationMin` →
        // `offensiveSlowFireGlobal`. The tails are listed longest first, or `ModifierChance`
        // would be eaten as `Chance`.
        static readonly string[] GlobalTails = {
            "ModifierChance", "DurationModifier", "DurationChance", "DurationMin", "DurationMax",
            "Modifier", "Chance", "Min", "Max",
        };

        static string GlobalFlagName(string field)
        {
            foreach (string tail in GlobalTails)
                if (field.EndsWith(tail, StringComparison.Ordinal))
                    return field.Substring(0, field.Length - tail.Length) + "Global";
            return field + "Global";
        }

        // Whether the field goes into the chance group: then no "25% chance of …" of its own is
        // appended to the line — the chance already stands in the header above it.
        static bool InGlobalGroup(DbRecord rec, string field, int slot)
        {
            if (field == null) return false;
            bool off = field.StartsWith("offensive", StringComparison.Ordinal);
            if (!off && !field.StartsWith("retaliation", StringComparison.Ordinal)) return false;
            if ((Num(rec, off ? "offensiveGlobalChance" : "retaliationGlobalChance", slot) ?? 0) < 0.0001)
                return false;
            return Math.Abs(Num(rec, GlobalFlagName(field), slot) ?? 0) > 0.0001;
        }

        // WHAT IS ROLLED BUT REACHED NO TABLE.
        //
        // The user asked on 17 August: why a "white list" of lines, would it not be simpler to
        // show everything except the knowingly useless? The answer is that a table is not
        // permission to show but the WORDS. It holds, per field, the game's tag, its min–max
        // pair, the units and the duration the game prints on that same line. Without them a
        // line cannot be assembled — a human phrase will not come out of the bare name
        // `offensiveSlowManaLeachMin`.
        //
        // But there is truth in the question, and here it is: a field absent from the tables
        // must not be passed over in silence. So now such fields print themselves — raw name
        // and number, under their own marker. Nothing can vanish unnoticed any more: today mana
        // leech and nine other fields vanished that way, and it was noticed only through a
        // screenshot from the game.
        //
        // It is empty on all 822 items across all saves (`--edittest`, threshold zero), so this
        // block will not catch the eye — it is a guard, not an ornament.
        static void AppendUnknown(GameData game, DbRecord rec, List<TipLine> lines, int indent, int slot)
        {
            if (rec == null || !rolls) return;
            foreach (string field in ItemOrder.Fields(rec, RollJitter))
            {
                if (Told(field)) continue;
                double got;
                double? value = Num(rec, field, slot);
                if (!value.HasValue) continue;
                string text = (RolledOf(field, out got) ? Fmt(got) : Fmt(value.Value)) + "   " + field;
                lines.Add(new TipLine(text + "   " + Lang.T("item.noGameWords"), TipKind.Warning, indent));
            }
        }

        // Whether the field is named in any display table — by its own name or as a pair.
        static bool Told(string field)
        {
            Stat[][] tables = { OffenceStats, DefenceStats, SkillFacts, SkillArmour };
            foreach (Stat[] table in tables)
                foreach (Stat s in table)
                    if (s.Field == field || s.Pair == field ||
                        (s.Attach != null && (s.Attach + "Min" == field || s.Attach + "Max" == field))) return true;
            return ItemRoll.IsConversion(field);
        }

        // The spread a single item instance can roll, shown the way the game shows
        // it: "+704 health [576-864]". The record holds one number and the game
        // rolls a value around it from the item's seed.
        //
        //     min = ceil (base × 0.8)      but never closer than base − 1
        //     max = floor(base × 1.2)      but never closer than base + 1
        //
        // The spread is a flat twenty per cent — **not** the record's own
        // attributeScalePercent, which was the obvious guess and is wrong. The
        // A level-75 item settles it: its record says
        // attributeScalePercent = 30, and the game still rolls its 444 health as
        // [356-864/…] — 444 × 0.8 and 444 × 1.2, not 0.85 and 1.15.
        //
        // Checked against the game on thirty-seven bonuses of seven items
        // (screen-relo-armor2..9.jpg, screen-relo-weapon1.jpg) — every one to the
        // unit, including the small values where the ±1 floor is what makes it
        // come out right, and the two items whose attributeScalePercent differs.
        //
        // Damage follows the very same rule — the earlier reading, "scale first,
        // then ±20%", was wrong and left three of eleven ranges one wider at each
        // end. attributeScalePercent does not take part in the draw at all: it
        // multiplies the ALREADY DRAWN INTEGER, and it multiplies it the way C
        // divides, throwing the fraction away.
        //
        //     j    ∈ [ ceil(base × 0.8), floor(base × 1.2) ]
        //     damage = j × (100 + scale) / 100, truncated
        //
        // What settles it is a range the old rule could not produce at all.
        // The same rule applies to acid 94, where the game prints [106–156]; no constant
        // multiplier gives both ends. The new rule gives them exactly:
        // ceil(75.2) = 76 → 76×140/100 = 106, floor(112.8) = 112 → 156. The same
        // also matches cold 85 → [95–142] and a breastplate value of 62 → [70–103].
        //
        // Checked on all 270 damage numbers the game has shown us
        // (seed-rolls.tsv): 98.5% of them land on a value this rule can produce,
        // where blind chance gives 71.2%. The old rule offered no such test — it
        // allowed every integer in its range.
        //
        // A caveat used to stand here: "the real roll sometimes lands one step BELOW the low end
        // the game prints" — from the example of offensiveFireMax 14 at scale 40, rolling 15
        // against a printed low of 16. THE CAVEAT WAS WITHDRAWN on 13 August: in a "min–max"
        // pair the upper end is not rolled at all, the game rolls the WIDTH of the range and
        // adds it to the rolled lower end (ItemRoll.PairMax). That 15 is 12 + 3, and there is no
        // stepping outside the bracket.
        const double RollSpread = 0.2;

        [ThreadStatic] static int rollScale;
        [ThreadStatic] static bool rolls;

        // THE CHARACTER'S COOLDOWN REDUCTION, in percent, for the "N s skill recharge" line.
        // The game prints it ALREADY REDUCED while we printed the record's number. Measured
        // 18 August from two skills-window shots of `_Izaar`, whose reduction is exactly 4%:
        //     one skill   record 3.5  →  game 3.4   (3.5 × 0.96 = 3.36)
        //     another     record 18   →  game 17.3  (18 × 0.96 = 17.28)
        // The game prints both numbers to one decimal, and both agree under ordinary rounding.
        // It is set only where the character is known — in the skills window; in an ITEM's
        // tooltip (granted skills) there is no measurement, and there it is zero.
        [ThreadStatic] public static double SkillCooldownCut;
        // MODIFIERS OF THE SAME SKILL FROM THE TREE change the cooldown and the cost BEFORE the
        // character's percent. Measured 5 September on a necromancer, two numbers, both agreeing
        // to a tenth:
        //
        //   a summon's record says 18 s, its tree modifier (rank 13) gives −6.2 s, and the
        //     character's reduction is 16.1%:
        //       (18 − 6.2) × 0.839 = 9.90   game 9.9        ✔
        //        18 × 0.839 − 6.2  = 8.90   wrong           ✘  — so subtract FIRST
        //   the cost is 230 in the record and the same modifier gives −51%:
        //       230 × 0.49 = 112.7          game 112.7      ✔
        //
        // BY ANALOGY, UNMEASURED: the same share comes off `skillActiveManaCost` (the per-second
        // cost of toggles) — the field is called a reduction of skill costs without reservation,
        // but we have not met a case of it yet.
        [ThreadStatic] public static double SkillCooldownAdd;
        [ThreadStatic] public static double SkillManaCut;
        // THE DISPLAY MULTIPLIER FOR A SKILL'S NUMBERS: record field → how many times larger the
        // game prints it (MECHANICS 22k). Set for the duration of drawing the skill's block and
        // cleared right after — exactly like `SkillCooldownCut`.
        [ThreadStatic] public static Func<string, double> SkillDamageScale;
        // The computed rolled numbers of this part of the item: field → what its stream gave.
        // Empty when there is nothing to compute (a catalog entry with no seed, a set, a skill).
        [ThreadStatic] static Dictionary<string, long> rolled;
        // On a part with ONE rolled field the traversal order does not enter the answer, and the
        // number is always exact. On the rest it depends on the order (ItemOrder.cs).
        [ThreadStatic] static bool rolledExact;

        static bool Rolls(string field)
        {
            // A duration in seconds is fixed: the game shows "3 s" with the range [3-3]. A
            // duration *percentage* is not — it rolls like any other bonus. A weapon of `_Relo`
            // carries offensiveSlowBleedingDurationModifier = 30 and the game shows 74% with
            // 50% coming from devotion, i.e. 24 = ceil(30 × 0.8).
            // `defensive{Type}Duration` is a DURATION REDUCTION IN PERCENT, and it is rolled:
            // one pair of legs has 50 in its record while the game showed −53% and −44%
            // (seed-test6). The rule "a Duration without Modifier is fixed" was derived from
            // `offensiveSlow…DurationMin`, which holds SECONDS, and those really are fixed.
            // Different things with similar names.
            if (field != null && field.StartsWith("defensive", StringComparison.Ordinal) &&
                field.EndsWith("Duration", StringComparison.Ordinal)) return true;
            if (field != null && field.IndexOf("Duration", StringComparison.Ordinal) >= 0 &&
                field.IndexOf("Modifier", StringComparison.Ordinal) < 0) return false;
            // A cap on a resistance is fixed: the game writes "+4% to maximum aether resistance
            // [4-4]", and so is a requirement discount: "-15% constitution for armour [15-15]".
            // Both seen on _Relo, screen-relo-eq4, eq11 and eq12.
            if (field != null &&
                (field.EndsWith("MaxResist", StringComparison.Ordinal) ||
                 field.IndexOf("ReqReduction", StringComparison.Ordinal) >= 0)) return false;
            // `skillCooldownReduction` IS ROLLED, and that is measured rather than derived: one
            // component has 15 in its record while the game showed 18 at seed 1 and 17 at
            // seed 2 (seed-test2/rolls.tsv, the tooltip's third line). Our list knew only five
            // name prefixes and held this field fixed — so on every item with a skill cooldown
            // the spread was computed wrongly. Neighbouring fields of the same family are
            // marked along with it; there is no measurement on them, and if it turns out
            // otherwise `--rolltest` will catch it.
            if (field == "skillCooldownReduction" || field == "skillCooldownReductionModifier" ||
                field == "skillManaCostReduction" || field == "skillManaCostReductionModifier" ||
                field == "skillProjectileSpeedModifier") return true;
            // These, however, are NOT rolled, and that is visible in the game itself: on one
            // shield the tooltip prints "20% chance to block 130 damage" and "0.80 s block
            // recovery" WITHOUT brackets, and on an off-hand "100% armour piercing" also without
            // one. Both begin with the familiar `defensive`/`offensive` and so fell among the
            // rolled by mistake — and one extra roll shifts the whole item after it. Screenshots
            // of 12 August, seed-test3.
            // `characterManaRegen` (energy per second) is NOT rolled: the game prints a
            // degenerate bracket both for a fractional value ([5.2-5.2] on a caftan) and for a
            // whole one ([4-4] on a shield). Our rule about fractions did not catch it, and the
            // field ate a roll — while without it that shield lines up exactly on rolls 1-11 in
            // a row.
            if (field == "characterManaRegen") return false;
            // `characterLightRadius` is NOT rolled: one ring has 25 in its record and the game
            // prints "+25% light radius [25-25]" — a degenerate bracket. Our rule would have
            // given [20-30] and eaten a roll; without it the whole ring lines up exactly on
            // rolls 1-7 in a row (screenshot of 13 August).
            if (field == "characterLightRadius") return false;
            // `characterIncreasedExperience` ("+N% experience") is NOT rolled: one medal in
            // `_Assira`'s stash has 6 in its record and the game prints "+6% experience [6-6]" —
            // a degenerate bracket. Our rule would have given [5,7], the roll ate a word, and
            // everything after it drifted: five numbers fell into place the moment the field was
            // taken out of the stream — cold 66, aether 66, frostburn 74, poison res. 33,
            // elemental res. 25 (they had been 74/75/67, 27 and 30).
            if (field == "characterIncreasedExperience") return false;
            // A CHANCE IS FIXED; only the magnitude is rolled. Two screenshots of 13 August,
            // both printing the chance WITHOUT a bracket and the magnitude with one:
            //   a helm: "10% chance of 21% total slow for 3 s [16-25] [3-3]" — the record's
            //      chance is 10 and so is the game's (`offensiveSlowTotalSpeedChance`);
            //   a relic: "25% chance of: +160% fire damage …" — six lines with no brackets
            //      (`offensive{Type}ModifierChance` = 25).
            // THE MAGNITUDES THEMSELVES ARE ROLLED: the relic's record holds all six at 200 and
            // the game showed six different numbers. The game simply prints no bracket on a
            // chance line.
            // `offensive{Type}Global` is not a bonus at all but the flag "the chance is shared
            // by all types" (1 in the record). It fell among the rolled and ate SEVEN rolls on
            // a single relic.
            // THE SAME GOES FOR RETALIATION DAMAGE — measured 15 August on one item of
            // `_Amakata`'s stash: its record holds `retaliationConfusionChance` 24 and the game
            // prints "24% chance of 1 s retaliation confusion", i.e. exactly the record's
            // number. Our extra roll shifted the three fields after it.
            if (field != null &&
                (field.StartsWith("offensive", StringComparison.Ordinal) ||
                 field.StartsWith("retaliation", StringComparison.Ordinal)) &&
                (field.EndsWith("Chance", StringComparison.Ordinal) ||
                 field.EndsWith("Global", StringComparison.Ordinal))) return false;
            // BLOCK CHANCE IS NOT ROLLED — measured 15 August from screenshots of the shields in
            // `_Amakata`'s stash, and the proof here is not one but four:
            //   * TWO identical shields with DIFFERENT seeds show the same "31% chance to block
            //     1105 damage" although all their other numbers differ (158/133 life damage,
            //     36/33% life, 58/51% resistance);
            //   * the same for another identical pair: both 35% and 1380;
            //   * a third shield: 34 in the record, 34 in the game, our roll gave 39;
            //   * a fourth: 40 in the record, 40 in the game, our roll gave 37.
            // And above all, the game prints this line WITHOUT A BRACKET, exactly as for the
            // other non-rolled fields; the neighbouring `defensiveBlockModifier` does have a
            // bracket ("+4% shield block chance [3-5]") and is rolled.
            // The extra roll shifted everything after it: on the third shield both conversions
            // drifted (45/12 against the game's 38/15) and so did the cooldown (14 against 12).
            // THE CHANCE ON `defensive*` IS NOT ROLLED EITHER — the same rule already stated
            // above for `offensive*Chance` and `retaliation*Chance`. Measured 26 August on TWO
            // identical components in `_Amakata`'s bag: the record holds
            // `defensiveFreezeChance 20`, the game prints "20% chance" on both, and our roll
            // gave 19 on one of the two. The game prints no bracket at all here — exactly as for
            // the other non-rolled fields — while the neighbouring `defensiveFreeze` comes with
            // a bracket [80-120] and is rolled (90 and 114, both matched). Before this, only
            // `defensiveBlockChance` stood here by name.
            if (field != null &&
                field.StartsWith("defensive", StringComparison.Ordinal) &&
                field.EndsWith("Chance", StringComparison.Ordinal)) return false;
            if (field == "defensiveProtection" || field == "characterBaseAttackSpeed" ||
                field == "defensiveBlock" || field == "blockAbsorption" ||
                field == "blockRecoveryTime" || field == "offensivePierceRatioMin" ||
                field == "offensivePierceRatioMax") return false;
            return field != null &&
                   (field.StartsWith("character", StringComparison.Ordinal) ||
                    field.StartsWith("defensive", StringComparison.Ordinal) ||
                    field.StartsWith("retaliation", StringComparison.Ordinal) ||
                    field.StartsWith("offensive", StringComparison.Ordinal) ||
                    field.StartsWith("conversionPercentage", StringComparison.Ordinal));
        }

        // Damage bonuses — the ones the game scales — against everything else.
        // The same from outside: a pet's skill damage is multiplied by its bonuses, and what
        // counts as "damage" must be decided in one place — otherwise an offensive-ability cut
        // travels along with physical damage (which is what happened in the first version: one
        // pet skill came out with 2381 of cut instead of 180).
        public static bool IsDamageField(string field) { return IsDamage(field); }

        static bool IsDamage(string field)
        {
            if (field == null || !field.StartsWith("offensive", StringComparison.Ordinal)) return false;
            // "% total damage" is a damage percentage like any other, and it is
            // scaled the same way: on _Relo the belt's 36 has to reach 49 for the
            // game's +302% total to come out, which the plain ±20% cannot give
            // and the scaled rule can.
            if (field == "offensiveTotalDamageModifier") return true;
            // Four fields added on 12 August: without the scale their rolled numbers are
            // IMPOSSIBLE rather than merely unlikely — they are above the ±20% ceiling.
            //     offensiveSlowPoisonDurationModifier  30 → 42   (ceiling without scale 36)
            //     offensiveSlowTotalSpeedMin           15 → 21   (ceiling 18)
            //     offensiveBonusPhysicalMin            50 → 106  (ceiling 60, scale 80)
            //     offensiveBonusPhysicalMax            70 → 127  (ceiling 84)
            // With the scale each matches exactly. The check is `--rolltest`.
            if (field == "offensiveSlowTotalSpeedMin" || field == "offensiveSlowTotalSpeedMax") return true;
            // AND A FIFTH, 22 October, found the same way: without the scale the game's number
            // is IMPOSSIBLE, not merely unlikely. `offensiveStunModifier` ("+19% stun duration")
            // on `_RuneTask`'s machete — record 15, jitter 12, scale 40:
            //     without the scale the ceiling is 15 + floor(15×0.12) = 16, and the game prints 19
            //     with it      trunc(14 × 1.4) = 19   ← exactly, and 14 is what we rolled
            // The whole item then reads off in full: 75 / 69 / 74 / 70 / 19 / 16 / 58 / 17,
            // every number as the game's tooltip prints it (MECHANICS 174).
            //
            // ONLY THIS FIELD, not its family: the neighbours `offensiveFreezeModifier`,
            // `offensiveSleepModifier` and the rest have no measurement, and no item of ours
            // carries one. Adding them by symmetry would be a guess with nothing to catch it.
            if (field == "offensiveStunModifier") return true;
            // MANA LEECH IS DAMAGE TOO, and the scale applies to it. Measured 15 August on one
            // weapon of `_Amakata`'s stash (record 32, scale 40): the game prints "108 mana
            // leech over 3 s [108-159]", i.e. [36-53] per second. Without the scale the rule
            // gives [26-38] × 3 = [78-114] — the game's numbers are IMPOSSIBLE; with it
            // trunc(26×1.4)=36 and trunc(38×1.4)=53 match at both ends, and the rolled 36 × 3 =
            // 108 matches as well.
            // `offensiveLifeLeechMin` ("% of damage converted to health"), however, is NOT
            // scaled, and that is a measurement too: another weapon's record 12 at scale 40 was
            // printed as [10-14], i.e. exactly ±20% without it. Two fields neighbouring in
            // meaning with different rules — hence the note.
            if (field == "offensiveSlowManaLeachMin" || field == "offensiveSlowManaLeachMax") return true;
            foreach (DamageType d in Types)
            {
                if (field == "offensive" + d.Field + "Modifier") return true;
                if (field == "offensiveSlow" + d.Field + "Modifier") return true;
                if (field == "offensiveSlow" + d.Field + "DurationModifier") return true;
                if (field == "offensive" + d.Field + "Min" || field == "offensive" + d.Field + "Max") return true;
                if (field == "offensiveBase" + d.Field + "Min" || field == "offensiveBase" + d.Field + "Max") return true;
                if (field == "offensiveBonus" + d.Field + "Min" || field == "offensiveBonus" + d.Field + "Max") return true;
                if (field == "offensiveSlow" + d.Field + "Min" || field == "offensiveSlow" + d.Field + "Max") return true;
            }
            // `retaliation*`, however, does NOT obey the scale, and that is not a guess: eleven
            // rolled numbers (`retaliationPoisonMin`, `…PhysicalMin`, `…TotalDamageModifier` on
            // `_Relo` and `_Itaan`) are all explained WITHOUT it and NOT ONE with it. The same
            // for `offensiveCritDamageModifier` and `offensiveLifeLeechMin`: they too stay
            // outside the scale.
            return false;
        }

        // The roll range of one field of one item, for anyone who needs the
        // numbers rather than the text.
        // ±20% is the jitter of an item's BASE record. An affix has ITS OWN, held in its record
        // as `lootRandomizerJitter`. Found 13 August 2026 in GD Stash's code: `ARZDecompress`
        // puts two different tags into the same `rngPercent` field — `attributeScalePercent` for
        // items and `lootRandomizerJitter` for affixes — and `GDItem.applyRNG` takes the roll's
        // bounds from it.
        //
        // Checked by number on a rare item (`_Amakata`, bag 1, item 16), and ALL of the game's
        // brackets match:
        //   prefix, jitter 8:  acid 18–30 at scale 45 → the game prints [(24-35)-(27-40)], which
        //       is exactly our four numbers (at ±20% it would be [(21-…)-(30-…)] — wrong on all
        //       four);
        //   suffix, jitter 12: attack speed 13 → the game gives [12-14] (at ±20%, [11-15]),
        //       cold 15 at scale 45 → the game gives [20-23] (at ±20%, [17-26]).
        // Before this find, NOT ONE affix number of a rare item matched.
        // AN AFFIX WITHOUT `lootRandomizerJitter` HAS NO JITTER AT ALL — the field is fixed.
        //
        // That was measured 14 August by experiment seed-test9 and resolves the contradiction
        // that forced the change to be rolled back the day before. Two prefixes, both with a
        // number whose raw bracket is degenerate:
        //   `aa009a_oamod_01`  characterOffensiveAbilityModifier 4, NO jitter field
        //       → the game prints [4-4] and pulls no roll. The medal on it stayed 47 / 50, i.e.
        //         on the FIRST roll;
        //   `ao004a_cold_04`   offensiveColdMin 5, jitter 15 IS there
        //       → it does pull a roll: the medal shifted to 51 / 47, and the cold itself gave
        //         7 and 8, i.e. j = 5 and 6 — bracket [4,6], the ∓1 caveat works.
        // The difference is neither the kind of field nor whether it is an affix or an item, but
        // the presence of the jitter field itself. We used to substitute 20% for a missing one,
        // and it ate an extra roll — which is why one amulet and everything standing after such
        // a field in the stream did not match.
        //
        // An item's BASE record never has a `lootRandomizerJitter` field, and there the jitter is
        // its own 20% — measured on 87 items and held in place by the checks.
        public static int JitterOf(DbRecord rec)
        {
            if (rec == null) return 20;
            // An affix is recognised by its class: `LootRandomizer` covers a prefix, a suffix, an
            // ascension and a component's completion bonus alike.
            if (string.Equals(rec.Class, "LootRandomizer", StringComparison.OrdinalIgnoreCase))
                return rec.GetInt("lootRandomizerJitter");
            int j = rec.GetInt("lootRandomizerJitter");
            return j > 0 ? j : 20;
        }

        // The jitter of the item part currently being shown. Stored SHIFTED BY ONE, and that is
        // no trifle: zero is a lawful value ("the field is not rolled at all", for an affix
        // without `lootRandomizerJitter`), while an unset field must mean the usual 20%. Without
        // the shift the two are indistinguishable, and the first version of the 14 August change
        // stumbled on exactly that: zero turned back into 20.
        [ThreadStatic] static int rollJitterPlus1;
        public static int RollJitter
        {
            get { return rollJitterPlus1 == 0 ? 20 : rollJitterPlus1 - 1; }
            set { rollJitterPlus1 = value + 1; }
        }

        public static bool RollBounds(string field, double value, int scale, out double lo, out double hi)
        {
            return RollBounds(field, value, scale, RollJitter, out lo, out hi);
        }

        // Fields whose ROLL bracket is not widened to "no closer than ∓1". The measurement and
        // reasoning are at the caveat itself in RollBounds below. A list of one field on purpose:
        // there is no measurement on its `skill*` group neighbours.
        static bool NoWiden(string field)
        {
            return field == "skillCooldownReduction";
        }

        public static bool RollBounds(string field, double value, int scale, int jitter,
                                      out double lo, out double hi)
        {
            return RollBounds(field, value, scale, jitter, true, out lo, out hi);
        }

        // THE DISPLAY BRACKET AND THE ROLL BRACKET ARE NOT THE SAME THING, and `shown` is exactly
        // what tells them apart. The game prints the bracket ALREADY CLAMPED ("not below 1") but
        // rolls within the unclamped one and clamps the result. Measured 15 August; the reasoning
        // is at `if (shown && value >= 1 ...)` below.
        public static bool RollBounds(string field, double value, int scale, int jitter, bool shown,
                                      out double lo, out double hi)
        {
            lo = hi = value;
            if (!Rolls(field) || Math.Abs(value) < 0.0001) return false;
            // CONVERSION HAS A BRACKET OF ITS OWN: `v ± round(v × jitter/100)`, not ceil/floor of
            // ±jitter%. Measured from three screenshots with three different jitters — the
            // reasoning is at [ItemRoll.ConvBounds](ItemRoll.cs). It comes out degenerate exactly
            // when round(v × jitter/100) = 0, and then `ItemOrder.Fields` lets such a field into
            // no stream — as with every other field.
            if (ItemRoll.IsConversion(field)) return ItemRoll.ConvBounds(value, jitter, out lo, out hi);
            // THE RULE IS ONE FOR EVERY FIELD and is always taken from the record's number: ±20%
            // in whole numbers, ceil at the bottom and floor at the top. attributeScalePercent
            // takes no part in the roll itself — it multiplies the ALREADY ROLLED INTEGER, and
            // multiplies it integrally: j × (100 + scale) / 100 with the fraction thrown away.
            // The reasoning and the check are covered by the exact-roll audit.
            // A fractional value is NOT rolled: the game prints a degenerate bracket for it —
            // "+4.5 energy per second [4.5-4.5]", "0.80 s block recovery". Measured by the fourth
            // experiment: on three items with energy regeneration 5.2 the ordinary fields landed
            // on the FIRST roll, i.e. the fractional field pulls none.
            if (Math.Abs(value - Math.Truncate(value)) > 0.0001) return true;
            // A jitter of ZERO means the field is not rolled at all, and the "no closer than ∓1"
            // caveat does not widen it: the bracket stays degenerate, and `ItemOrder.Fields` lets
            // such a field into no stream.
            if (jitter == 0) { lo = hi = value; return true; }
            // IT MUST BE COMPUTED AS `v × (100 ± jitter) / 100`, NOT `v × (1 ± jitter/100)`.
            // The difference is not in elegance but in a whole number: `200 × (1 + 0.15)` in
            // double precision gives 229.99999999999997, and floor cuts it to 229 — the bracket
            // comes out [170,229] instead of [170,230], one number narrower, and EVERY roll of
            // such a field lands in the wrong place. `200 × 115 / 100` gives exactly 230.
            // Measured 15 August on one belt in `_Relo`'s stash: the game prints "+173 health
            // [170-230]" while we showed 219 — both the number and the bracket wrong.
            lo = Math.Ceiling(value * (100 - jitter) / 100.0);
            hi = Math.Floor(value * (100 + jitter) / 100.0);
            // THE "NO CLOSER THAN ∓1" CAVEAT: A SKILL COOLDOWN DOES NOT HAVE IT IN THE ROLL.
            //
            // Validated with multiple items and controlled seeds.
            // `skillCooldownReduction` with a record of 4 has a degenerate ±20% bracket
            // (3.2…4.8, exactly one integer inside):
            //   SIX identical helms, seeds 1–6 — the game gave −4% on ALL SIX while printing the
            //     bracket as [3-5];
            //   FOUR identical weapons, seeds 3–6 — also −4% on all, plus yesterday's two with
            //     seeds 1 and 2: ten items, ten fours.
            // With a roll bracket of [3,5] that is (1/3)¹⁰ — one case in sixty thousand.
            // The same experiment cleared the suspicion that the item kind matters: the helm and
            // the axe gave the same thing, while another weapon (record 16, bracket [13,19], not
            // degenerate) gave −13/−19/−18/−17 on four seeds — exactly our calculation, four out
            // of four.
            //
            // WHY THIS IS NOT A GENERAL RULE. The opposite was checked first — "the caveat does
            // not apply to the roll at all" — and our own numbers refute it: with a record of 4
            // the game showed 3 and 5 on `defensivePhysical` (six items),
            // `characterOffensiveAbilityModifier`, `characterDodgePercent` and
            // `characterStrengthModifier`, and with a record of 3 it showed 2 and 4 on
            // `characterDefensiveAbilityModifier` (one devotion star, on two different
            // characters). Remove the caveat everywhere and 19 numbers of `--ordertest` go red,
            // plus four more checks. So the widening is real, but THIS field does not have it:
            // its only structural difference is that it belongs to the `skill*` group.
            //
            // THE RULE IS NOT EXTENDED TO ITS GROUP NEIGHBOURS: there is no measurement at all on
            // `skillManaCostReduction` and `skillProjectileSpeedModifier`, and there is nothing to
            // guess here — an item with such a record will turn up, and then the same experiment
            // will settle it. WHY the field behaves differently we do not know; checked and
            // rejected: the field's type in the record (all of them are integers, not
            // fractional), the item kind, and the record's number by itself.
            //
            // The DISPLAY bracket is still widened for everyone — the game itself prints [3-5] on
            // an item that always gives 4.
            //
            // …and the "no closer than ∓1" caveat does not carry past zero: the game prints the
            // bracket itself, and there it is 1. On both rings of `_Jien` (eq7/eq8) cold 1–14 is
            // printed as [(1–12)–(2–17)], i.e. the lower end is shown as [1,2]. For negatives the
            // same by symmetry — there is no measurement.
            //
            // BUT THE GAME ROLLS IN THE UNCLAMPED BRACKET AND CLAMPS THE RESULT. That was the
            // "lower end of the pair" which had not matched since 13 August. With a record of 1
            // and a jitter of 20% the general rule gives `[0,2]` — three numbers — while the clamp
            // 0 → 1 makes one twice as likely as two. Measured 15 August on fifteen rings of
            // `seed-test10` (one record, chosen seeds):
            //     bracket [1,2], remainder — 8 of 15, i.e. no better than chance;
            //     bracket [0,2], remainder, then the clamp — 15 OF 15.
            // And the brute-force search singled it out unambiguously: out of all combinations of
            // "word 1..12 × bracket × three mappings", EXACTLY ONE met the condition "at least 14
            // of 15". It also proved the search was being run in the wrong place: NOT ONE word of
            // the stream 1..14 separates the two values by a threshold at all, so the whole class
            // of "word × mapping" searches is empty here.
            //
            // The same trick as the conversion ceiling (ItemRoll.ConvValue): the display is
            // clamped, the roll is not. The result's clamp is in ItemRoll.Value/Raw.
            if (shown || !NoWiden(field))
            {
                if (lo > value - 1) lo = value - 1;
                if (hi < value + 1) hi = value + 1;
            }
            if (shown && value >= 1 && lo < 1) lo = 1;
            if (shown && value <= -1 && hi > -1) hi = -1;
            if (IsDamage(field) && scale != 0) { lo = Scaled(lo, scale); hi = Scaled(hi, scale); }
            if (value < 0) { double t = lo; lo = hi; hi = t; }
            return true;
        }

        // The item's integer scale: j × (100 + scale) / 100, with the fraction thrown away
        // (towards zero, as C divides). It is what gives [106–156] for one weapon's acid 94 —
        // numbers the old rule "scale first, then ±20%" produced at no multiplier whatsoever.
        static double Scaled(double j, int scale)
        {
            return Math.Truncate(j * (100 + scale) / 100.0);
        }

        // Whether a field can give EXACTLY this number. On ordinary fields everything inside the
        // bracket is reachable; on damage it is not, because the integer multiplication by
        // attributeScalePercent skips some of the numbers. That is exactly what makes the rule
        // testable: at scale = 40 only every fifth seventh of the range is reachable, and all of
        // the game's rolled numbers land on those. The check is `--rolltest`.
        //
        // The low end is taken one step wider than the game prints: the game prints
        // ceil(base × 0.8), while the measured roll also landed on floor — see the comment at
        // RollBounds. The caveat stands here rather than in the bracket so that the display stays
        // exactly as in the game.
        public static bool RollCanGive(string field, double value, int scale, double got)
        {
            if (!Rolls(field) || Math.Abs(value) < 0.0001) return false;
            // Conversion goes by its own rule: its roll bracket is fractional, and what is
            // reachable inside it is not "every integer of the game's bracket" but a segment of
            // its own (ItemRoll).
            if (ItemRoll.IsConversion(field)) return ItemRoll.ConvCanGive(value, RollJitter, got);
            double sign = value < 0 ? -1 : 1, v = Math.Abs(value);
            double j0 = Math.Min(Math.Floor(v * (1 - RollSpread)), v - 1);
            double j1 = Math.Max(Math.Floor(v * (1 + RollSpread)), v + 1);
            bool dmg = IsDamage(field) && scale != 0;
            for (double j = j0; j <= j1; j++)
                if (sign * (dmg ? Scaled(j, scale) : j) == Math.Round(got)) return true;
            return false;
        }

        // The item's integer scale applied to an already rolled integer. Exposed separately so
        // that the roll itself (ItemRoll) can use it too, not only the bracket display.
        public static double RollScaled(string field, double j, int scale)
        {
            return IsDamage(field) && scale != 0 ? Scaled(j, scale) : j;
        }

        // The same WITHOUT throwing the fraction away. The game SHOWS the truncated value — that
        // is measured on hundreds of numbers; whether it keeps the fractional one in the
        // calculation, a tooltip cannot say, just as with conversion. It is checked by comparing
        // the damage itself.
        public static double RollScaledExact(string field, double j, int scale)
        {
            return IsDamage(field) && scale != 0 ? j * (100 + scale) / 100.0 : j;
        }

        // Whether the field is rolled at all. Needed where "does not match" must be told from
        // "nothing to compare": `characterArmorStrengthReqReduction` has no brackets, and the
        // tooltip holds exactly the record's number, only with a minus sign.
        public static bool RollsAtAll(string field) { return Rolls(field); }

        // THE GAME PRINTS DAMAGE OVER TIME AS A TOTAL, A DEBUFF NOT. Both are written "over
        // N s", but the number beside them differs, and this is a measurement, not a guess:
        //   "114 frostburn over 3 s [90-120]"            — 38 per second × 3, a TOTAL;
        //   "108 mana leech over 3 s [108-159]"          — 36 × 3, a TOTAL;
        //   "5940 retaliation poison damage over 5 s"    — 1188 × 5, a TOTAL;
        //   "124 reduced defensive ability for 5 s [88-132]" — 124 is just 124, WITHOUT ×5
        //       (record 110, the bracket exactly ±20% of it — nothing to multiply).
        // So what ACCUMULATES over the seconds is multiplied, while a steady debuff is not. Mana
        // leech counts as accumulation, and that too comes from a screenshot.
        static bool ShownWholeDuration(string field)
        {
            if (field == null) return false;
            int cut = field.IndexOf("Slow", StringComparison.Ordinal);
            if (cut < 0) return false;
            string rest = field.Substring(cut + 4);
            if (rest.StartsWith("ManaLeach", StringComparison.Ordinal)) return true;
            foreach (DamageType d in Types)
                if (rest.StartsWith(d.Field, StringComparison.Ordinal)) return true;
            return false;
        }

        // The duration field belonging to this dot: `offensiveSlowColdModifier` →
        // `offensiveSlowColdDurationModifier`. It is both printed on the same line with it and
        // stands right after it in the roll stream (ItemOrder.Compare).
        static string DurationSibling(string field)
        {
            if (field == null || !field.EndsWith("Modifier", StringComparison.Ordinal)) return null;
            if (field.IndexOf("Slow", StringComparison.Ordinal) < 0) return null;
            if (field.IndexOf("Duration", StringComparison.Ordinal) >= 0) return null;
            return field.Substring(0, field.Length - 8) + "DurationModifier";
        }

        static string RollRange(string field, double value) { return RollRange(field, value, 1); }

        // `mult` is the dot's duration: the game prints both the number and the bracket for the
        // WHOLE duration, and it is the finished bracket that gets multiplied, not the record's
        // number. The order matters: on one claymore 3 × [30,40] gives [90,120], as in the game,
        // while a bracket taken from the tripled record would give [91,120].
        static string RollRange(string field, double value, double mult)
        {
            double lo, hi;
            if (!rolls || !RollBounds(field, value, rollScale, out lo, out hi)) return "";
            return "  [" + Fmt(lo * mult) + "–" + Fmt(hi * mult) + "]";
        }

        // What actually came up — computed from this item part's seed.
        //
        // This is neither the record nor the bracket but the very number the game would show in
        // its tooltip.
        //
        // THE QUESTION MARK BECAME TARGETED (15 August). It used to be placed WHOLESALE — on
        // every item with more than one rolled field — because the traversal order was not fully
        // derived, and doubting everything was the honest thing. Now it is derived: all 83 items
        // of the table reproduce completely, and `--wholetest` holds 259 of the game's numbers.
        // So the doubt must be placed not wholesale but where it belongs:
        // [ItemOrder.DoubtOf](ItemOrder.cs) names the FIRST place where the order is guessed
        // rather than measured, and the whole tail after it. The mark goes only on those lines;
        // across the saves that is 42 items of 763 (`--ordertrust`).
        static string RollGot(string field, string pair, bool isRange)
        {
            if (rolled == null) return "";
            long v;
            if (!rolled.TryGetValue(field, out v)) return "";
            string text = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            long hi;
            if (isRange && pair != null && rolled.TryGetValue(pair, out hi))
                text += "–" + hi.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "  " + Lang.T("item.rolledValue", text);
        }

        // THE EXACT NUMBER INSTEAD OF THE BRACKET'S MIDPOINT.
        //
        // The record holds the midpoint while the game shows what THIS instance rolled. While
        // the fields' traversal order was not fully derived, the honest thing was to print the
        // record's number and append the computed one beside it ("rolled N"). Now the order is
        // derived, and on 729 items of 763 there is nothing to doubt — the number goes straight
        // into the line, as in the game, and no "rolled" beside it is needed any more.
        //
        // Where a field's place in the stream is still GUESSED, everything stays as before: the
        // line holds the record's number (the whole damage model computes from it), "rolled N ?"
        // stands beside it, and the line itself is marked red. That shows what to trust and what
        // to check next.
        static bool RolledOf(string field, out double got)
        {
            got = 0;
            long v;
            if (rolled == null || field == null || !rolled.TryGetValue(field, out v)) return false;
            got = v;
            return true;
        }

        static bool DoubtOf(string field)
        { return field != null && rolledDoubt != null && rolledDoubt.Contains(field); }

        // Set (or clear) the rolled-number tally for one part of the item. Each part's stream is
        // its own and starts afresh from its own seed — measured on the pet bonus
        // (MECHANICS 14a).
        // The WHOLE item's rolled numbers are computed at once (the base and the affixes share
        // one stream — ItemOrder.RollWhole) and are only split by part for display.
        [ThreadStatic] static List<ItemOrder.Rolled> wholeItem;

        // Fields of the WHOLE item whose place in the stream is guessed rather than measured.
        // Computed once per item together with the traversal itself; empty means everything can
        // be trusted.
        [ThreadStatic] static HashSet<string> rolledDoubt;

        // Take one part's lines out of the common computation: 0 base, 1 prefix, 2 suffix.
        // Whose part is being printed right now. The item edit window needs it: the seed search
        // edits a field of a SPECIFIC part, and the parts cannot be told apart by a field name
        // alone — `characterLife` occurs on the base and on both affixes.
        [ThreadStatic] static int linePart;

        static void RolledPart(DbRecord part, int which)
        {
            rolled = null; rolledExact = false; linePart = which;
            // The jitter is a property of THIS record and is also needed to show the brackets of
            // its lines, so it is set here rather than only inside the computation.
            RollJitter = JitterOf(part);
            if (part == null || wholeItem == null) return;
            Dictionary<string, long> mine = new Dictionary<string, long>(StringComparer.Ordinal);
            int n = 0;
            foreach (ItemOrder.Rolled r in wholeItem)
            {
                if (r.Part != which) continue;
                mine[r.Field] = r.Got; n++;
            }
            if (n == 0) return;
            rolled = mine;
            rolledExact = rolledDoubt == null || rolledDoubt.Count == 0;
        }

        // "25% Cold Damage converted to Vitality Damage" — three fields that only
        // mean anything together, so they cannot live in the table.
        static void AppendConversion(GameData game, DbRecord rec, List<TipLine> lines, int indent, string n, int slot)
        {
            double? percent = Num(rec, "conversionPercentage" + n, slot);
            string from = rec.Get("conversionInType" + n), to = rec.Get("conversionOutType" + n);
            // A conversion with no receiver is still rendered as a line.
            //
            // An item modifier may provide this conversion.
            // (`set3_oathkeeper1b.dbr`) holds `conversionInType Stun` and
            // `conversionPercentage 100` and NO `conversionOutType` at all. The game prints
            // such a record as a loss of the source itself, named by the source's own
            // The source conversion tag is used even when no receiver is present.
            // the empty receiver threw the whole line away.
            //
            // Measured on ONE record and only for Stun; other control types are not seen yet.
            if (!percent.HasValue || percent.Value <= 0 || string.IsNullOrEmpty(from)) return;
            string fromName = DamageName(game, from);
            string toName = string.IsNullOrEmpty(to) ? null : DamageName(game, to);
            // The bracket and the rolled number come from ITS OWN field (`conversionPercentage2`
            // is the second conversion of the same record and has a number of its own). The name
            // used to stand here without the index: the bracket came out of the first conversion.
            string field = "conversionPercentage" + n;
            double recValue = percent.Value, gv;
            bool doubt = DoubtOf(field);
            if (!doubt && RolledOf(field, out gv)) percent = gv;
            string text = game == null || toName == null ? null
                : game.FormatLocalTag("tagDamageConversion", percent.Value, fromName, toName);
            TipLine made = new TipLine((text ?? (toName == null
                                            ? Lang.T("item.conversionGone", Fmt(percent.Value), fromName)
                                            : Lang.T("item.conversion", Fmt(percent.Value), fromName, toName)))
                                       + RollRange(field, recValue)
                                       + (doubt ? RollGot(field, null, false) : ""),
                                       TipKind.Bonus, indent);
            made.Doubt = doubt && rolled != null && rolled.ContainsKey(field);
            made.Explains = new string[] { field };
            if (rolled != null) made.Part = linePart;
            lines.Add(made);
        }

        // The damage type names the conversion line splices in are their own tags.
        static string DamageName(GameData game, string type)
        {
            string name = game == null ? null : game.Tag("tagConversion" + type);
            if (!string.IsNullOrEmpty(name)) return name;
            foreach (DamageType d in Types) if (d.Field == type) return d.Ru;
            return type;
        }

        // "+12% Damage to Chthonics": a percentage plus the race it applies to.
        static void AppendRacial(GameData game, DbRecord rec, List<TipLine> lines, int indent, int slot)
        {
            string race = rec.Get("racialBonusRace");
            string[,] kinds = {
                { "racialBonusPercentDamage", "RacialBonusPercentDamage", Lang.T("item.racial.damage") },
                { "racialBonusAbsoluteDamage", "RacialBonusAbsoluteDamage", Lang.T("item.racial.damage") },
                { "racialBonusPercentDefense", "RacialBonusPercentDefense", Lang.T("item.racial.defence") },
                { "racialBonusAbsoluteDefense", "RacialBonusAbsoluteDefense", Lang.T("item.racial.defence") },
            };
            for (int i = 0; i < kinds.GetLength(0); i++)
            {
                double? v = Num(rec, kinds[i, 0], slot);
                if (!v.HasValue || Math.Abs(v.Value) < 0.0001) continue;
                // One bonus can name several races at once ("Race003;Race005"),
                // and the line reads "damage to <them>", so each name is taken in
                // its plural form: tagRace004P, not tagRace004.
                List<string> names = new List<string>();
                foreach (string one in (race ?? "").Split(';'))
                {
                    if (one.Length == 0) continue;
                    names.Add(game == null ? one
                        : game.Tag("tag" + one + "P") ?? game.Tag("tag" + one) ?? one);
                }
                string raceName = string.Join(", ", names.ToArray());
                string text = game == null ? null : game.FormatLocalTag(kinds[i, 1], v.Value, raceName);
                TipLine racial = new TipLine(text ?? ((v.Value > 0 ? "+" : "") + Fmt(v.Value) + "% " + kinds[i, 2] + raceName),
                                           TipKind.Bonus, indent);
                // The field is written onto the line — otherwise `--saidtest` counts it unshown.
                racial.Explains = new string[] { kinds[i, 0] };
                lines.Add(racial);
            }
        }

        // A PET's skill, not the character's. The game's own marker, not ours: `isPetDisplayable`
        // stands on every record the game draws in a pet's tooltip, and only on those. It is
        // needed because several lines have their own wording for a pet.
        // THIS IS A PET'S SKILL — AND WHOEVER DRAWS IT KNOWS THAT, THE RECORD DOES NOT.
        //
        // The marker survived two attempts to infer it from the field, and both were wrong:
        //
        //   4 September  "the `isPetDisplayable` field is not empty" — the field is in EVERY
        //     skill's template and holds zero, so the marker fired everywhere: the item tooltips
        //     of `_Iman` came out with nine "pet base damage" lines and not one "weapon damage",
        //     although there are no pets there at all;
        //   5 September  "the field's value is above zero" — but half the pet skills have no such
        //     field: one familiar's skill lacks it, and we printed "143% weapon damage" where the
        //     game prints "143% pet base damage".
        //
        // So the marker is set FROM OUTSIDE, for the duration of drawing — the same trick as
        // `SkillCooldownCut` and `SkillDamageScale`. It is set by `PetPool`, which alone knows
        // that it is drawing a pet.
        [ThreadStatic] public static bool SkillOfPet;

        public static bool IsPetSkill(DbRecord rec)
        { return SkillOfPet || (rec != null && AttackSkill.Value(rec, "isPetDisplayable", 0) > 0.5); }

        static void AppendTable(GameData game, DbRecord rec, List<TipLine> lines, int indent, Stat[] table, int slot)
        {
            // Fields already written onto somebody else's line, so they do not
            // get a second one of their own.
            List<string> used = new List<string>();
            foreach (Stat s in table)
            {
                if (used.Contains(s.Field)) continue;
                double? value = Num(rec, s.Field, slot);
                if (!value.HasValue || Math.Abs(value.Value) < 0.0001) continue;
                // A COOLDOWN IS PRINTED ALREADY REDUCED — the measurement is at SkillCooldownCut.
                // Rounding to a tenth also comes from the game: it prints 3.4 and 17.3 where the
                // exact numbers are 3.36 and 17.28.
                // The tree modifier is subtracted FIRST and the character's percent second: the
                // order is measured, and both numbers are at `SkillCooldownAdd`.
                if (s.Field == "skillCooldownTime" &&
                    (Math.Abs(SkillCooldownCut) > 0.0001 || Math.Abs(SkillCooldownAdd) > 0.0001))
                {
                    double left = value.Value + SkillCooldownAdd;
                    if (left < 0) left = 0;
                    value = Math.Round(left * (100 - SkillCooldownCut) / 100.0, 1,
                                       MidpointRounding.AwayFromZero);
                }
                if ((s.Field == "skillManaCost" || s.Field == "skillActiveManaCost") &&
                    Math.Abs(SkillManaCut) > 0.0001)
                    value = Math.Round(value.Value * (100 - SkillManaCut) / 100.0, 1,
                                       MidpointRounding.AwayFromZero);
                double? hi = s.Pair == null ? null : Num(rec, s.Pair, slot);
                // The game rolls the real number out of the seed; the record only
                // carries the range. A missing maximum means the value is fixed.
                bool isRange = hi.HasValue && hi.Value > value.Value + 0.0001;
                // The [low–high] bracket is ALWAYS computed from the record's number, so it has
                // to be remembered before the rolled one is substituted.
                double recValue = value.Value;
                // The rolled number goes into the line itself if its place is measured (see
                // RolledOf). If guessed, we leave things as they were and mark the line.
                bool doubt = DoubtOf(s.Field) || (isRange && DoubtOf(s.Pair));
                double gv, gh;
                if (!doubt && RolledOf(s.Field, out gv))
                {
                    value = gv;
                    if (isRange && RolledOf(s.Pair, out gh)) hi = gh;
                }
                // A DOT IS PRINTED FOR ITS WHOLE DURATION, NOT PER SECOND. The game writes
                // "114 frostburn over 3 s [90-120]", where 114 is 38 per second multiplied by
                // three; the record and the calculation hold the per-second number while the
                // game shows the total. We printed 38 and diverged from every such line — a
                // claymore of `_Assira`, a weapon with 108 = 36 × 3, a medal with 80 = 16 × 5,
                // one pair of legs with 5940 = 1188 × 5. The bracket is multiplied too — in the
                // game it is also for the whole duration.
                // The field itself is NOT touched in the calculation: the multiplier here is
                // purely for display.
                double perTick = 1;
                if (s.Attach != null && ShownWholeDuration(s.Field))
                {
                    double? secsAll = Num(rec, s.Attach + "Min", slot);
                    if (secsAll.HasValue && secsAll.Value > 0.0001) perTick = secsAll.Value;
                }
                // A SKILL'S NUMBERS ARE PRINTED WITH THE CHARACTER'S MULTIPLIERS
                // (MECHANICS 22k). Set from outside for the duration of drawing a skill's block
                // — granted or modified — and cleared right after; on the item's own lines there
                // is no multiplier and must not be (the game prints them as in the record:
                // "75 bleeding over 3 s [63-87]" on one relic).
                //
                // AND IT IS COMPUTED BEFORE THE MULTIPLICATION BY DURATION, WITH THE PER-SECOND
                // NUMBER ROUNDED. The order is measured, not chosen: on one weapon the modifier
                // gives 140 bleeding per second at a multiplier of 24.3328, and the game prints
                // "10221 over 3 s" —
                //     round afterwards: 140 × 24.3328 × 3 = 10219.8  →  10220  wrong
                //     round first:      140 × 24.3328 = 3406.59 → 3407 × 3 = 10221  EXACT
                // The same on a set (280 → 6813 → 20439 against the game's 20439) and on a relic
                // (50 → 1217 → 3651 against the game's 3651). Three numbers, one order.
                //
                // This does not touch the field in the calculation, nor does `perTick` below:
                // both quantities are purely for display.
                if (SkillDamageScale != null)
                {
                    double sc = SkillDamageScale(s.Field);
                    if (Math.Abs(sc - 1) > 0.0001)
                    {
                        value = Math.Round(value.Value * sc, MidpointRounding.AwayFromZero);
                        if (hi.HasValue)
                            hi = Math.Round(hi.Value * sc, MidpointRounding.AwayFromZero);
                    }
                }
                if (perTick != 1)
                {
                    value = value.Value * perTick;
                    if (hi.HasValue) hi = hi.Value * perTick;
                }

                // The game ships the wording of every bonus in whatever language
                // is installed, so its own template is used when there is one and
                // the built-in Russian text is only a fallback.
                // Only if the game has this line in the chosen language: it ships
                // "104-137 Physical Damage" in English only, and our own Russian
                // wording is better than that.
                string text = null, label = s.Label;
                // "% WEAPON DAMAGE" IS NAMED DIFFERENTLY FOR A PET, AND THE GAME HAS ITS OWN
                // WORDS FOR IT. The field is the same (`weaponDamagePct`) and the number is the
                // same, but the tag differs: `SkillWeaponDamageFormat` is "{%.0f0%} weapon
                // damage", `SkillWeaponDamagePetFormat` is "{%.0f0%} pet base damage". The marker
                // "this is a pet's skill" comes from the game, not from us: every such record
                // carries `isPetDisplayable` (checked on nine of them).
                //
                // THE NUMBERS MATCHED FOUR OUT OF FOUR on `_666` (4 September): 125, 18, 135 and
                // 50 across four pet skills. So only the wording diverged, and the fix is about
                // display.
                string ownTag = s.Tag;
                if (ownTag == "SkillWeaponDamageFormat" && IsPetSkill(rec))
                { ownTag = "SkillWeaponDamagePetFormat"; label = Lang.T("item.label.petBaseDamage"); }
                if (game != null && ownTag != null)
                {
                    string key = isRange ? ownTag + "R" : ownTag;
                    string template = game.LocalTag(key);
                    // A HINDRANCE IS SECONDS, AND THE NUMBER IS INSERTED INTO IT AS WORDS.
                    // The template of such lines is `stun{%t0}`, and `{%t0}` here is not a
                    // number but a READY PHRASE about the duration: the game takes it from
                    // `DamageFixedSingleFormatTime` ("for {%.1f0} s") and substitutes it whole.
                    // We treated `{%t0}` as a number, knew nothing of the `t` specifier, and
                    // printed "stun2" instead of "stun for 1.5 s".
                    //
                    // MEASURED 21 August from a tooltip screenshot of a skill with
                    // `offensiveStunMin 1.5`: the game prints "stun for 1.5 s", which is exactly
                    // `stun` + `for {%.1f0} s`. The other hindrances (freeze, knockdown, fear,
                    // confusion, sleep, petrify, entrapment, conversion, skill disruption) are
                    // assembled by the same `…{%t0}` template and print the same way; ONE of
                    // them was measured — stun.
                    //
                    // A hindrance's words are THE SAME for a single number and for a pair: it
                    // never has a `…R` tag, and only the duration phrase changes. The game
                    // prints "knockdown for 1.5 - 3 s" (a screenshot from `_Relo`,
                    // `offensiveKnockdownMin/Max` 1.5 and 3) and "stun for 1.5 s" — i.e. it
                    // picks `…RangeFormatTime` or `…SingleFormatTime` by whether there is an
                    // upper end.
                    if (s.Time)
                    {
                        string words = game.LocalTag(s.Tag);
                        if (words != null && words.IndexOf("{%t", StringComparison.Ordinal) >= 0)
                        {
                            string phrase = hi.HasValue && hi.Value > value.Value + 0.0001
                                ? game.FormatLocalTag("DamageFixedRangeFormatTime", value.Value, hi.Value)
                                : game.FormatLocalTag("DamageFixedSingleFormatTime", value.Value);
                            if (!string.IsNullOrEmpty(phrase))
                                text = game.FormatLocalTag(s.Tag, " " + phrase, " " + phrase);
                        }
                    }
                    // A template with a placeholder is a whole line. One without
                    // is only the words — the game assembles the number and the
                    // duration around it — so it becomes our label instead.
                    if (text != null) { }
                    else if (GameData.HasPlaceholder(template))
                        text = game.FormatLocalTag(key, value.Value, hi ?? 0);
                    else if (!string.IsNullOrEmpty(template))
                        label = template.Trim();
                }

                if (text == null)
                {
                    string unit = s.Unit ?? (s.Percent ? "%" : "");
                    // The game's bare labels sometimes carry the per cent sign themselves
                    // ("% chance of fumbled attacks"), and then the number goes straight in
                    // front of it with no space.
                    string gap = " ";
                    if (label.StartsWith("%", StringComparison.Ordinal)) { unit = ""; gap = ""; }
                    if (isRange) text = Fmt(value.Value) + "–" + Fmt(hi.Value) + unit + gap + label;
                    else if (s.Pair != null || s.NoSign) text = Fmt(value.Value) + unit + gap + label;
                    else text = (value.Value > 0 ? "+" : "") + Fmt(value.Value) + unit + gap + label;
                }
                // A DOT'S DURATION GOES ON THE SAME LINE as the dot itself. The game prints
                // "+31% frostburn damage with +45% increased duration" while we gave two
                // consecutive lines: "+31% frostburn damage" and "+45% cold damage duration".
                // The numbers were right — but across two lines it is invisible what the
                // duration belongs to, and it belongs to its own dot (noticed by the user on one
                // weapon of `_Mivan`). The game prints the brackets consecutively too, as two.
                string durField = DurationSibling(s.Field);
                string durTail = "";
                if (durField != null)
                {
                    double? dv = Num(rec, durField, slot);
                    if (dv.HasValue && Math.Abs(dv.Value) > 0.0001)
                    {
                        double dRec = dv.Value, dg;
                        bool dDoubt = DoubtOf(durField);
                        if (!dDoubt && RolledOf(durField, out dg)) dv = dg;
                        // The words come from the game's `ImprovedTimeFormat` template ("with …
                        // Increased Duration"); we substitute our own number as a ready string,
                        // so a fractional one is not rounded. No game or no tag — the language
                        // file.
                        string dNum = (dv.Value > 0 ? "+" : "") + Fmt(dv.Value);
                        string dWith = game == null ? null : game.FormatTag("ImprovedTimeFormat", dNum);
                        text += " " + (string.IsNullOrEmpty(dWith) ? Lang.T("item.durationWith", dNum) : dWith);
                        durTail = RollRange(durField, dRec);
                        used.Add(durField);
                        doubt = doubt || dDoubt;
                    }
                }
                // "30% CHANCE OF …" — a prefix we did not have at all.
                //
                // The game prints a chance-based bonus as "30% chance of +58% pierce damage",
                // and without those words the line reads as a permanent addition, i.e. LIES by
                // multiples. The calculation had long accounted for the chance (it enters as an
                // expectation, `CharacterTotals.Add`) while the display had not. Noticed by the
                // user on one weapon of `_Mivan` and on the pet bonus of one item of `_Izaar`:
                // "10% chance of +1133% lightning damage".
                //
                // The chance itself is NOT rolled — it always equals the record's number, and
                // the game prints no bracket for it (measured 13 August, see Rolls()).
                // Except for the fields of a chance GROUP: there the chance stands once in the
                // "25% chance of:" header (AppendGlobalGroup), and repeating it on every line is
                // not what the game prints.
                string chanceField = CharacterTotals.ChanceSibling(s.Field);
                if (chanceField != null && !InGlobalGroup(rec, s.Field, slot))
                {
                    double ch = CharacterTotals.Chance(rec, chanceField, slot);
                    if (ch > 0 && ch < 100) text = Lang.T("item.chanceOf", Fmt(ch)) + " " + text;
                }
                // A paired stat rolls both ends; the game shows it as
                // "[(8-13)-(11-18)]" and that shape is not worked out yet.
                // The bracket always comes from the RECORD's number, not from the substituted
                // rolled one.
                if (!isRange) text += RollRange(s.Field, recValue, perTick);
                text += durTail;
                // "Rolled N ?" remains only where the field's place is guessed: the line then
                // holds the record's number, and without the note the computed one is invisible.
                if (doubt) text += RollGot(s.Field, s.Pair, isRange);

                // The duration that belongs to this line and nowhere else.
                List<string> from = new List<string>();
                from.Add(s.Field);
                if (s.Pair != null) from.Add(s.Pair);
                // A dot's duration is printed ON THIS SAME line ("with +45% increased duration"),
                // so the line is built out of it too. It used not to reach the list, and the edit
                // window could not find which line to attribute it to — the field drifted into
                // the "no line" tail.
                if (durField != null && !from.Contains(durField)) from.Add(durField);
                if (s.Attach != null)
                {
                    double? secs = Num(rec, s.Attach + "Min", slot);
                    double? secsHi = Num(rec, s.Attach + "Max", slot);
                    if (secs.HasValue && Math.Abs(secs.Value) > 0.0001)
                    {
                        text += " " + Lang.T("item.forSeconds", Fmt(secs.Value) +
                                (secsHi.HasValue && secsHi.Value > secs.Value + 0.0001
                                     ? "–" + Fmt(secsHi.Value) : ""));
                        used.Add(s.Attach + "Min");
                        used.Add(s.Attach + "Max");
                        from.Add(s.Attach + "Min");
                    }
                }

                // THE WEAPON-DAMAGE SHARE IS PRINTED PER HAND, AS IN THE GAME.
                //
                // The game does not write "400% weapon damage" — it writes "400% MAIN-hand
                // weapon damage" and, if the second hand takes part in the strike, "400% OFF-hand
                // weapon damage" as well. As many lines as there are hands, and that is the main
                // visible marker: one skill with two one-handers has exactly one line, and it is
                // about the off hand.
                //
                // Which hand it is, the skill's animation says (SkillHands, MECHANICS 14r), and
                // that depends on WHAT IS IN THE HANDS: with a two-hander the same skill prints
                // "main-hand". So the hands arrive here from outside — from whoever knows the
                // character — and without a character the old common line remains, naming no
                // hand.
                if (s.Field == "weaponDamagePct" && SkillHandsTwo && game != null)
                {
                    if (SkillHandsNow != Hands.Off)
                        lines.Add(HandTip(HandWords(game, "SkillWeaponDamageMainhandFormat",
                                                    value.Value, Lang.T("item.hand.main"), 0), indent));
                    if (SkillHandsNow != Hands.Main)
                        lines.Add(HandTip(HandWords(game, "SkillWeaponDamageOffhandFormat",
                                                    value.Value, Lang.T("item.hand.off"), 1), indent));
                    continue;
                }

                TipLine made = new TipLine(text, TipKind.Bonus, indent);
                // Over how many seconds the line's number is printed — see TipLine.PerTick.
                made.PerTick = perTick;
                // Remember which fields the line came from, so a view that sums
                // several records can show what went into this number.
                made.Explains = from.ToArray();
                made.Paired = isRange && s.Pair != null;
                made.Doubt = doubt && rolled != null && rolled.ContainsKey(s.Field);
                // The part is set ONLY where the rolled-number tally is live. Otherwise a set
                // bonus with the same field name would pass itself off as the base's field, and
                // the window would offer to edit something that is not in the stream.
                if (rolled != null) made.Part = linePart;
                lines.Add(made);
            }
        }

        static void AppendSkills(GameData game, DbRecord rec, List<TipLine> lines, int indent,
                                 CharacterContext who) { AppendSkills(game, rec, lines, indent, who, 0); }

        static void AppendSkills(GameData game, DbRecord rec, List<TipLine> lines, int indent,
                                 CharacterContext who, int slot)
        {
            for (int i = 1; i <= 4; i++)
            {
                string skill = rec.Get("augmentSkillName" + i);
                if (string.IsNullOrEmpty(skill)) continue;
                int level = (int)(Num(rec, "augmentSkillLevel" + i, slot) ?? 0);
                if (level <= 0 && slot > 0) continue;
                string name = SkillName(game, skill);
                string text = game == null ? null : game.FormatTag("ItemSkillIncrement", level, name);
                lines.Add(SkillLine((text ?? Lang.T("item.skillPlus", level, name)) + Mastery(game, skill),
                                    indent, game, skill, who));
            }
            for (int i = 1; i <= 3; i++)
            {
                string mastery = rec.Get("augmentMasteryName" + i);
                if (string.IsNullOrEmpty(mastery)) continue;
                int mlevel = (int)(Num(rec, "augmentMasteryLevel" + i, slot) ?? 0);
                if (mlevel <= 0) continue;
                // No mastery suffix here: the name on this line already is the mastery, and
                // "Nightblade (Nightblade)" reads as a bug.
                lines.Add(SkillLine(Lang.T("item.masteryPlus", mlevel, SkillName(game, mastery)),
                                    indent, game, mastery, who));
            }
            // "+N TO ALL SKILLS" is a field of its own, `augmentAllLevel`, and until 17 August
            // nobody printed it. Noticed by the user on one mask (`_Amakata`, bag 3): the game
            // writes "+1 to all skills" and we had no such line at all.
            //
            // The old check could not have caught it: `--edittest` guards the fields that ARE
            // ROLLED, and this field pulls no roll (the game prints no bracket for it). So a
            // check over ALL of a record's fields was created together with the fix —
            // `--saidtest`, threshold zero.
            //
            // The game's words: the tag `ItemAllSkillIncrement`.
            double all = Num(rec, "augmentAllLevel", slot) ?? 0;
            if (Math.Abs(all) > 0.0001)
            {
                string text = game == null ? null : game.FormatTag("ItemAllSkillIncrement", all);
                TipLine made = new TipLine(text ?? Lang.T("item.allSkillsPlus", Fmt(all)), TipKind.Skill, indent);
                made.Explains = new string[] { "augmentAllLevel" };
                lines.Add(made);
            }

            AppendSkillModifiers(game, rec, lines, indent, who);
            string granted = rec.Get("itemSkillName");
            if (!string.IsNullOrEmpty(granted))
            {
                // The game has no plain "grants skill" label — its own template
                // reads "to X", which means something else — so the skill is
                // shown on its own line with a marker instead.
                int fromGranted = lines.Count;
                lines.Add(SkillLine("» " + SkillName(game, granted) + Mastery(game, granted),
                                    indent, game, granted, who));
                // What the granted skill actually does. Only for skills the item
                // gives outright — a "+3 to <skill you already have>" line needs
                // no explanation and would double the length of every tooltip.
                string what = SkillDescription(game, granted);
                if (what != null) lines.Add(new TipLine(what, TipKind.Subtle, indent + 1));
                // The game prints a GRANTED skill's numbers the same way as a modifier's — with
                // the character's multipliers (MECHANICS 22k). That is also the old plan item
                // about one relic: the game showed "336 fire damage" where we had 55 — one rule,
                // not two different ones.
                Func<string, double> wasG = SkillDamageScale;
                SkillDamageScale = ScaleOf(who);
                try
                {
                    AppendSkillStats(game, rec, granted, lines, indent + 1);
                }
                finally { SkillDamageScale = wasG; }
                // A GRANTED SKILL OF A MASTERY THE CHARACTER LACKS is dimmed together with its
                // numbers. Usually an item grants a skill that works for everyone — such a skill
                // has no mastery at all and `Foreign` returns false, so that commonest case stays
                // coloured.
                Dim(lines, fromGranted, Foreign(granted, who));
            }
        }

        // The skills an item does not grant but CHANGES. `modifiedSkillName{i}`
        // names the skill, `modifierSkillName{i}` is the record holding what is
        // added to it. The game draws this as its own block — the skill's icon and
        // name, then the bonus lines under it — and we drew nothing at all, for
        // any item. It went unnoticed until the ascension, where the block is often the whole of
        // what the affix does: one ascended brigandine gives a single line, "+2% defensive
        // ability" to Spectral Binding, and the ascended-bonus heading stood over an empty
        // space.
        //
        // The numbers are the record's own, so they read like a skill's do: the game shows them
        // already multiplied by the character's bonuses.
        static void AppendSkillModifiers(GameData game, DbRecord rec, List<TipLine> lines, int indent,
                                         CharacterContext who)
        {
            for (int i = 1; i <= 8; i++)
            {
                string target = rec.Get("modifiedSkillName" + i);
                string mod = rec.Get("modifierSkillName" + i);
                if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(mod)) continue;
                int from = lines.Count;
                lines.Add(SkillLine("» " + SkillName(game, target) + Mastery(game, target),
                                    indent, game, target, who));
                DbRecord mr = game == null ? null : game.Record(mod);
                // A PET SKILL'S MODIFIER KEEPS ITS NUMBERS NOT IN ITSELF BUT IN THE NEXT RECORD.
                // The `SkillSecondary_PetModifier` class has no fields of its own at all — just a
                // `petSkillName` — and our block came out EMPTY: the skill's heading was there
                // with nothing under it. That is how, for instance, both lines of one pet totem
                // on one hood went missing.
                //
                // THE MEASUREMENT — the user's screenshots of 3 September, three numbers from two
                // items: "990 vitality damage" (`offensiveLifeMin 90`) and "12% reduced damage
                // for 2 s" on one, "restores 4% health" on the other. Their multiplier is THE
                // SAME as an ordinary modifier's: 90 × 11 = 990, and that is visible on the same
                // item — its other skill gives 120 × 11 = 1320, a number we printed correctly
                // even before.
                //
                // The chain is the same as `ConditionOf`'s: `buffSkillName` / `petSkillName` /
                // `skillBaseName`, no more than three steps.
                //
                // BOTH RECORDS ARE DRAWN, NOT ONLY THE LAST. The first attempt hopped along the
                // chain and printed only its end — and `--saidtest` went red at once: on eighteen
                // items the `cooldownTime` sits PRECISELY in the forwarding record (one relic
                // gives −6 s to a summon), and by hopping over it we lost that line. The check
                // worked exactly as intended.
                if (mr == null) continue;
                List<DbRecord> chain = new List<DbRecord>();
                chain.Add(mr);
                DbRecord step = mr;
                for (int hop = 0; hop < 3 && step != null &&
                     string.Equals(step.Class, "SkillSecondary_PetModifier",
                                   StringComparison.OrdinalIgnoreCase); hop++)
                {
                    string next = NextSkillRecord(step);
                    step = string.IsNullOrEmpty(next) ? null : game.Record(next);
                    if (step != null) chain.Add(step);
                }
                Func<string, double> was = SkillDamageScale;
                SkillDamageScale = ScaleOf(who);
                try
                {
                    foreach (DbRecord one in chain) RenderStats(game, one, lines, indent + 1, 0);
                }
                finally { SkillDamageScale = was; }
                foreach (DbRecord one in chain) RenderModifierExtras(game, one, lines, indent + 1);
                // A MASTERY THE CHARACTER LACKS IS DIMMED WHOLE, not just its heading line. Until
                // 17 August only the heading itself dimmed while the numbers under it stayed
                // green — and a modifier of a skill from a mastery the character does not have
                // read as working. Noticed by the user.
                Dim(lines, from, Foreign(target, who));
            }
        }

        // This character's display multiplier, or null when there is no character (the catalog,
        // an item window without a save) — then the record's numbers are printed, as before.
        static Func<string, double> ScaleOf(CharacterContext who)
        {
            if (who == null || who.SkillScale.Count == 0) return null;
            Dictionary<string, double> map = who.SkillScale;
            return delegate (string field)
            {
                double m;
                return map.TryGetValue(field, out m) ? m : 1;
            };
        }

        // Whether this mastery is foreign: the character has not a single point invested in it.
        static bool Foreign(string skillDbr, CharacterContext who)
        {
            string folder = MasteryFolder(skillDbr);
            return folder != null && who != null && !who.Masteries.Contains(folder);
        }

        // Dim the lines from `from` to the end — in the same grey as the heading. What is already
        // coloured (a set bonus the character does not have) is left alone.
        static void Dim(List<TipLine> lines, int from, bool yes)
        {
            if (!yes) return;
            for (int i = from; i < lines.Count; i++)
                if (lines[i].Override == Color.Empty) lines[i].Override = Color.FromArgb(120, 118, 112);
        }

        // Fields that occur only on a skill MODIFIER: there they are additions to a skill rather
        // than its own numbers — hence a table of their own, and hence the skill tree shows
        // `skillChanceWeight` as a plain percent. The words are the game's; the first two lines
        // were checked against a screenshot (screen-amakata-eq17.jpg).
        //
        // ON 2 SEPTEMBER THE LINES BECAME TEN INSTEAD OF TWO. `--saidtest` learned to ask
        // modifier records (before, it walked only the items' own fields) and named 20 kinds of
        // field the tooltip does not speak. Ten of them have a caption of their own in the game
        // — they are here; the rest have no caption at all, and that is worked through field by
        // field in the check's own `Silent` list.
        public static void RenderModifierExtras(GameData game, DbRecord rec, List<TipLine> lines, int indent)
        {
            AppendTable(game, rec, lines, indent, ModifierFacts, 0);
            AppendPetSpawn(game, rec, lines, indent);
            RenderSkillExtras(game, rec, lines, indent);
        }

        // "+1 pet" against "+2 pets" — THE GAME HAS TWO TAGS FOR THIS and picks between them by
        // the number: `SkillPetBurstSpawnMod` at one, `SkillPetBurstSpawnsMod` at anything else.
        // That is why this line is not in the table: there a tag is one per field. The numbers in
        // modifier records are 1, 2 and 4 (a census over 3590 records), so the case is no corner
        // case.
        static void AppendPetSpawn(GameData game, DbRecord rec, List<TipLine> lines, int indent)
        {
            double n = Num(rec, "petBurstSpawn") ?? 0;
            if (game == null || Math.Abs(n) < 0.0001) return;
            string text = game.FormatLocalTag(
                Math.Abs(n - 1) < 0.0001 ? "SkillPetBurstSpawnMod" : "SkillPetBurstSpawnsMod", n);
            if (string.IsNullOrEmpty(text)) text = Lang.T("item.summonCount", Fmt(n));
            TipLine made = new TipLine(text, TipKind.Bonus, indent);
            made.Explains = new string[] { "petBurstSpawn" };
            lines.Add(made);
        }

        // "(50% chance on critical attack) to reduce the active cooldown by 2 s" — one line
        // assembled from THREE fields of a modifier record: `refreshCooldownChance`,
        // `refreshCooldownTrigger` and `refreshCooldownAmount`. The template and the words are
        // entirely the game's (`tagSkillCooldownRefresh`), and the condition is the same twelve
        // `tagAutoSkillCondition` entries as for chance-based gear skills
        // (`SkillText.ConditionTag`), which is why it is taken from there rather than written
        // anew. An example is the set `set_d314_shadowstrike`.
        static void AppendCooldownRefresh(GameData game, DbRecord rec, List<TipLine> lines, int indent, int slot)
        {
            double amount = AtSlot(rec, "refreshCooldownAmount", slot);
            if (game == null || Math.Abs(amount) < 0.0001) return;
            string when = SkillText.TriggerWords(game, rec.Get("refreshCooldownTrigger"),
                                                 AtSlot(rec, "refreshCooldownChance", slot));
            if (when == null) return;
            // WHOSE COOLDOWN — IF IT IS NAMED. The game has TWO tags for this and picks between
            // them by whether the skill is named in `refreshCooldownSkill`: gear modifiers
            // usually do not name one ("reduce the active cooldown"), while mastery skills do —
            // "reduce the cooldown of (Rip and Tear)". Without the name the berserker's tooltip
            // read as being about the skill itself.
            string aim = rec.Get("refreshCooldownSkill");
            string aimName = string.IsNullOrEmpty(aim) ? null : SkillText.Name(game, aim);
            if (aimName != null && aimName.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase)) aimName = null;
            string text = aimName != null
                ? game.FormatLocalTag("tagSkillCooldownRefreshName", when, aimName, amount,
                                      game.Word("tagSeconds", Lang.T("item.unit.sec")))
                : game.FormatLocalTag("tagSkillCooldownRefresh", when, amount,
                                      game.Word("tagSeconds", Lang.T("item.unit.sec")));
            if (string.IsNullOrEmpty(text)) return;
            TipLine made = new TipLine(text, TipKind.Bonus, indent);
            made.Explains = new string[] { "refreshCooldownAmount", "refreshCooldownChance" };
            lines.Add(made);
        }

        // "48% chance when hit to reset the duration of (a skill) by 1 s (max 12 s)" — the same
        // construction as the cooldown reset but about DURATION, and with five fields:
        //
        //   refreshDurationSkill / Amount / Chance / Trigger / Max
        //
        // The game has four tags — with and without the skill's name, and with "reset" (when a
        // cap is named) or "increase" (when it is not). We take exactly the ones that fit the
        // fields present: otherwise the line is left with empty slots.
        static void AppendDurationRefresh(GameData game, DbRecord rec, List<TipLine> lines, int indent, int slot)
        {
            double amount = AtSlot(rec, "refreshDurationAmount", slot);
            if (game == null || Math.Abs(amount) < 0.0001) return;
            string when = SkillText.TriggerWords(game, rec.Get("refreshDurationTrigger"),
                                                 AtSlot(rec, "refreshDurationChance", slot));
            if (when == null) return;
            double max = AtSlot(rec, "refreshDurationMax", slot);
            string secs = game.Word("tagSeconds", Lang.T("item.unit.sec"));
            string aim = rec.Get("refreshDurationSkill");
            string aimName = string.IsNullOrEmpty(aim) ? null : SkillText.Name(game, aim);
            if (aimName != null && aimName.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase)) aimName = null;
            string text;
            if (aimName != null && max > 0.0001)
                text = game.FormatLocalTag("tagSkillDurationRefreshNameMax", when, aimName, amount, secs, max, secs);
            else if (aimName != null)
                text = game.FormatLocalTag("tagSkillDurationRefreshName", when, aimName, amount, secs, max, secs);
            else if (max > 0.0001)
                text = game.FormatLocalTag("tagSkillDurationRefreshMax", when, amount, secs, max, secs);
            else
                text = game.FormatLocalTag("tagSkillDurationRefresh", when, amount, secs, max, secs);
            if (string.IsNullOrEmpty(text)) return;
            TipLine made = new TipLine(text, TipKind.Bonus, indent);
            made.Explains = new string[] { "refreshDurationAmount", "refreshDurationChance" };
            lines.Add(made);
        }

        // THE SAME AS `RenderModifierExtras`, BUT FOR TREE SKILLS.
        //
        // The lines about cooldown and duration resets were assembled only for modifiers FROM
        // GEAR, and nobody showed them for masteries: on the berserker that is three tooltips out
        // of four (Recklessness, Everwinter, Beast Form). The numbers themselves do go into the
        // calculation — only the words were missing.
        public static void RenderSkillExtras(GameData game, DbRecord rec, List<TipLine> lines, int indent)
        { RenderSkillExtras(game, rec, lines, indent, 0); }

        // `slot` IS THE STEP BY THE SKILL'S LEVEL, and without it all of these lines were printed
        // from level one: Recklessness came out as "8% chance" instead of 22, another skill as
        // "8% health" instead of 15, and Beast Form as "max 4 s" instead of 12. The error is
        // visible only beside a screenshot of the game — the numbers look plausible by
        // themselves, because they are real, just not the right ones.
        public static void RenderSkillExtras(GameData game, DbRecord rec, List<TipLine> lines,
                                             int indent, int slot)
        {
            AppendActivationChance(game, rec, lines, indent, slot);
            AppendLifePercentBuff(game, rec, lines, indent, slot);
            AppendCooldownRefresh(game, rec, lines, indent, slot);
            AppendDurationRefresh(game, rec, lines, indent, slot);
        }

        // A field's number at the required step. `Num` reads the first one, which is enough for a
        // gear modifier (it has no steps) but not for a tree skill.
        static double AtSlot(DbRecord rec, string field, int slot)
        { return AttackSkill.Value(rec, field, slot); }

        // "100% activation chance" belongs to a passive that fires on a strike
        // (`Skill_PassiveOnCritBuffSelf` and kin). Printed ONLY in a tree skill's tooltip: for a
        // gear skill the game states the same chance in the condition's heading ("15% chance on
        // attack"), and a second line would be a repetition.
        static void AppendActivationChance(GameData game, DbRecord rec, List<TipLine> lines, int indent, int slot)
        {
            double n = AtSlot(rec, "onHitActivationChance", slot);
            if (game == null || n <= 0.0001) return;
            TipLine made = new TipLine(Fmt(n) + "% " + Word(game, "SkillActivationChance", Lang.T("item.activationChance")),
                                       TipKind.Bonus, indent);
            made.Explains = new string[] { "onHitActivationChance" };
            lines.Add(made);
        }

        // "Restores 15% health per second" has a line of its own in the game
        // (`tagBonusLifePercentBuff`), and its field is named unexpectedly:
        // `skillLifePercentBuffDuration`. The name deceives — it is not a duration but the
        // percent itself; the duration sits in `skillActiveDuration` nearby.
        static void AppendLifePercentBuff(GameData game, DbRecord rec, List<TipLine> lines, int indent, int slot)
        {
            double n = AtSlot(rec, "skillLifePercentBuffDuration", slot);
            if (game == null || Math.Abs(n) < 0.0001) return;
            string text = game.FormatLocalTag("tagBonusLifePercentBuff", n);
            if (string.IsNullOrEmpty(text)) return;
            TipLine made = new TipLine(text, TipKind.Bonus, indent);
            made.Explains = new string[] { "skillLifePercentBuffDuration" };
            lines.Add(made);
        }

        // " (Mastery)" after a skill bonus, naming the mastery it belongs to.
        static string Mastery(GameData game, string skillDbr)
        {
            string name = MasteryName(game, MasteryFolder(skillDbr));
            return name == null ? "" : "  (" + name + ")";
        }

        // A bonus to a mastery the character never took does nothing for them.
        // The game greys such a line out rather than annotating it, so we do the
        // same: it reads as inactive without adding words.
        static TipLine SkillLine(string text, int indent, GameData game, string skillDbr, CharacterContext who)
        {
            TipLine line = new TipLine(text, TipKind.Skill, indent);
            string folder = MasteryFolder(skillDbr);
            if (folder != null && who != null && !who.Masteries.Contains(folder))
                line.Override = Color.FromArgb(120, 118, 112);
            return line;
        }

        // What the granted skill does in numbers — "162 acid damage over 2s" and
        // the like. A skill record carries the same offensive/defensive fields an
        // item does, so the ordinary stat table renders it unchanged.
        //
        // Two things differ. The numbers are per skill level ("162;175"), indexed
        // by the level the item grants; and the record the item points at is often
        // only a controller, with the numbers on the skill it triggers.
        static void AppendSkillStats(GameData game, DbRecord item, string dbr, List<TipLine> lines, int indent)
        {
            if (game == null) return;
            int level = (int)(Num(item, "itemSkillLevel") ?? 1);
            if (level < 1) level = 1;

            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(dbr); hop++)
            {
                DbRecord r = game.Record(dbr);
                if (r == null) return;

                int max = (int)(Num(r, "skillMaxLevel") ?? 1);
                List<TipLine> found = new List<TipLine>();
                // THE WHOLE SET OF A SKILL'S LINES IS DRAWN, NOT ONE STAT TABLE.
                //
                // `AppendStats` used to stand here, and it knows nothing of "energy cost /
                // cooldown / radius / duration" (`SkillFacts`), nor of "restores N% health"
                // (`AppendRestore`), nor of a skill's flat armour. A whole class of granted skills
                // has no damage fields of its own at all — and the block came out EMPTY: a name,
                // a description, no numbers. Found by the user on 3 September on one pair of legs:
                // its granted skill is a `SkillBuff_Passive` with a single `skillLifePercent 8;10`,
                // and before the fix nothing was printed under it.
                //
                // The game does print these lines — visible in a tooltip from the same batch of
                // screenshots: under one granted skill it shows "280 energy cost" and "30 s skill
                // recharge", i.e. exactly `SkillFacts`.
                RenderStats(game, r, found, indent, Math.Max(0, Math.Min(level, max) - 1));
                if (found.Count > 0) { lines.AddRange(found); return; }

                dbr = FirstOf(r, "buffSkillName", "petSkillName", "skillBaseName");
            }
        }

        // The skill's own description text. A granted skill is sometimes a
        // controller whose record only points at the skill that does the work, so
        // one step of indirection is followed before giving up.
        static string SkillDescription(GameData game, string dbr)
        {
            if (game == null || string.IsNullOrEmpty(dbr)) return null;
            for (int hop = 0; hop < 2; hop++)
            {
                DbRecord r = game.Record(dbr);
                if (r == null) return null;
                string text = game.Tag(r.Get("skillBaseDescription"));
                if (!string.IsNullOrEmpty(text)) return Shorten(text);
                dbr = FirstOf(r, "buffSkillName", "petSkillName", "skillBaseName");
                if (string.IsNullOrEmpty(dbr)) return null;
            }
            return null;
        }

        // Descriptions are a sentence or two, but a few run long enough to make
        // the description taller than the window.
        static string Shorten(string s)
        {
            s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (s.Length <= 300) return s;
            int cut = s.LastIndexOf(' ', 300);
            return s.Substring(0, cut > 200 ? cut : 300).TrimEnd(' ', ',', '.') + "…";
        }

        // Some skills keep their name on the buff they apply rather than on the
        // record the item points at, so the same one hop is followed here — it is
        // why one bonus used to read "+3 to skill pox1.dbr".
        static string SkillName(GameData game, string dbr)
        {
            string first = dbr;
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(dbr); hop++)
            {
                DbRecord r = game.Record(dbr);
                if (r == null) break;
                string n = game.Tag(r.Get("skillDisplayName"));
                if (!string.IsNullOrEmpty(n)) return n;
                dbr = FirstOf(r, "buffSkillName", "petSkillName", "skillBaseName");
            }
            return ShortPath(first);
        }

        static double? Num(DbRecord rec, string field) { return Num(rec, field, 0); }

        // A set record stores each bonus as a semicolon list, one entry per number
        // of pieces worn: "0;24;24;24" is nothing for one piece and 24 from two
        // onwards. An ordinary item record has a single value, which is the same
        // thing read at index zero.
        static double? Num(DbRecord rec, string field, int index)
        {
            string v = rec.Get(field);
            if (string.IsNullOrEmpty(v)) return null;
            string[] parts = v.Split(';');
            // Past the end of the table the last entry repeats: a devotion skill
            // levels itself to 25 while its record only lists ten steps, and the
            // game keeps showing the tenth. Returning nothing dropped the line.
            string one = parts[Math.Min(Math.Max(0, index), parts.Length - 1)];
            double d;
            return double.TryParse(one, NumberStyles.Float, CultureInfo.InvariantCulture, out d)
                ? (double?)d : null;
        }

        static string Fmt(double v)
        {
            if (Math.Abs(v - Math.Round(v)) < 0.005) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
