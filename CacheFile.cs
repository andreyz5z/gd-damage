// WHERE OUR IMPORTED TABLES LIE, AND WHY THEY ARE NO LONGER CALLED `.tsv`.
//
// Six tables are built from the game's own archives and from the GrimTools site
// and kept beside the program: the item catalogue, the affixes, the enemies, the
// hands, the riftgates and the GrimTools signatures. They are ours, they are
// rebuilt whenever the game is patched, and nobody is meant to open them by hand.
//
// Called `.tsv` they opened in a spreadsheet on a double click — and a spreadsheet
// is only too glad to save a seven-megabyte table back in its own way, which would
// quietly destroy it. `.gdd` belongs to no program, so a double click does nothing.
// The content has not changed: still tabs and lines, still read by `cat` and
// `grep`, still written and parsed by exactly the same code.
//
// ONE PLACE FOR THE NAME. Each table named its own file before, in six different
// files; the extension lives here now, so there is one line to change and no sixth
// place to forget (rule 18 of the second section of PLAN).
//
// AN OLD FILE IS NOT THROWN AWAY. The first call renames `<name>.tsv` into
// `<name>.gdd`: rebuilding the catalogue costs minutes, and the GrimTools table
// cannot be rebuilt at all without going back to the site. If the rename fails —
// the folder is read-only, or another run holds the file — the old file is used
// where it lies rather than lost.

using System;
using System.IO;

namespace Gdc.Gui
{
    public static class CacheFile
    {
        public const string Extension = ".gdd";

        // `bare` is the name without an extension: "catalog", "enemies", …
        public static string Of(string bare)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string now = Path.Combine(dir, bare + Extension);
            if (File.Exists(now)) return now;
            string was = Path.Combine(dir, bare + ".tsv");
            if (!File.Exists(was)) return now;
            try { File.Move(was, now); }
            catch (Exception) { return was; }
            return now;
        }
    }
}
