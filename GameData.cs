// Lazy access to the installed game's data: item records out of the .arz
// databases and item icons out of the Items.arc archives.
//
// The record and file tables are small enough to keep in memory; the payloads
// are seeked to when needed. Nothing stays open between reads: a held handle
// stops Steam from patching the game — its update fails with "file is locked"
// and it retries the whole thing on the next launch. So a read opens the file,
// takes what it needs and closes it, and it opens it in a way that would not
// stand in the way even if it were held: sharing set to allow writers and
// deleters both.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Gdc.Gui
{
    // Record table of one .arz, without decompressing anything up front.
    public class ArzIndex : IDisposable
    {
        class Slot { public int Offset, Compressed, Decompressed; public string Class; }

        readonly string path;
        readonly string[] strings;
        readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);

        public int Count { get { return slots.Count; } }

        // Every record path this database holds, for the callers that need to
        // sweep rather than look one up.
        public List<string> Names { get { return new List<string>(slots.Keys); } }

        public ArzIndex(string path)
        {
            this.path = path;
            byte[] head, st, rt;
            using (FileStream fs = GameFile.Open(path))
            {
                head = GameFile.Read(fs, 0, 24);
                int recordStart0 = BitConverter.ToInt32(head, 4);
                int stringStart0 = BitConverter.ToInt32(head, 16);
                int stringSize0 = BitConverter.ToInt32(head, 20);
                st = GameFile.Read(fs, stringStart0, stringSize0);
                rt = GameFile.Read(fs, recordStart0,
                                   fs.Length - recordStart0 < 0 ? 0 : stringStart0 - recordStart0);
            }
            int recordCount = BitConverter.ToInt32(head, 12);

            int p = 0;
            int stringCount = BitConverter.ToInt32(st, p); p += 4;
            strings = new string[stringCount];
            for (int i = 0; i < stringCount; i++)
            {
                int len = BitConverter.ToInt32(st, p); p += 4;
                strings[i] = Encoding.ASCII.GetString(st, p, len); p += len;
            }

            p = 0;
            for (int i = 0; i < recordCount && p + 12 < rt.Length; i++)
            {
                int nameIndex = BitConverter.ToInt32(rt, p); p += 4;
                int classLength = BitConverter.ToInt32(rt, p); p += 4;
                string cls = Encoding.ASCII.GetString(rt, p, classLength); p += classLength;
                Slot s = new Slot();
                s.Class = cls;
                s.Offset = BitConverter.ToInt32(rt, p); p += 4;
                s.Compressed = BitConverter.ToInt32(rt, p); p += 4;
                s.Decompressed = BitConverter.ToInt32(rt, p); p += 4;
                p += 8; // file time
                slots[strings[nameIndex]] = s;
            }
        }

        public bool Has(string recordPath) { return slots.ContainsKey(recordPath); }

        public DbRecord Get(string recordPath)
        {
            Slot s;
            if (!slots.TryGetValue(recordPath, out s)) return null;
            byte[] packed;
            using (FileStream fs = GameFile.Open(path))
                packed = GameFile.Read(fs, 24 + s.Offset, s.Compressed);
            if (packed == null) return null;
            byte[] raw = Lz4.Decode(packed, 0, s.Compressed, s.Decompressed);

            DbRecord rec = new DbRecord();
            rec.Path = recordPath;
            rec.Class = s.Class;
            int q = 0;
            while (q + 8 <= raw.Length)
            {
                int type = BitConverter.ToUInt16(raw, q);
                int count = BitConverter.ToUInt16(raw, q + 2);
                int field = BitConverter.ToInt32(raw, q + 4);
                q += 8;
                StringBuilder sb = new StringBuilder();
                for (int k = 0; k < count; k++)
                {
                    if (k > 0) sb.Append(';');
                    if (type == 1) sb.Append(BitConverter.ToSingle(raw, q).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    else if (type == 2) sb.Append(strings[BitConverter.ToInt32(raw, q)]);
                    else sb.Append(BitConverter.ToInt32(raw, q));
                    q += 4;
                }
                rec.Fields[strings[field]] = sb.ToString();
            }
            return rec;
        }

        // Nothing is held, so there is nothing to let go of. Kept so callers can
        // go on treating this as a resource.
        public void Dispose() { }
    }

    // Opening a file of the installed game. Two rules, both about staying out of
    // the way of the game's own updater: never hold the handle, and share it with
    // whoever wants to write or delete the file while we read.
    static class GameFile
    {
        public static FileStream Open(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read,
                                  FileShare.ReadWrite | FileShare.Delete);
        }

        public static byte[] Read(FileStream fs, long offset, long length)
        {
            if (length <= 0 || offset < 0) return new byte[0];
            byte[] b = new byte[length];
            fs.Position = offset;
            int got = 0;
            while (got < b.Length)
            {
                int n = fs.Read(b, got, b.Length - got);
                if (n <= 0) break;
                got += n;
            }
            return b;
        }
    }

    // File table of one .arc, again without unpacking anything up front.
    public class ArcIndex : IDisposable
    {
        class Slot { public int Size, Parts, FirstPart; }

        readonly string path;
        readonly byte[] partTable;
        readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);

        public ArcIndex(string path)
        {
            this.path = path;
            byte[] stringTable, entryTable;
            int entryCount;
            using (FileStream fs = GameFile.Open(path))
            {
                byte[] head = GameFile.Read(fs, 0, 28);
                entryCount = BitConverter.ToInt32(head, 8);
                int partCount = BitConverter.ToInt32(head, 12);
                int stringSize = BitConverter.ToInt32(head, 20);
                int partOffset = BitConverter.ToInt32(head, 24);

                // The three tables sit back to back from partOffset on.
                partTable = GameFile.Read(fs, partOffset, (long)partCount * 12);
                stringTable = GameFile.Read(fs, partOffset + (long)partCount * 12, stringSize);
                entryTable = GameFile.Read(fs, partOffset + (long)partCount * 12 + stringSize,
                                           (long)entryCount * 44);
            }

            for (int i = 0; i < entryCount; i++)
            {
                int e = i * 44;
                Slot s = new Slot();
                s.Size = BitConverter.ToInt32(entryTable, e + 12);
                s.Parts = BitConverter.ToInt32(entryTable, e + 28);
                s.FirstPart = BitConverter.ToInt32(entryTable, e + 32);
                int nameLength = BitConverter.ToInt32(entryTable, e + 36);
                int nameOffset = BitConverter.ToInt32(entryTable, e + 40);
                slots[Encoding.ASCII.GetString(stringTable, nameOffset, nameLength)] = s;
            }
        }

        public List<string> Names { get { return new List<string>(slots.Keys); } }

        // One entry read part by part instead of all at once. The world file is
        // eight hundred megabytes unpacked and only needs to be looked through,
        // not held, so the scan gets a window at a time with an overlap wide
        // enough that nothing straddling a seam is missed.
        public void ForEachChunk(string name, int overlap, Action<byte[], int, long> sink)
        {
            Slot s;
            if (!slots.TryGetValue(name, out s)) return;
            byte[] window = null;
            int carried = 0;
            long consumed = 0;
            using (FileStream fs = GameFile.Open(path))
                for (int k = 0; k < s.Parts; k++)
                {
                    int pt = (s.FirstPart + k) * 12;
                    int offset = BitConverter.ToInt32(partTable, pt);
                    int compressed = BitConverter.ToInt32(partTable, pt + 4);
                    int decompressed = BitConverter.ToInt32(partTable, pt + 8);
                    byte[] chunk = GameFile.Read(fs, offset, compressed);
                    byte[] plain = compressed == decompressed
                        ? chunk : Lz4.Decode(chunk, 0, compressed, decompressed);

                    if (window == null || window.Length < carried + decompressed)
                        Array.Resize(ref window, carried + decompressed);
                    Buffer.BlockCopy(plain, 0, window, carried, decompressed);
                    int have = carried + decompressed;

                    sink(window, have, consumed - carried);

                    carried = Math.Min(overlap, have);
                    Buffer.BlockCopy(window, have - carried, window, 0, carried);
                    consumed += decompressed;
                }
        }

        public byte[] Get(string name)
        {
            Slot s;
            if (!slots.TryGetValue(name, out s)) return null;
            byte[] result = new byte[s.Size];
            int op = 0;
            using (FileStream fs = GameFile.Open(path))
                for (int k = 0; k < s.Parts; k++)
                {
                    int pt = (s.FirstPart + k) * 12;
                    int offset = BitConverter.ToInt32(partTable, pt);
                    int compressed = BitConverter.ToInt32(partTable, pt + 4);
                    int decompressed = BitConverter.ToInt32(partTable, pt + 8);
                    byte[] chunk = GameFile.Read(fs, offset, compressed);
                    if (compressed == decompressed) Buffer.BlockCopy(chunk, 0, result, op, decompressed);
                    else Buffer.BlockCopy(Lz4.Decode(chunk, 0, compressed, decompressed), 0, result, op, decompressed);
                    op += decompressed;
                }
            return result;
        }

        // Nothing is held between reads, so this only exists for symmetry.
        public void Dispose() { }
    }

    // Grim Dawn .tex is a twelve byte wrapper around a headerless-magic DDS.
    // Every item icon turned out to be uncompressed 32 bit BGRA with no mipmaps,
    // which is exactly the memory layout of Format32bppArgb.
    public static class Tex
    {
        // What the header says this texture is, for the times Decode says no.
        public static string Describe(byte[] data)
        {
            if (data == null || data.Length < 160) return "too few bytes";
            int marker = -1;
            for (int i = 0; i + 3 < 64 && i + 3 < data.Length; i++)
                if (data[i] == 'D' && data[i + 1] == 'D' && data[i + 2] == 'S') { marker = i; break; }
            if (marker < 0) return "missing DDS marker";
            int header = marker + 4;
            int height = BitConverter.ToInt32(data, header + 8);
            int width = BitConverter.ToInt32(data, header + 12);
            int flags = BitConverter.ToInt32(data, header + 72 + 4);
            int bits = BitConverter.ToInt32(data, header + 72 + 12);
            string four = Encoding.ASCII.GetString(data, header + 72 + 8, 4);
            return width + "x" + height + ", bits per pixel " + bits +
                   ", flags 0x" + flags.ToString("X") + ", fourCC \"" + four + "\"";
        }

        public static Bitmap Decode(byte[] data)
        {
            if (data == null || data.Length < 160) return null;
            int marker = -1;
            for (int i = 0; i + 3 < 64 && i + 3 < data.Length; i++)
                if (data[i] == 'D' && data[i + 1] == 'D' && data[i + 2] == 'S') { marker = i; break; }
            if (marker < 0) return null;

            int header = marker + 4;
            int height = BitConverter.ToInt32(data, header + 8);
            int width = BitConverter.ToInt32(data, header + 12);
            int bits = BitConverter.ToInt32(data, header + 72 + 12);
            int pixels = header + 124;
            // Item icons are all 32 bit BGRA, but a handful of skill icons in
            // UI.arc images are 24-bit BGR without an alpha channel; each mastery
            // tree contains one opaque icon.
            if (bits != 32 && bits != 24) return null;
            if (width <= 0 || height <= 0) return null;
            int step = bits / 8;
            if (pixels + width * height * step > data.Length) return null;

            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData locked = bmp.LockBits(new Rectangle(0, 0, width, height),
                                             ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (step == 4)
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(data, pixels + y * width * 4,
                                     IntPtr.Add(locked.Scan0, y * locked.Stride), width * 4);
                else
                {
                    // Three bytes in, four bytes out, opaque: without an alpha
                    // channel every pixel of the picture is meant to be seen.
                    byte[] row = new byte[width * 4];
                    for (int y = 0; y < height; y++)
                    {
                        int src = pixels + y * width * 3;
                        for (int x = 0; x < width; x++)
                        {
                            row[x * 4 + 0] = data[src + x * 3 + 0];
                            row[x * 4 + 1] = data[src + x * 3 + 1];
                            row[x * 4 + 2] = data[src + x * 3 + 2];
                            row[x * 4 + 3] = 255;
                        }
                        Marshal.Copy(row, 0, IntPtr.Add(locked.Scan0, y * locked.Stride), row.Length);
                    }
                }
            }
            finally { bmp.UnlockBits(locked); }
            return bmp;
        }
    }

    public class GameData : IDisposable
    {
        public const int CellPixels = 32; // one inventory cell is 32x32 in the icons

        public string GameDir { get; private set; }

        // Language code used by name caches.
        public string Language { get; private set; }
        readonly List<ArzIndex> databases = new List<ArzIndex>();
        readonly List<ArcIndex> itemArchives = new List<ArcIndex>();
        readonly List<ArcIndex> uiArchives = new List<ArcIndex>();
        readonly Dictionary<string, string> tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DbRecord> recordCache = new Dictionary<string, DbRecord>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Bitmap> iconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        public static string FindGame(string hint) { return ItemDbLocator.FindGame(hint); }

        // Every record path containing a piece of text, across all four
        // databases, without duplicates. An expansion overrides a record by
        // keeping its path, so the later database wins and the path is listed
        // once — Record() resolves it to the live version.
        public List<string> PathsContaining(string part)
        {
            List<string> made = new List<string>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (ArzIndex db in databases)
                foreach (string p in db.Names)
                {
                    if (p.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (seen.ContainsKey(p)) continue;
                    seen[p] = true;
                    made.Add(p);
                }
            return made;
        }

        public GameData(string gameDir) : this(gameDir, "RU") { }

        public GameData(string gameDir, string language)
        {
            GameDir = gameDir;
            Language = (language ?? "RU").ToUpperInvariant();
            string[] arz ={ @"database\database.arz", @"gdx1\database\GDX1.arz",
                             @"gdx2\database\GDX2.arz", @"gdx3\database\GDX3.arz" };
            foreach (string rel in arz)
            {
                string p = Path.Combine(gameDir, rel);
                if (File.Exists(p)) databases.Add(new ArzIndex(p));
            }
            string[] arc = { @"resources\Items.arc", @"gdx1\resources\Items.arc",
                             @"gdx2\resources\Items.arc", @"gdx3\resources\Items.arc" };
            foreach (string rel in arc)
            {
                string p = Path.Combine(gameDir, rel);
                if (File.Exists(p)) itemArchives.Add(new ArcIndex(p));
            }
            // Skill icons come from the interface archive, not the item one.
            string[] ui = { "resources/UI.arc", "gdx1/resources/UI.arc",
                            "gdx2/resources/UI.arc", "gdx3/resources/UI.arc" };
            foreach (string rel in ui)
            {
                string p = Path.Combine(gameDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p)) uiArchives.Add(new ArcIndex(p));
            }
            // English is loaded first as the fallback, then the chosen language
            // on top. The localised archives are single files that already cover
            // the expansions, so anything they miss keeps its English text.
            bool englishChosen = string.Equals(language, "EN", StringComparison.OrdinalIgnoreCase);
            LoadLanguage(gameDir, "EN", englishChosen);
            if (!englishChosen) LoadLanguage(gameDir, language, true);
        }

        // Templates that came from the chosen language, as opposed to the English
        // fallback. The game does not translate every line — the range form of
        // physical damage, for one, exists only in English — and there our own
        // Russian wording reads better than the game's English.
        readonly Dictionary<string, string> localised = new Dictionary<string, string>(StringComparer.Ordinal);

        // Keep the English tag text when a selected language overrides a key with
        // an empty value. Do not replace intentional empty suffixes.
        readonly Dictionary<string, string> english = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string EnglishTag(string key)
        {
            string v;
            return !string.IsNullOrEmpty(key) && english.TryGetValue(key, out v) ? v : null;
        }

        public string ResolveNameSelector(string selector)
        {
            return selector.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)
                ? Tag(selector.Substring(4))
                : selector;
        }

        void LoadLanguage(string gameDir, string language, bool chosen)
        {
            string[] folders = { "resources", @"gdx1\resources", @"gdx2\resources", @"gdx3\resources" };
            foreach (string folder in folders)
            {
                string p = Path.Combine(gameDir, folder, "Text_" + language.ToUpperInvariant() + ".arc");
                if (!File.Exists(p)) continue;
                foreach (KeyValuePair<string, string> file in Arc.ReadTextFiles(p))
                {
                    if (!file.Key.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (string rawLine in file.Value.Split('\n'))
                    {
                        string line = rawLine.Trim('\r', ' ', '\t');
                        if (line.Length == 0 || line[0] == '#') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq), value = Clean(line.Substring(eq + 1));
                        tags[key] = value;
                        if (chosen) localised[key] = value;
                        if (string.Equals(language, "EN", StringComparison.OrdinalIgnoreCase)) english[key] = value;
                    }
                }
            }
        }

        // Languages the installation actually has, by the Text_XX.arc files present.
        public static List<string> AvailableLanguages(string gameDir)
        {
            List<string> found = new List<string>();
            string dir = Path.Combine(gameDir ?? "", "resources");
            if (!Directory.Exists(dir)) return found;
            foreach (string f in Directory.GetFiles(dir, "Text_*.arc"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (name.Length > 5) found.Add(name.Substring(5).ToUpperInvariant());
            }
            found.Sort();
            return found;
        }

        // Tag values carry inline formatting: "{^E}" colour switches and bare
        // "^k" codes. The "{%...}" placeholders must survive — they are what the
        // stat templates substitute numbers into.
        static string Clean(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '{' && i + 3 < s.Length && s[i + 1] == '^' && s[i + 3] == '}') { i += 3; continue; }
                if (s[i] == '^' && i + 1 < s.Length) { i++; continue; }
                sb.Append(s[i]);
            }
            return sb.ToString().Trim();
        }

        // Fills one of the game's own stat templates, for example
        //     DefenseFire = localized resistance text
        // Returns null when the game has no such tag, so the caller can fall back.
        public string FormatStat(string tagKey, double first, double second)
        {
            return FormatTag(tagKey, first, second);
        }

        // Placeholders come in two shapes: "{%.0f0}" and "{+%d0}", where the
        // trailing digit is the argument number and the rest is the format.
        // Whether a template contains a numeric placeholder. A sign may appear
        // between the brace and percent marker.
        public static bool HasPlaceholder(string template)
        {
            if (string.IsNullOrEmpty(template)) return false;
            for (int i = 0; i + 1 < template.Length; i++)
            {
                if (template[i] != '{') continue;
                if (template[i + 1] == '%') return true;
                if ((template[i + 1] == '+' || template[i + 1] == '-') &&
                    i + 2 < template.Length && template[i + 2] == '%') return true;
            }
            return false;
        }

        public string FormatTag(string tagKey, params object[] args)
        {
            string template = Tag(tagKey);
            if (string.IsNullOrEmpty(template)) return null;

            StringBuilder sb = new StringBuilder(template.Length + 16);
            for (int i = 0; i < template.Length; i++)
            {
                // A sign before `%` inside braces is printed with the value.
                // Keep the sign separate from a negative numeric value.
                int percent = template[i] == '{' && i + 1 < template.Length
                    ? (template[i + 1] == '%' ? i + 1
                       : ((template[i + 1] == '+' || template[i + 1] == '-') &&
                          i + 2 < template.Length && template[i + 2] == '%' ? i + 2 : -1))
                    : -1;
                if (percent < 0) { sb.Append(template[i]); continue; }

                int close = template.IndexOf('}', i);
                if (close < 0) { sb.Append(template[i]); continue; }

                bool leadingSign = template[i + 1] == '+';
                bool leadingMinus = template[i + 1] == '-';
                string body = template.Substring(percent + 1, close - percent - 1);
                i = close;

                // A per cent sign can sit inside the braces after the argument
                // number — "{%.0f0%}" is "22%" — and then it is a literal, not
                // part of the format.
                string trailer = "";
                if (body.EndsWith("%", StringComparison.Ordinal))
                { trailer = "%"; body = body.Substring(0, body.Length - 1); }

                int digits = body.Length;
                while (digits > 0 && char.IsDigit(body[digits - 1])) digits--;
                string spec = body.Substring(0, digits);
                int index;
                if (!int.TryParse(body.Substring(digits), out index)) index = 0;
                if (leadingSign) spec = "+" + spec;

                object arg = index >= 0 && index < args.Length ? args[index] : null;
                if (spec.IndexOf('s') >= 0 || arg is string) sb.Append(arg == null ? "" : arg.ToString());
                else
                {
                    double v = arg == null ? 0 : Convert.ToDouble(arg);
                    if (leadingMinus && v >= 0) sb.Append('-');
                    sb.Append(FormatValue(spec, v));
                }
                sb.Append(trailer);
            }
            return CollapseSpaces(sb.ToString());
        }

        // A localized word from the game, or the supplied fallback.
        public string Word(string tagKey, string fallback)
        {
            string v = Tag(tagKey);
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        static string FormatValue(string spec, double value)
        {
            bool signed = spec.StartsWith("+");
            if (signed) spec = spec.Substring(1);

            int decimals = 0;
            int dot = spec.IndexOf('.');
            if (dot >= 0 && dot + 1 < spec.Length && char.IsDigit(spec[dot + 1]))
                decimals = spec[dot + 1] - '0';
            // The game omits the fractional part for integer-valued templates,
            // while values that are inherently fractional retain it.
            if (decimals > 0 && Math.Abs(value - Math.Round(value)) < 0.0001) decimals = 0;

            // The game rounds midpoint values away from zero; .NET's default is
            // banker's rounding. Use `MidpointRounding.AwayFromZero` consistently.
            double shown = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
            string text = shown.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
            if (signed && value > 0) text = "+" + text;
            return text;
        }

        static string CollapseSpaces(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s)
            {
                if (c == ' ') { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        // Every tag whose key or text contains the given piece. The game's own
        // wording is the only right wording, so finding the tag it uses beats
        // writing a line of our own beside it.
        public List<KeyValuePair<string, string>> FindTags(string part)
        {
            List<KeyValuePair<string, string>> hits = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(part)) return hits;
            foreach (KeyValuePair<string, string> kv in tags)
                if (kv.Key.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (kv.Value ?? "").IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                    hits.Add(kv);
            hits.Sort(delegate (KeyValuePair<string, string> a, KeyValuePair<string, string> b)
                      { return StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key); });
            return hits;
        }

        public string Tag(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string v;
            return tags.TryGetValue(key, out v) ? v : null;
        }

        // The raw template in the chosen language, or null. Some of the game's
        // lines carry no placeholder at all — "DamageDurationPoison" is just the
        // word, because the game builds the number and duration around
        // it itself — so the caller has to look before using one as a whole line.
        public string LocalTag(string key)
        {
            string v;
            return key != null && localised.TryGetValue(key, out v) ? v : null;
        }

        // Fills the template only if the game ships it in the chosen language.
        // Callers with their own wording use this, so an untranslated line falls
        // back to our text instead of to the game's English.
        public string FormatLocalTag(string tagKey, params object[] args)
        {
            if (string.IsNullOrEmpty(tagKey) || !localised.ContainsKey(tagKey)) return null;
            return FormatTag(tagKey, args);
        }

        // Localised names carry grammatical markers: a noun is tagged with its
        // gender marker and an adjective lists every form, so the
        // game can agree one with the other. Splitting them apart lets the item
        // name be assembled the same way.
        static List<KeyValuePair<string, string>> Forms(string raw)
        {
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(raw)) return list;
            int i = 0;
            while (i < raw.Length)
            {
                // Gender markers use only Latin letters inside brackets. Ignore
                // other bracketed text so localized titles and enemy names remain intact.
                if (raw[i] == '[')
                {
                    int close = raw.IndexOf(']', i);
                    if (close > i && close - i <= 4 && IsMarker(raw, i + 1, close))
                    {
                        string marker = raw.Substring(i + 1, close - i - 1);
                        int next = raw.IndexOf('[', close + 1);
                        string text = next < 0 ? raw.Substring(close + 1) : raw.Substring(close + 1, next - close - 1);
                        list.Add(new KeyValuePair<string, string>(marker, text));
                        i = next < 0 ? raw.Length : next;
                        continue;
                    }
                }
                // A bracket that is not a marker is ordinary text; continue after it.
                int bracket = raw.IndexOf('[', raw[i] == '[' ? i + 1 : i);
                string plain = bracket < 0 ? raw.Substring(i) : raw.Substring(i, bracket - i);
                // Text after a parsed marker remains part of the same localized name.
                if (plain.Length > 0 && list.Count > 0)
                    list[list.Count - 1] = new KeyValuePair<string, string>(list[list.Count - 1].Key,
                                                                            list[list.Count - 1].Value + plain);
                else if (plain.Length > 0) list.Add(new KeyValuePair<string, string>("", plain));
                i = bracket < 0 ? raw.Length : bracket;
            }
            return list;
        }

        static bool IsMarker(string raw, int from, int to)
        {
            if (to <= from) return false;
            for (int k = from; k < to; k++)
            {
                char c = raw[k];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) return false;
            }
            return true;
        }

        public static string GenderOf(string raw)
        {
            List<KeyValuePair<string, string>> f = Forms(raw);
            return f.Count == 0 ? "" : f[0].Key;
        }

        public static string Form(string raw, string gender)
        {
            List<KeyValuePair<string, string>> f = Forms(raw);
            if (f.Count == 0) return Plain(raw ?? "");
            foreach (KeyValuePair<string, string> kv in f) if (kv.Key == gender) return Plain(kv.Value);
            foreach (KeyValuePair<string, string> kv in f) if (kv.Key == "") return Plain(kv.Value);
            return Plain(f[0].Value);
        }

        // Strip game-internal prefix markers such as `$` and `|1` before displaying
        // localized names. These markers are used only when composing names.
        static string Plain(string value)
        {
            string s = (value ?? "").Trim();
            while (s.Length > 0)
            {
                if (s[0] == '$') { s = s.Substring(1).TrimStart(); continue; }
                if (s[0] == '|' && s.Length > 1 && s[1] >= '0' && s[1] <= '9')
                { s = s.Substring(2).TrimStart(); continue; }
                break;
            }
            return s;
        }

        // Later archives override earlier ones, same as the game's load order.
        public DbRecord Record(string dbr)
        {
            if (string.IsNullOrEmpty(dbr)) return null;
            DbRecord cached;
            if (recordCache.TryGetValue(dbr, out cached)) return cached;
            DbRecord found = null;
            for (int i = databases.Count - 1; i >= 0; i--)
            {
                found = databases[i].Get(dbr);
                if (found != null) break;
            }
            recordCache[dbr] = found;
            return found;
        }

        public Bitmap Icon(string texturePath) { return Picture(texturePath, itemArchives); }

        // Skill icons live in UI.arc, not in the item archives, and skill records
        // name them as "ui/skills/…". Same decoding, different shelf.
        public Bitmap SkillIcon(string texturePath) { return Picture(texturePath, uiArchives); }

        // Why a picture did not come out, in one line: it says whether the archive
        // has the entry at all, which separates "we looked in the wrong place"
        // from "we cannot read this shape of .tex".
        public string PictureProblem(string texturePath)
        {
            if (string.IsNullOrEmpty(texturePath)) return "empty texture path";
            string entry = texturePath.Replace('\\', '/');
            int slash = entry.IndexOf('/');
            string[] shapes = slash > 0 ? new[] { entry, entry.Substring(slash + 1) } : new[] { entry };
            foreach (ArcIndex a in uiArchives)
                foreach (string shape in shapes)
                {
                    byte[] data = a.Get(shape);
                    if (data == null) continue;
                    if (Tex.Decode(data) != null) return null;
                    return "entry found (" + data.Length + " bytes), but decoding failed: " + Tex.Describe(data);
                }
            return "entry not found in the UI archives";
        }

        Bitmap Picture(string texturePath, List<ArcIndex> archives)
        {
            if (string.IsNullOrEmpty(texturePath) || archives.Count == 0) return null;
            Bitmap cached;
            if (iconCache.TryGetValue(texturePath, out cached)) return cached;

            // An archive is rooted at its own top folder, so a record path like
            // "items/…" or "ui/…" is tried both whole and with that first segment
            // taken off rather than guessing which shape this archive uses.
            string entry = texturePath.Replace('\\', '/');
            int slash = entry.IndexOf('/');
            string[] shapes = slash > 0 ? new[] { entry, entry.Substring(slash + 1) } : new[] { entry };

            Bitmap bmp = null;
            for (int i = archives.Count - 1; i >= 0 && bmp == null; i--)
                foreach (string shape in shapes)
                {
                    byte[] data = archives[i].Get(shape);
                    if (data == null) continue;
                    bmp = Tex.Decode(data);
                    break;
                }
            iconCache[texturePath] = bmp;
            return bmp;
        }

        public void Dispose()
        {
            foreach (ArzIndex a in databases) a.Dispose();
            foreach (ArcIndex a in itemArchives) a.Dispose();
            foreach (ArcIndex a in uiArchives) a.Dispose();
            // The icons are deliberately not disposed. Item views, the catalogue
            // and the hover window all hold references to them, and a disposed
            // bitmap turns every later use into "Parameter is not valid". Letting
            // the collector reclaim them once nothing points at them costs a few
            // megabytes at worst and cannot crash.
            iconCache.Clear();
        }
    }

    // Same search the command line tool uses, kept here so the GUI does not
    // depend on that project's internals.
    public static class ItemDbLocator
    {
        // Where the game might be. Steam is only one way to own it, so the search
        // also asks the registry — which is where GOG and the stand-alone
        // installers record themselves — and then walks the obvious folders on
        // every fixed drive. A folder counts as the game only if the main
        // database is in it.
        public static string FindGame(string hint)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(hint)) candidates.Add(hint);
            AddSteam(candidates);
            AddRegistry(candidates);
            AddCommonFolders(candidates);

            foreach (string c in candidates)
                if (!string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, @"database\database.arz")))
                    return c;
            return null;
        }

        static void AddSteam(List<string> candidates)
        {
            List<string> roots = new List<string> { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" };
            string installed = RegistryValue(Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            if (!string.IsNullOrEmpty(installed)) roots.Insert(0, installed.Replace('/', '\\'));

            foreach (string steam in roots)
            {
                candidates.Add(Path.Combine(steam, @"steamapps\common\Grim Dawn"));
                string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (string line in File.ReadAllLines(vdf))
                {
                    int a = line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase);
                    if (a < 0) continue;
                    int q1 = line.IndexOf('"', a + 6);
                    int q2 = q1 < 0 ? -1 : line.IndexOf('"', q1 + 1);
                    if (q2 < 0) continue;
                    candidates.Add(Path.Combine(line.Substring(q1 + 1, q2 - q1 - 1).Replace(@"\\", @"\"),
                                                @"steamapps\common\Grim Dawn"));
                }
            }
        }

        // GOG keeps a key per game; every Windows installer leaves an uninstall
        // entry with the folder it wrote to.
        static void AddRegistry(List<string> candidates)
        {
            string[] uninstall = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            foreach (Microsoft.Win32.RegistryKey hive in
                     new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
                foreach (string path in uninstall)
                    ForEachSubkey(hive, path, delegate (Microsoft.Win32.RegistryKey key)
                    {
                        string name = key.GetValue("DisplayName") as string;
                        if (name == null || name.IndexOf("Grim Dawn", StringComparison.OrdinalIgnoreCase) < 0) return;
                        string where = key.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(where)) candidates.Add(where.Trim('"'));
                    });

            foreach (string path in new[] { @"SOFTWARE\GOG.com\Games", @"SOFTWARE\WOW6432Node\GOG.com\Games" })
                ForEachSubkey(Microsoft.Win32.Registry.LocalMachine, path, delegate (Microsoft.Win32.RegistryKey key)
                {
                    string name = key.GetValue("gameName") as string;
                    if (name == null || name.IndexOf("Grim Dawn", StringComparison.OrdinalIgnoreCase) < 0) return;
                    string where = key.GetValue("path") as string;
                    if (!string.IsNullOrEmpty(where)) candidates.Add(where);
                });
        }

        static void AddCommonFolders(List<string> candidates)
        {
            string[] shapes = {
                @"Grim Dawn", @"Games\Grim Dawn", @"GOG Games\Grim Dawn",
                @"Program Files\Grim Dawn", @"Program Files (x86)\Grim Dawn",
                @"SteamLibrary\steamapps\common\Grim Dawn", @"Steam\steamapps\common\Grim Dawn",
            };
            foreach (DriveInfo drive in SafeDrives())
                foreach (string shape in shapes)
                    candidates.Add(Path.Combine(drive.RootDirectory.FullName, shape));
        }

        static IEnumerable<DriveInfo> SafeDrives()
        {
            DriveInfo[] all;
            try { all = DriveInfo.GetDrives(); }
            catch (Exception) { yield break; }
            foreach (DriveInfo d in all)
            {
                bool usable;
                try { usable = d.IsReady && d.DriveType == DriveType.Fixed; }
                catch (Exception) { usable = false; }
                if (usable) yield return d;
            }
        }

        static string RegistryValue(Microsoft.Win32.RegistryKey hive, string path, string name)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key = hive.OpenSubKey(path))
                    return key == null ? null : key.GetValue(name) as string;
            }
            catch (Exception) { return null; }
        }

        static void ForEachSubkey(Microsoft.Win32.RegistryKey hive, string path,
                                  Action<Microsoft.Win32.RegistryKey> visit)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey root = hive.OpenSubKey(path))
                {
                    if (root == null) return;
                    foreach (string name in root.GetSubKeyNames())
                        using (Microsoft.Win32.RegistryKey sub = root.OpenSubKey(name))
                            if (sub != null) visit(sub);
                }
            }
            catch (Exception)
            {
                // A registry we may not read is simply one more place the game is not.
            }
        }
    }
}
