using System;
using System.Collections.Generic;
using System.Globalization;
using Gdc;

namespace Gdc.Gui
{
    // DAMAGE TIMELINE, stage E1: event queue, autoattack only, dummy target.
    //
    // NOT ONE line about windows here: the engine must also run in the battery, which
    // has no window at all (PLAN, K2). It does not compute damage itself — it calls the
    // same `DamageModel` as the panel, but with `HitRoll` (PLAN, M: user's decision).
    //
    // SHAPE THAT COULD NOT BE REDONE CHEAPLY, laid in from the start:
    //   • EVENT QUEUE, not a loop step: every event schedules the next one and takes
    //     the interval from the speed AT THAT MOMENT (E6 — buffs on the fly);
    //   • the event is a SWING, not an autoattack: at E4 a weapon-pool skill REPLACES
    //     the autoattack, and that is decided inside the swing;
    //   • PARTICIPANT, not character: at E7 pets arrive as their own streams (E.8);
    //   • FIRM ORDER within one moment, else the run is not repeatable;
    //   • a damage instance has a SOURCE — dots will need it at E5.
    public enum Swing { Both = 0, Main = 1, Off = 2 }

    // The target. At E1 it is the dummy: zero resistances to the usual types, but it
    // does have armour, and armour only cuts physical (MECHANICS 32).
    public sealed class TimelineTarget
    {
        public string Name = Lang.T("log.target.dummy");
        public double DefensiveAbility;
        public double Armour;
        public double Absorb = 70;

        // Target resistances by our type names ("Fire", "LifeDot" and so on), in
        // percent. The dummy has none at all; a real enemy does, and without them the
        // run shows damage that never happens.
        //
        // Resistance reduction is done IN FULL (9 September): all three kinds, from all
        // sources — auras, procs, skills, gear. Fields below: `Resist` is the main kind,
        // `RelativeCut` the relative one, `AbsoluteCut` the flat one.
        public readonly Dictionary<string, double> Resist =
            new Dictionary<string, double>(StringComparer.Ordinal);

        // TARGET RACE and our bonus against it — task item 6 ("+% damage to Chthonics").
        // `Race` is the game code (`Race004`), `RacialPercent` the sum of our percents
        // against THIS race only, already filtered: bonuses against other races do not
        // apply to this target at all.
        //
        // THE DUMMY HAS NO RACE — so on the dummy this bonus gives nothing, and it has
        // to be checked on a real target. Not an engine caveat but a property of the
        // dummy itself: its `characterRacialProfile` is empty.
        public string Race = "";
        public double RacialPercent;

        // TARGET LIFE AND ITS "PERCENT LIFE REDUCTION" RESISTANCE — for damage as a
        // percent of current life (Annihilation, Blood Pox) and for TTK (MECHANICS 77).
        // The resistance can exceed one hundred: 60…98 by level on heroes and bosses,
        // 500 on the dummy, which makes it immune to that damage. Life matches the game
        // to the unit, nemesis included (Avatar of Mogdrogen, MECHANICS 89l): the old
        // "off by 3–5× on some superbosses" was a comparison against the wrong record.
        // One open case: Balok'Anatu (−10% on two levels, and one shot ×1.12 on top —
        // MECHANICS 90e).
        public double Life, PercentLifeResist;
        // The dummy DOES NOT DIE in game — it has a low-health shield
        // (`targetdummy_lowhealthshield_01`). Its TTK is nominal.
        public bool IsDummy;

        // Flat racial bonus ("+40 damage to Chthonics"). COLLECTED BUT NOT APPLIED, and
        // we must not lie about it — kept here so the run can say so out loud. Why not
        // applied: TWO unknowns, not one — which damage type it arrives as (the tooltip
        // does not name it) and where it sits in the order. The percent one we place by
        // the article's formula; for the flat one there is no formula, in the article or
        // in the files. Guessing by analogy has backfired three times (MECHANICS 48), so
        // it is a measurement or nothing.
        public double RacialFlat;

        // FLAT RESISTANCE REDUCTION — "20 reduced resistances", no percent. Subtracted
        // as a NUMBER from the current resistance of any type, and a negative one is no
        // obstacle: on the dummy's zero it yields −20%, i.e. ×1.20.
        //
        // Measured 9 September: Madness (20 reduction) lifted a fire dot 419 → 503,
        // exactly ×1.2005 (MECHANICS 69).
        //
        // KEPT SEPARATE FROM `Resist` instead of being subtracted into it, for two
        // reasons: equal ones DO NOT STACK (the strongest applies — MECHANICS 7), and
        // applied AFTER the additive kind but BEFORE the percent one (MECHANICS 113; it
        // stood last until 7 October). Mix it into the additive kind and both are lost.
        public double AbsoluteCut;

        // TARGET ARMOUR CUT — as a number, the strongest of those applied
        // (MECHANICS 89e). The defence equation takes armour net of it, floored at zero.
        public double ArmorCut;

        // RELATIVE reduction, in percent: cuts a percent OF THE CURRENT resistance,
        // whatever it is, negative included. The article's formula is
        // `res − p × |res|`: 80% with a 30% cut becomes 56%, and −73% becomes −91%.
        //
        // Useful only where the enemy either has plenty of resistance or it is already
        // driven negative by the main kind. On the dummy's zero it gives NOTHING —
        // checked three times (MECHANICS 41, 67).
        public double RelativeCut;

        // `--rrbreak`: put the two cuts back in the pre-7-October order (percent first,
        // flat last) so that `--rrtest` can be seen going red. Nothing else reads it.
        public static bool BreakCutOrder;

        // `--dummybare`: leave the training dummy without its resistances, the way it
        // stood before 8 October, so the guard over them can be seen going red.
        public static bool BreakDummyResists;

        // `--chargesbreak`: press a skill once per cooldown again, knowing nothing of
        // `cooldownCharges` or of the chance to reset the cooldown — the way the queue
        // stood before 16 October (MECHANICS 151). Nothing else reads it.
        public static bool BreakCharges;

        // How much damage of this type reaches the target.
        public double Through(string type, double damage)
        { return Through(type, damage, true); }

        // `racial` — whether to count OUR bonus against the target's race. A pet has
        // none: "+15% damage to Chthonics" from our gear is ours and does not apply to
        // the pet, which has its own (`PetFields`). See MECHANICS 78e.
        public double Through(string type, double damage, bool racial)
        {
            if (damage <= 0) return damage;
            // THE RACIAL BONUS IS ITS OWN MULTIPLIER, AND IT GOES FIRST.
            //
            // The game's order (MECHANICS 7b):
            //     ... × (1 + % of type + % from the attribute) × (1 + % vs race)
            //         × resistance reduction × crit multiplier
            //
            // So it is NOT a term of the common percent sum: 1500% of type and 50% vs
            // race give ×16 × ×1.5, not ×16.5.
            //
            // WHAT IS KNOWN AND WHAT IS INFERRED. Known: it stands BEFORE resistance
            // reduction — that is the formula. Inferred: therefore before armour too,
            // because it multiplies OUR damage, while armour and resistances are the
            // target's defence, and that is where the two sides split. Against the
            // reduction the order does not even matter (multiplication commutes); against
            // ARMOUR it does, since armour subtracts rather than multiplies.
            if (racial && RacialPercent != 0) damage *= 1 + RacialPercent / 100.0;
            // Armour cuts ONLY physical — that is how the game's equation reads
            // (`physicalDamageDV`, MECHANICS 32). It does not touch dots at all.
            // The target's armour cut (MECHANICS 89e) is a number, floored at zero.
            if (type == "Physical") damage = Combat.AfterArmour(damage, Math.Max(0, Armour - ArmorCut), Absorb);
            double r;
            Resist.TryGetValue(type, out r);
            // ORDER OF THE THREE KINDS. The flat one goes BEFORE the percent one, and
            // the percent one is LAST of all (MECHANICS 113):
            //
            //   additive   "-32% fire resistance"        already summed into `Resist`, stacks with everything
            //   flat       "24 reduced target's res"     subtracted as a number, strongest source wins
            //   percent    "20% reduced target's res"    percent OF WHAT IS LEFT, strongest wins, LAST
            //
            // The worked example the order is taken from:
            //
            //     90 - 40%  additive = 50
            //     50 - 40   flat     = 10
            //     10 - 10%  percent  = 9      <- a tenth of 10, not of 50
            //
            // Being last is what makes the percent kind weak: the first two kinds have
            // already eaten the resistance it would take its percentage of.
            //
            // IT STILL WORKS BELOW ZERO. By itself the percent kind cannot push a
            // positive resistance under zero (`r - p*r` keeps the sign), but once the
            // first two have driven it negative it goes on cutting: -10 with 11% gives
            // -11.1. Hence `Math.Abs(r)` rather than a clamp at zero.
            //
            // WAS THE OTHER WAY ROUND UNTIL 7 OCTOBER — percent before flat, taken from
            // the article (MECHANICS 7). `--rrbreak` puts that order back, so the guard
            // over this can be seen red.
            if (BreakCutOrder)
            {
                if (RelativeCut > 0.0001) r -= RelativeCut / 100.0 * Math.Abs(r);
                r -= AbsoluteCut;
            }
            else
            {
                r -= AbsoluteCut;
                if (RelativeCut > 0.0001) r -= RelativeCut / 100.0 * Math.Abs(r);
            }
            if (Math.Abs(r) > 0.0001) damage *= 1 - r / 100.0;
            return damage;
        }

        // A target from the enemy table — the same one the Target tab uses.
        public static TimelineTarget Of(GameData game, string name, EnemyStats s)
        {
            TimelineTarget t = new TimelineTarget();
            t.Name = name ?? Lang.T("log.target.generic");
            t.Absorb = Combat.BaseAbsorb(game);
            if (s == null) return t;
            t.Race = s.Race ?? "";
            t.Life = s.Life;
            double plr;
            if (s.Resist.TryGetValue("defensivePercentCurrentLife", out plr)) t.PercentLifeResist = plr;
            t.DefensiveAbility = s.DefensiveAbility;
            t.Armour = s.Armour;
            CopyResists(t, s);
            return t;
        }

        // THE TARGET'S RESISTANCES, from the game's field names into our type names.
        //
        // ONE PLACE FOR BOTH TARGETS, and it has to be: until 8 October the training
        // dummy went through `Dummy()`, which copied the defensive ability, the armour
        // and the life — AND NOT THIS. So the dummy came out with zero resistance to
        // every type, and every number measured on it was out by its resistance.
        //
        // The user's measurement that found it: Siphon Souls hits the dummy on
        // Ascendant for 4818 before the target, and the game showed 4240 — exactly
        // 4818 × 0.88, i.e. the dummy's +12% vitality resistance (MECHANICS 114).
        static void CopyResists(TimelineTarget t, EnemyStats s)
        {
            if (t == null || s == null) return;
            // Elemental resistance adds to fire, cold and lightning — as in game.
            foreach (KeyValuePair<string, double> kv in s.Resist)
            {
                if (!kv.Key.StartsWith("defensive", StringComparison.Ordinal)) continue;
                string type = kv.Key.Substring("defensive".Length);
                if (type == "ElementalResistance")
                {
                    foreach (string one in new[] { "Fire", "Cold", "Lightning" }) Add(t.Resist, one, kv.Value);
                    continue;
                }
                if (type.EndsWith("Resistance", StringComparison.Ordinal))
                    type = type.Substring(0, type.Length - "Resistance".Length);
                Add(t.Resist, type, kv.Value);
                // A dot of the same root is cut by the same resistance.
                Add(t.Resist, type + "Dot", kv.Value);
            }
        }

        static void Add(Dictionary<string, double> into, string key, double value)
        {
            double had;
            into.TryGetValue(key, out had);
            into[key] = had + value;
        }

        // The dummy comes wholly from the game's files, not from numbers in code
        // (rule 7): `trainingdummy.dbr` → defensive ability from its bio record, armour
        // from the passive skill `armorbase04` at its level.
        // The dummy's level is `charLevel*1+2`, i.e. from where it stands; our 15 is not
        // invented but follows from three crit measurements on `_Itaan`
        // (PLAN 3.2c: defensive ability 100 = 15×5+25).
        public static TimelineTarget Dummy(GameData game, int level)
        { return Dummy(game, level, -1); }

        // `difficulty` — 0 normal, 1 elite, 2 ultimate, −1 no adjustment. The difficulty
        // pack CHANGES the target's defensive ability, and that is no trifle: on
        // 6 September the game showed 89.3% to hit the dummy while the bare record gave
        // 88.2% (MECHANICS 33). So the target is resolved by THE SAME enemy table as in
        // the damage window — it knows about packs; reading the record ourselves stays
        // as a fallback for when the table is not built.
        public static TimelineTarget Dummy(GameData game, int level, int difficulty)
        {
            TimelineTarget t = new TimelineTarget();
            t.Absorb = Combat.BaseAbsorb(game);

            string cache = EnemyTable.CachePath;
            // THE ENEMY TABLE IS TAKEN READY OR BUILT RIGHT HERE, the way the button on the
            // "Target" tab does it. Before, we only READ the file, and that had two faults,
            // both of them silent (MECHANICS 115):
            //   no file       → the fallback branch below, and its target is a DIFFERENT one:
            //                   defensive ability 535 instead of 1821, no difficulty pack and
            //                   no resistances at all;
            //   a stale file  → read as if it were good. The table carried no signature of the
            //                   game, so after the patch of 8 October the run counted its
            //                   target from the table of 16 September — every other cache
            //                   rebuilt itself, this one did not.
            foreach (Enemy e in EnemyTable.Ready(game, cache, localizeNames: false))
                {
                    if (!e.Dummy || e.Path.IndexOf("trainingdummy.dbr",
                                                   StringComparison.OrdinalIgnoreCase) < 0) continue;
                    EnemyStats s = EnemyTable.Resolve(game, e, level, difficulty);
                    // The cached name is in the language it was built in; re-read it now.
                    EnemyTable.Rename(game, new List<Enemy> { e });
                    t.Name = string.IsNullOrEmpty(e.Name) ? t.Name : e.Name;
                    t.DefensiveAbility = s.DefensiveAbility;
                    t.Armour = s.Armour;
                    t.Life = s.Life;
                    t.IsDummy = true;
                    double dplr;
                    if (s.Resist.TryGetValue("defensivePercentCurrentLife", out dplr)) t.PercentLifeResist = dplr;
                    // AND THE RESISTANCES — missing here until 8 October, which left the
                    // dummy immune to the mechanics and bare to every damage type at once.
                    // On Ascendant it has Life 12, Bleeding 9, Poison 10, Fire 8 and so on,
                    // so every number measured on it was out by that much (MECHANICS 114).
                    if (!BreakDummyResists) CopyResists(t, s);
                    return t;
                }

            // WE GET HERE ONLY IF THERE IS NO ENEMY TABLE NEXT TO US — and the target is
            // then a DIFFERENT one: no full defence equation, no difficulty adjustment.
            // The name stays the short one, not the full dummy name, so that this shows
            // in the report: a side build without `enemies.gdd` had its numbers drift,
            // and half an hour went into comparing the incomparable.
            DbRecord dummy = game.Record("records/creatures/enemies/special/trainingdummy.dbr");
            if (dummy == null) return t;

            DbRecord bio = game.Record(dummy.Get("characterAttributeEquations") ?? "");
            t.DefensiveAbility = Eval(bio == null ? null : bio.Get("characterDefensiveAbility"), level);

            // Armour sits in a passive skill, one step per dummy level.
            for (int i = 1; i <= 8; i++)
            {
                DbRecord skill = game.Record(dummy.Get("skillName" + i) ?? "");
                string steps = skill == null ? null : skill.Get("defensiveProtection");
                if (string.IsNullOrEmpty(steps)) continue;
                string[] parts = steps.Split(';');
                int at = level - 1;                       // level 1 is the first step
                if (at < 0) at = 0;
                if (at >= parts.Length) at = parts.Length - 1;
                double v;
                if (double.TryParse(parts[at], NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    t.Armour += v;
            }
            return t;
        }

        // `(charLevel*5)+25` — what `EnemyTable.Eval` does, but without the whole enemy
        // table: that takes long to build, and only one dummy is needed.
        //
        // Public because the same thing computes a monster's attributes when the two
        // readings of attack and defensive ability are compared (`--enemyshot`,
        // MECHANICS 34).
        public static double Eval(string equation, int level)
        {
            if (string.IsNullOrEmpty(equation)) return 0;
            string s = equation.Replace("charLevel", level.ToString(CultureInfo.InvariantCulture));
            double v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return Arithmetic(s);
        }

        // Four operations and parentheses are enough — these records hold nothing else.
        static double Arithmetic(string s)
        {
            int at = 0;
            double v = Sum(s, ref at);
            return v;
        }

        static double Sum(string s, ref int at)
        {
            double v = Product(s, ref at);
            while (at < s.Length && (s[at] == '+' || s[at] == '-'))
            {
                char op = s[at++];
                double r = Product(s, ref at);
                v = op == '+' ? v + r : v - r;
            }
            return v;
        }

        static double Product(string s, ref int at)
        {
            double v = Atom(s, ref at);
            while (at < s.Length && (s[at] == '*' || s[at] == '/'))
            {
                char op = s[at++];
                double r = Atom(s, ref at);
                v = op == '*' ? v * r : (Math.Abs(r) < 1e-9 ? 0 : v / r);
            }
            return v;
        }

        static double Atom(string s, ref int at)
        {
            while (at < s.Length && s[at] == ' ') at++;
            if (at < s.Length && s[at] == '(')
            {
                at++;
                double v = Sum(s, ref at);
                if (at < s.Length && s[at] == ')') at++;
                return v;
            }
            int from = at;
            while (at < s.Length && (char.IsDigit(s[at]) || s[at] == '.' || s[at] == '-' && at == from)) at++;
            double got;
            double.TryParse(s.Substring(from, at - from), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out got);
            return got;
        }
    }

    public sealed class TimelineOptions
    {
        public double Seconds = 5;      // queue length, input field (item 10)
        public int Seed = 1;            // the run must be repeatable (PLAN, I)
        // DUMMY LEVEL IS 102. The user, 5 September: "the training dummy is level 102
        // for us, in case it matters", and that agrees with the record (`charLevel*1+2`
        // at level 100) and with his own measurement: four misses out of seventeen do
        // happen at 88% to hit and do not at 98.5%.
        // The dummy's level IS NOT CONSTANT — it comes from where it stands; hence this
        // is an argument, not a number in the calculation.
        public int DummyLevel = 102;
        // Difficulty: 0 normal, 1 elite, 2 ultimate, −1 no pack adjustment.
        //
        // NORMAL BECAUSE THE USER SAID SO, not because a number matched. On 6 September
        // the game showed "89.3% chance to hit" the dummy at attack 1822, and our
        // numbers fell out like this:
        //   no pack        defence 535     88.21%
        //   normal         defence 454.75  90.08%   ← the character plays HERE
        //   elite/ultimate defence 492.2   89.20%   ← where our number "lands"
        // The temptation to set ultimate and declare agreement is exactly what rule 15
        // forbids: turning SOMEONE ELSE'S knob to make a number match. Normal stands,
        // and the 0.8-point gap is recorded open (MECHANICS 33).
        public int Difficulty = 0;
        public bool LogEveryHit = true;
        // Whether a dot's source counts per HAND. Off by default: our only measurement
        // says "two identical swords = one source" (PLAN 5.4). The knob is kept so the
        // difference can be seen as a number once measurement D1 is done.
        public bool DotSourceByHand;

        // WHAT THE USER PRESSES — records of the skills that are ticked.
        //
        // The rule is simple and comes from `timeline.txt`, item 1: "every selected skill
        // is pressed as soon as its cooldown is up", and "at the start all selected ones
        // may be taken as pressed at once". Auras are NOT here — they are chosen in their
        // own panel (5.0a).
        public readonly List<string> Pressed = new List<string>();

        // For skills WITHOUT a cooldown: after how many seconds to press again.
        // Key is the skill record. No entry — the skill is pressed once, as before.
        public readonly Dictionary<string, double> PressEvery =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // WHAT THE DEFAULT ATTACK STRIKES WITH — the assigned skill's record, empty for
        // the weapon.
        //
        // Savagery, Cadence and Righteous Fervor (`Skill_WeaponPool_*`) are not part of
        // the chance pool: they are PUT on the attack button, which is the player's
        // choice, not a roll. The save knows what lies on the skill bar (`_Jien` has both
        // Savagery and Weapon Attack there) but NOT what is on the left button right now:
        // `SkillSettingValid` = 0. So we ask, as with the pressed ones.
        public string Attack = "";

        // BUFFS THAT GO INTO THE QUEUE INSTEAD OF HANGING PERMANENTLY.
        //
        // The user's decision of 8 September, and it is more accurate than the old one:
        // "see, it lasts only 5 seconds and recharges in 27.9 — holding it up permanently
        // would be wrong. Let us do them like the damaging skills: if its checkbox is on,
        // we put it in the queue, write a line that it activated, for its duration it acts
        // as a buff, then it cannot be raised again until the cooldown expires, and then
        // we activate it again."
        //
        // Here go the ones whose CONDITION we cannot model ("at low health", "when we are
        // hit"): the condition itself is not rolled, but the duration and the cooldown are
        // real. Ghoulish Hunger: 5 seconds out of 27.9, i.e. about 18% of the run, not
        // 100%.
        public readonly List<string> TimedBuffs = new List<string>();

        // WHICH PETS THE USER SUMMONS — records of the ticked summon skills. The user,
        // 12 September: "either the pet is summoned by the user (a checkbox in the
        // available skills, so as not to summon them all), or… a summon trigger fired."
        // Empty — we take those ticked in the pets window, so the two windows do not
        // disagree.
        public readonly List<string> Pets = new List<string>();

        // THE WINDOW HAS MADE THE CHOICE. An empty list then means "summon nobody", not
        // "ask the pets window": the tick was cleared, so there must be no pet.
        public bool PetsChosen;

        // PROGRESS: called once per elapsed SECOND of the run, not per log line.
        //
        // The user's request of 8 September — "so the user is not left staring at an
        // empty screen for many seconds". Per line would be truer in spirit and dearer in
        // practice: sixty seconds give about two thousand lines, and redrawing the window
        // for each would cost more than the calculation itself. A second of the run means
        // sixty refreshes instead of two thousand, and the eye is content with that.
        public Action<double> OnSecond;
        // EVERY NEW LOG LINE GOES OUT AT ONCE. The user, 11 September: "so the user sees
        // each added line immediately instead of waiting for everything to appear at
        // once". The window itself decides how often to redraw.
        public Action<TimelineLine> OnLine;
    }

    // LOG LINE KIND. The window colours the line by it, and the check picks out ticks by
    // it. Until 17 September both looked at the first word of the text ("tick", "proc",
    // "miss"…): with the log in another language the window would go one colour and the
    // tick check would be empty yet green (PLAN 7c, stage 4). The kind is set by whoever
    // creates the line.
    //   Plain     swing, stream, spell, aura, summon, pet, expired, cooldown reset
    //   Miss      a miss — ours, a skill's, a pet's
    //   Tick      a dot tick
    //   Silent    fired without damage
    //   Devotion  a devotion star
    //   Proc      fired by itself (a proc)
    //   Pool      weapon pool, "instead of the attack"
    //   Pressed   a pressed skill, pressed without damage, a buff
    //   Life      the target's life dropping and its death
    public enum LogKind { Plain, Miss, Tick, Silent, Devotion, Proc, Pool, Pressed, Life }

    // WHAT A SKILL THAT DOES NOT STRIKE ITSELF DOES (`Timeline.SilentKind`), and "damage"
    // for a striking star in the press list. Until 17 September these were Russian words
    // that were both compared (in the engine, the census and the check) and printed. Now
    // the enum is compared and `Timeline.EffectWord` from the language file is printed.
    public enum EffectKind { Damage, Summon, Buff, Debuff, None }

    // One log line. It prints the REASON, not just the number: the source, the roll with
    // its threshold, the refusal. This is how the user checks the order by eye (PLAN, Z2).
    public sealed class TimelineLine
    {
        public double At;
        public LogKind Kind;
        public string What = "";        // "swing", "miss"
        public string Why = "";         // "roll 43 < 138.6"
        public double Damage;           // after the target, both hands together
        public double Before;           // before reduction: with crit, without race, armour, resists

        // EACH HAND'S DAMAGE SEPARATELY, not their sum. The user, 6 September: "when you
        // write a line 'strikes with both hands', you have one number, and there should be
        // two, from both hands; it must not be a sum." This is not a quibble about display:
        // the game prints TWO figures over the target at once (MECHANICS 31), and the log
        // must look the same — otherwise it cannot be checked by eye against the game.
        public readonly double[] Hand = new double[2];
        public readonly bool[] Hit = new bool[2];
        // "BEFORE REDUCTION" IS PER HAND TOO. The user's question of 11 September about a
        // line reading "damage 1000, to target 800" exposed two faults in the bracket.
        // First: the hands' numbers stand apart while the bracket was their SUM — "1000 and
        // 900 (to target 2100)". Second: "to target" reads as "reached the target", i.e.
        // the other way round. Now the bracket holds as many numbers as there are hands,
        // and the wording is his own from the task (item 9): "both damage figures BEFORE
        // reduction and AFTER".
        public readonly double[] HandBefore = new double[2];
        // A tick carries the dot's TYPE ("FireDot"). The check needs it: a tick must be
        // compared against the bracket of ITS OWN type, not the sum of all. On a character
        // with one dot the difference is invisible; on `_Itaan` with three it is 120 misses.
        public string Type = "";
        // WHOSE DAMAGE THIS IS: ours or a pet's. The check compares ticks against the
        // CHARACTER PANEL's bracket, and a pet's numbers are its own — its ticks must not
        // be compared against it (E7).
        public bool Pet;
        // TICK FRACTION: 1 is full, less means the last, partial one. The check needs it:
        // a partial tick must be divided by the fraction before comparing with the panel's
        // bracket, or it is declared "out of bracket" while being genuine (MECHANICS 70).
        public double Part = 1;

        public override string ToString()
        {
            List<string> numbers = new List<string>();
            List<string> was = new List<string>();
            for (int h = 0; h < 2; h++)
                if (Hit[h])
                {
                    numbers.Add(Math.Round(Hand[h]).ToString(CultureInfo.InvariantCulture));
                    was.Add(Math.Round(HandBefore[h]).ToString(CultureInfo.InvariantCulture));
                }
            // A line where "before" is not split per hand (life reduction) keeps one
            // number, as before.
            // Words come from the language file `lang/<CODE>/log.txt` (PLAN 7c, stage 4);
            // the gaps between parts are here, since the file trims text edges.
            string and = " " + Lang.T("log.line.and") + " ";
            string before = HandBefore[0] + HandBefore[1] > 0.0001
                          ? string.Join(and, was.ToArray())
                          : Math.Round(Before).ToString(CultureInfo.InvariantCulture);
            return Lang.T("log.line.at", At.ToString("F2", CultureInfo.InvariantCulture)) + "  " + What +
                   (numbers.Count > 0
                        ? "   " + string.Join(and, numbers.ToArray()) +
                          (Math.Abs(Before - Damage) > 0.5 ? "   " + Lang.T("log.line.before", before) : "")
                        : "") +
                   (string.IsNullOrEmpty(Why) ? "" : "   [" + Why + "]");
        }
    }

    public sealed class ProcRolls
    {
        public readonly string Dbr, Name;
        public readonly int Rolled;
        public readonly double Chance;
        public ProcRolls(string dbr, string name, int rolled, double chance)
        { Dbr = dbr ?? ""; Name = name ?? ""; Rolled = rolled; Chance = chance; }
    }

    public sealed class TimelineRun
    {
        public readonly List<TimelineLine> Log = new List<TimelineLine>();
        public int Swings, Hits, Misses;
        // HITS PER HAND AND OVER ALL STRIKES — the crit share is computed from these.
        // `Hits` counts swings where at least one hand landed, while crits come per hand
        // and from everything that strikes (skills, pool, procs). Dividing one by the
        // other printed "crits 574 (106% of hits)" — as it did on `_Itaan` 11 September.
        public int HandHits;
        public readonly int[] Picked = new int[3];      // swings with both/first/second hand
        public double Total, TotalBefore;
        // TARGET LIFE AND TTK (MECHANICS 77). `KillAt` — when everything that landed on
        // it equalled its life; −1 means not killed within the run. `PercentLifeTotal` —
        // how much the percent-life damage took off; `PercentLifeBlocked` — how many times
        // it fired and the target's resistance ate it whole.
        public double TargetLife, PercentLifeResist, PercentLifeTotal, KillAt = -1;
        public int PercentLifeHits, PercentLifeBlocked;
        public bool TargetIsDummy;
        public readonly List<double> PerSecond = new List<double>();
        // THE SAME SERIES BUT DIRECT DAMAGE ONLY — no dots, no procs. The check's
        // tolerance needs it: direct damage is what is compared, so the spread must be
        // computed from it. Over the common series the tolerance came out at 97%, because
        // it holds six-thousand-point procs that are not in the compared number.
        public readonly List<double> PerSecondDirect = new List<double>();
        public double HitPercent, TargetDefence, TargetArmour;
        public int Rolls;
        // A DOT'S DURATION IS NEVER SHORTENED (MECHANICS 89z): how many times a refresh
        // arrived with an earlier end and the duration stayed — and how many times it did
        // shorten (only under `--dotshorten`; the guard goes red).
        public int DotKeptLonger, DotShortened;
        // What came up in "one of" (XOR) groups: "record|field|chance" → times
        // (HitRoll.XorCounts).
        public readonly SortedDictionary<string, int> XorCounts =
            new SortedDictionary<string, int>(StringComparer.Ordinal);
        // Chance-based terms of the sum: key → [rolls, hits, chance] (HitRoll.ChanceCounts).
        public readonly SortedDictionary<string, double[]> ChanceCounts =
            new SortedDictionary<string, double[]>(StringComparer.Ordinal);
        // Crits: how many in total and how many at each tier. Tier 2 is the first crit
        // (×1.1), so the first two cells of the list are always empty.
        public int Crits;
        public readonly List<int> ByTier = new List<int>();
        // Dots: how many were applied anew, how many times refreshed and how much damage
        // they gave. Refreshes always far outnumber applications — which is exactly what
        // "one source holds ONE dot instead of breeding them" means (E.2).
        public int DotApplied, DotRefresh;
        // Skill presses over the run — total and by name; and how many times the
        // self-firing ones went off (procs and gear skills with an "on attack" condition).
        public int Presses, Procs, PoolFired;
        public int ProjectileVolleys;
        // Fired WITHOUT DAMAGE — summons and devotion buffs. Counted apart from `Procs`
        // on purpose: they add not a point of damage, and mixing them with the striking
        // procs would misstate how often the striking ones fire.
        public int ProcsSilent;
        // Whether the last `Fire` strike crit — for "on critical attack" procs, which are
        // rolled after the strike.
        public bool LastCrit;
        // CENSUS OF GEAR AND SET SKILLS: what it gives, under what condition and what
        // became of it in the run. Nothing may vanish silently (that is how the four
        // missing skills of `_Acid Vines` were found on 10 September).
        public readonly List<string> GearLines = new List<string>();
        // HOW MANY TIMES EACH SKILL FIRED — by record and by name. The `--firecheck`
        // check needs it: the census says a skill HAS a place, this says it ACTUALLY
        // fired in the queue (the user, 11 September: "I would not be surprised if
        // something else turns out not to work").
        public readonly Dictionary<string, int> Fired = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // How many times each proc and star was rolled and at what chance (filled at the
        // end of the run) — to tell the rare from the dead.
        public readonly List<ProcRolls> ProcRollList = new List<ProcRolls>();

        public void Count(string dbr, string name)
        {
            int n;
            if (!string.IsNullOrEmpty(dbr)) { Fired.TryGetValue(dbr, out n); Fired[dbr] = n + 1; }
            if (!string.IsNullOrEmpty(name) && !string.Equals(name, dbr, StringComparison.OrdinalIgnoreCase))
            { Fired.TryGetValue(name, out n); Fired[name] = n + 1; }
        }
        // The assigned attack: how many times it struck itself (for Cadence, how many
        // finales there were) and how high the charges got. The charges are for the eye:
        // if they stay at one, swings are rarer than the charges' lifetime.
        public int AssignedFired, ChargeSeen;
        // The largest cut to the target's defensive ability over the run — it shows
        // whether the cut works at all and how much it changes the chance to hit.
        public double DefenceCutSeen;
        // The largest flat resistance reduction over the run.
        public double AbsoluteCutSeen;
        public string AssignedName = "";
        // Striking auras: how many times they all pulsed, how much damage they gave and
        // which auras they are by name. A separate line, because they depend neither on a
        // swing nor on a roll — they show the "floor" of damage that is always there.
        // How many DIFFERENT buff sets occurred over the run. One means the set never
        // changed; more means buffs came and went and the character was rebuilt.
        // CHANCE-BASED COOLDOWN RESETS the character has, and their count over the run.
        internal List<Timeline.Refresher> Refreshers;
        // AN ACTION TAKES ITS ANIMATION TIME (MECHANICS 87): how many times a CHARACTER's
        // swing or press waited for someone else's animation to end and how many seconds
        // that came to; the same for pets. These show whether animation ate a noticeable
        // share of the fight.
        public int ActionWaits, PetWaits;
        public double WaitedSeconds, PetWaitedSeconds;
        public int Loadouts = 1;
        // PANELS OF EVERY SET met over the run. The dot check needs them: a tick that
        // happened under a buff or a pet's aura must lie in the bracket of THAT set, not
        // of the bare one. While there was a single panel, a summoner's run went red on a
        // hundred ticks — justly red, only not through its own fault.
        public readonly List<DamageModel> Panels = new List<DamageModel>();
        public readonly List<string> BuffLines = new List<string>();
        public int AuraPulses;
        public double AuraTotal;
        public readonly List<string> AuraLines = new List<string>();
        // One line per dot source created over the run. It guards the rule "an aura's dot
        // does not merge with a weapon's": if sources stick together there are fewer
        // lines, and that is visible.
        public readonly List<string> DotSources = new List<string>();
        // THE MAIN SKILL INSTEAD OF THE AUTOATTACK: a stream or a spell with no cooldown.
        // Its name, the word for the log (stream/spell), the cast speed and the interval
        // at the start, and how many times it struck over the run.
        public string MainSpell, MainSpellWord;
        public double CastSpeedPercent, MainSpellInterval;
        public int MainSpellTicks;
        // What was taken off the target's resistances and by what. Empty means nothing was.
        public readonly List<string> ResistCuts = new List<string>();

        // WHAT THE PETS TOOK OFF THE TARGET — as numbers, not as a line of text.
        //
        // A pet debuff lowers the target's resistance for EVERYONE, so our own damage
        // grows by it; but the check's expectation has no count of its own for pets —
        // it does not model when they are summoned at all. So it takes the share of
        // time from here, from the run itself, and says so in its report.
        //
        // Before 7 October this did not matter much: a pet's percent cut on a dummy
        // with zero resistance gave nothing. With the flat cut now applied FIRST
        // (MECHANICS 113) the percent cut bites into it, and the pets' share became
        // worth several per cent of our damage.
        public sealed class HeldCut
        {
            public string Name;
            public double Defensive, Absolute, Relative, Armor;
            public double Share;      // share of the run's time, 0…1
        }
        public readonly List<HeldCut> PetCuts = new List<HeldCut>();
        // The bonus against the target's race: to what and how much. Printed OUT LOUD for
        // the same reason as the target's resistances — otherwise a ×1.5 multiplier slips
        // into the number silently, and the gap with the game gets hunted in the engine.
        public readonly List<string> RacialLines = new List<string>();
        public readonly List<string> SilentNames = new List<string>();
        // Devotion stars by name: whose, from which skill, at what chance, how many times
        // it fired. Exactly what can be checked against the game by eye.
        public readonly List<string> StarLines = new List<string>();
        // How many times a proc's roll was not made at all because its cooldown was
        // running. The check needs it: this number shows what limits the rate — the
        // chance or the cooldown.
        public int ProcBlocked;
        // HOW MANY TIMES A COOLDOWN WAS RESET BY THE ROLL (`skillCooldownReductionChance`,
        // MECHANICS 151). It is the rate's second half beside the charges, and without a
        // count in the report there is no way to tell a run where the die was kind from one
        // where the rule is wrong.
        public int CooldownResets;
        // How many chances to roll a proc came up over the run at all — i.e. how many
        // STRIKES there were in the procs' sense. Compared against the check's own count:
        // if the engine learned a new way to strike and the expectation does not know of
        // it, that shows at once and by name.
        public int ProcChances;
        // Damage dealt by PROCS. Kept apart because procs are rare and large: fifty
        // firings over a run give a spread by themselves (±14% over fifty events), and
        // comparing them BY SUM paints the check with randomness. The RATE is compared,
        // and the sum is counted separately.
        public double ProcTotal;
        public readonly List<string> PressedNames = new List<string>();
        public double DotTotal;
        // PETS (E7): how many were summoned, how many strikes they made and how much
        // damage. Their damage goes into the common `Total` — it is as real as ours.
        public double PetTotal;
        public int PetHits, PetSummons, PetMisses, PetCrits;
        public readonly List<string> PetLines = new List<string>();
        // What the engine computed APPROXIMATELY and must say so about: an empty list
        // means there were no approximations. Silence here would read as "everything is
        // exact", and that is not the same thing.
        //
        // THERE ARE THREE KINDS OF NOTE, AND SINCE 14 SEPTEMBER EACH HAS ITS OWN LIST
        // (PLAN, item 2). All eighteen used to print the word "APPROXIMATED", though only
        // four are "not as the game does it": the rest are a human's choice or a hole in
        // the game's record.
        public readonly List<string> Notes = new List<string>();
        // NOT READ — the record lacks what is needed (duration, damage, rate, animations).
        public readonly List<string> Unread = new List<string>();
        // A HINT — the calculation is right, but the human's choice is probably not.
        public readonly List<string> Hints = new List<string>();
        public readonly List<string> DotWarned = new List<string>();
        // Damage types that occurred in this run at all. The display needs them: the
        // target has three dozen resistances and two or three of them matter.
        public readonly List<string> Types = new List<string>();

        public double Dps { get { return PerSecond.Count == 0 ? 0 : Total / PerSecond.Count; } }

        // Median of the PER-SECOND SUMS — the user's decision (E.1): the run is cut into
        // seconds, damage within each is summed, and the median is taken over that series.
        public double Median
        {
            get
            {
                if (PerSecond.Count == 0) return 0;
                List<double> a = new List<double>(PerSecond);
                a.Sort();
                int n = a.Count;
                return n % 2 == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) / 2;
            }
        }
    }

