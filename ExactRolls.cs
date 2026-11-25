// Rolled equipment values computed from the seeds in a character's save.
// Keep fields whose roll position is uncertain on their normal record ranges.

using System;
using System.Collections.Generic;

namespace Gdc.Gui
{
    public partial class ExactRolls
    {
        // One equipped item. Identical names can have different seeds and rolls.
        public class Entry
        {
            public string Name = "", Slot = "";
            public string EnglishName;

            public bool Matches(string name)
            {
                return name != null &&
                    (string.Equals(Name, name, StringComparison.CurrentCultureIgnoreCase) ||
                     string.Equals(EnglishName, name, StringComparison.CurrentCultureIgnoreCase));
            }
            // Rolled values grouped by item part. A rare item has base, prefix,
            // and suffix records, while the tooltip merges their values. The table
            // identifies the owning part so only that record value is replaced.
            // Without this the whole item had to be affix-free to be checkable.
            public readonly Dictionary<int, Dictionary<string, double>> ByPart =
                new Dictionary<int, Dictionary<string, double>>();

            public Dictionary<string, double> Part(int index)
            {
                Dictionary<string, double> got;
                return ByPart.TryGetValue(index, out got) ? got : null;
            }

            // The base record, which is what every table held before parts existed.
            public Dictionary<string, double> Fields
            {
                get
                {
                    Dictionary<string, double> got = Part(PartBase);
                    if (got == null) { got = new Dictionary<string, double>(StringComparer.Ordinal); ByPart[PartBase] = got; }
                    return got;
                }
            }
        }

        // The parts of a worn item, in the order CharacterTotals walks them.
        public const int PartBase = 0, PartPrefix = 1, PartSuffix = 2, PartRelic = 3,
                         PartRelicBonus = 4, PartAugment = 5, PartModifier = 6, PartAscension = 7;

        readonly List<Entry> entries = new List<Entry>();

        // Replace rolled values with values derived from the item seed.
        //
        // Stand tables contain tooltip transcriptions. Seed-based rolling makes
        // equipment values exact even when no screenshot is available.
        //
        // Only calculated rolled values are replaced; stand settings and expected
        // panel values remain unchanged. This turns the table into a full check of
        // the seed-roll calculation.
        //
        // Item traversal must match `CharacterTotals.Of` and `DamageModel`: worn
        // gear, first weapon set, then second set. Duplicate names use the next
        // ordinal so values are assigned to the correct item.
        public void ReplaceWithSeed(GameData game, Character c) { ReplaceWithSeed(game, c, null); }

