// Every riftgate the game has, read out of the world files.
//
// A save records only the gates its character found: block 6 is three lists of
// sixteen-byte identifiers, one list per difficulty. Those identifiers belong to
// objects placed in the world, so nothing in the record database knows them and
// the full set cannot be worked out from a save. It is in the world file:
//
//     resources/Levels.arc -> world001.map
//         … <uid:16> <int32 length> records/ui/riftgatemap/locations/riftgatea_devilscrossing.dbr …
//
// The identifier sits exactly twenty bytes before the path — sixteen bytes of
// identifier and the four-byte length in front of the string. The record it
// points at is class StaticTeleporter and carries the gate's name in
// "description", so every gate comes out with the name the game shows.
//
// The base game and each expansion ship their own world001.map, and later ones
// carry the earlier world too, so the four are read and pooled by identifier.
//
// Reading them takes about a minute and the largest unpacks to eight hundred
// megabytes, so the result is cached in portals.gdd next to the program the same
// way the item catalogue is.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Gdc.Gui
{
    public class Portal
    {
        public byte[] Uid;          // sixteen bytes, as the save stores them
        public string Hex;          // the same, as a dictionary key
        public string Dbr;          // records/ui/riftgatemap/locations/….dbr
        public string Name;         // what the game calls it, in the chosen language
        public string Source;       // which world file it came from
    }

    public class PortalTable
    {
        public readonly List<Portal> Portals = new List<Portal>();
        public string GameStamp = "";

        const string Marker = "records/ui/riftgatemap/locations/";
        const int UidBefore = 20;   // 16 bytes of identifier plus the length word

        public static string CachePath
        {
            get { return CacheFile.Of("portals"); }
        }

        static readonly string[] WorldArcs = {
            @"resources\Levels.arc", @"gdx1\resources\Levels.arc",
            @"gdx2\resources\Levels.arc", @"gdx3\resources\Levels.arc" };

        // The sizes and dates of the world files, so a patched game is noticed the
        // same way a patched database is.
        public static string StampOf(string gameDir)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string rel in WorldArcs)
            {
                string p = Path.Combine(gameDir ?? "", rel);
                if (!File.Exists(p)) continue;
                FileInfo fi = new FileInfo(p);
                sb.Append(rel).Append(':').Append(fi.Length).Append(':')
                  .Append(fi.LastWriteTimeUtc.Ticks).Append(';');
            }
            return sb.ToString();
        }

        // The layer that holds the gates, as the world file names it: a
        // length-prefixed "Riftgates", then the number of entries, then that many
        // of them one after another.
        //
        //     09 00 00 00  "Riftgates"  1E 00 00 00      ← nine letters, thirty gates
        //     <uid:16> <int32 len> <path> <transform>
        //     <uid:16> <int32 len> <path> <transform>    ← the transform varies in
        //     …                                            length, so the entries
        //                                                  are found by their path
        //
        // Reading the count matters: the same path appears elsewhere in the file
        // — on the map layer, among other things — where the bytes in front of it
        // are not an identifier at all. Taking exactly as many as the layer
        // declares leaves no room for a guess.
        static readonly byte[] LayerName = Encoding.ASCII.GetBytes("\x09\x00\x00\x00Riftgates");

        public static PortalTable Build(GameData game, string gameDir, Action<string> progress)
        {
            PortalTable t = new PortalTable();
            t.GameStamp = StampOf(gameDir);
            Dictionary<string, Portal> seen = new Dictionary<string, Portal>(StringComparer.Ordinal);
            byte[] marker = Encoding.ASCII.GetBytes(Marker);

            foreach (string rel in WorldArcs)
            {
                string p = Path.Combine(gameDir, rel);
                if (!File.Exists(p)) continue;
                if (progress != null) progress(rel);
                int remaining = 0;
                long done = -1;   // absolute offset of the last entry taken
                string world = rel;
                using (ArcIndex arc = new ArcIndex(p))
                {
                    // The overlap has to cover an identifier, the length word and
                    // the longest path, or an entry lying across a seam is lost.
                    arc.ForEachChunk("world001.map", 512, delegate (byte[] buf, int have, long at)
                    {
                        for (int i = UidBefore; i + marker.Length <= have; i++)
                        {
                            long abs = at + i;
                            if (abs <= done) continue;   // seen in the previous window

                            if (remaining <= 0)
                            {
                                if (i + LayerName.Length + 4 > have || buf[i] != LayerName[0]) continue;
                                bool head = true;
                                for (int k = 1; k < LayerName.Length && head; k++)
                                    if (buf[i + k] != LayerName[k]) head = false;
                                if (!head) continue;
                                int count = BitConverter.ToInt32(buf, i + LayerName.Length);
                                if (count > 0 && count < 10000)
                                {
                                    remaining = count;
                                    if (progress != null)
                                        progress("    " + Lang.T("skills.portals.layer", abs, count));
                                }
                                continue;
                            }

                            if (buf[i] != marker[0]) continue;
                            bool same = true;
                            for (int k = 1; k < marker.Length && same; k++) if (buf[i + k] != marker[k]) same = false;
                            if (!same) continue;

                            // The length word in front of the string says where the
                            // path ends; reading up to the first unprintable byte
                            // runs into whatever follows it.
                            int length = BitConverter.ToInt32(buf, i - 4);
                            if (length < marker.Length || length > 200 || i + length > have) continue;
                            string dbr = Encoding.ASCII.GetString(buf, i, length);

                            byte[] uid = new byte[16];
                            Buffer.BlockCopy(buf, i - UidBefore, uid, 0, 16);
                            remaining--;
                            done = abs;

                            string hex = BitConverter.ToString(uid).Replace("-", "");
                            if (seen.ContainsKey(hex)) continue;
                            Portal g = new Portal();
                            g.Uid = uid; g.Hex = hex; g.Dbr = dbr; g.Source = world;
                            g.Name = NameOf(game, dbr);
                            seen[hex] = g;
                            t.Portals.Add(g);
                        }
                    });
                }
            }

            t.Portals.Sort(delegate (Portal a, Portal b)
            { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
            return t;
        }

        static string NameOf(GameData game, string dbr)
        {
            if (game == null) return dbr;
            DbRecord rec = game.Record(dbr);
            string tag = rec == null ? null : rec.Get("description");
            string name = tag == null ? null : game.Tag(tag);
            if (!string.IsNullOrEmpty(name)) return GameData.Form(name, GameData.GenderOf(name));
            // A gate whose record or text is missing still has to be selectable,
            // so it keeps the file name rather than disappearing.
            int slash = dbr.LastIndexOf('/');
            return slash >= 0 ? dbr.Substring(slash + 1) : dbr;
        }

        // ------------------------------------------------------------- cache

        const string Header = "#gdc-portals\t1\t";

        public void Save(string path)
        {
            try
            {
                using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(true)))
                {
                    w.WriteLine(Header + GameStamp);
                    foreach (Portal g in Portals)
                        w.WriteLine(g.Hex + "\t" + g.Dbr + "\t" + g.Name + "\t" + g.Source);
                }
            }
            catch (Exception) { }
        }

        public static PortalTable Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                PortalTable t = new PortalTable();
                using (StreamReader r = new StreamReader(path, Encoding.UTF8))
                {
                    string head = r.ReadLine();
                    if (head == null || !head.StartsWith(Header, StringComparison.Ordinal)) return null;
                    t.GameStamp = head.Substring(Header.Length);
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        string[] f = line.Split('\t');
                        if (f.Length < 3 || f[0].Length != 32) continue;
                        Portal g = new Portal();
                        g.Hex = f[0]; g.Dbr = f[1]; g.Name = f[2];
                        g.Source = f.Length > 3 ? f[3] : "";
                        g.Uid = new byte[16];
                        for (int i = 0; i < 16; i++)
                            g.Uid[i] = byte.Parse(f[0].Substring(i * 2, 2), NumberStyles.HexNumber,
                                                  CultureInfo.InvariantCulture);
                        t.Portals.Add(g);
                    }
                }
                return t.Portals.Count > 0 ? t : null;
            }
            catch (Exception) { return null; }
        }

        public bool MatchesGame(string gameDir) { return GameStamp == StampOf(gameDir); }

        // Resolve names in the currently selected language. The cache stores the
        // record path, so names can be re-read with `NameOf` and re-sorted after
        // a language change.
        public void Rename(GameData game)
        {
            if (game == null) return;
            foreach (Portal g in Portals) g.Name = NameOf(game, g.Dbr);
            Portals.Sort(delegate (Portal a, Portal b)
            { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
        }
    }
}
