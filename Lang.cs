// UI text comes from language files rather than code.
// Files under lang/<CODE> use key=value entries; # starts a comment. Escape and
// substitution markers are supported. Fallback order is selected language,
// English, then Russian. Russian files are also embedded as a fallback resource.
// Game names remain in GameData.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Gdc.Gui
{
    public static class Lang
    {
        // Language whose files are expected to be complete.
        public const string Source = "RU";
        const string Fallback = "EN";

        static readonly object gate = new object();
        // Replaced atomically on language changes; readers may use any stream.
        static Dictionary<string, string> texts;
        static Dictionary<string, string> sourceTexts;
        static string current;

        public static string Folder
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang"); }
        }

        public static string Current { get { Ensure(); return current; } }

        // Increments on each language load for consumers caching localized arrays.
        public static int Version { get { Ensure(); return version; } }
        static int version;

        // Controls whose text follows the language, so the main window can switch without
        // being rebuilt. Held weakly: a closed dialog must not be kept alive by its labels.
        sealed class Binding
        {
            public WeakReference<Control> Target;
            public string Key;
            public object[] Args;
        }
        static readonly List<Binding> bindings = new List<Binding>();

        // Fires after a reload, for texts a window assembles by hand.
        public static event Action Changed;

        static void Ensure()
        {
            if (texts != null) return;
            lock (gate)
            {
                if (texts != null) return;
                Settings s = Settings.Load(null);
                LoadCore(s.Language ?? Settings.DefaultLanguage);
            }
        }

        public static void Load(string language)
        {
            lock (gate) LoadCore(language ?? Source);
            List<Binding> alive = new List<Binding>();
            lock (bindings)
            {
                bindings.RemoveAll(delegate (Binding b) { Control c; return !b.Target.TryGetTarget(out c) || c.IsDisposed; });
                alive.AddRange(bindings);
            }
            foreach (Binding b in alive)
            {
                Control c;
                if (b.Target.TryGetTarget(out c)) c.Text = T(b.Key, b.Args);
            }
            Action handler = Changed;
            if (handler != null) handler();
        }

        static void LoadCore(string language)
        {
            string code = language.ToUpperInvariant();
            Dictionary<string, string> source = new Dictionary<string, string>(StringComparer.Ordinal);
            ReadEmbeddedInto(source);
            ReadFolder(Path.Combine(Folder, Source), source, null, null);

            Dictionary<string, string> all = new Dictionary<string, string>(source, StringComparer.Ordinal);
            if (code != Source)
            {
                ReadEmbeddedInto(all, Fallback);
                ReadFolder(Path.Combine(Folder, Fallback), all, null, null);
                if (code != Fallback) ReadFolder(Path.Combine(Folder, code), all, null, null);
            }
            sourceTexts = source;
            texts = all;
            current = code;
            version++;
        }

        // Public for --langtest, which compares the embedded resources with the source files.
        public static void ReadEmbeddedInto(Dictionary<string, string> into, string language = Source)
        {
            Assembly asm = typeof(Lang).Assembly;
            string prefix = "lang/" + language + "/";
            List<string> names = new List<string>();
            foreach (string name in asm.GetManifestResourceNames())
                if (name.StartsWith(prefix, StringComparison.Ordinal)) names.Add(name);
            names.Sort(StringComparer.Ordinal);
            foreach (string name in names)
                using (Stream st = asm.GetManifestResourceStream(name))
                using (StreamReader r = new StreamReader(st, Encoding.UTF8, true))
                    ReadText(r.ReadToEnd(), Path.GetFileNameWithoutExtension(name), into, null, null);
        }

        // Reads every *.txt of one language folder into `into` (later lines win). Problems and the
        // file each key came from are collected for `--langtest`; the program itself ignores them.
        public static void ReadFolder(string dir, Dictionary<string, string> into,
                                      List<string> problems, Dictionary<string, string> fileOfKey)
        {
            if (!Directory.Exists(dir)) return;
            string[] files = Directory.GetFiles(dir, "*.txt");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                string text;
                try { text = File.ReadAllText(f, Encoding.UTF8); }
                catch (Exception e) { if (problems != null) problems.Add(f + ": could not read the file: " + e.Message); continue; }
                ReadText(text, Path.GetFileNameWithoutExtension(f), into, problems, fileOfKey);
            }
        }

        static void ReadText(string text, string file, Dictionary<string, string> into,
                             List<string> problems, Dictionary<string, string> fileOfKey)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                string bare = line.Trim();
                if (bare.Length == 0 || bare[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    if (problems != null) problems.Add(file + ".txt, line " + (i + 1) + ": missing '=': " + bare);
                    continue;
                }
                string key = line.Substring(0, eq).Trim();
                string raw = line.Substring(eq + 1);
                if (problems != null)
                {
                    if (!KeyShape.IsMatch(key))
                        problems.Add(file + ".txt, line " + (i + 1) + ": key '" + key + "' must use only Latin letters, digits, and dots");
                    else if (!key.StartsWith(file + ".", StringComparison.Ordinal))
                        problems.Add(file + ".txt, line " + (i + 1) + ": key '" + key + "' must start with '" + file + ".'");
                    if (raw.Length > 0 && (char.IsWhiteSpace(raw[0]) || char.IsWhiteSpace(raw[raw.Length - 1])))
                        problems.Add(file + ".txt, line " + (i + 1) + ": leading or trailing whitespace will be trimmed from '" + key + "'");
                    if (fileOfKey != null && fileOfKey.ContainsKey(key))
                        problems.Add(file + ".txt, line " + (i + 1) + ": key '" + key + "' already appeared in " + fileOfKey[key] + ".txt");
                }
                if (fileOfKey != null) fileOfKey[key] = file;
                into[key] = Unescape(raw.Trim());
            }
        }

        public static readonly Regex KeyShape = new Regex(@"^[a-z][A-Za-z0-9]*(\.[A-Za-z0-9_\-]+)+$");

        public static string Unescape(string raw)
        {
            if (raw.IndexOf('\\') < 0) return raw;
            StringBuilder sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '\\' && i + 1 < raw.Length)
                {
                    char n = raw[i + 1];
                    if (n == 'n') { sb.Append('\n'); i++; continue; }
                    if (n == 't') { sb.Append('\t'); i++; continue; }
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // The substitution slots a text uses: {0}, {1:0.##} → 0, 1. A translation must keep the
        // same set, or a number silently drops out of the sentence.
        public static SortedSet<int> Slots(string text)
        {
            SortedSet<int> found = new SortedSet<int>();
            foreach (Match m in SlotShape.Matches(text ?? ""))
                found.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            return found;
        }
        static readonly Regex SlotShape = new Regex(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}");

        public static bool Has(string key)
        {
            Ensure();
            return texts.ContainsKey(key);
        }

        public static string T(string key)
        {
            Ensure();
            string v;
            return texts.TryGetValue(key, out v) ? v : "[" + key + "]";
        }

        // Numbers are formatted with the current culture — the same as the `"…" + n` the texts
        // were built with before they moved here, so the Russian screens stay byte for byte.
        public static string T(string key, params object[] args)
        {
            string template = T(key);
            if (args == null || args.Length == 0) return template;
            try { return string.Format(CultureInfo.CurrentCulture, template, args); }
            catch (FormatException)
            {
                // A broken translation must not take the window down: the source text is used.
                string v;
                if (sourceTexts != null && sourceTexts.TryGetValue(key, out v))
                {
                    try { return string.Format(CultureInfo.CurrentCulture, v, args); }
                    catch (FormatException) { }
                }
                return template;
            }
        }

        // `\n` of a language file as the Windows line break. A multiline TextBox shows a bare
        // `\n` as nothing, and the texts that moved here were built with Environment.NewLine.
        public static string WinLines(string text)
        {
            return text == null ? null : text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        }

        // Sets the control's text and keeps it in step with the language.
        public static void Bind(Control control, string key, params object[] args)
        {
            control.Text = T(key, args);
            lock (bindings)
                bindings.Add(new Binding { Target = new WeakReference<Control>(control), Key = key, Args = args });
        }
    }
}