    // ONE DOT PER SOURCE — the user's decision (PLAN, E.2):
    //
    //   "mainly do not forget that identical sources refresh each other's ticks too; in
    //    effect there will be only 1 tick, at the best application within that time"
    //
    // So a new hit from the same source does NOT start a second stream of ticks: it
    // extends the duration and raises the damage to the BEST of what was applied,
    // including a crit one (5.4a). Only dots of DIFFERENT sources add up.
    //
    // WHAT COUNTS AS ONE SOURCE — THE LINE IS DRAWN AT WEAPON DAMAGE.
    //
    // The user's rule, 6 September: "it is not only two swords that are identical — the
    // whole pool of skills that have weapon damage counts as identical too; only skills
    // without weapon damage are different."
    //
    //   ALL WEAPON DAMAGE IS ONE SOURCE     the default attack, both swords, any skill
    //                                        with a `weaponDamagePct` share. They do not
    //                                        add to each other but OVERRIDE one another
    //                                        by the best application
    //   A SKILL WITHOUT WEAPON DAMAGE        is its own, and adds to all the rest
    //
    // This closes the main uncertainty of plan section 5.4: six cases were named there,
    // and "default attack versus a skill with a weapon share" decided damage per second
    // by multiples. It is now decided, and decided towards NOT adding.
    //
    // Hence the source key: "weapon" for everything weapon-based, the skill's record for
    // the rest, plus the dot's type. The hand does not go into the key: two identical
    // swords are one source (measured on `_Amakata`), which is the same case. The
    // `TimelineOptions.DotSourceByHand` knob is kept only so the difference can be seen
    // as a number if the rule ever has to be rechecked.
    sealed class Dot
    {
        public string Key = "";        // source: hand + type
        public bool Pet;               // a pet's dot: its numbers are not from our panel
        public string Type = "";       // "FireDot" and the rest — for target resistances
        public double PerSecond;       // damage per second, crit multiplier included
        public double Until;           // when it ends
        public double NextTick;        // when the next tick is due
        public int Hand;
        // IS THE TICK CHAIN ALIVE. A tick schedules the next one, but only while the dot
        // has not ended; if the dot has expired there is nobody to schedule it — so a
        // refresh must REVIVE the chain. Without this a dot that burned out once never
        // ticked again although it was refreshed duly: on one seed out of three the dot
        // damage came out exactly half, and the check caught it.
        public bool Ticking;
        // WHEN IT LAST TICKED (or when the dot was applied). A tick pays for the time
        // ELAPSED since that moment — which is why the last tick comes out partial if the
        // dot ended mid-second.
        //
        // The user's measurement of 9 September, six numbers out of six (MECHANICS 64):
        // with a full tick of 2258, the tail shows 2032, 1806, 903, 452, 226 — that is
        // 0.9, 0.8, 0.4, 0.2 and 0.1 of a tick.
        public double LastAt;
        // WHAT APPLIED the dot's current damage. A tick runs on its own schedule — once a
        // second, not right after the strike — and the tick line does not say whose it is.
        // That is what the user stumbled on: "if the second hand landed in the skill, then
        // after the skill there should be a fire dot in the log, and there is none?" The
        // dot was there, but said nothing about rising from 398 to 497 from the skill.
        public string From = "";
        // A WEAPON dot — then the hand matters. A dot from a skill without a weapon share
        // has no hand at all: Meteor strikes once, and a "(first hand)" note on its burn
        // would only confuse.
        public bool ByWeapon;
    }

    public static class Timeline
    {
        // Whether the record chain contains any value that changes our damage:
        // `offensive*`, attack values, speed, attributes, or target resistance reduction.
        internal static bool AffectsDamage(GameData game, string dbr)
        {
            string at = dbr;
            for (int hop = 0; hop < 4 && !string.IsNullOrEmpty(at); hop++)
            {
                DbRecord r = game.Record(at);
                if (r == null) break;
                foreach (KeyValuePair<string, string> kv in r.Fields)
                {
                    string f = kv.Key;
                    if (string.IsNullOrEmpty(kv.Value) || kv.Value == "0") continue;
                    if (f.StartsWith("offensive", StringComparison.Ordinal) ||
                        f.StartsWith("characterOffensiveAbility", StringComparison.Ordinal) ||
                        f.StartsWith("characterDefensiveAbility", StringComparison.Ordinal) ||
                        f.StartsWith("characterAttackSpeed", StringComparison.Ordinal) ||
                        f.StartsWith("characterTotalSpeed", StringComparison.Ordinal) ||
                        f.StartsWith("characterSpellCastSpeed", StringComparison.Ordinal) ||
                        f.StartsWith("characterStrength", StringComparison.Ordinal) ||
                        f.StartsWith("characterDexterity", StringComparison.Ordinal) ||
                        f.StartsWith("characterIntelligence", StringComparison.Ordinal) ||
                        (f.StartsWith("defensive", StringComparison.Ordinal) && r.GetInt("debufSkill") == 1) ||
                        f.StartsWith("weaponDamagePct", StringComparison.Ordinal) ||
                        f.StartsWith("damageMultiplier", StringComparison.Ordinal))
                        return true;
                }
                at = ItemViewFactory.NextSkillRecord(r);
            }
            return false;
        }

        // The name of the shared source of everything that strikes with the WEAPON. One for
        // both hands, for the default attack and for any skill with a `weaponDamagePct` share
        // — see the `Dot` class and the user's rule of 6 September.
        public const string WeaponSource = "weapon";

        // THE INTERNAL MARKERS OF A SOURCE. They are dictionary keys and prefixes, not words
        // for the eye: `GearSource` groups everything that comes from gear, `PetTag` marks a
        // pet's own strike or debuff, `PetAuraTag` a pet's aura, `SummonTag` a summon warned
        // about once, and `HandTag` splits a dot's key by hand when the run asks for that.
        // They were Russian until 12 October, and nothing but these constants compared them.
        public const string GearSource = "gear", PetTag = "pet:", SummonTag = "summon:",
                            HandTag = "|hand";

        // The mark on a dot that belongs to the WEAPON's shared source — see `DotSources`.
        public const string ByWeaponMark = "by the weapon";

        // A queue event: a swing or a dot tick. The kind is named explicitly because at E4
        // procs and skills arrive here, and the order within a moment must be firm.
        // PETS (E7) added three kinds: a summon (by tick, by the skill's cooldown), a
        // pet's strike, and its departure at the end of its lifetime.
        enum Kind { Swing = 0, Tick = 1, Press = 2, Aura = 3, Summon = 4, PetHit = 5, PetGone = 6, Volley = 7 }

        // ONE PET STANDING BY. There can be several identical ones (Raise Skeletons caps
        // at eight) and all of them strike — hence a number, not just a kind.
        sealed class PetLive
        {
            public TimelinePets.Kind Kind;
            public int Number;         // its index among its own kind
            public double Until;       // when it disappears; 0 means permanent
            public bool Gone;
            public int Hits;
            // UNTIL WHEN IT IS BUSY WITH AN ANIMATION (MECHANICS 87). A strike or skill
            // that comes up earlier waits for the end: a pet does not act twice at once.
            public double BusyUntil;
            // Addition to the cooldown of the skill that just fired — its animation's
            // length, if the cooldown counts from its end (`TimelinePets.CdFromEnd`,
            // MECHANICS 88).
            public double CdExtra;
        }

        struct Event
        {
            public double At;
            public int Actor;
            public int Order;       // firm order within one moment
            public Kind What;
            public Dot Source;      // for a tick: whose it is
            public Press Skill;     // for a press: which skill
            public Aura Ring;       // for an aura: which one
            public TimelinePets.Kind Summon;   // for a summon: whom we call
            public PetLive Pet;                // for a strike or departure: which pet
            public TimelinePets.Move Move;     // for a strike: what it strikes with
            public LogKind VolleyLog = default;
            public string VolleyCaption = null;
            public bool VolleyTriggersProcs = false;
            public Event(double at, int actor, int order)
            { At = at; Actor = actor; Order = order; What = Kind.Swing; Source = null; Skill = null; Ring = null; Summon = null; Pet = null; Move = null; }
            public Event(double at, int actor, int order, Dot dot)
            { At = at; Actor = actor; Order = order; What = Kind.Tick; Source = dot; Skill = null; Ring = null; Summon = null; Pet = null; Move = null; }
            public Event(double at, int actor, int order, Press press)
            { At = at; Actor = actor; Order = order; What = Kind.Press; Source = null; Skill = press; Ring = null; Summon = null; Pet = null; Move = null; }
            public Event(double at, int actor, int order, Aura ring)
            { At = at; Actor = actor; Order = order; What = Kind.Aura; Source = null; Skill = null; Ring = ring; Summon = null; Pet = null; Move = null; }
            public Event(double at, int actor, int order, TimelinePets.Kind summon)
            { At = at; Actor = actor; Order = order; What = Kind.Summon; Source = null; Skill = null; Ring = null; Summon = summon; Pet = null; Move = null; }
            public Event(double at, int actor, int order, PetLive pet, TimelinePets.Move move)
            { At = at; Actor = actor; Order = order; What = Kind.PetHit; Source = null; Skill = null; Ring = null; Summon = null; Pet = pet; Move = move; }
            public Event(double at, int actor, int order, PetLive pet)
            { At = at; Actor = actor; Order = order; What = Kind.PetGone; Source = null; Skill = null; Ring = null; Summon = null; Pet = pet; Move = null; }
        }

        // THE MOMENT'S LOADOUT: all of the character's numbers under a given set of raised
        // buffs.
        //
        // A buff changes MORE THAN DAMAGE. Pneumatic Burst gives +110 offensive ability
        // (hence a different chance to hit and to crit) and +14% total speed (hence a
        // different gap between swings), Ghoulish Hunger adds +22% attack speed, and
        // Hungering Void +18% crit damage. Task item 7 says so outright: "an attack-speed
        // buff may activate and the number of strikes will change on the fly… when
        // triggered buffs activate, ALL damage parameters must change on the fly".
        //
        // So the set is computed whole and at once — as if the character were rebuilt.
        sealed class Loadout
        {
            public CharacterTotals Totals;
            public DamageModel Panel;
            public double Pth, CritDamage, Below;
            public double Oa;      // offensive ability in this set — needed to redo PTH
        }

        // CUTS FROM GEAR: THE STEADY PART AND THE CHANCE-BASED TERMS (MECHANICS 89b).
        // One place for the engine (`DebuffBook.PutGear`) and for the check's expectation.
        // The fifth is the target's ARMOUR cut (MECHANICS 89e): on gear it always comes
        // with a chance and a duration.
        static readonly string[] GearCutFields = {
            "offensiveSlowDefensiveAbilityMin", "offensiveSlowOffensiveAbilityMin",
            "offensiveTotalResistanceReductionAbsoluteMin", "offensiveTotalResistanceReductionPercentMin",
            "offensiveSlowDefensiveReductionMin" };

        // Everything that hangs without a chance, as one cut, as it was before the roll.
        public static AbilityCut GearSteadyCut(CharacterTotals totals)
        {
            AbilityCut cut = new AbilityCut();
            if (totals == null) return cut;
            cut.Defensive = GearSteady(totals, GearCutFields[0]);
            cut.Offensive = GearSteady(totals, GearCutFields[1]);
            cut.Absolute = GearSteady(totals, GearCutFields[2]);
            cut.Relative = GearSteady(totals, GearCutFields[3]);
            cut.Armor = GearSteady(totals, GearCutFields[4]);
            cut.Secs = Math.Max(Math.Max(Math.Max(GearField(totals, "offensiveSlowDefensiveAbilityDurationMin"),
                                         GearField(totals, "offensiveTotalResistanceReductionAbsoluteDurationMin")),
                                GearField(totals, "offensiveTotalResistanceReductionPercentDurationMin")),
                                GearField(totals, "offensiveSlowDefensiveReductionDurationMin"));
            if (cut.Secs <= 0) cut.Secs = 3;
            return cut;
        }

        // Chance-based: each term has its own cut — the full number, its chance and the
        // duration of its own source. The key is "source|field".
        public static List<KeyValuePair<string, AbilityCut>> GearChanceCuts(CharacterTotals totals)
        {
            List<KeyValuePair<string, AbilityCut>> list = new List<KeyValuePair<string, AbilityCut>>();
            if (totals == null) return list;
            for (int f = 0; f < GearCutFields.Length; f++)
            {
                TotalsEntry e;
                if (!totals.Fields.TryGetValue(GearCutFields[f], out e)) continue;
                for (int i = 0; i < e.Terms.Count && i < e.TermChance.Count; i++)
                {
                    double chance = e.TermChance[i];
                    if (chance <= 0) continue;
                    string source = e.Terms[i].Key;
                    double full = e.Terms[i].Value * 100 / chance;
                    AbilityCut cut = new AbilityCut();
                    if (f == 0) cut.Defensive = full;
                    else if (f == 1) cut.Offensive = full;
                    else if (f == 2) cut.Absolute = full;
                    else if (f == 3) cut.Relative = full;
                    else cut.Armor = full;
                    cut.Chance = chance;
                    string field = GearCutFields[f];
                    cut.Secs = GearTerm(totals, field.Substring(0, field.Length - 3) + "DurationMin", source);
                    if (cut.Secs <= 0) cut.Secs = 3;
                    list.Add(new KeyValuePair<string, AbilityCut>(source + "|" + field, cut));
                }
            }
            return list;
        }

        static double GearSteady(CharacterTotals totals, string field)
        {
            TotalsEntry e;
            return totals.Fields.TryGetValue(field, out e)
                 ? e.ValueOn(delegate (string key, double chance) { return false; }) : 0;
        }

        static double GearField(CharacterTotals totals, string field)
        { TotalsEntry e; return totals.Fields.TryGetValue(field, out e) ? e.Value : 0; }

        static double GearTerm(CharacterTotals totals, string field, string source)
        {
            TotalsEntry e;
            if (!totals.Fields.TryGetValue(field, out e)) return 0;
            foreach (KeyValuePair<string, double> term in e.Terms) if (term.Key == source) return term.Value;
            return 0;
        }

        // REDUCED RESISTANCES OVER TIME: what is taken off, by what, and until when.
        //
        // Simpler than buffs: resistances live in the target itself, so they can be cut
        // right there — subtracted on application, restored on expiry. No recomputation of
        // the character is needed at all, because it is the TARGET that changes, not him.
        //
        // Re-applying the same skill EXTENDS THE DURATION but does not cut twice — the
        // same rule as for dots and buffs.
        sealed class DebuffBook
        {
            public readonly Dictionary<string, double> Until =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<ResistCut>> What =
                new Dictionary<string, List<ResistCut>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, double> Held =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Names =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public double Seconds;

            // ABILITY CUTS — apart from resistances: they live not in the target's
            // dictionary but in its own numbers, and the CHANCE TO HIT and the CRIT depend
            // on them. Equal ones do not stack — the strongest applies (the game's rule
            // for cuts, MECHANICS 7); so we keep them per skill and take the maximum.
            public readonly Dictionary<string, AbilityCut> Abil =
                new Dictionary<string, AbilityCut>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, double> AbilUntil =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, double> AbilHeld =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            // A separate stream for the debuff roll — by rule K3: add an item with a
            // chance-based cut, and the hit rolls and their brackets do not drift.
            public Random Rng;
            public int Rolled, Landed;

            public void PutAbility(string dbr, string name, AbilityCut cut, double at)
            {
                if (cut == null || !cut.Any) return;
                // CHANCE TO APPLY. "15% chance of 15% reduced resistances" — a roll on
                // EVERY application, as with a proc. A zero chance means "always".
                if (cut.Chance > 0.0001 && Rng != null)
                {
                    Rolled++;
                    if (Rng.NextDouble() * 100 >= cut.Chance) return;
                    Landed++;
                }
                double had;
                bool on = AbilUntil.TryGetValue(dbr, out had) && had > at + 1e-9;
                double from = on ? had : at;
                double to = at + cut.Secs;
                if (Seconds > 0 && to > Seconds) to = Seconds;
                if (to > from)
                {
                    double sum; AbilHeld.TryGetValue(dbr, out sum);
                    AbilHeld[dbr] = sum + (to - from);
                }
                AbilUntil[dbr] = at + cut.Secs;
                Abil[dbr] = cut;
                Names[dbr] = name;
            }

            // THE CUT FROM GEAR — any strike applies it, and it is already summed into the
            // character's stats. One key for all gear: however many items carry it,
            // `CharacterTotals` has already reduced them to a single number.
            public void PutGear(CharacterTotals totals, double at)
            {
                if (totals == null) return;
                // THE STEADY PART — one key for all gear, as before.
                AbilityCut cut = GearSteadyCut(totals);
                if (cut.Any) PutAbility(GearSource, GearSource, cut, at);
                // THE CHANCE-BASED PART — BY ROLL, NOT BY AVERAGE (MECHANICS 89b, the
                // user's decision of 14 September: "any stat with a chance is rolled").
                // Before that, "15% chance of 15% reduction" arrived here already as 2.25
                // and hung permanently: the average was right, the spread was not
                // (debt #18). Now every chance-based term has its own cut — in full, with
                // its own chance per strike and its own source's duration.
                foreach (KeyValuePair<string, AbilityCut> one in GearChanceCuts(totals))
                    PutAbility(GearSource + ": " + one.Key,
                               GearSource + ": " + one.Key.Substring(0, one.Key.IndexOf('|')), one.Value, at);
            }

            static double Of(CharacterTotals totals, string field)
            {
                TotalsEntry e;
                return totals.Fields.TryGetValue(field, out e) ? e.Value : 0;
            }

            // How much is cut NOW — by the strongest of those applied, not by their sum.
            public double DefenceCut(double at)
            {
                double best = 0;
                foreach (KeyValuePair<string, double> kv in AbilUntil)
                {
                    if (kv.Value <= at + 1e-9) continue;
                    AbilityCut cut;
                    if (Abil.TryGetValue(kv.Key, out cut) && cut.Defensive > best) best = cut.Defensive;
                }
                return best;
            }

            // The same for flat resistance reduction: the STRONGEST applies. We have no
            // measurement of two sources at once — the rule is taken from the article
            // (MECHANICS 7) and marked there as unmeasured.
            public double AbsoluteCut(double at)
            {
                double best = 0;
                foreach (KeyValuePair<string, double> kv in AbilUntil)
                {
                    if (kv.Value <= at + 1e-9) continue;
                    AbilityCut cut;
                    if (Abil.TryGetValue(kv.Key, out cut) && cut.Absolute > best) best = cut.Absolute;
                }
                return best;
            }

            // And the target's ARMOUR cut — the strongest of those applied (MECHANICS 89e).
            public double ArmorCut(double at)
            {
                double best = 0;
                foreach (KeyValuePair<string, double> kv in AbilUntil)
                {
                    if (kv.Value <= at + 1e-9) continue;
                    AbilityCut cut;
                    if (Abil.TryGetValue(kv.Key, out cut) && cut.Armor > best) best = cut.Armor;
                }
                return best;
            }

            // And the same for the relative one — they share the rule.
            public double RelativeCut(double at)
            {
                double best = 0;
                foreach (KeyValuePair<string, double> kv in AbilUntil)
                {
                    if (kv.Value <= at + 1e-9) continue;
                    AbilityCut cut;
                    if (Abil.TryGetValue(kv.Key, out cut) && cut.Relative > best) best = cut.Relative;
                }
                return best;
            }

            public void Put(TimelineTarget target, string dbr, string name,
                            List<ResistCut> cuts, double at, double secs)
            {
                if (cuts == null || cuts.Count == 0 || secs <= 0) return;
                double had;
                bool on = Until.TryGetValue(dbr, out had) && had > at + 1e-9;
                if (!on) Apply(target, cuts, -1);      // not applied yet — cut it now
                double from = on ? had : at;
                double to = at + secs;
                if (Seconds > 0 && to > Seconds) to = Seconds;
                if (to > from)
                {
                    double sum; Held.TryGetValue(dbr, out sum);
                    Held[dbr] = sum + (to - from);
                }
                Until[dbr] = at + secs;
                What[dbr] = cuts;
                Names[dbr] = name;
            }

            // What has expired goes back to the target.
            public void Expire(TimelineTarget target, double at)
            {
                List<string> gone = null;
                foreach (KeyValuePair<string, double> kv in Until)
                    if (kv.Value <= at + 1e-9)
                    { if (gone == null) gone = new List<string>(); gone.Add(kv.Key); }
                if (gone == null) return;
                foreach (string dbr in gone)
                {
                    Apply(target, What[dbr], +1);
                    Until.Remove(dbr);
                }
            }

            static void Apply(TimelineTarget target, List<ResistCut> cuts, int sign)
            {
                foreach (ResistCut cut in cuts)
                    foreach (string key in new[] { cut.Type, cut.Type + "Dot" })
                    {
                        double had;
                        target.Resist.TryGetValue(key, out had);
                        target.Resist[key] = had + sign * cut.Amount;
                    }
            }
        }

        // RAISED BUFFS OVER TIME, and the loadout for each of their sets.
        //
        // There are few sets over a run (buffs are few and come and go), so a set is
        // computed ONCE and remembered: `CharacterTotals` is not free, and there is no
        // point rebuilding it on every strike.
        sealed class BuffBook
        {
            public readonly Dictionary<string, double> Until =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Loadout> Cache =
                new Dictionary<string, Loadout>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> Names =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // How many seconds each buff stood — for the report. The number shows at once
            // whether it is up always, never, or, like Hungering Void, two thirds of the
            // time.
            public readonly Dictionary<string, double> Held =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public int Raised;
            public double Seconds;      // the run's length: nothing counts beyond it

            public void Raise(string dbr, string name, double at, double secs)
            {
                if (string.IsNullOrEmpty(dbr) || secs <= 0) return;
                double had;
                // AN EXTENSION, NOT A SECOND BUFF: the same buff raised again simply
                // stands on from the new moment. The same rule as for dots.
                double from = Until.TryGetValue(dbr, out had) && had > at ? had : at;
                Until[dbr] = at + secs;
                // WE COUNT ONLY WHAT FELL INSIDE THE RUN. A buff raised at second 31 of a
                // forty-second run stands until 51 — but we got nine seconds of it, not
                // twenty. Without the clamp it printed "stood 40 out of 40 — 100%" where
                // the truth was 71%.
                double to = at + secs;
                if (Seconds > 0 && to > Seconds) to = Seconds;
                if (to > from)
                {
                    double sum;
                    Held.TryGetValue(dbr, out sum);
                    Held[dbr] = sum + (to - from);
                }
                Names[dbr] = name;
                Raised++;
            }

            // The set's key: which buffs are up NOW, in order, so that the same set always
            // yields the same string.
            public string Key(double at)
            {
                List<string> on = new List<string>();
                foreach (KeyValuePair<string, double> kv in Until)
                    if (kv.Value > at + 1e-9) on.Add(kv.Key);
                on.Sort(StringComparer.Ordinal);
                return string.Join("|", on.ToArray());
            }
        }

        // A STRIKING AURA — the sixth way damage enters the queue.
        //
        // It strikes BY ITSELF: no swing, no press, no roll. The worked-out numbers are in
        // MECHANICS 41. Briefly, what is known and from where:
        //
        //   ONCE A SECOND       there is NO rate in the records at all — the user's
        //                       observation: "it seems to deal damage once per 1 sec"
        //   ALL AT ONCE, APART  "on _Amakata, Veil of Shadow and Spectral Binding, once
        //                       per 1 sec I see 2 different damage figures" — they do not
        //                       merge into one
        //   NO SPREAD           "the dot is always 66, the direct damage always 62": the
        //                       record holds a single number, not a bracket, so there is
        //                       nothing to roll. The same measurement shows no misses and
        //                       no crits
        //   TRIGGERS PROCS      "it can set off the meteor too" — for a proc its strike is
        //                       as good as a swing (MECHANICS 39)
        //   THE DOT NEVER DIES  the dot lasts 2 seconds and it strikes once a second — the
        //                       refresh arrives before the dot ends
        //
        // THE DAMAGE SITS IN THE TOGGLE'S MODIFIER, NOT IN THE TOGGLE ITSELF. For Veil of
        // Shadow the toggle applies a debuff to the enemy (−20 offensive ability) while
        // all the damage is in the Night's Chill modifier. That also saves us from double
        // counting: the toggle's flat damage is already summed by `CharacterTotals`, the
        // modifier's is NOT (checked by a census of the terms on `_777`).
        sealed class Aura
        {
            public AttackSkill Skill;
            public string Name;      // the modifier's name — what the game prints
            public string From;      // the toggle's name it is tied to
            public int Pulses;
        }

        // A SELF-FIRING SKILL: its chance, its OWN cooldown and when it is ready again.
        // The cooldown is no trifle here: Meteor with a 10% chance on attack and a
        // 5-second cooldown fires no more than once every five seconds, however often the
        // roll comes up. The user's reminder of 6 September; the plan had it from the start
        // (section Zh2 — "each has its own cooldown, independent of the activator's"),
        // the code did not.
        sealed class Proc
        {
            public AttackSkill Skill;
            public double Chance;
            public double Cooldown;
            public double ReadyAt;       // nothing to roll before this moment
            public int Fired, Blocked;   // fired / blocked by the cooldown
            // HOW MANY TIMES IT WAS ROLLED — without this a proc's rate cannot be checked:
            // "fired 15, cooldown blocked 23" does not say out of how many rolls those 15
             // came (a damage-over-time case).
            public int Rolled;
            // FIRES BUT DEALS NO DAMAGE: a summon, a buff, a debuff, or no damage in the
            // record. Empty means an ordinary damaging proc. The user's request: emulate
            // these anyway and log the fact that they fired — their roll and cooldown are
            // real, only the damage is missing.
            public EffectKind? NoDamage;
            public string Name;
            // Devotion stars only: whether it fires on a crit alone (`AttackEnemyCrit`)
            // and the name of the skill it is tied to.
            public bool OnlyCrit;
            // "On applying a buff": rolled not on a strike but when a buff goes up. An
            // honest condition — the queue knows the moment of raising exactly.
            public bool OnCastBuff;
            public string Activator, ActivatorClass;
            public int Level;      // the star's OWN level (`DevotionLevel` from the save)
            public string Dbr;     // the record — the key of the raised buff
            public double Duration; // how many seconds it stands, if it is a buff
        }

        // STARS TIED TO SKILLS: which skill has which.
        //
        // The key is the ACTIVATOR SKILL's record. A star is rolled only when that skill
        // strikes: not on a swing, not on another skill, not on a proc. `Seen` remembers
        // which activators struck at all over the run — so that at the end we can speak
        // about the stars whose activator was never used instead of staying silent.
        sealed class StarBook
        {
            public readonly Dictionary<string, List<Proc>> By =
                new Dictionary<string, List<Proc>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<Proc, Star> Whose = new Dictionary<Proc, Star>();
            public readonly List<string> Seen = new List<string>();
            // SUMMONS THAT FIRED — what and when. The firing itself is written by
            // `SilentEffect`, while the pet is put into the pool by the main loop: the
            // event queue and the order counter do not reach here (`ref int order` cannot
            // be held). The list is processed at the start of the next loop step, with its
            // OWN time.
            public readonly List<KeyValuePair<string, double>> Summons =
                new List<KeyValuePair<string, double>>();
            // Where raised buffs go: the star book and the buff book travel together,
            // because a buff star raises its buff the instant it fires.
            public BuffBook Buffs;
            // Reduced resistances likewise: a strike applies its debuff the same instant.
            public DebuffBook Debuffs;
            public Random Rng;
        }

