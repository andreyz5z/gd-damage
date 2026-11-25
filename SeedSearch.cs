// Find a seed that produces the requested rolled values.
//
// Unlike the broad seed scan, the editor needs field-specific constraints:
//
//   * values are named rather than forced to their maximum;
//   * unchecked fields add no constraint;
//   * search stops at the first matching seed;
//   * failure is reported only after the complete residue ring is checked.
//
// The item stream is Park–Miller: field i is a^(i+2)·seed modulo M = 2³¹−1.
// Because multiplication by a^k is bijective, enumerate the first constrained
// word instead of every seed, then recover the seed with the modular inverse.
//
// Roll order and values come from ItemOrder and ItemRoll. This class only builds
// necessary residue constraints; every candidate is recalculated with the same
// full path used by the tooltip before it is accepted.
//
// No useful sieve exists for scaled multi-part fields, an unconstrained lower
// endpoint of a range pair, or unchecked fields.
//
// The search changes only `seed`; item records, affixes, components, augments,
// and ascension data remain untouched.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Gdc.Gui
{
    public static class SeedSearch
    {
        const long M = 2147483647;   // 2³¹ − 1
        const long A = 16807;

        // ================================ PLAN ================================

        // The item and pet-bonus streams use the same seed and word sequence;
        // pet bonuses use a separate stream without item scaling.
        public class Lane
        {
            public List<ItemOrder.Rolled> Slots;
            public int Scale;
            public string Name;
        }

        public class Plan
        {
            public List<Lane> Lanes = new List<Lane>();
            public GameData Game;
            public Item Item;
            public DbRecord Rec;
            public uint OriginalSeed;

            public void Roll(uint seed)
            {
                foreach (Lane l in Lanes) ItemOrder.RollPlan(l.Slots, seed, l.Scale);
            }

            public ItemOrder.Rolled Slot(int part, string field)
            {
                foreach (Lane l in Lanes)
                    foreach (ItemOrder.Rolled r in l.Slots)
                        if (r.Part == part && string.Equals(r.Field, field, StringComparison.Ordinal)) return r;
                return null;
            }

            public int LaneOf(ItemOrder.Rolled slot)
            {
                for (int i = 0; i < Lanes.Count; i++)
                    if (Lanes[i].Slots.Contains(slot)) return i;
                return -1;
            }

            public int Count
            {
                get { int n = 0; foreach (Lane l in Lanes) n += l.Slots.Count; return n; }
            }
        }

        // Plan index for a pet bonus attached to item part k. Values 0/1/2 are
        // reserved for the item's own parts.
        public const int PetPart = 10;

        public static Plan Build(GameData game, Item item, DbRecord rec)
        {
            Plan p = new Plan();
            p.Game = game; p.Item = item; p.Rec = rec;
            if (item == null || rec == null) return p;
            p.OriginalSeed = item.Seed;
            Lane main = new Lane();
            main.Slots = ItemOrder.WholePlan(game, item, rec);
            main.Scale = rec.GetInt("attributeScalePercent");
            main.Name = "item";
            p.Lanes.Add(main);

            // A pet bonus uses the jitter of the record that names it; the bonus
            // itself has no `lootRandomizerJitter`.
            string[] parts = { item.BaseName, item.PrefixName, item.SuffixName };
            string[] names = { "base", "prefix", "suffix" };
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i]) || game == null) continue;
                DbRecord part = i == 0 ? rec : game.Record(parts[i]);
                DbRecord pet = part == null ? null : game.Record(part.Get("petBonusName"));
                if (pet == null) continue;
                int jitter = ItemViewFactory.JitterOf(part);
                Lane lane = new Lane();
                lane.Slots = new List<ItemOrder.Rolled>();
                lane.Scale = 0;
                lane.Name = "pets (" + names[i] + ")";
                foreach (string f in ItemOrder.Fields(pet, jitter))
                {
                    ItemOrder.Rolled r = new ItemOrder.Rolled();
                    r.Part = PetPart + p.Lanes.Count - 1;
                    r.Field = f;
                    r.Value = Num(pet, f);
                    r.Jitter = jitter;
                    lane.Slots.Add(r);
                }
                if (lane.Slots.Count > 0) p.Lanes.Add(lane);
            }
            p.Roll(item.Seed);
            return p;
        }

        static double Num(DbRecord rec, string field)
        {
            double v;
            double.TryParse((rec.Get(field) ?? "").Split(';')[0], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out v);
            return v;
        }

        // =============================== WANTS ================================

        public class Want
        {
            public int Part;
            public string Field;
            public long Value;
            public string Label;      // Field label used in messages.
            public override string ToString() { return (Label ?? Field) + " = " + Value; }
        }

        // One constraint for one stream word. `Ok` stores valid residues, conversion
        // uses a word interval, and `Any` means no sieve is available.
        class Cond
        {
            public int Word;
            public long Size;
            public bool[] Ok;
            public long Lo, Hi;
            public bool Any;
            public Want W;
            public long Passed;       // Candidates remaining at this check.
            public double Chance;     // Seed share producing this value alone.
            public string Range;      // Values this field can produce.
        }

        public class Report
        {
            public bool Found;
            public uint Seed;
            public bool Cancelled;
            public long Scanned;
            public double Seconds;
            public List<uint> Matches = new List<uint>();          // Used by Options.All.
            public List<string> Impossible = new List<string>();  // Not reachable alone.
            public string Deepest;      // Field where the search progressed furthest.
            // `Text` is the detailed CLI diagnostic; `Short` is the concise UI status.
            public string Text = "";
            public string Short = "";
        }

        public class Options
        {
            // Starting point in the residue ring. Zero selects a random start so
            // repeated searches do not return the same seed every time.
            public long Start;
            public long Limit;            // Residues to inspect; 0 means all.
            public bool All;              // Collect every match instead of the first.
            public bool Verify = true;    // Recheck matches independently.
            public Func<long, bool> Tick; // Return false to cancel.
        }

        // ============================== SEARCH ================================

        public static Report Find(Plan plan, List<Want> wants, Func<long, bool> tick)
        {
            Options o = new Options();
            o.Tick = tick;
            return Find(plan, wants, o);
        }

        public static Report Find(Plan plan, List<Want> wants, Options opt)
        {
            if (opt == null) opt = new Options();
            long start = opt.Start;
            bool verify = opt.Verify;
            Func<long, bool> tick = opt.Tick;
            Report rep = new Report();
            StringBuilder sb = new StringBuilder();
            DateTime began = DateTime.Now;
            if (plan == null || plan.Lanes.Count == 0)
            { rep.Text = "This item has no rollable fields."; return rep; }

            List<Cond> conds = new List<Cond>();
            int keep = ItemViewFactory.RollJitter;
            try
            {
                foreach (Want w in wants)
                {
                    Cond c = Make(plan, wants, w);
                    if (c == null)
                    {
                        // This text is also shown in the editor from `edit.txt`.
                        rep.Impossible.Add(Lang.T("edit.report.impossible", w.Label ?? w.Field, w.Value));
                        continue;
                    }
                    conds.Add(c);
                }
            }
            finally { ItemViewFactory.RollJitter = keep; }

            if (rep.Impossible.Count > 0)
            {
                foreach (string one in rep.Impossible) sb.AppendLine("UNREACHABLE  " + one);
                sb.AppendLine();
                sb.AppendLine("The item cannot roll in the game because a value is outside its roll range.");
                rep.Text = sb.ToString();
                rep.Short = "";   // The UI formats this from Impossible.
                return rep;
            }

            conds.Sort(delegate (Cond a, Cond b) { return a.Word - b.Word; });

            sb.AppendLine("=== SEARCH TARGET ===");
            double chance = 1;
            foreach (Cond c in conds)
            {
                sb.AppendLine(string.Format("  word {0,2}  {1,-46} = {2,-8} {3,-22} share {4}",
                                            c.Word + 1, (c.W.Label ?? c.W.Field), c.W.Value, c.Range,
                                            c.Chance.ToString("0.####", CultureInfo.InvariantCulture)));
                chance *= c.Chance;
            }
            if (conds.Count == 0)
            {
                sb.AppendLine("  (no required fields)");
            }
            sb.AppendLine();
            sb.AppendLine("If the words were independent, the expected number of matching seeds would be " +
                          (chance * (M - 1)).ToString("0.###", CultureInfo.InvariantCulture) +
                          " out of " + (M - 1));
            sb.AppendLine("Use this estimate only to gauge search time. The words are linked (x₂ = a·x₁).");
            sb.AppendLine();

            // With no constraints any seed is valid; choose a random one.
            if (conds.Count == 0)
            {
                rep.Found = true;
                rep.Seed = (uint)(Rand() % (M - 1) + 1);
                plan.Roll(rep.Seed);
                rep.Text = sb.ToString();
                return rep;
            }

            int first = conds[0].Word;
            // Field i is a^(i+2)·seed, so seed = a^−(i+2)·word.
            long back = PowMod(InvA(), first + 2);
            long[] step = new long[conds.Count];
            for (int i = 1; i < conds.Count; i++) step[i] = PowMod(A, conds[i].Word - conds[i - 1].Word);

            // Seed zero is valid and produces an all-zero stream, so it is checked
            // separately and is excluded when the caller requests an exact window.
            if (opt.Limit == 0 && Check(plan, wants, 0u, verify))
            {
                rep.Seed = 0; rep.Matches.Add(0);
                if (!opt.All) rep.Found = true;
            }

            long y = start > 0 ? ((start - 1) % (M - 1)) + 1 : (Rand() % (M - 1)) + 1;
            long scanned = 0;
            long tickEvery = 4 * 1024 * 1024;
            long total = opt.Limit > 0 && opt.Limit < M - 1 ? opt.Limit : M - 1;
            for (long t = 0; !rep.Found && t < total; t++, scanned++, y = y + 1 >= M ? 1 : y + 1)
            {
                long w = y;
                bool ok = true;
                for (int i = 0; i < conds.Count; i++)
                {
                    if (i > 0) w = Mul(w, step[i]);
                    Cond c = conds[i];
                    c.Passed++;
                    if (c.Any) continue;
                    if (c.Size > 0) { if (!c.Ok[w % c.Size]) { ok = false; break; } }
                    else if (w < c.Lo || w > c.Hi) { ok = false; break; }
                }
                if (ok)
                {
                    uint seed = (uint)Mul(y, back);
                    if (Check(plan, wants, seed, verify))
                    {
                        rep.Seed = seed;
                        rep.Matches.Add(seed);
                        if (!opt.All) { rep.Found = true; break; }
                    }
                }
                if ((t & (tickEvery - 1)) == 0 && tick != null && !tick(t))
                { rep.Cancelled = true; break; }
            }

            rep.Scanned = scanned;
            rep.Seconds = (DateTime.Now - began).TotalSeconds;
            if (opt.All) rep.Found = rep.Matches.Count > 0;
            // `counted` and `Short` are editor-facing text from `edit.txt`.
            string counted = Lang.T("edit.report.scanned", scanned,
                                    rep.Seconds.ToString("0.0", CultureInfo.InvariantCulture));
            if (rep.Found)
            {
                plan.Roll(rep.Seed);
                sb.AppendLine("FOUND: seed " + rep.Seed + "   (" + counted + ")");
                rep.Short = Lang.T("edit.report.found", counted);
            }
            else
            {
                plan.Roll(plan.OriginalSeed);
                // Funnel counts show how many candidates survive each constraint.
                // They are diagnostic output for `--seedwant`; the editor shows only
                // the concise status.
                sb.AppendLine(rep.Cancelled ? "Cancelled by the user" :
                              "No matching seed found after checking all " + (M - 1) + " residues");
                sb.AppendLine(counted);
                sb.AppendLine();
                sb.AppendLine("=== CANDIDATES REMAINING AFTER EACH CHECK ===");
                string worstField = null;
                for (int i = 0; i < conds.Count; i++)
                {
                    sb.AppendLine(string.Format("  {0,-46} = {1,-8} remaining: {2}",
                                                (conds[i].W.Label ?? conds[i].W.Field), conds[i].W.Value, conds[i].Passed));
                    if (conds[i].Passed == 0 && worstField == null) worstField = conds[i].W.Field;
                }
                if (worstField == null && conds.Count > 0) worstField = conds[conds.Count - 1].W.Field;
                rep.Deepest = worstField;
                rep.Short = counted;
            }
            rep.Text = sb.ToString();
            return rep;
        }

        // Recalculate the complete item with the tooltip code and require every
        // requested value to match. The sieve only narrows the candidates.
        static bool Check(Plan plan, List<Want> wants, uint seed, bool verify)
        {
            plan.Roll(seed);
            foreach (Want w in wants)
            {
                ItemOrder.Rolled s = plan.Slot(w.Part, w.Field);
                if (s == null || s.Got != w.Value) return false;
            }
            return !verify || Verify(plan, wants, seed);
        }

        // Recheck the result from a fresh plan and item traversal so a corrupted
        // search plan cannot validate itself.
        static bool Verify(Plan plan, List<Want> wants, uint seed)
        {
            if (plan.Game == null || plan.Item == null || plan.Rec == null) return true;
            Item probe = Item.Create(plan.Item.BaseName, 4);
            probe.PrefixName = plan.Item.PrefixName;
            probe.SuffixName = plan.Item.SuffixName;
            probe.Seed = seed;
            List<ItemOrder.Rolled> whole = ItemOrder.RollWhole(plan.Game, probe, plan.Rec);
            foreach (Want w in wants)
            {
                long got;
                if (w.Part < PetPart)
                {
                    got = long.MinValue;
                    foreach (ItemOrder.Rolled r in whole)
                        if (r.Part == w.Part && r.Field == w.Field) { got = r.Got; break; }
                }
                else
                {
                    Lane lane = plan.Lanes[w.Part - PetPart + 1];
                    DbRecord pet = PetRecordOf(plan, w.Part);
                    if (pet == null) return false;
                    Dictionary<string, long> all =
                        ItemOrder.RollAll(pet, seed, 0, lane.Slots.Count > 0 ? lane.Slots[0].Jitter : 20);
                    if (!all.TryGetValue(w.Field, out got)) return false;
                }
                if (got != w.Value) return false;
            }
            return true;
        }

        public static DbRecord PetRecordOf(Plan plan, int part)
        {
            int which = part - PetPart;   // Pet-bonus field index.
            string[] parts = { plan.Item.BaseName, plan.Item.PrefixName, plan.Item.SuffixName };
            int seen = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                DbRecord one = i == 0 ? plan.Rec : plan.Game.Record(parts[i]);
                DbRecord pet = one == null ? null : plan.Game.Record(one.Get("petBonusName"));
                if (pet == null) continue;
                if (seen == which) return pet;
                seen++;
            }
            return null;
        }

        // ===================== BRUTE FORCE CHECK =============================
        //
        // Scan the same residue window without the sieve. This is slow but verifies
        // that the sieve does not discard valid seeds.
        public static List<uint> Brute(Plan plan, List<Want> wants, long start, long window)
        {
            List<uint> found = new List<uint>();
            int first = FirstWord(plan, wants);
            if (first < 0) return found;
            long back = PowMod(InvA(), first + 2);
            long y = ((start - 1) % (M - 1)) + 1;
            for (long t = 0; t < window; t++, y = y + 1 >= M ? 1 : y + 1)
            {
                uint seed = (uint)Mul(y, back);
                plan.Roll(seed);
                bool ok = true;
                foreach (Want w in wants)
                {
                    ItemOrder.Rolled s = plan.Slot(w.Part, w.Field);
                    if (s == null || s.Got != w.Value) { ok = false; break; }
                }
                if (ok) found.Add(seed);
            }
            return found;
        }

        // Stream word associated with the earliest constrained field.
        public static int FirstWord(Plan plan, List<Want> wants)
        {
            int best = -1;
            foreach (Want w in wants)
            {
                ItemOrder.Rolled slot = plan.Slot(w.Part, w.Field);
                if (slot == null) continue;
                foreach (Lane lane in plan.Lanes)
                {
                    int i = lane.Slots.IndexOf(slot);
                    if (i >= 0 && (best < 0 || i < best)) best = i;
                }
            }
            return best;
        }

        // Return the enumerated word corresponding to a known seed.
        public static long WordOfFirst(Plan plan, List<Want> wants, uint seed)
        {
            int first = FirstWord(plan, wants);
            if (first < 0) return 1;
            return Mul(PowMod(A, first + 2), seed % M);
        }

        // Move backwards in the residue ring.
        public static long Back(long y, long steps)
        {
            long r = (y - 1 - steps) % (M - 1);
            if (r < 0) r += M - 1;
            return r + 1;
        }

        // =============================== SIEVE ===============================

        // Valid residues for one want. `null` means no word can produce the value.
        static Cond Make(Plan plan, List<Want> wants, Want w)
        {
            ItemOrder.Rolled slot = plan.Slot(w.Part, w.Field);
            if (slot == null) return null;
            int lane = -1, index = -1;
            for (int i = 0; i < plan.Lanes.Count && index < 0; i++)
            {
                index = plan.Lanes[i].Slots.IndexOf(slot);
                if (index >= 0) lane = i;
            }
            if (index < 0) return null;
            int scale = plan.Lanes[lane].Scale;

            Cond c = new Cond();
            c.W = w;
            c.Word = index;
            ItemViewFactory.RollJitter = slot.Jitter;

            // 1. A scaled multi-part field depends on several words; no sieve.
            if (SumTail(plan.Lanes[lane], index))
            {
                c.Any = true; c.Chance = 1; c.Range = "(verified after rolling)";
                return c;
            }

            // 2. Conversion maps the word by division, so valid words form an
            //    interval. Find its bounds through ItemRoll.ConvValue itself.
            if (ItemRoll.IsConversion(slot.Field))
            {
                long lo = ConvEdge(slot, w.Value, true);
                long hi = ConvEdge(slot, w.Value, false);
                if (lo < 0 || hi < lo) return null;
                c.Lo = lo; c.Hi = hi; c.Size = 0;
                c.Chance = (double)(hi - lo + 1) / M;
                c.Range = "[" + ItemRoll.ConvValue(slot.Value, slot.Jitter, 0) + ".." +
                          ItemRoll.ConvValue(slot.Value, slot.Jitter, M - 1) + "]";
                return c;
            }

            // 3. An upper range endpoint rolls a width and adds the rolled lower end.
            ItemOrder.Rolled low = LowOf(plan.Lanes[lane], slot);
            if (low != null)
            {
                long plo, phi;
                if (!ItemRoll.PairBounds(low.Value, slot.Value, out plo, out phi)) return null;
                long size = phi - plo + 1;
                c.Size = size;
                c.Ok = new bool[size];
                // A fixed lower endpoint gives one valid residue.
                long wantLow = long.MinValue;
                foreach (Want other in wants)
                    if (other.Part == w.Part && other.Field == low.Field) wantLow = other.Value;
                int good = 0;
                if (wantLow != long.MinValue)
                {
                    long r = w.Value - wantLow - plo;
                    if (r < 0 || r >= size) return null;
                    c.Ok[r] = true; good = 1;
                }
                else
                {
                    // With an unconstrained lower endpoint, accept any residue that
                    // can combine with a reachable lower value to produce the target.
                    foreach (long m in Reachable(low, plan.Lanes[lane].Scale))
                    {
                        long r = w.Value - m - plo;
                        if (r >= 0 && r < size && !c.Ok[r]) { c.Ok[r] = true; good++; }
                    }
                    if (good == 0) return null;
                }
                c.Chance = (double)good / size;
                c.Range = "width [" + plo + ".." + phi + "]";
                return c;
            }

            // 4. Ordinary fields use the residues enumerated by ItemRoll.Value.
            long lo2, hi2;
            if (!ItemRoll.Bounds(slot.Field, slot.Value, 0, out lo2, out hi2)) return null;
            long a = Math.Min(Math.Abs(lo2), Math.Abs(hi2)), b = Math.Max(Math.Abs(lo2), Math.Abs(hi2));
            long size2 = b - a + 1;
            if (size2 < 1) size2 = 1;
            c.Size = size2;
            c.Ok = new bool[size2];
            int hits = 0;
            long min = long.MaxValue, max = long.MinValue;
            for (long r = 0; r < size2; r++)
            {
                long got = ItemRoll.Value(slot.Field, slot.Value, scale, r);
                if (got < min) min = got;
                if (got > max) max = got;
                if (got == w.Value) { c.Ok[r] = true; hits++; }
            }
            if (hits == 0) return null;
            c.Chance = (double)hits / size2;
            c.Range = "[" + min + ".." + max + "]";
            return c;
        }

        // Values reachable by a field, used when a lower endpoint is unconstrained.
        static List<long> Reachable(ItemOrder.Rolled slot, int scale)
        {
            List<long> list = new List<long>();
            int keep = ItemViewFactory.RollJitter;
            ItemViewFactory.RollJitter = slot.Jitter;
            try
            {
                long lo, hi;
                if (!ItemRoll.Bounds(slot.Field, slot.Value, 0, out lo, out hi)) { list.Add((long)slot.Value); return list; }
                long a = Math.Min(Math.Abs(lo), Math.Abs(hi)), b = Math.Max(Math.Abs(lo), Math.Abs(hi));
                for (long r = 0; r <= b - a; r++)
                {
                    long got = ItemRoll.Value(slot.Field, slot.Value, scale, r);
                    if (!list.Contains(got)) list.Add(got);
                }
            }
            finally { ItemViewFactory.RollJitter = keep; }
            return list;
        }

        // Lower endpoint paired with an upper endpoint on the same item part.
        static ItemOrder.Rolled LowOf(Lane lane, ItemOrder.Rolled slot)
        {
            if (!slot.Field.EndsWith("Max", StringComparison.Ordinal)) return null;
            string min = slot.Field.Substring(0, slot.Field.Length - 3) + "Min";
            foreach (ItemOrder.Rolled o in lane.Slots)
                if (o.Part == slot.Part && o.Field == min) return o;
            return null;
        }

        // Last part of a scaled multi-part field; its value is the remaining sum and
        // cannot be determined from one stream word.
        static bool SumTail(Lane lane, int index)
        {
            if (lane.Scale == 0) return false;
            string f = lane.Slots[index].Field;
            int count = 0, last = -1;
            for (int i = 0; i < lane.Slots.Count; i++)
                if (lane.Slots[i].Field == f) { count++; last = i; }
            return count > 1 && last == index;
        }

        // Boundary word for an exact conversion value. Use ItemRoll.ConvValue as
        // the single source of truth for the monotonic search.
        static long ConvEdge(ItemOrder.Rolled slot, long want, bool lower)
        {
            long lo = 0, hi = M - 1;
            if (ItemRoll.ConvValue(slot.Value, slot.Jitter, lo) > want) return -1;
            if (ItemRoll.ConvValue(slot.Value, slot.Jitter, hi) < want) return -1;
            while (lo < hi)
            {
                long mid = lower ? (lo + hi) / 2 : (lo + hi + 1) / 2;
                long got = ItemRoll.ConvValue(slot.Value, slot.Jitter, mid);
                if (lower) { if (got >= want) hi = mid; else lo = mid + 1; }
                else { if (got <= want) lo = mid; else hi = mid - 1; }
            }
            return ItemRoll.ConvValue(slot.Value, slot.Jitter, lo) == want ? lo : -1;
        }

        // ============================== ARITHMETIC =============================

        static long Mul(long x, long y) { return (x * y) % M; }

        static long PowMod(long a, long e)
        {
            long r = 1, b = a % M;
            while (e > 0)
            {
                if ((e & 1) != 0) r = Mul(r, b);
                b = Mul(b, b);
                e >>= 1;
            }
            return r;
        }

        // Modular inverse from Fermat's little theorem: a^(M−2).
        static long InvA() { return PowMod(A, M - 2); }

        [ThreadStatic] static Random rng;
        static long Rand()
        {
            if (rng == null) rng = new Random(Guid.NewGuid().GetHashCode());
            return ((long)rng.Next() << 16) ^ rng.Next();
        }

        // ===================== FIELD RANGE ====================================

        // Range of the raw roll offered by the editor, not the clamped display range.
        public static bool Range(Plan plan, ItemOrder.Rolled slot, out long lo, out long hi)
        {
            lo = hi = 0;
            if (plan == null || slot == null) return false;
            int lane = -1;
            for (int i = 0; i < plan.Lanes.Count && lane < 0; i++)
                if (plan.Lanes[i].Slots.Contains(slot)) lane = i;
            if (lane < 0) return false;
            int scale = plan.Lanes[lane].Scale;
            int keep = ItemViewFactory.RollJitter;
            ItemViewFactory.RollJitter = slot.Jitter;
            try
            {
                if (ItemRoll.IsConversion(slot.Field))
                {
                    lo = ItemRoll.ConvValue(slot.Value, slot.Jitter, 0);
                    hi = ItemRoll.ConvValue(slot.Value, slot.Jitter, M - 1);
                    return true;
                }
                ItemOrder.Rolled low = LowOf(plan.Lanes[lane], slot);
                if (low != null)
                {
                    long plo, phi;
                    if (!ItemRoll.PairBounds(low.Value, slot.Value, out plo, out phi)) return false;
                    List<long> mins = Reachable(low, scale);
                    long a = long.MaxValue, b = long.MinValue;
                    foreach (long m in mins) { if (m < a) a = m; if (m > b) b = m; }
                    lo = a + plo; hi = b + phi;
                    return true;
                }
                long l2, h2;
                if (!ItemRoll.Bounds(slot.Field, slot.Value, 0, out l2, out h2)) return false;
                long x = Math.Min(Math.Abs(l2), Math.Abs(h2)), y = Math.Max(Math.Abs(l2), Math.Abs(h2));
                long min = long.MaxValue, max = long.MinValue;
                for (long r = 0; r <= y - x; r++)
                {
                    long got = ItemRoll.Value(slot.Field, slot.Value, scale, r);
                    if (got < min) min = got;
                    if (got > max) max = got;
                }
                lo = min; hi = max;
                return true;
            }
            finally { ItemViewFactory.RollJitter = keep; }
        }

        // Fraction of seeds for which this field alone produces the value, using
        // the same sieve as the search. Zero means the value is impossible.
        public static double ChanceOf(Plan plan, ItemOrder.Rolled slot, long value)
        {
            Want w = new Want();
            w.Part = slot.Part; w.Field = slot.Field; w.Value = value;
            int keep = ItemViewFactory.RollJitter;
            try
            {
                Cond c = Make(plan, new List<Want>(), w);
                return c == null ? 0 : c.Chance;
            }
            finally { ItemViewFactory.RollJitter = keep; }
        }

        // Field limit used for maximum-value searches. For signed reductions the
        // record is already negative, so the larger magnitude is still the maximum.
        public static long Best(Plan plan, ItemOrder.Rolled slot)
        {
            long lo, hi;
            if (!Range(plan, slot, out lo, out hi)) return slot == null ? 0 : slot.Got;
            return Math.Abs(hi) >= Math.Abs(lo) ? hi : lo;
        }
    }
}
