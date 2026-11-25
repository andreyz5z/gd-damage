// Pet definitions used by the damage timeline.
//
// Keep a shared pool of summoned pets, their limits, lifetimes, skills, and attack
// timing. Timeline events are implemented in `Timeline.cs`.
//
// This file describes which pets can be summoned, their attacks, timing, and values.
//
// `PetPool.Of` supplies species limits, lifetime, skills, and bonuses. `PetPool.Numbers`
// supplies skill values using the same modifiers and conversion as the pet window.
// `--pettest`, `--petdmgtest`).
//
// This file adds pet attack speed and damage-type decomposition.

using System;
using System.Collections.Generic;
using System.IO;

namespace Gdc.Gui
{
    public static partial class TimelinePets
    {
        // Use the same damage-type keys as `DamageModel.Kinds` so target resistance
        // and damage-over-time aggregation remain compatible.
        static readonly string[] Types =
        { "Physical", "Pierce", "Fire", "Cold", "Lightning", "Poison", "Life", "Aether", "Chaos", "Bleeding" };

        // Pet AI interpretation switches, exposed for controlled validation.
        //
        // Each setting represents an ambiguity in the game data; validation can try
        // the alternatives instead of embedding an assumption:
        //   CdPick         interval source: max, delay, or timeout;
        //   CdFromEnd      cooldown starts at cast start or animation end;
        //   CastForSkills  pet skills use attack or cast speed;
        //   ReadyAtSummon  skills are ready immediately or after the first cooldown.
        // Set through `--petcd=`, `--petcdfrom=`, `--petspeed=`, and `--petready=`.
        //
        // Defaults use animation-end cooldown and `Delay`; skills are ready at summon.
        //
        // Pet basic attacks use attack speed; pet skills use cast speed. Character
        // cast speed is not inherited.
        public static string CdPick = "delay";
        public static bool CdFromEnd = true, CastForSkills = true, ReadyAtSummon = true;

        // One pet hit: an innate attack or a distinct skill.
        public sealed class Move
        {
            public string Dbr = "", Name = "";
            public int Level;
            public double Cooldown;        // 0 uses attack speed for the intrinsic attack.
            public bool Auto;              // Intrinsic attack used as the normal attack.
            public bool Aura;              // Area effect with a one-second interval.
            // A trap death skill fires once when the trap is triggered; see
            // `Kind.TrapDelay`.
            public bool Once;
            // An attack named by the pet record (`attackSkillName`) uses attack speed
            // and its own values; a passive `Auto` record may supply weapon damage.
            public bool Basic;
            public double Chance = 100;    // Activation chance from the record.
            public double FirstAt;         // Delay before the first activation.
            public double WeaponPct;       // Intrinsic-attack weapon contribution.
            // Skill animation duration at speed 1. During it, the pet cannot attack
            // with another action. Basic attacks use `1 / Aps`; auras use zero.
            public double AnimSecs;
            public readonly Dictionary<string, double> Min = new Dictionary<string, double>(StringComparer.Ordinal);
            public readonly Dictionary<string, double> Max = new Dictionary<string, double>(StringComparer.Ordinal);
            // Skill damage-over-time values: damage per second and duration.
            public readonly Dictionary<string, double> DotMin = new Dictionary<string, double>(StringComparer.Ordinal);
            public readonly Dictionary<string, double> DotMax = new Dictionary<string, double>(StringComparer.Ordinal);
            public readonly Dictionary<string, double> DotSecs = new Dictionary<string, double>(StringComparer.Ordinal);
            public bool Hits { get { return Min.Count > 0 || DotMin.Count > 0 || WeaponPct > 0.0001; } }
        }

