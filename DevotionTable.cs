// The devotion map, as the game stores it.
//
// The save knows only which stars a character has taken — a flat list of record
// paths. Which constellation a star belongs to, what that constellation is
// called, what affinity it gives and what it costs are all in the UI records:
//
//     records/ui/skills/devotion/constellations/constellationNN.dbr
//         constellationDisplayTag   tagDevotion_A21
//         affinityGivenName1/2      Order, Primordial      affinityGiven1/2   2, 3
//         affinityRequiredName1     Primordial             affinityRequired1  1
//         devotionButton1..N        records/ui/skills/devotion/tier1_21a.dbr
//         devotionLinks2..N         which button each one hangs off
//
// and each button record names the skill it stands for. The list of
// constellations is named by `devotion_mastertable.dbr` itself, so nothing has to
// be guessed; probing `constellation01`…`constellation99` is kept as a fallback,
// the same way the mastery trees keep theirs.
//
// THE SAME RECORD LIVES IN SEVERAL ARCHIVES WITH DIFFERENT POSITIONS. Every
// expansion moved the sky: the Crab's picture stands at 554,772 in the base game,
// 662,1050 in GDX1 and 632,1746 in GDX2, and the map itself grew from 6144×5120 to
// 6144×6144 to 7168×6144. `GameData.Record` already takes the last archive that
// has a record, which is the one the game uses — so nothing here may carry a
// position of its own.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Gdc.Gui
{
    // ONE STAR, WHERE THE GAME PUTS IT. The UI record of a button carries the
    // picture's top-left corner in map coordinates — which are counted from the
    // CENTRE of the sky and are freely negative — and the record it points at is
    // the bonus itself.
    public sealed class DevotionStar
    {
        public string Dbr = "";      // records/skills/devotion/tierN_MMx.dbr — the bonus
        public string UiDbr = "";    // records/ui/skills/devotion/tierN_MMx.dbr — the button
        public int X, Y;             // top-left of the 64×64 star picture, map coordinates
        public int Parent = -1;      // index of the star it hangs off, −1 for the root
        public int Index;            // its own place in the constellation's list
        // THE NUMBER THE CONSTELLATION'S RECORD GAVE THE BUTTON (`devotionButtonN`),
        // which is not the same as `Index` when a number is skipped. It is kept
        // because it is the ONLY thing that tells two stars apart when everything else
        // about them is equal: two stars can reference the same record word for word.
        public int Button;
        public bool Power;           // grants a celestial power instead of a passive bonus
        public Constellation Home;
    }

    public class Constellation
    {
        public string Path = "", Name = "";
        public readonly List<string> Stars = new List<string>();     // skill records
        public readonly List<string> Given = new List<string>();     // localized display text
        public readonly List<string> Required = new List<string>();  // localized display text
        public int RequiredTotal;                                    // for ordering

        // The same two lists as numbers, by the affinity's English name, for the
        // rules: the words above are for reading, these are for counting.
        public readonly Dictionary<string, int> Gives =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Needs =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // The stars, in the game's own order and with the game's own positions;
        // index-for-index the same list as `Stars` above.
        public readonly List<DevotionStar> Buttons = new List<DevotionStar>();

        // The constellation's picture and where it stands, and the three colours
        // the game tints it with: closed, open-and-empty, and taken.
        public string ArtPath;
        public int ArtX, ArtY;
        public readonly Tint Active = new Tint(), Available = new Tint(), Unavailable = new Tint();
    }

    // A colour multiplier as the game's records write it: four numbers from 0 to 1.
    // Kept as numbers rather than a `Color` so that reading the database still owes
    // nothing to the drawing code.
    public sealed class Tint
    {
        public float R = 1, G = 1, B = 1, A = 1;
    }

    public static class DevotionTable
    {
        // Built once per open database: 110 constellation records, each with a
        // handful of buttons, so reading is cheap enough not to need a cache file.
        public static List<Constellation> Of(GameData game)
        {
            List<Constellation> made = new List<Constellation>();
            foreach (Constellation c in Build(game))
                if (c.Stars.Count > 0) made.Add(c);
            // Cheapest first: that is the order the map opens up in, and it puts
            // the one-point starters at the top where they belong.
            made.Sort(delegate (Constellation a, Constellation b)
            {
                if (a.RequiredTotal != b.RequiredTotal) return a.RequiredTotal.CompareTo(b.RequiredTotal);
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            });
            return made;
        }

        // CONSTELLATION RECORDS THAT CARRY A PICTURE AND NO STARS. There is one,
        // `constellation87` — "Crossroads - Bitmap", the plate in the middle of the
        // sky — and the map has to draw it. Everyone else wants the list without
        // it: counted as a constellation it would read as "0 of 0, complete".
        public static List<Constellation> Backdrops(GameData game)
        {
            List<Constellation> made = new List<Constellation>();
            foreach (Constellation c in Build(game))
                if (c.Stars.Count == 0 && c.ArtPath != null) made.Add(c);
            return made;
        }

        // Everything the constellation records say, in the game's own order.
        //
        // The list of them is named by the master table (`devotionConstellation1…N`)
        // — 87 records in the base game, 101 after GDX1, 110 after GDX2 — so a
        // constellation added by a patch comes along on its own. Probing the
        // numbered paths is kept as the fallback: it found the same 110.
        // Held for the database it was read from: the window asks for the table
        // three or four times while it opens, and each pass is some nine hundred
        // record lookups. A different `GameData` throws it away, so a self-test
        // that opens the database again reads it again.
        static GameData builtFor;
        static List<Constellation> built;

        static List<Constellation> Build(GameData game)
        {
            if (game != null && game == builtFor && built != null) return built;
            List<Constellation> made = new List<Constellation>();
            if (game == null) return made;
            foreach (string path in Paths(game))
            {
                DbRecord rec = game.Record(path);
                if (rec == null) continue;
                Constellation c = new Constellation();
                int slash = path.LastIndexOf('/');
                c.Path = slash < 0 ? path : path.Substring(slash + 1).Replace(".dbr", "");
                c.Name = game.Tag(rec.Get("constellationDisplayTag"));
                if (string.IsNullOrEmpty(c.Name)) c.Name = rec.Get("FileDescription") ?? c.Path;
                c.Name = GameData.Form(c.Name, null);

                for (int n = 1; n <= 4; n++)
                {
                    int gives = rec.GetInt("affinityGiven" + n);
                    string giveName = rec.Get("affinityGivenName" + n);
                    string affinity = Affinity(game, giveName, gives);
                    if (affinity != null) { c.Given.Add(affinity); Put(c.Gives, giveName, gives); }
                    int need = rec.GetInt("affinityRequired" + n);
                    string needName = rec.Get("affinityRequiredName" + n);
                    string cost = Affinity(game, needName, need);
                    if (cost != null) { c.Required.Add(cost); c.RequiredTotal += need; Put(c.Needs, needName, need); }
                }

                // The buttons, with the number the record gave them: `devotionLinksN`
                // says which BUTTON NUMBER star N hangs off, and a button whose record
                // is missing must not shift the rest.
                List<int> numbers = new List<int>();
                for (int n = 1; n <= 24; n++)
                {
                    DbRecord button = game.Record(rec.Get("devotionButton" + n));
                    if (button == null) continue;
                    string skill = button.Get("skillName");
                    if (string.IsNullOrEmpty(skill) || c.Stars.Contains(skill)) continue;
                    DevotionStar star = new DevotionStar();
                    star.Dbr = skill;
                    star.UiDbr = rec.Get("devotionButton" + n);
                    star.X = button.GetInt("bitmapPositionX");
                    star.Y = button.GetInt("bitmapPositionY");
                    star.Index = c.Stars.Count;
                    star.Button = n;
                    star.Home = c;
                    DbRecord skillRec = game.Record(skill);
                    star.Power = skillRec != null &&
                                 !(skillRec.Class ?? "").StartsWith("Skill_Passive", StringComparison.OrdinalIgnoreCase);
                    c.Stars.Add(skill);
                    c.Buttons.Add(star);
                    numbers.Add(n);
                }
                // A STAR HAS AT MOST ONE PARENT: `devotionLinksN` is a single number
                // everywhere in the base — checked over all 110 constellations, values
                // 1…7, no lists — and the star with no line at all is the root.
                for (int i = 0; i < c.Buttons.Count; i++)
                {
                    int link = rec.GetInt("devotionLinks" + numbers[i]);
                    c.Buttons[i].Parent = link <= 0 ? -1 : numbers.IndexOf(link);
                }

                DbRecord art = game.Record(rec.Get("constellationBackground"));
                if (art != null)
                {
                    c.ArtPath = art.Get("bitmapName");
                    c.ArtX = art.GetInt("bitmapPositionX");
                    c.ArtY = art.GetInt("bitmapPositionY");
                }
                Colour(rec, "constellationActive", c.Active);
                Colour(rec, "constellationAvailable", c.Available);
                Colour(rec, "constellationUnavailable", c.Unavailable);
                made.Add(c);
            }
            builtFor = game;
            built = made;
            return made;
        }

        static void Put(Dictionary<string, int> into, string name, int count)
        {
            if (string.IsNullOrEmpty(name) || count <= 0) return;
            int had;
            into[name] = (into.TryGetValue(name, out had) ? had : 0) + count;
        }

        static void Colour(DbRecord rec, string prefix, Tint into)
        {
            into.R = Num(rec.Get(prefix + "Red"), 1);
            into.G = Num(rec.Get(prefix + "Green"), 1);
            into.B = Num(rec.Get(prefix + "Blue"), 1);
            into.A = Num(rec.Get(prefix + "Alpha"), 1);
        }

        static float Num(string text, float fallback)
        {
            float v;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        // Where the list of constellations comes from.
        public static List<string> Paths(GameData game)
        {
            List<string> made = new List<string>();
            DbRecord master = game == null ? null : game.Record(MasterTable);
            if (master != null)
                for (int i = 1; i <= 400; i++)
                {
                    string one = master.Get("devotionConstellation" + i);
                    if (!string.IsNullOrEmpty(one)) made.Add(one.Trim());
                }
            if (made.Count > 0) return made;
            for (int i = 1; i <= 200; i++)
            {
                string path = "records/ui/skills/devotion/constellations/constellation" + i.ToString("00") + ".dbr";
                if (game != null && game.Record(path) != null) made.Add(path);
            }
            return made;
        }

        public const string MasterTable = "records/ui/skills/devotion/devotion_mastertable.dbr";

        // The record names the affinity in English, and the game's own strings
        // are numbered — tagDevotionAffinity01..05. Five of them, in the order
        // the strings themselves are listed.
        static readonly string[] Affinities =
            { "Ascendant", "Chaos", "Eldritch", "Order", "Primordial" };

        static string Affinity(GameData game, string name, int count)
        {
            if (string.IsNullOrEmpty(name) || count <= 0) return null;
            string text = null;
            for (int i = 0; i < Affinities.Length; i++)
                if (string.Equals(Affinities[i], name, StringComparison.OrdinalIgnoreCase))
                    text = game.Tag("tagDevotionAffinity" + (i + 1).ToString("00"));
            if (string.IsNullOrEmpty(text)) text = name;
            return count + " " + GameData.Form(text, null);
        }
    }
}
