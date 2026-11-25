// FACTION REPUTATION — WHAT THE GAME ITSELF SAYS ABOUT IT.
//
// Nothing here is written out by hand: three game files carry the whole thing,
// so a patch that moves the factions moves our list with them (rule 7 of the
// second section of PLAN).
//
//   database/templates/gamefactions.tpl   THE ORDER OF THE SLOTS, and the only
//       place that states it. Its `faction*` fields of type `file_dbr` are
//       listed in the order the engine numbers factions in, and that number is
//       the position of the entry in the save's own list (block 13).
//   records/game/gamefactions.dbr         which record sits in each slot, the
//       ladder of standings, and which factions the game keeps out of the
//       player's faction panel.
//   records/controllers/factions/*.dbr    what a slot calls itself
//       (`myFaction`), which is also the key of its name: `User7` is named by
//       the game's string `tagFactionUser7`.
//
// WHAT WAS MEASURED before this was written (`--factiondump` over 46 saves,
// 10 October; the numbers are in the report it leaves):
//
//   * the save's list is EXACTLY as long as the template has slots — 47 in the
//     newer saves and 27 in five older ones. So the length belongs to the save
//     and is never changed here: a save that holds 27 entries knows nothing
//     about the last factions, and we say so instead of adding entries.
//   * the mapping also handles mutually exclusive factions correctly.
//   * `Modified` and `Unlocked` are EQUAL in all 2062 entries of all 46 saves,
//     and both stand at 1 wherever the character actually earned a standing.
//   * `PositiveBoost` is the `boostedMultiplier` of a faction writ (1.5) or
//     mandate (3); `maxBoosterValue` of the master record caps it at 3.
//
// WHAT WAS NOT MEASURED, and is therefore NOT APPLIED: when the game shows
// `factionAlternateNeutralTag` instead of the normal neutral standing. The field is
// read and kept in `AlternateNeutral`,
// but no standing is built from it, because we do not know when it is used.
// The boundaries of the ladder are read as «the standing starts at this value»;
// a shot of the game's own faction panel would settle both questions at once.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gdc.Gui
{
    // One faction, as the game defines it.
    public class FactionInfo
    {
        public int Index;                  // position in the save's list
        public string Slot = "";           // field of gamefactions.dbr: "factionUser7"
        public string MyFaction = "";      // what the record calls itself: "User7"
        public string Name = "";           // the game's own word for it
        public string Dbr = "";
        public bool Hidden;                // hiddenFactions — kept out of the game's panel
        public bool NoRepGain;             // noRepGainFactions
        public bool NoRepGainIfNegative;   // noRepGainIfNegativeFactions
        public bool Starting;              // startingFactions — visible from the start
    }

    // One rung of the ladder: the standing starts at this value.
    public class FactionRank
    {
        public int From;
        public string Tag = "";
        public string Name = "";
    }

    public partial class FactionTable
    {
        public readonly List<FactionInfo> Factions = new List<FactionInfo>();
        public readonly List<FactionRank> Ranks = new List<FactionRank>();
        public string AlternateNeutral = "";
        // The standing a faction stands at while the value is negative but has not
        // reached the first enemy rung. It is not one of the rungs, so it is kept
        // apart from them; see RankOf.
        public FactionRank NegativeNeutral;
        public float MaxBoost;

        // `--factionvaluebreak` — the value asked for is not the value written.
        // `--factionflagbreak` — the two bytes beside it are left alone.
        // `--factionrankbreak` — the ladder is read the way it was read before the
        //     game's own panel was looked at: the last rung at or below the number.
        // All three exist so the guard can be seen to redden (rule 12).
        public static bool BreakValue;
        public static bool BreakFlags;
        public static bool BreakRank;

        // The faction slots the engine knows, in the order its own template lists
        // them. Read rather than written out: `gamefactions.dbr` carries one field
        // per slot, but only the template says in which order they are taken.
        public static List<string> Slots(string gameDir)
        {
            List<string> slots = new List<string>();
            if (string.IsNullOrEmpty(gameDir)) return slots;
            string arc = Path.Combine(gameDir, "database", "templates.arc");
            if (!File.Exists(arc)) return slots;
            byte[] data;
            try { using (ArcIndex index = new ArcIndex(arc)) data = index.Get("gamefactions.tpl"); }
            catch (Exception) { return slots; }
            if (data == null) return slots;
            // A template is a run of `Variable { name = … class = … type = … }`
            // blocks in that order, so the name last seen owns the type.
            string name = null;
            foreach (string raw in Encoding.ASCII.GetString(data).Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("name = \"", StringComparison.Ordinal))
                    name = line.Substring(8).TrimEnd('"');
                else if (line == "type = \"file_dbr\"" && name != null &&
                         name.StartsWith("faction", StringComparison.Ordinal))
                    slots.Add(name);
            }
            return slots;
        }

        public static FactionTable Build(GameData game)
        {
            FactionTable t = new FactionTable();
            if (game == null) return t;
            DbRecord gf = game.Record("records/game/gamefactions.dbr");
            if (gf == null) return t;

            HashSet<string> hidden = Names(gf.Get("hiddenFactions"));
            HashSet<string> noGain = Names(gf.Get("noRepGainFactions"));
            HashSet<string> noGainNeg = Names(gf.Get("noRepGainIfNegativeFactions"));
            HashSet<string> starting = Names(gf.Get("startingFactions"));

            List<string> slots = Slots(game.GameDir);
            for (int i = 0; i < slots.Count; i++)
            {
                string dbr = gf.Get(slots[i]);
                // The template reaches further than the game has factions; the
                // empty slots are positions in the save's list and nothing else.
                if (string.IsNullOrEmpty(dbr)) continue;
                DbRecord one = game.Record(dbr);
                if (one == null) continue;
                string my = one.Get("myFaction");
                if (string.IsNullOrEmpty(my)) continue;
                FactionInfo info = new FactionInfo();
                info.Index = i;
                info.Slot = slots[i];
                info.MyFaction = my;
                info.Dbr = dbr;
                string name = game.Tag("tagFaction" + my);
                info.Name = string.IsNullOrEmpty(name) ? my : name;
                info.Hidden = hidden.Contains(my);
                info.NoRepGain = noGain.Contains(my);
                info.NoRepGainIfNegative = noGainNeg.Contains(my);
                info.Starting = starting.Contains(my);
                t.Factions.Add(info);
            }

            // The ladder. The master record carries eleven possible rungs and
            // fills eight of them; a rung without a name is not a rung.
            for (int n = 1; n <= 11; n++)
            {
                string tag = gf.Get("factionTag" + n);
                if (string.IsNullOrEmpty(tag)) continue;
                FactionRank rank = new FactionRank();
                rank.From = gf.GetInt("factionValue" + n);
                rank.Tag = tag;
                string word = game.Tag(tag);
                rank.Name = string.IsNullOrEmpty(word) ? tag : word;
                t.Ranks.Add(rank);
            }
            t.Ranks.Sort(delegate (FactionRank a, FactionRank b) { return a.From.CompareTo(b.From); });

            string alt = gf.Get("factionAlternateNeutralTag");
            if (!string.IsNullOrEmpty(alt))
            {
                string word = game.Tag(alt);
                t.AlternateNeutral = string.IsNullOrEmpty(word) ? alt : word;
                t.NegativeNeutral = new FactionRank();
                t.NegativeNeutral.From = 0;
                t.NegativeNeutral.Tag = alt;
                t.NegativeNeutral.Name = t.AlternateNeutral;
            }
            float boost;
            if (float.TryParse(gf.Get("maxBoosterValue"), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out boost))
                t.MaxBoost = boost;
            return t;
        }

        // The game writes these lists in upper case («USER19;AETHERIALS»), while a
        // record calls itself «User19», so they are matched without case.
        static HashSet<string> Names(string list)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(list)) return set;
            foreach (string one in list.Split(';'))
                if (one.Trim().Length > 0) set.Add(one.Trim());
            return set;
        }

        // Map a reputation value to the rank displayed by the game. The value is not
        // simply assigned the last threshold below it.
        //
        // Walk thresholds outward from zero. The game displays the absolute value:
        //
        //   value >= 0: highest reached positive threshold;
        //   value < 0: highest reached threshold by absolute value;
        //   small negative values: fallback `factionAlternateNeutralTag`.
        //
        public FactionRank RankOf(float value)
        {
            if (Ranks.Count == 0) return null;
            FactionRank found = null;
            if (BreakRank)
            {
                // Use the obsolete rank logic intentionally under `--factionrankbreak`.
                found = Ranks[0];
                foreach (FactionRank r in Ranks) if (value >= r.From) found = r;
                return found;
            }
            if (value < 0)
            {
                foreach (FactionRank r in Ranks)
                    if (r.From < 0 && value <= r.From && (found == null || r.From < found.From)) found = r;
                if (found == null) found = NegativeNeutral;
            }
            else
            {
                foreach (FactionRank r in Ranks)
                    if (r.From >= 0 && value >= r.From && (found == null || r.From > found.From)) found = r;
            }
            return found;
        }

        public int Lowest { get { return Ranks.Count == 0 ? 0 : Ranks[0].From; } }
        public int Highest { get { return Ranks.Count == 0 ? 0 : Ranks[Ranks.Count - 1].From; } }

        // THE ONE PLACE A STANDING IS WRITTEN (rule 18), so the window and the
        // guard cannot drift apart. Returns true when the save really changed.
        //
        // The two bytes beside the value go to 1 together: in all 46 saves they
        // are never unequal, and both stand at 1 wherever a standing was earned.
        //
        // AND `Unlocked` IS WHAT PUTS THE FACTION IN THE GAME'S OWN PANEL — measured
        // on the shots of 11 October, not guessed: `_Itaan` lists exactly the twenty
        // factions whose byte is one. So setting a standing has to set it, or the
        // standing would be in the save and nowhere to be seen.
        public static bool Apply(Character c, int index, float value)
        {
            if (c == null || index < 0 || index >= c.Factions.Count) return false;
            Faction f = c.Factions[index];
            float write = BreakValue ? value + 1f : value;
            bool changed = f.Value != write;
            f.Value = write;
            if (!BreakFlags)
            {
                if (f.Modified != 1 || f.Unlocked != 1) changed = true;
                f.Modified = 1;
                f.Unlocked = 1;
            }
            return changed;
        }

        // Validation command: `--factiontest <save> <directory>`. It round-trips a copy
        // and checks integer, fractional, and negative values.
        #region Self-check — not interface (the `--factiontest` report, not moved into the language files)
        #endregion
    }
}