        // Pet definition: one summon skill and all data needed by the timeline.
        public sealed class Kind
        {
            public PetPool.Pet Info;
            public string Summon = "";     // Summoning skill record.
            public string Title = "";      // Display title for the summon.
            public string Name = "";       // Display name for the summoned unit.
            public int Limit = 1;          // Simultaneous unit limit.
            public double Seconds;         // Lifetime; 0 means permanent.
            public double SummonCooldown;  // Summoning skill cooldown.
            public double Aps;             // Attacks per second.
            // Pet attack ability and critical-damage modifier come from the record
            // equations and pet bonuses; hit and critical rolls use player rules.
            public double Oa, CritDamage;
            // Playback speed is record attack speed × (1 + pet speed bonus). Divide
            // the animation duration at speed 1 by this multiplier.
            public double SpeedScale;
            // The same calculation uses cast speed when pet skills use it.
            // (`CastForSkills`, MECHANICS 88).
            public double CastScale;
            public bool ByHand;            // Explicitly summoned by the user.
            public readonly List<Move> Moves = new List<Move>();
            public bool HasBasic
            {
                get
                {
                    foreach (Move m in Moves) if (m.Basic) return true;
                    return false;
                }
            }
            // Death skills are not scheduled because pet death is not modeled.
            // Traps are the exception: their death skill is their attack.
            public readonly List<string> Dying = new List<string>();
            // A trap attacks only when triggered by an enemy.
            //
            // The object record provides both values:
            //     deathFromEnemyRange   trigger distance
            //     deathFromEnemyDelay   delay after the trigger
            // `dyingSkillName` identifies the attack.
            //
            // Only a small set of pet families uses these fields. Their paths are:
            //     devotion/pets/pet_arcanetrap          3 m / 1.0 s
            //     playerclass07/pets/concussiverune     2 m / 1.0 s
            //     playerclass07/pets/icerune            2 m / 1.0 s
            //     itemskills/pets/itempet_gazeofdreeg   2 m / 0.5 s
            //     itemskillsgdx1/pets/itempet_stormrune 2 m / 0.5 s
            // Only these records expose `deathFromEnemyRange`.
            //
            // `deathFromEnemyDelay` is the preparation time shown by the game.
            //
            // Enemy approach distance is not modeled; count `TrapDelay` from summon.
            public double TrapDelay, TrapRange;
            public bool IsTrap { get { return TrapRange > 0.0001; } }
            // Weapon-pool records whose values match the basic attack are folded into it.
            public readonly List<string> PoolFolded = new List<string>();
            // The innate attack is the pet's basic attack and receives `weaponDamagePct`.
            public Move Auto
            {
                get
                {
                    foreach (Move m in Moves) if (m.Auto) return m;
                    return null;
                }
            }
            // Pet auras that debuff enemies remain active while the pet is nearby and
            // therefore affect player damage. Store the source record and level.
            public readonly List<string> Debuffs = new List<string>();
            public readonly List<int> DebuffLevels = new List<int>();
            // Pet ally auras affect the player and other pets while the source lives.
            // Store their source records and levels like enemy debuffs.
            public readonly List<string> Auras = new List<string>();
            public readonly List<int> AuraLevels = new List<int>();
            public readonly List<string> AuraNames = new List<string>();
        // Per-run counters for diagnostics.
            public int Alive, Summoned, Blocked, Expired, Hits, Misses, Crits;
            public double Damage;
        }

        // All pet types available to the character. `picked` overrides the pet-window
        // selection when provided.
        public static List<Kind> Of(GameData game, Character c, CharacterTotals totals,
                                    ActiveSkills chosen, List<string> picked, List<string> notes)
        { return Of(game, c, totals, chosen, picked, notes, null); }

