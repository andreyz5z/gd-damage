using System;
using System.Collections.Generic;

namespace Gdc.Gui
{
    // Roll one concrete hit.
    //
    // Each min-max damage line has its own roll and is rolled again for every hit.
    // The panel exposes ranges and averages; the timeline reuses the same roll
    // object for concrete events.
    //
    // Cache the fraction, not the value. A line may be requested twice while the
    // second hand is subtracted, and the two callers can use different bounds.
    public sealed class HitRoll
    {
        readonly Random rng;
        // Key: source|field. Each item part has its own source name and each
        // displayed line has one field, so the pair identifies one roll.
        readonly Dictionary<string, double> share =
            new Dictionary<string, double>(StringComparer.Ordinal);

        public HitRoll(int seed) { rng = new Random(seed); }

        // Number of rolls since the run began, for logs and checks.
        public int Rolls { get; private set; }

        // Start a new hit; subsequent requests produce new values. Called before
        // every hit and separately for each hand.
        public void NextHit() { share.Clear(); }

        // This line's fraction of its range is stable within one hit.
        public double Share(string source, string field)
        {
            string key = source + "|" + field;
            double t;
            if (!share.TryGetValue(key, out t))
            {
                t = rng.NextDouble();
                share[key] = t;
                Rolls++;
            }
            return t;
        }

        // "ONE OF" (XOR, MECHANICS 85): a group of record fields is rolled with ONE die per
        // strike — the share is the line's own. `fresh` means the die was just thrown rather
        // than taken from this same strike; the count of how often the group was rolled uses it.
        //
        // The sub-key below is an INTERNAL name and nothing else: `Share` draws from the stream
        // in call order and only caches by the key, so the text never reaches a number. All of
        // its spellings were put into English together (12 October) and the timeline came out
        // identical, down to the dice count.
        public double XorShare(string group, out bool fresh)
        {
            fresh = !share.ContainsKey(group + "|" + OneOf);
            return Share(group, OneOf);
        }

        public const string OneOf = "oneOf", Nothing = "nothing", Chance = "chance";

        // What the "one of" groups rolled over a run: the key is "record|field|field's chance".
        // The run's guard compares every field's share with its chance — each one must come up.
        public readonly SortedDictionary<string, int> XorCounts =
            new SortedDictionary<string, int>(StringComparer.Ordinal);

        public void CountXor(string name, string root, double chance)
        {
            string key = name + "|" + (root ?? Nothing) + "|" +
                         chance.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            int n;
            XorCounts.TryGetValue(key, out n);
            XorCounts[key] = n + 1;
        }

        // Whether a chance-based sum component landed on this hit. Use one roll
        // per key and hit; `AlwaysLands` (`--chancealways`) forces a hit for tests.
        public static bool AlwaysLands;
        public readonly SortedDictionary<string, double[]> ChanceCounts =
            new SortedDictionary<string, double[]>(StringComparer.Ordinal);

        public bool Landed(string key, double chance)
        {
            bool fresh = !share.ContainsKey(key + "|" + Chance);
            // Roll even with `AlwaysLands` so repeated requests share one value.
            bool hit = Share(key, Chance) * 100 < chance || AlwaysLands;
            if (fresh)
            {
                double[] seen;
                if (!ChanceCounts.TryGetValue(key, out seen)) ChanceCounts[key] = seen = new double[3];
                seen[0]++;
                if (hit) seen[1]++;
                seen[2] = chance;
            }
            return hit;
        }

        // Value for a line with these bounds. A fixed line has nothing to roll.
        public double Pick(string source, string field, double lo, double hi)
        {
            if (Math.Abs(hi - lo) < 0.0001) return lo;
            return lo + Share(source, field) * (hi - lo);
        }
    }
}