        // A PRESSED SKILL: what we press, how often, and what it strikes with.
        sealed class Press
        {
            public AttackSkill Skill;
            public double Cooldown;      // seconds, character's reduction already applied
            // COOLDOWN CHARGES (`cooldownCharges`) — A STORE FOR A BURST, NOT A RATE
            // MULTIPLIER, and that is measured rather than reasoned (MECHANICS 151).
            //
            // The skill holds up to `Charges` uses; a press spends one, and one comes back
            // every cooldown. At the start the store is full, so the first `Charges` presses
            // go one after another and only then does the cooldown begin to be felt.
            //
            // `ResetChance` is the other half of the same tooltip line: `aegis3` carries
            // `skillCooldownReduction 100` beside `skillCooldownReductionChance`, and the
            // game prints the pair as "30% chance of −100% skill cooldown". A successful roll
            // hands the charge straight back.
            //
            // THE USER'S MEASUREMENT, eight runs of 20 seconds on Aegis of Menhir, cooldown
            // 1.9 s, three charges, a 30% chance: 20, 18, 20, 17, 19, 15, 16, 17 — mean
            // 17.75, standard error 0.65. Four readings, and only one fits:
            //
            //   no charges, no reset     1 + 20/1.9       = 11.5
            //   charges, no reset        3 + 20/1.9       = 13.5   below EVERY observation
            //   reset, no charges        1 + 20/(1.9×0.7) = 16.0   2.6 sigma below the mean
            //   charges AND reset        3 + 20/(1.9×0.7) = 18.0   <- 0.4 sigma, taken
            //
            // "Three per cooldown" would have given 45 and is out of the question.
            public int Charges = 1;
            public double ResetChance;
            public int Stored = 1;       // charges in hand; starts full
            public double RegenAt;       // when the next charge lands (while the store is not full)
            public Hands Hands;          // which hands strike — by animation (MECHANICS 14r)
            public int Count;            // how many times it was pressed over the run
            // What to say in the log BEFORE the hands are rolled — for the assigned attack
            // this is the layout roll itself: it strikes 50/25/25 like the default attack,
            // and without this line it is unclear why there are one or two figures.
            public string Note = "";
            // At which charge tier it strikes (0 is full, as the panel counts it). The
            // assigned attack needs it: Savagery builds a charge with every strike.
            public int AtCharge;
            // A BUFF ON COOLDOWN: the buff's record and how many seconds it stands.
            // Empty for an ordinary pressed skill — that strikes rather than stands.
            public string BuffDbr;
            public double Secs;
            // A BUFF WITHOUT A DURATION — an absorb shield (Turtle Shell): it stands until
            // broken, and we do not model the enemy's damage. There is nothing to raise,
            // but the FIRING is shown — the user's request of 12 September: "even without
            // an effect on damage we should at least show that they fired… it is like a
            // buff after all". Here: what to say in the log line.
            public string NoHold;
            // After how many seconds to press again if there is NO cooldown of its own.
            // Zero means do not press again at all (as it was before 9 September).
            public double EverySecs;
            // A STREAM: tick after tick while the button is held. The interval is not its
            // own but comes from the cast speed AT THAT MOMENT — computed at every tick.
            public bool Channel;
            // THE MAIN skill instead of the autoattack (a stream or an uninterrupted spell).
            public bool Main;
            // A SKILL WITHOUT A COOLDOWN FOR WHICH THE HUMAN SET NO INTERVAL — pressed by
            // its own cast animation: seconds at speed "1", divided by the cast speed AT
            // THAT MOMENT (MECHANICS 74).
            public double AnimSecs;
            // A PRESS WITHOUT DAMAGE — a curse, War Cry, Madness from a mask: it does not
            // strike but applies its debuff and rolls the stars tied to it (MECHANICS 76).
            // `SilentKind` is a debuff or "no damage in the record".
            public EffectKind? SilentKind;
            public string SilentDbr, SilentName;
            public int SilentLevel;
            public double SilentSecs;
            public string Name { get { return Skill == null ? (SilentName ?? "") : Skill.Name; } }
        }

        // A DEVOTION STAR TIED TO A SKILL AND FIRING ON ATTACK.
        public sealed class Star
        {
            public string Dbr, Name, Class;
            public int Level;
            public double Chance, Cooldown;
            public AttackSkill Skill;    // empty for those that deal no damage
            public EffectKind? NoDamage; // summon, buff, debuff, no damage in the record
            // WHICH SKILL THE STAR IS TIED TO. It fires NOT from any strike but only when
            // THAT skill strikes — the user's remark of 7 September: "I do not see Arcane
            // Bomb firing after Shadow Strike fires". Without the binding the star rained
            // on every swing, i.e. on other skills' strikes.
            public string Activator, ActivatorName, ActivatorClass;
            public double Duration;   // how many seconds the buff stands
            // `AttackEnemy` is on a strike, `AttackEnemyCrit` only on a crit.
            public string Trigger;
        }