        // `extraPercents` contains allied aura percentages applied to pets. Build the
        // pool once to discover auras and again with those percentages.
        public static List<Kind> Of(GameData game, Character c, CharacterTotals totals,
                                    ActiveSkills chosen, List<string> picked, List<string> notes,
                                    Dictionary<string, double> extraPercents)
        {
            List<Kind> list = new List<Kind>();
            if (game == null || c == null) return list;
            SkillText.LevelBonus bonus = SkillText.LevelBonus.Of(game, c);
            AttackSkills.Attach attach = AttackSkills.Attach.Of(game, c);
            ItemSkillMods mods = ItemSkillMods.Of(game, c);
            foreach (PetPool.Pet p in PetPool.Of(game, c, chosen, extraPercents))
            {
                Kind k = new Kind();
                k.Info = p;
                k.Summon = p.Skill;
                k.Title = p.Title;
                k.Name = p.Name;
                k.Limit = p.Limit > 0 ? p.Limit : 1;
                k.Seconds = p.Seconds;
                // An explicit list, including an empty one, overrides the pet window.
                k.ByHand = picked != null ? Has(picked, p.Skill) : p.Chosen;
                k.SummonCooldown = SkillTiming.Cooldown(game, c, totals, attach, mods, bonus, p.Skill, p.Level);
                k.Aps = AttackSpeed(game, p.Dbr, totals);
                k.SpeedScale = SpeedScale(game, p.Dbr, totals);
                k.CastScale = CastScale(game, p.Dbr, totals);
                // Pet-specific item modifiers also affect critical damage and attack ability.
                //
                // Apply both shared `petBonusName` modifiers and pet-specific
                // `petSkillName` modifiers; the two sources are disjoint.
                double ownCrit = 0, ownOaFlat = 0, ownOaPct = 0;
                for (int i = 0; i < p.Passives.Count && i < p.PassiveLevels.Count; i++)
                {
                    DbRecord pass = game.Record(p.Passives[i]);
                    if (pass == null) continue;
                    int slot = SkillText.Slot(pass, p.PassiveLevels[i]);
                    ownCrit += AttackSkill.Value(pass, "offensiveCritDamageModifier", slot);
                    ownOaFlat += AttackSkill.Value(pass, "characterOffensiveAbility", slot);
                    ownOaPct += AttackSkill.Value(pass, "characterOffensiveAbilityModifier", slot);
                }
                k.Oa = Ability(game, p.Dbr, c, totals, ownOaFlat, ownOaPct);
                if (totals != null)
                {
                    TotalsEntry cd;
                    if (totals.PetFields.TryGetValue("offensiveCritDamageModifier", out cd)) k.CritDamage = cd.Value;
                }
                k.CritDamage += ownCrit;
                // Pet skill frequency is stored on the pet record, not the skill.
                // Read `specialAttackSkillName`, numbered variants, and
                // `buffSelfSkillName` from the pet definition.
                DbRecord petRec = game.Record(p.Dbr);
                Dictionary<string, double[]> cadence = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
                string selfBuff = petRec == null ? null : petRec.Get("buffSelfSkillName");
                // Basic and death skills are also named by the pet record.
                string basicDbr = petRec == null ? null : petRec.Get("attackSkillName");
                string dyingDbr = petRec == null ? null : petRec.Get("dyingSkillName");
                // Trap trigger and delay come from the object record (`Kind.TrapDelay`).
                k.TrapRange = petRec == null ? 0 : AttackSkill.Value(petRec, "deathFromEnemyRange", 0);
                k.TrapDelay = petRec == null ? 0 : AttackSkill.Value(petRec, "deathFromEnemyDelay", 0);
                // `initialSkillName` is applied on spawn and may be the object's full
                // aura damage rather than a direct hit.
                string initialDbr = petRec == null ? null : petRec.Get("initialSkillName");
                if (petRec != null)
                    for (int t = 1; t <= 9; t++)
                    {
                        string tag = t == 1 ? "specialAttack" : "specialAttack" + t;
                        string skill = petRec.Get(tag + "SkillName");
                        if (string.IsNullOrEmpty(skill)) continue;
                        double chance = AttackSkill.Value(petRec, tag + "Chance", 0);
                        double delay = AttackSkill.Value(petRec, tag + "Delay", 0);
                        double timeout = AttackSkill.Value(petRec, tag + "Timeout", 0);
                        // Use the configured cooldown source; the default is the
                        // larger of the two record values.
                        double gap = CdPick == "delay" ? delay
                                   : CdPick == "timeout" ? timeout
                                   : Math.Max(delay, timeout);
                        cadence[skill] = new[] { gap, chance <= 0 ? 100 : chance, delay };
                    }
                // Add named skills first, then unnamed pet records with damage values.
                List<string> dbrs = new List<string>(p.SkillDbrs);
                List<int> levels = new List<int>(p.SkillLevels);
                List<string> names = new List<string>(p.Skills);
                for (int i = 0; i < p.Passives.Count && i < p.PassiveLevels.Count; i++)
                {
                    if (dbrs.Contains(p.Passives[i])) continue;
                    if (!PetPool.HasDamage(game, p.Passives[i])) continue;
                    dbrs.Add(p.Passives[i]);
                    levels.Add(p.PassiveLevels[i]);
                    // Use the record-path tail for unnamed entries in diagnostics.
                    names.Add(Tail(p.Passives[i]));
                }
                // Enemy-debuff auras are separate from pet hits. Use `debufSkill`
                // and inspect all pet records, including unnamed ones.
                for (int i = 0; i < p.Passives.Count && i < p.PassiveLevels.Count; i++)
                {
                    string cut = DebuffOf(game, p.Passives[i]);
                    if (cut == null || k.Debuffs.Contains(cut)) continue;
                    k.Debuffs.Add(cut);
                    k.DebuffLevels.Add(p.PassiveLevels[i]);
                }
                // Record paths classified as hits; remaining entries may be allied auras.
                List<string> struck = new List<string>();
                List<Move> pool = new List<Move>();
                for (int i = 0; i < dbrs.Count; i++)
                {
                    // Classify allied auras before hits using `BuffRadius` and the
                    // absence of `debufSkill`; attack-radius records are excluded.
                    if (AllyAura(game, dbrs[i])) continue;
                    Move m = MoveOf(game, p, dbrs[i], levels[i], names[i]);
                    if (m == null || !m.Hits) continue;
                    struck.Add(m.Dbr);
                    // Death skills are not scheduled because pet death is not modeled.
                    if (!string.IsNullOrEmpty(dyingDbr) &&
                        string.Equals(dyingDbr, m.Dbr, StringComparison.OrdinalIgnoreCase))
                    {
                        k.Dying.Add(m.Name);
                        // A trap is the exception: its death skill is the scheduled
                        // attack and is defined by `Kind.TrapDelay`.
                        if (!k.IsTrap) continue;
                        m.Once = true;
                        m.FirstAt = k.TrapDelay;
                        m.Cooldown = 0;
                        k.Moves.Add(m);
                        continue;
                    }
                    double[] how;
                    if (cadence.TryGetValue(m.Dbr, out how))
                    { m.Cooldown = how[0]; m.Chance = how[1]; m.FirstAt = how[2]; }
                    // Self-applied skills are aura damage around the object. They come
                    // from `buffSelfSkillName` or `initialSkillName`, not direct hits.
                    else if ((!string.IsNullOrEmpty(selfBuff) &&
                              string.Equals(selfBuff, m.Dbr, StringComparison.OrdinalIgnoreCase)) ||
                             (!string.IsNullOrEmpty(initialDbr) &&
                              string.Equals(initialDbr, m.Dbr, StringComparison.OrdinalIgnoreCase)))
                        m.Aura = true;
                // Basic attacks named by the pet record use attack speed.
                    else if (!string.IsNullOrEmpty(basicDbr) &&
                             string.Equals(basicDbr, m.Dbr, StringComparison.OrdinalIgnoreCase))
                        m.Basic = true;
                    else
                    {
                        // A passive record without a frequency is part of the pet's
                        // weapon damage; combine multiple such records into one hit.
                        DbRecord rec = game.Record(m.Dbr);
                        string cls = rec == null ? "" : (rec.Class ?? "");
                        if (cls.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) < 0)
                            m.Auto = true;
                        else if (cls.StartsWith("Skill_WPAttack", StringComparison.OrdinalIgnoreCase))
                        {
                            // Fold matching weapon-pool records into the basic attack.
                            pool.Add(m);
                            continue;
                        }
                        else
                        {
                            // Report a damaging record with no frequency rather than
                            // inventing a schedule or dropping it silently.
                            if (notes != null && k.ByHand)
                                notes.Add(Lang.T("log.unread.petMoveNoRate", k.Name, m.Name));
                            continue;
                        }
                    }
                    k.Moves.Add(m);
                }
                // Combine all passive pet attacks into one weapon hit.
                Move first = null;
                List<Move> merged = new List<Move>();
                foreach (Move m in k.Moves)
                {
                    if (!m.Auto) { merged.Add(m); continue; }
                    if (first == null) { first = m; merged.Add(m); continue; }
                    Merge(first, m);
                }
                k.Moves.Clear();
                k.Moves.AddRange(merged);
                // Fold weapon-pool records into the pet's basic attack only when their
                // damage values match; otherwise keep them as an unread source.
                Move basicMove = null;
                foreach (Move mv in k.Moves) if (mv.Basic) { basicMove = mv; break; }
                foreach (Move pm in pool)
                {
                    if (basicMove != null && SameNumbers(basicMove, pm)) { k.PoolFolded.Add(pm.Name); continue; }
                    if (notes != null && k.ByHand)
                        notes.Add(Lang.T("log.unread.petMoveNoRate", k.Name, pm.Name));
                }
                // A pet skill occupies its animation duration; resolve the animation
                // file and speed from the pet's animation table.
                string anmTable = petRec == null ? null : petRec.Get("charAnimationTableName");
                foreach (Move mv in k.Moves)
                {
                    if (mv.Auto || mv.Basic || mv.Aura) continue;
                    DbRecord mr = game.Record(mv.Dbr);
                    mv.AnimSecs = SkillHands.CreatureSkillSeconds(game, anmTable,
                                      mr == null ? null : mr.Get("skillSpecialAnimationName"));
                }
                // Pet auras that affect allies.
                //
                // Use an ally-radius class without `debufSkill`, after excluding
                // records already classified as hits or enemy debuff auras.
                for (int i = 0; i < p.Passives.Count && i < p.PassiveLevels.Count; i++)
                {
                    string dbr = p.Passives[i];
                    if (struck.Contains(dbr) || k.Debuffs.Contains(dbr) || k.Auras.Contains(dbr)) continue;
                    if (DebuffOf(game, dbr) != null) continue;
                    // Require `BuffRadius`, not any class containing `Radius`; an
                    // attack-radius record is a hit, not an ally aura.
                    DbRecord top = game.Record(dbr);
                    if (top == null ||
                        (top.Class ?? "").IndexOf("BuffRadius", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!CharacterTotals.IsAllyAura(game, dbr)) continue;
                    k.Auras.Add(dbr);
                    k.AuraLevels.Add(p.PassiveLevels[i]);
                    string title = SkillText.Name(game, dbr);
                    k.AuraNames.Add(string.IsNullOrEmpty(title) ||
                                    title.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase)
                                    ? Tail(dbr) : title);
                }
                if (k.Aps <= 0 && notes != null && k.ByHand)
                    notes.Add(Lang.T("log.unread.petNoSpeed", k.Name));
                // Report a pet with no classified hit as an unread source, not as a
                // weak pet.
                if (k.Moves.Count == 0 && notes != null && k.ByHand)
                    notes.Add(Lang.T("log.unread.petNoMoves", k.Name));
                list.Add(k);
            }
            return list;
        }

