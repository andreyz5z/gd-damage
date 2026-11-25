// DAMAGE CONVERSION — ONE PLACE FOR THE WHOLE EDITOR.
//
// Kept out of `DamageModel` so the character and a pet convert by the same code: the rule is not
// simple (shares add up, over a hundred per cent is split, a dot follows its root but not into
// every type), and two copies of it would drift apart silently.
//
// THE RULE (measured — MECHANICS 6 and 14v):
//   * shares ADD UP, they are not applied one after another: 30% into fire and 20% into cold are
//     both taken from the base, not the second from what the first left;
//   * no more than 100% leaves a type: the hundred is split between the takers by their shares;
//   * NO CHAINS: what arrived in one pass is not a source in the next;
//   * a dot follows its root, but only where a dot exists at all — pierce, aether and chaos have
//     none, and the share that went there is not taken off the dot (`_Jien`: 78 poison per second);
//   * "Elemental" is not a type but three at once, and the sides are not equal: OUT of elemental
//     each of the three gets the whole share, INTO it the share is split three ways.
//
// THE PASSES, IN ORDER — three of them; inside a pass shares add up, between passes the remainder
// counts as a whole and what arrived earlier is no longer a source:
//   1. the skill's own conversion ("local") — its transmuter and the modifiers bound to it,
//      including those granted by items. `Pass(pool, own, first: true)` from `DamageModel.Build`;
//      pairs from `DamageModel.SkillConversions`.
//   2. the character's ELEMENTAL conversion (`conversionInType Elemental`) — runs BEFORE the
//      per-type one; the measurement is at `ElementalFirst`.
//   3. the rest of the character's conversion ("global") — items, sets, auras and active skills,
//      all in ONE pass. Pairs from `DamageModel.Conversions`; `Pass` itself splits 2 from 3.
//   And after all of it — armour piercing (`Pierce`, at the end of this file): it takes what is
//   left of physical and is not a conversion in the ordinary sense.
//
// Players (Crate forum, "All you need to know about conversion") give the same order. Their last
// step, "Equipment, Auras and Passives", is about PERCENT bonuses rather than conversion — and
// splitting items from auras into two passes breaks `_Itaan` physical by +28% (MECHANICS 95z).
//
// Only the mechanics live here; where the pairs come from is the caller's business —
// `DamageModel.Conversions` for the character, `PetPool.ConversionsOf` for a pet.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Gdc.Gui
{
    // Damage by root: low and high end, the dot and its seconds. `Came*` remembers what arrived
    // in the first pass — the second pass leaves that alone.
    public class DamagePool
    {
        public readonly Dictionary<string, double> Lo = new Dictionary<string, double>(StringComparer.Ordinal);
        public readonly Dictionary<string, double> Hi = new Dictionary<string, double>(StringComparer.Ordinal);
        public readonly Dictionary<string, double> DotLo = new Dictionary<string, double>(StringComparer.Ordinal);
        public readonly Dictionary<string, double> DotHi = new Dictionary<string, double>(StringComparer.Ordinal);
        public readonly Dictionary<string, double> DotSecs = new Dictionary<string, double>(StringComparer.Ordinal);
        public readonly Dictionary<string, double> CameLo = new Dictionary<string, double>(StringComparer.Ordinal);
        public readonly Dictionary<string, double> CameHi = new Dictionary<string, double>(StringComparer.Ordinal);
        public readonly Dictionary<string, double> CameDotLo = new Dictionary<string, double>(StringComparer.Ordinal);
        public readonly Dictionary<string, double> CameDotHi = new Dictionary<string, double>(StringComparer.Ordinal);

        // How much of this type ARRIVED BY THE CHARACTER'S CONVERSION rather than was born as it.
        // Separate from `Came*`: that one holds the first pass and exists to prevent chains, this
        // one holds the second pass and exists for another rule — the skill's own percentage does
        // NOT lie on what arrived by conversion (MECHANICS 19i). The skill's own conversion must
        // not go here: with it the percentage does travel along (MECHANICS 6, `SklCarried`).
        public readonly Dictionary<string, double> InLo = new Dictionary<string, double>(StringComparer.Ordinal);
        public readonly Dictionary<string, double> InHi = new Dictionary<string, double>(StringComparer.Ordinal);
        public readonly Dictionary<string, double> InDotLo = new Dictionary<string, double>(StringComparer.Ordinal);
        public readonly Dictionary<string, double> InDotHi = new Dictionary<string, double>(StringComparer.Ordinal);

        // THE SAME, BUT REMEMBERING WHERE FROM: "into which type" → "out of which type" → how much.
        // The travelling percentage needs it: it belongs to the SOURCE, so damage that arrived from
        // different types is owed different percentages (MECHANICS 95n). Without this memory one
        // average value is laid on the whole arrived pool, and `_Itaan` acid came out above the
        // game's.
        public readonly Dictionary<string, Dictionary<string, double>> FromLo =
            new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        public readonly Dictionary<string, Dictionary<string, double>> FromHi =
            new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        public readonly Dictionary<string, Dictionary<string, double>> FromDotLo =
            new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        public readonly Dictionary<string, Dictionary<string, double>> FromDotHi =
            new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);

        public static void NoteFrom(Dictionary<string, Dictionary<string, double>> where,
                                    string to, string from, double value)
        {
            if (where == null || string.IsNullOrEmpty(to) || string.IsNullOrEmpty(from)) return;
            Dictionary<string, double> inner;
            if (!where.TryGetValue(to, out inner))
                where[to] = inner = new Dictionary<string, double>(StringComparer.Ordinal);
            double had;
            inner.TryGetValue(from, out had);
            inner[from] = had + value;
        }

        // Every type present from the start, so the code that fills a pool can add
        // to it without asking whether the key is there.
        public DamagePool()
        {
            foreach (string root in Conversion.Roots) { Lo[root] = 0; Hi[root] = 0; }
            foreach (string root in Conversion.DotRoots) { DotLo[root] = 0; DotHi[root] = 0; DotSecs[root] = 0; }
        }
    }

    // One conversion pair: out of which type, into which, and how many per cent.
    public class Move
    {
        public readonly string From, To;
        public readonly double Percent;
        // Where it came from — for the `--convtable` listing. Nothing is counted from it; it is a
        // label for the eye, without which "some per cent of physical into something" can neither
        // be checked nor argued with.
        public readonly string Source;
        // From an AURA or an active skill rather than from an item. Needed only by the
        // `Conversion.Staged` experiment — in the ordinary count all of the character's are equal.
        public bool Late;
        // Declared from ELEMENTAL (`conversionInType Elemental`) rather than from fire, cold or
        // lightning one by one. Those run in a SEPARATE, EARLIER pass — see `ElementalFirst`.
        public bool FromElemental;
        public Move(string from, string to, double percent) : this(from, to, percent, null) { }
        public Move(string from, string to, double percent, string source)
        { From = from; To = to; Percent = percent; Source = source; }
    }

    public static class Conversion
    {
        // The damage roots. The order is the one in `DamageModel.Kinds` — lines are drawn by it,
        // and a difference here would break the display.
        public static readonly string[] Roots =
        { "Physical", "Pierce", "Fire", "Cold", "Lightning", "Poison", "Life", "Aether", "Chaos", "Bleeding" };

        // Which roots have a dot. Pierce, aether and chaos are missing on purpose: they have none.
        public static readonly string[] DotRoots =
        { "Physical", "Fire", "Cold", "Lightning", "Poison", "Life", "Bleeding" };

        public static bool HasDot(string root)
        { foreach (string one in DotRoots) if (one == root) return true; return false; }

        // One column of a "by level" list; past the end of the table the game repeats the last one,
        // as everywhere else here (`SkillText.Slot`).
        static string Step(string list, int slot)
        {
            if (string.IsNullOrEmpty(list)) return "";
            string[] parts = list.Split(';');
            return parts[Math.Min(Math.Max(0, slot), parts.Length - 1)];
        }

        public static void Read(DbRecord r, Dictionary<string, double> known, List<Move> into)
        { Read(r, known, into, null); }

        public static void Read(DbRecord r, Dictionary<string, double> known, List<Move> into, string source)
        { Read(r, known, into, source, 0); }

        // The conversion pairs of one record, appended to the list. `known` is what this part of
        // the item ROLLED: the share is rolled like every other number, and the rolled value wins
        // over the record's.
        //
        // `slot` — THE TABLE ROW FOR THE SKILL'S LEVEL. A share can be a list: Star Pact
        // (`playerclass05/mindovermatter2`) holds `conversionPercentage2 = 15;18;21;…;60`, i.e.
        // aether into cold grows with the level. We always took the first column, and on `_Assira`
        // that gave aether 310 against the game's 146 (+112%) with cold 15–20% low — one shortfall
        // seen from both sides. Items have no tables, their slot is 0.
        public static void Read(DbRecord r, Dictionary<string, double> known, List<Move> into,
                                string source, int slot)
        {
            if (r == null) return;
            for (int n = 0; n <= 6; n++)
            {
                string tail = n == 0 ? "" : n.ToString();
                string from = r.Get("conversionInType" + tail), to = r.Get("conversionOutType" + tail);
                string pct = r.Get("conversionPercentage" + tail);
                double p;
                if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) ||
                    !double.TryParse(Step(pct, slot), NumberStyles.Float,
                                     CultureInfo.InvariantCulture, out p) || p <= 0) continue;
                double rolled;
                if (known != null && known.TryGetValue("conversionPercentage" + tail, out rolled)) p = rolled;
                // "Elemental" is not a damage type but three at once, and the two directions are
                // not symmetric: OUT of elemental each of the three gets the whole percentage,
                // INTO it the percentage is split three ways. From the reverse of the GrimTools
                // calculator (SITE-VS-US.md, step 6).
                bool fromElem = from.Equals("Elemental", StringComparison.OrdinalIgnoreCase);
                bool toElem = to.Equals("Elemental", StringComparison.OrdinalIgnoreCase);
                if (fromElem || toElem)
                {
                    foreach (string one in new[] { "Fire", "Cold", "Lightning" })
                    {
                        string a = fromElem ? one : from, b = toElem ? one : to;
                        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) continue;
                        Move made = new Move(a, b, toElem ? p / 3 : p, source ?? (r.Path ?? ""));
                        // Further on it is no longer visible where the pair came from — the three
                        // shares are already spread over the types. And it must be known: the
                        // elemental one has a pass of its own (`ElementalFirst`).
                        made.FromElemental = fromElem;
                        into.Add(made);
                    }
                    continue;
                }
                into.Add(new Move(from, to, p, source ?? (r.Path ?? "")));
            }
        }

        // EXPERIMENT `--stagedconv`: split the CHARACTER's conversion into two passes — gear and
        // sets first, auras and active skills after — as an article of 26 September describes.
        //
        // We keep them as ONE pass with the shares added, and that is measured, not assumed: on
        // `_Itaan` physical goes into acid as four shares from items (53.4%) and one from the aura
        // Path of the Three (15%), and the panel matches TO THE UNIT only when they add up (68.4%
        // leaving). Splitting would give 60.4% and lift the line by a quarter. The flag exists so
        // the article can be answered with a number and run across every stand at once.
        public static bool Staged;

        // THE ELEMENTAL CONVERSION RUNS BEFORE THE PER-TYPE ONE, and what it leaves counts as a
        // whole. Entered 26 September; `--elemtogether` restores the old addition so the rule can
        // be broken on purpose.
        //
        // THE MEASUREMENT THAT DEMANDED IT (MECHANICS 95z). On `_Itaan` fire goes into acid as two
        // shares: 25.2% from the hood, declared `Elemental → Poison`, and 50% from the aura Path of
        // the Three, declared `Fire → Poison`. Burn of Righteous Fervor (transmuter reset):
        //
        //     shares added (75.2% leaving)        156     game 247    −36.8%
        //     elemental first (0.748 × 0.5)       247     game 247    TO THE UNIT
        //
        // Players say the same (Crate forum, same thread): elemental has priority, the remaining
        // fire counts as 100% for further conversion. Their model alone would prove nothing
        // (PLAN, rule 18), but here it matched our measurement, which was made first.
        //
        // WHAT THE RULE DOES NOT TOUCH: physical, life and the other non-elemental types have no
        // elemental share at all, and there the shares still ADD UP — checked on the same `_Itaan`,
        // where physical goes into acid as five shares and the panel matches to the unit
        // (1114–1579 against the game's 1114–1579).
        public static bool ElementalFirst = true;

        // One conversion pass. `first` marks what arrived so the next pass will not take it again.
        public static void Pass(DamagePool pool, List<Move> moves, bool first)
        {
            // With `--stagedconv` the auras go in a SEPARATE pass after the items, and what the
            // items brought is no longer a source for them (`first` marks it).
            if (Staged && moves != null && !first)
            {
                List<Move> gear = new List<Move>(), late = new List<Move>();
                foreach (Move m in moves) (m.Late ? late : gear).Add(m);
                if (gear.Count > 0 && late.Count > 0)
                {
                    PassOne(pool, gear, true);
                    PassOne(pool, late, false);
                    return;
                }
            }
            // The elemental ones go in their own pass BEFORE the rest of the same rank. Split only
            // when both kinds are present, otherwise it is an extra walk for nothing.
            if (ElementalFirst && moves != null && moves.Count > 1)
            {
                List<Move> elem = new List<Move>(), rest = new List<Move>();
                foreach (Move m in moves) (m.FromElemental ? elem : rest).Add(m);
                if (elem.Count > 0 && rest.Count > 0)
                {
                    PassOne(pool, elem, first);
                    // What the elemental pass brought is not a source for the second one: the same
                    // "no damage is converted twice" that keeps the second pass off the first's.
                    PassOne(pool, rest, first, true);
                    return;
                }
            }
            PassOne(pool, moves, first);
        }

        static void PassOne(DamagePool pool, List<Move> moves, bool first)
        { PassOne(pool, moves, first, false); }

        // `afterElemental` — this pass runs AFTER the elemental one, so what that pass brought does
        // not count as a source.
        static void PassOne(DamagePool pool, List<Move> moves, bool first, bool afterElemental)
        {
            if (pool == null || moves == null || moves.Count == 0) return;
            Dictionary<string, double> lo = pool.Lo, hi = pool.Hi, dlo = pool.DotLo, dhi = pool.DotHi;
            Dictionary<string, double> leaving = new Dictionary<string, double>(StringComparer.Ordinal);
            // HOW MUCH LEAVES THE DOT — and it is NOT the same as what leaves the direct damage.
            //
            // A conversion drags the dot of the same root with it, but only where the RECEIVER has
            // a dot at all: pierce, aether and chaos have none. Adding a dot there we never did —
            // but we did subtract it from the source by the FULL share, the part that went into a
            // dotless type included, so the dot simply vanished.
            //
            // Measured on `_Jien`: 25% of acid leaves into cold and 58% into pierce, and the panel
            // shows 78 poison per second from both hands. The old count left 17% and gave 18
            // (−77%); with pierce not touching the dot, 75% remains and it comes out near 78.
            Dictionary<string, double> leavingDot = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (Move m in moves)
            {
                leaving[m.From] = (leaving.ContainsKey(m.From) ? leaving[m.From] : 0) + m.Percent;
                if (dlo.ContainsKey(m.From) && dlo.ContainsKey(m.To))
                    leavingDot[m.From] = (leavingDot.ContainsKey(m.From) ? leavingDot[m.From] : 0) + m.Percent;
            }

            Dictionary<string, double> addLo = new Dictionary<string, double>(StringComparer.Ordinal);
            Dictionary<string, double> addHi = new Dictionary<string, double>(StringComparer.Ordinal);
            Dictionary<string, double> dAddLo = new Dictionary<string, double>(StringComparer.Ordinal);
            Dictionary<string, double> dAddHi = new Dictionary<string, double>(StringComparer.Ordinal);

            foreach (Move m in moves)
            {
                string from = m.From, to = m.To;
                double share = m.Percent / 100.0;
                // More than everything cannot leave: the hundred per cent is split
                // between the takers in proportion to their shares (m.txt).
                double total = leaving[from] / 100.0;
                if (total > 1) share /= total;
                if (!lo.ContainsKey(from) || !lo.ContainsKey(to)) continue;
                // What arrived in the previous pass is not a source here — no chains.
                double srcLo = lo[from] - Arrived(pool.CameLo, from) -
                               (afterElemental ? Arrived(pool.InLo, from) : 0);
                double srcHi = hi[from] - Arrived(pool.CameHi, from) -
                               (afterElemental ? Arrived(pool.InHi, from) : 0);
                addLo[to] = (addLo.ContainsKey(to) ? addLo[to] : 0) + srcLo * share;
                addHi[to] = (addHi.ContainsKey(to) ? addHi[to] : 0) + srcHi * share;
                // And BY NAME — where from (see `DamagePool.FromLo`). Written in either pass: the
                // travelling percentage is asked of both the skill's own conversion and the
                // character's, and the two must not be added — their percentages differ.
                if (!first)
                {
                    DamagePool.NoteFrom(pool.FromLo, to, from, srcLo * share);
                    DamagePool.NoteFrom(pool.FromHi, to, from, srcHi * share);
                }
                // A conversion drags the dot of the same root with it — except
                // into aether, chaos and pierce, which have no dot of their own.
                if (dlo.ContainsKey(from) && dlo.ContainsKey(to))
                {
                    double dSrcLo = dlo[from] - Arrived(pool.CameDotLo, from) -
                                    (afterElemental ? Arrived(pool.InDotLo, from) : 0);
                    double dSrcHi = dhi[from] - Arrived(pool.CameDotHi, from) -
                                    (afterElemental ? Arrived(pool.InDotHi, from) : 0);
                    dAddLo[to] = (dAddLo.ContainsKey(to) ? dAddLo[to] : 0) + dSrcLo * share;
                    dAddHi[to] = (dAddHi.ContainsKey(to) ? dAddHi[to] : 0) + dSrcHi * share;
                    if (!first)
                    {
                        DamagePool.NoteFrom(pool.FromDotLo, to, from, dSrcLo * share);
                        DamagePool.NoteFrom(pool.FromDotHi, to, from, dSrcHi * share);
                    }
                }
            }
            // The share leaves what WAS a source; what arrived in the previous pass stays put.
            foreach (KeyValuePair<string, double> kv in leaving)
            {
                double keep = Math.Max(0, 1 - kv.Value / 100.0);
                if (kv.Value > 100) keep = 0;
                if (lo.ContainsKey(kv.Key))
                {
                    double keptLo = Arrived(pool.CameLo, kv.Key), keptHi = Arrived(pool.CameHi, kv.Key);
                    lo[kv.Key] = (lo[kv.Key] - keptLo) * keep + keptLo;
                    hi[kv.Key] = (hi[kv.Key] - keptHi) * keep + keptHi;
                }
                if (dlo.ContainsKey(kv.Key))
                {
                    // The dot has a leaving share of its own — only what went into a type that has
                    // a dot (see `leavingDot` above).
                    double gone = leavingDot.ContainsKey(kv.Key) ? leavingDot[kv.Key] : 0;
                    // Same proportion as for direct damage: when more than a hundred per cent
                    // leaves, the hundred is split between the takers by their shares and the dot
                    // gets its part of that hundred.
                    if (kv.Value > 100) gone = gone / kv.Value * 100;
                    double keepDot = Math.Max(0, 1 - gone / 100.0);
                    double keptLo = Arrived(pool.CameDotLo, kv.Key), keptHi = Arrived(pool.CameDotHi, kv.Key);
                    dlo[kv.Key] = (dlo[kv.Key] - keptLo) * keepDot + keptLo;
                    dhi[kv.Key] = (dhi[kv.Key] - keptHi) * keepDot + keptHi;
                }
            }
            foreach (KeyValuePair<string, double> kv in addLo) lo[kv.Key] += kv.Value;
            foreach (KeyValuePair<string, double> kv in addHi) hi[kv.Key] += kv.Value;
            foreach (KeyValuePair<string, double> kv in dAddLo) dlo[kv.Key] += kv.Value;
            foreach (KeyValuePair<string, double> kv in dAddHi) dhi[kv.Key] += kv.Value;
            if (!first)
            {
                // The second pass is the CHARACTER's conversion. Remember how much arrived: the
                // skill's own percentage does not lie on it (MECHANICS 19i).
                foreach (KeyValuePair<string, double> kv in addLo) Note(pool.InLo, kv.Key, kv.Value);
                foreach (KeyValuePair<string, double> kv in addHi) Note(pool.InHi, kv.Key, kv.Value);
                foreach (KeyValuePair<string, double> kv in dAddLo) Note(pool.InDotLo, kv.Key, kv.Value);
                foreach (KeyValuePair<string, double> kv in dAddHi) Note(pool.InDotHi, kv.Key, kv.Value);
                return;
            }
            foreach (KeyValuePair<string, double> kv in addLo) Note(pool.CameLo, kv.Key, kv.Value);
            foreach (KeyValuePair<string, double> kv in addHi) Note(pool.CameHi, kv.Key, kv.Value);
            foreach (KeyValuePair<string, double> kv in dAddLo) Note(pool.CameDotLo, kv.Key, kv.Value);
            foreach (KeyValuePair<string, double> kv in dAddHi) Note(pool.CameDotHi, kv.Key, kv.Value);
        }

        // ARMOUR PIERCING IS A CONVERSION TOO, and the last one: it takes what is left of physical.
        // Checked on Fist of the Blind Assassin — 100% piercing with 9% converted into life gave 9% to
        // life and 91% to pierce, not 9% and 100%. Pierce has no dot, so internal trauma stays.
        public static void Pierce(DamagePool pool, double ratio)
        {
            if (pool == null || ratio <= 0) return;
            if (ratio > 1) ratio = 1;
            // …AND IT HAS NO CHAINS EITHER: physical that ARRIVED by conversion is not touched by
            // armour piercing — the same rule `Pass` follows above.
            //
            // MEASURED on `_555`, Vampiric Shadows (1 September). The skill's own damage is life only
            // (600), and the character sends life four ways at once — 28.8% and 42.7% into pierce,
            // 24% and 25.7% into physical. The sums pass a hundred, so the hundred is split by
            // shares: 354 to pierce, 246 to physical. Piercing is 100%, and the old count took
            // those 246 next, leaving NO physical in the panel at all (600 of 600 into pierce).
            //
            // The game says otherwise, and its four numbers land together:
            //
            //   physical   weapon 0.75 × 1748 × 3.40 = 4458 + own 4536 = 8995   game 8992
            //   high       0.75 × 2116 × 3.40 = 5397 + 4536            = 9933   game 9930
            //   pierce     weapon 17199 + own 354 × 1.5 × 24.6043      = 30262  game 30255
            //   high       21704 + 13063                              = 34767  game 34760
            //
            // We had 5944–7196 and 39342–43848 — off by a third in both directions. The same fix
            // repairs the AVERAGING too: while the skill's own physical was zeroed, the "only one
            // hand carries this type" test (14d4) fired and the weapon part went WITHOUT its
            // quarter — 5944 instead of 4458. One rule, not two.
            //
            // WHAT THE MEASUREMENT DOES NOT SAY: what to do with physical that arrived by the
            // SKILL's own conversion (`Came*`). We have no such case; here it is treated like the
            // character's, the reason being the same — it has been converted once already.
            double keepLo = Arrived(pool.CameLo, "Physical") + Arrived(pool.InLo, "Physical");
            double keepHi = Arrived(pool.CameHi, "Physical") + Arrived(pool.InHi, "Physical");
            double srcLo = pool.Lo["Physical"] - keepLo;
            double srcHi = pool.Hi["Physical"] - keepHi;
            if (srcLo < 0) srcLo = 0;
            if (srcHi < 0) srcHi = 0;
            pool.Lo["Pierce"] += srcLo * ratio;
            pool.Hi["Pierce"] += srcHi * ratio;
            pool.Lo["Physical"] -= srcLo * ratio;
            pool.Hi["Physical"] -= srcHi * ratio;
        }

        // The shares brought to what will actually leave: when more than a hundred per cent leaves
        // a type, the hundred is split between the takers by their shares. For DISPLAY only — the
        // calculation does the same inside `Pass`.
        public static List<Move> Effective(List<Move> moves)
        {
            List<Move> made = new List<Move>();
            if (moves == null) return made;
            Dictionary<string, double> outOf = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (Move m in moves)
                outOf[m.From] = (outOf.ContainsKey(m.From) ? outOf[m.From] : 0) + m.Percent;
            foreach (Move m in moves)
            {
                double total = outOf[m.From];
                made.Add(new Move(m.From, m.To, total > 100 ? m.Percent / total * 100 : m.Percent, m.Source));
            }
            return made;
        }

        // Identical moves folded into one line: an item and its affix carry the same pair, and
        // printing it twice is pointless.
        public static List<Move> Merge(List<Move> moves)
        {
            List<Move> made = new List<Move>();
            if (moves == null) return made;
            Dictionary<string, int> at = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Move m in moves)
            {
                string key = m.From + "\t" + m.To;
                int i;
                if (at.TryGetValue(key, out i))
                    made[i] = new Move(m.From, m.To, made[i].Percent + m.Percent,
                                       made[i].Source + "; " + m.Source);
                else { at[key] = made.Count; made.Add(new Move(m.From, m.To, m.Percent, m.Source)); }
            }
            return made;
        }

        // The root's name in the words the game itself prints in a conversion line ("50%
        // conversion: Vitality Damage to Acid Damage"). THE WORDS ARE THE GAME'S
        // (`tagConversion<Type>`), ours are the fallback; bleeding has no such tag in the game, so
        // there the word is only ours.
        public static string RootName(GameData game, string root)
        {
            string name = game == null ? null : game.Tag("tagConversion" + root);
            if (!string.IsNullOrEmpty(name)) return name;
            switch (root)
            {
                case "Physical": return Lang.T("item.conv.Physical");
                case "Pierce": return Lang.T("item.conv.Pierce");
                case "Fire": return Lang.T("item.conv.Fire");
                case "Cold": return Lang.T("item.conv.Cold");
                case "Lightning": return Lang.T("item.conv.Lightning");
                case "Poison": return Lang.T("item.conv.Poison");
                case "Life": return Lang.T("item.conv.Life");
                case "Aether": return Lang.T("item.conv.Aether");
                case "Chaos": return Lang.T("item.conv.Chaos");
                case "Bleeding": return Lang.T("item.conv.Bleeding");
            }
            return root;
        }

        static double Arrived(Dictionary<string, double> came, string type)
        { double v; return came.TryGetValue(type, out v) ? v : 0; }

        static void Note(Dictionary<string, double> came, string type, double value)
        { came[type] = (came.ContainsKey(type) ? came[type] : 0) + value; }
    }
}