        // STAR PARSING — ONE FOR THE ENGINE AND FOR THE CHECK.
        //
        // Stars do not enter the common skill list at all: the save keeps them apart, in
        // the activator skill's `AutoCastSkill` field. The chance is computed by the game's
        // formula (MECHANICS 18b), not from the raw `chanceToRun`.
        //
        // WHY SHARED. While there were two traversals, the check walked only
        // `AttackSkills.Of` and knew nothing of stars: the proc expectation came out at
        // 1.64 against twenty-five, and the dot bracket was built without Acid Spray —
        // fourteen false misses on `_Itaan`. Both times the error was NOT in the engine
        // but in comparing different sets.
        // THE CHARACTER'S STRIKING AURAS — one parsing for the engine and for the check.
        //
        // We look for them like this: a toggle that is on, and its modifiers WITH DAMAGE. The
        // toggle itself deliberately does not enter the list of damaging skills
        // (`AttackSkills.Choosable` rejects it), and rightly so: its flat damage is already
        // summed into the character's totals. The MODIFIER's damage is not in that sum — so
        // that is what we take, and there is no double counting.
        //
        // "Is it on" is decided as in `CharacterTotals`: a toggle counts as raised by
        // itself, and the user's decision (the active-skills window) is laid over that. Two
        // different answers to "what is raised" would set the panel and the queue apart,
        // and nobody would understand why.
        public static List<AuraFound> Auras(GameData game, Character c, ActiveSkills chosen,
                                            AttackSkills.Attach attach, SkillText.LevelBonus bonus)
        {
            List<AuraFound> found = new List<AuraFound>();
            foreach (Skill s in c.Skills)
            {
                if (string.IsNullOrEmpty(s.Name) || s.Level <= 0) continue;
                DbRecord rec = game.Record(s.Name);
                if (rec == null || !CharacterTotals.IsToggleClass(rec.Class)) continue;
                bool? said = chosen == null ? null : chosen.For(s.Name);
                if (said.HasValue ? !said.Value : false) continue;
                string ringName = SkillText.Name(game, s.Name) ?? s.Name;
                // This toggle's modifiers are the ones whose parent it is. The binding is
                // taken the same way as everywhere: the game's `skillDependancy` field,
                // then the parent's own marker (MECHANICS 28). We do not guess from the
                // position in the tree.
                foreach (Skill m in c.Skills)
                {
                    if (string.IsNullOrEmpty(m.Name) || m.Level <= 0) continue;
                    if (string.Equals(m.Name, s.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    string parent = null;
                    foreach (string one in attach.Parents(game, m.Name)) { parent = one; break; }
                    if (string.IsNullOrEmpty(parent)) parent = attach.Extra(m.Name);
                    if (!string.Equals(parent, s.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    int lvl = m.Level + (bonus == null ? 0 : bonus.For(m.Name, m.Level));
                    string modName = SkillText.Name(game, m.Name) ?? m.Name;
                    AttackSkill a = AttackSkills.One(game, m.Name, lvl, "aura \"" + ringName + "\"");
                    if (a == null) continue;      // a modifier without damage is no aura
                    AuraFound af = new AuraFound();
                    af.Skill = a; af.Name = modName; af.From = ringName;
                    found.Add(af);
                }
            }
            return found;
        }

        public sealed class AuraFound
        {
            public AttackSkill Skill;
            public string Name, From;
        }

        // RESISTANCE REDUCTION, KIND ONE — THE "ADDITIVE" ONE.
        //
        // There are three kinds (MECHANICS 7), applied in turn:
        //   ADDITIVE    stacks, applied FIRST; in the records it is `defensive<Type>`
        //               with a NEGATIVE value on a debuff
        //   relative    cuts a percent of the current value, the strongest applies
        //   flat        subtracts a number, the strongest applies, applied last
        //
        // Only the first is done here, and only from striking auras — as the user asked:
        // "let us try it in parts at least, do the type the aura strikes with". For Night's
        // Chill that is `defensiveCold −3`, `defensivePierce −3`, `defensivePoison −3`,
        // `defensiveLife −3` at level one.
        //
        // A RESISTANCE GOING NEGATIVE IS NOT AN ERROR: the user's remark — "some kinds of
        // reduction can make the resistance negative, which raises the damage to the
        // enemy". The target layer computes `× (1 − r/100)`, so r = −3 gives ×1.03 —
        // exactly the 3% by which the game hit above our count (MECHANICS 41).
        public static List<ResistCut> AuraResistCuts(GameData game, Character c, ActiveSkills chosen,
                                                     AttackSkills.Attach attach,
                                                     SkillText.LevelBonus bonus)
        {
            List<ResistCut> made = new List<ResistCut>();
            foreach (Skill s in c.Skills)
            {
                if (string.IsNullOrEmpty(s.Name) || s.Level <= 0) continue;
                DbRecord rec = game.Record(s.Name);
                if (rec == null || !CharacterTotals.IsToggleClass(rec.Class)) continue;
                bool? said = chosen == null ? null : chosen.For(s.Name);
                if (said.HasValue && !said.Value) continue;
                string ringName = SkillText.Name(game, s.Name) ?? s.Name;
                // Both the toggle and its modifiers: the minus can sit on either.
                List<string> look = new List<string>();
                look.Add(s.Name);
                foreach (Skill m in c.Skills)
                {
                    if (string.IsNullOrEmpty(m.Name) || m.Level <= 0) continue;
                    if (string.Equals(m.Name, s.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    string parent = null;
                    foreach (string one in attach.Parents(game, m.Name)) { parent = one; break; }
                    if (string.IsNullOrEmpty(parent)) parent = attach.Extra(m.Name);
                    if (string.Equals(parent, s.Name, StringComparison.OrdinalIgnoreCase)) look.Add(m.Name);
                }
                foreach (string dbr in look)
                {
                    DbRecord r = game.Record(dbr);
                    if (r == null) continue;
                    int lvl = 1;
                    foreach (Skill f in c.Skills)
                        if (string.Equals(f.Name, dbr, StringComparison.OrdinalIgnoreCase))
                        { lvl = f.Level + (bonus == null ? 0 : bonus.For(f.Name, f.Level)); break; }
                    int slot = SkillText.Slot(r, lvl);
                    string title = SkillText.Name(game, dbr) ?? dbr;
                    foreach (string type in ResistTypes)
                    {
                        double v = AttackSkill.Value(r, "defensive" + type, slot);
                        if (v >= -0.0001) continue;      // a cut is a NEGATIVE value
                        ResistCut cut = new ResistCut();
                        cut.Type = type; cut.Amount = -v;
                        cut.From = title + (title == ringName ? "" : " (aura \"" + ringName + "\")");
                        made.Add(cut);
                    }
                }
            }
            return made;
        }

        // RESISTANCE REDUCTION FROM A SINGLE SKILL — as for an aura, but for a strike.
        //
        // Procs carry it on a par with auras and markedly more of it: on `_Acid Vines`,
        // Rumor takes −30% off poison and acid, Widow's Bite −10%/−10%/−12%, Scorpion
        // Sting −150 defensive ability. For a poison build that is a large shortfall, and
        // until 9 September NONE of it was applied.
        //
        // We walk the record chain, as everywhere: for Rumor the minuses may sit on a child
        // record rather than the top one.
        public static List<ResistCut> SkillResistCuts(GameData game, string dbr, int level, string name)
        {
            List<ResistCut> made = new List<ResistCut>();
            string at = dbr;
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(at); hop++)
            {
                DbRecord r = game.Record(at);
                if (r == null) break;
                int slot = SkillText.Slot(r, level);
                // "ALL RESISTANCE" IS ONE FIELD FOR EVERY TYPE. The Great White Wolf's
                // Mogdrogen's Presence takes `defensiveAllResistance -8`, and it is not
                // written out per type: we spread it ourselves, or such an aura would cut
                // NOTHING (found 12 September on pets).
                double all = AttackSkill.Value(r, "defensiveAllResistance", slot);
                foreach (string type in ResistTypes)
                {
                    double v = AttackSkill.Value(r, "defensive" + type, slot);
                    if (all < -0.0001) v += all;
                    if (v >= -0.0001) continue;      // a cut is a NEGATIVE value
                    ResistCut cut = new ResistCut();
                    cut.Type = type; cut.Amount = -v; cut.From = name;
                    made.Add(cut);
                }
                if (made.Count > 0) break;      // found on this record — go no deeper
                at = ItemViewFactory.NextSkillRecord(r);
            }
            return made;
        }

        // Types that can have `defensive<Type>`. Same names as in the target layer.
        static readonly string[] ResistTypes =
            { "Physical", "Pierce", "Fire", "Cold", "Lightning", "Poison", "Life", "Aether", "Chaos" };

        // STARS WHOSE CONDITION WE DO NOT ROLL — one list for all of them.
        //
        // The decision on each kind was taken with the user on 8 September:
        //
        //   AttackEnemy, AttackEnemyCrit   rolled honestly — we have strikes and crits
        //   CastBuff                       rolled: the moment a buff goes up is known
        //   LowHealth, LowMana, OnKill     NOT rolled: "we will not count energy",
        //   HitBy*, Block                  "we cannot count an enemy's death exactly",
        //                                  and we do not count the enemy's damage at all
        //   OnEquip                        stands while the item is worn; none of our five
        //                                  characters has one
        //
        // Everything from the second group comes here: a tick in the window plus the queue
        // by cooldown. The condition is not rolled; the duration and cooldown are real.
        public static List<Star> ByOtherCondition(GameData game, Character c)
        {
            List<Star> found = new List<Star>();
            // GEAR SKILLS HERE TOO — the same omission as above.
            List<Skill> where = new List<Skill>(c.Skills);
            foreach (ItemSkill it in c.ItemSkills)
            {
                if (string.IsNullOrEmpty(it.AutoCastSkill) || string.IsNullOrEmpty(it.SkillName)) continue;
                Skill fake = new Skill();
                fake.Name = it.SkillName;
                fake.AutoCastSkill = it.AutoCastSkill;
                fake.AutoCastController = it.AutoCastController;
                where.Add(fake);
            }
            foreach (Skill s in where)
            {
                if (string.IsNullOrEmpty(s.AutoCastSkill)) continue;
                string trg; double chn;
                if (!SkillText.AutoOf(game, s.AutoCastSkill, s.AutoCastController,
                                      s.Name, out trg, out chn)) continue;
                if (trg == "AttackEnemy" || trg == "AttackEnemyCrit" || trg == "CastBuff") continue;
                if (game.Record(s.AutoCastSkill) == null) continue;
                Star st = new Star();
                st.Dbr = s.AutoCastSkill;
                st.Name = SkillText.Name(game, s.AutoCastSkill) ?? s.AutoCastSkill;
                st.Trigger = trg;
                st.Chance = chn;
                st.ActivatorName = SkillText.Name(game, s.Name) ?? s.Name;
                found.Add(st);
            }
            return found;
        }

        public sealed class ResistCut
        {
            public string Type, From;
            public double Amount;      // how many POINTS are cut, positive
        }

        // CUTS TO THE ENEMY'S OFFENSIVE AND DEFENSIVE ABILITY.
        //
        // The user's request of 9 September: "these parameters matter quite a lot to us,
        // since whether we hit the enemy and what crit we deal depend on its defensive
        // ability". So they do: `Combat.Pth` is computed from it, and the hit share and the
        // crit tier both come out of PTH.
        //
        // The game's fields are the same family as a dot's, with the duration alongside:
        //
        //     offensiveSlowDefensiveAbilityMin / …DurationMin   "150 reduced DA for 5s"
        //     offensiveSlowOffensiveAbilityMin / …DurationMin   "−55 offensive ability"
        //
        // The enemy's OFFENSIVE ability is not used anywhere yet (we do not count its
        // strikes on us), but it is read and shown, so that the cut is visible.
        public sealed class AbilityCut
        {
            public double Defensive, Offensive, Secs;
            // FLAT RESISTANCE REDUCTION — here too, because it lives by the same rules: its
            // own duration, strongest instead of sum, and it belongs to the TARGET.
            // Otherwise a third book just like this would be needed.
            public double Absolute;
            // RELATIVE reduction: cuts a PERCENT OF THE CURRENT resistance, whatever it is,
            // negative included (MECHANICS 7). Measured 9 September from the other side: on
            // the dummy's zero it gives NOTHING — and should not (MECHANICS 67).
            public double Relative;
            // Chance to apply, in percent. Zero means always. Affixes have it:
            // "15% chance of 15% reduced resistances".
            public double Chance;
            // THE TARGET'S ARMOUR CUT AS A NUMBER (MECHANICS 89e):
            // `offensiveSlowDefensiveReductionMin` — "reduce armor 230 5s" in the record's
            // description; strongest rather than sum (the `StrongestWins` list), with its
            // own duration and chance. It lives by the same rules as flat resistance
            // reduction — hence it is here.
            public double Armor;
            public bool Any
            {
                get
                {
                    return Defensive > 0.0001 || Offensive > 0.0001 ||
                           Absolute > 0.0001 || Relative > 0.0001 || Armor > 0.0001;
                }
            }
        }

        // THE ABILITY CUT CARRIED BY THE STRIKE ITSELF — TOGETHER WITH ITS MODIFIERS.
        //
        // `SkillAbilityCut` below walks the skill RECORD's chain and does not see a modifier
        // that hangs on the form rather than on it. On the berserker that is Recklessness
        // (`werewolf3`): `offensiveSlowDefensiveAbilityMin` up to 350 for 3 seconds, and it
        // belongs to the FORM, reaching the strike through `AttackSkill.Parts` — the same
        // route as all of its damage (`skillDependancy` → `grantedSkills`).
        //
        // Noticed from the user's measurement of 12 September: on `_555` in form he sees
        // multipliers 2.01 and 2.5, i.e. the THIRD crit tier, while ours never rose above
        // the second — the chance to hit was stuck at 119.1, and the third tier needs over
        // 120. Those 270 points of cut are exactly what gives it.
        public static AbilityCut AttackAbilityCut(AttackSkill a)
        {
            AbilityCut cut = new AbilityCut();
            if (a == null) return cut;
            cut.Defensive = a.Sum("offensiveSlowDefensiveAbilityMin");
            cut.Offensive = a.Sum("offensiveSlowOffensiveAbilityMin");
            cut.Absolute = a.Sum("offensiveTotalResistanceReductionAbsoluteMin");
            cut.Relative = a.Sum("offensiveTotalResistanceReductionPercentMin");
            if (!cut.Any) return cut;
            // The duration is the longest the parts name; if none names one, three seconds,
            // as for any debuff without its own duration.
            double secs = 0;
            foreach (string field in new[] { "offensiveSlowDefensiveAbilityDurationMin",
                                             "offensiveSlowOffensiveAbilityDurationMin",
                                             "offensiveTotalResistanceReductionAbsoluteDurationMin",
                                             "offensiveTotalResistanceReductionPercentDurationMin" })
            {
                double one = a.Sum(field);
                if (one > secs) secs = one;
            }
            cut.Secs = secs > 0 ? secs : 3;
            return cut;
        }

        public static AbilityCut SkillAbilityCut(GameData game, string dbr, int level)
        {
            AbilityCut cut = new AbilityCut();
            string at = dbr;
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(at); hop++)
            {
                DbRecord r = game.Record(at);
                if (r == null) break;
                int slot = SkillText.Slot(r, level);
                double d = AttackSkill.Value(r, "offensiveSlowDefensiveAbilityMin", slot);
                double o = AttackSkill.Value(r, "offensiveSlowOffensiveAbilityMin", slot);
                // Flat resistance reduction — "20 reduced resistances". With its own
                // chance: affixes carry `…AbsoluteChance`.
                double abs = AttackSkill.Value(r, "offensiveTotalResistanceReductionAbsoluteMin", slot);
                double rel = AttackSkill.Value(r, "offensiveTotalResistanceReductionPercentMin", slot);
                // The target's armour cut as a number (MECHANICS 89e) — with its own chance
                // and duration.
                double arm = AttackSkill.Value(r, "offensiveSlowDefensiveReductionMin", slot);
                // A DEBUFF SPELL (Annihilation: `SkillBuff_Debuf`, `debufSkill 1`) writes
                // its cut not in a strike field but DIRECTLY as a character number with a
                // minus sign: `characterDefensiveAbility −55` means −55 defensive ability
                // on the TARGET the spell landed on. The duration is `skillActiveDuration`.
                bool debuf = r.GetInt("debufSkill") == 1 ||
                             (r.Class ?? "").IndexOf("Debuf", StringComparison.OrdinalIgnoreCase) >= 0;
                if (debuf && d <= 0.0001 && o <= 0.0001)
                {
                    double dd = -AttackSkill.Value(r, "characterDefensiveAbility", slot);
                    double oo = -AttackSkill.Value(r, "characterOffensiveAbility", slot);
                    if (dd > 0.0001 || oo > 0.0001)
                    {
                        cut.Defensive = Math.Max(0, dd); cut.Offensive = Math.Max(0, oo);
                        cut.Secs = AttackSkill.Value(r, "skillActiveDuration", slot);
                        if (cut.Secs <= 0) cut.Secs = 3;
                        return cut;
                    }
                }
                if (d > 0.0001 || o > 0.0001 || abs > 0.0001 || rel > 0.0001 || arm > 0.0001)
                {
                    cut.Defensive = d; cut.Offensive = o; cut.Absolute = abs; cut.Relative = rel; cut.Armor = arm;
                    // THE CHANCE is its own per kind; we take the one standing next to a
                    // non-empty number. For Hexxer's Kiss it is 15%.
                    cut.Chance = abs > 0.0001
                        ? AttackSkill.Value(r, "offensiveTotalResistanceReductionAbsoluteChance", slot)
                        : rel > 0.0001
                        ? AttackSkill.Value(r, "offensiveTotalResistanceReductionPercentChance", slot)
                        : AttackSkill.Value(r, "offensiveSlowDefensiveReductionChance", slot);
                    cut.Secs = Math.Max(Math.Max(Math.Max(Math.Max(
                        AttackSkill.Value(r, "offensiveSlowDefensiveAbilityDurationMin", slot),
                        AttackSkill.Value(r, "offensiveSlowOffensiveAbilityDurationMin", slot)),
                        AttackSkill.Value(r, "offensiveTotalResistanceReductionAbsoluteDurationMin", slot)),
                        AttackSkill.Value(r, "offensiveTotalResistanceReductionPercentDurationMin", slot)),
                        AttackSkill.Value(r, "offensiveSlowDefensiveReductionDurationMin", slot));
                    if (cut.Secs <= 0) cut.Secs = 3;
                    return cut;
                }
                at = ItemViewFactory.NextSkillRecord(r);
            }
            return cut;
        }

        public static List<Star> DevotionOnAttack(GameData game, Character c, CharacterTotals totals,
                                                  AttackSkills.Attach attach, ItemSkillMods mods,
                                                  SkillText.LevelBonus bonus)
        {
            List<Star> found = new List<Star>();
            // STARS ARE TIED TO GEAR SKILLS TOO, NOT ONLY TO MASTERY SKILLS.
            //
            // Found 9 September on `_Acid Vines`: Scorpion Sting is tied to Biting Blades,
            // which is a gear skill. The traversal went only over `c.Skills`, so such a
            // star vanished WITHOUT A TRACE — it was neither in the report nor in the
            // queue. The devotion window has long read both lists
            // (`SkillsForm.MapStars`).
            List<Skill> where = new List<Skill>(c.Skills);
            foreach (ItemSkill it in c.ItemSkills)
            {
                if (string.IsNullOrEmpty(it.AutoCastSkill) || string.IsNullOrEmpty(it.SkillName)) continue;
                Skill fake = new Skill();
                fake.Name = it.SkillName;
                fake.AutoCastSkill = it.AutoCastSkill;
                fake.AutoCastController = it.AutoCastController;
                where.Add(fake);
            }
            foreach (Skill s in where)
            {
                if (string.IsNullOrEmpty(s.AutoCastSkill)) continue;
                string trg; double chn;
                if (!SkillText.AutoOf(game, s.AutoCastSkill, s.AutoCastController,
                                      s.Name, out trg, out chn)) continue;
                // `CastBuff` is "on applying a buff". Rolled honestly: the queue now knows
                // the moment a buff goes up exactly (the user's decision of 8 September —
                // "CastBuff can be done").
                if (trg != "AttackEnemy" && trg != "AttackEnemyCrit" && trg != "CastBuff") continue;
                DbRecord sr = game.Record(s.AutoCastSkill);
                if (sr == null || chn <= 0.0001) continue;
                Star st = new Star();
                st.Dbr = s.AutoCastSkill;
                st.Class = sr.Class ?? "";
                st.Name = SkillText.Name(game, s.AutoCastSkill) ?? s.AutoCastSkill;
                // A STAR'S LEVEL IS ITS OWN, NOT THE ACTIVATOR'S.
                //
                // I used to take `s.Level`, the level of the skill the star is tied to.
                // That is someone else's number: Righteous Fervor has 16+12, while its star
                // counts its own. Its own sits in the save's `DevotionLevel` field — a star
                // levels by experience, not by points, and the rest of the editor already
                // reads that field (`CharacterTotals`, `PetPool`, `SelfTest`); the timeline
                // was the only place that did not ask.
                //
                // WHAT IT CHANGES. A star's `skillMaxLevel` is always 1 and lies: Tainted
                // Eruption has a damage array of FIFTEEN values
                // (`offensiveSlowPoisonMin 130;152;…;472`). The fifteenth matches the game
                // exactly: 472 × 32.91 × 5 s = 77667 against 77675. The user, 8 September:
                // "the real current level is what is needed".
                st.Level = 1;
                foreach (Skill d in c.Skills)
                    if (string.Equals(d.Name, s.AutoCastSkill, StringComparison.OrdinalIgnoreCase))
                    { st.Level = d.DevotionLevel > 0 ? d.DevotionLevel : d.Level; break; }
                st.Chance = chn;
                st.Activator = s.Name;
                st.ActivatorName = SkillText.Name(game, s.Name) ?? s.Name;
                st.Trigger = trg;
                DbRecord arec = game.Record(s.Name);
                st.ActivatorClass = arec == null ? "" : (arec.Class ?? "");
                // A SUMMON STAR AND A BUFF STAR DEAL NO DAMAGE BUT DO FIRE — and that must
                // be shown. The user's request: "even though it summons a pet, it still has
                // to be emulated and at least the fact that it fired put in the log… we
                // also have devotion as buffs, those must be shown too".
                //
                // We do not count the summoned ones' damage at all — that is separate work,
                // with lifetimes and their own strike conditions. But their roll, cooldown
                // and log line are as honest as any proc's.
                // A SUMMON IS DECIDED BY CLASS, EVERYTHING ELSE BY THE RECORD.
                //
                // A summon does have damage numbers in its record, but they are the PET's,
                // not ours, and must not be taken for ourselves — hence the class decides
                // here.
                //
                // Beyond that, ask the record, not the class name. `Skill_AttackBuff`
                // (Wendigo's Mark) reads as a buff by name, and by name one could also
                // throw out a skill that does strike. There is one truth: whether the
                // record has damage numbers — that is what we ask. The class stays only as
                // a caption, so the log holds a word rather than a blank.
                bool summons = st.Class.IndexOf("SpawnPet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               st.Class.IndexOf("Summon", StringComparison.OrdinalIgnoreCase) >= 0;
                if (summons) st.NoDamage = EffectKind.Summon;
                else
                {
                    st.Skill = AttackSkills.One(game, st.Dbr, st.Level, "devotion: " + st.Name);
                    // No damage was found in the record — but the star fires all the same,
                    // and we must not keep silent: missing from the log, it would look
                    // unpressed.
                    //
                    // A DEBUFF IS NOT A BUFF. Until 10 September a class with the word
                    // `Debuf` in it got the word "buff", and the damage-free branch raised
                    // it as a BUFF ON US: a curse's −55 offensive ability would land on the
                    // character. Now a debuff is applied to the target (`Silent`).
                    if (st.Skill == null) st.NoDamage = SilentKind(game, st.Dbr, st.Class);
                }
                st.Cooldown = SkillTiming.Cooldown(game, c, totals, attach, mods, bonus,
                                                   st.Dbr, st.Level);
                st.Duration = SkillTiming.Duration(game, st.Dbr, st.Level);
                found.Add(st);
            }
            return found;
        }

        // Our bonus against THIS target's race: the percent goes into the multiplier, the
        // flat one only into the report (see `TimelineTarget.RacialFlat` for why).
        //
        // The dummy's race is empty, and then there is no bonus at all — that is the game's
        // truth, not a gap in the engine: there is nothing on a dummy for "damage to
        // Chthonics" to bite.
        static void RacialOf(GameData game, CharacterTotals t, TimelineTarget target, TimelineRun run)
        {
            target.RacialPercent = 0;
            target.RacialFlat = 0;
            if (t == null || target == null) return;
            string race = target.Race ?? "";
            string ru = game == null || race.Length == 0
                      ? race : (game.Tag("tag" + race) ?? race);
            if (race.Length == 0)
            {
                // We must not keep silent: a person may carry twenty such bonuses on gear
                // and none of them works on this target. We must say WHY.
                if (t.Racial.Count > 0 && run != null)
                    // Service detail hidden 17 September (the user asked to hide, not
                    // delete). The wording was: "… it has NO race at all
                    // (`characterRacialProfile` is empty — as on the dummy)".
                    run.RacialLines.Add(Lang.T("log.racial.noRace", t.Racial.Count));
                return;
            }
            double[] pair;
            if (!t.Racial.TryGetValue(race, out pair))
            {
                if (t.Racial.Count > 0 && run != null)
                    run.RacialLines.Add(Lang.T("log.racial.none", ru, t.Racial.Count));
                return;
            }
            target.RacialPercent = pair[0];
            target.RacialFlat = pair[1];
            if (run == null) return;
            if (Math.Abs(pair[0]) > 0.0001)
                run.RacialLines.Add(Lang.T("log.racial.has", ru, (pair[0] > 0 ? "+" : "") + Fmt(pair[0]),
                                           (1 + pair[0] / 100.0).ToString("F4", CultureInfo.InvariantCulture)));
            if (Math.Abs(pair[1]) > 0.0001)
                run.Notes.Add(Lang.T("log.racial.flat", ru, (pair[1] > 0 ? "+" : "") + Fmt(pair[1])));
        }

        public static TimelineRun Run(GameData game, Character c, CharacterTotals baseTotals,
                                      ExactRolls seeded, TimelineTarget target, TimelineOptions opt)
        {
            if (opt == null) opt = new TimelineOptions();
            TimelineRun run = new TimelineRun();

            // THE CHARACTER'S NUMBERS VARY OVER TIME, THEY ARE NOT CONSTANT.
            //
            // `baseTotals` is the character WITHOUT temporary buffs. While a buff stands,
            // the calculation must use a different set of numbers, and they all change at
            // once: damage, attack speed, offensive ability, crit multiplier. Below, in the
            // loop, `totals` and `panel` are REASSIGNED to the current moment's set — hence
            // the parameter's name: so that every place inside the loop takes the numbers of
            // now rather than those of the start.
            BuffBook buffs = new BuffBook();
            buffs.Seconds = opt.Seconds;
            DebuffBook debuffs = new DebuffBook();
            debuffs.Seconds = opt.Seconds;
            debuffs.Rng = new Random(opt.Seed * 31 + 11);
            // BUFFS THAT ARE QUEUED IN TIME ARE REMOVED FROM THE BASE SET.
            //
            // The user ticked them, so the `CharacterTotals` from the window arrived with
            // them RAISED. For the panel that is right, for the queue it is not: the queue
            // raises them itself, for their duration. Keeping both would count them twice
            // and hold them forever.
            if (opt.TimedBuffs.Count > 0)
                baseTotals = CharacterTotals.Of(game, c, seeded,
                                                ActiveSkills.Of(c.Name).With(null, opt.TimedBuffs));
            CharacterTotals totals = baseTotals;
            // THE BONUS AGAINST THE TARGET'S RACE — task item 6. We pick ITS race only:
            // "+15% damage to undead" does not touch a Chthonic at all, and summing
            // everything indiscriminately would be the worst kind of error — the invisible
            // kind.
            //
            // Computed ONCE, from the base set, not per buff set. That is allowed because
            // the racial bonus lives on gear and in auras, which stand for the whole run; a
            // temporary buff with such a field has never occurred to us. If one does, it
            // will surface here, in the line below.
            RacialOf(game, totals, target, run);
            DamageModel panel = new DamageModel(game, c, totals, null, true, seeded);
            double oa = Combat.Ability(game, c, totals, c.Cunning,
                                       "characterOffensiveAbility", "characterDexterity");
            double pth = Combat.Pth(game,oa, target.DefensiveAbility);
            // The `pthMinimum` floor applies to the hit percent, which is now PTH itself.
            double floorPth = Combat.PthMinimum(game);
            if (pth < floorPth) pth = floorPth;
            run.HitPercent = Math.Min(100, Combat.Percent(pth));
            // Tiers and multipliers come from the game's file; the character's "+% crit
            // damage" is added to the tier's multiplier (measured 10 August).
            double[] thresholds = Combat.Thresholds(game);
            double[] multipliers = Combat.Multipliers(game);
            TotalsEntry critEntry;
            double critDamage = totals.Fields.TryGetValue("offensiveCritDamageModifier", out critEntry)
                              ? critEntry.Value : 0;
            // Damage reduction below the first threshold.
            double below = Combat.BelowThreshold(game, pth);
            // The set WITHOUT buffs is cached right away: it is needed most often, and
            // there is no point computing it twice.
            {
                Loadout bare = new Loadout();
                bare.Totals = baseTotals; bare.Panel = panel;
                bare.Oa = oa;
                bare.Pth = pth; bare.CritDamage = critDamage; bare.Below = below;
                buffs.Cache[""] = bare;
            }
            run.TargetDefence = target.DefensiveAbility;
            run.TargetArmour = target.Armour;

            bool dual = panel.HandsKnown;
            // Hand layout shares — the weights of the set's attack animations
            // (MECHANICS 89o), 50/25/25.
            SkillHands.DualSplit handSplit = SkillHands.DualSwing(game, dual && DamageModel.IsDualRanged(game, c));
            HitRoll roll = new HitRoll(opt.Seed);
            // A separate stream per kind of roll: add a pet or take off a ring and the
            // character's rolls do not drift, so two runs stay comparable (PLAN, K3).
            Random hitRng = new Random(opt.Seed * 31 + 1);
            Random handRng = new Random(opt.Seed * 31 + 2);
            // Procs get their own stream, so that enabling a proc does not shift the hit
            // and bracket rolls and two runs stay comparable (PLAN, K3).
            Random procRng = new Random(opt.Seed * 31 + 3);
            // THE ROLL FOR "A CHANCE TO RESET THE COOLDOWN" GETS A STREAM OF ITS OWN, and
            // that is on purpose: taking it from `procRng` would shift every proc roll after
            // it and move stands that have nothing to do with charges (MECHANICS 151).
            Random chargeRng = new Random(opt.Seed * 31 + 17);

            List<Event> queue = new List<Event>();
            queue.Add(new Event(0, 0, 0));
            // UNTIL WHEN THE CHARACTER IS BUSY WITH AN ANIMATION (MECHANICS 87).
            //
            // The user's observation of 13 September: "our timeline cannot apply a blast, a
            // spit and an autoattack all in the same unit of time… and for the character, it
            // seems to me, that applies too". Before this a swing and a press each ran in
            // their own stream and coincided freely. Now a swing or press that comes up
            // before this mark is moved to it. Procs, ticks, auras and stars take no time
            // and do not wait — they are not animated.
            double busyUntil = 0;
            // ORDER FOR A PET'S SKILLS moved to the end of an animation: at the same instant
            // they stand BEFORE its autoattack — the AI strikes with a skill as soon as it is
            // ready and with claws in between (the user's words: "spit — 2 autoattacks —
            // spit").
            int petFirst = int.MinValue / 2;
            int order = 0;
            // Live dots by source. The key is hand plus type: see the `Dot` class.
            Dictionary<string, Dot> dots = new Dictionary<string, Dot>(StringComparer.Ordinal);

            // PRESSED SKILLS. At the start all are pressed at once (`timeline.txt`, item 1),
            // after that each on its own cooldown.
            // SELF-FIRING ONES. The user does not press them — they go by themselves, and
            // must enter the queue without his involvement (PLAN, section Zh).
            //
            //   WEAPON POOL     `Skill_WPAttack*`: REPLACES the default attack, the shares
            //                   split one hundred, at most one fires
            //   ON-HIT PROC     `onHitActivationChance` and gear skills with an "on attack"
            //                   condition: each has its own roll, on EVERY swing — misses
            //                   included (the "miss + meteor" measurement)
            //
            // Devotion belongs here too once the user ties a star: its chance is computed by
            // the same formula (`AutoChance`, MECHANICS 18b).
            List<AttackSkill> pool = new List<AttackSkill>();
            // THE ASSIGNED ATTACK and its charges — state for the whole run.
            AttackSkill assigned = null;
            int charge = 0;
            double chargeAt = -1e9;
            if (!string.IsNullOrEmpty(opt.Attack))
                foreach (AttackSkill a in AttackSkills.Of(game, c))
                    if (a.Assigned &&
                        string.Equals(a.Dbr, opt.Attack, StringComparison.OrdinalIgnoreCase))
                    { assigned = a; break; }

            // A CHANNELLED SKILL INSTEAD OF THE AUTOATTACK — task item 1: "the user picks
            // the main active skill… or the autoattack". While the beam is held the
            // character does not strike with the weapon: there are no swings, hence no
            // weapon pool either (it replaces the swing itself). A tick comes once per
            // interval by cast speed (MECHANICS 74).
            //
            // Until 10 September a channel was pressed like an ordinary skill — on a
            // "cooldown" of 0.3 with reduction (0.255 on `_Satirita`) and TOGETHER with the
            // autoattack. Both are wrong: its tick is 0.12 at 200% cast speed, and there is
            // no autoattack.
            //
            // THE SAME GOES FOR A SPELL WITHOUT A COOLDOWN THAT IS PRESSED CONTINUOUSLY
            // (Panetti's Replicating Missile, Bloody Pox). Its interval is the length of the
            // cast animation at the current cast speed (MECHANICS 74), and while the
            // animation runs the weapon does not strike. Pressing it continuously AND
            // swinging means three streams of action for one character: there is no time for
            // that in game. So "continuously" is for the main skill only; among the ticked
            // ones it stays an "insert between strikes" with its own interval.
            AttackSkill mainSpell = null;
            double mainAnim = 0;
            // ...AND A DAMAGING SKILL **WITH A COOLDOWN** INSTEAD OF THE AUTOATTACK — the
            // user's request of 16 October: "we pick, in the list where the autoattack is
            // picked, a skill that can deal damage (not an aura), instead of the autoattack".
            //
            // WHY IT WAS MISSING AND WHAT IT COST. Until today the list took only a channel
            // and a spell without a cooldown; a skill with one was offered as a TICKED press
            // and always went BESIDE the weapon swings. So the case "the character strikes
            // with this and with nothing else" could not be expressed at all — and on
            // 16 October that cost a false conclusion: a "control" run with
             // an ordinary `--attack` printed "we strike with the weapon, as without a
            // choice", gave the same numbers, and the suspect ("the autoattack steals the
            // animation") was written down as refuted when it had never been tested
            // (MECHANICS 151a).
            AttackSkill mainHit = null;
            double mainHitCd = 0;
            if (!string.IsNullOrEmpty(opt.Attack) && assigned == null)
                foreach (AttackSkill a in AttackSkills.Of(game, c))
                {
                    if (!string.Equals(a.Dbr, opt.Attack, StringComparison.OrdinalIgnoreCase)) continue;
                    if (a.ChannelMs > 0) { mainSpell = a; break; }
                    double mcd = SkillTiming.Cooldown(game, c, totals, AttackSkills.Attach.Of(game, c),
                                                      ItemSkillMods.Of(game, c), SkillText.LevelBonus.Of(game, c),
                                                      a.Dbr, a.Level);
                    if (mcd <= 0.01)
                    {
                        mainAnim = SkillHands.CastSeconds(game, game.Record(a.Dbr), c);
                        if (mainAnim > 0) mainSpell = a;
                    }
                    // A cooldown of its own is no longer a refusal: the skill becomes the
                    // only action, swings and all are dropped below, and it is pressed by its
                    // cooldown — with its charges and its chance to reset, like any press.
                    else if (!IsBuffSkill(game, a.Dbr)) { mainHit = a; mainHitCd = mcd; }
                    if (mainSpell == null && mainHit == null)
                        run.Hints.Add(Lang.T("log.hint.mainNotSpell", a.Name));
                    break;
                }
            // THE WEAPON STOPS SWINGING FOR BOTH KINDS OF "instead of the autoattack": a
            // channel and a spell without a cooldown held continuously, and now a skill with
            // a cooldown chosen as the only thing we strike with.
            if (mainHit != null)
                queue.RemoveAll(delegate (Event x) { return x.What == Kind.Swing; });
            if (mainSpell != null)
            {
                queue.RemoveAll(delegate (Event x) { return x.What == Kind.Swing; });
                run.MainSpell = mainSpell.Name;
                run.MainSpellWord = mainSpell.ChannelMs > 0 ? Lang.T("log.fire.channel") : Lang.T("log.fire.spell");
                run.CastSpeedPercent = panel.CastSpeedPercent;
                run.MainSpellInterval = mainSpell.ChannelMs > 0
                    ? Combat.ChannelInterval(game, mainSpell.ChannelMs, panel.CastSpeedPercent)
                    : panel.CastInterval(mainAnim);
            }

            // AURAS' RESISTANCE REDUCTION — BEFORE THE FIRST STRIKE.
            //
            // Auras stand from the start of the fight, so the debuff on the target stands
            // from the start too. The target is EDITED in place: the check computes its
            // expectation from the same target after the run, and two different sets of
            // resistances would set them apart. The same target must not be passed twice —
            // the cut would be doubled.
            foreach (ResistCut cut in AuraResistCuts(game, c, ActiveSkills.Of(c.Name),
                                                     AttackSkills.Attach.Of(game, c),
                                                     SkillText.LevelBonus.Of(game, c)))
            {
                double had;
                target.Resist.TryGetValue(cut.Type, out had);
                target.Resist[cut.Type] = had - cut.Amount;
                // AND THE DOT OF THE SAME ROOT — the game's rule: "resistance reduction
                // applies both to one-off damage and to the dot of its type"
                // (MECHANICS 7). The target holds the dot's resistance under a separate
                // key, and both must be cut; otherwise the aura's direct damage would grow
                // and its own dot would not.
                double hadDot;
                target.Resist.TryGetValue(cut.Type + "Dot", out hadDot);
                target.Resist[cut.Type + "Dot"] = hadDot - cut.Amount;
                run.ResistCuts.Add(TypeName(cut.Type) + ": " + Fmt(had) + "% → " +
                                   Fmt(had - cut.Amount) + "%   (−" + Fmt(cut.Amount) +
                                   " from \"" + cut.From + "\", and as much off the dot)");
            }

            // STRIKING AURAS. Each gets its own event once a second, starting from zero:
            // they already stand when the fight begins rather than switching on later.
            List<Aura> auras = new List<Aura>();
            foreach (AuraFound af in Auras(game, c, ActiveSkills.Of(c.Name),
                                           AttackSkills.Attach.Of(game, c),
                                           SkillText.LevelBonus.Of(game, c)))
            {
                Aura ring = new Aura();
                ring.Skill = af.Skill; ring.Name = af.Name; ring.From = af.From;
                auras.Add(ring);
            }

            // CHANCE-BASED COOLDOWN RESETS — collected once per run and rolled on every
            // strike, together with the procs (see `Refresh`).
            run.Refreshers = RefreshersOf(game, c);
            List<Proc> procs = new List<Proc>();
            // Stars get their own seed: roll them from the shared `procRng` and adding a
            // star would shift every proc roll, i.e. the run would stop repeating because
            // of a change that does not concern it.
            StarBook book = new StarBook();
            book.Rng = new Random(opt.Seed * 31 + 7);
            book.Buffs = buffs;
            book.Debuffs = debuffs;
            {
                AttackSkills.Attach pAttach = AttackSkills.Attach.Of(game, c);
                ItemSkillMods pMods = ItemSkillMods.Of(game, c);
                SkillText.LevelBonus pBonus = SkillText.LevelBonus.Of(game, c);
                foreach (AttackSkill a in AttackSkills.Of(game, c))
                {
                    if (a.PoolChance > 0.0001) { pool.Add(a); continue; }
                    bool onAttack = a.HitChance > 0.0001 ||
                                    a.AutoTrigger == "AttackEnemy" || a.AutoTrigger == "AttackEnemyCrit";
                    if (!onAttack) continue;
                    Proc pr = new Proc();
                    pr.Skill = a;
                    pr.Chance = a.HitChance > 0.0001 ? a.HitChance : a.AutoChance;
                    pr.Cooldown = SkillTiming.Cooldown(game, c, totals, pAttach, pMods, pBonus,
                                                       a.Dbr, a.Level);
                    // "ON CRITICAL ATTACK" MEANS ON A CRIT ONLY. Until 10 September such a
                    // gear proc was rolled on EVERY strike: `Procs` knew nothing of crits,
                    // and the check erred the same way and so kept silent. For devotion
                    // stars this had long been done — for gear it had not.
                    pr.OnlyCrit = a.AutoTrigger == "AttackEnemyCrit";
                    pr.Name = a.Name; pr.Dbr = a.Dbr; pr.Level = a.Level;
                    procs.Add(pr);
                }
                // AND THE NON-STRIKING GEAR AND SET SKILLS "ON ATTACK"/"ON CRIT": a summon
                // (Conjure Living Blossom), a debuff (Annihilation), a buff. The roll and
                // cooldown are real, the log line is there, and the debuff is applied.
                foreach (GearProc gp in GearSilent(game, c, totals))
                {
                    Proc pr = new Proc();
                    pr.Chance = gp.Chance; pr.Cooldown = gp.Cooldown; pr.OnlyCrit = gp.OnlyCrit;
                    pr.NoDamage = gp.Kind; pr.Name = gp.Name; pr.Dbr = gp.Dbr; pr.Level = gp.Level;
                    pr.Duration = gp.Duration; pr.Activator = gp.Source;
                    procs.Add(pr);
                }

                // DEVOTION STARS GO NOT INTO THE COMMON PROC LIST BUT TO THEIR OWN SKILL.
                //
                // A star is tied to its ACTIVATOR SKILL and fires only when that skill
                // strikes. The user's remark of 7 September: "I do not see Arcane Bomb
                // firing after Shadow Strike fires" — while it was raining on every swing,
                // i.e. on other skills' strikes.
                //
                // The parsing is shared with the check (`DevotionOnAttack`) on purpose:
                // while there were two, the check knew nothing of stars at all and compared
                // an Acid Spray tick against a bracket that lacks that source.
                foreach (Star st in DevotionOnAttack(game, c, totals, pAttach, pMods, pBonus))
                {
                    Proc pr2 = new Proc();
                    pr2.Chance = st.Chance;
                    pr2.NoDamage = st.NoDamage;
                    pr2.Name = st.Name;
                    pr2.Skill = st.Skill;
                    pr2.Cooldown = st.Cooldown;
                    pr2.OnlyCrit = st.Trigger == "AttackEnemyCrit";
                    pr2.OnCastBuff = st.Trigger == "CastBuff";
                    pr2.Activator = st.ActivatorName;
                    pr2.ActivatorClass = st.ActivatorClass;
                    pr2.Level = st.Level;
                    pr2.Dbr = st.Dbr;
                    pr2.Duration = st.Duration;
                    // The fact of firing is honest, the consequences are not, and that must
                    // be said out loud, or the log line reads as "counted".
                    // A SUMMON — since 12 September the pet enters the pool and strikes
                    // (E7). The old caveat ("summoned ones deal no damage") became untrue
                    // and was removed; what it actually did is visible in the lines and in
                    // the totals.
                    if (st.NoDamage == EffectKind.Summon) { }
                    else if (st.NoDamage == EffectKind.Buff)
                    {
                        // A buff with a duration stands and TAKES EFFECT; without one there
                        // is nothing to hold. Before, the second branch caught the first
                        // case too and said "no damage read" about it — untrue.
                        if (st.Duration <= 0.0001)
                            // Service detail hidden 17 September (the user asked). The
                            // wording was: "the star '…' applies a BUFF (" + st.Class + "),
                            // but the record has no duration (`skillActiveDuration`) — …".
                            run.Unread.Add(Lang.T("log.unread.starBuffNoDuration", st.Name));
                    }
                    // A DEBUFF IS NOT "NOT READ": it is applied to the target and counted
                    // (`SilentEffect`). Until 15 September the note was printed for it too —
                    // Assassin's Mark on `_Hamarina` was called unread although it did
                    // reduce resistances (MECHANICS 90b).
                    else if (st.NoDamage != null && st.NoDamage != EffectKind.Debuff)
                        // The record class hidden 17 September (the user asked). The wording
                        // was: "for the star '…' (" + st.Class + ") we read no damage: …".
                        run.Unread.Add(Lang.T("log.unread.starNoDamage", st.Name));
                    List<Proc> bound;
                    if (!book.By.TryGetValue(st.Activator, out bound))
                    { bound = new List<Proc>(); book.By[st.Activator] = bound; }
                    bound.Add(pr2);
                    book.Whose[pr2] = st;
                }
            }

            List<Press> presses = new List<Press>();
            if (opt.Pressed.Count > 0)
            {
                AttackSkills.Attach attach = AttackSkills.Attach.Of(game, c);
                ItemSkillMods itemMods = ItemSkillMods.Of(game, c);
                SkillText.LevelBonus levelBonus = SkillText.LevelBonus.Of(game, c);
                foreach (AttackSkill a in AttackSkills.Of(game, c))
                {
                    if (!opt.Pressed.Contains(a.Dbr)) continue;
                    // A buff is not pressed as a strike — it belongs to the timed buffs.
                    if (IsBuffSkill(game, a.Dbr))
                    {
                        // The command-line switch hidden 17 September (the user asked). The
                        // wording was: "… Tick it among the buffs (or `--timedbuff=`)".
                        run.Hints.Add(Lang.T("log.hint.buffPressed", a.Name));
                        continue;
                    }
                    // A channel chosen as the main skill is not pressed separately as well —
                    // it is the same skill, and it cannot be held twice.
                    if (mainSpell != null &&
                        string.Equals(a.Dbr, mainSpell.Dbr, StringComparison.OrdinalIgnoreCase)) continue;
                    // The same for a cooldown skill chosen instead of the autoattack: it is
                    // queued once, below, and a ticked copy would press it twice.
                    if (mainHit != null &&
                        string.Equals(a.Dbr, mainHit.Dbr, StringComparison.OrdinalIgnoreCase)) continue;
                    // A CHANNEL TICKED FOR PRESSING TOGETHER WITH THE AUTOATTACK does not
                    // happen in game: while the beam is held the weapon does not strike. The
                    // window does not allow that choice (a channel sits under "what we
                    // strike with"), the command line can — and then the run must say that
                    // it computed an impossible fight.
                    if (a.ChannelMs > 0)
                        // The command-line switch hidden 17 September (the user asked). The
                        // wording was: "… Choose it as the main one (`--attack=" + a.Name +
                        // "`)".
                        run.Hints.Add(Lang.T("log.hint.channelWithAttack", a.Name));
                    Press p = new Press();
                    p.Skill = a;
                    p.Cooldown = SkillTiming.Cooldown(game, c, totals, attach, itemMods,
                                                      levelBonus, a.Dbr, a.Level);
                    SkillTiming.Charges(game, c, attach, itemMods, levelBonus, a.Dbr, a.Level,
                                        out p.Charges, out p.ResetChance);
                    p.Stored = p.Charges;
                    DbRecord rec = game.Record(a.Dbr);
                    // Which hands strike — by the skill's ANIMATION, the same marker the
                    // panel decides by (MECHANICS 14r). A skill has no 50/25/25 layout: one
                    // that strikes with two always strikes with both (MECHANICS 38).
                    p.Hands = rec == null ? Hands.Main : SkillHands.Of(game, rec, c);
                    double every;
                    if (opt.PressEvery.TryGetValue(a.Dbr, out every)) p.EverySecs = every;
                    // No cooldown of its own and no interval set — go by the animation.
                    if (p.Cooldown <= 0.01 && p.EverySecs <= 0.01)
                        p.AnimSecs = SkillHands.CastSeconds(game, rec, c);
                    presses.Add(p);
                    // CHARGES AND THE RESET CHANCE GO INTO THE LIST OF PRESSED SKILLS, not
                    // only into the arithmetic: they decide the rate, and a report that keeps
                    // quiet about them leaves "why so many presses" unanswerable (rule 23 —
                    // check both ends, the showing and the counting).
                    string burst = (p.Charges > 1 ? ", charges " + p.Charges : "") +
                                   (p.ResetChance > 0.0001
                                        ? ", " + p.ResetChance.ToString("0.#", CultureInfo.InvariantCulture) +
                                          "% chance to reset the cooldown" : "");
                    run.PressedNames.Add(a.Name + burst +
                        (p.Cooldown > 0.01
                             ? " (cooldown " + p.Cooldown.ToString("0.##", CultureInfo.InvariantCulture) + " s)"
                             : p.EverySecs > 0.01
                                 ? " (no cooldown, pressed once every " +
                                   p.EverySecs.ToString("0.##", CultureInfo.InvariantCulture) + " s)"
                                 : p.AnimSecs > 0
                                     ? " (no cooldown — by the cast animation of " +
                                       p.AnimSecs.ToString("0.###", CultureInfo.InvariantCulture) +
                                       " s: at " + Math.Round(panel.CastSpeedPercent).ToString(CultureInfo.InvariantCulture) +
                                       "% cast speed, once every " +
                                       panel.CastInterval(p.AnimSecs).ToString("0.###", CultureInfo.InvariantCulture) + " s)"
                                     : " (no cooldown, pressed ONCE — no animation found, set how often to press)"));
                    queue.Add(new Event(0, 0, ++order, p));
                }
                // BEYOND THE DAMAGING SKILLS — the striking stars whose condition we do not
                // roll, and the pressed ones without damage (MECHANICS 76).
                foreach (Extra x in PressExtras(game, c))
                {
                    if (!opt.Pressed.Contains(x.Dbr)) continue;
                    Press p = new Press();
                    if (x.Skill != null)
                    {
                        p.Skill = x.Skill;
                        DbRecord xr = game.Record(x.Dbr);
                        p.Hands = xr == null ? Hands.Main : SkillHands.Of(game, xr, c);
                    }
                    else
                    {
                        p.SilentKind = x.Kind; p.SilentDbr = x.Dbr; p.SilentName = x.Name;
                        p.SilentLevel = x.Level;
                        p.SilentSecs = SkillTiming.Duration(game, x.Dbr, x.Level);
                    }
                    p.Cooldown = SkillTiming.Cooldown(game, c, totals, attach, itemMods, levelBonus, x.Dbr, x.Level);
                    SkillTiming.Charges(game, c, attach, itemMods, levelBonus, x.Dbr, x.Level,
                                        out p.Charges, out p.ResetChance);
                    p.Stored = p.Charges;
                    double every;
                    if (opt.PressEvery.TryGetValue(x.Dbr, out every)) p.EverySecs = every;
                    presses.Add(p);
                    run.PressedNames.Add(x.Name + " (" + (x.Skill != null ? "a star, and it strikes" : EffectWord(x.Kind)) +
                        (string.IsNullOrEmpty(x.When) ? "" : ", the condition \"" + x.When + "\" is not rolled") +
                        (p.Cooldown > 0.01
                             ? ", cooldown " + p.Cooldown.ToString("0.##", CultureInfo.InvariantCulture) + " s)"
                             : p.EverySecs > 0.01
                                 ? ", pressed once every " + p.EverySecs.ToString("0.##", CultureInfo.InvariantCulture) + " s)"
                                 : ", no cooldown — pressed once)"));
                    queue.Add(new Event(0, 0, ++order, p));
                }
            }

            // BUFFS ON COOLDOWN go into the queue like pressed skills.
            //
            // At the start all are raised at once (the same rule as for pressed ones, task
            // item 1), after that each on its own cooldown. The duration is its own:
            // Ghoulish Hunger has 5 seconds out of 27.9.
            List<Press> timed = new List<Press>();
            if (opt.TimedBuffs.Count > 0)
            {
                AttackSkills.Attach tAttach = AttackSkills.Attach.Of(game, c);
                ItemSkillMods tMods = ItemSkillMods.Of(game, c);
                SkillText.LevelBonus tBonus = SkillText.LevelBonus.Of(game, c);
                foreach (string dbr in opt.TimedBuffs)
                {
                    if (game.Record(dbr) == null) continue;
                    int lvl = 0;
                    foreach (Skill s in c.Skills)
                        if (string.Equals(s.Name, dbr, StringComparison.OrdinalIgnoreCase))
                        { lvl = s.DevotionLevel > 0 ? s.DevotionLevel
                                                    : s.Level + tBonus.For(s.Name, s.Level); break; }
                    // A buff FROM GEAR takes the item's own level (MECHANICS 58a), not one.
                    if (lvl <= 0)
                        foreach (AttackSkills.GrantedSkill g in AttackSkills.GearGranted(game, c))
                            if (string.Equals(g.Dbr, dbr, StringComparison.OrdinalIgnoreCase)) { lvl = g.Level; break; }
                    if (lvl <= 0) lvl = 1;
                    // A star that STRIKES is not raised as a buff — its place is among the
                    // presses (`PressExtras`), where it strikes with its own hit.
                    if (SkillsForm.IsDevotion(dbr) && AttackSkills.One(game, dbr, lvl, "devotion") != null)
                    {
                        run.Hints.Add(Lang.T("log.hint.starHits", SkillText.Name(game, dbr) ?? dbr));
                        continue;
                    }
                    Press up = new Press();
                    up.BuffDbr = dbr;
                    up.Note = SkillText.Name(game, dbr) ?? dbr;
                    up.Cooldown = SkillTiming.Cooldown(game, c, baseTotals, tAttach, tMods, tBonus, dbr, lvl);
                    up.Secs = SkillTiming.Duration(game, dbr, lvl);
                    // WITHOUT A DURATION THERE IS NOTHING TO HOLD — an absorb shield (Turtle
                    // Shell) lives until it is broken. But the FIRING on cooldown is shown,
                    // as for any buff (the user's request of 12 September). If something
                    // without a duration turns out to CHANGE damage, say so out loud: its
                    // additions do not act in the queue.
                    if (up.Secs <= 0)
                    {
                        DbRecord hr = game.Record(dbr);
                        double absorb = hr == null ? 0
                                      : AttackSkill.Value(hr, "damageAbsorption", SkillText.Slot(hr, lvl));
                        bool affects = AffectsDamage(game, dbr);
                        up.NoHold = (absorb > 0 ? Lang.T("log.buff.shield", Fmt0(absorb))
                                                : Lang.T("log.buff.noDuration")) + "; " +
                                    (affects ? Lang.T("log.buff.notInQueue") : Lang.T("log.buff.noEffect"));
                        if (affects)
                            run.Unread.Add(Lang.T("log.unread.buffNoDuration", up.Note));
                    }
                    timed.Add(up);
                    queue.Add(new Event(0, 0, ++order, up));
                }
            }

            // AURAS INTO THE QUEUE — all at second zero. They already stand when the fight
            // begins and strike ALL AT ONCE, each with its own number: the user's
            // measurement on `_Amakata` — "once per 1 sec I see 2 different damage figures".
            foreach (Aura ring in auras) queue.Add(new Event(0, 0, ++order, ring));

            // The main skill's first strike is at the start, like every press (task item 1).
            // THE ONLY THING WE STRIKE WITH, AND IT HAS A COOLDOWN. It is an ordinary press
            // in every respect — animation, charges, the reset roll — only there is nothing
            // beside it: the swings were dropped above. `Main` is NOT set, on purpose: that
            // mark means "a stream of action with its own tick", and this one waits for its
            // cooldown like a button.
            if (mainHit != null)
            {
                Press only = new Press();
                only.Skill = mainHit;
                only.Cooldown = mainHitCd;
                SkillTiming.Charges(game, c, AttackSkills.Attach.Of(game, c), ItemSkillMods.Of(game, c),
                                    SkillText.LevelBonus.Of(game, c), mainHit.Dbr, mainHit.Level,
                                    out only.Charges, out only.ResetChance);
                only.Stored = only.Charges;
                DbRecord orec = game.Record(mainHit.Dbr);
                only.Hands = orec == null ? Hands.Main : SkillHands.Of(game, orec, c);
                presses.Add(only);
                run.PressedNames.Add(mainHit.Name + " — instead of the weapon, nothing else strikes" +
                                     (only.Charges > 1 ? ", charges " + only.Charges : "") +
                                     (only.ResetChance > 0.0001
                                          ? ", " + only.ResetChance.ToString("0.#", CultureInfo.InvariantCulture) +
                                            "% chance to reset the cooldown" : "") +
                                     " (cooldown " + mainHitCd.ToString("0.##", CultureInfo.InvariantCulture) + " s)");
                queue.Add(new Event(0, 0, ++order, only));
            }
            if (mainSpell != null)
            {
                Press ch = new Press();
                ch.Skill = mainSpell;
                ch.Main = true;
                ch.Channel = mainSpell.ChannelMs > 0;
                ch.AnimSecs = mainAnim;
                DbRecord crec = game.Record(mainSpell.Dbr);
                ch.Hands = crec == null ? Hands.Main : SkillHands.Of(game, crec, c);
                queue.Add(new Event(0, 0, ++order, ch));
            }

            // PETS (E7, task item 9). The pool is built only if there is a reason: either
            // the user ticked somebody, or a summon may fire by itself during the run (a
            // star or a gear skill). Otherwise it is wasted work on every run — `PetPool.Of`
            // computes bonuses, conversion and modifiers.
            List<TimelinePets.Kind> petKinds = new List<TimelinePets.Kind>();
            Dictionary<string, TimelinePets.Kind> petBySummon =
                new Dictionary<string, TimelinePets.Kind>(StringComparer.OrdinalIgnoreCase);
            List<PetLive> petsAlive = new List<PetLive>();
            Random petRng = new Random(opt.Seed * 31 + 13);
            // A PET'S NUMBERS UNDER THE LIVE GIVERS OF ALLIED AURAS (MECHANICS 89a). One
            // rebuild per set of givers, and it is remembered; the kinds in it are the same
            // summons, with the auras' percents in their bonuses. If nobody carries an aura,
            // the kind's own numbers are used.
            Dictionary<string, Dictionary<string, TimelinePets.Kind>> petVariants =
                new Dictionary<string, Dictionary<string, TimelinePets.Kind>>(StringComparer.Ordinal);
            Func<TimelinePets.Kind, TimelinePets.Kind> petNumbers = delegate (TimelinePets.Kind kind)
            {
                List<TimelinePets.Kind> givers = new List<TimelinePets.Kind>();
                List<string> names = new List<string>();
                foreach (PetLive p in petsAlive)
                    if (!p.Gone && p.Kind.Auras.Count > 0 && !givers.Contains(p.Kind))
                    {
                        givers.Add(p.Kind);
                        names.Add(p.Kind.Summon);
                    }
                if (givers.Count == 0) return kind;
                names.Sort(StringComparer.Ordinal);
                string key = string.Join("|", names.ToArray());
                Dictionary<string, TimelinePets.Kind> made;
                if (!petVariants.TryGetValue(key, out made))
                {
                    made = new Dictionary<string, TimelinePets.Kind>(StringComparer.OrdinalIgnoreCase);
                    Dictionary<string, double> pct = TimelinePets.AuraPercents(game, givers);
                    if (pct.Count > 0)
                    {
                        foreach (TimelinePets.Kind v in TimelinePets.Of(game, c, baseTotals, ActiveSkills.Of(c.Name),
                                                                        opt.PetsChosen ? opt.Pets : null, null, pct))
                            made[v.Summon] = v;
                        List<string> said = new List<string>(), who = new List<string>();
                        foreach (KeyValuePair<string, double> kv in pct) said.Add(kv.Key + " +" + Fmt(kv.Value) + "%");
                        foreach (TimelinePets.Kind g in givers) who.Add("«" + g.Name + "»");
                        run.PetLines.Add(Lang.T("log.petLine.auras", string.Join(", ", who.ToArray()),
                                                string.Join(", ", said.ToArray())));
                    }
                    petVariants[key] = made;
                }
                TimelinePets.Kind got;
                return made.TryGetValue(kind.Summon, out got) ? got : kind;
            };
            {
                bool anySummon = opt.Pets.Count > 0;
                if (!anySummon)
                    foreach (Proc pr in procs) if (pr.NoDamage == EffectKind.Summon) { anySummon = true; break; }
                if (!anySummon)
                    foreach (KeyValuePair<string, List<Proc>> kv in book.By)
                        foreach (Proc pr in kv.Value) if (pr.NoDamage == EffectKind.Summon) { anySummon = true; break; }
                // The window said nothing — so we ask the pets window, and then the pool is
                // needed by anyone who has a summon at all (permanent pets always stand by;
                // nobody "calls" them during the fight).
                if (!anySummon && !opt.PetsChosen) anySummon = PetPool.AnySummon(game, c);
                if (anySummon)
                {
                    petKinds = TimelinePets.Of(game, c, baseTotals, ActiveSkills.Of(c.Name),
                                               opt.PetsChosen ? opt.Pets : null, run.Unread);
                    // THE PETS' OWN ALLIED-AURA PERCENTS — WHILE THE GIVER LIVES
                    // (MECHANICS 89a). A pet's damage multiplier is computed from its
                    // bonuses, and an aura's percents can only reach it through a rebuild
                    // (MECHANICS 81). Until 14 September there was one rebuild for the whole
                    // run, and it took the auras only of pets summoned by button: a
                    // temporary pet summoned by a trigger gave its aura to nobody, while a
                    // permanent one gave it even before being summoned. Now the kinds here
                    // are aura-free, and a pet's strike takes its numbers from the rebuild
                    // under the givers alive at that instant (`petNumbers`).
                    foreach (TimelinePets.Kind pk in petKinds)
                    {
                        petBySummon[pk.Summon] = pk;
                        // Ticked ones are called at once, like everything else at the start
                        // (task item 1), after that on the summon's cooldown.
                        if (pk.ByHand) queue.Add(new Event(0, 0, ++order, pk));
                    }
                }
            }

            List<double> perSecond = new List<double>();
            int toldSecond = -1;      // up to which second progress was already reported
            double lifeAt = 0;        // time of the last processed event — for TTK
            run.TargetLife = target.Life;
            run.TargetIsDummy = target.IsDummy;
            run.PercentLifeResist = target.PercentLifeResist;
            while (queue.Count > 0)
            {
                // The nearest event; at an equal moment, by the order it was queued.
                int pick = 0;
                for (int i = 1; i < queue.Count; i++)
                    if (queue[i].At < queue[pick].At ||
                        (queue[i].At == queue[pick].At && queue[i].Order < queue[pick].Order)) pick = i;
                Event e = queue[pick];
                queue.RemoveAt(pick);
                // SUMMONS THAT FIRED BY THEMSELVES on the previous step go into the pool
                // (E7). Here rather than inside `SilentEffect`: that has neither the event
                // queue nor the order counter. Their time is their own, recorded when they
                // fired.
                while (book.Summons.Count > 0)
                {
                    KeyValuePair<string, double> want = book.Summons[0];
                    book.Summons.RemoveAt(0);
                    TimelinePets.Kind pk2;
                    if (!petBySummon.TryGetValue(want.Key, out pk2))
                    {
                        // The summon fired but its kind is not in the pool — we must not keep
                        // silent: this is exactly the case where damage vanishes unnoticed.
                        string who = SkillText.Name(game, want.Key) ?? want.Key;
                        if (!run.DotWarned.Contains(SummonTag + want.Key))
                        {
                            run.DotWarned.Add(SummonTag + want.Key);
                            run.Unread.Add(Lang.T("log.unread.summonNoPet", who));
                        }
                        continue;
                    }
                    Event at2 = new Event(want.Value, 0, 0);
                    Call(run, opt, queue, dots, ref order, at2, pk2, petsAlive, Lang.T("log.pet.how.self"));
                    PetDebuffs(run, opt, game, target, debuffs, at2, pk2, petsAlive);
                    PetAuras(run, opt, buffs, at2, pk2, petsAlive);
                }
                // THE RUN IS THE HALF-OPEN INTERVAL [0, Seconds).
                //
                // An event EXACTLY on the last second is not ours: a 20-second run has
                // exactly twenty per-second sums (cells 0…19), and a strike at 20.00 started
                // a twenty-first — which came out nearly empty and dragged the average down.
                // On swings this was invisible (they rarely land on a round number), but an
                // aura strikes EXACTLY on the second: 21 strikes instead of twenty, and −19%
                // in the comparison. Dot ticks behave the same way — so the error predated
                // the auras, there was just nothing to notice it with.
                if (e.At >= opt.Seconds) break;
                // TTK IS THE MOMENT WHEN EVERYTHING THAT LANDED EQUALS THE TARGET'S LIFE.
                // The run goes on as before (damage per second is still measured), but there
                // is nothing left to take a percent of life from (MECHANICS 77).
                if (target.Life > 0 && run.KillAt < 0 && run.Total >= target.Life)
                {
                    run.KillAt = lifeAt;
                    Add(run, opt, lifeAt, LogKind.Life, Lang.T("log.life.killed"),
                        Lang.T("log.life.killedWhy", Fmt(lifeAt), Fmt0(run.Total), Fmt0(target.Life)), 0, 0, null, null);
                }
                lifeAt = e.At;

                // WHATEVER EXPIRED AMONG THE REDUCED RESISTANCES goes back to the target.
                debuffs.Expire(target, e.At);

                // PROGRESS — once a second, no more often.
                if (opt.OnSecond != null && (int)e.At > toldSecond)
                { toldSecond = (int)e.At; opt.OnSecond(e.At); }

                // THIS MOMENT'S NUMBERS. The buff set may have changed since the previous
                // event — then the character is rebuilt whole and at once. An identical set
                // is computed once and remembered.
                {
                    string key = buffs.Key(e.At);
                    Loadout cur;
                    if (!buffs.Cache.TryGetValue(key, out cur))
                    {
                        // PETS' AURAS SIT IN THE SAME KEY AS BUFFS, but they are not the
                        // character's skills: `ActiveSkills` knows nothing of them. So they
                        // carry a prefix and are taken out of the set before it goes into
                        // the totals (MECHANICS 81).
                        List<string> on = new List<string>(), petAuras = new List<string>();
                        foreach (string one in key.Split('|'))
                        {
                            if (one.StartsWith(PetAuraTag, StringComparison.Ordinal)) petAuras.Add(one);
                            else on.Add(one);
                        }
                        cur = new Loadout();
                        cur.Totals = CharacterTotals.Of(game, c, seeded,
                                                        ActiveSkills.Of(c.Name)
                                                                    .With(on, opt.TimedBuffs));
                        foreach (string one in petAuras)
                        {
                            string body = one.Substring(PetAuraTag.Length);
                            int at = body.LastIndexOf('@');
                            string auraDbr = at > 0 ? body.Substring(0, at) : body;
                            int auraLevel;
                            if (at < 0 || !int.TryParse(body.Substring(at + 1), out auraLevel)) auraLevel = 1;
                            string title = SkillText.Name(game, auraDbr);
                            cur.Totals.AddPetAura(game, auraDbr, auraLevel, "pet aura: " +
                                (string.IsNullOrEmpty(title) ? auraDbr : title));
                        }
                        cur.Panel = new DamageModel(game, c, cur.Totals, null, true, seeded);
                        double oa2 = Combat.Ability(game, c, cur.Totals, c.Cunning,
                                                    "characterOffensiveAbility", "characterDexterity");
                        double p2 = Combat.Pth(game,oa2, target.DefensiveAbility);
                        if (p2 < floorPth) p2 = floorPth;
                        cur.Oa = oa2;
                        cur.Pth = p2;
                        TotalsEntry ce;
                        cur.CritDamage = cur.Totals.Fields.TryGetValue("offensiveCritDamageModifier", out ce)
                                       ? ce.Value : 0;
                        cur.Below = Combat.BelowThreshold(game, p2);
                        buffs.Cache[key] = cur;
                        run.Loadouts++;
                        run.Panels.Add(cur.Panel);
                    }
                    totals = cur.Totals; panel = cur.Panel;
                    pth = cur.Pth; critDamage = cur.CritDamage; below = cur.Below;

                    // THE TARGET'S CUT DEFENSIVE ABILITY — laid over the set of numbers.
                    //
                    // It changes more often than buffs (every strike carrying such a cut
                    // applies it) and belongs to the TARGET, not to the character — hence it
                    // is computed here rather than remembered in the set.
                    // Flat resistance reduction goes straight into the target, where it is
                    // applied last (see `TimelineTarget.Through`).
                    target.AbsoluteCut = debuffs.AbsoluteCut(e.At);
                    target.RelativeCut = debuffs.RelativeCut(e.At);
                    target.ArmorCut = debuffs.ArmorCut(e.At);
                    if (target.AbsoluteCut > run.AbsoluteCutSeen)
                        run.AbsoluteCutSeen = target.AbsoluteCut;

                    double daCut = debuffs.DefenceCut(e.At);
                    if (daCut > 0.0001)
                    {
                        double da = target.DefensiveAbility - daCut;
                        if (da < 0) da = 0;
                        pth = Combat.Pth(game,cur.Oa, da);
                        if (pth < floorPth) pth = floorPth;
                        below = Combat.BelowThreshold(game, pth);
                        if (daCut > run.DefenceCutSeen) run.DefenceCutSeen = daCut;
                    }
                }

                // SUMMONING A PET — by tick, on the summon skill's cooldown.
                if (e.What == Kind.Summon)
                {
                    TimelinePets.Kind pk = e.Summon;
                    if (pk == null) continue;
                    Call(run, opt, queue, dots, ref order, e, pk, petsAlive, Lang.T("log.pet.how.pressed"));
                    PetDebuffs(run, opt, game, target, debuffs, e, pk, petsAlive);
                    PetAuras(run, opt, buffs, e, pk, petsAlive);
                    // The user goes on pressing the summon: the slot frees up when the pet
                    // leaves at the end of its lifetime.
                    if (pk.ByHand && pk.SummonCooldown > 0.01)
                        queue.Add(new Event(e.At + pk.SummonCooldown, e.Actor, ++order, pk));
                    continue;
                }

                // THE PET LEFT — its lifetime expired.
                if (e.What == Kind.PetGone)
                {
                    PetLive live = e.Pet;
                    if (live == null || live.Gone) continue;
                    live.Gone = true;
                    live.Kind.Expired++;
                    // A TRAP NEVER LEAVES BY TIME — it leaves because it went off, and the
                    // log must not say "lifetime expired" about an object that blew up a
                    // second after the summon. Its own record is what decides this
                    // (`TimelinePets.Kind.TrapDelay`), and the delay is always shorter than
                    // the lifetime, so there is no second way out to tell apart.
                    Add(run, opt, e.At, LogKind.Plain, Lang.T("log.pet.gone", live.Kind.Name, live.Number),
                        live.Kind.IsTrap ? Lang.T("log.pet.goneTrap", live.Hits)
                                         : Lang.T("log.pet.goneWhy", Fmt(live.Kind.Seconds), live.Hits),
                        0, 0, null, null);
                    continue;
                }

                // A PET'S STRIKE — an autoattack, a skill on its own rate, or its aura.
                if (e.What == Kind.PetHit)
                {
                    PetLive live = e.Pet;
                    TimelinePets.Move m = e.Move;
                    if (live == null || m == null || live.Gone) continue;
                    // A PET DOES NOT ACT TWICE AT ONCE (MECHANICS 87). An aura is not
                    // animated and does not wait; a strike or skill that falls inside
                    // another animation is moved to its end. At that instant a skill stands
                    // BEFORE the claws — the AI strikes with a skill as soon as it can.
                    if (!m.Aura && e.At < live.BusyUntil - 1e-9)
                    {
                        run.PetWaits++;
                        run.PetWaitedSeconds += live.BusyUntil - e.At;
                        queue.Add(new Event(live.BusyUntil, e.Actor, m.Auto || m.Basic ? ++order : ++petFirst, live, m));
                        continue;
                    }
                    // The chance to use it comes from the pet's record
                    // (`specialAttackChance`).
                    if (m.Chance >= 99.9 || petRng.NextDouble() * 100 < m.Chance)
                    {
                        // THE STRIKE'S NUMBERS COME FROM THE REBUILD UNDER THE LIVE AURA
                        // GIVERS (MECHANICS 89a): the same move of the same kind, but with
                        // the aura percents of those alive right now.
                        TimelinePets.Kind numbers = petNumbers(live.Kind);
                        TimelinePets.Move nm = m;
                        if (numbers != live.Kind)
                        {
                            int mi = live.Kind.Moves.IndexOf(m);
                            if (mi >= 0 && mi < numbers.Moves.Count) nm = numbers.Moves[mi];
                        }
                        bool petCrit = PetStrike(run, opt, target, queue, dots, ref order, e, live, nm,
                                                 petRng, perSecond, thresholds, multipliers, totals,
                                                 game, debuffs, numbers.Auto);
                        // A STAR TIED TO A SUMMON IS ROLLED BY THE PET'S ATTACKS — as in the
                        // game (Acid Spray on Summon Eldritch Talon, Eye of Korvaak on a
                        // summon). Until 12 September nobody rolled such stars: there were
                        // no pets in the queue at all, and the census honestly called this a
                        // known gap (MECHANICS 76). A star's damage is OURS, not the pet's —
                        // the star is ours.
                        Stars(run, opt, book, e, live.Kind.Summon, petCrit, false,
                              game, c, totals, seeded, target, queue, dots, ref order, roll, hitRng,
                              pth, thresholds, multipliers, critDamage, below, perSecond);
                        // The strike happened — the pet is busy with its animation
                        // (MECHANICS 87). If the chance did not come up there was no
                        // animation and nothing to wait for.
                        double took = TimelinePets.Occupy(live.Kind, m);
                        if (took > 0) live.BusyUntil = e.At + took;
                        // COOLDOWN FROM THE END OF THE ANIMATION — one of two readings
                        // (MECHANICS 88): the next use is pushed back by this animation's
                        // length as well.
                        if (TimelinePets.CdFromEnd && !m.Auto && !m.Basic && !m.Aura && took > 0)
                            live.CdExtra = took;
                    }
                    // A TRAP BLEW UP — AND THAT WAS ITS DEATH. It does not strike again and
                    // does not wait out its lifetime: the blast IS the dying skill, so the
                    // object goes right now and frees its place under the kind's limit.
                    if (m.Once)
                    {
                        queue.Add(new Event(e.At, e.Actor, ++order, live));
                        continue;
                    }
                    double again = m.Auto || m.Basic ? (live.Kind.Aps > 0 ? 1 / live.Kind.Aps : 0)
                                 : m.Aura ? Combat.AuraPulseSeconds
                                 : m.Cooldown + live.CdExtra;
                    live.CdExtra = 0;
                    if (again > 0.01) queue.Add(new Event(e.At + again, e.Actor, ++order, live, m));
                    continue;
                }

                // AN AURA'S STRIKE. It differs from everything else in three ways, all three
                // measured (MECHANICS 41): NO hit roll, NO crit, NO bracket — "the dot is
                // always 66, the direct damage always 62". So there is nothing to compute
                // but the number itself: its own skill through the target layer.
                if (e.What == Kind.Aura)
                {
                    Aura ring = e.Ring;
                    if (ring == null) continue;
                    ring.Pulses++;
                    run.Count(ring.Skill.Dbr, ring.Name);
                    if (!string.IsNullOrEmpty(ring.From)) run.Count(null, ring.From);
                    run.AuraPulses++;
                    // An aura is a strike too, and it applies ability cuts the same way.
                    AbilityCut ringCut = SkillAbilityCut(game, ring.Skill.Dbr, ring.Skill.Level);
                    if (ringCut.Any) debuffs.PutAbility(ring.Skill.Dbr, ring.Name, ringCut, e.At);
                    roll.NextHit();
                    DamageModel one = new DamageModel(game, c, totals, ring.Skill, true, seeded, roll);
                    double dmg = 0, raw = 0;
                    double[] hands = { 0, 0 };
                    foreach (KeyValuePair<string, double> kv in one.HitHandMin[0])
                    {
                        if (kv.Value <= 0.0001) continue;
                        if (kv.Key.EndsWith("Dot", StringComparison.Ordinal))
                        {
                            // An aura's dot lasts two seconds while it strikes once a
                            // second, so the refresh arrives before it dies. And it does
                            // not die while the aura stands — as in game.
                            Apply(queue, dots, run, opt, e, ref order, 0, kv.Key,
                                  kv.Value, one, totals, ring.Skill.Dbr, "\"" + ring.Name + "\"");
                            continue;
                        }
                        if (!run.Types.Contains(kv.Key)) run.Types.Add(kv.Key);
                        raw += kv.Value;
                        double through = target.Through(kv.Key, kv.Value);
                        dmg += through; hands[0] += through;
                    }
                    run.Total += dmg;
                    run.TotalBefore += raw;
                    run.AuraTotal += dmg;
                    While(perSecond, e.At, dmg);
                    // An aura's direct damage goes into the compared series too: it is
                    // CONSTANT (no roll, no crit), so it can only help the check — the
                    // expectation is computed exactly rather than with a spread.
                    While(run.PerSecondDirect, e.At, dmg);
                    Add(run, opt, e.At, LogKind.Plain, Lang.T("log.aura", ring.Name), Lang.T("log.from", ring.From),
                        dmg, raw, hands, new[] { true, false }, new[] { raw, 0.0 });
                    // AN AURA TRIGGERS PROCS — the user's measurement: "it can set off the
                    // meteor too". For a proc its strike is as good as any. An aura has no
                    // crit, so "on critical attack" never fires from it.
                    Procs(game, c, totals, seeded, target, opt, run, queue, dots, ref order, e,
                          procs, roll, hitRng, procRng, pth, thresholds, multipliers, critDamage,
                          below, perSecond, book, false);
                    queue.Add(new Event(e.At + Combat.AuraPulseSeconds, e.Actor, ++order, ring));
                    continue;
                }

                if (e.What == Kind.Volley)
                {
                    FireVolley(game, c, totals, seeded, target, opt, run, queue, dots, ref order,
                               e, e.Skill, roll, hitRng, pth, thresholds, multipliers, critDamage,
                               below, perSecond, e.VolleyLog, e.VolleyCaption, book, ProjectileVolley.Of(e.Skill.Skill),
                               e.VolleyTriggersProcs ? procs : null, procRng);
                    continue;
                }

                // A SKILL PRESS. It strikes with its own hands (no layout — MECHANICS 38),
                // each hand with its own hit roll and its own crit, and the damage is taken
                // from `HitHandMin` — with the skill's share and its own damage.
                if (e.What == Kind.Press)
                {
                    Press p = e.Skill;
                    if (p == null) continue;
                    // A PRESS WAITS FOR THE CURRENT ANIMATION TO END (MECHANICS 87): if a
                    // swing or another skill is running, move it to the end. A channel and
                    // the main spell wait the same way: while someone else's animation
                    // plays, their tick does not start. What is not a button (a star, a
                    // skill by condition) does not wait at all (`IsButton`).
                    if (e.At < busyUntil - 1e-9 &&
                        IsButton(p.Skill, p.Skill != null ? p.Skill.Dbr : p.BuffDbr ?? p.SilentDbr))
                    {
                        run.ActionWaits++;
                        run.WaitedSeconds += busyUntil - e.At;
                        // A PRESS STANDS BEFORE A SWING AT THE SAME INSTANT. A swing, when it
                        // frees up, schedules the next swing exactly at the end of its own
                        // animation — the same place a press is moved to — and under the
                        // ordinary order it would always win: on the first attempt Shadow
                        // Strike on `_777` was never pressed in 60 seconds (95 deferrals).
                        // "Pressed as soon as the cooldown is up" (task item 1) means: the
                        // moment the character is free, the skill goes. The main skill (a
                        // channel, a spell instead of the autoattack) is the stream of
                        // action itself, and has no priority over presses.
                        queue.Add(new Event(busyUntil, e.Actor, p.Main ? ++order : ++petFirst, p));
                        continue;
                    }
                    // How many seconds this press itself takes — for a buff and a silent
                    // press, by their animation; for a striking one below, after the
                    // interval is computed.
                    double occupy = p.Main || p.Channel ? 0
                                  : PressOccupy(game, c, panel, p.Skill,
                                                p.Skill != null ? p.Skill.Dbr : p.BuffDbr ?? p.SilentDbr);
                    // A BUFF ON COOLDOWN: it does not strike but goes up for its duration.
                    //
                    // We write a line that it activated, as the user asked. After that it
                    // acts for `Secs` seconds, and it cannot be raised again until the
                    // cooldown expires: the next event is scheduled exactly there.
                    if (!string.IsNullOrEmpty(p.BuffDbr))
                    {
                        if (p.NoHold == null) buffs.Raise(p.BuffDbr, p.Note, e.At, p.Secs);
                        run.Count(p.BuffDbr, p.Note);
                        p.Count++;
                        Add(run, opt, e.At, LogKind.Pressed, Lang.T("log.buff", p.Note),
                            (p.NoHold != null ? p.NoHold : Lang.T("log.buff.holds", Fmt(p.Secs))) +
                            ", " + Lang.T("log.buff.cooldown", Fmt(p.Cooldown)),
                            0, 0, null, null);
                        // "ON APPLYING A BUFF" — this is that moment. The one condition from
                        // the unmodellable list that became modellable: the queue now knows
                        // when a buff goes up.
                        Stars(run, opt, book, e, p.BuffDbr, false, true,
                              game, c, totals, seeded, target, queue, dots, ref order, roll,
                              hitRng, pth, thresholds, multipliers, critDamage, below, perSecond);
                        if (p.Cooldown > 0.01)
                            queue.Add(new Event(e.At + p.Cooldown, e.Actor, ++order, p));
                        // Raising a buff takes its own animation (MECHANICS 87).
                        if (occupy > 0) busyUntil = e.At + occupy;
                        continue;
                    }
                    // A PRESS WITHOUT DAMAGE: applies its debuff and rolls the stars tied to
                    // it (Falcon Swoop on War Cry). It calls no "on attack" procs — it is
                    // not a strike.
                    if (p.SilentKind != null)
                    {
                        p.Count++;
                        run.Presses++;
                        SilentEffect(run, opt, book, target, game, e, p.SilentKind.Value, p.SilentDbr, p.SilentLevel,
                                     p.SilentName, p.SilentSecs, LogKind.Pressed, Lang.T("log.silent.pressed") + " ", "", perSecond);
                        Stars(run, opt, book, e, p.SilentDbr, false, false,
                              game, c, totals, seeded, target, queue, dots, ref order, roll,
                              hitRng, pth, thresholds, multipliers, critDamage, below, perSecond);
                        double quiet = p.Cooldown > 0.01 ? p.Cooldown : p.EverySecs;
                        if (quiet > 0.01) queue.Add(new Event(e.At + quiet, e.Actor, ++order, p));
                        // A curse and a cry are animated too (MECHANICS 87).
                        if (occupy > 0) busyUntil = e.At + occupy;
                        continue;
                    }
                    p.Count++;
                    if (p.Main) run.MainSpellTicks++; else run.Presses++;
                    Fire(game, c, totals, seeded, target, opt, run, queue, dots, ref order, e, p,
                         roll, hitRng, pth, thresholds, multipliers, critDamage, below, perSecond,
                         p.Channel || p.Main ? LogKind.Plain : LogKind.Pressed,
                         Lang.T(p.Channel ? "log.fire.channel" : p.Main ? "log.fire.spell" : "log.fire.skill"),
                         book, procs, procRng);
                    // The next press comes as soon as the cooldown is up.
                    //
                    // AND IF THERE IS NO COOLDOWN, WE TAKE THE INTERVAL FROM THE USER.
                    //
                    // Bloody Pox and Biting Blades have no cooldown at all: in game they are
                    // pressed one after another, as fast as one can. Before, such a skill was
                    // pressed EXACTLY ONCE and never again — and the stars tied to it never
                    // fired over a run. We do not know their press rate (it depends on the
                    // hands and on cast speed), so we ask: the user's decision of
                    // 9 September — "let us add an input field of seconds, after how many to
                    // activate this skill".
                    // A CHANNEL uses its own interval by the cast speed AT THAT MOMENT: let a
                    // buff raise casting and the ticks quicken at once.
                    double again = p.Channel
                                 ? Combat.ChannelInterval(game, p.Skill.ChannelMs, panel.CastSpeedPercent)
                                 : p.Cooldown > 0.01 ? p.Cooldown
                                 : p.EverySecs > 0.01 ? p.EverySecs
                                 : panel.CastInterval(p.AnimSecs);
                    // CHARGES AND THE CHANCE TO RESET THE COOLDOWN (MECHANICS 151, measured
                    // on the user's eight runs). A skill with neither goes through here
                    // untouched and draws no die, so no stand without charges can move.
                    //
                    //   the store is topped up by the clock, one charge per cooldown;
                    //   a press spends one;
                    //   a successful roll of `ResetChance` hands it straight back;
                    //   while the store is not empty the next press does not wait for the
                    //   cooldown at all — only for the animation (`busyUntil` defers it).
                    bool chargeRule = false;
                    if (!p.Channel && !p.Main && p.Cooldown > 0.01 &&
                        (p.Charges > 1 || p.ResetChance > 0.0001) && !TimelineTarget.BreakCharges)
                    {
                        chargeRule = true;
                        while (p.Stored < p.Charges && p.RegenAt <= e.At + 1e-9)
                        { p.Stored++; p.RegenAt += p.Cooldown; }
                        bool wasFull = p.Stored >= p.Charges;
                        if (p.Stored > 0) p.Stored--;
                        if (wasFull) p.RegenAt = e.At + p.Cooldown;
                        if (p.ResetChance > 0.0001 && chargeRng.NextDouble() * 100 < p.ResetChance)
                        { p.RegenAt = e.At; run.CooldownResets++; }
                        again = p.Stored > 0 ? 0 : Math.Max(0, p.RegenAt - e.At);
                    }
                    if (again > 0.01)
                        queue.Add(new Event(e.At + again, e.Actor, ++order, p));
                    // A CHARGE IN HAND, OR A COOLDOWN THE ROLL JUST WIPED OUT: press again at
                    // this very instant. The animation gate above moves it to the end of the
                    // swing, so a burst comes out as fast as the character can move and no
                    // faster. Without this branch a successful reset would STOP the skill for
                    // the rest of the run — the interval is zero, and the `> 0.01` test above
                    // schedules nothing.
                    else if (chargeRule)
                        queue.Add(new Event(e.At, e.Actor, ++order, p));
                    // HOW LONG THE PRESS ITSELF TOOK (MECHANICS 87). For a channel and the
                    // main spell that is their own tick — they are the stream of action; for
                    // the rest it is the skill's animation length.
                    double took = p.Main || p.Channel ? again : occupy;
                    if (took > 0) busyUntil = e.At + took;
                    continue;
                }

                // A DOT TICK. The damage per second was computed when it was applied; here it
                // only reaches the target and goes into the log. A dot that was refreshed
                // ticks by ITS OWN event — that event is extended.
                if (e.What == Kind.Tick)
                {
                    Dot d = e.Source;
                    if (d == null || e.At > d.Until + 1e-9) continue;
                    // THE TICK'S FRACTION — for the time elapsed since the last tick, but no
                    // more than the tick period (`Combat.DotTickSeconds`). A full tick is
                    // exactly that; the last comes out partial by itself, with no special
                    // case.
                    double part = e.At - d.LastAt;
                    if (part > Combat.DotTickSeconds) part = Combat.DotTickSeconds;
                    // The dot was REFRESHED while the event stood at the old end: paying a
                    // fraction here is premature — a whole period lies ahead. Reschedule.
                    if (part < Combat.DotTickSeconds - 1e-9 && e.At < d.Until - 1e-9)
                    {
                        double when = Math.Min(d.LastAt + Combat.DotTickSeconds, d.Until);
                        if (when > e.At + 1e-9) { queue.Add(new Event(when, e.Actor, ++order, d)); continue; }
                    }
                    if (part <= 1e-9) continue;
                    // A pet's dot goes without our racial bonus (MECHANICS 78e).
                    double tick = target.Through(d.Type, d.PerSecond, !d.Pet) * part;
                    run.Total += tick;
                    // With the tick's fraction, like what landed: the partial last tick used
                    // to go into "before" whole and slightly overstated it in the total.
                    run.TotalBefore += d.PerSecond * part;
                    run.DotTotal += tick;
                    While(perSecond, e.At, tick);
                    TimelineLine tl = new TimelineLine();
                    // WHAT IT BURNS FROM goes into the tick's line. Otherwise it is invisible
                    // that the dot rose from the autoattack to a skill: a tick comes once a
                    // second and on its own, not right after the strike.
                    tl.At = e.At; tl.Kind = LogKind.Tick;
                    string whose = d.ByWeapon ? Lang.T(d.Hand == 0 ? "log.hand.main" : "log.hand.off") : "";
                    if (!string.IsNullOrEmpty(d.From))
                        whose += (whose.Length > 0 ? ", " : "") + Lang.T("log.tick.from", d.From);
                    tl.What = Lang.T("log.tick", DotName(d.Type)) + (whose.Length > 0 ? " (" + whose + ")" : "");
                    tl.Damage = tick; tl.Before = d.PerSecond * part; tl.Type = d.Type; tl.Part = part;
                    tl.Pet = d.Pet;
                    tl.Hand[d.Hand] = tick; tl.Hit[d.Hand] = true; tl.HandBefore[d.Hand] = tl.Before;
                    tl.Why = Lang.T("log.tick.left", Fmt(d.Until - e.At)) +
                             (part < Combat.DotTickSeconds - 1e-9
                                  ? ", " + Lang.T("log.tick.partial", Fmt(part / Combat.DotTickSeconds * 100)) : "");
                    // A tick is written past `Add`, so it has to be handed to the window's
                    // live tail here, or ticks are invisible while the run goes on.
                    if (opt.LogEveryHit)
                    {
                        run.Log.Add(tl);
                        if (opt.OnLine != null) opt.OnLine(tl);
                    }
                    d.LastAt = e.At;
                    // THE NEXT TICK comes after the period (`Combat.DotTickSeconds`) OR at the
                    // dot's very end, whichever is sooner. The latter is what gives a partial
                    // tick: before, the chain simply broke here and the dot's tail WAS LOST.
                    d.NextTick = Math.Min(e.At + Combat.DotTickSeconds, d.Until);
                    if (d.NextTick > e.At + 1e-9)
                        queue.Add(new Event(d.NextTick, e.Actor, ++order, d));
                    else d.Ticking = false;      // chain ended — a refresh will revive it
                    continue;
                }

                // A SWING WAITS FOR THE CURRENT ANIMATION TO END (MECHANICS 87). If a press is
                // still playing, the swing moves to its end — after everything already
                // scheduled for that instant.
                if (e.At < busyUntil - 1e-9)
                {
                    run.ActionWaits++;
                    run.WaitedSeconds += busyUntil - e.At;
                    queue.Add(new Event(busyUntil, e.Actor, ++order));
                    continue;
                }
                run.Swings++;
                Swing kind = Swing.Main;
                double damage = 0, before = 0;

                // 1. THE HIT ROLL IS ITS OWN PER HAND, and it also decides the crit tier.
                //    The roll is uniform on [0, 100): a hit if it is below `PTH`; the tier is
                //    the highest whose threshold it crossed.
                //
                //    WHY PER HAND AND NOT PER SWING. The user's measurement of 6 September on
                //    Shadow Strike: "either hand can miss just like the default attack, I have
                //    even seen both miss at once". The average does not change (the hit share
                //    is the same on each hand); the log and the spread do — and the log is now
                //    honest.
                double[] rr = { hitRng.NextDouble() * 100, hitRng.NextDouble() * 100 };
                int[] tiers = { Combat.Tier(pth, rr[0], thresholds),
                                Combat.Tier(pth, rr[1], thresholds) };
                double[] critMuls = { Combat.CritMultiplier(tiers[0], multipliers, critDamage),
                                      Combat.CritMultiplier(tiers[1], multipliers, critDamage) };
                double r = rr[0];
                int tier = tiers[0];
                double critMul = critMuls[0];

                // 2. THE HAND-LAYOUT ROLL comes right after the hit, as the user asked
                //    (PLAN N1), and on EVERY swing: the hands swung even on a miss, and which
                //    ones they were sets the interval to the next swing (N2). With one weapon
                //    there is no roll at all. It belongs to the DEFAULT ATTACK only: a skill
                //    that strikes with two hands has no layout — it always strikes with both
                //    (measured 6 September, Shadow Strike — always two figures).
                string why = "";
                if (dual)
                {
                    double h = handRng.NextDouble() * 100;
                    kind = h < handSplit.Both * 100 ? Swing.Both
                         : (h < (handSplit.Both + handSplit.Main) * 100 ? Swing.Main : Swing.Off);
                    why = Lang.T("log.swing.hands", Fmt(h), Name(kind));
                }

                // THE ASSIGNED ATTACK: Savagery, Cadence, Righteous Fervor.
                //
                // This is not a roll but the player's choice: the skill sits on the attack
                // button and strikes instead of the weapon. It counts as the DEFAULT ATTACK —
                // the user, 7 September: "it counts as an autoattack and I think it will
                // activate all the autoattack skills" — so procs and stars fire from it
                // exactly as from a swing.
                //
                // Charges build with every strike and decay once striking stops:
                //   BUILDING  it strikes with the skill on EVERY swing, charges grow to the
                //             cap;
                //   FINALE    swings are ordinary, and strike number `ChargeLevels + 1`
                //             strikes with the skill ("every 3rd" with two charges) — and the
                //             weapon pool is NOT rolled on it: the user says the replacement
                //             has already happened.
                //
                // Here: the charges and the decision whether it strikes on this swing.
                //
                // THE STRIKE ITSELF IS BELOW, AFTER THE POOL. The user's measurement of
                 // a test save: "the auto-activated skills do activate from
                // Savagery (the scissors and Nidalla's invisible hand fire)", and his own
                // sample shows "902 (both hands — that is probably the scissors)". So the
                // weapon pool is rolled under Savagery AS ALWAYS and displaces it just as it
                // would displace an ordinary strike. My guess was the opposite and proved
                // wrong — corrected by measurement (debt #8 closed).
                bool assignNow = false;
                if (assigned != null)
                {
                    if (charge > 0 && assigned.ChargeDuration > 0.01 &&
                        e.At - chargeAt > assigned.ChargeDuration + 1e-9) charge = 0;
                    assignNow = !assigned.Finale || charge >= assigned.ChargeLevels;
                    if (assigned.Finale && assignNow) charge = 0;
                    else if (charge < assigned.ChargeLevels) charge++;
                    chargeAt = e.At;
                    if (charge > run.ChargeSeen) run.ChargeSeen = charge;
                }

                // THE WEAPON POOL: a skill REPLACES the default attack rather than adding to
                // it (`timeline.txt`, item 12). The shares split one hundred, and NO MORE
                // THAN ONE fires — so there is ONE roll for the whole pool here, unlike
                // procs, where each has its own (PLAN, Zh2).
                //
                // The shares can sum above one hundred: then each one's share is taken by its
                // WEIGHT among the rest (the same rule as for conversion above a hundred).
                // THE POOL IS NOT ROLLED ONLY ON CADENCE'S FINALE — there the replacement has
                // already happened (the user's words). Under Savagery it is rolled: Savagery
                // is the default attack itself, and the pool displaces it the same way.
                bool finaleNow = assigned != null && assigned.Finale && assignNow;
                if (pool.Count > 0 && !finaleNow)
                {
                    double weight = 0;
                    foreach (AttackSkill a in pool) weight += a.PoolChance;
                    double scale = weight > 100 ? weight / 100.0 : 1;
                    double shot = procRng.NextDouble() * 100, at = 0;
                    AttackSkill won = null;
                    foreach (AttackSkill a in pool)
                    {
                        at += a.PoolChance / scale;
                        if (shot < at) { won = a; break; }
                    }
                    if (won != null)
                    {
                        Press swap = new Press();
                        swap.Skill = won;
                        DbRecord wrec = game.Record(won.Dbr);
                        swap.Hands = wrec == null ? Hands.Main : SkillHands.Of(game, wrec, c);
                        run.PoolFired++;
                        Fire(game, c, totals, seeded, target, opt, run, queue, dots, ref order, e,
                             swap, roll, hitRng, pth, thresholds, multipliers, critDamage,
                             below, perSecond, LogKind.Pool, Lang.T("log.fire.pool"), book, procs, procRng);
                        double apsPool = panel.SwingAPS(dual ? (int)kind : 0);
                        if (apsPool > 0) queue.Add(new Event(e.At + 1 / apsPool, e.Actor, ++order));
                        // A pool skill IS the swing: it takes the swing's interval.
                        if (apsPool > 0) busyUntil = e.At + 1 / apsPool;
                        continue;      // there WAS NO default attack in this swing
                    }
                }

                // THE ASSIGNED ATTACK'S STRIKE — if the pool did not displace it.
                //
                // ITS HANDS ARE ROLLED 50/25/25, AS FOR THE DEFAULT ATTACK. The user's
                // measurement of 8 September, and he corrected me outright: "it took me a
                // while to realise that the hands under Savagery strike by the 50/25/25 rule
                // like the autoattack… and they did strike singly". I was taking the hands
                // from the skill's record (`SkillHands`) — i.e. as for a pressed skill, which
                // has no layout at all. For the assigned attack that is wrong: it IS the
                // default attack, and its layout is the same.
                if (assigned != null && assignNow)
                {
                    Press hit = new Press();
                    hit.Skill = assigned;
                    hit.Hands = !dual ? Hands.Main
                              : kind == Swing.Both ? Hands.Both
                              : kind == Swing.Main ? Hands.Main : Hands.Off;
                    hit.Note = why;      // the hand roll was already made above
                    // THE CHARGE THIS STRIKE LANDS AT. Cadence's finale strikes at the full
                    // one (the charge has just been built), Savagery at the current one.
                    hit.AtCharge = assigned.Finale ? 0 : charge;
                    run.AssignedFired++;
                    Fire(game, c, totals, seeded, target, opt, run, queue, dots, ref order, e,
                         hit, roll, hitRng, pth, thresholds, multipliers, critDamage,
                         below, perSecond, LogKind.Plain,
                         assigned.Finale ? Lang.T("log.fire.finale", Fmt0(assigned.ChargeLevels + 1))
                                         : Lang.T("log.fire.attack") + (assigned.ChargeLevels > 0
                                                          ? " " + Lang.T("log.fire.charges", charge) : ""),
                         book, procs, procRng);
                    double apsA = panel.SwingAPS(dual ? (int)kind : 0);
                    if (apsA > 0) queue.Add(new Event(e.At + 1 / apsA, e.Actor, ++order));
                    // The assigned attack IS the swing: it takes the swing's interval.
                    if (apsA > 0) busyUntil = e.At + 1 / apsA;
                    continue;      // there WAS NO weapon default attack in this swing
                }

                // Which hands SWUNG, and whether each landed — by its own roll.
                bool[] swung = { kind != Swing.Off, dual && kind != Swing.Main };
                bool[] landed = { swung[0] && tiers[0] > 0, swung[1] && tiers[1] > 0 };
                bool hitAny = landed[0] || landed[1];
                for (int h2 = 0; h2 < 2; h2++)
                {
                    if (!swung[h2]) continue;
                    why += (why.Length > 0 ? ", " : "") + Lang.T(h2 == 0 ? "log.roll.main" : "log.roll.off") + " " +
                           Fmt(rr[h2]) + (landed[h2] ? " < " : " ≥ ") + Fmt(pth);
                }

                if (!hitAny)
                {
                    run.Misses++;
                    Add(run, opt, e.At, LogKind.Miss, Lang.T("log.miss"), why, 0, 0, null, null);
                }
                else
                {
                    run.Hits++;
                    run.Picked[(int)kind]++;
                    // THE CUT FROM GEAR IS APPLIED BY AN ORDINARY SWING TOO, not only by a
                    // skill.
                    //
                    // Until 10 September only `Fire` applied it — presses, procs, the pool —
                    // and a character striking with the weapon alone never got it, although
                    // the `offensiveSlowDefensiveAbilityMin` field sits precisely on WEAPONS
                    // (eighteen prefixes in the database). Found when the check needed to
                    // know what applies it (MECHANICS 75).
                    debuffs.PutGear(totals, e.At);

                    // 3. THE DAMAGE CALCULATION. The same code as the panel's, but with a
                    //    roll: every line has its own bracket and its own die
                    //    (MECHANICS 31).
                    roll.NextHit();
                    DamageModel one = new DamageModel(game, c, totals, null, true, seeded, roll);
                    double[] hands = new double[2];
                    double[] handsBefore = new double[2];
                    for (int hand = 0; hand < 2; hand++)
                    {
                        if (!landed[hand]) continue;
                        run.HandHits++;
                        // Each hand's crit is ITS OWN — it came out of its own roll.
                        critMul = critMuls[hand];
                        tier = tiers[hand];
                        if (tier > 1)
                        {
                            run.Crits++;
                            while (run.ByTier.Count <= tier) run.ByTier.Add(0);
                            run.ByTier[tier]++;
                            why += ", " + Lang.T("log.swing.crit", Lang.T(hand == 0 ? "log.hand.main" : "log.hand.off"),
                                                 tier - 1, critMul.ToString("0.##", CultureInfo.InvariantCulture));
                        }
                        foreach (KeyValuePair<string, double> kv in one.HandTypeMin[hand])
                        {
                            // A DOT IS APPLIED, IT DOES NOT STRIKE AT ONCE. The user's rule
                            // (MECHANICS 33): "a dot is applied guaranteed when the hand
                            // whose weapon carries the burn lands" — an item's dot has no
                            // chance of its own at all.
                            if (kv.Key.EndsWith("Dot", StringComparison.Ordinal))
                            {
                                if (kv.Value <= 0.0001) continue;
                                // The default attack is WEAPON damage, hence a shared source
                                // with every skill that has a weapon share (those arrive at
                                // E3).
                                Apply(queue, dots, run, opt, e, ref order, hand, kv.Key,
                                      kv.Value * critMul * below, one, totals, WeaponSource,
                                      Lang.T("log.tick.autoattack"));
                                continue;
                            }
                            if (kv.Value > 0.0001 && !run.Types.Contains(kv.Key)) run.Types.Add(kv.Key);
                            // 4. THE CRIT MULTIPLIER AND THE BELOW-FIRST-THRESHOLD CUT both
                            //    come before the target layer: armour cuts what already flew.
                            double one4 = kv.Value * critMul * below;
                            before += one4;
                            handsBefore[hand] += one4;
                            // 5. THE TARGET LAYER: armour on physical and resistances by
                            //    type (MECHANICS 32).
                            double through = target.Through(kv.Key, one4);
                            damage += through;
                            hands[hand] += through;
                        }
                    }
                    Add(run, opt, e.At, LogKind.Plain, Lang.T("log.swing", Name(kind)) + Partial(swung, landed),
                        why, damage, before, hands, landed, handsBefore);
                }

                // 6. SELF-FIRING ONES go on EVERY swing, hit or miss. The user's measurement:
                //    the sequence contains "miss + meteor", i.e. a proc's roll is made on the
                //    swing itself (MECHANICS 33), and it can fire from any strike — a pressed
                //    skill included (MECHANICS 39).
                Procs(game, c, totals, seeded, target, opt, run, queue, dots, ref order, e,
                      procs, roll, hitRng, procRng, pth, thresholds, multipliers, critDamage,
                      below, perSecond, book,
                      (landed[0] && tiers[0] > 1) || (landed[1] && tiers[1] > 1));

                run.Total += damage;
                run.TotalBefore += before;
                While(perSecond, e.At, damage);
                While(run.PerSecondDirect, e.At, damage);   // a swing is direct damage
                // Percent-life damage from gear applies on an ordinary strike too
                // (MECHANICS 77).
                if (hitAny)
                    PercentLifeHit(run, opt, target, e, GearPercentLife(totals), Lang.T("log.life.gear"), perSecond);

                // The event schedules the next one itself and takes the interval from the
                // speed AT THAT MOMENT — each kind of swing has its own (PLAN, N2).
                double aps = panel.SwingAPS(dual ? (int)kind : 0);
                if (aps <= 0) break;
                queue.Add(new Event(e.At + 1 / aps, e.Actor, ++order));
                // A swing takes its interval: a press falling inside it waits for its end
                // (MECHANICS 87).
                busyUntil = e.At + 1 / aps;
            }

            // CENSUS OF GEAR AND SET SKILLS — what became of each.
            {
                List<AttackSkill> hittingNow = AttackSkills.Of(game, c);
                foreach (AttackSkills.GrantedSkill g in AttackSkills.GearGranted(game, c))
                {
                    string head = "«" + g.Name + "» (" + g.Source + ")" +
                                  (string.IsNullOrEmpty(g.When) ? "" : ", " + g.When) + " — ";
                    Proc fired = null;
                    foreach (Proc pr in procs)
                        if (string.Equals(pr.Dbr, g.Dbr, StringComparison.OrdinalIgnoreCase)) { fired = pr; break; }
                    Press pressed = null;
                    foreach (Press pp in presses)
                        if ((pp.Skill != null && string.Equals(pp.Skill.Dbr, g.Dbr, StringComparison.OrdinalIgnoreCase)) ||
                            string.Equals(pp.SilentDbr, g.Dbr, StringComparison.OrdinalIgnoreCase))
                        { pressed = pp; break; }
                    AttackSkill hit = null;
                    foreach (AttackSkill a in hittingNow)
                        if (string.Equals(a.Dbr, g.Dbr, StringComparison.OrdinalIgnoreCase)) { hit = a; break; }
                    string tail;
                    if (mainSpell != null && string.Equals(mainSpell.Dbr, g.Dbr, StringComparison.OrdinalIgnoreCase))
                        tail = Lang.T("log.gear.mainSpell", run.MainSpellTicks);
                    else if (hit != null && hit.PoolChance > 0.0001)
                        tail = Lang.T("log.gear.pool");
                    else if (fired != null)
                        tail = (fired.OnlyCrit ? Lang.T("log.gear.onlyCrit") + " " : "") +
                               Lang.T("log.gear.fired", fired.Fired) +
                               (fired.NoDamage == EffectKind.Summon ? " " + Lang.T("log.gear.firedSummon")
                                : fired.NoDamage == EffectKind.Debuff ? " " + Lang.T("log.gear.firedDebuff")
                                : fired.NoDamage == EffectKind.Buff ? " " + Lang.T("log.gear.firedBuff")
                                : fired.NoDamage != null ? " — " + EffectWord(fired.NoDamage.Value) : "");
                    else if (pressed != null)
                        tail = (string.IsNullOrEmpty(g.Trigger) ? "" : Lang.T("log.gear.conditionNotModelled") + " ") +
                               Lang.T("log.gear.pressed", pressed.Count);
                    else if (hit != null)
                        tail = string.IsNullOrEmpty(g.Trigger)
                             ? Lang.T("log.gear.notPressed")
                             : Lang.T("log.gear.conditionHits");
                    else
                        tail = string.IsNullOrEmpty(g.Trigger) || g.Trigger == "AttackEnemy" || g.Trigger == "AttackEnemyCrit"
                             ? Lang.T("log.gear.noHit", EffectWord(SilentKind(game, g.Dbr, g.Class)))
                             : Lang.T("log.gear.conditionNoHit", EffectWord(SilentKind(game, g.Dbr, g.Class)));
                    run.GearLines.Add(head + tail);
                }
            }

            // Killed by the run's very last event — TTK is counted all the same.
            if (target.Life > 0 && run.KillAt < 0 && run.Total >= target.Life) run.KillAt = lifeAt;

            // THE RUN IS OVER — DROP THE TIMED DEBUFFS, RESTORE THE TARGET.
            //
            // The target is a shared object: after the run the check computes its expectation
            // from it and the window prints the target's resistances. Until 10 September both
            // saw the target as the LAST second left it — with whichever debuff happened to
            // hang in that second. On `_Itaan` that was Acid Spray, −28 flat to every
            // resistance, and the Righteous Fervor expectation was inflated by that accident
            // (MECHANICS 73). What is permanent — the auras' cut, applied at the start —
            // stays: it hangs the whole fight, and that is how it should be seen.
            debuffs.Expire(target, double.MaxValue);
            target.AbsoluteCut = 0;
            target.RelativeCut = 0;
            target.ArmorCut = 0;

            // The tail second counts whole: a 5-second run is five per-second sums even if
            // the last swing fell at 4.6.
            int seconds = (int)Math.Ceiling(opt.Seconds - 1e-9);
            while (perSecond.Count < seconds) perSecond.Add(0);
            run.PerSecond.AddRange(perSecond);
            run.Rolls = roll.Rolls;
            foreach (KeyValuePair<string, int> kv in roll.XorCounts) run.XorCounts[kv.Key] = kv.Value;
            foreach (KeyValuePair<string, double[]> kv in roll.ChanceCounts) run.ChanceCounts[kv.Key] = kv.Value;
            // HOW LONG EACH BUFF STOOD. Exactly the number all this was done for: Hungering
            // Void — 20 seconds on a 27.9 cooldown, i.e. neither "always" nor "never". The
            // share is taken against the run's length.
            // THE ENEMY'S ABILITY CUT gets its own line: it decides the chance to hit.
            foreach (KeyValuePair<string, double> kv in debuffs.AbilHeld)
            {
                string nm; debuffs.Names.TryGetValue(kv.Key, out nm);
                AbilityCut cut; debuffs.Abil.TryGetValue(kv.Key, out cut);
                if (cut == null) continue;
                List<string> what = new List<string>();
                if (cut.Defensive > 0.0001) what.Add("defensive ability −" + Fmt(cut.Defensive));
                if (cut.Offensive > 0.0001) what.Add("attack ability −" + Fmt(cut.Offensive));
                if (cut.Absolute > 0.0001) what.Add("ALL resistances −" + Fmt(cut.Absolute) + " (as a number)");
                if (cut.Relative > 0.0001) what.Add("ALL resistances −" + Fmt(cut.Relative) + "% (of the current)");
                if (cut.Armor > 0.0001) what.Add("armour −" + Fmt(cut.Armor) + " (as a number, MECHANICS 89e)");
                run.ResistCuts.Add("\"" + (nm ?? kv.Key) + "\" cut " +
                                   string.Join(", ", what.ToArray()) + " — held for " +
                                   Fmt(kv.Value) + " s of " + Fmt(opt.Seconds) +
                                   " (" + Fmt(kv.Value / opt.Seconds * 100) + "%)" +
                                   (cut.Offensive > 0.0001
                                        ? "   [the enemy's attack ability is used nowhere by us yet]" : ""));
                // A PET'S OWN DEBUFF — kept as numbers as well, for the check's
                // expectation (`TimelineRun.HeldCut`). The key says whose it is: the
                // run files pet debuffs under `PetTag` + record (see where pets strike).
                if (kv.Key.StartsWith(PetTag, StringComparison.Ordinal))
                    run.PetCuts.Add(new TimelineRun.HeldCut
                    {
                        Name = nm ?? kv.Key,
                        Defensive = cut.Defensive,
                        Absolute = cut.Absolute,
                        Relative = cut.Relative,
                        Armor = cut.Armor,
                        Share = opt.Seconds > 0 ? Math.Min(1, kv.Value / opt.Seconds) : 0
                    });
            }

            // WHAT STRIKES TOOK OFF THE RESISTANCES — and for how long it stood.
            foreach (KeyValuePair<string, double> kv in debuffs.Held)
            {
                string nm; debuffs.Names.TryGetValue(kv.Key, out nm);
                List<ResistCut> cuts; debuffs.What.TryGetValue(kv.Key, out cuts);
                List<string> what = new List<string>();
                if (cuts != null)
                    foreach (ResistCut cut in cuts)
                        what.Add(TypeName(cut.Type) + " −" + Fmt(cut.Amount));
                run.ResistCuts.Add("\"" + (nm ?? kv.Key) + "\" took off " +
                                   string.Join(", ", what.ToArray()) + " — held for " +
                                   Fmt(kv.Value) + " s of " + Fmt(opt.Seconds) +
                                   " (" + Fmt(kv.Value / opt.Seconds * 100) + "%)");
            }
            foreach (KeyValuePair<string, double> kv in buffs.Held)
            {
                string nm;
                buffs.Names.TryGetValue(kv.Key, out nm);
                double held = kv.Value;
                run.BuffLines.Add("\"" + (nm ?? kv.Key) + "\" held for " + Fmt(held) + " s of " +
                                  Fmt(opt.Seconds) + " — " + Fmt(held / opt.Seconds * 100) + "% of the run");
            }
            foreach (Aura ring in auras)
                run.AuraLines.Add("\"" + ring.Name + "\" from \"" + ring.From + "\" — strikes " + ring.Pulses);
            // A CAVEAT USED TO STAND HERE: "resistance reduction is not done in full: only
            // the first kind of three and only from auras". It stopped being true on
            // 14 September and hung on for an extra month — the same lesson as MECHANICS 71b.
            // WHAT IS STILL MISSING FROM RESISTANCE REDUCTION — since 14 September, NOTHING.
            // All three kinds are done and taken from everywhere; the last was the chance on
            // GEAR, which the totals summed as an average. It is now rolled on every strike
            // (`PutGear`, MECHANICS 89b), and the note "goes by EXPECTATION, not by roll" is
            // gone. It used to print on ANY resistance reduction, even without chance-based
            // items.
            if (assigned != null)
            {
                run.AssignedName = assigned.Name;
                // A CAVEAT HUNG HERE FOR TWO DAYS: "charges are COUNTED but DO NOT ACT yet"
                // — and it printed UNCONDITIONALLY, at any tier. It stopped being true on
                // 8 September: a strike lands at the current charge (`hit.AtCharge`,
                // MECHANICS 52), and that is visible as a number — on `_Itaan` the second
                // hand without a crit gives 18666 at the first charge, 19922 at the third
                // and about 22090 at the ninth. Removed 10 September. The same lesson as
                // MECHANICS 71b: a caveat without a condition does not go out when the work
                // is done — it has to be hunted by eye.
            }

            // A STAR WHOSE ACTIVATOR NEVER STRUCK MUST SAY SO.
            //
            // Otherwise its absence from the log reads two ways: either the roll did not come
            // up, or we forgot about it. On `_777` Arcane Bomb hangs on Shadow Strike, which
            // is not ticked for pressing — so there will be no bomb in the run at all, and
            // that must be said in words rather than by emptiness.
            foreach (KeyValuePair<string, List<Proc>> kv in book.By)
            {
                bool fired = book.Seen.Contains(kv.Key);
                foreach (Proc pr in kv.Value)
                {
                    if (!fired)
                    {
                        // A REPLACING ATTACK IS A SEPARATE CASE, AND WE MUST NOT LIE ABOUT IT.
                        //
                        // Savagery (`Skill_WeaponPool_ChargedScaling`) is neither a skill on a
                        // button nor a weighted member of the pool: it is ASSIGNED to the
                        // attack button and replaces the ordinary strike entirely.
                        //
                        // A STALE CAVEAT USED TO STAND HERE: "we do not count such skills at
                        // all — neither their damage nor this star". It was true until
                        // 7 September; replacing attacks are done (MECHANICS 46), and the line
                        // went on lying for two days. The reason the star stays silent is now
                        // a DIFFERENT one: the save does not store the assignment
                        // (`SkillSettingValid=0`), so what to strike with is the user's choice,
                        // and in this run something else was chosen. On why caveats go stale
                        // silently, see MECHANICS 71b.
                        if (pr.ActivatorClass != null &&
                            pr.ActivatorClass.StartsWith("Skill_WeaponPool", StringComparison.OrdinalIgnoreCase))
                            // The record class and the command-line switch hidden 17 September
                            // (the user asked). The wording was: "the star '…' is tied to '…'
                            // (" + pr.ActivatorClass + ") — … Choose it in the 'what we strike
                            // with' list (or with --attack=" + pr.Activator + ") and the star
                            // will work".
                            run.Hints.Add(Lang.T("log.hint.starReplacing", pr.Name, pr.Activator));
                        else
                            run.Hints.Add(Lang.T("log.hint.starIdle", pr.Name, pr.Activator));
                        continue;
                    }
                    // A PER-STAR COUNT, so that the run can be checked BY EYE and not only by
                    // a guard: which skill the star comes from, at what chance, how many
                    // times it fired and how many times the roll was skipped because of the
                    // cooldown. This is the very way of checking the user named: "the order in
                    // which all the skills are used, with their chances, can be seen by eye or
                    // in the long log".
                    run.StarLines.Add(Lang.T("log.star.head", pr.Name, pr.Level, pr.Activator) +
                                      (pr.OnlyCrit ? " " + Lang.T("log.star.onlyCrit") : "") +
                                      ", " + Lang.T("log.star.chance", Fmt(pr.Chance)) +
                                      (pr.Cooldown > 0.01 ? ", " + Lang.T("log.star.cooldown", Fmt(pr.Cooldown)) : "") +
                                      " — " + Lang.T("log.star.fired", pr.Fired, pr.Rolled) +
                                      (pr.Blocked > 0 ? ", " + Lang.T("log.star.blocked", pr.Blocked) : "") +
                                      (pr.NoDamage != null ? "  [" + Lang.T("log.silent.noDamage", EffectWord(pr.NoDamage.Value)) + "]" : ""));
                }
            }
            // PETS BY NAME — like the stars: how many times it was summoned, how many strikes
            // it made and how much damage it itself gave (E7).
            foreach (TimelinePets.Kind pk in petKinds)
            {
                if (pk.Summoned == 0 && pk.Blocked == 0) continue;
                run.PetLines.Add(Lang.T("log.petLine.head", pk.Name, pk.Title, pk.Summoned) +
                                 ", " + Lang.T("log.petLine.limit", pk.Limit) +
                                 (pk.Seconds > 0 ? ", " + Lang.T("log.pet.lives", Fmt(pk.Seconds)) : "") +
                                 ", " + Lang.T("log.petLine.hits", pk.Hits) +
                                 (pk.Misses > 0 ? ", " + Lang.T("log.petLine.misses", pk.Misses) : "") +
                                 (pk.Crits > 0 ? ", " + Lang.T("log.petLine.crits", pk.Crits) : "") +
                                 ", " + Lang.T("log.petLine.damage", Fmt0(pk.Damage)) +
                                 (pk.Expired > 0 ? ", " + Lang.T("log.petLine.expired", pk.Expired) : "") +
                                 (pk.Blocked > 0 ? ", " + Lang.T("log.petLine.blocked", pk.Blocked) : ""));
            }

            // ROLLS AND CHANCES OF EVERY SELF-FIRING ONE — for the firing check: without them
            // a rare proc that did not come up in 45 seconds is indistinguishable from a dead
             // one (a damage-over-time case: 0 of 34 at 15% — over several seeds it came
            // out at exactly 14.5%).
            foreach (Proc pr in procs) run.ProcRollList.Add(new ProcRolls(pr.Dbr, pr.Name, pr.Rolled, pr.Chance));
            foreach (KeyValuePair<string, List<Proc>> kv in book.By)
                foreach (Proc pr in kv.Value) run.ProcRollList.Add(new ProcRolls(pr.Dbr, pr.Name, pr.Rolled, pr.Chance));
            return run;
        }

        // SELF-FIRING ONES ON THIS STRIKE — a roll for each separately.
        //
        // Called both from a swing and from a pressed skill: a proc fires from any strike
        // (MECHANICS 39), and from a miss too (MECHANICS 33).
        // A CHANCE-BASED CUT TO AN ACTIVE COOLDOWN — the fifth thing a skill may want from a
        // strike, and it is built exactly like a proc: four fields side by side.
        //
        //   refreshCooldownSkill     whose cooldown we move
        //   refreshCooldownAmount    by how many seconds
        //   refreshCooldownChance    at what chance
        //   refreshCooldownTrigger   from what: AttackEnemy, AttackEnemyCrit, HitByEnemy
        //
        // The triggers are THE SAME as for chance-based skills, and two of the three we roll
        // honestly (we have strikes and crits); `HitByEnemy` is not modelled at all — the
        // enemy's damage on us is not in the queue, and that is said out loud in the report.
        //
        // The berserker has two: Recklessness (22% on a crit — Rip and Tear by 0.8 s) and
        // Everwinter (11% on attack — Cold Snap by 0.8 s).
        internal sealed class Refresher
        {
            public string Target = "";     // the skill record whose cooldown we move
            public string TargetName = "";
            public string Source = "";     // whose property it is — for the log
            public double Amount, Chance;
            public string Trigger = "";
            public int Rolled, Fired;
            public double Moved;           // how many seconds were won back in total
        }

        // All such properties of the character: over all of his skills and the whole chain of
        // each record — the numbers do not necessarily sit in the top one.
        static List<Refresher> RefreshersOf(GameData game, Character c)
        {
            List<Refresher> made = new List<Refresher>();
            if (game == null || c == null) return made;
            SkillText.LevelBonus bonus = SkillText.LevelBonus.Of(game, c);
            List<string> seen = new List<string>();
            foreach (Skill s in c.Skills)
            {
                if (string.IsNullOrEmpty(s.Name) || s.Level <= 0) continue;
                int level = s.Level + (bonus == null ? 0 : bonus.For(s.Name, s.Level));
                string at = s.Name;
                for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(at); hop++)
                {
                    DbRecord rec = game.Record(at);
                    if (rec == null || seen.Contains(rec.Path)) break;
                    seen.Add(rec.Path);
                    string aim = rec.Get("refreshCooldownSkill");
                    double amount = AttackSkill.Value(rec, "refreshCooldownAmount", 0);
                    if (!string.IsNullOrEmpty(aim) && amount > 0.0001)
                    {
                        Refresher r = new Refresher();
                        r.Target = aim;
                        r.TargetName = SkillText.Name(game, aim) ?? aim;
                        r.Source = SkillText.Name(game, s.Name) ?? s.Name;
                        r.Amount = amount;
                        r.Chance = AttackSkill.Value(rec, "refreshCooldownChance", SkillText.Slot(rec, level));
                        r.Trigger = rec.Get("refreshCooldownTrigger") ?? "";
                        if (r.Chance > 0.0001) made.Add(r);
                    }
                    at = ItemViewFactory.NextSkillRecord(rec);
                }
            }
            return made;
        }

        // A roll on every strike. Moving a cooldown means moving the EVENT of the next press:
        // it already stands in the queue, and we have no other place where "how long until
        // ready" lives.
        static void Refresh(TimelineRun run, TimelineOptions opt, List<Event> queue,
                            Event e, Random rng, bool crit)
        {
            if (run.Refreshers == null) return;
            foreach (Refresher r in run.Refreshers)
            {
                if (string.Equals(r.Trigger, "AttackEnemyCrit", StringComparison.OrdinalIgnoreCase) && !crit) continue;
                if (!string.Equals(r.Trigger, "AttackEnemy", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(r.Trigger, "AttackEnemyCrit", StringComparison.OrdinalIgnoreCase)) continue;
                r.Rolled++;
                if (rng.NextDouble() * 100 >= r.Chance) continue;
                // Find this skill's pending press and pull it closer.
                //
                // `Event` IS A STRUCT: a copy taken from the list lives on its own, and
                // editing its field does not reach the queue. So we keep the INDEX and put
                // the changed copy back at it.
                int idx = -1;
                for (int i = 0; i < queue.Count; i++)
                {
                    Event q = queue[i];
                    if (q.What != Kind.Press || q.Skill == null || q.Skill.Skill == null) continue;
                    if (!string.Equals(q.Skill.Skill.Dbr, r.Target, StringComparison.OrdinalIgnoreCase)) continue;
                    if (q.At <= e.At + 1e-9) continue;    // already ready, nothing to move
                    if (idx < 0 || q.At < queue[idx].At) idx = i;
                }
                if (idx < 0) continue;
                Event move = queue[idx];
                double was = move.At;
                double now = was - r.Amount;
                if (now < e.At) now = e.At;
                move.At = now;
                queue[idx] = move;
                r.Fired++;
                r.Moved += was - now;
                Add(run, opt, e.At, LogKind.Plain, Lang.T("log.reset", r.TargetName),
                    Lang.T("log.reset.why", r.Source, Fmt(r.Chance),
                           Lang.T(string.Equals(r.Trigger, "AttackEnemyCrit", StringComparison.OrdinalIgnoreCase)
                                  ? "log.reset.onCrit" : "log.reset.onAttack"),
                           Fmt(r.Amount), Fmt(now), Fmt(was)),
                    0, 0, null, null);
            }
        }

        static void Procs(GameData game, Character c, CharacterTotals totals, ExactRolls seeded,
                          TimelineTarget target, TimelineOptions opt, TimelineRun run,
                          List<Event> queue, Dictionary<string, Dot> dots, ref int order, Event e,
                          List<Proc> procs, HitRoll roll, Random hitRng, Random procRng,
                          double pth, double[] thresholds, double[] multipliers, double critDamage,
                          double below, List<double> perSecond, StarBook book, bool crit)
        {
            // HOW MANY CHANCES TO ROLL A PROC THERE WERE OVER THE RUN AT ALL.
            //
            // Counted so the check can compare it against ITS OWN count of strikes per
            // second. Three times on 7 September the engine learned a new way to strike —
            // stars, the assigned attack, auras — and three times the proc expectation did
            // not know of it: "104 procs against the expected 73". Each time the error was
            // not in the engine, and it had to be unpicked after the fact.
            //
            // Now two numbers are enough to compare, and the check ITSELF says that a new way
            // was forgotten instead of going red on the rate.
            run.ProcChances++;
            Refresh(run, opt, queue, e, procRng, crit);
            foreach (Proc pr in procs)
            {
                AttackSkill a = pr.Skill;
                if (pr.Chance <= 0.0001) continue;
                // "On critical attack" is not rolled at all without a crit.
                if (pr.OnlyCrit && !crit) continue;
                // ITS OWN COOLDOWN. While it runs the skill does not fire, however often the
                // roll comes up (the user's reminder, see the `Proc` class).
                if (e.At < pr.ReadyAt - 1e-9) { pr.Blocked++; run.ProcBlocked++; continue; }
                // A ROLL FOR EACH CASE SEPARATELY, not one shared roll for all procs
                // (PLAN, Zh2). There is one exception — the weapon pool, handled elsewhere.
                pr.Rolled++;
                if (procRng.NextDouble() * 100 >= pr.Chance) continue;
                pr.ReadyAt = e.At + pr.Cooldown;
                pr.Fired++;
                if (pr.NoDamage != null)
                {
                    Silent(run, opt, book, target, game, e, pr, perSecond);
                    continue;
                }
                Press fired = new Press();
                fired.Skill = a;
                DbRecord prec = game.Record(a.Dbr);
                fired.Hands = prec == null ? Hands.Main : SkillHands.Of(game, prec, c);
                run.Procs++;
                Fire(game, c, totals, seeded, target, opt, run, queue, dots, ref order, e,
                     fired, roll, hitRng, pth, thresholds, multipliers, critDamage,
                     below, perSecond, LogKind.Proc, Lang.T("log.fire.proc"), book);
            }
        }

        // DEVOTION STARS TIED TO THE SKILL THAT STRUCK.
        //
        // A devotion star hangs ON A SKILL and fires only from it. The user's remark of
        // 7 September: "I do not see Arcane Bomb firing after Shadow Strike fires". It could
        // not have been there — the engine put stars into the common proc list, and they
        // rained on every swing without once asking whose skill it was.
        //
        // An empty `book` means the call comes from a star's own strike: a star does not
        // trigger a star, and there must be no recursion here.
        static void Stars(TimelineRun run, TimelineOptions opt, StarBook book, Event e,
                          string skillDbr, bool crit, bool castBuff,
                          GameData game, Character c, CharacterTotals totals, ExactRolls seeded,
                          TimelineTarget target, List<Event> queue, Dictionary<string, Dot> dots,
                          ref int order, HitRoll roll, Random hitRng, double pth,
                          double[] thresholds, double[] multipliers, double critDamage,
                          double below, List<double> perSecond)
        {
            if (book == null || string.IsNullOrEmpty(skillDbr)) return;
            List<Proc> bound;
            if (!book.By.TryGetValue(skillDbr, out bound)) return;
            if (!book.Seen.Contains(skillDbr)) book.Seen.Add(skillDbr);
            foreach (Proc pr in bound)
            {
                // "ONLY ON A CRIT" is `AttackEnemyCrit`, and it means exactly what it says:
                // without a crit there is no roll at all.
                if (pr.OnlyCrit && !crit) continue;
                // "On applying a buff" is not rolled on a STRIKE — it has its own moment and
                // is called separately (`CastBuffStars`).
                if (pr.OnCastBuff != castBuff) continue;
                if (pr.Chance <= 0.0001) continue;
                if (e.At < pr.ReadyAt - 1e-9) { pr.Blocked++; run.ProcBlocked++; continue; }
                pr.Rolled++;
                if (book.Rng.NextDouble() * 100 >= pr.Chance) continue;
                pr.ReadyAt = e.At + pr.Cooldown;
                pr.Fired++;
                if (pr.NoDamage != null)
                {
                    Silent(run, opt, book, target, game, e, pr, perSecond);
                    continue;
                }
                Press fired = new Press();
                fired.Skill = pr.Skill;
                DbRecord srec = game.Record(pr.Skill.Dbr);
                fired.Hands = srec == null ? Hands.Main : SkillHands.Of(game, srec, c);
                run.Procs++;
                // THE BOOK IS PASSED ON, NOT CLEARED.
                //
                // We used to clear it to guard against "a star triggers a star" recursion,
                // but the resistance reduction the star itself carries was lost with it:
                // Rumor takes −23% off cold and −30% off poison and acid, and none of it was
                // applied. There is no recursion anyway: `Stars` looks up the ACTIVATOR
                // SKILL's record, and a star's record is never an activator.
                Fire(game, c, totals, seeded, target, opt, run, queue, dots, ref order, e,
                     fired, roll, hitRng, pth, thresholds, multipliers, critDamage,
                     below, perSecond, LogKind.Devotion, Lang.T("log.fire.devotion"), book);
            }
        }

        // "N% REDUCED HEALTH" sits in the skill's record or along its record chain
        // (`buffSkillName`: for Annihilation and Bloody Pox the number is on the debuff).
        // The `Min–Max` bracket is taken at its midpoint; across records, the largest.
        public static double PercentLifeOf(GameData game, string dbr, int level)
        {
            double best = 0;
            string at = dbr;
            for (int hop = 0; hop < 4 && !string.IsNullOrEmpty(at); hop++)
            {
                DbRecord r = game.Record(at);
                if (r == null) break;
                int slot = SkillText.Slot(r, level);
                double lo = AttackSkill.Value(r, "offensivePercentCurrentLifeMin", slot);
                double hi = AttackSkill.Value(r, "offensivePercentCurrentLifeMax", slot);
                double v = hi > lo ? (lo + hi) / 2 : lo;
                if (v > best) best = v;
                at = ItemViewFactory.NextSkillRecord(r);
            }
            return best;
        }

        // The same from gear — already in the character's totals.
        static double GearPercentLife(CharacterTotals t)
        {
            if (t == null) return 0;
            TotalsEntry lo, hi;
            double a = t.Fields.TryGetValue("offensivePercentCurrentLifeMin", out lo) ? lo.Value : 0;
            double b = t.Fields.TryGetValue("offensivePercentCurrentLifeMax", out hi) ? hi.Value : 0;
            return b > a ? (a + b) / 2 : a;
        }

        // TAKE A PERCENT OF THE TARGET'S CURRENT LIFE. The rules come from the user's words
        // of 11 September and from the game's records:
        //   * such effects DO NOT STACK — the largest is taken (the caller already passes
        //     the largest of this strike's sources);
        //   * enemies have their own resistance, `defensivePercentCurrentLife`, which can
        //     exceed one hundred — then the effect dies out entirely;
        //   * crit, armour and damage resistances do not act on it (MECHANICS 27b);
        //   * the percent is of CURRENT life: that is tracked as "maximum minus everything
        //     that already landed". It is exact only when the target's life is exact.
        static void PercentLifeHit(TimelineRun run, TimelineOptions opt, TimelineTarget target, Event e,
                                   double pct, string from, List<double> perSecond)
        {
            if (pct <= 0 || target == null || target.Life <= 0) return;
            double now = target.Life - run.Total;
            if (now <= 0) return;                       // target already dead — nothing to take
            double eff = pct * Math.Max(0, 1 - target.PercentLifeResist / 100.0);
            if (eff <= 0.0001) { run.PercentLifeBlocked++; return; }
            double dmg = now * eff / 100.0;
            run.Total += dmg;
            run.TotalBefore += dmg;
            run.PercentLifeTotal += dmg;
            run.PercentLifeHits++;
            While(perSecond, e.At, dmg);
            Add(run, opt, e.At, LogKind.Life, Lang.T("log.life.cut", Fmt(eff), from),
                Lang.T("log.life.cutWhy", Fmt0(now), Fmt0(now - dmg)) +
                (target.PercentLifeResist > 0.0001
                     ? "; " + Lang.T("log.life.cutResist", Fmt(pct), Fmt0(target.PercentLifeResist)) : ""),
                dmg, dmg, null, null);
        }

        // FIRED BUT DOES NOT STRIKE: a summon, a buff, a debuff. Their roll and cooldown are
        // real; only the damage is missing — and we must not keep silent about it (the user's
        // request: "emulate it and at least put the fact in the log").
        //
        //   buff    goes up ON US for its duration — task items 7 and 9;
        //   debuff  is applied TO THE TARGET: resistance reduction and ability cuts, as for a
        //           striking proc (Annihilation gives −55 defensive ability for 5 s);
        //   summon  only a line: pets' damage is not in the queue (task item 9).
        static void Silent(TimelineRun run, TimelineOptions opt, StarBook book, TimelineTarget target,
                           GameData game, Event e, Proc pr, List<double> perSecond)
        {
            SilentEffect(run, opt, book, target, game, e, pr.NoDamage.Value, pr.Dbr, pr.Level, pr.Name,
                         pr.Duration, LogKind.Silent, Lang.T("log.silent.fired") + "   ",
                         Lang.T("log.from", pr.Activator) + ", ", perSecond);
            run.ProcsSilent++;
            if (!run.SilentNames.Contains(pr.Name)) run.SilentNames.Add(pr.Name);
        }

        // WHAT A NON-STRIKING SKILL DOES — one body for the self-fired case (`Silent`) and
        // for the pressed one (`Press.SilentKind`).
        static void SilentEffect(TimelineRun run, TimelineOptions opt, StarBook book, TimelineTarget target,
                                 GameData game, Event e, EffectKind kind, string dbr, int level, string name,
                                 double duration, LogKind lineKind, string verb, string from, List<double> perSecond)
        {
            run.Count(dbr, name);
            // A SELF-FIRED SUMMON (Conjure Living Blossom, Living Shadow) is a request for a
            // pet: the pet itself enters the pool at the start of the next loop step (E7).
            if (kind == EffectKind.Summon && book != null)
                book.Summons.Add(new KeyValuePair<string, double>(dbr, e.At));
            string tail;
            if (kind == EffectKind.Buff && duration > 0.0001 && book != null && book.Buffs != null)
            {
                book.Buffs.Raise(dbr, name, e.At, duration);
                tail = Lang.T("log.silent.buff", Fmt(duration));
            }
            else if (kind == EffectKind.Debuff && book != null && book.Debuffs != null)
            {
                double secs = duration > 0.0001 ? duration : 3;
                List<string> said = new List<string>();
                List<ResistCut> cuts = SkillResistCuts(game, dbr, level, name);
                if (cuts.Count > 0)
                {
                    book.Debuffs.Put(target, dbr, name, cuts, e.At, secs);
                    said.Add(Lang.T("log.silent.resists"));
                }
                AbilityCut ab = SkillAbilityCut(game, dbr, level);
                if (ab.Any)
                {
                    book.Debuffs.PutAbility(dbr, name, ab, e.At);
                    if (ab.Defensive > 0.0001) said.Add(Lang.T("log.silent.defense", Fmt0(ab.Defensive)));
                    if (ab.Offensive > 0.0001) said.Add(Lang.T("log.silent.offense", Fmt0(ab.Offensive)));
                    if (ab.Absolute > 0.0001) said.Add(Lang.T("log.silent.absolute", Fmt0(ab.Absolute)));
                    if (ab.Relative > 0.0001) said.Add(Lang.T("log.silent.relative", Fmt0(ab.Relative)));
                }
                tail = Lang.T("log.silent.debuff", Fmt(secs)) +
                       (said.Count > 0 ? ": " + string.Join(", ", said.ToArray())
                                       : ", " + Lang.T("log.silent.debuffNothing"));
            }
            else if (kind == EffectKind.Summon)
                // IT USED TO SAY: "we do not count pets' damage in the queue (item 9)". Since
                // 12 September we do: the summoned one enters the pool and strikes with its
                // own lines (E7). If its kind was not found in the pool, the next line says
                // so.
                tail = Lang.T("log.silent.summon");
            else
                tail = Lang.T("log.silent.noDamage", EffectWord(kind));
            Add(run, opt, e.At, lineKind, verb + name, from + tail, 0, 0, null, null);
            // A DEBUFF TAKES LIFE WHEN IT LANDS: Annihilation's "20% reduced health"
            // (MECHANICS 77).
            if (kind == EffectKind.Debuff)
                PercentLifeHit(run, opt, target, e, PercentLifeOf(game, dbr, level), name, perSecond);
        }

        // ================================================================
        // WHAT ELSE CAN BE "PRESSED" — BEYOND THE DAMAGING SKILLS (`AttackSkills.Of`).
        //
        // The census of 10 September (MECHANICS 76) found three kinds of skill that were
        // neither in the queue nor among the ticks, although in game they are pressed or they
        // fire:
        //   a star whose condition we do not roll but which DOES STRIKE — Targo's Hammer (on
        //     a block), Hyrian's Glare (when we are hit). A tick used to raise it as a buff —
        //     now it strikes with damage on cooldown;
        //   something pressed WITHOUT damage — Curse of Frailty, the soldier's War Cry,
        //     Madness from a mask: on cooldown it applies its debuff and rolls the stars tied
        //     to it (Falcon Swoop on War Cry);
        //   a buff FROM GEAR (with or without a condition) and a buff of class
        //     `Skill_BuffRadius` — among the timed buffs (`TimedCandidates`).
        // One list for the window, the engine, the check and the census — so that the window
        // does not offer one thing while the queue schedules another.
        public sealed class Extra
        {
            public string Dbr = "", Name = "", Source = "";
            public EffectKind Kind;   // Damage for a striking star; otherwise debuff or no damage
            public string Trigger = "", When = "";
            public int Level = 1;
            public AttackSkill Skill;   // for the striking ones
        }

        public static List<Extra> PressExtras(GameData game, Character c)
        {
            List<Extra> made = new List<Extra>();
            if (game == null || c == null) return made;
            HashSet<string> have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AttackSkill a in AttackSkills.Of(game, c)) have.Add(a.Dbr);
            SkillText.LevelBonus lb = SkillText.LevelBonus.Of(game, c);
            // 1. Stars whose condition we do not roll but which DO STRIKE.
            foreach (Star st in ByOtherCondition(game, c))
            {
                if (have.Contains(st.Dbr)) continue;
                int lvl = DevotionLevelOf(c, st.Dbr);
                AttackSkill a = AttackSkills.One(game, st.Dbr, lvl, "devotion: " + st.Name);
                if (a == null) continue;
                have.Add(st.Dbr);
                Extra x = new Extra();
                x.Dbr = st.Dbr; x.Name = st.Name; x.Kind = EffectKind.Damage; x.Level = lvl; x.Skill = a;
                x.Trigger = st.Trigger ?? "";
                x.When = ActiveSkills.TriggerName(st.Trigger);
                x.Source = "a star on \"" + st.ActivatorName + "\"";
                made.Add(x);
            }
            // 2. Pressed without damage — mastery skills.
            foreach (Skill s in c.Skills)
            {
                if (s.Level <= 0 || string.IsNullOrEmpty(s.Name) || SkillsForm.IsDevotion(s.Name)) continue;
                if (have.Contains(s.Name)) continue;
                DbRecord r = game.Record(s.Name);
                if (r == null || !SilentPressableClass(r.Class)) continue;
                EffectKind kind = SilentKind(game, s.Name, r.Class);
                if (kind == EffectKind.Summon || kind == EffectKind.Buff) continue;
                have.Add(s.Name);
                Extra x = new Extra();
                x.Dbr = s.Name; x.Kind = kind; x.Level = s.Level + lb.For(s.Name, s.Level);
                x.Name = SkillText.Name(game, s.Name) ?? s.Name;
                string folder = ItemViewFactory.MasteryFolder(s.Name);
                x.Source = folder == null ? "class" : (ItemViewFactory.MasteryName(game, folder) ?? folder);
                made.Add(x);
            }
            // 3. Pressed without damage — gear and sets: with no condition, or with one we do
            //    not roll. "On attack/on crit" fires by itself (`GearSilent`) and does not
            //    come here.
            foreach (AttackSkills.GrantedSkill g in AttackSkills.GearGranted(game, c))
            {
                if (have.Contains(g.Dbr)) continue;
                if (g.Trigger == "AttackEnemy" || g.Trigger == "AttackEnemyCrit") continue;
                if (!SilentPressableClass(g.Class)) continue;
                EffectKind kind = SilentKind(game, g.Dbr, g.Class);
                if (kind == EffectKind.Summon || kind == EffectKind.Buff) continue;
                have.Add(g.Dbr);
                Extra x = new Extra();
                x.Dbr = g.Dbr; x.Name = g.Name; x.Kind = kind; x.Level = g.Level;
                x.Trigger = g.Trigger ?? ""; x.When = g.When ?? ""; x.Source = g.Source;
                made.Add(x);
            }
            return made;
        }

        // BUFFS QUEUED ON COOLDOWN FOR THEIR OWN DURATION. Mastery skills
        // (`Skill_BuffSelf*`, `Skill_BuffRadius`, not toggles) and buffs from gear and sets —
        // with no condition or with an unmodellable one. Devotion stars whose condition we do
        // not roll live in their own place (`ByOtherCondition`); they are not here.
        public sealed class TimedBuff
        {
            public string Dbr = "", Name = "", Source = "", When = "";
            public int Level = 1;
        }

        public static List<TimedBuff> TimedCandidates(GameData game, Character c)
        {
            List<TimedBuff> made = new List<TimedBuff>();
            if (game == null || c == null) return made;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SkillText.LevelBonus lb = SkillText.LevelBonus.Of(game, c);
            foreach (Skill s in c.Skills)
            {
                if (s.Level <= 0 || string.IsNullOrEmpty(s.Name) || SkillsForm.IsDevotion(s.Name)) continue;
                DbRecord r = game.Record(s.Name);
                if (r == null) continue;
                string cls = r.Class ?? "";
                if (CharacterTotals.IsToggleClass(cls)) continue;   // toggles stand by themselves
                if (!cls.StartsWith("Skill_BuffSelf", StringComparison.OrdinalIgnoreCase) &&
                    !cls.Equals("Skill_BuffRadius", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(s.Name)) continue;
                TimedBuff b = new TimedBuff();
                b.Dbr = s.Name; b.Level = s.Level + lb.For(s.Name, s.Level);
                b.Name = SkillText.Name(game, s.Name) ?? s.Name;
                string folder = ItemViewFactory.MasteryFolder(s.Name);
                b.Source = folder == null ? "class" : (ItemViewFactory.MasteryName(game, folder) ?? folder);
                made.Add(b);
            }
            foreach (AttackSkills.GrantedSkill g in AttackSkills.GearGranted(game, c))
            {
                if (g.Trigger == "AttackEnemy" || g.Trigger == "AttackEnemyCrit") continue;   // self-firing
                if (CharacterTotals.IsToggleClass(g.Class)) continue;
                if (!SilentPressableClass(g.Class) && !g.Class.StartsWith("Skill_Buff", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (SilentKind(game, g.Dbr, g.Class) != EffectKind.Buff) continue;
                if (SkillTiming.Duration(game, g.Dbr, g.Level) <= 0) continue;   // nothing to hold
                if (!seen.Add(g.Dbr)) continue;
                TimedBuff b = new TimedBuff();
                b.Dbr = g.Dbr; b.Name = g.Name; b.Level = g.Level; b.Source = g.Source; b.When = g.When ?? "";
                made.Add(b);
            }
            return made;
        }

        // A BUFF THE PANEL SHOWS AS A DAMAGING SKILL. Blood of Dreeg is `Skill_BuffRadius`,
        // and its buff record carries `offensivePoisonMin 5…88`: that is an ADDITION of flat
        // poison to every strike while the buff stands, not a strike. In the panel's "what we
        // strike with" it belongs there rightly (the game prints its number the same way),
        // but in the queue it must be raised as a buff, not pressed as a strike — pressed, it
        // "struck" for 4300 (MECHANICS 77a).
        public static bool IsBuffSkill(GameData game, string dbr)
        {
            DbRecord r = game == null ? null : game.Record(dbr ?? "");
            string cls = r == null ? "" : (r.Class ?? "");
            return (cls.StartsWith("Skill_Buff", StringComparison.OrdinalIgnoreCase) ||
                    cls.StartsWith("SkillBuff", StringComparison.OrdinalIgnoreCase)) &&
                   cls.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) < 0 &&
                   !CharacterTotals.IsToggleClass(cls);
        }

        // A devotion star's level is `DevotionLevel` from the save (MECHANICS 51).
        public static int DevotionLevelOf(Character c, string dbr)
        {
            foreach (Skill d in c.Skills)
                if (string.Equals(d.Name, dbr, StringComparison.OrdinalIgnoreCase))
                    return d.DevotionLevel > 0 ? d.DevotionLevel : Math.Max(1, d.Level);
            return 1;
        }

        // SERVICE SKILLS — every character has them and they are not part of the fight:
        // potions, walking, the kick, evade, the pet command — and the default attack itself,
        // which IS the swing.
        public static bool ServiceClass(string cls)
        {
            cls = cls ?? "";
            foreach (string one in new[] { "Skill_ChargePotion", "Skill_PotionModifier", "Skill_PotionContainer",
                                           "Skill_Move", "Skill_Kick", "Skill_Evade", "Skill_PetAttack",
                                           "Skill_WeaponPool_Default", "Skill_WPAttack_BasicAttack" })
                if (cls.Equals(one, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Whether this can be pressed as a non-striking skill: not a passive, not a modifier,
        // not a toggle, not a service skill, not a form, not a summon.
        static bool SilentPressableClass(string cls)
        {
            cls = cls ?? "";
            if (cls.Length == 0 || ServiceClass(cls)) return false;
            if (cls.IndexOf("Mastery", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (cls.StartsWith("Skill_Passive", StringComparison.OrdinalIgnoreCase)) return false;
            if (cls.Equals("Skill_Modifier", StringComparison.OrdinalIgnoreCase) ||
                cls.Equals("Skill_Transmuter", StringComparison.OrdinalIgnoreCase) ||
                cls.IndexOf("ProjectileModifier", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.StartsWith("SkillSecondary", StringComparison.OrdinalIgnoreCase)) return false;
            if (CharacterTotals.IsToggleClass(cls)) return false;
            if (cls.StartsWith("Skill_Shapeshift", StringComparison.OrdinalIgnoreCase)) return false;
            if (cls.IndexOf("SpawnPet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.IndexOf("Summon", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }
        // ================================================================

        // What kind of non-striking skill this is: a summon is decided by CLASS (the damage
        // numbers in a summon's record are the pet's, not ours), a debuff by the game's
        // `debufSkill` marker or by a class holding the word `Debuf`, either on the record
        // itself or on the one it points to (`buffSkillName`: Annihilation's top record is
        // `Skill_AttackBuff` while the debuff is one floor down).
        public static EffectKind SilentKind(GameData game, string dbr, string cls)
        {
            cls = cls ?? "";
            if (cls.IndexOf("SpawnPet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.IndexOf("Summon", StringComparison.OrdinalIgnoreCase) >= 0) return EffectKind.Summon;
            string at = dbr;
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(at); hop++)
            {
                DbRecord r = game == null ? null : game.Record(at);
                if (r == null) break;
                if (r.GetInt("debufSkill") == 1 ||
                    (r.Class ?? "").IndexOf("Debuf", StringComparison.OrdinalIgnoreCase) >= 0) return EffectKind.Debuff;
                at = ItemViewFactory.NextSkillRecord(r);
            }
            if (cls.IndexOf("Buff", StringComparison.OrdinalIgnoreCase) >= 0) return EffectKind.Buff;
            // A CUT TO THE TARGET IN THE RECORD ITSELF is a debuff too, whatever the class.
            // Madness from a mask is `Skill_AttackRadius` with no damage and no `debufSkill`,
            // while its record holds −20 flat resistances: by the class marker it read as "no
            // damage", and the press applied no debuff (MECHANICS 76).
            if (game != null && (SkillAbilityCut(game, dbr, 1).Any || SkillResistCuts(game, dbr, 1, "").Count > 0))
                return EffectKind.Debuff;
            return EffectKind.None;
        }

        // THE LABEL WORD FOR PRINTING — in the log, the run's notes, the press list and the
        // census. One place, from the language file: it must not be compared; `EffectKind` is.
        public static string EffectWord(EffectKind kind)
        {
            switch (kind)
            {
                case EffectKind.Damage: return Lang.T("log.effect.damage");
                case EffectKind.Summon: return Lang.T("log.effect.summon");
                case EffectKind.Buff:   return Lang.T("log.effect.buff");
                case EffectKind.Debuff: return Lang.T("log.effect.debuff");
                default:                return Lang.T("log.effect.none");
            }
        }

        // GEAR AND SET SKILLS "ON ATTACK" AND "ON CRIT" THAT DO NOT STRIKE.
        // One list for the engine and for the check, so the check expects exactly what the
        // engine rolls. Striking ones are not here: they are already in `AttackSkills.Of`.
        public sealed class GearProc
        {
            public string Dbr, Name, Source;
            public EffectKind Kind;
            public int Level;
            public double Chance, Cooldown, Duration;
            public bool OnlyCrit;
        }

        public static List<GearProc> GearSilent(GameData game, Character c, CharacterTotals totals)
        {
            List<GearProc> made = new List<GearProc>();
            List<AttackSkill> hitting = AttackSkills.Of(game, c);
            AttackSkills.Attach at = AttackSkills.Attach.Of(game, c);
            ItemSkillMods mods = ItemSkillMods.Of(game, c);
            SkillText.LevelBonus lb = SkillText.LevelBonus.Of(game, c);
            foreach (AttackSkills.GrantedSkill g in AttackSkills.GearGranted(game, c))
            {
                if (g.Trigger != "AttackEnemy" && g.Trigger != "AttackEnemyCrit") continue;
                if (g.Chance <= 0.0001) continue;
                bool hits = false;
                foreach (AttackSkill a in hitting)
                    if (string.Equals(a.Dbr, g.Dbr, StringComparison.OrdinalIgnoreCase)) { hits = true; break; }
                if (hits) continue;
                GearProc gp = new GearProc();
                gp.Dbr = g.Dbr; gp.Name = g.Name; gp.Source = g.Source; gp.Level = g.Level;
                gp.Chance = g.Chance;
                gp.OnlyCrit = g.Trigger == "AttackEnemyCrit";
                gp.Kind = SilentKind(game, g.Dbr, g.Class);
                gp.Cooldown = SkillTiming.Cooldown(game, c, totals, at, mods, lb, g.Dbr, g.Level);
                gp.Duration = SkillTiming.Duration(game, g.Dbr, g.Level);
                made.Add(gp);
            }
            return made;
        }

        // A PRESSED SKILL'S STRIKE. It differs from a swing in exactly two ways, both
        // measured: there is no hand layout (it always strikes with its own), and the damage
        // comes from `HitHandMin`, where the skill's share and its own damage are already
        // summed (MECHANICS 38).
        // `lineKind` is the log line's kind (its colour in the window) and at the same time
        // the proc marker for the totals: the proc sum used to recognise a proc by the word
        // "proc" inside `kindWord`.
        static void Fire(GameData game, Character c, CharacterTotals totals, ExactRolls seeded,
                         TimelineTarget target, TimelineOptions opt, TimelineRun run,
                         List<Event> queue, Dictionary<string, Dot> dots, ref int order,
                         Event e, Press p, HitRoll roll, Random hitRng, double pth,
                         double[] thresholds, double[] multipliers, double critDamage,
                         double below, List<double> perSecond, LogKind lineKind, string kindWord, StarBook book,
                         List<Proc> procs = null, Random procRng = null)
        {
            if (p.Skill != null) run.Count(p.Skill.Dbr, p.Skill.Name);
            ProjectileVolley volley = ProjectileVolley.Of(p.Skill);
            // A persistent rain repeats its volley at the imported interval. Later waves
            // use the loadout and target defenses at their own impact time.
            if (volley.Interval > 0)
                for (double delay = volley.Interval; delay < volley.Duration - 1e-9 && e.At + delay <= opt.Seconds;
                     delay += volley.Interval)
                    queue.Add(new Event(e.At + delay, e.Actor, ++order, p)
                    {
                        What = Kind.Volley,
                        VolleyLog = lineKind,
                        VolleyCaption = kindWord,
                        VolleyTriggersProcs = procs != null
                    });
            FireVolley(game, c, totals, seeded, target, opt, run, queue, dots, ref order, e, p,
                       roll, hitRng, pth, thresholds, multipliers, critDamage, below, perSecond,
                       lineKind, kindWord, book, volley, procs, procRng);
        }

        static void FireVolley(GameData game, Character c, CharacterTotals totals, ExactRolls seeded,
                               TimelineTarget target, TimelineOptions opt, TimelineRun run,
                               List<Event> queue, Dictionary<string, Dot> dots, ref int order,
                               Event e, Press p, HitRoll roll, Random hitRng, double pth,
                               double[] thresholds, double[] multipliers, double critDamage,
                               double below, List<double> perSecond, LogKind lineKind, string kindWord, StarBook book,
                               ProjectileVolley volley, List<Proc> procs, Random procRng)
        {
            bool multiple = volley.Count > 1 || volley.Interval > 0;
            if (multiple) run.ProjectileVolleys++;
            double defenceCut = multiple && book != null ? book.Debuffs.DefenceCut(e.At) : 0;
            for (int i = 0; i < volley.Count; i++)
            {
                string title = p.Name;
                if (multiple)
                {
                    title += " [" + (i + 1) + "/" + volley.Count + "]";
                    if (volley.Coverage < 1 && hitRng.NextDouble() >= volley.Coverage)
                    {
                        run.Misses++;
                        Add(run, opt, e.At, LogKind.Miss, Lang.T("log.miss") + ": " + kindWord + " " + title,
                            "", 0, 0, null, null);
                        continue;
                    }
                }

                if (i > 0 && book != null)
                {
                    // Earlier projectiles can apply reductions before a later one lands.
                    target.AbsoluteCut = book.Debuffs.AbsoluteCut(e.At);
                    target.RelativeCut = book.Debuffs.RelativeCut(e.At);
                    target.ArmorCut = book.Debuffs.ArmorCut(e.At);
                    double cut = book.Debuffs.DefenceCut(e.At);
                    if (cut != defenceCut)
                    {
                        defenceCut = cut;
                        double oa = Combat.Ability(game, c, totals, c.Cunning,
                                                   "characterOffensiveAbility", "characterDexterity");
                        pth = Math.Max(Combat.PthMinimum(game),
                                       Combat.Pth(game, oa, Math.Max(0, target.DefensiveAbility - cut)));
                        below = Combat.BelowThreshold(game, pth);
                    }
                }

                bool crit;
                bool landed = FireHit(game, c, totals, seeded, target, opt, run, queue, dots, ref order, e, p,
                                      roll, hitRng, pth, thresholds, multipliers, critDamage, below, perSecond,
                                      lineKind, kindWord, book, title, multiple, out crit);
                if (procs != null && (!multiple || landed))
                    Procs(game, c, totals, seeded, target, opt, run, queue, dots, ref order, e,
                          procs, roll, hitRng, procRng, pth, thresholds, multipliers, critDamage, below,
                          perSecond, book, multiple ? crit : run.LastCrit);
            }
        }

        static bool FireHit(GameData game, Character c, CharacterTotals totals, ExactRolls seeded,
                            TimelineTarget target, TimelineOptions opt, TimelineRun run,
                            List<Event> queue, Dictionary<string, Dot> dots, ref int order,
                            Event e, Press p, HitRoll roll, Random hitRng, double pth,
                            double[] thresholds, double[] multipliers, double critDamage,
                            double below, List<double> perSecond, LogKind lineKind, string kindWord,
                            StarBook book, string title, bool multiple, out bool crit)
        {
            // A SKILL WITHOUT A WEAPON-DAMAGE SHARE STRIKES ONCE, NOT ONCE PER HAND.
            //
            // For such a skill `HitHandMin` is the same for both hands — it holds only its
            // OWN damage, and that flies once per swing. Striking with "both" would double
            // it: Meteor came out as 6131 and 6131 instead of a single 6131. Hands make sense
            // exactly where there is something to strike with — i.e. where there is a weapon
            // share.
            run.LastCrit = false;
            bool byWeapon = p.Skill != null && Math.Abs(p.Skill.WeaponPct) > 0.0001;
            bool[] swung = byWeapon
                         ? new[] { p.Hands != Hands.Off, p.Hands == Hands.Both || p.Hands == Hands.Off }
                         : new[] { true, false };
            double[] rr = { hitRng.NextDouble() * 100, hitRng.NextDouble() * 100 };
            int[] tiers = { Combat.Tier(pth, rr[0], thresholds), Combat.Tier(pth, rr[1], thresholds) };
            bool[] landed = { swung[0] && tiers[0] > 0, swung[1] && tiers[1] > 0 };

            string why = p.Name + (string.IsNullOrEmpty(p.Note) ? "" : ", " + p.Note);
            for (int h = 0; h < 2; h++)
                if (swung[h])
                    why += ", " + Lang.T(h == 0 ? "log.roll.main" : "log.roll.off") + " " + Fmt(rr[h]) +
                           (landed[h] ? " < " : " ≥ ") + Fmt(pth);

            // The crit for the `AttackEnemyCrit` condition — the tier has already been
            // rolled, and damage does not affect whether a star fires, so it can be known in
            // advance.
            crit = (landed[0] && tiers[0] > 1) || (landed[1] && tiers[1] > 1);
            // For "on critical attack" procs — those are rolled AFTER the strike.
            run.LastCrit = crit;

            // THE STRIKE'S OWN DEBUFF PART goes to the target the moment it lands.
            //
            // Until 12 September everything applied it except the ordinary strike: a debuff
            // skill, a proc, a striking aura, a pet's strike — but not a swing or the assigned
            // attack, so the berserker's Recklessness (−270 to the target's defensive
            // ability) never acted at all. The numbers are at `AttackAbilityCut`.
            if (book != null && book.Debuffs != null && p.Skill != null && (landed[0] || landed[1]))
            {
                AbilityCut own = AttackAbilityCut(p.Skill);
                if (own.Any) book.Debuffs.PutAbility(p.Skill.Dbr, p.Name, own, e.At);
            }

            if (!landed[0] && !landed[1])
            {
                run.Misses++;
                Add(run, opt, e.At, LogKind.Miss, Lang.T("log.miss") + ": " + kindWord + " " + title, why, 0, 0, null, null);
                // Preserve the single-strike proc rule (MECHANICS 33). A volley gives one
                // opportunity for each projectile that actually hits the target.
                if (!multiple)
                    Stars(run, opt, book, e, p.Skill == null ? null : p.Skill.Dbr, false, false,
                          game, c, totals, seeded, target, queue, dots, ref order, roll, hitRng,
                          pth, thresholds, multipliers, critDamage, below, perSecond);
                return false;
            }
            run.Hits++;

            // THE RESISTANCE REDUCTION CARRIED BY THE SKILL ITSELF.
            //
            // Procs carry it on a par with auras: Rumor −30% to poison and acid, Widow's Bite
            // −10%/−10%/−12%. Until 9 September NONE of it was applied — the plain answer to
            // the user's question "are all the buffs from them used" was "not one". It is cut
            // for the skill's own duration and restored when that expires; a repeat hit
            // extends it.
            //
            // THE ORDER MATTERS: we cut BEFORE computing this very strike's damage — the game
            // does the same, a debuff is applied by the strike and acts at once.
            if (book != null && p.Skill != null)
            {
                List<ResistCut> cuts = SkillResistCuts(game, p.Skill.Dbr, p.Skill.Level, p.Name);
                if (cuts.Count > 0)
                {
                    double secs = SkillTiming.Duration(game, p.Skill.Dbr, p.Skill.Level);
                    if (secs <= 0) secs = 3;      // no duration in the record — take three seconds
                    book.Debuffs.Put(target, p.Skill.Dbr, p.Name, cuts, e.At, secs);
                }
                // THE DEFENSIVE-ABILITY CUT decides whether we hit and how often we crit, so
                // it matters more than resistances. Its own duration sits next to the number
                // (`offensiveSlowDefensiveAbilityDurationMin`).
                AbilityCut ab = SkillAbilityCut(game, p.Skill.Dbr, p.Skill.Level);
                if (ab.Any) book.Debuffs.PutAbility(p.Skill.Dbr, p.Name, ab, e.At);
            }
            // AND THE CUT CARRIED BY THE GEAR ITSELF, not by a skill.
            //
            // The user's request of 9 September: "check that it is counted from ANY source
            // (skill/ability/devotion/set)". Skills all went through `p.Skill` above, while
            // items went nowhere: the `offensiveSlowDefensiveAbilityMin` field also sits on
            // weapon prefixes (eighteen records in the database), and ANY strike of yours
            // applies such a cut.
            if (book != null) book.Debuffs.PutGear(totals, e.At);

            // The same calculation as the panel's, but with bracket rolls and WITH THE SKILL.
            roll.NextHit();
            DamageModel one = new DamageModel(game, c, totals, p.Skill, true, seeded, roll, p.AtCharge);
            // THE DOT'S SOURCE: if the skill has a weapon-damage share it belongs to the
            // common weapon pool; with no share it is its own source (the user's rule,
            // MECHANICS 37).
            string origin = p.Skill != null && Math.Abs(p.Skill.WeaponPct) > 0.0001
                          ? WeaponSource : p.Skill == null ? WeaponSource : p.Skill.Dbr;

            double damage = 0, before = 0;
            double[] hands = new double[2];
            double[] handsBefore = new double[2];
            for (int hand = 0; hand < 2; hand++)
            {
                if (!landed[hand]) continue;
                run.HandHits++;
                // "+% CRIT DAMAGE" CAN BELONG TO THE SKILL ITSELF, AND IT IS NO TRIFLE.
                //
                // The Nightblade has its own addition on almost every strike: Shadow Strike
                // up to +70%, Phantasmal Blade +50%, Ring of Steel +45%, and an amulet can
                // add another +20% through a skill modifier. These do not belong to the
                // character (a modifier belongs to the strike) and are not in his totals.
                //
                // Hence the multipliers in the user's shots of 12 September: on `_Hamarina`
                // the auras strike at ×1.29/×1.39 while the same character with Ring of Steel
                // gets ×1.84/×1.94. One tier explains that not at all; two additions to ONE
                // ladder explain it exactly.
                double critMul = Combat.CritMultiplier(tiers[hand], multipliers,
                                                       critDamage + SkillCritDamage(p.Skill));
                if (tiers[hand] > 1)
                {
                    run.Crits++;
                    while (run.ByTier.Count <= tiers[hand]) run.ByTier.Add(0);
                    run.ByTier[tiers[hand]]++;
                    why += ", " + Lang.T("log.fire.crit", Lang.T(hand == 0 ? "log.hand.main" : "log.hand.off"),
                                         critMul.ToString("0.##", CultureInfo.InvariantCulture));
                }
                foreach (KeyValuePair<string, double> kv in one.HitHandMin[hand])
                {
                    if (kv.Value <= 0.0001) continue;
                    if (kv.Key.EndsWith("Dot", StringComparison.Ordinal))
                    {
                        Apply(queue, dots, run, opt, e, ref order, hand, kv.Key,
                              kv.Value * critMul * below, one, totals, origin, "\"" + p.Name + "\"");
                        continue;
                    }
                    if (!run.Types.Contains(kv.Key)) run.Types.Add(kv.Key);
                    double raw = kv.Value * critMul * below;
                    before += raw;
                    handsBefore[hand] += raw;
                    double through = target.Through(kv.Key, raw);
                    damage += through;
                    hands[hand] += through;
                }
            }
            run.Total += damage;
            run.TotalBefore += before;
            if (lineKind == LogKind.Proc) run.ProcTotal += damage;
            While(perSecond, e.At, damage);
            // A pressed skill and a pool skill are direct damage too; the expectation has
            // them. A proc is not: it is checked by rate and does not enter the spread series.
            if (lineKind != LogKind.Proc) While(run.PerSecondDirect, e.At, damage);
            // ONE HAND MISSING IS NOT A MISS, BUT IT MUST BE SAID. The user's request: a full
            // miss only when both miss, and a partial one must be visible in words, not only
            // from the roll figures.
            Add(run, opt, e.At, lineKind, kindWord + ": " + title + Partial(swung, landed),
                why, damage, before, hands, landed, handsBefore);
            // PERCENT-LIFE DAMAGE FROM THIS STRIKE — the strongest of the skill's own and the
            // gear's (they do not stack; MECHANICS 77).
            {
                double plSkill = p.Skill != null ? PercentLifeOf(game, p.Skill.Dbr, p.Skill.Level) : 0;
                double plGear = GearPercentLife(totals);
                double pl = Math.Max(plSkill, plGear);
                if (pl > 0)
                    PercentLifeHit(run, opt, target, e, pl, plSkill >= plGear ? p.Name : Lang.T("log.life.gear"), perSecond);
            }

            // DEVOTION STARS TIED TO THIS SKILL are rolled here and only here, and AFTER the
            // strike's own line: a star fires FROM it, and the log must read in the same order
            // as the game.
            Stars(run, opt, book, e, p.Skill == null ? null : p.Skill.Dbr, crit, false,
                  game, c, totals, seeded, target, queue, dots, ref order, roll, hitRng,
                  pth, thresholds, multipliers, critDamage, below, perSecond);
            return true;
        }

        // APPLYING A DOT. The same source means the same dot: the duration is extended and the
        // damage rises to the BEST of what was applied (E.2, and 5.4a on crits). A new stream
        // of ticks is started only for a source not seen before.
        // `origin` says WHOSE dot it is in the sense of source: `Timeline.WeaponSource` for
        // everything that strikes with the weapon (the default attack and any skill with a
        // weapon share), or the skill's record for those with no weapon share. See the `Dot`
        // class.
        // `--dotshorten` makes a dot's duration "from this strike", as before 15 September: a
        // short strike clips a long one's remainder. Only so the guard can go red
        // (MECHANICS 89z).
        public static bool DotShortenOld;

        static void Apply(List<Event> queue, Dictionary<string, Dot> dots, TimelineRun run,
                          TimelineOptions opt, Event e, ref int order, int hand, string type,
                          double perSecond, DamageModel one, CharacterTotals totals, string origin,
                          string byWhat)
        {
            double secs;
            // THE DURATION COMES FROM THE SAME STRIKE AS THE NUMBER. We read a dot's damage
            // from `HitHandMin`, so the duration must come from `HitHandSecs`, or a skill with
            // its own dot gets the WEAPON's duration. `HandTypeSecs` stays as a fallback for
            // when the strike is not from a skill at all.
            one.HitHandSecs[hand].TryGetValue(type, out secs);
            if (secs <= 0) one.HandTypeSecs[hand].TryGetValue(type, out secs);
            if (secs <= 0) secs = 1;
            // "+% duration" has its own field per dot type. The panel deliberately does not
            // apply it (a dot there is per second), while here it decides how many ticks there
            // will be: on `_777`, +113% turns 3 seconds into 6.39.
            string root = type.EndsWith("Dot", StringComparison.Ordinal)
                        ? type.Substring(0, type.Length - 3) : type;
            TotalsEntry e2;
            double durPercent = totals.Fields.TryGetValue(
                "offensiveSlow" + root + "DurationModifier", out e2) ? e2.Value : 0;
            // This percent has no cap in the game, nor here: +500% happens.
            secs *= 1 + durPercent / 100.0;

            // ONE DOT PER SOURCE AND TYPE, WITH THE DURATION OF ITS LONGEST PART
            // (MECHANICS 89z). On 15 September the strike was split here into parts with
            // different durations ticking separately (89d) — the user refuted that: "all the
            // weapon ones simply overwrite each other, they have no separate ticks", the best
            // one ticks. A short duration shows only when the long one has already expired and
            // the short one is applied later — `PutDot` does that, never letting a refresh
            // shorten a dot.
            PutDot(queue, dots, run, opt, e, ref order, hand, type, perSecond, secs, origin, byWhat);
        }

        // SUMMON A PET IF THERE IS ROOM. The user's rule of 12 September:
        //
        //   "until the max number of summoned ones of this kind is reached we keep summoning
        //    them, or rather adding them to the active pool. Once the max is reached, the
        //    skills from the checkboxes need not be re-summoned; let the triggered ones keep
        //    showing in the log, but do not increase the count."
        //
        // So the log line is ALWAYS there (for the pressed and for the self-fired alike),
        // while the pet is added only while there is room. The cap being hit is reported ONCE:
        // otherwise the log would be buried under the same line every few seconds.
        static void Call(TimelineRun run, TimelineOptions opt, List<Event> queue,
                         Dictionary<string, Dot> dots, ref int order, Event e,
                         TimelinePets.Kind pk, List<PetLive> alive, string how)
        {
            int now = 0;
            foreach (PetLive p in alive) if (!p.Gone && p.Kind == pk) now++;
            if (now >= pk.Limit)
            {
                pk.Blocked++;
                if (pk.Blocked == 1)
                    Add(run, opt, e.At, LogKind.Plain, Lang.T("log.pet.summonBlocked", how, pk.Title),
                        Lang.T("log.pet.limit", pk.Limit, pk.Name),
                        0, 0, null, null);
                return;
            }
            PetLive live = new PetLive();
            live.Kind = pk;
            live.Number = now + 1;
            live.Until = pk.Seconds > 0 ? e.At + pk.Seconds : 0;
            alive.Add(live);
            pk.Summoned++;
            run.PetSummons++;
            // The firing count for `--firecheck`: a summon has "fired" too.
            run.Count(pk.Summon, pk.Title);
            Add(run, opt, e.At, LogKind.Plain, Lang.T("log.pet.summoned", how, pk.Name, live.Number),
                Lang.T("log.pet.standing", now + 1, pk.Limit) +
                ", " + (pk.Seconds > 0 ? Lang.T("log.pet.lives", Fmt(pk.Seconds)) : Lang.T("log.pet.permanent")) +
                ", " + Lang.T("log.pet.aps", Fmt(pk.Aps)), 0, 0, null, null);
            // Its strikes: the autoattack by speed, an aura once a second, skills at their own
            // rate from the pet's record.
            bool hasBasic = pk.HasBasic;
            foreach (TimelinePets.Move m in pk.Moves)
            {
                // A DEFAULT ATTACK IN THE RECORD (`attackSkillName`, MECHANICS 90c): if it is
                // there, a damaging passive does not strike by itself — it is the "weapon",
                // and the default attack's `weaponDamagePct` share is taken from it (the
                // Familiar's Storm Orb — 18%).
                if (m.Auto && hasBasic) continue;
                // A TRAP FIRES ONCE, AND NOT AT THE SUMMON — it waits out the record's own
                // "preparation" (`deathFromEnemyDelay`, see `TimelinePets.Kind.TrapDelay`).
                // It is not "ready at the summon" like an ordinary pet skill: the delay IS
                // the rule here, and skipping it would move the whole blast a second early.
                if (m.Once)
                {
                    queue.Add(new Event(e.At + m.FirstAt, e.Actor, ++order, live, m));
                    continue;
                }
                double first = m.Auto || m.Basic ? (pk.Aps > 0 ? 1 / pk.Aps : 0)
                             : m.Aura ? 1
                             : m.Cooldown;
                // SKILLS ARE READY RIGHT AT THE SUMMON — one of the readings (MECHANICS 88):
                // the user's sequence starts with a skill, not with an autoattack. Scheduled
                // for the same instant, ahead of the claws.
                if (TimelinePets.ReadyAtSummon && !m.Auto && !m.Basic && !m.Aura)
                {
                    queue.Add(new Event(e.At, e.Actor, ++order, live, m));
                    continue;
                }
                if (first > 0.01) queue.Add(new Event(e.At + first, e.Actor, ++order, live, m));
            }
            if (live.Until > 0) queue.Add(new Event(live.Until, e.Actor, ++order, live));
        }

        // IS THIS A BUTTON — something a person presses themselves in game (MECHANICS 87c).
        //
        // A devotion star, and a skill that fires in game by a condition ("when hit", "at low
        // health"), are not pressed: we queue them "on cooldown" only INSTEAD of the condition
        // we do not roll (MECHANICS 76). Like procs, they play no animation on the character —
        // so they neither take time nor wait for someone else's animation to end.
        //
        // The battery of 13 September, `_222` with two stars (Targo's Hammer, Trample): while
        // the stars TOOK time, only one swing was left in 60 seconds; when they stopped taking
        // time but still WAITED, a star with a short cooldown fired only once per swing, and
        // the procs came to 349 against the expected 1116. One rule covers both cases.
        public static bool IsButton(AttackSkill a, string dbr)
        {
            if (!string.IsNullOrEmpty(dbr) && SkillsForm.IsDevotion(dbr)) return false;
            if (a != null && !string.IsNullOrEmpty(a.When)) return false;
            return true;
        }

        // HOW MANY SECONDS A PRESS TAKES — its animation's length in combat (MECHANICS 87).
        //
        // One function for the engine and the check, or the expectation would drift from the
        // run:
        //   instant (`instantCast 1` somewhere in the chain) — zero;
        //   strikes with the weapon — `AttackInterval`, by attack speed, like a swing;
        //   otherwise — `CastInterval`, by cast speed (measured, MECHANICS 74);
        //   no animation found — zero: it takes nothing, as before 13 September.
        public static double PressOccupy(GameData game, Character c, DamageModel panel,
                                         AttackSkill a, string dbr)
        {
            if (game == null || panel == null || string.IsNullOrEmpty(dbr)) return 0;
            DbRecord rec = game.Record(dbr);
            if (rec == null) return 0;
            if (!IsButton(a, dbr)) return 0;
            string at = dbr;
            for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(at); hop++)
            {
                DbRecord r = game.Record(at);
                if (r == null) break;
                if (r.GetInt("instantCast") == 1) return 0;
                at = ItemViewFactory.NextSkillRecord(r);
            }
            double anim = SkillHands.CastSeconds(game, rec, c);
            if (anim <= 0) return 0;
            return a != null && Math.Abs(a.WeaponPct) > 0.0001
                 ? panel.AttackInterval(anim)
                 : panel.CastInterval(anim);
        }

        // THE SKILL'S OWN "+% CRIT DAMAGE" — from its own records and modifiers
        // (`AttackSkill.Parts`, which holds both tree nodes and modifiers from gear). The
        // character has no such addition: a modifier belongs to the strike, not to him
        // (MECHANICS 79).
        public static double SkillCritDamage(AttackSkill a)
        { return a == null ? 0 : a.Sum("offensiveCritDamageModifier"); }

        // A PET'S AURAS THAT STRIKE THE ENEMY — onto the target while it stands by.
        //
        // The Great White Wolf's Mogdrogen's Presence takes 50 defensive ability and 8% of ALL
        // resistances off the target; Eldritch Instability takes speed and poison resistance.
        // For OUR damage this matters more than the pet's own strikes: reduced resistances
        // multiply everything we hit with. They hold while the pet lives, so the duration
        // comes from its lifetime (a permanent one until the run's end), and this is called on
        // every summon: a slot may have freed up.
        // The key prefix for pets' auras in the buff book: the set of numbers tells them from
        // our own skills by it. A "|" cannot get inside — `BuffBook.Key` separates raised
        // entries with it.
        const string PetAuraTag = "petaura:";

        // A PET'S ALLY-BUFFING AURAS GO UP WHILE IT LIVES.
        //
        // Done through the buff book rather than a separate bucket: their numbers change OUR
        // set (flat damage, type percents, "+% total damage"), and the set can rebuild itself
        // anyway. The duration is the pet's lifetime, for a permanent one until the run's end:
        // its debuff aura is counted exactly the same way (78z).
        static void PetAuras(TimelineRun run, TimelineOptions opt, BuffBook buffs,
                             Event e, TimelinePets.Kind pk, List<PetLive> alive)
        {
            if (buffs == null || pk == null || pk.Auras.Count == 0) return;
            bool any = false;
            foreach (PetLive p in alive) if (!p.Gone && p.Kind == pk) { any = true; break; }
            if (!any) return;
            double secs = pk.Seconds > 0 ? pk.Seconds : (opt.Seconds > 0 ? opt.Seconds : 60);
            for (int i = 0; i < pk.Auras.Count && i < pk.AuraLevels.Count; i++)
            {
                string name = i < pk.AuraNames.Count ? pk.AuraNames[i] : pk.Auras[i];
                buffs.Raise(PetAuraTag + pk.Auras[i] + "@" + pk.AuraLevels[i],
                            "pet aura \"" + pk.Name + "\": " + name, e.At, secs);
                Add(run, opt, e.At, LogKind.Plain, Lang.T("log.pet.aura", pk.Name, name),
                    Lang.T("log.pet.auraAllies") +
                    (pk.Seconds > 0 ? " (" + Lang.T("log.seconds", Fmt(secs)) + ")" : ""), 0, 0, null, null);
            }
        }

        static void PetDebuffs(TimelineRun run, TimelineOptions opt, GameData game, TimelineTarget target,
                               DebuffBook debuffs, Event e, TimelinePets.Kind pk, List<PetLive> alive)
        {
            if (pk.Debuffs.Count == 0) return;
            bool any = false;
            foreach (PetLive p in alive) if (!p.Gone && p.Kind == pk) { any = true; break; }
            if (!any) return;
            double secs = pk.Seconds > 0 ? pk.Seconds : (opt.Seconds > 0 ? opt.Seconds : 60);
            for (int i = 0; i < pk.Debuffs.Count && i < pk.DebuffLevels.Count; i++)
            {
                string dbr = pk.Debuffs[i];
                int level = pk.DebuffLevels[i];
                string name = "aura \"" + pk.Name + "\"";
                List<ResistCut> cuts = SkillResistCuts(game, dbr, level, name);
                if (cuts.Count > 0) debuffs.Put(target, dbr, name, cuts, e.At, secs);
                AbilityCut ability = SkillAbilityCut(game, dbr, level);
                // THE DURATION COMES FROM THE PET'S LIFE, NOT FROM THE RECORD. An aura has no
                // `skillActiveDuration`, and without this the defensive-ability cut would live
                // the default three seconds, while the aura stands as long as the pet is there.
                if (ability.Any) { ability.Secs = secs; debuffs.PutAbility(dbr, name, ability, e.At); }
                if (cuts.Count > 0 || ability.Any)
                    Add(run, opt, e.At, LogKind.Plain, Lang.T("log.pet.auraTarget", pk.Name),
                        Lang.T("log.pet.auraOnTarget") + (pk.Seconds > 0 ? " (" + Lang.T("log.seconds", Fmt(secs)) + ")" : ""),
                        0, 0, null, null);
            }
        }

        // A PET'S STRIKE. It differs from ours in two ways, both recorded as approximations
        // (PLAN, P7):
        //   * a pet has no HANDS — there is one number;
        //   * DOTS get THEIR OWN source per pet and per skill, so they merge neither with ours
        //     nor between two identical pets.
        // HIT AND CRIT have been rolled since 12 September — the offensive ability comes from
        // its "bio", as for a monster (78a…78e), and "+% crit damage" is summed from the bonus
        // panel and the pet's OWN additions (78i).
        // The numbers come from `PetPool.Numbers` through `TimelinePets`: the same ones the
        // pets window prints, and that window is checked against the game.
        static bool PetStrike(TimelineRun run, TimelineOptions opt, TimelineTarget target,
                              List<Event> queue, Dictionary<string, Dot> dots, ref int order,
                              Event e, PetLive live, TimelinePets.Move m, Random rng,
                              List<double> perSecond, double[] thresholds, double[] multipliers,
                              CharacterTotals totals, GameData game, DebuffBook debuffs,
                              TimelinePets.Move autoNumbers)
        {
            TimelinePets.Kind pk = live.Kind;
            // The innate attack comes from the same rebuild under the live aura givers as `m`
            // (MECHANICS 89a); the counters stay on the kind `pk` itself.
            TimelinePets.Move auto = autoNumbers ?? pk.Auto;
            double damage = 0, before = 0;
            // AURAS THAT COVER ALLIES COVER THE PET TOO (Blood of Dreeg): offensive ability
            // and flat damage. Taken from the CURRENT set of numbers, because a buff comes and
            // goes.
            double auraOa = TimelinePets.AuraAbility(totals);
            // A PET'S HIT AND CRIT USE THE SAME TIERS AS THE CHARACTER'S.
            //
            // Its offensive ability comes from its "bio" (78a…78e): the equation by level plus
            // the pet bonuses. After that everything is as for us: one roll decides both the
            // hit and the tier, and "+% crit damage to pets" is added to the tier's
            // multiplier. An aura makes no rolls — neither does ours (MECHANICS 41).
            double critMul = 1;
            int petTier = 0;
            if (!m.Aura && pk.Oa > 0)
            {
                double pth = Combat.Pth(game,pk.Oa + auraOa, target.DefensiveAbility);
                double petFloor = game != null ? Combat.PthMinimum(game) : 55;
                if (pth < petFloor) pth = petFloor;
                int tier = petTier = Combat.Tier(pth, rng.NextDouble() * 100, thresholds);
                if (tier == 0)
                {
                    run.PetMisses++;
                    pk.Misses++;
                    Add(run, opt, e.At, LogKind.Miss, Lang.T("log.pet.miss", pk.Name, live.Number, m.Name),
                        Lang.T("log.pet.hitChance", Fmt(Math.Min(100, pth))), 0, 0, null, null);
                    return false;
                }
                critMul = Combat.CritMultiplier(tier, multipliers, pk.CritDamage);
                if (tier > 1) { run.PetCrits++; pk.Crits++; }
            }
            // A PET'S "WEAPON" SHARE is its innate attack: in a pet's skill `weaponDamagePct`
            // means exactly that (Blight Burst — 76%).
            double share = m.WeaponPct > 0.0001 && auto != null && !m.Auto ? m.WeaponPct / 100.0 : 0;
            foreach (KeyValuePair<string, double> kv in m.Min)
            {
                double hi;
                m.Max.TryGetValue(kv.Key, out hi);
                if (hi < kv.Value) hi = kv.Value;
                double raw = kv.Value + (hi - kv.Value) * rng.NextDouble();
                if (share > 0 && auto != null)
                {
                    double alo, ahi;
                    if (auto.Min.TryGetValue(kv.Key, out alo))
                    {
                        auto.Max.TryGetValue(kv.Key, out ahi);
                        if (ahi < alo) ahi = alo;
                        raw += (alo + (ahi - alo) * rng.NextDouble()) * share;
                    }
                }
                raw += AuraFlatFor(totals, kv.Key, m, rng);
                raw *= critMul;
                if (!run.Types.Contains(kv.Key)) run.Types.Add(kv.Key);
                before += raw;
                // The racial bonus is OURS; the pet does not get it (MECHANICS 78e).
                damage += target.Through(kv.Key, raw, false);
            }
            // AND THE TYPES THE PET ITSELF LACKS WHILE AN AURA GIVES THEM: Blood of Dreeg
            // gives acid to someone who does not strike with acid at all.
            foreach (string type in TimelinePets.DamageTypes)
            {
                if (m.Min.ContainsKey(type)) continue;
                double add = AuraFlatFor(totals, type, m, rng);
                if (add <= 0.0001) continue;
                if (!run.Types.Contains(type)) run.Types.Add(type);
                add *= critMul;
                before += add;
                damage += target.Through(type, add, false);
            }
            string origin = PetTag + pk.Summon + "#" + live.Number;
            string byWhat = "\"" + Lang.T("log.pet.source", pk.Name, live.Number, m.Name) + "\"";
            foreach (KeyValuePair<string, double> kv in m.DotMin)
            {
                double hi, secs;
                m.DotMax.TryGetValue(kv.Key, out hi);
                m.DotSecs.TryGetValue(kv.Key, out secs);
                if (hi < kv.Value) hi = kv.Value;
                if (secs <= 0) continue;
                // A pet's dot crits too — just like ours (5.4a).
                double each = (kv.Value + (hi - kv.Value) * rng.NextDouble()) * critMul;
                PutDot(queue, dots, run, opt, e, ref order, 0, kv.Key + "Dot", each, secs,
                       origin + "|" + m.Dbr, byWhat);
            }
            live.Hits++;
            pk.Hits++;
            pk.Damage += damage;
            run.PetHits++;
            run.Total += damage;
            run.TotalBefore += before;
            run.PetTotal += damage;
            While(perSecond, e.At, damage);
            // THE DEBUFF PART OF THE PET'S OWN STRIKE — what the icons on the enemy show.
            //
            // Noticed by the user on 12 September: "from the debuff icons on the enemy you can
            // see that the beast's damage applies three kinds of elemental damage and does
            // something to armour or resistance". The first are Rotting Fumes' dots (poison,
            // converted to fire/cold/lightning on `_Izaar`), the second is that same aura's
            // `offensiveSlowDefensiveAbilityMin`, up to −335 to the target's defensive ability.
            //
            // Rotting Fumes does not get into the debuff-aura list (`PetDebuffs`): the marker
            // there is a `debufSkill` flag in the chain, and this aura has none — it is
            // `Skill_BuffAttackRadiusToggled`, i.e. a STRIKE around itself, and the cut sits
            // in a strike field. So it is applied here, exactly like the debuff part of our
            // own strike, and for its own duration from the record.
            if (game != null && debuffs != null && !string.IsNullOrEmpty(m.Dbr))
            {
                AbilityCut petCut = SkillAbilityCut(game, m.Dbr, m.Level);
                if (petCut.Any)
                    debuffs.PutAbility(PetTag + m.Dbr, pk.Name + ": " + m.Name, petCut, e.At);
            }
            // A PET'S CRIT TIER IS PRINTED THE SAME WAY AS OURS. Without it the user's
            // measurement ("the Blight Beast shows 1.86 / 1.96 / 2.06") had nothing in the run
            // to be compared with: the log held only the damage figure.
            string petWhy = m.Once ? Lang.T("log.pet.onceAt", Fmt(m.FirstAt))
                          : m.Aura ? Lang.T("log.pet.perSecond")
                          : m.Auto || m.Basic ? Lang.T("log.pet.speed", Fmt(pk.Aps))
                                   : Lang.T("log.pet.every", Fmt(m.Cooldown));
            if (petTier > 1)
                petWhy += ", " + Lang.T("log.pet.crit", petTier - 1,
                                        critMul.ToString("0.##", CultureInfo.InvariantCulture));
            if (damage > 0.0001 || before > 0.0001)
                Add(run, opt, e.At, LogKind.Plain,
                    Lang.T("log.pet.hit", pk.Name, live.Number, m.Name) +
                    (m.Auto || m.Basic ? " " + Lang.T("log.pet.auto") : m.Aura ? " " + Lang.T("log.pet.auraMark") : ""),
                    petWhy,
                    damage, before, new[] { damage, 0.0 }, new[] { true, false }, new[] { before, 0.0 });
            else
                Add(run, opt, e.At, LogKind.Plain,
                    Lang.T("log.pet.hit", pk.Name, live.Number, m.Name) +
                    (m.Aura ? " " + Lang.T("log.pet.auraMark") : ""), Lang.T("log.pet.dotApplied"), 0, 0, null, null);
            return critMul > 1.0001;
        }

        // THE FLAT DAMAGE OF AN ALLIED AURA FALLING ON THIS PET STRIKE.
        //
        // It arrives with the "weapon", as for the character: the innate attack gets it whole,
        // while a skill gets it by its share of the attack (`weaponDamagePct`, which for a pet
        // means the pet's base damage, MECHANICS 27g). APPROXIMATED: the pet's own percents are
        // not applied to it — we have not measured in what order the game sums this.
        static double AuraFlatFor(CharacterTotals totals, string type, TimelinePets.Move m, Random rng)
        {
            double lo = TimelinePets.AuraFlat(totals, type, false);
            if (lo <= 0.0001) return 0;
            double hi = TimelinePets.AuraFlat(totals, type, true);
            if (hi < lo) hi = lo;
            // A default attack from the record with no share of its own (Shadow Blades,
            // `Skill_AttackWeapon`) IS the whole weapon strike — an aura's flat damage falls on
            // it as on an autoattack.
            double share = m.Auto ? 1
                         : m.WeaponPct > 0.0001 ? m.WeaponPct / 100.0
                         : m.Basic ? 1 : 0;
            if (share <= 0) return 0;
            return (lo + (hi - lo) * rng.NextDouble()) * share;
        }

        // APPLY A DOT WITH A READY DURATION — the shared part for our damage and for pets'
        // (E7): theirs takes the duration from their skill's numbers, and our duration
        // percents do not apply to them.
        static void PutDot(List<Event> queue, Dictionary<string, Dot> dots, TimelineRun run,
                           TimelineOptions opt, Event e, ref int order, int hand, string type,
                           double perSecond, double secs, string origin, string byWhat)
        {
            string key = origin + "|" + type + (opt.DotSourceByHand ? HandTag + hand : "");
            Dot had;
            if (dots.TryGetValue(key, out had))
            {
                // THE BEST IS TAKEN ONLY AMONG WHAT WAS APPLIED WITHIN THIS TIME, not ever.
                //
                // The user's words: "at the best application WITHIN THAT TIME". So once the
                // dot has burned out, the old crit no longer counts: a new application starts
                // from itself. On the first attempt I kept the maximum forever — and a crit
                // dot from a minute ago would carry the whole run.
                bool expired = e.At > had.Until + 1e-9;
                // THE DURATION IS NEVER SHORTENED (MECHANICS 89z, the user's rule of
                // 15 September): a dot holds until the latest end among those applied. It
                // used to read `Until = at + secs` here, and a short strike after a long one
                // clipped the long one's remainder. `--dotshorten` brings that back so the
                // guard can go red.
                double until = e.At + secs;
                if (!expired && until < had.Until - 1e-9)
                {
                    run.DotKeptLonger++;
                    if (!DotShortenOld) until = had.Until;
                    else run.DotShortened++;
                }
                had.Until = until;
                if (expired || perSecond > had.PerSecond)
                { had.PerSecond = perSecond; had.From = byWhat; }
                run.DotRefresh++;
                // But if the chain has ended, start it again.
                if (!had.Ticking)
                {
                    // The tick fraction starts counting AFRESH: the old tail is already paid.
                    had.LastAt = e.At;
                    had.NextTick = Math.Min(e.At + Combat.DotTickSeconds, had.Until);
                    had.Ticking = true;
                    queue.Add(new Event(had.NextTick, e.Actor, ++order, had));
                }
                return;
            }
            Dot dot = new Dot();
            dot.Key = key; dot.Type = type; dot.Hand = hand; dot.From = byWhat;
            dot.Pet = origin.StartsWith(PetTag, StringComparison.Ordinal);
            dot.ByWeapon = origin == WeaponSource;
            dot.PerSecond = perSecond;
            dot.Until = e.At + secs;
            dot.LastAt = e.At;
            // The first tick comes after the period (`Combat.DotTickSeconds`) OR at the dot's
            // end if that is shorter than the period. A 0.4-second dot used to not tick AT
            // ALL; now it gives 0.4 of a tick.
            dot.NextTick = Math.Min(e.At + Combat.DotTickSeconds, dot.Until);
            dots[key] = dot;
            run.DotApplied++;
            // EVERY DOT SOURCE GETS ITS OWN LINE IN THE REPORT.
            //
            // The user's rule of 7 September: "auras without weapon damage give dots DIFFERENT
            // from the weapon ones — they do not extend each other, they add up". For us that
            // follows by itself: a dot's key is source plus type, and an aura without a weapon
            // share has its own source. But "follows by itself" is a poor guarantee: break
            // `origin` and two dots merge into one silently. Better to have it visible as a
            // list.
            // THE FIRST WORD OF THE MARK IS A CONSTANT: `TimelineTest` searches a line for it to
            // make sure an aura's dot never merges with the weapon's. Until 12 October it
            // searched for a Russian word, and translating this line would have switched that
            // guard off in silence.
            run.DotSources.Add(DotName(type) + " — " + byWhat +
                               (dot.ByWeapon ? " (" + ByWeaponMark + ", shared by both hands and every skill with a weapon share)"
                                             : " (its own source, NOT merged with the weapon's)"));
            if (dot.NextTick > e.At + 1e-9)
            {
                dot.Ticking = true;
                queue.Add(new Event(dot.NextTick, e.Actor, ++order, dot));
            }
        }

        // "(off hand missed)" — when both hands swung and only one landed. That does not count
        // as a full miss (the user's rule), but it must be visible in the log.
        static string Partial(bool[] swung, bool[] landed)
        {
            if (!swung[0] || !swung[1]) return "";          // one hand swung — nothing to compare
            if (landed[0] && landed[1]) return "";
            if (!landed[0] && !landed[1]) return "";        // that is a full miss already
            return "  " + Lang.T(landed[0] ? "log.partial.offMissed" : "log.partial.mainMissed");
        }

        // The name of the type's ROOT — for the resistance line. `DotName` will not do here: it
        // names the dot, while a resistance is reduced on the whole type.
        // Made public on 8 October: the report names the TARGET's resistances with it too.
        public static string TypeName(string type)
        {
            switch (type)
            {
                case "Physical": return "physical";
                case "Pierce": return "pierce";
                case "Fire": return "fire";
                case "Cold": return "cold";
                case "Lightning": return "lightning";
                case "Poison": return "poison";
                case "Life": return "life";
                case "Aether": return "aether";
                case "Chaos": return "chaos";
                // Bleeding is a type like any other and the target has its own resistance
                // to it (9% on the dummy at Ascendant). It was missing from this list
                // because only the REDUCTION report used to call it, and a reduction is
                // usually "to everything"; the target-resistance line asks type by type.
                case "Bleeding": return "bleeding";
                default: return type;
            }
        }

        // The dot's name in a tick line. The words come from the language file; the game does
        // have type names, but in another grammatical case, so whether to take them is stage 5.
        static string DotName(string type)
        {
            switch (type)
            {
                case "FireDot": return Lang.T("log.dot.FireDot");
                case "ColdDot": return Lang.T("log.dot.ColdDot");
                case "LightningDot": return Lang.T("log.dot.LightningDot");
                case "PoisonDot": return Lang.T("log.dot.PoisonDot");
                case "LifeDot": return Lang.T("log.dot.LifeDot");
                case "BleedingDot": return Lang.T("log.dot.BleedingDot");
                case "PhysicalDot": return Lang.T("log.dot.PhysicalDot");
                default: return type;
            }
        }

        static void While(List<double> perSecond, double at, double damage)
        {
            int sec = (int)at;
            while (perSecond.Count <= sec) perSecond.Add(0);
            perSecond[sec] += damage;
        }

        static void Add(TimelineRun run, TimelineOptions opt, double at, LogKind kind, string what, string why,
                        double damage, double before, double[] hands, bool[] landed,
                        double[] handsBefore = null)
        {
            if (!opt.LogEveryHit) return;
            TimelineLine line = new TimelineLine();
            line.At = at; line.Kind = kind; line.What = what; line.Why = why;
            line.Damage = damage; line.Before = before;
            if (hands != null)
                for (int h = 0; h < 2; h++) { line.Hand[h] = hands[h]; line.Hit[h] = landed[h]; }
            if (handsBefore != null)
                for (int h = 0; h < 2; h++) line.HandBefore[h] = handsBefore[h];
            run.Log.Add(line);
            if (opt.OnLine != null) opt.OnLine(line);
        }

        static string Name(Swing kind)
        { return Lang.T(kind == Swing.Both ? "log.hand.both" : (kind == Swing.Main ? "log.hand.main" : "log.hand.off")); }

        static string Fmt0(double v)
        { return v.ToString("F0", CultureInfo.InvariantCulture); }

        static string Fmt(double v)
        { return v.ToString("F1", CultureInfo.InvariantCulture); }
    }
}