        // Return the enemy-debuff aura record for a pet entry, if any.
        //
        // The aura record delegates values to `buffSkillName`, which is marked
        // `debufSkill 1` and therefore applies to the enemy.
        static string DebuffOf(GameData game, string dbr)
        {
            DbRecord rec = game == null ? null : game.Record(dbr);
            if (rec == null) return null;
            for (int hop = 0; hop < 3 && rec != null; hop++)
            {
                if (rec.GetInt("debufSkill") == 1) return rec.Path;
                string next = ItemViewFactory.NextSkillRecord(rec);
                rec = string.IsNullOrEmpty(next) ? null : game.Record(next);
            }
            return null;
        }

        // Whether this record buffs allies, including the player and other pets.
        static bool AllyAura(GameData game, string dbr)
        {
            DbRecord top = game == null || string.IsNullOrEmpty(dbr) ? null : game.Record(dbr);
            if (top == null) return false;
            if ((top.Class ?? "").IndexOf("BuffRadius", StringComparison.OrdinalIgnoreCase) < 0) return false;
            return CharacterTotals.IsAllyAura(game, dbr);
        }

        // Use the record-path tail as a name only for entries without a game name.
        static string Tail(string dbr)
        {
            string s = (dbr ?? "").Replace('\\', '/');
            int cut = s.LastIndexOf('/');
            if (cut >= 0) s = s.Substring(cut + 1);
            if (s.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            if (s.StartsWith("petskill_", StringComparison.OrdinalIgnoreCase)) s = s.Substring(9);
            return Lang.T("log.pet.noName", s);
        }

        // Merge a second hit: add direct and damage-over-time values, keeping the
        // larger duration and weapon fraction.
        static void Merge(Move into, Move more)
        {
            foreach (KeyValuePair<string, double> kv in more.Min)
            {
                double had;
                into.Min.TryGetValue(kv.Key, out had);
                into.Min[kv.Key] = had + kv.Value;
                double hi, hadHi;
                more.Max.TryGetValue(kv.Key, out hi);
                into.Max.TryGetValue(kv.Key, out hadHi);
                into.Max[kv.Key] = hadHi + Math.Max(hi, kv.Value);
            }
            foreach (KeyValuePair<string, double> kv in more.DotMin)
            {
                double had;
                into.DotMin.TryGetValue(kv.Key, out had);
                into.DotMin[kv.Key] = had + kv.Value;
                double hi, hadHi;
                more.DotMax.TryGetValue(kv.Key, out hi);
                into.DotMax.TryGetValue(kv.Key, out hadHi);
                into.DotMax[kv.Key] = hadHi + Math.Max(hi, kv.Value);
                double secs, hadSecs;
                more.DotSecs.TryGetValue(kv.Key, out secs);
                into.DotSecs.TryGetValue(kv.Key, out hadSecs);
                into.DotSecs[kv.Key] = Math.Max(secs, hadSecs);
            }
            if (more.WeaponPct > into.WeaponPct) into.WeaponPct = more.WeaponPct;
        }

        // Compare two hits field by field, including damage-over-time duration and weapon fraction.
        static bool SameNumbers(Move a, Move b)
        {
            return Same(a.Min, b.Min) && Same(a.Max, b.Max) && Same(a.DotMin, b.DotMin) &&
                   Same(a.DotMax, b.DotMax) && Same(a.DotSecs, b.DotSecs) &&
                   Math.Abs(a.WeaponPct - b.WeaponPct) < 1e-9;
        }

        static bool Same(Dictionary<string, double> a, Dictionary<string, double> b)
        {
            if (a.Count != b.Count) return false;
            foreach (KeyValuePair<string, double> kv in a)
            {
                double other;
                if (!b.TryGetValue(kv.Key, out other) || Math.Abs(other - kv.Value) > 1e-9) return false;
            }
            return true;
        }

        static bool Has(List<string> list, string dbr)
        {
            foreach (string one in list)
                if (string.Equals(one, dbr, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Pet skill values come from `PetPool.Numbers`, including skill and gear
        // conversion and pet-bonus modifiers; this matches the pet window.
        static Move MoveOf(GameData game, PetPool.Pet p, string dbr, int level, string name)
        {
            if (!PetPool.HasDamage(game, dbr)) return null;
            // Read values from the record that owns them while keeping the public
            // skill name and path from the outer record.
            string holds = PetPool.DamageRecord(game, dbr);
            // Use the same conversion and modifier path as `PetsForm` so the window
            // and timeline show identical values.
            DbRecord made = PetPool.Numbers(game, holds, level, p.Bonus,
                                            p.ConvMoves, p.Scale, p.OwnConv);
            if (made == null) return null;
            Move m = new Move();
            m.Dbr = dbr;
            m.Name = name;
            m.Level = level;
            m.Cooldown = AttackSkill.Value(made, "skillCooldownTime", 0);
            m.WeaponPct = AttackSkill.Value(made, "weaponDamagePct", 0);
            foreach (string type in Types)
            {
                double lo = AttackSkill.Value(made, "offensive" + type + "Min", 0);
                double hi = AttackSkill.Value(made, "offensive" + type + "Max", 0);
                if (hi < lo) hi = lo;
                if (lo > 0.0001) { m.Min[type] = lo; m.Max[type] = hi; }
                double dlo = AttackSkill.Value(made, "offensiveSlow" + type + "Min", 0);
                double dhi = AttackSkill.Value(made, "offensiveSlow" + type + "Max", 0);
                double secs = AttackSkill.Value(made, "offensiveSlow" + type + "DurationMin", 0);
                if (dhi < dlo) dhi = dlo;
                if (dlo > 0.0001 && secs > 0.0001)
                { m.DotMin[type] = dlo; m.DotMax[type] = dhi; m.DotSecs[type] = secs; }
            }
            return m;
        }


        static string F(double v)
        { return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); }

        static string F0(double v)
        { return v.ToString("0", System.Globalization.CultureInfo.InvariantCulture); }

        // Allied auras applied to pets.
        //
        // Ally-radius auras contribute attack ability and flat damage through
        // `CharacterTotals.IsAllyAura`; their values are stored in `AuraFields`.
        //
        // Read these values for each hit because the active allied aura set can change.
        public static double AuraAbility(CharacterTotals totals)
        {
            if (totals == null) return 0;
            TotalsEntry e;
            return totals.AuraFields.TryGetValue("characterOffensiveAbility", out e) ? e.Value : 0;
        }

        // Flat damage from an allied aura by damage type.
        public static double AuraFlat(CharacterTotals totals, string type, bool max)
        {
            if (totals == null) return 0;
            TotalsEntry e;
            string field = "offensive" + type + (max ? "Max" : "Min");
            return totals.AuraFields.TryGetValue(field, out e) ? e.Value : 0;
        }

        // Expose damage types used by pet hits.
        public static string[] DamageTypes { get { return Types; } }

        // Percentage damage bonuses that pet auras grant to other pets.
        //
        // Apply aura percentages to nearby pets; player totals already receive the
        // corresponding aura through `CharacterTotals.AddPetAura`.
        //
        // Build the pool, collect its auras, then rebuild it with those modifiers.
        //
        // Aura providers are the pets alive at the current timeline instant.
        public static Dictionary<string, double> AuraPercents(GameData game, List<Kind> kinds)
        {
            Dictionary<string, double> sum = new Dictionary<string, double>(StringComparer.Ordinal);
            if (game == null || kinds == null) return sum;
            List<string> seen = new List<string>();
            foreach (Kind k in kinds)
            {
                for (int i = 0; i < k.Auras.Count && i < k.AuraLevels.Count; i++)
                {
                    if (seen.Contains(k.Auras[i])) continue;
                    seen.Add(k.Auras[i]);
                    // Follow the record chain to `buffSkillName`, which owns the values.
                    string at = k.Auras[i];
                    for (int hop = 0; hop < 3 && !string.IsNullOrEmpty(at); hop++)
                    {
                        DbRecord rec = game.Record(at);
                        if (rec == null) break;
                        int slot = SkillText.Slot(rec, k.AuraLevels[i]);
                        foreach (KeyValuePair<string, string> kv in rec.Fields)
                        {
                            if (!kv.Key.StartsWith("offensive", StringComparison.Ordinal) ||
                                !kv.Key.EndsWith("Modifier", StringComparison.Ordinal)) continue;
                            double v = AttackSkill.Value(rec, kv.Key, slot);
                            if (Math.Abs(v) < 0.0001) continue;
                            double had;
                            sum.TryGetValue(kv.Key, out had);
                            sum[kv.Key] = had + v;
                        }
                        at = ItemViewFactory.NextSkillRecord(rec);
                    }
                }
            }
            return sum;
        }

        // Add auras from selected named pets to the character totals.
        //
        // Used by diagnostics and stands to model selected pets nearby while the
        // damage panel otherwise represents the player alone.
        //
        // `names` is a comma-separated list; `*` selects all marked pets. Returns
        // the number of applied auras, or -1 for an unknown pet.
        public static int ApplyAuras(GameData game, Character c, CharacterTotals totals,
                                     ActiveSkills chosen, string names)
        {
            if (game == null || c == null || totals == null || string.IsNullOrEmpty(names)) return 0;
            List<Kind> kinds = Of(game, c, totals, chosen, null, null);
            bool all = names.Trim() == "*";
            List<string> want = new List<string>();
            if (!all)
                foreach (string one in names.Split(','))
                    if (one.Trim().Length > 0) want.Add(one.Trim());
            int put = 0;
            HashSet<string> found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Kind k in kinds)
            {
                bool mine = all ? k.Info != null && k.Info.Chosen : false;
                if (!all)
                    foreach (string one in want)
                        if (string.Equals(game.ResolveNameSelector(one), k.Name, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(one, k.Summon, StringComparison.OrdinalIgnoreCase))
                        { mine = true; found.Add(one); }
                if (!mine) continue;
                for (int i = 0; i < k.Auras.Count && i < k.AuraLevels.Count; i++)
                {
                    string title = i < k.AuraNames.Count ? k.AuraNames[i] : k.Auras[i];
                    totals.AddPetAura(game, k.Auras[i], k.AuraLevels[i],
                                      "pet aura \"" + k.Name + "\": " + title);
                    put++;
                }
            }
            foreach (string one in want)
                if (!found.Contains(one)) return -1;
            return put;
        }

        // Pet attack ability follows the record equation, then applies flat and
        // percentage pet bonuses.
        public static double Ability(GameData game, string petDbr, Character c, CharacterTotals totals)
        { return Ability(game, petDbr, c, totals, 0, 0); }

        // `ownFlat` and `ownPct` are modifiers attached to this pet via `petSkillName`.
        public static double Ability(GameData game, string petDbr, Character c, CharacterTotals totals,
                                     double ownFlat, double ownPct)
        {
            DbRecord pet = game == null ? null : game.Record(petDbr);
            if (pet == null || c == null) return 0;
            DbRecord bio = game.Record(pet.Get("characterAttributeEquations") ?? "");
            if (bio == null) return 0;
            double level = c.Level;
            // Pet level is read from its own record equation.
            double own;
            if (Equation.TryEval(pet.Get("charLevel") ?? "", delegate(string nm)
                {
                    return string.Equals(nm, "charLevel", StringComparison.OrdinalIgnoreCase) ? level : 0;
                }, out own) && own > 0) level = own;
            double oa;
            if (!Equation.TryEval(bio.Get("characterOffensiveAbility") ?? "", delegate(string nm)
                {
                    return string.Equals(nm, "charLevel", StringComparison.OrdinalIgnoreCase) ? level : 0;
                }, out oa)) return 0;
            // Apply the same level and agility contribution used by monster ability
            // equations; the pet record uses the same structure.
            double cunning;
            if (!Equation.TryEval(bio.Get("characterDexterity") ?? "", delegate(string nm)
                {
                    return string.Equals(nm, "charLevel", StringComparison.OrdinalIgnoreCase) ? level : 0;
                }, out cunning)) cunning = 0;
            // Use `Combat.AbilityEquation`; pet-specific percentage modifiers apply after it.
            oa = Combat.AbilityEquation(game, true, oa, level, cunning, 0);
            double pct = ownPct;
            oa += ownFlat;
            if (totals != null)
            {
                TotalsEntry e;
                if (totals.PetFields.TryGetValue("characterOffensiveAbility", out e)) oa += e.Value;
                if (totals.PetFields.TryGetValue("characterOffensiveAbilityModifier", out e)) pct += e.Value;
            }
            return oa * (1 + pct / 100.0);
        }

        // Pet attack speed uses the same animation-based calculation as player speed:
        //
        //     attacks per second = record characterAttackSpeed / attack animation length
        //
        // Resolve the pet's attack speed and animation prefix from its animation table.
        //
        // Add the general and pet-specific speed percentages from `PetFields`.
        public static double AttackSpeed(GameData game, string petDbr, CharacterTotals totals)
        {
            DbRecord pet = game == null ? null : game.Record(petDbr);
            if (pet == null) return 0;
            double own = AttackSkill.Value(pet, "characterAttackSpeed", 0);
            if (own <= 0) own = 1;
            double secs = SkillHands.CreatureAttackSeconds(game, pet.Get("charAnimationTableName"));
            if (secs <= 0.0001) return 0;
            double pct = 0;
            if (totals != null)
            {
                TotalsEntry e;
                if (totals.PetFields.TryGetValue("characterAttackSpeedModifier", out e)) pct += e.Value;
                if (totals.PetFields.TryGetValue("characterTotalSpeedModifier", out e)) pct += e.Value;
            }
            return own / secs * (1 + pct / 100.0);
        }

        // Pet animation playback multiplier: `own × (1 + percent)`.
        public static double SpeedScale(GameData game, string petDbr, CharacterTotals totals)
        {
            DbRecord pet = game == null ? null : game.Record(petDbr);
            if (pet == null) return 0;
            double own = AttackSkill.Value(pet, "characterAttackSpeed", 0);
            if (own <= 0) own = 1;
            double pct = 0;
            if (totals != null)
            {
                TotalsEntry e;
                if (totals.PetFields.TryGetValue("characterAttackSpeedModifier", out e)) pct += e.Value;
                if (totals.PetFields.TryGetValue("characterTotalSpeedModifier", out e)) pct += e.Value;
            }
            return own * (1 + pct / 100.0);
        }

        // Duration of a pet hit: basic attack `1 / Aps`, aura zero, or skill animation
        // duration divided by playback speed. Missing animation yields zero.
        public static double Occupy(Kind k, Move m)
        {
            if (k == null || m == null || m.Aura) return 0;
            if (m.Auto || m.Basic) return k.Aps > 0 ? 1 / k.Aps : 0;
            // Select attack or cast speed according to the configured switch.
            double scale = CastForSkills ? k.CastScale : k.SpeedScale;
            return m.AnimSecs > 0 && scale > 0 ? m.AnimSecs / scale : 0;
        }

        // Pet cast speed uses the record base multiplied by general and cast-speed
        // bonuses, matching the player formula.
        public static double CastScale(GameData game, string petDbr, CharacterTotals totals)
        {
            DbRecord pet = game == null ? null : game.Record(petDbr);
            if (pet == null) return 0;
            double own = AttackSkill.Value(pet, "characterSpellCastSpeed", 0);
            if (own <= 0) own = 1;
            double pct = 0;
            if (totals != null)
            {
                TotalsEntry e;
                if (totals.PetFields.TryGetValue("characterSpellCastSpeedModifier", out e)) pct += e.Value;
                if (totals.PetFields.TryGetValue("characterTotalSpeedModifier", out e)) pct += e.Value;
            }
            return own * (1 + pct / 100.0);
        }
    }
}
