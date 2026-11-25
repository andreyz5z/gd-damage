// Completion bonuses are defined by one table named in the relic's
// `bonusTableName`. Its `randomizerName1..N` values are the complete pool;
// unlike ascension, no item-class mapping is needed.
//
// The bonus is rolled from the item's seed using its own
// `lootRandomizerJitter`. The number of displayed rows comes from the selected
// record. Tables are either numeric or skill-based, and
// `artifactClassification` controls the crafting tier.
//
// Relics without `bonusTableName` have no completion bonus and produce an empty
// list.

using System;
using System.Collections.Generic;
using System.IO;

namespace Gdc.Gui
{
    public static class RelicBonus
    {
        public const string TableDir = "lootaffixes/completionrelics";

        // Whether the item is a relic. This is the same condition used by
        // `RelicBonusOf`; ordinary items may also contain the field.
        public static bool IsRelic(DbRecord rec)
        {
            return rec != null &&
                   string.Equals(rec.Class, "ItemArtifact", StringComparison.OrdinalIgnoreCase);
        }

        // Selection table for this relic, or null.
        public static string TableOf(DbRecord relic)
        {
            if (relic == null) return null;
            string t = relic.Get("bonusTableName");
            return string.IsNullOrEmpty(t) ? null : t;
        }

        public class Choice
        {
            public string Path;
            public string Table;       // source selection table
            public bool Suggested;     // table assigned to this relic
        }

        // Bonus candidates. With `everything = false`, use only this relic's
        // table; true exposes all completion tables for diagnostics.
        public static List<Choice> Choices(GameData game, DbRecord relic, bool everything)
        {
            List<Choice> found = new List<Choice>();
            if (game == null) return found;
            string mine = TableOf(relic);
            string mineShort = Short(mine);

            Dictionary<string, Choice> seen = new Dictionary<string, Choice>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in game.PathsContaining(TableDir))
            {
                DbRecord table = game.Record(path);
                if (table == null) return found;
                if (!string.Equals(table.Class, "LootRandomizerTable", StringComparison.OrdinalIgnoreCase)) continue;
                bool ours = mine != null && string.Equals(path, mine, StringComparison.OrdinalIgnoreCase);
                if (!ours && !everything) continue;
                string shortName = Short(path);
                for (int i = 1; i <= 64; i++)
                {
                    string one = table.Get("randomizerName" + i);
                    if (string.IsNullOrEmpty(one)) continue;
                    Choice had;
                    if (seen.TryGetValue(one, out had))
                    {
                        // A record may occur in multiple tables; the relic's own
                        // table takes precedence.
                        if (ours) { had.Table = shortName; had.Suggested = true; }
                        continue;
                    }
                    Choice c = new Choice();
                    c.Path = one; c.Table = shortName; c.Suggested = ours;
                    seen[one] = c;
                    found.Add(c);
                }
            }
            found.Sort(delegate (Choice a, Choice b)
            {
                if (a.Suggested != b.Suggested) return a.Suggested ? -1 : 1;
                int c = string.Compare(a.Table, b.Table, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
            return found;
        }

        // «…/completionbonus_a003_tier3offense.dbr» → «a003_tier3offense».
        public static string Short(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string name = Path.GetFileNameWithoutExtension(path);
            return name.StartsWith("completionbonus_", StringComparison.OrdinalIgnoreCase)
                 ? name.Substring("completionbonus_".Length) : name;
        }

        // List row using the rolled values for this item, not the record values.
        // Completion bonuses have no game display name, so show what they grant.
        public static string Describe(GameData game, string bonus, uint seed)
        {
            DbRecord r = game == null ? null : game.Record(bonus);
            if (r == null) return bonus;
            List<string> what = new List<string>();
            foreach (TipLine l in ItemViewFactory.DescribeRolled(game, r, seed))
                if (!string.IsNullOrEmpty(l.Text) && what.Count < 4) what.Add(l.Text.Trim());
            return what.Count > 0 ? string.Join(";   ", what.ToArray())
                                  : Path.GetFileNameWithoutExtension(bonus);
        }
    }
}