        // If supplied, `log` receives each mismatch between the transcribed table
        // and the calculation. This isolates field-order errors and table typos.
        public void ReplaceWithSeed(GameData game, Character c, System.Text.StringBuilder log)
        {
            if (game == null || c == null) return;
            // Keep table values for fields that are not modeled, such as armor,
            // conversion, resistance caps, base weapon damage, and durations.
            // Replace only fields that the calculation can reproduce.
            List<Entry> old = new List<Entry>(entries);
            int same = 0, diff = 0, added = 0;
            List<Item> worn = new List<Item>();
            // The alternate weapon set is excluded; only the equipped set affects
            // the calculation.
            worn.AddRange(c.Equipment); worn.AddRange(c.Weapons1);
            Dictionary<string, int> seen =
                new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            foreach (Item it in worn)
            {
                if (string.IsNullOrEmpty(it.BaseName)) continue;
                // Stand-supplied items referenced by `#wear` and `#weapon` have no
                // saved seed, so their values must remain from the table. A real
                // equipped zero-seed item is still rolled: `ItemRoll.Next` maps zero
                // to the lower end of each range. Skip only items without a seed.
                if (it.FromStand && it.Seed == 0) continue;
                string name = ItemViewFactory.Build(game, it, false).Name;
                if (string.IsNullOrEmpty(name)) name = it.BaseName;
                int ordinal = seen.ContainsKey(name) ? seen[name] : 0;
                seen[name] = ordinal + 1;

                // Use the table entry when present and overlay calculated fields.
                // If absent, create an entry so the item can be calculated from seed.
                Entry e = null;
                int look = 0;
                foreach (Entry o in old)
                    if (o.Matches(name) &&
                        look++ == ordinal) { e = o; break; }
                if (e == null) { e = new Entry(); e.Name = name; entries.Add(e); }

                DbRecord rec = game.Record(it.BaseName);
                List<ItemOrder.Rolled> walk = ItemOrder.RollWhole(game, it, rec);
                // Fields with an inferred stream position are not substituted:
                // the calculated value may be wrong. Keep the record value and let
                // the tooltip mark the field as uncertain.
                HashSet<string> doubt = new HashSet<string>(ItemOrder.Doubts(walk), StringComparer.Ordinal);
                foreach (ItemOrder.Rolled r in walk)
                {
                    if (doubt.Contains(r.Field)) continue;
                    // `Part` only reads and returns null for a missing part; create
                    // the dictionary entry here when needed.
                    Dictionary<string, double> into;
                    if (!e.ByPart.TryGetValue(r.Part, out into))
                    { into = new Dictionary<string, double>(StringComparer.Ordinal); e.ByPart[r.Part] = into; }
                    // Before replacement, compare the calculation with the table
                    // and report each mismatch.
                    double was;
                    if (log != null && into.TryGetValue(r.Field, out was) &&
                        Math.Abs(was - r.Got) > 0.0001)
                    {
                        diff++;
                        log.AppendLine("  MISMATCH  " + name + " (part " + r.Part +
                                       ")  " + r.Field + ":  table " + was + ", calculated " + r.Got);
                    }
                    else if (log != null && into.ContainsKey(r.Field)) same++;
                    else if (log != null) added++;
                    into[r.Field] = UseExact ? r.Exact : r.Got;
                }

                // The relic completion bonus uses its own stream from the same seed,
                // separate from base, prefix, and suffix rolls. Its range is taken
                // from the record's `lootRandomizerJitter` by `RollAll`.
                string bonusName = ItemViewFactory.RelicBonusOf(game, it);
                DbRecord bonusRec = string.IsNullOrEmpty(bonusName) ? null : game.Record(bonusName);
                if (bonusRec != null)
                {
                    Dictionary<string, double> into;
                    if (!e.ByPart.TryGetValue(4, out into))
                    { into = new Dictionary<string, double>(StringComparer.Ordinal); e.ByPart[4] = into; }
                    foreach (KeyValuePair<string, long> kv in ItemOrder.RollAll(bonusRec, it.Seed, 0))
                    {
                        double was;
                        if (log != null && into.TryGetValue(kv.Key, out was) &&
                            Math.Abs(was - kv.Value) > 0.0001)
                        {
                            diff++;
                            log.AppendLine("  MISMATCH  " + name + " (bonus)  " + kv.Key +
                                           ":  table " + was + ", calculated " + kv.Value);
                        }
                        else if (log != null && into.ContainsKey(kv.Key)) same++;
                        else if (log != null) added++;
                        into[kv.Key] = kv.Value;
                    }
                }
            }
            if (log != null)
                log.AppendLine("  table vs. calculation: matched " + same + ", differed " + diff +
                               ", additional fields calculated " + added);
        }

        // Use fractional conversion instead of the rounded tooltip value.
        public static bool UseExact = true;

        // Optionally keep the item scale fractional as well. The candidate formula
        // is `trunc(j x (100+scale)/100)`; ordinary rolls remain integral.
        public static bool ExactScale;

        // The ordinal-th item of that name, counting from zero. The worn items are
        // walked in the same order every time, so the first entry of a name in the
        // file goes to the first such item worn. For two rings that does not
        // matter — they are added together and neither converts anything. For two
        // weapons it does: only Weapons1[0] is swung, and its own conversion is
        // read off it. Which snapshot is the main hand cannot be told from the
        // snapshots; the main-hand order is settled by the game's own attack panel.
        public Dictionary<string, double> For(string itemName, int ordinal)
        { return For(itemName, ordinal, PartBase); }

        public Dictionary<string, double> For(string itemName, int ordinal, int part)
        {
            Entry e = EntryFor(itemName, ordinal);
            return e == null ? null : e.Part(part);
        }

        Entry EntryFor(string itemName, int ordinal)
        {
            if (itemName == null) return null;
            int seen = 0;
            foreach (Entry e in entries)
            {
                if (!e.Matches(itemName)) continue;
                if (seen++ == ordinal) return e;
            }
            return null;
        }

    }
}
